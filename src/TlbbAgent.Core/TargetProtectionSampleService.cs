using System.Text.Json;
using System.Text.Json.Nodes;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class TargetProtectionSampleService : ITargetProtectionSampleService
{
    private readonly ILogService _log;
    private readonly object _gate = new();
    private string _loadedPath = "";
    private DateTime _loadedWriteTime;
    private IReadOnlyList<TargetProtectionTemplate> _templates = Array.Empty<TargetProtectionTemplate>();

    public TargetProtectionSampleService(ILogService log)
    {
        _log = log;
    }

    public TargetProtectionSampleMatch? Match(CaptureResult client, CombatRecognitionConfig config, TargetingPreset targeting)
    {
        if (!targeting.TargetProtectionSampleMatchingEnabled)
            return null;

        var path = ResolveIndexPath(targeting.TargetProtectionSampleIndexPath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var templates = LoadTemplates(path);
        if (templates.Count == 0)
            return null;

        var current = BuildCurrentHashes(client, config);
        if (current is null)
            return null;

        var best = (Template: (TargetProtectionTemplate?)null, Score: 0.0);
        foreach (var template in templates)
        {
            var score = Score(current, template);
            if (score > best.Score)
                best = (template, score);
        }

        var threshold = Math.Clamp(targeting.TargetProtectionSampleThreshold <= 0 ? 0.93 : targeting.TargetProtectionSampleThreshold, 0.70, 1.0);
        if (best.Template is null || best.Score < threshold)
            return null;

        return new TargetProtectionSampleMatch(
            best.Template.Kind,
            best.Template.Label,
            best.Template.SampleId,
            best.Template.SourcePath,
            best.Score);
    }

    private IReadOnlyList<TargetProtectionTemplate> LoadTemplates(string path)
    {
        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase)
                && writeTime == _loadedWriteTime)
                return _templates;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path));
                var samples = root?["samples"]?.AsArray();
                if (samples is null)
                {
                    _templates = Array.Empty<TargetProtectionTemplate>();
                }
                else
                {
                    _templates = samples
                        .Select(ReadTemplate)
                        .Where(t => t is not null)
                        .Select(t => t!)
                        .ToArray();
                }

                _loadedPath = path;
                _loadedWriteTime = writeTime;
                _log.Info("目标保护", $"样本索引已加载：{_templates.Count} 个。");
            }
            catch (Exception ex)
            {
                _templates = Array.Empty<TargetProtectionTemplate>();
                _loadedPath = path;
                _loadedWriteTime = writeTime;
                _log.Warn("目标保护", $"样本索引读取失败：{ex.Message}");
            }

            return _templates;
        }
    }

    private static TargetProtectionTemplate? ReadTemplate(JsonNode? node)
    {
        if (node is null)
            return null;

        var kind = NormalizeKind(node["kind"]?.GetValue<string>() ?? "");
        if (string.IsNullOrWhiteSpace(kind))
            return null;

        var sampleId = node["sampleId"]?.GetValue<string>() ?? "";
        var label = node["label"]?.GetValue<string>() ?? sampleId;
        var sourcePath = node["sourcePath"]?.GetValue<string>() ?? "";
        var schoolMarkerHash = NormalizeHash(node["schoolMarkerHash"]?.GetValue<string>() ?? "");
        var targetPortraitHash = NormalizeHash(node["targetPortraitHash"]?.GetValue<string>() ?? "");
        var targetStatusHash = NormalizeHash(node["targetStatusHash"]?.GetValue<string>() ?? "");

        if (string.IsNullOrWhiteSpace(schoolMarkerHash)
            && string.IsNullOrWhiteSpace(targetPortraitHash)
            && string.IsNullOrWhiteSpace(targetStatusHash))
            return null;

        return new TargetProtectionTemplate(sampleId, label, kind, sourcePath, schoolMarkerHash, targetPortraitHash, targetStatusHash);
    }

    private static TargetProtectionCurrentHashes? BuildCurrentHashes(CaptureResult client, CombatRecognitionConfig config)
    {
        var hp = OcrRegionProjection.Resolve(config.TargetHp, client.Width, client.Height);
        if (hp.IsEmpty)
            return null;

        var school = CropCaptureRect(
            client,
            Math.Max(0, hp.X - 36),
            Math.Max(0, hp.Y - 24),
            Math.Min(36, client.Width - Math.Max(0, hp.X - 36)),
            Math.Min(82, client.Height - Math.Max(0, hp.Y - 24)));
        var portrait = CropCaptureRect(
            client,
            Math.Min(client.Width - 1, hp.X + hp.Width + 10),
            Math.Max(0, hp.Y - 24),
            88,
            88);
        var status = CropCaptureRect(
            client,
            hp.X,
            hp.Y,
            hp.Width,
            Math.Min(26, client.Height - hp.Y));

        return new TargetProtectionCurrentHashes(
            school is null ? "" : ComputeAverageHash64(school),
            portrait is null ? "" : ComputeAverageHash64(portrait),
            status is null ? "" : ComputeAverageHash64(status));
    }

    private static double Score(TargetProtectionCurrentHashes current, TargetProtectionTemplate template)
    {
        var weighted = 0.0;
        var weight = 0.0;

        AddScore(current.SchoolMarkerHash, template.SchoolMarkerHash, 0.45, ref weighted, ref weight);
        AddScore(current.TargetPortraitHash, template.TargetPortraitHash, 0.35, ref weighted, ref weight);
        AddScore(current.TargetStatusHash, template.TargetStatusHash, 0.20, ref weighted, ref weight);

        return weight <= 0 ? 0 : weighted / weight;
    }

    private static void AddScore(string current, string template, double itemWeight, ref double weighted, ref double weight)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(template))
            return;

        weighted += CompareHashSimilarity(current, template) * itemWeight;
        weight += itemWeight;
    }

    private static string ResolveIndexPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Environment.ExpandEnvironmentVariables(configuredPath.Trim());

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LongBanXing", "samples", "target-protection-index.json");
    }

    private static string NormalizeKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "player" or "monster" or "boss" or "elite" or "pet" or "none"
            ? normalized
            : "";
    }

    private static string NormalizeHash(string value)
    {
        var hash = value.Trim();
        return hash.Length == 16 && ulong.TryParse(hash, System.Globalization.NumberStyles.HexNumber, null, out _)
            ? hash.ToUpperInvariant()
            : "";
    }

    private static double CompareHashSimilarity(string left, string right)
    {
        if (!ulong.TryParse(left, System.Globalization.NumberStyles.HexNumber, null, out var a)
            || !ulong.TryParse(right, System.Globalization.NumberStyles.HexNumber, null, out var b))
            return 0.0;

        var distance = System.Numerics.BitOperations.PopCount(a ^ b);
        return Math.Clamp(1.0 - distance / 64.0, 0.0, 1.0);
    }

    private static string ComputeAverageHash64(CaptureResult image)
    {
        const int size = 8;
        Span<double> values = stackalloc double[size * size];
        for (var y = 0; y < size; y++)
        {
            var sourceY = Math.Clamp((int)Math.Round((y + 0.5) * image.Height / size), 0, Math.Max(0, image.Height - 1));
            for (var x = 0; x < size; x++)
            {
                var sourceX = Math.Clamp((int)Math.Round((x + 0.5) * image.Width / size), 0, Math.Max(0, image.Width - 1));
                var p = (sourceY * image.Width + sourceX) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                values[y * size + x] = r * 0.299 + g * 0.587 + b * 0.114;
            }
        }

        var avg = 0.0;
        for (var i = 0; i < values.Length; i++)
            avg += values[i];
        avg /= values.Length;

        ulong bits = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] >= avg)
                bits |= 1UL << i;
        }

        return bits.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CaptureResult? CropCaptureRect(CaptureResult source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var left = Math.Clamp(x, 0, Math.Max(0, source.Width - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, source.Height - 1));
        var right = Math.Clamp(x + width, left + 1, source.Width);
        var bottom = Math.Clamp(y + height, top + 1, source.Height);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
            return null;

        var dst = new byte[cropWidth * cropHeight * 4];
        var srcStride = source.Width * 4;
        var dstStride = cropWidth * 4;
        for (var row = 0; row < cropHeight; row++)
            Array.Copy(source.Pixels, (top + row) * srcStride + left * 4, dst, row * dstStride, dstStride);

        return new CaptureResult(cropWidth, cropHeight, dst);
    }

    private sealed record TargetProtectionTemplate(
        string SampleId,
        string Label,
        string Kind,
        string SourcePath,
        string SchoolMarkerHash,
        string TargetPortraitHash,
        string TargetStatusHash);

    private sealed record TargetProtectionCurrentHashes(
        string SchoolMarkerHash,
        string TargetPortraitHash,
        string TargetStatusHash);
}
