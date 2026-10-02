using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class AutoMonkRoutePointViewModel : ObservableObject
{
    private readonly AutoMonkRoutePoint _model;
    private readonly Action _onChanged;

    public AutoMonkRoutePointViewModel(AutoMonkRoutePoint model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public AutoMonkRoutePoint Model => _model;
    public string Id => _model.Id;
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string SourceModule => string.IsNullOrWhiteSpace(_model.SourceModule) ? "手动" : "配置";
}

public sealed partial class AutoMonkPageViewModel : PageViewModel
{
    private readonly IAutoMonkConfigService _service;
    private readonly IRuntimeDataService _runtimeData;
    private readonly ILogService _log;

    public AutoMonkPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IAutoMonkConfigService service,
        IRuntimeDataService runtimeData,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _runtimeData = runtimeData;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        ControllerSubscribe();
        RebuildPoints();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;
    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public Array NavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<AutoMonkRoutePointViewModel> Points { get; } = new();

    private AutoMonkConfig C => _service.Current;

    public string ProfileName { get => C.ProfileName; set { C.ProfileName = value; OnPropertyChanged(); } }
    public string TargetKeywords { get => C.TargetKeywords; set { C.TargetKeywords = value; OnConfigChanged(); } }
    public string ActivityEndKeywords { get => C.ActivityEndKeywords; set { C.ActivityEndKeywords = value; OnConfigChanged(); } }
    public string SearchHotkey { get => C.SearchHotkey; set { C.SearchHotkey = value; OnConfigChanged(); } }
    public string AttackHotkey { get => C.AttackHotkey; set { C.AttackHotkey = value; OnConfigChanged(); } }
    public bool EnableTeamCheck { get => C.EnableTeamCheck; set { C.EnableTeamCheck = value; OnConfigChanged(); } }
    public bool EnableRouteNavigation { get => C.EnableRouteNavigation; set { C.EnableRouteNavigation = value; OnConfigChanged(); } }
    public bool EnableBattleDetection { get => C.EnableBattleDetection; set { C.EnableBattleDetection = value; OnConfigChanged(); } }
    public bool StopWhenActivityEnds { get => C.StopWhenActivityEnds; set { C.StopWhenActivityEnds = value; OnConfigChanged(); } }
    public bool EnableRecovery { get => C.EnableRecovery; set { C.EnableRecovery = value; OnConfigChanged(); } }
    public int ScanIntervalMs { get => C.ScanIntervalMs; set { C.ScanIntervalMs = value; OnConfigChanged(); } }
    public int BattleCheckIntervalMs { get => C.BattleCheckIntervalMs; set { C.BattleCheckIntervalMs = value; OnConfigChanged(); } }
    public int ArrivalDistance { get => C.ArrivalDistance; set { C.ArrivalDistance = value; OnConfigChanged(); } }
    public int RouteRetryLimit { get => C.RouteRetryLimit; set { C.RouteRetryLimit = value; OnConfigChanged(); } }

    public ReturnNavigationMode RouteNavigationMode { get => C.RouteNavigation.Mode; set { C.RouteNavigation.Mode = value; OnConfigChanged(); } }
    public string RouteNavigationHotkey { get => C.RouteNavigation.Hotkey; set { C.RouteNavigation.Hotkey = value; OnConfigChanged(); } }
    public int RouteNavigationClickX { get => C.RouteNavigation.ClickX; set { C.RouteNavigation.ClickX = value; OnConfigChanged(); } }
    public int RouteNavigationClickY { get => C.RouteNavigation.ClickY; set { C.RouteNavigation.ClickY = value; OnConfigChanged(); } }
    public int RouteNavigationActionIntervalMs { get => C.RouteNavigation.ActionIntervalMs; set { C.RouteNavigation.ActionIntervalMs = value; OnConfigChanged(); } }
    public InputDispatchMode RouteNavigationDispatchMode { get => C.RouteNavigation.DispatchMode; set { C.RouteNavigation.DispatchMode = value; OnConfigChanged(); } }

    public ReturnNavigationMode RecoveryNavigationMode { get => C.RecoveryNavigation.Mode; set { C.RecoveryNavigation.Mode = value; OnConfigChanged(); } }
    public string RecoveryNavigationHotkey { get => C.RecoveryNavigation.Hotkey; set { C.RecoveryNavigation.Hotkey = value; OnConfigChanged(); } }
    public int RecoveryNavigationClickX { get => C.RecoveryNavigation.ClickX; set { C.RecoveryNavigation.ClickX = value; OnConfigChanged(); } }
    public int RecoveryNavigationClickY { get => C.RecoveryNavigation.ClickY; set { C.RecoveryNavigation.ClickY = value; OnConfigChanged(); } }
    public int RecoveryNavigationActionIntervalMs { get => C.RecoveryNavigation.ActionIntervalMs; set { C.RecoveryNavigation.ActionIntervalMs = value; OnConfigChanged(); } }
    public InputDispatchMode RecoveryNavigationDispatchMode { get => C.RecoveryNavigation.DispatchMode; set { C.RecoveryNavigation.DispatchMode = value; OnConfigChanged(); } }

    public int MapRegionX { get => C.NavigationRecognition.MapName.X; set { C.NavigationRecognition.MapName.X = value; OnPropertyChanged(); } }
    public int MapRegionY { get => C.NavigationRecognition.MapName.Y; set { C.NavigationRecognition.MapName.Y = value; OnPropertyChanged(); } }
    public int MapRegionWidth { get => C.NavigationRecognition.MapName.Width; set { C.NavigationRecognition.MapName.Width = value; OnPropertyChanged(); } }
    public int MapRegionHeight { get => C.NavigationRecognition.MapName.Height; set { C.NavigationRecognition.MapName.Height = value; OnPropertyChanged(); } }
    public int CoordRegionX { get => C.NavigationRecognition.Coordinate.X; set { C.NavigationRecognition.Coordinate.X = value; OnPropertyChanged(); } }
    public int CoordRegionY { get => C.NavigationRecognition.Coordinate.Y; set { C.NavigationRecognition.Coordinate.Y = value; OnPropertyChanged(); } }
    public int CoordRegionWidth { get => C.NavigationRecognition.Coordinate.Width; set { C.NavigationRecognition.Coordinate.Width = value; OnPropertyChanged(); } }
    public int CoordRegionHeight { get => C.NavigationRecognition.Coordinate.Height; set { C.NavigationRecognition.Coordinate.Height = value; OnPropertyChanged(); } }
    public int TargetNameRegionX { get => C.CombatRecognition.TargetName.X; set { C.CombatRecognition.TargetName.X = value; OnPropertyChanged(); } }
    public int TargetNameRegionY { get => C.CombatRecognition.TargetName.Y; set { C.CombatRecognition.TargetName.Y = value; OnPropertyChanged(); } }
    public int TargetNameRegionWidth { get => C.CombatRecognition.TargetName.Width; set { C.CombatRecognition.TargetName.Width = value; OnPropertyChanged(); } }
    public int TargetNameRegionHeight { get => C.CombatRecognition.TargetName.Height; set { C.CombatRecognition.TargetName.Height = value; OnPropertyChanged(); } }
    public int ActivityStatusRegionX { get => C.ActivityStatusRegion.X; set { C.ActivityStatusRegion.X = value; OnPropertyChanged(); } }
    public int ActivityStatusRegionY { get => C.ActivityStatusRegion.Y; set { C.ActivityStatusRegion.Y = value; OnPropertyChanged(); } }
    public int ActivityStatusRegionWidth { get => C.ActivityStatusRegion.Width; set { C.ActivityStatusRegion.Width = value; OnPropertyChanged(); } }
    public int ActivityStatusRegionHeight { get => C.ActivityStatusRegion.Height; set { C.ActivityStatusRegion.Height = value; OnPropertyChanged(); } }

    public string RouteSummary => $"地图 {_service.MapNames.Count} 个，路线点 {C.RoutePoints.Count} 个";
    public string SourceSummary => C.SourceSummary;
    public string TargetSummary => $"目标 {KeywordCount(TargetKeywords)} 组 / 扫描 {ScanIntervalMs}ms";
    public string ActionSummary => $"搜索 {Blank(SearchHotkey)} / 攻击 {Blank(AttackHotkey)} / 战斗 {BattleCheckIntervalMs}ms";
    public string RouteActionSummary => EnableRouteNavigation
        ? $"{NavigationModeText(RouteNavigationMode)} {Blank(RouteNavigationHotkey)} / 到达 {ArrivalDistance}"
        : "路线关闭";
    public string RecoverySummary => EnableRecovery
        ? $"{NavigationModeText(RecoveryNavigationMode)} {Blank(RecoveryNavigationHotkey)} / 重试 {RouteRetryLimit}"
        : "恢复关闭";
    public string EndSummary => StopWhenActivityEnds
        ? $"结束 {KeywordCount(ActivityEndKeywords)} 组 / {RegionText(C.ActivityStatusRegion)}"
        : "结束忽略";
    public string RecognitionSummary => $"地图 {RegionText(C.NavigationRecognition.MapName)} / 坐标 {RegionText(C.NavigationRecognition.Coordinate)} / 目标 {RegionText(C.CombatRecognition.TargetName)}";
    public string ReadySummary
    {
        get
        {
            if (C.RoutePoints.Count == 0)
                return "缺少路线";
            if (string.IsNullOrWhiteSpace(TargetKeywords))
                return "缺少目标";
            if (StopWhenActivityEnds && string.IsNullOrWhiteSpace(ActivityEndKeywords))
                return "缺少结束词";
            if (string.IsNullOrWhiteSpace(SearchHotkey))
                return "缺少搜索键";
            if (string.IsNullOrWhiteSpace(AttackHotkey))
                return "缺少攻击键";
            if (EnableRouteNavigation && RouteNavigationMode != ReturnNavigationMode.Disabled && string.IsNullOrWhiteSpace(RouteNavigationHotkey))
                return "缺少路线键";
            if (EnableRecovery && RecoveryNavigationMode != ReturnNavigationMode.Disabled && string.IsNullOrWhiteSpace(RecoveryNavigationHotkey))
                return "缺少恢复键";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    [ObservableProperty] private string _operationStatus = "载入或手动添加路线。";
    [ObservableProperty] private string _newMapName = "少林";
    [ObservableProperty] private int _newX;
    [ObservableProperty] private int _newY;

    [RelayCommand]
    private void ImportRoutes()
    {
        if (!_runtimeData.TryResolveFile(Path.Combine("routes", "route-points-a.ini"), out var path))
        {
            OperationStatus = "未找到路线文件。";
            _log.Warn("AutoMonk", OperationStatus);
            return;
        }

        var result = _service.ImportLegacyRoutes(path, "配置");
        OperationStatus = $"已载入：{result.MapCount} 图 / {result.PointCount} 点。";
    }

    [RelayCommand]
    private void AddPoint()
    {
        if (string.IsNullOrWhiteSpace(NewMapName))
        {
            OperationStatus = "地图名不能为空。";
            return;
        }

        _service.AddPoint(new AutoMonkRoutePoint
        {
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            Label = $"{NewMapName.Trim()}-{C.RoutePoints.Count + 1}",
            SourceModule = "手动",
            SourcePath = ""
        });
        OperationStatus = "已添加。";
    }

    [RelayCommand]
    private void RemovePoint(AutoMonkRoutePointViewModel? point)
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
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认和尚" : ProfileName;
        _service.Save(name);
        OperationStatus = "已保存。";
    }

    [RelayCommand]
    private void Load()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? "默认和尚" : ProfileName;
        OperationStatus = _service.Load(name) ? "已加载。" : "未找到。";
    }

    private void Refresh()
    {
        RebuildPoints();
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(TargetKeywords));
        OnPropertyChanged(nameof(ActivityEndKeywords));
        OnPropertyChanged(nameof(SearchHotkey));
        OnPropertyChanged(nameof(AttackHotkey));
        OnPropertyChanged(nameof(EnableTeamCheck));
        OnPropertyChanged(nameof(EnableRouteNavigation));
        OnPropertyChanged(nameof(EnableBattleDetection));
        OnPropertyChanged(nameof(StopWhenActivityEnds));
        OnPropertyChanged(nameof(EnableRecovery));
        OnPropertyChanged(nameof(ScanIntervalMs));
        OnPropertyChanged(nameof(BattleCheckIntervalMs));
        OnPropertyChanged(nameof(ArrivalDistance));
        OnPropertyChanged(nameof(RouteRetryLimit));
        OnPropertyChanged(nameof(RouteNavigationMode));
        OnPropertyChanged(nameof(RouteNavigationHotkey));
        OnPropertyChanged(nameof(RouteNavigationClickX));
        OnPropertyChanged(nameof(RouteNavigationClickY));
        OnPropertyChanged(nameof(RouteNavigationActionIntervalMs));
        OnPropertyChanged(nameof(RouteNavigationDispatchMode));
        OnPropertyChanged(nameof(RecoveryNavigationMode));
        OnPropertyChanged(nameof(RecoveryNavigationHotkey));
        OnPropertyChanged(nameof(RecoveryNavigationClickX));
        OnPropertyChanged(nameof(RecoveryNavigationClickY));
        OnPropertyChanged(nameof(RecoveryNavigationActionIntervalMs));
        OnPropertyChanged(nameof(RecoveryNavigationDispatchMode));
        OnPropertyChanged(nameof(MapRegionX));
        OnPropertyChanged(nameof(MapRegionY));
        OnPropertyChanged(nameof(MapRegionWidth));
        OnPropertyChanged(nameof(MapRegionHeight));
        OnPropertyChanged(nameof(CoordRegionX));
        OnPropertyChanged(nameof(CoordRegionY));
        OnPropertyChanged(nameof(CoordRegionWidth));
        OnPropertyChanged(nameof(CoordRegionHeight));
        OnPropertyChanged(nameof(TargetNameRegionX));
        OnPropertyChanged(nameof(TargetNameRegionY));
        OnPropertyChanged(nameof(TargetNameRegionWidth));
        OnPropertyChanged(nameof(TargetNameRegionHeight));
        OnPropertyChanged(nameof(ActivityStatusRegionX));
        OnPropertyChanged(nameof(ActivityStatusRegionY));
        OnPropertyChanged(nameof(ActivityStatusRegionWidth));
        OnPropertyChanged(nameof(ActivityStatusRegionHeight));
        OnPropertyChanged(nameof(SourceSummary));
        RefreshSummary();
    }

    private void RebuildPoints()
    {
        Points.Clear();
        foreach (var point in C.RoutePoints.Take(200))
            Points.Add(new AutoMonkRoutePointViewModel(point, RefreshSummary));

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
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(ActionSummary));
        OnPropertyChanged(nameof(RouteActionSummary));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(EndSummary));
        OnPropertyChanged(nameof(RecognitionSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private static int KeywordCount(string value) =>
        (value ?? string.Empty).Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string RegionText(OcrRegionConfig region) => $"{region.X},{region.Y},{region.Width},{region.Height}";

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

