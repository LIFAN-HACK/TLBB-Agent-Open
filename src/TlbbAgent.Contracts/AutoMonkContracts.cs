namespace TlbbAgent.Contracts;

public sealed class AutoMonkRoutePoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string SourcePath { get; set; } = "";
}

public sealed class AutoMonkConfig
{
    public string ProfileName { get; set; } = "默认和尚";
    public string TargetKeywords { get; set; } = "和尚;丹和尚;恶僧;僧";
    public string ActivityEndKeywords { get; set; } = "活动结束;和尚结束;没有和尚;已结束";
    public string SearchHotkey { get; set; } = "Tab";
    public string AttackHotkey { get; set; } = "F1";
    public bool EnableTeamCheck { get; set; } = true;
    public bool EnableRouteNavigation { get; set; } = true;
    public bool EnableBattleDetection { get; set; } = true;
    public bool StopWhenActivityEnds { get; set; } = true;
    public bool EnableRecovery { get; set; } = true;
    public int ScanIntervalMs { get; set; } = 500;
    public int BattleCheckIntervalMs { get; set; } = 800;
    public int ArrivalDistance { get; set; } = 8;
    public int RouteRetryLimit { get; set; } = 3;
    public NavigationRecognitionConfig NavigationRecognition { get; set; } = new();
    public CombatRecognitionConfig CombatRecognition { get; set; } = new();
    public OcrRegionConfig ActivityStatusRegion { get; set; } = new() { X = 330, Y = 180, Width = 420, Height = 100 };
    public ReturnNavigationConfig RouteNavigation { get; set; } = new();
    public ReturnNavigationConfig RecoveryNavigation { get; set; } = new();
    public string SourceSummary { get; set; } = "";
    public List<AutoMonkRoutePoint> RoutePoints { get; set; } = new();
}

public sealed record AutoMonkImportResult(
    string SourcePath,
    string SourceModule,
    int MapCount,
    int PointCount,
    int SkippedCount);

public interface IAutoMonkConfigService
{
    AutoMonkConfig Current { get; }
    event EventHandler? Changed;
    IReadOnlyList<string> MapNames { get; }
    void AddPoint(AutoMonkRoutePoint point);
    bool RemovePoint(string id);
    void Clear();
    AutoMonkImportResult ImportLegacyRoutes(string path, string sourceModule);
    void Save(string profileName);
    bool Load(string profileName);
}
