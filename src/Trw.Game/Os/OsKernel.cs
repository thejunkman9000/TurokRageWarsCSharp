using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// libultra's operating system, replaced by the host (port of the C port's os_shim.c): threads, message queues, timers, events.
///
/// libultra threads are priority scheduled and only switch when something blocks or a higher-priority thread is woken.
/// Each game thread is a .NET thread, but only the thread holding the "baton" (<see cref="current"/>) runs; everyone else waits on a
/// private event. Message queues use the guest's own OSMesgQueue structures (message buffer, count, first).
/// </summary>
public static partial class Game
{
    // ---- guest structure layouts (include/common.h) ----
    private const uint THREAD_PRIORITY = 0x04, THREAD_STATE = 0x10, THREAD_ID = 0x14;
    private const uint MQ_VALID = 0x08, MQ_FIRST = 0x0C, MQ_COUNT = 0x10, MQ_MSG = 0x14;
    private const uint TIMER_INTERVAL = 0x08, TIMER_MQ = 0x18, TIMER_MSG = 0x1C;
    private const int OS_MESG_NOBLOCK = 0, OS_STATE_STOPPED = 1, OS_STATE_RUNNABLE = 2;
    private const int OS_EVENT_VI = 7;

    private sealed class HThread
    {
        public uint T;                    // guest OSThread
        public readonly AutoResetEvent Evt = new(false);
        public int Prio, State;           // 0 free, 1 stopped, 2 ready, 3 running, 4 waiting
        public uint Entry, Arg;
        public uint WaitQ; public int WaitKind;      // waiting on a queue: 1 = receive (empty), 2 = send (full)
        public uint Seq;                  // FIFO order among equal priorities
        public readonly ulong[] Fpr = new ulong[32]; // the guest FPU registers of this thread
        public Thread? Host;
    }

    private const int MaxThreads = 64;
    internal static readonly object kernelLock = new();
    private static readonly HThread[] threads = Enumerable.Range(0, MaxThreads).Select(_ => new HThread()).ToArray();
    private static HThread? current;
    private static uint seqCounter;
    private static volatile bool preempt;
    [ThreadStatic] private static HThread? tlsSelf;
    private static volatile bool bootSeen;

    // last host-side events (posts, task starts), kept for the watchdog dump
    private static readonly string[] hostLog = new string[96];
    private static int hostLogN;
    private static readonly bool hostLogOn = Environment.GetEnvironmentVariable("TRW_WATCHDOG") != null;
    internal static void LogHost(string s)
    {
        if (!hostLogOn) return;
        lock (hostLog) hostLog[hostLogN++ % hostLog.Length] = $"tick {CurrentTick}: {s}";
    }
    internal static int CurrentTick;

    /// <summary>Debug aid (TRW_WATCHDOG): the scheduler state of every game thread and the queue it waits on, with the queue's guest contents.</summary>
    public static string DumpThreads()
    {
        var sb = new System.Text.StringBuilder();
        lock (kernelLock)
        {
            sb.AppendLine($"current = T{Array.IndexOf(threads, current)}");
            for (int i = 0; i < threads.Length; i++)
            {
                var h = threads[i];
                if (h.State == 0) continue;
                string st = h.State switch { 1 => "stopped", 2 => "ready", 3 => "running", 4 => "waiting", _ => "?" };
                sb.Append($"T{i} id={(h.T != 0 ? Mem.S32(h.T + 0x14) : -1)} prio={h.Prio} {st} entry={Funcs.NameOf(h.Entry)}");
                if (h.State == 4 && h.WaitQ != 0)
                    sb.Append($" waits {(h.WaitKind == 1 ? "to receive from" : "to send to")} queue {h.WaitQ:X8} (valid {Mem.S32(h.WaitQ + 8)} first {Mem.S32(h.WaitQ + 0xC)} capacity {Mem.S32(h.WaitQ + 0x10)})");
                sb.AppendLine();
            }
        }
        lock (hostLog)
        {
            sb.AppendLine("--- last host events (oldest first)");
            for (int i = Math.Max(0, hostLogN - hostLog.Length); i < hostLogN; i++) sb.AppendLine(hostLog[i % hostLog.Length]);
        }
        return sb.ToString();
    }

    private static uint threadStackNext = 0x7F000000;
    private const uint ThreadStackSize = 0x20000;

    private static readonly bool traceOs = Environment.GetEnvironmentVariable("TRW_TRACE_OS") != null;
    private static string Who() => tlsSelf != null ? $"T{Array.IndexOf(threads, tlsSelf)}" : "T?";
    private static void TraceOs(string s) { if (traceOs) Console.Error.WriteLine($"[{Who()}@{CurrentTick}] {s}"); }

    /// <summary>A differential test (Debug/DiffTest) must not reach anything that changes scheduler or timer state: it aborts instead.</summary>
    internal static void GuardTest(string what) { if (Debug.DiffTest.Active) throw new Debug.DiffTest.TestAbort(what); }

    private static HThread? SelfThread()
    {
        if (tlsSelf != null) return tlsSelf;
        // an unregistered host thread calling into the OS is the game's boot thread: adopt it as a running thread
        lock (kernelLock)
        {
            foreach (var h in threads)
            {
                if (h.State != 0) continue;
                h.Prio = 10; h.State = 3; h.Seq = ++seqCounter; h.Host = Thread.CurrentThread;
                tlsSelf = h;
                current ??= h;
                bootSeen = true;
                return h;
            }
        }
        return null;
    }

    /// <summary>Best runnable thread; the caller holds the lock.</summary>
    private static HThread? Pick()
    {
        HThread? best = null;
        foreach (var h in threads)
        {
            if (h.State != 2 && h.State != 3) continue;
            if (best == null || h.Prio > best.Prio || (h.Prio == best.Prio && h.Seq < best.Seq)) best = h;
        }
        return best;
    }

    /// <summary>Gives the baton away if someone better is runnable (or self blocked); waits until self is current again. The lock is held on entry and exit.</summary>
    private static void Reschedule(HThread self)
    {
        GuardTest("Reschedule");
        for (;;)
        {
            var best = Pick();
            if (best == self && self.State != 4) { self.State = 3; current = self; return; }
            if (self.State == 3)
            {
                self.State = 2; self.Seq = ++seqCounter; best = Pick();
                if (best == self) { self.State = 3; current = self; return; }
            }
            current = best;
            if (best != null) { best.State = 3; best.Evt.Set(); }
            Array.Copy(Cpu.Fpr, self.Fpr, 32);
            Monitor.Exit(kernelLock);
            for (;;)    // wait for the baton; the timeout makes a lost wake-up harmless
            {
                self.Evt.WaitOne(10);
                Monitor.Enter(kernelLock);
                if (current == null)
                {
                    var b = Pick();
                    if (b != null) { current = b; b.State = 3; if (b != self) b.Evt.Set(); }
                }
                if (current == self) { self.State = 3; Array.Copy(self.Fpr, Cpu.Fpr, 32); return; }
                Monitor.Exit(kernelLock);
            }
        }
    }

    // TRW_DETERMINISTIC + TRW_TICK_OPS=<n>: the retrace tick may also interrupt a game thread that computes for long without blocking (a level load
    // spans many ticks on real hardware). The interrupt point is deterministic: the n-th scheduler-visible call (message send/receive, clock read)
    // since the last tick parks the calling thread until the host has delivered the tick. 0 = ticks only when every thread is blocked.
    private static readonly int opBudget = int.TryParse(Environment.GetEnvironmentVariable("TRW_TICK_OPS"), out var ob) ? ob : 0;
    private static int opsThisTick;
    private static volatile bool parked;
    private static readonly ManualResetEventSlim resume = new(false);

    private static void OpPoint()
    {
        if (opBudget <= 0 || Debug.DiffTest.Active || ++opsThisTick < opBudget) return;       // a differential test holds kernelLock: parking here would deadlock the tick
        opsThisTick = 0;
        resume.Reset();
        parked = true;
        resume.Wait();
    }

    /// <summary>Host side, before the tick's messages are posted: restarts the op count of the tick. It must happen before the post, while every game thread is
    /// quiescent: the thread woken by the first post runs concurrently with the host, and its first ops used to be erased by a reset that came after the post
    /// (a race that moved the park point of the tick, visible as a one-tick divergence of the digest under CPU load).</summary>
    internal static void ResetOps() { opsThisTick = 0; }

    /// <summary>Host side: lets a thread parked by <see cref="OpPoint"/> continue (called after the tick's messages were delivered).</summary>
    internal static void ReleaseParked()
    {
        if (parked) { parked = false; resume.Set(); }
    }

    private static void MaybeYield(HThread self)
    {
        if (Oracle.Deterministic) OpPoint();
        if (!preempt) return;
        lock (kernelLock) { preempt = false; Reschedule(self); }
    }

    /// <summary>Wakes the highest-priority waiter on a queue; the caller holds the lock.</summary>
    private static void WakeOne(uint mq, int kind)
    {
        GuardTest("WakeOne");
        HThread? best = null;
        foreach (var h in threads)
            if (h.State == 4 && h.WaitQ == mq && h.WaitKind == kind && (best == null || h.Prio > best.Prio)) best = h;
        if (best == null) return;
        best.State = 2; best.WaitQ = 0; best.Seq = ++seqCounter;
        if (current == null || best.Prio > current.Prio) preempt = true;
    }

    // ---------------------------------------------------------------------------------------------- message queues

    public static void osCreateMesgQueue(uint mq, uint msg, int count)
    {
        TraceOs($"osCreateMesgQueue {mq:X8} count={count}");
        Mem.Set(mq, 0u); Mem.Set(mq + 4, 0u);
        Mem.Set(mq + MQ_VALID, 0); Mem.Set(mq + MQ_FIRST, 0); Mem.Set(mq + MQ_COUNT, count); Mem.Set(mq + MQ_MSG, msg);
        SiQueueRecreated(mq);
    }

    private static int SendCommon(uint mq, uint m, int flags, bool jam)
    {
        TraceOs($"osSendMesg {mq:X8} msg={m:X8} flags={flags}");
        var self = SelfThread()!;
        MaybeYield(self);
        lock (kernelLock)
        {
            while (Mem.S32(mq + MQ_VALID) >= Mem.S32(mq + MQ_COUNT))
            {
                if (flags == OS_MESG_NOBLOCK) return -1;
                self.State = 4; self.WaitQ = mq; self.WaitKind = 2;
                Reschedule(self);
            }
            uint buf = Mem.U32(mq + MQ_MSG);
            int cnt = Mem.S32(mq + MQ_COUNT), first = Mem.S32(mq + MQ_FIRST), valid = Mem.S32(mq + MQ_VALID);
            if (jam) { first = (first + cnt - 1) % cnt; Mem.Set(mq + MQ_FIRST, first); Mem.Set(buf + (uint)(first * 4), m); }
            else Mem.Set(buf + (uint)(((first + valid) % cnt) * 4), m);
            Mem.Set(mq + MQ_VALID, valid + 1);
            WakeOne(mq, 1);
            if (preempt) { preempt = false; Reschedule(self); }
            return 0;
        }
    }

    public static int osSendMesg(uint mq, uint m, int flags) => SendCommon(mq, m, flags, false);
    public static int osJamMesg(uint mq, uint m, int flags) => SendCommon(mq, m, flags, true);

    public static int osRecvMesg(uint mq, uint m, int flags)
    {
        TraceOs($"osRecvMesg {mq:X8} flags={flags} valid={Mem.S32(mq + MQ_VALID)}");
        var self = SelfThread()!;
        MaybeYield(self);     // a polling thread must not starve a higher-priority thread that became ready
        DeliverAudioCompletion(mq);
        SiRecvGuard(mq, flags);      // Os/OsDevices.cs: a pad poll waiting for a transfer nobody started
        lock (kernelLock)
        {
            while (Mem.S32(mq + MQ_VALID) == 0)
            {
                if (flags == OS_MESG_NOBLOCK) return -1;
                self.State = 4; self.WaitQ = mq; self.WaitKind = 1;
                Reschedule(self);
            }
            uint buf = Mem.U32(mq + MQ_MSG);
            int cnt = Mem.S32(mq + MQ_COUNT), first = Mem.S32(mq + MQ_FIRST);
            if (m != 0) Mem.Set(m, Mem.U32(buf + (uint)(first * 4)));
            Mem.Set(mq + MQ_FIRST, (first + 1) % cnt);
            Mem.Set(mq + MQ_VALID, Mem.S32(mq + MQ_VALID) - 1);
            WakeOne(mq, 2);
            if (preempt) { preempt = false; Reschedule(self); }
            return 0;
        }
    }

    /// <summary>Messages posted from host threads (VI tick, timers, DMA completion): never block.</summary>
    private static void PostFromHost(uint mq, uint m)
    {
        GuardTest("PostFromHost");
        if (mq == 0) return;
        lock (kernelLock)
        {
            LogHost($"post {mq:X8} msg={m:X8} valid={Mem.S32(mq + MQ_VALID)}/{Mem.S32(mq + MQ_COUNT)}");
            if (Mem.S32(mq + MQ_VALID) >= Mem.S32(mq + MQ_COUNT)) LogHost($"  DROPPED: queue {mq:X8} is full");
            if (Mem.S32(mq + MQ_VALID) < Mem.S32(mq + MQ_COUNT))
            {
                uint buf = Mem.U32(mq + MQ_MSG);
                int cnt = Mem.S32(mq + MQ_COUNT), first = Mem.S32(mq + MQ_FIRST), valid = Mem.S32(mq + MQ_VALID);
                if (cnt <= 0 || !Mem.IsMapped(buf)) { Console.Error.WriteLine($"PostFromHost: bad queue {mq:X8} msg={buf:X8} count={cnt}"); return; }
                Mem.Set(buf + (uint)(((first + valid) % cnt) * 4), m);
                Mem.Set(mq + MQ_VALID, valid + 1);
                WakeOne(mq, 1);
                if (current == null)
                {
                    var b = Pick();
                    if (b != null) { current = b; b.State = 3; b.Evt.Set(); }
                    preempt = false;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- threads

    public static void osCreateThread(uint t, int id, uint entry, uint arg, uint sp, int pri)
    {
        GuardTest("osCreateThread");
        TraceOs($"osCreateThread id={id} entry={Funcs.NameOf(entry)} pri={pri}");
        lock (kernelLock)
        {
            foreach (var h in threads)
            {
                if (h.State != 0) continue;
                h.T = t; h.Prio = pri; h.State = 1; h.Entry = entry; h.Arg = arg; h.WaitQ = 0;
                Mem.Set(t + THREAD_PRIORITY, pri); Mem.Set(t + THREAD_ID, id); Mem.Set(t + THREAD_STATE, (ushort)OS_STATE_STOPPED);
                Mem.Set(t, 0u); Mem.Set(t + 8, 0u);
                // the host thread gets its own guest stack for locals/frames
                uint stackBase = threadStackNext; threadStackNext += ThreadStackSize;
                Mem.EnsureStackRegion(stackBase, ThreadStackSize);
                var th = new Thread(() => ThreadProc(h, stackBase), 1 << 20) { IsBackground = true, Name = $"osThread {id}" };
                h.Host = th;
                th.Start();
                return;
            }
        }
    }

    private static void ThreadProc(HThread h, uint stackBase)
    {
        tlsSelf = h;
        Stack.Init(stackBase, ThreadStackSize);
        h.Evt.WaitOne();     // wait for our first turn
        try { Funcs.CallReg(h.Entry, h.Arg, 0, 0, 0); }
        catch (Exception e) { Console.Error.WriteLine($"game thread {Funcs.NameOf(h.Entry)} died: {e}"); }
        ThreadEnded(h);
    }

    private static void ThreadEnded(HThread h)
    {
        lock (kernelLock)
        {
            h.State = 1;
            if (current == h) current = null;
            var b = Pick();
            if (b != null) { current = b; b.State = 3; b.Evt.Set(); }
        }
    }

    private static HThread? FindThread(uint t)
    {
        if (t == 0) return SelfThread();
        foreach (var h in threads) if (h.State != 0 && h.T == t) return h;
        return null;
    }

    public static void osStartThread(uint t)
    {
        GuardTest("osStartThread");
        TraceOs($"osStartThread {t:X8}");
        var self = SelfThread()!;
        lock (kernelLock)
        {
            var h = FindThread(t);
            if (h != null && h.State == 1)
            {
                h.State = 2; h.Seq = ++seqCounter;
                Mem.Set(t + THREAD_STATE, (ushort)OS_STATE_RUNNABLE);
                Reschedule(self);
            }
        }
    }

    public static void osSetThreadPri(uint t, int pri)
    {
        GuardTest("osSetThreadPri");
        var self = SelfThread()!;
        lock (kernelLock)
        {
            var h = FindThread(t);
            if (h != null) { h.Prio = pri; if (h.T != 0) Mem.Set(h.T + THREAD_PRIORITY, pri); }
            Reschedule(self);
        }
    }

    public static void osYieldThread()
    {
        GuardTest("osYieldThread");
        var self = SelfThread()!;
        lock (kernelLock) { self.Seq = ++seqCounter; Reschedule(self); }
    }

    public static void osDestroyThread(uint t)
    {
        GuardTest("osDestroyThread");
        lock (kernelLock)
        {
            var h = FindThread(t);
            if (h == null) return;
            h.State = 1;
            if (current == h) current = null;
        }
    }

    public static int osGetThreadPri(uint t)
    {
        var h = FindThread(t);
        return h != null ? h.Prio : 0;
    }

    public static int osGetThreadId(uint t)
    {
        var h = FindThread(t);
        return h != null && h.T != 0 ? Mem.S32(h.T + THREAD_ID) : 0;
    }

    // ---------------------------------------------------------------------------------------------- time and timers

    /// <summary>Set by the differential tester (Debug/DiffTest.cs) while it runs a function twice: both runs then read the same count (the
    /// interpreter reads it as CP0 register 9), so code that stores the count (the profiling timers) compares equal.</summary>
    internal static uint? FrozenCount;
    public static uint osGetCount() { TraceOs("osGetCount"); if (Oracle.Deterministic && tlsSelf != null) OpPoint(); return FrozenCount ?? (uint)Host.Counter(); }
    public static ulong osGetTime() { TraceOs("osGetTime"); MaybeYield(SelfThread()!); return Host.Counter(); }

    private sealed class HTimer { public uint T; public ulong Due, Interval; public bool Live; }
    private static readonly HTimer[] timers = Enumerable.Range(0, 64).Select(_ => new HTimer()).ToArray();

    public static int osSetTimer(uint t, ulong value, ulong interval, uint mq, uint msg)
    {
        GuardTest("osSetTimer");
        Mem.Set(t + TIMER_MQ, mq); Mem.Set(t + TIMER_MSG, msg); Mem.Set(t + TIMER_INTERVAL, interval);
        lock (kernelLock)
        {
            foreach (var tm in timers)
                if (!tm.Live || tm.T == t) { tm.T = t; tm.Due = Host.Counter() + value; tm.Interval = interval; tm.Live = true; break; }
        }
        return 0;
    }

    public static int osStopTimer(uint t)
    {
        GuardTest("osStopTimer");
        lock (kernelLock) foreach (var tm in timers) if (tm.Live && tm.T == t) tm.Live = false;
        return 0;
    }

    /// <summary>
    /// TRW_DETERMINISTIC lockstep: blocks until no game thread is runnable or running (all are waiting for a message, i.e. for the next tick).
    /// Returns false on timeout (a game thread is spinning; the run is then not reproducible).
    /// </summary>
    public static bool WaitQuiescent(int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int spin = 0; ; spin++)
        {
            lock (kernelLock)
            {
                if (parked) return true;
                bool busy = current != null || !bootSeen;      // before the boot thread made its first OS call nothing is registered yet
                if (!busy) foreach (var h in threads) if (h.State == 2 || h.State == 3) { busy = true; break; }
                if (!busy) return true;
            }
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            if (spin < 200) Thread.Yield(); else Thread.Sleep(0);
        }
    }

    private static void RunTimers()      // called from the platform loop once per frame
    {
        ulong now = Host.Counter();
        foreach (var tm in timers)
        {
            uint t = 0;
            lock (kernelLock)
            {
                if (tm.Live && now >= tm.Due)
                {
                    t = tm.T;
                    if (tm.Interval != 0) tm.Due += tm.Interval; else tm.Live = false;
                }
            }
            if (t != 0) PostFromHost(Mem.U32(t + TIMER_MQ), Mem.U32(t + TIMER_MSG));
        }
    }
}


