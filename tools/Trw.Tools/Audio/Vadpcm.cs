namespace Trw.Tools.Audio;

/// <summary>Standard N64 VADPCM (9-byte frame -> 16 samples) decoder.</summary>
public static class Vadpcm
{
    /// <param name="book">order * npredictors * 8 coefficients, layout [predictor][order][8].</param>
    public static short[] Decode(byte[] src, int offset, int length, short[] book, int order, int npredictors)
    {
        int frames = length / 9;
        var output = new short[frames * 16];
        int l1 = 0, l2 = 0;           // l1 = sample n-2, l2 = sample n-1 (matches the RSP microcode)
        var x = new int[16];
        var half = new int[8];
        for (int f = 0; f < frames; f++)
        {
            int p = offset + f * 9;
            int hdr = src[p];
            int scale = hdr >> 4;
            int pred = hdr & 0xF;
            if (pred >= npredictors) pred = 0;
            for (int i = 0; i < 16; i++)
            {
                int b = src[p + 1 + (i >> 1)];
                int nib = (i & 1) == 0 ? b >> 4 : b & 0xF;
                if (nib >= 8) nib -= 16;
                x[i] = nib << scale;
            }
            int bookBase = pred * order * 8;
            for (int h = 0; h < 2; h++)
            {
                for (int i = 0; i < 8; i++)
                {
                    long acc = (long)x[h * 8 + i] << 11;
                    if (order >= 1)
                    {
                        // vector 0 weights sample n-2, vector 1 weights sample n-1
                        acc += (long)book[bookBase + 0 * 8 + i] * l1;
                        if (order >= 2)
                        {
                            acc += (long)book[bookBase + 1 * 8 + i] * l2;
                            for (int j = 0; j < i; j++)
                                acc += (long)x[h * 8 + j] * book[bookBase + 8 + (i - 1 - j)];
                        }
                    }
                    int v = (int)(acc >> 11);
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    half[i] = v;
                    output[f * 16 + h * 8 + i] = (short)v;
                }
                l1 = half[6];
                l2 = half[7];
            }
        }
        return output;
    }
}
