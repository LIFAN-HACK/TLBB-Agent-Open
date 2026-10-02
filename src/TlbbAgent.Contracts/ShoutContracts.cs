namespace TlbbAgent.Contracts;

public enum ShoutState
{
    Idle = 0,
    BindWindow,
    ValidateConfig,
    SelectChannel,
    OpenChat,
    SendMessage,
    CommitMessage,
    WaitInterval,
    Stop,
    Error
}

public enum ShoutChannel
{
    Nearby = 0,
    World,
    SameCity,
    Guild,
    Alliance,
    School,
    Team
}

public sealed class ShoutConfig
{
    public const int WorldChannelCooldownMs = 180_000;

    public bool Enabled { get; set; }
    public string OpenChatHotkey { get; set; } = "Enter";
    public string CommitHotkey { get; set; } = "Enter";
    public int IntervalMs { get; set; } = 30000;
    public int MessageDelayMs { get; set; } = 80;
    public int CommitDelayMs { get; set; } = 80;
    public bool RotateMessages { get; set; } = true;
    public bool StopWhenMainModeStopped { get; set; }
    public List<ShoutMessage> Messages { get; set; } = new();
}

public sealed class ShoutMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public ShoutChannel Channel { get; set; } = ShoutChannel.Nearby;
    public string Text { get; set; } = "";
    public string SourceModule { get; set; } = "内置";
    public string SourcePath { get; set; } = "";
}

public interface IShoutChannelSelector
{
    bool IsReady { get; }
    string Status { get; }
    bool CanSelect(ShoutChannel channel);
    bool TrySelect(BoundGameWindow window, ShoutChannel channel, InputDispatchMode mode);
}

public interface IShoutConfigService
{
    ShoutConfig Current { get; }
    event EventHandler? Changed;
    void NotifyChanged();
    void Save(string profileName);
    bool Load(string profileName);
}

public interface IShoutController : IAutomationController
{
    bool IsChannelRoutingReady { get; }
    string ChannelRoutingStatus { get; }
}
