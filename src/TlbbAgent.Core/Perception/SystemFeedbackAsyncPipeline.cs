using System.Collections.Concurrent;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class SystemFeedbackAsyncPipeline : ISystemFeedbackAsyncPipeline, IDisposable
{
    private static readonly TimeSpan IdleRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StaleFrameWindow = TimeSpan.FromSeconds(2);
    private readonly ISystemFeedbackRegionDetectService _detector;
    private readonly IOpenTextRecognizer _openText;
    private readonly ILogService _log;
    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, WindowState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private int _requestCount;
    private int _disposed;

    public SystemFeedbackAsyncPipeline(
        ISystemFeedbackRegionDetectService detector,
        IOpenTextRecognizer openText,
        ILogService log)
    {
        _detector = detector;
        _openText = openText;
        _log = log;
    }

    public SystemFeedbackAsyncRead Request(string windowId, CaptureResult frame)
    {
        if (string.IsNullOrWhiteSpace(windowId) || frame.Sequence <= 0 || Volatile.Read(ref _disposed) != 0)
        {
            return new SystemFeedbackAsyncRead(
                frame.Sequence,
                false,
                null,
                "System feedback async request was rejected.");
        }

        var state = _states.GetOrAdd(windowId, static _ => new WindowState());
        var startWorker = false;
        SystemFeedbackAsyncRead read;
        lock (state.Gate)
        {
            state.LastTouched = DateTimeOffset.UtcNow;
            if (frame.Sequence > state.LastRequestedFrameSequence)
            {
                state.LastRequestedFrameSequence = frame.Sequence;
                state.PendingFrame = frame;
            }

            if (!state.Running && state.PendingFrame is not null)
            {
                state.Running = true;
                startWorker = true;
            }

            read = Snapshot(state, "System feedback OCR queued without blocking the caller.");
        }

        if (startWorker)
        {
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    var worker = Task.Run(() => RunWindowAsync(windowId, state));
                    _workers[windowId] = worker;
                    _ = worker.ContinueWith(
                        completed => RemoveCompletedWorker(windowId, completed),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    lock (state.Gate)
                    {
                        state.PendingFrame = null;
                        state.Running = false;
                        read = Snapshot(state, "System feedback async pipeline is shutting down.");
                    }
                }
            }
        }

        if (Interlocked.Increment(ref _requestCount) % 64 == 0)
            PruneIdleStates();

        return read;
    }

    public SystemFeedbackAsyncRead Read(string windowId)
    {
        if (!_states.TryGetValue(windowId, out var state))
            return new SystemFeedbackAsyncRead(0, false, null, "No system feedback request exists for this window.");

        lock (state.Gate)
        {
            state.LastTouched = DateTimeOffset.UtcNow;
            return Snapshot(state, "System feedback async state read.");
        }
    }

    public void Reset(string windowId)
    {
        if (!string.IsNullOrWhiteSpace(windowId))
            _states.TryRemove(windowId, out _);
    }

    public void Dispose()
    {
        Task[] workers;
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _shutdown.Cancel();
            workers = _workers.Values.Distinct().ToArray();
        }

        var stopped = workers.Length == 0 || Task.WaitAll(workers, TimeSpan.FromSeconds(7));
        if (!stopped)
            _log.Warn("Vision", "System feedback async workers did not stop within 7 seconds.");
        else
            _shutdown.Dispose();
    }

    private async Task RunWindowAsync(string windowId, WindowState state)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                CaptureResult? frame;
                lock (state.Gate)
                {
                    frame = state.PendingFrame;
                    state.PendingFrame = null;
                    if (frame is null)
                    {
                        state.Running = false;
                        return;
                    }
                }

                await _inferenceGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    if (ShouldSkipStaleFrame(state, frame))
                        continue;

                    var result = _detector.Detect(frame, _openText, includeOpenText: true);
                    lock (state.Gate)
                    {
                        if (state.Latest is null || result.FrameSequence >= state.Latest.FrameSequence)
                            state.Latest = result;
                        state.LastTouched = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    lock (state.Gate)
                    {
                        state.Latest = SystemFeedbackPerception.Unavailable(
                            frame.Sequence,
                            $"Asynchronous system feedback failed: {ex.Message}") with
                        {
                            TextRecognitionRequested = true
                        };
                        state.LastTouched = DateTimeOffset.UtcNow;
                    }
                    _log.Warn("Vision", $"Asynchronous system feedback failed for {windowId}: {ex.Message}");
                }
                finally
                {
                    _inferenceGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Application shutdown.
        }
        finally
        {
            lock (state.Gate)
                state.Running = false;
        }
    }

    private static bool ShouldSkipStaleFrame(WindowState state, CaptureResult frame)
    {
        if (DateTimeOffset.UtcNow - frame.CapturedAt <= StaleFrameWindow)
            return false;

        lock (state.Gate)
            return state.PendingFrame is { Sequence: > 0 } pending && pending.Sequence > frame.Sequence;
    }

    private static SystemFeedbackAsyncRead Snapshot(WindowState state, string message) =>
        new(
            state.LastRequestedFrameSequence,
            state.Running || state.PendingFrame is not null,
            state.Latest,
            message);

    private void PruneIdleStates()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleRetention;
        foreach (var pair in _states)
        {
            var remove = false;
            lock (pair.Value.Gate)
                remove = !pair.Value.Running && pair.Value.PendingFrame is null && pair.Value.LastTouched < cutoff;
            if (remove)
                _states.TryRemove(pair.Key, out _);
        }
    }

    private void RemoveCompletedWorker(string windowId, Task completed)
    {
        if (_workers.TryGetValue(windowId, out var current) && ReferenceEquals(current, completed))
            _workers.TryRemove(windowId, out _);
    }

    private sealed class WindowState
    {
        public object Gate { get; } = new();
        public CaptureResult? PendingFrame { get; set; }
        public SystemFeedbackPerception? Latest { get; set; }
        public long LastRequestedFrameSequence { get; set; }
        public bool Running { get; set; }
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }
}
