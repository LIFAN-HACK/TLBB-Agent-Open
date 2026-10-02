using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class TradeRoutePointViewModel : ObservableObject
{
    private readonly TradeRoutePoint _model;
    private readonly Action _onChanged;

    public TradeRoutePointViewModel(TradeRoutePoint model, Action onChanged)
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
    public string GoodsName { get => _model.GoodsName; set { _model.GoodsName = value; OnPropertyChanged(); _onChanged(); } }
    public TradeRouteAction Action { get => _model.Action; set { _model.Action = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int WaitMs { get => _model.WaitMs; set { _model.WaitMs = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed partial class TradePageViewModel : PageViewModel
{
    private readonly ITradeConfigService _service;
    private readonly ILogService _log;

    public TradePageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        ITradeConfigService service,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _log = log;
        _newMapName = "洛阳";
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        SubscribeController();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;

    public Array Actions { get; } = Enum.GetValues(typeof(TradeRouteAction));
    public Array ExecutionModes { get; } = Enum.GetValues(typeof(TradeExecutionMode));
    public Array NavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<TradeRoutePointViewModel> Points { get; } = new();

    private TradeConfig C => _service.Current;
    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string RouteSummary => $"路线 {Blank(C.RouteName)} / 步骤 {C.Points.Count} / 地图 {_service.MapNames.Count}";
    public string ExecutionSummary => $"{ExecutionModeText(ExecutionMode)} / {DispatchModeText(DispatchMode)} / 默认 {Blank(OpenMapHotkey)}";
    public string TimingSummary => $"步骤 {StepIntervalMs}ms / 动作 {ActionIntervalMs}ms / 重试 {MaxStepRetry}";
    public string NavigationSummary => NavigationMode == ReturnNavigationMode.Disabled
        ? "导航关闭"
        : $"{NavigationModeText(NavigationMode)} {Blank(NavigationHotkey)} / 到点 {ArrivalDistance}";
    public string RouteEndSummary => $"{(LoopRoute ? "循环" : "单轮")} / {(StopWhenRouteEnds ? "结束停" : "结束不停")}";
    public string ReadySummary
    {
        get
        {
            if (C.Points.Count == 0)
                return "缺少路线";
            if (string.IsNullOrWhiteSpace(OpenMapHotkey))
                return "缺少默认键";
            if (NavigationMode != ReturnNavigationMode.Disabled && string.IsNullOrWhiteSpace(NavigationHotkey))
                return "缺少导航键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string RouteName { get => C.RouteName; set { C.RouteName = value; OnConfigChanged(); } }
    public bool LoopRoute { get => C.LoopRoute; set { C.LoopRoute = value; OnConfigChanged(); } }
    public bool StopWhenRouteEnds { get => C.StopWhenRouteEnds; set { C.StopWhenRouteEnds = value; OnConfigChanged(); } }
    public int ArrivalDistance { get => C.ArrivalDistance; set { C.ArrivalDistance = value; OnConfigChanged(); } }
    public int StepIntervalMs { get => C.StepIntervalMs; set { C.StepIntervalMs = value; OnConfigChanged(); } }
    public int ActionIntervalMs { get => C.ActionIntervalMs; set { C.ActionIntervalMs = value; OnConfigChanged(); } }
    public int MaxStepRetry { get => C.MaxStepRetry; set { C.MaxStepRetry = value; OnConfigChanged(); } }
    public string OpenMapHotkey { get => C.OpenMapHotkey; set { C.OpenMapHotkey = value; OnConfigChanged(); } }
    public TradeExecutionMode ExecutionMode { get => C.ExecutionMode; set { C.ExecutionMode = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }
    public ReturnNavigationMode NavigationMode { get => C.Navigation.Mode; set { C.Navigation.Mode = value; OnConfigChanged(); } }
    public string NavigationHotkey { get => C.Navigation.Hotkey; set { C.Navigation.Hotkey = value; OnConfigChanged(); } }
    public int NavigationClickX { get => C.Navigation.ClickX; set { C.Navigation.ClickX = value; OnConfigChanged(); } }
    public int NavigationClickY { get => C.Navigation.ClickY; set { C.Navigation.ClickY = value; OnConfigChanged(); } }
    public int NavigationActionIntervalMs { get => C.Navigation.ActionIntervalMs; set { C.Navigation.ActionIntervalMs = value; OnConfigChanged(); } }

    [ObservableProperty] private string _operationStatus = "载入或添加路线。";
    [ObservableProperty] private string _newMapName;
    [ObservableProperty] private int _newX;
    [ObservableProperty] private int _newY;
    [ObservableProperty] private string _newNpcName = "";
    [ObservableProperty] private string _newGoodsName = "";
    [ObservableProperty] private TradeRouteAction _newAction = TradeRouteAction.Navigate;
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private int _newClickX = 520;
    [ObservableProperty] private int _newClickY = 360;

    [RelayCommand]
    private void AddPoint()
    {
        if (string.IsNullOrWhiteSpace(NewMapName))
        {
            OperationStatus = "地图不能为空。";
            return;
        }

        _service.AddPoint(new TradeRoutePoint
        {
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            NpcName = NewNpcName.Trim(),
            GoodsName = NewGoodsName.Trim(),
            Action = NewAction,
            Hotkey = NewHotkey.Trim(),
            ClickX = NewClickX,
            ClickY = NewClickY,
            Label = $"{C.Points.Count + 1:D2}-{NewAction}",
            SourceModule = "手动",
            SourcePath = ""
        });

        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemovePoint(TradeRoutePointViewModel? point)
    {
        if (point is null)
            return;

        if (_service.RemovePoint(point.Id))
            OperationStatus = "已删除。";
    }

    [RelayCommand]
    private void ClearPoints()
    {
        _service.Clear();
        OperationStatus = "已清空。";
    }

    [RelayCommand]
    private void ImportRoute()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入路线",
            Filter = "路线文件 (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = _service.ImportPlainRoute(dialog.FileName, "配置");
            OperationStatus = result.Message;
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("Trade", OperationStatus);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认跑商" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认跑商" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void Refresh()
    {
        RebuildPoints();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(RouteName));
        OnPropertyChanged(nameof(LoopRoute));
        OnPropertyChanged(nameof(StopWhenRouteEnds));
        OnPropertyChanged(nameof(ArrivalDistance));
        OnPropertyChanged(nameof(StepIntervalMs));
        OnPropertyChanged(nameof(ActionIntervalMs));
        OnPropertyChanged(nameof(MaxStepRetry));
        OnPropertyChanged(nameof(OpenMapHotkey));
        OnPropertyChanged(nameof(ExecutionMode));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(NavigationMode));
        OnPropertyChanged(nameof(NavigationHotkey));
        OnPropertyChanged(nameof(NavigationClickX));
        OnPropertyChanged(nameof(NavigationClickY));
        OnPropertyChanged(nameof(NavigationActionIntervalMs));
        RefreshSummary();
    }

    private void RebuildPoints()
    {
        Points.Clear();
        foreach (var point in _service.Route.Take(240))
            Points.Add(new TradeRoutePointViewModel(point, RefreshSummary));
        RefreshSummary();
    }

    private void OnConfigChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(ExecutionSummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(NavigationSummary));
        OnPropertyChanged(nameof(RouteEndSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ExecutionModeText(TradeExecutionMode mode) => mode switch
    {
        TradeExecutionMode.HotkeyOnly => "热键",
        TradeExecutionMode.ClickOnly => "点击",
        TradeExecutionMode.HotkeyThenClick => "热键+点击",
        _ => mode.ToString()
    };

    private static string DispatchModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "后台",
        InputDispatchMode.SendInput => "前台",
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

