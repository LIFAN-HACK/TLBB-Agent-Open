namespace TlbbAgent.Contracts;

public sealed class AutoRebelRoutePoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string SourcePath { get; set; } = "";
}

public sealed class AutoRebelConfig
{
    public string ProfileName { get; set; } = "默认反贼";
    public string TargetKeywords { get; set; } = "反贼;造反恶贼;恶贼";
    public string SearchHotkey { get; set; } = "Tab";
    public string AttackHotkey { get; set; } = "F1";
    public bool EnableTeamCheck { get; set; } = true;
    public bool EnableRouteNavigation { get; set; } = true;
    public bool EnableBattleDetection { get; set; } = true;
    public bool EnableRecovery { get; set; } = true;
    public int ScanIntervalMs { get; set; } = 500;
    public int BattleCheckIntervalMs { get; set; } = 800;
    public int ArrivalDistance { get; set; } = 8;
    public int TargetLostDelayMs { get; set; } = 800;
    public int RouteRetryLimit { get; set; } = 3;
    public NavigationRecognitionConfig NavigationRecognition { get; set; } = new();
    public CombatRecognitionConfig CombatRecognition { get; set; } = new();
    public ReturnNavigationConfig RouteNavigation { get; set; } = new();
    public ReturnNavigationConfig RecoveryNavigation { get; set; } = new();
    public string SourceSummary { get; set; } = "";
    public List<AutoRebelRoutePoint> RoutePoints { get; set; } = new();
}

public sealed record AutoRebelImportResult(
    string SourcePath,
    string SourceModule,
    int MapCount,
    int PointCount,
    int SkippedCount);

public interface IAutoRebelConfigService
{
    AutoRebelConfig Current { get; }
    event EventHandler? Changed;
    IReadOnlyList<string> MapNames { get; }
    void AddPoint(AutoRebelRoutePoint point);
    bool RemovePoint(string id);
    void Clear();
    AutoRebelImportResult ImportLegacyRoutes(string path, string sourceModule);
    void Save(string profileName);
    bool Load(string profileName);
}
