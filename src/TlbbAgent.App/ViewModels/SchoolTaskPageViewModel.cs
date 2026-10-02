using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class SchoolTaskStepViewModel : ObservableObject
{
    private readonly SchoolTaskStep _model;
    private readonly Action _onChanged;

    public SchoolTaskStepViewModel(SchoolTaskStep model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public string NpcName { get => _model.NpcName; set { _model.NpcName = value; OnPropertyChanged(); _onChanged(); } }
    public SchoolTaskStepAction Action { get => _model.Action; set { _model.Action = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public string Text { get => _model.Text; set { _model.Text = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int WaitMs { get => _model.WaitMs; set { _model.WaitMs = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed partial class SchoolTaskPageViewModel : PageViewModel
{
    private readonly ISchoolTaskConfigService _service;
    private readonly ILogService _log;

    public SchoolTaskPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        ISchoolTaskConfigService service,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        SubscribeController();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;

    public Array Actions { get; } = Enum.GetValues(typeof(SchoolTaskStepAction));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public Array ExecutionModes { get; } = Enum.GetValues(typeof(SchoolTaskExecutionMode));
    public ObservableCollection<SchoolTaskStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> StoreItems { get; } = new();
    public ObservableCollection<string> TradeItems { get; } = new();

    private SchoolTaskConfig C => _service.Current;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string PlanSummary => $"流程 {Blank(C.PlanName)} / 步骤 {C.Steps.Count} / 轮次 {C.RoundCount}";
    public string RunSummary => $"{ExecutionModeText(ExecutionMode)} / {DispatchModeText(DispatchMode)} / {(LoopPlan ? "循环" : "单轮")}";
    public string TimingSummary => $"步骤 {StepIntervalMs}ms / 动作 {ActionIntervalMs}ms / 重试 {MaxStepRetry}";
    public string CleanupSummary => EnableCleanupAfterRound
        ? $"清包 {Blank(CleanupHotkey)}"
        : "清包关闭";
    public string ItemSummary => $"存放 {StoreItems.Count} / 交易 {TradeItems.Count}";
    public string ReadySummary
    {
        get
        {
            if (C.Steps.Count == 0)
                return "缺少流程";
            if (RoundCount <= 0)
                return "轮次无效";
            if (string.IsNullOrWhiteSpace(OpenTaskHotkey))
                return "缺少任务键";
            if (EnableCleanupAfterRound && string.IsNullOrWhiteSpace(CleanupHotkey))
                return "缺少清包键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string PlanName { get => C.PlanName; set { C.PlanName = value; OnConfigChanged(); } }
    public int RoundCount { get => C.RoundCount; set { C.RoundCount = value; OnConfigChanged(); } }
    public bool LoopPlan { get => C.LoopPlan; set { C.LoopPlan = value; OnConfigChanged(); } }
    public bool StopAfterRounds { get => C.StopAfterRounds; set { C.StopAfterRounds = value; OnConfigChanged(); } }
    public int ArrivalDistance { get => C.ArrivalDistance; set { C.ArrivalDistance = value; OnConfigChanged(); } }
    public int StepIntervalMs { get => C.StepIntervalMs; set { C.StepIntervalMs = value; OnConfigChanged(); } }
    public int ActionIntervalMs { get => C.ActionIntervalMs; set { C.ActionIntervalMs = value; OnConfigChanged(); } }
    public int MaxStepRetry { get => C.MaxStepRetry; set { C.MaxStepRetry = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }
    public SchoolTaskExecutionMode ExecutionMode { get => C.ExecutionMode; set { C.ExecutionMode = value; OnConfigChanged(); } }
    public string OpenTaskHotkey { get => C.OpenTaskHotkey; set { C.OpenTaskHotkey = value; OnConfigChanged(); } }
    public bool EnableCleanupAfterRound { get => C.EnableCleanupAfterRound; set { C.EnableCleanupAfterRound = value; OnConfigChanged(); } }
    public string CleanupHotkey { get => C.CleanupHotkey; set { C.CleanupHotkey = value; OnConfigChanged(); } }

    [ObservableProperty] private string _operationStatus = "载入或添加流程。";
    [ObservableProperty] private string _newMapName = "门派";
    [ObservableProperty] private int _newX = 100;
    [ObservableProperty] private int _newY = 100;
    [ObservableProperty] private string _newNpcName = "";
    [ObservableProperty] private SchoolTaskStepAction _newAction = SchoolTaskStepAction.TalkNpc;
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private int _newClickX = 520;
    [ObservableProperty] private int _newClickY = 360;
    [ObservableProperty] private int _newWaitMs = 800;
    [ObservableProperty] private string _newText = "";

    [RelayCommand]
    private void AddStep()
    {
        _service.AddStep(new SchoolTaskStep
        {
            Label = $"{C.Steps.Count + 1:D2}-{NewAction}",
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            NpcName = NewNpcName.Trim(),
            Action = NewAction,
            Hotkey = NewHotkey.Trim(),
            ClickX = NewClickX,
            ClickY = NewClickY,
            WaitMs = NewWaitMs,
            Text = NewText.Trim(),
            RetryLimit = C.MaxStepRetry,
            SourceModule = "手动",
            SourcePath = ""
        });

        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemoveStep(SchoolTaskStepViewModel? step)
    {
        if (step is null)
            return;

        if (_service.RemoveStep(step.Id))
            OperationStatus = "已删除。";
    }

    [RelayCommand]
    private void ClearSteps()
    {
        _service.ClearSteps();
        OperationStatus = "已清空流程。";
    }

    [RelayCommand]
    private void ImportPlan()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入流程",
            Filter = "流程文件 (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = _service.ImportPlainPlan(dialog.FileName, "配置");
            OperationStatus = result.Message;
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("SchoolTask", OperationStatus);
        }
    }

    [RelayCommand]
    private void ImportStoreItems() => ImportItems(tradeItems: false);

    [RelayCommand]
    private void ImportTradeItems() => ImportItems(tradeItems: true);

    [RelayCommand]
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认师门" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认师门" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void ImportItems(bool tradeItems)
    {
        var dialog = new OpenFileDialog
        {
            Title = tradeItems ? "载入交易物品" : "载入存放物品",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var count = _service.LoadItemList(dialog.FileName, tradeItems);
            OperationStatus = $"已载入：{count}。";
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("SchoolTask", OperationStatus);
        }
    }

    private void Refresh()
    {
        RebuildSteps();
        RebuildItems();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(PlanName));
        OnPropertyChanged(nameof(RoundCount));
        OnPropertyChanged(nameof(LoopPlan));
        OnPropertyChanged(nameof(StopAfterRounds));
        OnPropertyChanged(nameof(ArrivalDistance));
        OnPropertyChanged(nameof(StepIntervalMs));
        OnPropertyChanged(nameof(ActionIntervalMs));
        OnPropertyChanged(nameof(MaxStepRetry));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(ExecutionMode));
        OnPropertyChanged(nameof(OpenTaskHotkey));
        OnPropertyChanged(nameof(EnableCleanupAfterRound));
        OnPropertyChanged(nameof(CleanupHotkey));
        RefreshSummary();
    }

    private void RebuildSteps()
    {
        Steps.Clear();
        foreach (var step in _service.Steps.Take(240))
            Steps.Add(new SchoolTaskStepViewModel(step, RefreshSummary));
        RefreshSummary();
    }

    private void RebuildItems()
    {
        StoreItems.Clear();
        foreach (var item in _service.StoreItems.Take(80))
            StoreItems.Add(item);

        TradeItems.Clear();
        foreach (var item in _service.TradeItems.Take(80))
            TradeItems.Add(item);

        RefreshSummary();
    }

    private void OnConfigChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(CleanupSummary));
        OnPropertyChanged(nameof(ItemSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ExecutionModeText(SchoolTaskExecutionMode mode) => mode switch
    {
        SchoolTaskExecutionMode.HotkeyOnly => "热键",
        SchoolTaskExecutionMode.ClickOnly => "点击",
        SchoolTaskExecutionMode.HotkeyThenClick => "热键+点击",
        _ => mode.ToString()
    };

    private static string DispatchModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "后台",
        InputDispatchMode.SendInput => "前台",
        _ => mode.ToString()
    };

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

