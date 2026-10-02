using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class AutoRebelController : AutomationControllerBase<AutoRebelState>, IAutoRebelController
{
    private readonly IAutoRebelConfigService _config;
    private readonly IGlobalConfigService _global;
    private readonly INavigationRecognitionService _navigation;
    private readonly IPerceptionHub _perception;
    private readonly IReturnNavigationService _routeNavigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _routeIndex;
    private int _routeRetryCount;
    private DateTimeOffset _lastRouteAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSearchAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttackAction = DateTimeOffset.MinValue;

    public AutoRebelController(
        ILogService log,
        IAutoRebelConfigService config,
        IGlobalConfigService global,
        INavigationRecognitionService navigation,
        IPerceptionHub perception,
        IReturnNavigationService routeNavigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(AutoRebelState.Idle, log)
    {
        _config = config;
        _global = global;
        _navigation = navigation;
        _perception = perception;
        _routeNavigation = routeNavigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "act.rebel";
    public override string Name => "自动反贼";
    public override string Group => "活动";

    protected override void OnStart()
    {
        Machine.Goto(AutoRebelState.ValidateConfig);
        var cfg = _config.Current;
        if (cfg.RoutePoints.Count == 0)
        {
            RunState = RunState.Error;
            Machine.Goto(AutoRebelState.Recover);
            Log.Warn(Name, "未配置反贼路线坐标，无法启动自动反贼。", Target?.Id ?? "-");
            RaiseStateChanged();
            return;
        }

        var maps = cfg.RoutePoints.Select(p => p.MapName).Distinct(StringComparer.Ordinal).Count();
        Log.Info(Name, $"自动反贼已就绪：路线点 {cfg.RoutePoints.Count} 个。", Target!.Id);
        _routeIndex = 0;
        _routeRetryCount = 0;
        _lastRouteAction = DateTimeOffset.MinValue;
        _lastSearchAction = DateTimeOffset.MinValue;
        _lastAttackAction = DateTimeOffset.MinValue;
        _runner?.Dispose();
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
        while (!token.IsCancellationRequested && RunState == RunState.Running)
        {
            var window = Target;
            if (window is null)
            {
                RunState = RunState.Error;
                Machine.Goto(AutoRebelState.Recover);
                Log.Warn(Name, "自动反贼状态机丢失绑定窗口。");
                RaiseStateChanged();
                break;
            }

            var cfg = _config.Current;
            Machine.Goto(AutoRebelState.DetectTarget);
            var combat = VisionCombatResultMapper.FromSnapshot(
                _perception.Read(window, _global.Current, includeOpenText: true));
            if (IsRebelTarget(combat, cfg))
            {
                EngageTarget(window, combat, cfg);
                await Delay(cfg.BattleCheckIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            if (cfg.EnableRouteNavigation)
            {
                Machine.Goto(AutoRebelState.RecognizePosition);
                var nav = _navigation.Recognize(window, cfg.NavigationRecognition);
                if (nav.HasCoordinate)
                    NavigateRoute(window, nav, cfg);
                else
                    Recover(window, cfg, "未识别当前坐标，无法执行反贼路线。");
            }

            TrySearch(window, cfg);
            await Delay(cfg.ScanIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(AutoRebelState.Stop);

        RaiseStateChanged();
    }

    private void NavigateRoute(BoundGameWindow window, NavigationRecognitionResult current, AutoRebelConfig cfg)
    {
        if (cfg.RoutePoints.Count == 0)
            return;

        var target = SelectRoutePoint(current, cfg);
        var distance = Distance(current.X!.Value, current.Y!.Value, target.X, target.Y);
        if (distance <= cfg.ArrivalDistance)
        {
            _routeRetryCount = 0;
            _routeIndex = (_routeIndex + 1) % cfg.RoutePoints.Count;
            Machine.Goto(AutoRebelState.DetectTarget);
            Log.Info(Name, $"已到达反贼路线点：{target.MapName}({target.X},{target.Y})，切换到下一个点。", window.Id);
            return;
        }

        Machine.Goto(AutoRebelState.NavigateRoute);
        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, cfg.RouteNavigation.ActionIntervalMs);
        if ((now - _lastRouteAction).TotalMilliseconds < intervalMs)
            return;

        _lastRouteAction = now;
        var pseudoPoint = new ReturnPoint
        {
            MapName = target.MapName,
            X = target.X,
            Y = target.Y,
            Label = target.Label,
            SourceModule = target.SourceModule,
            SourcePath = target.SourcePath
        };
        var result = _routeNavigation.TryNavigate(window, current, pseudoPoint, distance, cfg.RouteNavigation);
        if (result.Executed)
        {
            _routeRetryCount++;
            Log.Info(Name, $"执行反贼路线动作：{result.Message}", window.Id);
        }
        else
        {
            Log.Warn(Name, $"反贼路线动作未执行：{result.Message}", window.Id);
        }

        if (_routeRetryCount > Math.Max(0, cfg.RouteRetryLimit) && cfg.EnableRecovery)
        {
            Recover(window, cfg, $"路线动作重试超过 {cfg.RouteRetryLimit} 次。");
            _routeRetryCount = 0;
        }
    }

    private AutoRebelRoutePoint SelectRoutePoint(NavigationRecognitionResult current, AutoRebelConfig cfg)
    {
        var sameMap = current.HasMap
            ? cfg.RoutePoints.Where(p => p.MapName.Contains(current.MapNameText, StringComparison.Ordinal)
                                         || current.MapNameText.Contains(p.MapName, StringComparison.Ordinal)).ToList()
            : new List<AutoRebelRoutePoint>();

        if (sameMap.Count > 0)
            return sameMap.OrderBy(p => Distance(current.X!.Value, current.Y!.Value, p.X, p.Y)).First();

        if (_routeIndex >= cfg.RoutePoints.Count)
            _routeIndex = 0;
        return cfg.RoutePoints[_routeIndex];
    }

    private void TrySearch(BoundGameWindow window, AutoRebelConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.SearchHotkey))
            return;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastSearchAction).TotalMilliseconds < Math.Max(100, cfg.ScanIntervalMs))
            return;

        if (!_input.TryParseHotkey(cfg.SearchHotkey, out var error))
        {
            Log.Warn(Name, $"自动反贼搜索快捷键无效：{cfg.SearchHotkey} | {error}", window.Id);
            return;
        }

        _lastSearchAction = now;
        _input.SendKey(window.Hwnd, cfg.SearchHotkey);
        Log.Trace(Name, $"发送反贼搜索/选怪快捷键：{cfg.SearchHotkey}", window.Id);
    }

    private void EngageTarget(BoundGameWindow window, CombatRecognitionResult combat, AutoRebelConfig cfg)
    {
        Machine.Goto(AutoRebelState.EngageTarget);
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastAttackAction).TotalMilliseconds < Math.Max(100, cfg.BattleCheckIntervalMs))
            return;

        if (!_input.TryParseHotkey(cfg.AttackHotkey, out var error))
        {
            Log.Warn(Name, $"自动反贼攻击快捷键无效：{cfg.AttackHotkey} | {error}", window.Id);
            Machine.Goto(AutoRebelState.Recover);
            return;
        }

        _lastAttackAction = now;
        _input.SendKey(window.Hwnd, cfg.AttackHotkey);
        Log.Info(Name, "识别到目标，执行攻击。", window.Id);
        Machine.Goto(AutoRebelState.WaitBattle);
    }

    private void Recover(BoundGameWindow window, AutoRebelConfig cfg, string reason)
    {
        Machine.Goto(AutoRebelState.Recover);
        Log.Warn(Name, $"自动反贼异常恢复：{reason}", window.Id);

        if (cfg.RecoveryNavigation.Mode == ReturnNavigationMode.Disabled)
            return;

        var target = cfg.RoutePoints.FirstOrDefault();
        if (target is null)
            return;

        var pseudoPoint = new ReturnPoint { MapName = target.MapName, X = target.X, Y = target.Y, Label = target.Label };
        var result = _routeNavigation.TryNavigate(
            window,
            new NavigationRecognitionResult(string.Empty, string.Empty, null, null),
            pseudoPoint,
            0,
            cfg.RecoveryNavigation);
        if (result.Executed)
            Log.Warn(Name, $"已执行自动反贼恢复动作：{result.Message}", window.Id);
    }

    private static bool IsRebelTarget(CombatRecognitionResult combat, AutoRebelConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(combat.TargetNameText))
            return false;

        return SplitKeywords(cfg.TargetKeywords)
            .Any(k => combat.TargetNameText.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitKeywords(string text) =>
        text.Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s));

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static Task Delay(int milliseconds, CancellationToken token) =>
        Task.Delay(Math.Max(50, milliseconds), token);
}
