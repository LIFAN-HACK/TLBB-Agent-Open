using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

/// <summary>
/// Adapts the reviewed target-protection template index into the production
/// perception pipeline. A match below the configured threshold remains unknown.
/// </summary>
public sealed class TemplateTargetIdentityService : ITargetIdentityService
{
    private const string ModelId = "target-protection-template-v1";
    private readonly ITargetProtectionSampleService _samples;
    private readonly IResolutionProfileService _resolutionProfiles;

    public TemplateTargetIdentityService(
        ITargetProtectionSampleService samples,
        IResolutionProfileService resolutionProfiles)
    {
        _samples = samples;
        _resolutionProfiles = resolutionProfiles;
    }

    public TargetIdentityEvidence Analyze(
        CaptureResult frame,
        LayoutSnapshot layout,
        GlobalConfig config)
    {
        if (layout.Source != PerceptionSourceKind.OnnxLayout
            || layout.Best(LayoutClass.TargetPanel) is null)
        {
            return TargetIdentityEvidence.Unavailable("当前帧没有可靠目标面板。", ModelId);
        }

        var recognition = _resolutionProfiles.ResolveCombat(
            frame.Width,
            frame.Height,
            config.CombatRecognition);
        var match = _samples.Match(frame, recognition, config.Targeting);
        if (match is null)
            return TargetIdentityEvidence.Unavailable("目标身份模板未达到匹配阈值。", ModelId);

        var identity = match.Kind.Trim().ToLowerInvariant() switch
        {
            "player" => TargetIdentity.Player,
            "monster" => TargetIdentity.Monster,
            "boss" => TargetIdentity.Boss,
            "elite" => TargetIdentity.Elite,
            "pet" => TargetIdentity.Pet,
            _ => TargetIdentity.Unknown
        };

        if (identity == TargetIdentity.Unknown)
            return TargetIdentityEvidence.Unavailable("目标身份模板结果不可用于分类。", ModelId);

        return new TargetIdentityEvidence(
            identity,
            Math.Clamp(match.Score, 0, 1),
            PerceptionSourceKind.Fused,
            ModelId,
            $"模板命中：kind={match.Kind}, score={match.Score:0.000}, sample={match.SampleId}");
    }
}
