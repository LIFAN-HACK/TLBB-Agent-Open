using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class TreasureMapController : AutomationControllerBase<TreasureMapState>, ITreasureMapController
{
    private readonly ITreasureMapConfigService _config;
    private readonly INavigationRecognitionService _recognition;
    private readonly IReturnNavigationService _navigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _pointIndex;
    private int _batchIndex;
    private int _retry;
    private bool _preparedCurrentPoint;

    public TreasureMapController(
        ILogService log,
        ITreasureMapConfigService config,
        INavigationRecognitionService recognition,
        IReturnNavigationService navigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(TreasureMapState.Idle, log)
    {
        _config = config;
        _recognition = recognition;
        _navigation = navigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "life.map";
    public override string Name => "挖图";
    public override string Group => "生活";

    protected override void OnStart()
    {
        _runner?.Dispose();
        _pointIndex = 0;
        _batchIndex = 0;
        _retry = 0;
        _preparedCurrentPoint = false;
        Machine.Goto(TreasureMapState.ValidateConfig);
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
                Log.Warn(Name, "未绑定窗口，挖图停止。");
                RunState = RunState.Stopped;
                break;
            }

            var cfg = _config.Current;
            var route = _config.Route;
            if (route.Count == 0)
            {
                Log.Warn(Name, "挖图坐标为空，请先录入或载入坐标。", window.Id);
                RunState = RunState.Stopped;
                break;
            }

            if (_pointIndex >= route.Count)
            {
                _batchIndex++;
                if (ShouldStop(cfg))
                {
                    Machine.Goto(TreasureMapState.Stop);
                    RunState = RunState.Stopped;
                    break;
                }

                _pointIndex = 0;
                _preparedCurrentPoint = false;
                continue;
            }

            var point = route[_pointIndex];
            if (!_preparedCurrentPoint)
            {
                Machine.Goto(TreasureMapState.PrepareMap);
                ExecutePrepare(window, cfg);
                _preparedCurrentPoint = true;
                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(TreasureMapState.RecognizePosition);
            var nav = _recognition.Recognize(window, cfg.Recognition);
            var distance = Distance(nav, point);

            if (distance > cfg.ArrivalDistance)
            {
                Machine.Goto(TreasureMapState.NavigatePoint);
                var result = _navigation.TryNavigate(window, nav, ToReturnPoint(point), distance, cfg.Navigation);
                _retry++;
                if (!result.Executed && _retry > EffectiveRetry(point, cfg))
                {
                    Machine.Goto(TreasureMapState.Recover);
                    Log.Warn(Name, $"挖图导航失败，跳过坐标：{point.Label}。原因：{result.Message}", window.Id);
                    NextPoint();
                }

                await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(TreasureMapState.ExecuteDig);
            if (ExecutePointAction(window, point, cfg))
            {
                Log.Info(Name, $"挖图动作完成：{point.MapName}({point.X},{point.Y})。", window.Id);
                _retry = 0;
                await Delay(Math.Max(cfg.ActionIntervalMs, point.WaitMs), token).ConfigureAwait(false);

                if (cfg.OpenNormalBox || cfg.OpenGoldBox)
                {
                    Machine.Goto(TreasureMapState.OpenReward);
                    ExecuteBox(window, cfg, point);
                    await Delay(cfg.ActionIntervalMs, token).ConfigureAwait(false);
                }

                if (cfg.PickupDrops && !string.IsNullOrWhiteSpace(cfg.PickupHotkey))
                    _input.SendKey(window.Hwnd, cfg.PickupHotkey, cfg.DispatchMode);

                if (cfg.EnableCleanupAfterDig)
                {
                    Machine.Goto(TreasureMapState.CleanupBag);
                    ExecuteCleanup(window, cfg);
                    await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
                }

                Machine.Goto(TreasureMapState.NextPoint);
                NextPoint();
                continue;
            }

            _retry++;
            if (_retry > EffectiveRetry(point, cfg))
            {
                Machine.Goto(TreasureMapState.Recover);
                Log.Warn(Name, $"挖图动作失败，跳过坐标：{point.Label}", window.Id);
                NextPoint();
            }

            await Delay(cfg.StepIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(TreasureMapState.Stop);

        RaiseStateChanged();
    }

    private void ExecutePrepare(BoundGameWindow window, TreasureMapConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.OpenMapHotkey))
            _input.SendKey(window.Hwnd, cfg.OpenMapHotkey, cfg.DispatchMode);

        if (cfg.UseLocator && !string.IsNullOrWhiteSpace(cfg.LocatorHotkey))
            _input.SendKey(window.Hwnd, cfg.LocatorHotkey, cfg.DispatchMode);

        if (cfg.FetchFromWarehouse)
            Log.Info(Name, "已启用仓库取图流程。", window.Id);

        if (cfg.FetchFromShop)
            Log.Info(Name, "已启用商会取图流程。", window.Id);
    }

    private bool ExecutePointAction(BoundGameWindow window, TreasureMapPoint point, TreasureMapConfig cfg)
    {
        var hotkey = string.IsNullOrWhiteSpace(point.Hotkey) ? cfg.DigHotkey : point.Hotkey;
        if (point.Action == TreasureMapStepAction.Wait)
            return true;

        if (point.Action == TreasureMapStepAction.Navigate)
            return true;

        if (point.Action == TreasureMapStepAction.UseLocator)
            hotkey = string.IsNullOrWhiteSpace(point.Hotkey) ? cfg.LocatorHotkey : point.Hotkey;

        var needsHotkey = cfg.ActionMode is TreasureMapActionMode.HotkeyOnly or TreasureMapActionMode.HotkeyThenClick
                          || point.Action is TreasureMapStepAction.OpenMap or TreasureMapStepAction.Dig or TreasureMapStepAction.Pickup or TreasureMapStepAction.UseLocator;
        var needsClick = cfg.ActionMode is TreasureMapActionMode.ClickOnly or TreasureMapActionMode.HotkeyThenClick
                         || point.Action is TreasureMapStepAction.Click or TreasureMapStepAction.OpenBox or TreasureMapStepAction.WarehouseFetch or TreasureMapStepAction.ShopFetch;
        var executed = false;

        if (needsHotkey && !string.IsNullOrWhiteSpace(hotkey))
            executed |= _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);

        if (needsClick)
            executed |= _input.Click(window.Hwnd, point.ClickX, point.ClickY, cfg.DispatchMode);

        return executed;
    }

    private bool ExecuteBox(BoundGameWindow window, TreasureMapConfig cfg, TreasureMapPoint point)
    {
        var hotkey = string.IsNullOrWhiteSpace(point.Hotkey) ? cfg.DigHotkey : point.Hotkey;
        var clicked = _input.Click(window.Hwnd, point.ClickX, point.ClickY, cfg.DispatchMode);
        if (!string.IsNullOrWhiteSpace(hotkey))
            clicked |= _input.SendKey(window.Hwnd, hotkey, cfg.DispatchMode);
        return clicked;
    }

    private bool ExecuteCleanup(BoundGameWindow window, TreasureMapConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.CleanupHotkey))
            return false;

        var ok = _input.SendKey(window.Hwnd, cfg.CleanupHotkey, cfg.DispatchMode);
        Log.Info(Name, ok ? "已执行清包动作。" : "清包动作失败。", window.Id);
        return ok;
    }

    private void NextPoint()
    {
        _retry = 0;
        _preparedCurrentPoint = false;
        _pointIndex++;
    }

    private bool ShouldStop(TreasureMapConfig cfg)
    {
        if (cfg.LoopRoute && !cfg.StopWhenRouteEnds)
            return false;

        return _batchIndex >= cfg.MapBatchCount;
    }

    private static int EffectiveRetry(TreasureMapPoint point, TreasureMapConfig cfg) =>
        Math.Max(0, point.RetryLimit > 0 ? point.RetryLimit : cfg.MaxPointRetry);

    private static ReturnPoint ToReturnPoint(TreasureMapPoint point) => new()
    {
        MapName = point.MapName,
        X = point.X,
        Y = point.Y,
        Label = point.Label,
        Kind = ReturnPointKind.Normal,
        SourceModule = point.SourceModule,
        SourcePath = point.SourcePath
    };

    private static double Distance(NavigationRecognitionResult nav, TreasureMapPoint point)
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
