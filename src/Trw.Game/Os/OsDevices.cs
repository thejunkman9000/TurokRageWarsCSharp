using Trw.Platform;

namespace Trw.Game;

/// <summary>The console's interfaces as the game's libultra calls see them (port of the second half of os_shim.c).</summary>
public static partial class Game
{
    // ---------------------------------------------------------------------------------------------- events / VI

    private static readonly uint[] evMq = new uint[16];
    private static readonly uint[] evMsg = new uint[16];

    public static void osSetEventMesg(uint e, uint mq, uint m) { TraceOs($"osSetEventMesg {e} mq={mq:X8}"); if (e < 16) { evMq[e] = mq; evMsg[e] = m; } }

    private static uint viMq, viMsg, framebuffer;
    private static int viCount = 1, viPhase;

    public static void osViSetEvent(uint mq, uint m, uint retraceCount) { TraceOs($"osViSetEvent mq={mq:X8} count={retraceCount}"); viMq = mq; viMsg = m; viCount = retraceCount != 0 ? (int)retraceCount : 1; }
    public static void osCreateViManager(int pri) { }
    public static void osViBlack(byte active) { }
    public static void osViSetMode(uint m) { }
    public static void osViSetSpecialFeatures(uint f) { }
    public static void osViSetYScale(float v) { }
    public static void osViSwapBuffer(uint fb) { framebuffer = fb; }
    public static uint osViGetCurrentFramebuffer() => framebuffer;

    // ---------------------------------------------------------------------------------------------- RSP / RDP tasks

    private static readonly Queue<bool> taskCompletions = new();       // true = graphics task
    private static uint pendingTask;

    /// <summary>The graphics task the renderer should draw next (guest address of the OSTask), consumed by <see cref="TakeGraphicsTask"/>.</summary>
    private static volatile uint graphicsTaskReady;
    public static uint TakeGraphicsTask() => Interlocked.Exchange(ref graphicsTaskReady, 0);

    // OSTask_t: type@0 flags@4 ... data_ptr@0x30 data_size@0x34
    public static void osSpTaskLoad(uint t) { pendingTask = t; }

    public static void osSpTaskStartGo(uint t)
    {
        GuardTest("osSpTaskStartGo");
        TraceOs($"osSpTaskStartGo type={Mem.U32(pendingTask)}");
        uint task = pendingTask;
        uint type = Mem.U32(task);
        bool gfx = type == 1;
        LogHost($"osSpTaskStartGo type={type} task={task:X8}");
        if (gfx) { gfxTasks++; graphicsTaskReady = task; Host.GraphicsTask?.Invoke(task); }
        if (type == 2) { audioTasks++; Host.AudioTask?.Invoke(Mem.U32(task + 0x30), Mem.U32(task + 0x34)); }
        // Audio tasks (the HLE already ran, the real RSP needs about 1 ms) complete as soon as the scheduler next waits on its event queue (osRecvMesg hook):
        // posting here, before the scheduler's post-dispatch bookkeeping, loses the completion. Delivering them from the frame tick, one task per tick shared with
        // the 30 graphics tasks/s, held audio production to about half of real time.
        if (type == 2) { audioDonePending = true; return; }
        // graphics completion is delivered from the frame tick: the caller still has bookkeeping to do
        lock (kernelLock) { if (taskCompletions.Count < 16) taskCompletions.Enqueue(gfx); }
    }

    private static volatile bool audioDonePending;

    private static bool QueueHasRoom(uint mq, int n) => Mem.S32(mq + MQ_VALID) + n <= Mem.S32(mq + MQ_COUNT);

    /// <summary>osRecvMesg hook: an audio task has finished and the receiver waits on the SP-done event queue, so deliver its SP-done message now.</summary>
    private static void DeliverAudioCompletion(uint mq)
    {
        if (!audioDonePending || mq != evMq[4] || mq == 0) return;
        if (!QueueHasRoom(mq, 1)) return;      // full of retraces the starved scheduler has not read yet: retry at its next osRecvMesg instead of dropping it
        audioDonePending = false;
        audioDone++;
        LogHost("completing audio task at osRecvMesg");
        PostFromHost(mq, evMsg[4]);
    }

    public static void osSpTaskYield() { }
    public static int osSpTaskYielded(uint t) => 0;
    public static int osDpSetNextBuffer(uint a, ulong b) => 0;

    // ---------------------------------------------------------------------------------------------- cartridge DMA

    // TRW_ROMLOG=<lo>-<hi> (hex ROM offsets): log the cartridge reads that touch that range (which level / asset gets loaded)
    private static readonly (uint lo, uint hi)? romLog = Environment.GetEnvironmentVariable("TRW_ROMLOG") is { } rs && rs.Split('-') is { Length: 2 } rp
        && uint.TryParse(rp[0], System.Globalization.NumberStyles.HexNumber, null, out var rlo) && uint.TryParse(rp[1], System.Globalization.NumberStyles.HexNumber, null, out var rhi) ? (rlo, rhi) : null;

    private static void RomCopy(uint dst, uint dev, uint n)
    {
        if (romLog is { } rl && (dev & 0x0FFFFFFF) < rl.hi && (dev & 0x0FFFFFFF) + n > rl.lo) Console.Error.WriteLine($"[rom] tick {CurrentTick}: read {dev & 0x0FFFFFFF:X6}+{n:X} -> {dst:X8}");
        var rom = Host.Rom;
        uint off = dev & 0x0FFFFFFF;        // the cartridge offset; the game passes it plain, as 0x10000000+off, or as the KSEG1 address 0xB0000000+off
        if (off >= rom.Length) return;
        int len = (int)Math.Min(n, (uint)rom.Length - off);
        Mem.Write(dst, rom.AsSpan((int)off, len));
    }

    // OSIoMesg: hdr.retQueue@4
    public static int osPiStartDma(uint mb, int pri, int dir, uint dev, uint vaddr, uint n, uint mq)
    {
        TraceOs($"osPiStartDma dir={dir} dev={dev:X8} dram={vaddr:X8} size={n:X}");
        if (dmaLog) Console.Error.WriteLine($"osPiRawStartDma dir={dir} dev={dev & 0x0FFFFFFF:X8} dram={vaddr:X8} size={n:X}");
        if (dir == 0) RomCopy(vaddr, dev, n);         // OS_READ
        Mem.Set(mb + 4, mq);
        PostFromHost(mq, mb);
        return 0;
    }

    private static readonly bool dmaLog = Environment.GetEnvironmentVariable("TRW_TRACE_DMA") != null;     // same line format as the C port's log

    public static int osPiRawStartDma(int dir, uint dev, uint vaddr, uint n)
    {
        if (dmaLog) Console.Error.WriteLine($"osPiRawStartDma dir={dir} dev={dev & 0x0FFFFFFF:X8} dram={vaddr:X8} size={n:X}");
        if (vaddr == 0) return -1;
        if (dir == 0) RomCopy(vaddr, dev, n);
        PostFromHost(evMq[8], evMsg[8]);              // PI interrupt: DMA finished
        return 0;
    }

    public static uint osPiGetStatus() => 0;

    // ---------------------------------------------------------------------------------------------- memory / cache / TLB / interrupts

    public static void osInvalDCache(uint a, int n) { }
    public static void osInvalICache(uint a, int n) { }
    public static void osWritebackDCache(uint a, int n) { }
    public static void osWritebackDCacheAll() { }
    public static void osMapTLB(int i, int ps, uint v, uint e0, uint e1, int a) { }
    public static void osUnmapTLB(int i) { }
    public static void osUnmapTLBAll() { }
    public static uint osVirtualToPhysical(uint v) => v >= 0x80000000u ? v & 0x1FFFFFFFu : v;
    public static uint osSetIntMask(uint m) => 0x3F01;
    public static uint __osDisableInt() => 1;
    public static void __osRestoreInt(uint v) { }
    public static void osInitialize() { }
    public static uint __osGetFpcCsr() => 0;
    public static uint __osSetFpcCsr(uint v) => 0;

    // ---------------------------------------------------------------------------------------------- serial interface (PIF)

    /// <summary>The 64-byte PIF RAM transfer runs through the virtual PIF / Controller Pak model (Trw.Platform Pif, ControllerPak) and completes at once:
    /// dir 1 (OS_WRITE) hands the command list to the PIF, dir 0 (OS_READ) runs it and copies the replies back. The game's own libultra pak code
    /// (osPfsInitPak, __osContRamRead/Write, osPfsAllocateFile, osMotorInit ...) runs unchanged on top of it. No EEPROM (channel 4 answers "no device").</summary>
    public static int __osSiRawStartDma(int dir, uint pifRam)
    {
        if (pifRam != 0 && Mem.IsMapped(pifRam))
        {
            Span<byte> buf = stackalloc byte[64];
            if (dir == 1) Mem.Read(pifRam, buf);
            Paks.Dma(dir, buf);
            if (dir == 0) Mem.Write(pifRam, buf);
        }
        PostFromHost(evMq[5], evMsg[5]);
        return 0;
    }

    /// <summary>The virtual PIF with the controller paks (created from TRW_PAK* on first use, see PakConfig).</summary>
    private static Pif Paks => ControllerPaks.Get(PadConnected);

    // ---------------------------------------------------------------------------------------------- controllers

    // OSContStatus: type u16@0 status u8@2 errno u8@3 (4 bytes); OSContPad: button u16@0 stick_x s8@2 stick_y s8@3 errno u8@4 (6 bytes)
    private static bool PadConnected(int i)
    {
        if (i == 0) return true;
        return Host.ReadPad != null && Host.ReadPad(i, out _, out _, out _);
    }

    public static int osContInit(uint mq, uint bitpattern, uint status)
    {
        byte bp = 0;
        for (int i = 0; i < 4; i++)
        {
            bool c = PadConnected(i);
            if (c) bp |= (byte)(1 << i);
            Mem.Set(status + (uint)(i * 4), (ushort)(c ? 0x0500 : 0));
            Mem.Set(status + (uint)(i * 4) + 2, Paks.ContStatus(i, c));       // accessory bits (CONT_CARD_ON / CONT_CARD_PULL / CONT_ADDR_CRC_ER), same latches as the PIF's identify
            Mem.Set(status + (uint)(i * 4) + 3, (byte)(c ? 0 : 0x08));       // CONT_NO_RESPONSE_ERROR
        }
        Mem.Set(bitpattern, bp);
        return 0;
    }

    public static int osContStartReadData(uint mq) { PostFromHost(mq, 0); return 0; }

    public static void osContGetReadData(uint pad)
    {
        Mem.Fill(pad, 4 * 6, 0);
        for (int i = 0; i < 4; i++)
        {
            if (!PadConnected(i)) continue;
            ushort b = 0; sbyte x = 0, y = 0;
            Host.ReadPad?.Invoke(i, out b, out x, out y);
            if (Host.ReadMouse != null) { Host.ReadMouse(i, out float yaw, out float pitch, out bool act); MouseLatch(i, yaw, pitch, act); }      // native mouse look (Os/MouseLook.cs)
            Mem.Set(pad + (uint)(i * 6), b);
            Mem.Set(pad + (uint)(i * 6) + 2, x);
            Mem.Set(pad + (uint)(i * 6) + 3, y);
        }
    }

    public static int osContStartQuery(uint mq) { PostFromHost(mq, 0); return 0; }

    public static void osContGetQuery(uint status)
    {
        Mem.Fill(status, 4 * 4, 0);
        for (int i = 0; i < 4; i++)
        {
            bool c = PadConnected(i);
            Mem.Set(status + (uint)(i * 4), (ushort)(c ? 0x0500 : 0));
            Mem.Set(status + (uint)(i * 4) + 2, Paks.ContStatus(i, c));
            Mem.Set(status + (uint)(i * 4) + 3, (byte)(c ? 0 : 0x08));       // CONT_NO_RESPONSE_ERROR
        }
    }

    public static int osContReset(uint mq, uint status) => 0;
    // osMotorInit / osMotorStart / osMotorStop are the game's own libultra code (Generated/osMotorInit.cs ...), talking to the virtual PIF:
    // they succeed only on a port whose slot holds a rumble pak (TRW_PAKn_TYPE=rumble); a memory pak answers PFS_ERR_DEVICE, an empty slot PFS_ERR_NOPACK.

    // ---------------------------------------------------------------------------------------------- audio interface

    private static readonly AiFifo aiFifo = new();

    public static int osAiSetFrequency(uint f) { aiFifo.SetFrequency(f); Host.AudioFrequency = f; Host.AudioSetFrequency?.Invoke(f); return (int)f; }

    public static int osAiSetNextBuffer(uint b, uint n)
    {
        lock (aiFifo) aiFifo.Queue(Host.Counter(), n);
        aiBlocks++; aiBytes += n;
        if (b != 0 && n != 0 && Host.AudioPush != null)
        {
            var buf = new byte[n];
            Mem.Read(b, buf);
            Host.AudioPush(buf, (int)n);
        }
        if (Oracle.Enabled && b != 0 && n != 0)
        {
            var pcm = new byte[n];
            Mem.Read(b, pcm);
            Oracle.AudioBlock(pcm, (int)n);
        }
        return 0;
    }

    // FULL follows the clock model (which also times the AI interrupts); with a live sound device a ring below its prebuffer clears FULL so the game
    // makes up for host-side drift and hiccups. Reporting FULL from the ring alone starved the AI interrupts (the model's queue ran dry) and silenced the audio.
    public static uint osAiGetStatus()
    {
        uint s; lock (aiFifo) s = aiFifo.Status(Host.Counter());
        if (s != 0 && !Oracle.Deterministic && Host.AudioLow is { } low && low()) s = 0;
        return s;
    }

    // ---------------------------------------------------------------------------------------------- frame tick

    /// <summary>Called once per displayed frame by the platform loop: timers, task completions, retrace events.</summary>
    private static int tickCount, gfxTasks, audioTasks, audioDone, aiBlocks, lastState = -1, lastScreen = -2;
    private static long aiBytes;
    private static readonly bool statusLog = Environment.GetEnvironmentVariable("TRW_STATUS") != null;
    private static readonly string? watchdogFile = Environment.GetEnvironmentVariable("TRW_WATCHDOG");     // write a thread dump here when graphics stops
    private static readonly string? peekSpec = Environment.GetEnvironmentVariable("TRW_PEEK");
    private static bool peeked;
    private static readonly int peekEvery = int.TryParse(Environment.GetEnvironmentVariable("TRW_PEEK_EVERY"), out var pe) ? pe : 0;
    private static int peekWait, watchdogGfx, watchdogTick;
    private static bool watchdogFired;

    /// <summary>The game's state (D_80146878) and the UI screen id, with the tick they last changed: scripted input waits for them (TRW_PAD_SCRIPT "R&lt;screen&gt;+&lt;delay&gt;" / "T&lt;state&gt;+&lt;delay&gt;").</summary>
    public static int StateId { get; private set; } = -1;
    public static int ScreenId { get; private set; } = -2;
    public static int StateSince { get; private set; }
    public static int ScreenSince { get; private set; }

    private static void TrackScreen()
    {
        int st = Mem.S32(0x80146878);
        if (st != StateId) { StateId = st; StateSince = tickCount; }
        int scr = Mem.S32(0x8014D098);
        if (scr < 0) { uint mgr = Mem.U32(0x8014D080); int top; scr = mgr == 0 || !Mem.IsMapped(mgr) || (top = Mem.S32(mgr + 4)) < 0 ? -1 : Mem.S32(Mem.U32(mgr + 0xC) + (uint)top * 0x1C + 4); }
        if (scr != ScreenId) { ScreenId = scr; ScreenSince = tickCount; }
    }

    /// <summary>One lockstep retrace: the tick, then wait until the game has consumed it completely, then record the oracle digest.</summary>
    public static void LockstepTick()
    {
        LockstepBegin();
        LockstepEnd();
    }

    /// <summary>First half of <see cref="LockstepTick"/>: delivers the tick and lets the game threads run it. The host thread may do other work
    /// (draw the previous frame) as long as it touches no game state, and must call <see cref="LockstepEnd"/> before the next tick or before reading RAM.
    /// The sequence of host actions the game threads see is the same as with LockstepTick, so the simulation is identical (netplay's window loop).</summary>
    public static void LockstepBegin()
    {
        ResetOps();
        ViTick();
        ReleaseParked();
    }

    /// <summary>Second half of <see cref="LockstepTick"/>: waits until the tick is consumed (every game thread blocked or parked), then the oracle digest.</summary>
    public static void LockstepEnd()
    {
        if (!WaitQuiescent()) { Oracle.Note($"tick {tickCount}: game threads did not quiesce"); Console.Error.WriteLine($"[oracle] tick {tickCount}: game threads did not quiesce (run is not reproducible)"); }
        Oracle.Tick(tickCount);
    }

    private static readonly int dumpAt = int.TryParse(Environment.GetEnvironmentVariable("TRW_DUMP_AT"), out var da) ? da : -1;
    public static void ViTick()
    {
        tickCount++; CurrentTick = tickCount; Mem.WTick = tickCount; if (tickCount > 0 && Environment.GetEnvironmentVariable("TRW_WLOG") != null) Mem.WLogFlush();
        if (Oracle.Deterministic) Host.DetAdvance(tickCount);
        if (tickCount == dumpAt) Console.Error.WriteLine($"[dump] tick {tickCount}\n{DumpThreads()}");      // TRW_DUMP_AT=<tick>: scheduler state of every game thread
        Trw.Game.Debug.HeapCheck.Tick(tickCount);
        TrackScreen();
        if (statusLog)
        {
            int st = Mem.S32(0x80146878);
            if (st != lastState) { Console.Error.WriteLine($"[state] tick {tickCount}: {lastState} -> {st}"); lastState = st; if (st == Dbg.ArmState) Dbg.Armed = true; }
            int scr = Mem.S32(0x8014D098);
            if (scr < 0) { uint mgr = Mem.U32(0x8014D080); int top; scr = mgr == 0 || Mem.IsMapped(mgr) == false || (top = Mem.S32(mgr + 4)) < 0 ? -1 : Mem.S32(Mem.U32(mgr + 0xC) + (uint)top * 0x1C + 4); }
            if (scr != lastScreen) { Console.Error.WriteLine($"[screen] tick {tickCount}: {lastScreen} -> {scr}"); lastScreen = scr; }
        }
        if (statusLog && tickCount % 300 == 0)
            Console.Error.WriteLine($"[status] tick {tickCount}: fade t={Mem.F32(0x80146884)} (clear above {Mem.F32(0x800CA564)}, scale {Mem.F32(0x800CA568)}); gfx tasks {gfxTasks}, audio tasks {audioTasks} (done {audioDone}), AI blocks {aiBlocks} ({aiBytes} bytes, {aiFifo.Frequency} Hz), state D_80146878={Mem.S32(0x80146878)} D_800D2984={Mem.S32(0x800D2984)} D_80146D68={Mem.S32(0x80146D68)} D_8014ADA0={Mem.S32(0x8014ADA0)}");
        // TRW_PEEK="addr:bytes;addr:bytes": dump guest memory once the match runs (TRW_PEEK_EVERY=<ticks>: and again every <ticks> after that)
        if (peekSpec != null && !peeked && Mem.S32(0x80146878) == 13 && ++peekWait > 240)
        {
            peeked = peekEvery <= 0; peekWait = 240 - peekEvery;
            Console.Error.WriteLine($"[peek] tick {tickCount}");
            foreach (var raw in peekSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                // "addr:len" or "addr:len@offset:len2" (the second dump is at the pointer stored at addr+offset)
                var part = raw; uint a; int n;
                var at = raw.Split('@');
                var kv = at[0].Split(':');
                a = Convert.ToUInt32(kv[0], 16); n = kv.Length > 1 ? Convert.ToInt32(kv[1], 16) : 64;
                if (at.Length > 1)
                {
                    var kv2 = at[1].Split(':');
                    uint ptr = Mem.U32(a + Convert.ToUInt32(kv2[0], 16));
                    Console.Error.WriteLine($"[peek] pointer at {a:X8}+{kv2[0]} = {ptr:X8}");
                    a = ptr; n = kv2.Length > 1 ? Convert.ToInt32(kv2[1], 16) : 64;
                    if (!Mem.IsMapped(a)) continue;
                }
                var sb = new System.Text.StringBuilder($"[peek] {a:X8}:");
                for (int i = 0; i < n; i += 4) { if (i % 32 == 0) sb.Append("\n  +").Append(i.ToString("X3")).Append(':'); sb.Append(' ').Append(Mem.U32(a + (uint)i).ToString("X8")); }
                Console.Error.WriteLine(sb.ToString());
            }
        }
        if (watchdogFile != null && !watchdogFired)
        {
            if (gfxTasks != watchdogGfx) { watchdogGfx = gfxTasks; watchdogTick = tickCount; }
            else if (tickCount - watchdogTick > 480)       // 8 s without a graphics task while the console keeps ticking
            {
                watchdogFired = true;
                File.WriteAllText(watchdogFile, $"stall: no graphics task since tick {watchdogTick}, now tick {tickCount} (gfx {gfxTasks}, audio {audioTasks})\n{DumpThreads()}");
                Console.Error.WriteLine($"[watchdog] graphics stalled at tick {tickCount}; thread dump in {watchdogFile}");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("dotnet-stack", $"report -p {Environment.ProcessId}") { RedirectStandardOutput = true, UseShellExecute = false };
                    var pr = System.Diagnostics.Process.Start(psi);
                    if (pr != null) _ = Task.Run(() => { var text = pr.StandardOutput.ReadToEnd(); File.AppendAllText(watchdogFile, "\n--- dotnet-stack\n" + text); });
                }
                catch (Exception) { }
            }
        }
        // lockstep: deliver all of this tick's messages atomically. A thread woken by the first post must not run (and read the clock or the
        // queues) while the remaining posts of the same tick are still being made; it blocks on kernelLock in Reschedule until we are done.
        bool locked = Oracle.Deterministic;
        if (locked) Monitor.Enter(kernelLock);
        try {
        RunTimers();
        bool? done = null;
        // a task completion is never dropped: on a busy host the scheduler can fall behind and its event queue fill up with retraces (the hardware drops those
        // too), and a lost SP / DP done left the scheduler waiting for the RSP forever (every thread blocked, no graphics, no audio). Wait for room instead.
        lock (kernelLock)
        {
            if (taskCompletions.Count > 0)
            {
                bool g = taskCompletions.Peek();
                int need = g && evMq[9] == evMq[4] ? 2 : 1;
                if ((evMq[4] == 0 || QueueHasRoom(evMq[4], need)) && (!g || evMq[9] == 0 || evMq[9] == evMq[4] || QueueHasRoom(evMq[9], 1))) done = taskCompletions.Dequeue();
            }
        }
        if (done is bool isGfx)       // one queued RSP task completion per tick: SP done, then DP done for graphics tasks
        {
            LogHost($"completing {(isGfx ? "graphics" : "audio")} task (SP event queue {evMq[4]:X8}, DP event queue {evMq[9]:X8})");
            if (evMq[4] != 0) PostFromHost(evMq[4], evMsg[4]);                  // SP done
            if (isGfx && evMq[9] != 0) PostFromHost(evMq[9], evMsg[9]);         // DP done
        }
        if (evMq[OS_EVENT_VI] != 0) PostFromHost(evMq[OS_EVENT_VI], evMsg[OS_EVENT_VI]);
        // AI DMA-done interrupt (OS_EVENT_AI = 6): about one per frame while audio is queued; the game's audio thread refills the AI FIFO on it
        // (osSetEventMesg(6, audio queue, 7)) and otherwise only every 21 retraces, which left 350 ms gaps once audio tasks completed at once
        bool aiDue; lock (aiFifo) aiDue = aiFifo.DmaDoneDue(Host.Counter());
        if (evMq[6] != 0 && aiDue) PostFromHost(evMq[6], evMsg[6]);
        if (viMq != 0 && ++viPhase >= viCount) { viPhase = 0; PostFromHost(viMq, viMsg); }
        } finally { if (locked) Monitor.Exit(kernelLock); }
    }
}
