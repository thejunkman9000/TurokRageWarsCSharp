using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Trw.Transpile;

// usage: trw-transpile --root <c-repo> --out <file.cs> [--syms symbol_addrs.txt] [--sigs-in f] [--sigs-out f] [--report] <file.c | @list.txt> ...
bool partial = false; string? overrideDir = null; string? astCache = null; string? usedOut = null, defsOut = null, calledOut = null, mipsOut = null; var excludePaths = new List<string>(); string? excludePath = null, root = null, outPath = null, outDir = null, symsPath = null, sigsIn = null, sigsOut = null;
bool report = false;
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--out-dir": outDir = args[++i]; break;
        case "--exclude": excludePaths.Add(args[++i]); break;
        case "--used-out": usedOut = args[++i]; break;
        case "--called-out": calledOut = args[++i]; break;
        case "--mips-out": mipsOut = args[++i]; break;
        case "--defs-out": defsOut = args[++i]; break;
        case "--partial": partial = true; break;
        case "--ast-cache": astCache = args[++i]; break;
        case "--override-dir": overrideDir = args[++i]; break;
        case "--syms": symsPath = args[++i]; break;
        case "--sigs-in": sigsIn = args[++i]; break;
        case "--sigs-out": sigsOut = args[++i]; break;
        case "--report": report = true; break;
        default:
            if (args[i].StartsWith('@')) inputs.AddRange(File.ReadAllLines(args[i][1..]).Where(l => l.Trim() != ""));
            else inputs.Add(args[i]);
            break;
    }
}
if (root == null || inputs.Count == 0) { Console.Error.WriteLine("usage: --root <c-repo> --out <file.cs> [--syms file] [--sigs-in f] [--sigs-out f] [--report] <file.c|@list>..."); return 2; }

var exclude = excludePaths.Where(File.Exists).SelectMany(File.ReadLines).Select(l => l.Split('|')[0].Trim()).Where(l => l != "" && !l.StartsWith("#")).ToHashSet();
var overrideNames = new HashSet<string>();
if (overrideDir != null && Directory.Exists(overrideDir))
    foreach (var of in Directory.GetFiles(overrideDir, "*.c")) overrideNames.Add(Path.GetFileNameWithoutExtension(of));
// `f32 b08_sqrtf(f32) __asm__("sqrtf");` in headers: C name -> linker symbol
var headerAsmLabels = new Dictionary<string, string>();
if (Directory.Exists(Path.Combine(root, "include")))
    foreach (var h in Directory.GetFiles(Path.Combine(root, "include"), "*.h"))
        foreach (Match am in Regex.Matches(File.ReadAllText(h), @"(\w+)\s*\([^()]*\)\s*(?:__asm__|__asm|asm)\s*\(\s*""(\w+)""\s*\)\s*;"))
            headerAsmLabels[am.Groups[1].Value] = am.Groups[2].Value;
// the AST cache is valid while neither the .c nor any header has changed
DateTime headerStamp = DateTime.MinValue;
if (astCache != null)
{
    Directory.CreateDirectory(astCache);
    foreach (var h in Directory.EnumerateFiles(root, "*.h", SearchOption.AllDirectories).Where(f => !f.Contains("\\.venv\\") && !f.Contains("\\build\\")))
    {
        var t = File.GetLastWriteTimeUtc(h);
        if (t > headerStamp) headerStamp = t;
    }
}
var symbols = new Dictionary<string, uint>();
if (symsPath != null)
    foreach (var line in File.ReadLines(symsPath))
    {
        var m = Regex.Match(line, @"^\s*(\w+)\s*=\s*0x([0-9A-Fa-f]+)");
        if (m.Success) symbols[m.Groups[1].Value] = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
    }
uint? Lookup(string name)
{
    name = Emitter.Norm(name);
    var m = Regex.Match(name, @"^(?:D|func)_([0-9A-Fa-f]{8})[a-z]?$|^D_([0-9A-Fa-f]{1,7})$");
    if (m.Success && !m.Groups[1].Success) return uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
    if (m.Success) return uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
    return symbols.TryGetValue(name, out var a) ? a : null;
}

var sigs = new Dictionary<string, Sig>();
if (sigsIn != null && File.Exists(sigsIn))
    foreach (var line in File.ReadLines(sigsIn))
    {
        var p = line.Split('|');
        sigs[p[0]] = new Sig(p[1], p[2] == "" ? Array.Empty<string>() : p[2].Split(','));
    }

// one result per input file, filled in parallel and assembled in input order
var results = new FileResult[inputs.Count];
Parallel.For(0, inputs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) }, idx =>
{
    var file = inputs[idx];
    var res = results[idx] = new FileResult();
    var path = Path.IsPathRooted(file) ? file : Path.Combine(root, file);
    JsonNode ast;
    try { ast = ParseAst(root, path, astCache, headerStamp); }
    catch (Exception ex) { res.Fail("clang: " + ex.Message.Split('\n')[0], file); return; }
    var ix = new AstIndex(ast);
    foreach (var w in ix.Warnings) res.Warnings.Add(w);
    if (Environment.GetEnvironmentVariable("TRW_DUMP_LAYOUTS") is string dumpFile)
        lock (typeof(Program)) File.AppendAllLines(dumpFile, new[] { "# file " + file }.Concat(ix.DumpLayouts()));
    foreach (var kv in headerAsmLabels) ix.AsmLabels[kv.Key] = kv.Value;
    foreach (Match am in Regex.Matches(File.ReadAllText(path), @"(\w+)\s*\([^()]*\)\s*(?:__asm__|__asm|asm)\s*\(\s*""(\w+)""\s*\)\s*;"))
        ix.AsmLabels[am.Groups[1].Value] = am.Groups[2].Value;
    var em = new Emitter(ix, Lookup, sigs) { StaticPrefix = Path.GetFileNameWithoutExtension(path) + "_", SourceText = File.ReadAllText(path) };
    var mainName = Path.GetFileName(path);
    bool isOverrideFile = overrideDir != null && Path.GetFullPath(Path.GetDirectoryName(path)!).Equals(Path.GetFullPath(overrideDir), StringComparison.OrdinalIgnoreCase);
    string curFile = "";
    var emitted = new HashSet<string>();
    void EmitOne(JsonNode n)
    {
        var name = em.FnName(n);
        if (!emitted.Add(name)) return;
        // a hand-checked override (tools/overrides/<function>.c) replaces the function from the decompilation
        if (!isOverrideFile && overrideNames.Contains(name) && !ix.StaticFunctions.Contains((string)n["name"]!)) { try { res.Sigs.Add((name, em.ToSig(n))); } catch (Exception) { } res.Defs.Add(name); return; }
        if (!ix.StaticFunctions.Contains((string)n["name"]!)) res.Defs.Add(name);
        if (exclude.Contains(name))       // implemented by hand in src/Trw.Game/Os: keep the signature for call sites, emit nothing
        {
            try { res.Sigs.Add((name, em.ToSig(n))); } catch (Exception) { }
            return;
        }
        try
        {
            res.Text.Append(em.EmitFunction(n)).Append('\n');
            res.Ok++;
            var sig = em.ToSig(n);
            res.Sigs.Add((name, sig));
            // file-static functions are registered too when the symbol table knows their address (callbacks stored in tables or passed as pointers)
            if (Lookup(ix.StaticFunctions.Contains((string)n["name"]!) ? (string)n["name"]! : name) is uint addr)
                res.Registry.Add((addr, name, sig));
        }
        catch (Exception u) when (u is Unsupported or ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            var why = u is Unsupported ? u.Message : "internal: " + u.GetType().Name + " " + u.Message.Split('\n')[0] + (Environment.GetEnvironmentVariable("TRW_TRACE") != null ? "\n" + u.StackTrace : "");
            res.Fail(why, name);
            res.Text.Append($"    // TODO not transpiled: {name} ({why})\n\n");
            // a function the C cannot express: the exact MIPS code stands in for it (see regen.ps1)
            if (!ix.StaticFunctions.Contains((string)n["name"]!))
            {
                res.NeedsMips.Add(name);
                try { res.Sigs.Add((name, em.ToSig(n))); } catch (Exception) { }
            }
        }
    }
    foreach (var n in ast["inner"]!.AsArray())
    {
        // clang prints "file" only when it changes between consecutive nodes; keep only definitions that live in the .c itself
        var loc = n?["loc"];
        var f = (string?)loc?["file"] ?? (string?)loc?["spellingLoc"]?["file"];
        if (f != null) curFile = f;
        if (Path.GetFileName(curFile) != mainName) continue;
        if ((string?)n?["kind"] != "FunctionDecl" || n["inner"]?.AsArray().Any(c => (string?)c?["kind"] == "CompoundStmt") != true) continue;
        EmitOne(n);
    }
    // prototypes of functions defined elsewhere (asm, other files): their signatures let call sites coerce arguments
    foreach (var n in ast["inner"]!.AsArray())
    {
        if ((string?)n?["kind"] != "FunctionDecl") continue;
        var pname = (string)n["name"]!;
        if (ix.StaticFunctions.Contains(pname) || n["inner"]?.AsArray().Any(c => (string?)c?["kind"] == "CompoundStmt") == true) continue;
        if (((string?)n["type"]?["qualType"] ?? "").EndsWith("()")) continue;       // unprototyped: the argument list says nothing
        if (((string?)n["type"]?["qualType"] ?? "").Contains("...")) continue;
        try { res.Protos[pname] = em.ToSig(n); } catch (Exception ex) { if (Environment.GetEnvironmentVariable("TRW_TRACE") != null) Console.Error.WriteLine($"proto {pname}: {ex.Message}"); }
    }
    // static (inline) helpers defined in headers are emitted next to their first user, repeatedly until nothing new is referenced
    for (bool more = true; more;)
    {
        more = false;
        foreach (var sname in em.ReferencedStatics.ToList())
            if (ix.StaticDefinitions.TryGetValue(sname, out var def) && !emitted.Contains(em.FnName(def))) { EmitOne(def); more = true; }
    }
    foreach (var u in em.ValueUsed) res.Used.Add(u);
    foreach (var c in em.Called) res.Called.Add(c);
});

var outText = new StringBuilder();
outText.Append("// <auto-generated> by Trw.Transpile from the decompiled C. Edit freely: this file is the source now. </auto-generated>\n");
outText.Append("using Trw.Platform;\n\nnamespace Trw.Game;\n\npublic static partial class Game\n{\n");
int ok = 0;
var failures = new Dictionary<string, List<string>>();
var registry = new List<(uint addr, string name, Sig sig)>();
var allSigs = new Dictionary<string, Sig>();
foreach (var r in results)
{
    outText.Append(r.Text);
    ok += r.Ok;
    foreach (var (why, who) in r.Failures)
    {
        if (!failures.TryGetValue(why, out var l)) failures[why] = l = new();
        l.AddRange(who);
    }
    registry.AddRange(r.Registry);
    foreach (var (n, s) in r.Sigs) allSigs[n] = s;
}
foreach (var r in results) foreach (var (n, s) in r.Protos) allSigs.TryAdd(n, s);     // definitions win over prototypes
outText.Append("    /// <summary>Registers every translated function by its original code address, as a register-convention adapter (used by function pointers).</summary>\n");
const int chunk = 150;
var reglist = registry.OrderBy(x => x.addr).ToList();
outText.Append("    public static void RegisterFunctions()\n    {\n");
for (int c = 0; c * chunk < reglist.Count; c++) outText.Append($"        RegisterFunctions{c}();\n");
outText.Append("    }\n\n");
for (int c = 0; c * chunk < reglist.Count; c++)
{
    outText.Append($"    private static void RegisterFunctions{c}()\n    {{\n");
    foreach (var (addr, name, sig) in reglist.Skip(c * chunk).Take(chunk))
    {
        outText.Append($"        Funcs.Add(0x{addr:X8}u, \"{name}\", (a0, a1, a2, a3) =>\n        {{\n");
        outText.Append(Abi.AdapterBody(sig, name, "            "));
        outText.Append("        });\n");
    }
    outText.Append("    }\n\n");
}
outText.Append("}\n");
if (outPath != null) File.WriteAllText(outPath, outText.ToString());
if (outDir != null)
{
    // one C# file per C file, same stem, so the layout mirrors the decompilation and diffs stay small
    Directory.CreateDirectory(outDir);
    const string head = "// <auto-generated> by Trw.Transpile from the decompiled C. Edit freely: this file is the source now. </auto-generated>\nusing Trw.Platform;\n\nnamespace Trw.Game;\n\npublic static partial class Game\n{\n";
    for (int i = 0; i < inputs.Count; i++)
    {
        if (results[i].Text.Length == 0) continue;
        File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(inputs[i]) + ".cs"), head + results[i].Text + "}\n");
    }
    var reg = outText.ToString();
    File.WriteAllText(Path.Combine(outDir, "_Registry.cs"), head + reg[reg.IndexOf("    /// <summary>Registers every", StringComparison.Ordinal)..]);
}
if (calledOut != null) File.WriteAllLines(calledOut, results.SelectMany(r => r.Called).Distinct().OrderBy(x => x));
if (mipsOut != null) File.WriteAllLines(mipsOut, results.SelectMany(r => r.NeedsMips).Distinct().OrderBy(x => x));
if (defsOut != null) File.WriteAllLines(defsOut, results.SelectMany(r => r.Defs).Distinct().OrderBy(x => x));
if (usedOut != null) File.WriteAllLines(usedOut, results.SelectMany(r => r.Used).Distinct().OrderBy(x => x.name).Select(x => $"{x.name}|{x.ret}"));
if (sigsOut != null) File.WriteAllLines(sigsOut, allSigs.OrderBy(k => k.Key).Select(kv => kv.Value.Serialize(kv.Key)));
foreach (var w in results.SelectMany(r => r.Warnings).Distinct().OrderBy(x => x)) Console.Error.WriteLine("layout warning: " + w);
Console.WriteLine($"transpiled {ok} functions, {failures.Values.Sum(v => v.Count)} failures");
if (report)
    foreach (var (why, who) in failures.OrderByDescending(kv => kv.Value.Count))
        Console.WriteLine($"  {who.Count,5}  {why}   e.g. {string.Join(", ", who.Take(3))}");
return 0;

static JsonNode ParseAst(string root, string cfile, string? cacheDir, DateTime headerStamp)
{
    string? cachePath = null;
    if (cacheDir != null)
    {
        cachePath = Path.Combine(cacheDir, Path.GetRelativePath(root, cfile).Replace('\\', '_').Replace('/', '_') + ".json.gz");
        var need = File.GetLastWriteTimeUtc(cfile) > headerStamp ? File.GetLastWriteTimeUtc(cfile) : headerStamp;
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= need)
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(cachePath), System.IO.Compression.CompressionMode.Decompress);
            return JsonNode.Parse(gz, null, new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 })!;
        }
    }
    var psi = new ProcessStartInfo("python")
    {
        WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var a in new[] { "-m", "ziglang", "cc", "-target", "mips-linux-gnueabi", "-fsyntax-only", "-w", "-std=gnu89",
                 "-D_LANGUAGE_C", "-DTARGET_PC", "-DM2CTX", "-Iinclude", "-Xclang", "-ast-dump=json", cfile })
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    var json = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (json.Length == 0) throw new Exception(err.Result.Trim());
    if (cachePath != null)
    {
        var tmp = cachePath + ".tmp" + Environment.CurrentManagedThreadId;
        using (var gz = new System.IO.Compression.GZipStream(File.Create(tmp), System.IO.Compression.CompressionLevel.Fastest))
        {
            var b = Encoding.UTF8.GetBytes(json);
            gz.Write(b, 0, b.Length);
        }
        File.Move(tmp, cachePath, true);
    }
    return JsonNode.Parse(json, null, new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 })!;
}

sealed class FileResult
{
    public StringBuilder Text = new();
    public int Ok;
    public List<(string why, List<string> who)> Failures = new();
    public List<(string, Sig)> Sigs = new();
    public List<(uint, string, Sig)> Registry = new();
    public Dictionary<string, Sig> Protos = new();
    public List<string> Defs = new();
    public List<string> Warnings = new();
    public HashSet<string> Called = new();
    public List<string> NeedsMips = new();
    public List<(string name, string ret)> Used = new();
    public void Fail(string why, string who)
    {
        var e = Failures.FirstOrDefault(f => f.why == why);
        if (e.who == null) Failures.Add((why, new List<string> { who }));
        else e.who.Add(who);
    }
}














