using System.Buffers.Binary;
using System.IO.Compression;

namespace Trw.Tools.Models;

/// <summary>Minimal PNG encoder (8-bit RGBA, no interlace) using System.IO.Compression for deflate.</summary>
public static class PngWriter
{
    public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgba, int scale = 1)
    {
        File.WriteAllBytes(path, EncodeRgba(width, height, rgba, scale));
    }

    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba, int scale = 1)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("pixel buffer too small");
        int ow = width * scale, oh = height * scale;
        var raw = new byte[(ow * 4 + 1) * oh];
        int p = 0;
        for (int y = 0; y < oh; y++)
        {
            raw[p++] = 0; // filter: none
            int sy = y / scale;
            for (int x = 0; x < ow; x++)
            {
                int si = (sy * width + x / scale) * 4;
                raw[p++] = rgba[si]; raw[p++] = rgba[si + 1]; raw[p++] = rgba[si + 2]; raw[p++] = rgba[si + 3];
            }
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)ow);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)oh);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            z.WriteByte(0x78); z.WriteByte(0x9C); // zlib header
            using (var d = new DeflateStream(z, CompressionLevel.Optimal, leaveOpen: true)) d.Write(raw);
            uint a = Adler32(raw);
            Span<byte> ad = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ad, a);
            z.Write(ad);
            WriteChunk(ms, "IDAT", z.ToArray());
        }
        WriteChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        data.CopyTo(td.AsSpan(4));
        s.Write(td);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(td));
        s.Write(len);
    }

    static uint Adler32(ReadOnlySpan<byte> d)
    {
        uint a = 1, b = 0;
        foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    static readonly uint[] CrcTable = MakeTable();
    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in d) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
