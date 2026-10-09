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
        return r.Data.AsSpan((int)off, n);
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
    public static sbyte Set(uint a, sbyte v) { At(a, 1)[0] = (byte)v; return v; }
    public static byte Set(uint a, byte v) { At(a, 1)[0] = v; return v; }
    public static short Set(uint a, short v) { BinaryPrimitives.WriteInt16BigEndian(At(a, 2), v); return v; }
    public static ushort Set(uint a, ushort v) { BinaryPrimitives.WriteUInt16BigEndian(At(a, 2), v); return v; }
    public static int Set(uint a, int v) { BinaryPrimitives.WriteInt32BigEndian(At(a, 4), v); return v; }
    public static uint Set(uint a, uint v) { BinaryPrimitives.WriteUInt32BigEndian(At(a, 4), v); return v; }
    public static long Set(uint a, long v) { BinaryPrimitives.WriteInt64BigEndian(At(a, 8), v); return v; }
    public static ulong Set(uint a, ulong v) { BinaryPrimitives.WriteUInt64BigEndian(At(a, 8), v); return v; }
    public static float Set(uint a, float v) { BinaryPrimitives.WriteSingleBigEndian(At(a, 4), v); return v; }
    public static double Set(uint a, double v) { BinaryPrimitives.WriteDoubleBigEndian(At(a, 8), v); return v; }

    // ---- bulk helpers for the platform layer (DMA, memcpy, ...) ----
    public static void Write(uint a, ReadOnlySpan<byte> src) => src.CopyTo(At(a, src.Length));
    public static void Read(uint a, Span<byte> dst) => At(a, dst.Length).CopyTo(dst);
    public static void Fill(uint a, int count, byte v) => At(a, count).Fill(v);
    public static void Copy(uint dst, uint src, int count)
    {
        var s = At(src, count);
        var d = At(dst, count);
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
