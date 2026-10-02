using System.Security.Cryptography;
using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class ShoutController : AutomationControllerBase<ShoutState>, IShoutController
{
    private const string FeatureKey = FeatureKeys.LifeShout;
    private readonly IShoutConfigService _config;
    private readonly IGlobalConfigService _global;
    private readonly IAppProfileService _profiles;
    private readonly IRoleSessionManager _sessions;
    private readonly IShoutQueueRuntimeService _runtime;
    private readonly IShoutChannelSelector _channelSelector;
    private string? _roleSessionId;

    public ShoutController(
        ILogService log,
        IShoutConfigService config,
        IGlobalConfigService global,
        IAppProfileService profiles,
        IRoleSessionManager sessions,
        IShoutQueueRuntimeService runtime,
        IShoutChannelSelector channelSelector)
        : base(ShoutState.Idle, log)
    {
        _config = config;
        _global = global;
        _profiles = profiles;
        _sessions = sessions;
        _runtime = runtime;
        _channelSelector = channelSelector;
        _sessions.FeatureSnapshotChanged += OnFeatureSnapshotChanged;
    }

    public override string Key => FeatureKey;
    public override string Name => "喊话";
    public override string Group => "通用辅助";
    public bool IsChannelRoutingReady => _channelSelector.IsReady;
    public string ChannelRoutingStatus => _channelSelector.Status;

    protected override void OnStart()
    {
        Machine.Goto(ShoutState.BindWindow);
        if (Target is null)
        {
            Fail("未绑定窗口，喊话无法启动。");
            return;
        }

        var profileId = ResolveProfileId(Target);
        var roleSessionId = BuildRoleSessionId(Target, profileId);
        if (_roleSessionId is not null && !string.Equals(_roleSessionId, roleSessionId, StringComparison.Ordinal))
            _sessions.Close(_roleSessionId);

        RoleSessionSnapshot session;
        try
        {
            session = _sessions.OpenOrUpdate(roleSessionId, profileId, Target);
        }
        catch (Exception ex)
        {
            Fail($"角色会话创建失败：{ex.Message}");
            return;
        }

        _roleSessionId = roleSessionId;
        Machine.Goto(ShoutState.ValidateConfig);
        var apply = _runtime.ApplyConfig(new FeatureCommand<ShoutConfig>(
            Guid.NewGuid().ToString("N"),
            roleSessionId,
            session.ConfigRevision,
            _config.Current));
        if (!apply.Succeeded || apply.Value?.Accepted != true)
        {
            Fail(apply.Value?.Summary ?? apply.ErrorMessage ?? "喊话配置无效。");
            return;
        }

        var isPaused = _runtime.TryGetSnapshot(roleSessionId, out var current)
                       && current.Feature.RunState == ShoutQueueRunState.Paused.ToString();
        var result = isPaused
            ? _runtime.Resume(new FeatureCommand<EmptyFeaturePayload>(
                Guid.NewGuid().ToString("N"),
                roleSessionId,
                apply.CurrentRevision,
                new EmptyFeaturePayload()))
            : _runtime.Start(new FeatureCommand<ShoutQueueStartRequest>(
                Guid.NewGuid().ToString("N"),
                roleSessionId,
                apply.CurrentRevision,
                new ShoutQueueStartRequest(_global.Current.InputDispatchMode)));

        if (!result.Succeeded || result.Value?.Accepted != true)
        {
            Fail(result.Value?.Summary ?? result.ErrorMessage ?? "喊话未就绪。");
            return;
        }

        RunState = RunState.Running;
        Machine.Goto(ShoutState.SelectChannel);
    }

    public override void Pause()
    {
        if (RunState != RunState.Running || !TryGetRevision(out var revision))
            return;

        var result = _runtime.Pause(new FeatureCommand<EmptyFeaturePayload>(
            Guid.NewGuid().ToString("N"),
            _roleSessionId!,
            revision,
            new EmptyFeaturePayload()));
        if (!result.Succeeded || result.Value?.Accepted != true)
        {
            Log.Warn(Name, result.Value?.Summary ?? result.ErrorMessage ?? "暂停失败。", Target?.Id ?? "-");
            return;
        }

        RunState = RunState.Paused;
        Machine.Goto(ShoutState.WaitInterval);
        RaiseStateChanged();
    }

    public override void Stop()
    {
        if (TryGetRevision(out var revision))
        {
            _runtime.Stop(new FeatureCommand<EmptyFeaturePayload>(
                Guid.NewGuid().ToString("N"),
                _roleSessionId!,
                revision,
                new EmptyFeaturePayload()));
        }

        base.Stop();
    }

    private bool TryGetRevision(out long revision)
    {
        if (_roleSessionId is not null && _sessions.TryGetSnapshot(_roleSessionId, out var snapshot))
        {
            revision = snapshot.ConfigRevision;
            return true;
        }

        revision = 0;
        return false;
    }

    private void OnFeatureSnapshotChanged(object? sender, FeatureRuntimeSnapshot snapshot)
    {
        if (!string.Equals(snapshot.RoleSessionId, _roleSessionId, StringComparison.Ordinal)
            || !string.Equals(snapshot.FeatureKey, FeatureKey, StringComparison.Ordinal))
        {
            return;
        }

        RunState = snapshot.RunState switch
        {
            nameof(ShoutQueueRunState.Paused) => RunState.Paused,
            nameof(ShoutQueueRunState.NotReady) or nameof(ShoutQueueRunState.Error) => RunState.Error,
            nameof(ShoutQueueRunState.Stopped) => RunState.Stopped,
            _ => RunState.Running
        };
        Machine.Goto(snapshot.Phase switch
        {
            "WaitCooldown" or "Paused" => ShoutState.WaitInterval,
            "SelectChannel" => ShoutState.SelectChannel,
            "Send" => ShoutState.SendMessage,
            _ when RunState == RunState.Error => ShoutState.Error,
            _ when RunState == RunState.Stopped => ShoutState.Stop,
            _ => ShoutState.SelectChannel
        });
        RaiseStateChanged();
    }

    private void Fail(string message)
    {
        RunState = RunState.Error;
        Machine.Goto(ShoutState.Error);
        Log.Warn(Name, message, Target?.Id ?? "-");
        RaiseStateChanged();
    }

    private string ResolveProfileId(BoundGameWindow window) =>
        string.IsNullOrWhiteSpace(_profiles.CurrentProfileName)
            ? "window-" + StableHash($"{window.ProcessPath}|{window.Title}")
            : _profiles.CurrentProfileName!;

    private static string BuildRoleSessionId(BoundGameWindow window, string profileId) =>
        $"legacy-{window.Hwnd.ToInt64():X}-{StableHash(profileId)}";

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

public sealed class UnavailableShoutChannelSelector : IShoutChannelSelector
{
    public bool IsReady => false;
    public string Status => "频道未就绪";
    public bool CanSelect(ShoutChannel channel) => false;
    public bool TrySelect(BoundGameWindow window, ShoutChannel channel, InputDispatchMode mode) => false;
}
