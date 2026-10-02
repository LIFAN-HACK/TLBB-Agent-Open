using System.Text.Json;
using TlbbAgent.Contracts;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.Core.Automation;

public sealed class MaterialCraftController : AutomationControllerBase<MaterialCraftState>, IMaterialCraftController
{
    private readonly IMaterialCraftConfigService _config;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private readonly ILatestFrameService? _frames;
    private readonly IVisionV2SlotService? _visionSlots;
    private readonly IVisionActionPlanner? _visionPlanner;
    private readonly IVisionActionExecutor? _visionExecutor;
    private readonly IOpenTextRecognizer? _openText;
    private readonly IMaterialCraftEntryLocator? _entryLocator;
    private readonly IMaterialRecipeListReader? _recipeReader;
    private readonly IGlobalConfigService? _global;
    private readonly IInteractionWindowDetectService? _interactionWindows;
    private readonly IInteractionControlCatalogService? _interactionControls;
    private readonly IPerceptionHub? _perception;
    private IDisposable? _runner;
    private int _stepIndex;
    private int _batchIndex;
    private int _retry;
    private DateTimeOffset? _continuousProcessStartedAt;

    public MaterialCraftController(
        ILogService log,
        IMaterialCraftConfigService config,
        IInputService input,
        IStateMachineEngine engine,
        ILatestFrameService? frames = null,
        IVisionV2SlotService? visionSlots = null,
        IVisionActionPlanner? visionPlanner = null,
        IVisionActionExecutor? visionExecutor = null,
        IOpenTextRecognizer? openText = null,
        IMaterialCraftEntryLocator? entryLocator = null,
        IMaterialRecipeListReader? recipeReader = null,
        IGlobalConfigService? global = null,
        IInteractionWindowDetectService? interactionWindows = null,
        IInteractionControlCatalogService? interactionControls = null,
        IPerceptionHub? perception = null)
        : base(MaterialCraftState.Idle, log)
    {
        _config = config;
        _input = input;
        _engine = engine;
        _frames = frames;
        _visionSlots = visionSlots;
        _visionPlanner = visionPlanner;
        _visionExecutor = visionExecutor;
        _openText = openText;
        _entryLocator = entryLocator;
        _recipeReader = recipeReader;
        _global = global;
        _interactionWindows = interactionWindows;
        _interactionControls = interactionControls;
        _perception = perception;
    }

    public override string Key => FeatureKeys.HangMaterial;
    public override string Name => "材料加工";
    public override string Group => "生活";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _stepIndex = 0;
        _batchIndex = 0;
        _retry = 0;
        _continuousProcessStartedAt = null;
        Machine.Goto(MaterialCraftState.ValidateConfig);
        _runner = _engine.Run(Name, LoopAsync, MarkFaulted);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && RunState == RunState.Running)
        {
            var window = Target;
            if (window is null)
            {
                Log.Warn(Name, "未绑定窗口，材料加工停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            var steps = _config.Steps;
            if (steps.Count == 0)
            {
                Log.Warn(Name, "加工流程为空，请先添加步骤。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (cfg.RequireVisualConfirmation && cfg.ProcessAllRecipes)
            {
                var runResult = await RunAllRecipesAsync(window, cfg, token).ConfigureAwait(false);
                switch (runResult)
                {
                    case MaterialCraftRunResult.Completed:
                        Log.Info(Name, "全部已识别配方均已连续确认资源耗尽，材料加工完成。", window.Id);
                        break;
                    case MaterialCraftRunResult.Blocked:
                        Log.Warn(Name, "材料加工缺少安全视觉证据，流程已阻止。", window.Id);
                        break;
                    case MaterialCraftRunResult.EntryUnavailable:
                        Log.Warn(Name, "材料加工入口不可用；没有已授权生活技能控件，未执行固定坐标或盲点。", window.Id);
                        break;
                    default:
                        Log.Warn(Name, "材料加工未能确认全部配方耗尽，流程停止且不标记完成。", window.Id);
                        break;
                }

                Machine.Goto(runResult == MaterialCraftRunResult.EntryUnavailable
                    ? MaterialCraftState.EntryUnavailable
                    : MaterialCraftState.Stop);
                RunState = RunState.Stopped;
                break;
            }

            if (_stepIndex >= steps.Count)
            {
                if (cfg.EnableCleanupAfterBatch)
                {
                    Machine.Goto(MaterialCraftState.CleanupBag);
                    ExecuteCleanup(window, cfg);
                    await Delay(cfg.CleanupWaitMs, token).ConfigureAwait(false);
                }

                Machine.Goto(MaterialCraftState.NextBatch);
                _batchIndex++;
                _stepIndex = 0;
                _retry = 0;

                if (ShouldStop(cfg))
                {
                    Machine.Goto(MaterialCraftState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                continue;
            }

            var step = steps[_stepIndex];
            Machine.Goto(MaterialCraftState.ExecuteStep);
            var result = await ExecuteStepAsync(window, step, cfg, token).ConfigureAwait(false);
            if (result == MaterialCraftActionResult.Exhausted)
            {
                Log.Info(Name, "已连续两帧确认材料不足，加工流程完成。", window.Id);
                Machine.Goto(MaterialCraftState.Stop);
                RunState = RunState.Stopped;
                break;
            }

            if (result == MaterialCraftActionResult.Blocked)
            {
                Log.Warn(Name, $"加工步骤缺少安全确认，流程已停止：{step.Label}", window.Id);
                Machine.Goto(MaterialCraftState.Stop);
                RunState = RunState.Stopped;
                break;
            }

            if (result == MaterialCraftActionResult.EntryUnavailable)
            {
                Log.Warn(Name, "材料加工入口不可用：当前帧没有已授权的生活技能入口，未执行固定坐标或盲点。", window.Id);
                Machine.Goto(MaterialCraftState.EntryUnavailable);
                RunState = RunState.Stopped;
                break;
            }

            if (result is MaterialCraftActionResult.InsufficientResource or MaterialCraftActionResult.InvalidAction)
            {
                Log.Warn(Name, $"材料加工反馈阻止继续执行：{result}。", window.Id);
                Machine.Goto(MaterialCraftState.Stop);
                RunState = RunState.Stopped;
                break;
            }

            if (result == MaterialCraftActionResult.Cooldown)
            {
                Machine.Goto(MaterialCraftState.BusyWait);
                await Delay(500, token).ConfigureAwait(false);
                continue;
            }

            if (result == MaterialCraftActionResult.Busy)
            {
                Machine.Goto(MaterialCraftState.BusyWait);
                Log.Info(Name, "材料加工仍在执行，等待视觉状态变化，不重复点击。", window.Id);
                var completion = await WaitForMaterialProcessingAsync(window, cfg, token).ConfigureAwait(false);
                if (completion == MaterialCraftFeedbackKind.Exhausted)
                {
                    Log.Info(Name, "已连续两帧确认材料不足，加工流程完成。", window.Id);
                    Machine.Goto(MaterialCraftState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                continue;
            }

            if (result is MaterialCraftActionResult.Executed or MaterialCraftActionResult.Completed)
            {
                Machine.Goto(MaterialCraftState.WaitAfterStep);
                Log.Info(Name, $"加工动作已执行：{step.Label}", window.Id);
                _retry = 0;
                await Delay(Math.Max(cfg.StepIntervalMs, step.WaitMs), token).ConfigureAwait(false);

                if (step.Action == MaterialCraftStepAction.Process && cfg.ProcessUntilMaterialsExhausted)
                {
                    _continuousProcessStartedAt ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - _continuousProcessStartedAt >= TimeSpan.FromMinutes(cfg.MaxContinuousProcessMinutes))
                    {
                        Log.Warn(Name, "达到连续加工安全时限，但没有确认材料耗尽；流程停止且不标记完成。", window.Id);
                        Machine.Goto(MaterialCraftState.Stop);
                        RunState = RunState.Stopped;
                        break;
                    }

                    Machine.Goto(MaterialCraftState.ExecuteStep);
                    continue;
                }

                Machine.Goto(MaterialCraftState.NextStep);
                _stepIndex++;
                continue;
            }

            _retry++;
            if (_retry > EffectiveRetry(step, cfg))
            {
                Machine.Goto(MaterialCraftState.Recover);
                var stopInsteadOfSkip = step.Action == MaterialCraftStepAction.Process
                                        && cfg.ProcessUntilMaterialsExhausted;
                Log.Warn(Name, stopInsteadOfSkip
                    ? $"加工步骤连续失败，流程停止且不标记完成：{step.Label}"
                    : $"加工步骤失败，跳过：{step.Label}", window.Id);
                _retry = 0;
                if (stopInsteadOfSkip)
                {
                    Machine.Goto(MaterialCraftState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                _stepIndex++;
            }

            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested
            && RunState == RunState.Stopped
            && Machine.Current != MaterialCraftState.EntryUnavailable)
            Machine.Goto(MaterialCraftState.Stop);

        RaiseStateChanged();
    }

    private async Task<MaterialCraftRunResult> RunAllRecipesAsync(
        BoundGameWindow window,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        if (_recipeReader is null
            || _visionExecutor is null
            || _frames is null
            || _interactionWindows is null
            || _interactionControls is null)
            return MaterialCraftRunResult.Blocked;

        if (_interactionControls.RuntimeAuthorized != true)
        {
            Log.Warn(
                Name,
                "材料加工控件目前仅允许只读识别，配方选择和加工按钮尚未获得运行授权。流程未发送任何点击。",
                window.Id);
            return MaterialCraftRunResult.EntryUnavailable;
        }

        var openStep = new MaterialCraftStep { Action = MaterialCraftStepAction.OpenPanel, Label = "打开材料加工" };
        Machine.Goto(MaterialCraftState.EnsurePanel);
        var openResult = await ExecuteOpenPanelAsync(window, openStep, cfg, token).ConfigureAwait(false);
        if (openResult == MaterialCraftActionResult.EntryUnavailable)
        {
            Machine.Goto(MaterialCraftState.EntryUnavailable);
            return MaterialCraftRunResult.EntryUnavailable;
        }
        if (openResult == MaterialCraftActionResult.Blocked)
            return MaterialCraftRunResult.Blocked;
        if (openResult != MaterialCraftActionResult.Executed)
            return MaterialCraftRunResult.Failed;

        var processedRecipes = new HashSet<string>(StringComparer.Ordinal);
        var previousFingerprint = string.Empty;
        var unchangedPageCount = 0;

        for (var pageIndex = 0; pageIndex < cfg.MaxRecipePages && !token.IsCancellationRequested; pageIndex++)
        {
            if (!TryReadRecipePage(window, out var page))
                return MaterialCraftRunResult.Failed;

            if (string.Equals(page.Fingerprint, previousFingerprint, StringComparison.Ordinal))
                unchangedPageCount++;
            else
                unchangedPageCount = 0;
            previousFingerprint = page.Fingerprint;

            var newRows = page.Rows.Where(row => !processedRecipes.Contains(row.Name)).ToArray();
            foreach (var row in newRows)
            {
                if (!TrySelectRecipe(window, row, page, cfg))
                    return MaterialCraftRunResult.Failed;

                await Delay(180, token).ConfigureAwait(false);
                var processResult = await ProcessSelectedRecipeToExhaustionAsync(window, cfg, token).ConfigureAwait(false);
                if (processResult == MaterialCraftRunResult.Blocked)
                    return MaterialCraftRunResult.Blocked;
                if (processResult != MaterialCraftRunResult.Completed)
                    return MaterialCraftRunResult.Failed;

                processedRecipes.Add(row.Name);
                Log.Info(Name, $"配方已确认耗尽：{row.Name}", window.Id);
            }

            if (unchangedPageCount >= 2)
                return processedRecipes.Count > 0
                    ? MaterialCraftRunResult.Completed
                    : MaterialCraftRunResult.Failed;

            var scrollResult = await ExecuteVisionPanelActionAsync(
                window,
                cfg,
                "material_panel",
                "scroll_down",
                requireOpenText: true,
                inspectMaterialFeedback: false,
                token).ConfigureAwait(false);
            if (scrollResult == MaterialCraftActionResult.Blocked)
                return MaterialCraftRunResult.Blocked;
            if (scrollResult != MaterialCraftActionResult.Executed)
                return MaterialCraftRunResult.Failed;

            await Delay(220, token).ConfigureAwait(false);
        }

        return MaterialCraftRunResult.Failed;
    }

    private async Task<MaterialCraftRunResult> ProcessSelectedRecipeToExhaustionAsync(
        BoundGameWindow window,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var retry = 0;
        while (!token.IsCancellationRequested
               && DateTimeOffset.UtcNow - startedAt < TimeSpan.FromMinutes(cfg.MaxContinuousProcessMinutes))
        {
            var result = await ExecuteVisionPanelActionAsync(
                window,
                cfg,
                "material_panel",
                "manufacture_all",
                requireOpenText: false,
                inspectMaterialFeedback: true,
                token).ConfigureAwait(false);
            if (result == MaterialCraftActionResult.Exhausted)
                return MaterialCraftRunResult.Completed;
            if (result == MaterialCraftActionResult.Blocked)
                return MaterialCraftRunResult.Blocked;
            if (result is MaterialCraftActionResult.InsufficientResource or MaterialCraftActionResult.InvalidAction)
                return MaterialCraftRunResult.Blocked;
            if (result == MaterialCraftActionResult.Cooldown)
            {
                await Delay(500, token).ConfigureAwait(false);
                continue;
            }
            if (result == MaterialCraftActionResult.Busy)
            {
                retry = 0;
                var completion = await WaitForMaterialProcessingAsync(window, cfg, token).ConfigureAwait(false);
                if (completion == MaterialCraftFeedbackKind.Exhausted)
                    return MaterialCraftRunResult.Completed;
                continue;
            }
            if (result == MaterialCraftActionResult.Retry)
            {
                retry++;
                if (retry > cfg.MaxStepRetry)
                    return MaterialCraftRunResult.Failed;
            }
            else
            {
                retry = 0;
            }

            await Delay(Math.Max(cfg.StepIntervalMs, 900), token).ConfigureAwait(false);
        }

        return MaterialCraftRunResult.Failed;
    }

    private bool TryReadRecipePage(BoundGameWindow window, out MaterialRecipePage page)
    {
        page = MaterialRecipePage.Unavailable("材料面板不可用。");
        if (!TryGetMaterialPanel(window, out var frame, out var panel, out var panelCrop))
            return false;

        page = _recipeReader!.Read(panelCrop, panel.Box) with
        {
            FrameSequence = frame.Sequence,
            PanelBox = panel.Box
        };
        if (!page.IsReliable)
            Log.Warn(Name, $"配方列表读取失败：{page.Message}", window.Id);
        return page.IsReliable;
    }

    private bool TrySelectRecipe(
        BoundGameWindow window,
        MaterialRecipeRow row,
        MaterialRecipePage page,
        MaterialCraftConfig cfg)
    {
        if (row.Confidence < 0.62
            || row.Box.IsEmpty
            || page.FrameSequence <= 0
            || page.PanelBox.IsEmpty
            || !Contains(page.PanelBox, row.Box)
            || _visionExecutor is null
            || _interactionControls?.RuntimeAuthorized != true)
            return false;

        var decision = new VisionActionDecision(
            true,
            VisionActionKind.Click,
            row.Box.X + row.Box.Width / 2,
            row.Box.Y + row.Box.Height / 2,
            $"选择材料配方：{row.Name}");
        var global = _global?.Current ?? new GlobalConfig { InputDispatchMode = cfg.DispatchMode };
        return _visionExecutor.TryExecuteClick(window, decision, global);
    }

    private bool TryGetMaterialPanel(
        BoundGameWindow window,
        out CaptureResult frame,
        out InteractionWindowDetection panel,
        out CaptureResult panelCrop)
    {
        frame = null!;
        panel = null!;
        panelCrop = null!;
        if (_frames is null || _interactionWindows is null)
            return false;

        var current = _frames.GetLatest(window.Hwnd);
        if (current is null)
            return false;
        var detected = _interactionWindows.Detect(current).Detections
            .Where(item => string.Equals(item.ClassName, "material_panel", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (detected is null || detected.Confidence < 0.75)
            return false;
        var crop = OpenTextCropUtil.Crop(current, detected.Box, pad: 0);
        if (crop is null)
            return false;

        frame = current;
        panel = detected;
        panelCrop = crop;
        return true;
    }

    private Task<MaterialCraftActionResult> ExecuteStepAsync(
        BoundGameWindow window,
        MaterialCraftStep step,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        return step.Action switch
        {
            MaterialCraftStepAction.Wait => Task.FromResult(MaterialCraftActionResult.Executed),
            MaterialCraftStepAction.Text => Task.FromResult(ToResult(!string.IsNullOrWhiteSpace(step.Text)
                && _input.SendText(window.Hwnd, step.Text, cfg.DispatchMode))),
            MaterialCraftStepAction.Click => Task.FromResult(ToResult(
                _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode))),
            MaterialCraftStepAction.HotkeyThenClick => Task.FromResult(ToResult(
                SendHotkey(window, step.Hotkey, cfg)
                && _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode))),
            MaterialCraftStepAction.SelectRecipe => Task.FromResult(ExecuteSelectRecipe(window, step, cfg)),
            MaterialCraftStepAction.Process => ExecuteProcessAsync(window, step, cfg, token),
            MaterialCraftStepAction.Confirm => ExecuteConfirmAsync(window, step, cfg, token),
            MaterialCraftStepAction.CleanupBag => Task.FromResult(ToResult(ExecuteStepCleanup(window, step, cfg))),
            MaterialCraftStepAction.OpenPanel => ExecuteOpenPanelAsync(window, step, cfg, token),
            MaterialCraftStepAction.Hotkey => Task.FromResult(ToResult(SendHotkey(window, step.Hotkey, cfg))),
            _ => Task.FromResult(MaterialCraftActionResult.Blocked)
        };
    }

    private async Task<MaterialCraftActionResult> ExecuteOpenPanelAsync(
        BoundGameWindow window,
        MaterialCraftStep step,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        if (!cfg.RequireVisualConfirmation)
            return ToResult(SendHotkey(window, step.Hotkey, cfg));

        if (_frames is null || _interactionWindows is null || _visionExecutor is null)
        {
            Log.Warn(Name, "材料加工入口视觉链路不可用，已阻止盲目点击。", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        var frame = _frames.GetLatest(window.Hwnd);
        if (frame is null)
            return MaterialCraftActionResult.Retry;

        if (HasPanel(frame, "material_panel"))
            return MaterialCraftActionResult.Executed;

        if (_interactionControls?.RuntimeAuthorized != true)
        {
            Log.Warn(Name, "材料加工入口仅完成只读识别，当前没有已授权生活技能控件。", window.Id);
            return MaterialCraftActionResult.EntryUnavailable;
        }

        if (_entryLocator?.IsAvailable != true)
        {
            Log.Warn(Name, "材料加工面板未打开，入口定位模型尚不可用。", window.Id);
            return MaterialCraftActionResult.EntryUnavailable;
        }

        var entry = _entryLocator.Locate(frame);
        if (!entry.Found || entry.Box.IsEmpty || entry.Confidence < 0.75)
        {
            Log.Warn(Name, $"材料加工入口未达到安全点击阈值：{entry.Message}", window.Id);
            return MaterialCraftActionResult.EntryUnavailable;
        }

        var decision = new VisionActionDecision(
            true,
            VisionActionKind.Click,
            entry.Box.X + entry.Box.Width / 2,
            entry.Box.Y + entry.Box.Height / 2,
            $"材料加工入口：{entry.Source}");
        var global = _global?.Current ?? new GlobalConfig { InputDispatchMode = cfg.DispatchMode };
        if (!_visionExecutor.TryExecuteClick(window, decision, global))
            return MaterialCraftActionResult.Retry;

        var freshFrame = await GetFreshFrameAsync(window.Hwnd, frame.Sequence, 1000, token).ConfigureAwait(false);
        if (freshFrame is null || !HasPanel(freshFrame, "material_panel"))
        {
            Log.Warn(Name, "点击入口后未确认材料加工面板，准备重试。", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        Log.Info(Name, "已定位材料加工入口并确认面板打开。", window.Id);
        return MaterialCraftActionResult.Executed;
    }

    private MaterialCraftActionResult ExecuteSelectRecipe(BoundGameWindow window, MaterialCraftStep step, MaterialCraftConfig cfg)
    {
        if (cfg.RequireVisualConfirmation)
        {
            Log.Warn(Name, "配方列表尚无可靠视觉语义，已阻止盲点配方。", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        var hotkeyOk = string.IsNullOrWhiteSpace(step.Hotkey) || SendHotkey(window, step.Hotkey, cfg);
        return ToResult(hotkeyOk && _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode));
    }

    private Task<MaterialCraftActionResult> ExecuteProcessAsync(
        BoundGameWindow window,
        MaterialCraftStep step,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        if (cfg.RequireVisualConfirmation)
        {
            if (cfg.ProcessUntilMaterialsExhausted && _openText?.IsAvailable != true)
            {
                Log.Warn(Name, "L2 文本识别不可用，无法确认材料耗尽，已阻止连续加工。", window.Id);
                return Task.FromResult(MaterialCraftActionResult.Blocked);
            }

            var requestedAction = string.Equals(step.Text, "manufacture_all", StringComparison.OrdinalIgnoreCase)
                ? "manufacture_all"
                : "manufacture";
            return ExecuteVisionPanelActionAsync(
                window,
                cfg,
                "material_panel",
                requestedAction,
                requireOpenText: false,
                inspectMaterialFeedback: cfg.ProcessUntilMaterialsExhausted,
                token);
        }

        if (!string.IsNullOrWhiteSpace(step.Hotkey))
            return Task.FromResult(ToResult(SendHotkey(window, step.Hotkey, cfg)));

        return Task.FromResult(ToResult(_input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode)));
    }

    private Task<MaterialCraftActionResult> ExecuteConfirmAsync(
        BoundGameWindow window,
        MaterialCraftStep step,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        if (cfg.RequireVisualConfirmation)
            return ExecuteVisionPanelActionAsync(
                window,
                cfg,
                "confirm_dialog",
                "confirm",
                requireOpenText: true,
                inspectMaterialFeedback: false,
                token);

        if (!string.IsNullOrWhiteSpace(step.Hotkey))
            return Task.FromResult(ToResult(SendHotkey(window, step.Hotkey, cfg)));

        return Task.FromResult(ToResult(_input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode)));
    }

    private async Task<MaterialCraftActionResult> ExecuteVisionPanelActionAsync(
        BoundGameWindow window,
        MaterialCraftConfig cfg,
        string panelClass,
        string requestedAction,
        bool requireOpenText,
        bool inspectMaterialFeedback,
        CancellationToken token)
    {
        if (string.Equals(panelClass, "material_panel", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteV3MaterialPanelActionAsync(
                window,
                cfg,
                requestedAction,
                requireOpenText,
                inspectMaterialFeedback,
                token).ConfigureAwait(false);
        }

        if (_frames is null || _visionSlots is null || _visionPlanner is null || _visionExecutor is null)
        {
            Log.Warn(Name, "Vision-confirmed material processing is unavailable; the action was blocked.", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        var frame = _frames.GetLatest(window.Hwnd);
        if (frame is null)
        {
            Log.Warn(Name, "No current client frame is available; the material action was blocked.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var panels = _visionSlots.AnalyzeAuxPanels(frame, new[] { panelClass });
        var panel = panels.Detections
            .Where(item => string.Equals(item.ClassName, panelClass, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (panel is null)
        {
            Log.Warn(Name, $"Required panel was not confirmed: {panelClass}. {panels.Message}", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var crop = OpenTextCropUtil.Crop(frame, panel.Box, pad: 0);
        if (crop is null)
        {
            Log.Warn(Name, $"Panel crop failed: {panelClass}.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var text = string.Empty;
        if (requireOpenText)
        {
            if (_openText?.IsAvailable != true)
            {
                Log.Warn(Name, $"OpenText confirmation is unavailable for {panelClass}; the action was blocked.", window.Id);
                return MaterialCraftActionResult.Blocked;
            }
            text = _openText.Recognize(crop, OpenTextSlot.DialogOption).Text;
        }

        var analysis = _visionSlots.AnalyzePanel(
            crop,
            panel.Box,
            panelClass,
            requestedAction,
            text,
            l1Confidence: panel.Confidence);
        if (!analysis.IsConfirmedCandidate || string.IsNullOrWhiteSpace(analysis.RawJson))
        {
            Log.Warn(Name, $"Panel action was not confirmed: {panelClass}/{requestedAction}. {analysis.Message}", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        using var document = JsonDocument.Parse(analysis.RawJson);
        var decision = _visionPlanner.Plan("policy", JsonSerializer.Serialize(new
        {
            slot_result = document.RootElement.Clone(),
            policy_mode = "material"
        }));
        if (decision.Action != VisionActionKind.Click)
        {
            Log.Warn(Name, $"Material policy returned no safe click: {decision.Reason}", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var global = _global?.Current ?? new GlobalConfig { InputDispatchMode = cfg.DispatchMode };
        if (!_visionExecutor.TryExecuteClick(window, decision, global))
        {
            Log.Warn(Name, $"Confirmed material click failed: {decision.Reason}", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        Log.Info(Name, $"Vision-confirmed material action executed: {panelClass}/{requestedAction}.", window.Id);
        if (inspectMaterialFeedback)
        {
            var feedback = await ConfirmMaterialFeedbackAsync(window, frame.Sequence, token).ConfigureAwait(false);
            return feedback switch
            {
                MaterialCraftFeedbackKind.Exhausted => MaterialCraftActionResult.Exhausted,
                MaterialCraftFeedbackKind.Busy => MaterialCraftActionResult.Busy,
                MaterialCraftFeedbackKind.Completed => MaterialCraftActionResult.Completed,
                MaterialCraftFeedbackKind.Cooldown => MaterialCraftActionResult.Cooldown,
                MaterialCraftFeedbackKind.InsufficientResource => MaterialCraftActionResult.InsufficientResource,
                MaterialCraftFeedbackKind.InvalidAction => MaterialCraftActionResult.InvalidAction,
                _ => MaterialCraftActionResult.Retry
            };
        }

        return MaterialCraftActionResult.Executed;
    }

    private async Task<MaterialCraftActionResult> ExecuteV3MaterialPanelActionAsync(
        BoundGameWindow window,
        MaterialCraftConfig cfg,
        string requestedAction,
        bool requireOpenText,
        bool inspectMaterialFeedback,
        CancellationToken token)
    {
        if (_frames is null
            || _interactionWindows is null
            || _interactionControls is null
            || _visionExecutor is null)
        {
            Log.Warn(Name, "Material V3 perception is unavailable; the action was blocked.", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        if (!_interactionControls.IsAvailable || !_interactionControls.RuntimeAuthorized)
        {
            Log.Warn(
                Name,
                $"Material controls are observation-only ({_interactionControls.Message}); action={requestedAction} blocked.",
                window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        var firstFrame = _frames.GetLatest(window.Hwnd);
        if (firstFrame is null
            || !TryLocateV3Panel(firstFrame, "material_panel", out var firstPanel))
        {
            Log.Warn(Name, "Material panel V3 was not found on the current frame.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var stableFrame = await GetFreshFrameAsync(
            window.Hwnd,
            firstFrame.Sequence,
            700,
            token).ConfigureAwait(false);
        if (stableFrame is null
            || !TryLocateV3Panel(stableFrame, "material_panel", out var stablePanel)
            || IntersectionOverUnion(firstPanel.Box, stablePanel.Box) < 0.85)
        {
            Log.Warn(Name, "Material panel did not remain stable across two frames.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        var layout = _interactionControls.Map(stableFrame.Sequence, stablePanel);
        if (!layout.RuntimeAuthorized || layout.FrameSequence != stableFrame.Sequence)
        {
            Log.Warn(Name, "Material control layout is stale or not runtime-authorized.", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        var control = layout.Controls.SingleOrDefault(item =>
            string.Equals(item.ControlId, requestedAction, StringComparison.Ordinal));
        if (control is null
            || control.ClickX is null
            || control.ClickY is null
            || control.SafeClickBox.IsEmpty
            || !ContainsPoint(control.SafeClickBox, control.ClickX.Value, control.ClickY.Value)
            || !Contains(layout.PanelBox, control.SafeClickBox))
        {
            Log.Warn(Name, $"Material control is missing or unsafe: {requestedAction}.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        if (control.RuntimeDefault.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            && !cfg.EnabledIntent)
        {
            Log.Warn(Name, $"Material resource action lacks explicit runtime intent: {requestedAction}.", window.Id);
            return MaterialCraftActionResult.Blocked;
        }

        if (requireOpenText)
        {
            if (_openText?.IsAvailable != true)
            {
                Log.Warn(Name, "Material action requires OpenText but OCR is unavailable.", window.Id);
                return MaterialCraftActionResult.Blocked;
            }

            var crop = OpenTextCropUtil.Crop(stableFrame, stablePanel.Box, pad: 0);
            var text = crop is null
                ? OpenTextResult.Empty(OpenTextSlot.Generic, "material panel crop failed")
                : _openText.Recognize(crop, OpenTextSlot.Generic);
            if (!text.HasText)
            {
                Log.Warn(Name, $"Material panel text was not confirmed before {requestedAction}.", window.Id);
                return MaterialCraftActionResult.Retry;
            }
        }

        var decision = new VisionActionDecision(
            true,
            VisionActionKind.Click,
            control.ClickX.Value,
            control.ClickY.Value,
            $"interaction-controls-v3:{layout.AssetVersion}:{requestedAction}:frame={stableFrame.Sequence}");
        var global = _global?.Current ?? new GlobalConfig { InputDispatchMode = cfg.DispatchMode };
        if (!_visionExecutor.TryExecuteClick(window, decision, global))
        {
            Log.Warn(Name, $"Material V3 click failed: {requestedAction}.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        if (inspectMaterialFeedback)
        {
            Machine.Goto(MaterialCraftState.ObserveFeedback);
            var feedback = await ConfirmMaterialFeedbackAsync(window, stableFrame.Sequence, token).ConfigureAwait(false);
            return feedback switch
            {
                MaterialCraftFeedbackKind.Exhausted => MaterialCraftActionResult.Exhausted,
                MaterialCraftFeedbackKind.Busy => MaterialCraftActionResult.Busy,
                MaterialCraftFeedbackKind.Completed => MaterialCraftActionResult.Completed,
                MaterialCraftFeedbackKind.Cooldown => MaterialCraftActionResult.Cooldown,
                MaterialCraftFeedbackKind.InsufficientResource => MaterialCraftActionResult.InsufficientResource,
                MaterialCraftFeedbackKind.InvalidAction => MaterialCraftActionResult.InvalidAction,
                _ => MaterialCraftActionResult.Retry
            };
        }

        var postFrame = await GetFreshFrameAsync(window.Hwnd, stableFrame.Sequence, 900, token).ConfigureAwait(false);
        if (postFrame is null || !HasPanel(postFrame, "material_panel"))
        {
            Log.Warn(Name, $"Material postcondition was not observed after {requestedAction}.", window.Id);
            return MaterialCraftActionResult.Retry;
        }

        return MaterialCraftActionResult.Executed;
    }

    private async Task<MaterialCraftFeedbackKind> ConfirmMaterialFeedbackAsync(
        BoundGameWindow window,
        long previousSequence,
        CancellationToken token)
    {
        var firstFrame = await GetFreshFrameAsync(window.Hwnd, previousSequence, 900, token).ConfigureAwait(false);
        if (firstFrame is null)
            return MaterialCraftFeedbackKind.Unknown;

        var first = ReadMaterialFeedback(window, firstFrame.Sequence);
        if (first != MaterialCraftFeedbackKind.Exhausted)
            return first;

        var secondFrame = await GetFreshFrameAsync(window.Hwnd, firstFrame.Sequence, 500, token).ConfigureAwait(false);
        if (secondFrame is null)
            return MaterialCraftFeedbackKind.Unknown;

        var second = ReadMaterialFeedback(window, secondFrame.Sequence);
        if (second == MaterialCraftFeedbackKind.Completed)
            return MaterialCraftFeedbackKind.Completed;
        return second == MaterialCraftFeedbackKind.Exhausted
            ? MaterialCraftFeedbackKind.Exhausted
            : second;
    }

    private async Task<MaterialCraftFeedbackKind> WaitForMaterialProcessingAsync(
        BoundGameWindow window,
        MaterialCraftConfig cfg,
        CancellationToken token)
    {
        if (_frames is null)
            return MaterialCraftFeedbackKind.Unknown;

        var startedAt = DateTimeOffset.UtcNow;
        var latest = _frames.GetLatest(window.Hwnd, 0);
        var previousSequence = latest?.Sequence ?? 0;
        var unknownFrames = 0;
        long exhaustedFrameSequence = 0;
        var noStateDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        while (!token.IsCancellationRequested
               && DateTimeOffset.UtcNow - startedAt < TimeSpan.FromMinutes(cfg.MaxContinuousProcessMinutes)
               && DateTimeOffset.UtcNow < noStateDeadline)
        {
            var frame = await GetFreshFrameAsync(window.Hwnd, previousSequence, 1200, token).ConfigureAwait(false);
            if (frame is null)
                continue;

            previousSequence = frame.Sequence;
            var feedback = ReadMaterialFeedback(window, frame.Sequence);
            if (feedback == MaterialCraftFeedbackKind.Busy)
            {
                unknownFrames = 0;
                await Delay(180, token).ConfigureAwait(false);
                continue;
            }

            if (feedback == MaterialCraftFeedbackKind.Exhausted)
            {
                if (exhaustedFrameSequence > 0 && exhaustedFrameSequence != frame.Sequence)
                    return MaterialCraftFeedbackKind.Exhausted;
                exhaustedFrameSequence = frame.Sequence;
                unknownFrames = 0;
                continue;
            }

            if (feedback == MaterialCraftFeedbackKind.Completed)
            {
                exhaustedFrameSequence = 0;
                return MaterialCraftFeedbackKind.Completed;
            }

            if (feedback is MaterialCraftFeedbackKind.Cooldown
                or MaterialCraftFeedbackKind.InsufficientResource
                or MaterialCraftFeedbackKind.InvalidAction)
                return feedback;

            unknownFrames++;
            if (unknownFrames >= 5)
                return MaterialCraftFeedbackKind.Unknown;

            await Delay(120, token).ConfigureAwait(false);
        }

        return MaterialCraftFeedbackKind.Unknown;
    }

    private MaterialCraftFeedbackKind ReadMaterialFeedback(BoundGameWindow window, long expectedFrameSequence)
    {
        if (_perception is null || _global is null || expectedFrameSequence <= 0)
            return MaterialCraftFeedbackKind.Unknown;
        var snapshot = _perception.Read(
            window,
            _global.Current,
            PerceptionReadOptions.SystemFeedbackText);
        if (snapshot.FrameSequence != expectedFrameSequence
            || snapshot.SystemFeedback.FrameSequence != expectedFrameSequence
            || !snapshot.SystemFeedback.Available)
            return MaterialCraftFeedbackKind.Unknown;

        var semantics = snapshot.SystemFeedback.Observations
            .Where(item => item.FrameSequence == expectedFrameSequence)
            .Select(item => item.Semantic)
            .ToHashSet();
        if (semantics.Contains(SystemFeedbackSemanticKind.MaterialCompleted))
            return MaterialCraftFeedbackKind.Completed;
        if (semantics.Contains(SystemFeedbackSemanticKind.MaterialBusy))
            return MaterialCraftFeedbackKind.Busy;
        if (semantics.Contains(SystemFeedbackSemanticKind.MaterialExhausted))
            return MaterialCraftFeedbackKind.Exhausted;
        if (semantics.Contains(SystemFeedbackSemanticKind.Cooldown))
            return MaterialCraftFeedbackKind.Cooldown;
        if (semantics.Contains(SystemFeedbackSemanticKind.InsufficientResource))
            return MaterialCraftFeedbackKind.InsufficientResource;
        if (semantics.Contains(SystemFeedbackSemanticKind.InvalidAction))
            return MaterialCraftFeedbackKind.InvalidAction;
        return MaterialCraftFeedbackKind.Unknown;
    }

    private bool HasPanel(CaptureResult frame, string panelClass)
    {
        if (string.Equals(panelClass, "material_panel", StringComparison.OrdinalIgnoreCase))
            return TryLocateV3Panel(frame, panelClass, out _);

        return _visionSlots?.AnalyzeAuxPanels(frame, new[] { panelClass }).Detections.Any(
            item => string.Equals(item.ClassName, panelClass, StringComparison.OrdinalIgnoreCase)
                    && item.Confidence >= 0.60) == true;
    }

    private bool TryLocateV3Panel(
        CaptureResult frame,
        string panelClass,
        out InteractionWindowDetection panel)
    {
        panel = null!;
        if (_interactionWindows is null)
            return false;

        panel = _interactionWindows.Detect(frame).Detections
            .Where(item => string.Equals(item.ClassName, panelClass, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault()!;
        return panel is not null && !panel.Box.IsEmpty && panel.Confidence >= 0.75;
    }

    private static bool Contains(VisionBox outer, VisionBox inner) =>
        !outer.IsEmpty
        && !inner.IsEmpty
        && inner.X >= outer.X
        && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width
        && inner.Y + inner.Height <= outer.Y + outer.Height;

    private static bool ContainsPoint(VisionBox box, int x, int y) =>
        !box.IsEmpty
        && x >= box.X
        && y >= box.Y
        && x < box.X + box.Width
        && y < box.Y + box.Height;

    private static double IntersectionOverUnion(VisionBox left, VisionBox right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        if (intersection <= 0)
            return 0;

        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
    }

    private async Task<CaptureResult?> GetFreshFrameAsync(
        nint hwnd,
        long previousSequence,
        int timeoutMilliseconds,
        CancellationToken token)
    {
        if (_frames is null)
            return null;

        var deadline = Environment.TickCount64 + Math.Max(50, timeoutMilliseconds);
        while (!token.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            var frame = _frames.GetLatest(hwnd, 0);
            if (frame is not null && (previousSequence <= 0 || frame.Sequence != previousSequence))
                return frame;

            await Task.Delay(35, token).ConfigureAwait(false);
        }

        return null;
    }

    private bool ExecuteStepCleanup(BoundGameWindow window, MaterialCraftStep step, MaterialCraftConfig cfg)
    {
        var hotkeyOk = string.IsNullOrWhiteSpace(step.Hotkey) || SendHotkey(window, step.Hotkey, cfg);
        if (!hotkeyOk)
            return false;

        return step.ClickX > 0 && step.ClickY > 0
            ? _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode)
            : hotkeyOk;
    }

    private bool ExecuteCleanup(BoundGameWindow window, MaterialCraftConfig cfg)
    {
        var executed = false;
        if (!string.IsNullOrWhiteSpace(cfg.CleanupHotkey))
            executed |= _input.SendKey(window.Hwnd, cfg.CleanupHotkey, cfg.DispatchMode);

        if (cfg.CleanupClickX > 0 && cfg.CleanupClickY > 0)
            executed |= _input.Click(window.Hwnd, cfg.CleanupClickX, cfg.CleanupClickY, cfg.DispatchMode);

        Log.Info(Name, executed ? "已执行清包动作。" : "清包已启用但未配置动作。", window.Id);
        return executed;
    }

    private bool SendHotkey(BoundGameWindow window, string hotkey, MaterialCraftConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "加工热键为空。", window.Id);
            return false;
        }

        return _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);
    }

    private bool ShouldStop(MaterialCraftConfig cfg)
    {
        if (cfg.LoopPlan && !cfg.StopAfterBatch)
            return false;

        return _batchIndex >= cfg.BatchCount;
    }

    private static int EffectiveRetry(MaterialCraftStep step, MaterialCraftConfig cfg) =>
        Math.Max(0, step.RetryLimit > 0 ? step.RetryLimit : cfg.MaxStepRetry);

    private static MaterialCraftActionResult ToResult(bool executed) =>
        executed ? MaterialCraftActionResult.Executed : MaterialCraftActionResult.Retry;

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);

    private enum MaterialCraftRunResult
    {
        Completed = 0,
        Failed,
        Blocked,
        EntryUnavailable
    }
}
