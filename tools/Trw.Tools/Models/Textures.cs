using System.Buffers.Binary;

namespace Trw.Tools.Models;

/// <summary>RGBA5551 helpers (N64 16-bit colour).</summary>
public static class Rgba5551
{
    public static void Convert(ushort v, Span<byte> dst)
    {
        int r = (v >> 11) & 31, g = (v >> 6) & 31, b = (v >> 1) & 31;
        dst[0] = (byte)((r << 3) | (r >> 2));
        dst[1] = (byte)((g << 3) | (g >> 2));
        dst[2] = (byte)((b << 3) | (b >> 2));
        dst[3] = (byte)((v & 1) != 0 ? 255 : 0);
    }
}

/// <summary>
/// 3D (model) texture: a 3-section container found in the texture table (ROM 0x5F90E8, 507 entries; section 3 of the master container).
/// sec0 = 8 byte parameters, sec1 = container of image frames (CI4/CI8 indices), sec2 = container of palettes (RGBA5551).
/// </summary>
public sealed class CiTexture
{
    /// <summary>Parameter byte 0: 0 = CI8 + RGBA16 palette, 1 = CI4 + RGBA16 palette, 3 = CI4 + IA16 palette (8-bit intensity and 8-bit alpha: smoke, explosions, water), see transparency_and_animation.md.</summary>
    public byte FormatByte { get; private set; }
    /// <summary>Parameter byte 1: small integer 1..7 (hypothesis: animation frame delay).</summary>
    public byte Param1 { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BitsPerPixel { get; private set; }
    /// <summary>The raw 8-byte section 0: [0] format, [1] p1 (animation divisor), [2] log2 W, [3] log2 H, [4] PRIM LOD fraction (0xFF), [5] detail-mode selector (flags 0x2000 only), [6..7] 0.</summary>
    public byte[] Header { get; private set; } = [];
    public byte[][] Frames { get; private set; } = [];
    public ushort[][] Palettes { get; private set; } = [];

    public static CiTexture? Parse(byte[] chunk)
    {
        if (!SectionContainer.TryParse(chunk, out var c) || c!.Count != 3) return null;
        var p = c[0].Span;
        if (p.Length != 8) return null;
        var images = c.Sub(1);
        var pals = c.Sub(2);
        if (images == null || pals == null) return null;
        var t = new CiTexture { Header = p.ToArray(), FormatByte = p[0], Param1 = p[1], Width = 1 << p[2], Height = 1 << p[3] };
        if (p[2] > 9 || p[3] > 9) return null;
        t.Frames = new byte[images.Count][];
        for (int i = 0; i < images.Count; i++) t.Frames[i] = images[i].ToArray();
        t.Palettes = new ushort[pals.Count][];
        for (int i = 0; i < pals.Count; i++)
        {
            var s = pals[i].Span;
            var pal = new ushort[s.Length / 2];
            for (int k = 0; k < pal.Length; k++) pal[k] = BinaryPrimitives.ReadUInt16BigEndian(s[(2 * k)..]);
            t.Palettes[i] = pal;
        }
        if (t.Frames.Length == 0 || t.Palettes.Length == 0) return null;
        int px = t.Width * t.Height;
        if (t.Frames[0].Length == px) t.BitsPerPixel = 8;
        else if (t.Frames[0].Length == px / 2) t.BitsPerPixel = 4;
        else return null;
        return t;
    }

    /// <summary>
    /// Frame/palette pair the game selects (func_00269A80): key &gt;= 0 -> (key % frames, key % palettes);
    /// key -1 (animated) with step k = (tick / Param1) &amp; 255 -> (k % frames, k % palettes).
    /// </summary>
    public (int Frame, int Palette) Select(int keyOrStep) => (keyOrStep % Frames.Length, keyOrStep % Palettes.Length);

    /// <summary>
    /// PNG file name of a (frame, palette) look. The classic exporter writes <c>tex_NNN_f{frame}.png</c> with palette min(frame, palettes-1);
    /// every other combination is <c>tex_NNN_f{frame}_p{palette}.png</c>.
    /// </summary>
    public string FileName(int id, int frame, int palette) =>
        palette == Math.Min(frame, Palettes.Length - 1) ? $"tex_{id:D3}_f{frame}.png" : $"tex_{id:D3}_f{frame}_p{palette}.png";

    /// <summary>All (frame, palette) combinations the game can display: animated key -1 steps 0..255 plus fixed keys 0..127.</summary>
    public SortedSet<(int Frame, int Palette)> ReachableLooks()
    {
        var set = new SortedSet<(int, int)>();
        for (int k = 0; k < 256; k++) set.Add(Select(k));
        for (int k = 0; k < 128; k++) set.Add(Select(k));
        return set;
    }

    /// <summary>Decode frame <paramref name="frame"/> using palette <paramref name="palette"/> (default: the palette with the same index, else the last).</summary>
    public byte[] ToRgba(int frame = 0, int palette = -1)
    {
        if (palette < 0) palette = Math.Min(frame, Palettes.Length - 1);
        var pal = Palettes[palette];
        var img = Frames[frame];
        var rgba = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++)
        {
            int idx = BitsPerPixel == 8 ? img[i] : ((i & 1) == 0 ? img[i >> 1] >> 4 : img[i >> 1] & 15);
            ushort col = idx < pal.Length ? pal[idx] : (ushort)0;
            if (FormatByte == 3)
            {
                // format 3 = CI4 with an IA16 TLUT (func_00295FB4 case 3 sets G_TT_IA16, 0xC000): palette entry = I8 (high byte), A8 (low byte).
                var px = rgba.AsSpan(i * 4, 4);
                px[0] = px[1] = px[2] = (byte)(col >> 8);
                px[3] = (byte)col;
            }
            else Rgba5551.Convert(col, rgba.AsSpan(i * 4, 4));
        }
        return rgba;
    }
}

/// <summary>
/// 2D (HUD/UI) texture chunk: header + tiles. Header (24 bytes): u16 version(1), u16 id, u32 0, u32 0x10, u32 0, u16 cols, u16 rows, u16 width, u16 height.
/// Then cols*rows tiles (row-major, nominal tile size 32x32): u32 w, u32 h, w*h RGBA5551 pixels, w*h/2 bytes of 4-bit alpha (high nibble first).
/// </summary>
public sealed class UiTexture
{
    public int Id { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Rgba { get; private set; } = [];

    public const int TileSize = 32;

    public static UiTexture? Parse(byte[] d)
    {
        if (d.Length < 0x20) return null;
        var s = d.AsSpan();
        if (BinaryPrimitives.ReadUInt16BigEndian(s) != 1) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(s[4..]) != 0 || BinaryPrimitives.ReadUInt32BigEndian(s[8..]) != 0x10 || BinaryPrimitives.ReadUInt32BigEndian(s[12..]) != 0) return null;
        var t = new UiTexture
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(s[2..]),
            Cols = BinaryPrimitives.ReadUInt16BigEndian(s[16..]),
            Rows = BinaryPrimitives.ReadUInt16BigEndian(s[18..]),
            Width = BinaryPrimitives.ReadUInt16BigEndian(s[20..]),
            Height = BinaryPrimitives.ReadUInt16BigEndian(s[22..]),
        };
        if (t.Cols == 0 || t.Rows == 0 || t.Width == 0 || t.Height == 0 || t.Width > 1024 || t.Height > 1024) return null;
        t.Rgba = new byte[t.Width * t.Height * 4];
        int p = 0x18;
        for (int ty = 0; ty < t.Rows; ty++)
            for (int tx = 0; tx < t.Cols; tx++)
            {
                if (p + 8 > d.Length) return null;
                int w = (int)BinaryPrimitives.ReadUInt32BigEndian(s[p..]);
                int h = (int)BinaryPrimitives.ReadUInt32BigEndian(s[(p + 4)..]);
                if (w <= 0 || h <= 0 || w > TileSize || h > TileSize) return null;
                p += 8;
                int colorBytes = w * h * 2, alphaBytes = (w * h + 1) / 2;
                if (p + colorBytes + alphaBytes > d.Length) return null;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        ushort col = BinaryPrimitives.ReadUInt16BigEndian(s[(p + 2 * i)..]);
                        int an = (i & 1) == 0 ? s[p + colorBytes + (i >> 1)] >> 4 : s[p + colorBytes + (i >> 1)] & 15;
                        int dx = tx * TileSize + x, dy = ty * TileSize + y;
                        if (dx >= t.Width || dy >= t.Height) continue;
                        var px = t.Rgba.AsSpan((dy * t.Width + dx) * 4, 4);
                        Rgba5551.Convert(col, px);
                        px[3] = (byte)(an * 17);
                    }
                p += colorBytes + alphaBytes;
            }
        return p == d.Length ? t : null;
    }
}
