using System.Security.Cryptography;

namespace Trw.Platform.Tests;

/// <summary>Virtual Controller Pak: CRCs, the Joybus engine, Project64's blank image, file round trips and the configuration.</summary>
public class ControllerPakTests
{
    // ------------------------------------------------------------------------------------------ CRCs

    /// <summary>libultra's __osContAddressCrc, transcribed literally (contaddresscrc.c), as an independent reference.</summary>
    private static byte LibultraAddressCrc(ushort addr)
    {
        byte temp = 0;
        for (int i = 0; i < 16; i++)
        {
            byte temp2 = (byte)((temp & 0x10) != 0 ? 0x15 : 0);
            temp <<= 1;
            temp |= (byte)((addr & 0x400) != 0 ? 1 : 0);
            addr <<= 1;
            temp ^= temp2;
        }
        return (byte)(temp & 0x1F);
    }

    /// <summary>Project64's CMempak::CalculateCrc, transcribed literally (Mempak.cpp), as an independent reference.</summary>
    private static byte Pj64DataCrc(byte[] data)
    {
        byte crc = 0;
        for (int count = 0; count < 0x21; count++)
            for (int length = 0x80; length >= 1; length >>= 1)
            {
                byte xorTap = (byte)((crc & 0x80) != 0 ? 0x85 : 0x00);
                crc <<= 1;
                if (count != 0x20 && (data[count] & length) != 0) crc |= 1;
                crc ^= xorTap;
            }
        return crc;
    }

    [Fact]
    public void AddressCrc_KnownVectors()
    {
        Assert.Equal(0x15, PakCrc.Address(1));
        Assert.Equal(0x0D, PakCrc.Address(0x7FF));
        Assert.Equal(0x1B, PakCrc.Address(0x600));
        Assert.Equal(0x8001, PakCrc.AddressWord(0x400));      // the rumble / bank probe address libultra sends
        Assert.Equal(0xC01B, PakCrc.AddressWord(0x600));      // the rumble motor address
        for (int b = 0; b < 0x800; b++) Assert.Equal(LibultraAddressCrc((ushort)b), PakCrc.Address(b));
    }

    [Fact]
    public void DataCrc_KnownVectors()
    {
        static byte[] Fill(Func<int, int> f) => Enumerable.Range(0, 32).Select(i => (byte)f(i)).ToArray();
        Assert.Equal(0x00, PakCrc.Data(Fill(_ => 0)));
        Assert.Equal(0xEB, PakCrc.Data(Fill(_ => 1)));          // the CRC libultra expects for the rumble motor-on block
        Assert.Equal(0xB8, PakCrc.Data(Fill(_ => 0x80)));
        Assert.Equal(0x0A, PakCrc.Data(Fill(_ => 0xFF)));
        Assert.Equal(0x33, PakCrc.Data(Fill(i => i)));
        Assert.Equal(0x8E, PakCrc.Data(Fill(i => i * 7 + 3)));
        var rng = new Random(5);
        for (int k = 0; k < 500; k++) { var d = new byte[32]; rng.NextBytes(d); Assert.Equal(Pj64DataCrc(d), PakCrc.Data(d)); }
    }

    // ------------------------------------------------------------------------------------------ Project64's blank image

    [Fact]
    public void BlankImage_IsByteIdenticalToProject64()
    {
        var img = Mempak.NewProject64Image();
        // SHA-256 of Project64's CMempak::Format result (its 0x300-byte Initialize table followed by zeros up to 0x8000), computed from
        // Source/Project64-core/N64System/Mips/Mempak.cpp of github.com/project64/project64 (branch develop, 2026-10-11)
        Assert.Equal("e9cb0e3b767c31c04450106ae5edb26d081792cd3cee605c05244ded70ef5109", Convert.ToHexString(SHA256.HashData(img)).ToLowerInvariant());
        Assert.Equal("810102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", Convert.ToHexString(img, 0, 32).ToLowerInvariant());
        foreach (int o in Mempak.IdBlockOffsets)
            Assert.Equal("ffffffff051a5f130000000000000000ffffffffffffffffffff01ff662599cd", Convert.ToHexString(img, o, 32).ToLowerInvariant());
        Assert.Equal(0x71, img[0x101]);
        Assert.Equal(0x71, img[0x201]);
        Assert.True(Mempak.Check(img, out var why), why);
        Assert.False(Mempak.IsBlank(img));
        Assert.Empty(Mempak.Notes(img));
        Assert.True(Mempak.IsBlank(new byte[Mempak.Size]));
    }

    // ------------------------------------------------------------------------------------------ Joybus

    /// <summary>One controller-accessory command through the PIF, built the way libultra's __osContRamRead/Write pack it.</summary>
    private static (byte err, byte[] data, byte crc) Transfer(Pif pif, int ch, bool write, ushort addressWord, byte[]? data = null)
    {
        var ram = new byte[64];
        int pos = ch;                                            // one 0x00 per skipped channel
        ram[pos++] = 0xFF;                                       // padding byte libultra puts in front
        ram[pos++] = (byte)(write ? 35 : 3); ram[pos++] = (byte)(write ? 1 : 33);
        ram[pos++] = (byte)(write ? 3 : 2); ram[pos++] = (byte)(addressWord >> 8); ram[pos++] = (byte)addressWord;
        int dataPos = pos;
        if (write) { data!.CopyTo(ram, pos); pos += 32; } else { for (int i = 0; i < 33; i++) ram[pos + i] = 0xFF; pos += 33; }
        if (write) ram[pos++] = 0xFF;
        ram[pos] = 0xFE;
        ram[63] = 1;
        pif.Dma(1, ram);
        var back = new byte[64];
        pif.Dma(0, back);
        byte err = (byte)(back[ch + 2] & 0xC0);
        return write ? (err, Array.Empty<byte>(), back[dataPos + 32]) : (err, back[dataPos..(dataPos + 32)], back[dataPos + 32]);
    }

    private static byte Identify(Pif pif, int ch)
    {
        var ram = new byte[64];
        for (int i = 0; i < ch; i++) ram[i] = 0;
        ram[ch] = 1; ram[ch + 1] = 3; ram[ch + 2] = 0; ram[ch + 3] = ram[ch + 4] = ram[ch + 5] = 0xFF; ram[ch + 6] = 0xFE; ram[63] = 1;
        pif.Dma(1, ram);
        var back = new byte[64];
        pif.Dma(0, back);
        if ((back[ch + 1] & 0x80) != 0) return 0xFF;
        Assert.Equal(0x05, back[ch + 3]);
        return back[ch + 5];
    }

    private static Pif MemoryOnly(params PakAccessory[] types)
    {
        var ports = Enumerable.Range(0, 4).Select(i => new ControllerPak(i, i < types.Length ? types[i] : PakAccessory.None, null)).ToArray();
        return new Pif(ports);
    }

    [Fact]
    public void Joybus_ReadWriteIdentify()
    {
        var pif = MemoryOnly(PakAccessory.Memory);
        var d = Enumerable.Range(0, 32).Select(i => (byte)(i * 3 + 1)).ToArray();
        // not yet identified: the new pak does not answer (libultra then sees a data CRC mismatch and asks for the status: PFS_ERR_NEW_PACK)
        var (_, _, crc0) = Transfer(pif, 0, true, PakCrc.AddressWord(0x20), d);
        Assert.Equal((byte)~PakCrc.Data(d), crc0);
        Assert.Equal(3, Identify(pif, 0));                       // present + new
        Assert.Equal(1, Identify(pif, 0));                       // present
        var (err, _, crc) = Transfer(pif, 0, true, PakCrc.AddressWord(0x20), d);
        Assert.Equal(0, err);
        Assert.Equal(PakCrc.Data(d), crc);
        var (err2, r, crc2) = Transfer(pif, 0, false, PakCrc.AddressWord(0x20));
        Assert.Equal(0, err2);
        Assert.Equal(d, r);
        Assert.Equal(PakCrc.Data(r), crc2);
        Assert.Equal(d, pif.Ports[0].Image.Slice(0x20 * 32, 32).ToArray());
        // 0x8000+ on a memory pak: reads zeros, writes are acknowledged and dropped (one bank; the rumble probe fails)
        Transfer(pif, 0, true, PakCrc.AddressWord(0x400), Enumerable.Repeat((byte)0x80, 32).ToArray());
        var (_, z, zc) = Transfer(pif, 0, false, PakCrc.AddressWord(0x400));
        Assert.All(z, b => Assert.Equal(0, b));
        Assert.Equal(0, zc);
        // address CRC error: no answer, error bit in the next identify
        var (_, bad, badCrc) = Transfer(pif, 0, false, (ushort)(PakCrc.AddressWord(0x20) ^ 1));
        Assert.Equal(0xFF, badCrc);
        Assert.All(bad, b => Assert.Equal(0, b));
        Assert.Equal(5, Identify(pif, 0));
        Assert.Equal(1, Identify(pif, 0));
    }

    [Fact]
    public void Joybus_EmptySlotRumbleAndDisconnected()
    {
        var pif = MemoryOnly(PakAccessory.None, PakAccessory.Rumble);
        Assert.Equal(2, Identify(pif, 0));                       // controller, no accessory
        var (_, _, crc) = Transfer(pif, 0, true, PakCrc.AddressWord(0x10), new byte[32]);
        Assert.Equal((byte)~PakCrc.Data(new byte[32]), crc);
        Assert.Equal(3, Identify(pif, 1));
        // libultra's osMotorInit probe: 0xFE is not echoed, 0x80 unlocks, then the motor register
        Transfer(pif, 1, true, PakCrc.AddressWord(0x400), Enumerable.Repeat((byte)0xFE, 32).ToArray());
        Assert.Equal(0, Transfer(pif, 1, false, PakCrc.AddressWord(0x400)).data[31]);
        Transfer(pif, 1, true, PakCrc.AddressWord(0x400), Enumerable.Repeat((byte)0x80, 32).ToArray());
        Assert.Equal(0x80, Transfer(pif, 1, false, PakCrc.AddressWord(0x400)).data[31]);
        var on = Enumerable.Repeat((byte)1, 32).ToArray();
        Assert.Equal(0xEB, Transfer(pif, 1, true, PakCrc.AddressWord(0x600), on).crc);
        Assert.True(pif.Ports[1].MotorOn);
        pif.Connected = ch => ch != 2;
        Assert.Equal(0xFF, Identify(pif, 2));                    // no controller: "no device" flag
        Assert.Equal(0, pif.ContStatus(2, false));
    }

    // ------------------------------------------------------------------------------------------ files

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "trw_pak_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void File_NewPakRoundTrip()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "sub", "Turok; Rage Wars_Cont_1.mpk");
            var pak = new ControllerPak(0, PakAccessory.Memory, path);
            var pif = new Pif(new[] { pak, new ControllerPak(1, PakAccessory.None, null), new ControllerPak(2, PakAccessory.None, null), new ControllerPak(3, PakAccessory.None, null) });
            Identify(pif, 0);
            Assert.False(pak.Loaded);                            // the file is opened on the first block access
            Transfer(pif, 0, false, PakCrc.AddressWord(1));
            Assert.True(pak.Dirty);
            Assert.True(pak.Flush());                            // the new formatted image is created at once
            Assert.Equal(Mempak.NewProject64Image(), File.ReadAllBytes(path));
            var d = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
            Transfer(pif, 0, true, PakCrc.AddressWord(0x123), d);
            Assert.True(pak.Dirty);
            Assert.False(pak.Flush(60000));                      // debounced: not due yet
            Assert.True(pak.Flush());
            Assert.False(pak.Dirty);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(Mempak.Size, bytes.Length);
            Assert.Equal(d, bytes.AsSpan(0x123 * 32, 32).ToArray());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp*"));
            // a second session sees the data
            var again = new ControllerPak(0, PakAccessory.Memory, path);
            Assert.Equal(d, again.Image.Slice(0x123 * 32, 32).ToArray());
            Assert.False(again.Dirty);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void File_BlankIsFormatted_ShortIsBackedUp_TailKept()
    {
        var dir = TempDir();
        try
        {
            var blank = Path.Combine(dir, "blank.mpk");
            File.WriteAllBytes(blank, new byte[Mempak.Size]);
            var p1 = new ControllerPak(0, PakAccessory.Memory, blank);
            Assert.True(Mempak.Check(p1.Image, out _));
            p1.Flush();
            Assert.Equal(Mempak.NewProject64Image(), File.ReadAllBytes(blank));

            var shortFile = Path.Combine(dir, "short.mpk");
            File.WriteAllBytes(shortFile, new byte[] { 1, 2, 3 });
            var p2 = new ControllerPak(0, PakAccessory.Memory, shortFile);
            Assert.Equal(Mempak.Size, p2.Image.Length);
            p2.Image[100] = 7;                                    // (a direct edit is not tracked; write through the protocol)
            var pif = new Pif(new[] { p2, new ControllerPak(1, PakAccessory.None, null), new ControllerPak(2, PakAccessory.None, null), new ControllerPak(3, PakAccessory.None, null) });
            Identify(pif, 0);
            Transfer(pif, 0, true, PakCrc.AddressWord(0x200), Enumerable.Repeat((byte)9, 32).ToArray());
            Assert.True(p2.Flush());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(shortFile + ".orig"));
            Assert.Equal(Mempak.Size, new FileInfo(shortFile).Length);

            var tail = Path.Combine(dir, "tail.mpk");
            var withTail = Mempak.NewProject64Image().Concat(new byte[] { 0xAA, 0xBB }).ToArray();
            File.WriteAllBytes(tail, withTail);
            var p3 = new ControllerPak(0, PakAccessory.Memory, tail);
            var pif3 = new Pif(new[] { p3, new ControllerPak(1, PakAccessory.None, null), new ControllerPak(2, PakAccessory.None, null), new ControllerPak(3, PakAccessory.None, null) });
            Identify(pif3, 0);
            Transfer(pif3, 0, true, PakCrc.AddressWord(0x300), Enumerable.Repeat((byte)5, 32).ToArray());
            p3.Flush();
            var t = File.ReadAllBytes(tail);
            Assert.Equal(Mempak.Size + 2, t.Length);
            Assert.Equal(0xBB, t[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------------------------------ configuration

    private static byte[] Rom(string title)
    {
        var rom = new byte[0x1000];
        var t = System.Text.Encoding.ASCII.GetBytes(title.PadRight(20));
        t.CopyTo(rom, 0x20);
        return rom;
    }

    [Fact]
    public void Config_NamesAndDefaults()
    {
        Assert.Equal("Turok; Rage Wars", PakConfig.RomGameName(Rom("Turok: Rage Wars")));
        Assert.Equal("A-B-C", PakConfig.RomGameName(Rom("A/B\\C")));
        var env = new Dictionary<string, string?> { ["TRW_SAVEDIR"] = Path.GetTempPath() };
        var c = PakConfig.FromEnvironment(k => env.GetValueOrDefault(k), "C:\\x", Rom("Turok: Rage Wars"), oracle: false);
        Assert.Equal(new[] { PakAccessory.Memory, PakAccessory.None, PakAccessory.None, PakAccessory.None }, c.Types);
        Assert.Equal(300, c.DelayMs);
        var o = PakConfig.FromEnvironment(k => env.GetValueOrDefault(k), "C:\\x", Rom("Turok: Rage Wars"), oracle: true);
        Assert.All(o.Types, t => Assert.Equal(PakAccessory.None, t));
        env["TRW_PAK2_TYPE"] = "rumble"; env["TRW_PAK1_TYPE"] = "none"; env["TRW_SAVEDIR"] = "\"C:\\saves dir\"";
        var r = PakConfig.FromEnvironment(k => env.GetValueOrDefault(k), "C:\\x", Rom("Turok: Rage Wars"), oracle: false);
        Assert.Equal(new[] { PakAccessory.None, PakAccessory.Rumble, PakAccessory.None, PakAccessory.None }, r.Types);
        Assert.Equal(Path.Combine("C:\\saves dir", "Turok; Rage Wars_Cont_1.mpk"), r.PathFor(0));
        env["TRW_PAK"] = "0";
        Assert.All(PakConfig.FromEnvironment(k => env.GetValueOrDefault(k), "C:\\x", Rom("Turok: Rage Wars"), oracle: false).Types, t => Assert.Equal(PakAccessory.None, t));
    }

    [Fact]
    public void Config_Project64SaveFolder()
    {
        var dir = TempDir();
        try
        {
            var c = new PakConfig { Pj64Save = dir, SaveDir = Path.Combine(dir, "own") };
            // nothing there yet: created in the Save folder itself
            Assert.Equal(Path.Combine(dir, "Turok; Rage Wars_Cont_1.mpk"), c.PathFor(0));
            // a per-game folder ("Unique Game Dir"): new files go there, existing ones are found there
            var game = Directory.CreateDirectory(Path.Combine(dir, "Turok; Rage Wars-0123456789ABCDEF0123456789ABCDEF")).FullName;
            Assert.Equal(Path.Combine(game, "Turok; Rage Wars_Cont_2.mpk"), c.PathFor(1));
            File.WriteAllBytes(Path.Combine(dir, "Turok; Rage Wars_Cont_1.mpk"), Mempak.NewProject64Image());
            Assert.Equal(Path.Combine(dir, "Turok; Rage Wars_Cont_1.mpk"), c.PathFor(0));
            c.Files[0] = Path.Combine(dir, "explicit.mpk");
            Assert.Equal(c.Files[0], c.PathFor(0));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Notes_ParsesAPlayerNote()
    {
        var img = Mempak.NewProject64Image();
        // one note "RAGE WARS.A" of game NRWE / publisher 51 on pages 5..11
        var e = img.AsSpan(0x300, 32);
        "NRWE"u8.CopyTo(e); "51"u8.CopyTo(e[4..]); e[6] = 0; e[7] = 5;
        e[12] = 0x1A;                                                     // 'A'
        byte[] name = { 0x2B, 0x1A, 0x20, 0x1E, 0x0F, 0x30, 0x1A, 0x2B, 0x2C };   // RAGE WARS
        name.CopyTo(e[16..]);
        for (int p = 5; p <= 11; p++) { img[0x100 + p * 2] = 0; img[0x100 + p * 2 + 1] = (byte)(p == 11 ? 1 : p + 1); }
        var n = Assert.Single(Mempak.Notes(img));
        Assert.Equal("RAGE WARS", n.Name);
        Assert.Equal("A", n.Extension);
        Assert.Equal(7, n.Pages);
    }
}
