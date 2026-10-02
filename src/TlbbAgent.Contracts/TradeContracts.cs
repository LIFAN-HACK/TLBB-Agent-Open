namespace TlbbAgent.Contracts;

public enum TradeRouteAction
{
    Navigate = 0,
    Buy,
    Sell,
    TalkNpc,
    Hotkey,
    Click
}

public enum TradeExecutionMode
{
    HotkeyOnly = 0,
    ClickOnly,
    HotkeyThenClick
}

public sealed class TradeRoutePoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string NpcName { get; set; } = "";
    public string GoodsName { get; set; } = "";
    public TradeRouteAction Action { get; set; } = TradeRouteAction.Navigate;
    public string Hotkey { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int WaitMs { get; set; } = 900;
    public int RetryLimit { get; set; } = 2;
    public string Label { get; set; } = "";
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class TradeConfig
{
    public string ProfileName { get; set; } = "默认跑商";
    public string RouteName { get; set; } = "默认路线";
    public bool LoopRoute { get; set; } = true;
    public bool StopWhenRouteEnds { get; set; }
    public int ArrivalDistance { get; set; } = 6;
    public int StepIntervalMs { get; set; } = 600;
    public int ActionIntervalMs { get; set; } = 1000;
    public int MaxStepRetry { get; set; } = 2;
    public string OpenMapHotkey { get; set; } = "M";
    public TradeExecutionMode ExecutionMode { get; set; } = TradeExecutionMode.HotkeyThenClick;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public ReturnNavigationConfig Navigation { get; set; } = new()
    {
        Mode = ReturnNavigationMode.HotkeyOnly,
        Hotkey = "M",
        DispatchMode = InputDispatchMode.Adaptive,
        OpenMapDelayMs = 200,
        ActionIntervalMs = 800
    };
    public NavigationRecognitionConfig Recognition { get; set; } = new();
    public List<TradeRoutePoint> Points { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record TradeImportResult(
    string SourcePath,
    int PointCount,
    int Skipped,
    string Message);

public interface ITradeConfigService
{
    TradeConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<string> MapNames { get; }
    IReadOnlyList<TradeRoutePoint> Route { get; }
    TradeImportResult ImportPlainRoute(string path, string sourceModule);
    void AddPoint(TradeRoutePoint point);
    bool RemovePoint(string id);
    void Clear();
    void Save(string profileName);
    bool Load(string profileName);
}
