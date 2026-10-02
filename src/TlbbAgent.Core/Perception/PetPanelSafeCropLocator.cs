using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public static class PetPanelSafeCropLocator
{
    private const int Left = 78;
    private const int Top = 70;
    private const int Right = 240;
    private const int Bottom = 138;

    public static bool TryResolve(CaptureResult frame, out VisionBox panel)
        => TryResolve(frame, 1.0, out panel);

    public static bool TryResolve(CaptureResult frame, double hudScale, out VisionBox panel)
    {
        var clientTop = GameClientHudLocator.ResolveClientTop(frame);
        hudScale = Math.Clamp(hudScale, 0.45, 1.60);
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * hudScale));
        if (frame.Width < Scale(Right) || frame.Height - clientTop < Scale(Bottom))
        {
            panel = VisionBox.Empty;
            return false;
        }

        panel = VisionBox.FromXyxy(
            Scale(Left),
            clientTop + Scale(Top),
            Scale(Right),
            clientTop + Scale(Bottom));
        return true;
    }
}
