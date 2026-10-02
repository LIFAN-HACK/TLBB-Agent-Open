using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class ReturnPointController : AutomationControllerBase<ReturnPointState>, IReturnPointController
{
    private readonly IReturnPointService _returnPoints;
    private readonly INavigationRecognitionService _navigation;
    private readonly IDeathRecognitionService _deathRecognition;
    private readonly IReturnNavigationService _returnNavigation;
    private readonly IStateMachineEngine _engine;
    private IDisposable? _runner;
    private DateTimeOffset _lastMissingNavigationLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInRangeLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReturnAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEarthSpiritAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeathReturnAction = DateTimeOffset.MinValue;
    private NavigationRecognitionResult? _lastMovementSample;
    private DateTimeOffset _lastMovementChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _stuckRecoveryHoldUntil = DateTimeOffset.MinValue;
    private int _returnAttemptCount;

    public ReturnPointController(
        ILogService log,
        IReturnPointService returnPoints,
        INavigationRecognitionService navigation,
        IDeathRecognitionService deathRecognition,
        IReturnNavigationService returnNavigation,
        IStateMachineEngine engine)
        : base(ReturnPointState.Idle, log)
    {
        _returnPoints = returnPoints;
        _navigation = navigation;
        _deathRecognition = deathRecognition;
        _returnNavigation = returnNavigation;
        _engine = engine;
    }

    public override string Key => "hang.return";
    public override string Name => "挂机返点";
    public override string Group => "挂机";

    protected override void OnStart()
    {
        // Feature retired until rebuild; keep controller key for catalog/UI compatibility.
        RunState = RunState.Error;
        Machine.Goto(ReturnPointState.ErrorRecovery);
        Log.Warn(Name, "挂机返点逻辑已下线，等待重新实现。当前不会执行返点或打断打怪。", Target?.Id ?? "-");
        RaiseStateChanged();
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
                Machine.Goto(ReturnPointState.ErrorRecovery);
                Log.Warn(Name, "返点状态机丢失绑定窗口，已进入异常恢复。");
                RaiseStateChanged();
                break;
            }

            EvaluateReturnPoint(Target);

            var delay = Math.Max(200, _returnPoints.Current.CheckIntervalMs);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EvaluateReturnPoint(BoundGameWindow window)
    {
        if (Machine.Current == ReturnPointState.StuckRecovery && DateTimeOffset.UtcNow < _stuckRecoveryHoldUntil)
            return;

        var config = _returnPoints.Current;
        if (TryHandleDeathReturn(window, config))
            return;

        var navigation = _navigation.Recognize(window, config.NavigationRecognition);
        if (!navigation.HasCoordinate)
        {
            LogMissingNavigation(window);
            Machine.Goto(ReturnPointState.WaitForReturnCondition);
            return;
        }

        var nearest = FindNearestPoint(config, navigation);
        if (nearest is null)
        {
            Log.Warn(Name, "当前返点配置没有可匹配坐标。", window.Id);
            Machine.Goto(ReturnPointState.ErrorRecovery);
            return;
        }

        var distance = Distance(navigation.X!.Value, navigation.Y!.Value, nearest.X, nearest.Y);
        if (distance <= config.ArrivalDistance)
        {
            _returnAttemptCount = 0;
            ResetMovementTracker();
            Machine.Goto(ReturnPointState.WaitForReturnCondition);
            LogInRange(window, nearest, distance);
            return;
        }

        Machine.Goto(ReturnPointState.ReturnToPoint);
        Log.Info(
            Name,
            $"当前坐标 {navigation.MapNameText}({navigation.X},{navigation.Y}) 距离返点 {nearest.MapName}({nearest.X},{nearest.Y}) 约 {distance:F1}，进入返点流程。",
            window.Id);

        if (TryHandleStuckRecovery(window, navigation, nearest, distance, config, reason: "坐标长时间未变化"))
            return;

        if (TryExecuteEarthSpiritReturn(window, navigation, nearest, distance, config))
            return;

        TryExecuteReturnNavigation(window, navigation, nearest, distance, config);
    }

    private bool TryExecuteEarthSpiritReturn(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance,
        ReturnPointConfig config)
    {
        if (!config.EnableEarthSpiritReturn)
            return false;

        Machine.Goto(ReturnPointState.EarthSpiritReturn);
        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.EarthSpiritNavigation.ActionIntervalMs);
        if ((now - _lastEarthSpiritAction).TotalMilliseconds < intervalMs)
            return true;

        _lastEarthSpiritAction = now;
        var target = FindPreferredPoint(config, ReturnPointKind.EarthSpirit) ?? nearest;
        var result = _returnNavigation.TryNavigate(window, current, target, distance, config.EarthSpiritNavigation);
        if (result.Executed)
        {
            _returnAttemptCount++;
            Log.Info(Name, $"已执行土灵返点动作：{result.Message}", window.Id);
            return true;
        }

        Log.Warn(Name, $"土灵返点已启用但动作未执行：{result.Message}；回落到普通返点动作。", window.Id);
        return false;
    }

    private bool TryHandleDeathReturn(BoundGameWindow window, ReturnPointConfig config)
    {
        if (!config.EnableDeathReturn)
            return false;

        var death = _deathRecognition.Recognize(window, config.DeathRecognition);
        if (!death.IsDead)
            return false;

        Machine.Goto(ReturnPointState.DeathReturn);
        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.DeathReturnNavigation.ActionIntervalMs);
        if ((now - _lastDeathReturnAction).TotalMilliseconds < intervalMs)
            return true;

        _lastDeathReturnAction = now;
        var target = FindPreferredPoint(config, ReturnPointKind.DeathReturn) ?? config.Points.First();
        var result = _returnNavigation.TryNavigate(
            window,
            new NavigationRecognitionResult(string.Empty, death.Text, null, null),
            target,
            0,
            config.DeathReturnNavigation);

        if (result.Executed)
            Log.Warn(Name, $"检测到死亡提示“{death.MatchedKeyword}”，已执行死亡返点动作：{result.Message}", window.Id);
        else
            Log.Warn(Name, $"检测到死亡提示“{death.MatchedKeyword}”，但死亡返点动作未执行：{result.Message}", window.Id);

        ResetMovementTracker();
        return true;
    }

    private static ReturnPoint? FindPreferredPoint(ReturnPointConfig config, ReturnPointKind kind) =>
        config.Points.FirstOrDefault(p => p.Kind == kind) ?? config.Points.FirstOrDefault();

    private void TryExecuteReturnNavigation(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance,
        ReturnPointConfig config)
    {
        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.ReturnNavigation.ActionIntervalMs);
        if ((now - _lastReturnAction).TotalMilliseconds < intervalMs)
            return;

        _lastReturnAction = now;
        var result = _returnNavigation.TryNavigate(window, current, nearest, distance, config.ReturnNavigation);
        if (!result.Executed)
        {
            Log.Warn(Name, result.Message, window.Id);
            return;
        }

        _returnAttemptCount++;
        var retryLimit = Math.Max(1, config.ReturnRetryLimit);
        if (_returnAttemptCount < retryLimit)
            return;

        if (!config.EnableStuckRecovery)
        {
            _returnAttemptCount = 0;
            return;
        }

        _ = TryHandleStuckRecovery(window, current, nearest, distance, config, reason: $"返点动作已执行 {_returnAttemptCount} 次仍未到达");
        _returnAttemptCount = 0;
    }

    private bool TryHandleStuckRecovery(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance,
        ReturnPointConfig config,
        string reason)
    {
        if (!config.EnableStuckRecovery)
        {
            UpdateMovementTracker(current);
            return false;
        }

        var timedOut = IsMovementStuck(current, Math.Max(1000, config.StuckTimeoutMs));
        var forcedByRetry = reason.Contains("仍未到达", StringComparison.Ordinal);
        if (!timedOut && !forcedByRetry)
            return false;

        Machine.Goto(ReturnPointState.StuckRecovery);
        _stuckRecoveryHoldUntil = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(500, config.CheckIntervalMs * 2));
        var result = _returnNavigation.TryNavigate(window, current, nearest, distance, config.StuckRecoveryNavigation);
        if (result.Executed)
            Log.Warn(Name, $"{reason}，已执行卡点恢复动作：{result.Message}", window.Id);
        else
            Log.Warn(Name, $"{reason}，进入卡点恢复但恢复动作未执行：{result.Message}", window.Id);

        ResetMovementTracker(current);
        return true;
    }

    private bool IsMovementStuck(NavigationRecognitionResult current, int stuckTimeoutMs)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastMovementSample is null)
        {
            _lastMovementSample = current;
            _lastMovementChangedAt = now;
            return false;
        }

        if (!SamePosition(_lastMovementSample, current))
        {
            _lastMovementSample = current;
            _lastMovementChangedAt = now;
            return false;
        }

        return (now - _lastMovementChangedAt).TotalMilliseconds >= stuckTimeoutMs;
    }

    private void UpdateMovementTracker(NavigationRecognitionResult current)
    {
        _lastMovementSample = current;
        _lastMovementChangedAt = DateTimeOffset.UtcNow;
    }

    private void ResetMovementTracker(NavigationRecognitionResult? current = null)
    {
        _lastMovementSample = current;
        _lastMovementChangedAt = current is null ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow;
    }

    private static bool SamePosition(NavigationRecognitionResult left, NavigationRecognitionResult right) =>
        left.X == right.X
        && left.Y == right.Y
        && string.Equals(left.MapNameText, right.MapNameText, StringComparison.Ordinal);

    private ReturnPoint? FindNearestPoint(ReturnPointConfig config, NavigationRecognitionResult navigation)
    {
        if (!navigation.HasMap || !navigation.HasCoordinate)
            return null;

        var sameMap = config.Points
            .Where(p => p.MapName.Contains(navigation.MapNameText, StringComparison.Ordinal)
                        || navigation.MapNameText.Contains(p.MapName, StringComparison.Ordinal))
            .ToList();
        if (sameMap.Count == 0)
            return null;

        return sameMap
            .OrderBy(p => Distance(navigation.X!.Value, navigation.Y!.Value, p.X, p.Y))
            .FirstOrDefault();
    }

    private void LogMissingNavigation(BoundGameWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMissingNavigationLog).TotalSeconds < 3)
            return;

        _lastMissingNavigationLog = now;
        Log.Warn(Name, "未识别当前坐标，请在数据页或返点页校准。", window.Id);
    }

    private void LogInRange(BoundGameWindow window, ReturnPoint nearest, double distance)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastInRangeLog).TotalSeconds < 3)
            return;

        _lastInRangeLog = now;
        Log.Info(Name, $"当前位置已在返点范围内：{nearest.MapName}({nearest.X},{nearest.Y})，距离 {distance:F1}。", window.Id);
    }

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
