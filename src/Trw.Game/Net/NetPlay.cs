using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Trw.Platform;

namespace Trw.Game.Net;

/// <summary>
/// Online play (<c>--netplay</c>, docs/NETPLAY.md): the lobby before the game boots, then lockstep. Every machine runs the whole game in
/// deterministic mode (TRW_DETERMINISTIC=1 + TRW_TICK_OPS, docs/ORACLE.md); only pad input crosses the network. Before tick t a peer needs
/// the merged <see cref="NetFrame"/> of tick t; the game's controller reads during the tick (<see cref="ReadPad"/>) answer from it, so all four
/// pads read the same on every machine and the RAM stays bit-identical, which the digest exchange checks.
/// </summary>
public static class NetPlay
{
    public static bool Enabled { get; private set; }
    private static NetConfig cfg = new();
    private static NetFlow? flow;
    private static NetUi? ui;
    private static TrwWindow? window;
    private static NetRoom? room;
    private static volatile NetFrame current = NetFrame.Empty(0, 1);
    private static bool booted, inFlight, finished;
    private static int nextTick = 1, sampledUpTo;
    private static StreamWriter? digestLog;
    private static readonly bool scriptedInput = Environment.GetEnvironmentVariable("TRW_PAD_SCRIPT") is { Length: > 0 } || Environment.GetEnvironmentVariable("TRW_KBM_SCRIPT") is { Length: > 0 };
    private static long stallSince = -1;
    // TRW_NET_PIPELINE=0: the window waits for each tick before drawing (the old behaviour, for comparisons); default: the tick runs while the window draws
    private static readonly bool pipelined = Environment.GetEnvironmentVariable("TRW_NET_PIPELINE") != "0";
    // performance counters (TRW_NET_PERF=1 logs them every 5 s; the overlay shows ticks/s)
    private static readonly bool perfLog = Environment.GetEnvironmentVariable("TRW_NET_PERF") == "1";
    private static readonly Stopwatch perfClock = Stopwatch.StartNew();
    private static long perfStart, perfEndWaitTicks, perfEndWaitMax, perfStepTicks;
    private static int perfTicks, perfStalls, perfUpdates;
    public static double TicksPerSecond { get; private set; }

    /// <summary>Program.cs, before anything reads the environment: a netplay process always runs the deterministic scheduler and boots without
    /// Controller Paks (their contents differ from machine to machine; the session has no saves).</summary>
    public static void Configure(string[] args)
    {
        Enabled = args.Contains("--netplay");
        if (!Enabled) return;
        Environment.SetEnvironmentVariable("TRW_DETERMINISTIC", "1");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRW_TICK_OPS"))) Environment.SetEnvironmentVariable("TRW_TICK_OPS", "400");
        Environment.SetEnvironmentVariable("TRW_PAK", "0");
        Environment.SetEnvironmentVariable("TRW_ORACLE_OUT", null);
        cfg = NetConfig.Load(args);
    }

    /// <summary>Program.cs in netplay mode, after the ROM and guest memory are loaded (the game is not started yet: the lobby starts it).</summary>
    public static int Run(bool headless)
    {
        if (!Oracle.Deterministic) { Console.Error.WriteLine("netplay needs the deterministic scheduler, but TRW_DETERMINISTIC was read before --netplay set it"); return 3; }
        string romSha = Convert.ToHexString(SHA256.HashData(Host.Rom)).ToLowerInvariant();
        string build = typeof(NetPlay).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        Console.Error.WriteLine($"[net] ROM sha256 {romSha[..16]}..., build {build}, master {cfg.MasterUrl}{(MasterLink.Available ? "" : " (no jsNetwork in this build)")}, input delay {cfg.InputDelay}");
        flow = new NetFlow(cfg, romSha, build);
        if (headless) return RunHeadless();
        using (var w = new TrwWindow()) w.Run();
        Shutdown("quit");
        return 0;
    }

    // ------------------------------------------------------------------------------------------ boot and ticks

    private static void Boot()
    {
        room = flow!.Room!;
        int n = room.Members.Count;
        NetSession.LocalPad = room.LocalPad;
        NetSession.PlayerCount = n;
        for (int i = 0; i < 4; i++) { NetSession.PlayerNames[i] = room.Names[i]; NetSession.PingMs[i] = room.PingMs[i]; }
        NetSession.Active = true;
        Console.Error.WriteLine($"[net] booting: player {room.LocalPad + 1} of {n}, input delay {room.InputDelay}");
        if (cfg.DigestLog != null) digestLog = new StreamWriter(cfg.DigestLog) { AutoFlush = false, NewLine = "\n" };
        current = NetFrame.Empty(0, room.Occupied);       // osContInit at power-on, before the first retrace
        Host.ReadPad = ReadPad;
        GameBoot.Start();
        Game.WaitQuiescent();                              // the boot thread runs until it first waits for a retrace (as in the oracle runner)
        booted = true;
        perfStart = perfClock.ElapsedTicks;
    }

    /// <summary>The game's controller read (osContInit / osContGetQuery / osContGetReadData), from the frame of the running tick.</summary>
    private static bool ReadPad(int pad, out ushort buttons, out sbyte stickX, out sbyte stickY)
    {
        var f = current;
        buttons = 0; stickX = 0; stickY = 0;
        if ((uint)pad > 3 || (f.Connected & (1 << pad)) == 0) return false;
        var p = f.Pads[pad];
        buttons = p.Buttons; stickX = p.X; stickY = p.Y;
        return true;
    }

    /// <summary>This machine's player: keyboard + mouse through InputMapper and the first gamepad, as offline; scripted input (TRW_PAD_SCRIPT /
    /// TRW_KBM_SCRIPT) alone when set, so tests are not disturbed by whatever devices the machine has.</summary>
    private static PadInput SampleLocal(int tick)
    {
        TrwWindow.scriptTick = tick;
        ushort b; sbyte x, y;
        if (window == null || scriptedInput) TrwWindow.ReadPadHeadless(0, out b, out x, out y);
        else TrwWindow.ReadPad(0, out b, out x, out y);
        return new PadInput(b, x, y);
    }

    /// <summary>Runs ticks whose frame is known: at most <paramref name="max"/>, and a second one only when the next frame is already waiting (catching up).
    /// With <paramref name="pipelined"/> the last tick is left running (the window draws meanwhile) and finished at the next call.</summary>
    private static void Step(int max, bool pipelined)
    {
        var r = room!;
        for (int n = 0; n < max; n++)
        {
            if (inFlight) FinishTick();
            if (finished) return;
            if (!r.TryGetFrame(nextTick, out var f))
            {
                if (stallSince < 0) { stallSince = Environment.TickCount64; perfStalls++; }
                break;
            }
            stallSince = -1;
            if (sampledUpTo < nextTick + r.InputDelay)
            {
                sampledUpTo = nextTick + r.InputDelay;
                r.SubmitLocalInput(sampledUpTo, SampleLocal(nextTick));
            }
            current = f;
            Game.LockstepBegin();
            inFlight = true;
            nextTick++;
            if (!pipelined) FinishTick();
            if (!r.TryGetFrame(nextTick + 1, out _)) break;       // not behind: one tick per update
        }
    }

    private static void FinishTick()
    {
        long t0 = perfClock.ElapsedTicks;
        Game.LockstepEnd();
        long w = perfClock.ElapsedTicks - t0;
        perfEndWaitTicks += w; perfEndWaitMax = Math.Max(perfEndWaitMax, w); perfTicks++;
        inFlight = false;
        int t = nextTick - 1;
        NetSession.Tick = t;
        var r = room!;
        if (t % r.HashEvery == 0)
        {
            var d = NetDigest.Take(t / r.HashEvery);
            r.SubmitDigest(t, d);
            digestLog?.WriteLine($"{t} {string.Join(' ', d.Select(x => x.ToString("x16")))}");
        }
        if (dumpTicks.Contains(t))       // TRW_NET_DUMP=a,b: all 8 MB of RAM after those ticks, for offline comparisons (net_ram_<pad>_<tick>.bin)
        {
            var ram = new byte[0x800000];
            Mem.Read(0x80000000, ram);
            File.WriteAllBytes($"net_ram_{r.LocalPad}_{t}.bin", ram);
        }
        if (t == testDesyncTick)         // TRW_NET_TEST_DESYNC=<tick>: corrupt one byte of (unused) RAM on this peer, to see the desync check fire
        {
            Mem.Set(0x807FFFF0, (byte)(Mem.U8(0x807FFFF0) ^ 0x5A));
            Console.Error.WriteLine($"[net] test: corrupted RAM 807FFFF0 after tick {t}");
        }
        if (cfg.ExitAfterTicks > 0 && t >= cfg.ExitAfterTicks) finished = true;
    }

    private static readonly HashSet<int> dumpTicks = (Environment.GetEnvironmentVariable("TRW_NET_DUMP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => int.TryParse(x, out var v) ? v : -1).ToHashSet();
    private static readonly int testDesyncTick = int.TryParse(Environment.GetEnvironmentVariable("TRW_NET_TEST_DESYNC"), out var td) ? td : -1;

    /// <summary>Copies the room's state into <see cref="NetSession"/> and shows the room's notices.</summary>
    private static void Follow()
    {
        var r = room!;
        for (int i = 0; i < 4; i++) { NetSession.PingMs[i] = r.PingMs[i]; NetSession.Dropped[i] = (r.DroppedMask & (1 << i)) != 0; }
        while (r.TryNotice(out var s)) ui?.Toast(s);
        string status = "";
        if (r.DesyncText is { } ds) { status = ds; NetSession.Desynced = true; }
        else if (r.Ended is { } why && !r.IsHost) status = why;
        else if (stallSince >= 0 && Environment.TickCount64 - stallSince > 500) status = "Waiting for " + r.WaitingFor(nextTick) + "...";
        NetSession.Status = status;
        long now = perfClock.ElapsedTicks;
        double secs = (now - perfStart) / (double)Stopwatch.Frequency;
        if (secs >= (perfLog ? 5 : 1))
        {
            TicksPerSecond = perfTicks / secs;
            if (perfLog)
                Console.Error.WriteLine($"[net perf] tick {nextTick - 1}: {TicksPerSecond:F1} ticks/s, {perfUpdates / secs:F1} updates/s, wait for the game avg {perfEndWaitTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, perfTicks):F2} ms max {perfEndWaitMax * 1000.0 / Stopwatch.Frequency:F1} ms, step avg {perfStepTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, perfUpdates):F2} ms, stalls {perfStalls}");
            perfStart = now; perfTicks = perfStalls = perfUpdates = 0; perfEndWaitTicks = perfEndWaitMax = perfStepTicks = 0;
        }
    }

    private static void Shutdown(string reason)
    {
        if (flow == null) return;
        digestLog?.Flush(); digestLog?.Dispose(); digestLog = null;
        room?.Leave(reason);
        flow.Dispose();
        flow = null;
    }

    // ------------------------------------------------------------------------------------------ window

    /// <summary>TrwWindow.Update, first thing: true while the lobby owns the window (the game has not booted).</summary>
    public static bool LobbyUpdate(TrwWindow w, GameTime time)
    {
        if (!Enabled || booted || flow == null) return false;
        if (window == null)
        {
            window = w;
            ui = new NetUi(w);
            w.Exiting += (_, _) => Shutdown("quit");
        }
        flow.Update();
        ui!.Update(flow, time);
        if (flow.Screen == NetScreen.InGame) Boot();
        return true;
    }

    /// <summary>TrwWindow.Update, in place of the offline retrace: runs the next tick(s) of the session.</summary>
    public static void WindowTick()
    {
        if (!booted || room == null) return;
        long t0 = perfClock.ElapsedTicks;
        flow?.Update();
        Step(3, pipelined);
        perfStepTicks += perfClock.ElapsedTicks - t0; perfUpdates++;
        Follow();
        if (finished) { Shutdown("done"); window?.Exit(); }
    }

    /// <summary>TrwWindow.Draw, first thing: true when the lobby drew the frame.</summary>
    public static bool LobbyDraw(GraphicsDevice device)
    {
        if (!Enabled || booted || ui == null || flow == null) return false;
        ui.Draw(device, flow);
        return true;
    }

    /// <summary>TrwWindow.Draw, after the game frame: the session status line and notices.</summary>
    public static void OverlayDraw(GraphicsDevice device)
    {
        if (!Enabled || !booted || ui == null) return;
        ui.DrawOverlay(device);
    }

    // ------------------------------------------------------------------------------------------ headless

    /// <summary>--headless --netplay: the lobby by automation switches only, then the session paced at 60 ticks/s (--net-fast: as fast as the frames come).</summary>
    private static int RunHeadless()
    {
        var f = flow!;
        var interp = new Trw.Platform.Gfx.DisplayListInterpreter();       // display lists are interpreted as in the window (the screen-settled script steps look at them)
        Host.GraphicsTask = task => TrwWindow.RecordSignature(interp.Run(task));
        string lastStatus = "", lastSession = "";
        var clock = Stopwatch.StartNew();
        double? until = cfg.ExitAfterSeconds > 0 ? cfg.ExitAfterSeconds : null;
        long paceStart = 0; int pacedTicks = 0;
        while (true)
        {
            f.Update();
            if (f.Status != lastStatus) { lastStatus = f.Status; if (lastStatus.Length > 0) Console.Error.WriteLine("[net] " + lastStatus); }
            if (until is double u && clock.Elapsed.TotalSeconds > u && cfg.ExitAfterTicks == 0) { Shutdown("time"); return 0; }
            if (!booted)
            {
                if (f.Screen == NetScreen.InGame) { Boot(); paceStart = clock.ElapsedMilliseconds; }
                else
                {
                    if (f.Room == null && f.Screen == NetScreen.SignIn && f.Status.Length > 0 && !f.Busy && cfg.User == null && cfg.Direct == null && !cfg.Listen)
                    { Console.Error.WriteLine("[net] headless netplay needs --net-user/--net-pass (or --net-direct / --net-listen)"); return 2; }
                    Thread.Sleep(10);
                    continue;
                }
            }
            if (!cfg.Fast)
            {
                long due = paceStart + (long)(pacedTicks * 1000.0 / 60);
                long wait = due - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)Math.Min(wait, 20));
                if (clock.ElapsedMilliseconds < due) continue;
            }
            int before = nextTick;
            Step(1, pipelined: false);
            if (nextTick != before) pacedTicks++;
            else Thread.Sleep(1);
            Follow();
            if (NetSession.Status != lastSession) { lastSession = NetSession.Status; if (lastSession.Length > 0 && NetSession.Desynced == false) Console.Error.WriteLine("[net] " + lastSession); }
            if (finished || (room!.Ended != null && !room!.IsHost && stallSince >= 0 && Environment.TickCount64 - stallSince > 3000))
            {
                if (room!.IsHost)
                {
                    // let the joiners finish the last ticks and send their digests before the host goes
                    var end = Environment.TickCount64 + 2000;
                    while (Environment.TickCount64 < end) { Thread.Sleep(20); }
                }
                var r = room;
                Console.Error.WriteLine($"[net] end at tick {nextTick - 1}: player {r.LocalPad + 1}, {(r.IsHost ? $"{r.DigestsCompared} digests compared with the joiners, " : "")}" +
                    $"{(r.DesyncText != null ? r.DesyncText : "no desync")}{(r.Ended != null ? "; " + r.Ended : "")}");
                Shutdown(finished ? "done" : "host gone");
                return r.DesyncText != null ? 4 : finished ? 0 : 5;
            }
        }
    }
}
