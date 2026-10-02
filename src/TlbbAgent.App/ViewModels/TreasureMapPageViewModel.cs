using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class TreasureMapPointViewModel : ObservableObject
{
    private readonly TreasureMapPoint _model;
    private readonly Action _onChanged;

    public TreasureMapPointViewModel(TreasureMapPoint model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public TreasureMapStepAction Action { get => _model.Action; set { _model.Action = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int WaitMs { get => _model.WaitMs; set { _model.WaitMs = value; OnPropertyChanged(); _onChanged(); } }
}

public sealed partial class TreasureMapPageViewModel : PageViewModel
{
    private readonly ITreasureMapConfigService _service;
    private readonly ILogService _log;

    public TreasureMapPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        ITreasureMapConfigService service,
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

    public Array Actions { get; } = Enum.GetValues(typeof(TreasureMapStepAction));
    public Array ActionModes { get; } = Enum.GetValues(typeof(TreasureMapActionMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<TreasureMapPointViewModel> Points { get; } = new();
    public ObservableCollection<string> KeepItems { get; } = new();

    private TreasureMapConfig C => _service.Current;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string PlanSummary => $"流程 {Blank(C.PlanName)} / 坐标 {C.Points.Count} / 批次 {C.MapBatchCount}";
    public string ModeSummary => $"{(SmallMapOnly ? "小图" : "全部")} / {ActionModeText(ActionMode)} / {DispatchModeText(DispatchMode)}";
    public string FeatureSummary
    {
        get
        {
            var flags = new List<string>();
            if (UseLocator) flags.Add("定位");
            if (FetchFromWarehouse) flags.Add("仓库");
            if (FetchFromShop) flags.Add("商会");
            if (PickupDrops) flags.Add("拾取");
            if (EnableCleanupAfterDig) flags.Add("清包");
            if (OpenNormalBox) flags.Add("普通箱");
            if (OpenGoldBox) flags.Add("金币箱");
            return flags.Count == 0 ? "无附加" : string.Join(" / ", flags);
        }
    }
    public string HotkeySummary => $"开图 {Blank(OpenMapHotkey)} / 挖图 {Blank(DigHotkey)} / 拾取 {Blank(PickupHotkey)}";
    public string TimingSummary => $"步骤 {StepIntervalMs}ms / 动作 {ActionIntervalMs}ms / 重试 {MaxPointRetry}";
    public string ReadySummary
    {
        get
        {
            if (C.Points.Count == 0)
                return "缺少坐标";
            if (MapBatchCount <= 0)
                return "批次无效";
            if (string.IsNullOrWhiteSpace(OpenMapHotkey))
                return "缺少开图键";
            if (string.IsNullOrWhiteSpace(DigHotkey))
                return "缺少挖图键";
            if (PickupDrops && string.IsNullOrWhiteSpace(PickupHotkey))
                return "缺少拾取键";
            if (UseLocator && string.IsNullOrWhiteSpace(LocatorHotkey))
                return "缺少定位键";
            if (EnableCleanupAfterDig && string.IsNullOrWhiteSpace(CleanupHotkey))
                return "缺少清包键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string PlanName { get => C.PlanName; set { C.PlanName = value; OnConfigChanged(); } }
    public string MapName { get => C.MapName; set { C.MapName = value; OnConfigChanged(); } }
    public bool SmallMapOnly { get => C.SmallMapOnly; set { C.SmallMapOnly = value; OnConfigChanged(); } }
    public string BigMapKeywords { get => C.BigMapKeywords; set { C.BigMapKeywords = value; OnConfigChanged(); } }
    public bool UseLocator { get => C.UseLocator; set { C.UseLocator = value; OnConfigChanged(); } }
    public bool FetchFromWarehouse { get => C.FetchFromWarehouse; set { C.FetchFromWarehouse = value; OnConfigChanged(); } }
    public bool FetchFromShop { get => C.FetchFromShop; set { C.FetchFromShop = value; OnConfigChanged(); } }
    public bool PickupDrops { get => C.PickupDrops; set { C.PickupDrops = value; OnConfigChanged(); } }
    public bool EnableCleanupAfterDig { get => C.EnableCleanupAfterDig; set { C.EnableCleanupAfterDig = value; OnConfigChanged(); } }
    public bool OpenNormalBox { get => C.OpenNormalBox; set { C.OpenNormalBox = value; OnConfigChanged(); } }
    public bool OpenGoldBox { get => C.OpenGoldBox; set { C.OpenGoldBox = value; OnConfigChanged(); } }
    public string BoxOrder { get => C.BoxOrder; set { C.BoxOrder = value; OnConfigChanged(); } }
    public int MapBatchCount { get => C.MapBatchCount; set { C.MapBatchCount = value; OnConfigChanged(); } }
    public bool LoopRoute { get => C.LoopRoute; set { C.LoopRoute = value; OnConfigChanged(); } }
    public bool StopWhenRouteEnds { get => C.StopWhenRouteEnds; set { C.StopWhenRouteEnds = value; OnConfigChanged(); } }
    public int ArrivalDistance { get => C.ArrivalDistance; set { C.ArrivalDistance = value; OnConfigChanged(); } }
    public int StepIntervalMs { get => C.StepIntervalMs; set { C.StepIntervalMs = value; OnConfigChanged(); } }
    public int ActionIntervalMs { get => C.ActionIntervalMs; set { C.ActionIntervalMs = value; OnConfigChanged(); } }
    public int MaxPointRetry { get => C.MaxPointRetry; set { C.MaxPointRetry = value; OnConfigChanged(); } }
    public string OpenMapHotkey { get => C.OpenMapHotkey; set { C.OpenMapHotkey = value; OnConfigChanged(); } }
    public string DigHotkey { get => C.DigHotkey; set { C.DigHotkey = value; OnConfigChanged(); } }
    public string PickupHotkey { get => C.PickupHotkey; set { C.PickupHotkey = value; OnConfigChanged(); } }
    public string LocatorHotkey { get => C.LocatorHotkey; set { C.LocatorHotkey = value; OnConfigChanged(); } }
    public string CleanupHotkey { get => C.CleanupHotkey; set { C.CleanupHotkey = value; OnConfigChanged(); } }
    public TreasureMapActionMode ActionMode { get => C.ActionMode; set { C.ActionMode = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }

    [ObservableProperty] private string _operationStatus = "载入或添加坐标。";
    [ObservableProperty] private string _newMapName = "藏宝图";
    [ObservableProperty] private int _newX = 100;
    [ObservableProperty] private int _newY = 100;
    [ObservableProperty] private TreasureMapStepAction _newAction = TreasureMapStepAction.Dig;
    [ObservableProperty] private string _newHotkey = "";
    [ObservableProperty] private int _newClickX = 520;
    [ObservableProperty] private int _newClickY = 360;
    [ObservableProperty] private int _newWaitMs = 900;

    [RelayCommand]
    private void AddPoint()
    {
        _service.AddPoint(new TreasureMapPoint
        {
            Label = $"{C.Points.Count + 1:D2}-{NewAction}",
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            Action = NewAction,
            Hotkey = NewHotkey.Trim(),
            ClickX = NewClickX,
            ClickY = NewClickY,
            WaitMs = NewWaitMs,
            RetryLimit = C.MaxPointRetry,
            SourceModule = "手动",
            SourcePath = ""
        });

        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemovePoint(TreasureMapPointViewModel? point)
    {
        if (point is null)
            return;

        if (_service.RemovePoint(point.Id))
            OperationStatus = "已删除。";
    }

    [RelayCommand]
    private void ClearPoints()
    {
        _service.ClearPoints();
        OperationStatus = "已清空坐标。";
    }

    [RelayCommand]
    private void ImportCoordinates()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入坐标",
            Filter = "坐标文件 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = _service.ImportCoordinateFile(dialog.FileName, "配置", MapName);
            OperationStatus = result.Message;
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("TreasureMap", OperationStatus);
        }
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
        OperationStatus = $"已载入：{count}。";
        }
        catch
        {
            OperationStatus = "载入失败。";
            _log.Error("TreasureMap", OperationStatus);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认挖图" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认挖图" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void Refresh()
    {
        RebuildPoints();
        RebuildKeepItems();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(PlanName));
        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(SmallMapOnly));
        OnPropertyChanged(nameof(BigMapKeywords));
        OnPropertyChanged(nameof(UseLocator));
        OnPropertyChanged(nameof(FetchFromWarehouse));
        OnPropertyChanged(nameof(FetchFromShop));
        OnPropertyChanged(nameof(PickupDrops));
        OnPropertyChanged(nameof(EnableCleanupAfterDig));
        OnPropertyChanged(nameof(OpenNormalBox));
        OnPropertyChanged(nameof(OpenGoldBox));
        OnPropertyChanged(nameof(BoxOrder));
        OnPropertyChanged(nameof(MapBatchCount));
        OnPropertyChanged(nameof(LoopRoute));
        OnPropertyChanged(nameof(StopWhenRouteEnds));
        OnPropertyChanged(nameof(ArrivalDistance));
        OnPropertyChanged(nameof(StepIntervalMs));
        OnPropertyChanged(nameof(ActionIntervalMs));
        OnPropertyChanged(nameof(MaxPointRetry));
        OnPropertyChanged(nameof(OpenMapHotkey));
        OnPropertyChanged(nameof(DigHotkey));
        OnPropertyChanged(nameof(PickupHotkey));
        OnPropertyChanged(nameof(LocatorHotkey));
        OnPropertyChanged(nameof(CleanupHotkey));
        OnPropertyChanged(nameof(ActionMode));
        OnPropertyChanged(nameof(DispatchMode));
        RefreshSummary();
    }

    private void RebuildPoints()
    {
        Points.Clear();
        foreach (var point in _service.Route.Take(300))
            Points.Add(new TreasureMapPointViewModel(point, RefreshSummary));
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
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(FeatureSummary));
        OnPropertyChanged(nameof(HotkeySummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string ActionModeText(TreasureMapActionMode mode) => mode switch
    {
        TreasureMapActionMode.HotkeyOnly => "热键",
        TreasureMapActionMode.ClickOnly => "点击",
        TreasureMapActionMode.HotkeyThenClick => "热键+点击",
        _ => mode.ToString()
    };

    private static string DispatchModeText(InputDispatchMode mode) => mode switch
    {
        InputDispatchMode.Adaptive => "自动兼容",
        InputDispatchMode.PostMessage => "后台",
        InputDispatchMode.SendInput => "前台",
        _ => mode.ToString()
    };

    private void RebuildKeepItems()
    {
        KeepItems.Clear();
        foreach (var item in _service.KeepItems.Take(120))
            KeepItems.Add(item);
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

