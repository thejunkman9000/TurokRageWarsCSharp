namespace Trw.Platform;

/// <summary>
/// Opt-in call tracing for the generated code: set TRW_TRACE_FN to a comma separated list of function names (or *) and every call of those
/// functions logs its first four argument words, and (for recompiled functions) the value returned in v0.
/// When the variable is not set the checks are a single static readonly bool test.
/// </summary>
public static class Dbg
{
    private static readonly HashSet<string>? names = Parse(Environment.GetEnvironmentVariable("TRW_TRACE_FN"));
    private static readonly bool all = names != null && names.Contains("*");
    private static readonly int limit = int.TryParse(Environment.GetEnvironmentVariable("TRW_TRACE_LIMIT"), out var l) ? l : 200;
    private static int printed;

    public static readonly bool On = names != null;

    private static HashSet<string>? Parse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : new HashSet<string>(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool Wanted(string name) => all || names!.Contains(name);

    public static void Enter(string name, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        if (!Wanted(name) || Interlocked.Increment(ref printed) > limit) return;
        Console.Error.WriteLine($"[call] {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8})");
    }

    public static void Leave(string name, uint v0)
    {
        if (!Wanted(name) || printed > limit) return;
        Console.Error.WriteLine($"[ret ] {name} -> {v0:X8}");
    }
}
