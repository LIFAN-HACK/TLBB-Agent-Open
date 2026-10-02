using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

[Obsolete("Legacy fixed-region OCR is disabled in production. Use IPerceptionHub.")]
public sealed partial class CombatRecognitionService : ICombatRecognitionService, ICombatFrameRecognitionService
{
    private const string Module = "CombatOcr";
    private readonly IWindowCaptureService _capture;
    private readonly ITemplateOcrService _ocr;
    private readonly ILogService _log;
    private readonly IResolutionProfileService? _resolutionProfiles;
    private readonly ITargetProtectionSampleService? _targetSamples;
    private readonly IGlobalConfigService? _global;
    private readonly IRuntimeDataService? _runtimeData;
    private readonly object _dictionaryGate = new();
    private bool _combatDictionariesAttempted;

    public CombatRecognitionService(
        IWindowCaptureService capture,
        ITemplateOcrService ocr,
        ILogService log,
        IResolutionProfileService? resolutionProfiles = null,
        ITargetProtectionSampleService? targetSamples = null,
        IGlobalConfigService? global = null,
        IRuntimeDataService? runtimeData = null)
    {
        _capture = capture;
        _ocr = ocr;
        _log = log;
        _resolutionProfiles = resolutionProfiles;
        _targetSamples = targetSamples;
        _global = global;
        _runtimeData = runtimeData;
    }

    public CombatRecognitionResult Recognize(BoundGameWindow window, CombatRecognitionConfig config)
    {
        if (window.Hwnd == nint.Zero)
        {
            _log.Warn(Module, "窗口无效。", window.Id);
            return Empty();
        }

        var client = _capture.CaptureClient(window.Hwnd);
        if (client is null)
        {
            _log.Trace(Module, "截图为空。", window.Id);
            return Empty();
        }

        return Recognize(window, config, client);
    }

    public CombatRecognitionResult Recognize(
        BoundGameWindow window,
        CombatRecognitionConfig config,
        CaptureResult client)
    {
        if (window.Hwnd == nint.Zero)
            return Empty();

        EnsureCombatDictionariesLoaded();

        var effectiveConfig = _resolutionProfiles?.ResolveCombat(client.Width, client.Height, config) ?? config;

        var targetName = NormalizeTargetName(RecognizeTextRegion(window, client, effectiveConfig.TargetName, "TargetName"));
        var targetHp = RecognizeStatusRegion(window, client, effectiveConfig.TargetHp, "TargetHp");
        var playerHp = RecognizeStatusRegion(window, client, effectiveConfig.PlayerHp, "PlayerHp");
        var playerMp = RecognizeStatusRegion(window, client, effectiveConfig.PlayerMp, "PlayerMp");
        var petHp = RecognizeStatusRegion(window, client, effectiveConfig.PetHp, "PetHp");
        var sampleMatch = _targetSamples?.Match(client, effectiveConfig, _global?.Current.Targeting ?? new TargetingPreset());
        var sampleKind = sampleMatch?.Kind ?? "";
        var sampleIsPlayer = string.Equals(sampleKind, "player", StringComparison.OrdinalIgnoreCase);
        var sampleIsPet = string.Equals(sampleKind, "pet", StringComparison.OrdinalIgnoreCase);
        var sampleIsNonPlayerTarget =
            string.Equals(sampleKind, "monster", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sampleKind, "boss", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sampleKind, "elite", StringComparison.OrdinalIgnoreCase);
        var targetHasSchoolMarker = DetectTargetSchoolMarker(client, effectiveConfig);
        var targetHasPlayerPortrait = DetectTargetPlayerPortrait(client, effectiveConfig);
        var targetHasPlayerStatusBars = DetectTargetPlayerStatusBars(client, effectiveConfig);
        var highConfidencePlayerSample = sampleIsPlayer && sampleMatch?.Score >= 0.975;
        var structuralPlayerTarget = !sampleIsNonPlayerTarget
                                     && !sampleIsPet
                                     && DetectTargetLooksLikePlayer(targetHasSchoolMarker, targetHasPlayerPortrait, targetHasPlayerStatusBars);
        // Structural detection is intentionally diagnostic-only unless a
        // high-confidence player sample confirms it. Some boss/elite target
        // frames use portrait-like ornaments and vertical gold labels, which
        // previously caused hard player-protection false positives.
        var targetLooksLikePlayer = highConfidencePlayerSample;
        var targetProtectionAmbiguous = !targetLooksLikePlayer
                                        && !sampleIsNonPlayerTarget
                                        && !sampleIsPet
                                        && (sampleIsPlayer || structuralPlayerTarget);
        var targetIsAttackable = DetectTargetIsAttackable(
            targetHp.Percent,
            sampleIsNonPlayerTarget,
            highConfidencePlayerSample,
            sampleIsPet,
            targetLooksLikePlayer,
            targetProtectionAmbiguous);

        var result = new CombatRecognitionResult(
            targetName,
            targetHp.Text,
            playerHp.Text,
            playerMp.Text,
            petHp.Text,
            targetHp.Percent,
            playerHp.Percent,
            playerMp.Percent,
            petHp.Percent)
        {
            TargetLooksLikePlayer = targetLooksLikePlayer,
            TargetProtectionAmbiguous = targetProtectionAmbiguous,
            TargetLooksLikePet = sampleIsPet,
            TargetIsAttackable = targetIsAttackable,
            TargetHasSchoolMarker = targetHasSchoolMarker,
            TargetHasPlayerPortrait = targetHasPlayerPortrait,
            TargetHasPlayerStatusBars = targetHasPlayerStatusBars,
            TargetSampleKind = sampleKind,
            TargetSampleLabel = sampleMatch?.Label ?? "",
            TargetSampleSource = sampleMatch?.SourcePath ?? "",
            TargetSampleScore = sampleMatch?.Score ?? 0
        };

        _log.Trace(Module, "识别完成。", window.Id);
        return result;
    }

    private void EnsureCombatDictionariesLoaded()
    {
        if (_combatDictionariesAttempted || _runtimeData is null)
            return;

        lock (_dictionaryGate)
        {
            if (_combatDictionariesAttempted)
                return;

            _combatDictionariesAttempted = true;
            var files = _runtimeData.EnumerateCombatDictionaryFiles();
            if (files.Count == 0)
            {
                _log.Warn(Module, "未找到 UU 数字/核心字库，目标血数字增强不可用。");
                return;
            }

            var loaded = 0;
            foreach (var file in files)
            {
                try
                {
                    _ocr.LoadDictionary(file);
                    loaded++;
                }
                catch (Exception ex)
                {
                    _log.Warn(Module, $"字库加载失败：{Path.GetFileName(file)}，{ex.Message}");
                }
            }

            _log.Info(Module, $"挂机识别字库已加载：{loaded}/{files.Count}，可用字形 {_ocr.RenderableGlyphCount}。");
        }
    }

    private string RecognizeTextRegion(BoundGameWindow window, CaptureResult client, OcrRegionConfig region, string name)
    {
        var capture = CaptureConfiguredRegion(window, client, region, name);
        if (capture is null)
        {
            _log.Trace(Module, "区域为空。", window.Id);
            return string.Empty;
        }

        if (_ocr.RenderableGlyphCount == 0)
            return string.Empty;

        return _ocr.Recognize(capture).Trim();
    }

    private StatusRegionResult RecognizeStatusRegion(BoundGameWindow window, CaptureResult client, OcrRegionConfig region, string name)
    {
        var capture = CaptureConfiguredRegion(window, client, region, name);
        if (capture is null)
        {
            _log.Trace(Module, "区域为空。", window.Id);
            return new StatusRegionResult(string.Empty, null);
        }

        // TargetHp: empty corpse bar (死后空血几秒) must read as 0 so kill-then-switch can fire
        // before the whole target panel disappears.
        var emptyBarAsZero =
            string.Equals(name, "PlayerMp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "TargetHp", StringComparison.OrdinalIgnoreCase);
        var barPercent = TryParseBarPercent(
            capture,
            StatusBarPredicateFor(name),
            emptyBarAsZero: emptyBarAsZero);

        // Digit OCR is expensive and noisy on live bars (false "0" wiped real HP and
        // froze the hang loop). Only use UU number packs to confirm empty/dead TargetHp.
        string ocrText = string.Empty;
        int? ocrPercent = null;
        if (ShouldRunDigitOcr(name, barPercent, emptyBarAsZero) && _ocr.RenderableGlyphCount > 0)
        {
            var digitCapture = CaptureDigitOcrRegion(client, region, name);
            if (digitCapture is not null)
            {
                ocrText = _ocr.Recognize(digitCapture).Trim();
                ocrPercent = TryParseHpReading(ocrText);
            }
        }

        return FuseStatusReading(name, barPercent, ocrText, ocrPercent, emptyBarAsZero);
    }

    /// <summary>
    /// Digit OCR only for TargetHp when bar is already empty/low — confirm death, never
    /// re-score a healthy bar (OCR false zeros previously blocked all combat).
    /// </summary>
    private static bool ShouldRunDigitOcr(string name, int? barPercent, bool emptyBarAsZero)
    {
        if (!string.Equals(name, "TargetHp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!emptyBarAsZero)
            return false;

        // Live bar (>5%): trust color only, skip OCR.
        if (barPercent is > 5)
            return false;

        return true;
    }

    private CaptureResult? CaptureDigitOcrRegion(CaptureResult client, OcrRegionConfig region, string name)
    {
        if (!region.Enabled || region.Width <= 0 || region.Height <= 0)
            return null;

        var resolved = OcrRegionProjection.Resolve(region, client.Width, client.Height);
        if (resolved.IsEmpty)
            return null;

        var expanded = ExpandForDigitOcr(resolved, client.Width, client.Height, name);
        return Crop(client, expanded);
    }

    private static OcrResolvedRegion ExpandForDigitOcr(
        OcrResolvedRegion bar,
        int clientWidth,
        int clientHeight,
        string name)
    {
        // Number dictionaries are ~8px tall; thin status bars need vertical padding.
        var minHeight = name switch
        {
            "TargetHp" or "PlayerHp" or "PetHp" => 14,
            "PlayerMp" or "TargetMp" => 12,
            _ => Math.Max(bar.Height, 10)
        };

        var height = Math.Max(bar.Height, minHeight);
        var padY = Math.Max(0, (height - bar.Height + 1) / 2);
        // Prefer a little extra space above the bar (name/HP text often sits on the fill).
        var y = Math.Max(0, bar.Y - padY - 1);
        if (y + height > clientHeight)
            height = Math.Max(1, clientHeight - y);

        // Slight horizontal pad so leading/trailing digits are not clipped.
        var padX = Math.Min(6, Math.Max(2, bar.Width / 20));
        var x = Math.Max(0, bar.X - padX);
        var width = Math.Min(clientWidth - x, bar.Width + padX * 2);

        return new OcrResolvedRegion(x, y, width, height);
    }

    private static StatusRegionResult FuseStatusReading(
        string name,
        int? barPercent,
        string ocrText,
        int? ocrPercent,
        bool emptyBarAsZero)
    {
        var text = string.IsNullOrWhiteSpace(ocrText) ? string.Empty : ocrText;

        // Live color bar always wins. Never let a stray OCR "0" kill a full bar.
        if (barPercent is > 5)
            return new StatusRegionResult(text, barPercent.Value);

        if (barPercent is not null)
        {
            if (emptyBarAsZero && barPercent.Value <= 5)
            {
                // Empty / residual bar: OCR "0/xxxx" confirms death; OCR silence also → 0.
                // Only override empty-bar→0 when OCR clearly says still healthy (>15%).
                if (ocrPercent is > 15)
                    return new StatusRegionResult(text, ocrPercent.Value);

                return new StatusRegionResult(text, 0);
            }

            return new StatusRegionResult(text, barPercent.Value);
        }

        // No bar fill: only accept strong digit death/ratio readings for TargetHp.
        if (emptyBarAsZero
            && string.Equals(name, "TargetHp", StringComparison.OrdinalIgnoreCase)
            && ocrPercent is not null)
        {
            if (ocrPercent.Value <= 5)
                return new StatusRegionResult(text, 0);
            if (ocrPercent.Value > 15)
                return new StatusRegionResult(text, ocrPercent.Value);
        }

        return new StatusRegionResult(string.Empty, null);
    }

    private CaptureResult? CaptureConfiguredRegion(BoundGameWindow window, CaptureResult client, OcrRegionConfig region, string name)
    {
        if (!region.Enabled)
            return null;

        if (region.Width <= 0 || region.Height <= 0)
        {
            _log.Warn(Module, "区域尺寸无效。", window.Id);
            return null;
        }

        var resolved = OcrRegionProjection.Resolve(region, client.Width, client.Height);
        if (resolved.IsEmpty)
        {
            _log.Warn(Module, "区域超出窗口。", window.Id);
            return null;
        }

        return Crop(client, resolved);
    }

    private static CaptureResult? Crop(CaptureResult src, OcrResolvedRegion region)
    {
        if (region.IsEmpty)
            return null;

        var dst = new byte[region.Width * region.Height * 4];
        var srcStride = src.Width * 4;
        var dstStride = region.Width * 4;
        for (var row = 0; row < region.Height; row++)
            Array.Copy(src.Pixels, (region.Y + row) * srcStride + region.X * 4, dst, row * dstStride, dstStride);
        return new CaptureResult(region.Width, region.Height, dst);
    }

    private static CombatRecognitionResult Empty() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null, null);

    private static int? TryParsePercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var matches = PercentNumberRegex().Matches(text);
        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Value, out var value))
                continue;

            if (value is >= 0 and <= 100)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Parse UU digit-OCR output. Prefer strong forms: "0/12345", "85%".
    /// Bare single digits alone are too noisy on bar chrome and are ignored.
    /// </summary>
    private static int? TryParseHpReading(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Strip common OCR noise around the HP string.
        var cleaned = HpNoiseRegex().Replace(text, "");
        if (cleaned.Length == 0)
            return null;

        var ratio = HpRatioRegex().Match(cleaned);
        if (ratio.Success
            && long.TryParse(ratio.Groups[1].Value, out var current)
            && long.TryParse(ratio.Groups[2].Value, out var max)
            && max > 0)
        {
            if (current <= 0)
                return 0;

            var pct = (int)Math.Round(current * 100.0 / max);
            return Math.Clamp(pct, 1, 100);
        }

        var percentMark = HpPercentMarkRegex().Match(cleaned);
        if (percentMark.Success && int.TryParse(percentMark.Groups[1].Value, out var marked)
            && marked is >= 0 and <= 100)
            return marked;

        // Do NOT trust bare "0"/"50" without '/' or '%': bar chrome often false-matches a single digit.
        return null;
    }

    private static int? TryParseBarPercent(
        CaptureResult image,
        Func<byte, byte, byte, bool> isBarPixel,
        bool emptyBarAsZero = false)
    {
        if (image.Width <= 0 || image.Height <= 0)
            return null;

        var coloredColumns = new bool[image.Width];
        var requiredRows = image.Height <= 4
            ? 1
            : Math.Max(2, image.Height / 2);

        for (var x = 0; x < image.Width; x++)
        {
            var coloredRows = 0;
            for (var y = 0; y < image.Height; y++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];

                if (isBarPixel(r, g, b))
                    coloredRows++;
            }

            coloredColumns[x] = coloredRows >= requiredRows;
        }

        var longestRun = LongestContinuousRun(coloredColumns, allowedGap: 1);
        if (longestRun <= 0)
        {
            // Empty / near-empty target bar (死后空血): treat as 0 when requested.
            if (emptyBarAsZero)
                return LooksLikeEmptyStatusBar(image) || LooksLikeMostlyEmptyBarFrame(image) ? 0 : 0;
            return null;
        }

        var percent = (int)Math.Round(longestRun * 100.0 / image.Width);
        // Tiny residual chrome on an empty corpse bar often maps to 1–5%.
        // For target HP, force those to 0 so kill-switch fires before panel vanishes.
        if (emptyBarAsZero && percent <= 5)
            return 0;

        return Math.Clamp(percent, 1, 100);
    }

    private static bool LooksLikeEmptyStatusBar(CaptureResult image)
    {
        if (image.Width < 20 || image.Height < 2)
            return true;

        var total = image.Width * image.Height;
        var dark = 0;
        var saturated = 0;

        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            var b = image.Pixels[p];
            var g = image.Pixels[p + 1];
            var r = image.Pixels[p + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));

            if (max < 55)
                dark++;
            if (max - min > 35 && max > 65)
                saturated++;
        }

        return dark / (double)total > 0.45
               && saturated / (double)total < 0.06;
    }

    private static bool LooksLikeMostlyEmptyBarFrame(CaptureResult image)
    {
        // Fallback: very few saturated red/green pixels overall.
        if (image.Width <= 0 || image.Height <= 0)
            return true;
        var total = image.Width * image.Height;
        var barish = 0;
        for (var i = 0; i < total; i++)
        {
            var p = i * 4;
            var b = image.Pixels[p];
            var g = image.Pixels[p + 1];
            var r = image.Pixels[p + 2];
            if (IsRedStatusPixel(r, g, b) || IsGreenStatusPixel(r, g, b))
                barish++;
        }

        return barish / (double)total < 0.04;
    }

    private static bool DetectTargetLooksLikePlayer(bool hasSchoolMarker, bool hasPlayerPortrait, bool hasPlayerStatusBars)
    {
        // Player targets can have empty MP/rage bars. The stable structure is
        // school marker + target portrait; bars alone are not enough because
        // boss/elite frames can also expose multiple colored bars.
        if (hasSchoolMarker && hasPlayerPortrait)
            return true;

        return false;
    }

    private static bool? DetectTargetIsAttackable(
        int? targetHpPercent,
        bool sampleIsNonPlayerTarget,
        bool sampleIsPlayer,
        bool sampleIsPet,
        bool targetLooksLikePlayer,
        bool targetProtectionAmbiguous)
    {
        if (sampleIsNonPlayerTarget)
            return true;

        if (sampleIsPlayer || sampleIsPet || targetLooksLikePlayer)
            return false;

        // HP-only must NOT become TargetIsAttackable=true.
        // Noise bars (empty panel / miscalibrated OCR) previously upgraded to a
        // "strong" attackable signal and locked the hang loop on ghost targets.
        // Attackable=true is reserved for sample-backed non-player targets above.
        _ = targetHpPercent;
        _ = targetProtectionAmbiguous;
        return null;
    }

    private static bool DetectTargetPlayerStatusBars(CaptureResult client, CombatRecognitionConfig config)
    {
        var hp = OcrRegionProjection.Resolve(config.TargetHp, client.Width, client.Height);
        var mp = OcrRegionProjection.Resolve(config.TargetMp, client.Width, client.Height);
        var rage = OcrRegionProjection.Resolve(config.TargetRage, client.Width, client.Height);
        if (hp.IsEmpty || mp.IsEmpty || rage.IsEmpty)
            return false;

        if (hp.Width < 40 || mp.Width < 40 || rage.Width < 40)
            return false;

        var hpSample = Crop(client, hp);
        var mpSample = Crop(client, mp);
        var rageSample = Crop(client, rage);
        if (hpSample is null || mpSample is null || rageSample is null)
            return false;

        var hasHp = HasHorizontalColorBar(hpSample, IsRedStatusPixel, 0.30)
                    || HasHorizontalColorBar(hpSample, IsGreenStatusPixel, 0.30);
        var hasBlue = HasHorizontalColorBar(mpSample, IsBlueStatusPixel, 0.24);
        var hasYellow = HasHorizontalColorBar(rageSample, IsYellowStatusPixel, 0.24);

        return hasHp && (hasBlue || hasYellow);
    }

    private static bool DetectTargetSchoolMarker(CaptureResult client, CombatRecognitionConfig config)
    {
        var hp = OcrRegionProjection.Resolve(config.TargetHp, client.Width, client.Height);
        if (hp.IsEmpty)
            return false;

        // Player target frames have a vertical school marker to the left of the
        // target HP/MP/rage bars. This catches cases where MP/rage are empty.
        var markerX = Math.Max(0, hp.X - 36);
        var markerY = Math.Max(0, hp.Y - 24);
        var marker = new OcrResolvedRegion(
            markerX,
            markerY,
            Math.Min(36, client.Width - markerX),
            Math.Min(82, client.Height - markerY));

        if (marker.Width < 28 || marker.Height < 48)
            return false;

        var sample = Crop(client, marker);
        return sample is not null && LooksLikeSchoolMarkerBlock(sample);
    }

    private static bool LooksLikeSchoolMarkerBlock(CaptureResult image)
    {
        var total = image.Width * image.Height;
        if (total <= 0)
            return false;

        var gold = 0;
        var dark = 0;
        var verticalGoldColumns = 0;
        var strongEdges = 0;

        for (var x = 0; x < image.Width; x++)
        {
            var columnGold = 0;
            for (var y = 0; y < image.Height; y++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];

                if (IsSchoolGoldPixel(r, g, b))
                {
                    gold++;
                    columnGold++;
                }

                if (Math.Max(r, Math.Max(g, b)) < 55)
                    dark++;

                if (x > 0)
                {
                    var left = p - 4;
                    var lum = r + g + b;
                    var leftLum = image.Pixels[left + 2] + image.Pixels[left + 1] + image.Pixels[left];
                    if (Math.Abs(lum - leftLum) > 100)
                        strongEdges++;
                }
            }

            if (columnGold >= Math.Max(5, image.Height / 5))
                verticalGoldColumns++;
        }

        var goldRatio = gold / (double)total;
        var darkRatio = dark / (double)total;
        var edgeRatio = strongEdges / (double)total;

        return goldRatio > 0.045
               && darkRatio > 0.18
               && edgeRatio > 0.045
               && verticalGoldColumns >= 2;
    }

    private static bool HasHorizontalColorBar(CaptureResult image, Func<byte, byte, byte, bool> predicate, double minCoverage)
    {
        var bestRun = 0;
        var currentRun = 0;
        var minPixels = Math.Max(6, (int)Math.Round(image.Width * minCoverage));

        for (var y = 0; y < image.Height; y++)
        {
            var count = 0;
            for (var x = 0; x < image.Width; x++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                if (predicate(r, g, b))
                    count++;
            }

            if (count >= minPixels)
            {
                currentRun++;
                bestRun = Math.Max(bestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return bestRun >= 2;
    }

    private static bool IsRedStatusPixel(byte r, byte g, byte b) =>
        r > 95 && r > g + 25 && r > b + 25;

    private static bool IsBlueStatusPixel(byte r, byte g, byte b) =>
        b > 85 && b > r + 20 && b > g + 5;

    private static bool IsGreenStatusPixel(byte r, byte g, byte b) =>
        g > 95 && g > r + 20 && g > b + 20;

    private static bool IsYellowStatusPixel(byte r, byte g, byte b) =>
        r > 115 && g > 85 && b < 90 && r > b + 40 && g > b + 30;

    private static Func<byte, byte, byte, bool> StatusBarPredicateFor(string name) =>
        name switch
        {
            "PlayerMp" or "TargetMp" => IsBlueStatusPixel,
            "TargetRage" or "PlayerRage" => IsYellowStatusPixel,
            _ => IsHealthStatusPixel
        };

    private static bool IsHealthStatusPixel(byte r, byte g, byte b) =>
        IsRedStatusPixel(r, g, b) || IsGreenStatusPixel(r, g, b);

    private static bool IsSchoolGoldPixel(byte r, byte g, byte b) =>
        r > 120 && g > 80 && b < 95 && r >= g && r > b + 35 && g > b + 20;

    private static bool DetectTargetPlayerPortrait(CaptureResult client, CombatRecognitionConfig config)
    {
        var name = OcrRegionProjection.Resolve(config.TargetName, client.Width, client.Height);
        if (name.IsEmpty)
            return false;

        // Full target portrait region. This is kept as auxiliary diagnostics; a
        // portrait alone is not enough to classify the target as a player.
        var hp = OcrRegionProjection.Resolve(config.TargetHp, client.Width, client.Height);
        if (hp.IsEmpty)
            return false;

        var portrait = new OcrResolvedRegion(
            Math.Min(client.Width - 1, hp.X + hp.Width + 10),
            Math.Max(0, hp.Y - 24),
            88,
            88);

        if (portrait.X >= client.Width || portrait.Y >= client.Height)
            return false;

        var clamped = new OcrResolvedRegion(
            portrait.X,
            portrait.Y,
            Math.Min(portrait.Width, client.Width - portrait.X),
            Math.Min(portrait.Height, client.Height - portrait.Y));

        if (clamped.Width < 36 || clamped.Height < 36)
            return false;

        var sample = Crop(client, clamped);
        return sample is not null && LooksLikePlayerPortraitBlock(sample);
    }

    private static bool LooksLikePlayerPortraitBlock(CaptureResult image)
    {
        var total = image.Width * image.Height;
        if (total <= 0)
            return false;

        var goldOrSkin = 0;
        var saturated = 0;
        var bright = 0;
        var dark = 0;
        var edge = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max > 135)
                    bright++;
                if (max < 45)
                    dark++;
                if (max - min > 55)
                    saturated++;
                if (IsSkinOrGoldPixel(r, g, b))
                    goldOrSkin++;

                if (x > 0 && y > 0)
                {
                    var left = p - 4;
                    var up = p - image.Width * 4;
                    var lum = r + g + b;
                    var leftLum = image.Pixels[left + 2] + image.Pixels[left + 1] + image.Pixels[left];
                    var upLum = image.Pixels[up + 2] + image.Pixels[up + 1] + image.Pixels[up];
                    if (Math.Abs(lum - leftLum) > 95 || Math.Abs(lum - upLum) > 95)
                        edge++;
                }
            }
        }

        var goldSkinRatio = goldOrSkin / (double)total;
        var saturatedRatio = saturated / (double)total;
        var brightRatio = bright / (double)total;
        var darkRatio = dark / (double)total;
        var edgeRatio = edge / (double)total;

        return goldSkinRatio > 0.10
               && saturatedRatio > 0.18
               && brightRatio > 0.08
               && darkRatio > 0.05
               && edgeRatio > 0.08;
    }

    private static bool IsSkinOrGoldPixel(byte r, byte g, byte b)
    {
        var skin = r > 115 && g > 70 && b > 45 && r > g + 12 && g > b - 10;
        var gold = r > 130 && g > 95 && b < 90 && r >= g && g > b + 20;
        return skin || gold;
    }

    private static int LongestContinuousRun(bool[] columns, int allowedGap)
    {
        var best = 0;
        var start = 0;
        var gaps = 0;

        for (var end = 0; end < columns.Length; end++)
        {
            if (!columns[end])
                gaps++;

            while (gaps > allowedGap && start <= end)
            {
                if (!columns[start])
                    gaps--;
                start++;
            }

            if (columns[end])
                best = Math.Max(best, end - start + 1 - gaps);
        }

        return best;
    }

    private static string NormalizeTargetName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var chars = text.Where(IsTargetNameChar)
            .ToArray();

        if (chars.Length < 2)
            return string.Empty;

        return new string(chars);
    }

    private static bool IsTargetNameChar(char c)
    {
        if (c is '一' or '丨' or '丶' or '丿' or '亅' or '｜' or '|')
            return false;

        return char.IsLetterOrDigit(c)
               || c is >= '\u4E00' and <= '\u9FFF';
    }

    [GeneratedRegex(@"\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex PercentNumberRegex();

    [GeneratedRegex(@"(\d{1,9})\s*/\s*(\d{1,9})", RegexOptions.CultureInvariant)]
    private static partial Regex HpRatioRegex();

    [GeneratedRegex(@"(\d{1,3})\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex HpPercentMarkRegex();

    [GeneratedRegex(@"[^\d/%]", RegexOptions.CultureInvariant)]
    private static partial Regex HpNoiseRegex();

    private sealed record StatusRegionResult(string Text, int? Percent);
}
