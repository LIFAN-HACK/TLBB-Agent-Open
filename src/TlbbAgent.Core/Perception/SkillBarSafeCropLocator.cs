using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public static class SkillBarSafeCropLocator
{
    private const int ContentWidth = 520;
    private const int ContentHeight = 142;
    private const int ContentBottomInset = 16;

    public static bool TryResolveContent(CaptureResult frame, out VisionBox content)
        => TryResolveContent(frame, 1.0, out content);

    public static bool TryResolveContent(CaptureResult frame, double hudScale, out VisionBox content)
    {
        hudScale = Math.Clamp(hudScale, 0.45, 1.60);
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * hudScale));
        var contentWidth = Scale(ContentWidth);
        var contentHeight = Scale(ContentHeight);
        var bottomInset = Scale(ContentBottomInset);
        if (frame.Width < contentWidth || frame.Height < contentHeight + bottomInset)
        {
            content = VisionBox.Empty;
            return false;
        }

        // The decorated L1 anchor is centered. Its actual 3x14 icon grid starts
        // 80 px inside the anchor and ends 16 px above the client bottom.
        var left = frame.Width / 2 - Scale(180);
        var top = frame.Height - contentHeight - bottomInset;
        if (left < 0 || left + contentWidth > frame.Width || top < 0)
        {
            content = VisionBox.Empty;
            return false;
        }

        content = new VisionBox(left, top, contentWidth, contentHeight);
        return true;
    }

    public static VisionBox ResolveFromAnchor(VisionBox anchor, int frameWidth, int frameHeight)
    {
        if (anchor.IsEmpty || frameWidth <= 0 || frameHeight <= 0)
            return VisionBox.Empty;

        var left = anchor.X + (int)Math.Round(anchor.Width * (80.0 / 522.0));
        var top = anchor.Y - (int)Math.Round(anchor.Height * (16.0 / 142.0));
        var width = Math.Max(1, (int)Math.Round(anchor.Width * (520.0 / 522.0)));
        var height = Math.Max(1, anchor.Height);
        left = Math.Clamp(left, 0, Math.Max(0, frameWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, frameHeight - 1));
        var right = Math.Clamp(left + width, left + 1, frameWidth);
        var bottom = Math.Clamp(top + height, top + 1, frameHeight);
        return VisionBox.FromXyxy(left, top, right, bottom);
    }
}
