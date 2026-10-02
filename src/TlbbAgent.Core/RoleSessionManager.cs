using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RoleSessionManager : IRoleSessionManager, IDisposable
{
    private const int MaxRememberedCommandsPerSession = 512;
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedSessionIds = new(StringComparer.Ordinal);
    private readonly ILogService _log;
    private bool _disposed;

    public RoleSessionManager(ILogService log)
    {
        _log = log;
    }

    public event EventHandler<RoleSessionSnapshot>? SessionChanged;
    public event EventHandler<FeatureRuntimeSnapshot>? FeatureSnapshotChanged;

    public IReadOnlyList<RoleSessionSnapshot> Sessions
    {
        get
        {
            SessionState[] states;
            lock (_sessionsGate)
                states = _sessions.Values.ToArray();

            return states
                .Select(ReadSnapshot)
                .OrderBy(snapshot => snapshot.CreatedAt)
                .ToArray();
        }
    }

    public RoleSessionSnapshot OpenOrUpdate(
        string roleSessionId,
        string characterProfileId,
        BoundGameWindow window,
        long initialConfigRevision = 0)
    {
        roleSessionId = RequireId(roleSessionId, nameof(roleSessionId));
        characterProfileId = RequireId(characterProfileId, nameof(characterProfileId));
        ArgumentNullException.ThrowIfNull(window);
        if (window.Hwnd == nint.Zero)
            throw new ArgumentException("窗口句柄不能为空。", nameof(window));

        var now = DateTimeOffset.UtcNow;
        RoleSessionSnapshot snapshot;
        CancellationTokenSource? replacedLifetime = null;
        lock (_sessionsGate)
        {
            ThrowIfDisposed();

            if (_closedSessionIds.Contains(roleSessionId))
            {
                throw new RoleSessionOpenException(
                    RoleSessionOpenFailure.SessionClosed,
                    $"角色会话已关闭，不能重新打开：{roleSessionId}");
            }

            if (_sessions.TryGetValue(roleSessionId, out var state))
            {
                lock (state.Sync)
                {
                    if (state.LifecycleState == RoleSessionLifecycleState.Closed)
                    {
                        throw new RoleSessionOpenException(
                            RoleSessionOpenFailure.SessionClosed,
                            $"角色会话已关闭：{roleSessionId}");
                    }

                    if (!string.Equals(state.CharacterProfileId, characterProfileId, StringComparison.Ordinal))
                    {
                        throw new RoleSessionOpenException(
                            RoleSessionOpenFailure.CharacterProfileConflict,
                            $"角色会话 {roleSessionId} 已绑定角色档案 {state.CharacterProfileId}，不能静默更换为 {characterProfileId}。");
                    }

                    var owner = FindWindowOwner(window.Hwnd, roleSessionId);
                    if (owner is not null)
                        throw WindowAlreadyOwned(window.Hwnd, owner.RoleSessionId);

                    if (state.Window.Hwnd != window.Hwnd)
                        replacedLifetime = ResetRuntimeForWindowReplacement(state);

                    state.Window = window;
                    state.ConfigRevision = Math.Max(state.ConfigRevision, Math.Max(0, initialConfigRevision));
                    state.UpdatedAt = now;
                    snapshot = state.ToSnapshot();
                }
            }
            else
            {
                var owner = FindWindowOwner(window.Hwnd, exceptRoleSessionId: null);
                if (owner is not null)
                    throw WindowAlreadyOwned(window.Hwnd, owner.RoleSessionId);

                if (_sessions.Count >= RoleSessionLimits.MaxActiveSessions)
                {
                    throw new RoleSessionOpenException(
                        RoleSessionOpenFailure.CapacityReached,
                        $"最多只能同时管理 {RoleSessionLimits.MaxActiveSessions} 个角色会话。");
                }

                state = new SessionState(
                    roleSessionId,
                    characterProfileId,
                    window,
                    Math.Max(0, initialConfigRevision),
                    now);
                _sessions.Add(roleSessionId, state);
                snapshot = state.ToSnapshot();
            }
        }

        if (replacedLifetime is not null)
            CancelAndDisposeLifetime(replacedLifetime, roleSessionId);

        RaiseSessionChanged(snapshot);
        return snapshot;
    }

    public bool TryGetSnapshot(string roleSessionId, out RoleSessionSnapshot snapshot)
    {
        if (!TryGetState(roleSessionId, out var state))
        {
            snapshot = null!;
            return false;
        }

        snapshot = ReadSnapshot(state);
        return true;
    }

    public bool TryGetCancellationToken(string roleSessionId, out CancellationToken cancellationToken)
    {
        if (!TryGetState(roleSessionId, out var state))
        {
            cancellationToken = CancellationToken.None;
            return false;
        }

        lock (state.Sync)
        {
            if (state.LifecycleState == RoleSessionLifecycleState.Closed)
            {
                cancellationToken = CancellationToken.None;
                return false;
            }

            cancellationToken = state.Lifetime.Token;
            return true;
        }
    }

    public bool TryGetFeatureSnapshot(string roleSessionId, string featureKey, out FeatureRuntimeSnapshot snapshot)
    {
        if (!TryGetState(roleSessionId, out var state))
        {
            snapshot = null!;
            return false;
        }

        lock (state.Sync)
            return state.FeatureSnapshots.TryGetValue(featureKey, out snapshot!);
    }

    public SessionCommandResult<TResult> Execute<TPayload, TResult>(
        FeatureCommand<TPayload> command,
        bool advancesRevision,
        Func<RoleSessionCommandContext, TPayload, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        var commandId = command.CommandId?.Trim() ?? string.Empty;
        var roleSessionId = command.RoleSessionId?.Trim() ?? string.Empty;
        if (commandId.Length == 0)
            return Rejected<TResult>(0, "COMMAND_ID_REQUIRED", "命令 ID 不能为空。");

        if (roleSessionId.Length == 0 || !TryGetState(roleSessionId, out var state))
            return new SessionCommandResult<TResult>(
                SessionCommandStatus.SessionNotFound,
                false,
                0,
                default,
                "SESSION_NOT_FOUND",
                "角色会话不存在。");

        state.CommandGate.Wait();
        try
        {
            RoleSessionCommandContext context;
            lock (state.Sync)
            {
                if (state.CommandResults.TryGetValue(commandId, out var cached))
                    return Replay<TResult>(cached, state.ConfigRevision);

                if (state.LifecycleState == RoleSessionLifecycleState.Closed || state.Lifetime.IsCancellationRequested)
                {
                    var closed = Rejected<TResult>(state.ConfigRevision, "SESSION_CLOSED", "角色会话已关闭。");
                    Remember(state, commandId, closed);
                    return closed;
                }

                if (command.ExpectedRevision != state.ConfigRevision)
                {
                    var conflict = new SessionCommandResult<TResult>(
                        SessionCommandStatus.RevisionConflict,
                        false,
                        state.ConfigRevision,
                        default,
                        "REVISION_CONFLICT",
                        $"配置修订冲突，当前修订为 {state.ConfigRevision}。");
                    Remember(state, commandId, conflict);
                    return conflict;
                }

                context = new RoleSessionCommandContext(state.ToSnapshot(), state.Window, state.Lifetime.Token);
            }

            TResult value;
            try
            {
                value = handler(context, command.Payload);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                lock (state.Sync)
                {
                    var cancelled = Rejected<TResult>(state.ConfigRevision, "SESSION_CANCELLED", "角色会话已取消。");
                    Remember(state, commandId, cancelled);
                    return cancelled;
                }
            }
            catch (Exception ex)
            {
                string windowId;
                SessionCommandResult<TResult> failed;
                lock (state.Sync)
                {
                    windowId = state.Window.Id;
                    failed = new SessionCommandResult<TResult>(
                        SessionCommandStatus.Faulted,
                        false,
                        state.ConfigRevision,
                        default,
                        "COMMAND_FAILED",
                        ex.Message);
                    Remember(state, commandId, failed);
                }

                _log.Error("RoleSession", $"命令执行失败：{ex.Message}", windowId);
                return failed;
            }

            RoleSessionSnapshot changedSnapshot;
            SessionCommandResult<TResult> result;
            lock (state.Sync)
            {
                if (state.LifecycleState == RoleSessionLifecycleState.Closed || state.Lifetime.IsCancellationRequested)
                {
                    var cancelled = Rejected<TResult>(state.ConfigRevision, "SESSION_CANCELLED", "角色会话已取消。");
                    Remember(state, commandId, cancelled);
                    return cancelled;
                }

                if (advancesRevision)
                    state.ConfigRevision++;

                state.UpdatedAt = DateTimeOffset.UtcNow;
                result = new SessionCommandResult<TResult>(
                    SessionCommandStatus.Succeeded,
                    false,
                    state.ConfigRevision,
                    value);
                Remember(state, commandId, result);
                changedSnapshot = state.ToSnapshot();
            }

            RaiseSessionChanged(changedSnapshot);
            return result;
        }
        finally
        {
            state.CommandGate.Release();
        }
    }

    public bool SetLifecycleState(string roleSessionId, RoleSessionLifecycleState lifecycleState)
    {
        if (lifecycleState == RoleSessionLifecycleState.Closed
            || !TryGetState(roleSessionId, out var state))
        {
            return false;
        }

        RoleSessionSnapshot snapshot;
        lock (state.Sync)
        {
            if (state.LifecycleState == RoleSessionLifecycleState.Closed)
                return false;

            state.LifecycleState = lifecycleState;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            snapshot = state.ToSnapshot();
        }

        RaiseSessionChanged(snapshot);
        return true;
    }

    public bool PublishFeatureSnapshot(FeatureRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.FeatureKey)
            || !TryGetState(snapshot.RoleSessionId, out var state))
        {
            return false;
        }

        var normalized = snapshot with { UpdatedAt = DateTimeOffset.UtcNow };
        lock (state.Sync)
        {
            if (state.LifecycleState == RoleSessionLifecycleState.Closed)
                return false;

            state.FeatureSnapshots[normalized.FeatureKey] = normalized;
            state.UpdatedAt = normalized.UpdatedAt;
        }

        RaiseFeatureSnapshotChanged(normalized);
        return true;
    }

    public bool Close(string roleSessionId)
    {
        roleSessionId = roleSessionId?.Trim() ?? string.Empty;
        if (roleSessionId.Length == 0)
            return false;

        PreparedClosure closure;
        lock (_sessionsGate)
        {
            if (!_sessions.Remove(roleSessionId, out var state))
                return false;

            _closedSessionIds.Add(roleSessionId);
            closure = PrepareClose(state);
        }

        CancelAndDisposeLifetime(closure.Lifetime, roleSessionId);
        RaiseSessionChanged(closure.Snapshot);
        return true;
    }

    public void Dispose()
    {
        List<PreparedClosure> closures;
        lock (_sessionsGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            closures = new List<PreparedClosure>(_sessions.Count);
            foreach (var state in _sessions.Values)
            {
                _closedSessionIds.Add(state.RoleSessionId);
                closures.Add(PrepareClose(state));
            }

            _sessions.Clear();
        }

        foreach (var closure in closures)
        {
            CancelAndDisposeLifetime(closure.Lifetime, closure.Snapshot.RoleSessionId);
            RaiseSessionChanged(closure.Snapshot);
        }
    }

    private bool TryGetState(string? roleSessionId, out SessionState state)
    {
        roleSessionId = roleSessionId?.Trim();
        if (string.IsNullOrEmpty(roleSessionId))
        {
            state = null!;
            return false;
        }

        lock (_sessionsGate)
            return _sessions.TryGetValue(roleSessionId, out state!);
    }

    private SessionState? FindWindowOwner(nint hwnd, string? exceptRoleSessionId) =>
        _sessions.Values.FirstOrDefault(state =>
            state.Window.Hwnd == hwnd
            && !string.Equals(state.RoleSessionId, exceptRoleSessionId, StringComparison.Ordinal));

    private static CancellationTokenSource ResetRuntimeForWindowReplacement(SessionState state)
    {
        var replacedLifetime = state.Lifetime;
        state.Lifetime = new CancellationTokenSource();
        state.FeatureSnapshots.Clear();
        state.CommandResults.Clear();
        state.CommandOrder.Clear();
        state.LifecycleState = RoleSessionLifecycleState.Ready;
        return replacedLifetime;
    }

    private static PreparedClosure PrepareClose(SessionState state)
    {
        lock (state.Sync)
        {
            state.LifecycleState = RoleSessionLifecycleState.Closed;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            state.FeatureSnapshots.Clear();
            state.CommandResults.Clear();
            state.CommandOrder.Clear();
            return new PreparedClosure(state.ToSnapshot(), state.Lifetime);
        }
    }

    private void CancelAndDisposeLifetime(CancellationTokenSource lifetime, string roleSessionId)
    {
        try
        {
            lifetime.Cancel();
        }
        catch (Exception ex)
        {
            _log.Warn("RoleSession", $"会话取消回调异常：{ex.Message}", roleSessionId);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private static RoleSessionOpenException WindowAlreadyOwned(nint hwnd, string ownerRoleSessionId) =>
        new(
            RoleSessionOpenFailure.WindowAlreadyOwned,
            $"窗口 0x{hwnd.ToInt64():X} 已属于角色会话 {ownerRoleSessionId}。",
            ownerRoleSessionId);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RoleSessionManager));
    }

    private static RoleSessionSnapshot ReadSnapshot(SessionState state)
    {
        lock (state.Sync)
            return state.ToSnapshot();
    }

    private static string RequireId(string value, string parameterName)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length == 0
            ? throw new ArgumentException("标识不能为空。", parameterName)
            : value;
    }

    private static SessionCommandResult<TResult> Rejected<TResult>(long revision, string code, string message) =>
        new(SessionCommandStatus.Rejected, false, revision, default, code, message);

    private static SessionCommandResult<TResult> Replay<TResult>(CachedCommandResult cached, long currentRevision)
    {
        if (cached.ResultType != typeof(TResult))
        {
            return new SessionCommandResult<TResult>(
                SessionCommandStatus.Rejected,
                true,
                currentRevision,
                default,
                "COMMAND_TYPE_MISMATCH",
                "同一命令 ID 被用于不同结果类型。");
        }

        return new SessionCommandResult<TResult>(
            cached.Status,
            true,
            cached.Revision,
            cached.Value is null ? default : (TResult)cached.Value,
            cached.ErrorCode,
            cached.ErrorMessage);
    }

    private static void Remember<TResult>(SessionState state, string commandId, SessionCommandResult<TResult> result)
    {
        state.CommandResults[commandId] = new CachedCommandResult(
            typeof(TResult),
            result.Status,
            result.CurrentRevision,
            result.Value,
            result.ErrorCode,
            result.ErrorMessage);
        state.CommandOrder.Enqueue(commandId);

        while (state.CommandOrder.Count > MaxRememberedCommandsPerSession)
        {
            var oldest = state.CommandOrder.Dequeue();
            state.CommandResults.Remove(oldest);
        }
    }

    private void RaiseSessionChanged(RoleSessionSnapshot snapshot)
    {
        var handlers = SessionChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<RoleSessionSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception ex) { _log.Warn("RoleSession", $"会话订阅者异常：{ex.Message}"); }
        }
    }

    private void RaiseFeatureSnapshotChanged(FeatureRuntimeSnapshot snapshot)
    {
        var handlers = FeatureSnapshotChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<FeatureRuntimeSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception ex) { _log.Warn("RoleSession", $"运行快照订阅者异常：{ex.Message}"); }
        }
    }

    private sealed class SessionState
    {
        public SessionState(
            string roleSessionId,
            string characterProfileId,
            BoundGameWindow window,
            long configRevision,
            DateTimeOffset createdAt)
        {
            RoleSessionId = roleSessionId;
            CharacterProfileId = characterProfileId;
            Window = window;
            ConfigRevision = configRevision;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
        }

        public object Sync { get; } = new();
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public string RoleSessionId { get; }
        public string CharacterProfileId { get; }
        public BoundGameWindow Window { get; set; }
        public long ConfigRevision { get; set; }
        public RoleSessionLifecycleState LifecycleState { get; set; } = RoleSessionLifecycleState.Ready;
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public CancellationTokenSource Lifetime { get; set; } = new();
        public Dictionary<string, CachedCommandResult> CommandResults { get; } = new(StringComparer.Ordinal);
        public Queue<string> CommandOrder { get; } = new();
        public Dictionary<string, FeatureRuntimeSnapshot> FeatureSnapshots { get; } = new(StringComparer.Ordinal);

        public RoleSessionSnapshot ToSnapshot() => new(
            RoleSessionId,
            CharacterProfileId,
            Window.Hwnd.ToInt64(),
            Window.Pid,
            Window.Title,
            Window.Bitness,
            ConfigRevision,
            LifecycleState,
            CreatedAt,
            UpdatedAt);
    }

    private sealed record CachedCommandResult(
        Type ResultType,
        SessionCommandStatus Status,
        long Revision,
        object? Value,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record PreparedClosure(
        RoleSessionSnapshot Snapshot,
        CancellationTokenSource Lifetime);
}
