namespace TlbbAgent.Contracts;

public static class VisionRemoteProtocol
{
    public const string ApiVersion = "2026-07-11.v1";
}

public enum VisionExecutionLocation
{
    Local = 0,
    Remote
}

public enum VisionFrameEncoding
{
    Png = 0,
    Jpeg
}

public sealed record VisionRemoteFrameDescriptor
{
    public string ApiVersion { get; init; } = VisionRemoteProtocol.ApiVersion;
    public string RequestId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public long FrameSequence { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public VisionFrameEncoding Encoding { get; init; } = VisionFrameEncoding.Png;
    public string ImageSha256 { get; init; } = "";
}

public sealed record VisionRemoteServerTiming
{
    public double DecodeMilliseconds { get; init; }
    public double LayoutMilliseconds { get; init; }
    public double SlotMilliseconds { get; init; }
    public double TotalMilliseconds { get; init; }
}

public sealed record VisionRemoteActionProposal
{
    public VisionActionKind Action { get; init; } = VisionActionKind.Noop;
    public int? ClientX { get; init; }
    public int? ClientY { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record VisionRemoteInferenceResponse
{
    public string ApiVersion { get; init; } = VisionRemoteProtocol.ApiVersion;
    public string RequestId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public long FrameSequence { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
    public DateTimeOffset ValidUntil { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string ImageSha256 { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public LayoutSnapshot Layout { get; init; } =
        LayoutSnapshot.Unavailable(0, 0, "Remote layout has not run.");
    public VisionV2CoreSlots Core { get; init; } = new();
    public IReadOnlyList<VisionRemoteActionProposal> ActionProposals { get; init; } =
        Array.Empty<VisionRemoteActionProposal>();
    public VisionRemoteServerTiming Timing { get; init; } = new();
}

public sealed record VisionRemoteValidationResult(
    bool IsAccepted,
    string Reason,
    IReadOnlyList<VisionActionDecision> AuthorizedActions)
{
    public static VisionRemoteValidationResult Reject(string reason) =>
        new(false, reason, Array.Empty<VisionActionDecision>());
}

public interface IVisionRemoteResponseSafetyGate
{
    VisionRemoteValidationResult Validate(
        VisionRemoteFrameDescriptor request,
        VisionRemoteInferenceResponse response,
        int currentClientWidth,
        int currentClientHeight,
        string expectedModelSha256,
        DateTimeOffset now);
}
