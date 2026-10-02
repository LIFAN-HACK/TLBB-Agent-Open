using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class AutoLoginController : AutomationControllerBase<AutoLoginState>, IAutoLoginController
{
    private readonly IAutoLoginConfigService _config;
    private readonly IGlobalConfigService _global;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;

    public AutoLoginController(
        ILogService log,
        IAutoLoginConfigService config,
        IGlobalConfigService global,
        IInputService input,
        IStateMachineEngine engine)
        : base(AutoLoginState.Idle, log)
    {
        _config = config;
        _global = global;
        _input = input;
        _engine = engine;
    }

    public override string Key => FeatureKeys.UtilityAutoLogin;
    public override string Name => "自动上号";
    public override string Group => "通用辅助";

    protected override void OnStart()
    {
        _runner?.Dispose();
        Machine.Goto(AutoLoginState.BindWindow);

        if (Target is null)
        {
            RunState = RunState.Error;
            Machine.Goto(AutoLoginState.Error);
            Log.Warn(Name, "未绑定窗口，自动上号无法启动。");
            RaiseStateChanged();
            return;
        }

        if (!ValidateConfig(_config.Current))
        {
            RunState = RunState.Error;
            Machine.Goto(AutoLoginState.Error);
            RaiseStateChanged();
            return;
        }

        var count = EnabledSteps(_config.Current).Count;
        Log.Info(Name, $"自动上号已启动：动作 {count} 个。", Target.Id);
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
        var config = _config.Current;
        try
        {
            Machine.Goto(AutoLoginState.WaitBeforeStart);
            await Delay(config.StartDelayMs, token).ConfigureAwait(false);

            foreach (var step in EnabledSteps(config))
            {
                if (token.IsCancellationRequested)
                    break;

                if (RunState == RunState.Paused)
                {
                    while (RunState == RunState.Paused && !token.IsCancellationRequested)
                        await Delay(200, token).ConfigureAwait(false);
                }

                if (Target is null)
                {
                    RunState = RunState.Error;
                    Machine.Goto(AutoLoginState.Error);
                    Log.Warn(Name, "目标窗口丢失，自动上号停止。");
                    RaiseStateChanged();
                    return;
                }

                Machine.Goto(AutoLoginState.ExecuteStep);
                if (!ExecuteStep(step))
                {
                    RunState = RunState.Error;
                    Machine.Goto(AutoLoginState.Error);
                    RaiseStateChanged();
                    return;
                }

                Machine.Goto(AutoLoginState.WaitStep);
                await Delay(step.DelayAfterMs, token).ConfigureAwait(false);
            }

            Machine.Goto(AutoLoginState.Stop);
            RunState = RunState.Stopped;
            if (config.StopAfterSequence && config.Enabled)
            {
                config.Enabled = false;
                _config.NotifyChanged();
            }
            Log.Info(Name, "自动上号动作序列已完成。", Target?.Id ?? "-");
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ExecuteStep(AutoLoginStep step)
    {
        if (Target is null)
            return false;

        switch (step.Kind)
        {
            case AutoLoginStepKind.Wait:
                Log.Trace(Name, $"等待：{step.Label}，{step.DelayAfterMs}ms。", Target.Id);
                return true;

            case AutoLoginStepKind.Hotkey:
                if (!_input.TryParseHotkey(step.Hotkey, out var error))
                {
                    Log.Warn(Name, $"热键无效：{step.Label} [{step.Hotkey}] | {error}", Target.Id);
                    return false;
                }

                if (!_input.SendKey(Target.Hwnd, step.Hotkey, _global.Current.InputDispatchMode))
                {
                    Log.Warn(Name, $"发送热键失败：{step.Label} [{step.Hotkey}]", Target.Id);
                    return false;
                }

                Log.Info(Name, $"执行热键：{step.Label} [{step.Hotkey}]", Target.Id);
                return true;

            case AutoLoginStepKind.Click:
                if (step.ClickX < 0 || step.ClickY < 0)
                {
                    Log.Warn(Name, $"点击坐标无效：{step.Label} {step.ClickX},{step.ClickY}", Target.Id);
                    return false;
                }

                if (!_input.Click(Target.Hwnd, step.ClickX, step.ClickY, _global.Current.InputDispatchMode))
                {
                    Log.Warn(Name, $"点击失败：{step.Label} {step.ClickX},{step.ClickY}", Target.Id);
                    return false;
                }

                Log.Info(Name, $"执行点击：{step.Label} {step.ClickX},{step.ClickY}", Target.Id);
                return true;

            default:
                Log.Warn(Name, $"未知动作类型：{step.Kind}", Target.Id);
                return false;
        }
    }

    private bool ValidateConfig(AutoLoginConfig config)
    {
        Machine.Goto(AutoLoginState.ValidateConfig);

        if (!config.Enabled)
        {
            Log.Warn(Name, "自动上号未启用。", Target?.Id ?? "-");
            return false;
        }

        if (EnabledSteps(config).Count == 0)
        {
            Log.Warn(Name, "自动上号没有可执行动作。", Target?.Id ?? "-");
            return false;
        }

        foreach (var step in EnabledSteps(config).Where(s => s.Kind == AutoLoginStepKind.Hotkey))
        {
            if (!_input.TryParseHotkey(step.Hotkey, out var error))
            {
                Log.Warn(Name, $"热键无效：{step.Label} [{step.Hotkey}] | {error}", Target?.Id ?? "-");
                return false;
            }
        }

        return true;
    }

    private static List<AutoLoginStep> EnabledSteps(AutoLoginConfig config) =>
        config.Steps
            .Where(s => s.Enabled)
            .ToList();

    private static Task Delay(int ms, CancellationToken token) =>
        Task.Delay(Math.Max(0, ms), token);
}
