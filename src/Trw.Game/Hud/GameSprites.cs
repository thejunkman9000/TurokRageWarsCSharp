using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game.PortHud;

/// <summary>
/// The game's HUD sprites, read straight from guest RAM (read-only; nothing here calls game code or writes memory).
/// <para>Lookup (Sprite.Draw -&gt; Sprite.Lookup with the HUD library <c>D_8011FE88</c>, re-implemented without its side effects): the library's key table
/// at +0x9C (<c>{?, n, keys[n]}</c>, sorted) gives the index of the sprite id (Sprite.FindIndex, binary search); the offset table at +0x5C and the base at +0x2C give
/// the resource's cartridge key <c>base + off[idx]</c> (Resource.GetCartridgeKey); the resource cache's hash (ResourceCache.Hash: buckets <c>*Gv.AddrHash_BucketsPtr</c>, mask
/// <c>*0x80105190</c>, chained through +0xC) maps that key to the cache entry, whose node's first word is the data in RAM. A sprite the game has not loaded
/// is not found (it is not drawn then either).</para>
/// <para>Data (Sprite.GetFrameData_002AB19C / Sprite.GetFrame): <c>s16 frames</c>, then at +8 one <c>{s32 offset, u8 kind}</c> per frame; a frame is <c>u16 cols, u16 rows</c> and from +8
/// the cells row by row, each <c>u32 w, u32 h</c> and the texels: kind 0 = RGBA 5551 colour (w*h*2 bytes) followed by a 4-bit intensity alpha plane
/// (w*h/2 bytes), loaded as two tiles and combined TEXEL0 * ENV (colour), TEXEL1 * ENV (alpha); kind 1 = the 4-bit plane only.</para>
/// Decoded cells are <see cref="DecodedTexture"/>s with exactly the texels the display-list interpreter decodes for the game's own draws.
/// </summary>
public static class GameSprites
{
    public const uint Library = Gv.LevelMgr, HashBuckets = Gv.AddrHash_BucketsPtr, HashMask = 0x80105190;
    public const int DigitFont = 4;          // Hud.DrawNumber: frames 0..9 = digits, 10 = minus

    public sealed class Cell { public float X, Y; public int W, H; public uint Colour, Alpha; public int Kind; public DecodedTexture? Tex0, Tex1; }
    public sealed class Frame { public List<Cell> Cells = new(); public float Width, Height; }
    public sealed class Sprite
    {
        public int Id; public uint Data, End; public int Frames; public ulong Stamp;
        public readonly Dictionary<int, Frame> FrameCache = new();
        public Frame? Get(int frame) => Load(this, frame);
    }

    private static readonly Dictionary<int, Sprite> cache = new();
    private static readonly object gate = new();

    private static bool Ram(uint a, uint n) => a >= 0x80000000u && a + n <= 0x80800000u && n < 0x800000 && Mem.IsMapped(a) && Mem.IsMapped(a + n - 1);

    /// <summary>The RAM address of sprite <paramref name="id"/>'s data (0 = not loaded).</summary>
    public static uint Locate(int id)
    {
        try
        {
            uint t = Mem.U32(Library + 0x9C);
            if (!Ram(t, 8)) return 0;
            int n = Mem.S32(t + 4);
            if (n <= 0 || n > 20000 || !Ram(t + 8, (uint)n * 4)) return 0;
            int lo = 0, hi = n - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (Mem.U32(t + 8 + (uint)mid * 4) < (uint)id) lo = mid + 1; else hi = mid; }
            if (Mem.U32(t + 8 + (uint)lo * 4) != (uint)id) return 0;
            uint tbl = Mem.U32(Library + 0x5C), bas = Mem.U32(Library + 0x2C);
            if (!Ram(tbl + (uint)lo * 4, 12)) return 0;
            uint key = bas + Mem.U32(tbl + (uint)(lo + 1) * 4);
            uint buckets = Mem.U32(HashBuckets), mask = Mem.U32(HashMask);
            uint b = buckets + (((key << 5) ^ (key >> 1) ^ (key >> 9) ^ (key >> 17)) & mask) * 16;
            for (int guard = 0; guard < 64 && Ram(b, 16); guard++, b = Mem.U32(b + 0xC))
            {
                if (Mem.U32(b) != key) continue;
                uint ent = Mem.U32(b + 4);
                if (!Ram(ent, 4)) return 0;
                uint node = Mem.U32(ent);
                if (!Ram(node, 4)) return 0;
                uint data = Mem.U32(node);
                return Ram(data, 16) ? data : 0;
            }
        }
        catch (Mem.GuestFault) { }
        return 0;
    }

    /// <summary>Sprite <paramref name="id"/> as currently loaded, or null. Cached while its data stays where it is and unchanged.</summary>
    public static Sprite? Get(int id)
    {
        uint data = Locate(id);
        lock (gate)
        {
            if (data == 0) { cache.Remove(id); return null; }
            ulong stamp = Stamp(data);
            if (cache.TryGetValue(id, out var s) && s.Data == data && s.Stamp == stamp) return s;
            s = new Sprite { Id = id, Data = data, Stamp = stamp };
            try
            {
                int frames = Mem.S16(data);
                if (frames <= 0 || frames > 256 || !Ram(data, 8 + (uint)frames * 8)) return null;
                s.Frames = frames;
                s.End = data + 8 + (uint)frames * 8;
                for (int f = 0; f < frames; f++) { var fr = Load(s, f); if (fr != null) foreach (var c in fr.Cells) s.End = Math.Max(s.End, c.Alpha + (uint)(c.W * c.H / 2)); }
            }
            catch (Mem.GuestFault) { return null; }
            cache[id] = s;
            return s;
        }
    }

    /// <summary>A cheap identity of the data (header words): a reloaded or replaced resource gets new textures.</summary>
    private static ulong Stamp(uint data)
    {
        ulong h = 1469598103934665603UL;
        for (uint i = 0; i < 32; i += 4) h = (h ^ Mem.U32(data + i)) * 1099511628211UL;
        return h;
    }

    private static Frame? Load(Sprite s, int frame)
    {
        if (s.Frames <= 0) return null;
        frame %= s.Frames;
        if (frame < 0) frame += s.Frames;
        if (s.FrameCache.TryGetValue(frame, out var fr)) return fr;
        try
        {
            uint e = s.Data + 8 + (uint)frame * 8;
            uint tab = s.Data + Mem.U32(e);
            int kind = Mem.U8(e + 4);
            if (!Ram(tab, 8)) return null;
            int cols = Mem.U16(tab), rows = Mem.U16(tab + 2);
            if (cols <= 0 || rows <= 0 || cols * rows > 64) return null;
            fr = new Frame();
            uint p = tab + 8;
            float y = 0;
            for (int r = 0; r < rows; r++)
            {
                float x = 0; int h = 0;
                for (int c = 0; c < cols; c++)
                {
                    int w = (int)Mem.U32(p); h = (int)Mem.U32(p + 4);
                    if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;
                    int n = w * h;
                    var cell = new Cell { X = x, Y = y, W = w, H = h, Kind = kind };
                    if (kind == 0) { cell.Colour = p + 8; cell.Alpha = p + 8 + (uint)n * 2; p += 8 + (uint)(n * 2 + n / 2); }
                    else { cell.Colour = 0; cell.Alpha = p + 8; p += 8 + (uint)(n / 2); }
                    if (!Ram(cell.Alpha, (uint)Math.Max(1, n / 2))) return null;
                    Decode(cell);
                    fr.Cells.Add(cell);
                    x += w;
                    fr.Width = Math.Max(fr.Width, x);
                }
                y += h;
            }
            fr.Height = y;
            s.FrameCache[frame] = fr;
            return fr;
        }
        catch (Mem.GuestFault) { return null; }
    }

    private static ulong nextKey = 0x4855440000000000UL;      // "HUD": keys of port-made textures, apart from the interpreter's content hashes

    /// <summary>RGBA 5551 colour (as the interpreter decodes G_IM_FMT_RGBA 16b) and I4 alpha (intensity in all four channels).</summary>
    private static void Decode(Cell c)
    {
        int n = c.W * c.H;
        var a = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int b = Mem.U8(c.Alpha + (uint)(i >> 1)), v = (i & 1) != 0 ? (b & 15) : (b >> 4);
            byte iv = (byte)(v * 17);
            a[i * 4] = a[i * 4 + 1] = a[i * 4 + 2] = a[i * 4 + 3] = iv;
        }
        c.Tex1 = new DecodedTexture { Width = c.W, Height = c.H, Rgba = a, Key = Interlocked.Increment(ref nextKey), Src = c.Alpha };
        if (c.Kind != 0) { c.Tex0 = c.Tex1; return; }
        var px = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int v = Mem.U16(c.Colour + (uint)i * 2);
            px[i * 4] = (byte)(((v >> 11) & 31) * 255 / 31); px[i * 4 + 1] = (byte)(((v >> 6) & 31) * 255 / 31); px[i * 4 + 2] = (byte)(((v >> 1) & 31) * 255 / 31);
            px[i * 4 + 3] = (byte)((v & 1) != 0 ? 255 : 0);
        }
        c.Tex0 = new DecodedTexture { Width = c.W, Height = c.H, Rgba = px, Key = Interlocked.Increment(ref nextKey), Src = c.Colour };
    }

    /// <summary>The sprite id whose data holds <paramref name="src"/> (a texture's RDRAM source), among <paramref name="ids"/>; -1 = none of them.</summary>
    public static int Owner(uint src, IEnumerable<int> ids)
    {
        if (src == 0) return -1;
        lock (gate)
            foreach (var id in ids)
                if (cache.TryGetValue(id, out var s) && src >= s.Data && src < s.End) return id;
        return -1;
    }

    /// <summary>Forget everything (layout reload).</summary>
    public static void Clear() { lock (gate) cache.Clear(); }
}
