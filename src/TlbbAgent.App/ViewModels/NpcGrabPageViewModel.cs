using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class NpcGrabTargetViewModel : ObservableObject
{
    private readonly NpcGrabTargetConfig _model;
    private readonly Action _onChanged;

    public NpcGrabTargetViewModel(NpcGrabTargetConfig model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public NpcGrabTargetConfig Model => _model;
    public NpcGrabTargetKind Kind => _model.Kind;
    public string DisplayName { get => _model.DisplayName; set { _model.DisplayName = value; OnPropertyChanged(); _onChanged(); } }
    public bool Enabled { get => _model.Enabled; set { _model.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public string Keywords { get => _model.Keywords; set { _model.Keywords = value; OnPropertyChanged(); _onChanged(); } }
    public int Priority { get => _model.Priority; set { _model.Priority = value; OnPropertyChanged(); _onChanged(); } }
    public string SourceModule => _model.SourceModule;
    public int KeywordCount => SplitKeywords(_model.Keywords).Count;

    private static IReadOnlyList<string> SplitKeywords(string value) =>
        (value ?? string.Empty)
            .Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length >= 2)
            .ToList();
}

public sealed partial class NpcGrabPageViewModel : PageViewModel
{
    private readonly INpcGrabConfigService _service;
    private readonly ILogService _log;

    public NpcGrabPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        INpcGrabConfigService service,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        ControllerSubscribe();
        RebuildTargets();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;
    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public Array ActionModes { get; } = Enum.GetValues(typeof(NpcGrabActionMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public Array SmallStrategies { get; } = Enum.GetValues(typeof(SmallNpcGrabStrategy));
    public ObservableCollection<NpcGrabTargetViewModel> Targets { get; } = new();

    private NpcGrabConfig C => _service.Current;

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string ScanHotkey { get => C.ScanHotkey; set { C.ScanHotkey = value; OnConfigChanged(); } }
    public string ActionHotkey { get => C.ActionHotkey; set { C.ActionHotkey = value; OnConfigChanged(); } }
    public int ScanIntervalMs { get => C.ScanIntervalMs; set { C.ScanIntervalMs = value; OnConfigChanged(); } }
    public int ClickIntervalMs { get => C.ClickIntervalMs; set { C.ClickIntervalMs = value; OnConfigChanged(); } }
    public int RetryDelayMs { get => C.RetryDelayMs; set { C.RetryDelayMs = value; OnConfigChanged(); } }
    public int RetryLimit { get => C.RetryLimit; set { C.RetryLimit = value; OnConfigChanged(); } }
    public NpcGrabActionMode ActionMode { get => C.ActionMode; set { C.ActionMode = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }
    public SmallNpcGrabStrategy SmallStrategy { get => C.SmallNpcFastGrab.Strategy; set { C.SmallNpcFastGrab.Strategy = value; OnConfigChanged(); } }
    public bool EnableRefreshBurst { get => C.SmallNpcFastGrab.EnableRefreshBurst; set { C.SmallNpcFastGrab.EnableRefreshBurst = value; OnConfigChanged(); } }
    public string RefreshMinuteMarks { get => C.SmallNpcFastGrab.RefreshMinuteMarks; set { C.SmallNpcFastGrab.RefreshMinuteMarks = value; OnConfigChanged(); } }
    public int RefreshWindowBeforeSeconds { get => C.SmallNpcFastGrab.RefreshWindowBeforeSeconds; set { C.SmallNpcFastGrab.RefreshWindowBeforeSeconds = value; OnConfigChanged(); } }
    public int RefreshWindowAfterSeconds { get => C.SmallNpcFastGrab.RefreshWindowAfterSeconds; set { C.SmallNpcFastGrab.RefreshWindowAfterSeconds = value; OnConfigChanged(); } }
    public int BurstScanIntervalMs { get => C.SmallNpcFastGrab.BurstScanIntervalMs; set { C.SmallNpcFastGrab.BurstScanIntervalMs = value; OnConfigChanged(); } }
    public bool EnableContextFastAction { get => C.SmallNpcFastGrab.EnableContextFastAction; set { C.SmallNpcFastGrab.EnableContextFastAction = value; OnConfigChanged(); } }
    public bool ContextFastOnlyInRefreshWindow { get => C.SmallNpcFastGrab.ContextFastOnlyInRefreshWindow; set { C.SmallNpcFastGrab.ContextFastOnlyInRefreshWindow = value; OnConfigChanged(); } }
    public int ContextValidSeconds { get => C.SmallNpcFastGrab.ContextValidSeconds; set { C.SmallNpcFastGrab.ContextValidSeconds = value; OnConfigChanged(); } }
    public int MaxContextFastAttemptsPerWindow { get => C.SmallNpcFastGrab.MaxContextFastAttemptsPerWindow; set { C.SmallNpcFastGrab.MaxContextFastAttemptsPerWindow = value; OnConfigChanged(); } }
    public int ClickX { get => C.ClickX; set { C.ClickX = value; OnConfigChanged(); } }
    public int ClickY { get => C.ClickY; set { C.ClickY = value; OnConfigChanged(); } }
    public int TargetNameX { get => C.Recognition.TargetName.X; set { C.Recognition.TargetName.X = value; OnConfigChanged(); } }
    public int TargetNameY { get => C.Recognition.TargetName.Y; set { C.Recognition.TargetName.Y = value; OnConfigChanged(); } }
    public int TargetNameWidth { get => C.Recognition.TargetName.Width; set { C.Recognition.TargetName.Width = value; OnConfigChanged(); } }
    public int TargetNameHeight { get => C.Recognition.TargetName.Height; set { C.Recognition.TargetName.Height = value; OnConfigChanged(); } }
    public bool TargetNameEnabled { get => C.Recognition.TargetName.Enabled; set { C.Recognition.TargetName.Enabled = value; OnConfigChanged(); } }
    public string SourceSummary => C.SourceSummary;
    public string EnabledSummary => $"已启用 {_service.EnabledTargets.Count} / {C.Targets.Count} 个目标";
    public string TargetMatrixSummary
    {
        get
        {
            var enabled = C.Targets.Where(t => t.Enabled).OrderBy(t => t.Priority).Select(t => t.DisplayName).ToList();
            return enabled.Count == 0 ? "未启用目标" : string.Join(" / ", enabled);
        }
    }
    public string TimingSummary => $"扫描 {ScanIntervalMs}ms / 点击 {ClickIntervalMs}ms / 重试 {RetryLimit} 次";
    public string ActionSummary => $"{ActionModeText(ActionMode)}，{DispatchModeText(DispatchMode)}，动作 {Blank(ActionHotkey)}";
    public string SmallFastSummary
    {
        get
        {
            var burst = EnableRefreshBurst ? $"高频 {BurstScanIntervalMs}ms" : "高频关闭";
            var context = EnableContextFastAction ? $"快抢 {MaxContextFastAttemptsPerWindow} 次" : "快抢关闭";
            return $"{SmallStrategyText(SmallStrategy)}，{burst}，{context}";
        }
    }
    public string ReadySummary
    {
        get
        {
            if (_service.EnabledTargets.Count == 0)
                return "未启用目标";
            if (string.IsNullOrWhiteSpace(ScanHotkey))
                return "缺少扫描键";
            if (ActionMode != NpcGrabActionMode.ClickClientPoint && string.IsNullOrWhiteSpace(ActionHotkey))
                return "缺少动作键";
            if (ActionMode != NpcGrabActionMode.HotkeyOnly && (ClickX < 0 || ClickY < 0))
                return "点击坐标无效";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    [ObservableProperty] private string _operationStatus = "检查目标和按键。";

    [RelayCommand]
    private void Save()
    {
        _service.Save(string.IsNullOrWhiteSpace(ProfileName) ? "默认抢NPC" : ProfileName);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认抢NPC" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _service.ResetDefaults();
        OperationStatus = "已恢复默认。";
    }

    [RelayCommand]
    private void Apply()
    {
        RefreshSummary();
        _log.Info("NpcGrab", $"配置已应用：{EnabledSummary}。");
    }

    private void Refresh()
    {
        RebuildTargets();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ScanHotkey));
        OnPropertyChanged(nameof(ActionHotkey));
        OnPropertyChanged(nameof(ScanIntervalMs));
        OnPropertyChanged(nameof(ClickIntervalMs));
        OnPropertyChanged(nameof(RetryDelayMs));
        OnPropertyChanged(nameof(RetryLimit));
        OnPropertyChanged(nameof(ActionMode));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(SmallStrategy));
        OnPropertyChanged(nameof(EnableRefreshBurst));
        OnPropertyChanged(nameof(RefreshMinuteMarks));
        OnPropertyChanged(nameof(RefreshWindowBeforeSeconds));
        OnPropertyChanged(nameof(RefreshWindowAfterSeconds));
        OnPropertyChanged(nameof(BurstScanIntervalMs));
        OnPropertyChanged(nameof(EnableContextFastAction));
        OnPropertyChanged(nameof(ContextFastOnlyInRefreshWindow));
        OnPropertyChanged(nameof(ContextValidSeconds));
        OnPropertyChanged(nameof(MaxContextFastAttemptsPerWindow));
        OnPropertyChanged(nameof(ClickX));
        OnPropertyChanged(nameof(ClickY));
        OnPropertyChanged(nameof(TargetNameX));
        OnPropertyChanged(nameof(TargetNameY));
        OnPropertyChanged(nameof(TargetNameWidth));
        OnPropertyChanged(nameof(TargetNameHeight));
        OnPropertyChanged(nameof(TargetNameEnabled));
        OnPropertyChanged(nameof(SourceSummary));
        RefreshSummary();
    }

    private void RebuildTargets()
    {
        Targets.Clear();
        foreach (var target in C.Targets.OrderBy(t => t.Priority))
        {
            Targets.Add(new NpcGrabTargetViewModel(target, RefreshSummary));
        }
        RefreshSummary();
    }

    private void OnConfigChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(EnabledSummary));
        OnPropertyChanged(nameof(TargetMatrixSummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(ActionSummary));
        OnPropertyChanged(nameof(SmallFastSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ActionModeText(NpcGrabActionMode mode) => mode switch
    {
        NpcGrabActionMode.HotkeyOnly => "热键",
        NpcGrabActionMode.ClickClientPoint => "点击",
        NpcGrabActionMode.HotkeyThenClickClientPoint => "热键+点击",
        _ => mode.ToString()
    };

    private static string DispatchModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "后台",
        InputDispatchMode.SendInput => "前台",
        _ => mode.ToString()
    };

    private static string SmallStrategyText(SmallNpcGrabStrategy strategy) => strategy switch
    {
        SmallNpcGrabStrategy.AutoRecognizeOnly => "识别",
        SmallNpcGrabStrategy.ContextFastOnly => "快抢",
        SmallNpcGrabStrategy.DualChannelPriority => "双通道",
        _ => strategy.ToString()
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

