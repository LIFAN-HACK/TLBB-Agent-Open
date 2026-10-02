using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class NullLayoutDetectService : ILayoutDetectService
{
    private readonly IModelArtifactRegistry _artifacts;

    public NullLayoutDetectService(IModelArtifactRegistry artifacts)
    {
        _artifacts = artifacts;
    }

    public LayoutSnapshot Detect(CaptureResult frame)
    {
        var artifact = _artifacts.FindL1LayoutModel();
        var message = artifact.IsAvailable
            ? "L1 layout ONNX artifact is available, but ONNX inference adapter is not enabled yet."
            : artifact.Message;

        return LayoutSnapshot.Unavailable(frame.Width, frame.Height, message) with
        {
            ModelId = artifact.ModelId
        };
    }
}
