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
        TraceOs($"osSpTaskStartGo type={Mem.U32(pendingTask)}");
        uint task = pendingTask;
        uint type = Mem.U32(task);
        bool gfx = type == 1;
        if (gfx) { gfxTasks++; graphicsTaskReady = task; Host.GraphicsTask?.Invoke(task); }
        if (type == 2) { audioTasks++; Host.AudioTask?.Invoke(Mem.U32(task + 0x30), Mem.U32(task + 0x34)); }
        // completion is delivered from the frame tick: the caller still has bookkeeping to do
        lock (kernelLock) { if (taskCompletions.Count < 16) taskCompletions.Enqueue(gfx); }
    }

    public static void osSpTaskYield() { }
    public static int osSpTaskYielded(uint t) => 0;
    public static int osDpSetNextBuffer(uint a, ulong b) => 0;

    // ---------------------------------------------------------------------------------------------- cartridge DMA

    private static void RomCopy(uint dst, uint dev, uint n)
    {
        var rom = Host.Rom;
        uint off = dev >= 0x10000000 ? dev - 0x10000000 : dev & 0x0FFFFFFF;
        if (off >= rom.Length) return;
        int len = (int)Math.Min(n, (uint)rom.Length - off);
        Mem.Write(dst, rom.AsSpan((int)off, len));
    }

    // OSIoMesg: hdr.retQueue@4
    public static int osPiStartDma(uint mb, int pri, int dir, uint dev, uint vaddr, uint n, uint mq)
    {
        TraceOs($"osPiStartDma dir={dir} dev={dev:X8} dram={vaddr:X8} size={n:X}");
        if (dir == 0) RomCopy(vaddr, dev, n);         // OS_READ
        Mem.Set(mb + 4, mq);
        PostFromHost(mq, mb);
        return 0;
    }

    public static int osPiRawStartDma(int dir, uint dev, uint vaddr, uint n)
    {
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

    /// <summary>No controller pak / EEPROM on the host: the transfer "completes" with the buffer untouched.</summary>
    public static int __osSiRawStartDma(int dir, uint pifRam) { PostFromHost(evMq[5], evMsg[5]); return 0; }

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
            Mem.Set(status + (uint)(i * 4) + 2, (byte)0);
            Mem.Set(status + (uint)(i * 4) + 3, (byte)(c ? 0 : 0x80));
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
            Mem.Set(status + (uint)(i * 4) + 3, (byte)(c ? 0 : 0x80));
        }
    }

    public static int osContReset(uint mq, uint status) => 0;
    public static int osMotorInit(uint mq, uint pfs, int channel) => 1;
    public static int osMotorStart(uint pfs) => 0;
    public static int osMotorStop(uint pfs) => 0;

    // ---------------------------------------------------------------------------------------------- audio interface

    public static int osAiSetFrequency(uint f) { Host.AudioSetFrequency?.Invoke(f); return (int)f; }

    public static int osAiSetNextBuffer(uint b, uint n)
    {
        if (b != 0 && n != 0 && Host.AudioPush != null)
        {
            var buf = new byte[n];
            Mem.Read(b, buf);
            Host.AudioPush(buf, (int)n);
        }
        return 0;
    }

    public static uint osAiGetStatus() => 0;

    // ---------------------------------------------------------------------------------------------- frame tick

    /// <summary>Called once per displayed frame by the platform loop: timers, task completions, retrace events.</summary>
    private static int tickCount, gfxTasks, audioTasks, lastState = -1;
    private static readonly bool statusLog = Environment.GetEnvironmentVariable("TRW_STATUS") != null;

    public static void ViTick()
    {
        tickCount++;
        if (statusLog)
        {
            int st = Mem.S32(0x80146878);
            if (st != lastState) { Console.Error.WriteLine($"[state] tick {tickCount}: {lastState} -> {st}"); lastState = st; }
        }
        if (statusLog && tickCount % 600 == 0)
            Console.Error.WriteLine($"[status] tick {tickCount}: gfx tasks {gfxTasks}, audio tasks {audioTasks}, state D_80146878={Mem.S32(0x80146878)} D_800D2984={Mem.S32(0x800D2984)} D_80146D68={Mem.S32(0x80146D68)} D_8014ADA0={Mem.S32(0x8014ADA0)}");
        RunTimers();
        bool? done = null;
        lock (kernelLock) { if (taskCompletions.Count > 0) done = taskCompletions.Dequeue(); }
        if (done is bool isGfx)       // one queued RSP task completion per tick: SP done, then DP done for graphics tasks
        {
            if (evMq[4] != 0) PostFromHost(evMq[4], evMsg[4]);                  // SP done
            if (isGfx && evMq[9] != 0) PostFromHost(evMq[9], evMsg[9]);         // DP done
        }
        if (evMq[OS_EVENT_VI] != 0) PostFromHost(evMq[OS_EVENT_VI], evMsg[OS_EVENT_VI]);
        if (viMq != 0 && ++viPhase >= viCount) { viPhase = 0; PostFromHost(viMq, viMsg); }
    }
}
