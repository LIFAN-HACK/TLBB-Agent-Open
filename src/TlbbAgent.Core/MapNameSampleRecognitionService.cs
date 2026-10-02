using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class MapNameSampleRecognitionService : IMapNameSampleRecognitionService
{
    private const double ConfidenceThreshold = 0.70;
    private readonly string? _sampleRoot;
    private readonly Lazy<IReadOnlyList<MapNameSample>> _samples;

    public MapNameSampleRecognitionService()
    {
        _sampleRoot = FindMapNameSampleRoot();
        _samples = new Lazy<IReadOnlyList<MapNameSample>>(() => LoadAcceptedSamples(_sampleRoot));
    }

    public string SampleRoot => _sampleRoot ?? "";
    public int SampleCount => _samples.Value.Count;

    public MapNameSampleMatchResult? Match(CaptureResult mapNameCapture)
    {
        var current = BuildGoldMask(mapNameCapture);
        if (current.DarkPixelCount <= 0)
            return null;

        var best = (Sample: (MapNameSample?)null, Score: 0.0);
        foreach (var sample in _samples.Value)
        {
            var score = CompareMasks(current, sample.Mask);
            if (score > best.Score)
                best = (sample, score);
        }

        if (best.Sample is null || best.Score <= 0)
            return null;

        return new MapNameSampleMatchResult(
            best.Sample.Label,
            best.Score,
            best.Score >= ConfidenceThreshold,
            best.Sample.Id,
            best.Sample.SamplePath);
    }

    private static IReadOnlyList<MapNameSample> LoadAcceptedSamples(string? root)
    {
        if (root is null)
            return Array.Empty<MapNameSample>();

        var samples = new List<MapNameSample>();
        foreach (var samplePath in Directory.EnumerateFiles(root, "sample.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = JsonSerializer.Deserialize<MapNameSampleJson>(File.ReadAllText(samplePath), JsonOptions);
                if (json is not { Trusted: true, ReviewStatus: "Accepted", Mask.Width: > 0, Mask.Height: > 0 }
                    || string.IsNullOrWhiteSpace(json.Label)
                    || json.Mask.Rows.Length != json.Mask.Height)
                {
                    continue;
                }

                var mask = new BinaryMask(
                    json.Mask.Width,
                    json.Mask.Height,
                    RowsToMask(json.Mask.Rows, json.Mask.Width, json.Mask.Height));
                samples.Add(new MapNameSample(
                    Path.GetFileName(Path.GetDirectoryName(samplePath)) ?? "-",
                    json.Label.Trim(),
                    samplePath,
                    mask));
            }
            catch
            {
                // A broken sample should not block runtime recognition.
            }
        }

        return samples
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static string? FindMapNameSampleRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "data", "samples", "map-names");
                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static bool[] RowsToMask(string[] rows, int width, int height)
    {
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = rows[y];
            for (var x = 0; x < width && x < row.Length; x++)
                mask[y * width + x] = row[x] == '#';
        }

        return mask;
    }

    private static BinaryMask BuildGoldMask(CaptureResult image)
    {
        var mask = new bool[image.Width * image.Height];
        var darkPixels = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            var p = i * 4;
            var b = image.Pixels[p];
            var g = image.Pixels[p + 1];
            var r = image.Pixels[p + 2];
            if (!IsGoldMapTextPixel(r, g, b))
                continue;

            mask[i] = true;
            darkPixels++;
        }

        return new BinaryMask(image.Width, image.Height, mask, darkPixels);
    }

    private static bool IsGoldMapTextPixel(byte r, byte g, byte b)
    {
        return r >= 145
               && g >= 105
               && b <= 120
               && r >= g
               && g >= b + 20
               && r + g >= 285;
    }

    private static double CompareMasks(BinaryMask current, BinaryMask sample)
    {
        var normalized = current.Width == sample.Width && current.Height == sample.Height
            ? current
            : ResizeMask(current, sample.Width, sample.Height);

        var intersection = 0;
        var union = 0;
        for (var i = 0; i < sample.Mask.Length; i++)
        {
            var a = normalized.Mask[i];
            var b = sample.Mask[i];
            if (a && b)
                intersection++;
            if (a || b)
                union++;
        }

        return union == 0 ? 0 : intersection / (double)union;
    }

    private static BinaryMask ResizeMask(BinaryMask source, int width, int height)
    {
        var mask = new bool[width * height];
        var darkPixels = 0;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(source.Height - 1, (int)Math.Round(y * (source.Height - 1) / (double)Math.Max(1, height - 1)));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(source.Width - 1, (int)Math.Round(x * (source.Width - 1) / (double)Math.Max(1, width - 1)));
                var value = source.Mask[sourceY * source.Width + sourceX];
                mask[y * width + x] = value;
                if (value)
                    darkPixels++;
            }
        }

        return new BinaryMask(width, height, mask, darkPixels);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record MapNameSample(string Id, string Label, string SamplePath, BinaryMask Mask);

    private sealed record BinaryMask(int Width, int Height, bool[] Mask, int DarkPixelCount = 0);

    private sealed class MapNameSampleJson
    {
        public string Label { get; set; } = "";
        public bool Trusted { get; set; }
        public string ReviewStatus { get; set; } = "";
        public MapNameMaskJson Mask { get; set; } = new();
    }

    private sealed class MapNameMaskJson
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string[] Rows { get; set; } = Array.Empty<string>();
    }
}
