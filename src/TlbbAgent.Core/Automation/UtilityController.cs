using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class UtilityController : AutomationControllerBase<UtilityState>, IUtilityController
{
    private readonly IUtilityConfigService _config;
    private readonly IInputService _input;
    private readonly IBagCleanupService _bagCleanup;
    private readonly IStateMachineEngine _engine;
    private readonly ILatestFrameService? _frames;
    private readonly IInteractionReadOnlyPlanService? _interactionPlans;
    private IDisposable? _runner;

    public UtilityController(
        ILogService log,
        IUtilityConfigService config,
        IInputService input,
        IBagCleanupService bagCleanup,
        IStateMachineEngine engine,
        ILatestFrameService? frames = null,
        IInteractionReadOnlyPlanService? interactionPlans = null)
        : base(UtilityState.Idle, log)
    {
        _config = config;
        _input = input;
        _bagCleanup = bagCleanup;
        _engine = engine;
        _frames = frames;
        _interactionPlans = interactionPlans;
    }

    public override string Key => "hang.utility";
    public override string Name => "清包 / 随身便捷";
    public override string Group => "挂机";

    protected override void OnStart()
    {
        _runner?.Dispose();
        Machine.Goto(UtilityState.ValidateConfig);

        if (Target is null)
        {
            RunState = RunState.Error;
            Machine.Goto(UtilityState.Error);
            Log.Warn(Name, "未绑定窗口，便捷动作无法启动。");
            RaiseStateChanged();
            return;
        }

        if (EnabledSteps(_config.Current).Count == 0)
        {
            RunState = RunState.Error;
            Machine.Goto(UtilityState.Error);
            Log.Warn(Name, "没有启用的便捷动作。", Target.Id);
            RaiseStateChanged();
            return;
        }

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
        try
        {
            do
            {
                foreach (var step in EnabledSteps(_config.Current))
                {
                    if (token.IsCancellationRequested)
                        return;

                    while (RunState == RunState.Paused && !token.IsCancellationRequested)
                        await Delay(200, token).ConfigureAwait(false);

                    if (Target is null)
                    {
                        RunState = RunState.Error;
                        Machine.Goto(UtilityState.Error);
                        Log.Warn(Name, "目标窗口丢失，便捷动作停止。");
                        RaiseStateChanged();
                        return;
                    }

                    Machine.Goto(UtilityState.ExecuteAction);
                    if (!ExecuteStep(step))
                    {
                        RunState = RunState.Error;
                        Machine.Goto(UtilityState.Error);
                        RaiseStateChanged();
                        return;
                    }

                    Machine.Goto(UtilityState.WaitAfterAction);
                    await Delay(step.DelayAfterMs, token).ConfigureAwait(false);
                    Machine.Goto(UtilityState.NextAction);
                }

                if (_config.Current.RepeatSequence && !_config.Current.StopAfterSequence)
                {
                    Machine.Goto(UtilityState.WaitRepeat);
                    await Delay(_config.Current.RepeatIntervalMs, token).ConfigureAwait(false);
                }
                else
                {
                    break;
                }
            }
            while (!token.IsCancellationRequested);

            Machine.Goto(UtilityState.Stop);
            RunState = RunState.Stopped;
            Log.Info(Name, "便捷动作已完成。", Target?.Id ?? "-");
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ExecuteStep(UtilityActionStep step)
    {
        if (Target is null)
            return false;

        if (step.Group == UtilityActionGroup.Cleanup && _config.Current.Cleanup.Mode != BagCleanupExecutionMode.Disabled)
        {
            var result = _bagCleanup.Execute(Target, _config.Current.Cleanup);
            Log.Info(Name, result.Message, Target.Id);
            return result.Success;
        }

        if (step.Group == UtilityActionGroup.Cleanup)
        {
            Log.Warn(Name, "清包分组未配置只读清包模式；已阻止把通用热键或坐标当作清包动作。", Target.Id);
            return false;
        }

        if (step.Group is UtilityActionGroup.PortableBank or UtilityActionGroup.PortableShop)
            return InspectPortableActionAndBlock(step);

        return step.Kind switch
        {
            UtilityActionKind.Wait => true,
            UtilityActionKind.Hotkey => SendHotkey(step),
            UtilityActionKind.Click => Click(step),
            UtilityActionKind.HotkeyThenClick => SendHotkey(step) && Click(step),
            UtilityActionKind.Text => !string.IsNullOrWhiteSpace(step.Text)
                                      && _input.SendText(Target.Hwnd, step.Text, _config.Current.DispatchMode),
            _ => false
        };
    }

    private bool InspectPortableActionAndBlock(UtilityActionStep step)
    {
        if (Target is null || _frames is null || _interactionPlans is null)
        {
            Log.Warn(Name, $"{step.Label} 缺少交互只读计划服务；已阻止旧热键/绝对坐标动作。", Target?.Id ?? "-");
            return false;
        }

        var frame = _frames.GetLatest(Target.Hwnd);
        if (frame is null)
        {
            Log.Warn(Name, $"{step.Label} 无当前帧；已阻止动作。", Target.Id);
            return false;
        }

        const string panelClass = "monthly_card_panel";
        var controlId = step.Group == UtilityActionGroup.PortableBank
            ? "open_portable_bank"
            : "open_portable_pet_shop";
        var plan = _interactionPlans.Build(Target.Id, frame, panelClass);
        var action = plan.Actions.SingleOrDefault(item =>
            string.Equals(item.ControlId, controlId, StringComparison.Ordinal));
        if (!plan.Available || action is null)
        {
            Log.Warn(
                Name,
                $"{step.Label} 只读计划不可用：status={plan.Status}，control={controlId}；未发送输入。",
                Target.Id);
            return false;
        }

        Log.Warn(
            Name,
            $"{step.Label} 已定位只读控件 {controlId}，frame={plan.FrameSequence}，" +
            $"safe=({action.SafeClickBoxPx.X},{action.SafeClickBoxPx.Y},{action.SafeClickBoxPx.Width},{action.SafeClickBoxPx.Height})，" +
            $"policy={action.PolicyDecision}，runtime_authorized={action.RuntimeAuthorized.ToString().ToLowerInvariant()}；" +
            "通用便捷动作不执行该点击。",
            Target.Id);
        return false;
    }

    private bool SendHotkey(UtilityActionStep step)
    {
        if (Target is null)
            return false;

        if (!_input.TryParseHotkey(step.Hotkey, out var error))
        {
            Log.Warn(Name, $"热键无效：{step.Label} | {error}", Target.Id);
            return false;
        }

        var ok = _input.SendKey(Target.Hwnd, step.Hotkey, _config.Current.DispatchMode);
        Log.Info(Name, ok ? $"执行热键：{step.Label}" : $"热键失败：{step.Label}", Target.Id);
        return ok;
    }

    private bool Click(UtilityActionStep step)
    {
        if (Target is null)
            return false;

        var ok = _input.Click(Target.Hwnd, step.ClickX, step.ClickY, _config.Current.DispatchMode);
        Log.Info(Name, ok ? $"执行点击：{step.Label}" : $"点击失败：{step.Label}", Target.Id);
        return ok;
    }

    private static List<UtilityActionStep> EnabledSteps(UtilityConfig config) =>
        config.Steps.Where(s => s.Enabled).ToList();

    private static Task Delay(int ms, CancellationToken token) =>
        Task.Delay(Math.Max(0, ms), token);
}
