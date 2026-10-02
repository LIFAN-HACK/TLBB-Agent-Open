using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnNavigationService : IReturnNavigationService
{
    private const string Module = "ReturnNavigation";
    private readonly IInputService _input;
    private readonly ILogService _log;

    public ReturnNavigationService(IInputService input, ILogService log)
    {
        _input = input;
        _log = log;
    }

    public ReturnNavigationResult TryNavigate(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint targetPoint,
        double distance,
        ReturnNavigationConfig config)
    {
        if (config.Mode == ReturnNavigationMode.Disabled)
            return new ReturnNavigationResult(false, "回点导航动作未启用，仅记录距离和目标点。");

        if (RequiresHotkey(config.Mode) && !_input.TryParseHotkey(config.Hotkey, out var error))
            return new ReturnNavigationResult(false, $"回点热键无效：{error}");

        if (RequiresClick(config.Mode) && (config.ClickX < 0 || config.ClickY < 0))
            return new ReturnNavigationResult(false, $"回点点击坐标无效：{config.ClickX},{config.ClickY}");

        if (RequiresDirectionalClick(config.Mode) && !current.HasCoordinate)
            return new ReturnNavigationResult(false, "方向点击需要当前坐标，当前坐标不可用。");

        if (RequiresRecordedSequence(config.Mode))
        {
            for (var i = 0; i < config.RecordedPreHotkeys.Count; i++)
            {
                var hotkey = config.RecordedPreHotkeys[i];
                if (string.IsNullOrWhiteSpace(hotkey.Hotkey))
                    return new ReturnNavigationResult(false, $"录制回点前置热键第 {i + 1} 步为空。");
                if (!_input.TryParseHotkey(hotkey.Hotkey, out var preHotkeyError))
                    return new ReturnNavigationResult(false, $"录制回点前置热键第 {i + 1} 步无效：{preHotkeyError}");
            }

            var validate = ValidateRecordedSequence(window, targetPoint, config);
            if (!validate.Valid)
                return new ReturnNavigationResult(false, validate.Message);
        }

        if (!string.IsNullOrWhiteSpace(config.AfterClickHotkey) && !_input.TryParseHotkey(config.AfterClickHotkey, out var afterClickError))
            return new ReturnNavigationResult(false, $"回点后置热键无效：{afterClickError}");

        if (RequiresHotkey(config.Mode))
        {
            if (!_input.SendKey(window.Hwnd, config.Hotkey, config.DispatchMode))
                return new ReturnNavigationResult(false, $"回点热键发送失败：{config.Hotkey}");

            if (config.OpenMapDelayMs > 0)
                Thread.Sleep(Math.Min(config.OpenMapDelayMs, 1000));
        }

        if (RequiresRecordedSequence(config.Mode))
        {
            var execute = ExecuteRecordedSequence(window, config);
            if (!execute.Executed)
                return execute;
        }

        var click = ResolveClickPoint(window, current, targetPoint, config);
        if (click is not null)
        {
            var repeat = Math.Clamp(config.ClickRepeat, 1, 10);
            for (var i = 0; i < repeat; i++)
            {
                if (!_input.Click(window.Hwnd, click.Value.X, click.Value.Y, config.DispatchMode, config.ClickButton))
                    return new ReturnNavigationResult(false, $"回点点击发送失败：{click.Value.X},{click.Value.Y}，按钮 {config.ClickButton}，第 {i + 1}/{repeat} 次。");

                if (i + 1 < repeat && config.ClickIntervalMs > 0)
                    Thread.Sleep(Math.Clamp(config.ClickIntervalMs, 20, 1000));
            }
        }

        if (!string.IsNullOrWhiteSpace(config.AfterClickHotkey))
        {
            if (config.AfterClickDelayMs > 0)
                Thread.Sleep(Math.Clamp(config.AfterClickDelayMs, 0, 3000));

            if (!_input.SendKey(window.Hwnd, config.AfterClickHotkey, config.DispatchMode))
                return new ReturnNavigationResult(false, $"回点后置热键发送失败：{config.AfterClickHotkey}");
        }

        var clickSuffix = click is null
            ? ""
            : $"，点击={config.ClickButton}x{Math.Clamp(config.ClickRepeat, 1, 10)}";
        var afterSuffix = string.IsNullOrWhiteSpace(config.AfterClickHotkey)
            ? ""
            : $"，后置热键={config.AfterClickHotkey}";
        var message = config.Mode switch
        {
            ReturnNavigationMode.ClickClientPoint =>
                $"执行回点点击：客户区({config.ClickX},{config.ClickY}){clickSuffix}{afterSuffix}，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前距离 {distance:F1}。",
            ReturnNavigationMode.HotkeyOnly =>
                $"执行回点热键：{config.Hotkey}{afterSuffix}，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前距离 {distance:F1}。",
            ReturnNavigationMode.HotkeyThenClickClientPoint =>
                $"执行回点热键+点击：{config.Hotkey} -> 客户区({config.ClickX},{config.ClickY}){clickSuffix}{afterSuffix}，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前距离 {distance:F1}。",
            ReturnNavigationMode.DirectionalClientClick =>
                $"执行方向回点点击：客户区({click?.X},{click?.Y})，中心({config.ClickX},{config.ClickY}){clickSuffix}{afterSuffix}，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前({current.X},{current.Y})，距离 {distance:F1}。",
            ReturnNavigationMode.HotkeyThenDirectionalClientClick =>
                $"执行热键+方向回点点击：{config.Hotkey} -> 客户区({click?.X},{click?.Y})，中心({config.ClickX},{config.ClickY}){clickSuffix}{afterSuffix}，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前({current.X},{current.Y})，距离 {distance:F1}。",
            ReturnNavigationMode.RecordedClientClickSequence =>
                $"执行录制回点点击序列：{config.RecordedClicks.Count} 步，目标 {targetPoint.MapName}({targetPoint.X},{targetPoint.Y})，当前距离 {distance:F1}。",
            _ => "回点导航动作已执行。"
        };

        _log.Info(Module, message, window.Id);
        return new ReturnNavigationResult(true, message);
    }

    private static bool RequiresHotkey(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.HotkeyOnly
            or ReturnNavigationMode.HotkeyThenClickClientPoint
            or ReturnNavigationMode.HotkeyThenDirectionalClientClick;

    private static bool RequiresClick(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.ClickClientPoint
            or ReturnNavigationMode.HotkeyThenClickClientPoint
            or ReturnNavigationMode.DirectionalClientClick
            or ReturnNavigationMode.HotkeyThenDirectionalClientClick;

    private static bool RequiresDirectionalClick(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.DirectionalClientClick or ReturnNavigationMode.HotkeyThenDirectionalClientClick;

    private static bool RequiresRecordedSequence(ReturnNavigationMode mode) =>
        mode is ReturnNavigationMode.RecordedClientClickSequence;

    private static (bool Valid, string Message) ValidateRecordedSequence(
        BoundGameWindow window,
        ReturnPoint targetPoint,
        ReturnNavigationConfig config)
    {
        if (config.RecordedClicks.Count == 0)
            return (false, "录制回点点击序列为空，请先采集一次成功寻路样本。");

        if (!config.RecordedMovedCloserToTarget && !config.RecordedArrivedAtTarget)
            return (false, "录制回点样本没有通过靠近/到达验证，拒绝执行。请重新录制并确认样本 PASS 后再导入。");

        var syntheticSource = FindSyntheticRecordedSource(config);
        if (!string.IsNullOrWhiteSpace(syntheticSource))
            return (false, $"录制回点样本来自合成/Smoke 证据，只能用于回归测试，禁止真实执行：{syntheticSource}");

        if (!string.IsNullOrWhiteSpace(config.RecordedSourceClientSize)
            && !string.Equals(config.RecordedSourceClientSize.Trim(), window.ClientSizeText, StringComparison.OrdinalIgnoreCase))
            return (false, $"录制回点样本窗口尺寸为 {config.RecordedSourceClientSize}，当前窗口为 {window.ClientSizeText}；为避免坐标错位，请重新录制当前分辨率样本。");

        var destination = ValidateRecordedDestination(targetPoint, config);
        if (!destination.Valid)
            return (false, destination.Message);

        for (var i = 0; i < config.RecordedClicks.Count; i++)
        {
            var click = config.RecordedClicks[i];
            if (click.ClientX < 0 || click.ClientY < 0 || click.ClientX >= window.ClientWidth || click.ClientY >= window.ClientHeight)
                return (false, $"录制回点点击第 {i + 1} 步超出当前客户区：{click.ClientX},{click.ClientY}，当前 {window.ClientSizeText}。");
        }

        return (true, "");
    }

    private static string FindSyntheticRecordedSource(ReturnNavigationConfig config)
    {
        var candidates = config.RecordedPreHotkeys
            .Select(h => h.SourceEvidencePath)
            .Concat(config.RecordedClicks.Select(c => c.SourceEvidencePath))
            .Prepend(config.RecordedSourceEvidencePath);

        return candidates.FirstOrDefault(IsSyntheticRecordedSource) ?? "";
    }

    private static bool IsSyntheticRecordedSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var value = path.Replace('\\', '/').ToLowerInvariant();
        return value.Contains("synthetic", StringComparison.Ordinal)
               || value.Contains("smoke", StringComparison.Ordinal)
               || value.Contains("navigation-record-synthetic", StringComparison.Ordinal);
    }

    private static (bool Valid, string Message) ValidateRecordedDestination(
        ReturnPoint targetPoint,
        ReturnNavigationConfig config)
    {
        var recordedMap = NormalizeMapName(config.RecordedAfterMap);
        var targetMap = NormalizeMapName(targetPoint.MapName);
        if (!string.IsNullOrWhiteSpace(recordedMap)
            && !string.IsNullOrWhiteSpace(targetMap)
            && !MapsMatch(recordedMap, targetMap))
            return (false, $"录制回点样本终点地图为 {recordedMap}，当前回点地图为 {targetMap}；拒绝复放不匹配样本。");

        var recordedX = config.RecordedAfterX ?? config.RecordedTargetX;
        var recordedY = config.RecordedAfterY ?? config.RecordedTargetY;
        if (recordedX is not null && recordedY is not null)
        {
            var dx = targetPoint.X - recordedX.Value;
            var dy = targetPoint.Y - recordedY.Value;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > 8)
                return (false, $"录制回点样本终点为 {recordedX},{recordedY}，当前回点为 {targetPoint.X},{targetPoint.Y}，偏差 {distance:F1}；拒绝复放不匹配样本。");
        }

        return (true, "");
    }

    private static string NormalizeMapName(string value)
    {
        var chars = (value ?? string.Empty).Trim()
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c))
            .ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static bool MapsMatch(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
        || left.Contains(right, StringComparison.Ordinal)
        || right.Contains(left, StringComparison.Ordinal);

    private ReturnNavigationResult ExecuteRecordedSequence(BoundGameWindow window, ReturnNavigationConfig config)
    {
        for (var i = 0; i < config.RecordedPreHotkeys.Count; i++)
        {
            var hotkey = config.RecordedPreHotkeys[i];
            if (!_input.SendKey(window.Hwnd, hotkey.Hotkey, config.DispatchMode))
                return new ReturnNavigationResult(false, $"录制回点前置热键第 {i + 1}/{config.RecordedPreHotkeys.Count} 步发送失败：{hotkey.Hotkey}。");

            if (hotkey.DelayAfterMs > 0)
                Thread.Sleep(Math.Clamp(hotkey.DelayAfterMs, 20, 5000));
        }

        for (var i = 0; i < config.RecordedClicks.Count; i++)
        {
            var click = config.RecordedClicks[i];
            if (!_input.Click(window.Hwnd, click.ClientX, click.ClientY, config.DispatchMode, click.Button))
                return new ReturnNavigationResult(false, $"录制回点点击第 {i + 1}/{config.RecordedClicks.Count} 步发送失败：{click.Button} {click.ClientX},{click.ClientY}。");

            if (click.DelayAfterMs > 0 && i + 1 < config.RecordedClicks.Count)
                Thread.Sleep(Math.Clamp(click.DelayAfterMs, 20, 5000));
        }

        var source = config.RecordedPreHotkeys
            .Select(h => h.SourceEvidencePath)
            .Concat(config.RecordedClicks.Select(c => c.SourceEvidencePath))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var sourceSuffix = string.IsNullOrWhiteSpace(source) ? "" : $"，来源={source}";
        var preSuffix = config.RecordedPreHotkeys.Count == 0 ? "" : $"，前置热键 {config.RecordedPreHotkeys.Count} 步";
        return new ReturnNavigationResult(true, $"录制回点点击序列已执行：点击 {config.RecordedClicks.Count} 步{preSuffix}{sourceSuffix}。");
    }

    private static (int X, int Y)? ResolveClickPoint(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint targetPoint,
        ReturnNavigationConfig config)
    {
        if (RequiresRecordedSequence(config.Mode))
            return null;

        if (!RequiresClick(config.Mode))
            return null;

        if (!RequiresDirectionalClick(config.Mode))
            return (config.ClickX, config.ClickY);

        var dx = targetPoint.X - current.X!.Value;
        var dy = targetPoint.Y - current.Y!.Value;
        if (config.DirectionInvertX)
            dx = -dx;
        if (config.DirectionInvertY)
            dy = -dy;

        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
            return (ClampClientX(window, config.ClickX), ClampClientY(window, config.ClickY));

        var radius = Math.Clamp(config.DirectionRadius, 20, Math.Max(20, Math.Min(window.ClientWidth, window.ClientHeight) / 2));
        var x = config.ClickX + (int)Math.Round(dx / length * radius);
        var y = config.ClickY + (int)Math.Round(dy / length * radius);
        return (ClampClientX(window, x), ClampClientY(window, y));
    }

    private static int ClampClientX(BoundGameWindow window, int x) =>
        Math.Clamp(x, 0, Math.Max(0, window.ClientWidth - 1));

    private static int ClampClientY(BoundGameWindow window, int y) =>
        Math.Clamp(y, 0, Math.Max(0, window.ClientHeight - 1));
}
