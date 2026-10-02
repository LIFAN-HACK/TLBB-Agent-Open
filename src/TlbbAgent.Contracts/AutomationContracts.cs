namespace TlbbAgent.Contracts;

public interface IWindowCaptureService
{
    CaptureResult? CaptureClient(nint hwnd);
    CaptureResult? CaptureRegion(nint hwnd, int x, int y, int w, int h);
    CaptureResult? CaptureAvatar(nint hwnd);
}

public interface ILatestFrameService
{
    CaptureResult? GetLatest(nint hwnd, int initialWaitMilliseconds = 250);
    CaptureResult? GetCached(nint hwnd, long frameSequence) => null;
    void Stop(nint hwnd);
}

public sealed record CaptureResult(int Width, int Height, byte[] Pixels)
{
    public long Sequence { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public double CaptureMilliseconds { get; init; }
    public string CaptureSource { get; init; } = "unknown";
}

public sealed record SkillCooldownInspection(
    SkillCooldownReadiness Readiness,
    string Reason,
    int DigitPixelCount,
    double DigitCoverage,
    double TemplateSimilarity,
    int X,
    int Y,
    int Width,
    int Height);

public interface ISkillCooldownRecognitionService
{
    SkillCooldownInspection Inspect(BoundGameWindow window, SkillPreset skill, GlobalConfig config);
    bool TryCaptureReadyTemplate(
        BoundGameWindow window,
        SkillPreset skill,
        GlobalConfig config,
        out SkillCooldownInspection inspection,
        out string error);
    void ClearCache();
}

public sealed record TemplateGlyphInfo(
    string Text,
    int Width,
    int Height,
    int OffsetX,
    int OffsetY,
    string Hex,
    string SourcePath,
    int SourceLine,
    string Format);

public interface ITemplateOcrService
{
    int LoadDictionary(string path);
    int LoadedGlyphCount { get; }
    int RenderableGlyphCount { get; }
    IReadOnlyList<TemplateGlyphInfo> Glyphs { get; }
    IReadOnlyList<TemplateGlyphInfo> RenderableGlyphs { get; }
    IReadOnlyCollection<string> LoadedSources { get; }
    string Recognize(CaptureResult image);
}

public interface IInputService
{
    InputDispatchMode DefaultDispatchMode { get; set; }
    bool YieldToUserInput { get; set; }
    int UserInputYieldMs { get; set; }
    InputYieldStatus GetYieldStatus(nint hwnd, InputDispatchMode mode);
    bool TryParseHotkey(string hotkey, out string error);
    bool SendKey(nint hwnd, string hotkey);
    bool SendKey(nint hwnd, string hotkey, InputDispatchMode mode);
    bool SendText(nint hwnd, string text);
    bool SendText(nint hwnd, string text, InputDispatchMode mode);
    // A true result only means the native input API accepted the dispatch.
    // Callers must confirm the intended game state on fresh perception frames.
    bool Click(nint hwnd, int x, int y);
    bool Click(nint hwnd, int x, int y, InputDispatchMode mode);
    bool Click(nint hwnd, int x, int y, InputDispatchMode mode, InputMouseButton button);
    void ConfigureWindowInput(
        nint hwnd,
        InputDispatchMode mode,
        bool yieldToUserInput,
        int userInputYieldMs)
    {
        DefaultDispatchMode = mode;
        YieldToUserInput = yieldToUserInput;
        UserInputYieldMs = userInputYieldMs;
    }
    void ClearWindowInputConfiguration(nint hwnd) { }
    bool TryConsumeGlobalCooldown(int cooldownMs);
    bool TryConsumeGlobalCooldown(nint hwnd, int cooldownMs) => TryConsumeGlobalCooldown(cooldownMs);
}

public sealed record InputYieldStatus(
    bool Enabled,
    bool CanReadLastInput,
    bool ShouldYield,
    int YieldMs,
    double LastInputAgeMs,
    double RemainingYieldMs,
    bool OwnRecentAutomationInput,
    bool TargetForeground,
    bool CursorInsideClient,
    bool TargetActive,
    string Scope,
    string Reason);

public enum InputDispatchMode
{
    PostMessage = 0,
    SendInput = 1,
    /// <summary>
    /// Keyboard and text stay on the non-invasive background channel. Mouse
    /// clicks use a short foreground lease and restore the previous foreground
    /// window and cursor position before returning.
    /// </summary>
    Adaptive = 2
}

public enum InputMouseButton
{
    Left = 0,
    Right = 1
}

public interface ILegacyDataAdapter
{
    void RegisterImport(string sourceModule, string sourcePath, string dataType, string targetModule, string notes);
    LegacyImportResult ImportKnownData(LegacyImportOptions options);
}

public sealed class LegacyImportOptions
{
    public string SourceRoot { get; init; } = "";
    public string ImportedRoot { get; init; } = Path.Combine(Environment.CurrentDirectory, "data", "imported");
    public bool Overwrite { get; init; } = true;
}

public sealed class LegacyImportResult
{
    public string SourceRoot { get; init; } = "";
    public string ImportedRoot { get; init; } = "";
    public int TotalItems { get; init; }
    public int CopiedItems { get; init; }
    public int SanitizedItems { get; init; }
    public int MissingItems { get; init; }
    public int DirectoryItems { get; init; }
    public string IndexPath { get; init; } = "";
    public string InternalSourceIndexPath { get; init; } = "";
    public IReadOnlyList<LegacyImportIndexItem> Items { get; init; } = Array.Empty<LegacyImportIndexItem>();
}

public sealed class LegacyImportIndexItem
{
    public string SourceModule { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string DataType { get; init; } = "";
    public string TargetModule { get; init; } = "";
    public string Priority { get; init; } = "";
    public string Status { get; init; } = "";
    public string Notes { get; init; } = "";
    public long? SourceBytes { get; init; }
    public long? TargetBytes { get; init; }
    public int? CopiedFileCount { get; init; }
    public string? TargetSha256 { get; init; }
    public string ImportedAt { get; init; } = "";
}

public sealed record CombatRecognitionResult(
    string TargetNameText,
    string TargetHpText,
    string PlayerHpText,
    string PlayerMpText,
    string PetHpText,
    int? TargetHpPercent,
    int? PlayerHpPercent,
    int? PlayerMpPercent,
    int? PetHpPercent)
{
    public bool HasTargetName => HasMeaningfulTargetText(TargetNameText);
    public bool HasTargetHp => TargetHpPercent is > 0;
    public bool HasTarget => HasTargetName || HasTargetHp || TargetLooksLikePlayer || TargetProtectionAmbiguous || TargetLooksLikePet || TargetIsAttackable is not null;
    public bool TargetLooksLikePlayer { get; init; }
    public bool TargetProtectionAmbiguous { get; init; }
    public bool TargetLooksLikePet { get; init; }
    public bool? TargetIsAttackable { get; init; }
    public bool TargetHasSchoolMarker { get; init; }
    public bool TargetHasPlayerPortrait { get; init; }
    public bool TargetHasPlayerStatusBars { get; init; }
    public bool TargetHostilityEvidenceAvailable { get; init; }
    public bool TargetIsHostileRedName { get; init; }
    public bool TargetAggressionEvidenceAvailable { get; init; }
    public bool TargetRecentlyDamagedPlayer { get; init; }
    public string TargetSampleKind { get; init; } = "";
    public string TargetSampleLabel { get; init; } = "";
    public string TargetSampleSource { get; init; } = "";
    public double TargetSampleScore { get; init; }
    public PetDeploymentState PetDeploymentState { get; init; } = PetDeploymentState.Unknown;
    public VisionTriState NearbyMonsterState { get; init; } = VisionTriState.Unknown;

    private static bool HasMeaningfulTargetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var chars = text.Where(IsTargetNameChar).ToArray();
        if (chars.Length < 2)
            return false;

        if (chars.All(c => c == chars[0]) && NoiseChars.Contains(chars[0]))
            return false;

        return true;
    }

    private static bool IsTargetNameChar(char c) =>
        char.IsLetterOrDigit(c)
        || c is >= '\u4E00' and <= '\u9FFF';

    private static readonly HashSet<char> NoiseChars = new()
    {
        '一', '丨', '丶', '丿', '亅', '｜', '|', '-', '_', '[', ']'
    };
}

public interface ICombatRecognitionService
{
    CombatRecognitionResult Recognize(BoundGameWindow window, CombatRecognitionConfig config);
}

public interface ICombatFrameRecognitionService
{
    CombatRecognitionResult Recognize(
        BoundGameWindow window,
        CombatRecognitionConfig config,
        CaptureResult clientFrame);
}

public sealed record CombatEnvironmentGateResult(
    bool ShouldSkipCombat,
    string Reason,
    string MapName = "",
    RuntimeSnapshotQuality Quality = RuntimeSnapshotQuality.Unavailable,
    int ConfidencePercent = 0)
{
    public static CombatEnvironmentGateResult Allow(string reason = "") => new(false, reason);

    public static CombatEnvironmentGateResult Skip(
        string reason,
        string mapName,
        RuntimeSnapshotQuality quality,
        int confidencePercent) => new(true, reason, mapName, quality, confidencePercent);
}

public interface ICombatEnvironmentGateService
{
    CombatEnvironmentGateResult Evaluate(BoundGameWindow window, GlobalConfig config);
}

public enum CombatLoopDecisionKind
{
    Unknown = 0,
    AcquireTarget,
    PendingFallbackAttack,
    PureKeyBaseline,
    ContinueWeakTarget,
    AttackCurrentTarget,
    SkipProtectedTarget,
    TargetDeadSwitch,
    TargetLostWait,
    TargetLostSwitch,
    NoDamageDiagnosis,
    NoActionDiagnosis,
    NoDamageReacquire,
    GrabMode,
    ReturnPoint,
    GameBuiltInMonitor,
    Recovery,
    EnvironmentGate,
    Stop
}

public sealed record CombatLoopDecision(
    CombatLoopDecisionKind Kind,
    string Reason,
    bool WillAcquire = false,
    bool WillAttack = false,
    bool WillCastSkill = false,
    bool WillSkipAttack = false,
    bool IsProtection = false)
{
    public static CombatLoopDecision For(
        CombatLoopDecisionKind kind,
        string reason,
        bool willAcquire = false,
        bool willAttack = false,
        bool willCastSkill = false,
        bool willSkipAttack = false,
        bool isProtection = false) =>
        new(kind, reason, willAcquire, willAttack, willCastSkill, willSkipAttack, isProtection);
}

public sealed record TargetProtectionSampleMatch(
    string Kind,
    string Label,
    string SampleId,
    string SourcePath,
    double Score);

public interface ITargetProtectionSampleService
{
    TargetProtectionSampleMatch? Match(CaptureResult client, CombatRecognitionConfig config, TargetingPreset targeting);
}

public sealed record TargetSafetyOptions(
    bool AllowWeakKnownTarget = false,
    bool HadTarget = false,
    bool HasConfirmedIdentity = false,
    bool IncludeStrategyFilters = true);

public sealed record TargetSafetyDecision(
    bool Allowed,
    string Reason,
    string AcquireReason,
    bool IsProtectedTarget,
    bool ShouldAcquire,
    bool ResetTargetProgress = false,
    string WarningLabel = "",
    string WarningDetail = "",
    string UnsupportedStrategyWarning = "")
{
    public static TargetSafetyDecision Allow() =>
        new(true, "允许攻击", string.Empty, false, false);

    public static TargetSafetyDecision Reject(
        string reason,
        string acquireReason,
        bool isProtectedTarget,
        bool resetTargetProgress = false,
        string warningLabel = "",
        string warningDetail = "",
        string unsupportedStrategyWarning = "") =>
        new(false, reason, acquireReason, isProtectedTarget, true, resetTargetProgress, warningLabel, warningDetail, unsupportedStrategyWarning);
}

public interface ITargetSafetyService
{
    TargetSafetyDecision Evaluate(GlobalConfig config, CombatRecognitionResult combat, TargetSafetyOptions? options = null);
    bool HasActionableTarget(CombatRecognitionResult combat, TargetSafetyOptions? options = null);
    bool HasStrongTargetSignal(CombatRecognitionResult combat);
    bool RequiresRecognizedTarget(GlobalConfig config);
}

public interface IAutomationController
{
    string Key { get; }
    string Name { get; }
    string Group { get; }
    RunState RunState { get; }
    string CurrentStateName { get; }
    event EventHandler? StateChanged;

    void Attach(BoundGameWindow window);
    void Start();
    void Pause();
    void Stop();
}

public interface INormalHangController : IAutomationController { }
public interface IReturnPointController : IAutomationController { }
public interface IHorseBanditModeController : IAutomationController { }
public interface IAutoRebelController : IAutomationController { }
public interface IAutoMonkController : IAutomationController { }
public interface IDungeonController : IAutomationController { }
public interface INpcGrabController : IAutomationController { }
public interface ILifeCollectController : IAutomationController { }
public interface ITradeController : IAutomationController { }
public interface IMaterialCraftController : IAutomationController { }
public interface ISchoolTaskController : IAutomationController { }
public interface ITreasureMapController : IAutomationController { }
public interface IGuildContributionController : IAutomationController { }
public interface IUtilityController : IAutomationController { }

public interface INpcTargetingService
{
    NpcTargetingResult SelectTarget(BoundGameWindow window, NpcGrabConfig config);
}

public interface INpcActionService
{
    NpcActionResult Scan(BoundGameWindow window, NpcGrabConfig config);
    NpcActionResult Execute(BoundGameWindow window, NpcTargetingResult target, NpcGrabConfig config);
}

public interface IStateMachineEngine
{
    IDisposable Run(string name, Func<CancellationToken, Task> loop, Action<Exception>? onFault = null);
}

public interface IAutomationRegistry
{
    event EventHandler<AutomationControllerRegisteredEventArgs>? ControllerRegistered;
    IReadOnlyList<IAutomationController> Controllers { get; }
    void AttachAll(BoundGameWindow? window);
    IAutomationController? Find(string key);
    IAutomationController? Find(BoundGameWindow window, string key);
    IAutomationController? Prepare(BoundGameWindow window, string key);
    IReadOnlyList<IAutomationController> FindForWindow(BoundGameWindow window);
    BoundGameWindow? GetWindow(IAutomationController controller);
    bool RefreshConfiguration(BoundGameWindow window);
    void StopWindow(BoundGameWindow window);
    void StopAll();
}

public sealed class AutomationControllerRegisteredEventArgs(
    BoundGameWindow window,
    IAutomationController controller) : EventArgs
{
    public BoundGameWindow Window { get; } = window;
    public IAutomationController Controller { get; } = controller;
}
