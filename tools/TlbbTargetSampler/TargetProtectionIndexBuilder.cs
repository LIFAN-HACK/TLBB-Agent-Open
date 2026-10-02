using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TlbbAgent.Contracts;

namespace TlbbTargetSampler;

internal static class TargetProtectionIndexBuilder
{
    private static readonly string[] RequiredRegionKeys =
    [
        "target_name",
        "target_hp",
        "target_mp",
        "target_rage",
        "target_status",
        "school_marker",
        "target_portrait",
        "player_hp",
        "player_mp",
        "player_rage",
        "pet_hp"
    ];

    public static string DefaultOutputPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LongBanXing",
        "samples",
        "target-protection-index.json");

    public static TargetProtectionIndexBuildResult Rebuild(string samplesRoot, string? outputPath = null)
    {
        outputPath = string.IsNullOrWhiteSpace(outputPath) ? DefaultOutputPath : outputPath;
        var coverage = AnalyzeCoverage(samplesRoot);
        if (!Directory.Exists(samplesRoot))
            return new TargetProtectionIndexBuildResult(outputPath, 0, 0, false, coverage, $"样本目录不存在：{samplesRoot}");

        var samples = new JsonArray();
        var skipped = 0;
        foreach (var sampleDir in Directory.EnumerateDirectories(samplesRoot).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = ReadMetadata(sampleDir);
            var kind = NormalizeKind(metadata.ExpectedKind);
            if (kind is "" or "unknown" or "none")
            {
                skipped++;
                continue;
            }

            var cropsDir = Path.Combine(sampleDir, "crops");
            var schoolMarkerHash = ReadCropHash(cropsDir, "school_marker");
            var portraitHash = ReadCropHash(cropsDir, "target_portrait");
            var statusHash = ReadCropHash(cropsDir, "target_status");
            if (string.IsNullOrWhiteSpace(schoolMarkerHash)
                && string.IsNullOrWhiteSpace(portraitHash)
                && string.IsNullOrWhiteSpace(statusHash))
            {
                skipped++;
                continue;
            }

            samples.Add(new JsonObject
            {
                ["sampleId"] = Path.GetFileName(sampleDir),
                ["label"] = string.IsNullOrWhiteSpace(metadata.Label) ? Path.GetFileName(sampleDir) : metadata.Label,
                ["kind"] = kind,
                ["sourcePath"] = sampleDir,
                ["schoolMarkerHash"] = schoolMarkerHash,
                ["targetPortraitHash"] = portraitHash,
                ["targetStatusHash"] = statusHash
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["createdAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["samplesRoot"] = samplesRoot,
            ["samples"] = samples,
            ["notes"] = "只读目标保护样本索引；由目标采集样本生成，不访问游戏、不发送输入。"
        };
        File.WriteAllText(outputPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new UTF8Encoding(false));

        return new TargetProtectionIndexBuildResult(
            outputPath,
            samples.Count,
            skipped,
            samples.Count > 0,
            coverage,
            samples.Count > 0
                ? $"目标保护索引已更新：{samples.Count} 个样本，跳过 {skipped} 个。"
                : $"未生成可用目标保护样本，跳过 {skipped} 个。");
    }

    public static TargetSampleCoverageResult AnalyzeCoverage(string samplesRoot)
    {
        if (!Directory.Exists(samplesRoot))
        {
            return new TargetSampleCoverageResult(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                ["样本目录不存在"],
                []);
        }

        var samples = Directory.EnumerateDirectories(samplesRoot)
            .Select(ReadSampleCoverage)
            .ToArray();

        int Count(string kind) => samples.Count(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
        var bossOrElite = Count("boss") + Count("elite");
        var monsterLike = Count("monster") + bossOrElite;
        var knownTemplateSamples = samples.Where(s => s.Kind is not "" and not "unknown").ToArray();
        var notReady = knownTemplateSamples.Where(s => !s.ReadyForTemplate).ToArray();
        var missing = new List<string>();
        if (Count("player") < 3)
            missing.Add($"玩家样本还需 {3 - Count("player")} 个");
        if (Count("monster") < 3)
            missing.Add($"普通怪物样本还需 {3 - Count("monster")} 个");
        if (bossOrElite < 2)
            missing.Add($"首领/BOSS样本还需 {2 - bossOrElite} 个");
        if (Count("pet") < 1)
            missing.Add("宠物/宝宝样本还需 1 个");
        if (Count("none") < 1)
            missing.Add("无目标样本还需 1 个");
        if (notReady.Length > 0)
            missing.Add($"裁剪不完整样本 {notReady.Length} 个");

        var minimalReady = Count("player") >= 1
                           && monsterLike >= 1
                           && knownTemplateSamples
                               .Where(s => s.Kind is "player" or "monster" or "boss" or "elite")
                               .All(s => s.ReadyForTemplate);
        var recommendedReady = Count("player") >= 3
                               && Count("monster") >= 3
                               && bossOrElite >= 2
                               && Count("pet") >= 1
                               && Count("none") >= 1
                               && notReady.Length == 0;

        return new TargetSampleCoverageResult(
            samples.Length,
            Count("player"),
            Count("monster"),
            bossOrElite,
            Count("pet"),
            Count("none"),
            Count("unknown"),
            notReady.Length,
            minimalReady,
            recommendedReady,
            missing,
            samples);
    }

    public static string? CanonicalCropFileName(string key)
    {
        return key switch
        {
            "school_marker" => "school-marker.png",
            "target_portrait" => "target-portrait.png",
            "target_status" => "target-status.png",
            _ => null
        };
    }

    private static TargetSampleCoverageItem ReadSampleCoverage(string sampleDir)
    {
        var metadata = ReadMetadata(sampleDir);
        var kind = NormalizeKind(metadata.ExpectedKind);
        if (string.IsNullOrWhiteSpace(kind))
            kind = "unknown";

        var cropsDir = Path.Combine(sampleDir, "crops");
        var missing = RequiredRegionKeys
            .Where(key => string.IsNullOrWhiteSpace(ResolveCropPath(cropsDir, key)))
            .ToArray();
        var clientOk = File.Exists(Path.Combine(sampleDir, "client.png"));
        var metadataOk = File.Exists(Path.Combine(sampleDir, "metadata.json"));
        var readyForTemplate = kind is not "unknown" && clientOk && metadataOk && missing.Length == 0;

        return new TargetSampleCoverageItem(
            sampleDir,
            string.IsNullOrWhiteSpace(metadata.Label) ? Path.GetFileName(sampleDir) : metadata.Label,
            kind,
            readyForTemplate,
            missing);
    }

    private static TargetSampleMetadataView ReadMetadata(string sampleDir)
    {
        var metadataPath = Path.Combine(sampleDir, "metadata.json");
        if (!File.Exists(metadataPath))
            return new TargetSampleMetadataView(Path.GetFileName(sampleDir), "");

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(metadataPath));
            return new TargetSampleMetadataView(
                ReadString(root, "Label", "label"),
                ReadString(root, "ExpectedKind", "expectedKind", "kind"));
        }
        catch
        {
            return new TargetSampleMetadataView(Path.GetFileName(sampleDir), "");
        }
    }

    private static string ReadString(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node?[name]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string NormalizeKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "player" or "monster" or "boss" or "elite" or "pet" or "none" or "unknown"
            ? normalized
            : "";
    }

    private static string ReadCropHash(string cropsDir, string key)
    {
        var path = ResolveCropPath(cropsDir, key);
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var capture = LoadCapturePng(path);
        return capture is null ? "" : ComputeAverageHash64(capture);
    }

    private static string ResolveCropPath(string cropsDir, string key)
    {
        if (!Directory.Exists(cropsDir))
            return "";

        var canonical = CanonicalCropFileName(key);
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            var canonicalPath = Path.Combine(cropsDir, canonical);
            if (File.Exists(canonicalPath))
                return canonicalPath;
        }

        var hyphenKey = key.Replace('_', '-');
        return Directory.EnumerateFiles(cropsDir, "*.png")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.Equals(key, StringComparison.OrdinalIgnoreCase)
                       || name.Equals(hyphenKey, StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith(hyphenKey + "-", StringComparison.OrdinalIgnoreCase);
            }) ?? "";
    }

    private static CaptureResult? LoadCapturePng(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource converted = frame.Format == PixelFormats.Bgr32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            converted.Freeze();

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return new CaptureResult(converted.PixelWidth, converted.PixelHeight, pixels);
        }
        catch
        {
            return null;
        }
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

        return bits.ToString("X16", CultureInfo.InvariantCulture);
    }

    private sealed record TargetSampleMetadataView(string Label, string ExpectedKind);
}

internal sealed record TargetProtectionIndexBuildResult(
    string OutputPath,
    int SampleCount,
    int SkippedCount,
    bool Success,
    TargetSampleCoverageResult Coverage,
    string Message);

internal sealed record TargetSampleCoverageResult(
    int Total,
    int Player,
    int Monster,
    int BossOrElite,
    int Pet,
    int None,
    int Unknown,
    int NotReadyForTemplate,
    bool MinimalReady,
    bool RecommendedReady,
    IReadOnlyList<string> MissingRecommended,
    IReadOnlyList<TargetSampleCoverageItem> Samples)
{
    public string Summary
    {
        get
        {
            var state = RecommendedReady ? "推荐覆盖已满足" : MinimalReady ? "最小链路可用，仍需补样本" : "样本不足";
            var missing = MissingRecommended.Count == 0 ? "无" : string.Join("；", MissingRecommended);
            return $"{state} | 总数 {Total}，玩家 {Player}，普通怪 {Monster}，BOSS/首领 {BossOrElite}，宠物 {Pet}，无目标 {None}，未知 {Unknown}。缺口：{missing}";
        }
    }
}

internal sealed record TargetSampleCoverageItem(
    string Directory,
    string Label,
    string Kind,
    bool ReadyForTemplate,
    IReadOnlyList<string> MissingRegions);
