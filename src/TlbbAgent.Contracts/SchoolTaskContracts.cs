namespace TlbbAgent.Contracts;

public enum SchoolTaskStepAction
{
    Navigate = 0,
    TalkNpc,
    AcceptTask,
    CompleteTask,
    SubmitTask,
    BuyItem,
    StoreItem,
    TradeItem,
    UseHotkey,
    Click,
    Text,
    Wait,
    CleanupBag
}

public enum SchoolTaskState
{
    Idle,
    ValidateConfig,
    RecognizePosition,
    NavigateStep,
    ExecuteStep,
    WaitAfterStep,
    NextStep,
    NextRound,
    Recover,
    Stop
}

public enum SchoolTaskExecutionMode
{
    HotkeyOnly = 0,
    ClickOnly,
    HotkeyThenClick
}

public sealed class SchoolTaskStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string NpcName { get; set; } = "";
    public SchoolTaskStepAction Action { get; set; } = SchoolTaskStepAction.Navigate;
    public string Hotkey { get; set; } = "";
    public string Text { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int WaitMs { get; set; } = 800;
    public int RetryLimit { get; set; } = 2;
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class SchoolTaskConfig
{
    public string ProfileName { get; set; } = "默认师门";
    public string PlanName { get; set; } = "师门流程";
    public int RoundCount { get; set; } = 20;
    public bool LoopPlan { get; set; }
    public bool StopAfterRounds { get; set; } = true;
    public int ArrivalDistance { get; set; } = 8;
    public int StepIntervalMs { get; set; } = 600;
    public int ActionIntervalMs { get; set; } = 800;
    public int MaxStepRetry { get; set; } = 2;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public SchoolTaskExecutionMode ExecutionMode { get; set; } = SchoolTaskExecutionMode.HotkeyThenClick;
    public string OpenTaskHotkey { get; set; } = "F8";
    public string CleanupHotkey { get; set; } = "";
    public bool EnableCleanupAfterRound { get; set; }
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
    public List<SchoolTaskStep> Steps { get; set; } = new();
    public List<string> StoreItems { get; set; } = new();
    public List<string> TradeItems { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record SchoolTaskImportResult(
    string SourcePath,
    int StepCount,
    int StoreItemCount,
    int TradeItemCount,
    int Skipped,
    string Message);

public interface ISchoolTaskConfigService
{
    SchoolTaskConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<SchoolTaskStep> Steps { get; }
    IReadOnlyList<string> StoreItems { get; }
    IReadOnlyList<string> TradeItems { get; }
    SchoolTaskImportResult ImportPlainPlan(string path, string sourceModule);
    int LoadItemList(string path, bool tradeItems);
    void AddStep(SchoolTaskStep step);
    bool RemoveStep(string id);
    void ClearSteps();
    void Save(string profileName);
    bool Load(string profileName);
}
