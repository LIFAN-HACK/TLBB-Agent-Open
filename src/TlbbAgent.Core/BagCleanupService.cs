using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class BagCleanupService : IBagCleanupService
{
    private const string Module = "BagCleanup";
    private readonly IInventoryRecognitionService _inventory;
    private readonly IItemPolicyService _policy;
    private readonly IInputService _input;
    private readonly ILogService _log;
    private readonly IWindowCaptureService? _capture;
    private readonly IBagItemTemplateMatchService? _templates;
    private readonly IVisionV2SlotService? _vision;
    private readonly IInteractionWindowDetectService? _interactionWindows;
    private readonly IInteractionControlCatalogService? _interactionControls;

    public BagCleanupService(
        IInventoryRecognitionService inventory,
        IItemPolicyService policy,
        IInputService input,
        ILogService log)
        : this(inventory, policy, input, log, null, null, null)
    {
    }

    public BagCleanupService(
        IInventoryRecognitionService inventory,
        IItemPolicyService policy,
        IInputService input,
        ILogService log,
        IWindowCaptureService? capture,
        IBagItemTemplateMatchService? templates,
        IVisionV2SlotService? vision = null,
        IInteractionWindowDetectService? interactionWindows = null,
        IInteractionControlCatalogService? interactionControls = null)
    {
        _inventory = inventory;
        _policy = policy;
        _input = input;
        _log = log;
        _capture = capture;
        _templates = templates;
        _vision = vision;
        _interactionWindows = interactionWindows;
        _interactionControls = interactionControls;
    }

    public BagCleanupResult Analyze(BoundGameWindow window, BagCleanupConfig config)
    {
        Normalize(config);
        if (config.Mode == BagCleanupExecutionMode.Disabled)
            return Result(false, false, "清包未启用。", EmptyScan(), Array.Empty<ItemCleanupCandidate>(), BagCleanupSafety.Disabled);

        if (config.OpenBagBeforeScan)
        {
            if (!config.OpenBagHotkeyExplicitlyConfigured
                || string.IsNullOrWhiteSpace(config.OpenBagHotkey))
            {
                _log.Warn(Module, "未配置开包快捷键；只读分析不会猜测默认键位。", window.Id);
            }
            else if (!_input.TryParseHotkey(config.OpenBagHotkey, out _))
                _log.Warn(Module, $"打开背包热键无效：{config.OpenBagHotkey}", window.Id);
            else
                _log.Info(Module, $"只读分析不会自动发送打开背包热键：{config.OpenBagHotkey}；如需开包，请使用显式受控开包流程。", window.Id);
        }

        var grid = CloneGrid(config.Grid);
        var policy = ClonePolicy(config.Policy);
        var capture = _capture?.CaptureClient(window.Hwnd);
        InteractionWindowDetection? bagPanelV3 = null;
        InteractionPanelControlLayout? bagLayoutV3 = null;
        AuxPanelDetection? legacyBagPanel = null;
        if (_interactionWindows is not null)
        {
            if (capture is null)
            {
                const string captureMessage = "无法取得当前游戏帧；V3 背包定位未运行，本轮不生成清包候选。";
                _log.Warn(Module, captureMessage, window.Id);
                return Result(
                    false,
                    false,
                    captureMessage,
                    EmptyScan(),
                    Array.Empty<ItemCleanupCandidate>(),
                    BagCleanupSafety.ReadOnlyDryRun);
            }

            bagPanelV3 = _interactionWindows
                .Detect(capture)
                .Detections
                .Where(item => item.ClassName == "bag_panel")
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (bagPanelV3 is null || bagPanelV3.Confidence < 0.75)
            {
                const string panelMessage = "交互窗口 V3 未可靠确认背包面板；本轮不生成清包候选。";
                _log.Warn(Module, panelMessage, window.Id);
                return Result(
                    false,
                    false,
                    panelMessage,
                    EmptyScan(),
                    Array.Empty<ItemCleanupCandidate>(),
                    BagCleanupSafety.ReadOnlyDryRun);
            }

            if (_interactionControls is null || !_interactionControls.IsAvailable)
            {
                const string catalogMessage = "交互控件 V3 资产不可用；无法精确重建背包格子，本轮不生成清包候选。";
                _log.Warn(Module, catalogMessage, window.Id);
                return Result(
                    false,
                    false,
                    catalogMessage,
                    EmptyScan(),
                    Array.Empty<ItemCleanupCandidate>(),
                    BagCleanupSafety.ReadOnlyDryRun);
            }

            bagLayoutV3 = _interactionControls.Map(capture.Sequence, bagPanelV3);
            if (bagLayoutV3.FrameSequence != capture.Sequence || !TryApplyBagGrid(bagLayoutV3, grid))
            {
                const string gridMessage = "交互控件 V3 未映射出完整 10x6 背包格子；本轮不生成清包候选。";
                _log.Warn(Module, gridMessage, window.Id);
                return Result(
                    false,
                    false,
                    gridMessage,
                    EmptyScan(),
                    Array.Empty<ItemCleanupCandidate>(),
                    BagCleanupSafety.ReadOnlyDryRun);
            }
        }
        else if (capture is not null && _vision is { IsAvailable: true })
        {
            legacyBagPanel = _vision
                .AnalyzeAuxPanels(capture, new[] { "bag_panel" })
                .Detections
                .Where(item => item.ClassName == "bag_panel")
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (legacyBagPanel is null)
            {
                const string panelMessage = "旧视觉模型未确认背包面板；本轮不生成清包候选。";
                _log.Warn(Module, panelMessage, window.Id);
                return Result(
                    false,
                    false,
                    panelMessage,
                    EmptyScan(),
                    Array.Empty<ItemCleanupCandidate>(),
                    BagCleanupSafety.ReadOnlyDryRun);
            }
        }
        var templateResult = ApplyTemplatesIfEnabled(capture, grid, policy, config.TemplateMatching);

        var scan = capture is not null
            ? _inventory.Scan(capture, grid)
            : _inventory.Scan(window, grid);
        if (!scan.Success)
            return Result(false, false, scan.Message, scan, Array.Empty<ItemCleanupCandidate>(), BagCleanupSafety.ReadOnlyDryRun)
                with { TemplateMatch = templateResult };

        var candidates = BuildCandidates(scan, policy);
        var free = scan.EmptyCount;
        var message =
            $"清包分析完成：占用 {scan.OccupiedCount}，空格 {free}，可卖候选 {candidates.Count(c => c.Decision == ItemCleanupDecision.SellCandidate)}，保护 {candidates.Count(c => c.Decision != ItemCleanupDecision.SellCandidate)}。";
        if (templateResult.Enabled)
            message += $" 模板命中 {templateResult.MatchedCount}，应用 {templateResult.AppliedCount}。";
        if (bagLayoutV3 is not null)
        {
            message +=
                $" 背包面板已由交互窗口 V3 确认（{bagLayoutV3.PanelConfidence:P0}），" +
                $"10x6 格子来自帧 {bagLayoutV3.FrameSequence} 的控件映射，" +
                $"runtime_authorized={bagLayoutV3.RuntimeAuthorized.ToString().ToLowerInvariant()}。";
        }
        else if (legacyBagPanel is not null)
        {
            message += $" 背包面板仅由旧视觉模型确认（{legacyBagPanel.Confidence:P0}），当前为只读兼容路径。";
        }
        if (free < config.MinFreeSlotsWarning)
            message += $" 空格低于阈值 {config.MinFreeSlotsWarning}，建议先人工确认候选。";

        _log.Info(Module, message, window.Id);
        return Result(true, false, message, scan, candidates, BagCleanupSafety.ReadOnlyDryRun)
            with { TemplateMatch = templateResult };
    }

    public BagCleanupResult Execute(BoundGameWindow window, BagCleanupConfig config)
    {
        Normalize(config);
        var analysis = Analyze(window, config);
        if (!analysis.Success)
            return analysis;

        if (config.Mode == BagCleanupExecutionMode.DryRun)
        {
            var msg = "清包处于模拟模式：只生成候选，不出售、不丢弃。";
            _log.Warn(Module, msg, window.Id);
            return analysis with { Message = msg, Executed = false, Safety = BagCleanupSafety.ReadOnlyDryRun };
        }

        if (!config.AllowActualSell)
        {
            var msg = "已阻止实际出售：未开启 AllowActualSell。";
            _log.Warn(Module, msg, window.Id);
            return analysis with { Message = msg, Executed = false, Safety = BagCleanupSafety.BlockedActualSell() };
        }

        if (analysis.SellCandidateCount == 0)
        {
            var msg = "没有明确可卖候选，未执行清包。";
            _log.Info(Module, msg, window.Id);
            return analysis with { Message = msg, Executed = false, Safety = BagCleanupSafety.BlockedActualSell() };
        }

        var blocked = "实际出售流程尚需商店窗口和确认区域校准；当前版本只输出候选，未点击出售。";
        _log.Warn(Module, blocked, window.Id);
        return analysis with { Message = blocked, Executed = false, Safety = BagCleanupSafety.BlockedActualSell() };
    }

    private BagItemTemplateApplyResult ApplyTemplatesIfEnabled(
        CaptureResult? capture,
        InventoryGridConfig grid,
        ItemPolicyConfig policy,
        BagItemTemplateMatchConfig config)
    {
        if (!config.Enabled)
            return BagItemTemplateApplyResult.Disabled;

        if (capture is null || _templates is null)
        {
            return new BagItemTemplateApplyResult(
                true,
                false,
                "窗口截图失败，模板匹配未执行。",
                0,
                0,
                0,
                0,
                Array.Empty<BagItemTemplateSlotMatch>());
        }

        return _templates.Apply(capture, grid, policy, config);
    }

    private IReadOnlyList<ItemCleanupCandidate> BuildCandidates(InventoryScanResult scan, ItemPolicyConfig policy)
    {
        return scan.Slots
            .Where(s => s.State == InventorySlotState.Occupied)
            .Select(slot =>
            {
                var decision = _policy.Decide(slot.ItemName, policy, out var reason);
                return new ItemCleanupCandidate(slot, decision, reason);
            })
            .ToList();
    }

    private static BagCleanupResult Result(
        bool success,
        bool executed,
        string message,
        InventoryScanResult scan,
        IReadOnlyList<ItemCleanupCandidate> candidates,
        BagCleanupSafety safety) =>
        new(success, executed, message, scan, candidates) { Safety = safety };

    private static InventoryScanResult EmptyScan() =>
        new(false, "", 0, 0, Array.Empty<InventorySlotSnapshot>());

    private static bool TryApplyBagGrid(
        InteractionPanelControlLayout layout,
        InventoryGridConfig grid)
    {
        var region = layout.Regions.SingleOrDefault(item =>
            string.Equals(item.RegionId, "bag_cells", StringComparison.Ordinal)
            && string.Equals(item.Kind, "regular_grid", StringComparison.OrdinalIgnoreCase));
        if (region is null
            || region.Rows != 6
            || region.Columns != 10
            || region.Cells.Count != 60)
            return false;

        var first = region.Cells.SingleOrDefault(item => item.Row == 0 && item.Column == 0);
        var secondColumn = region.Cells.SingleOrDefault(item => item.Row == 0 && item.Column == 1);
        var secondRow = region.Cells.SingleOrDefault(item => item.Row == 1 && item.Column == 0);
        if (first is null
            || secondColumn is null
            || secondRow is null
            || first.CellBox.IsEmpty
            || secondColumn.CellBox.IsEmpty
            || secondRow.CellBox.IsEmpty)
            return false;

        var gapX = secondColumn.CellBox.X - first.CellBox.X - first.CellBox.Width;
        var gapY = secondRow.CellBox.Y - first.CellBox.Y - first.CellBox.Height;
        if (gapX < 0 || gapY < 0)
            return false;

        grid.Enabled = true;
        grid.ScanMode = InventoryScanMode.ManualGrid;
        grid.X = first.CellBox.X;
        grid.Y = first.CellBox.Y;
        grid.Columns = region.Columns;
        grid.Rows = region.Rows;
        grid.CellWidth = first.CellBox.Width;
        grid.CellHeight = first.CellBox.Height;
        grid.GapX = gapX;
        grid.GapY = gapY;
        return true;
    }

    private static void Normalize(BagCleanupConfig config)
    {
        config.Grid ??= new InventoryGridConfig();
        config.Policy ??= new ItemPolicyConfig();
        config.TemplateMatching ??= new BagItemTemplateMatchConfig();
        config.MinFreeSlotsWarning = Math.Clamp(config.MinFreeSlotsWarning, 0, 80);
    }

    private static InventoryGridConfig CloneGrid(InventoryGridConfig source) =>
        new()
        {
            ScanMode = source.ScanMode,
            Enabled = source.Enabled,
            X = source.X,
            Y = source.Y,
            Columns = source.Columns,
            Rows = source.Rows,
            CellWidth = source.CellWidth,
            CellHeight = source.CellHeight,
            GapX = source.GapX,
            GapY = source.GapY,
            SlotNameOverrides = new Dictionary<int, string>(source.SlotNameOverrides),
            SlotNameSources = new Dictionary<int, string>(source.SlotNameSources)
        };

    private static ItemPolicyConfig ClonePolicy(ItemPolicyConfig source) =>
        new()
        {
            AllowedSellItems = source.AllowedSellItems.ToList(),
            KeepItems = source.KeepItems.ToList(),
            StoreItems = source.StoreItems.ToList(),
            TradeItems = source.TradeItems.ToList(),
            SellKeywords = source.SellKeywords.ToList(),
            BlockKeywords = source.BlockKeywords.ToList(),
            RequireExplicitSellKeyword = source.RequireExplicitSellKeyword,
            ProtectUnknownItems = source.ProtectUnknownItems
        };
}
