using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class TargetHpSlotService : ITargetHpSlotService, IDisposable
{
    private readonly ILogService _log;
    private readonly YoloV8OnnxDetector? _detector;
    private readonly TargetHpSlotModelConfig? _config;
    private readonly string _unavailableReason = "";

    public TargetHpSlotService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        var artifact = artifacts.FindTargetHpSlotModel();
        if (!artifact.IsAvailable)
        {
            _unavailableReason = artifact.Message;
            return;
        }

        try
        {
            _config = TargetHpSlotModelConfig.Load(artifact.ConfigPath);
            _detector = new YoloV8OnnxDetector(
                artifact.ModelPath,
                _config.Classes,
                new Dictionary<int, float>
                {
                    [0] = _config.HpGroupConfidence,
                    [1] = _config.FillConfidence
                },
                _config.InputSize,
                _config.NmsIou,
                _config.MaximumDetections,
                _config.Sha256);

            _log.Info(
                "Vision",
                $"目标血量模型已加载：{_config.ModelVersion}，SHA256={_detector.Sha256}。");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"Target HP slot load failed: {ex.Message}";
            _log.Error("Vision", _unavailableReason);
        }
    }

    public TargetHpSlotResult Analyze(CaptureResult targetPanelCrop)
    {
        if (_detector is null || _config is null)
            return TargetHpSlotResult.Unavailable(_unavailableReason);

        try
        {
            var inference = _detector.Detect(targetPanelCrop);
            var group = inference.Detections
                .Where(item => item.ClassId == 0)
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            var fill = inference.Detections
                .Where(item => item.ClassId == 1)
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();

            if (group is null)
            {
                return new TargetHpSlotResult(
                    TargetHpSlotSignal.Unknown,
                    false,
                    null,
                    0,
                    false,
                    null,
                    fill?.Confidence ?? 0,
                    null,
                    _config.ModelVersion,
                    new[] { "hp_bar_group missing" },
                    inference.InferenceMilliseconds);
            }

            var fillVisible = fill is not null && fill.Box.Width >= _config.MinNativeFillWidth;
            float? ratio = null;
            if (fillVisible)
            {
                var referenceWidth = _config.ReferenceFillWidth * targetPanelCrop.Width / _config.ReferenceCropWidth;
                if (referenceWidth > 0)
                    ratio = Math.Clamp(fill!.Box.Width / referenceWidth, 0, 1);
            }

            return new TargetHpSlotResult(
                fillVisible ? TargetHpSlotSignal.Alive : TargetHpSlotSignal.DeadByHpBottomCandidate,
                true,
                group.Box,
                group.Confidence,
                fillVisible,
                fill?.Box,
                fill?.Confidence ?? 0,
                ratio,
                _config.ModelVersion,
                Array.Empty<string>(),
                inference.InferenceMilliseconds);
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"目标血量推理失败：{ex.Message}");
            return TargetHpSlotResult.Unavailable(ex.Message, _config.ModelVersion);
        }
    }

    public void Dispose() => _detector?.Dispose();

    private sealed record TargetHpSlotModelConfig(
        string ModelVersion,
        string Sha256,
        int InputSize,
        IReadOnlyList<string> Classes,
        float HpGroupConfidence,
        float FillConfidence,
        float MinNativeFillWidth,
        float NmsIou,
        int MaximumDetections,
        float ReferenceCropWidth,
        float ReferenceFillWidth)
    {
        public static TargetHpSlotModelConfig Load(string path)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var thresholds = root.GetProperty("class_conf_thresholds");
            var reference = root.GetProperty("native_target_frame_reference");

            return new TargetHpSlotModelConfig(
                root.GetProperty("model_version").GetString() ?? "target-hp-slot",
                root.GetProperty("sha256").GetString() ?? "",
                root.GetProperty("input_size").GetInt32(),
                root.GetProperty("classes").EnumerateArray().Select(item => item.GetString() ?? "").ToArray(),
                thresholds.GetProperty("hp_bar_group").GetSingle(),
                thresholds.GetProperty("hp_fill_visible").GetSingle(),
                root.GetProperty("min_fill_width_px_in_target_frame").GetSingle(),
                root.GetProperty("nms_iou_threshold").GetSingle(),
                root.GetProperty("maximum_detections").GetInt32(),
                reference.GetProperty("width").GetSingle(),
                reference.GetProperty("active_row_fill_width_px").GetSingle());
        }
    }
}
