using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class RightSkillBarDetectService : IRightSkillBarDetectService, IDisposable
{
    private const float CandidateThreshold = 0.001f;
    private const float BusinessThreshold = 0.25f;
    private const float NmsIou = 0.70f;
    private const int MaximumCandidates = 20;
    private const int SlotWidth = 32;
    private const int SlotHeight = 32;
    private const int ColumnPitch = 44;
    private const int RowPitch = 35;
    private const int Columns = 2;
    private const int Rows = 10;
    private const int AnchorSearchRadiusX = 6;
    private const int AnchorSearchUp = 6;
    private const int AnchorSearchDown = 24;
    private const int ExpandedAnchorSearchDown = 80;
    private const int ExpandedAnchorModelHeightThreshold = 370;

    private readonly ILogService _log;
    private readonly YoloV8OnnxDetector? _detector640;
    private readonly YoloV8OnnxDetector? _detector1280;
    private readonly string _unavailableReason = "";

    public RightSkillBarDetectService(IModelArtifactRegistry artifacts, ILogService log)
    {
        _log = log;
        var artifact = artifacts.FindRightSkillBarModels();
        if (!artifact.IsAvailable)
        {
            _unavailableReason = artifact.Message;
            return;
        }

        try
        {
            var classes = new[] { "right_skill_bar" };
            var thresholds = new Dictionary<int, float> { [0] = CandidateThreshold };
            _detector640 = new YoloV8OnnxDetector(
                artifact.Model640Path,
                classes,
                thresholds,
                640,
                NmsIou,
                MaximumCandidates,
                artifact.Sha256640);
            _detector1280 = new YoloV8OnnxDetector(
                artifact.Model1280Path,
                classes,
                thresholds,
                1280,
                NmsIou,
                MaximumCandidates,
                artifact.Sha2561280);
            _log.Info(
                "Vision",
                $"Right skill bar L1 loaded: 640={_detector640.ModelId}, 1280={_detector1280.ModelId}.");
        }
        catch (Exception ex)
        {
            _detector640?.Dispose();
            _detector1280?.Dispose();
            _unavailableReason = $"Right skill bar L1 load failed: {ex.Message}";
            _log.Error("Vision", _unavailableReason);
        }
    }

    public RightSkillBarPerception Detect(CaptureResult frame)
    {
        var modelVersion = BuildModelVersion();
        if (_detector640 is null || _detector1280 is null)
            return RightSkillBarPerception.Unavailable(frame.Sequence, _unavailableReason, modelVersion);

        try
        {
            var result640 = _detector640.Detect(frame);
            var result1280 = _detector1280.Detect(frame);
            var candidates = ValidCandidates(result640, "640", frame)
                .Concat(ValidCandidates(result1280, "1280", frame))
                .OrderByDescending(item => item.Detection.Confidence)
                .ToArray();
            var best = candidates.FirstOrDefault();
            var elapsed = result640.InferenceMilliseconds + result1280.InferenceMilliseconds;
            if (best is null)
            {
                return RightSkillBarPerception.Unavailable(
                    frame.Sequence,
                    $"No legal right skill bar candidate. raw640={result640.Detections.Count}, raw1280={result1280.Detections.Count}.",
                    modelVersion) with
                {
                    InferenceMilliseconds = elapsed,
                    Source = PerceptionSourceKind.OnnxLayout
                };
            }

            var modelBox = VisionBoxUtil.FromRect(best.Detection.Box, frame.Width, frame.Height);
            var anchor = SnapAnchor(frame, modelBox.X, modelBox.Y, modelBox.Height);
            var slots = BuildSlots(frame, anchor.X, anchor.Y);
            return new RightSkillBarPerception
            {
                FrameSequence = frame.Sequence,
                Available = true,
                Confidence = best.Detection.Confidence,
                SourceProfile = best.Profile,
                BoundingBoxPx = modelBox,
                GeometryGatePassed = true,
                Slots = slots,
                ModelVersion = modelVersion,
                InferenceMilliseconds = elapsed,
                Source = PerceptionSourceKind.OnnxLayout,
                Message = $"profile={best.Profile}, legal={candidates.Length}, anchor={anchor.X},{anchor.Y}, complete_slots={slots.Count(item => item.IsComplete)}/20"
            };
        }
        catch (Exception ex)
        {
            _log.Warn("Vision", $"Right skill bar L1 inference failed: {ex.Message}");
            return RightSkillBarPerception.Unavailable(frame.Sequence, ex.Message, modelVersion);
        }
    }

    public void Dispose()
    {
        _detector640?.Dispose();
        _detector1280?.Dispose();
    }

    private IEnumerable<ProfileCandidate> ValidCandidates(
        YoloInferenceResult result,
        string profile,
        CaptureResult frame)
    {
        foreach (var detection in result.Detections)
        {
            if (detection.Confidence < BusinessThreshold)
                continue;
            var width = detection.Box.Width;
            var height = detection.Box.Height;
            var rightMargin = frame.Width - detection.Box.Right;
            if (width is < 52 or > 105
                || height is < 245 or > 440
                || rightMargin is < -6 or > 65
                || detection.Box.Y < 0
                || detection.Box.Bottom > frame.Height + 4)
            {
                continue;
            }

            yield return new ProfileCandidate(profile, detection);
        }
    }

    private static IReadOnlyList<RightSkillSlotGeometry> BuildSlots(CaptureResult frame, int x0, int y0)
    {
        var slots = new List<RightSkillSlotGeometry>(Rows * Columns);
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var box = new VisionBox(
                    x0 + column * ColumnPitch,
                    y0 + row * RowPitch,
                    SlotWidth,
                    SlotHeight);
                var complete = VisionBoxUtil.IsInside(box, frame.Width, frame.Height);
                slots.Add(new RightSkillSlotGeometry
                {
                    Index = row * Columns + column,
                    SlotId = $"r{row + 1}c{column + 1}",
                    Row = row,
                    Column = column,
                    Box = complete ? box : VisionBox.Empty,
                    IsComplete = complete,
                    BorderConfidence = complete ? ScoreSlotBorder(frame, box.X, box.Y) : 0
                });
            }
        }

        return slots;
    }

    private static AnchorCandidate SnapAnchor(CaptureResult frame, int modelX, int modelY, int modelHeight)
    {
        // Sparse or clipped bars can make the detector include isolated icons above the first full row.
        var searchDown = modelHeight > ExpandedAnchorModelHeightThreshold
            ? ExpandedAnchorSearchDown
            : AnchorSearchDown;
        var best = new AnchorCandidate(modelX, modelY, ScoreAnchor(frame, modelX, modelY));
        for (var dy = -AnchorSearchUp; dy <= searchDown; dy++)
        {
            for (var dx = -AnchorSearchRadiusX; dx <= AnchorSearchRadiusX; dx++)
            {
                var x = modelX + dx;
                var y = modelY + dy;
                var rawScore = ScoreAnchor(frame, x, y);
                var adjusted = rawScore - (Math.Abs(dx) + Math.Abs(dy)) * 0.0025;
                var currentAdjusted = best.Score
                                      - (Math.Abs(best.X - modelX) + Math.Abs(best.Y - modelY)) * 0.0025;
                if (adjusted > currentAdjusted)
                    best = new AnchorCandidate(x, y, rawScore);
            }
        }

        return best;
    }

    private static double ScoreAnchor(CaptureResult frame, int x, int y)
    {
        var offsets = new[]
        {
            (0, 0),
            (ColumnPitch, 0),
            (0, RowPitch),
            (ColumnPitch, RowPitch)
        };
        var scores = offsets
            .Select(offset => ScoreSlotBorder(frame, x + offset.Item1, y + offset.Item2))
            .Where(score => score > 0)
            .ToArray();
        return scores.Length == 0 ? 0 : scores.Average();
    }

    private static double ScoreSlotBorder(CaptureResult frame, int x, int y)
    {
        if (x < 1 || y < 1 || x + SlotWidth >= frame.Width || y + SlotHeight >= frame.Height)
            return 0;

        double total = 0;
        var samples = 0;
        for (var offset = 2; offset < SlotWidth - 2; offset += 2)
        {
            total += Math.Abs(Luminance(frame, x + offset, y) - Luminance(frame, x + offset, y + 2));
            total += Math.Abs(Luminance(frame, x + offset, y + SlotHeight - 1) - Luminance(frame, x + offset, y + SlotHeight - 3));
            total += Math.Abs(Luminance(frame, x, y + offset) - Luminance(frame, x + 2, y + offset));
            total += Math.Abs(Luminance(frame, x + SlotWidth - 1, y + offset) - Luminance(frame, x + SlotWidth - 3, y + offset));
            samples += 4;
        }

        return samples == 0 ? 0 : Math.Clamp(total / (samples * 255.0), 0, 1);
    }

    private static int Luminance(CaptureResult frame, int x, int y)
    {
        var index = (y * frame.Width + x) * 4;
        var blue = frame.Pixels[index];
        var green = frame.Pixels[index + 1];
        var red = frame.Pixels[index + 2];
        return (red * 77 + green * 150 + blue * 29) >> 8;
    }

    private string BuildModelVersion()
    {
        var left = _detector640?.ModelId ?? "missing";
        var right = _detector1280?.ModelId ?? "missing";
        return $"right-skill-v2:{left}+{right}";
    }

    private sealed record ProfileCandidate(string Profile, YoloDetection Detection);
    private sealed record AnchorCandidate(int X, int Y, double Score);
}
