namespace Trw.Platform;

/// <summary>
/// High-level emulation of libultra's aspMain (ABI1) RSP audio microcode (port of the C port's audio_hle.c, including the semantics verified against
/// the game's own command stream). DMEM is a big-endian byte array so DMA load/save are plain copies. Command lists and sample data live in guest memory.
/// </summary>
public sealed class AudioHle
{
    // Acmd opcodes (w0 >> 24)
    private const int A_SPNOOP = 0, A_ADPCM = 1, A_CLEARBUFF = 2, A_ENVMIXER = 3, A_LOADBUFF = 4, A_RESAMPLE = 5, A_SAVEBUFF = 6,
        A_SEGMENT = 7, A_SETBUFF = 8, A_SETVOL = 9, A_DMEMMOVE = 10, A_LOADADPCM = 11, A_MIXER = 12, A_INTERLEAVE = 13,
        A_POLEF = 14, A_SETLOOP = 15;
    // flag bits (bits 16..23 of w0)
    private const uint A_INIT = 0x01, A_LOOP = 0x02, A_LEFT = 0x02, A_VOL = 0x04, A_AUX = 0x08;
    private const int VolShift = 15;      // envmixer: sample * vol >> shift (0x7FFF ~ unity)

    private readonly byte[] dmem = new byte[4096];
    private readonly uint[] seg = new uint[16];
    private readonly short[] book = new short[256];      // ADPCM codebook (also the reverb filter's coefficients)
    private int bookN;
    private uint loop;                                   // A_SETLOOP address (resolved lazily)
    private ushort inp, outp, count;
    private ushort dryRight, wetLeft, wetRight;
    private readonly short[] vol = new short[2], target = new short[2];
    private readonly int[] rate = new int[2];
    private short dry, wet;
    public uint Unknown { get; private set; }

    /// <summary>The 64-phase 4-tap interpolation table of the audio microcode (the taps apply to the 4 samples before the input position).</summary>
    private static readonly short[,] ResampTab = BuildTable();

    private static short[,] BuildTable()
    {
        short[] lut =
        {
            0x0c39, 0x66ad, 0x0d46, -0x0021, 0x0b39, 0x6696, 0x0e5f, -0x0028, 0x0a44, 0x6669, 0x0f83, -0x0030, 0x095a, 0x6626, 0x10b4, -0x0038,
            0x087d, 0x65cd, 0x11f0, -0x0041, 0x07ab, 0x655e, 0x1338, -0x004a, 0x06e4, 0x64d9, 0x148c, -0x0054, 0x0628, 0x643f, 0x15eb, -0x005f,
            0x0577, 0x638f, 0x1756, -0x006a, 0x04d1, 0x62cb, 0x18cb, -0x0076, 0x0435, 0x61f3, 0x1a4c, -0x0082, 0x03a4, 0x6106, 0x1bd7, -0x008f,
            0x031c, 0x6007, 0x1d6c, -0x009c, 0x029f, 0x5ef5, 0x1f0b, -0x00aa, 0x022a, 0x5dd0, 0x20b3, -0x00b8, 0x01be, 0x5c9a, 0x2262, -0x00c6,
            0x015b, 0x5b53, 0x2419, -0x00d4, 0x0101, 0x59fc, 0x25d7, -0x00e2, 0x00ae, 0x5896, 0x279a, -0x00f0, 0x0063, 0x5720, 0x2961, -0x00fe,
            0x001f, 0x559d, 0x2b2c, -0x010c, -0x001e, 0x540d, 0x2cf9, -0x0118, -0x0054, 0x5270, 0x2ec8, -0x0125, -0x0084, 0x50c7, 0x3097, -0x0130,
            -0x00ad, 0x4f14, 0x3266, -0x013a, -0x00d2, 0x4d57, 0x3432, -0x0143, -0x00f1, 0x4b91, 0x35fb, -0x014a, -0x010b, 0x49c2, 0x37bf, -0x0150,
            -0x0121, 0x47ed, 0x397e, -0x0154, -0x0132, 0x4611, 0x3b37, -0x0155, -0x0140, 0x4430, 0x3ce8, -0x0154, -0x014a, 0x424a, 0x3e91, -0x0151,
            -0x0151, 0x4060, 0x4030, -0x014a, -0x0151, 0x3e91, 0x424a, -0x014a, -0x0154, 0x3ce8, 0x4430, -0x0140, -0x0155, 0x3b37, 0x4611, -0x0132,
            -0x0154, 0x397e, 0x47ed, -0x0121, -0x0150, 0x37bf, 0x49c2, -0x010b, -0x014a, 0x35fb, 0x4b91, -0x00f1, -0x0143, 0x3432, 0x4d57, -0x00d2,
            -0x013a, 0x3266, 0x4f14, -0x00ad, -0x0130, 0x3097, 0x50c7, -0x0084, -0x0125, 0x2ec8, 0x5270, -0x0054, -0x0118, 0x2cf9, 0x540d, -0x001e,
            -0x010c, 0x2b2c, 0x559d, 0x001f, -0x00fe, 0x2961, 0x5720, 0x0063, -0x00f0, 0x279a, 0x5896, 0x00ae, -0x00e2, 0x25d7, 0x59fc, 0x0101,
            -0x00d4, 0x2419, 0x5b53, 0x015b, -0x00c6, 0x2262, 0x5c9a, 0x01be, -0x00b8, 0x20b3, 0x5dd0, 0x022a, -0x00aa, 0x1f0b, 0x5ef5, 0x029f,
            -0x009c, 0x1d6c, 0x6007, 0x031c, -0x008f, 0x1bd7, 0x6106, 0x03a4, -0x0082, 0x1a4c, 0x61f3, 0x0435, -0x0076, 0x18cb, 0x62cb, 0x04d1,
            -0x006a, 0x1756, 0x638f, 0x0577, -0x005f, 0x15eb, 0x643f, 0x0628, -0x0054, 0x148c, 0x64d9, 0x06e4, -0x004a, 0x1338, 0x655e, 0x07ab,
            -0x0041, 0x11f0, 0x65cd, 0x087d, -0x0038, 0x10b4, 0x6626, 0x095a, -0x0030, 0x0f83, 0x6669, 0x0a44, -0x0028, 0x0e5f, 0x6696, 0x0b39,
        };
        var t = new short[64, 4];
        for (int i = 0; i < 64; i++)
            for (int k = 0; k < 4; k++) t[i, k] = lut[i * 4 + k];
        return t;
    }

    public void Reset()
    {
        Array.Clear(dmem); Array.Clear(seg); Array.Clear(book); bookN = 0; loop = 0;
        inp = outp = count = 0; dryRight = wetLeft = wetRight = 0;
        Array.Clear(vol); Array.Clear(target); Array.Clear(rate); dry = wet = 0; Unknown = 0;
    }

    private static short Clamp16(int v) => v > 32767 ? (short)32767 : v < -32768 ? (short)-32768 : (short)v;

    private byte Dm(uint a) => dmem[a & 0xFFF];
    private void DmSet(uint a, byte v) => dmem[a & 0xFFF] = v;
    private short DmRd(uint a) => (short)((Dm(a) << 8) | Dm(a + 1));
    private void DmWr(uint a, short v) { DmSet(a, (byte)((ushort)v >> 8)); DmSet(a + 1, (byte)v); }

    /// <summary>Command addresses are segment offsets; the guest memory is RDRAM, so the physical offset selects the KSEG0 address.</summary>
    private uint Resolve(uint a) => 0x80000000u | ((seg[(a >> 24) & 0xF] + (a & 0xFFFFFF)) & 0x7FFFFF);

    private void ClearBuff(uint w0, uint w1)
    {
        uint a = w0 & 0xFFFF, n = w1 & 0xFFFF;
        for (uint i = 0; i < n; i++) DmSet(a + i, 0);
    }

    private void LoadBuff(uint w1)
    {
        uint p = Resolve(w1);
        for (uint i = 0; i < count; i++) DmSet(inp + i, Mem.U8(p + i));
    }

    private void SaveBuff(uint w1)
    {
        uint p = Resolve(w1);
        for (uint i = 0; i < count; i++) Mem.Set(p + i, Dm(outp + i));
    }

    private void SetBuff(uint w0, uint w1)
    {
        if (((w0 >> 16) & A_AUX) != 0) { dryRight = (ushort)w0; wetLeft = (ushort)(w1 >> 16); wetRight = (ushort)w1; }
        else { inp = (ushort)w0; outp = (ushort)(w1 >> 16); count = (ushort)w1; }
    }

    private void SetVol(uint w0, uint w1)
    {
        uint f = (w0 >> 16) & 0xFF;
        int lr = (f & A_LEFT) != 0 ? 0 : 1;
        if ((f & A_AUX) != 0) { dry = (short)w0; wet = (short)w1; }
        else if ((f & A_VOL) != 0) vol[lr] = (short)w0;
        else { target[lr] = (short)w0; rate[lr] = (int)w1; }
    }

    private void DmemMove(uint w0, uint w1)
    {
        uint src = w0 & 0xFFFF, dst = w1 >> 16, n = w1 & 0xFFFF;
        var tmp = new byte[4096];
        for (uint i = 0; i < n; i++) tmp[i & 0xFFF] = Dm(src + i);
        for (uint i = 0; i < n; i++) DmSet(dst + i, tmp[i & 0xFFF]);
    }

    private void LoadAdpcm(uint w0, uint w1)
    {
        uint n = (w0 & 0xFFFFFF) / 2;
        uint p = Resolve(w1);
        if (n > 256) n = 256;
        bookN = (int)n;
        for (uint i = 0; i < n; i++) book[i] = Mem.S16(p + 2 * i);
    }

    private void Adpcm(uint w0, uint w1)
    {
        uint f = (w0 >> 16) & 0xFF;
        uint st = Resolve(w1);
        var last = new short[16];
        uint srcAddr = 0; bool haveSrc = true;
        if ((f & A_INIT) != 0) haveSrc = false;
        else if ((f & A_LOOP) != 0) srcAddr = Resolve(loop);
        else srcAddr = st;
        if (haveSrc) for (uint k = 0; k < 16; k++) last[k] = Mem.S16(srcAddr + 2 * k);

        uint inA = inp, outA = outp;
        for (uint k = 0; k < 16; k++) DmWr(outA + 2 * k, last[k]);      // history prefix: the resampler reads its 4-sample window from the end of it
        outA += 32;
        uint frames = ((uint)count + 31) / 32;
        var o = new short[16];
        var res = new int[16];
        for (uint fr = 0; fr < frames; fr++)
        {
            byte hdr = Dm(inA);
            int scale = hdr >> 4, pred = hdr & 0xF;
            if ((pred + 1) * 16 > bookN) pred = 0;
            int b1 = pred * 16, b2 = b1 + 8;
            for (int j = 0; j < 16; j++)
            {
                int n = Dm(inA + 1 + (uint)(j / 2));
                n = (j & 1) != 0 ? (n & 0xF) : (n >> 4);
                if (n >= 8) n -= 16;
                res[j] = n * (1 << scale);
            }
            for (int h = 0; h < 2; h++)
            {
                int l1 = h != 0 ? o[6] : last[14];
                int l2 = h != 0 ? o[7] : last[15];
                int fm = h * 8;
                for (int i = 0; i < 8; i++)
                {
                    int acc = res[fm + i] * 2048 + book[b1 + i] * l1 + book[b2 + i] * l2;
                    for (int j = 0; j < i; j++) acc += res[fm + i - j - 1] * book[b2 + j];
                    o[h * 8 + i] = Clamp16(acc >> 11);
                }
            }
            for (uint j = 0; j < 16; j++) DmWr(outA + 2 * j, o[j]);
            Array.Copy(o, last, 16);
            inA += 9; outA += 32;
        }
        for (uint k = 0; k < 16; k++) Mem.Set(st + 2 * k, last[k]);
    }

    private void Resample(uint w0, uint w1)
    {
        uint f = (w0 >> 16) & 0xFF;
        uint step = (w0 & 0xFFFF) << 1;       // 16.16; field 0x8000 = ratio 1.0
        uint st = Resolve(w1);
        var hist = new short[4];
        uint pos = 0, n = (uint)count / 2;
        if ((f & A_INIT) == 0)
        {
            for (uint k = 0; k < 4; k++) hist[k] = Mem.S16(st + 2 * k);
            pos = Mem.U16(st + 8);
        }
        short X(int i) => i < 0 ? hist[4 + i] : DmRd(inp + 2 * (uint)i);
        for (uint k = 0; k < n; k++)
        {
            int i = (int)(pos >> 16), fr = (int)((pos >> 10) & 63);
            int acc = ResampTab[fr, 0] * X(i - 4) + ResampTab[fr, 1] * X(i - 3) + ResampTab[fr, 2] * X(i - 2) + ResampTab[fr, 3] * X(i - 1);
            DmWr(outp + 2 * k, Clamp16((acc + 16384) >> 15));
            pos += step;
        }
        uint consumed = pos >> 16;
        for (uint k = 0; k < 4; k++) Mem.Set(st + 2 * k, X((int)consumed - 4 + (int)k));
        Mem.Set(st + 8, (short)(pos & 0xFFFF));
        Mem.Set(st + 10, (short)0); Mem.Set(st + 12, (short)0); Mem.Set(st + 14, (short)0);
    }

    /// <summary>
    /// ENVMIXER (exponential envelope): the state block is 80 bytes (alEnvmixerNew); words 0..9 hold wet (low half of word 0), dry (low half of word 1),
    /// target L/R, rate L/R, the exponential sequence L/R and the current value L/R. Every 8 samples the sequence is multiplied by the 16.16 rate and
    /// the per-sample step becomes (seq - value) / 8.
    /// </summary>
    private void EnvMixer(uint w0, uint w1)
    {
        uint f = (w0 >> 16) & 0xFF, n = (uint)count / 2;
        uint st = Resolve(w1);
        var val = new int[2]; var tgt = new int[2]; var rt = new int[2]; var seq = new int[2]; var step = new int[2];
        short d = dry, w = wet;
        bool aux = (f & A_AUX) != 0;
        if ((f & A_INIT) != 0)
        {
            for (int lr = 0; lr < 2; lr++)
            {
                val[lr] = vol[lr] << 16; tgt[lr] = target[lr] << 16; rt[lr] = rate[lr];
                seq[lr] = (int)((long)vol[lr] * rate[lr]);
            }
        }
        else
        {
            w = Mem.S16(st + 2); d = Mem.S16(st + 6);
            for (uint lr = 0; lr < 2; lr++)
            {
                tgt[lr] = Mem.S32(st + 8 + 4 * lr); rt[lr] = Mem.S32(st + 16 + 4 * lr);
                seq[lr] = Mem.S32(st + 24 + 4 * lr); val[lr] = Mem.S32(st + 32 + 4 * lr);
            }
        }
        step[0] = tgt[0] - val[0]; step[1] = tgt[1] - val[1];
        uint k = 0;
        for (uint y = 0; y < n; y += 8)
        {
            for (int lr = 0; lr < 2; lr++)
                if (step[lr] != 0)
                {
                    seq[lr] = (int)(((long)seq[lr] * rt[lr]) >> 16);
                    step[lr] = (seq[lr] - val[lr]) >> 3;
                }
            for (int x = 0; x < 8 && k < n; x++, k++)
            {
                int s = DmRd(inp + 2 * k);
                var v = new int[2];
                for (int lr = 0; lr < 2; lr++)
                {
                    val[lr] += step[lr];
                    bool reached = step[lr] <= 0 ? val[lr] <= tgt[lr] : val[lr] >= tgt[lr];
                    if (reached) { val[lr] = tgt[lr]; step[lr] = 0; }
                    v[lr] = val[lr] >> 16;
                }
                int g = Clamp16((v[0] * d + 0x4000) >> 15); uint o = outp + 2 * k;
                DmWr(o, Clamp16(DmRd(o) + ((s * g) >> 15)));
                g = Clamp16((v[1] * d + 0x4000) >> 15); o = dryRight + 2 * k;
                DmWr(o, Clamp16(DmRd(o) + ((s * g) >> 15)));
                if (aux)
                {
                    g = Clamp16((v[0] * w + 0x4000) >> 15); o = wetLeft + 2 * k;
                    DmWr(o, Clamp16(DmRd(o) + ((s * g) >> 15)));
                    g = Clamp16((v[1] * w + 0x4000) >> 15); o = wetRight + 2 * k;
                    DmWr(o, Clamp16(DmRd(o) + ((s * g) >> 15)));
                }
            }
        }
        Mem.Set(st + 0, (short)0); Mem.Set(st + 2, w); Mem.Set(st + 4, (short)0); Mem.Set(st + 6, d);
        for (uint lr = 0; lr < 2; lr++)
        {
            Mem.Set(st + 8 + 4 * lr, tgt[lr]); Mem.Set(st + 16 + 4 * lr, rt[lr]);
            Mem.Set(st + 24 + 4 * lr, seq[lr]); Mem.Set(st + 32 + 4 * lr, val[lr]);
        }
    }

    private void Mixer(uint w0, uint w1)
    {
        int gain = (short)w0;
        uint inA = w1 >> 16, outA = w1 & 0xFFFF, n = (uint)count / 2;
        for (uint k = 0; k < n; k++)
            DmWr(outA + 2 * k, Clamp16(DmRd(outA + 2 * k) + ((DmRd(inA + 2 * k) * gain) >> 15)));
    }

    /// <summary>count is the byte length of ONE channel: the output is twice as long (the following SAVEBUFF saves 2 * count bytes).</summary>
    private void Interleave(uint w1)
    {
        uint l = w1 >> 16, r = w1 & 0xFFFF, n = (uint)count / 2;
        for (uint k = 0; k < n; k++)
        {
            short a = DmRd(l + 2 * k), b = DmRd(r + 2 * k);
            DmWr(outp + 4 * k, a);
            DmWr(outp + 4 * k + 2, b);
        }
    }

    /// <summary>POLEF: the reverb's pole filter (8-sample frames, coefficients in the codebook).</summary>
    private void Polef(uint w0, uint w1)
    {
        uint f = (w0 >> 16) & 0xFF, gain = w0 & 0xFFFF;
        uint st = Resolve(w1);
        uint inA = inp, outA = outp, cnt = ((uint)count + 15) & ~15u;
        var h1 = new short[8]; var h2 = new short[8]; var h2b = new short[8]; var frame = new short[8]; var o = new short[8];
        int l1 = 0, l2 = 0;
        for (int i = 0; i < 8; i++)
        {
            h1[i] = book[i]; h2b[i] = book[8 + i];
            h2[i] = (short)(((int)h2b[i] * (int)gain) >> 14);
            book[8 + i] = h2[i];
        }
        if ((f & A_INIT) == 0) { l1 = Mem.S16(st + 4); l2 = Mem.S16(st + 6); }
        for (uint k = 0; k < cnt; k += 16)
        {
            for (uint i = 0; i < 8; i++) frame[i] = DmRd(inA + k + 2 * i);
            for (int i = 0; i < 8; i++)
            {
                int acc = (int)frame[i] * (int)gain + h1[i] * l1 + h2b[i] * l2;
                for (int j = 0; j < i; j++) acc += h2[j] * frame[i - 1 - j];
                o[i] = Clamp16(acc >> 14);
            }
            for (uint i = 0; i < 8; i++) DmWr(outA + k + 2 * i, o[i]);
            l1 = o[6]; l2 = o[7];
        }
        for (uint i = 0; i < 4; i++) Mem.Set(st + 2 * i, o[4 + (int)i]);
    }

    /// <summary>Runs a command list at a guest address. Returns the number of commands executed.</summary>
    public int Run(uint cmdListAddr, uint byteLen)
    {
        uint baseAddr = 0x80000000u | (cmdListAddr & 0x7FFFFF);
        int n = 0;
        for (uint off = 0; off + 8 <= byteLen; off += 8, n++)
        {
            uint w0 = Mem.U32(baseAddr + off), w1 = Mem.U32(baseAddr + off + 4);
            switch (w0 >> 24)
            {
                case A_SPNOOP: break;
                case A_ADPCM: Adpcm(w0, w1); break;
                case A_CLEARBUFF: ClearBuff(w0, w1); break;
                case A_ENVMIXER: EnvMixer(w0, w1); break;
                case A_LOADBUFF: LoadBuff(w1); break;
                case A_RESAMPLE: Resample(w0, w1); break;
                case A_SAVEBUFF: SaveBuff(w1); break;
                case A_SEGMENT: seg[(w1 >> 24) & 0xF] = w1 & 0xFFFFFF; break;
                case A_SETBUFF: SetBuff(w0, w1); break;
                case A_SETVOL: SetVol(w0, w1); break;
                case A_DMEMMOVE: DmemMove(w0, w1); break;
                case A_LOADADPCM: LoadAdpcm(w0, w1); break;
                case A_MIXER: Mixer(w0, w1); break;
                case A_INTERLEAVE: Interleave(w1); break;
                case A_POLEF: Polef(w0, w1); break;
                case A_SETLOOP: loop = w1; break;        // resolved lazily at A_ADPCM time
                default: Unknown++; break;
            }
        }
        return n;
    }
}
