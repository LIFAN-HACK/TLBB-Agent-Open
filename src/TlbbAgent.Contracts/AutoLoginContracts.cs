namespace TlbbAgent.Contracts;

public enum AutoLoginState
{
    Idle = 0,
    BindWindow,
    ValidateConfig,
    WaitBeforeStart,
    ExecuteStep,
    WaitStep,
    Stop,
    Error
}

public enum AutoLoginStepKind
{
    Wait = 0,
    Hotkey,
    Click
}

public sealed class AutoLoginStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public AutoLoginStepKind Kind { get; set; } = AutoLoginStepKind.Hotkey;
    public string Label { get; set; } = "步骤";
    public string Hotkey { get; set; } = "";
    public int ClickX { get; set; }
    public int ClickY { get; set; }
    public int DelayAfterMs { get; set; } = 1000;
}

public sealed class AutoLoginConfig
{
    public bool Enabled { get; set; }
    public int StartDelayMs { get; set; } = 1500;
    public bool StopAfterSequence { get; set; } = true;
    public List<AutoLoginStep> Steps { get; set; } = new();
}

public interface IAutoLoginConfigService
{
    AutoLoginConfig Current { get; }
    event EventHandler? Changed;
    void NotifyChanged();
    void Save(string profileName);
    bool Load(string profileName);
    void AddStep(AutoLoginStep step);
    bool RemoveStep(string id);
    void MoveStep(string id, int offset);
}

public interface IAutoLoginController : IAutomationController { }
