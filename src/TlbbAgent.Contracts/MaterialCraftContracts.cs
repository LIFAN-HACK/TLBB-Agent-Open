namespace TlbbAgent.Contracts;

public enum MaterialCraftStepAction
{
    Hotkey = 0,
    Click,
    HotkeyThenClick,
    Text,
    Wait,
    OpenPanel,
    SelectRecipe,
    Process,
    Confirm,
    CleanupBag
}

public enum MaterialCraftActionResult
{
    Executed = 0,
    Retry,
    Busy,
    Exhausted,
    Blocked,
    Completed,
    Cooldown,
    InsufficientResource,
    InvalidAction,
    EntryUnavailable
}

public sealed record MaterialCraftEntryDetection(
    bool Found,
    VisionBox Box,
    double Confidence,
    string Source,
    string Message)
{
    public static MaterialCraftEntryDetection Unavailable(string message) =>
        new(false, VisionBox.Empty, 0, "unavailable", message);
}

public interface IMaterialCraftEntryLocator
{
    bool IsAvailable { get; }
    string EngineId { get; }
    MaterialCraftEntryDetection Locate(CaptureResult frame);
}

public sealed record MaterialRecipeRow(
    string Name,
    VisionBox Box,
    double Confidence,
    string RawText);

public sealed record MaterialRecipePage(
    bool IsReliable,
    IReadOnlyList<MaterialRecipeRow> Rows,
    string Fingerprint,
    string Message)
{
    public long FrameSequence { get; init; }
    public VisionBox PanelBox { get; init; } = VisionBox.Empty;

    public static MaterialRecipePage Unavailable(string message) =>
        new(false, Array.Empty<MaterialRecipeRow>(), "", message);
}

public interface IMaterialRecipeListReader
{
    MaterialRecipePage Read(CaptureResult panelCrop, VisionBox panelBox);
}

public sealed class MaterialCraftStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public MaterialCraftStepAction Action { get; set; } = MaterialCraftStepAction.Hotkey;
    public string MaterialName { get; set; } = "";
    public string RecipeName { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public string Text { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int WaitMs { get; set; } = 600;
    public int RetryLimit { get; set; } = 2;
    public string SourceModule { get; set; } = "配置";
    public string SourcePath { get; set; } = "";
}

public sealed class MaterialCraftConfig
{
    public bool EnabledIntent { get; set; }
    public bool RequireVisualConfirmation { get; set; } = true;
    public int ProcessingCycleMinutes { get; set; } = 30;
    public string ProfileName { get; set; } = "默认加工";
    public string PlanName { get; set; } = "默认流程";
    public int BatchCount { get; set; } = 1;
    public bool LoopPlan { get; set; }
    public bool StopAfterBatch { get; set; } = true;
    public bool ProcessUntilMaterialsExhausted { get; set; } = true;
    public bool ProcessAllRecipes { get; set; } = true;
    public int MaxContinuousProcessMinutes { get; set; } = 30;
    public int MaxRecipePages { get; set; } = 20;
    public int StepIntervalMs { get; set; } = 500;
    public int MaxStepRetry { get; set; } = 2;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public bool EnableCleanupAfterBatch { get; set; }
    public string CleanupHotkey { get; set; } = "";
    public int CleanupClickX { get; set; } = 520;
    public int CleanupClickY { get; set; } = 360;
    public int CleanupWaitMs { get; set; } = 800;
    public List<MaterialCraftStep> Steps { get; set; } = new();
    public string SourceSummary { get; set; } = "";
}

public sealed record MaterialCraftImportResult(
    string SourcePath,
    int StepCount,
    int Skipped,
    string Message);

public interface IMaterialCraftConfigService
{
    MaterialCraftConfig Current { get; }
    event EventHandler? Changed;

    void NotifyChanged();

    IReadOnlyList<MaterialCraftStep> Steps { get; }
    MaterialCraftImportResult ImportPlainPlan(string path, string sourceModule);
    void AddStep(MaterialCraftStep step);
    bool RemoveStep(string id);
    void Clear();
    void Save(string profileName);
    bool Load(string profileName);
}
