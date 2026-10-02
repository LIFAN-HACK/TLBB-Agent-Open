using System.Windows.Media;
using System.Windows.Media.Imaging;
using TlbbAgent.Contracts;

namespace TlbbAgent.App;

internal static class CaptureImaging
{
    public static BitmapSource? ToBitmapSource(CaptureResult? c)
    {
        if (c is null || c.Width <= 0 || c.Height <= 0) return null;
        var bmp = BitmapSource.Create(
            c.Width, c.Height, 96, 96,
            PixelFormats.Bgr32, null, c.Pixels, c.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource? Crop(CaptureResult? c, VisionBox box)
    {
        var source = ToBitmapSource(c);
        if (source is null || box.Width <= 0 || box.Height <= 0)
            return null;

        var x = Math.Clamp(box.X, 0, Math.Max(0, c!.Width - 1));
        var y = Math.Clamp(box.Y, 0, Math.Max(0, c.Height - 1));
        var width = Math.Min(box.Width, c.Width - x);
        var height = Math.Min(box.Height, c.Height - y);
        if (width <= 0 || height <= 0)
            return null;

        var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(x, y, width, height));
        cropped.Freeze();
        return cropped;
    }
}
