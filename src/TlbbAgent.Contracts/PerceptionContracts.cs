namespace TlbbAgent.Contracts;

public enum PerceptionSourceKind
{
    Unavailable = 0,
    [Obsolete("Legacy OCR is disabled in the production perception pipeline.")]
    LegacyOcr,
    OnnxLayout,
    OpenText,
    Fused,
    Degraded
}

/// <summary>
/// L2 OpenText slot hint. Affects preprocess/postprocess only — never closed-set class labels.
/// </summary>
public enum OpenTextSlot
{
    Generic = 0,
    TargetName,
    MapName,
    ItemName,
    DialogOption,
    Toast,
    QuestTracker,
    CoordText
}

public sealed record OpenTextResult(
    string Text,
    double Confidence,
    OpenTextSlot Slot,
    PerceptionSourceKind Source,
    string Engine = "",
    string Message = "")
{
    public static OpenTextResult Empty(OpenTextSlot slot, string message = "", string engine = "") =>
        new("", 0, slot, PerceptionSourceKind.Unavailable, engine, message);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public enum LayoutClass
{
    Unknown = 0,
    TargetPanel,
    TargetHpBar,
    TargetName,
    PlayerStatusPanel,
    PetStatusPanel,
    SkillBar,
    Minimap,
    PlayerHpBar,
    PlayerMpBar,
    PetHpBar,
    Dialog,
    ProgressBar
}

public enum TargetPresence
{
    Unknown = 0,
    None,
    Present
}

public enum TargetIdentity
{
    Unknown = 0,
    Monster,
    Player,
    Pet,
    Boss,
    Elite,
    Npc
}

public enum TargetLifeState
{
    Unknown = 0,
    NoTarget,
    Live,
    Weak,
    DeadByHpBottom,
    Gone,
    Ambiguous
}

public sealed record VisionBox(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public static VisionBox Empty { get; } = new(0, 0, 0, 0);

    public static VisionBox FromXyxy(int x1, int y1, int x2, int y2) =>
        new(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
}

public sealed record LayoutDetectionItem(
    LayoutClass Class,
    VisionBox Box,
    double Confidence,
    string Label = "");

public sealed record LayoutSnapshot(
    int Width,
    int Height,
    PerceptionSourceKind Source,
    DateTimeOffset CapturedAt,
    IReadOnlyList<LayoutDetectionItem> Detections,
    string ModelId = "",
    string Message = "",
    double InferenceMilliseconds = 0)
{
    public static LayoutSnapshot Unavailable(int width, int height, string message) =>
        new(width, height, PerceptionSourceKind.Unavailable, DateTimeOffset.Now, Array.Empty<LayoutDetectionItem>(), "", message);

    public LayoutDetectionItem? Best(LayoutClass @class) =>
        Detections
            .Where(item => item.Class == @class)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
}

public sealed record TargetPerception
{
    public TargetPresence Presence { get; init; } = TargetPresence.Unknown;
    public TargetIdentity Identity { get; init; } = TargetIdentity.Unknown;
    public TargetLifeState Life { get; init; } = TargetLifeState.Unknown;
    public int? HpPercent { get; init; }
    public string NameText { get; init; } = "";
    public bool ShouldAcquireTarget { get; init; }
    public bool ShouldSwitchTarget { get; init; }
    public bool IsProtectedTarget { get; init; }
    public bool IsActionableTarget { get; init; }
    public bool HostilityEvidenceAvailable { get; init; }
    public bool IsHostileRedName { get; init; }
    public bool AggressionEvidenceAvailable { get; init; }
    public bool RecentlyDamagedPlayer { get; init; }
    public double Confidence { get; init; }
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
    public VisionBox PanelBox { get; init; } = VisionBox.Empty;
    public VisionBox HpBarBox { get; init; } = VisionBox.Empty;
    public VisionBox NameBox { get; init; } = VisionBox.Empty;
    public string Message { get; init; } = "";
}

/// <summary>
/// Identity evidence produced by a dedicated L2 target classifier.
/// Unknown means the classifier is unavailable or did not reach its confidence threshold.
/// </summary>
public sealed record TargetIdentityEvidence(
    TargetIdentity Identity,
    double Confidence,
    PerceptionSourceKind Source,
    string ModelId = "",
    string Message = "")
{
    public bool IsAvailable => Source != PerceptionSourceKind.Unavailable;
    public bool HostilityEvidenceAvailable { get; init; }
    public bool IsHostileRedName { get; init; }
    public bool AggressionEvidenceAvailable { get; init; }
    public bool RecentlyDamagedPlayer { get; init; }

    public static TargetIdentityEvidence Unavailable(string message, string modelId = "") =>
        new(TargetIdentity.Unknown, 0, PerceptionSourceKind.Unavailable, modelId, message);
}

public sealed record SelfBarsPerception
{
    public int? PlayerHpPercent { get; init; }
    public int? PlayerMpPercent { get; init; }
    public int? PetHpPercent { get; init; }
    public VisionBox PlayerPanelBox { get; init; } = VisionBox.Empty;
    public string PlayerPanelGeometrySource { get; init; } = "unavailable";
    public VisionBox PlayerHpRelativeBox { get; init; } = VisionBox.Empty;
    public VisionBox PlayerMpRelativeBox { get; init; } = VisionBox.Empty;
    public VisionBox PlayerHpSourceBox { get; init; } = VisionBox.Empty;
    public VisionBox PlayerMpSourceBox { get; init; } = VisionBox.Empty;
    public VisionBox PetPanelBox { get; init; } = VisionBox.Empty;
    public string PetPanelGeometrySource { get; init; } = "unavailable";
    public VisionBox PetHpRelativeBox { get; init; } = VisionBox.Empty;
    public VisionBox PetHpSourceBox { get; init; } = VisionBox.Empty;
    public PetDeploymentState PetState { get; init; } = PetDeploymentState.Unknown;
    public double PlayerConfidence { get; init; }
    public double PetConfidence { get; init; }
    public string Message { get; init; } = "";
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
}

public enum VisionTriState
{
    Unknown = 0,
    Absent,
    Present
}

public enum PetDeploymentState
{
    Unknown = 0,
    RestingOrAbsent,
    Deployed
}

public enum VisionCooldownState
{
    Unknown = 0,
    Ready,
    Cooling
}

public enum VisionSkillIdentityState
{
    Unknown = 0,
    Empty,
    Candidate,
    Matched
}

public sealed record VisionSkillSlotPerception
{
    public long FrameSequence { get; init; }
    public int Index { get; init; }
    public string SlotId { get; init; } = "";
    public int Row { get; init; }
    public int Column { get; init; }
    public string Surface { get; init; } = "";
    public string Key { get; init; } = "";
    public string ActivationKind { get; init; } = "";
    public VisionBox SlotBox { get; init; } = VisionBox.Empty;
    public int? ClickX { get; init; }
    public int? ClickY { get; init; }
    public VisionCooldownState Cooldown { get; init; } = VisionCooldownState.Unknown;
    public string SkillId { get; init; } = "";
    public string SkillName { get; init; } = "";
    public VisionSkillIdentityState Identity { get; init; } = VisionSkillIdentityState.Unknown;
    public string IdentitySource { get; init; } = "";
    public double MatchScore { get; init; }
    public double MatchMargin { get; init; }
    public bool HangUsable { get; init; }
    public bool Castable { get; init; }
    public string PolicyClass { get; init; } = "";
    public string ReleaseRule { get; init; } = "";
    public double Confidence { get; init; }
    public string Message { get; init; } = "";
}

public sealed record PetRestoreSkillPerception
{
    public string Slot { get; init; } = "";
    public string Variant { get; init; } = "";
    public string BusinessClass { get; init; } = "";
    public VisionCooldownState Cooldown { get; init; } = VisionCooldownState.Unknown;
    public int? ClickX { get; init; }
    public int? ClickY { get; init; }
    public double Confidence { get; init; }
    public bool IsMatched { get; init; }
}

public sealed record SkillBarPerception
{
    public IReadOnlyList<SkillCooldownInspection> Slots { get; init; } =
        Array.Empty<SkillCooldownInspection>();
    public IReadOnlyList<VisionSkillSlotPerception> VisualSlots { get; init; } =
        Array.Empty<VisionSkillSlotPerception>();
    public IReadOnlyList<PetRestoreSkillPerception> PetRestoreSkills { get; init; } =
        Array.Empty<PetRestoreSkillPerception>();
    public string Message { get; init; } = "";
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
}

public sealed record RightSkillSlotGeometry
{
    public int Index { get; init; }
    public string SlotId { get; init; } = "";
    public int Row { get; init; }
    public int Column { get; init; }
    public VisionBox Box { get; init; } = VisionBox.Empty;
    public bool IsComplete { get; init; }
    public double BorderConfidence { get; init; }
}

public sealed record RightSkillBarPerception
{
    public long FrameSequence { get; init; }
    public bool Available { get; init; }
    public double Confidence { get; init; }
    public string SourceProfile { get; init; } = "";
    public VisionBox BoundingBoxPx { get; init; } = VisionBox.Empty;
    public bool GeometryGatePassed { get; init; }
    public IReadOnlyList<RightSkillSlotGeometry> Slots { get; init; } =
        Array.Empty<RightSkillSlotGeometry>();
    public IReadOnlyList<VisionSkillSlotPerception> VisualSlots { get; init; } =
        Array.Empty<VisionSkillSlotPerception>();
    public string ModelVersion { get; init; } = "";
    public double InferenceMilliseconds { get; init; }
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
    public string Message { get; init; } = "";

    public static RightSkillBarPerception Unavailable(long frameSequence, string message, string modelVersion = "") =>
        new()
        {
            FrameSequence = frameSequence,
            ModelVersion = modelVersion,
            Message = message
        };
}

public sealed record BuffAnchorPerception
{
    public long FrameSequence { get; init; }
    public bool Available { get; init; }
    public VisionBox AnchorBox { get; init; } = VisionBox.Empty;
    public IReadOnlyList<VisionBox> CandidateSlots { get; init; } = Array.Empty<VisionBox>();
    public int CandidateRows { get; init; }
    public int CandidateColumns { get; init; }
    public VisionTriState Mounted { get; init; } = VisionTriState.Unknown;
    public VisionTriState Fatigue { get; init; } = VisionTriState.Unknown;
    public bool PetActiveSkillBlocked { get; init; } = true;
    public double Confidence { get; init; }
    public string ModelVersion { get; init; } = "";
    public double InferenceMilliseconds { get; init; }
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
    public string Message { get; init; } = "";

    public static BuffAnchorPerception Unavailable(long frameSequence, string message, string modelVersion = "") =>
        new()
        {
            FrameSequence = frameSequence,
            ModelVersion = modelVersion,
            Message = message
        };
}

public sealed record InteractionWindowDetection
{
    public string ClassName { get; init; } = "";
    public VisionBox Box { get; init; } = VisionBox.Empty;
    public double Confidence { get; init; }
}

public sealed record InteractionWindowPerception
{
    public long FrameSequence { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Available { get; init; }
    public bool QuestLogPanelRequested { get; init; }
    public VisionTriState QuestLogPanelState { get; init; } = VisionTriState.Unknown;
    public IReadOnlyList<InteractionWindowDetection> Detections { get; init; } =
        Array.Empty<InteractionWindowDetection>();
    public string ModelVersion { get; init; } = "";
    public double InferenceMilliseconds { get; init; }
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
    public string Message { get; init; } = "";

    public static InteractionWindowPerception Unavailable(long frameSequence, string message, string modelVersion = "") =>
        new()
        {
            FrameSequence = frameSequence,
            ModelVersion = modelVersion,
            Message = message
        };
}

public sealed record InteractionControlCandidate
{
    public string ControlId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Gesture { get; init; } = "";
    public VisionBox VisualBox { get; init; } = VisionBox.Empty;
    public VisionBox SafeClickBox { get; init; } = VisionBox.Empty;
    public int? ClickX { get; init; }
    public int? ClickY { get; init; }
    public string Risk { get; init; } = "";
    public string RuntimeDefault { get; init; } = "";
    public IReadOnlyList<string> Preconditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExpectedPostconditions { get; init; } = Array.Empty<string>();
    public string FailurePolicy { get; init; } = "";
}

public sealed record InteractionGridCellCandidate
{
    public string RegionId { get; init; } = "";
    public int Row { get; init; }
    public int Column { get; init; }
    public VisionBox CellBox { get; init; } = VisionBox.Empty;
    public VisionBox SafeClickBox { get; init; } = VisionBox.Empty;
    public int? ClickX { get; init; }
    public int? ClickY { get; init; }
    public bool IsDisabled { get; init; }
}

public sealed record InteractionRegionCandidate
{
    public string RegionId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Kind { get; init; } = "";
    public VisionBox Box { get; init; } = VisionBox.Empty;
    public int Rows { get; init; }
    public int Columns { get; init; }
    public IReadOnlyList<InteractionGridCellCandidate> Cells { get; init; } =
        Array.Empty<InteractionGridCellCandidate>();
    public string FailurePolicy { get; init; } = "";
}

public sealed record InteractionPanelControlLayout
{
    public long FrameSequence { get; init; }
    public string PanelClass { get; init; } = "";
    public VisionBox PanelBox { get; init; } = VisionBox.Empty;
    public double PanelConfidence { get; init; }
    public IReadOnlyList<InteractionControlCandidate> Controls { get; init; } =
        Array.Empty<InteractionControlCandidate>();
    public IReadOnlyList<InteractionRegionCandidate> Regions { get; init; } =
        Array.Empty<InteractionRegionCandidate>();
    public bool RuntimeAuthorized { get; init; }
    public string AssetVersion { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record MinimapPerception
{
    public long ObservationSequence { get; init; }
    public string DecisionScope { get; init; } = "single_frame_observation";
    public VisionTriState FrameMarkerState { get; init; } = VisionTriState.Unknown;
    public string MapName { get; init; } = "";
    public MapNamePolicyPerception MapNamePolicy { get; init; } = new();
    public int? X { get; init; }
    public int? Y { get; init; }
    public bool? IsDungeon { get; init; }
    public VisionTriState NearbyMonsterState { get; init; } = VisionTriState.Unknown;
    public int NearbyMonsterCount { get; init; }
    public IReadOnlyList<VisionBox> MonsterMarkers { get; init; } = Array.Empty<VisionBox>();
    public int UuTargetRingCount { get; init; }
    public int NativeOrUuOrangeDotCount { get; init; }
    public int ResourceMarkerCount { get; init; }
    public IReadOnlyList<VisionBox> ResourceMarkers { get; init; } = Array.Empty<VisionBox>();
    public bool AbsenceConfirmed { get; init; }
    public int TemporalSampleCount { get; init; }
    public int TemporalSpanMilliseconds { get; init; }
    public int TargetSearchMissCount { get; init; }
    public string FusionReason { get; init; } = "";
    public VisionBox CoordinateBox { get; init; } = VisionBox.Empty;
    public VisionBox MapNameBox { get; init; } = VisionBox.Empty;
    public double Confidence { get; init; }
    public string CoordinateEngine { get; init; } = "";
    public string Message { get; init; } = "";
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;
}

public sealed record MapNamePolicyPerception
{
    public string RawText { get; init; } = "";
    public string NormalizedText { get; init; } = "";
    public string CanonicalCandidate { get; init; } = "";
    public string StableMapName { get; init; } = "";
    public string CandidateSource { get; init; } = "unknown";
    public string CandidateReason { get; init; } = "unknown";
    public string StabilityReason { get; init; } = "unstable";
    public double Confidence { get; init; }
    public bool CandidateAccepted { get; init; }
    public bool Stable { get; init; }
    public int Votes { get; init; }
    public int HistoryCount { get; init; }
    public long ObservationSequence { get; init; }
    public bool PolicyAvailable { get; init; }
    public bool RuntimeAuthorized { get; init; }
    public string AssetVersion { get; init; } = "";
    public string AssetSha256 { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record PerceptionDegradation(
    string Code,
    string Message,
    PerceptionSourceKind Source = PerceptionSourceKind.Degraded);

public sealed record RuntimePerceptionSnapshot
{
    public long FrameSequence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public double FrameAgeMilliseconds { get; init; }
    public double CaptureMilliseconds { get; init; }
    public LayoutSnapshot Layout { get; init; } =
        LayoutSnapshot.Unavailable(0, 0, "Layout detection has not run.");
    public TargetPerception Target { get; init; } = new();
    public TargetHpSlotResult? TargetHpSlot { get; init; }
    public TargetIdentityEvidence TargetIdentity { get; init; } =
        TargetIdentityEvidence.Unavailable("Target identity model has not run.");
    public SelfBarsPerception SelfBars { get; init; } = new();
    public SkillBarPerception SkillBar { get; init; } = new();
    public RightSkillBarPerception RightSkillBar { get; init; } = new();
    public BuffAnchorPerception BuffBar { get; init; } = new();
    public InteractionWindowPerception InteractionWindows { get; init; } = new();
    public SystemFeedbackPerception SystemFeedback { get; init; } =
        SystemFeedbackPerception.Unavailable(0, "System feedback detection has not run.");
    public IReadOnlyList<InteractionPanelControlLayout> InteractionControlLayouts { get; init; } =
        Array.Empty<InteractionPanelControlLayout>();
    public MinimapPerception Minimap { get; init; } = new();
    /// <summary>Latest L2 OpenText read for the target-name slot (open string, not class id).</summary>
    public OpenTextResult TargetNameOpenText { get; init; } =
        OpenTextResult.Empty(OpenTextSlot.TargetName, "OpenText has not run.");
    public IReadOnlyList<PerceptionDegradation> Degradations { get; init; } =
        Array.Empty<PerceptionDegradation>();
}

public sealed record ModelArtifactInfo(
    string Key,
    string Directory,
    string ModelPath,
    string ClassesPath,
    string ConfigPath,
    string ModelId,
    bool IsAvailable,
    string Message);

public sealed record VisionV2ArtifactSet(
    string RootDirectory,
    string SlotWorkerPath,
    string CoordinateWorkerPath,
    string FixedUiWorkerPath,
    string AuxLocatorPath,
    string PanelWorkerPath,
    string ActionPlannerPath,
    bool IsAvailable,
    string Message);

public sealed record RightSkillBarArtifactSet(
    string Directory,
    string Model640Path,
    string Model1280Path,
    string ConfigPath,
    string Sha256640,
    string Sha2561280,
    bool IsAvailable,
    string Message);

public enum VisionActionKind
{
    Noop = 0,
    Click,
    AllowFindMonsterOrAttackLoop,
    StopFindMonsterPolling,
    ContinueNavigation,
    Arrived,
    MaybeStuck,
    KeepItem,
    AllowSellItem
}

public sealed record VisionActionDecision(
    bool Ok,
    VisionActionKind Action,
    int? ClientX,
    int? ClientY,
    string Reason,
    string RawJson = "")
{
    public static VisionActionDecision Noop(string reason, string rawJson = "") =>
        new(true, VisionActionKind.Noop, null, null, reason, rawJson);
}

public sealed record VisionV2CoreSlots
{
    public SelfBarsPerception SelfBars { get; init; } = new();
    public SkillBarPerception SkillBar { get; init; } = new();
    public MinimapPerception Minimap { get; init; } = new();
    public IReadOnlyList<PerceptionDegradation> Degradations { get; init; } =
        Array.Empty<PerceptionDegradation>();
    public double InferenceMilliseconds { get; init; }
}

public sealed record AuxPanelDetection(
    string ClassName,
    VisionBox Box,
    double Confidence,
    string Template = "");

public sealed record AuxPanelSnapshot(
    bool Ok,
    IReadOnlyList<AuxPanelDetection> Detections,
    string Engine,
    string Message,
    string RawJson = "")
{
    public static AuxPanelSnapshot Unavailable(string message) =>
        new(false, Array.Empty<AuxPanelDetection>(), "", message);
}

public sealed record VisionPanelAnalysis(
    bool Ok,
    string PanelClass,
    string RequestedAction,
    string PolicyState,
    bool ButtonCandidateUsable,
    int? ClientX,
    int? ClientY,
    IReadOnlyList<string> MatchedKeywords,
    string Message,
    string RawJson = "")
{
    public bool IsConfirmedCandidate => Ok
        && PolicyState == "confirmed_candidate"
        && ButtonCandidateUsable;

    public static VisionPanelAnalysis Unavailable(string panelClass, string action, string message) =>
        new(false, panelClass, action, "unavailable", false, null, null, Array.Empty<string>(), message);
}

public enum TargetHpSlotSignal
{
    Unknown = 0,
    Alive,
    DeadByHpBottomCandidate
}

public sealed record VisionRectF(float X, float Y, float Width, float Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

public sealed record TargetHpSlotResult(
    TargetHpSlotSignal Signal,
    bool HpGroupFound,
    VisionRectF? HpGroupBox,
    float HpGroupConfidence,
    bool FillVisible,
    VisionRectF? FillBox,
    float FillConfidence,
    float? ActiveRowFillRatio,
    string ModelVersion,
    IReadOnlyList<string> DegradedReasons,
    double InferenceMilliseconds = 0)
{
    public static TargetHpSlotResult Unavailable(string reason, string modelVersion = "") =>
        new(
            TargetHpSlotSignal.Unknown,
            false,
            null,
            0,
            false,
            null,
            0,
            null,
            modelVersion,
            new[] { reason });
}

public interface IModelArtifactRegistry
{
    ModelArtifactInfo FindL1LayoutModel();
    ModelArtifactInfo FindTargetHpSlotModel();
    ModelArtifactInfo FindL2OpenTextModel();
    RightSkillBarArtifactSet FindRightSkillBarModels();
    ModelArtifactInfo FindBuffAnchorModel();
    ModelArtifactInfo FindInteractionWindowModel();
    ModelArtifactInfo FindQuestLogPanelModel();
    ModelArtifactInfo FindSystemFeedbackRegionModel();
    VisionV2ArtifactSet FindVisionV2Artifacts();
}

public interface IVisionV2SlotService
{
    bool IsAvailable { get; }
    string EngineId { get; }
    VisionV2CoreSlots AnalyzeCore(
        string windowId,
        long frameSequence,
        CaptureResult frame,
        LayoutSnapshot layout);
    IReadOnlyList<VisionSkillSlotPerception> AnalyzeSkillSurface(
        long frameSequence,
        CaptureResult frame,
        string surface,
        IReadOnlyList<RightSkillSlotGeometry> slots,
        double surfaceConfidence);
    AuxPanelSnapshot AnalyzeAuxPanels(CaptureResult frame, IReadOnlyCollection<string>? classAllowlist = null);
    VisionPanelAnalysis AnalyzePanel(
        CaptureResult panelCrop,
        VisionBox panelBox,
        string panelClass,
        string requestedAction,
        string ocrText = "",
        string itemPolicy = "unknown",
        double l1Confidence = 1.0);
}

public interface IVisionActionPlanner
{
    VisionActionDecision Plan(string mode, string requestJson);
}

public interface IVisionActionExecutor
{
    InputDispatchReceipt DispatchClick(BoundGameWindow window, VisionActionDecision decision, GlobalConfig config)
    {
        var x = decision.ClientX ?? -1;
        var y = decision.ClientY ?? -1;
        if (!TryExecuteClick(window, decision, config))
        {
            return InputDispatchReceipt.Rejected(
                config.InputDispatchMode,
                InputMouseButton.Left,
                x,
                y,
                "legacy_executor_rejected_or_yielded");
        }

        var state = config.InputDispatchMode == InputDispatchMode.PostMessage
            ? InputDispatchDeliveryState.WindowMessagePostedAwaitingConfirmation
            : InputDispatchDeliveryState.ForegroundInputInjectedAwaitingConfirmation;
        return new InputDispatchReceipt(
            state,
            config.InputDispatchMode,
            InputMouseButton.Left,
            x,
            y,
            "legacy_executor_delivery_requires_visual_confirmation");
    }

    bool TryExecuteClick(BoundGameWindow window, VisionActionDecision decision, GlobalConfig config);
}

/// <summary>
/// L2 open-vocabulary text recognizer. Reads crop → string; policy maps names to types later.
/// </summary>
public interface IOpenTextRecognizer
{
    bool IsAvailable { get; }
    string EngineId { get; }
    OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic);

    bool TryWarmup(out string message)
    {
        var available = IsAvailable;
        message = available ? $"{EngineId} is ready." : $"{EngineId} is unavailable.";
        return available;
    }
}

public interface ILayoutDetectService
{
    LayoutSnapshot Detect(CaptureResult frame);
}

public interface IRightSkillBarDetectService
{
    RightSkillBarPerception Detect(CaptureResult frame);
}

public interface IBuffAnchorDetectService
{
    BuffAnchorPerception Detect(CaptureResult frame);
}

public interface IBuffStateFusionService
{
    BuffAnchorPerception Observe(
        string windowId,
        BuffAnchorPerception anchor,
        VisionTriState mountedObservation,
        VisionTriState fatigueObservation);
    void Reset(string windowId);
}

public interface IInteractionWindowDetectService
{
    InteractionWindowPerception Detect(CaptureResult frame);

    InteractionWindowPerception Detect(CaptureResult frame, bool includeQuestLogPanel) => Detect(frame);
}

public interface IInteractionControlCatalogService
{
    bool IsAvailable { get; }
    bool RuntimeAuthorized { get; }
    string AssetVersion { get; }
    string Message { get; }
    InteractionPanelControlLayout Map(
        long frameSequence,
        InteractionWindowDetection panel);
    bool TryRenderDebugOverlay(
        CaptureResult frame,
        IReadOnlyList<InteractionPanelControlLayout> layouts,
        string outputPath,
        out string error);
}

public interface ITargetHpSlotService
{
    TargetHpSlotResult Analyze(CaptureResult targetPanelCrop);
}

public interface ITargetIdentityService
{
    TargetIdentityEvidence Analyze(CaptureResult frame, LayoutSnapshot layout, GlobalConfig config);
}

public interface ITargetJudgementService
{
    TargetPerception Judge(
        string windowId,
        long frameSequence,
        LayoutSnapshot layout,
        TargetHpSlotResult? hpSlot,
        OpenTextResult targetName,
        TargetIdentityEvidence identity,
        GlobalConfig config);
}

public interface IPerceptionHub
{
    RuntimePerceptionSnapshot Read(BoundGameWindow window, GlobalConfig config, bool includeOpenText = false);

    bool TryGetLatest(string windowId, out RuntimePerceptionSnapshot snapshot)
    {
        snapshot = null!;
        return false;
    }

    RuntimePerceptionSnapshot Read(
        BoundGameWindow window,
        GlobalConfig config,
        PerceptionReadOptions options) =>
        Read(window, config, options.IncludeTargetOpenText);
}

public sealed record PerceptionReadOptions(
    bool IncludeTargetOpenText = false,
    bool IncludeSystemFeedback = false,
    bool IncludeSystemFeedbackText = false,
    bool RequestSystemFeedbackTextAsync = false,
    bool PollSystemFeedbackTextAsync = false,
    bool IncludeQuestLogPanel = false)
{
    public static PerceptionReadOptions Fast { get; } = new();
    public static PerceptionReadOptions SystemFeedbackText { get; } = new(
        IncludeSystemFeedback: true,
        IncludeSystemFeedbackText: true);
    public static PerceptionReadOptions SystemFeedbackTextAsync { get; } = new(
        RequestSystemFeedbackTextAsync: true);
    public static PerceptionReadOptions SystemFeedbackTextAsyncPoll { get; } = new(
        PollSystemFeedbackTextAsync: true);
    public static PerceptionReadOptions QuestLogPanelShadow { get; } = new(
        IncludeQuestLogPanel: true);
}

public interface IMinimapTemporalFusionService
{
    MinimapPerception Observe(
        string windowId,
        DateTimeOffset capturedAt,
        MinimapPerception singleFrame,
        bool targetPanelVisible,
        bool? worldMonsterVisible = null);

    void ReportTargetSearchAttempt(string windowId);
    void Reset(string windowId);
}
