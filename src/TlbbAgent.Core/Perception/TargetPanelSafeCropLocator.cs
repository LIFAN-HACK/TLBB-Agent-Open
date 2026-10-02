using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public static class TargetPanelSafeCropLocator
{
    public static bool TryResolve(CaptureResult frame, out VisionBox panel)
    {
        var clientTop = GameClientHudLocator.ResolveClientTop(frame);
        if (frame.Width >= 512 && frame.Height - clientTop >= 88)
        {
            panel = new VisionBox(242, clientTop, 270, 88);
            return true;
        }

        panel = VisionBox.Empty;
        return false;
    }
}
