using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class TradeController : AutomationControllerBase<TradeState>, ITradeController
{
    private readonly ITradeConfigService _config;
    private readonly INavigationRecognitionService _recognition;
    private readonly IReturnNavigationService _navigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _stepIndex;
    private int _retry;

    public TradeController(
        ILogService log,
        ITradeConfigService config,
        INavigationRecognitionService recognition,
        IReturnNavigationService navigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(TradeState.Idle, log)
    {
        _config = config;
        _recognition = recognition;
        _navigation = navigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "life.trade";
    public override string Name => "跑商";
    public override string Group => "生活";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _stepIndex = 0;
        _retry = 0;
        Machine.Goto(TradeState.ValidateConfig);
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
                Log.Warn(Name, "未绑定窗口，跑商状态机停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            var route = _config.Route;
            if (route.Count == 0)
            {
                Log.Warn(Name, "跑商路线为空，请先录入或导入路线。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (_stepIndex >= route.Count)
            {
                if (cfg.LoopRoute && !cfg.StopWhenRouteEnds)
                {
                    _stepIndex = 0;
                }
                else
                {
                    Machine.Goto(TradeState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }
            }

            var point = route[_stepIndex];
            Machine.Goto(TradeState.RecognizePosition);
            var nav = _recognition.Recognize(window, cfg.Recognition);
            var distance = Distance(nav, point);

            if (distance > cfg.ArrivalDistance)
            {
                Machine.Goto(TradeState.NavigatePoint);
                var result = _navigation.TryNavigate(window, nav, ToReturnPoint(point), distance, cfg.Navigation);
                _retry++;
                if (!result.Executed && _retry > EffectiveRetry(point, cfg))
                {
                    Machine.Goto(TradeState.Recover);
                    Log.Warn(Name, $"跑商导航失败，跳过步骤：{point.Label}。原因：{result.Message}", window.Id);
                    NextStep();
                }

                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(TradeState.ExecuteTradeAction);
            if (ExecuteAction(window, point, cfg))
            {
                Machine.Goto(TradeState.WaitAfterAction);
                Log.Info(Name, $"跑商动作完成：{point.Action} | {point.MapName}({point.X},{point.Y}) | {DisplayName(point)}", window.Id);
                _retry = 0;
                await Delay(Math.Max(cfg.ActionIntervalMs, point.WaitMs), token).ConfigureAwait(false);
                Machine.Goto(TradeState.NextPoint);
                NextStep();
                continue;
            }

            _retry++;
            if (_retry > EffectiveRetry(point, cfg))
            {
                Machine.Goto(TradeState.Recover);
                Log.Warn(Name, $"跑商动作失败超过上限，跳过步骤：{point.Label}。", window.Id);
                NextStep();
            }

            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(TradeState.Stop);

        RaiseStateChanged();
    }

    private bool ExecuteAction(BoundGameWindow window, TradeRoutePoint point, TradeConfig cfg)
    {
        if (point.Action == TradeRouteAction.Navigate)
            return true;

        var hotkey = string.IsNullOrWhiteSpace(point.Hotkey) ? cfg.OpenMapHotkey : point.Hotkey;
        var needsHotkey = cfg.ExecutionMode is TradeExecutionMode.HotkeyOnly or TradeExecutionMode.HotkeyThenClick
                          || point.Action == TradeRouteAction.Hotkey;
        var needsClick = cfg.ExecutionMode is TradeExecutionMode.ClickOnly or TradeExecutionMode.HotkeyThenClick
                         || point.Action is TradeRouteAction.Click or TradeRouteAction.Buy or TradeRouteAction.Sell or TradeRouteAction.TalkNpc;

        if (needsHotkey && !string.IsNullOrWhiteSpace(hotkey))
        {
            if (!_input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode))
                return false;
        }

        if (needsClick)
        {
            if (!_input.Click(window.Hwnd, point.ClickX, point.ClickY, cfg.DispatchMode))
                return false;
        }

        return needsHotkey || needsClick;
    }

    private void NextStep()
    {
        _retry = 0;
        _stepIndex++;
    }

    private static int EffectiveRetry(TradeRoutePoint point, TradeConfig cfg) =>
        Math.Max(0, point.RetryLimit > 0 ? point.RetryLimit : cfg.MaxStepRetry);

    private static ReturnPoint ToReturnPoint(TradeRoutePoint point) => new()
    {
        MapName = point.MapName,
        X = point.X,
        Y = point.Y,
        Label = point.Label,
        Kind = ReturnPointKind.Normal,
        SourceModule = point.SourceModule,
        SourcePath = point.SourcePath
    };

    private static string DisplayName(TradeRoutePoint point)
    {
        if (!string.IsNullOrWhiteSpace(point.NpcName) && !string.IsNullOrWhiteSpace(point.GoodsName))
            return $"{point.NpcName}/{point.GoodsName}";
        if (!string.IsNullOrWhiteSpace(point.NpcName))
            return point.NpcName;
        if (!string.IsNullOrWhiteSpace(point.GoodsName))
            return point.GoodsName;
        return point.Label;
    }

    private static double Distance(NavigationRecognitionResult nav, TradeRoutePoint point)
    {
        if (!nav.HasCoordinate || nav.X is null || nav.Y is null)
            return double.MaxValue;

        var dx = nav.X.Value - point.X;
        var dy = nav.Y.Value - point.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);
}
