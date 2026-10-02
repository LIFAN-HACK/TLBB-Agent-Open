using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnPointRuntimeService : IReturnPointRuntimeService
{
    private const string Module = "ReturnPoint";
    private const int RequiredStableCoordinateReads = 3;
    private const int MaxCredibleCoordinateStepDelta = 20;
    private static readonly TimeSpan CoordinateStabilityWindow = TimeSpan.FromSeconds(8);

    private readonly IReturnPointService _returnPoints;
    private readonly ISceneStateService _sceneState;
    private readonly IDeathRecognitionService _deathRecognition;
    private readonly IReturnNavigationService _returnNavigation;
    private readonly ILogService _log;

    private DateTimeOffset _lastMissingNavigationLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMissingMapLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNoMatchingPointLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCoordinateStabilityLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastInRangeLog = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReturnAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEarthSpiritAction = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDeathReturnAction = DateTimeOffset.MinValue;
    private DateTimeOffset _stuckRecoveryHoldUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _coordinateCandidateAt = DateTimeOffset.MinValue;
    private NavigationRecognitionResult? _coordinateCandidate;
    private int _coordinateStableReadCount;
    private NavigationRecognitionResult? _lastMovementSample;
    private DateTimeOffset _lastMovementChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _returnProgressHoldUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReturnProgressLog = DateTimeOffset.MinValue;
    private ReturnPoint? _activeReturnTarget;
    private ReturnPointRuntimeAction _activeReturnAction = ReturnPointRuntimeAction.None;
    private double? _bestReturnDistance;
    private int _returnAttemptCount;
    private bool _emptyConfigWarned;

    public ReturnPointRuntimeService(
        IReturnPointService returnPoints,
        ISceneStateService sceneState,
        IDeathRecognitionService deathRecognition,
        IReturnNavigationService returnNavigation,
        ILogService log)
    {
        _returnPoints = returnPoints;
        _sceneState = sceneState;
        _deathRecognition = deathRecognition;
        _returnNavigation = returnNavigation;
        _log = log;
    }

    public bool HasEnabledReturnPoint => IsAnyReturnEnabled(_returnPoints.Current);

    public void Reset()
    {
        _lastMissingNavigationLog = DateTimeOffset.MinValue;
        _lastMissingMapLog = DateTimeOffset.MinValue;
        _lastNoMatchingPointLog = DateTimeOffset.MinValue;
        _lastCoordinateStabilityLog = DateTimeOffset.MinValue;
        _lastInRangeLog = DateTimeOffset.MinValue;
        _lastReturnAction = DateTimeOffset.MinValue;
        _lastEarthSpiritAction = DateTimeOffset.MinValue;
        _lastDeathReturnAction = DateTimeOffset.MinValue;
        _stuckRecoveryHoldUntil = DateTimeOffset.MinValue;
        _coordinateCandidateAt = DateTimeOffset.MinValue;
        _coordinateCandidate = null;
        _coordinateStableReadCount = 0;
        _lastMovementSample = null;
        _lastMovementChangedAt = DateTimeOffset.MinValue;
        ResetReturnProgress();
        _returnAttemptCount = 0;
        _emptyConfigWarned = false;
    }

    public ReturnPointRuntimeResult Evaluate(BoundGameWindow window)
    {
        if (DateTimeOffset.UtcNow < _stuckRecoveryHoldUntil)
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.StuckRecovery, true, "卡点恢复动作冷却中。");

        var config = _returnPoints.Current;
        if (!IsAnyReturnEnabled(config))
            return None("返点未启用。");

        var death = TryHandleDeathReturn(window, config);
        if (death.Action != ReturnPointRuntimeAction.None)
            return death;

        if (config.Points.Count == 0)
        {
            WarnEmptyConfigOnce(window);
            return None("未配置返点坐标。");
        }

        if (!HasNavigationReturnEnabled(config))
            return None("仅死亡返点启用，当前未检测到死亡。");

        var navigation = _sceneState.Recognize(window, config.NavigationRecognition);
        if (!navigation.HasCoordinate)
        {
            var reason = BuildSceneGateReason(navigation);
            var pending = HoldReturnProgressIfNeeded(window, config, reason);
            if (pending is not null)
                return pending;

            LogMissingNavigation(window, reason);
            ResetCoordinateStability();
            ResetMovementTracker();
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.MissingNavigation, false, reason);
        }

        if (!HasReliableRuntimeMap(navigation))
        {
            var reason = BuildSceneGateReason(navigation);
            var pending = HoldReturnProgressIfNeeded(window, config, reason);
            if (pending is not null)
                return pending;

            LogMissingMap(window, navigation, reason);
            ResetCoordinateStability();
            ResetMovementTracker();
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.MissingNavigation, false, reason);
        }

        if (!IsCoordinateStable(navigation, out var stabilityReason))
        {
            var pending = HoldReturnProgressIfNeeded(window, config, stabilityReason);
            if (pending is not null)
                return pending;

            LogCoordinateStability(window, stabilityReason);
            ResetMovementTracker();
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.MissingNavigation, false, stabilityReason);
        }

        var nearest = FindNearestPoint(config, navigation);
        if (nearest is null)
        {
            var reason = $"当前地图 {navigation.MapNameText} 没有可用返点坐标，禁止跨地图回点。";
            LogNoMatchingPoint(window, reason);
            ResetMovementTracker();
            ResetReturnProgress();
            return None(reason);
        }

        var distance = Distance(navigation.X!.Value, navigation.Y!.Value, nearest.X, nearest.Y);
        if (distance <= config.ArrivalDistance)
        {
            _returnAttemptCount = 0;
            ResetMovementTracker();
            ResetReturnProgress();
            LogInRange(window, nearest, distance);
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.InRange, false, $"当前位置在返点范围内，距离 {distance:F1}。");
        }

        _log.Info(
            Module,
            $"当前坐标 {navigation.MapNameText}({navigation.X},{navigation.Y}) 距离返点 {nearest.MapName}({nearest.X},{nearest.Y}) 约 {distance:F1}，进入返点判断。",
            window.Id);

        var stuck = TryHandleStuckRecovery(window, navigation, nearest, distance, config, "坐标长时间未变化");
        if (stuck.Action != ReturnPointRuntimeAction.None)
            return stuck;

        var pendingReturn = HoldReturnProgressIfNeeded(window, config, navigation, nearest, distance);
        if (pendingReturn is not null)
            return pendingReturn;

        var earth = TryExecuteEarthSpiritReturn(window, navigation, nearest, distance, config);
        if (earth.Action != ReturnPointRuntimeAction.None)
            return earth;

        return TryExecuteReturnNavigation(window, navigation, nearest, distance, config);
    }

    private ReturnPointRuntimeResult TryHandleDeathReturn(BoundGameWindow window, ReturnPointConfig config)
    {
        if (!config.EnableDeathReturn)
            return None("死亡返点未启用。");

        var death = _deathRecognition.Recognize(window, config.DeathRecognition);
        if (!death.IsDead)
            return None("未检测到死亡提示。");

        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.DeathReturnNavigation.ActionIntervalMs);
        if ((now - _lastDeathReturnAction).TotalMilliseconds < intervalMs)
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.DeathReturn, true, "死亡返点动作冷却中。");

        _lastDeathReturnAction = now;
        var target = FindDeathReturnTarget(config);
        if (config.Points.Count == 0)
            _log.Warn(Module, "检测到死亡提示，但未配置死亡回点坐标；将只执行已配置的死亡返回动作。", window.Id);

        var result = _returnNavigation.TryNavigate(
            window,
            new NavigationRecognitionResult(string.Empty, death.Text, null, null),
            target,
            0,
            config.DeathReturnNavigation);

        if (result.Executed)
            _log.Warn(Module, $"检测到死亡关键词 {death.MatchedKeyword}，已执行死亡返点动作：{result.Message}", window.Id);
        else
            _log.Warn(Module, $"检测到死亡关键词 {death.MatchedKeyword}，但死亡返点动作未执行：{result.Message}", window.Id);

        ResetCoordinateStability();
        ResetMovementTracker();
        return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.DeathReturn, result.Executed, result.Message);
    }

    private ReturnPointRuntimeResult TryExecuteEarthSpiritReturn(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance,
        ReturnPointConfig config)
    {
        if (!config.EnableEarthSpiritReturn)
            return None("土灵返点未启用。");

        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.EarthSpiritNavigation.ActionIntervalMs);
        if ((now - _lastEarthSpiritAction).TotalMilliseconds < intervalMs)
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.EarthSpiritReturn, true, "土灵返点动作冷却中。");

        _lastEarthSpiritAction = now;
        var target = FindPreferredPoint(config, ReturnPointKind.EarthSpirit) ?? nearest;
        var result = _returnNavigation.TryNavigate(window, current, target, distance, config.EarthSpiritNavigation);
        if (result.Executed)
        {
            _returnAttemptCount++;
            BeginReturnProgress(target, distance, ReturnPointRuntimeAction.EarthSpiritReturn, config);
            _log.Info(Module, $"已执行土灵返点动作：{result.Message}", window.Id);
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.EarthSpiritReturn, true, result.Message);
        }

        return None(result.Message);
    }

    private ReturnPointRuntimeResult TryExecuteReturnNavigation(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance,
        ReturnPointConfig config)
    {
        if (!config.EnableFixedFarmReturn)
            return None("普通返点未启用。");

        var now = DateTimeOffset.UtcNow;
        var intervalMs = Math.Max(200, config.ReturnNavigation.ActionIntervalMs);
        if ((now - _lastReturnAction).TotalMilliseconds < intervalMs)
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.ReturnToPoint, true, "普通返点动作冷却中。");

        _lastReturnAction = now;
        var result = _returnNavigation.TryNavigate(window, current, nearest, distance, config.ReturnNavigation);
        if (!result.Executed)
            return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.ReturnToPoint, false, result.Message);

        _returnAttemptCount++;
        BeginReturnProgress(nearest, distance, ReturnPointRuntimeAction.ReturnToPoint, config);
        if (_returnAttemptCount >= Math.Max(1, config.ReturnRetryLimit))
            return TryHandleStuckRecovery(window, current, nearest, distance, config, "多次返点后仍未到达");

        return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.ReturnToPoint, true, result.Message);
    }

    private ReturnPointRuntimeResult TryHandleStuckRecovery(
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
            return None("卡点恢复未启用。");
        }

        var timedOut = IsMovementStuck(current, Math.Max(1000, config.StuckTimeoutMs));
        var forcedByRetry = reason.Contains("仍未到达", StringComparison.Ordinal);
        if (!timedOut && !forcedByRetry)
            return None("未达到卡点恢复条件。");

        _stuckRecoveryHoldUntil = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(500, config.CheckIntervalMs * 2));
        var result = _returnNavigation.TryNavigate(window, current, nearest, distance, config.StuckRecoveryNavigation);
        if (result.Executed)
        {
            BeginReturnProgress(nearest, distance, ReturnPointRuntimeAction.StuckRecovery, config);
            _log.Warn(Module, $"{reason}，已执行卡点恢复动作：{result.Message}", window.Id);
        }
        else
            _log.Warn(Module, $"{reason}，进入卡点恢复但恢复动作未执行：{result.Message}", window.Id);

        ResetMovementTracker(current);
        return new ReturnPointRuntimeResult(ReturnPointRuntimeAction.StuckRecovery, result.Executed, result.Message);
    }

    private bool IsCoordinateStable(NavigationRecognitionResult current, out string reason)
    {
        reason = string.Empty;
        if (!HasReliableRuntimeMap(current) || !current.HasCoordinate)
        {
            ResetCoordinateStability();
            reason = "地图或坐标未识别。";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_coordinateCandidate is null
            || !SameMap(_coordinateCandidate, current)
            || IsCoordinateJumpTooLarge(_coordinateCandidate, current)
            || now - _coordinateCandidateAt > CoordinateStabilityWindow)
        {
            _coordinateCandidate = current;
            _coordinateCandidateAt = now;
            _coordinateStableReadCount = 1;
            reason = $"坐标候选初始化：{current.MapNameText}({current.X},{current.Y})，等待稳定。";
            return false;
        }

        if (SamePosition(_coordinateCandidate, current))
            _coordinateStableReadCount++;
        else
        {
            _coordinateCandidate = current;
            _coordinateCandidateAt = now;
            _coordinateStableReadCount = 1;
            reason = $"坐标从候选切换为 {current.MapNameText}({current.X},{current.Y})，等待稳定。";
            return false;
        }

        if (_coordinateStableReadCount < RequiredStableCoordinateReads)
        {
            reason = $"坐标稳定读数 {_coordinateStableReadCount}/{RequiredStableCoordinateReads}，暂不触发回点。";
            return false;
        }

        return true;
    }

    private void ResetCoordinateStability()
    {
        _coordinateCandidate = null;
        _coordinateCandidateAt = DateTimeOffset.MinValue;
        _coordinateStableReadCount = 0;
    }

    private bool IsMovementStuck(NavigationRecognitionResult current, int stuckTimeoutMs)
    {
        if (!current.HasCoordinate)
        {
            ResetMovementTracker();
            return false;
        }

        if (_lastMovementSample is null)
        {
            UpdateMovementTracker(current);
            return false;
        }

        if (!SamePosition(_lastMovementSample, current))
        {
            UpdateMovementTracker(current);
            return false;
        }

        return (DateTimeOffset.UtcNow - _lastMovementChangedAt).TotalMilliseconds >= stuckTimeoutMs;
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

    private void BeginReturnProgress(
        ReturnPoint target,
        double distance,
        ReturnPointRuntimeAction action,
        ReturnPointConfig config)
    {
        _activeReturnTarget = target;
        _activeReturnAction = action;
        _bestReturnDistance = distance;
        _returnProgressHoldUntil = DateTimeOffset.UtcNow.AddMilliseconds(ReturnTravelWaitMs(config));
    }

    private ReturnPointRuntimeResult? HoldReturnProgressIfNeeded(
        BoundGameWindow window,
        ReturnPointConfig config,
        string reason)
    {
        if (!IsReturnProgressPending())
            return null;

        var now = DateTimeOffset.UtcNow;
        if (now >= _returnProgressHoldUntil)
        {
            ResetReturnProgress();
            return null;
        }

        LogReturnProgressIfDue(window, $"回点动作已执行，等待地图/坐标稳定：{reason}");
        return new ReturnPointRuntimeResult(
            ActiveReturnActionOrDefault(),
            true,
            $"回点动作已执行，等待地图/坐标稳定：{reason}");
    }

    private ReturnPointRuntimeResult? HoldReturnProgressIfNeeded(
        BoundGameWindow window,
        ReturnPointConfig config,
        NavigationRecognitionResult current,
        ReturnPoint nearest,
        double distance)
    {
        if (!IsReturnProgressPending())
            return null;

        if (!IsSameReturnTarget(_activeReturnTarget, nearest))
        {
            ResetReturnProgress();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_bestReturnDistance is null || distance < _bestReturnDistance.Value - 0.5)
        {
            _bestReturnDistance = distance;
            _returnProgressHoldUntil = now.AddMilliseconds(ReturnTravelWaitMs(config));
        }

        if (now >= _returnProgressHoldUntil)
        {
            ResetReturnProgress();
            return null;
        }

        var message = $"回点进行中：当前 {current.MapNameText}({current.X},{current.Y})，目标 {nearest.MapName}({nearest.X},{nearest.Y})，距离 {distance:F1}，继续暂停打怪。";
        LogReturnProgressIfDue(window, message);
        return new ReturnPointRuntimeResult(ActiveReturnActionOrDefault(), true, message);
    }

    private bool IsReturnProgressPending() =>
        _activeReturnTarget is not null
        && _activeReturnAction != ReturnPointRuntimeAction.None
        && _returnProgressHoldUntil > DateTimeOffset.UtcNow;

    private ReturnPointRuntimeAction ActiveReturnActionOrDefault() =>
        _activeReturnAction == ReturnPointRuntimeAction.None
            ? ReturnPointRuntimeAction.ReturnToPoint
            : _activeReturnAction;

    private void ResetReturnProgress()
    {
        _returnProgressHoldUntil = DateTimeOffset.MinValue;
        _lastReturnProgressLog = DateTimeOffset.MinValue;
        _activeReturnTarget = null;
        _activeReturnAction = ReturnPointRuntimeAction.None;
        _bestReturnDistance = null;
    }

    private void LogReturnProgressIfDue(BoundGameWindow window, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastReturnProgressLog).TotalSeconds < 2)
            return;

        _lastReturnProgressLog = now;
        _log.Info(Module, message, window.Id);
    }

    private static bool IsSameReturnTarget(ReturnPoint? left, ReturnPoint right) =>
        left is not null
        && string.Equals(left.MapName, right.MapName, StringComparison.Ordinal)
        && left.X == right.X
        && left.Y == right.Y
        && left.Kind == right.Kind;

    private static int ReturnTravelWaitMs(ReturnPointConfig config) =>
        Math.Clamp(
            config.ReturnTravelWaitMs <= 0 ? 8000 : config.ReturnTravelWaitMs,
            Math.Max(500, config.CheckIntervalMs),
            60000);

    private static bool IsAnyReturnEnabled(ReturnPointConfig config) =>
        config.EnableReturnPoint
        && (config.EnableFixedFarmReturn
            || config.EnableEarthSpiritReturn
            || config.EnableDeathReturn
            || config.EnableStuckRecovery);

    private static bool HasNavigationReturnEnabled(ReturnPointConfig config) =>
        config.EnableReturnPoint
        && (config.EnableFixedFarmReturn
            || config.EnableEarthSpiritReturn
            || config.EnableStuckRecovery);

    private static ReturnPointRuntimeResult None(string message) =>
        new(ReturnPointRuntimeAction.None, false, message);

    private void WarnEmptyConfigOnce(BoundGameWindow window)
    {
        if (_emptyConfigWarned)
            return;

        _emptyConfigWarned = true;
        _log.Warn(Module, "返点能力已启用，但未配置返点坐标；普通挂机将继续运行，不执行返点动作。", window.Id);
    }

    private ReturnPoint? FindNearestPoint(ReturnPointConfig config, NavigationRecognitionResult navigation)
    {
        if (!HasReliableRuntimeMap(navigation) || !navigation.HasCoordinate)
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

    private static ReturnPoint? FindPreferredPoint(ReturnPointConfig config, ReturnPointKind kind) =>
        config.Points.FirstOrDefault(p => p.Kind == kind) ?? config.Points.FirstOrDefault();

    private static ReturnPoint FindDeathReturnTarget(ReturnPointConfig config) =>
        FindPreferredPoint(config, ReturnPointKind.DeathReturn)
        ?? new ReturnPoint
        {
            MapName = "死亡返回",
            X = 0,
            Y = 0,
            Label = "无坐标死亡返回动作",
            Kind = ReturnPointKind.DeathReturn,
            SourceModule = "runtime",
            SourcePath = "death-return-without-point"
        };

    private void LogMissingNavigation(BoundGameWindow window, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMissingNavigationLog).TotalSeconds < 3)
            return;

        _lastMissingNavigationLog = now;
        _log.Warn(Module, $"{reason} 请在数据页或返点页校准识别区域。", window.Id);
    }

    private void LogMissingMap(BoundGameWindow window, NavigationRecognitionResult navigation, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastMissingMapLog).TotalSeconds < 3)
            return;

        _lastMissingMapLog = now;
        _log.Warn(Module, $"{reason} 当前坐标文本：{navigation.CoordinateText}。", window.Id);
    }

    private void LogNoMatchingPoint(BoundGameWindow window, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastNoMatchingPointLog).TotalSeconds < 5)
            return;

        _lastNoMatchingPointLog = now;
        _log.Warn(Module, reason, window.Id);
    }

    private void LogCoordinateStability(BoundGameWindow window, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastCoordinateStabilityLog).TotalSeconds < 2)
            return;

        _lastCoordinateStabilityLog = now;
        _log.Info(Module, reason, window.Id);
    }

    private void LogInRange(BoundGameWindow window, ReturnPoint nearest, double distance)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastInRangeLog).TotalSeconds < 3)
            return;

        _lastInRangeLog = now;
        _log.Info(Module, $"当前位置已在返点范围内：{nearest.MapName}({nearest.X},{nearest.Y})，距离 {distance:F1}。", window.Id);
    }

    private static string BuildSceneGateReason(NavigationRecognitionResult navigation)
    {
        if (!navigation.HasMap && !navigation.HasCoordinate)
            return "地图和坐标均未实时识别，禁止触发自动回点。";

        if (!navigation.HasMap)
            return "已识别坐标但未实时识别地图名；为避免跨地图误回点，禁止触发自动回点。";

        if (!HasReliableRuntimeMap(navigation))
            return $"地图来源未授权自动回点({navigation.MapNameText}/{navigation.MapNameSource})；为避免跨地图误回点，禁止触发自动回点。";

        return "已实时识别地图但未识别坐标，禁止触发自动回点。";
    }

    private static bool HasReliableRuntimeMap(NavigationRecognitionResult navigation) =>
        navigation.HasMap
        && !string.Equals(navigation.MapNameSource, "manual-confirmed-map", StringComparison.OrdinalIgnoreCase)
        && !navigation.MapNameSource.Contains("route-locked", StringComparison.OrdinalIgnoreCase);

    private static bool SamePosition(NavigationRecognitionResult left, NavigationRecognitionResult right) =>
        left.X == right.X
        && left.Y == right.Y
        && SameMap(left, right);

    private static bool SameMap(NavigationRecognitionResult left, NavigationRecognitionResult right) =>
        string.Equals(left.MapNameText, right.MapNameText, StringComparison.Ordinal);

    private static bool IsCoordinateJumpTooLarge(NavigationRecognitionResult left, NavigationRecognitionResult right)
    {
        if (!left.HasCoordinate || !right.HasCoordinate)
            return true;

        return Math.Abs(left.X!.Value - right.X!.Value) > MaxCredibleCoordinateStepDelta
               || Math.Abs(left.Y!.Value - right.Y!.Value) > MaxCredibleCoordinateStepDelta;
    }

    private static double Distance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
