namespace TlbbAgent.Contracts;

public enum GuildContributionStepAction
{
    Navigate = 0,
    TalkNpc,
    AcceptTask,
    ExecuteTask,
    SubmitTask,
    BuyItem,
    DeliverItem,
    UseHotkey,
    Click,
    Text,
    Wait,
    CleanupBag
}

public enum GuildContributionState
{
    Idle,
    ValidateConfig,
    SelectTaskType,
    RecognizePosition,
    NavigateStep,
    ExecuteStep,
    WaitAfterStep,
    NextStep,
    NextTaskType,
    NextRound,
    Recover,
    Stop
}

public enum GuildContributionExecutionMode
{
    HotkeyOnly = 0,
    ClickOnly,
    HotkeyThenClick
}

public sealed class GuildContributionTaskType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
}

public sealed class GuildContributionStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string TaskType { get; set; } = "";
    public string Label { get; set; } = "";
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string NpcName { get; set; } = "";
    public GuildContributionStepAction Action { get; set; } = GuildContributionStepAction.Navigate;
    public string Hotkey { get; set; } = "";
    public string Text { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int WaitMs { get; set; } = 800;
    public int RetryLimit { get; set; } = 2;
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class GuildContributionConfig
{
    public string ProfileName { get; set; } = "默认帮贡";
    public string PlanName { get; set; } = "帮贡流程";
    public int RoundCount { get; set; } = 1;
    public bool LoopPlan { get; set; }
    public bool StopAfterRounds { get; set; } = true;
    public int ArrivalDistance { get; set; } = 8;
    public int StepIntervalMs { get; set; } = 600;
    public int ActionIntervalMs { get; set; } = 800;
    public int MaxStepRetry { get; set; } = 2;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public GuildContributionExecutionMode ExecutionMode { get; set; } = GuildContributionExecutionMode.HotkeyThenClick;
    public string OpenTaskHotkey { get; set; } = "F8";
    public string CleanupHotkey { get; set; } = "";
    public bool EnableCleanupAfterTask { get; set; }
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
    public List<GuildContributionTaskType> TaskTypes { get; set; } = new();
    public List<GuildContributionStep> Steps { get; set; } = new();
    public List<string> ItemRules { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record GuildContributionImportResult(
    string SourcePath,
    int StepCount,
    int ItemRuleCount,
    int Skipped,
    string Message);

public interface IGuildContributionConfigService
{
    GuildContributionConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<GuildContributionTaskType> TaskTypes { get; }
    IReadOnlyList<GuildContributionStep> Steps { get; }
    IReadOnlyList<string> ItemRules { get; }
    GuildContributionImportResult ImportPlainPlan(string path, string sourceModule);
    int LoadItemRules(string path);
    void AddStep(GuildContributionStep step);
    bool RemoveStep(string id);
    void ClearSteps();
    void Save(string profileName);
    bool Load(string profileName);
}
