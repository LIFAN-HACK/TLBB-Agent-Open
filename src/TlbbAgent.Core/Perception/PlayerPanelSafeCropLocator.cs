using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public static class PlayerPanelSafeCropLocator
{
    public static bool TryResolve(CaptureResult frame, out VisionBox panel)
    {
        var clientTop = GameClientHudLocator.ResolveClientTop(frame);
        if (frame.Width < 240 || frame.Height - clientTop < 66)
        {
            panel = VisionBox.Empty;
            return false;
        }

        panel = new VisionBox(0, clientTop, 240, 66);
        return true;
    }
}
