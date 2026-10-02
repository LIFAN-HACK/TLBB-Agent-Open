using TlbbAgent.Contracts;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.Core;

public sealed class DeathRecognitionService : IDeathRecognitionService
{
    private const string Module = "DeathRecognition";
    private readonly IWindowCaptureService _capture;
    private readonly IVisionV2SlotService? _vision;
    private readonly ILogService _log;

    public DeathRecognitionService(
        IWindowCaptureService capture,
        IOpenTextRecognizer openText,
        ILogService log,
        IVisionV2SlotService? vision = null)
    {
        _capture = capture;
        _vision = vision;
        _log = log;
    }

    public DeathRecognitionResult Recognize(BoundGameWindow window, DeathRecognitionConfig config)
    {
        if (window.Hwnd == nint.Zero || _vision is not { IsAvailable: true })
            return Empty();

        var frame = _capture.CaptureClient(window.Hwnd);
        if (frame is null)
            return Empty();

        var located = _vision.AnalyzeAuxPanels(frame, new[] { "death_dialog" });
        var panel = located.Detections
            .Where(item => item.ClassName == "death_dialog")
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (panel is null)
            return Empty();

        var crop = OpenTextCropUtil.Crop(frame, panel.Box, pad: 0);
        if (crop is null)
            return Empty();

        var analysis = _vision.AnalyzePanel(
            crop,
            panel.Box,
            "death_dialog",
            "revive",
            l1Confidence: panel.Confidence);
        var isDead = analysis.IsConfirmedCandidate;
        if (isDead)
            _log.Warn(Module, $"Death dialog confirmed at {panel.Box.X},{panel.Box.Y}.", window.Id);
        return new DeathRecognitionResult(
            isDead,
            analysis.PolicyState,
            isDead ? "death_dialog/revive" : "");
    }

    private static DeathRecognitionResult Empty() =>
        new(false, string.Empty, string.Empty);
}
