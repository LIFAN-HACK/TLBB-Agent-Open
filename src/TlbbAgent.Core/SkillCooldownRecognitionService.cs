using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

/// <summary>
/// Skill cooldown adapter backed by the Vision V2 skill_bar slot worker.
/// The V2 model owns bar localization and identity. Configuration selects a
/// stable SkillId; the current frame supplies the actual slot and key.
/// </summary>
public sealed class SkillCooldownRecognitionService : ISkillCooldownRecognitionService
{
    private readonly IPerceptionHub _perception;

    public SkillCooldownRecognitionService(IPerceptionHub perception)
    {
        _perception = perception;
    }

    public SkillCooldownInspection Inspect(BoundGameWindow window, SkillPreset skill, GlobalConfig config)
    {
        if (!config.SkillCooldownRecognitionEnabled)
            return Disabled("global-disabled");
        if (skill.Cooldown is not { Enabled: true })
            return Disabled("slot-disabled");
        var expectedSkillId = (skill.ExpectedVisualSkillId ?? string.Empty).Trim();
        if (expectedSkillId.Length == 0)
            return Unknown("v2-expected-skill-id-missing");

        var snapshot = _perception.Read(window, config);
        var slot = snapshot.SkillBar.VisualSlots
            .Where(item => item.FrameSequence > 0 && item.FrameSequence == snapshot.FrameSequence)
            .Where(item => item.Identity == VisionSkillIdentityState.Matched)
            .Where(item => string.Equals(item.IdentitySource, "icons_ingame", StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.SkillId, expectedSkillId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Cooldown == VisionCooldownState.Ready)
            .ThenByDescending(item => item.MatchScore)
            .FirstOrDefault();
        if (slot is null)
            return Unknown($"v2-skill-id-not-matched:{expectedSkillId}");
        if (!slot.HangUsable)
            return Disabled($"v2-skill-policy-blocked:{slot.SkillId}");

        var readiness = slot.Cooldown switch
        {
            VisionCooldownState.Ready => SkillCooldownReadiness.Ready,
            VisionCooldownState.Cooling => SkillCooldownReadiness.Cooldown,
            _ => SkillCooldownReadiness.Unknown
        };
        return new SkillCooldownInspection(
            readiness,
            $"vision-v2:{slot.Message}",
            0,
            0,
            slot.Confidence,
            slot.SlotBox.X,
            slot.SlotBox.Y,
            slot.SlotBox.Width,
            slot.SlotBox.Height);
    }

    public bool TryCaptureReadyTemplate(
        BoundGameWindow window,
        SkillPreset skill,
        GlobalConfig config,
        out SkillCooldownInspection inspection,
        out string error)
    {
        inspection = Inspect(window, skill, config);
        if (inspection.Readiness is SkillCooldownReadiness.Ready or SkillCooldownReadiness.Cooldown)
        {
            error = "";
            return true;
        }

        error = "Vision V2 does not require a user-captured ready template; the current slot state is unknown.";
        return false;
    }

    public void ClearCache()
    {
        // PerceptionHub owns frame-scoped caches; there is no per-skill template cache.
    }

    private static SkillCooldownInspection Disabled(string reason) =>
        new(SkillCooldownReadiness.Disabled, reason, 0, 0, 0, 0, 0, 0, 0);

    private static SkillCooldownInspection Unknown(string reason) =>
        new(SkillCooldownReadiness.Unknown, reason, 0, 0, 0, 0, 0, 0, 0);
}
