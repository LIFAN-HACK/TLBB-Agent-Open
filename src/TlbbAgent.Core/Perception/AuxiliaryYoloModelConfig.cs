using System.Globalization;
using System.Text.Json;

namespace TlbbAgent.Core.Perception;

internal sealed record AuxiliaryYoloModelConfig(
    int InputSize,
    float ConfidenceThreshold,
    float NmsIouThreshold,
    int MaximumDetections,
    string Sha256,
    IReadOnlyList<string> Classes,
    IReadOnlyDictionary<int, float> ClassThresholds)
{
    public static AuxiliaryYoloModelConfig Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var classes = ReadClasses(root);
        if (classes.Count == 0)
            throw new InvalidDataException("Auxiliary YOLO class list is empty.");

        var input = root.GetProperty("input");
        var inputSize = input.TryGetProperty("imgsz", out var imageSize)
            ? imageSize.GetInt32()
            : input.TryGetProperty("image_size", out var legacyImageSize)
                ? legacyImageSize.GetInt32()
                : input.TryGetProperty("shape", out var shape)
                    ? ReadSquareShape(shape)
                    : ReadSquareDimensions(input);

        var postprocess = root.TryGetProperty("postprocess", out var post)
            ? post
            : root.GetProperty("output");
        var confidence = ReadFloat(
            postprocess,
            0.35f,
            "confidence_threshold",
            "candidate_confidence_threshold",
            "confidence");
        var nms = ReadFloat(
            postprocess,
            0.70f,
            "nms_iou_threshold",
            "iou_threshold",
            "nmsIou");
        var maximum = ReadInt(
            postprocess,
            20,
            "max_detections",
            "maximum_detections",
            "maxDetections");
        var sha256 = root.TryGetProperty("hashes", out var hashes)
                     && hashes.TryGetProperty("onnx_sha256", out var hash)
            ? hash.GetString() ?? ""
            : "";

        var thresholds = classes
            .Select((_, index) => new KeyValuePair<int, float>(index, confidence))
            .ToDictionary(item => item.Key, item => item.Value);
        return new AuxiliaryYoloModelConfig(
            inputSize,
            confidence,
            nms,
            maximum,
            sha256,
            classes,
            thresholds);
    }

    private static IReadOnlyList<string> ReadClasses(JsonElement root)
    {
        var value = root.TryGetProperty("classes", out var classes)
            ? classes
            : root.TryGetProperty("class_names", out var legacyClasses)
                ? legacyClasses
                : root.GetProperty("classNames");
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(item => item.Length > 0)
                .ToArray();
        }

        return value.EnumerateObject()
            .Select(item => new
            {
                Index = int.Parse(item.Name, CultureInfo.InvariantCulture),
                Label = item.Value.GetString() ?? ""
            })
            .OrderBy(item => item.Index)
            .Select(item => item.Label)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static int ReadSquareShape(JsonElement shape)
    {
        var dimensions = shape.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (dimensions.Length != 4 || dimensions[2] <= 0 || dimensions[2] != dimensions[3])
            throw new InvalidDataException($"Expected a square NCHW input shape, got [{string.Join(',', dimensions)}].");
        return dimensions[2];
    }

    private static int ReadSquareDimensions(JsonElement input)
    {
        if (!input.TryGetProperty("width", out var widthValue)
            || !input.TryGetProperty("height", out var heightValue)
            || !widthValue.TryGetInt32(out var width)
            || !heightValue.TryGetInt32(out var height)
            || width <= 0
            || width != height)
        {
            throw new InvalidDataException("Expected a square YOLO input size.");
        }

        return width;
    }

    private static float ReadFloat(JsonElement root, float fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetSingle(out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static int ReadInt(JsonElement root, int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
                return parsed;
        }

        return fallback;
    }
}
