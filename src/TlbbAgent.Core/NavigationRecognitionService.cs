using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class NavigationRecognitionService : INavigationRecognitionService
{
    private const string Module = "NavigationRecognition";
    private readonly IPerceptionHub? _perception;
    private readonly IGlobalConfigService? _global;
    private readonly ILogService _log;

    public NavigationRecognitionService(
        IWindowCaptureService capture,
        IOpenTextRecognizer openText,
        ILogService log,
        IPerceptionHub? perception = null,
        IGlobalConfigService? global = null)
    {
        _perception = perception;
        _global = global;
        _log = log;
    }

    public NavigationRecognitionResult Recognize(BoundGameWindow window, NavigationRecognitionConfig config)
    {
        if (window.Hwnd == nint.Zero || _perception is null || _global is null)
            return Empty();

        var minimap = _perception.Read(window, _global.Current).Minimap;
        var mapPolicy = minimap.MapNamePolicy;
        var mapPolicyStable = mapPolicy is
        {
            PolicyAvailable: true,
            Stable: true
        };
        var map = mapPolicyStable
            ? NormalizeMapName(mapPolicy.StableMapName)
            : "";
        var mapSource = string.IsNullOrWhiteSpace(map)
            ? ""
            : mapPolicy.RuntimeAuthorized
                ? $"vision-v2/minimap-map-policy/{mapPolicy.CandidateSource}"
                : $"vision-v2/minimap-map-policy-route-locked/{mapPolicy.CandidateSource}";
        var mapConfidence = string.IsNullOrWhiteSpace(map)
            ? 0
            : Math.Clamp((int)Math.Round(mapPolicy.Confidence * 100), 0, 100);
        if (string.IsNullOrWhiteSpace(map))
        {
            map = NormalizeMapName(config.ConfirmedMapName);
            if (!string.IsNullOrWhiteSpace(map))
            {
                mapSource = "manual-confirmed-map";
                mapConfidence = 85;
            }
        }

        var coordinateText = minimap.X is not null && minimap.Y is not null
            ? $"{minimap.X},{minimap.Y}"
            : "";
        var coordinateSource = string.IsNullOrWhiteSpace(coordinateText)
            ? ""
            : $"vision-v2/{minimap.CoordinateEngine}";
        var coordinateConfidence = string.IsNullOrWhiteSpace(coordinateText)
            ? 0
            : Math.Clamp((int)Math.Round(minimap.Confidence * 100), 0, 100);
        var result = new NavigationRecognitionResult(
            map,
            coordinateText,
            minimap.X,
            minimap.Y,
            mapSource,
            mapConfidence,
            coordinateSource,
            coordinateConfidence);
        _log.Trace(
            Module,
            result.HasCoordinate
                ? $"Coordinate confirmed: {coordinateText}."
                : "V2 minimap coordinate is unknown.",
            window.Id);
        return result;
    }

    private static NavigationRecognitionResult Empty() =>
        new(string.Empty, string.Empty, null, null);

    private static string NormalizeMapName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var chars = text
            .Where(character => character is >= '\u4E00' and <= '\u9FFF' || char.IsDigit(character))
            .ToArray();
        return chars.Length < 2 ? string.Empty : new string(chars);
    }
}
