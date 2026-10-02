namespace TlbbAgent.Contracts;

public enum RoleSessionLifecycleState
{
    Ready = 0,
    Running,
    Paused,
    Stopped,
    Faulted,
    Closed
}

public enum SessionCommandStatus
{
    Succeeded = 0,
    SessionNotFound,
    RevisionConflict,
    Rejected,
    Faulted
}

public static class RoleSessionLimits
{
    public const int MaxActiveSessions = 4;
}

public enum RoleSessionOpenFailure
{
    CapacityReached = 0,
    WindowAlreadyOwned,
    CharacterProfileConflict,
    SessionClosed
}

public sealed class RoleSessionOpenException : InvalidOperationException
{
    public RoleSessionOpenException(
        RoleSessionOpenFailure failure,
        string message,
        string? conflictingRoleSessionId = null)
        : base(message)
    {
        Failure = failure;
        ConflictingRoleSessionId = conflictingRoleSessionId;
    }

    public RoleSessionOpenFailure Failure { get; }
    public string? ConflictingRoleSessionId { get; }
}

public sealed record FeatureCommand<T>(
    string CommandId,
    string RoleSessionId,
    long ExpectedRevision,
    T Payload);

public sealed record RoleSessionSnapshot(
    string RoleSessionId,
    string CharacterProfileId,
    long WindowHandle,
    int ProcessId,
    string WindowTitle,
    ClientBitness ClientBitness,
    long ConfigRevision,
    RoleSessionLifecycleState LifecycleState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RoleSessionCommandContext(
    RoleSessionSnapshot Session,
    BoundGameWindow Window,
    CancellationToken CancellationToken);

public sealed record SessionCommandResult<T>(
    SessionCommandStatus Status,
    bool IsReplay,
    long CurrentRevision,
    T? Value,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Succeeded => Status == SessionCommandStatus.Succeeded;
}

public sealed record FeatureRuntimeSnapshot(
    string RoleSessionId,
    string FeatureKey,
    string RunState,
    string Phase,
    bool IsReady,
    string Summary,
    string? BlockingReason,
    DateTimeOffset UpdatedAt);

public interface IRoleSessionManager
{
    event EventHandler<RoleSessionSnapshot>? SessionChanged;
    event EventHandler<FeatureRuntimeSnapshot>? FeatureSnapshotChanged;

    IReadOnlyList<RoleSessionSnapshot> Sessions { get; }

    RoleSessionSnapshot OpenOrUpdate(
        string roleSessionId,
        string characterProfileId,
        BoundGameWindow window,
        long initialConfigRevision = 0);

    bool TryGetSnapshot(string roleSessionId, out RoleSessionSnapshot snapshot);
    bool TryGetCancellationToken(string roleSessionId, out CancellationToken cancellationToken);
    bool TryGetFeatureSnapshot(string roleSessionId, string featureKey, out FeatureRuntimeSnapshot snapshot);

    SessionCommandResult<TResult> Execute<TPayload, TResult>(
        FeatureCommand<TPayload> command,
        bool advancesRevision,
        Func<RoleSessionCommandContext, TPayload, TResult> handler);

    bool SetLifecycleState(string roleSessionId, RoleSessionLifecycleState state);
    bool PublishFeatureSnapshot(FeatureRuntimeSnapshot snapshot);
    bool Close(string roleSessionId);
}
