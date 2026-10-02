namespace TlbbAgent.Contracts;

public enum NpcGrabTargetKind
{
    Rebel = 0,
    Small,
    SmallDan,
    Small2,
    Monk,
    Chest,
    Shelved,
    GaoJiaWei,
    GaoJiaWei23
}

public enum NpcGrabActionMode
{
    HotkeyOnly = 0,
    ClickClientPoint,
    HotkeyThenClickClientPoint
}

public enum SmallNpcGrabStrategy
{
    AutoRecognizeOnly = 0,
    ContextFastOnly,
    DualChannelPriority
}

public sealed class SmallNpcFastGrabConfig
{
    public SmallNpcGrabStrategy Strategy { get; set; } = SmallNpcGrabStrategy.DualChannelPriority;
    public bool EnableRefreshBurst { get; set; } = false;
    public string RefreshMinuteMarks { get; set; } = "0;15;30;45";
    public int RefreshWindowBeforeSeconds { get; set; } = 20;
    public int RefreshWindowAfterSeconds { get; set; } = 75;
    public int BurstScanIntervalMs { get; set; } = 80;
    public bool EnableContextFastAction { get; set; } = false;
    public bool ContextFastOnlyInRefreshWindow { get; set; } = true;
    public int ContextValidSeconds { get; set; } = 2100;
    public int MaxContextFastAttemptsPerWindow { get; set; } = 2;
}

public sealed class NpcGrabTargetConfig
{
    public NpcGrabTargetKind Kind { get; set; }
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public string Keywords { get; set; } = "";
    public int Priority { get; set; }
    public string SourceModule { get; set; } = "内置";
}

public sealed class NpcGrabConfig
{
    public string ProfileName { get; set; } = "默认抢NPC";
    public string ScanHotkey { get; set; } = "Tab";
    public string ActionHotkey { get; set; } = "F";
    public int ScanIntervalMs { get; set; } = 450;
    public int ClickIntervalMs { get; set; } = 250;
    public int RetryDelayMs { get; set; } = 500;
    public int RetryLimit { get; set; } = 3;
    public NpcGrabActionMode ActionMode { get; set; } = NpcGrabActionMode.HotkeyOnly;
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public CombatRecognitionConfig Recognition { get; set; } = new();
    public SmallNpcFastGrabConfig SmallNpcFastGrab { get; set; } = new();
    public string SourceSummary { get; set; } = "按目标词、刷新窗口和上下文快抢。";
    public List<NpcGrabTargetConfig> Targets { get; set; } = CreateDefaultTargets();

    public static List<NpcGrabTargetConfig> CreateDefaultTargets() =>
        new()
        {
            new() { Kind = NpcGrabTargetKind.Rebel, DisplayName = "抢反", Enabled = true, Keywords = "反贼;造反恶贼;恶贼", Priority = 10 },
            new() { Kind = NpcGrabTargetKind.Small, DisplayName = "抢小", Enabled = false, Keywords = "江湖宵小;天降奇兽", Priority = 20 },
            new() { Kind = NpcGrabTargetKind.SmallDan, DisplayName = "抢小丹", Enabled = false, Keywords = "小丹;天降奇兽;江湖宵小", Priority = 30 },
            new() { Kind = NpcGrabTargetKind.Small2, DisplayName = "抢小2", Enabled = false, Keywords = "江湖宵小;天降奇兽", Priority = 40 },
            new() { Kind = NpcGrabTargetKind.Monk, DisplayName = "抢和尚", Enabled = true, Keywords = "抢丹和尚;丹和尚;少林云游武僧;和尚;恶僧;蒙面恶僧", Priority = 50 },
            new() { Kind = NpcGrabTargetKind.Chest, DisplayName = "抢箱子", Enabled = false, Keywords = "箱子;宝箱", Priority = 60 },
            new() { Kind = NpcGrabTargetKind.Shelved, DisplayName = "抢上架", Enabled = false, Keywords = "上架;上架物品", Priority = 70 },
            new() { Kind = NpcGrabTargetKind.GaoJiaWei, DisplayName = "高甲卫", Enabled = false, Keywords = "高级方枢甲卫;高甲卫;方枢甲卫", Priority = 80 },
            new() { Kind = NpcGrabTargetKind.GaoJiaWei23, DisplayName = "高甲卫23", Enabled = false, Keywords = "高甲卫23;高甲23;高级方枢甲卫", Priority = 90 },
        };
}

public sealed record NpcTargetingResult(
    bool Found,
    NpcGrabTargetKind? Kind,
    string DisplayName,
    string RecognizedText,
    string MatchedKeyword)
{
    public static NpcTargetingResult Empty(string recognizedText = "") =>
        new(false, null, "", recognizedText, "");
}

public sealed record NpcActionResult(
    bool Executed,
    string Message);

public sealed record SmallNpcFastGrabPlan(
    bool ShouldExecuteContextFastAction,
    NpcTargetingResult? ContextTarget,
    int NextScanDelayMs,
    bool IsRefreshBurstWindow,
    bool ShouldRunActiveRecognition,
    string Reason);

public interface INpcGrabConfigService
{
    NpcGrabConfig Current { get; }
    event EventHandler? Changed;
    IReadOnlyList<NpcGrabTargetConfig> EnabledTargets { get; }
    void ResetDefaults();
    void Save(string profileName);
    bool Load(string profileName);
}

public interface ISmallNpcFastGrabService
{
    SmallNpcFastGrabPlan Plan(NpcGrabConfig config);
    void ObserveActionResult(NpcTargetingResult target, NpcActionResult result);
    bool IsSmallNpcTarget(NpcGrabTargetKind? kind);
}
