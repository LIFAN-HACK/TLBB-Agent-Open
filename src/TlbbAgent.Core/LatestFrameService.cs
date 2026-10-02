using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class LatestFrameService : ILatestFrameService, IDisposable
{
    private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromMilliseconds(750);
    private readonly IWindowCaptureService _capture;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<nint, CaptureSession> _sessions = new();
    private int _disposed;

    public LatestFrameService(IWindowCaptureService capture, ILogService log)
    {
        _capture = capture;
        _log = log;
    }

    public CaptureResult? GetLatest(nint hwnd, int initialWaitMilliseconds = 250)
    {
        if (hwnd == nint.Zero || Volatile.Read(ref _disposed) != 0)
            return null;

        var session = _sessions.GetOrAdd(hwnd, handle => new CaptureSession(handle, _capture, _log));
        var frame = session.GetLatest(Math.Clamp(initialWaitMilliseconds, 0, 2000));
        if (frame is null)
            return null;

        return DateTimeOffset.UtcNow - frame.CapturedAt <= MaximumFrameAge
            ? frame
            : null;
    }

    public CaptureResult? GetCached(nint hwnd, long frameSequence)
    {
        if (hwnd == nint.Zero || frameSequence <= 0 || Volatile.Read(ref _disposed) != 0)
            return null;
        return _sessions.TryGetValue(hwnd, out var session)
            ? session.GetCached(frameSequence)
            : null;
    }

    public void Stop(nint hwnd)
    {
        if (_sessions.TryRemove(hwnd, out var session))
            session.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var pair in _sessions.ToArray())
        {
            if (_sessions.TryRemove(pair.Key, out var session))
                session.Dispose();
        }
    }

    private sealed class CaptureSession : IDisposable
    {
        private static readonly TimeSpan ReuseWindow = TimeSpan.FromMilliseconds(33);
        private readonly nint _hwnd;
        private readonly IWindowCaptureService _capture;
        private readonly ILogService _log;
        private readonly object _captureGate = new();
        private CaptureResult? _latest;
        private long _lastErrorLogTicks;
        private int _disposed;

        public CaptureSession(nint hwnd, IWindowCaptureService capture, ILogService log)
        {
            _hwnd = hwnd;
            _capture = capture;
            _log = log;
        }

        public CaptureResult? GetLatest(int initialWaitMilliseconds)
        {
            var current = Volatile.Read(ref _latest);
            if (current is not null && DateTimeOffset.UtcNow - current.CapturedAt <= ReuseWindow)
                return current;

            lock (_captureGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return null;

                current = Volatile.Read(ref _latest);
                if (current is not null && DateTimeOffset.UtcNow - current.CapturedAt <= ReuseWindow)
                    return current;

                try
                {
                    var frame = _capture.CaptureClient(_hwnd);
                    if (frame is not null)
                        Volatile.Write(ref _latest, frame);
                }
                catch (Exception ex)
                {
                    var now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref _lastErrorLogTicks) >= 5000)
                    {
                        Interlocked.Exchange(ref _lastErrorLogTicks, now);
                        _log.Warn("Capture", $"Frame stream failed: {ex.Message}", $"0x{_hwnd.ToInt64():X}");
                    }
                }
            }

            return Volatile.Read(ref _latest);
        }

        public CaptureResult? GetCached(long frameSequence)
        {
            var current = Volatile.Read(ref _latest);
            return current?.Sequence == frameSequence ? current : null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
        }
    }
}
