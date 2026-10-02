using System.Diagnostics;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;
public sealed class WindowCaptureService(ILogService log, IResolutionProfileService? resolutionProfiles = null) : IWindowCaptureService
{
    private const string Module = "Capture";
    private static readonly TimeSpan ClientCacheTtl = TimeSpan.FromMilliseconds(12);

    private static readonly OcrRegionConfig DefaultAvatarRegion = new() { X = 6, Y = 6, Width = 224, Height = 86 };
    private readonly object _cacheGate = new();
    private readonly object _captureGate = new();
    private nint _cachedHwnd;
    private int _cachedWidth;
    private int _cachedHeight;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private CaptureResult? _cachedClient;
    private long _captureSequence;

    public CaptureResult? CaptureClient(nint hwnd)
    {
        if (hwnd == nint.Zero || !GetClientRect(hwnd, out var rc))
            return null;

        var width = rc.Right - rc.Left;
        var height = rc.Bottom - rc.Top;
        if (width <= 0 || height <= 0)
            return null;

        lock (_captureGate)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_cacheGate)
            {
                if (_cachedClient is not null
                    && _cachedHwnd == hwnd
                    && _cachedWidth == width
                    && _cachedHeight == height
                    && now - _cachedAt <= ClientCacheTtl)
                {
                    return _cachedClient;
                }
            }

            return CaptureFresh(hwnd, width, height);
        }
    }

    private CaptureResult? CaptureFresh(nint hwnd, int width, int height)
    {
        var stopwatch = Stopwatch.StartNew();
        var hdc = GetDC(hwnd);
        if (hdc == nint.Zero)
            return null;

        var memDc = CreateCompatibleDC(hdc);
        var bitmap = CreateCompatibleBitmap(hdc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            var renderedByPrintWindow = PrintWindow(hwnd, memDc, 0x3);
            if (!renderedByPrintWindow)
                BitBlt(memDc, 0, 0, width, height, hdc, 0, 0, SRCCOPY);

            var pixels = ExtractBgrx(memDc, bitmap, width, height);
            if (pixels is null)
                return null;

            stopwatch.Stop();
            var capturedAt = DateTimeOffset.UtcNow;
            var result = new CaptureResult(width, height, pixels)
            {
                Sequence = Interlocked.Increment(ref _captureSequence),
                CapturedAt = capturedAt,
                CaptureMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                CaptureSource = renderedByPrintWindow ? "PrintWindow" : "BitBlt"
            };

            lock (_cacheGate)
            {
                _cachedHwnd = hwnd;
                _cachedWidth = width;
                _cachedHeight = height;
                _cachedAt = capturedAt;
                _cachedClient = result;
            }

            return result;
        }
        catch (Exception ex)
        {
            log.Warn(Module, $"Screenshot failed: {ex.Message}");
            return null;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(hwnd, hdc);
        }
    }

    public CaptureResult? CaptureRegion(nint hwnd, int x, int y, int w, int h)
    {
        var full = CaptureClient(hwnd);
        if (full is null) return null;
        return Crop(full, x, y, w, h);
    }

    public CaptureResult? CaptureAvatar(nint hwnd)
    {
        var full = CaptureClient(hwnd);
        if (full is null)
            return null;

        var region = resolutionProfiles?.ResolveAvatar(full.Width, full.Height, DefaultAvatarRegion) ?? DefaultAvatarRegion;
        var resolved = OcrRegionProjection.Resolve(region, full.Width, full.Height);
        return resolved.IsEmpty ? null : Crop(full, resolved.X, resolved.Y, resolved.Width, resolved.Height);
    }

    private static CaptureResult? Crop(CaptureResult src, int x, int y, int w, int h)
    {
        x = Math.Max(0, x); y = Math.Max(0, y);
        w = Math.Min(w, src.Width - x); h = Math.Min(h, src.Height - y);
        if (w <= 0 || h <= 0) return null;

        var dst = new byte[w * h * 4];
        int srcStride = src.Width * 4, dstStride = w * 4;
        for (int row = 0; row < h; row++)
            Array.Copy(src.Pixels, (y + row) * srcStride + x * 4, dst, row * dstStride, dstStride);
        return new CaptureResult(w, h, dst)
        {
            Sequence = src.Sequence,
            CapturedAt = src.CapturedAt,
            CaptureMilliseconds = src.CaptureMilliseconds,
            CaptureSource = src.CaptureSource
        };
    }

    private static byte[]? ExtractBgrx(nint memDc, nint hbmp, int w, int h)
    {
        var bi = new BITMAPINFO
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        var buf = new byte[w * h * 4];
        int scanned = GetDIBits(memDc, hbmp, 0, (uint)h, buf, ref bi, 0 /*DIB_RGB_COLORS*/);
        return scanned == 0 ? null : buf;
    }

    #region WinAPI
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
        public uint colors;
    }

    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    #endregion
}
