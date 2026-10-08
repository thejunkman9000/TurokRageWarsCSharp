namespace Trw.Tools.Audio;

public sealed class AlEnvelope { public int Attack, Decay, Release; public int AttackVol, DecayVol; }
public sealed class AlKeyMap { public int VelMin, VelMax, KeyMin, KeyMax, KeyBase, Detune; }
public sealed class AlLoop { public uint Start, End, Count; public short[]? State; }

public sealed class AlWaveTable
{
    public int Offset;          // block-relative offset of this struct
    public int Base;            // byte offset into the sample region
    public int Len;             // bytes
    public int Type;            // 0 = ADPCM, 1 = RAW16
    public int Flags;
    public AlLoop? Loop;
    public int Order, NPredictors;
    public short[]? Book;
}

public sealed class AlSound
{
    public int Offset;
    public AlEnvelope? Envelope;
    public AlKeyMap? KeyMap;
    public AlWaveTable? Wave;
    public int Pan, Volume, Flags;
}

public sealed class AlInstrument
{
    public int Offset;
    public int Volume, Pan, Priority, Flags;
    public int TremType, TremRate, TremDepth, TremDelay, VibType, VibRate, VibDepth, VibDelay;
    public int BendRange;
    public List<AlSound> Sounds = new();
}

public sealed class AlBank
{
    public int Offset;
    public int Flags, SampleRate;
    public AlInstrument? Percussion;
    public List<AlInstrument?> Instruments = new();
}

/// <summary>
/// ALBankFile as stored in this game: a "pre-relocated" image where every pointer is 0x00800000 + offset from the
/// start of the loaded block (the block starts at ROM 0x28B264).
/// </summary>
public sealed class AlBankFile
{
    public const uint PtrBase = 0x800000;
    public int Offset;
    public int Revision;
    public List<AlBank> Banks = new();

    static int Rel(uint p) => p == 0 ? -1 : checked((int)(p - PtrBase));

    public static AlBankFile Parse(byte[] blk, int fileOffset)
    {
        var bf = new AlBankFile { Offset = fileOffset, Revision = Be.U16(blk, fileOffset) };
        int n = Be.U16(blk, fileOffset + 2);
        var cache = new Dictionary<int, AlWaveTable>();
        var instCache = new Dictionary<int, AlInstrument>();
        for (int i = 0; i < n; i++)
        {
            int bo = Rel(Be.U32(blk, fileOffset + 4 + 4 * i));
            if (bo < 0) continue;
            var bank = new AlBank { Offset = bo, Flags = blk[bo + 2], SampleRate = Be.S32(blk, bo + 4) };
            int ic = Be.U16(blk, bo);
            int po = Rel(Be.U32(blk, bo + 8));
            if (po >= 0) bank.Percussion = ParseInst(blk, po, instCache, cache);
            for (int k = 0; k < ic; k++)
            {
                int io = Rel(Be.U32(blk, bo + 12 + 4 * k));
                bank.Instruments.Add(io < 0 ? null : ParseInst(blk, io, instCache, cache));
            }
            bf.Banks.Add(bank);
        }
        return bf;
    }

    static AlInstrument ParseInst(byte[] b, int o, Dictionary<int, AlInstrument> ic, Dictionary<int, AlWaveTable> wc)
    {
        if (ic.TryGetValue(o, out var cached)) return cached;
        var ins = new AlInstrument
        {
            Offset = o, Volume = b[o], Pan = b[o + 1], Priority = b[o + 2], Flags = b[o + 3],
            TremType = b[o + 4], TremRate = b[o + 5], TremDepth = b[o + 6], TremDelay = b[o + 7],
            VibType = b[o + 8], VibRate = b[o + 9], VibDepth = b[o + 10], VibDelay = b[o + 11],
            BendRange = Be.S16(b, o + 12),
        };
        ic[o] = ins;
        int sc = Be.U16(b, o + 14);
        for (int s = 0; s < sc; s++)
        {
            int so = Rel(Be.U32(b, o + 16 + 4 * s));
            ins.Sounds.Add(ParseSound(b, so, wc));
        }
        return ins;
    }

    static AlSound ParseSound(byte[] b, int o, Dictionary<int, AlWaveTable> wc)
    {
        var snd = new AlSound { Offset = o, Pan = b[o + 12], Volume = b[o + 13], Flags = b[o + 14] };
        int eo = Rel(Be.U32(b, o)), ko = Rel(Be.U32(b, o + 4)), wo = Rel(Be.U32(b, o + 8));
        if (eo >= 0)
            snd.Envelope = new AlEnvelope
            {
                Attack = Be.S32(b, eo), Decay = Be.S32(b, eo + 4), Release = Be.S32(b, eo + 8),
                AttackVol = b[eo + 12], DecayVol = b[eo + 13],
            };
        if (ko >= 0)
            snd.KeyMap = new AlKeyMap
            {
                VelMin = b[ko], VelMax = b[ko + 1], KeyMin = b[ko + 2], KeyMax = b[ko + 3], KeyBase = b[ko + 4],
                Detune = (sbyte)b[ko + 5],
            };
        if (wo >= 0)
        {
            if (!wc.TryGetValue(wo, out var wt))
            {
                wt = new AlWaveTable { Offset = wo, Base = Be.S32(b, wo), Len = Be.S32(b, wo + 4), Type = b[wo + 8], Flags = b[wo + 9] };
                int lo = Rel(Be.U32(b, wo + 12));
                if (lo >= 0)
                {
                    wt.Loop = new AlLoop { Start = Be.U32(b, lo), End = Be.U32(b, lo + 4), Count = Be.U32(b, lo + 8) };
                    if (wt.Type == 0)
                    {
                        wt.Loop.State = new short[16];
                        for (int i = 0; i < 16; i++) wt.Loop.State[i] = Be.S16(b, lo + 12 + 2 * i);
                    }
                }
                if (wt.Type == 0)
                {
                    int bo = Rel(Be.U32(b, wo + 16));
                    if (bo >= 0)
                    {
                        wt.Order = Be.S32(b, bo); wt.NPredictors = Be.S32(b, bo + 4);
                        wt.Book = new short[wt.Order * wt.NPredictors * 8];
                        for (int i = 0; i < wt.Book.Length; i++) wt.Book[i] = Be.S16(b, bo + 8 + 2 * i);
                    }
                }
                wc[wo] = wt;
            }
            snd.Wave = wt;
        }
        return snd;
    }
}
