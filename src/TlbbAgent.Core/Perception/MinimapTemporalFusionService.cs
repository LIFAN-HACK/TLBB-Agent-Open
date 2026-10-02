using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class MinimapTemporalFusionService : IMinimapTemporalFusionService
{
    private const int WindowMilliseconds = 1200;
    private const int MinimumAbsentFrames = 5;
    private const int MinimumAbsentSpanMilliseconds = 800;
    private const int MinimumTargetSearchMisses = 3;

    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);

    public MinimapPerception Observe(
        string windowId,
        DateTimeOffset capturedAt,
        MinimapPerception singleFrame,
        bool targetPanelVisible,
        bool? worldMonsterVisible = null)
    {
        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        var state = _windows.GetOrAdd(key, _ => new WindowState());
        var timestamp = capturedAt == default
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : capturedAt.ToUnixTimeMilliseconds();
        var frameState = singleFrame.FrameMarkerState != VisionTriState.Unknown
            ? singleFrame.FrameMarkerState
            : singleFrame.NearbyMonsterState;

        lock (state)
        {
            if (state.LastTimestamp > 0 && timestamp < state.LastTimestamp)
            {
                state.Frames.Clear();
                state.LastObservationSequence = 0;
            }

            var observationSequence = singleFrame.ObservationSequence;
            if (observationSequence > 0
                && state.LastObservationSequence > 0
                && observationSequence < state.LastObservationSequence)
            {
                state.Frames.Clear();
                state.LastObservationSequence = 0;
            }

            var isNewObservation = observationSequence <= 0
                || observationSequence != state.LastObservationSequence;
            if (isNewObservation)
            {
                state.LastTimestamp = timestamp;
                state.LastObservationSequence = observationSequence;
                state.Frames.Enqueue(new MarkerObservation(timestamp, frameState));
            }

            var cutoff = timestamp - WindowMilliseconds;
            while (state.Frames.Count > 0 && state.Frames.Peek().TimestampMilliseconds < cutoff)
                state.Frames.Dequeue();

            if (targetPanelVisible)
                state.TargetSearchMissCount = 0;

            var observations = state.Frames.ToArray();
            var presentFrames = observations.Count(item => item.State == VisionTriState.Present);
            var absentFrames = observations.Count(item => item.State == VisionTriState.Absent);
            var span = observations.Length < 2
                ? 0
                : (int)Math.Clamp(
                    observations[^1].TimestampMilliseconds - observations[0].TimestampMilliseconds,
                    0,
                    int.MaxValue);

            var strongPositive = targetPanelVisible || worldMonsterVisible is true || presentFrames > 0;
            var temporalAbsence = observations.Length >= MinimumAbsentFrames
                && absentFrames == observations.Length
                && span >= MinimumAbsentSpanMilliseconds;
            var acquisitionAbsence = state.TargetSearchMissCount >= MinimumTargetSearchMisses;

            VisionTriState fused;
            string reason;
            if (strongPositive)
            {
                fused = VisionTriState.Present;
                reason = targetPanelVisible
                    ? "target_panel_visible"
                    : worldMonsterVisible is true
                        ? "world_monster_visible"
                        : "marker_present_within_temporal_window";
            }
            else if (temporalAbsence && acquisitionAbsence)
            {
                fused = VisionTriState.Absent;
                reason = "temporal_marker_absence_and_repeated_target_search_miss";
            }
            else
            {
                fused = VisionTriState.Unknown;
                reason = "single_or_unconfirmed_marker_absence";
            }

            return singleFrame with
            {
                DecisionScope = "temporal_fusion",
                FrameMarkerState = frameState,
                NearbyMonsterState = fused,
                AbsenceConfirmed = fused == VisionTriState.Absent,
                TemporalSampleCount = observations.Length,
                TemporalSpanMilliseconds = span,
                TargetSearchMissCount = state.TargetSearchMissCount,
                FusionReason = reason,
                Message = string.IsNullOrWhiteSpace(singleFrame.Message)
                    ? $"minimap={fused}, reason={reason}"
                    : $"{singleFrame.Message}, fused={fused}, reason={reason}"
            };
        }
    }

    public void ReportTargetSearchAttempt(string windowId)
    {
        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        var state = _windows.GetOrAdd(key, _ => new WindowState());
        lock (state)
            state.TargetSearchMissCount = Math.Min(1000, state.TargetSearchMissCount + 1);
    }

    public void Reset(string windowId)
    {
        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        _windows.TryRemove(key, out _);
    }

    private sealed record MarkerObservation(long TimestampMilliseconds, VisionTriState State);

    private sealed class WindowState
    {
        public Queue<MarkerObservation> Frames { get; } = new();
        public long LastTimestamp { get; set; }
        public long LastObservationSequence { get; set; }
        public int TargetSearchMissCount { get; set; }
    }
}
