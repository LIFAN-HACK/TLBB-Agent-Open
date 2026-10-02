namespace TlbbAgent.Contracts;

public enum InteractionReadOnlyPlanStatus
{
    Unavailable = 0,
    PanelNotFound,
    CaptureRequired,
    InvalidGeometry,
    ObservationOnly
}

public sealed record InteractionReadOnlyActionCandidate
{
    public string WindowId { get; init; } = "";
    public long FrameSequence { get; init; }
    public string PanelClass { get; init; } = "";
    public VisionBox PanelBoxPx { get; init; } = VisionBox.Empty;
    public string ControlId { get; init; } = "";
    public string SlotId { get; init; } = "";
    public VisionBox SafeClickBoxPx { get; init; } = VisionBox.Empty;
    public int? ClickPointX { get; init; }
    public int? ClickPointY { get; init; }
    public string Key { get; init; } = "";
    public double IdentityConfidence { get; init; }
    public string PolicyDecision { get; init; } = "blocked_observation_only";
    public IReadOnlyList<string> ExpectedPostconditions { get; init; } = Array.Empty<string>();
    public int TimeoutMilliseconds { get; init; } = 1500;
    public int RetryLimit { get; init; } = 1;
    public bool RuntimeAuthorized { get; init; }
}

public sealed record InteractionReadOnlyPlan
{
    public bool Available { get; init; }
    public InteractionReadOnlyPlanStatus Status { get; init; }
    public string WindowId { get; init; } = "";
    public long FrameSequence { get; init; }
    public string PanelClass { get; init; } = "";
    public VisionBox PanelBoxPx { get; init; } = VisionBox.Empty;
    public double PanelConfidence { get; init; }
    public IReadOnlyList<InteractionReadOnlyActionCandidate> Actions { get; init; } =
        Array.Empty<InteractionReadOnlyActionCandidate>();
    public IReadOnlyList<InteractionRegionCandidate> Regions { get; init; } =
        Array.Empty<InteractionRegionCandidate>();
    public string FeatureFlag { get; init; } = "";
    public bool FeatureFlagEnabled { get; init; }
    public bool RuntimeAuthorized { get; init; }
    public string AssetVersion { get; init; } = "";
    public string Message { get; init; } = "";
}

public interface IInteractionReadOnlyPlanService
{
    string FeatureFlag { get; }
    bool RuntimeActionsEnabled { get; }
    InteractionReadOnlyPlan Build(
        string windowId,
        CaptureResult frame,
        string panelClass);
}
