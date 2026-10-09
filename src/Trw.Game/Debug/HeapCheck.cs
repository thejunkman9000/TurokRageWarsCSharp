using Trw.Platform;

namespace Trw.Game.Debug;

/// <summary>
/// <c>TRW_HEAPCHECK=1</c>: every tick, walks the game's resource cache list (D_80104584, sorted by block address; node = key, size, refs, flags ... next at +0x24)
/// and reports each pair of nodes whose blocks overlap, with the tick and node details. The first report names the allocation that went wrong.
/// </summary>
public static class HeapCheck
{
    private static readonly bool on = Environment.GetEnvironmentVariable("TRW_HEAPCHECK") != null;
    private static readonly HashSet<(uint, uint)> reported = new();
    private static int lastCount;

    public static void Tick(int tick)
    {
        if (!on) return;
        lock (Game.kernelLock)
        {
            uint n = Mem.U32(0x80104584), prev = 0;
            int count = 0, overlaps = 0;
            while (n != 0 && count < 100000 && Mem.IsMapped(n))
            {
                count++;
                if (prev != 0)
                {
                    uint pk = Mem.U32(prev), ps = Mem.U32(prev + 4), k = Mem.U32(n);
                    if (ps != 0 && pk + ps > k && reported.Add((prev, n)) && reported.Count <= 40)
                    {
                        overlaps++;
                        Console.Error.WriteLine($"[heap] tick {tick}: node {prev:X8} (key {pk:X8} size {ps:X} refs {Mem.S32(prev + 8)} flags {Mem.U32(prev + 0xC):X}) overlaps node {n:X8} (key {k:X8} size {Mem.U32(n + 4):X} refs {Mem.S32(n + 8)} flags {Mem.U32(n + 0xC):X})");
                    }
                }
                prev = n; n = Mem.U32(n + 0x24);
            }
            if (count != lastCount && tick % 60 == 0) { lastCount = count; Console.Error.WriteLine($"[heap] tick {tick}: {count} cache nodes"); }
        }
    }
}
