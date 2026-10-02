using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

var log = new RuntimeLogService();
var ocr = new TemplateOcrService(log);
ocr.LoadDictionary(System.IO.Path.Combine(Environment.CurrentDirectory, "data", "imported", "ocr", "core-aj.txt"));
Console.WriteLine($"renderable={ocr.RenderableGlyphCount}");
var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    "TLBB 视觉模型", "datasets", "03_目标", "_name_ocr_test", "name_strips");
foreach (var f in Directory.GetFiles(dir, "strip_*.png").OrderBy(x=>x))
{
    var img = Load(f);
    var t = ocr.Recognize(img);
    Console.WriteLine($"{Path.GetFileName(f)} {img.Width}x{img.Height} => [{t}]");
}
static CaptureResult Load(string path)
{
    using var bmp = new Bitmap(path);
    var rect = new Rectangle(0,0,bmp.Width,bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
        var stride = Math.Abs(data.Stride);
        var bytes = new byte[stride*bmp.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        if (stride == bmp.Width*4) return new CaptureResult(bmp.Width,bmp.Height,bytes);
        var tight=new byte[bmp.Width*bmp.Height*4];
        for(int y=0;y<bmp.Height;y++) Buffer.BlockCopy(bytes,y*stride,tight,y*bmp.Width*4,bmp.Width*4);
        return new CaptureResult(bmp.Width,bmp.Height,tight);
    } finally { bmp.UnlockBits(data); }
}
