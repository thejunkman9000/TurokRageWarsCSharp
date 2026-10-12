namespace Trw.Platform;

/// <summary>
/// The PIF's Joybus engine with a controller (and its accessory slot) on each of the four ports: port of the C port's port/pak.c.
/// libultra builds Joybus command blocks in 64-byte PIF RAM images (__osPfsPifRam, __osMotorStartData ...) and transfers them with __osSiRawStartDma:
///   - RDRAM -> PIF (OS_WRITE, dir 1) copies the 64 bytes in and, when the control byte (offset 63) has bit 0 set, parses the command list;
///   - PIF -> RDRAM (OS_READ, dir 0) executes the parsed list (this is when the controllers are talked to) and copies the 64 bytes out.
/// Command list (n64brew "PIF-NUS"): 0x00 = skip a channel, 0xFF = padding, 0xFE = end, 0xFD = channel reset; otherwise a handshake
/// TX RX tx[TX] rx[RX] (TX bit 7 = skip, bit 6 = reset; the PIF writes error flags into the top bits of RX: 0x80 = no device, 0x40 = timeout).
/// Channels 0-3 are the controller ports; channel 4 (cartridge EEPROM) is not populated.
/// Controller commands: 0x00 / 0xFF identify (05 00 + status), 0x01 buttons (from <see cref="Buttons"/>, zeros without it), 0x02 read 32 bytes of
/// accessory memory, 0x03 write 32 bytes; address word = 11-bit block number &lt;&lt; 5 | 5-bit CRC, reply = data CRC-8 (<see cref="PakCrc"/>).
/// </summary>
public sealed class Pif
{
    public readonly ControllerPak[] Ports;
    /// <summary>Which controllers answer on the Joybus (a port without a controller answers "no device", whatever its slot holds).</summary>
    public Func<int, bool> Connected = _ => true;
    /// <summary>Optional: the 4 bytes of a button-read reply (buttons hi, lo, stick x, stick y).</summary>
    public Func<int, uint>? Buttons;
    public long Transfers;

    private readonly object gate = new();
    private readonly byte[] ram = new byte[64];
    private readonly (int chan, int pos, int tx, int rx, int flags)[] hs = new (int, int, int, int, int)[8];
    private int nhs;

    public Pif(ControllerPak[] ports) { Ports = ports; }

    /// <summary>One SI DMA of a 64-byte PIF RAM image. dir 1 = RDRAM -> PIF (OS_WRITE), 0 = PIF -> RDRAM (OS_READ; <paramref name="buf"/> receives the result).</summary>
    public void Dma(int dir, Span<byte> buf)
    {
        lock (gate)
        {
            Transfers++;
            if (dir == 1)
            {
                buf[..64].CopyTo(ram);
                if ((ram[63] & 1) != 0) { Parse(); ram[63] &= 0xFE; }       // the PIF consumes the "run the command list" bit
            }
            else
            {
                Execute();
                ram.CopyTo(buf);
            }
        }
    }

    /// <summary>OSContStatus.status for a controller query (osContInit / osContGetQuery): the same bits and latches as the identify command.</summary>
    public byte ContStatus(int port, bool connected) => !connected || port < 0 || port > 3 ? (byte)0 : Ports[port].IdentifyStatus();

    private void Parse()
    {
        int chan = 0, pos = 0;
        nhs = 0;
        while (pos < 63 && chan < 5)
        {
            byte b = ram[pos];
            if (b == 0xFE) break;                                    // end of list
            if (b == 0xFF) { pos++; continue; }                      // padding
            if (b == 0x00 || b == 0xFD) { chan++; pos++; continue; } // skip (or reset) this channel
            int tx = b & 0x3F;
            if (pos + 1 >= 63) break;
            int rx = ram[pos + 1] & 0x3F, nx = pos + 2 + tx + rx;
            if (nx > 63) break;                                      // incomplete handshake at the end: ignored
            hs[nhs++] = (chan, pos, tx, rx, b & 0xC0);
            pos = nx; chan++;
        }
    }

    private void Execute()
    {
        for (int i = nhs - 1; i >= 0; i--)                          // the real PIF talks to channel 4 first and channel 0 last
        {
            var h = hs[i];
            ram[h.pos + 1] &= 0x3F;
            if ((h.flags & 0xC0) != 0) continue;                     // skip / reset: no reply
            ram[h.pos + 1] |= Command(h.chan, ram.AsSpan(h.pos + 2, h.tx), ram.AsSpan(h.pos + 2 + h.tx, h.rx));
        }
    }

    private static byte Reply(Span<byte> rx, ReadOnlySpan<byte> resp)
    {
        int k = Math.Min(rx.Length, resp.Length);
        resp[..k].CopyTo(rx);
        return rx.Length > resp.Length ? (byte)0x40 : (byte)0;      // asking for more than the device sends = receive timeout
    }

    /// <summary>One controller command; returns the error flags for the RX byte.</summary>
    private byte Command(int chan, ReadOnlySpan<byte> tx, Span<byte> rx)
    {
        if (chan >= 4 || !Connected(chan)) return 0x80;
        if (tx.Length < 1) return 0x40;
        var p = Ports[chan];
        Span<byte> r = stackalloc byte[33];
        switch (tx[0])
        {
            case 0x00:
            case 0xFF:                                              // identify / reset
                if (tx.Length != 1) return 0x40;
                r[0] = 0x05; r[1] = 0x00; r[2] = p.IdentifyStatus();
                return Reply(rx, r[..3]);
            case 0x01:                                              // read buttons
            {
                if (tx.Length != 1) return 0x40;
                uint v = Buttons?.Invoke(chan) ?? 0;
                r[0] = (byte)(v >> 24); r[1] = (byte)(v >> 16); r[2] = (byte)(v >> 8); r[3] = (byte)v;
                return Reply(rx, r[..4]);
            }
            case 0x02:                                              // read accessory
                if (tx.Length != 3) return 0x40;
                r[32] = p.Read((ushort)(tx[1] << 8 | tx[2]), r);
                return Reply(rx, r);
            case 0x03:                                              // write accessory
                if (tx.Length != 35) return 0x40;
                r[0] = p.Write((ushort)(tx[1] << 8 | tx[2]), tx.Slice(3, 32));
                return Reply(rx, r[..1]);
            default:
                return 0x40;                                        // unknown command: the controller does not answer
        }
    }
}

/// <summary>
/// Configuration of the virtual Controller Paks (environment variables, the C# port's convention; the C port's TUROK_* names and turok_pak.ini
/// are not read):
///   TRW_PAK=0|1                master switch (default 1; 0 = nothing in any slot, the old behaviour; oracle runs (TRW_ORACLE_OUT) default to 0 like the C port's)
///   TRW_PAK1_TYPE..TRW_PAK4_TYPE=none|memory|rumble   accessory per port (default: memory pak in port 1, the other slots empty)
///   TRW_SAVEDIR=&lt;dir&gt;         folder of the pak images (default &lt;exe folder&gt;\saves, or %LOCALAPPDATA%\TurokRageWars\saves when that is not writable)
///   TRW_PJ64_SAVE=&lt;dir&gt;       a Project64 "Save" folder: port n uses "&lt;name&gt;_Cont_&lt;n&gt;.mpk" there or in its per-game folder "&lt;name&gt;-&lt;md5&gt;"
///                              (Project64's "Unique Game Dir", default on); without either file it is created in the per-game folder when one exists, else in &lt;dir&gt;
///   TRW_PAK1..TRW_PAK4=&lt;file&gt;  explicit image file for a port (wins over both folders)
///   TRW_PAK_DELAY=&lt;ms&gt;         write-back debounce (default 300; a pak written continuously still reaches the disk after 8 x delay + 1 s)
///   TRW_PAKLOG=1               log every accessory transaction
/// &lt;name&gt; is Project64's game name: the ROM's internal title with ':' -> ';' ("Turok; Rage Wars_Cont_1.mpk").
/// </summary>
public sealed class PakConfig
{
    public bool Enabled = true;
    public PakAccessory[] Types = { PakAccessory.Memory, PakAccessory.None, PakAccessory.None, PakAccessory.None };
    public string SaveDir = "";
    public string? Pj64Save;
    public string?[] Files = new string?[4];
    public int DelayMs = 300;
    public bool Trace;
    public string GameName = "Turok; Rage Wars";

    public static PakConfig FromEnvironment(Func<string, string?> env, string exeDir, byte[] rom, bool oracle)
    {
        var c = new PakConfig { GameName = RomGameName(rom) };
        static string? V(string? s) { s = s?.Trim(); if (s != null && s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1]; return string.IsNullOrEmpty(s) ? null : s; }
        c.Enabled = !oracle;
        if (V(env("TRW_PAK")) is string en) c.Enabled = ParseBool(en, c.Enabled);
        for (int i = 0; i < 4; i++)
        {
            if (V(env($"TRW_PAK{i + 1}_TYPE")) is string t) c.Types[i] = ParseType(t, c.Types[i]);
            if (!c.Enabled) c.Types[i] = PakAccessory.None;
            if (V(env($"TRW_PAK{i + 1}")) is string f) c.Files[i] = Path.GetFullPath(f);
        }
        if (int.TryParse(V(env("TRW_PAK_DELAY")), out var d)) c.DelayMs = Math.Clamp(d, 0, 60000);
        c.Trace = ParseBool(V(env("TRW_PAKLOG")) ?? "0", false);
        if (V(env("TRW_PJ64_SAVE")) is string pj) c.Pj64Save = Path.GetFullPath(pj);
        if (V(env("TRW_SAVEDIR")) is string sd) c.SaveDir = Path.GetFullPath(sd);
        else
        {
            c.SaveDir = Path.Combine(exeDir, "saves");
            if (c.Types.Contains(PakAccessory.Memory) && c.Pj64Save == null && !DirWritable(c.SaveDir))     // (the probe creates the folder: only when a pak will need it)
                c.SaveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurokRageWars", "saves");
        }
        return c;
    }

    public static bool ParseBool(string s, bool def) => s.Trim().ToLowerInvariant() switch
    {
        "" => def,
        "0" or "off" or "no" or "false" => false,
        _ => true,
    };

    public static PakAccessory ParseType(string s, PakAccessory def) => s.Trim().ToLowerInvariant() switch
    {
        "none" or "off" or "0" or "empty" => PakAccessory.None,
        "memory" or "mem" or "mempak" or "controller" or "1" => PakAccessory.Memory,
        "rumble" or "2" => PakAccessory.Rumble,
        _ => def,
    };

    /// <summary>Project64's game name (CN64Rom::CleanRomName): the 20-byte internal title at 0x20, trailing blanks removed, '/' '\' -> '-', ':' -> ';'.</summary>
    public static string RomGameName(byte[] rom)
    {
        if (rom.Length < 0x34) return "Turok; Rage Wars";
        var t = new System.Text.StringBuilder();
        for (int i = 0; i < 20 && rom[0x20 + i] != 0; i++) t.Append((char)rom[0x20 + i]);
        var s = t.ToString().TrimEnd(' ');
        s = s.Replace('/', '-').Replace('\\', '-').Replace(':', ';');
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Length == 0 ? "Turok; Rage Wars" : s;
    }

    /// <summary>Image file of port <paramref name="i"/> (0..3): explicit file &gt; Project64 Save folder &gt; &lt;savedir&gt;\&lt;name&gt;_Cont_&lt;n&gt;.mpk.</summary>
    public string PathFor(int i)
    {
        if (Files[i] != null) return Files[i]!;
        string leaf = $"{GameName}_Cont_{i + 1}.mpk";
        if (Pj64Save != null && Directory.Exists(Pj64Save))
        {
            // Project64 2.x / 3.x: Save\<name>_Cont_<n>.mpk, or Save\<name>-<md5>\<name>_Cont_<n>.mpk with "Unique Game Dir" (the default)
            var games = Directory.EnumerateDirectories(Pj64Save, GameName + "-*").OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
            var found = games.Select(g => Path.Combine(g, leaf)).Append(Path.Combine(Pj64Save, leaf)).Where(File.Exists)
                             .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found != null) return found;
            return Path.Combine(games.Count > 0 ? games[0] : Pj64Save, leaf);
        }
        return Path.Combine(SaveDir, leaf);
    }

    private static bool DirWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $"~probe{Environment.ProcessId}.tmp");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// The four virtual accessory slots behind the serial interface and their files: set up on first use from <see cref="PakConfig"/>, written back by a
/// background timer <see cref="PakConfig.DelayMs"/> after the last change, and on process exit (and before an unhandled exception takes the process down).
/// </summary>
public static class ControllerPaks
{
    private static readonly object initGate = new();
    private static volatile Pif? pif;
    private static Timer? timer;
    public static PakConfig? Config { get; private set; }

    private static void Log(string s) => Console.Error.WriteLine($"[pak] {s}");

    /// <summary>The PIF (created from the environment on first use). <paramref name="connected"/> says which controllers answer.</summary>
    public static Pif Get(Func<int, bool> connected)
    {
        if (pif != null) return pif;
        lock (initGate)
        {
            if (pif != null) return pif;
            var cfg = PakConfig.FromEnvironment(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, Host.Rom, Oracle.Enabled);
            pif = Create(cfg, connected);
        }
        return pif;
    }

    /// <summary>Builds the slots of <paramref name="cfg"/> and starts the write-back (also used by tests with a private configuration).</summary>
    public static Pif Create(PakConfig cfg, Func<int, bool> connected)
    {
        Config = cfg;
        var ports = new ControllerPak[4];
        for (int i = 0; i < 4; i++)
            ports[i] = new ControllerPak(i, cfg.Types[i], cfg.Types[i] == PakAccessory.Memory ? cfg.PathFor(i) : null, Log, cfg.Trace);
        var p = new Pif(ports) { Connected = connected };
        string slots = string.Concat(cfg.Types.Select(t => t switch { PakAccessory.Memory => 'm', PakAccessory.Rumble => 'r', _ => '-' }));
        Log($"virtual controller pak{(cfg.Enabled ? "" : " DISABLED (TRW_PAK=0, or an oracle run)")}: ports {slots} (m = memory pak, r = rumble pak, - = empty), save folder {cfg.SaveDir}" +
            (cfg.Pj64Save != null ? $", Project64 save folder {cfg.Pj64Save}" : "") + $", name '{cfg.GameName}'");
        if (cfg.Types.Contains(PakAccessory.Memory))
        {
            timer = new Timer(_ => FlushAll(cfg.DelayMs), null, 100, 100);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll(-1);
            AppDomain.CurrentDomain.UnhandledException += (_, _) => FlushAll(-1);
        }
        pif = p;
        return p;
    }

    private static int flushing;

    /// <summary>Writes every changed pak (<paramref name="delayMs"/> &lt; 0: now, otherwise only those that are due).</summary>
    public static void FlushAll(int delayMs = -1)
    {
        var p = pif;
        if (p == null) return;
        if (delayMs >= 0 && Interlocked.Exchange(ref flushing, 1) != 0) return;       // a slow disk: the timer does not pile up
        try { foreach (var port in p.Ports) port.Flush(delayMs); }
        catch (Exception e) { Log($"write-back failed: {e.Message}"); }
        finally { if (delayMs >= 0) Volatile.Write(ref flushing, 0); }
    }
}
