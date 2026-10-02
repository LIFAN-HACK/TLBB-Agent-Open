using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class QuestLogPanelTemporalGate
{
    private const string PanelClass = "quest_log_panel";
    private const double MinimumStableIou = 0.80;
    private static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, TrackState> _states = new(StringComparer.Ordinal);

    public InteractionWindowPerception Observe(
        string streamId,
        int frameWidth,
        int frameHeight,
        InteractionWindowPerception raw)
    {
        var withoutQuest = raw.Detections
            .Where(item => !string.Equals(item.ClassName, PanelClass, StringComparison.Ordinal))
            .ToArray();
        var quest = raw.Detections
            .Where(item => string.Equals(item.ClassName, PanelClass, StringComparison.Ordinal))
            .OrderByDescending(item => item.Confidence)
            .ToArray();

        if (raw.QuestLogPanelState == VisionTriState.Absent)
        {
            _states.TryRemove(streamId, out _);
            return raw with
            {
                Detections = withoutQuest,
                QuestLogPanelState = VisionTriState.Absent,
                Message = Append(raw.Message, "quest_temporal=absent")
            };
        }

        if (raw.QuestLogPanelState != VisionTriState.Present
            || quest.Length != 1
            || !InsideFrame(quest[0].Box, frameWidth, frameHeight))
        {
            _states.TryRemove(streamId, out _);
            return raw with
            {
                Detections = withoutQuest,
                QuestLogPanelState = VisionTriState.Unknown,
                Message = Append(raw.Message, "quest_temporal=unknown")
            };
        }

        var candidate = quest[0];
        var observedAt = raw.ObservedAt == default ? DateTimeOffset.UtcNow : raw.ObservedAt;
        var next = _states.AddOrUpdate(
            streamId,
            _ => new TrackState(candidate.Box, raw.FrameSequence, observedAt, 1),
            (_, previous) => Update(previous, candidate.Box, raw.FrameSequence, observedAt));
        if (next.StableFrames < 2)
        {
            return raw with
            {
                Detections = withoutQuest,
                QuestLogPanelState = VisionTriState.Unknown,
                Message = Append(raw.Message, "quest_temporal=warming(1/2)")
            };
        }

        return raw with
        {
            Detections = withoutQuest.Concat(new[] { candidate }).ToArray(),
            QuestLogPanelState = VisionTriState.Present,
            Message = Append(raw.Message, $"quest_temporal=stable({next.StableFrames})")
        };
    }

    public void Reset(string streamId) => _states.TryRemove(streamId, out _);

    private static TrackState Update(
        TrackState previous,
        VisionBox box,
        long frameSequence,
        DateTimeOffset observedAt)
    {
        var sameFrame = frameSequence > 0 && frameSequence == previous.FrameSequence;
        var forwardFrame = frameSequence <= 0 || previous.FrameSequence <= 0 || frameSequence > previous.FrameSequence;
        var gap = observedAt - previous.ObservedAt;
        var stableGeometry = IntersectionOverUnion(previous.Box, box) >= MinimumStableIou;
        if (sameFrame && stableGeometry)
            return previous;
        if (!forwardFrame || gap < TimeSpan.Zero || gap > MaximumObservationGap || !stableGeometry)
            return new TrackState(box, frameSequence, observedAt, 1);
        return new TrackState(box, frameSequence, observedAt, Math.Min(previous.StableFrames + 1, 3));
    }

    private static bool InsideFrame(VisionBox box, int width, int height) =>
        !box.IsEmpty
        && box.X >= 0
        && box.Y >= 0
        && box.Right <= width
        && box.Bottom <= height;

    private static double IntersectionOverUnion(VisionBox left, VisionBox right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.Right, right.Right);
        var y2 = Math.Min(left.Bottom, right.Bottom);
        var intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        if (intersection <= 0)
            return 0;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
    }

    private static string Append(string message, string detail) =>
        string.IsNullOrWhiteSpace(message) ? detail : $"{message}; {detail}";

    private sealed record TrackState(
        VisionBox Box,
        long FrameSequence,
        DateTimeOffset ObservedAt,
        int StableFrames);
}
