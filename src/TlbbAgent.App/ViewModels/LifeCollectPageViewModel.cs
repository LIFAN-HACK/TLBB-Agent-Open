using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class LifeCollectPointViewModel : ObservableObject
{
    private readonly LifeCollectPoint _model;
    private readonly Action _onChanged;

    public LifeCollectPointViewModel(LifeCollectPoint model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public LifeCollectPoint Model => _model;
    public string Id => _model.Id;
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string SourceModule => string.IsNullOrWhiteSpace(_model.SourceModule) ? "手动" : "配置";
}

public sealed partial class LifeCollectPageViewModel : PageViewModel
{
    private readonly ILifeCollectConfigService _service;
    private readonly IRuntimeDataService _runtimeData;
    private readonly ILogService _log;

    public LifeCollectPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        ILifeCollectConfigService service,
        IRuntimeDataService runtimeData,
        ILogService log,
        LifeCollectMode initialMode)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _runtimeData = runtimeData;
        _log = log;

        C.Mode = initialMode;
        _newMapName = string.IsNullOrWhiteSpace(C.MapName) ? "无量山" : C.MapName;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        SubscribeController();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;

    public Array Modes { get; } = Enum.GetValues(typeof(LifeCollectMode));
    public Array ActionModes { get; } = Enum.GetValues(typeof(LifeCollectActionMode));
    public Array NavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<string> MapNames { get; } = new();
    public ObservableCollection<LifeCollectPointViewModel> Points { get; } = new();

    private LifeCollectConfig C => _service.Current;

    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string RouteSummary => $"地图 {_service.MapNames.Count} 个，坐标 {C.Points.Count} 个，当前路线 {_service.CurrentRoute.Count} 个";
    public string SourceSummary => C.SourceSummary;
    public string ModeSummary => $"{ModeText(Mode)} / {Blank(MapName)} / 关键词 {KeywordCount(ResourceKeywords)} 组";
    public string ActionSummary => $"{ActionModeText(ActionMode)} / 扫描 {Blank(ScanHotkey)} / 采集 {Blank(CollectHotkey)}";
    public string TimingSummary => $"扫描 {ScanIntervalMs}ms / 采集 {CollectIntervalMs}ms / 重试 {MaxPointRetry}";
    public string NavigationSummary => EnableRouteNavigation
        ? $"{NavigationModeText(NavigationMode)} {Blank(NavigationHotkey)} / 到点 {ArrivalDistance}"
        : "导航关闭";
    public string RecognitionSummary => $"地图 {RegionText(C.Recognition.MapName)} / 坐标 {RegionText(C.Recognition.Coordinate)}";
    public string ReadySummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MapName))
                return "缺少地图";
            if (_service.CurrentRoute.Count == 0)
                return "缺少路线";
            if (string.IsNullOrWhiteSpace(ResourceKeywords))
                return "缺少关键词";
            if (string.IsNullOrWhiteSpace(ScanHotkey))
                return "缺少扫描键";
            if (ActionMode != LifeCollectActionMode.ClickClientPoint && string.IsNullOrWhiteSpace(CollectHotkey))
                return "缺少采集键";
            if (EnableRouteNavigation && NavigationMode != ReturnNavigationMode.Disabled && string.IsNullOrWhiteSpace(NavigationHotkey))
                return "缺少导航键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public LifeCollectMode Mode { get => C.Mode; set { C.Mode = value; OnConfigChanged(); } }
    public string MapName
    {
        get => C.MapName;
        set
        {
            if (C.MapName == value) return;
            C.MapName = value ?? "";
            OnConfigChanged();
            RebuildPoints();
        }
    }
    public string ResourceKeywords { get => C.ResourceKeywords; set { C.ResourceKeywords = value; OnConfigChanged(); } }
    public string ScanHotkey { get => C.ScanHotkey; set { C.ScanHotkey = value; OnConfigChanged(); } }
    public string CollectHotkey { get => C.CollectHotkey; set { C.CollectHotkey = value; OnConfigChanged(); } }
    public int ScanIntervalMs { get => C.ScanIntervalMs; set { C.ScanIntervalMs = value; OnConfigChanged(); } }
    public int CollectIntervalMs { get => C.CollectIntervalMs; set { C.CollectIntervalMs = value; OnConfigChanged(); } }
    public int ArrivalDistance { get => C.ArrivalDistance; set { C.ArrivalDistance = value; OnConfigChanged(); } }
    public int MaxPointRetry { get => C.MaxPointRetry; set { C.MaxPointRetry = value; OnConfigChanged(); } }
    public bool EnableRouteNavigation { get => C.EnableRouteNavigation; set { C.EnableRouteNavigation = value; OnConfigChanged(); } }
    public bool EnableBagFullStop { get => C.EnableBagFullStop; set { C.EnableBagFullStop = value; OnConfigChanged(); } }
    public LifeCollectActionMode ActionMode { get => C.ActionMode; set { C.ActionMode = value; OnConfigChanged(); } }
    public int ActionClickX { get => C.ActionClickX; set { C.ActionClickX = value; OnConfigChanged(); } }
    public int ActionClickY { get => C.ActionClickY; set { C.ActionClickY = value; OnConfigChanged(); } }
    public InputDispatchMode DispatchMode { get => C.DispatchMode; set { C.DispatchMode = value; OnConfigChanged(); } }

    public ReturnNavigationMode NavigationMode { get => C.Navigation.Mode; set { C.Navigation.Mode = value; OnConfigChanged(); } }
    public string NavigationHotkey { get => C.Navigation.Hotkey; set { C.Navigation.Hotkey = value; OnConfigChanged(); } }
    public int NavigationOpenMapDelayMs { get => C.Navigation.OpenMapDelayMs; set { C.Navigation.OpenMapDelayMs = value; OnConfigChanged(); } }
    public int NavigationClickX { get => C.Navigation.ClickX; set { C.Navigation.ClickX = value; OnConfigChanged(); } }
    public int NavigationClickY { get => C.Navigation.ClickY; set { C.Navigation.ClickY = value; OnConfigChanged(); } }
    public int NavigationActionIntervalMs { get => C.Navigation.ActionIntervalMs; set { C.Navigation.ActionIntervalMs = value; OnConfigChanged(); } }
    public InputDispatchMode NavigationDispatchMode { get => C.Navigation.DispatchMode; set { C.Navigation.DispatchMode = value; OnConfigChanged(); } }

    public int MapRegionX { get => C.Recognition.MapName.X; set { C.Recognition.MapName.X = value; OnPropertyChanged(); } }
    public int MapRegionY { get => C.Recognition.MapName.Y; set { C.Recognition.MapName.Y = value; OnPropertyChanged(); } }
    public int MapRegionWidth { get => C.Recognition.MapName.Width; set { C.Recognition.MapName.Width = value; OnPropertyChanged(); } }
    public int MapRegionHeight { get => C.Recognition.MapName.Height; set { C.Recognition.MapName.Height = value; OnPropertyChanged(); } }
    public int CoordinateRegionX { get => C.Recognition.Coordinate.X; set { C.Recognition.Coordinate.X = value; OnPropertyChanged(); } }
    public int CoordinateRegionY { get => C.Recognition.Coordinate.Y; set { C.Recognition.Coordinate.Y = value; OnPropertyChanged(); } }
    public int CoordinateRegionWidth { get => C.Recognition.Coordinate.Width; set { C.Recognition.Coordinate.Width = value; OnPropertyChanged(); } }
    public int CoordinateRegionHeight { get => C.Recognition.Coordinate.Height; set { C.Recognition.Coordinate.Height = value; OnPropertyChanged(); } }

    [ObservableProperty] private string _operationStatus = "载入或添加路线。";
    [ObservableProperty] private string _newMapName;
    [ObservableProperty] private int _newX;
    [ObservableProperty] private int _newY;
    [ObservableProperty] private string _newLabel = "手动点";

    [RelayCommand]
    private void ImportAWindowRoutes() =>
        ImportRoutes(Path.Combine("routes", "life-a"), "生活采集坐标A");

    [RelayCommand]
    private void ImportBWindowRoutes() =>
        ImportRoutes(Path.Combine("routes", "life-b"), "生活采集坐标B");

    [RelayCommand]
    private void AddPoint()
    {
        if (string.IsNullOrWhiteSpace(NewMapName))
        {
            OperationStatus = "地图名不能为空。";
            return;
        }

        _service.AddPoint(new LifeCollectPoint
        {
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            Label = string.IsNullOrWhiteSpace(NewLabel) ? $"{NewMapName.Trim()}-{C.Points.Count + 1}" : NewLabel.Trim(),
            SourceModule = "手动",
            SourcePath = ""
        });

        if (string.IsNullOrWhiteSpace(MapName))
            MapName = NewMapName.Trim();

        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemovePoint(LifeCollectPointViewModel? point)
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
    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认生活采集" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认生活采集" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void ImportRoutes(string relativeDirectory, string sourceModule)
    {
        if (!_runtimeData.TryResolveDirectory(relativeDirectory, out var path))
        {
            OperationStatus = "未找到路线目录。";
            _log.Warn("LifeCollect", OperationStatus);
            return;
        }

        var result = _service.ImportLegacyDirectory(path, sourceModule);
        if (string.IsNullOrWhiteSpace(MapName) && _service.MapNames.Count > 0)
            MapName = _service.MapNames[0];
        OperationStatus = $"已载入：{result.MapCount} 图 / {result.PointCount} 点。";
    }

    private void Refresh()
    {
        RebuildMapNames();
        RebuildPoints();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(ResourceKeywords));
        OnPropertyChanged(nameof(ScanHotkey));
        OnPropertyChanged(nameof(CollectHotkey));
        OnPropertyChanged(nameof(ScanIntervalMs));
        OnPropertyChanged(nameof(CollectIntervalMs));
        OnPropertyChanged(nameof(ArrivalDistance));
        OnPropertyChanged(nameof(MaxPointRetry));
        OnPropertyChanged(nameof(EnableRouteNavigation));
        OnPropertyChanged(nameof(EnableBagFullStop));
        OnPropertyChanged(nameof(ActionMode));
        OnPropertyChanged(nameof(ActionClickX));
        OnPropertyChanged(nameof(ActionClickY));
        OnPropertyChanged(nameof(DispatchMode));
        OnPropertyChanged(nameof(NavigationMode));
        OnPropertyChanged(nameof(NavigationHotkey));
        OnPropertyChanged(nameof(NavigationOpenMapDelayMs));
        OnPropertyChanged(nameof(NavigationClickX));
        OnPropertyChanged(nameof(NavigationClickY));
        OnPropertyChanged(nameof(NavigationActionIntervalMs));
        OnPropertyChanged(nameof(NavigationDispatchMode));
        OnPropertyChanged(nameof(MapRegionX));
        OnPropertyChanged(nameof(MapRegionY));
        OnPropertyChanged(nameof(MapRegionWidth));
        OnPropertyChanged(nameof(MapRegionHeight));
        OnPropertyChanged(nameof(CoordinateRegionX));
        OnPropertyChanged(nameof(CoordinateRegionY));
        OnPropertyChanged(nameof(CoordinateRegionWidth));
        OnPropertyChanged(nameof(CoordinateRegionHeight));
        OnPropertyChanged(nameof(SourceSummary));
        RefreshSummary();
    }

    private void RebuildMapNames()
    {
        MapNames.Clear();
        foreach (var map in _service.MapNames)
            MapNames.Add(map);
    }

    private void RebuildPoints()
    {
        Points.Clear();
        foreach (var point in _service.CurrentRoute.Take(240))
            Points.Add(new LifeCollectPointViewModel(point, RefreshSummary));
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
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(ActionSummary));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(NavigationSummary));
        OnPropertyChanged(nameof(RecognitionSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static int KeywordCount(string value) =>
        (value ?? string.Empty).Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string RegionText(OcrRegionConfig region) => $"{region.X},{region.Y},{region.Width},{region.Height}";

    private static string ModeText(LifeCollectMode mode) => mode switch
    {
        LifeCollectMode.Mining => "挖矿",
        LifeCollectMode.Herb => "采药",
        LifeCollectMode.Gathering => "采集",
        _ => mode.ToString()
    };

    private static string ActionModeText(LifeCollectActionMode mode) => mode switch
    {
        LifeCollectActionMode.HotkeyOnly => "热键",
        LifeCollectActionMode.ClickClientPoint => "点击",
        LifeCollectActionMode.HotkeyThenClickClientPoint => "热键+点击",
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

