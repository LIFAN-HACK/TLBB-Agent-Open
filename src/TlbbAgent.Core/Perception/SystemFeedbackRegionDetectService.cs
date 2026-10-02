using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class SystemFeedbackRegionDetectService : ISystemFeedbackRegionDetectService, IDisposable
{
    private readonly ILogService _log;
    private readonly object _detectorGate = new();
    private readonly YoloV8OnnxDetector? _detector;
    private readonly string _unavailableReason = "";

    public SystemFeedbackRegionDetectService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        var artifact = artifacts.FindSystemFeedbackRegionModel();
        if (!artifact.IsAvailable)
        {
            _unavailableReason = artifact.Message;
            return;
        }

        try
        {
            var config = AuxiliaryYoloModelConfig.Load(artifact.ConfigPath);
            _detector = new YoloV8OnnxDetector(
                artifact.ModelPath,
                config.Classes,
                config.ClassThresholds,
                config.InputSize,
                config.NmsIouThreshold,
                config.MaximumDetections,
                config.Sha256);
            _log.Info("Vision", $"System feedback L1 V2 loaded: model={_detector.ModelId}.");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"System feedback L1 V2 load failed: {ex.Message}";
            _log.Error("Vision", _unavailableReason);
        }
    }

    public SystemFeedbackPerception Detect(
        CaptureResult frame,
        IOpenTextRecognizer openText,
        bool includeOpenText = true)
    {
        if (_detector is null)
        {
            return SystemFeedbackPerception.Unavailable(frame.Sequence, _unavailableReason) with
            {
                TextRecognitionRequested = includeOpenText
            };
        }

        try
        {
            YoloInferenceResult inference;
            lock (_detectorGate)
                inference = _detector.Detect(frame);
            var raw = inference.Detections
                .Where(item => string.Equals(item.Label, "system_feedback_region", StringComparison.Ordinal))
                .Select(item => new SystemFeedbackRegionDetection(
                    VisionBoxUtil.FromRect(item.Box, frame.Width, frame.Height),
                    item.Confidence,
                    "system-feedback-region-l1-v2"))
                .Where(item => !item.Box.IsEmpty)
                .ToArray();
            var merged = FilterAndMerge(raw, frame.Width, frame.Height);
            var observations = new List<SystemFeedbackObservation>(merged.Count);
            var openTextAvailable = includeOpenText && openText.IsAvailable;
            if (includeOpenText)
            {
                foreach (var region in merged)
                {
                    var crop = OpenTextCropUtil.Crop(frame, region.Box, pad: 2);
                    var text = crop is null || !openTextAvailable
                        ? OpenTextResult.Empty(OpenTextSlot.Toast, "System feedback crop or OpenText unavailable.", openText.EngineId)
                        : openText.Recognize(crop, OpenTextSlot.Toast);
                    var normalized = MaterialCraftFeedbackClassifier.NormalizeText(text.Text);
                    observations.Add(new SystemFeedbackObservation(
                        frame.Sequence,
                        region.Box,
                        region.Confidence,
                        text,
                        normalized,
                        MaterialCraftFeedbackClassifier.ClassifySemantic(normalized),
                        frame.CapturedAt));
                }
            }

            return new SystemFeedbackPerception
            {
                FrameSequence = frame.Sequence,
                RequestedFrameSequence = frame.Sequence,
                DetectionRequested = true,
                TextRecognitionRequested = includeOpenText,
                TextRecognitionCompleted = includeOpenText && (merged.Count == 0 || openTextAvailable),
                Available = true,
                RawRegions = raw,
                Observations = observations,
                ModelVersion = $"system-feedback-l1-v2:{inference.ModelId}",
                InferenceMilliseconds = inference.InferenceMilliseconds,
                Message = $"raw={raw.Length}, central_merged={merged.Count}, " +
                          $"ocr={(includeOpenText ? observations.Count(item => item.Text.HasText) : "skipped")}",
                Source = PerceptionSourceKind.Fused
            };
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"System feedback L1 V2 inference failed: {ex.Message}");
            return SystemFeedbackPerception.Unavailable(
                frame.Sequence,
                ex.Message,
                $"system-feedback-l1-v2:{_detector.ModelId}") with
            {
                TextRecognitionRequested = includeOpenText
            };
        }
    }

    public static IReadOnlyList<SystemFeedbackRegionDetection> FilterAndMerge(
        IReadOnlyList<SystemFeedbackRegionDetection> detections,
        int frameWidth,
        int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return Array.Empty<SystemFeedbackRegionDetection>();

        var filtered = detections
            .Where(item => IsInCentralLane(item.Box, frameWidth, frameHeight))
            .OrderBy(item => item.Box.Y)
            .ThenBy(item => item.Box.X)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var leftIndex = 0; leftIndex < filtered.Count && !changed; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < filtered.Count; rightIndex++)
                {
                    var left = filtered[leftIndex];
                    var right = filtered[rightIndex];
                    if (!ShouldMerge(left.Box, right.Box, frameWidth, frameHeight))
                        continue;

                    filtered[leftIndex] = new SystemFeedbackRegionDetection(
                        Union(left.Box, right.Box),
                        Math.Max(left.Confidence, right.Confidence),
                        "system-feedback-region-l1-v2:merged");
                    filtered.RemoveAt(rightIndex);
                    changed = true;
                    break;
                }
            }
        }

        return filtered
            .OrderBy(item => item.Box.Y)
            .ThenBy(item => item.Box.X)
            .ToArray();
    }

    public void Dispose() => _detector?.Dispose();

    private static bool IsInCentralLane(VisionBox box, int width, int height)
    {
        if (box.IsEmpty)
            return false;
        var centerX = box.X + box.Width / 2.0;
        var centerY = box.Y + box.Height / 2.0;
        return Math.Abs(centerX - width / 2.0) <= width * 0.11
               && centerY / height >= 0.015
               && centerY / height <= 0.27;
    }

    private static bool ShouldMerge(VisionBox left, VisionBox right, int width, int height)
    {
        var horizontalGap = Math.Max(0, Math.Max(left.X, right.X) - Math.Min(left.X + left.Width, right.X + right.Width));
        var verticalGap = Math.Max(0, Math.Max(left.Y, right.Y) - Math.Min(left.Y + left.Height, right.Y + right.Height));
        var verticalOverlap = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var minimumHeight = Math.Max(1, Math.Min(left.Height, right.Height));
        var sameBand = verticalOverlap >= minimumHeight * 0.35
                       || Math.Abs(CenterY(left) - CenterY(right)) <= Math.Max(left.Height, right.Height) * 0.70;
        var horizontalAdjacent = sameBand && horizontalGap <= width * 0.08;
        var verticalAdjacent = verticalGap <= height * 0.025
                               && Math.Abs(CenterX(left) - CenterX(right)) <= width * 0.12;
        return horizontalAdjacent || verticalAdjacent;
    }

    private static double CenterX(VisionBox box) => box.X + box.Width / 2.0;
    private static double CenterY(VisionBox box) => box.Y + box.Height / 2.0;

    private static VisionBox Union(VisionBox left, VisionBox right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var rightEdge = Math.Max(left.X + left.Width, right.X + right.Width);
        var bottom = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new VisionBox(x, y, rightEdge - x, bottom - y);
    }
}
