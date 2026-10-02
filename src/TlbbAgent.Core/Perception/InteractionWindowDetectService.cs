using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class InteractionWindowDetectService : IInteractionWindowDetectService, IDisposable
{
    private const string QuestLogPanelClass = "quest_log_panel";
    private const string QuestLogPanelSha256 = "15ab659c94db902aba18a9ebe146465d4a1baa1accf1223d0dd2964dd9795f4e";
    private static readonly HashSet<string> PrimaryClasses = new(StringComparer.Ordinal)
    {
        "bag_panel",
        "shop_panel",
        "monthly_card_panel",
        "bank_panel",
        "material_panel"
    };

    private readonly ILogService _log;
    private readonly YoloV8OnnxDetector? _detector;
    private readonly YoloV8OnnxDetector? _questLogPanelDetector;
    private readonly string _unavailableReason = "";

    public InteractionWindowDetectService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        var reasons = new List<string>();

        var primary = TryLoadDetector(
            artifacts.FindInteractionWindowModel(),
            "Interaction window L1 V3",
            expectedSha256: "");
        _detector = primary.Detector;
        if (!string.IsNullOrWhiteSpace(primary.Error))
            reasons.Add(primary.Error);

        var quest = TryLoadDetector(
            artifacts.FindQuestLogPanelModel(),
            "Quest log panel L1 V3 candidate",
            QuestLogPanelSha256);
        _questLogPanelDetector = quest.Detector;
        if (!string.IsNullOrWhiteSpace(quest.Error))
            reasons.Add(quest.Error);

        _unavailableReason = string.Join("; ", reasons);
    }

    public InteractionWindowPerception Detect(CaptureResult frame) => Detect(frame, includeQuestLogPanel: false);

    public InteractionWindowPerception Detect(CaptureResult frame, bool includeQuestLogPanel)
    {
        if (_detector is null && (!includeQuestLogPanel || _questLogPanelDetector is null))
            return InteractionWindowPerception.Unavailable(frame.Sequence, _unavailableReason);

        var detections = new List<InteractionWindowDetection>();
        var versions = new List<string>();
        var messages = new List<string>();
        var inferenceMilliseconds = 0d;
        var inferenceSucceeded = false;
        var questState = VisionTriState.Unknown;

        if (_detector is not null)
        {
            try
            {
                var result = _detector.Detect(frame);
                inferenceSucceeded = true;
                inferenceMilliseconds += result.InferenceMilliseconds;
                versions.Add($"interaction-window-v3:{result.ModelId}");
                var primaryDetections = result.Detections
                    .Where(item => PrimaryClasses.Contains(item.Label))
                    .Select(item => ToDetection(item, frame))
                    .Where(item => !item.Box.IsEmpty)
                    .ToArray();
                detections.AddRange(primaryDetections);
                messages.Add($"primary={primaryDetections.Length}");
            }
            catch (Exception ex)
            {
                messages.Add($"primary_error={ex.Message}");
                _log.Warn("Vision", $"Interaction window L1 V3 inference failed: {ex.Message}");
            }
        }

        if (includeQuestLogPanel && _questLogPanelDetector is not null)
        {
            try
            {
                var result = _questLogPanelDetector.Detect(frame);
                inferenceSucceeded = true;
                inferenceMilliseconds += result.InferenceMilliseconds;
                versions.Add($"quest-log-panel-v3:{result.ModelId}");
                var questDetections = result.Detections
                    .Where(item => string.Equals(item.Label, QuestLogPanelClass, StringComparison.Ordinal))
                    .Select(item => ToDetection(item, frame))
                    .Where(item => !item.Box.IsEmpty)
                    .OrderByDescending(item => item.Confidence)
                    .ToArray();
                if (questDetections.Length == 0)
                {
                    questState = VisionTriState.Absent;
                    messages.Add("quest=absent");
                }
                else if (questDetections.Length == 1)
                {
                    questState = VisionTriState.Present;
                    detections.Add(questDetections[0]);
                    messages.Add($"quest=present@{questDetections[0].Confidence:0.000}");
                }
                else
                {
                    questState = VisionTriState.Unknown;
                    messages.Add($"quest=unknown_ambiguous({questDetections.Length})");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"quest_error={ex.Message}");
                _log.Warn("Vision", $"Quest log panel L1 V3 inference failed: {ex.Message}");
            }
        }

        if (!inferenceSucceeded)
        {
            return InteractionWindowPerception.Unavailable(
                frame.Sequence,
                string.Join("; ", messages),
                string.Join(";", versions));
        }

        return new InteractionWindowPerception
        {
            FrameSequence = frame.Sequence,
            ObservedAt = frame.CapturedAt,
            Available = true,
            QuestLogPanelRequested = includeQuestLogPanel,
            QuestLogPanelState = questState,
            Detections = detections.OrderByDescending(item => item.Confidence).ToArray(),
            ModelVersion = string.Join(";", versions),
            InferenceMilliseconds = inferenceMilliseconds,
            Source = PerceptionSourceKind.OnnxLayout,
            Message = string.Join("; ", messages)
        };
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _questLogPanelDetector?.Dispose();
    }

    private (YoloV8OnnxDetector? Detector, string Error) TryLoadDetector(
        ModelArtifactInfo artifact,
        string displayName,
        string expectedSha256)
    {
        if (!artifact.IsAvailable)
            return (null, artifact.Message);

        try
        {
            var config = AuxiliaryYoloModelConfig.Load(artifact.ConfigPath);
            var detector = new YoloV8OnnxDetector(
                artifact.ModelPath,
                config.Classes,
                config.ClassThresholds,
                config.InputSize,
                config.NmsIouThreshold,
                config.MaximumDetections,
                string.IsNullOrWhiteSpace(expectedSha256) ? config.Sha256 : expectedSha256);
            _log.Info(
                "Vision",
                $"{displayName} loaded: model={detector.ModelId}, classes={config.Classes.Count}.");
            return (detector, "");
        }
        catch (Exception ex)
        {
            var reason = $"{displayName} load failed: {ex.Message}";
            _log.Error("Vision", reason);
            return (null, reason);
        }
    }

    private static InteractionWindowDetection ToDetection(YoloDetection item, CaptureResult frame) =>
        new()
        {
            ClassName = item.Label,
            Box = VisionBoxUtil.FromRect(item.Box, frame.Width, frame.Height),
            Confidence = item.Confidence
        };
}
