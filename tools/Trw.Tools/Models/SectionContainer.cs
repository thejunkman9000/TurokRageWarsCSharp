using System.Buffers.Binary;

namespace Trw.Tools.Models;

/// <summary>
/// The game's generic "section container" (all fields big-endian):
/// <code>
/// u32 count
/// u32 offset[count + 1]   // relative to the container start; offset[0] == header size, offset[count] == total length
/// (u32 0 pad, only if needed so that the header is a multiple of 8 bytes)
/// section data...
/// </code>
/// Header size = ((count + 3) &amp; ~1) * 4. Verified against func_0028FD94 / func_0028FDA8 / func_0028FE1C and all 1621 chunks.
/// Containers nest freely (a section may itself be a container).
/// </summary>
public sealed class SectionContainer
{
    readonly ReadOnlyMemory<byte> _data;
    readonly int[] _offsets;

    public int Count { get; }
    public int Length => _data.Length;
    public ReadOnlyMemory<byte> Data => _data;

    SectionContainer(ReadOnlyMemory<byte> data, int count, int[] offsets)
    {
        _data = data;
        Count = count;
        _offsets = offsets;
    }

    public static int HeaderSize(int count) => ((count + 3) & ~1) * 4;

    public static bool TryParse(ReadOnlyMemory<byte> data, out SectionContainer? c)
    {
        c = null;
        var s = data.Span;
        if (s.Length < 8) return false;
        uint n = BinaryPrimitives.ReadUInt32BigEndian(s);
        if (n == 0 || n > 20000) return false;
        int count = (int)n;
        int hs = HeaderSize(count);
        if (s.Length < hs) return false;
        var offs = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            uint v = BinaryPrimitives.ReadUInt32BigEndian(s[(4 + 4 * i)..]);
            if (v > int.MaxValue) return false;
            offs[i] = (int)v;
        }
        if (offs[0] != hs || offs[count] != s.Length) return false;
        for (int i = 0; i < count; i++)
            if (offs[i] > offs[i + 1]) return false;
        c = new SectionContainer(data, count, offs);
        return true;
    }

    public static bool TryParse(byte[] data, out SectionContainer? c) => TryParse((ReadOnlyMemory<byte>)data, out c);

    public ReadOnlyMemory<byte> this[int i] => _data[_offsets[i].._offsets[i + 1]];

    public int SectionOffset(int i) => _offsets[i];
    public int SectionLength(int i) => _offsets[i + 1] - _offsets[i];

    public SectionContainer? Sub(int i) => TryParse(this[i], out var c) ? c : null;
}

/// <summary>
/// "Counted array" section: u32 elementSize, u32 count, then count*elementSize bytes (func_0028FE98 / func_0028FED0).
/// </summary>
public readonly struct CountedArray
{
    public int ElementSize { get; }
    public int Count { get; }
    public ReadOnlyMemory<byte> Elements { get; }

    CountedArray(int es, int n, ReadOnlyMemory<byte> e) { ElementSize = es; Count = n; Elements = e; }

    public static bool TryParse(ReadOnlyMemory<byte> d, out CountedArray a)
    {
        a = default;
        var s = d.Span;
        if (s.Length < 8) return false;
        uint es = BinaryPrimitives.ReadUInt32BigEndian(s);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
        if ((long)es * n + 8 != s.Length) return false;
        a = new CountedArray((int)es, (int)n, d[8..]);
        return true;
    }

    public ReadOnlySpan<byte> Element(int i) => Elements.Span.Slice(i * ElementSize, ElementSize);
}
