namespace TlbbAgent.Contracts;

public enum AutoReconnectState
{
    Idle = 0,
    BindWindow,
    MonitorWindow,
    RebindExistingProcess,
    RestartClient,
    RebindWindow,
    WaitRetry,
    Stop,
    Error
}

public sealed class AutoReconnectConfig
{
    public bool Enabled { get; set; }
    public int CheckIntervalMs { get; set; } = 3000;
    public int RebindTimeoutMs { get; set; } = 8000;
    public int RelaunchDelayMs { get; set; } = 1500;
    public int MaxRestartAttempts { get; set; } = 3;
    public bool RestartWhenProcessExited { get; set; } = true;
    public string ProcessPathOverride { get; set; } = "";
}

public sealed record ClientLaunchResult(
    bool Started,
    int? Pid,
    string Message);

public interface IAutoReconnectConfigService
{
    AutoReconnectConfig Current { get; }
    event EventHandler? Changed;
    void NotifyChanged();
    void Save(string profileName);
    bool Load(string profileName);
}

public interface IClientProcessService
{
    bool IsWindowAlive(nint hwnd);
    bool IsProcessAlive(int pid);
    nint WaitForMainWindow(int pid, int timeoutMs, int pollIntervalMs = 250);
    ClientLaunchResult StartClient(string processPath);
}

public interface IAutoReconnectController : IAutomationController { }
