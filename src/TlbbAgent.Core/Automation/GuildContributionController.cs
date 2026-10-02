using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class GuildContributionController : AutomationControllerBase<GuildContributionState>, IGuildContributionController
{
    private readonly IGuildContributionConfigService _config;
    private readonly INavigationRecognitionService _recognition;
    private readonly IReturnNavigationService _navigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private List<GuildContributionTaskType> _taskTypes = new();
    private int _taskIndex;
    private int _stepIndex;
    private int _roundIndex;
    private int _retry;

    public GuildContributionController(
        ILogService log,
        IGuildContributionConfigService config,
        INavigationRecognitionService recognition,
        IReturnNavigationService navigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(GuildContributionState.Idle, log)
    {
        _config = config;
        _recognition = recognition;
        _navigation = navigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "task.guild";
    public override string Name => "帮贡五合一";
    public override string Group => "任务自动化";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _taskTypes = _config.TaskTypes.Where(t => t.Enabled).OrderBy(t => t.Priority).ToList();
        _taskIndex = 0;
        _stepIndex = 0;
        _roundIndex = 0;
        _retry = 0;
        Machine.Goto(GuildContributionState.ValidateConfig);
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
                Log.Warn(Name, "未绑定窗口，帮贡停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            if (_taskTypes.Count == 0)
            {
                Log.Warn(Name, "未启用帮贡任务类型。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (_taskIndex >= _taskTypes.Count)
            {
                Machine.Goto(GuildContributionState.NextRound);
                _roundIndex++;
                _taskIndex = 0;
                _stepIndex = 0;
                _retry = 0;

                if (ShouldStop(cfg))
                {
                    Machine.Goto(GuildContributionState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                continue;
            }

            Machine.Goto(GuildContributionState.SelectTaskType);
            var task = _taskTypes[_taskIndex];
            var steps = _config.Steps
                .Where(s => string.Equals(s.TaskType, task.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (steps.Count == 0)
            {
                Log.Warn(Name, $"任务类型未配置步骤：{task.Name}", window.Id);
                NextTask();
                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            if (_stepIndex >= steps.Count)
            {
                if (cfg.EnableCleanupAfterTask)
                    ExecuteCleanup(window, cfg);

                Machine.Goto(GuildContributionState.NextTaskType);
                NextTask();
                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            var step = steps[_stepIndex];
            Machine.Goto(GuildContributionState.RecognizePosition);
            var nav = _recognition.Recognize(window, cfg.Recognition);
            var distance = Distance(nav, step);

            if (RequiresNavigation(step) && distance > cfg.ArrivalDistance)
            {
                Machine.Goto(GuildContributionState.NavigateStep);
                var result = _navigation.TryNavigate(window, nav, ToReturnPoint(step), distance, cfg.Navigation);
                _retry++;
                if (!result.Executed && _retry > EffectiveRetry(step, cfg))
                {
                    Machine.Goto(GuildContributionState.Recover);
                    Log.Warn(Name, $"帮贡导航失败，跳过步骤：{step.Label}。原因：{result.Message}", window.Id);
                    NextStep();
                }

                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(GuildContributionState.ExecuteStep);
            if (ExecuteStep(window, step, cfg))
            {
                Machine.Goto(GuildContributionState.WaitAfterStep);
                Log.Info(Name, $"帮贡步骤完成：{task.Name} / {step.Label}", window.Id);
                _retry = 0;
                await Delay(Math.Max(cfg.ActionIntervalMs, step.WaitMs), token).ConfigureAwait(false);
                Machine.Goto(GuildContributionState.NextStep);
                NextStep();
                continue;
            }

            _retry++;
            if (_retry > EffectiveRetry(step, cfg))
            {
                Machine.Goto(GuildContributionState.Recover);
                Log.Warn(Name, $"帮贡步骤失败，跳过：{step.Label}", window.Id);
                NextStep();
            }

            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(GuildContributionState.Stop);

        RaiseStateChanged();
    }

    private bool ExecuteStep(BoundGameWindow window, GuildContributionStep step, GuildContributionConfig cfg)
    {
        return step.Action switch
        {
            GuildContributionStepAction.Navigate => true,
            GuildContributionStepAction.Wait => true,
            GuildContributionStepAction.Text => !string.IsNullOrWhiteSpace(step.Text)
                                                 && _input.SendText(window.Hwnd, step.Text, cfg.DispatchMode),
            GuildContributionStepAction.Click => _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode),
            GuildContributionStepAction.UseHotkey => SendHotkey(window, step.Hotkey, cfg),
            GuildContributionStepAction.CleanupBag => ExecuteCleanupStep(window, step, cfg),
            GuildContributionStepAction.TalkNpc
                or GuildContributionStepAction.AcceptTask
                or GuildContributionStepAction.ExecuteTask
                or GuildContributionStepAction.SubmitTask
                or GuildContributionStepAction.BuyItem
                or GuildContributionStepAction.DeliverItem => ExecuteTaskAction(window, step, cfg),
            _ => false
        };
    }

    private bool ExecuteTaskAction(BoundGameWindow window, GuildContributionStep step, GuildContributionConfig cfg)
    {
        var hotkey = string.IsNullOrWhiteSpace(step.Hotkey) ? cfg.OpenTaskHotkey : step.Hotkey;
        var needsHotkey = cfg.ExecutionMode is GuildContributionExecutionMode.HotkeyOnly or GuildContributionExecutionMode.HotkeyThenClick;
        var needsClick = cfg.ExecutionMode is GuildContributionExecutionMode.ClickOnly or GuildContributionExecutionMode.HotkeyThenClick;
        var executed = false;

        if (needsHotkey && !string.IsNullOrWhiteSpace(hotkey))
            executed |= _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);

        if (needsClick)
            executed |= _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode);

        if (!string.IsNullOrWhiteSpace(step.Text))
            executed |= _input.SendText(window.Hwnd, step.Text, cfg.DispatchMode);

        return executed;
    }

    private bool ExecuteCleanupStep(BoundGameWindow window, GuildContributionStep step, GuildContributionConfig cfg)
    {
        var executed = false;
        if (!string.IsNullOrWhiteSpace(step.Hotkey))
            executed |= _input.SendKey(window.Hwnd, step.Hotkey, cfg.DispatchMode);
        if (step.ClickX > 0 && step.ClickY > 0)
            executed |= _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode);
        return executed;
    }

    private bool ExecuteCleanup(BoundGameWindow window, GuildContributionConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.CleanupHotkey))
            return false;

        var ok = _input.SendKey(window.Hwnd, cfg.CleanupHotkey, cfg.DispatchMode);
        Log.Info(Name, ok ? "已执行任务清包动作。" : "任务清包动作失败。", window.Id);
        return ok;
    }

    private bool SendHotkey(BoundGameWindow window, string hotkey, GuildContributionConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "帮贡步骤热键为空。", window.Id);
            return false;
        }

        return _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);
    }

    private void NextStep()
    {
        _retry = 0;
        _stepIndex++;
    }

    private void NextTask()
    {
        _retry = 0;
        _stepIndex = 0;
        _taskIndex++;
    }

    private bool ShouldStop(GuildContributionConfig cfg)
    {
        if (cfg.LoopPlan && !cfg.StopAfterRounds)
            return false;

        return _roundIndex >= cfg.RoundCount;
    }

    private static bool RequiresNavigation(GuildContributionStep step) =>
        !string.IsNullOrWhiteSpace(step.MapName) && (step.X > 0 || step.Y > 0);

    private static int EffectiveRetry(GuildContributionStep step, GuildContributionConfig cfg) =>
        Math.Max(0, step.RetryLimit > 0 ? step.RetryLimit : cfg.MaxStepRetry);

    private static ReturnPoint ToReturnPoint(GuildContributionStep step) => new()
    {
        MapName = step.MapName,
        X = step.X,
        Y = step.Y,
        Label = step.Label,
        Kind = ReturnPointKind.Normal,
        SourceModule = step.SourceModule,
        SourcePath = step.SourcePath
    };

    private static double Distance(NavigationRecognitionResult nav, GuildContributionStep step)
    {
        if (!nav.HasCoordinate || nav.X is null || nav.Y is null)
            return double.MaxValue;

        var dx = nav.X.Value - step.X;
        var dy = nav.Y.Value - step.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);
}
