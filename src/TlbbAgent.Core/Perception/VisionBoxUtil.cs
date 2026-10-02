using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

internal static class VisionBoxUtil
{
    public static VisionBox FromRect(VisionRectF box, int width, int height)
    {
        var left = Math.Clamp((int)MathF.Round(box.X), 0, width);
        var top = Math.Clamp((int)MathF.Round(box.Y), 0, height);
        var right = Math.Clamp((int)MathF.Round(box.Right), left, width);
        var bottom = Math.Clamp((int)MathF.Round(box.Bottom), top, height);
        return VisionBox.FromXyxy(left, top, right, bottom);
    }

    public static bool IsInside(VisionBox box, int width, int height) =>
        !box.IsEmpty
        && box.X >= 0
        && box.Y >= 0
        && box.Right <= width
        && box.Bottom <= height;
}
