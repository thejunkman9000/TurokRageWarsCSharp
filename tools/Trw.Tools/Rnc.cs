namespace Trw.Tools;

/// <summary>
/// Rob Northen Compression, method 1 ("RNC\x01"), the container used for most assets in the ROM.
/// Header (18 bytes, big-endian): magic, unpacked size, packed size, unpacked CRC16, packed CRC16, leeway, chunk count.
/// The payload is a stream of little-endian 16-bit words read LSB first; literal bytes are interleaved in the
/// byte stream between the words the bit reader has already pulled in.
/// </summary>
public static class Rnc
{
    public const int HeaderSize = 18;

    public readonly record struct Header(int UnpackedSize, int PackedSize, ushort UnpackedCrc, ushort PackedCrc);

    public static bool IsRnc(ReadOnlySpan<byte> d, int off) =>
        off + HeaderSize <= d.Length && d[off] == 'R' && d[off + 1] == 'N' && d[off + 2] == 'C' && d[off + 3] == 1;

    public static Header ReadHeader(ReadOnlySpan<byte> d, int off) => new(
        (int)ReadU32(d, off + 4), (int)ReadU32(d, off + 8),
        (ushort)((d[off + 12] << 8) | d[off + 13]), (ushort)((d[off + 14] << 8) | d[off + 15]));

    /// <summary>Total bytes the chunk occupies in the source (header plus packed payload).</summary>
    public static int ChunkLength(Header h) => HeaderSize + h.PackedSize;

    /// <summary>Unpack the chunk at <paramref name="off"/>. Throws InvalidDataException on corrupt data or CRC mismatch.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> d, int off)
    {
        if (!IsRnc(d, off)) throw new InvalidDataException($"no RNC1 header at 0x{off:X}");
        var h = ReadHeader(d, off);
        var output = new byte[h.UnpackedSize];
        int o = 0;
        var bs = new BitStream(d, off + HeaderSize);
        bs.Advance(2);

        while (o < output.Length)
        {
            var raw = ReadTable(ref bs);
            var dist = ReadTable(ref bs);
            var len = ReadTable(ref bs);
            int subChunks = bs.Read(16);

            while (true)
            {
                int n = raw.Length > 0 ? ReadSymbol(ref bs, raw) : 0;
                if (n > 0)
                {
                    if (bs.P + n > d.Length || o + n > output.Length) throw new InvalidDataException("literal run out of range");
                    d.Slice(bs.P, n).CopyTo(output.AsSpan(o));
                    o += n;
                    bs.SkipLiterals(n);
                }
                if (--subChunks <= 0) break;

                int pos = ReadSymbol(ref bs, dist) + 1;
                int count = ReadSymbol(ref bs, len) + 2;
                if (pos > o || o + count > output.Length) throw new InvalidDataException("bad match reference");
                for (int i = 0; i < count; i++, o++) output[o] = output[o - pos];
            }
        }

        if (Crc16(output) != h.UnpackedCrc) throw new InvalidDataException($"CRC mismatch for chunk at 0x{off:X}");
        return output;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data) crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF]);
        return crc;
    }

    static readonly ushort[] CrcTable = BuildCrcTable();

    static ushort[] BuildCrcTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
            t[i] = (ushort)c;
        }
        return t;
    }

    static uint ReadU32(ReadOnlySpan<byte> d, int off) =>
        (uint)((d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3]);

    readonly record struct Code(int Bits, int Length, int Value);

    static Code[] ReadTable(ref BitStream bs)
    {
        int leaves = bs.Read(5);
        if (leaves == 0) return [];
        var lengths = new int[leaves];
        for (int i = 0; i < leaves; i++) lengths[i] = bs.Read(4);

        // Canonical Huffman code assignment; codes are stored bit-reversed because the stream is read LSB first.
        var table = new List<Code>(leaves);
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int sym = 0; sym < leaves; sym++)
            {
                if (lengths[sym] != len) continue;
                table.Add(new Code(Reverse(code, len), len, sym));
                code++;
            }
            code <<= 1;
        }
        return table.ToArray();
    }

    static int Reverse(int v, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++, v >>= 1) r = (r << 1) | (v & 1);
        return r;
    }

    static int ReadSymbol(ref BitStream bs, Code[] table)
    {
        foreach (var c in table)
        {
            if (bs.Peek(c.Length) != c.Bits) continue;
            bs.Advance(c.Length);
            int v = c.Value;
            if (v >= 2)
            {
                int extra = v - 1;
                v = (1 << extra) | bs.Read(extra);
            }
            return v;
        }
        throw new InvalidDataException("invalid Huffman code");
    }

    ref struct BitStream
    {
        readonly ReadOnlySpan<byte> _d;
        public int P;
        ulong _buf;
        int _count;

        public BitStream(ReadOnlySpan<byte> d, int p)
        {
            _d = d; P = p;
            _buf = Word(p);
            _count = 16;
        }

        uint Word(int p) =>
            p + 1 < _d.Length ? (uint)(_d[p] | (_d[p + 1] << 8)) : p < _d.Length ? _d[p] : 0u;

        public int Peek(int bits) => (int)(_buf & ((1UL << bits) - 1));

        public void Advance(int bits)
        {
            _buf >>= bits;
            _count -= bits;
            if (_count < 16)
            {
                P += 2;
                _buf |= (ulong)Word(P) << _count;
                _count += 16;
            }
        }

        public int Read(int bits)
        {
            int v = Peek(bits);
            Advance(bits);
            return v;
        }

        /// <summary>After <paramref name="n"/> literal bytes were copied straight from the byte stream, reload the top word.</summary>
        public void SkipLiterals(int n)
        {
            P += n;
            _count -= 16;
            _buf &= (1UL << _count) - 1;
            _buf |= (ulong)Word(P) << _count;
            _count += 16;
        }
    }
}
