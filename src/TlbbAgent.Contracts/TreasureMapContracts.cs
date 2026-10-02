namespace TlbbAgent.Contracts;

public enum TreasureMapStepAction
{
    OpenMap = 0,
    Navigate,
    Dig,
    OpenBox,
    Pickup,
    Click,
    UseLocator,
    WarehouseFetch,
    ShopFetch,
    Heal,
    CleanupBag,
    Wait
}

public enum TreasureMapState
{
    Idle,
    ValidateConfig,
    PrepareMap,
    RecognizePosition,
    NavigatePoint,
    ExecuteDig,
    OpenReward,
    CleanupBag,
    NextPoint,
    Recover,
    Stop
}

public enum TreasureMapActionMode
{
    HotkeyOnly = 0,
    ClickOnly,
    HotkeyThenClick
}

public sealed class TreasureMapPoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; } = "";
    public TreasureMapStepAction Action { get; set; } = TreasureMapStepAction.Dig;
    public string Hotkey { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int WaitMs { get; set; } = 900;
    public int RetryLimit { get; set; } = 2;
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class TreasureMapConfig
{
    public string ProfileName { get; set; } = "默认挖图";
    public string PlanName { get; set; } = "挖图流程";
    public string MapName { get; set; } = "藏宝图";
    public bool SmallMapOnly { get; set; }
    public string BigMapKeywords { get; set; } = "南诏;南海;苗疆;长白山;黄龙府";
    public bool UseLocator { get; set; } = true;
    public bool FetchFromWarehouse { get; set; } = true;
    public bool FetchFromShop { get; set; }
    public bool PickupDrops { get; set; } = true;
    public bool EnableCleanupAfterDig { get; set; } = true;
    public bool OpenNormalBox { get; set; }
    public bool OpenGoldBox { get; set; }
    public string BoxOrder { get; set; } = "暮收;句芒;祝融;共工;后土";
    public int MapBatchCount { get; set; } = 1;
    public bool LoopRoute { get; set; }
    public bool StopWhenRouteEnds { get; set; } = true;
    public int ArrivalDistance { get; set; } = 6;
    public int StepIntervalMs { get; set; } = 600;
    public int ActionIntervalMs { get; set; } = 900;
    public int MaxPointRetry { get; set; } = 2;
    public string OpenMapHotkey { get; set; } = "F8";
    public string DigHotkey { get; set; } = "F9";
    public string PickupHotkey { get; set; } = "";
    public string LocatorHotkey { get; set; } = "";
    public string CleanupHotkey { get; set; } = "";
    public TreasureMapActionMode ActionMode { get; set; } = TreasureMapActionMode.HotkeyThenClick;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public ReturnNavigationConfig Navigation { get; set; } = new()
    {
        Mode = ReturnNavigationMode.HotkeyThenClickClientPoint,
        Hotkey = "F7",
        ClickX = 520,
        ClickY = 360,
        OpenMapDelayMs = 160,
        ActionIntervalMs = 800
    };
    public NavigationRecognitionConfig Recognition { get; set; } = new();
    public List<TreasureMapPoint> Points { get; set; } = new();
    public List<string> KeepItems { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record TreasureMapImportResult(
    string SourcePath,
    int PointCount,
    int KeepItemCount,
    int Skipped,
    string Message);

public interface ITreasureMapConfigService
{
    TreasureMapConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<TreasureMapPoint> Route { get; }
    IReadOnlyList<string> KeepItems { get; }
    TreasureMapImportResult ImportCoordinateFile(string path, string sourceModule, string mapName);
    int LoadKeepItems(string path);
    void AddPoint(TreasureMapPoint point);
    bool RemovePoint(string id);
    void ClearPoints();
    void Save(string profileName);
    bool Load(string profileName);
}
