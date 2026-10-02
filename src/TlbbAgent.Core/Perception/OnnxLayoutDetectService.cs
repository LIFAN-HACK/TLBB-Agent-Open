using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class OnnxLayoutDetectService : ILayoutDetectService, IDisposable
{
    private readonly ILogService _log;
    private readonly YoloV8OnnxDetector? _detector;
    private readonly string _unavailableReason = "";

    public OnnxLayoutDetectService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        var artifact = artifacts.FindL1LayoutModel();
        if (!artifact.IsAvailable)
        {
            _unavailableReason = artifact.Message;
            return;
        }

        try
        {
            var config = LayoutModelConfig.Load(artifact.ConfigPath);
            _detector = new YoloV8OnnxDetector(
                artifact.ModelPath,
                config.Classes,
                config.ClassConfidenceThresholds,
                config.InputSize,
                config.NmsIou,
                config.MaximumDetections,
                config.Sha256);

            _log.Info(
                "Vision",
                $"L1 V2 loaded: model={_detector.ModelId}, sha256={_detector.Sha256}, classes={config.Classes.Count}.");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"L1 V2 load failed: {ex.Message}";
            _log.Error("Vision", _unavailableReason);
        }
    }

    public LayoutSnapshot Detect(CaptureResult frame)
    {
        if (_detector is null)
            return LayoutSnapshot.Unavailable(frame.Width, frame.Height, _unavailableReason);

        try
        {
            var result = _detector.Detect(frame);
            var mapped = result.Detections
                .Select(item => new LayoutDetectionItem(
                    MapClass(item.Label),
                    ToVisionBox(item.Box, frame.Width, frame.Height),
                    item.Confidence,
                    item.Label))
                .Where(item => item.Class != LayoutClass.Unknown && !item.Box.IsEmpty)
                .ToArray();
            var detections = NormalizeLayout(
                mapped,
                frame.Width,
                frame.Height,
                GameClientHudLocator.ResolveClientTop(frame));

            return new LayoutSnapshot(
                frame.Width,
                frame.Height,
                PerceptionSourceKind.OnnxLayout,
                frame.CapturedAt,
                detections,
                result.ModelId,
                $"L1 V2 detections={detections.Count}/{mapped.Length}",
                result.InferenceMilliseconds);
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"L1 V2 inference failed: {ex.Message}");
            return LayoutSnapshot.Unavailable(frame.Width, frame.Height, ex.Message) with
            {
                ModelId = _detector.ModelId
            };
        }
    }

    public void Dispose() => _detector?.Dispose();

    private static LayoutClass MapClass(string label) => label switch
    {
        "target_panel" => LayoutClass.TargetPanel,
        "target_hp_bar" => LayoutClass.TargetHpBar,
        "target_name" => LayoutClass.TargetName,
        "player_status_panel" => LayoutClass.PlayerStatusPanel,
        "pet_status_panel" => LayoutClass.PetStatusPanel,
        "skill_bar" => LayoutClass.SkillBar,
        "minimap" => LayoutClass.Minimap,
        _ => LayoutClass.Unknown
    };

    private static VisionBox ToVisionBox(VisionRectF box, int width, int height)
    {
        var left = Math.Clamp((int)MathF.Floor(box.X), 0, width);
        var top = Math.Clamp((int)MathF.Floor(box.Y), 0, height);
        var right = Math.Clamp((int)MathF.Ceiling(box.Right), left, width);
        var bottom = Math.Clamp((int)MathF.Ceiling(box.Bottom), top, height);
        return VisionBox.FromXyxy(left, top, right, bottom);
    }

    private static IReadOnlyList<LayoutDetectionItem> NormalizeLayout(
        IReadOnlyList<LayoutDetectionItem> detections,
        int frameWidth,
        int frameHeight,
        int clientTop)
    {
        var result = new List<LayoutDetectionItem>();
        var hudScale = ResolveHudScale(detections);
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * hudScale));
        AddBest(
            result,
            detections,
            LayoutClass.PlayerStatusPanel,
            box => IsPlausiblePlayerPanel(box, frameWidth, frameHeight),
            _ => new VisionBox(
                0,
                clientTop,
                Math.Min(Scale(240), frameWidth),
                Math.Min(Scale(66), frameHeight - clientTop)));
        AddBest(
            result,
            detections,
            LayoutClass.PetStatusPanel,
            box => IsPlausiblePetPanel(box, frameWidth, frameHeight),
            _ => VisionBox.FromXyxy(
                Math.Min(Scale(78), frameWidth),
                Math.Min(clientTop + Scale(70), frameHeight),
                Math.Min(Scale(240), frameWidth),
                Math.Min(clientTop + Scale(138), frameHeight)));
        AddBest(
            result,
            detections,
            LayoutClass.SkillBar,
            box => IsPlausibleSkillBar(box, frameWidth, frameHeight),
            box => box.Bottom >= frameHeight - 20
                ? VisionBox.FromXyxy(
                    Math.Max(0, frameWidth / 2 - Scale(260)),
                    Math.Max(0, frameHeight - Scale(142)),
                    Math.Min(frameWidth, frameWidth / 2 + Scale(260)),
                    frameHeight)
                : box);
        AddBest(
            result,
            detections,
            LayoutClass.Minimap,
            box => IsPlausibleMinimap(box, frameWidth, frameHeight),
            _ => VisionBox.FromXyxy(
                Math.Max(0, frameWidth - Scale(240)),
                clientTop,
                frameWidth,
                Math.Min(frameHeight, clientTop + Scale(228))));

        var panel = detections
            .Where(item => item.Class == LayoutClass.TargetPanel)
            .Where(item => IsPlausibleTargetPanel(item.Box, frameWidth, frameHeight))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (panel is null)
            return result;

        var rawPanel = panel.Box;
        var canonicalPanel = VisionBox.FromXyxy(
            Math.Min(Scale(248), frameWidth),
            clientTop,
            Math.Min(Scale(508), frameWidth),
            Math.Min(frameHeight, clientTop + Scale(82)));
        if (canonicalPanel.IsEmpty)
            return result;

        result.Add(panel with { Box = canonicalPanel });
        var expanded = Expand(rawPanel, frameWidth, frameHeight);
        AddCanonicalTargetChild(
            result,
            detections,
            LayoutClass.TargetHpBar,
            expanded,
            VisionBox.FromXyxy(
                canonicalPanel.X + Scale(28),
                canonicalPanel.Y + Scale(26),
                canonicalPanel.X + Scale(162),
                canonicalPanel.Y + Scale(50)),
            panel.Confidence,
            "target_hp_bar_derived");
        AddCanonicalTargetChild(
            result,
            detections,
            LayoutClass.TargetName,
            expanded,
            VisionBox.FromXyxy(
                canonicalPanel.X + Scale(26),
                canonicalPanel.Y + Scale(1),
                canonicalPanel.X + Scale(164),
                canonicalPanel.Y + Scale(28)),
            panel.Confidence,
            "target_name_derived");
        return result;
    }

    private static double ResolveHudScale(IReadOnlyList<LayoutDetectionItem> detections)
    {
        var candidates = new[]
        {
            ScaleCandidate(detections, LayoutClass.PlayerStatusPanel, 240),
            ScaleCandidate(detections, LayoutClass.Minimap, 240),
            ScaleCandidate(detections, LayoutClass.TargetPanel, 260),
            ScaleCandidate(detections, LayoutClass.SkillBar, 520),
            ScaleCandidate(detections, LayoutClass.PetStatusPanel, 162)
        }
        .Where(value => value is >= 0.45 and <= 1.60)
        .ToArray();
        if (candidates.Length == 0)
            return 1.0;

        var ordered = candidates.OrderBy(value => value).ToArray();
        var median = ordered[ordered.Length / 2];
        return median is >= 0.85 and <= 1.15 ? 1.0 : median;
    }

    private static double ScaleCandidate(
        IEnumerable<LayoutDetectionItem> detections,
        LayoutClass targetClass,
        double canonicalWidth)
    {
        var best = detections
            .Where(item => item.Class == targetClass)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        return best is null ? 0 : best.Box.Width / canonicalWidth;
    }

    private static void AddBest(
        ICollection<LayoutDetectionItem> output,
        IEnumerable<LayoutDetectionItem> detections,
        LayoutClass targetClass,
        Func<VisionBox, bool> plausible,
        Func<VisionBox, VisionBox>? canonicalize = null)
    {
        var best = detections
            .Where(item => item.Class == targetClass && plausible(item.Box))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (best is not null)
            output.Add(canonicalize is null ? best : best with { Box = canonicalize(best.Box) });
    }

    private static void AddCanonicalTargetChild(
        ICollection<LayoutDetectionItem> output,
        IEnumerable<LayoutDetectionItem> detections,
        LayoutClass targetClass,
        VisionBox rawPanelSearchArea,
        VisionBox canonicalBox,
        double panelConfidence,
        string derivedLabel)
    {
        var best = detections
            .Where(item => item.Class == targetClass && BoxCenterInside(item.Box, rawPanelSearchArea))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        output.Add(best is null
            ? new LayoutDetectionItem(targetClass, canonicalBox, panelConfidence * 0.90, derivedLabel)
            : best with { Box = canonicalBox });
    }

    private static VisionBox Expand(VisionBox panel, int frameWidth, int frameHeight)
    {
        var left = Math.Max(0, panel.X - panel.Width / 10);
        var top = Math.Max(0, panel.Y - panel.Height / 4);
        var right = Math.Min(frameWidth, panel.Right + panel.Width / 10);
        var bottom = Math.Min(frameHeight, panel.Bottom + panel.Height / 4);
        return VisionBox.FromXyxy(left, top, right, bottom);
    }

    private static bool IsPlausibleTargetPanel(VisionBox box, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return false;

        var centerX = box.X + box.Width / 2.0;
        return box.X >= 0
               && centerX <= frameWidth * 0.50
               && box.Y <= Math.Max(180, frameHeight * 0.20)
               && box.Width is >= 95 and <= 520
               && box.Height is >= 25 and <= 200;
    }

    private static bool IsPlausiblePlayerPanel(VisionBox box, int frameWidth, int frameHeight) =>
        IsValidFrame(frameWidth, frameHeight)
        && box.X <= Math.Max(120, frameWidth * 0.10)
        && box.Y <= Math.Max(180, frameHeight * 0.20)
        && box.Width is >= 90 and <= 480
        && box.Height is >= 24 and <= 200;

    private static bool IsPlausiblePetPanel(VisionBox box, int frameWidth, int frameHeight) =>
        IsValidFrame(frameWidth, frameHeight)
        && box.X <= Math.Max(280, frameWidth * 0.20)
        && box.Y <= Math.Max(240, frameHeight * 0.30)
        && box.Width is >= 55 and <= 360
        && box.Height is >= 22 and <= 220;

    private static bool IsPlausibleSkillBar(VisionBox box, int frameWidth, int frameHeight)
    {
        if (!IsValidFrame(frameWidth, frameHeight))
            return false;
        var centerX = box.X + box.Width / 2.0;
        return centerX >= frameWidth * 0.20
               && centerX <= frameWidth * 0.80
               && box.Y >= frameHeight * 0.45
               && box.Bottom >= frameHeight * 0.75
               && box.Width is >= 220 and <= 820
               && box.Height is >= 55 and <= 320;
    }

    private static bool IsPlausibleMinimap(VisionBox box, int frameWidth, int frameHeight)
    {
        if (!IsValidFrame(frameWidth, frameHeight))
            return false;
        var centerX = box.X + box.Width / 2.0;
        return centerX >= frameWidth * 0.60
               && box.Y <= frameHeight * 0.25
               && box.Width is >= 90 and <= 500
               && box.Height is >= 90 and <= 500;
    }

    private static bool IsValidFrame(int width, int height) => width > 0 && height > 0;

    private static bool BoxCenterInside(VisionBox box, VisionBox container)
    {
        var centerX = box.X + box.Width / 2;
        var centerY = box.Y + box.Height / 2;
        return centerX >= container.X
               && centerX <= container.Right
               && centerY >= container.Y
               && centerY <= container.Bottom;
    }

    private sealed record LayoutModelConfig(
        int InputSize,
        float Confidence,
        float NmsIou,
        int MaximumDetections,
        string Sha256,
        IReadOnlyList<string> Classes,
        IReadOnlyDictionary<int, float> ClassConfidenceThresholds)
    {
        public static LayoutModelConfig Load(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (root.TryGetProperty("input", out var input)
                && root.TryGetProperty("postprocess", out var postprocess))
            {
                var classesObject = root.GetProperty("classes");
                var classes = classesObject
                    .EnumerateObject()
                    .Select(item => new
                    {
                        Index = int.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture),
                        Label = item.Value.GetString() ?? ""
                    })
                    .OrderBy(item => item.Index)
                    .Select(item => item.Label)
                    .Where(item => item.Length > 0)
                    .ToArray();
                var sha = root.TryGetProperty("hashes", out var hashes)
                          && hashes.TryGetProperty("onnx_sha256", out var hash)
                    ? hash.GetString() ?? ""
                    : "";

                var confidence = postprocess.GetProperty("confidence_threshold").GetSingle();
                var classThresholds = classes
                    .Select((_, index) => new KeyValuePair<int, float>(index, confidence))
                    .ToDictionary(item => item.Key, item => item.Value);
                if (postprocess.TryGetProperty("class_confidence_thresholds", out var overrides)
                    && overrides.ValueKind == JsonValueKind.Object)
                {
                    foreach (var item in overrides.EnumerateObject())
                    {
                        var classIndex = int.TryParse(item.Name, out var parsedIndex)
                            ? parsedIndex
                            : Array.FindIndex(classes, label => string.Equals(label, item.Name, StringComparison.Ordinal));
                        if (classIndex >= 0 && classIndex < classes.Length)
                            classThresholds[classIndex] = Math.Clamp(item.Value.GetSingle(), 0.01f, 0.99f);
                    }
                }

                return new LayoutModelConfig(
                    input.GetProperty("image_size").GetInt32(),
                    confidence,
                    postprocess.GetProperty("iou_threshold").GetSingle(),
                    postprocess.GetProperty("max_detections").GetInt32(),
                    sha,
                    classes,
                    classThresholds);
            }

            var legacyClasses = root.GetProperty("classes")
                .EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(item => item.Length > 0)
                .ToArray();
            return new LayoutModelConfig(
                root.GetProperty("input_size").GetInt32(),
                root.GetProperty("conf_threshold").GetSingle(),
                root.GetProperty("nms_iou_threshold").GetSingle(),
                root.TryGetProperty("maximum_detections", out var maximum) ? maximum.GetInt32() : 100,
                root.TryGetProperty("sha256", out var legacyHash) ? legacyHash.GetString() ?? "" : "",
                legacyClasses,
                legacyClasses
                    .Select((_, index) => new KeyValuePair<int, float>(index, root.GetProperty("conf_threshold").GetSingle()))
                    .ToDictionary(item => item.Key, item => item.Value));
        }
    }
}
