using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed partial class HorseBanditRecognitionService : IHorseBanditRecognitionService
{
    private readonly IWindowCaptureService _capture;
    private readonly IOpenTextRecognizer _openText;

    public HorseBanditRecognitionService(
        IWindowCaptureService capture,
        IOpenTextRecognizer openText)
    {
        _capture = capture;
        _openText = openText;
    }

    public HorseBanditRecognitionResult Recognize(BoundGameWindow window, HorseBanditRecognitionConfig config)
    {
        if (window.Hwnd == nint.Zero || !_openText.IsAvailable)
            return Empty();

        var team = RecognizeRegion(window, config.TeamStatus, OpenTextSlot.Generic);
        var follow = RecognizeRegion(window, config.FollowStatus, OpenTextSlot.Generic);
        var distanceText = RecognizeRegion(window, config.DistanceText, OpenTextSlot.CoordText);
        var combined = $"{team} {follow} {distanceText}";
        var distance = ParseDistance(distanceText);
        var isLost = ContainsAny(combined, config.LostFollowKeywords);
        var isFollowing = !isLost && ContainsAny(combined, config.FollowOkKeywords);
        return new HorseBanditRecognitionResult(team, follow, distanceText, distance, isFollowing, isLost);
    }

    private string RecognizeRegion(BoundGameWindow window, OcrRegionConfig region, OpenTextSlot slot)
    {
        if (!region.Enabled || region.Width <= 0 || region.Height <= 0)
            return string.Empty;

        var capture = _capture.CaptureRegion(window.Hwnd, region.X, region.Y, region.Width, region.Height);
        if (capture is null)
            return string.Empty;

        return NormalizeRecognizedText(_openText.Recognize(capture, slot).Text);
    }

    private static bool ContainsAny(string text, string keywords) =>
        SplitKeywords(keywords).Any(keyword => text.Contains(keyword, StringComparison.Ordinal));

    private static IEnumerable<string> SplitKeywords(string keywords) =>
        keywords
            .Split(new[] { ';', '；', ',', '，', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword));

    private static int? ParseDistance(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = DistanceRegex().Match(text);
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    private static string NormalizeRecognizedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var characters = text
            .Where(character => char.IsLetterOrDigit(character) || character is >= '\u4E00' and <= '\u9FFF')
            .ToArray();
        return characters.Length == 0 ? string.Empty : new string(characters);
    }

    private static HorseBanditRecognitionResult Empty() =>
        new(string.Empty, string.Empty, string.Empty, null, false, false);

    [GeneratedRegex(@"\d{1,4}", RegexOptions.CultureInvariant)]
    private static partial Regex DistanceRegex();
}
