using System.Buffers.Binary;
using System.Text;

namespace Trw.Tools.Levels;

/// <summary>Big-endian helpers shared by the level/resource parsers.</summary>
internal static class Bin
{
    public static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
    public static int S32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32BigEndian(d[o..]);
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d[o..]);
    public static float F32(ReadOnlySpan<byte> d, int o) => BitConverter.Int32BitsToSingle(S32(d, o));
    public static string Hex(ReadOnlySpan<byte> d)
    {
        var sb = new StringBuilder(d.Length * 2);
        foreach (var b in d) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }
    public static string Words(ReadOnlySpan<byte> d, int max = 64)
    {
        var sb = new StringBuilder();
        int n = Math.Min(d.Length / 4, max);
        for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(U32(d, i * 4).ToString("X8")); }
        if (d.Length / 4 > max) sb.Append(" ...");
        return sb.ToString();
    }
    public static string CString(ReadOnlySpan<byte> d)
    {
        int n = d.IndexOf((byte)0);
        if (n < 0) n = d.Length;
        return Encoding.ASCII.GetString(d[..n]);
    }
}

/// <summary>
/// Engine "section container": u32 count; u32 offset[count+1] (first = header size = 8-aligned 4*(count+2),
/// last = total size, non-decreasing); sections follow. Offsets are relative to the container start.
/// The same layout is used for ROM-level (uncompressed) containers and for the contents of RNC chunks.
/// Mirrors game helpers func_0028FD94 (section pointer), func_0028FDA8 / func_0028FE1C (pointer + size).
/// </summary>
public static class Container
{
    public static bool TryParse(ReadOnlySpan<byte> d, out int[] offsets)
    {
        offsets = [];
        if (d.Length < 12) return false;
        uint c = Bin.U32(d, 0);
        if (c < 1 || c > 4000) return false;
        int count = (int)c;
        int hdr = (4 * (count + 2) + 7) & ~7;
        if (hdr > d.Length) return false;
        var o = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            uint v = Bin.U32(d, 4 + 4 * i);
            if (v > int.MaxValue) return false;
            o[i] = (int)v;
        }
        if (o[0] != hdr || o[count] > d.Length) return false;
        for (int i = 0; i < count; i++) if (o[i] > o[i + 1]) return false;
        offsets = o;
        return true;
    }

    public static byte[][]? Sections(byte[] d)
    {
        if (!TryParse(d, out var o)) return null;
        var r = new byte[o.Length - 1][];
        for (int i = 0; i < r.Length; i++) r[i] = d.AsSpan(o[i], o[i + 1] - o[i]).ToArray();
        return r;
    }
}

/// <summary>
/// "Typed array" block: u32 stride; u32 count; stride*count bytes, padded to 8. Mirrors func_0028FE98 (size = stride*count+8) and
/// func_0028FEEC which de-transposes arrays whose bytes are stored column-major
/// (record i, byte j = data[i + j*count]) to compress better.
/// </summary>
public readonly record struct TypedArray(int Stride, int Count, byte[] Data)
{
    public static bool TryParse(byte[] d, out TypedArray a)
    {
        a = default;
        if (d.Length < 8) return false;
        uint w = Bin.U32(d, 0), h = Bin.U32(d, 4);
        if (w > 0x10000 || h > 0x100000) return false;
        long need = 8 + (long)w * h;
        if (d.Length < need || d.Length - need > 7) return false;
        a = new TypedArray((int)w, (int)h, d.AsSpan(8, (int)(w * h)).ToArray());
        return true;
    }

    public byte[] Record(int i) => Data.AsSpan(i * Stride, Stride).ToArray();

    /// <summary>Undo the column-major storage (as func_0028FEEC does).</summary>
    public TypedArray Untransposed()
    {
        var o = new byte[Data.Length];
        for (int i = 0; i < Count; i++)
            for (int j = 0; j < Stride; j++) o[i * Stride + j] = Data[i + j * Count];
        return new TypedArray(Stride, Count, o);
    }
}
