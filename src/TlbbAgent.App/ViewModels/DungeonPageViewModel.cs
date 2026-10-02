using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class DungeonScriptStepViewModel : ObservableObject
{
    private readonly DungeonScriptStep _model;
    private readonly Action _onChanged;

    public DungeonScriptStepViewModel(DungeonScriptStep model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public DungeonScriptStep Model => _model;
    public string Id => _model.Id;
    public int Order { get => _model.Order; set { _model.Order = value; OnPropertyChanged(); _onChanged(); } }
    public string StageName { get => _model.StageName; set { _model.StageName = value; OnPropertyChanged(); _onChanged(); } }
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public DungeonStepAction Action { get => _model.Action; set { _model.Action = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int DelayAfterMs { get => _model.DelayAfterMs; set { _model.DelayAfterMs = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed partial class DungeonPageViewModel : PageViewModel
{
    private readonly IDungeonConfigService _service;
    private readonly ILogService _log;

    public DungeonPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IDungeonConfigService service,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        ControllerSubscribe();
        RebuildSteps();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;
    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public Array Modes { get; } = Enum.GetValues(typeof(DungeonMode));
    public Array BossPositions { get; } = Enum.GetValues(typeof(DungeonBossPosition));
    public Array StepActions { get; } = Enum.GetValues(typeof(DungeonStepAction));
    public Array NavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<DungeonScriptStepViewModel> Steps { get; } = new();

    private DungeonConfig C => _service.Current;

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public DungeonMode Mode { get => C.Mode; set { C.Mode = value; OnConfigChanged(); Refresh(); } }
    public int RunCount { get => C.RunCount; set { C.RunCount = value; OnConfigChanged(); } }
    public int TeamNumber { get => C.TeamNumber; set { C.TeamNumber = value; OnConfigChanged(); } }
    public bool IGoLeftBridge { get => C.IGoLeftBridge; set { C.IGoLeftBridge = value; OnConfigChanged(); } }
    public DungeonBossPosition NewThreeBoss2Position { get => C.NewThreeBoss2Position; set { C.NewThreeBoss2Position = value; OnConfigChanged(); } }
    public int NewThreeBoss2CustomX { get => C.NewThreeBoss2CustomX; set { C.NewThreeBoss2CustomX = value; OnConfigChanged(); } }
    public int NewThreeBoss2CustomY { get => C.NewThreeBoss2CustomY; set { C.NewThreeBoss2CustomY = value; OnConfigChanged(); } }
    public bool EnableClearBagAfterFinish { get => C.EnableClearBagAfterFinish; set { C.EnableClearBagAfterFinish = value; OnConfigChanged(); } }
    public bool EnableChainNextTask { get => C.EnableChainNextTask; set { C.EnableChainNextTask = value; OnConfigChanged(); } }
    public string ChainNextModeKey { get => C.ChainNextModeKey; set { C.ChainNextModeKey = value; OnConfigChanged(); } }
    public string StartDungeonHotkey { get => C.StartDungeonHotkey; set { C.StartDungeonHotkey = value; OnConfigChanged(); } }
    public string InteractHotkey { get => C.InteractHotkey; set { C.InteractHotkey = value; OnConfigChanged(); } }
    public string AttackHotkey { get => C.AttackHotkey; set { C.AttackHotkey = value; OnConfigChanged(); } }
    public string ClearBagHotkey { get => C.ClearBagHotkey; set { C.ClearBagHotkey = value; OnConfigChanged(); } }
    public string ExitDungeonHotkey { get => C.ExitDungeonHotkey; set { C.ExitDungeonHotkey = value; OnConfigChanged(); } }
    public int StepIntervalMs { get => C.StepIntervalMs; set { C.StepIntervalMs = value; OnConfigChanged(); } }
    public int BattleCheckIntervalMs { get => C.BattleCheckIntervalMs; set { C.BattleCheckIntervalMs = value; OnConfigChanged(); } }
    public string CompletionKeywords { get => C.CompletionKeywords; set { C.CompletionKeywords = value; OnConfigChanged(); } }

    public ReturnNavigationMode StageNavigationMode { get => C.StageNavigation.Mode; set { C.StageNavigation.Mode = value; OnConfigChanged(); } }
    public string StageNavigationHotkey { get => C.StageNavigation.Hotkey; set { C.StageNavigation.Hotkey = value; OnConfigChanged(); } }
    public int StageNavigationClickX { get => C.StageNavigation.ClickX; set { C.StageNavigation.ClickX = value; OnConfigChanged(); } }
    public int StageNavigationClickY { get => C.StageNavigation.ClickY; set { C.StageNavigation.ClickY = value; OnConfigChanged(); } }
    public int StageNavigationActionIntervalMs { get => C.StageNavigation.ActionIntervalMs; set { C.StageNavigation.ActionIntervalMs = value; OnConfigChanged(); } }
    public InputDispatchMode StageNavigationDispatchMode { get => C.StageNavigation.DispatchMode; set { C.StageNavigation.DispatchMode = value; OnConfigChanged(); } }

    public int CompletionRegionX { get => C.CompletionRegion.X; set { C.CompletionRegion.X = value; OnConfigChanged(); } }
    public int CompletionRegionY { get => C.CompletionRegion.Y; set { C.CompletionRegion.Y = value; OnConfigChanged(); } }
    public int CompletionRegionWidth { get => C.CompletionRegion.Width; set { C.CompletionRegion.Width = value; OnConfigChanged(); } }
    public int CompletionRegionHeight { get => C.CompletionRegion.Height; set { C.CompletionRegion.Height = value; OnConfigChanged(); } }
    public string StepSummary => $"{(Mode == DungeonMode.OldThreeRings ? "老三环" : "新三环")} 步骤 {_service.CurrentSteps.Count} 个";
    public string SourceSummary => C.SourceSummary;
    public string ModeSummary => $"{DungeonModeText(Mode)} / {RunCount} 次 / 队伍 {TeamNumber}";
    public string HotkeySummary => $"进 {Blank(StartDungeonHotkey)} / 交互 {Blank(InteractHotkey)} / 攻击 {Blank(AttackHotkey)}";
    public string FlowSummary
    {
        get
        {
            var clear = EnableClearBagAfterFinish ? $"清包 {Blank(ClearBagHotkey)}" : "清包关";
            var chain = EnableChainNextTask ? $"衔接 {Blank(ChainNextModeKey)}" : "衔接关";
            return $"{clear} / {chain} / 间隔 {StepIntervalMs}ms";
        }
    }
    public string NavigationSummary => StageNavigationMode == ReturnNavigationMode.Disabled
        ? "阶段导航关"
        : $"{NavigationModeText(StageNavigationMode)} {Blank(StageNavigationHotkey)} / {StageNavigationActionIntervalMs}ms";
    public string CompletionSummary => $"完成词 {KeywordCount(CompletionKeywords)} 组 / {RegionText(C.CompletionRegion)}";
    public string ReadySummary
    {
        get
        {
            if (_service.CurrentSteps.Count == 0)
                return "缺少脚本";
            if (RunCount <= 0)
                return "次数无效";
            if (string.IsNullOrWhiteSpace(StartDungeonHotkey))
                return "缺少进本键";
            if (string.IsNullOrWhiteSpace(InteractHotkey))
                return "缺少交互键";
            if (string.IsNullOrWhiteSpace(AttackHotkey))
                return "缺少攻击键";
            if (EnableClearBagAfterFinish && string.IsNullOrWhiteSpace(ClearBagHotkey))
                return "缺少清包键";
            if (EnableChainNextTask && string.IsNullOrWhiteSpace(ChainNextModeKey))
                return "缺少衔接";
            if (StageNavigationMode != ReturnNavigationMode.Disabled && string.IsNullOrWhiteSpace(StageNavigationHotkey))
                return "缺少导航键";
            if (string.IsNullOrWhiteSpace(CompletionKeywords))
                return "缺少完成词";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    [ObservableProperty] private string _operationStatus = "脚本按模式保存。";

    [RelayCommand]
    private void AddStep()
    {
        _service.AddStep(new DungeonScriptStep
        {
            Mode = C.Mode,
            StageName = C.Mode == DungeonMode.OldThreeRings ? "老三环：新步骤" : "新三环：新步骤",
            Action = DungeonStepAction.HotkeyOnly,
            Hotkey = C.InteractHotkey,
            SourceModule = "手动",
            SourcePath = ""
        });
        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemoveStep(DungeonScriptStepViewModel? step)
    {
        if (step is null)
            return;

        if (_service.RemoveStep(step.Id))
            OperationStatus = "已删除。";
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _service.ClearSteps(C.Mode);
        OperationStatus = "已清空。";
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _service.ResetDefaults();
        OperationStatus = "已恢复默认。";
    }

    [RelayCommand]
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认三环" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认三环" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void Refresh()
    {
        RebuildSteps();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(RunCount));
        OnPropertyChanged(nameof(TeamNumber));
        OnPropertyChanged(nameof(IGoLeftBridge));
        OnPropertyChanged(nameof(NewThreeBoss2Position));
        OnPropertyChanged(nameof(NewThreeBoss2CustomX));
        OnPropertyChanged(nameof(NewThreeBoss2CustomY));
        OnPropertyChanged(nameof(EnableClearBagAfterFinish));
        OnPropertyChanged(nameof(EnableChainNextTask));
        OnPropertyChanged(nameof(ChainNextModeKey));
        OnPropertyChanged(nameof(StartDungeonHotkey));
        OnPropertyChanged(nameof(InteractHotkey));
        OnPropertyChanged(nameof(AttackHotkey));
        OnPropertyChanged(nameof(ClearBagHotkey));
        OnPropertyChanged(nameof(ExitDungeonHotkey));
        OnPropertyChanged(nameof(StepIntervalMs));
        OnPropertyChanged(nameof(BattleCheckIntervalMs));
        OnPropertyChanged(nameof(CompletionKeywords));
        OnPropertyChanged(nameof(StageNavigationMode));
        OnPropertyChanged(nameof(StageNavigationHotkey));
        OnPropertyChanged(nameof(StageNavigationClickX));
        OnPropertyChanged(nameof(StageNavigationClickY));
        OnPropertyChanged(nameof(StageNavigationActionIntervalMs));
        OnPropertyChanged(nameof(StageNavigationDispatchMode));
        OnPropertyChanged(nameof(CompletionRegionX));
        OnPropertyChanged(nameof(CompletionRegionY));
        OnPropertyChanged(nameof(CompletionRegionWidth));
        OnPropertyChanged(nameof(CompletionRegionHeight));
        OnPropertyChanged(nameof(SourceSummary));
        RefreshSummary();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var step in _service.CurrentSteps)
            Steps.Add(new DungeonScriptStepViewModel(step, RefreshSummary));
        RefreshSummary();
    }

    private void OnConfigChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(StepSummary));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(HotkeySummary));
        OnPropertyChanged(nameof(FlowSummary));
        OnPropertyChanged(nameof(NavigationSummary));
        OnPropertyChanged(nameof(CompletionSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static int KeywordCount(string value) =>
        (value ?? string.Empty).Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string RegionText(OcrRegionConfig region) => $"{region.X},{region.Y},{region.Width},{region.Height}";

    private static string DungeonModeText(DungeonMode mode) => mode switch
    {
        DungeonMode.OldThreeRings => "老三环",
        DungeonMode.NewThreeRings => "新三环",
        _ => mode.ToString()
    };

    private static string NavigationModeText(ReturnNavigationMode mode) => mode switch
    {
        ReturnNavigationMode.Disabled => "不导航",
        ReturnNavigationMode.HotkeyOnly => "热键",
        ReturnNavigationMode.ClickClientPoint => "点击",
        ReturnNavigationMode.HotkeyThenClickClientPoint => "热键+点击",
        ReturnNavigationMode.DirectionalClientClick => "方向点击",
        ReturnNavigationMode.HotkeyThenDirectionalClientClick => "热键+方向",
        ReturnNavigationMode.RecordedClientClickSequence => "录制序列",
        _ => mode.ToString()
    };

    private void ControllerSubscribe()
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

