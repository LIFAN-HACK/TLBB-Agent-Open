using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class BuffStateFusionService : IBuffStateFusionService
{
    private const int ConfirmationFrames = 2;
    private const int UnknownGraceFrames = 2;
    private readonly ConcurrentDictionary<string, WindowState> _states = new(StringComparer.Ordinal);

    public BuffAnchorPerception Observe(
        string windowId,
        BuffAnchorPerception anchor,
        VisionTriState mountedObservation,
        VisionTriState fatigueObservation)
    {
        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        if (!anchor.Available)
        {
            _states.TryRemove(key, out _);
            return anchor with
            {
                Mounted = VisionTriState.Unknown,
                Fatigue = VisionTriState.Unknown,
                PetActiveSkillBlocked = true
            };
        }

        var state = _states.GetOrAdd(key, _ => new WindowState());
        lock (state)
        {
            if (anchor.FrameSequence > 0 && state.LastFrameSequence == anchor.FrameSequence)
            {
                return anchor with
                {
                    Mounted = state.Mounted.Stable,
                    Fatigue = state.Fatigue.Stable,
                    PetActiveSkillBlocked = state.Fatigue.Stable != VisionTriState.Absent,
                    Message = $"{anchor.Message}; cached temporal state mounted={state.Mounted.Stable}; fatigue={state.Fatigue.Stable}"
                };
            }

            state.Mounted = Advance(state.Mounted, mountedObservation);
            state.Fatigue = Advance(state.Fatigue, fatigueObservation);
            state.LastFrameSequence = anchor.FrameSequence;
            return anchor with
            {
                Mounted = state.Mounted.Stable,
                Fatigue = state.Fatigue.Stable,
                PetActiveSkillBlocked = state.Fatigue.Stable != VisionTriState.Absent,
                Message = $"{anchor.Message}; mounted={state.Mounted.Stable}; fatigue={state.Fatigue.Stable}; temporal={ConfirmationFrames}"
            };
        }
    }

    public void Reset(string windowId)
    {
        if (!string.IsNullOrWhiteSpace(windowId))
            _states.TryRemove(windowId, out _);
    }

    private static StateValue Advance(StateValue current, VisionTriState observation)
    {
        if (observation == VisionTriState.Unknown)
        {
            var unknownCount = current.UnknownCount + 1;
            return unknownCount <= UnknownGraceFrames
                ? current with { UnknownCount = unknownCount, Candidate = VisionTriState.Unknown, CandidateCount = 0 }
                : new StateValue(VisionTriState.Unknown, VisionTriState.Unknown, 0, unknownCount);
        }

        var count = current.Candidate == observation ? current.CandidateCount + 1 : 1;
        var stable = count >= ConfirmationFrames ? observation : current.Stable;
        return new StateValue(stable, observation, count, 0);
    }

    private sealed class WindowState
    {
        public long LastFrameSequence { get; set; } = -1;
        public StateValue Mounted { get; set; } = new();
        public StateValue Fatigue { get; set; } = new();
    }

    private sealed record StateValue(
        VisionTriState Stable = VisionTriState.Unknown,
        VisionTriState Candidate = VisionTriState.Unknown,
        int CandidateCount = 0,
        int UnknownCount = 0);
}
