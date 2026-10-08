namespace Trw.Tools.Anim;

/// <summary>
/// Bit reader used by the animation streams (func_002607D8): bits are read LSB-first from big-endian 32-bit words,
/// equivalent to a little-endian bit stream over the byte-swapped data.
/// </summary>
public sealed class AnimBits
{
    readonly byte[] _le;
    public long Pos;
    public bool Overrun;
    public long Length => _le.Length * 8L;

    public AnimBits(ReadOnlySpan<byte> be)
    {
        int n = (be.Length + 3) & ~3;
        _le = new byte[n + 8];
        for (int i = 0; i < be.Length; i++) _le[(i & ~3) + 3 - (i & 3)] = be[i];
    }

    public uint Read(int n)
    {
        if (n == 0) return 0;
        ulong v = 0;
        long p = Pos;
        Pos += n;
        if (p + n > Length - 64) { Overrun = true; return 0; }
        int b = (int)(p >> 3);
        for (int i = 0; i < 8; i++) v |= (ulong)_le[b + i] << (8 * i);
        v >>= (int)(p & 7);
        if (n < 32) v &= (1UL << n) - 1;
        return (uint)v;
    }

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(Read(32));
}

/// <summary>Decoder for the keyframe streams of an animation chunk (func_0025F0F4 and its stream decoders).</summary>
public static class TrackDecoder
{
    /// <summary>Largest | |q| - 1 | seen before normalising rotation tracks (validation aid; ~0.01 for correct data).</summary>
    public static float LastMaxNormError;

    /// <returns>float[track][frame][component]; bitsUsed = -1 on overrun.</returns>
    public static float[][][] DecodeSection(ReadOnlySpan<byte> stream, int tracks, int comps, int frames, int compression,
        bool rawFloatRange, float lo, float hi, int smooth, out int bitsUsed, out int bitsTotal)
    {
        var br = new AnimBits(stream);
        if (comps == 4) LastMaxNormError = 0;
        var res = new float[tracks][][];
        for (int t = 0; t < tracks; t++)
        {
            res[t] = new float[frames][];
            for (int f = 0; f < frames; f++) res[t][f] = new float[comps];
            for (int c = 0; c < comps; c++)
            {
                br.Read(31);    // 31 bits skipped before every component stream (constant filler value in all samples)
                float[] v = compression switch
                {
                    0 => DecodeCubic(br, frames, rawFloatRange, lo, hi),
                    1 => DecodeLinear(br, frames, rawFloatRange, lo, hi),
                    2 => DecodeRaw(br, frames),
                    _ => new float[frames],
                };
                for (int f = 0; f < frames; f++) res[t][f][c] = v[f];
            }
            if (comps == 4)
            {
                if (smooth > 0) Smooth(res[t], smooth);
                foreach (var q in res[t])
                {
                    float l = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
                    if (smooth == 0) LastMaxNormError = Math.Max(LastMaxNormError, MathF.Abs(l - 1));
                    Normalize(q);
                }
            }
        }
        bitsUsed = br.Overrun ? -1 : (int)br.Pos;
        bitsTotal = stream.Length * 8;
        return res;
    }

    static void ReadRange(AnimBits br, bool raw, float lo, float hi, out float min, out float range)
    {
        if (raw)
        {
            min = br.ReadFloat();
            float max = br.ReadFloat();
            range = max - min;
        }
        else
        {
            float a = br.Read(6) / 63f, b = br.Read(6) / 63f;
            min = a * (hi - lo) + lo;
            float max = b * (hi - lo) + lo;
            range = max - min;
        }
    }

    // func_0025FFD0 / func_0025FD94 / func_00260158: piecewise cubic with 'bits'-bit control values per segment.
    static float[] DecodeCubic(AnimBits br, int frames, bool raw, float lo, float hi)
    {
        var o = new float[frames];
        int nseg = (int)br.Read(6);
        ReadRange(br, raw, lo, hi, out float min, out float range);
        int bits = (int)br.Read(6);
        float bas = min;
        int frame = 0;
        for (int s = 0; s < nseg; s++)
        {
            int span = (int)br.Read(6);
            long v1 = br.Read(bits), v2 = br.Read(bits), v3 = br.Read(bits), v4 = br.Read(bits);
            float a1 = ((v2 << 5) - v1 * 21 - v3 * 12 + v4) * (1f / 3f);          // D_800C9220 = 0.33333334
            float a2 = ((v2 << 6) - v1 * 24 - v3 * 48 + v4 * 8) * (1f / 3f);
            float a0 = v1 * 14 - (v2 << 5) + v3 * 20 - v4 * 2;
            float sc;
            if (bits != 0) sc = range / ((1 << bits) - 1);
            else { bas += range * 0.5f; range = 0; sc = 0; }                       // constant track: value = min + range/2
            float inv = span >= 2 ? 1f / (span - 1) : 1f;                           // D_800C9230 / D_800C9234 = 1.0
            float f2 = inv * sc, f3 = f2 * inv;
            float c0 = v1 * sc + bas, c1 = a1 * f2, c2 = a0 * f3, c3 = a2 * (f3 * inv);
            for (int d = 0; d < span && frame < frames; d++, frame++)
                o[frame] = c0 + c1 * d + c2 * d * d + c3 * d * d * d;
        }
        for (; frame < frames; frame++) o[frame] = frame > 0 ? o[frame - 1] : min;   // should not occur
        return o;
    }

    // func_0025EF54: min/range then 'bits'-bit quantised value per frame
    static float[] DecodeLinear(AnimBits br, int frames, bool raw, float lo, float hi)
    {
        ReadRange(br, raw, lo, hi, out float min, out float range);
        int bits = (int)br.Read(6);
        var o = new float[frames];
        float den = (float)((1UL << bits) - 1);
        for (int f = 0; f < frames; f++) o[f] = br.Read(bits) / den * range + min;
        return o;
    }

    // func_00261258: raw 32-bit floats
    static float[] DecodeRaw(AnimBits br, int frames)
    {
        var o = new float[frames];
        for (int f = 0; f < frames; f++) o[f] = br.ReadFloat();
        return o;
    }

    // func_0025F0F4: circular box filter of radius r over the 4 components independently
    static void Smooth(float[][] q, int r)
    {
        int n = q.Length;
        for (int c = 0; c < 4; c++)
            for (int f = 0; f < n; f++)
            {
                // the game filters in place (later frames see already-filtered earlier frames)
                float sum = 0, cnt = 0;
                for (int d = -r; d <= r; d++) { sum += q[((f + d) % n + n) % n][c]; cnt += 1f; }
                q[f][c] = sum / cnt;
            }
    }

    static void Normalize(float[] q)
    {
        float l = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (l > 1e-6f) for (int i = 0; i < 4; i++) q[i] /= l;
    }
}
