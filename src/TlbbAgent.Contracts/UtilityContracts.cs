namespace TlbbAgent.Contracts;

public enum UtilityActionGroup
{
    Cleanup = 0,
    PortableBank,
    PortableShop,
    Yulong,
    Custom
}

public enum UtilityActionKind
{
    Wait = 0,
    Hotkey,
    Click,
    HotkeyThenClick,
    Text
}

public enum UtilityState
{
    Idle,
    ValidateConfig,
    ExecuteAction,
    WaitAfterAction,
    NextAction,
    WaitRepeat,
    Stop,
    Error
}

public sealed class UtilityActionStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public UtilityActionGroup Group { get; set; } = UtilityActionGroup.Custom;
    public UtilityActionKind Kind { get; set; } = UtilityActionKind.Hotkey;
    public string Label { get; set; } = "动作";
    public string Hotkey { get; set; } = "";
    public string Text { get; set; } = "";
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public int DelayAfterMs { get; set; } = 300;
}

public sealed class UtilityConfig
{
    public string ProfileName { get; set; } = "默认便捷";
    public string PlanName { get; set; } = "便捷动作";
    public bool StopAfterSequence { get; set; } = true;
    public bool RepeatSequence { get; set; }
    public int RepeatIntervalMs { get; set; } = 30000;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public BagCleanupConfig Cleanup { get; set; } = new();
    public List<UtilityActionStep> Steps { get; set; } = new();
    public List<string> KeepItems { get; set; } = new();
}

public interface IUtilityConfigService
{
    UtilityConfig Current { get; }
    event EventHandler? Changed;

    void NotifyChanged();

    IReadOnlyList<UtilityActionStep> Steps { get; }
    IReadOnlyList<string> KeepItems { get; }
    int LoadKeepItems(string path);
    void AddStep(UtilityActionStep step);
    bool RemoveStep(string id);
    void MoveStep(string id, int offset);
    void Save(string profileName);
    bool Load(string profileName);
}
