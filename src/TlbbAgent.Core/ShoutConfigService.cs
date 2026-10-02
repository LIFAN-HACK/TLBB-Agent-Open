using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ShoutConfigService : IShoutConfigService
{
    private const string Module = "喊话";
    private const string Prefix = "shout-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ShoutConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
        Current = CreateDefault();
    }

    public ShoutConfig Current { get; private set; }
    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Save(string profileName)
    {
        Normalize(Current);
        _profiles.Save(Prefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"已保存喊话配置：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(Prefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            Current = JsonSerializer.Deserialize<ShoutConfig>(json) ?? CreateDefault();
            Normalize(Current);
            NotifyChanged();
            _log.Info(Module, $"已加载喊话配置：{profileName}");
            return true;
        }
        catch
        {
            _log.Warn(Module, "配置加载失败。");
            return false;
        }
    }

    private static ShoutConfig CreateDefault() => new()
    {
        Enabled = false,
        OpenChatHotkey = "Enter",
        CommitHotkey = "Enter",
        IntervalMs = 30000,
        MessageDelayMs = 80,
        CommitDelayMs = 80,
        RotateMessages = true,
        Messages =
        {
            new ShoutMessage
            {
                Order = 1,
                Enabled = false,
                Channel = ShoutChannel.Nearby,
                Text = "",
                SourceModule = "内置",
                SourcePath = ""
            }
        }
    };

    private static void Normalize(ShoutConfig config)
    {
        config.OpenChatHotkey = string.IsNullOrWhiteSpace(config.OpenChatHotkey) ? "Enter" : config.OpenChatHotkey.Trim();
        config.CommitHotkey = string.IsNullOrWhiteSpace(config.CommitHotkey) ? "Enter" : config.CommitHotkey.Trim();
        config.IntervalMs = Math.Clamp(config.IntervalMs, 1000, 3600_000);
        config.MessageDelayMs = Math.Clamp(config.MessageDelayMs, 0, 5000);
        config.CommitDelayMs = Math.Clamp(config.CommitDelayMs, 0, 5000);
        config.Messages ??= new List<ShoutMessage>();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < config.Messages.Count; index++)
        {
            var message = config.Messages[index];
            var id = message.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || !ids.Add(id))
            {
                do
                {
                    id = Guid.NewGuid().ToString("N");
                }
                while (!ids.Add(id));

                message.Id = id;
            }

            message.Order = index + 1;
            if (!Enum.IsDefined(message.Channel))
                message.Channel = ShoutChannel.Nearby;
            message.Text ??= string.Empty;
            message.SourceModule ??= "内置";
            message.SourcePath ??= string.Empty;
        }
    }
}
