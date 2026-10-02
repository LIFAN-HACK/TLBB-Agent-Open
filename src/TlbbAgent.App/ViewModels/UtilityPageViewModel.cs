using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class UtilityActionStepViewModel : ObservableObject
{
    private readonly UtilityActionStep _model;
    private readonly Action _onChanged;

    public UtilityActionStepViewModel(UtilityActionStep model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public bool Enabled { get => _model.Enabled; set { _model.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public UtilityActionGroup Group { get => _model.Group; set { _model.Group = value; OnPropertyChanged(); _onChanged(); } }
    public UtilityActionKind Kind { get => _model.Kind; set { _model.Kind = value; OnPropertyChanged(); _onChanged(); } }
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public string Text { get => _model.Text; set { _model.Text = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int DelayAfterMs { get => _model.DelayAfterMs; set { _model.DelayAfterMs = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed class CleanupCandidateViewModel
{
    public CleanupCandidateViewModel(ItemCleanupCandidate candidate)
    {
        Index = candidate.Slot.Index + 1;
        Position = $"{candidate.Slot.Row + 1}-{candidate.Slot.Column + 1}";
        ItemName = string.IsNullOrWhiteSpace(candidate.Slot.ItemName) ? "未识别" : candidate.Slot.ItemName;
        Decision = candidate.Decision switch
        {
            ItemCleanupDecision.Keep => "保留",
            ItemCleanupDecision.Store => "存放",
            ItemCleanupDecision.Trade => "交易",
            ItemCleanupDecision.SellCandidate => "可卖候选",
            ItemCleanupDecision.BlockedUnknown => "未知保护",
            _ => candidate.Decision.ToString()
        };
        Source = candidate.Slot.Source;
        Reason = string.IsNullOrWhiteSpace(Source)
            ? candidate.Reason
            : $"{Source} | {candidate.Reason}";
        Score = candidate.Slot.OccupancyScore.ToString("0.00");
    }

    public int Index { get; }
    public string Position { get; }
    public string ItemName { get; }
    public string Decision { get; }
    public string Reason { get; }
    public string Score { get; }
    public string Source { get; }
}

public sealed partial class UtilityPageViewModel : PageViewModel
{
    private static readonly string DefaultImportedDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductIdentity.DataFolderName,
        "legacy-imports");

    private readonly IUtilityConfigService _service;
    private readonly IWindowBindingService _binding;
    private readonly IBagCleanupService _bagCleanup;
    private readonly IBagCleanupReportService _bagCleanupReport;
    private readonly IInventoryRecognitionService _inventory;
    private readonly IItemPolicyService _itemPolicy;
    private readonly IInputService _input;
    private readonly ILogService _log;
    private BagCleanupResult? _lastCleanupResult;

    public UtilityPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IUtilityConfigService service,
        IWindowBindingService binding,
        IBagCleanupService bagCleanup,
        IBagCleanupReportService bagCleanupReport,
        IInventoryRecognitionService inventory,
        IItemPolicyService itemPolicy,
        IInputService input,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _binding = binding;
        _bagCleanup = bagCleanup;
        _bagCleanupReport = bagCleanupReport;
        _inventory = inventory;
        _itemPolicy = itemPolicy;
        _input = input;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        SubscribeController();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;

    public Array Groups { get; } = Enum.GetValues(typeof(UtilityActionGroup));
    public Array Kinds { get; } = Enum.GetValues(typeof(UtilityActionKind));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public Array CleanupModes { get; } = Enum.GetValues(typeof(BagCleanupExecutionMode));
    public Array CleanupScanModes { get; } = Enum.GetValues(typeof(InventoryScanMode));
    public ObservableCollection<UtilityActionStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> KeepItems { get; } = new();
    public ObservableCollection<CleanupCandidateViewModel> CleanupCandidates { get; } = new();

    private UtilityConfig C => _service.Current;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string PlanSummary => $"流程 {C.PlanName}，启用动作 {C.Steps.Count(s => s.Enabled)} 个";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string PlanName { get => C.PlanName; set { C.PlanName = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public bool StopAfterSequence { get => C.StopAfterSequence; set { C.StopAfterSequence = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public bool RepeatSequence { get => C.RepeatSequence; set { C.RepeatSequence = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int RepeatIntervalMs { get => C.RepeatIntervalMs; set { C.RepeatIntervalMs = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }

    public BagCleanupExecutionMode CleanupMode { get => C.Cleanup.Mode; set { C.Cleanup.Mode = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public bool CleanupOpenBagBeforeScan { get => C.Cleanup.OpenBagBeforeScan; set { C.Cleanup.OpenBagBeforeScan = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public string CleanupOpenBagHotkey
    {
        get => C.Cleanup.OpenBagHotkey;
        set
        {
            C.Cleanup.OpenBagHotkey = value?.Trim() ?? "";
            C.Cleanup.OpenBagHotkeyExplicitlyConfigured = !string.IsNullOrWhiteSpace(C.Cleanup.OpenBagHotkey);
            OnPropertyChanged();
            SaveCurrentProfileQuietly();
        }
    }
    public bool CleanupGridEnabled { get => C.Cleanup.Grid.Enabled; set { C.Cleanup.Grid.Enabled = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGridX { get => C.Cleanup.Grid.X; set { C.Cleanup.Grid.X = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGridY { get => C.Cleanup.Grid.Y; set { C.Cleanup.Grid.Y = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGridColumns { get => C.Cleanup.Grid.Columns; set { C.Cleanup.Grid.Columns = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGridRows { get => C.Cleanup.Grid.Rows; set { C.Cleanup.Grid.Rows = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupCellWidth { get => C.Cleanup.Grid.CellWidth; set { C.Cleanup.Grid.CellWidth = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupCellHeight { get => C.Cleanup.Grid.CellHeight; set { C.Cleanup.Grid.CellHeight = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGapX { get => C.Cleanup.Grid.GapX; set { C.Cleanup.Grid.GapX = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupGapY { get => C.Cleanup.Grid.GapY; set { C.Cleanup.Grid.GapY = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public InventoryScanMode CleanupGridScanMode { get => C.Cleanup.Grid.ScanMode; set { C.Cleanup.Grid.ScanMode = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public bool CleanupTemplateEnabled { get => C.Cleanup.TemplateMatching.Enabled; set { C.Cleanup.TemplateMatching.Enabled = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public string CleanupTemplateIndexPath { get => C.Cleanup.TemplateMatching.IndexPath; set { C.Cleanup.TemplateMatching.IndexPath = value; OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public double CleanupTemplateThreshold { get => C.Cleanup.TemplateMatching.Threshold; set { C.Cleanup.TemplateMatching.Threshold = Math.Clamp(value, 0.75, 1.0); OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupTemplateInnerPadding { get => C.Cleanup.TemplateMatching.InnerPadding; set { C.Cleanup.TemplateMatching.InnerPadding = Math.Clamp(value, 0, 8); OnPropertyChanged(); SaveCurrentProfileQuietly(); } }
    public int CleanupKeepCount => C.Cleanup.Policy.KeepItems.Count;
    public int CleanupStoreCount => C.Cleanup.Policy.StoreItems.Count;
    public int CleanupTradeCount => C.Cleanup.Policy.TradeItems.Count;
    public int CleanupSellKeywordCount => C.Cleanup.Policy.SellKeywords.Count;

    [ObservableProperty] private string _operationStatus = "配置动作后启动。";
    [ObservableProperty] private string _cleanupStatus = "未分析。默认只生成候选，不执行出售。";
    [ObservableProperty] private string _cleanupScanSummary = "占用/空格/候选等待分析。";
    [ObservableProperty] private string _cleanupSafetySummary = "安全状态：未分析。";
    [ObservableProperty] private string _cleanupTemplateSummary = "模板匹配：未启用。";
    [ObservableProperty] private UtilityActionGroup _newGroup = UtilityActionGroup.Custom;
    [ObservableProperty] private UtilityActionKind _newKind = UtilityActionKind.Hotkey;
    [ObservableProperty] private string _newLabel = "自定义动作";
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private int _newClickX = 520;
    [ObservableProperty] private int _newClickY = 360;
    [ObservableProperty] private int _newDelayAfterMs = 300;

    [RelayCommand]
    private void AddStep()
    {
        _service.AddStep(new UtilityActionStep
        {
            Enabled = true,
            Group = NewGroup,
            Kind = NewKind,
            Label = NewLabel.Trim(),
            Hotkey = NewHotkey.Trim(),
            Text = NewText.Trim(),
            ClickX = NewClickX,
            ClickY = NewClickY,
            DelayAfterMs = NewDelayAfterMs
        });
        SaveCurrentProfileQuietly();
        OperationStatus = $"已添加动作：{NewLabel}。";
    }

    [RelayCommand]
    private void RemoveStep(UtilityActionStepViewModel? step)
    {
        if (step is null)
            return;

        if (_service.RemoveStep(step.Id))
        {
            SaveCurrentProfileQuietly();
            OperationStatus = $"已删除动作：{step.Label}。";
        }
    }

    [RelayCommand]
    private void MoveUp(UtilityActionStepViewModel? step)
    {
        if (step is null) return;
        _service.MoveStep(step.Id, -1);
        SaveCurrentProfileQuietly();
    }

    [RelayCommand]
    private void MoveDown(UtilityActionStepViewModel? step)
    {
        if (step is null) return;
        _service.MoveStep(step.Id, 1);
        SaveCurrentProfileQuietly();
    }

    [RelayCommand]
    private void ImportKeepItems()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入保留物品",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var count = _service.LoadKeepItems(dialog.FileName);
            SaveCurrentProfileQuietly();
            OperationStatus = $"保留物品已载入：{count}。";
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("Utility", OperationStatus);
        }
    }

    [RelayCommand]
    private void ImportLegacyCleanupLists()
    {
        try
        {
            var count = _itemPolicy.LoadLegacyItemLists(DefaultImportedDataRoot, C.Cleanup.Policy);
            C.KeepItems = C.Cleanup.Policy.KeepItems.ToList();
            CleanupStatus = $"已导入旧物品清单：{count} 条。";
            _service.Save(ProfileNameOrDefault());
            RefreshCleanupCounts();
            RebuildKeepItems();
        }
        catch (Exception ex)
        {
            CleanupStatus = "旧物品清单导入失败。";
            _log.Error("BagCleanup", $"{CleanupStatus} {ex.Message}");
        }
    }

    [RelayCommand]
    private void AnalyzeCleanup()
    {
        var window = _binding.Current;
        if (window is null)
        {
            CleanupStatus = "请先绑定游戏窗口。";
            return;
        }

        C.Cleanup.Mode = C.Cleanup.Mode == BagCleanupExecutionMode.Disabled
            ? BagCleanupExecutionMode.DryRun
            : C.Cleanup.Mode;
        C.Cleanup.AllowActualSell = false;

        var result = _bagCleanup.Analyze(window, C.Cleanup);
        PresentCleanupResult(result);
        _service.Save(ProfileNameOrDefault());
        RefreshCleanupCounts();
        _log.Info("BagCleanup", CleanupStatus, window.Id);
    }

    [RelayCommand]
    private async Task OpenBagThenAnalyzeCleanup()
    {
        var window = _binding.Current;
        if (window is null)
        {
            CleanupStatus = "请先绑定游戏窗口。";
            return;
        }

        C.Cleanup.Mode = C.Cleanup.Mode == BagCleanupExecutionMode.Disabled
            ? BagCleanupExecutionMode.DryRun
            : C.Cleanup.Mode;
        C.Cleanup.AllowActualSell = false;

        if (!C.Cleanup.OpenBagHotkeyExplicitlyConfigured
            || string.IsNullOrWhiteSpace(C.Cleanup.OpenBagHotkey))
        {
            CleanupStatus = "未配置开包快捷键，且入口图标视觉链路尚不可用；请先手动打开背包后分析。";
            _log.Warn("BagCleanup", CleanupStatus, window.Id);
            return;
        }

        var hotkey = C.Cleanup.OpenBagHotkey.Trim();
        if (!_input.TryParseHotkey(hotkey, out var error))
        {
            CleanupStatus = $"打开背包热键无效：{error}";
            _log.Warn("BagCleanup", CleanupStatus, window.Id);
            return;
        }

        var yield = _input.GetYieldStatus(window.Hwnd, C.DispatchMode);
        if (yield.ShouldYield)
        {
            CleanupStatus = $"开包已让控：{ShortYieldReason(yield.Reason)}";
            _log.Warn("BagCleanup", CleanupStatus, window.Id);
            return;
        }

        var sent = _input.SendKey(window.Hwnd, hotkey, C.DispatchMode);
        CleanupStatus = sent
            ? $"已发送开包键 {hotkey}，准备只读分析。"
            : $"开包键 {hotkey} 发送失败。";
        _log.Info("BagCleanup", $"{CleanupStatus} 不出售、不点击候选。", window.Id);
        if (!sent)
            return;

        await Task.Delay(350);
        AnalyzeCleanup();
    }

    private static string ShortYieldReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "检测到人工操作，本次自动输入跳过。";

        return reason.Length <= 80 ? reason : reason[..80] + "...";
    }

    [RelayCommand]
    private void AutoCalibrateCleanupGrid()
    {
        var window = _binding.Current;
        if (window is null)
        {
            CleanupStatus = "请先绑定游戏窗口。";
            return;
        }

        var calibration = _inventory.AutoCalibrate(window);
        CleanupCandidates.Clear();
        if (!calibration.Success || calibration.Grid is null)
        {
            CleanupStatus = calibration.Message;
            return;
        }

        CopyGrid(calibration.Grid, C.Cleanup.Grid);
        C.Cleanup.Mode = C.Cleanup.Mode == BagCleanupExecutionMode.Disabled
            ? BagCleanupExecutionMode.DryRun
            : C.Cleanup.Mode;
        C.Cleanup.AllowActualSell = false;
        _service.Save(ProfileNameOrDefault());
        Refresh();

        var result = _bagCleanup.Analyze(window, C.Cleanup);
        PresentCleanupResult(result, calibration.Message);
        _log.Info("BagCleanup", CleanupStatus, window.Id);
    }

    [RelayCommand]
    private void ExportCleanupCandidates()
    {
        if (_lastCleanupResult is null)
        {
            CleanupStatus = "请先执行一次模拟分析。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出清包候选报告",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"清包候选-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _bagCleanupReport.SaveTextReport(dialog.FileName, _lastCleanupResult, C.Cleanup);
            CleanupStatus = $"清包候选报告已导出：{dialog.FileName}";
            _log.Info("BagCleanup", "清包候选报告已导出。");
        }
        catch (Exception ex)
        {
            CleanupStatus = "清包候选报告导出失败。";
            _log.Error("BagCleanup", $"{CleanupStatus} {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseCleanupTemplateIndex()
    {
        var defaultDir = Path.Combine(Environment.CurrentDirectory, "data", "samples", "bag-items");
        var dialog = new OpenFileDialog
        {
            Title = "选择清包模板索引",
            Filter = "模板索引 (index.json)|index.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : Environment.CurrentDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        CleanupTemplateIndexPath = dialog.FileName;
        CleanupTemplateEnabled = true;
        CleanupStatus = "已选择模板索引；下次只读分析会应用模板匹配。";
    }

    [RelayCommand]
    private void SaveCleanupGrid()
    {
        _service.Save(ProfileNameOrDefault());
        CleanupStatus = "清包网格配置已保存。";
    }

    [RelayCommand]
    private void Save()
    {
        var name = ProfileNameOrDefault();
        _service.Save(name);
        OperationStatus = $"已保存便捷配置：{name}";
    }

    [RelayCommand]
    private void Load()
    {
        var name = ProfileNameOrDefault();
        OperationStatus = _service.Load(name) ? $"已加载便捷配置：{name}" : $"未找到便捷配置：{name}";
    }

    private void Refresh()
    {
        RebuildSteps();
        RebuildKeepItems();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(PlanName));
        OnPropertyChanged(nameof(StopAfterSequence));
        OnPropertyChanged(nameof(RepeatSequence));
        OnPropertyChanged(nameof(RepeatIntervalMs));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(CleanupMode));
        OnPropertyChanged(nameof(CleanupOpenBagBeforeScan));
        OnPropertyChanged(nameof(CleanupOpenBagHotkey));
        OnPropertyChanged(nameof(CleanupGridEnabled));
        OnPropertyChanged(nameof(CleanupGridX));
        OnPropertyChanged(nameof(CleanupGridY));
        OnPropertyChanged(nameof(CleanupGridColumns));
        OnPropertyChanged(nameof(CleanupGridRows));
        OnPropertyChanged(nameof(CleanupCellWidth));
        OnPropertyChanged(nameof(CleanupCellHeight));
        OnPropertyChanged(nameof(CleanupGapX));
        OnPropertyChanged(nameof(CleanupGapY));
        OnPropertyChanged(nameof(CleanupGridScanMode));
        OnPropertyChanged(nameof(CleanupTemplateEnabled));
        OnPropertyChanged(nameof(CleanupTemplateIndexPath));
        OnPropertyChanged(nameof(CleanupTemplateThreshold));
        OnPropertyChanged(nameof(CleanupTemplateInnerPadding));
        RefreshCleanupCounts();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var step in _service.Steps.Take(120))
            Steps.Add(new UtilityActionStepViewModel(step, OnStepChanged));
        OnPropertyChanged(nameof(PlanSummary));
    }

    private void OnStepChanged()
    {
        OnPropertyChanged(nameof(PlanSummary));
        SaveCurrentProfileQuietly();
    }

    private void RebuildKeepItems()
    {
        KeepItems.Clear();
        foreach (var item in _service.KeepItems.Take(120))
            KeepItems.Add(item);
    }

    private void RefreshCleanupCounts()
    {
        OnPropertyChanged(nameof(CleanupKeepCount));
        OnPropertyChanged(nameof(CleanupStoreCount));
        OnPropertyChanged(nameof(CleanupTradeCount));
        OnPropertyChanged(nameof(CleanupSellKeywordCount));
    }

    private void PresentCleanupResult(BagCleanupResult result, string? prefix = null)
    {
        _lastCleanupResult = result;
        CleanupCandidates.Clear();
        foreach (var candidate in result.Candidates.Take(120))
            CleanupCandidates.Add(new CleanupCandidateViewModel(candidate));

        CleanupStatus = string.IsNullOrWhiteSpace(prefix)
            ? result.Message
            : $"{prefix.Trim()} {result.Message}";
        CleanupScanSummary = result.Success
            ? $"占用 {result.Scan.OccupiedCount} / 空格 {result.Scan.EmptyCount} / 可卖候选 {result.SellCandidateCount} / 保护 {result.ProtectedCount}"
            : "未识别到可靠背包网格；不会生成出售动作。";
        CleanupSafetySummary = BuildSafetySummary(result.Safety);
        CleanupTemplateSummary = result.TemplateMatch.Enabled
            ? result.TemplateMatch.Message
            : "模板匹配：未启用。";
    }

    private static string BuildSafetySummary(BagCleanupSafety safety)
    {
        static string YesNo(bool value) => value ? "是" : "否";

        return $"只读 {YesNo(safety.DryRun)} / 发输入 {YesNo(safety.SendsInput)} / 鼠标 {YesNo(safety.ClicksMouse)} / 出售 {YesNo(safety.SellsItems)} / 改背包 {YesNo(safety.ModifiesInventory)}";
    }

    private string ProfileNameOrDefault() =>
        string.IsNullOrWhiteSpace(ProfileName) ? "默认便捷" : ProfileName.Trim();

    private void SaveCurrentProfileQuietly()
    {
        if (!string.IsNullOrWhiteSpace(C.ProfileName))
            _service.Save(ProfileNameOrDefault());
    }

    private static void CopyGrid(InventoryGridConfig source, InventoryGridConfig target)
    {
        target.ScanMode = source.ScanMode;
        target.Enabled = source.Enabled;
        target.X = source.X;
        target.Y = source.Y;
        target.Columns = source.Columns;
        target.Rows = source.Rows;
        target.CellWidth = source.CellWidth;
        target.CellHeight = source.CellHeight;
        target.GapX = source.GapX;
        target.GapY = source.GapY;
        target.SlotNameOverrides = new Dictionary<int, string>(source.SlotNameOverrides);
        target.SlotNameSources = new Dictionary<int, string>(source.SlotNameSources);
    }

    private void SubscribeController()
    {
        if (Controller is null)
            return;

        Controller.StateChanged += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(CurrentStateName));
            OnPropertyChanged(nameof(RunState));
        });
    }
}
