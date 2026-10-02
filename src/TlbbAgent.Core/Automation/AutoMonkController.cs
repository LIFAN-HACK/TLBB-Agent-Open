using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class AutoMonkController : AutomationControllerBase<AutoMonkState>, IAutoMonkController
{
    private readonly IAutoMonkConfigService _config;
    private readonly IGlobalConfigService _global;
    private readonly INavigationRecognitionService _navigation;
    private readonly IPerceptionHub _perception;
    private readonly IWindowCaptureService _capture;
    private readonly IOpenTextRecognizer _openText;
    private readonly IReturnNavigationService _routeNavigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private int _routeIndex;
    private int _routeRetryCount;
    private DateTimeOffset _lastRouteAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSearchAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttackAction = DateTimeOffset.MinValue;

    public AutoMonkController(
        ILogService log,
        IAutoMonkConfigService config,
        IGlobalConfigService global,
        INavigationRecognitionService navigation,
        IPerceptionHub perception,
        IWindowCaptureService capture,
        IOpenTextRecognizer openText,
        IReturnNavigationService routeNavigation,
        IInputService input,
        IStateMachineEngine engine)
        : base(AutoMonkState.Idle, log)
    {
        _config = config;
        _global = global;
        _navigation = navigation;
        _perception = perception;
        _capture = capture;
        _openText = openText;
        _routeNavigation = routeNavigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "act.monk";
    public override string Name => "自动和尚";
    public override string Group => "活动";

    protected override void OnStart()
    {
        Machine.Goto(AutoMonkState.ValidateConfig);
        var cfg = _config.Current;
        if (cfg.RoutePoints.Count == 0)
        {
            RunState = RunState.Error;
            Machine.Goto(AutoMonkState.Recover);
            Log.Warn(Name, "未配置和尚路线坐标，无法启动自动和尚。", Target?.Id ?? "-");
            RaiseStateChanged();
            return;
        }

        var maps = cfg.RoutePoints.Select(p => p.MapName).Distinct(StringComparer.Ordinal).Count();
        Log.Info(Name, $"自动和尚已就绪：路线点 {cfg.RoutePoints.Count} 个。", Target!.Id);
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
                Machine.Goto(AutoMonkState.Recover);
                Log.Warn(Name, "自动和尚状态机丢失绑定窗口。");
                RaiseStateChanged();
                break;
            }

            var cfg = _config.Current;
            if (cfg.StopWhenActivityEnds && IsActivityEnded(window, cfg))
            {
                Log.Warn(Name, "检测到和尚活动结束提示，自动和尚停止。", window.Id);
                RunState = RunState.Stopped;
                Machine.Goto(AutoMonkState.Stop);
                RaiseStateChanged();
                break;
            }

            Machine.Goto(AutoMonkState.DetectTarget);
            var combat = VisionCombatResultMapper.FromSnapshot(
                _perception.Read(window, _global.Current, includeOpenText: true));
            if (IsMonkTarget(combat, cfg))
            {
                EngageTarget(window, combat, cfg);
                await Delay(cfg.BattleCheckIntervalMs, token).ConfigureAwait(false);
                continue;
            }

            if (cfg.EnableRouteNavigation)
            {
                Machine.Goto(AutoMonkState.RecognizePosition);
                var nav = _navigation.Recognize(window, cfg.NavigationRecognition);
                if (nav.HasCoordinate)
                    NavigateRoute(window, nav, cfg);
                else
                    Recover(window, cfg, "未识别当前坐标，无法执行和尚路线。");
            }

            TrySearch(window, cfg);
            await Delay(cfg.ScanIntervalMs, token).ConfigureAwait(false);
        }

        if (!token.IsCancellationRequested && RunState == RunState.Stopped)
            Machine.Goto(AutoMonkState.Stop);

        RaiseStateChanged();
    }

    private bool IsActivityEnded(BoundGameWindow window, AutoMonkConfig cfg)
    {
        Machine.Goto(AutoMonkState.DetectActivityEnd);
        var region = cfg.ActivityStatusRegion;
        if (!region.Enabled || region.Width <= 0 || region.Height <= 0)
            return false;

        var capture = _capture.CaptureRegion(window.Hwnd, region.X, region.Y, region.Width, region.Height);
        if (capture is null)
            return false;

        if (!_openText.IsAvailable)
            return false;

        var text = _openText.Recognize(capture, OpenTextSlot.Toast).Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var keyword in SplitKeywords(cfg.ActivityEndKeywords))
        {
            if (!text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            Log.Warn(Name, "检测到活动结束提示。", window.Id);
            return true;
        }

        return false;
    }

    private void NavigateRoute(BoundGameWindow window, NavigationRecognitionResult current, AutoMonkConfig cfg)
    {
        if (cfg.RoutePoints.Count == 0)
            return;

        var target = SelectRoutePoint(current, cfg);
        var distance = Distance(current.X!.Value, current.Y!.Value, target.X, target.Y);
        if (distance <= cfg.ArrivalDistance)
        {
            _routeRetryCount = 0;
            _routeIndex = (_routeIndex + 1) % cfg.RoutePoints.Count;
            Machine.Goto(AutoMonkState.DetectTarget);
            Log.Info(Name, $"已到达和尚路线点：{target.MapName}({target.X},{target.Y})，切换到下一个点。", window.Id);
            return;
        }

        Machine.Goto(AutoMonkState.NavigateRoute);
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
            Log.Info(Name, $"执行和尚路线动作：{result.Message}", window.Id);
        }
        else
        {
            Log.Warn(Name, $"和尚路线动作未执行：{result.Message}", window.Id);
        }

        if (_routeRetryCount > Math.Max(0, cfg.RouteRetryLimit) && cfg.EnableRecovery)
        {
            Recover(window, cfg, $"路线动作重试超过 {cfg.RouteRetryLimit} 次。");
            _routeRetryCount = 0;
        }
    }

    private AutoMonkRoutePoint SelectRoutePoint(NavigationRecognitionResult current, AutoMonkConfig cfg)
    {
        var sameMap = current.HasMap
            ? cfg.RoutePoints.Where(p => p.MapName.Contains(current.MapNameText, StringComparison.Ordinal)
                                         || current.MapNameText.Contains(p.MapName, StringComparison.Ordinal)).ToList()
            : new List<AutoMonkRoutePoint>();

        if (sameMap.Count > 0)
            return sameMap.OrderBy(p => Distance(current.X!.Value, current.Y!.Value, p.X, p.Y)).First();

        if (_routeIndex >= cfg.RoutePoints.Count)
            _routeIndex = 0;
        return cfg.RoutePoints[_routeIndex];
    }

    private void TrySearch(BoundGameWindow window, AutoMonkConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.SearchHotkey))
            return;

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastSearchAction).TotalMilliseconds < Math.Max(100, cfg.ScanIntervalMs))
            return;

        if (!_input.TryParseHotkey(cfg.SearchHotkey, out var error))
        {
            Log.Warn(Name, $"自动和尚搜索快捷键无效：{cfg.SearchHotkey} | {error}", window.Id);
            return;
        }

        _lastSearchAction = now;
        _input.SendKey(window.Hwnd, cfg.SearchHotkey);
        Log.Trace(Name, $"发送和尚搜索/选怪快捷键：{cfg.SearchHotkey}", window.Id);
    }

    private void EngageTarget(BoundGameWindow window, CombatRecognitionResult combat, AutoMonkConfig cfg)
    {
        Machine.Goto(AutoMonkState.EngageTarget);
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastAttackAction).TotalMilliseconds < Math.Max(100, cfg.BattleCheckIntervalMs))
            return;

        if (!_input.TryParseHotkey(cfg.AttackHotkey, out var error))
        {
            Log.Warn(Name, $"自动和尚攻击快捷键无效：{cfg.AttackHotkey} | {error}", window.Id);
            Machine.Goto(AutoMonkState.Recover);
            return;
        }

        _lastAttackAction = now;
        _input.SendKey(window.Hwnd, cfg.AttackHotkey);
        Log.Info(Name, "识别到目标，执行攻击。", window.Id);
        Machine.Goto(AutoMonkState.WaitBattle);
    }

    private void Recover(BoundGameWindow window, AutoMonkConfig cfg, string reason)
    {
        Machine.Goto(AutoMonkState.Recover);
        Log.Warn(Name, $"自动和尚异常恢复：{reason}", window.Id);

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
            Log.Warn(Name, $"已执行自动和尚恢复动作：{result.Message}", window.Id);
    }

    private static bool IsMonkTarget(CombatRecognitionResult combat, AutoMonkConfig cfg)
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
