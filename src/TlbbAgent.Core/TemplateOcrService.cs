using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

[Obsolete("Legacy bitmap dictionary OCR is disabled in production. Use IOpenTextRecognizer.")]
public sealed partial class TemplateOcrService : ITemplateOcrService
{
    private const int MaxRecognitionPixels = 200_000;
    private const double CandidateThreshold = 0.88;

    private readonly ILogService _log;
    private readonly List<TemplateGlyphInfo> _glyphs = new();
    private readonly List<RasterGlyph> _rasters = new();
    private readonly Dictionary<int, List<RasterGlyph>> _rastersByHeight = new();
    private readonly HashSet<string> _rasterKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TemplateGlyphInfo>> _byText = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedSources = new(StringComparer.OrdinalIgnoreCase);

    static TemplateOcrService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TemplateOcrService(ILogService log) => _log = log;

    public int LoadedGlyphCount => _glyphs.Count;
    public int RenderableGlyphCount => _rasters.Count;
    public IReadOnlyList<TemplateGlyphInfo> Glyphs => new ReadOnlyCollection<TemplateGlyphInfo>(_glyphs);
    public IReadOnlyList<TemplateGlyphInfo> RenderableGlyphs => new ReadOnlyCollection<TemplateGlyphInfo>(_rasters.Select(r => r.Info).ToList());
    public IReadOnlyCollection<string> LoadedSources => new ReadOnlyCollection<string>(_loadedSources.ToList());

    public int LoadDictionary(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Dictionary path cannot be empty.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Dictionary file does not exist.", fullPath);

        if (!_loadedSources.Add(fullPath))
        {
            _log.Trace("TemplateOcr", "字库已加载。");
            return LoadedGlyphCount;
        }

        var beforeGlyphs = _glyphs.Count;
        var beforeRasters = _rasters.Count;
        var failed = 0;
        var lines = ReadAllLines(fullPath);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (TryParseLine(line, fullPath, i + 1, out var glyph))
            {
                _glyphs.Add(glyph);
                if (!_byText.TryGetValue(glyph.Text, out var list))
                {
                    list = new List<TemplateGlyphInfo>();
                    _byText[glyph.Text] = list;
                }
                list.Add(glyph);

                if (TryBuildRasterGlyph(glyph, out var raster) && _rasterKeys.Add(RasterKey(raster)))
                {
                    _rasters.Add(raster);
                    if (!_rastersByHeight.TryGetValue(raster.Height, out var bucket))
                    {
                        bucket = new List<RasterGlyph>();
                        _rastersByHeight[raster.Height] = bucket;
                    }
                    bucket.Add(raster);
                }
            }
            else
            {
                failed++;
            }
        }

        var addedGlyphs = _glyphs.Count - beforeGlyphs;
        var addedRasters = _rasters.Count - beforeRasters;
        _log.Info("TemplateOcr", $"字库已加载：+{addedRasters}，失败 {failed}。");
        return LoadedGlyphCount;
    }

    public string Recognize(CaptureResult image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height * 4)
            throw new ArgumentException("Invalid capture result.", nameof(image));

        if (_rasters.Count == 0)
        {
            _log.Trace("TemplateOcr", "未加载字库。");
            return string.Empty;
        }

        var pixelCount = image.Width * image.Height;
        if (pixelCount > MaxRecognitionPixels)
        {
            _log.Warn("TemplateOcr", "区域过大，请重新校准。");
            return string.Empty;
        }

        var (darkMask, brightMask, contrast) = BuildPolarityMasks(image);
        if (contrast < 10)
        {
            _log.Trace("TemplateOcr", "区域对比度过低。");
            return string.Empty;
        }

        var candidates = new List<OcrCandidate>();
        CollectCandidates(darkMask, image.Width, image.Height, "dark", candidates);
        CollectCandidates(brightMask, image.Width, image.Height, "bright", candidates);

        if (candidates.Count == 0)
        {
            _log.Trace("TemplateOcr", "未识别。");
            return string.Empty;
        }

        var selected = SelectNonOverlapping(candidates)
            .OrderBy(c => c.X)
            .ThenBy(c => c.Y)
            .ToList();

        var result = string.Concat(selected.Select(c => c.Text));
        _log.Trace("TemplateOcr", $"识别完成：{selected.Count}/{candidates.Count}。");
        return result;
    }

    public IReadOnlyList<TemplateGlyphInfo> FindByText(string text) =>
        _byText.TryGetValue(text, out var list) ? list : Array.Empty<TemplateGlyphInfo>();

    public CaptureResult? RenderDiagnosticGlyph(string text, bool darkForeground = true, int padding = 2)
    {
        var raster = _rasters.FirstOrDefault(r => string.Equals(r.Info.Text, text, StringComparison.Ordinal))
            ?? _rasters.FirstOrDefault();

        if (raster is null)
            return null;

        return RenderDiagnosticRaster(raster, darkForeground, padding);
    }

    public CaptureResult? RenderDiagnosticGlyph(TemplateGlyphInfo glyph, bool darkForeground = true, int padding = 2)
    {
        var raster = _rasters.FirstOrDefault(r => ReferenceEquals(r.Info, glyph))
                     ?? _rasters.FirstOrDefault(r => r.Info.SourcePath == glyph.SourcePath && r.Info.SourceLine == glyph.SourceLine);

        if (raster is null)
            return null;

        return RenderDiagnosticRaster(raster, darkForeground, padding);
    }

    private static CaptureResult RenderDiagnosticRaster(RasterGlyph raster, bool darkForeground, int padding)
    {
        padding = Math.Max(0, padding);
        var width = raster.Width + padding * 2;
        var height = raster.Height + padding * 2;
        var pixels = new byte[width * height * 4];

        var bg = darkForeground ? (byte)255 : (byte)0;
        var fg = darkForeground ? (byte)0 : (byte)255;

        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = bg;
            pixels[i * 4 + 1] = bg;
            pixels[i * 4 + 2] = bg;
            pixels[i * 4 + 3] = 255;
        }

        foreach (var index in raster.ForegroundIndices)
        {
            var x = index % raster.Width + padding;
            var y = index / raster.Width + padding;
            var p = (y * width + x) * 4;
            pixels[p] = fg;
            pixels[p + 1] = fg;
            pixels[p + 2] = fg;
            pixels[p + 3] = 255;
        }

        return new CaptureResult(width, height, pixels);
    }

    private void CollectCandidates(bool[] imageMask, int imageWidth, int imageHeight, string polarity, List<OcrCandidate> candidates)
    {
        var prefix = BuildIntegral(imageMask, imageWidth, imageHeight);

        foreach (var raster in CandidateRastersFor(imageWidth, imageHeight))
        {
            var glyphWidth = raster.Width;
            var glyphHeight = raster.Height;
            if (glyphWidth > imageWidth || raster.ForegroundCount == 0)
                continue;

            for (var y = 0; y <= imageHeight - glyphHeight; y++)
            {
                for (var x = 0; x <= imageWidth - glyphWidth; x++)
                {
                    var imageForeground = RectSum(prefix, imageWidth, x, y, glyphWidth, glyphHeight);
                    if (imageForeground == 0)
                        continue;

                    var possible = Math.Min(imageForeground, raster.ForegroundCount);
                    var requiredBase = Math.Max(imageForeground, raster.ForegroundCount);
                    if (possible / (double)requiredBase < 0.55)
                        continue;

                    var matched = 0;
                    foreach (var index in raster.ForegroundIndices)
                    {
                        var gx = index % glyphWidth;
                        var gy = index / glyphWidth;
                        if (imageMask[(y + gy) * imageWidth + x + gx])
                            matched++;
                    }

                    var recall = matched / (double)raster.ForegroundCount;
                    if (recall < 0.76)
                        continue;

                    var precision = matched / (double)imageForeground;
                    var score = precision + recall == 0
                        ? 0
                        : 2 * precision * recall / (precision + recall);

                    if (score >= CandidateThreshold)
                    {
                        candidates.Add(new OcrCandidate(raster.Info, x, y, glyphWidth, glyphHeight, score, polarity));
                        if (candidates.Count > 5000)
                            PruneCandidates(candidates, 1000);
                    }
                }
            }
        }
    }

    private IEnumerable<RasterGlyph> CandidateRastersFor(int imageWidth, int imageHeight)
    {
        foreach (var (height, bucket) in _rastersByHeight)
        {
            if (height > imageHeight)
                continue;

            foreach (var raster in bucket)
            {
                if (raster.Width <= imageWidth)
                    yield return raster;
            }
        }
    }

    private static IReadOnlyList<OcrCandidate> SelectNonOverlapping(List<OcrCandidate> candidates)
    {
        var selected = new List<OcrCandidate>();
        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Score)
                     .ThenBy(c => c.Text.Length)
                     .ThenBy(c => c.X)
                     .ThenBy(c => c.Y))
        {
            if (selected.Any(s => IntersectionOverUnion(s, candidate) > 0.20 || CenterInside(s, candidate) || MostlyContained(s, candidate)))
                continue;

            selected.Add(candidate);
            if (selected.Count >= 128)
                break;
        }

        return selected;
    }

    private static bool CenterInside(OcrCandidate selected, OcrCandidate candidate)
    {
        var cx = candidate.X + candidate.Width / 2.0;
        var cy = candidate.Y + candidate.Height / 2.0;
        return cx >= selected.X
               && cx <= selected.X + selected.Width
               && cy >= selected.Y
               && cy <= selected.Y + selected.Height;
    }

    private static bool MostlyContained(OcrCandidate selected, OcrCandidate candidate)
    {
        var left = Math.Max(selected.X, candidate.X);
        var top = Math.Max(selected.Y, candidate.Y);
        var right = Math.Min(selected.X + selected.Width, candidate.X + candidate.Width);
        var bottom = Math.Min(selected.Y + selected.Height, candidate.Y + candidate.Height);
        if (right <= left || bottom <= top)
            return false;

        var intersection = (right - left) * (bottom - top);
        var candidateArea = candidate.Width * candidate.Height;
        return candidateArea > 0 && intersection / (double)candidateArea > 0.65;
    }

    private static void PruneCandidates(List<OcrCandidate> candidates, int keep)
    {
        var best = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.X)
            .ThenBy(c => c.Y)
            .Take(keep)
            .ToList();

        candidates.Clear();
        candidates.AddRange(best);
    }

    private static double IntersectionOverUnion(OcrCandidate a, OcrCandidate b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
            return 0;

        var intersection = (right - left) * (bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static (bool[] Dark, bool[] Bright, int Contrast) BuildPolarityMasks(CaptureResult image)
    {
        var count = image.Width * image.Height;
        var gray = new byte[count];
        var min = 255;
        var max = 0;

        for (var i = 0; i < count; i++)
        {
            var p = i * 4;
            var b = image.Pixels[p];
            var g = image.Pixels[p + 1];
            var r = image.Pixels[p + 2];
            var value = (r * 299 + g * 587 + b * 114) / 1000;
            gray[i] = (byte)value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        var threshold = (min + max) / 2;
        var dark = new bool[count];
        var bright = new bool[count];
        for (var i = 0; i < count; i++)
        {
            dark[i] = gray[i] <= threshold;
            bright[i] = gray[i] >= threshold;
        }

        return (dark, bright, max - min);
    }

    private static int[] BuildIntegral(bool[] mask, int width, int height)
    {
        var stride = width + 1;
        var prefix = new int[(width + 1) * (height + 1)];

        for (var y = 0; y < height; y++)
        {
            var row = 0;
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x])
                    row++;
                prefix[(y + 1) * stride + x + 1] = prefix[y * stride + x + 1] + row;
            }
        }

        return prefix;
    }

    private static int RectSum(int[] prefix, int imageWidth, int x, int y, int width, int height)
    {
        var stride = imageWidth + 1;
        var x2 = x + width;
        var y2 = y + height;
        return prefix[y2 * stride + x2]
               - prefix[y * stride + x2]
               - prefix[y2 * stride + x]
               + prefix[y * stride + x];
    }

    private static bool TryBuildRasterGlyph(TemplateGlyphInfo glyph, out RasterGlyph raster)
    {
        raster = default!;

        var requiredHexLength = (int)Math.Ceiling(glyph.Width * glyph.Height / 4.0);
        if (glyph.Hex.Length > requiredHexLength)
            return false;

        var mask = new bool[glyph.Width * glyph.Height];
        var foreground = new List<int>();

        for (var pixel = 0; pixel < mask.Length; pixel++)
        {
            var hexIndex = pixel / 4;
            if (hexIndex >= glyph.Hex.Length)
                continue;
            var bitInNibble = 3 - pixel % 4;
            var value = HexToNibble(glyph.Hex[hexIndex]);
            var isForeground = (value & (1 << bitInNibble)) != 0;
            mask[pixel] = isForeground;
            if (isForeground)
                foreground.Add(pixel);
        }

        if (foreground.Count == 0)
            return false;

        raster = new RasterGlyph(glyph, glyph.Width, glyph.Height, mask, foreground.ToArray(), foreground.Count);
        return true;
    }

    private static int HexToNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0
    };

    private static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        try
        {
            var text = utf8.GetString(bytes);
            if (!text.Contains('\uFFFD')) return SplitLines(text);
        }
        catch (DecoderFallbackException)
        {
        }

        var gb = Encoding.GetEncoding(936);
        return SplitLines(gb.GetString(bytes));
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static bool TryParseLine(string line, string sourcePath, int sourceLine, out TemplateGlyphInfo glyph)
    {
        glyph = default!;
        var parts = line.Split('$');

        if (parts.Length == 4 && IsHex(parts[0]) && TryParseAjZk(parts, sourcePath, sourceLine, out glyph))
            return true;

        if (parts.Length == 3 && IsHex(parts[2]) && TryParse001(parts, sourcePath, sourceLine, out glyph))
            return true;

        return false;
    }

    private static bool TryParseAjZk(string[] parts, string sourcePath, int sourceLine, out TemplateGlyphInfo glyph)
    {
        glyph = default!;
        var hex = NormalizeHex(parts[0]);
        var text = parts[1];
        var pos = parts[2].Split('.');
        if (pos.Length < 3) return false;
        if (!int.TryParse(pos[0], out var x)) return false;
        if (!int.TryParse(pos[1], out var y)) return false;
        if (!int.TryParse(pos[2], out _)) return false;
        if (!int.TryParse(parts[3], out var height)) return false;
        var width = DerivePackedWidth(hex, height);
        if (!IsValidGlyph(text, width, height, hex)) return false;

        glyph = new TemplateGlyphInfo(text, width, height, x, y, hex, sourcePath, sourceLine, "aj/zk");
        return true;
    }

    private static bool TryParse001(string[] parts, string sourcePath, int sourceLine, out TemplateGlyphInfo glyph)
    {
        glyph = default!;
        var text = parts[0];
        var dim = parts[1].Split('-');
        if (dim.Length < 2) return false;
        if (!int.TryParse(dim[0], out var width)) return false;
        if (!int.TryParse(dim[1], out var height)) return false;
        var hex = NormalizeHex(parts[2]);
        if (!IsValidGlyph(text, width, height, hex)) return false;

        glyph = new TemplateGlyphInfo(text, width, height, 0, 0, hex, sourcePath, sourceLine, "001");
        return true;
    }

    private static bool IsValidGlyph(string text, int width, int height, string hex) =>
        !string.IsNullOrWhiteSpace(text)
        && width > 0
        && height > 0
        && hex.Length > 0
        && IsHex(hex);

    private static string NormalizeHex(string value) => value.Trim().ToUpperInvariant();

    private static bool IsHex(string value) => HexRegex().IsMatch(value.Trim());

    private static int DerivePackedWidth(string hex, int height) =>
        height <= 0 ? 0 : (int)Math.Ceiling(hex.Length * 4 / (double)height);

    private static string RasterKey(RasterGlyph raster) =>
        $"{raster.Info.Text}\u001f{raster.Width}x{raster.Height}\u001f{raster.Info.Hex}";

    private sealed record RasterGlyph(
        TemplateGlyphInfo Info,
        int Width,
        int Height,
        bool[] Mask,
        int[] ForegroundIndices,
        int ForegroundCount);

    private sealed record OcrCandidate(
        TemplateGlyphInfo Info,
        int X,
        int Y,
        int Width,
        int Height,
        double Score,
        string Polarity)
    {
        public string Text => Info.Text;
    }

    [GeneratedRegex("^[0-9A-Fa-f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();
}
