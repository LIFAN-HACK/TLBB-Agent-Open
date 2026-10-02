using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

internal static class OpenTextCropUtil
{
    /// <summary>Default target-name strip on 2560×1369-style client (fractions of frame).</summary>
    public static readonly (double X0, double Y0, double X1, double Y1) TargetNameBarFrac =
        (0.00, 0.00, 0.28, 0.08);

    public static CaptureResult? Crop(CaptureResult frame, VisionBox box, int pad = 2)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < frame.Width * frame.Height * 4)
            return null;
        if (box.IsEmpty)
            return null;

        int x = Math.Clamp(box.X - pad, 0, frame.Width - 1);
        int y = Math.Clamp(box.Y - pad, 0, frame.Height - 1);
        int right = Math.Clamp(box.Right + pad, x + 1, frame.Width);
        int bottom = Math.Clamp(box.Bottom + pad, y + 1, frame.Height);
        int w = right - x;
        int h = bottom - y;
        if (w < 4 || h < 4)
            return null;

        var dst = new byte[w * h * 4];
        int srcStride = frame.Width * 4;
        int dstStride = w * 4;
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(frame.Pixels, (y + row) * srcStride + x * 4, dst, row * dstStride, dstStride);

        return new CaptureResult(w, h, dst);
    }

    public static CaptureResult? CropFrac(CaptureResult frame, double x0, double y0, double x1, double y1)
    {
        int xa = (int)(frame.Width * x0);
        int ya = (int)(frame.Height * y0);
        int xb = (int)(frame.Width * x1);
        int yb = (int)(frame.Height * y1);
        return Crop(frame, VisionBox.FromXyxy(xa, ya, xb, yb), pad: 0);
    }
}
