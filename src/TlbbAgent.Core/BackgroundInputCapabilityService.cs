using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class BackgroundInputCapabilityService : IBackgroundInputCapabilityService
{
    private const int UnsupportedAfterConsecutiveFailures = 2;
    private readonly ConcurrentDictionary<CapabilityKey, CapabilityEntry> _entries = new();

    public BackgroundInputCapabilityStatus GetStatus(nint hwnd, BackgroundInputKind kind)
    {
        var key = new CapabilityKey(hwnd, kind);
        return _entries.TryGetValue(key, out var entry)
            ? entry.ToStatus(key)
            : Unknown(key);
    }

    public bool CanDispatch(nint hwnd, BackgroundInputKind kind, out string reason)
    {
        var status = GetStatus(hwnd, kind);
        reason = status.Reason;
        return status.CanDispatch;
    }

    public BackgroundInputCapabilityStatus RecordConfirmed(
        nint hwnd,
        BackgroundInputKind kind,
        string reason)
    {
        var key = new CapabilityKey(hwnd, kind);
        var now = DateTimeOffset.UtcNow;
        var entry = _entries.AddOrUpdate(
            key,
            _ => new CapabilityEntry(BackgroundInputCapabilityState.Available, 0, NormalizeReason(reason), now),
            (_, _) => new CapabilityEntry(BackgroundInputCapabilityState.Available, 0, NormalizeReason(reason), now));
        return entry.ToStatus(key);
    }

    public BackgroundInputCapabilityStatus RecordUnconfirmed(
        nint hwnd,
        BackgroundInputKind kind,
        string reason)
    {
        var key = new CapabilityKey(hwnd, kind);
        var now = DateTimeOffset.UtcNow;
        var normalizedReason = NormalizeReason(reason);
        var entry = _entries.AddOrUpdate(
            key,
            _ => CreateFailure(1, normalizedReason, now),
            (_, current) => CreateFailure(current.ConsecutiveUnconfirmedActions + 1, normalizedReason, now));
        return entry.ToStatus(key);
    }

    public void ResetWindow(nint hwnd)
    {
        foreach (var key in _entries.Keys.Where(key => key.Hwnd == hwnd))
            _entries.TryRemove(key, out _);
    }

    private static CapabilityEntry CreateFailure(int count, string reason, DateTimeOffset now) =>
        new(
            count >= UnsupportedAfterConsecutiveFailures
                ? BackgroundInputCapabilityState.Unsupported
                : BackgroundInputCapabilityState.Unknown,
            count,
            count >= UnsupportedAfterConsecutiveFailures
                ? $"background_input_unsupported:{reason}"
                : $"background_input_unconfirmed:{reason}",
            now);

    private static BackgroundInputCapabilityStatus Unknown(CapabilityKey key) =>
        new(
            key.Hwnd,
            key.Kind,
            BackgroundInputCapabilityState.Unknown,
            0,
            "not_yet_verified",
            DateTimeOffset.MinValue);

    private static string NormalizeReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();

    private readonly record struct CapabilityKey(nint Hwnd, BackgroundInputKind Kind);

    private sealed record CapabilityEntry(
        BackgroundInputCapabilityState State,
        int ConsecutiveUnconfirmedActions,
        string Reason,
        DateTimeOffset UpdatedAt)
    {
        public BackgroundInputCapabilityStatus ToStatus(CapabilityKey key) =>
            new(key.Hwnd, key.Kind, State, ConsecutiveUnconfirmedActions, Reason, UpdatedAt);
    }
}
