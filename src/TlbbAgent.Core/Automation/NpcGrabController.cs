using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class NpcGrabController : AutomationControllerBase<NpcGrabState>, INpcGrabController
{
    private readonly INpcGrabConfigService _config;
    private readonly INpcTargetingService _targeting;
    private readonly INpcActionService _action;
    private readonly ISmallNpcFastGrabService _smallFastGrab;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _retryCount;

    public NpcGrabController(
        ILogService log,
        INpcGrabConfigService config,
        INpcTargetingService targeting,
        INpcActionService action,
        ISmallNpcFastGrabService smallFastGrab,
        IStateMachineEngine engine)
        : base(NpcGrabState.Idle, log)
    {
        _config = config;
        _targeting = targeting;
        _action = action;
        _smallFastGrab = smallFastGrab;
        _engine = engine;
    }

    public override string Key => "act.grab";
    public override string Name => "抢 NPC";
    public override string Group => "活动";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _retryCount = 0;
        Machine.Goto(NpcGrabState.Scan);
        _runner = _engine.Run(Name, LoopAsync, MarkFaulted);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && RunState == RunState.Running)
        {
            var window = Target;
            if (window is null)
            {
                Log.Warn(Name, "未绑定窗口，抢 NPC 状态机停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            if (_config.EnabledTargets.Count == 0)
            {
                Log.Warn(Name, "未启用任何抢 NPC 目标，状态机停止。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            var plan = _smallFastGrab.Plan(cfg);
            if (plan.IsRefreshBurstWindow)
                Log.Trace(Name, $"小小刷新窗口：{plan.Reason}，扫描间隔 {plan.NextScanDelayMs}ms。", window.Id);

            if (plan.ShouldExecuteContextFastAction && plan.ContextTarget is not null)
            {
                Machine.Goto(NpcGrabState.Action);
                var fastResult = _action.Execute(window, plan.ContextTarget, cfg);
                _smallFastGrab.ObserveActionResult(plan.ContextTarget, fastResult);
                if (fastResult.Executed)
                {
                    _retryCount = 0;
                    Log.Info(Name, $"抢小2上下文快抢已执行：{fastResult.Message}", window.Id);
                    await Delay(cfg.ClickIntervalMs, token).ConfigureAwait(false);
                    continue;
                }

                Log.Warn(Name, $"抢小2上下文快抢失败：{fastResult.Message}", window.Id);
                if (!plan.ShouldRunActiveRecognition)
                {
                    await Delay(plan.NextScanDelayMs, token).ConfigureAwait(false);
                    continue;
                }
            }

            if (!plan.ShouldRunActiveRecognition)
            {
                await Delay(plan.NextScanDelayMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(NpcGrabState.Scan);
            _action.Scan(window, cfg);
            await Delay(plan.NextScanDelayMs, token).ConfigureAwait(false);

            Machine.Goto(NpcGrabState.SelectTarget);
            var target = _targeting.SelectTarget(window, cfg);
            if (!target.Found)
            {
                await Delay(plan.NextScanDelayMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(NpcGrabState.Action);
            var result = _action.Execute(window, target, cfg);
            _smallFastGrab.ObserveActionResult(target, result);
            if (result.Executed)
            {
                _retryCount = 0;
                await Delay(cfg.ClickIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            _retryCount++;
            Machine.Goto(NpcGrabState.Retry);
            Log.Warn(Name, $"抢 NPC 动作失败：{result.Message} | 重试 {_retryCount}/{cfg.RetryLimit}", window.Id);

            if (_retryCount > Math.Max(0, cfg.RetryLimit))
            {
                Log.Warn(Name, "抢 NPC 达到重试上限，重置目标扫描。", window.Id);
                _retryCount = 0;
            }

            await Delay(cfg.RetryDelayMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(NpcGrabState.Stop);

        RaiseStateChanged();
    }

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);
}
