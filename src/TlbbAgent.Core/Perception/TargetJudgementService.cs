using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class TargetJudgementService : ITargetJudgementService
{
    private const int DeadConfirmFrames = 2;
    private const float ImmediateDeadGroupConfidence = 0.90f;
    private const double MinTrustedTargetNameConfidence = 0.45;
    private readonly ConcurrentDictionary<string, TargetHistory> _history = new(StringComparer.Ordinal);

    public TargetPerception Judge(
        string windowId,
        long frameSequence,
        LayoutSnapshot layout,
        TargetHpSlotResult? hpSlot,
        OpenTextResult targetName,
        TargetIdentityEvidence identity,
        GlobalConfig config)
    {
        var history = _history.GetOrAdd(windowId, _ => new TargetHistory());
        if (layout.Source != PerceptionSourceKind.OnnxLayout)
        {
            ResetDeadConfirmation(history, frameSequence);
            return new TargetPerception
            {
                Presence = TargetPresence.Unknown,
                Identity = TargetIdentity.Unknown,
                Life = TargetLifeState.Unknown,
                ShouldAcquireTarget = true,
                IsActionableTarget = false,
                Source = PerceptionSourceKind.Unavailable,
                Message = string.IsNullOrWhiteSpace(layout.Message)
                    ? "L1 布局模型不可用。"
                    : layout.Message
            };
        }

        var panel = layout.Best(LayoutClass.TargetPanel);
        if (panel is null)
            return BuildNoTargetResult(history, frameSequence);

        var life = ResolveSlotLife(history, frameSequence, hpSlot);
        var hpPercent = ResolveHpPercent(life, hpSlot);
        var name = targetName.HasText && targetName.Confidence >= MinTrustedTargetNameConfidence
            ? targetName.Text.Trim()
            : string.Empty;
        var hasLiveTarget = life is TargetLifeState.Live or TargetLifeState.Weak;
        var shouldSwitch = life == TargetLifeState.DeadByHpBottom;
        var isProtectedIdentity = identity.Identity is TargetIdentity.Player or TargetIdentity.Pet or TargetIdentity.Npc;

        lock (history)
            history.PanelPresent = true;

        return new TargetPerception
        {
            Presence = TargetPresence.Present,
            Identity = identity.Identity,
            Life = life,
            HpPercent = hpPercent,
            NameText = name,
            ShouldAcquireTarget = shouldSwitch,
            ShouldSwitchTarget = shouldSwitch,
            IsProtectedTarget = isProtectedIdentity,
            IsActionableTarget = hasLiveTarget && !isProtectedIdentity,
            HostilityEvidenceAvailable = identity.HostilityEvidenceAvailable,
            IsHostileRedName = identity.IsHostileRedName,
            AggressionEvidenceAvailable = identity.AggressionEvidenceAvailable,
            RecentlyDamagedPlayer = identity.RecentlyDamagedPlayer,
            Confidence = ResolveConfidence(layout, hpSlot, targetName, identity),
            Source = ResolveSource(targetName, identity),
            PanelBox = panel.Box,
            HpBarBox = layout.Best(LayoutClass.TargetHpBar)?.Box ?? VisionBox.Empty,
            NameBox = layout.Best(LayoutClass.TargetName)?.Box ?? VisionBox.Empty,
            Message = BuildMessage(hpSlot, life, identity)
        };
    }

    private static TargetPerception BuildNoTargetResult(TargetHistory history, long frameSequence)
    {
        bool wasPresent;
        lock (history)
        {
            wasPresent = history.PanelPresent;
            history.PanelPresent = false;
            history.DeadCandidateFrames = 0;
            history.LastHpFrameSequence = frameSequence;
            history.HasConfirmedLive = false;
        }

        return new TargetPerception
        {
            Presence = TargetPresence.None,
            Identity = TargetIdentity.Unknown,
            Life = wasPresent ? TargetLifeState.Gone : TargetLifeState.NoTarget,
            ShouldAcquireTarget = true,
            ShouldSwitchTarget = wasPresent,
            IsActionableTarget = false,
            Confidence = wasPresent ? 0.8 : 1.0,
            Source = PerceptionSourceKind.OnnxLayout,
            Message = wasPresent ? "目标面板已消失。" : "当前没有目标面板。"
        };
    }

    private static TargetLifeState ResolveSlotLife(
        TargetHistory history,
        long frameSequence,
        TargetHpSlotResult? hpSlot)
    {
        lock (history)
        {
            var isNewFrame = frameSequence <= 0 || history.LastHpFrameSequence != frameSequence;
            if (!isNewFrame)
                return ResolveRepeatedFrameLife(history, hpSlot);

            history.LastHpFrameSequence = frameSequence;
            if (hpSlot is null || hpSlot.Signal == TargetHpSlotSignal.Unknown)
            {
                history.DeadCandidateFrames = 0;
                return TargetLifeState.Ambiguous;
            }

            if (hpSlot.Signal == TargetHpSlotSignal.Alive)
            {
                history.DeadCandidateFrames = 0;
                history.HasConfirmedLive = true;
                return hpSlot.ActiveRowFillRatio is <= 0.25f
                    ? TargetLifeState.Weak
                    : TargetLifeState.Live;
            }

            history.DeadCandidateFrames++;
            if (history.HasConfirmedLive)
            {
                history.DeadCandidateFrames = DeadConfirmFrames;
                history.HasConfirmedLive = false;
                return TargetLifeState.DeadByHpBottom;
            }

            if (hpSlot.HpGroupFound && hpSlot.HpGroupConfidence >= ImmediateDeadGroupConfidence)
            {
                history.DeadCandidateFrames = DeadConfirmFrames;
                history.HasConfirmedLive = false;
                return TargetLifeState.DeadByHpBottom;
            }

            if (hpSlot.HpGroupFound
                && hpSlot.HpGroupConfidence >= 0.55f
                && history.DeadCandidateFrames >= DeadConfirmFrames)
            {
                return TargetLifeState.DeadByHpBottom;
            }

            return history.DeadCandidateFrames >= DeadConfirmFrames
                ? TargetLifeState.DeadByHpBottom
                : TargetLifeState.Ambiguous;
        }
    }

    private static TargetLifeState ResolveRepeatedFrameLife(TargetHistory history, TargetHpSlotResult? hpSlot)
    {
        if (hpSlot?.Signal == TargetHpSlotSignal.Alive)
        {
            return hpSlot.ActiveRowFillRatio is <= 0.25f
                ? TargetLifeState.Weak
                : TargetLifeState.Live;
        }

        return history.DeadCandidateFrames >= DeadConfirmFrames
            ? TargetLifeState.DeadByHpBottom
            : TargetLifeState.Ambiguous;
    }

    private static int? ResolveHpPercent(TargetLifeState life, TargetHpSlotResult? hpSlot) => life switch
    {
        TargetLifeState.DeadByHpBottom => 0,
        TargetLifeState.Live or TargetLifeState.Weak when hpSlot?.ActiveRowFillRatio is { } ratio =>
            Math.Clamp((int)Math.Round(ratio * 100), 1, 100),
        _ => null
    };

    private static double ResolveConfidence(
        LayoutSnapshot layout,
        TargetHpSlotResult? hpSlot,
        OpenTextResult targetName,
        TargetIdentityEvidence identity)
    {
        var values = new[]
        {
            layout.Best(LayoutClass.TargetPanel)?.Confidence ?? 0,
            hpSlot?.HpGroupConfidence ?? 0,
            hpSlot?.FillConfidence ?? 0,
            targetName.Confidence,
            identity.Confidence
        };
        return Math.Clamp(values.Max(), 0, 1);
    }

    private static PerceptionSourceKind ResolveSource(OpenTextResult targetName, TargetIdentityEvidence identity) =>
        targetName.HasText || identity.IsAvailable
            ? PerceptionSourceKind.Fused
            : PerceptionSourceKind.OnnxLayout;

    private static string BuildMessage(
        TargetHpSlotResult? hpSlot,
        TargetLifeState life,
        TargetIdentityEvidence identity)
    {
        var hpMessage = hpSlot is null
            ? "血量插槽模型未运行"
            : $"group={hpSlot.HpGroupConfidence:0.000}, fill={hpSlot.FillConfidence:0.000}";
        var identityMessage = identity.IsAvailable
            ? $"identity={identity.Identity}"
            : "identity=unavailable";
        return $"life={life}, {hpMessage}, {identityMessage}";
    }

    private static void ResetDeadConfirmation(TargetHistory history, long frameSequence)
    {
        lock (history)
        {
            history.PanelPresent = false;
            history.DeadCandidateFrames = 0;
            history.LastHpFrameSequence = frameSequence;
            history.HasConfirmedLive = false;
        }
    }

    private sealed class TargetHistory
    {
        public bool PanelPresent { get; set; }
        public int DeadCandidateFrames { get; set; }
        public long LastHpFrameSequence { get; set; } = -1;
        public bool HasConfirmedLive { get; set; }
    }
}
