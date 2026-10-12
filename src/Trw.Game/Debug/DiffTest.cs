using Trw.Platform;

namespace Trw.Game.Debug;

/// <summary>
/// Differential test of the translation against the ROM's machine code, in the context of the running game.
/// <c>TRW_DIFF=name,name|*</c>: at the first <c>TRW_DIFF_N</c> (default 3) calls of each named function the game's memory and FPU state is
/// captured, the original MIPS code (<see cref="MipsInterp"/>) runs the function from that state, the state is restored, the translated C#
/// runs from the same state, and the two results are compared: every byte of memory they changed (stack frames excluded), the integer
/// result register and (for float functions) F0. Mismatches are logged as <c>[diff]</c> lines.
/// Functions the interpreter cannot run (hardware, blocking OS calls, unsupported instructions) are counted as inconclusive.
/// TRW_DIFF_ROOT = the C port repository (default D:\Projects\TurokRageWars) for the listing; TRW_DIFF_SIGS = tools' sigs file.
/// </summary>
public static class DiffTest
{
    private static readonly string? spec = Environment.GetEnvironmentVariable("TRW_DIFF");
    private static readonly bool all = spec == "*";
    private static readonly HashSet<string> wanted = spec == null || all ? new() : new(spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static readonly int perFunction = int.TryParse(Environment.GetEnvironmentVariable("TRW_DIFF_N"), out var n) ? n : 3;
    private static readonly Dictionary<string, int> done = new(), calls = new();
    private static readonly int every = int.TryParse(Environment.GetEnvironmentVariable("TRW_DIFF_EVERY"), out var ev) ? ev : 1;
    private static readonly Dictionary<string, string[]> sigs = new();           // name -> [ret, p0, p1, ...]
    private static readonly Dictionary<string, int[]> sizes = new();             // name -> struct-by-value size in bytes per parameter (0 = scalar)
    // TRW_DIFF_WINDOW=a-b: only calls made while the retrace counter is in [a, b] are tested (TRW_DIFF_N then counts per function inside the window)
    private static readonly (int lo, int hi)? window = Environment.GetEnvironmentVariable("TRW_DIFF_WINDOW") is { } w && w.Split('-') is { Length: 2 } wp && int.TryParse(wp[0], out var wlo) && int.TryParse(wp[1], out var whi) ? (wlo, whi) : null;
    [ThreadStatic] private static bool inTest;
    /// <summary>True while a test runs on this thread (the OS layer refuses to change scheduler state then).</summary>
    public static bool Active => inTest;
    public sealed class TestAbort(string what) : Exception("OS call inside a test: " + what) { }
    private static bool ready;
    private static int tested, passed, failed, inconclusive;
    private static readonly Dictionary<string, int> abortReasons = new();

    public static void Install()
    {
        if (spec == null) return;
        var root = Environment.GetEnvironmentVariable("TRW_DIFF_ROOT") ?? @"D:\Projects\TurokRageWars";
        var shims = new List<string>();
        var shimFile = FindUp("tools/Trw.Transpile/shim_replaced.txt");
        if (shimFile != null) shims.AddRange(File.ReadLines(shimFile).Select(l => l.Split('|')[0].Trim()).Where(l => l != "" && !l.StartsWith('#')));
        shims.AddRange(new[] { "__divdi3", "__moddi3", "__udivdi3", "__umoddi3", "__floatdisf", "__floatdidf", "__cmpdi2", "__ucmpdi2", "__fixsfdi", "__fixunssfdi" });
        MipsInterp.LoadListing(root, shims);
        var sigFile = Environment.GetEnvironmentVariable("TRW_DIFF_SIGS") ?? FindUp("src/Trw.Game/Generated/sigs.txt");
        if (sigFile != null && File.Exists(sigFile))
            foreach (var l in File.ReadLines(sigFile))
            {
                var p = l.Split('|');
                if (p.Length >= 3) sigs[p[0]] = new[] { p[1] }.Concat(p[2] == "" ? Array.Empty<string>() : p[2].Split(',')).ToArray();
                if (p.Length >= 4 && p[3] != "" && p[3].Split(',').Select(x => int.TryParse(x, out var z) ? z : 0).ToArray() is { } sz && sz.Any(x => x > 4)) sizes[p[0]] = sz;
            }
        Dbg.DiffHook = Hook;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Summary();
        ready = true;
        Console.Error.WriteLine($"[diff] listing: {MipsInterp.Code.Count} instructions, {MipsInterp.FuncAddr.Count} functions; {sigs.Count} signatures");
    }

    private static string? FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Summary()
    {
        Console.Error.WriteLine($"[diff] summary: {tested} tests, {passed} identical, {failed} MISMATCH, {inconclusive} inconclusive");
        foreach (var kv in abortReasons.OrderByDescending(k => k.Value).Take(12)) Console.Error.WriteLine($"[diff]   inconclusive x{kv.Value}: {kv.Key}");
    }

    private static bool Testable(string name)
    {
        if (!sigs.TryGetValue(name, out var s)) return false;
        if (s.Length - 1 > 4) return false;
        for (int i = 1; i < s.Length; i++) if (s[i] is "float" or "double" or "long" or "ulong") return false;
        if (sizes.TryGetValue(name, out var sz))
        {
            // struct parameters travel as words in a0..a3 (the trace hands over the address of the callee's copy, see ExpandStructs): all words must fit the registers
            int words = 0;
            for (int i = 0; i < sz.Length; i++) words += sz[i] > 4 ? (sz[i] + 3) / 4 : 1;
            if (words > 4) return false;
        }
        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> finished = new();     // functions that need no more attention (tested enough, or not testable)

    /// <summary>The trace reports a struct-by-value parameter as the address of the callee's copy, but the original code (and the register-ABI entry the
    /// differential test calls) receives the struct's words in consecutive argument registers: replace the address by the words.</summary>
    private static void ExpandStructs(string name, ref uint a0, ref uint a1, ref uint a2, ref uint a3)
    {
        if (!sizes.TryGetValue(name, out var sz)) return;
        var r = new[] { a0, a1, a2, a3 };
        var o = new uint[4];
        int slot = 0;
        for (int i = 0; i < sz.Length && slot < 4; i++)
        {
            if (sz[i] > 4)
            {
                int n = (sz[i] + 3) / 4;
                for (int k = 0; k < n && slot + k < 4; k++) o[slot + k] = Mem.U32(r[slot] + 4u * (uint)k);
                slot += n;
            }
            else { o[slot] = r[slot]; slot++; }
        }
        a0 = o[0]; a1 = o[1]; a2 = o[2]; a3 = o[3];
    }

    private static void Hook(string name, uint a0, uint a1, uint a2, uint a3)
    {
        if (!ready || inTest || !Dbg.Armed || finished.ContainsKey(name)) return;
        if (window is { } win && (Game.CurrentTick < win.lo || Game.CurrentTick > win.hi)) return;       // Dbg.Armed: TRW_TRACE_ARM_STATE=<n> starts the tests once the game reaches state n
        if ((!all && !wanted.Contains(name)) || !MipsInterp.FuncAddr.ContainsKey(name) || !Testable(name))
        {
            if (verbose && (all || wanted.Contains(name)) && !finished.ContainsKey(name)) Console.Error.WriteLine($"[diff] skipped {name}: " + (!MipsInterp.FuncAddr.ContainsKey(name) ? "not in the listing" : !sigs.ContainsKey(name) ? "no signature" : "signature not testable (more than 4 words, float or 64-bit parameters, large structs)"));
            finished[name] = 0; return;
        }
        var addr = MipsInterp.FuncAddr[name];
        lock (done)
        {
            calls.TryGetValue(name, out var nc); calls[name] = nc + 1;
            if (every > 1 && nc % every != 0) return;          // TRW_DIFF_EVERY=k: test every k-th call, so the samples spread over the whole run
            done.TryGetValue(name, out var c);
            if (c >= perFunction) { finished[name] = 0; return; }
            done[name] = c + 1;
        }
        inTest = true;
        try { lock (Game.kernelLock) { ExpandStructs(name, ref a0, ref a1, ref a2, ref a3); Run(name, addr, a0, a1, a2, a3); } }       // no host thread may post a message while memory is being rolled back
        catch (Exception e) { Inconclusive($"{e.GetType().Name} {e.Message}".Split('\n')[0]); }
        finally { inTest = false; }
    }

    private static readonly bool verbose = Environment.GetEnvironmentVariable("TRW_DIFF_VERBOSE") != null;
    private static string current = "";
    private static void Inconclusive(string why)
    {
        if (verbose) Console.Error.WriteLine($"[diff] inconclusive {current}: {why}");
        inconclusive++; tested++;
        lock (abortReasons) { abortReasons.TryGetValue(why, out var c); abortReasons[why] = c + 1; }
    }

    private static void Run(string name, uint addr, uint a0, uint a1, uint a2, uint a3)
    {
        var sig = sigs[name];
        current = name;
        string ret = sig[0];
        var fpr = (ulong[])Cpu.Fpr.Clone(); uint fcsr = Cpu.Fcsr, v1 = Cpu.V1; var stk = (uint[])Cpu.Stk.Clone(); var cp0 = (uint[])Cpu.Cp0.Clone();
        void RestoreCpu() { Array.Copy(fpr, Cpu.Fpr, 32); Cpu.Fcsr = fcsr; Cpu.V1 = v1; Array.Copy(stk, Cpu.Stk, 32); Array.Copy(cp0, Cpu.Cp0, 32); }

        // 1. the original machine code
        uint scratch = Stack.Alloc(0x4000);
        var interp = new MipsInterp();
        interp.R[4] = MipsInterp.Sx(a0); interp.R[5] = MipsInterp.Sx(a1); interp.R[6] = MipsInterp.Sx(a2); interp.R[7] = MipsInterp.Sx(a3); interp.R[29] = MipsInterp.Sx(scratch + 0x4000 - 64);
        Mem.JournalBegin();
        bool ok;
        try { ok = interp.Run(addr); }
        catch (Mem.GuestFault f) { interp.Abort = $"guest fault at {f.Address:X8}"; ok = false; }
        catch (Exception e) { interp.Abort = e.Message.Split('\n')[0]; ok = false; }
        var j1 = Mem.JournalEnd();
        var bytes1 = ok ? Mem.JournalBytes(j1) : null;
        uint v0a = (uint)interp.R[2]; uint f0a = Cpu.W(0); ulong f0full = Cpu.Fpr[0];
        Mem.JournalUndo(j1);
        RestoreCpu();
        Stack.Free(0x4000);
        if (!ok) { Inconclusive(interp.Abort ?? "aborted"); return; }

        // 2. the translation
        Mem.JournalBegin();
        uint v0b;
        try { v0b = Funcs.CallReg(addr, a0, a1, a2, a3); }
        catch (Exception e)
        {
            Mem.JournalUndo(Mem.JournalEnd()); RestoreCpu(); Inconclusive("translated code threw " + e.GetType().Name);
            if (e is not TestAbort) Console.Error.WriteLine($"[diff] translated code threw {e.GetType().Name} ({e.Message.Split('\n')[0]}) in {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8}) although the original ran to completion ({interp.Steps} MIPS steps)");
            return;
        }
        var j2 = Mem.JournalEnd();
        var bytes2 = Mem.JournalBytes(j2);
        uint f0b = Cpu.W(0); ulong f0bfull = Cpu.Fpr[0];
        Mem.JournalUndo(j2);
        RestoreCpu();

        // 3. compare
        tested++;
        var diffs = new List<string>();
        var keys = new HashSet<uint>(bytes1!.Keys); keys.UnionWith(bytes2.Keys);
        foreach (var k in keys.OrderBy(x => x))
        {
            if (k >= 0x7E000000 && k < 0x80000000) continue;                                  // guest stacks and temp rings differ by design
            byte init = bytes1.TryGetValue(k, out var b1) ? b1.initial : bytes2[k].initial;
            byte x = bytes1.TryGetValue(k, out var u1) ? u1.final : init, y = bytes2.TryGetValue(k, out var u2) ? u2.final : init;
            if (x != y) diffs.Add($"{k:X8}: original {x:X2} translation {y:X2} (was {init:X2})");
        }
        // the result register is only informative: functions recompiled from MIPS have no prototype, so their declared result type is a guess
        bool retDiffers = ret != "void" && (ret is "float" ? f0a != f0b : ret is "double" ? f0full != f0bfull : v0a != v0b);
        if (diffs.Count == 0)
        {
            passed++;
            if (retDiffers && Environment.GetEnvironmentVariable("TRW_DIFF_RET") != null)
                Console.Error.WriteLine($"[diff] result register differs (memory identical) {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8}): original v0={v0a:X8} f0={f0a:X8}, translation v0={v0b:X8} f0={f0b:X8} (declared {ret})");
            return;
        }
        failed++;
        Console.Error.WriteLine($"[diff] MISMATCH {name}({a0:X8}, {a1:X8}, {a2:X8}, {a3:X8}) -> original v0={v0a:X8} f0={f0a:X8}, translation v0={v0b:X8} f0={f0b:X8} (ret {ret}); {diffs.Count} bytes differ ({interp.Steps} MIPS steps){(interp.ExternalCalls.Count > 0 ? ", externals " + string.Join(",", interp.ExternalCalls.Distinct()) : "")}");
        foreach (var d in diffs.Take(8)) Console.Error.WriteLine($"[diff]    {d}");
    }
}
