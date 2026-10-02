using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnPointCaptureService : IReturnPointCaptureService
{
    private const string Module = "ReturnPoint";
    private const int CommitStabilitySamples = 3;
    private const int CommitStabilityIntervalMs = 250;
    private const int MaxCommitCoordinateSpread = 3;

    private readonly IWindowBindingService _binding;
    private readonly INavigationRecognitionService _navigation;
    private readonly IReturnPointService _returnPoints;
    private readonly ILogService _log;

    public ReturnPointCaptureService(
        IWindowBindingService binding,
        INavigationRecognitionService navigation,
        IReturnPointService returnPoints,
        ILogService log)
    {
        _binding = binding;
        _navigation = navigation;
        _returnPoints = returnPoints;
        _log = log;
    }

    public ReturnPointCaptureResult PreviewCurrentPoint()
    {
        var window = _binding.Current;
        if (window is null)
            return Fail("未绑定游戏窗口，无法读取当前坐标。", null);

        if (!window.IsAutomationAllowed)
            return Fail("当前窗口暂不可用于坐标读取。", null, window.Id);

        var navigation = _navigation.Recognize(window, _returnPoints.Current.NavigationRecognition);
        if (!navigation.HasMap && !navigation.HasCoordinate)
            return Fail("未识别到当前地图和坐标，请先校准识别区域。", navigation, window.Id);

        if (!navigation.HasMap)
            return Fail($"已识别坐标 {navigation.CoordinateText}，但未识别地图名；禁止录入返点坐标。", navigation, window.Id);

        if (!navigation.HasCoordinate)
            return Fail($"已识别地图 {navigation.MapNameText}，但未识别当前坐标；请先校准坐标区域。", navigation, window.Id);

        if (IsManualConfirmedMap(navigation))
            return Fail($"已识别坐标 {navigation.CoordinateText}，但当前地图仅来自手动确认({navigation.MapNameText})；只读预览不生成可录入点，请先采集或导入地图名样本。", navigation, window.Id);

        var mapName = SanitizeMapName(navigation.MapNameText);
        if (string.IsNullOrWhiteSpace(mapName))
            return Fail($"当前地图名无效：{navigation.MapNameText}。", navigation, window.Id);

        var point = new ReturnPoint
        {
            MapName = mapName,
            X = navigation.X!.Value,
            Y = navigation.Y!.Value,
            Label = $"{mapName}-{navigation.X},{navigation.Y}",
            Kind = ReturnPointKind.FixedFarm,
            SourceModule = "只读预览",
            SourcePath = ""
        };

        var message = $"只读识别成功：{point.MapName} {point.X},{point.Y}。";
        _log.Info(Module, message, window.Id);
        return new ReturnPointCaptureResult(true, message, point, navigation);
    }

    public ReturnPointCaptureResult CaptureAndAddCurrentPoint(ReturnPointKind kind, string label)
    {
        var window = _binding.Current;
        if (window is null)
            return Fail("未绑定游戏窗口，无法读取当前坐标。", null);

        if (!window.IsAutomationAllowed)
            return Fail("当前窗口暂不可用于坐标读取。", null, window.Id);

        if (!TryReadStableNavigationForCommit(window, out var navigation, out var failure))
        {
            return Fail(failure, navigation, window.Id);
        }

        var mapName = SanitizeMapName(navigation.MapNameText);
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return Fail("当前地图名无效，禁止录入返点坐标。", navigation, window.Id);
        }

        var point = new ReturnPoint
        {
            MapName = mapName,
            X = navigation.X!.Value,
            Y = navigation.Y!.Value,
            Label = string.IsNullOrWhiteSpace(label) ? $"{mapName}-{navigation.X},{navigation.Y}" : label.Trim(),
            Kind = kind,
            SourceModule = "当前窗口",
            SourcePath = ""
        };

        _returnPoints.AddPoint(point);
        var message = $"已录入坐标：{point.MapName} {point.X},{point.Y}。";
        _log.Info(Module, message, window.Id);
        return new ReturnPointCaptureResult(true, message, point, navigation);
    }

    private bool TryReadStableNavigationForCommit(BoundGameWindow window, out NavigationRecognitionResult navigation, out string failure)
    {
        var samples = new List<NavigationRecognitionResult>();
        for (var i = 0; i < CommitStabilitySamples; i++)
        {
            var current = _navigation.Recognize(window, _returnPoints.Current.NavigationRecognition);
            samples.Add(current);
            if (i < CommitStabilitySamples - 1)
                Thread.Sleep(CommitStabilityIntervalMs);
        }

        navigation = samples.LastOrDefault() ?? new NavigationRecognitionResult("", "", null, null);
        failure = string.Empty;

        var missingMap = samples.FirstOrDefault(s => !s.HasMap);
        if (missingMap is not null)
        {
            navigation = missingMap;
            failure = "未稳定识别到当前地图名，禁止录入返点坐标，避免跨地图误回点。";
            return false;
        }

        var manualMap = samples.FirstOrDefault(s => IsManualConfirmedMap(s));
        if (manualMap is not null)
        {
            navigation = manualMap;
            failure = $"当前地图仅来自手动确认({manualMap.MapNameText})，不能写入自动回点坐标；请先采集或导入地图名样本。";
            return false;
        }

        var missingCoordinate = samples.FirstOrDefault(s => !s.HasCoordinate);
        if (missingCoordinate is not null)
        {
            navigation = missingCoordinate;
            failure = "未稳定识别到当前坐标，请先校准坐标区域。";
            return false;
        }

        var first = samples[0];
        var differentMap = samples.FirstOrDefault(s => !string.Equals(s.MapNameText, first.MapNameText, StringComparison.Ordinal));
        if (differentMap is not null)
        {
            navigation = differentMap;
            failure = $"连续采样地图不一致：{string.Join(" -> ", samples.Select(s => SanitizeMapName(s.MapNameText)).Distinct(StringComparer.Ordinal))}，禁止写入回点坐标。";
            return false;
        }

        var minX = samples.Min(s => s.X!.Value);
        var maxX = samples.Max(s => s.X!.Value);
        var minY = samples.Min(s => s.Y!.Value);
        var maxY = samples.Max(s => s.Y!.Value);
        if (maxX - minX > MaxCommitCoordinateSpread || maxY - minY > MaxCommitCoordinateSpread)
        {
            failure = $"连续采样坐标不稳定：X {minX}-{maxX}, Y {minY}-{maxY}，请角色停稳后再采集。";
            return false;
        }

        var avgX = (int)Math.Round(samples.Average(s => s.X!.Value));
        var avgY = (int)Math.Round(samples.Average(s => s.Y!.Value));
        navigation = first with
        {
            CoordinateText = $"{avgX},{avgY}",
            X = avgX,
            Y = avgY
        };
        return true;
    }

    private static bool IsManualConfirmedMap(NavigationRecognitionResult navigation) =>
        string.Equals(navigation.MapNameSource, "manual-confirmed-map", StringComparison.OrdinalIgnoreCase);

    private ReturnPointCaptureResult Fail(string message, NavigationRecognitionResult? navigation, string windowId = "-")
    {
        _log.Warn(Module, message, windowId);
        return new ReturnPointCaptureResult(false, message, null, navigation);
    }

    private static string SanitizeMapName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = new string(text.Trim()
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsDigit(c))
            .ToArray());

        return cleaned.Length >= 2 ? cleaned : string.Empty;
    }
}
