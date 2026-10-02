namespace TlbbAgent.Contracts;

public enum LifeCollectMode
{
    Mining = 0,
    Herb,
    Gathering
}

public enum LifeCollectActionMode
{
    HotkeyOnly = 0,
    ClickClientPoint,
    HotkeyThenClickClientPoint
}

public sealed class LifeCollectPoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; } = "";
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class LifeCollectConfig
{
    public string ProfileName { get; set; } = "默认生活采集";
    public LifeCollectMode Mode { get; set; } = LifeCollectMode.Mining;
    public string MapName { get; set; } = "无量山";
    public string ResourceKeywords { get; set; } = "矿;药;采集";
    public string ScanHotkey { get; set; } = "Tab";
    public string CollectHotkey { get; set; } = "F";
    public int ScanIntervalMs { get; set; } = 600;
    public int CollectIntervalMs { get; set; } = 1200;
    public int ArrivalDistance { get; set; } = 6;
    public int MaxPointRetry { get; set; } = 3;
    public bool EnableRouteNavigation { get; set; } = true;
    public bool EnableBagFullStop { get; set; } = true;
    public LifeCollectActionMode ActionMode { get; set; } = LifeCollectActionMode.HotkeyOnly;
    public int ActionClickX { get; set; } = 520;
    public int ActionClickY { get; set; } = 360;
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
    public List<LifeCollectPoint> Points { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record LifeCollectImportResult(
    string SourcePath,
    int MapCount,
    int PointCount,
    int Skipped);

public interface ILifeCollectConfigService
{
    LifeCollectConfig Current { get; }
    event EventHandler? Changed;
    IReadOnlyList<string> MapNames { get; }
    IReadOnlyList<LifeCollectPoint> CurrentRoute { get; }
    LifeCollectImportResult ImportLegacyDirectory(string directory, string sourceModule);
    void AddPoint(LifeCollectPoint point);
    bool RemovePoint(string id);
    void Clear();
    void Save(string profileName);
    bool Load(string profileName);
}
