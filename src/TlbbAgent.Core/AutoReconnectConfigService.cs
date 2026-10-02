using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class AutoReconnectConfigService : IAutoReconnectConfigService
{
    private const string Module = "自动重连";
    private const string Prefix = "auto-reconnect-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AutoReconnectConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public AutoReconnectConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Normalize(Current);
        _profiles.Save(Prefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"已保存自动重连配置：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(Prefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            Current = JsonSerializer.Deserialize<AutoReconnectConfig>(json) ?? new AutoReconnectConfig();
            Normalize(Current);
            Changed?.Invoke(this, EventArgs.Empty);
            _log.Info(Module, $"已加载自动重连配置：{profileName}");
            return true;
        }
        catch
        {
            _log.Warn(Module, "配置加载失败。");
            return false;
        }
    }

    private static void Normalize(AutoReconnectConfig config)
    {
        config.CheckIntervalMs = Math.Clamp(config.CheckIntervalMs, 500, 60000);
        config.RebindTimeoutMs = Math.Clamp(config.RebindTimeoutMs, 1000, 120000);
        config.RelaunchDelayMs = Math.Clamp(config.RelaunchDelayMs, 0, 60000);
        config.MaxRestartAttempts = Math.Clamp(config.MaxRestartAttempts, 1, 20);
        config.ProcessPathOverride = config.ProcessPathOverride?.Trim() ?? "";
    }
}
