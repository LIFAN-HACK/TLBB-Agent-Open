namespace TlbbAgent.Contracts;

public enum SystemFeedbackSemanticKind
{
    Unknown = 0,
    MaterialExhausted,
    MaterialBusy,
    MaterialCompleted,
    Cooldown,
    InsufficientResource,
    InvalidAction
}

public sealed record SystemFeedbackRegionDetection(
    VisionBox Box,
    double Confidence,
    string Source);

public sealed record SystemFeedbackObservation(
    long FrameSequence,
    VisionBox SourceBox,
    double RegionConfidence,
    OpenTextResult Text,
    string NormalizedText,
    SystemFeedbackSemanticKind Semantic,
    DateTimeOffset ObservedAt);

public sealed record SystemFeedbackPerception
{
    public long FrameSequence { get; init; }
    public long RequestedFrameSequence { get; init; }
    public bool DetectionRequested { get; init; }
    public bool TextRecognitionRequested { get; init; }
    public bool TextRecognitionCompleted { get; init; }
    public bool AsyncRequestInFlight { get; init; }
    public bool Available { get; init; }
    public IReadOnlyList<SystemFeedbackRegionDetection> RawRegions { get; init; } =
        Array.Empty<SystemFeedbackRegionDetection>();
    public IReadOnlyList<SystemFeedbackObservation> Observations { get; init; } =
        Array.Empty<SystemFeedbackObservation>();
    public string ModelVersion { get; init; } = "";
    public double InferenceMilliseconds { get; init; }
    public string Message { get; init; } = "";
    public PerceptionSourceKind Source { get; init; } = PerceptionSourceKind.Unavailable;

    public static SystemFeedbackPerception NotRequested(long frameSequence) =>
        new()
        {
            FrameSequence = frameSequence,
            Message = "System feedback detection was not requested for this read.",
            Source = PerceptionSourceKind.Unavailable
        };

    public static SystemFeedbackPerception Pending(long requestedFrameSequence, string message) =>
        new()
        {
            RequestedFrameSequence = requestedFrameSequence,
            DetectionRequested = true,
            TextRecognitionRequested = true,
            AsyncRequestInFlight = true,
            Message = message,
            Source = PerceptionSourceKind.Unavailable
        };

    public static SystemFeedbackPerception Unavailable(long frameSequence, string message, string modelVersion = "") =>
        new()
        {
            FrameSequence = frameSequence,
            RequestedFrameSequence = frameSequence,
            DetectionRequested = true,
            ModelVersion = modelVersion,
            Message = message,
            Source = PerceptionSourceKind.Unavailable
        };
}

public interface ISystemFeedbackRegionDetectService
{
    SystemFeedbackPerception Detect(
        CaptureResult frame,
        IOpenTextRecognizer openText,
        bool includeOpenText = true);
}

public sealed record SystemFeedbackAsyncRead(
    long RequestedFrameSequence,
    bool InFlight,
    SystemFeedbackPerception? Latest,
    string Message);

public interface ISystemFeedbackAsyncPipeline
{
    SystemFeedbackAsyncRead Request(string windowId, CaptureResult frame);
    SystemFeedbackAsyncRead Read(string windowId);
    void Reset(string windowId);
}
