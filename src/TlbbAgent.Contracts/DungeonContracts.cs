namespace TlbbAgent.Contracts;

public enum DungeonMode
{
    OldThreeRings = 0,
    NewThreeRings = 1
}

public enum DungeonBossPosition
{
    Default = 0,
    Left,
    Middle,
    Right,
    Custom
}

public enum DungeonStepAction
{
    HotkeyOnly = 0,
    ClickClientPoint,
    HotkeyThenClickClientPoint,
    WaitBattle,
    WaitCompletion
}

public sealed class DungeonScriptStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DungeonMode Mode { get; set; } = DungeonMode.OldThreeRings;
    public int Order { get; set; }
    public string StageName { get; set; } = "";
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public DungeonStepAction Action { get; set; } = DungeonStepAction.HotkeyOnly;
    public string Hotkey { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int DelayAfterMs { get; set; } = 800;
    public string SourceModule { get; set; } = "内置";
    public string SourcePath { get; set; } = "";
}

public sealed class DungeonConfig
{
    public string ProfileName { get; set; } = "默认三环";
    public DungeonMode Mode { get; set; } = DungeonMode.OldThreeRings;
    public int RunCount { get; set; } = 1;
    public int TeamNumber { get; set; } = 1;
    public bool IGoLeftBridge { get; set; }
    public DungeonBossPosition NewThreeBoss2Position { get; set; } = DungeonBossPosition.Default;
    public int NewThreeBoss2CustomX { get; set; } = 520;
    public int NewThreeBoss2CustomY { get; set; } = 360;
    public bool EnableClearBagAfterFinish { get; set; }
    public bool EnableChainNextTask { get; set; }
    public string ChainNextModeKey { get; set; } = "";
    public string StartDungeonHotkey { get; set; } = "F8";
    public string InteractHotkey { get; set; } = "F";
    public string AttackHotkey { get; set; } = "F1";
    public string ClearBagHotkey { get; set; } = "";
    public string ExitDungeonHotkey { get; set; } = "";
    public int StepIntervalMs { get; set; } = 800;
    public int BattleCheckIntervalMs { get; set; } = 1000;
    public string CompletionKeywords { get; set; } = "完成;副本结束;通关;离开副本";
    public NavigationRecognitionConfig NavigationRecognition { get; set; } = new();
    public CombatRecognitionConfig CombatRecognition { get; set; } = new();
    public OcrRegionConfig CompletionRegion { get; set; } = new() { X = 330, Y = 180, Width = 420, Height = 100 };
    public ReturnNavigationConfig StageNavigation { get; set; } = new();
    public ReturnNavigationConfig RecoveryNavigation { get; set; } = new();
    public string SourceSummary { get; set; } = "";
    public List<DungeonScriptStep> Steps { get; set; } = CreateDefaultSteps();

    public static List<DungeonScriptStep> CreateDefaultSteps() =>
        new()
        {
            new() { Mode = DungeonMode.OldThreeRings, Order = 10, StageName = "老三环：进入副本", Action = DungeonStepAction.HotkeyOnly, Hotkey = "F8" },
            new() { Mode = DungeonMode.OldThreeRings, Order = 20, StageName = "老三环：执行战斗", Action = DungeonStepAction.WaitBattle, Hotkey = "F1" },
            new() { Mode = DungeonMode.OldThreeRings, Order = 30, StageName = "老三环：确认完成", Action = DungeonStepAction.WaitCompletion },
            new() { Mode = DungeonMode.NewThreeRings, Order = 10, StageName = "新三环：进入副本", Action = DungeonStepAction.HotkeyOnly, Hotkey = "F8" },
            new() { Mode = DungeonMode.NewThreeRings, Order = 20, StageName = "新三环：左桥/队伍站位", Action = DungeonStepAction.ClickClientPoint, ClickX = 520, ClickY = 360 },
            new() { Mode = DungeonMode.NewThreeRings, Order = 30, StageName = "新三环：2 boss 站位", Action = DungeonStepAction.ClickClientPoint, ClickX = 560, ClickY = 380 },
            new() { Mode = DungeonMode.NewThreeRings, Order = 40, StageName = "新三环：执行战斗", Action = DungeonStepAction.WaitBattle, Hotkey = "F1" },
            new() { Mode = DungeonMode.NewThreeRings, Order = 50, StageName = "新三环：确认完成", Action = DungeonStepAction.WaitCompletion },
        };
}

public interface IDungeonConfigService
{
    DungeonConfig Current { get; }
    event EventHandler? Changed;
    IReadOnlyList<DungeonScriptStep> CurrentSteps { get; }
    void AddStep(DungeonScriptStep step);
    bool RemoveStep(string id);
    void ClearSteps(DungeonMode mode);
    void ResetDefaults();
    void Save(string profileName);
    bool Load(string profileName);
}
