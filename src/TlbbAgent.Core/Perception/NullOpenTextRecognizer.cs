using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

/// <summary>Placeholder when L2 OpenText is disabled.</summary>
public sealed class NullOpenTextRecognizer : IOpenTextRecognizer
{
    public bool IsAvailable => false;
    public string EngineId => "null";

    public OpenTextResult Recognize(CaptureResult crop, OpenTextSlot slot = OpenTextSlot.Generic) =>
        OpenTextResult.Empty(slot, "OpenText disabled.", EngineId);
}
