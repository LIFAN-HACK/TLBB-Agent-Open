using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class LifeCollectController : AutomationControllerBase<LifeCollectState>, ILifeCollectController
{
    private readonly ILifeCollectConfigService _config;
    private readonly INavigationRecognitionService _recognition;
    private readonly IReturnNavigationService _navigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _pointIndex;
    private int _pointRetry;

    public LifeCollectController(
        ILogService log,
        ILifeCollectConfigService config,
        INavigationRecognitionService recognition,
        IReturnNavigationService navigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(LifeCollectState.Idle, log)
    {
        _config = config;
        _recognition = recognition;
        _navigation = navigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "life.collect";
    public override string Name => "采药 / 采集";
    public override string Group => "生活";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _pointIndex = 0;
        _pointRetry = 0;
        Machine.Goto(LifeCollectState.ValidateConfig);
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
                Log.Warn(Name, "未绑定窗口，生活采集状态机停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            var route = _config.CurrentRoute;
            if (route.Count == 0)
            {
                Log.Warn(Name, $"地图 {cfg.MapName} 未配置生活采集坐标，状态机停止。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (!_input.TryParseHotkey(cfg.CollectHotkey, out var collectError))
            {
                Log.Warn(Name, $"采集快捷键无效：{collectError}", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            var point = route[_pointIndex % route.Count];
            Machine.Goto(LifeCollectState.RecognizePosition);
            var nav = _recognition.Recognize(window, cfg.Recognition);
            var distance = Distance(nav, point);

            if (cfg.EnableRouteNavigation && distance > cfg.ArrivalDistance)
            {
                Machine.Goto(LifeCollectState.NavigatePoint);
                var rp = new ReturnPoint
                {
                    MapName = point.MapName,
                    X = point.X,
                    Y = point.Y,
                    Kind = ReturnPointKind.FixedFarm,
                    Label = point.Label,
                    SourceModule = point.SourceModule,
                    SourcePath = point.SourcePath
                };
                var navResult = _navigation.TryNavigate(window, nav, rp, distance, cfg.Navigation);
                _pointRetry++;
                if (!navResult.Executed && _pointRetry > Math.Max(0, cfg.MaxPointRetry))
                {
                    Machine.Goto(LifeCollectState.Recover);
                    Log.Warn(Name, $"生活采集导航失败超过上限：{navResult.Message}", window.Id);
                    _pointRetry = 0;
                    _pointIndex++;
                }
                await Delay(cfg.ScanIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(LifeCollectState.Collect);
            if (ExecuteCollectAction(window, cfg, point))
            {
                Log.Info(Name, $"执行采集动作：{cfg.Mode} | {point.MapName}({point.X},{point.Y}) | {point.Label}", window.Id);
                _pointRetry = 0;
                Machine.Goto(LifeCollectState.NextPoint);
                _pointIndex++;
                await Delay(cfg.CollectIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            _pointRetry++;
            if (_pointRetry > Math.Max(0, cfg.MaxPointRetry))
            {
                Machine.Goto(LifeCollectState.Recover);
                Log.Warn(Name, $"采集动作失败超过上限，切换下一个采集点：{point.MapName}({point.X},{point.Y})。", window.Id);
                _pointRetry = 0;
                _pointIndex++;
            }

            await Delay(cfg.ScanIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(LifeCollectState.Stop);

        RaiseStateChanged();
    }

    private bool ExecuteCollectAction(BoundGameWindow window, LifeCollectConfig cfg, LifeCollectPoint point)
    {
        if (cfg.ActionMode is LifeCollectActionMode.HotkeyOnly or LifeCollectActionMode.HotkeyThenClickClientPoint)
        {
            if (!_input.SendKey(window.Hwnd, cfg.CollectHotkey, cfg.DispatchMode))
                return false;
        }

        if (cfg.ActionMode is LifeCollectActionMode.ClickClientPoint or LifeCollectActionMode.HotkeyThenClickClientPoint)
        {
            if (!_input.Click(window.Hwnd, cfg.ActionClickX, cfg.ActionClickY, cfg.DispatchMode))
                return false;
        }

        return true;
    }

    private static double Distance(NavigationRecognitionResult nav, LifeCollectPoint point)
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
