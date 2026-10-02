using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class SchoolTaskController : AutomationControllerBase<SchoolTaskState>, ISchoolTaskController
{
    private readonly ISchoolTaskConfigService _config;
    private readonly INavigationRecognitionService _recognition;
    private readonly IReturnNavigationService _navigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _stepIndex;
    private int _roundIndex;
    private int _retry;

    public SchoolTaskController(
        ILogService log,
        ISchoolTaskConfigService config,
        INavigationRecognitionService recognition,
        IReturnNavigationService navigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(SchoolTaskState.Idle, log)
    {
        _config = config;
        _recognition = recognition;
        _navigation = navigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "task.school";
    public override string Name => "单师门";
    public override string Group => "任务自动化";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _stepIndex = 0;
        _roundIndex = 0;
        _retry = 0;
        Machine.Goto(SchoolTaskState.ValidateConfig);
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
                Log.Warn(Name, "未绑定窗口，单师门停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            var steps = _config.Steps;
            if (steps.Count == 0)
            {
                Log.Warn(Name, "师门流程为空，请先录入或载入流程。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (_stepIndex >= steps.Count)
            {
                if (cfg.EnableCleanupAfterRound)
                    ExecuteCleanup(window, cfg);

                Machine.Goto(SchoolTaskState.NextRound);
                _roundIndex++;
                _stepIndex = 0;
                _retry = 0;

                if (ShouldStop(cfg))
                {
                    Machine.Goto(SchoolTaskState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                continue;
            }

            var step = steps[_stepIndex];
            Machine.Goto(SchoolTaskState.RecognizePosition);
            var nav = _recognition.Recognize(window, cfg.Recognition);
            var distance = Distance(nav, step);

            if (RequiresNavigation(step) && distance > cfg.ArrivalDistance)
            {
                Machine.Goto(SchoolTaskState.NavigateStep);
                var result = _navigation.TryNavigate(window, nav, ToReturnPoint(step), distance, cfg.Navigation);
                _retry++;
                if (!result.Executed && _retry > EffectiveRetry(step, cfg))
                {
                    Machine.Goto(SchoolTaskState.Recover);
                    Log.Warn(Name, $"师门导航失败，跳过步骤：{step.Label}。原因：{result.Message}", window.Id);
                    NextStep();
                }

                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(SchoolTaskState.ExecuteStep);
            if (ExecuteStep(window, step, cfg))
            {
                Machine.Goto(SchoolTaskState.WaitAfterStep);
                Log.Info(Name, $"师门步骤完成：{step.Label}", window.Id);
                _retry = 0;
                await Delay(Math.Max(cfg.ActionIntervalMs, step.WaitMs), token).ConfigureAwait(false);
                Machine.Goto(SchoolTaskState.NextStep);
                NextStep();
                continue;
            }

            _retry++;
            if (_retry > EffectiveRetry(step, cfg))
            {
                Machine.Goto(SchoolTaskState.Recover);
                Log.Warn(Name, $"师门步骤失败，跳过：{step.Label}", window.Id);
                NextStep();
            }

            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(SchoolTaskState.Stop);

        RaiseStateChanged();
    }

    private bool ExecuteStep(BoundGameWindow window, SchoolTaskStep step, SchoolTaskConfig cfg)
    {
        return step.Action switch
        {
            SchoolTaskStepAction.Navigate => true,
            SchoolTaskStepAction.Wait => true,
            SchoolTaskStepAction.Text => !string.IsNullOrWhiteSpace(step.Text)
                                         && _input.SendText(window.Hwnd, step.Text, cfg.DispatchMode),
            SchoolTaskStepAction.Click => _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode),
            SchoolTaskStepAction.UseHotkey => SendHotkey(window, step.Hotkey, cfg),
            SchoolTaskStepAction.CleanupBag => ExecuteCleanupStep(window, step, cfg),
            SchoolTaskStepAction.TalkNpc
                or SchoolTaskStepAction.AcceptTask
                or SchoolTaskStepAction.CompleteTask
                or SchoolTaskStepAction.SubmitTask
                or SchoolTaskStepAction.BuyItem
                or SchoolTaskStepAction.StoreItem
                or SchoolTaskStepAction.TradeItem => ExecuteTaskAction(window, step, cfg),
            _ => false
        };
    }

    private bool ExecuteTaskAction(BoundGameWindow window, SchoolTaskStep step, SchoolTaskConfig cfg)
    {
        var hotkey = string.IsNullOrWhiteSpace(step.Hotkey) ? cfg.OpenTaskHotkey : step.Hotkey;
        var needsHotkey = cfg.ExecutionMode is SchoolTaskExecutionMode.HotkeyOnly or SchoolTaskExecutionMode.HotkeyThenClick;
        var needsClick = cfg.ExecutionMode is SchoolTaskExecutionMode.ClickOnly or SchoolTaskExecutionMode.HotkeyThenClick;
        var executed = false;

        if (needsHotkey && !string.IsNullOrWhiteSpace(hotkey))
            executed |= _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);

        if (needsClick)
            executed |= _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode);

        if (!string.IsNullOrWhiteSpace(step.Text))
            executed |= _input.SendText(window.Hwnd, step.Text, cfg.DispatchMode);

        return executed;
    }

    private bool ExecuteCleanupStep(BoundGameWindow window, SchoolTaskStep step, SchoolTaskConfig cfg)
    {
        var executed = false;
        if (!string.IsNullOrWhiteSpace(step.Hotkey))
            executed |= _input.SendKey(window.Hwnd, step.Hotkey, cfg.DispatchMode);
        if (step.ClickX > 0 && step.ClickY > 0)
            executed |= _input.Click(window.Hwnd, step.ClickX, step.ClickY, cfg.DispatchMode);
        return executed;
    }

    private bool ExecuteCleanup(BoundGameWindow window, SchoolTaskConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.CleanupHotkey))
            return false;

        var ok = _input.SendKey(window.Hwnd, cfg.CleanupHotkey, cfg.DispatchMode);
        Log.Info(Name, ok ? "已执行轮次清包动作。" : "轮次清包动作失败。", window.Id);
        return ok;
    }

    private bool SendHotkey(BoundGameWindow window, string hotkey, SchoolTaskConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "师门步骤热键为空。", window.Id);
            return false;
        }

        return _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);
    }

    private void NextStep()
    {
        _retry = 0;
        _stepIndex++;
    }

    private bool ShouldStop(SchoolTaskConfig cfg)
    {
        if (cfg.LoopPlan && !cfg.StopAfterRounds)
            return false;

        return _roundIndex >= cfg.RoundCount;
    }

    private static bool RequiresNavigation(SchoolTaskStep step) =>
        !string.IsNullOrWhiteSpace(step.MapName) && (step.X > 0 || step.Y > 0);

    private static int EffectiveRetry(SchoolTaskStep step, SchoolTaskConfig cfg) =>
        Math.Max(0, step.RetryLimit > 0 ? step.RetryLimit : cfg.MaxStepRetry);

    private static ReturnPoint ToReturnPoint(SchoolTaskStep step) => new()
    {
        MapName = step.MapName,
        X = step.X,
        Y = step.Y,
        Label = step.Label,
        Kind = ReturnPointKind.Normal,
        SourceModule = step.SourceModule,
        SourcePath = step.SourcePath
    };

    private static double Distance(NavigationRecognitionResult nav, SchoolTaskStep step)
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
