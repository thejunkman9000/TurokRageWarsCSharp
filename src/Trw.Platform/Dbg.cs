using System.Collections.Concurrent;

namespace Trw.Platform;

/// <summary>
/// Opt-in call tracing for the generated code.
///   TRW_TRACE_FN=name,name,...|*   every call of those functions logs its first four argument words (TRW_TRACE_LIMIT lines at most)
///   TRW_TRACE_HIST=file            counts the calls of every function and writes "name count" lines to the file when the process exits
/// When neither variable is set the checks are a single static readonly bool test.
/// </summary>
public static class Dbg
{
    private static readonly HashSet<string>? names = Parse(Environment.GetEnvironmentVariable("TRW_TRACE_FN"));
    private static readonly bool all = names != null && names.Contains("*");
    private static readonly int limit = int.TryParse(Environment.GetEnvironmentVariable("TRW_TRACE_LIMIT"), out var l) ? l : 200;
    private static readonly string? histPath = Environment.GetEnvironmentVariable("TRW_TRACE_HIST");
    private static readonly ConcurrentDictionary<string, int[]>? hist = histPath != null ? new() : null;
    private static int printed, batchPrinted;
    private static readonly bool batchLog = Environment.GetEnvironmentVariable("TRW_BATCHLOG") != null;

    /// <summary>Called on every function entry when set (differential tests, see Trw.Game/Debug/DiffTest).</summary>
    public static Action<string, uint, uint, uint, uint>? DiffHook;

    public static readonly bool On = names != null || hist != null || Environment.GetEnvironmentVariable("TRW_DIFF") != null || Environment.GetEnvironmentVariable("TRW_BATCHLOG") != null;

    static Dbg()
    {
        if (hist != null) AppDomain.CurrentDomain.ProcessExit += (_, _) => DumpHistogram();
    }

    private static HashSet<string>? Parse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : new HashSet<string>(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>TRW_TRACE_ARM_STATE=<n>: the call trace stays off until the game reaches state n (see Os/OsDevices.ViTick).</summary>
    public static volatile bool Armed = Environment.GetEnvironmentVariable("TRW_TRACE_ARM_STATE") == null;
    public static readonly int ArmState = int.TryParse(Environment.GetEnvironmentVariable("TRW_TRACE_ARM_STATE"), out var arm) ? arm : -1;

    private static readonly string[] prefixes = names == null ? Array.Empty<string>() : names.Where(n => n.EndsWith('*') && n.Length > 1).Select(n => n[..^1]).ToArray();   // "func_0044*" = prefix

    private static bool Wanted(string name) => Armed && names != null && (all || names.Contains(name) || (prefixes.Length > 0 && prefixes.Any(name.StartsWith)));

    public static void Enter(string name, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        DiffHook?.Invoke(name, a0, a1, a2, a3);
        if (batchLog && Armed && (name == "func_0026C020" || name == "func_0026D9D0") && Interlocked.Increment(ref batchPrinted) <= 60)
            Console.Error.WriteLine($"[batch] {name}: DLptr={Mem.U32(0x80110634):X8} count(D_80110640)={Mem.S32(0x80110640)} model(D_8011064C)={Mem.U32(0x8011064C):X8} key(D_80110624)={Mem.S32(0x80110624):X} nres(D_801153D0)={Mem.S32(0x801153D0)} e0={Mem.U32(0x801157D8):X8},{Mem.U32(0x801157DC):X8},{Mem.U32(0x801157E0):X8},{Mem.U32(0x801157E4):X8}");
        if (hist != null) Interlocked.Increment(ref hist.GetOrAdd(name, _ => new int[1])[0]);
        if (!Wanted(name) || Interlocked.Increment(ref printed) > limit) return;
        Console.Error.WriteLine($"[call] {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8})");
        if (dumpBytes > 0 && Mem.IsMapped(a0) && Mem.IsMapped(a0 + (uint)dumpBytes - 1))       // TRW_TRACE_DUMP=<bytes>: the memory a0 points to
        {
            var sb = new System.Text.StringBuilder("       a0:");
            for (int i = 0; i < dumpBytes; i += 4) { if (i % 32 == 0) sb.Append("\n       +").Append(i.ToString("X3")).Append(':'); sb.Append(' ').Append(Mem.U32(a0 + (uint)i).ToString("X8")); }
            Console.Error.WriteLine(sb.ToString());
        }
    }
    private static readonly int dumpBytes = int.TryParse(Environment.GetEnvironmentVariable("TRW_TRACE_DUMP"), out var db) ? db : 0;

    public static void Leave(string name, uint v0)
    {
        if (batchLog && Armed && (name == "func_0026C020" || name == "func_0026D9D0") && batchPrinted < 400)
            Console.Error.WriteLine($"[batch] leave {name}: DLptr={Mem.U32(0x80110634):X8} lists: D_80110628={Mem.U32(0x80110628):X8} D_8011063C={Mem.U32(0x8011063C):X8} D_80110630(n)={Mem.S32(0x80110630)} D_80110638(n)={Mem.S32(0x80110638)}");
        if (!Wanted(name) || printed > limit) return;
        Console.Error.WriteLine($"[ret ] {name} -> {v0:X8}");
    }

    /// <summary>Writes the call counts gathered so far (TRW_TRACE_HIST).</summary>
    public static void DumpHistogram()
    {
        if (hist == null || histPath == null) return;
        try { File.WriteAllLines(histPath, hist.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value[0]}")); }
        catch (IOException) { }
    }
}
