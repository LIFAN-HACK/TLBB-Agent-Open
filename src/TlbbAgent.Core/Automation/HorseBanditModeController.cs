using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class HorseBanditModeController : AutomationControllerBase<HorseBanditState>, IHorseBanditModeController
{
    private readonly IHorseBanditConfigService _horseBandit;
    private readonly IGlobalConfigService _global;
    private readonly IHorseBanditRecognitionService _recognition;
    private readonly IPerceptionHub _perception;
    private readonly ITargetSafetyService _targetSafety;
    private readonly INavigationRecognitionService _navigation;
    private readonly IReturnNavigationService _routeNavigation;
    private readonly IInputService _input;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private DateTimeOffset _lastSearchAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttackAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFollowAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPatrolAction = DateTimeOffset.MinValue;
    private DateTimeOffset _followRecoveryHoldUntil = DateTimeOffset.MinValue;
    private int _routeIndex;

    public HorseBanditModeController(
        ILogService log,
        IHorseBanditConfigService horseBandit,
        IGlobalConfigService global,
        IHorseBanditRecognitionService recognition,
        IPerceptionHub perception,
        INavigationRecognitionService navigation,
        IReturnNavigationService routeNavigation,
        IInputService input,
        IStateMachineEngine engine,
        ITargetSafetyService? targetSafety = null)
        : base(HorseBanditState.Idle, log)
    {
        _horseBandit = horseBandit;
        _global = global;
        _recognition = recognition;
        _perception = perception;
        _targetSafety = targetSafety ?? new TargetSafetyService();
        _navigation = navigation;
        _routeNavigation = routeNavigation;
        _input = input;
        _engine = engine;
    }

    public override string Key => "hang.horse";
    public override string Name => "马贼模式";
    public override string Group => "挂机";

    protected override void OnStart()
    {
        var config = _horseBandit.Current;
        Machine.Goto(HorseBanditState.BindWindow);
        Log.Info(Name, $"马贼已就绪：路线点 {config.RoutePoints.Count} 个。", Target!.Id);

        if (config.EnableAutoFollow)
            Log.Info(Name, $"自动跟随已启用：掉队距离={config.LostFollowDistance}，转圈拉跟随={config.CirclePullFollowCount} 次。", Target.Id);

        _lastSearchAction = DateTimeOffset.MinValue;
        _lastAttackAction = DateTimeOffset.MinValue;
        _lastFollowAction = DateTimeOffset.MinValue;
        _lastPatrolAction = DateTimeOffset.MinValue;
        _followRecoveryHoldUntil = DateTimeOffset.MinValue;
        _routeIndex = 0;
        _runner?.Dispose();
        _runner = _engine.Run(Name, RunLoopAsync, MarkFaulted);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (RunState == RunState.Paused)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (RunState != RunState.Running)
                break;

            if (Target is null)
            {
                RunState = RunState.Error;
                Machine.Goto(HorseBanditState.Recover);
                Log.Warn(Name, "马贼状态机丢失绑定窗口，已进入恢复。");
                RaiseStateChanged();
                break;
            }

            Evaluate(Target);
            await Task.Delay(Math.Max(200, _horseBandit.Current.ScanIntervalMs), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Evaluate(BoundGameWindow window)
    {
        if (Machine.Current == HorseBanditState.ReturnToFollow && DateTimeOffset.UtcNow < _followRecoveryHoldUntil)
            return;

        var config = _horseBandit.Current;

        if (Machine.Current == HorseBanditState.BindWindow)
            Machine.Goto(HorseBanditState.CheckClientReady);

        if (Machine.Current == HorseBanditState.CheckClientReady)
        {
            if (!window.IsAutomationAllowed)
            {
                RunState = RunState.Error;
                Machine.Goto(HorseBanditState.Recover);
                Log.Warn(Name, window.AutomationBlockReason, window.Id);
                RaiseStateChanged();
                return;
            }

            Machine.Goto(HorseBanditState.CheckTeamState);
        }

        if (Machine.Current == HorseBanditState.CheckTeamState)
        {
            if (config.EnableTeamCheck)
            {
                var team = _recognition.Recognize(window, config.Recognition);
                Log.Trace(Name, "队伍检查完成。", window.Id);
                if (ShouldRecoverFollow(team, config))
                {
                    RecoverFollow(window, config, team);
                    return;
                }
            }

            Machine.Goto(config.EnableAutoFollow ? HorseBanditState.FollowLeader : HorseBanditState.SearchHorseBandit);
        }

        if (Machine.Current == HorseBanditState.FollowLeader)
        {
            var follow = _recognition.Recognize(window, config.Recognition);
            if (ShouldRecoverFollow(follow, config))
            {
                RecoverFollow(window, config, follow);
                return;
            }

            TryFollowLeader(window, config);
            Machine.Goto(HorseBanditState.SearchHorseBandit);
        }

        var result = VisionCombatResultMapper.FromSnapshot(
            _perception.Read(window, _global.Current, includeOpenText: true));
        var targetDecision = _targetSafety.Evaluate(
            _global.Current,
            result,
            new TargetSafetyOptions(IncludeStrategyFilters: false));
        if (!targetDecision.Allowed && targetDecision.IsProtectedTarget)
        {
            Machine.Goto(HorseBanditState.SearchHorseBandit);
            var label = string.IsNullOrWhiteSpace(targetDecision.WarningLabel) ? targetDecision.Reason : targetDecision.WarningLabel;
            var detail = string.IsNullOrWhiteSpace(targetDecision.WarningDetail) ? targetDecision.Reason : targetDecision.WarningDetail;
            Log.Warn(Name, $"{label}：{detail}，马贼模式已跳过攻击并重新找目标。", window.Id);
            TrySearchTarget(window, config, force: true);
            return;
        }

        if (!IsHorseBanditTarget(result.TargetNameText, config))
        {
            Machine.Goto(HorseBanditState.SearchHorseBandit);
            TrySearchTarget(window, config);
            TryPatrolRoute(window, config);
            return;
        }

        Machine.Goto(HorseBanditState.SelectTarget);
                Log.Info(Name, "识别到马贼目标。", window.Id);
        Machine.Goto(HorseBanditState.AttackTarget);
        TryAttackTarget(window, config);
        Machine.Goto(HorseBanditState.WaitBattle);

        if (result.TargetHpPercent is 0)
        {
            Machine.Goto(HorseBanditState.DetectKillOrLostTarget);
            Log.Info(Name, "马贼目标血量为 0，准备寻找下一只。", window.Id);
            Machine.Goto(config.EnableAutoFollow ? HorseBanditState.ReturnToFollow : HorseBanditState.SearchHorseBandit);
        }
    }

    private bool ShouldRecoverFollow(HorseBanditRecognitionResult result, HorseBanditConfig config)
    {
        if (!config.EnableAutoFollow)
            return false;

        if (result.IsLostFollow)
            return true;

        return result.Distance is not null && result.Distance.Value > config.LostFollowDistance;
    }

    private void RecoverFollow(BoundGameWindow window, HorseBanditConfig config, HorseBanditRecognitionResult result)
    {
        Machine.Goto(HorseBanditState.Recover);
        var reason = result.IsLostFollow
            ? "跟随状态异常"
            : $"队长距离 {result.Distance} 超过阈值 {config.LostFollowDistance}";
        Log.Warn(Name, $"{reason}，执行跟随恢复。", window.Id);
        TryFollowLeader(window, config, force: true);
        Machine.Goto(HorseBanditState.ReturnToFollow);
        _followRecoveryHoldUntil = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(500, config.RecoverDelayMs));
    }

    private void TryFollowLeader(BoundGameWindow window, HorseBanditConfig config, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(config.FollowHotkey))
            return;

        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastFollowAction).TotalMilliseconds < Math.Max(500, config.ScanIntervalMs))
            return;

        _lastFollowAction = now;
        _input.SendKey(window.Hwnd, config.FollowHotkey);
        Log.Info(Name, $"执行队员跟随热键：{config.FollowHotkey}", window.Id);
    }

    private void TrySearchTarget(BoundGameWindow window, HorseBanditConfig config, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastSearchAction).TotalMilliseconds < Math.Max(200, config.ScanIntervalMs))
            return;

        _lastSearchAction = now;
        var hotkey = string.IsNullOrWhiteSpace(config.SearchHotkey) ? _global.Current.AttackHotkey : config.SearchHotkey;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "未配置马贼找目标热键，无法主动搜索目标。", window.Id);
            return;
        }

        _input.SendKey(window.Hwnd, hotkey);
        Log.Info(Name, $"搜索马贼目标：发送热键 {hotkey}", window.Id);
    }

    private void TryPatrolRoute(BoundGameWindow window, HorseBanditConfig config)
    {
        if ((!config.UseBuiltInRoutes && !config.UseCustomRoutes) || config.RoutePoints.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var interval = Math.Max(500, config.PatrolNavigation.ActionIntervalMs);
        if ((now - _lastPatrolAction).TotalMilliseconds < interval)
            return;

        _lastPatrolAction = now;
        var current = _navigation.Recognize(window, config.NavigationRecognition);
        var point = SelectNextRoutePoint(config, current);
        if (point is null)
        {
            Log.Warn(Name, "马贼巡逻未找到可用路线点。", window.Id);
            return;
        }

        var target = new ReturnPoint
        {
            MapName = point.MapName,
            X = point.X,
            Y = point.Y,
            Label = "马贼巡逻点",
            Kind = ReturnPointKind.Normal,
            SourceModule = point.SourceModule,
            SourcePath = point.SourcePath
        };
        var distance = current.HasCoordinate ? Distance(current.X!.Value, current.Y!.Value, point.X, point.Y) : 0;
        var result = _routeNavigation.TryNavigate(window, current, target, distance, config.PatrolNavigation);
        if (result.Executed)
            Log.Info(Name, $"马贼巡逻推进：{point.MapName}({point.X},{point.Y})。", window.Id);
        else
            Log.Trace(Name, $"马贼巡逻未执行：{result.Message}", window.Id);
    }

    private HorseBanditRoutePoint? SelectNextRoutePoint(HorseBanditConfig config, NavigationRecognitionResult current)
    {
        var candidates = config.RoutePoints
            .Where(p => !IsAvoidedMap(p.MapName, config.AvoidMaps))
            .ToList();
        if (candidates.Count == 0)
            return null;

        if (current.HasMap)
        {
            var sameMap = candidates
                .Where(p => current.MapNameText.Contains(p.MapName, StringComparison.Ordinal)
                            || p.MapName.Contains(current.MapNameText, StringComparison.Ordinal))
                .ToList();
            if (sameMap.Count > 0)
                candidates = sameMap;
        }

        var point = candidates[_routeIndex % candidates.Count];
        _routeIndex++;
        return point;
    }

    private void TryAttackTarget(BoundGameWindow window, HorseBanditConfig config)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastAttackAction).TotalMilliseconds < Math.Max(200, config.AttackIntervalMs))
            return;

        if (config.Role == HorseBanditRole.Leader && config.LeaderNoAttack)
        {
            Log.Trace(Name, "队长不打怪已启用，跳过攻击动作。", window.Id);
            return;
        }

        _lastAttackAction = now;
        var hotkey = string.IsNullOrWhiteSpace(config.AttackHotkey) ? _global.Current.AttackHotkey : config.AttackHotkey;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "未配置马贼攻击热键，无法攻击目标。", window.Id);
            return;
        }

        _input.SendKey(window.Hwnd, hotkey);
        Log.Info(Name, $"攻击马贼目标：发送热键 {hotkey}", window.Id);
    }

    private static bool IsHorseBanditTarget(string text, HorseBanditConfig config)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return SplitKeywords(config.TargetKeywords)
            .Any(k => text.Contains(k, StringComparison.Ordinal));
    }

    private static bool IsAvoidedMap(string mapName, string avoidMaps)
    {
        if (string.IsNullOrWhiteSpace(mapName) || string.IsNullOrWhiteSpace(avoidMaps))
            return false;

        return SplitKeywords(avoidMaps).Any(mapName.Contains);
    }

    private static IEnumerable<string> SplitKeywords(string value) =>
        value.Split(new[] { ';', ',', '，', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Distance(int ax, int ay, int bx, int by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
