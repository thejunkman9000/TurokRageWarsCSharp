using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Trw.Platform;

/// <summary>
/// The game's address space. Guest addresses are 32-bit (<see cref="uint"/>), exactly as on the N64, and memory is stored
/// big-endian so ROM data can be copied in untouched. The host process itself is a normal 64-bit .NET process.
/// The space is split into 16 MB slots (top byte of the address); each slot is backed by its own array.
/// </summary>
public static class Mem
{
    private sealed class Region
    {
        public byte[] Data = Array.Empty<byte>();
        public uint Base;
        /// <summary>For demand-paged regions: which 4 KB pages are present, and how to bring one in (fills the page, returns false if it cannot).</summary>
        public bool[]? Present;
        public Func<uint, bool>? PageIn;
    }

    private static readonly Region?[] Slots = new Region?[256];

    /// <summary>Maps <paramref name="size"/> zeroed bytes at <paramref name="baseAddr"/>. A region never spans two 16 MB slots.</summary>
    public static byte[] Map(uint baseAddr, int size)
    {
        if ((baseAddr >> 24) != ((baseAddr + (uint)size - 1) >> 24))
            throw new ArgumentException($"region {baseAddr:X8}+{size:X} crosses a 16 MB slot");
        var slot = baseAddr >> 24;
        if (Slots[slot] != null) throw new InvalidOperationException($"slot {slot:X2} already mapped");
        var r = new Region { Data = new byte[size], Base = baseAddr };
        Slots[slot] = r;
        return r.Data;
    }

    /// <summary>
    /// Maps a region whose pages are brought in on first access by <paramref name="pageIn"/> (called with the page address; it writes the page
    /// with <see cref="Write"/>). Accesses below <paramref name="firstPagedAddr"/> are plain memory.
    /// </summary>
    public static byte[] MapPaged(uint baseAddr, int size, uint firstPagedAddr, Func<uint, bool> pageIn)
    {
        var data = Map(baseAddr, size);
        var r = Slots[baseAddr >> 24]!;
        r.Present = new bool[(size + 0xFFF) >> 12];
        for (uint p = 0; p < r.Present.Length; p++) r.Present[p] = baseAddr + (p << 12) < firstPagedAddr;
        r.PageIn = pageIn;
        return data;
    }

    /// <summary>Maps the 16 MB slot containing <paramref name="baseAddr"/> if nothing is mapped there yet (used for per-thread guest stacks).</summary>
    public static void EnsureStackRegion(uint baseAddr, uint size)
    {
        if (Slots[baseAddr >> 24] == null) Map(baseAddr & 0xFF000000u, 0x1000000);
    }

    public static void Unmap(uint baseAddr) => Slots[baseAddr >> 24] = null;

    /// <summary>Raised for an access outside every mapped region. Carries the guest address for debugging.</summary>
    public sealed class GuestFault(uint addr, int size) : Exception($"guest fault: {size}-byte access at {addr:X8}")
    {
        public uint Address { get; } = addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<byte> At(uint a, int n)
    {
        var r = Slots[a >> 24];
        if (r == null) throw new GuestFault(a, n);
        var off = a - r.Base;
        if (off > (uint)(r.Data.Length - n) || a < r.Base) throw new GuestFault(a, n);
        if (r.Present != null) EnsurePresent(r, a, off, n);
        return r.Data.AsSpan((int)off, n);
    }

    private static void EnsurePresent(Region r, uint a, uint off, int n)
    {
        for (uint p = off >> 12; p <= (off + (uint)n - 1) >> 12; p++)
        {
            if (r.Present![p]) continue;
            r.Present[p] = true;      // set first: PageIn writes the page through Mem
            var saved = journal; journal = null;          // a page coming in is not a write of the program (a differential test must not undo it)
            bool ok;
            try { ok = r.PageIn!(r.Base + (p << 12)); } finally { journal = saved; }
            if (!ok) { r.Present[p] = false; throw new GuestFault(a, n); }
        }
    }

    public static bool IsMapped(uint a)
    {
        var r = Slots[a >> 24];
        return r != null && a >= r.Base && a - r.Base < (uint)r.Data.Length;
    }

    // ---- loads (names follow the game's own type names: s8/u8/s16/u16/s32/u32/f32/f64) ----
    public static sbyte S8(uint a) => (sbyte)At(a, 1)[0];
    public static byte U8(uint a) => At(a, 1)[0];
    public static short S16(uint a) => BinaryPrimitives.ReadInt16BigEndian(At(a, 2));
    public static ushort U16(uint a) => BinaryPrimitives.ReadUInt16BigEndian(At(a, 2));
    public static int S32(uint a) => BinaryPrimitives.ReadInt32BigEndian(At(a, 4));
    public static uint U32(uint a) => BinaryPrimitives.ReadUInt32BigEndian(At(a, 4));
    public static long S64(uint a) => BinaryPrimitives.ReadInt64BigEndian(At(a, 8));
    public static ulong U64(uint a) => BinaryPrimitives.ReadUInt64BigEndian(At(a, 8));
    public static float F32(uint a) => BinaryPrimitives.ReadSingleBigEndian(At(a, 4));
    public static double F64(uint a) => BinaryPrimitives.ReadDoubleBigEndian(At(a, 8));

    // ---- stores: return the stored value so they can be used inside expressions, like C assignment ----
    public static sbyte Set(uint a, sbyte v) { AtW(a, 1)[0] = (byte)v; return v; }
    public static byte Set(uint a, byte v) { AtW(a, 1)[0] = v; return v; }
    public static short Set(uint a, short v) { BinaryPrimitives.WriteInt16BigEndian(AtW(a, 2), v); return v; }
    public static ushort Set(uint a, ushort v) { BinaryPrimitives.WriteUInt16BigEndian(AtW(a, 2), v); return v; }
    public static int Set(uint a, int v) { BinaryPrimitives.WriteInt32BigEndian(AtW(a, 4), v); return v; }
    public static uint Set(uint a, uint v) { BinaryPrimitives.WriteUInt32BigEndian(AtW(a, 4), v); return v; }
    public static long Set(uint a, long v) { BinaryPrimitives.WriteInt64BigEndian(AtW(a, 8), v); return v; }
    public static ulong Set(uint a, ulong v) { BinaryPrimitives.WriteUInt64BigEndian(AtW(a, 8), v); return v; }
    public static float Set(uint a, float v) { BinaryPrimitives.WriteSingleBigEndian(AtW(a, 4), v); return v; }
    public static double Set(uint a, double v) { BinaryPrimitives.WriteDoubleBigEndian(AtW(a, 8), v); return v; }


    // ---- debug: TRW_WATCH="addr:len" (hex) prints the translated call stack of every write that overlaps the range (TRW_WATCH_MAX hits, default 20) ----
    private static readonly string? watchSpec = Environment.GetEnvironmentVariable("TRW_WATCH");
    public static readonly bool WatchOn = watchSpec != null;
    private static readonly (uint lo, uint hi)[] watchRanges = watchSpec == null ? Array.Empty<(uint, uint)>() : watchSpec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(r => { var q = r.Split(':'); uint lo = Convert.ToUInt32(q[0], 16); return (lo, lo + (q.Length > 1 ? Convert.ToUInt32(q[1], 16) : 4u)); }).ToArray();     // "addr:len;addr:len"
    private static readonly int watchMax = int.TryParse(Environment.GetEnvironmentVariable("TRW_WATCH_MAX"), out var wm) ? wm : 20;
    private static int watchHits;

    // ---- debug: write journal (differential tests undo a run by replaying the old bytes) ----
    private static volatile bool instrument = WatchOn;
    private static List<(uint addr, byte[] old)>? journal;

    public static void JournalBegin() { journal = new(); instrument = true; }
    public static List<(uint addr, byte[] old)> JournalEnd() { var j = journal ?? new(); journal = null; instrument = WatchOn; return j; }

    /// <summary>Puts the memory back as it was before the journalled writes.</summary>
    public static void JournalUndo(List<(uint addr, byte[] old)> j)
    {
        for (int i = j.Count - 1; i >= 0; i--) j[i].old.CopyTo(At(j[i].addr, j[i].old.Length));
    }

    /// <summary>Current contents of every byte the journal touched, with the value it had before the first write.</summary>
    public static Dictionary<uint, (byte initial, byte final)> JournalBytes(List<(uint addr, byte[] old)> j)
    {
        var d = new Dictionary<uint, (byte, byte)>();
        foreach (var (addr, old) in j)
            for (int k = 0; k < old.Length; k++)
            {
                uint a = addr + (uint)k;
                if (!d.ContainsKey(a)) d[a] = (old[k], At(a, 1)[0]);
                else d[a] = (d[a].Item1, At(a, 1)[0]);
            }
        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<byte> AtW(uint a, int n)
    {
        if (instrument) Instrumented(a, n);
        return At(a, n);
    }

    private static void Instrumented(uint a, int n)
    {
        if (WatchOn) Watch(a, n);
        var j = journal;
        if (j != null) j.Add((a, At(a, n).ToArray()));
    }

    private static void Watch(uint a, int n)
    {
        if (!Dbg.Armed) return;       // TRW_TRACE_ARM_STATE also delays the watch
        bool hit = false;
        foreach (var (lo, hi) in watchRanges) if (a < hi && a + (uint)n > lo) { hit = true; break; }
        if (!hit || Interlocked.Increment(ref watchHits) > watchMax) return;
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames().Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType?.Name is "Game" or "Funcs" or "GameBoot").Select(m => m!.Name).Take(10);
        Console.Error.WriteLine($"[watch] write of {n} bytes at {a:X8} (was {Convert.ToHexString(At(a, n))}) by {string.Join(" <- ", frames)}");
    }

    // ---- bulk helpers for the platform layer (DMA, memcpy, ...) ----
    public static void Write(uint a, ReadOnlySpan<byte> src) => src.CopyTo(AtW(a, src.Length));
    public static void Read(uint a, Span<byte> dst) => At(a, dst.Length).CopyTo(dst);
    public static void Fill(uint a, int count, byte v) => AtW(a, count).Fill(v);
    public static void Copy(uint dst, uint src, int count)
    {
        var s = At(src, count);
        var d = AtW(dst, count);
        s.CopyTo(d); // Span.CopyTo handles overlap like memmove
    }

    /// <summary>Reads a NUL-terminated ASCII string.</summary>
    public static string CString(uint a)
    {
        var sb = new System.Text.StringBuilder();
        for (byte b; (b = U8(a)) != 0; a++) sb.Append((char)b);
        return sb.ToString();
    }
}


