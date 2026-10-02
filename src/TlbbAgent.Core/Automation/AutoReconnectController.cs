using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class AutoReconnectController : AutomationControllerBase<AutoReconnectState>, IAutoReconnectController
{
    private readonly IAutoReconnectConfigService _config;
    private readonly IClientProcessService _clientProcess;
    private readonly IWindowBindingService _binding;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _restartAttempts;

    public AutoReconnectController(
        ILogService log,
        IAutoReconnectConfigService config,
        IClientProcessService clientProcess,
        IWindowBindingService binding,
        IStateMachineEngine engine)
        : base(AutoReconnectState.Idle, log)
    {
        _config = config;
        _clientProcess = clientProcess;
        _binding = binding;
        _engine = engine;
    }

    public override string Key => FeatureKeys.UtilityAutoReconnect;
    public override string Name => "自动重连";
    public override string Group => "通用辅助";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _restartAttempts = 0;
        Machine.Goto(AutoReconnectState.BindWindow);

        if (Target is null)
        {
            RunState = RunState.Error;
            Machine.Goto(AutoReconnectState.Error);
            Log.Warn(Name, "未绑定窗口，自动重连无法启动。");
            RaiseStateChanged();
            return;
        }

        var config = _config.Current;
        if (!config.Enabled)
        {
            RunState = RunState.Stopped;
            Machine.Goto(AutoReconnectState.Stop);
            Log.Warn(Name, "自动重连未启用。", Target.Id);
            RaiseStateChanged();
            return;
        }

        Log.Info(Name, $"自动重连已启动：检查间隔 {config.CheckIntervalMs}ms，最大重启 {config.MaxRestartAttempts} 次。", Target.Id);
        _runner = _engine.Run(Name, RunLoopAsync, MarkFaulted);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var config = _config.Current;
            if (!config.Enabled)
            {
                RunState = RunState.Stopped;
                Machine.Goto(AutoReconnectState.Stop);
                RaiseStateChanged();
                break;
            }

            if (RunState == RunState.Paused)
            {
                await Delay(250, token).ConfigureAwait(false);
                continue;
            }

            if (Target is null)
            {
                RunState = RunState.Error;
                Machine.Goto(AutoReconnectState.Error);
                Log.Warn(Name, "绑定窗口丢失，自动重连停止。");
                RaiseStateChanged();
                break;
            }

            Machine.Goto(AutoReconnectState.MonitorWindow);
            var windowAlive = _clientProcess.IsWindowAlive(Target.Hwnd);
            var processAlive = _clientProcess.IsProcessAlive(Target.Pid);
            if (windowAlive && processAlive)
            {
                if (RunState != RunState.Running)
                {
                    RunState = RunState.Running;
                    RaiseStateChanged();
                }

                await Delay(config.CheckIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            RunState = RunState.Reconnecting;
            RaiseStateChanged();

            if (processAlive)
            {
                if (TryRebindExistingProcess(config))
                    continue;

                await WaitRetry(config, token).ConfigureAwait(false);
                continue;
            }

            if (!config.RestartWhenProcessExited)
            {
                RunState = RunState.Stopped;
                Machine.Goto(AutoReconnectState.Stop);
                Log.Warn(Name, "目标进程已退出，配置为不自动启动客户端，自动重连停止。", Target.Id);
                RaiseStateChanged();
                break;
            }

            if (!TryRestartClient(config))
            {
                if (_restartAttempts >= config.MaxRestartAttempts)
                {
                    RunState = RunState.Error;
                    Machine.Goto(AutoReconnectState.Error);
                    Log.Warn(Name, $"自动重连达到最大重启次数：{config.MaxRestartAttempts}。", Target.Id);
                    RaiseStateChanged();
                    break;
                }

                await WaitRetry(config, token).ConfigureAwait(false);
                continue;
            }
        }
    }

    private bool TryRebindExistingProcess(AutoReconnectConfig config)
    {
        if (Target is null)
            return false;

        Machine.Goto(AutoReconnectState.RebindExistingProcess);
        Log.Warn(Name, "窗口失效，正在找回。", Target.Id);
        var hwnd = _clientProcess.WaitForMainWindow(Target.Pid, config.RebindTimeoutMs);
        return TryBindRecoveredWindow(hwnd, "已找回窗口并重新绑定。");
    }

    private bool TryRestartClient(AutoReconnectConfig config)
    {
        if (Target is null)
            return false;

        _restartAttempts++;
        Machine.Goto(AutoReconnectState.RestartClient);

        var processPath = string.IsNullOrWhiteSpace(config.ProcessPathOverride)
            ? Target.ProcessPath
            : config.ProcessPathOverride;

        if (string.IsNullOrWhiteSpace(processPath))
        {
            Log.Warn(Name, "请在通用配置中填写启动文件。", Target.Id);
            return false;
        }

        if (config.RelaunchDelayMs > 0)
            Thread.Sleep(config.RelaunchDelayMs);

        var launch = _clientProcess.StartClient(processPath);
        if (!launch.Started || launch.Pid is null)
        {
            Log.Warn(Name, $"客户端启动失败：{launch.Message}", Target.Id);
            return false;
        }

        Machine.Goto(AutoReconnectState.RebindWindow);
        Log.Info(Name, "客户端已启动，等待窗口。", Target.Id);
        var hwnd = _clientProcess.WaitForMainWindow(launch.Pid.Value, config.RebindTimeoutMs);
        return TryBindRecoveredWindow(hwnd, "重启后已重新绑定窗口。");
    }

    private bool TryBindRecoveredWindow(nint hwnd, string successMessage)
    {
        if (hwnd == nint.Zero)
        {
            Log.Warn(Name, "未找到可绑定窗口。", Target?.Id ?? "-");
            return false;
        }

        var rebound = _binding.InspectByHandle(hwnd);
        if (rebound is null)
        {
            Log.Warn(Name, "窗口找回成功，但绑定失败。", Target?.Id ?? "-");
            return false;
        }

        Target = rebound;
        RunState = RunState.Running;
        Machine.Goto(AutoReconnectState.MonitorWindow);
        _restartAttempts = 0;
        Log.Info(Name, successMessage, Target.Id);
        RaiseStateChanged();
        return true;
    }

    private async Task WaitRetry(AutoReconnectConfig config, CancellationToken token)
    {
        Machine.Goto(AutoReconnectState.WaitRetry);
        await Delay(Math.Max(config.CheckIntervalMs, 500), token).ConfigureAwait(false);
    }

    private static Task Delay(int ms, CancellationToken token) =>
        Task.Delay(Math.Max(0, ms), token);
}
