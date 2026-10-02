using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CaptchaPoc;

// 验证码打码 PoC —— 以"游戏窗口客户区居中"为基准(分辨率无关)。
//
// 阶段1 标定(默认，安全：不点击/不花积分)：
//   CaptchaPoc --game 天龙
//   持续把客户区居中框截图存到 game_panel.png(覆盖)。验证出现时图就被存下，我据此量坐标。
//
// 阶段2 识别测试(花少量积分，不点击)：
//   CaptchaPoc --game 天龙 --img -120,-60,240,120 --send --type dianxuan
//   把"中心+偏移"的子区域发云码，打印返回坐标。
//
// 阶段3 全自动(识别+点击+确定)：
//   CaptchaPoc --game 天龙 --img -120,-60,240,120 --ok 0,90 --send --click --type dianxuan
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var o = Options.Parse(args);

        if (o.Send || o.Click)
        {
            if (string.IsNullOrWhiteSpace(o.Token)) { Console.WriteLine("--send/--click 需要 --token"); return 1; }
            try { Console.WriteLine($"云码积分: {await Yunma.QueryBalanceAsync(o.Token)}"); } catch (Exception e) { Console.WriteLine($"查余额失败: {e.Message}"); }
        }

        Console.WriteLine($"找游戏窗口标题含『{o.Game}』… 模式={(o.Send ? (o.Click ? "识别+点击" : "仅识别") : "仅标定截图")}");
        Console.WriteLine("Ctrl+C 退出。\n");

        nint lastHandled = 0; DateTime lastAt = DateTime.MinValue;
        // 帧只放内存环形缓存，不持续写盘；Ctrl+C 退出时一次性落盘
        var ring = new Queue<(string Name, byte[] Png)>();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; FlushRing(ring); Environment.Exit(0); };

        while (true)
        {
            await Task.Delay(o.Interval);
            nint game = Win.FindByTitle(o.Game);
            if (game == 0) { Console.Write("\r未找到游戏窗口…   "); continue; }
            if (!Win.GetClientRect(game, out var rc)) continue;
            int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
            if (cw < 200 || ch < 200) continue;
            if (!Win.ClientOriginOnScreen(game, out int gox, out int goy)) continue;

            int cx = gox + cw / 2, cy = goy + ch / 2;   // 客户区中心(屏幕坐标)

            // 截图区域：默认居中 box；给了 --img 则用"中心+偏移"的子区域
            int rx, ry, rw, rh;
            if (o.Img is { } im) { rw = im.W; rh = im.H; rx = cx + im.Dx; ry = cy + im.Dy; }
            else { rw = o.BoxW; rh = o.BoxH; rx = cx - rw / 2; ry = cy - rh / 2; }

            var shot = Screen.Capture(rx, ry, rw, rh);
            // 只进内存环形缓存，不写盘（产品里应如此：检测在内存做，只有真命中验证才落一张）
            ring.Enqueue(($"cap_{DateTime.Now:HHmmss_fff}.png", shot.Png));
            while (ring.Count > o.Keep) ring.Dequeue();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户区 {cw}x{ch} 中心({cx},{cy}) | 截图区({rw}x{rh}) | 内存缓存 {ring.Count} 帧（Ctrl+C 落盘）");

            if (!o.Send) continue;  // 标定模式：只存图

            // 同一轮 8 秒内不重复发送
            if (game == lastHandled && (DateTime.Now - lastAt).TotalSeconds < 8) continue;

            try
            {
                var (code, msg, data, uniq) = await Yunma.SolveAsync(o.Token, o.TypeCode, shot.Png);
                Console.WriteLine($"   云码: code={code} msg={msg} data={data}");
                if (code == 10000)
                {
                    lastHandled = game; lastAt = DateTime.Now;
                    var pts = ParsePoints(data);
                    foreach (var p in pts) Console.WriteLine($"     点 图内({p.x},{p.y}) -> 屏幕({rx + p.x},{ry + p.y})");
                    if (o.Click && pts.Count > 0)
                    {
                        foreach (var p in pts) { Mouse.Click(rx + p.x, ry + p.y); await Task.Delay(300 + Random.Shared.Next(120)); }
                        if (o.Ok is { } ok) { await Task.Delay(250); Mouse.Click(cx + ok.Dx, cy + ok.Dy); Console.WriteLine($"     点确定 屏幕({cx + ok.Dx},{cy + ok.Dy})"); }
                        Console.WriteLine("   [已点击完成]");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"   识别异常: {ex.Message}"); }
        }
    }

    private static void FlushRing(Queue<(string Name, byte[] Png)> ring)
    {
        Directory.CreateDirectory("frames");
        foreach (var (name, png) in ring) File.WriteAllBytes(Path.Combine("frames", name), png);
        Console.WriteLine($"\n已落盘 {ring.Count} 帧到 frames\\");
    }

    private static List<(int x, int y)> ParsePoints(string raw)
    {
        var pts = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(raw)) return pts;
        foreach (var seg in raw.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = seg.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length >= 2 && int.TryParse(xy[0].Trim(), out var x) && int.TryParse(xy[1].Trim(), out var y)) pts.Add((x, y));
        }
        return pts;
    }
}

internal sealed class Options
{
    public string Token = Environment.GetEnvironmentVariable("YUNMA_TOKEN") ?? "";
    public string Game = "天龙";
    public string TypeName = "dianxuan", TypeCode = "30100";
    public int BoxW = 760, BoxH = 620, Interval = 800, Keep = 120;
    public bool Send, Click;
    public (int Dx, int Dy, int W, int H)? Img;
    public (int Dx, int Dy)? Ok;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--token": o.Token = N(a, ref i); break;
                case "--game": o.Game = N(a, ref i); break;
                case "--type": (o.TypeName, o.TypeCode) = Map(N(a, ref i)); break;
                case "--box": { var p = N(a, ref i).Split('x', ','); if (p.Length == 2) { o.BoxW = int.Parse(p[0]); o.BoxH = int.Parse(p[1]); } break; }
                case "--img": { var p = N(a, ref i).Split(','); if (p.Length == 4) o.Img = (int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])); break; }
                case "--ok": { var p = N(a, ref i).Split(','); if (p.Length == 2) o.Ok = (int.Parse(p[0]), int.Parse(p[1])); break; }
                case "--interval": o.Interval = int.Parse(N(a, ref i), CultureInfo.InvariantCulture); break;
                case "--keep": o.Keep = int.Parse(N(a, ref i), CultureInfo.InvariantCulture); break;
                case "--send": o.Send = true; break;
                case "--click": o.Send = true; o.Click = true; break;
            }
        return o;
    }
    private static string N(string[] a, ref int i) => (++i < a.Length) ? a[i] : "";
    private static (string, string) Map(string t) => t.ToLowerInvariant() switch
    {
        "dianxuan" or "点选" or "成语" or "chengyu" => ("文字点选", "30100"),
        "renyi" or "任意" => ("任意点选", "30009"),
        "huakuai" or "quxian" or "滑块" or "曲线" => ("滑块", "20111"),
        "pingtu" or "拼图" => ("拼图", "20111"),
        "xuanzhuan" or "旋转" => ("旋转", "90007"),
        _ => t.All(char.IsDigit) ? ($"code{t}", t) : ("文字点选", "30100")
    };
}

internal static class Yunma
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Base = "http://api.jfbym.com";
    public static async Task<(int, string, string, string?)> SolveAsync(string token, string type, byte[] png)
    {
        var payload = new Dictionary<string, string> { ["token"] = token, ["type"] = type, ["image"] = Convert.ToBase64String(png) };
        using var r = await Http.PostAsJsonAsync($"{Base}/api/YmServer/customApi", payload);
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var root = d.RootElement;
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        string msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";
        string data = ""; string? uniq = null;
        if (root.TryGetProperty("data", out var dt) && dt.ValueKind == JsonValueKind.Object)
        { if (dt.TryGetProperty("data", out var dd)) data = dd.GetString() ?? ""; if (dt.TryGetProperty("unique_code", out var u)) uniq = u.GetString(); }
        return (code, msg, data, uniq);
    }
    public static async Task<string> QueryBalanceAsync(string token)
    {
        var payload = new Dictionary<string, string> { ["token"] = token, ["type"] = "score" };
        using var r = await Http.PostAsJsonAsync($"{Base}/api/YmServer/getUserInfoApi", payload);
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.TryGetProperty("data", out var dt) && dt.ValueKind == JsonValueKind.Object && dt.TryGetProperty("score", out var s)
            ? (s.ValueKind == JsonValueKind.String ? s.GetString() ?? "0" : s.GetRawText()) : d.RootElement.GetRawText();
    }
}

internal static class Screen
{
    public readonly record struct Shot(int W, int H, byte[] Png);
    public static Shot Capture(int x, int y, int w, int h)
    {
        var sdc = Win.GetDC(0); var mem = Win.CreateCompatibleDC(sdc); var bmp = Win.CreateCompatibleBitmap(sdc, w, h); var old = Win.SelectObject(mem, bmp);
        try { Win.BitBlt(mem, 0, 0, w, h, sdc, x, y, 0x00CC0020 | 0x40000000); return new Shot(w, h, Png.Encode(w, h, Bgrx(mem, bmp, w, h))); }
        finally { Win.SelectObject(mem, old); Win.DeleteObject(bmp); Win.DeleteDC(mem); Win.ReleaseDC(0, sdc); }
    }
    private static byte[] Bgrx(nint dc, nint bmp, int w, int h)
    {
        var bi = new Win.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        var buf = new byte[w * h * 4]; Win.GetDIBits(dc, bmp, 0, (uint)h, buf, ref bi, 0); return buf;
    }
}

internal static class Mouse
{
    public static void Click(int x, int y) { Win.SetCursorPos(x, y); System.Threading.Thread.Sleep(30); Win.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); Win.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
}

internal static class Win
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant, c0, c1, c2; }

    private delegate bool EnumProc(nint h, nint l);
    public static nint FindByTitle(string sub)
    {
        nint found = 0;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().Contains(sub, StringComparison.OrdinalIgnoreCase))
            { GetClientRect(h, out var r); if (r.Right - r.Left > 300 && r.Bottom - r.Top > 300) { found = h; return false; } }
            return true;
        }, 0);
        return found;
    }
    public static bool ClientOriginOnScreen(nint h, out int x, out int y) { var p = new POINT(); bool ok = ClientToScreen(h, ref p); x = p.X; y = p.Y; return ok; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern nint GetDC(nint h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint d, int x, int y, int w, int h, nint s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
}

internal static class Png
{
    public static byte[] Encode(int width, int height, byte[] bgrx)
    {
        int stride = width * 4; var raw = new byte[height * (1 + width * 3)]; int p = 0;
        for (int y = 0; y < height; y++) { raw[p++] = 0; int row = y * stride; for (int x = 0; x < width; x++) { int i = row + x * 4; raw[p++] = bgrx[i + 2]; raw[p++] = bgrx[i + 1]; raw[p++] = bgrx[i]; } }
        byte[] idat = Zlib(raw);
        using var ms = new MemoryStream(); ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13]; BE(ihdr, 0, width); BE(ihdr, 4, height); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr); Chunk(ms, "IDAT", idat); Chunk(ms, "IEND", Array.Empty<byte>()); return ms.ToArray();
    }
    private static byte[] Zlib(byte[] d) { using var ms = new MemoryStream(); ms.WriteByte(0x78); ms.WriteByte(0x9C); using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(d, 0, d.Length); uint a = Adler(d); ms.WriteByte((byte)(a >> 24)); ms.WriteByte((byte)(a >> 16)); ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a); return ms.ToArray(); }
    private static void Chunk(Stream s, string t, byte[] d) { var l = new byte[4]; BE(l, 0, d.Length); s.Write(l); var tt = Encoding.ASCII.GetBytes(t); s.Write(tt); s.Write(d); var c = new byte[4]; BE(c, 0, (int)Crc(tt, d)); s.Write(c); }
    private static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static uint Adler(byte[] d) { const uint M = 65521; uint a = 1, b = 0; foreach (var x in d) { a = (a + x) % M; b = (b + a) % M; } return (b << 16) | a; }
    private static readonly uint[] T = Build();
    private static uint[] Build() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    private static uint Crc(byte[] a, byte[] b) { uint c = 0xFFFFFFFF; foreach (var x in a) c = T[(c ^ x) & 0xFF] ^ (c >> 8); foreach (var x in b) c = T[(c ^ x) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFF; }
}
