using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

/// <summary>
/// Explicit placeholder until the trained L2 identity model is delivered.
/// It intentionally does not fall back to legacy OCR, fixed ROIs, or template hashes.
/// </summary>
public sealed class UnavailableTargetIdentityService : ITargetIdentityService
{
    public TargetIdentityEvidence Analyze(CaptureResult frame, LayoutSnapshot layout, GlobalConfig config) =>
        TargetIdentityEvidence.Unavailable(
            "目标身份模型尚未接入；当前只提供目标框、血量插槽和开放文本结果。");
}
