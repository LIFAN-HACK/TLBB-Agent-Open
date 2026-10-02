using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ShoutQueueRuntimeService : IShoutQueueRuntimeService, IDisposable
{
    private const string FeatureKey = FeatureKeys.LifeShout;
    private const int PersistedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IRoleSessionManager _sessions;
    private readonly IProfileService _profiles;
    private readonly IInputService _input;
    private readonly IShoutChannelSelector _channelSelector;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<string, RuntimeState> _states = new(StringComparer.Ordinal);
    private bool _disposed;

    public ShoutQueueRuntimeService(
        IRoleSessionManager sessions,
        IProfileService profiles,
        IInputService input,
        IShoutChannelSelector channelSelector,
        ILogService log)
    {
        _sessions = sessions;
        _profiles = profiles;
        _input = input;
        _channelSelector = channelSelector;
        _log = log;
        _sessions.SessionChanged += OnSessionChanged;
    }

    public SessionCommandResult<ShoutQueueCommandReply> ApplyConfig(FeatureCommand<ShoutConfig> command) =>
        _sessions.Execute(command, advancesRevision: true, (context, payload) =>
        {
            var state = GetOrCreate(context);
            var config = CloneAndNormalize(payload);
            lock (state.Sync)
            {
                state.Config = config;
                state.LastErrorCode = null;
                state.LastErrorMessage = null;
            }

            Persist(state);
            Publish(state);
            return Reply(true, "CONFIG_APPLIED", "喊话配置已更新。");
        });

    public SessionCommandResult<ShoutQueueCommandReply> Start(FeatureCommand<ShoutQueueStartRequest> command) =>
        _sessions.Execute(command, advancesRevision: false, (context, payload) =>
        {
            var state = GetOrCreate(context);
            lock (state.Sync)
            {
                if (state.RunState is ShoutQueueRunState.Running
                    or ShoutQueueRunState.WaitingCooldown
                    or ShoutQueueRunState.SelectingChannel
                    or ShoutQueueRunState.Sending)
                {
                    return Reply(true, "ALREADY_RUNNING", "喊话队列已在运行。");
                }

                var validation = Validate(state.Config);
                if (validation is not null)
                {
                    state.RunState = ShoutQueueRunState.NotReady;
                    state.LastErrorCode = validation.Value.Code;
                    state.LastErrorMessage = validation.Value.Message;
                    PublishLocked(state);
                    return Reply(false, validation.Value.Code, validation.Value.Message);
                }

                state.InputDispatchMode = payload.InputDispatchMode;
                state.RunState = ShoutQueueRunState.Running;
                state.LastErrorCode = null;
                state.LastErrorMessage = null;
                state.RunnerCancellation?.Cancel();
                state.RunnerCancellation?.Dispose();
                state.RunnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                state.Runner = Task.Run(() => RunAsync(state, state.RunnerCancellation.Token));
                PublishLocked(state);
                return Reply(true, "STARTED", "喊话队列已启动。");
            }
        });

    public SessionCommandResult<ShoutQueueCommandReply> Pause(FeatureCommand<EmptyFeaturePayload> command) =>
        _sessions.Execute(command, advancesRevision: false, (context, _) =>
        {
            var state = GetOrCreate(context);
            lock (state.Sync)
            {
                if (state.RunState == ShoutQueueRunState.Paused)
                    return Reply(true, "ALREADY_PAUSED", "喊话队列已暂停。");

                if (!IsActive(state.RunState))
                    return Reply(false, "NOT_RUNNING", "喊话队列未运行。");

                state.RunState = ShoutQueueRunState.Paused;
                PublishLocked(state);
                return Reply(true, "PAUSED", "喊话队列已暂停。");
            }
        });

    public SessionCommandResult<ShoutQueueCommandReply> Resume(FeatureCommand<EmptyFeaturePayload> command) =>
        _sessions.Execute(command, advancesRevision: false, (context, _) =>
        {
            var state = GetOrCreate(context);
            lock (state.Sync)
            {
                if (state.RunState != ShoutQueueRunState.Paused)
                    return Reply(false, "NOT_PAUSED", "喊话队列未暂停。");

                state.RunState = ShoutQueueRunState.Running;
                PublishLocked(state);
                return Reply(true, "RESUMED", "喊话队列已恢复。");
            }
        });

    public SessionCommandResult<ShoutQueueCommandReply> Stop(FeatureCommand<EmptyFeaturePayload> command) =>
        _sessions.Execute(command, advancesRevision: false, (context, _) =>
        {
            var state = GetOrCreate(context);
            StopRuntime(state, ShoutQueueRunState.Stopped, null, null);
            return Reply(true, "STOPPED", "喊话队列已停止。");
        });

    public bool TryGetSnapshot(string roleSessionId, out ShoutQueueRuntimeSnapshot snapshot)
    {
        if (!_states.TryGetValue(roleSessionId, out var state))
        {
            snapshot = null!;
            return false;
        }

        lock (state.Sync)
        {
            snapshot = BuildSnapshot(state);
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sessions.SessionChanged -= OnSessionChanged;
        foreach (var state in _states.Values)
            StopRuntime(state, ShoutQueueRunState.Stopped, null, null, publish: false);
        _states.Clear();
    }

    private async Task RunAsync(RuntimeState state, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                ShoutMessage? item;
                ShoutConfig config;
                InputDispatchMode dispatchMode;

                lock (state.Sync)
                {
                    if (state.RunState == ShoutQueueRunState.Paused)
                    {
                        item = null;
                        config = state.Config;
                        dispatchMode = state.InputDispatchMode;
                    }
                    else
                    {
                        config = state.Config;
                        dispatchMode = state.InputDispatchMode;
                        item = SelectNextItem(state, config);
                    }
                }

                if (IsPaused(state))
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                if (item is null)
                {
                    StopRuntime(state, ShoutQueueRunState.NotReady, "EMPTY_QUEUE", "没有可发送的喊话内容。");
                    return;
                }

                if (item.Channel == ShoutChannel.World)
                {
                    var remaining = RemainingWorldCooldown(state);
                    if (remaining > TimeSpan.Zero)
                    {
                        lock (state.Sync)
                        {
                            state.CurrentItemId = item.Id;
                            state.CurrentChannel = item.Channel;
                            state.RunState = ShoutQueueRunState.WaitingCooldown;
                            PublishLocked(state);
                        }

                        await Task.Delay((int)Math.Min(1000, Math.Max(1, remaining.TotalMilliseconds)), token)
                            .ConfigureAwait(false);
                        continue;
                    }
                }

                await WaitUntilRunnableAsync(state, token).ConfigureAwait(false);
                lock (state.Sync)
                {
                    state.CurrentItemId = item.Id;
                    state.CurrentChannel = item.Channel;
                    state.RunState = ShoutQueueRunState.SelectingChannel;
                    PublishLocked(state);
                }

                if (!_channelSelector.TrySelect(state.Window, item.Channel, dispatchMode))
                {
                    StopRuntime(state, ShoutQueueRunState.NotReady, "CHANNEL_NOT_READY", _channelSelector.Status);
                    return;
                }

                await WaitUntilRunnableAsync(state, token).ConfigureAwait(false);
                lock (state.Sync)
                {
                    state.RunState = ShoutQueueRunState.Sending;
                    PublishLocked(state);
                }

                var opened = _input.SendKey(state.Window.Hwnd, config.OpenChatHotkey, dispatchMode);
                if (!opened)
                {
                    MarkItemFailure(state, "OPEN_CHAT_FAILED", "打开聊天失败。");
                    await Task.Delay(RetryDelay(config), token).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(config.MessageDelayMs, token).ConfigureAwait(false);
                await WaitUntilRunnableAsync(state, token).ConfigureAwait(false);
                var typed = _input.SendText(state.Window.Hwnd, NormalizeMessage(item.Text), dispatchMode);
                if (!typed)
                {
                    MarkItemFailure(state, "INPUT_TEXT_FAILED", "输入喊话内容失败。");
                    await Task.Delay(RetryDelay(config), token).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(config.CommitDelayMs, token).ConfigureAwait(false);
                await WaitUntilRunnableAsync(state, token).ConfigureAwait(false);
                var submitted = _input.SendKey(state.Window.Hwnd, config.CommitHotkey, dispatchMode);

                lock (state.Sync)
                {
                    state.LastCommittedItemId = item.Id;
                    state.LastCommittedOrder = item.Order;
                    state.LastItemState = submitted ? ShoutItemRunState.Sent : ShoutItemRunState.Failed;
                    state.LastErrorCode = submitted ? null : "SUBMIT_UNCONFIRMED";
                    state.LastErrorMessage = submitted ? null : "提交结果未确认，已保守计入本轮。";
                    if (item.Channel == ShoutChannel.World)
                        state.WorldNextEligibleAt = DateTimeOffset.UtcNow.AddMilliseconds(ShoutConfig.WorldChannelCooldownMs);
                    state.RunState = ShoutQueueRunState.Running;
                    PublishLocked(state);
                }

                Persist(state);
                await Task.Delay(RetryDelay(config), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StopRuntime(state, ShoutQueueRunState.Error, "RUNTIME_FAILED", ex.Message);
            _log.Error("喊话", $"喊话队列异常：{ex.Message}", state.Window.Id);
        }
    }

    private RuntimeState GetOrCreate(RoleSessionCommandContext context)
    {
        var state = _states.GetOrAdd(context.Session.RoleSessionId, _ => LoadState(context));
        if (state.Window.Hwnd == context.Window.Hwnd)
            return state;

        StopRuntime(state, ShoutQueueRunState.Idle, null, null, publish: false);
        lock (state.Sync)
        {
            state.Window = context.Window;
            state.CurrentItemId = null;
            state.CurrentChannel = null;
        }

        return state;
    }

    private RuntimeState LoadState(RoleSessionCommandContext context)
    {
        var state = new RuntimeState(
            context.Session.RoleSessionId,
            context.Session.CharacterProfileId,
            context.Window);
        var profileKey = ProfileKey(context.Session.CharacterProfileId);
        if (!_profiles.ListProfileNames().Contains(profileKey, StringComparer.Ordinal))
            return state;

        var json = _profiles.Load(profileKey);
        if (string.IsNullOrWhiteSpace(json))
            return state;

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedState>(json);
            if (persisted is null || persisted.SchemaVersion != PersistedSchemaVersion)
                return state;

            state.Config = CloneAndNormalize(persisted.Config ?? new ShoutConfig());
            state.WorldNextEligibleAt = persisted.WorldNextEligibleAt;
            state.LastCommittedItemId = persisted.LastCommittedItemId;
            state.LastCommittedOrder = persisted.LastCommittedOrder;
        }
        catch (Exception ex)
        {
            _log.Warn("喊话", $"角色喊话运行态加载失败：{ex.Message}", context.Window.Id);
        }

        return state;
    }

    private void Persist(RuntimeState state)
    {
        PersistedState persisted;
        lock (state.Sync)
        {
            persisted = new PersistedState
            {
                Config = state.Config,
                WorldNextEligibleAt = state.WorldNextEligibleAt,
                LastCommittedItemId = state.LastCommittedItemId,
                LastCommittedOrder = state.LastCommittedOrder
            };
        }

        try
        {
            using var suppression = _log.Suppress(entry =>
                entry.Level == LogLevel.Info && string.Equals(entry.Module, "Profile", StringComparison.Ordinal));
            _profiles.Save(ProfileKey(state.CharacterProfileId), JsonSerializer.Serialize(persisted, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Warn("喊话", $"角色喊话运行态保存失败：{ex.Message}", state.Window.Id);
        }
    }

    private (string Code, string Message)? Validate(ShoutConfig config)
    {
        if (!config.Enabled)
            return ("FEATURE_DISABLED", "喊话未启用。");
        if (!EnabledMessages(config).Any())
            return ("EMPTY_QUEUE", "没有可发送的喊话内容。");
        if (!_channelSelector.IsReady)
            return ("CHANNEL_NOT_READY", _channelSelector.Status);
        if (EnabledMessages(config).Any(item => !_channelSelector.CanSelect(item.Channel)))
            return ("CHANNEL_UNSUPPORTED", "喊话频道未就绪。");
        if (!_input.TryParseHotkey(config.OpenChatHotkey, out _))
            return ("OPEN_CHAT_HOTKEY_INVALID", "打开聊天动作未就绪。");
        if (!_input.TryParseHotkey(config.CommitHotkey, out _))
            return ("COMMIT_HOTKEY_INVALID", "发送动作未就绪。");
        return null;
    }

    private static ShoutMessage? SelectNextItem(RuntimeState state, ShoutConfig config)
    {
        var enabled = EnabledMessages(config);
        if (enabled.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(state.LastCommittedItemId))
            return enabled[0];

        var currentIndex = enabled.FindIndex(item => item.Id == state.LastCommittedItemId);
        if (currentIndex >= 0)
            return enabled[(currentIndex + 1) % enabled.Count];

        return enabled.FirstOrDefault(item => item.Order > state.LastCommittedOrder) ?? enabled[0];
    }

    private static List<ShoutMessage> EnabledMessages(ShoutConfig config) =>
        config.Messages
            .Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

    private void MarkItemFailure(RuntimeState state, string code, string message)
    {
        lock (state.Sync)
        {
            state.LastItemState = ShoutItemRunState.Failed;
            state.LastErrorCode = code;
            state.LastErrorMessage = message;
            state.RunState = ShoutQueueRunState.Running;
            PublishLocked(state);
        }
    }

    private void StopRuntime(
        RuntimeState state,
        ShoutQueueRunState runState,
        string? errorCode,
        string? errorMessage,
        bool publish = true)
    {
        CancellationTokenSource? cancellation;
        lock (state.Sync)
        {
            cancellation = state.RunnerCancellation;
            state.RunnerCancellation = null;
            state.Runner = null;
            state.RunState = runState;
            state.LastErrorCode = errorCode;
            state.LastErrorMessage = errorMessage;
            state.CurrentItemId = null;
            state.CurrentChannel = null;
            if (publish)
                PublishLocked(state);
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void Publish(RuntimeState state)
    {
        lock (state.Sync)
            PublishLocked(state);
    }

    private void PublishLocked(RuntimeState state) =>
        _sessions.PublishFeatureSnapshot(BuildSnapshot(state).Feature);

    private ShoutQueueRuntimeSnapshot BuildSnapshot(RuntimeState state)
    {
        var remaining = (int)Math.Ceiling(Math.Max(0, RemainingWorldCooldown(state).TotalSeconds));
        var feature = new FeatureRuntimeSnapshot(
            state.RoleSessionId,
            FeatureKey,
            state.RunState.ToString(),
            Phase(state.RunState),
            state.RunState is not (ShoutQueueRunState.NotReady or ShoutQueueRunState.Error),
            Summary(state, remaining),
            state.LastErrorMessage,
            DateTimeOffset.UtcNow);

        var items = state.Config.Messages
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ShoutItemRuntimeSnapshot(
                item.Id,
                item.Order,
                item.Channel,
                ItemState(state, item),
                item.Channel == ShoutChannel.World && remaining > 0 ? remaining : null,
                item.Id == state.CurrentItemId || item.Id == state.LastCommittedItemId
                    ? state.LastErrorCode
                    : null))
            .ToArray();

        return new ShoutQueueRuntimeSnapshot(
            feature,
            state.CurrentItemId,
            state.CurrentChannel,
            remaining,
            items);
    }

    private static ShoutItemRunState ItemState(RuntimeState state, ShoutMessage item)
    {
        if (!item.Enabled || string.IsNullOrWhiteSpace(item.Text))
            return ShoutItemRunState.Disabled;
        if (item.Id == state.CurrentItemId)
        {
            return state.RunState switch
            {
                ShoutQueueRunState.WaitingCooldown => ShoutItemRunState.CoolingDown,
                ShoutQueueRunState.SelectingChannel => ShoutItemRunState.SelectingChannel,
                ShoutQueueRunState.Sending => ShoutItemRunState.Sending,
                _ => ShoutItemRunState.Waiting
            };
        }

        return item.Id == state.LastCommittedItemId ? state.LastItemState : ShoutItemRunState.Waiting;
    }

    private static TimeSpan RemainingWorldCooldown(RuntimeState state) =>
        state.WorldNextEligibleAt <= DateTimeOffset.UtcNow
            ? TimeSpan.Zero
            : state.WorldNextEligibleAt - DateTimeOffset.UtcNow;

    private static string Phase(ShoutQueueRunState state) => state switch
    {
        ShoutQueueRunState.WaitingCooldown => "WaitCooldown",
        ShoutQueueRunState.SelectingChannel => "SelectChannel",
        ShoutQueueRunState.Sending => "Send",
        ShoutQueueRunState.Paused => "Paused",
        _ => state.ToString()
    };

    private static string Summary(RuntimeState state, int remaining) => state.RunState switch
    {
        ShoutQueueRunState.Running => "运行中",
        ShoutQueueRunState.Paused => "已暂停",
        ShoutQueueRunState.WaitingCooldown => $"世界频道冷却 {remaining / 60:00}:{remaining % 60:00}",
        ShoutQueueRunState.SelectingChannel => "正在选择频道",
        ShoutQueueRunState.Sending => "发送中",
        ShoutQueueRunState.NotReady => "未就绪",
        ShoutQueueRunState.Error => "执行失败",
        ShoutQueueRunState.Stopped => "已停止",
        _ => "待启动"
    };

    private static bool IsActive(ShoutQueueRunState state) => state is
        ShoutQueueRunState.Running
        or ShoutQueueRunState.WaitingCooldown
        or ShoutQueueRunState.SelectingChannel
        or ShoutQueueRunState.Sending;

    private static bool IsPaused(RuntimeState state)
    {
        lock (state.Sync)
            return state.RunState == ShoutQueueRunState.Paused;
    }

    private static async Task WaitUntilRunnableAsync(RuntimeState state, CancellationToken token)
    {
        while (IsPaused(state))
            await Task.Delay(100, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
    }

    private static ShoutQueueCommandReply Reply(bool accepted, string code, string summary) =>
        new(accepted, code, summary);

    private static int RetryDelay(ShoutConfig config) => Math.Clamp(config.IntervalMs, 1000, 3_600_000);

    private static string NormalizeMessage(string value) =>
        string.Join(' ', (value ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Trim();

    private static ShoutConfig CloneAndNormalize(ShoutConfig source)
    {
        var clone = JsonSerializer.Deserialize<ShoutConfig>(JsonSerializer.Serialize(source)) ?? new ShoutConfig();
        clone.OpenChatHotkey = string.IsNullOrWhiteSpace(clone.OpenChatHotkey) ? "Enter" : clone.OpenChatHotkey.Trim();
        clone.CommitHotkey = string.IsNullOrWhiteSpace(clone.CommitHotkey) ? "Enter" : clone.CommitHotkey.Trim();
        clone.IntervalMs = Math.Clamp(clone.IntervalMs, 1000, 3_600_000);
        clone.MessageDelayMs = Math.Clamp(clone.MessageDelayMs, 0, 5000);
        clone.CommitDelayMs = Math.Clamp(clone.CommitDelayMs, 0, 5000);
        clone.Messages ??= new List<ShoutMessage>();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < clone.Messages.Count; index++)
        {
            var item = clone.Messages[index];
            var id = item.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || !ids.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!ids.Add(id));
            }

            item.Id = id;
            item.Order = index + 1;
            item.Text = item.Text?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(item.Channel))
                item.Channel = ShoutChannel.Nearby;
        }

        return clone;
    }

    private static string ProfileKey(string characterProfileId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(characterProfileId));
        return "_session-shout-" + Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private void OnSessionChanged(object? sender, RoleSessionSnapshot snapshot)
    {
        if (snapshot.LifecycleState != RoleSessionLifecycleState.Closed
            || !_states.TryRemove(snapshot.RoleSessionId, out var state))
        {
            return;
        }

        StopRuntime(state, ShoutQueueRunState.Stopped, null, null, publish: false);
    }

    private sealed class RuntimeState
    {
        public RuntimeState(string roleSessionId, string characterProfileId, BoundGameWindow window)
        {
            RoleSessionId = roleSessionId;
            CharacterProfileId = characterProfileId;
            Window = window;
        }

        public object Sync { get; } = new();
        public string RoleSessionId { get; }
        public string CharacterProfileId { get; }
        public BoundGameWindow Window { get; set; }
        public ShoutConfig Config { get; set; } = new();
        public InputDispatchMode InputDispatchMode { get; set; } = InputDispatchMode.PostMessage;
        public ShoutQueueRunState RunState { get; set; } = ShoutQueueRunState.Idle;
        public string? CurrentItemId { get; set; }
        public ShoutChannel? CurrentChannel { get; set; }
        public string? LastCommittedItemId { get; set; }
        public int LastCommittedOrder { get; set; }
        public ShoutItemRunState LastItemState { get; set; } = ShoutItemRunState.Waiting;
        public DateTimeOffset WorldNextEligibleAt { get; set; }
        public string? LastErrorCode { get; set; }
        public string? LastErrorMessage { get; set; }
        public CancellationTokenSource? RunnerCancellation { get; set; }
        public Task? Runner { get; set; }
    }

    private sealed class PersistedState
    {
        public int SchemaVersion { get; set; } = PersistedSchemaVersion;
        public ShoutConfig? Config { get; set; }
        public DateTimeOffset WorldNextEligibleAt { get; set; }
        public string? LastCommittedItemId { get; set; }
        public int LastCommittedOrder { get; set; }
    }
}
