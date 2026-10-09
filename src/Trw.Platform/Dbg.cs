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
    private static int printed;

    public static readonly bool On = names != null || hist != null;

    static Dbg()
    {
        if (hist != null) AppDomain.CurrentDomain.ProcessExit += (_, _) => DumpHistogram();
    }

    private static HashSet<string>? Parse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : new HashSet<string>(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool Wanted(string name) => names != null && (all || names.Contains(name));

    public static void Enter(string name, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        if (hist != null) Interlocked.Increment(ref hist.GetOrAdd(name, _ => new int[1])[0]);
        if (!Wanted(name) || Interlocked.Increment(ref printed) > limit) return;
        Console.Error.WriteLine($"[call] {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8})");
    }

    public static void Leave(string name, uint v0)
    {
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
