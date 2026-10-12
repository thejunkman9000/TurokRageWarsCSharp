using System.Buffers.Binary;
using System.Text;

namespace Trw.Platform;

/// <summary>What is plugged into a controller's accessory slot.</summary>
public enum PakAccessory { None = 0, Memory = 1, Rumble = 2 }

/// <summary>
/// The two CRCs of the controller accessory protocol (Joybus commands 0x02 / 0x03), as libultra's __osContAddressCrc / __osContDataCrc
/// compute them and the controller checks / answers them (port of the C port's pak_crc5 / pak_crc8, port/pak.c).
/// </summary>
public static class PakCrc
{
    /// <summary>5-bit CRC of an 11-bit block number (polynomial x^5+x^4+x^2+1, 16 shifts: the block number followed by five zero bits).</summary>
    public static byte Address(int block)
    {
        uint crc = 0, a = (uint)block & 0x7FF;
        for (int i = 0; i < 16; i++)
        {
            uint bit = (a >> 10) & 1, tap = (crc & 0x10) != 0 ? 0x15u : 0u;
            a <<= 1;
            crc = (((crc << 1) & 0xFF) | bit) ^ tap;
        }
        return (byte)(crc & 0x1F);
    }

    /// <summary>The 16-bit address word of a command: block number &lt;&lt; 5 | its CRC (0x8001 for the rumble probe at 0x8000, 0xC01B for the motor at 0xC000).</summary>
    public static ushort AddressWord(int block) => (ushort)(((block & 0x7FF) << 5) | Address(block));

    /// <summary>CRC-8 of a 32-byte block (polynomial 0x85, seed 0, 33 byte times: the last one shifts in zeros). Same as Project64's CMempak::CalculateCrc.</summary>
    public static byte Data(ReadOnlySpan<byte> d)
    {
        uint crc = 0;
        for (int i = 0; i <= 32; i++)
            for (int b = 7; b >= 0; b--)
            {
                uint tap = (crc & 0x80) != 0 ? 0x85u : 0u, bit = i < 32 ? (uint)(d[i] >> b) & 1u : 0u;
                crc = (((crc << 1) & 0xFF) | bit) ^ tap;
            }
        return (byte)crc;
    }
}

/// <summary>
/// The memory pak's file system layout (libultra "pfs", one 32 KiB bank of 128 pages of 256 bytes) and the image Project64 writes for a new pak.
///   page 0      label (0x00-0x1F, unused by libultra) and the id block, four copies at 0x20 / 0x60 / 0x80 / 0xC0 (repaired u32, random u32, serial 16 bytes,
///               device id u16, banks u8, version u8, checksum u16 = sum of the first 14 big-endian words, inverted checksum u16 = 0xFFF2 - checksum)
///   pages 1, 2  index table (inode) and its backup: 128 big-endian u16 page links, entries 0-4 reserved (byte 1 = byte sum of entries 5..127),
///               1 = last page of a note, 3 = free page, otherwise the next page
///   pages 3, 4  note table: 16 entries of 32 bytes (game code u32, publisher u16, first page u16, status, reserved, data sum u16, extension[4], name[16])
///   pages 5-127 note data
/// Sources (checked 2026-10-11):
///   Project64 Source/Project64-core/N64System/Mips/Mempak.cpp (github.com/project64/project64, branch develop): CMempak::Format (the 0x300-byte
///     "Initialize" table followed by zeros: this is <see cref="FormatProject64"/>), CMempak::CalculateCrc, ReadFrom / WriteTo (0x8000+ reads zeros,
///     ignores writes), LoadMempak (file "&lt;Game_GameName&gt;_Cont_&lt;port&gt;.mpk", 0x8000 bytes raw, in Directory_NativeSave, optionally in the
///     per-game folder Game_UniqueSaveDir = "&lt;name&gt;-&lt;MD5&gt;").
///   Project64 Source/Project64-core/N64System/N64Rom.cpp CN64Rom::CleanRomName: the game name is the 20-byte internal ROM title, trailing blanks removed,
///     '/' and '\' -> '-', ':' -> ';' ("Turok: Rage Wars" -> "Turok; Rage Wars").
///   Project64 Source/Project64-core/Settings.cpp: Directory_NativeSave defaults to "Save" under the Project64 folder, "Unique Game Dir" defaults to on.
///   n64brew "Controller Pak" / "Joybus Protocol" and libultra (pfsinitpak.c, pfschecker.c, contramread.c) for the layout and the protocol.
/// </summary>
public static class Mempak
{
    public const int Size = 0x8000;
    public const int PageSize = 0x100;
    public static readonly int[] IdBlockOffsets = { 0x20, 0x60, 0x80, 0xC0 };

    /// <summary>Serial part of Project64's id block (bytes 0-23: repaired = 0xFFFFFFFF, random = 0x051A5F13, 8 zero bytes, 8 0xFF bytes).</summary>
    private static readonly byte[] Pj64Serial =
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0x05, 0x1A, 0x5F, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    /// <summary>Writes the image Project64 creates for a new pak (CMempak::Format): label 81 01 02 .. 1F, the id block (device id 0xFFFF, 1 bank,
    /// version 0xFF, checksums 0x6625 / 0x99CD) at 0x20 / 0x60 / 0x80 / 0xC0, both index tables with every data page free, an empty note table.</summary>
    public static void FormatProject64(Span<byte> img)
    {
        img[..Size].Clear();
        img[0] = 0x81;
        for (int i = 1; i < 32; i++) img[i] = (byte)i;
        Span<byte> id = stackalloc byte[32];
        Pj64Serial.CopyTo(id);
        id[24] = 0xFF; id[25] = 0xFF;          // device id (bit 0 = controller pak)
        id[26] = 0x01;                         // banks
        id[27] = 0xFF;                         // version
        var (sum, inv) = IdSums(id);
        BinaryPrimitives.WriteUInt16BigEndian(id[28..], sum);
        BinaryPrimitives.WriteUInt16BigEndian(id[30..], inv);
        foreach (int o in IdBlockOffsets) id.CopyTo(img[o..]);
        for (int t = 1; t <= 2; t++)
        {
            var page = img.Slice(t * PageSize, PageSize);
            for (int e = 5; e < 128; e++) page[e * 2 + 1] = 3;
            page[1] = IndexSum(page);
        }
    }

    public static byte[] NewProject64Image() { var b = new byte[Size]; FormatProject64(b); return b; }

    /// <summary>Id block checksums: sum of the first 14 big-endian words, and 0xFFF2 minus that (= the sum of the inverted words).</summary>
    public static (ushort sum, ushort inverted) IdSums(ReadOnlySpan<byte> id)
    {
        uint s = 0;
        for (int i = 0; i < 28; i += 2) s += BinaryPrimitives.ReadUInt16BigEndian(id[i..]);
        return ((ushort)s, (ushort)(0xFFF2u - s));
    }

    /// <summary>Index table checksum (libultra __osSumcalc over entries 5..127): byte sum of bytes 10..255 of the page, stored in byte 1.</summary>
    public static byte IndexSum(ReadOnlySpan<byte> page)
    {
        uint s = 0;
        for (int i = 10; i < PageSize; i++) s += page[i];
        return (byte)s;
    }

    /// <summary>An all-0x00 or all-0xFF image: an unformatted card.</summary>
    public static bool IsBlank(ReadOnlySpan<byte> img)
    {
        byte a = img[0];
        if (a != 0x00 && a != 0xFF) return false;
        return !img[..Size].ContainsAnyExcept(a);
    }

    /// <summary>Structural check: at least one valid id block copy, both index table checksums.</summary>
    public static bool Check(ReadOnlySpan<byte> img, out string why)
    {
        int ok = 0;
        foreach (int o in IdBlockOffsets)
        {
            var b = img.Slice(o, 32);
            var (s, inv) = IdSums(b);
            if (BinaryPrimitives.ReadUInt16BigEndian(b[28..]) == s && BinaryPrimitives.ReadUInt16BigEndian(b[30..]) == inv) ok++;
        }
        if (ok == 0) { why = "no valid id block"; return false; }
        for (int t = 1; t <= 2; t++)
            if (IndexSum(img.Slice(t * PageSize, PageSize)) != img[t * PageSize + 1]) { why = $"index table {t} checksum"; return false; }
        why = $"{ok} of 4 id blocks valid";
        return true;
    }

    /// <summary>One used entry of the note table.</summary>
    public readonly record struct Note(int Index, uint GameCode, ushort Publisher, int StartPage, string Name, string Extension, int Pages);

    /// <summary>The used notes (game code and publisher not zero) with the length of their page chain in the primary index table (-1 = broken chain).</summary>
    public static List<Note> Notes(ReadOnlySpan<byte> img)
    {
        var list = new List<Note>();
        var inode = img.Slice(PageSize, PageSize);
        for (int n = 0; n < 16; n++)
        {
            var e = img.Slice(3 * PageSize + n * 32, 32);
            uint game = BinaryPrimitives.ReadUInt32BigEndian(e);
            ushort pub = BinaryPrimitives.ReadUInt16BigEndian(e[4..]);
            if (game == 0 || pub == 0) continue;
            int start = e[7], pages = 0, p = start;
            while (p >= 5 && p < 128 && pages <= 128) { pages++; int next = BinaryPrimitives.ReadUInt16BigEndian(inode[(p * 2)..]); if (next == 1) break; p = next & 0xFF; if (next < 5 || next >= 128) { pages = -1; break; } }
            if (pages > 128) pages = -1;
            list.Add(new Note(n, game, pub, start, DecodeName(e.Slice(16, 16)), DecodeName(e.Slice(12, 4)), pages));
        }
        return list;
    }

    /// <summary>Note names use the N64 font code (0x0F = space, 0x10-0x19 digits, 0x1A-0x33 A-Z, a few punctuation marks).</summary>
    public static string DecodeName(ReadOnlySpan<byte> s)
    {
        const string punct = "!\"#'*+,-./:=?@";
        var sb = new StringBuilder();
        foreach (byte c in s)
        {
            if (c == 0) break;
            sb.Append(c switch
            {
                0x0F => ' ',
                >= 0x10 and <= 0x19 => (char)('0' + c - 0x10),
                >= 0x1A and <= 0x33 => (char)('A' + c - 0x1A),
                >= 0x34 and < 0x34 + 14 => punct[c - 0x34],
                _ => '?',
            });
        }
        return sb.ToString();
    }
}

/// <summary>
/// One controller's accessory slot: a memory pak backed by a 32 KiB image file (Project64 .mpk layout), a rumble pak, or nothing.
/// Device behaviour (port of the C port's port/pak.c, which follows the hardware):
///   memory pak : blocks 0x000-0x3FF (addresses 0x0000-0x7FFF) are the SRAM; 0x8000-0xFFFF reads zeros and ignores writes, so libultra's bank
///                probe sees one bank and its rumble probe fails with PFS_ERR_DEVICE (Project64 answers the same).
///   rumble pak : 0x8000-0xBFFF identification (reads 0x80 once 0x80 was written there, else 0), 0xC000-0xFFFF motor (bit 0 = on); 0x0000-0x7FFF reads 0.
///   identify status: 1 = accessory present, 2 = none, 3 = a new accessory since the last identify (cleared by it), +4 = address CRC error in the
///   previous command. A freshly inserted accessory does not answer reads / writes until it was identified, so libultra reports PFS_ERR_NEW_PACK.
/// The image is loaded on first use; a missing, empty, all-0x00 or all-0xFF file becomes Project64's freshly formatted image. Changes mark the pak
/// dirty; <see cref="Flush"/> writes the whole file atomically (temp file in the same folder, then a rename over the old one).
/// </summary>
public sealed class ControllerPak
{
    public int Port { get; }
    public PakAccessory Accessory { get; private set; }
    /// <summary>Backing file (absolute); null = memory only.</summary>
    public string? FilePath { get; }
    public bool Persistent { get; private set; }
    public bool Loaded { get; private set; }
    public bool Dirty { get { lock (gate) return dirty; } }
    public bool MotorOn { get; private set; }
    public long Reads, Writes, Identifies, CrcErrors;
    /// <summary>Called when the rumble motor changes (port, on); null = nobody vibrates.</summary>
    public Action<int, bool>? MotorChanged;

    private readonly object gate = new();
    private readonly Action<string> log;
    private readonly bool trace;
    private byte[] file = Array.Empty<byte>();      // the whole file (a longer file keeps its tail); the pak is bytes 0..0x7FFF
    private bool dirty, needBackup, backedUp;
    private long gen, tFirst, tLast, retryAt;
    private int fails;
    private bool changed, fresh, crcErr, unlocked;

    public ControllerPak(int port, PakAccessory accessory, string? filePath, Action<string>? log = null, bool trace = false)
    {
        Port = port; FilePath = filePath; this.log = log ?? (_ => { }); this.trace = trace;
        Accessory = accessory;
        changed = fresh = accessory != PakAccessory.None;       // at power-on the first identify reports "new accessory"
    }

    /// <summary>The 32 KiB image (loads the file on first use).</summary>
    public Span<byte> Image { get { lock (gate) { EnsureLoaded(); return file.AsSpan(0, Mempak.Size); } } }

    /// <summary>Plugs in another accessory (or none): the next identify reports a new accessory.</summary>
    public void Insert(PakAccessory a)
    {
        lock (gate)
        {
            if (a == Accessory) return;
            Accessory = a; changed = true; fresh = a != PakAccessory.None; unlocked = false; crcErr = false;
            SetMotor(false);
            log($"port {Port + 1}: {Name(a)} inserted");
        }
    }

    public static string Name(PakAccessory a) => a switch { PakAccessory.Memory => "memory pak", PakAccessory.Rumble => "rumble pak", _ => "nothing" };

    /// <summary>The status byte of an identify reply / OSContStatus.status (consumes the "new accessory" and "CRC error" latches).</summary>
    public byte IdentifyStatus()
    {
        lock (gate)
        {
            byte st = (byte)(Accessory == PakAccessory.None ? 2 : changed ? 3 : 1);
            if (crcErr) st |= 4;
            changed = fresh = crcErr = false; Identifies++;
            return st;
        }
    }

    /// <summary>Joybus 0x02: reads block <paramref name="addressWord"/> &gt;&gt; 5 into <paramref name="data"/> (32 bytes) and returns the data CRC byte of the reply.</summary>
    public byte Read(ushort addressWord, Span<byte> data)
    {
        lock (gate)
        {
            int blk = addressWord >> 5;
            bool ok = (addressWord & 31) == PakCrc.Address(blk);
            if (!ok) { crcErr = true; CrcErrors++; }
            data[..32].Clear();
            if (Accessory == PakAccessory.None || fresh || !ok)
            {
                if (trace) log($"port {Port + 1}: read blk {blk:X3} {(ok ? "" : "ADDR-CRC-ERR ")}(no answer)");
                return 0xFF;
            }
            Reads++;
            if (Accessory == PakAccessory.Memory) { if (blk < 0x400) { EnsureLoaded(); file.AsSpan(blk * 32, 32).CopyTo(data); } }
            else if (blk >= 0x400 && blk < 0x600) data[..32].Fill(unlocked ? (byte)0x80 : (byte)0);
            else if (blk >= 0x600) data[..32].Fill(MotorOn ? (byte)1 : (byte)0);
            if (trace) log($"port {Port + 1}: read blk {blk:X3}");
            return PakCrc.Data(data);
        }
    }

    /// <summary>Joybus 0x03: writes 32 bytes to block <paramref name="addressWord"/> &gt;&gt; 5 and returns the reply byte (CRC of the data it received,
    /// complemented when the accessory did not take the data: none, not yet identified, or a bad address CRC).</summary>
    public byte Write(ushort addressWord, ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            int blk = addressWord >> 5;
            bool ok = (addressWord & 31) == PakCrc.Address(blk);
            if (!ok) { crcErr = true; CrcErrors++; }
            byte crc = PakCrc.Data(data);
            if (Accessory == PakAccessory.None || fresh || !ok)
            {
                if (trace) log($"port {Port + 1}: write blk {blk:X3} {(ok ? "" : "ADDR-CRC-ERR ")}(no answer)");
                return (byte)~crc;
            }
            Writes++;
            if (Accessory == PakAccessory.Memory)
            {
                if (blk < 0x400)
                {
                    EnsureLoaded();
                    var dst = file.AsSpan(blk * 32, 32);
                    if (!dst.SequenceEqual(data[..32])) { data[..32].CopyTo(dst); MarkDirty(); }
                }
            }
            else if (blk >= 0x400 && blk < 0x600) unlocked = data[31] == 0x80;
            else if (blk >= 0x600) SetMotor((data[31] & 1) != 0);
            if (trace) log($"port {Port + 1}: write blk {blk:X3}");
            return crc;
        }
    }

    private void SetMotor(bool on)
    {
        if (on == MotorOn) return;
        MotorOn = on;
        if (trace) log($"port {Port + 1}: rumble motor {(on ? "ON" : "off")}");
        MotorChanged?.Invoke(Port, on);
    }

    private void MarkDirty()
    {
        if (!Persistent) return;
        long now = Environment.TickCount64;
        if (!dirty) { dirty = true; tFirst = now; }
        tLast = now; gen++;
    }

    /// <summary>Loads the image file (lock held). Missing / empty / blank files become Project64's formatted image (and are created on the next flush);
    /// an unreadable or read-only file gives a session-only pak and is never touched.</summary>
    private void EnsureLoaded()
    {
        if (Loaded) return;
        Loaded = true;
        Persistent = FilePath != null;
        string what;
        byte[]? data = null;
        if (FilePath != null)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Length > 16 << 20) { Persistent = false; log($"{FilePath}: too large ({fi.Length} bytes) to be a pak image - session-only pak, the file is left alone"); }
                    else
                    {
                        data = File.ReadAllBytes(FilePath);
                        if (fi.IsReadOnly) { Persistent = false; log($"{FilePath}: read-only - changes are kept for this session only"); }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Persistent = false; data = null;
                log($"{FilePath}: cannot read ({e.Message}) - session-only pak, the file is left alone");
            }
        }
        if (data == null || data.Length == 0)
        {
            file = Mempak.NewProject64Image();
            what = "new formatted image";
            MarkDirty();
        }
        else
        {
            if (data.Length < Mempak.Size)
            {
                var padded = new byte[Mempak.Size]; data.CopyTo(padded, 0); data = padded; needBackup = true;
                log($"{FilePath}: short file, padded to 32768 bytes (the original is kept as .orig before the first write)");
            }
            file = data;
            what = data.Length == Mempak.Size ? "32 KiB image" : $"32 KiB image + {data.Length - Mempak.Size} trailing bytes kept";
            if (Mempak.IsBlank(file)) { Mempak.FormatProject64(file); MarkDirty(); what = "blank image, formatted"; }
        }
        bool valid = Mempak.Check(file, out var why);
        if (!valid && data != null) needBackup = true;          // damaged: keep the original once before the game's repair rewrites it
        log($"port {Port + 1}: {FilePath ?? "(memory only)"} [{what}{(Persistent ? "" : ", NOT SAVED")}] {(valid ? "valid" : "DAMAGED")}: {why}");
    }

    /// <summary>Writes the image if it changed. <paramref name="delayMs"/>: only when nothing changed for that long (or the first unsaved change is
    /// older than 8 x delay + 1 s); -1 = now. Returns true when the pak is clean afterwards.</summary>
    public bool Flush(int delayMs = -1)
    {
        byte[] copy; long g; bool backup; string path;
        lock (gate)
        {
            if (!dirty || !Persistent || FilePath == null) return true;
            long now = Environment.TickCount64;
            if (delayMs >= 0)
            {
                if (now < retryAt) return false;
                if (now - tLast < delayMs && now - tFirst < delayMs * 8L + 1000) return false;
            }
            copy = (byte[])file.Clone(); g = gen; path = FilePath;
            backup = needBackup && !backedUp && File.Exists(path);
        }
        bool ok = true; string? err = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (backup)
            {
                var orig = path + ".orig";
                if (!File.Exists(orig)) { File.Copy(path, orig); log($"{path}: original kept as .orig"); }
            }
            WriteAtomic(path, copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ok = false; err = e.Message; }
        lock (gate)
        {
            if (ok)
            {
                fails = 0; retryAt = 0; if (backup) backedUp = true;
                if (gen == g) dirty = false;
                if (trace) log($"wrote {path}");
            }
            else
            {
                fails++;
                if (fails == 1 || fails % 20 == 0) log($"{path}: write failed ({err}), will retry");
                retryAt = Environment.TickCount64 + Math.Min(500L << Math.Min(fails, 5), 16000);
            }
            return !dirty;
        }
    }

    /// <summary>Temp file in the same folder, flushed to the disk, then renamed over the target: a crash leaves the old or the new file, never half of one.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        string tmp = $"{path}.tmp{Environment.ProcessId}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }
}
