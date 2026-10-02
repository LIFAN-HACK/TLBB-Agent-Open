namespace TlbbAgent.Contracts;

public enum ShoutQueueRunState
{
    Idle = 0,
    Validating,
    Running,
    Paused,
    WaitingCooldown,
    SelectingChannel,
    Sending,
    Stopped,
    NotReady,
    Error
}

public enum ShoutItemRunState
{
    Disabled = 0,
    Waiting,
    CoolingDown,
    SelectingChannel,
    Sending,
    Sent,
    Failed
}

public sealed record EmptyFeaturePayload;

public sealed record ShoutQueueStartRequest(InputDispatchMode InputDispatchMode);

public sealed record ShoutQueueCommandReply(
    bool Accepted,
    string Code,
    string Summary);

public sealed record ShoutItemRuntimeSnapshot(
    string ItemId,
    int Order,
    ShoutChannel Channel,
    ShoutItemRunState State,
    int? RemainingCooldownSeconds,
    string? ErrorCode);

public sealed record ShoutQueueRuntimeSnapshot(
    FeatureRuntimeSnapshot Feature,
    string? CurrentItemId,
    ShoutChannel? CurrentChannel,
    int RemainingWorldCooldownSeconds,
    IReadOnlyList<ShoutItemRuntimeSnapshot> Items);

public interface IShoutQueueRuntimeService
{
    SessionCommandResult<ShoutQueueCommandReply> ApplyConfig(FeatureCommand<ShoutConfig> command);
    SessionCommandResult<ShoutQueueCommandReply> Start(FeatureCommand<ShoutQueueStartRequest> command);
    SessionCommandResult<ShoutQueueCommandReply> Pause(FeatureCommand<EmptyFeaturePayload> command);
    SessionCommandResult<ShoutQueueCommandReply> Resume(FeatureCommand<EmptyFeaturePayload> command);
    SessionCommandResult<ShoutQueueCommandReply> Stop(FeatureCommand<EmptyFeaturePayload> command);
    bool TryGetSnapshot(string roleSessionId, out ShoutQueueRuntimeSnapshot snapshot);
}
