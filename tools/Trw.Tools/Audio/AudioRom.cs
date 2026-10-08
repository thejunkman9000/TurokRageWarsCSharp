namespace Trw.Tools.Audio;

public sealed class SfxEntry
{
    public int TreeIndex;        // index into the sound tree (what func_0025828C takes directly)
    public uint Key;             // game sound id (sorted key table; duplicates = random variants)
    public int Weight;           // variant weight (x10 in the chooser)
    public byte[] Header = Array.Empty<byte>();
    public int Stride, Count;
    public List<byte[]> Records = new();  // SndInfo records; record.w0 (s16) = index into SFX instrument sound array
}

public sealed class SongEntry
{
    public int Index;
    public uint Key;             // song id used by the game
    public int Volume;           // node[2i] w0
    public int TempoParam;       // node[2i] h4
    public int SeqOffset, SeqLength;  // ROM offsets
}

/// <summary>Locates and parses all audio data of Turok: Rage Wars (US v1.1).</summary>
public sealed class AudioRom
{
    public const int TableRomOffset = 0x28B244;   // 8 x u32 ROM offsets, see docs/formats/audio.md

    public byte[] Rom;
    public uint[] Table = new uint[8];
    public byte[] BankBlock = Array.Empty<byte>();
    public int BankBlockRom;
    public int SampleBase;                          // ROM offset of the sample region
    public int SampleEnd;
    public AlBankFile MusicBanks = null!;           // block+0x14: 21-instrument 44.1 kHz bank (music)
    public AlBankFile SfxBanks = null!;             // block+0x38EC (second bank file)
    public int SfxRoot, SfxRootEnd;
    public List<SfxEntry> Sfx = new();
    public List<SongEntry> Songs = new();
    public AlInstrument SfxInstrument => SfxBanks.Banks[0].Instruments[0]!;

    public AudioRom(byte[] rom)
    {
        Rom = rom;
        for (int i = 0; i < 8; i++) Table[i] = Be.U32(rom, TableRomOffset + 4 * i);
        BankBlockRom = (int)Table[0];
        int blkLen = (int)(Table[1] - Table[0]);
        BankBlock = new byte[blkLen];
        Array.Copy(rom, BankBlockRom, BankBlock, 0, blkLen);
        SampleBase = (int)Table[2];
        SampleEnd = (int)Table[3];
        // Block header: u32 2, ptr bankFile0, u32 0, ptr bankFile1, ...
        int bf0 = AlBankPtr(Be.U32(BankBlock, 4));
        int bf1 = AlBankPtr(Be.U32(BankBlock, 0xC));
        MusicBanks = AlBankFile.Parse(BankBlock, bf0);
        SfxBanks = AlBankFile.Parse(BankBlock, bf1);
        SfxRoot = (int)Table[4];
        SfxRootEnd = (int)Table[5];
        ParseSoundTables();
    }

    static int AlBankPtr(uint p) => (int)(p - AlBankFile.PtrBase);

    void ParseSoundTables()
    {
        var root = new Container(Rom, SfxRoot);
        var s3 = root.Sub(0);                 // sound tables
        var keys = s3.SectionOffset(0);       // 8 byte header, then u32 keys (sorted)
        var weights = s3.SectionOffset(1);    // 8 byte header, then s32 weights
        var tree = s3.Sub(2);
        int n = tree.Count;
        for (int i = 0; i < n; i++)
        {
            var e = tree.Sub(i);
            var se = new SfxEntry
            {
                TreeIndex = i,
                Key = Be.U32(Rom, keys + 8 + 4 * i),
                Weight = Be.S32(Rom, weights + 8 + 4 * i),
            };
            int h0 = e.SectionOffset(0), h1 = e.SectionEnd(0);
            se.Header = Rom[h0..h1];
            int r = e.SectionOffset(1);
            se.Stride = Be.S32(Rom, r);
            se.Count = Be.S32(Rom, r + 4);
            for (int k = 0; k < se.Count; k++)
                se.Records.Add(Rom[(r + 8 + k * se.Stride)..(r + 8 + (k + 1) * se.Stride)]);
            Sfx.Add(se);
        }
        // music: root[1] -> [0] key table (8-byte header, u32 keys) ; [1] container of (info, sequence) pairs
        var m = root.Sub(1);
        int kt = m.SectionOffset(0);
        int keyCount = Be.S32(Rom, kt + 4);
        var pairs = m.Sub(1);
        for (int i = 0; i < keyCount; i++)
        {
            int infoOff = pairs.SectionOffset(2 * i);
            int seqOff = pairs.SectionOffset(2 * i + 1);
            int seqEnd = pairs.SectionEnd(2 * i + 1);
            Songs.Add(new SongEntry
            {
                Index = i,
                Key = Be.U32(Rom, kt + 8 + 4 * i),
                Volume = Be.S32(Rom, infoOff),
                TempoParam = Be.U16(Rom, infoOff + 4),
                SeqOffset = seqOff,
                SeqLength = seqEnd - seqOff,
            });
        }
    }

    /// <summary>Decode a wavetable to PCM. Returns null if unsupported.</summary>
    public short[]? Decode(AlWaveTable wt)
    {
        int off = SampleBase + wt.Base;
        if (off < 0 || off + wt.Len > Rom.Length) return null;
        if (wt.Type == 0)
        {
            if (wt.Book == null) return null;
            return Vadpcm.Decode(Rom, off, wt.Len, wt.Book, wt.Order, wt.NPredictors);
        }
        if (wt.Type == 1)
        {
            var pcm = new short[wt.Len / 2];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = Be.S16(Rom, off + 2 * i);
            return pcm;
        }
        return null;
    }
}
