using System.IO.Compression;
using System.Text;

namespace TlbbAgent.Core.Captcha;

internal static class PngEncoder
{
    public static string ToBase64(int width, int height, byte[] bgrx)
        => Convert.ToBase64String(Encode(width, height, bgrx));

    public static byte[] Encode(int width, int height, byte[] bgrx)
    {
        int stride = width * 4;
        var raw = new byte[height * (1 + width * 3)];
        int p = 0;
        for (int y = 0; y < height; y++)
        {
            raw[p++] = 0;
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * 4;
                raw[p++] = bgrx[i + 2];
                raw[p++] = bgrx[i + 1];
                raw[p++] = bgrx[i + 0];
            }
        }

        byte[] idat = ZlibCompress(raw);

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        uint adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, data);
        var c = new byte[4];
        WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    private static void WriteBE(byte[] buf, int off, int value)
    {
        buf[off] = (byte)(value >> 24);
        buf[off + 1] = (byte)(value >> 16);
        buf[off + 2] = (byte)(value >> 8);
        buf[off + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        const uint MOD = 65521;
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
