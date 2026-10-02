namespace TlbbAgent.Contracts;

public enum HorseBanditRole
{
    Leader = 0,
    Member = 1
}

public sealed class HorseBanditRoutePoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string SourceModule { get; set; } = "";
    public string SourcePath { get; set; } = "";
}

public sealed class HorseBanditConfig
{
    public string ProfileName { get; set; } = "默认马贼";
    public HorseBanditRole Role { get; set; } = HorseBanditRole.Leader;
    public bool EnableAutoFollow { get; set; } = true;
    public bool EnableTeamCheck { get; set; } = true;
    public bool AgreeTeamInvite { get; set; } = true;
    public bool WaitMembers { get; set; }
    public int WaitMemberSeconds { get; set; } = 10;
    public bool MemberSelectsTarget { get; set; }
    public bool LeaderNoAttack { get; set; } = true;
    public bool LeaderStayMounted { get; set; } = true;
    public bool MemberAttackAllTargets { get; set; }
    public bool CheckMembersBeforeStart { get; set; } = true;
    public bool CheckMembersAfterStart { get; set; } = true;
    public bool CheckMembersDuringPatrol { get; set; } = true;
    public int LostFollowDistance { get; set; } = 60;
    public int CirclePullFollowCount { get; set; } = 4;
    public int ForwardDistance { get; set; } = 4;
    public int LeadDistance { get; set; } = 10;
    public int WaitX { get; set; } = 237;
    public int WaitY { get; set; } = 128;
    public bool UseBuiltInRoutes { get; set; } = true;
    public bool UseCustomRoutes { get; set; }
    public int FindHorseMode { get; set; } = 1;
    public string AvoidMaps { get; set; } = "";
    public string TargetKeywords { get; set; } = "马贼;马贼喽啰;夺宝马贼;小丹;天降奇兽";
    public string SearchHotkey { get; set; } = "3";
    public string AttackHotkey { get; set; } = "3";
    public string MountHotkey { get; set; } = "5";
    public string FollowHotkey { get; set; } = "";
    public int ScanIntervalMs { get; set; } = 800;
    public int AttackIntervalMs { get; set; } = 1000;
    public int RecoverDelayMs { get; set; } = 1200;
    public NavigationRecognitionConfig NavigationRecognition { get; set; } = new();
    public ReturnNavigationConfig PatrolNavigation { get; set; } = new();
    public HorseBanditRecognitionConfig Recognition { get; set; } = new();
    public string SourceSummary { get; set; } = "";
    public List<HorseBanditRoutePoint> RoutePoints { get; set; } = new();
}

public sealed class HorseBanditRecognitionConfig
{
    public OcrRegionConfig TeamStatus { get; set; } = new() { X = 190, Y = 120, Width = 220, Height = 80 };
    public OcrRegionConfig FollowStatus { get; set; } = new() { X = 360, Y = 120, Width = 260, Height = 80 };
    public OcrRegionConfig DistanceText { get; set; } = new() { X = 620, Y = 120, Width = 120, Height = 40 };
    public string FollowOkKeywords { get; set; } = "跟随;队长;附近";
    public string LostFollowKeywords { get; set; } = "掉队;距离过远;找不到队长;离队;超出范围";
}

public sealed record HorseBanditRecognitionResult(
    string TeamStatusText,
    string FollowStatusText,
    string DistanceText,
    int? Distance,
    bool IsFollowing,
    bool IsLostFollow);

public sealed record HorseBanditImportResult(
    string SourcePath,
    string SourceModule,
    int FieldCount,
    int RouteMapCount,
    int RoutePointCount,
    int SkippedCount);

public interface IHorseBanditConfigService
{
    HorseBanditConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<string> MapNames { get; }
    HorseBanditImportResult ImportLegacyConfig(string path, string sourceModule);
    HorseBanditImportResult ImportLegacyRoutes(string path, string sourceModule);
    void Save(string profileName);
    bool Load(string profileName);
}

public interface IHorseBanditRecognitionService
{
    HorseBanditRecognitionResult Recognize(BoundGameWindow window, HorseBanditRecognitionConfig config);
}
