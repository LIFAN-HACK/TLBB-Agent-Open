using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class BuffAnchorDetectService : IBuffAnchorDetectService, IDisposable
{
    private const string FeatureFlag = "TLBB_AGENT_BUFF_ANCHOR_ENABLED";
    private const int MaximumColumns = 6;
    private const int MaximumRows = 5;

    private readonly ILogService _log;
    private readonly YoloV8OnnxDetector? _detector;
    private readonly string _unavailableReason = "";

    public BuffAnchorDetectService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        if (!IsFeatureEnabled())
        {
            _unavailableReason = $"Buff anchor feature flag {FeatureFlag} is disabled.";
            return;
        }

        var artifact = artifacts.FindBuffAnchorModel();
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
            _log.Info("Vision", $"Buff anchor L1 loaded under feature flag: model={_detector.ModelId}.");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"Buff anchor L1 load failed: {ex.Message}";
            _log.Error("Vision", _unavailableReason);
        }
    }

    public BuffAnchorPerception Detect(CaptureResult frame)
    {
        if (_detector is null)
            return BuffAnchorPerception.Unavailable(frame.Sequence, _unavailableReason);

        try
        {
            var result = _detector.Detect(frame);
            var best = result.Detections
                .Where(item => item.Label == "buff_anchor_row")
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (best is null)
            {
                return new BuffAnchorPerception
                {
                    FrameSequence = frame.Sequence,
                    Available = true,
                    ModelVersion = $"buff-anchor-r3:{result.ModelId}",
                    InferenceMilliseconds = result.InferenceMilliseconds,
                    Source = PerceptionSourceKind.OnnxLayout,
                    Message = "No visible Buff anchor row; mounted/fatigue remain unknown."
                };
            }

            var anchor = VisionBoxUtil.FromRect(best.Box, frame.Width, frame.Height);
            var geometry = DeriveGrid(anchor, frame.Width, frame.Height);
            return new BuffAnchorPerception
            {
                FrameSequence = frame.Sequence,
                Available = true,
                AnchorBox = anchor,
                CandidateSlots = geometry.Slots,
                CandidateRows = geometry.Rows,
                CandidateColumns = geometry.Columns,
                Mounted = VisionTriState.Unknown,
                Fatigue = VisionTriState.Unknown,
                PetActiveSkillBlocked = true,
                Confidence = best.Confidence,
                ModelVersion = $"buff-anchor-r3:{result.ModelId}",
                InferenceMilliseconds = result.InferenceMilliseconds,
                Source = PerceptionSourceKind.OnnxLayout,
                Message = $"anchor only; grid={geometry.Rows}x{geometry.Columns}; identity L2 unavailable"
            };
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"Buff anchor L1 inference failed: {ex.Message}");
            return BuffAnchorPerception.Unavailable(
                frame.Sequence,
                ex.Message,
                $"buff-anchor-r3:{_detector.ModelId}");
        }
    }

    public void Dispose() => _detector?.Dispose();

    private static GridGeometry DeriveGrid(VisionBox anchor, int frameWidth, int frameHeight)
    {
        if (anchor.IsEmpty)
            return new GridGeometry(0, 0, Array.Empty<VisionBox>());

        var slotSize = Math.Clamp(anchor.Height, 24, 40);
        var columns = ResolveColumnCount(anchor.Width, slotSize);
        var pitch = columns <= 1
            ? slotSize + 2
            : Math.Max(slotSize, (int)Math.Round((anchor.Width - slotSize) / (double)(columns - 1)));
        var slots = new List<VisionBox>(MaximumRows * columns);
        for (var row = 0; row < MaximumRows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var box = new VisionBox(
                    anchor.X + column * pitch,
                    anchor.Y + row * pitch,
                    slotSize,
                    slotSize);
                if (VisionBoxUtil.IsInside(box, frameWidth, frameHeight))
                    slots.Add(box);
            }
        }

        var rows = slots.Count == 0 ? 0 : slots.Max(box => (box.Y - anchor.Y) / pitch) + 1;
        return new GridGeometry(rows, columns, slots);
    }

    private static int ResolveColumnCount(int width, int slotSize)
    {
        var bestColumns = 1;
        var bestError = double.MaxValue;
        for (var columns = 3; columns <= MaximumColumns; columns++)
        {
            var pitch = (width - slotSize) / (double)(columns - 1);
            if (pitch < slotSize - 1 || pitch > slotSize + 6)
                continue;
            var error = Math.Abs(pitch - (slotSize + 2));
            if (error < bestError)
            {
                bestError = error;
                bestColumns = columns;
            }
        }

        return bestColumns;
    }

    private static bool IsFeatureEnabled()
    {
        var value = Environment.GetEnvironmentVariable(FeatureFlag);
        return value is not null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record GridGeometry(int Rows, int Columns, IReadOnlyList<VisionBox> Slots);
}
