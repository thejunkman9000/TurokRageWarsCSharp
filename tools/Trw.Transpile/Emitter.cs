using System.Text;
using System.Text.Json.Nodes;

namespace Trw.Transpile;

public sealed class Unsupported(string what) : Exception(what) { }

/// <summary>
/// Turns one C function (clang JSON AST, MIPS o32 target) into a C# static method. All game memory goes through
/// <c>Mem</c>; scalar locals are C# locals; pointers are <c>uint</c> guest addresses. Locals that need an address
/// (structs, arrays, anything whose address is taken) live in a guest stack frame (<c>Stack.Alloc</c>).
/// </summary>
public sealed partial class Emitter
{
    readonly AstIndex ix;
    readonly Func<string, uint?> symbolAddress;
    readonly IReadOnlyDictionary<string, Sig> sigs;
    readonly StringBuilder sb = new();
    int indent;

    /// <summary>File-local (static) functions are renamed `file_name` so equal names in different files cannot collide.</summary>
    public string StaticPrefix = "";

    // per-function state
    Dictionary<string, int> frame = new();   // VarDecl/ParmVarDecl id -> offset in the guest stack frame
    int frameSize;
    Dictionary<string, string> labels = new();
    HashSet<string> localIds = new();
    Dictionary<JsonNode, int> callTemps = new(ReferenceEqualityComparer.Instance);
    CType? returnsRecord;                      // set while emitting a function that returns a struct by value
    bool lastTerminates;
    bool noRetCast;
    /// <summary>(callee, C# type) pairs for direct calls whose result is used; a callee defined as void but used with a value needs the original MIPS code.</summary>
    public readonly HashSet<(string name, string ret)> ValueUsed = new();
    /// <summary>Every function this file calls directly (so the generator can stub the ones nobody defines).</summary>
    public readonly HashSet<string> Called = new();

    /// <summary>name -> how many of a0-a3 the ROM code reads (tools/arity.py); empty when no table was given.</summary>
    readonly IReadOnlyDictionary<string, int> romWords;

    // per-function state of the register forwarding (see LeftInRegister)
    List<(string name, int word)> fwdParams = new();   // scalar parameters that still hold their incoming register value, and the word they arrived in
    HashSet<string> writtenVars = new();                // variables assigned somewhere in the function
    int callsEmitted;                                   // calls rendered so far: any earlier call may have clobbered a0-a3
    int loopDepth;                                      // >0 while emitting a loop (its calls repeat)
    bool flatMode, curIsStatic;
    string[] dbgWords = new[] { "0u", "0u", "0u", "0u" };

    public Emitter(AstIndex ix, Func<string, uint?> symbolAddress, IReadOnlyDictionary<string, Sig> sigs, IReadOnlyDictionary<string, int>? romWords = null)
    {
        this.ix = ix; this.symbolAddress = symbolAddress; this.sigs = sigs;
        this.romWords = romWords ?? new Dictionary<string, int>();
    }

    static readonly HashSet<string> Keywords = new()
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate",
        "do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in",
        "int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private",
        "protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
        "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };
    static string Id(string name) => Keywords.Contains(name) ? "@" + name : name;

    static string KindOf(JsonNode? n) => (string?)n?["kind"] ?? "";
    JsonNode Inner(JsonNode n, int i)
    {
        var a = n["inner"]?.AsArray();
        if (a == null || i >= a.Count || a[i] == null) throw new Unsupported($"missing child {i} of {KindOf(n)}");
        return a[i]!;
    }
    static int InnerCount(JsonNode n) => n["inner"]?.AsArray().Count ?? 0;
    static IEnumerable<JsonNode> Kids(JsonNode n) => n["inner"]?.AsArray().Where(x => x != null).Select(x => x!) ?? Enumerable.Empty<JsonNode>();

    CType TypeOf(JsonNode e)
    {
        var t = e["type"] ?? throw new Unsupported($"no type on {KindOf(e)}");
        var s = (string?)t["desugaredQualType"] ?? (string?)t["qualType"] ?? "";
        return CType.Parse(s, ix);
    }

    // ------------------------------------------------------------------ function

    /// <summary>Static functions referenced while emitting (so header-defined ones can be emitted next to their user).</summary>
    public readonly HashSet<string> ReferencedStatics = new();

    /// <summary>Overlay functions appear as func_4009F4 in some sources and func_004009F4 in others; use the long form.</summary>
    /// <summary>Names the other decompilation gave to functions of this ROM (func_802A1898_de) -> the name used here (see Program.cs).</summary>
    public static readonly Dictionary<string, string> Aliases = new();

    public static string Norm(string n)
    {
        if (Aliases.TryGetValue(n, out var alias)) return alias;
        if (n.Length == 11 && n.StartsWith("func_")) return "func_00" + n[5..];
        // func_802BC380 is the KSEG0 (cached) alias of the code at 0x002BC380
        if (n.Length == 13 && n.StartsWith("func_") && "89ABab".Contains(n[5]) && uint.TryParse(n[5..], System.Globalization.NumberStyles.HexNumber, null, out var a))
            return $"func_{a & 0x1FFFFFFF:X8}";
        return n;
    }

    public string FnName(JsonNode fn)
    {
        var n = (string)fn["name"]!;
        if (ix.AsmLabels.TryGetValue(n, out var label)) return Norm(label);
        if (!ix.StaticFunctions.Contains(n)) return Norm(n);
        ReferencedStatics.Add(n);
        return StaticPrefix + n;
    }

    /// <summary>
    /// The C# view of a function: a struct result becomes a hidden leading <c>uint</c> parameter, struct parameters become addresses, and the
    /// argument registers the ROM code reads beyond what the C prototype declares become trailing <c>uint</c> parameters (see <see cref="Extend"/>).
    /// </summary>
    public Sig ToSig(JsonNode fn) => Extend(FnName(fn), BaseSig(fn));

    Sig BaseSig(JsonNode fn)
    {
        var (ret, ps) = SignatureOf(fn);
        string Cs(CType t) => t.Kind == K.Record ? "uint" : t.Cs;
        var list = ps.Select(Cs).ToList();
        var structs = ps.Select(p => p.Kind == K.Record ? p.Size : 0).ToList();
        if (ret.Kind == K.Record) { list.Insert(0, "uint"); structs.Insert(0, 0); }
        return new Sig(ret.Kind == K.Void ? "void" : Cs(ret), list.ToArray(), structs.ToArray(), ret.Kind == K.Record);
    }

    /// <summary>
    /// The decompiled C prototype can be shorter than the machine code: a <c>void f(void)</c> stub that is only <c>jal g</c> forwards its caller's
    /// a0..a3 to g, and a stale prototype omits trailing arguments the body reads. The ROM listing says how many argument words the code reads
    /// (tools/arity.py); the missing ones become trailing <c>uint</c> parameters so callers pass them and the body can forward them.
    /// </summary>
    Sig Extend(string name, Sig s)
    {
        if (!romWords.TryGetValue(name, out var rw)) return s;
        var (_, have) = Abi.Layout(s.Params, s.Structs);
        rw = Math.Min(rw, 4);
        if (rw <= have) return s;
        var ps = s.Params.ToList(); var st = s.Structs.ToList();
        for (int w = have; w < rw; w++) { ps.Add("uint"); st.Add(0); }
        return s with { Params = ps.ToArray(), Structs = st.ToArray() };
    }
    public (CType ret, List<CType> ps) SignatureOf(JsonNode fn)
    {
        var ft = CType.Parse((string?)fn["type"]?["desugaredQualType"] ?? (string)fn["type"]!["qualType"]!, ix);
        var ps = Kids(fn).Where(x => KindOf(x) == "ParmVarDecl").Select(TypeOf).ToList();
        return (ft.Ret ?? CType.S32, ps);
    }

    /// <summary>
    /// <c>void f(void) { fp(); }</c> is how the decompiler shows a MIPS stub that is only <c>lw t9, fp; jr t9</c>: the real callers pass a0..a3 and the
    /// target receives them untouched. A C prototype cannot say that, so such stubs are recompiled from the MIPS (which forwards r4..r7).
    /// </summary>
    static bool IsRegisterForwardingWrapper(JsonNode body)
    {
        var stmts = Kids(body).ToList();
        if (stmts.Count != 1) return false;
        var s = stmts[0];
        if (KindOf(s) == "ReturnStmt") s = Kids(s).FirstOrDefault();
        while (s != null && KindOf(s) is "ImplicitCastExpr" or "ParenExpr") s = Kids(s).FirstOrDefault();
        if (s == null || KindOf(s) != "CallExpr") return false;
        var parts = Kids(s).ToList();
        if (parts.Count != 1) return false;                       // arguments present: the C already says what is passed
        var callee = parts[0];
        while (KindOf(callee) is "ImplicitCastExpr" or "ParenExpr" or "UnaryOperator" && Kids(callee).FirstOrDefault() is JsonNode inner) callee = inner;
        return !(KindOf(callee) == "DeclRefExpr" && (string?)callee["referencedDecl"]?["kind"] == "FunctionDecl");
    }

    public string EmitFunction(JsonNode fn)
    {
        sb.Clear(); indent = 1; frame = new(); renames = new(); frameSize = 0; labels = new(); lastTerminates = false;
        localIds = new(); callTemps = new(ReferenceEqualityComparer.Instance);
        var name = FnName(fn);
        var (ret, _) = SignatureOf(fn);
        var body = Kids(fn).First(x => KindOf(x) == "CompoundStmt");
        var ps = Kids(fn).Where(x => KindOf(x) == "ParmVarDecl").ToList();
        if (ps.Count == 0 && IsRegisterForwardingWrapper(body))
            throw new Unsupported("tail-jump wrapper (jr t9): forwards the caller's argument registers, which the C prototype cannot express");
        foreach (var p in ps) localIds.Add((string)p["id"]!);
        returnsRecord = ret.Kind == K.Record ? ret : null;
        writtenVars = new(); fwdParams = new(); callsEmitted = 0; loopDepth = 0; flatMode = false;
        curIsStatic = ix.StaticFunctions.Contains((string)fn["name"]!);

        PlanFrame(ps, body);

        var plist = new List<string>();
        var prologue = new List<string>();
        if (returnsRecord != null) plist.Add("uint _ret");   // o32 hidden pointer: the result is written to the caller's buffer
        foreach (var p in ps)
        {
            var pt = TypeOf(p);
            var pname = Id((string)p["name"]!);
            if (pt.Kind == K.Record)
            {
                // struct by value: the caller passes the address of its copy; take a private copy so writes stay local
                plist.Add($"uint {pname}_in");
                prologue.Add($"Mem.Copy(_fp + {frame[(string)p["id"]!]}u, {pname}_in, {pt.Size});");
            }
            else if (frame.TryGetValue((string)p["id"]!, out var off))
            {
                plist.Add($"{pt.Cs} {pname}_in");
                prologue.Add($"Mem.Set(_fp + {off}u, {pname}_in);");
            }
            else plist.Add($"{pt.Cs} {pname}");
        }
        {
            // argument registers the ROM code reads beyond the C prototype become hidden trailing parameters (see Extend); the parameters that still hold
            // their incoming value can be forwarded into calls that omit an argument (see LeftInRegister)
            var baseSig = BaseSig(fn);
            var (_, declaredWords) = Abi.Layout(baseSig.Params, baseSig.Structs);
            var full = Extend(name, baseSig);
            var (first, _2) = Abi.Layout(full.Params, full.Structs);
            int j = returnsRecord != null ? 1 : 0;
            dbgWords = new[] { "0u", "0u", "0u", "0u" };       // the argument registers a0..a3 as the trace and the differential tests see them
            foreach (var p in ps)
            {
                var pt = TypeOf(p);
                var id = (string)p["id"]!;
                if (pt.IsScalarMem && !Abi.IsFp(pt.Cs) && !Abi.IsWide(pt.Cs) && !frame.ContainsKey(id) && !writtenVars.Contains(id) && j < first.Length && first[j] >= 0 && first[j] < 4)
                    fwdParams.Add((Id((string)p["name"]!), first[j]));
                if ((pt.Kind == K.Record || (pt.Kind is K.Int or K.Ptr && !Abi.IsWide(pt.Cs))) && j < first.Length && first[j] >= 0 && first[j] < 4)
                {
                    var pn = Id((string)p["name"]!);
                    dbgWords[first[j]] = $"(uint){(pt.Kind == K.Record || frame.ContainsKey(id) ? pn + "_in" : pn)}";
                }
                j++;
            }
            for (int e = 0; declaredWords + e < Math.Min(romWords.GetValueOrDefault(name), 4); e++)
            {
                plist.Add($"uint _a{declaredWords + e}");
                fwdParams.Add(($"_a{declaredWords + e}", declaredWords + e));
                dbgWords[declaredWords + e] = $"_a{declaredWords + e}";
            }
        }

        string retCs = ret.Kind == K.Void ? "void" : ret.Kind == K.Record ? "uint" : ret.Cs;
        Line($"public static {retCs} {name}({string.Join(", ", plist)})");
        Line("{");
        indent++;
        if (frameSize > 0)
        {
            Line($"uint _fp = Stack.Alloc({frameSize});");
            Line("try");
            Line("{");
            indent++;
        }
        Line("unchecked");
        Line("{");
        indent++;
        foreach (var l in prologue) Line(l);
        {
            // opt-in call tracing / differential tests (TRW_TRACE_FN, TRW_DIFF): the argument registers a0..a3 as words
            int lastWord = 3;
            while (lastWord >= 0 && dbgWords[lastWord] == "0u") lastWord--;
            Line($"if (Dbg.On) Dbg.Enter(\"{name}\"{(lastWord >= 0 ? ", " + string.Join(", ", dbgWords.Take(lastWord + 1)) : "")});");
        }
        if (NeedsFlat(body))
        {
            // the C jumps into the middle of a block (or has case labels inside nested blocks): C# cannot, so lower the whole function to labels and gotos
            FlatBody(body, ps);
            if (ret.Kind == K.Record) Line("return _ret;");
            else if (ret.Kind != K.Void) Line($"return {ret.Zero};");
        }
        else
        {
            foreach (var s in body["inner"]?.AsArray() ?? new JsonArray()) Stmt(s!);
            if (ret.Kind == K.Record && !EndsWithReturn(body)) Line("return _ret;");
            else if (ret.Kind is not (K.Void or K.Record) && !EndsWithReturn(body)) Line($"return {ret.Zero};");
        }
        indent--;
        Line("}");
        if (frameSize > 0)
        {
            indent--;
            Line("}");
            Line($"finally {{ Stack.Free({frameSize}); }}");
        }
        indent--;
        Line("}");
        return sb.ToString();
    }

    static bool EndsWithReturn(JsonNode body) => KindOf(body["inner"]?.AsArray().LastOrDefault()) == "ReturnStmt";

    // ------------------------------------------------------------------ flat mode (labels and gotos only)

    Dictionary<string, string> renames = new();     // local variable id -> unique C# name (flat mode hoists every local to the top)
    int flatLabel;
    readonly Stack<string> breakTargets = new();
    readonly Stack<string> continueTargets = new();
    Dictionary<JsonNode, string> caseLabels = new(ReferenceEqualityComparer.Instance);

    /// <summary>Does the function contain a goto into a block that does not enclose it, or a case label that is not directly in its switch block?</summary>
    bool NeedsFlat(JsonNode body)
    {
        var labelBlock = new Dictionary<string, JsonNode>();
        var gotos = new List<(string target, List<JsonNode> chain)>();
        var stack = new List<JsonNode>();
        bool oddCase = false;
        void Walk(JsonNode n)
        {
            var k = KindOf(n);
            if (k == "LabelStmt")
                labelBlock[(string)n["declId"]!] = stack.LastOrDefault(x => KindOf(x) == "CompoundStmt") ?? body;
            else if (k == "GotoStmt") gotos.Add(((string)n["targetLabelDeclId"]!, stack.ToList()));
            else if (k is "CaseStmt" or "DefaultStmt")
            {
                var parent = stack.LastOrDefault();
                var grand = stack.Count >= 2 ? stack[^2] : null;
                bool ok = parent != null && ((KindOf(parent) == "CompoundStmt" && KindOf(grand) == "SwitchStmt") || KindOf(parent) is "CaseStmt" or "DefaultStmt");
                if (!ok) oddCase = true;
            }
            stack.Add(n);
            foreach (var c in Kids(n)) Walk(c);
            stack.RemoveAt(stack.Count - 1);
        }
        Walk(body);
        if (oddCase) return true;
        return gotos.Any(g => labelBlock.TryGetValue(g.target, out var blk) && !g.chain.Contains(blk));
    }

    string NewFlatLabel() => $"_f{flatLabel++}";

    void FlatBody(JsonNode body, List<JsonNode> ps)
    {
        flatLabel = 0; renames = new(); breakTargets.Clear(); continueTargets.Clear(); caseLabels = new(ReferenceEqualityComparer.Instance);
        flatMode = true;       // gotos and loops expressed as labels: the order of calls in the text says nothing about the order they run in
        var used = new HashSet<string>(ps.Select(p => (string)p["name"]!));
        // hoist the scalar locals that do not live in the guest frame
        void Hoist(JsonNode n)
        {
            if (KindOf(n) == "VarDecl" && !frame.ContainsKey((string)n["id"]!) && (string?)n["storageClass"] != "static")
            {
                var t = TypeOf(n);
                if (!t.IsScalarMem) throw new Unsupported($"local of type {t.Kind} {t.Name}");
                var nm = (string)n["name"]!;
                var uniq = nm; int k = 2;
                while (!used.Add(uniq)) uniq = $"{nm}_{k++}";
                renames[(string)n["id"]!] = Id(uniq);
                Line($"{t.Cs} {Id(uniq)} = {t.Zero};");
            }
            foreach (var c in Kids(n)) Hoist(c);
        }
        Hoist(body);
        foreach (var s in body["inner"]?.AsArray() ?? new JsonArray()) FlatStmt(s!);
    }

    string FlatDecl(JsonNode d)
    {
        if (KindOf(d) != "VarDecl") throw new Unsupported($"local {KindOf(d)}");
        if (frame.ContainsKey((string)d["id"]!)) return LocalDecl(d);       // frame locals: the initialiser is a store
        if (InnerCount(d) == 0) return "";
        return $"{renames[(string)d["id"]!]} = {Value(Inner(d, 0))}";
    }

    void FlatStmt(JsonNode s)
    {
        switch (KindOf(s))
        {
            case "CompoundStmt": foreach (var c in Kids(s)) FlatStmt(c); break;
            case "NullStmt": case "<<<NULL>>>": break;
            case "DeclStmt":
                foreach (var d in Kids(s)) { var l = FlatDecl(d); if (l != "") Line(l + ";"); }
                break;
            case "ReturnStmt":
                if (returnsRecord != null && InnerCount(s) > 0)
                {
                    Line($"Mem.Copy(_ret, {Addr(StripValueCast(Inner(s, 0)))}, {returnsRecord.Size});");
                    Line("return _ret;");
                }
                else Line(InnerCount(s) == 0 ? "return;" : $"return {Value(Inner(s, 0))};");
                break;
            case "IfStmt":
            {
                var lelse = NewFlatLabel();
                Line($"if (!({Cond(Inner(s, 0))})) goto {lelse};");
                FlatStmt(Inner(s, 1));
                if (InnerCount(s) > 2)
                {
                    var lend = NewFlatLabel();
                    Line($"goto {lend};");
                    Line($"{lelse}: ;");
                    FlatStmt(Inner(s, 2));
                    Line($"{lend}: ;");
                }
                else Line($"{lelse}: ;");
                break;
            }
            case "WhileStmt":
            {
                var lc = NewFlatLabel(); var lb = NewFlatLabel();
                breakTargets.Push(lb); continueTargets.Push(lc);
                Line($"{lc}: ;");
                Line($"if (!({Cond(Inner(s, 0))})) goto {lb};");
                FlatStmt(Inner(s, 1));
                Line($"goto {lc};");
                Line($"{lb}: ;");
                breakTargets.Pop(); continueTargets.Pop();
                break;
            }
            case "DoStmt":
            {
                var lt = NewFlatLabel(); var lc = NewFlatLabel(); var lb = NewFlatLabel();
                breakTargets.Push(lb); continueTargets.Push(lc);
                Line($"{lt}: ;");
                FlatStmt(Inner(s, 0));
                Line($"{lc}: ;");
                Line($"if ({Cond(Inner(s, 1))}) goto {lt};");
                Line($"{lb}: ;");
                breakTargets.Pop(); continueTargets.Pop();
                break;
            }
            case "ForStmt":
            {
                var kids = s["inner"]!.AsArray().Select(x => x!).ToList();
                if (kids.Count != 5) throw new Unsupported("for layout");
                if (!IsNull(kids[0])) FlatStmt(KindOf(kids[0]) == "DeclStmt" ? kids[0] : WrapExpr(kids[0]));
                var lcond = NewFlatLabel(); var lc = NewFlatLabel(); var lb = NewFlatLabel();
                breakTargets.Push(lb); continueTargets.Push(lc);
                Line($"{lcond}: ;");
                if (!IsNull(kids[2])) Line($"if (!({Cond(kids[2])})) goto {lb};");
                FlatStmt(kids[4]);
                Line($"{lc}: ;");
                if (!IsNull(kids[3])) foreach (var part in ForParts(kids[3])) Line(part + ";");
                Line($"goto {lcond};");
                Line($"{lb}: ;");
                breakTargets.Pop(); continueTargets.Pop();
                break;
            }
            case "BreakStmt": Line($"goto {breakTargets.Peek()};"); break;
            case "ContinueStmt": Line($"goto {continueTargets.Peek()};"); break;
            case "GotoStmt": Line($"goto {labels[(string)s["targetLabelDeclId"]!]};"); break;
            case "LabelStmt":
                Line($"{labels[(string)s["declId"]!]}: ;");
                foreach (var c in Kids(s)) FlatStmt(c);
                break;
            case "SwitchStmt": FlatSwitch(s); break;
            case "ExprWrapper": Line(StmtExpr(Inner(s, 0)) + ";"); break;
            case "GCCAsmStmt": Line(AsmCopy(s) + ";"); break;
            case "CaseStmt":
                Line($"{caseLabels[s]}: ;");
                FlatStmt(s["inner"]!.AsArray().Last()!);
                break;
            case "DefaultStmt":
                Line($"{caseLabels[s]}: ;");
                FlatStmt(Inner(s, 0));
                break;
            default:
                Line(StmtExpr(s) + ";");
                break;
        }
    }

    /// <summary>An expression statement node (so it can go through FlatStmt).</summary>
    static JsonNode WrapExpr(JsonNode e) => new JsonObject { ["kind"] = "ExprWrapper", ["inner"] = new JsonArray(e.DeepClone()) };

    void FlatSwitch(JsonNode s)
    {
        var kids = Kids(s).ToList();
        var body = kids[^1];
        var cases = new List<JsonNode>();
        void Collect(JsonNode n)
        {
            if (KindOf(n) is "CaseStmt" or "DefaultStmt") cases.Add(n);
            if (KindOf(n) == "SwitchStmt") return;               // an inner switch owns its labels
            foreach (var c in Kids(n)) Collect(c);
        }
        foreach (var c in Kids(body)) Collect(c);
        if (KindOf(body) is "CaseStmt" or "DefaultStmt") Collect(body);
        var lb = NewFlatLabel();
        string? ldefault = null;
        var dispatch = new StringBuilder();
        foreach (var c in cases)
        {
            var l = NewFlatLabel();
            caseLabels[c] = l;
            if (KindOf(c) == "DefaultStmt") ldefault = l;
            else dispatch.Append($"case {Value(Inner(c, 0))}: goto {l}; ");
        }
        Line($"switch ({Value(kids[^2])}) {{ {dispatch}default: goto {ldefault ?? lb}; }}");
        breakTargets.Push(lb);
        FlatStmt(body);
        breakTargets.Pop();
        Line($"{lb}: ;");
    }

    /// <summary>Decides which locals live in the guest stack frame and collects goto labels.</summary>
    void PlanFrame(List<JsonNode> ps, JsonNode body)
    {
        var addrTaken = new HashSet<string>();
        var decls = new List<JsonNode>();
        void Scan(JsonNode n)
        {
            switch (KindOf(n))
            {
                case "UnaryOperator" when (string)n["opcode"]! == "&":
                {
                    var t = n["inner"]![0]!;
                    while (KindOf(t) is "ParenExpr") t = t["inner"]![0]!;
                    if (KindOf(t) == "DeclRefExpr") addrTaken.Add((string)t["referencedDecl"]!["id"]!);
                    break;
                }
                case "BinaryOperator" when (string)n["opcode"]! == "=":
                case "CompoundAssignOperator":
                case "UnaryOperator" when (string)n["opcode"]! is "++" or "--":
                {
                    var t = n["inner"]![0]!;
                    while (KindOf(t) is "ParenExpr" or "ImplicitCastExpr") t = t["inner"]![0]!;
                    if (KindOf(t) == "DeclRefExpr") writtenVars.Add((string)t["referencedDecl"]!["id"]!);
                    break;
                }
                case "VarDecl": decls.Add(n); localIds.Add((string)n["id"]!); break;
                case "CallExpr" when TypeOf(n).Kind == K.Record:
                {
                    var rt = TypeOf(n);
                    frameSize = (frameSize + 7) / 8 * 8;
                    callTemps[n] = frameSize;      // struct returned by value: the callee writes into this slot
                    frameSize += Math.Max(1, rt.Size);
                    break;
                }
                case "LabelStmt": labels[(string)n["declId"]!] = "L_" + (string)n["name"]!; break;
            }
            foreach (var c in Kids(n)) Scan(c);
        }
        Scan(body);
        // C scopes are finer than C#'s: a block-scoped local may reuse the name of a local of an enclosing scope (or one that comes later), which C# rejects
        // (CS0136). Every local gets a function-wide unique C# name.
        {
            var taken = new HashSet<string>(ps.Select(p => (string)p["name"]!));
            foreach (var d in decls)
            {
                var nm = (string)d["name"]!;
                var uniq = nm; int k = 2;
                while (!taken.Add(uniq)) uniq = $"{nm}_{k++}";
                if (uniq != nm) renames[(string)d["id"]!] = Id(uniq);
            }
        }

        void Place(JsonNode d)
        {
            var t = TypeOf(d);
            var al = t.Kind switch { K.Record => 8, K.Array => 8, _ => Math.Max(1, Math.Min(t.Size, 8)) };
            frameSize = (frameSize + al - 1) / al * al;
            frame[(string)d["id"]!] = frameSize;
            frameSize += Math.Max(1, t.Size);
        }
        foreach (var p in ps)
        {
            var t = TypeOf(p);
            if (t.Kind == K.Record || addrTaken.Contains((string)p["id"]!)) Place(p);
        }
        foreach (var d in decls)
        {
            if ((string?)d["storageClass"] == "static") continue;
            var t = TypeOf(d);
            if (t.Kind is K.Record or K.Array || addrTaken.Contains((string)d["id"]!)) Place(d);
        }
    }

    void Line(string s) => sb.Append(new string(' ', indent * 4)).Append(s).Append('\n');

    // ------------------------------------------------------------------ statements

    void Block(JsonNode s)
    {
        Line("{");
        indent++;
        if (KindOf(s) == "CompoundStmt") foreach (var c in s["inner"]?.AsArray() ?? new JsonArray()) Stmt(c!);
        else Stmt(s);
        indent--;
        Line("}");
    }

    void Stmt(JsonNode s)
    {
        StmtCore(s);
        if (KindOf(s) is not ("ReturnStmt" or "BreakStmt" or "ContinueStmt" or "GotoStmt" or "LabelStmt")) lastTerminates = false;
    }

    void StmtCore(JsonNode s)
    {
        lastTerminates = false;
        switch (KindOf(s))
        {
            case "CompoundStmt": Block(s); break;
            case "NullStmt": case "<<<NULL>>>": break;
            case "DeclStmt":
                foreach (var d in Kids(s))
                {
                    var l = LocalDecl(d);
                    if (l != "") Line(l + ";");
                }
                break;
            case "ReturnStmt":
                if (returnsRecord != null && InnerCount(s) > 0)
                {
                    Line($"Mem.Copy(_ret, {Addr(StripValueCast(Inner(s, 0)))}, {returnsRecord.Size});");
                    Line("return _ret;");
                }
                else Line(InnerCount(s) == 0 ? "return;" : $"return {Value(Inner(s, 0))};");
                lastTerminates = true;
                break;
            case "IfStmt":
                Line($"if ({Cond(Inner(s, 0))})");
                Block(Inner(s, 1));
                if (InnerCount(s) > 2) { Line("else"); Block(Inner(s, 2)); }
                break;
            case "WhileStmt":
                loopDepth++;
                Line($"while ({Cond(Inner(s, 0))})");
                Block(Inner(s, 1));
                loopDepth--;
                break;
            case "DoStmt":
                loopDepth++;
                Line("do");
                Block(Inner(s, 0));
                Line($"while ({Cond(Inner(s, 1))});");
                loopDepth--;
                break;
            case "ForStmt":
            {
                var kids = s["inner"]!.AsArray().Select(x => x!).ToList();   // init, (condVar), cond, inc, body; absent parts are "<<<NULL>>>"
                if (kids.Count != 5) throw new Unsupported("for layout");
                loopDepth++;
                string init = IsNull(kids[0]) ? "" : KindOf(kids[0]) == "DeclStmt"
                    ? DeclList(kids[0]) : ForList(kids[0]);
                string cond = IsNull(kids[2]) ? "" : Cond(kids[2]);
                string inc = IsNull(kids[3]) ? "" : ForList(kids[3]);
                Line($"for ({init}; {cond}; {inc})");
                Block(kids[4]);
                loopDepth--;
                break;
            }
            case "BreakStmt": Line("break;"); lastTerminates = true; break;
            case "ContinueStmt": Line("continue;"); lastTerminates = true; break;
            case "GotoStmt":
                Line($"goto {labels[(string)s["targetLabelDeclId"]!]};");
                lastTerminates = true;
                break;
            case "LabelStmt":
                Line($"{labels[(string)s["declId"]!]}: ;");
                foreach (var c in Kids(s)) Stmt(c);
                break;
            case "SwitchStmt": Switch(s); break;
            case "GCCAsmStmt": Line(AsmCopy(s) + ";"); break;
            case "CaseStmt": case "DefaultStmt":
                throw new Unsupported("case label outside the top level of a switch");
            default:
                Line(StmtExpr(s) + ";");
                break;
        }
    }

    /// <summary>The C source of the file being emitted (used for the few things the AST does not carry, like inline-asm templates).</summary>
    public string SourceText = "";

    /// <summary>
    /// `__asm__("" : "=r"(k) : "0"(value))` is the idiom for "an opaque copy of value" (the compiler must not fold it):
    /// translate it to the assignment k = value. Any other inline assembly is not supported.
    /// </summary>
    string AsmCopy(JsonNode s)
    {
        var kids = Kids(s).ToList();
        if (kids.Count != 2 || !SourceText.Contains("__asm__(\"\" :")) throw new Unsupported("inline assembly");
        var dst = kids[0];
        var val = Value(kids[1]);
        return IsLocalRef(dst) ? $"{Value(dst)} = {val}" : $"Mem.Set({Addr(dst)}, {val})";
    }

    static bool IsNull(JsonNode n) => KindOf(n) == "<<<NULL>>>" || n.AsObject().Count == 0;

    string DeclList(JsonNode declStmt)
    {
        var parts = Kids(declStmt).Select(LocalDecl).Where(x => x != "").ToList();
        if (parts.Count == 0) return "";
        if (parts.Any(p => !p.Contains(' '))) throw new Unsupported("for-init frame store");
        return string.Join(", ", parts);
    }

    /// <summary>A comma-expression flattened into a C# for-header list.</summary>
    string ForList(JsonNode e) => string.Join(", ", ForParts(e));

    List<string> ForParts(JsonNode e)
    {
        e = StripParens(e);
        if (KindOf(e) == "BinaryOperator" && (string)e["opcode"]! == ",")
        {
            var l = ForParts(Inner(e, 0));
            l.AddRange(ForParts(Inner(e, 1)));
            return l;
        }
        return new List<string> { StmtExpr(e) };
    }
    void Switch(JsonNode s)
    {
        var kids = Kids(s).ToList();
        var body = kids[^1];
        if (KindOf(body) != "CompoundStmt") throw new Unsupported("switch body is not a block");
        Line($"switch ({Value(kids[^2])})");
        Line("{");
        indent++;
        bool anyLabel = false, hasBody = false;
        void OnLabel(string label, string gotoText)
        {
            if (hasBody && !lastTerminates) Line(gotoText);     // C# has no implicit fall-through
            indent--; Line(label); indent++;
            anyLabel = true; hasBody = false; lastTerminates = false;
        }
        void Flatten(JsonNode n)
        {
            switch (KindOf(n))
            {
                case "CaseStmt":
                {
                    var v = Value(Inner(n, 0));
                    OnLabel($"case {v}:", $"goto case {v};");
                    Flatten(n["inner"]!.AsArray().Last()!);
                    break;
                }
                case "DefaultStmt":
                    OnLabel("default:", "goto default;");
                    Flatten(Inner(n, 0));
                    break;
                default:
                    Stmt(n);
                    hasBody = true;
                    break;
            }
        }
        foreach (var c in body["inner"]!.AsArray()) Flatten(c!);
        if (anyLabel && (!hasBody || !lastTerminates)) Line("break;");
        indent--;
        Line("}");
    }
    string LocalDecl(JsonNode d)
    {
        if (KindOf(d) != "VarDecl") throw new Unsupported($"local {KindOf(d)}");
        if ((string?)d["storageClass"] == "static") throw new Unsupported("static local");
        var t = TypeOf(d);
        var name = renames.TryGetValue((string)d["id"]!, out var uname) ? uname : Id((string)d["name"]!);
        if (frame.TryGetValue((string)d["id"]!, out var off))
        {
            if (InnerCount(d) == 0) return "";
            var init = Inner(d, 0);
            if (KindOf(init) == "InitListExpr") throw new Unsupported("initializer list");
            if (t.Kind == K.Record) return $"Mem.Copy(_fp + {off}u, {Addr(StripValueCast(init))}, {t.Size})";
            if (t.Kind == K.Array) throw new Unsupported("array initializer");
            return $"Mem.Set(_fp + {off}u, {Value(init)})";
        }
        if (!t.IsScalarMem) throw new Unsupported($"local of type {t.Kind} {t.Name}");
        var iv = InnerCount(d) > 0 ? Value(Inner(d, 0)) : t.Zero;
        return $"{t.Cs} {name} = {iv}";
    }

    JsonNode StripValueCast(JsonNode e)
    {
        e = StripParens(e);
        if (KindOf(e) == "ImplicitCastExpr" && (string?)e["castKind"] == "LValueToRValue") return Inner(e, 0);
        return e;
    }

    /// <summary>An expression that is legal as a C# statement (call, assignment, ++/--).</summary>
    string StmtExpr(JsonNode e)
    {
        e = StripParens(e);
        switch (KindOf(e))
        {
            case "CallExpr":
                if (TypeOf(e).Kind == K.Record) return "_ = " + Value(e);
                noRetCast = true;   // the result is discarded: no coercion to a stale prototype's return type
                return Raw(e);
            case "BinaryOperator" when (string)e["opcode"]! == "=":
            case "CompoundAssignOperator":
                return Raw(e);
            case "UnaryOperator" when (string)e["opcode"]! is "++" or "--":
            {
                var c = e.DeepClone();
                c["isPostfix"] = false;   // value unused: the prefix form is simpler
                return Raw(c);
            }
            case "ImplicitCastExpr" or "CStyleCastExpr" when (string?)e["castKind"] == "ToVoid":
                return StmtExpr(Inner(e, 0));
            case "BinaryOperator" when (string)e["opcode"]! == ",":
                return StmtExpr(Inner(e, 0)) + "; " + StmtExpr(Inner(e, 1));
            default:
                return "_ = " + Value(e);
        }
    }

    static JsonNode StripParens(JsonNode e) { while (KindOf(e) == "ParenExpr") e = e["inner"]![0]!; return e; }

    // ------------------------------------------------------------------ expressions

    /// <summary>Raw form of an assignment/call/inc expression without the outer parentheses.</summary>
    string Raw(JsonNode e)
    {
        var s = Value(e);
        return s.StartsWith('(') && s.EndsWith(')') && Balanced(s[1..^1]) ? s[1..^1] : s;
    }
    static bool Balanced(string s)
    {
        int d = 0;
        foreach (var c in s) { if (c == '(') d++; else if (c == ')' && --d < 0) return false; }
        return d == 0;
    }

    string Cond(JsonNode e)
    {
        e = StripParens(e);
        if (KindOf(e) == "BinaryOperator")
        {
            var op = (string)e["opcode"]!;
            switch (op)
            {
                case "==": case "!=": case "<": case ">": case "<=": case ">=":
                    return $"({Value(Inner(e, 0))} {op} {Value(Inner(e, 1))})";
                case "&&": case "||":
                    return $"({Cond(Inner(e, 0))} {op} {Cond(Inner(e, 1))})";
            }
        }
        if (KindOf(e) == "UnaryOperator" && (string)e["opcode"]! == "!") return $"!{Cond(Inner(e, 0))}";
        if (KindOf(e) is "ImplicitCastExpr" && (string?)e["castKind"] is "IntegralToBoolean" or "PointerToBoolean" or "FloatingToBoolean")
            return Cond(Inner(e, 0));
        var t = TypeOf(e);
        return $"({Value(e)} != {t.Zero})";
    }

    string Value(JsonNode e)
    {
        switch (KindOf(e))
        {
            case "ParenExpr": return Value(Inner(e, 0));
            case "ConstantExpr": case "IntegerLiteral": case "CharacterLiteral": return Literal(e);
            case "FloatingLiteral":
            {
                var t = TypeOf(e);
                var v = (string)e["value"]!;
                if (!v.Contains('.') && !v.Contains('e') && !v.Contains('E')) v += ".0";
                return t.Size == 4 ? v + "f" : v;
            }
            case "ImplicitCastExpr": case "CStyleCastExpr": return Cast(e);
            case "DeclRefExpr": return DeclRef(e);
            case "UnaryOperator": return Unary(e);
            case "BinaryOperator": return Binary(e);
            case "CompoundAssignOperator": return CompoundAssign(e);
            case "ConditionalOperator":
                return $"({Cond(Inner(e, 0))} ? {Value(Inner(e, 1))} : {Value(Inner(e, 2))})";
            case "CallExpr": return Call(e);
            case "UnaryExprOrTypeTraitExpr":
            {
                if ((string)e["name"]! != "sizeof") throw new Unsupported("alignof");
                var at = e["argType"] != null ? CType.Parse((string)e["argType"]!["qualType"]!, ix) : TypeOf(Inner(e, 0));
                return $"{at.Size}u";
            }
            case "MemberExpr": case "ArraySubscriptExpr":
                return Load(e);
            case "StringLiteral": throw new Unsupported("string literal");
            default: throw new Unsupported(KindOf(e));
        }
    }

    string Literal(JsonNode e)
    {
        if (KindOf(e) == "ConstantExpr")
        {
            if (e["value"] == null) return Value(Inner(e, 0));
            return Num((string)e["value"]!, TypeOf(e));
        }
        return Num(e["value"]!.ToString(), TypeOf(e));
    }

    static string Num(string v, CType t)
    {
        if (t.Kind == K.Int && t.Size == 8) return v + (t.Signed ? "L" : "UL");
        if (t.Kind == K.Int && !t.Signed && t.Size >= 4) return v + "u";
        if (t.Kind == K.Float) return v.Contains('.') ? v : v + ".0";
        return v;
    }

    string DeclRef(JsonNode e)
    {
        var d = e["referencedDecl"]!;
        var kind = KindOf(d);
        var name = (string)d["name"]!;
        switch (kind)
        {
            case "ParmVarDecl":
                return frame.ContainsKey((string)d["id"]!) ? Load(e) : Id(name);
            case "EnumConstantDecl":
                return ix.EnumValues.TryGetValue((string)d["id"]!, out var v) ? v.ToString() : throw new Unsupported($"enum {name}");
            case "VarDecl":
                if (IsGlobal(d) || frame.ContainsKey((string)d["id"]!)) return Load(e);
                return renames.TryGetValue((string)d["id"]!, out var rn) ? rn : Id(name);
            case "FunctionDecl": throw new Unsupported("function used as a value");
            default: throw new Unsupported($"declref {kind}");
        }
    }

    /// <summary>A variable is global unless it was declared inside the function being emitted (or is one of its parameters).</summary>
    bool IsGlobal(JsonNode d) => !localIds.Contains((string)d["id"]!);

    bool IsLocalRef(JsonNode e)
    {
        e = StripParens(e);
        if (KindOf(e) != "DeclRefExpr") return false;
        var d = e["referencedDecl"]!;
        if (frame.ContainsKey((string)d["id"]!)) return false;
        return KindOf(d) is "ParmVarDecl" || (KindOf(d) == "VarDecl" && !IsGlobal(d));
    }

    // ---- lvalues

    /// <summary>Guest address of an lvalue.</summary>
    string Addr(JsonNode e)
    {
        e = StripParens(e);
        switch (KindOf(e))
        {
            case "DeclRefExpr":
            {
                var d = e["referencedDecl"]!;
                if (frame.TryGetValue((string)d["id"]!, out var off)) return off == 0 ? "_fp" : $"(_fp + {off}u)";
                if (KindOf(d) != "VarDecl" || !IsGlobal(d)) throw new Unsupported("address of an unplaced local");
                var name = (string)d["name"]!;
                var a = symbolAddress(name) ?? throw new Unsupported($"unknown symbol {name}");
                return $"0x{a:X8}u";
            }
            case "UnaryOperator" when (string)e["opcode"]! == "*":
                return Value(Inner(e, 0));
            case "ArraySubscriptExpr":
            {
                var bt = TypeOf(Inner(e, 0));
                var idx = Value(Inner(e, 1));
                var sz = bt.ElemSize;
                return sz == 1 ? $"({Value(Inner(e, 0))} + (uint){idx})" : $"({Value(Inner(e, 0))} + (uint)({idx} * {sz}))";
            }
            case "MemberExpr":
            {
                var f = ix.FieldsById.GetValueOrDefault((string)e["referencedMemberDecl"]!) ?? throw new Unsupported("member of anonymous/indirect field");
                if (f.Offset < 0) throw new Unsupported($"bitfield {f.Name}");
                if (Views != null && TryMember(e, out var av0, out var am0) && am0.T.K is 'N' or 'A') return $"{av0}.{am0.Name}.P";
                var b = e["isArrow"]?.GetValue<bool>() == true ? Value(Inner(e, 0)) : Addr(Inner(e, 0));
                return f.Offset == 0 ? b : $"({b} + {f.Offset}u)";
            }
            case "CStyleCastExpr" or "ImplicitCastExpr":
                return Addr(Inner(e, 0));
            case "StringLiteral":
                return $"Str.Lit({CSharpString((string)e["value"]!)})";
            case "CallExpr" when TypeOf(e).Kind == K.Record:
                return Call(e);     // the call yields the address of its result buffer
            default: throw new Unsupported($"address of {KindOf(e)}");
        }
    }

    /// <summary>C string literal text (as clang prints it, with C escapes) to a C# string literal using the same bytes.</summary>
    static string CSharpString(string cLiteral)
    {
        var s = cLiteral.Length >= 2 && cLiteral[0] == '"' ? cLiteral[1..^1] : cLiteral;
        var o = new StringBuilder("\"");
        void Put(int ch)
        {
            if (ch == '"' || ch == '\\') o.Append('\\').Append((char)ch);
            else if (ch >= 0x20 && ch < 0x7F) o.Append((char)ch);
            else o.Append("\\u").Append(ch.ToString("X4"));
        }
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { Put(s[i]); continue; }
            i++;
            switch (s[i])
            {
                case 'n': Put('\n'); break;
                case 't': Put('\t'); break;
                case 'r': Put('\r'); break;
                case 'a': Put(7); break;
                case 'b': Put(8); break;
                case 'f': Put(12); break;
                case 'v': Put(11); break;
                case 'x':
                {
                    int v = 0, n = 0;
                    while (i + 1 < s.Length && Uri.IsHexDigit(s[i + 1])) { v = v * 16 + Convert.ToInt32(s[++i].ToString(), 16); n++; }
                    Put(v & 0xFF);
                    break;
                }
                case >= '0' and <= '7':
                {
                    int v = s[i] - '0', n = 1;
                    while (n < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7') { v = v * 8 + (s[++i] - '0'); n++; }
                    Put(v & 0xFF);
                    break;
                }
                default: Put(s[i]); break;   // \\ \' \" \?
            }
        }
        return o.Append('"').ToString();
    }

    static string Getter(CType t) => t.Kind switch
    {
        K.Float => t.Size == 4 ? "F32" : "F64",
        K.Ptr => "U32",
        K.Int => (t.Size, t.Signed) switch { (1, true) => "S8", (1, false) => "U8", (2, true) => "S16", (2, false) => "U16", (4, true) => "S32", (4, false) => "U32", (8, true) => "S64", _ => "U64" },
        _ => throw new Unsupported($"memory access of {t.Kind} {t.Name}"),
    };

    string Load(JsonNode e)
    {
        if (IsLocalRef(e)) return Value(e);
        var t = TypeOf(e);
        if (t.Kind is K.Array or K.Record) throw new Unsupported($"rvalue of {t.Kind}");
        if (Views != null && TryViewAccess(e, t, out var vr, out _)) return vr;
        return $"Mem.{Getter(t)}({Addr(e)})";
    }

    static bool HasSideEffects(JsonNode e)
    {
        var k = KindOf(e);
        if (k is "CallExpr" or "CompoundAssignOperator") return true;
        if (k == "UnaryOperator" && (string)e["opcode"]! is "++" or "--") return true;
        if (k == "BinaryOperator" && (string)e["opcode"]! is "=" or ",") return true;
        foreach (var c in Kids(e)) if (HasSideEffects(c)) return true;
        return false;
    }

    /// <summary>Read-modify-write on any lvalue: <paramref name="compute"/> gets the old value text and returns the new one.</summary>
    string Modify(JsonNode lv, Func<string, string> compute)
    {
        var t = TypeOf(lv);
        if (IsLocalRef(lv))
        {
            var n = Value(lv);
            return $"({n} = {CastTo(t, compute(n))})";
        }
        if (HasSideEffects(lv)) throw new Unsupported("read-modify-write through an address with side effects");
        if (Views != null && TryViewAccess(lv, t, out var vread, out var vwrite)) return vwrite(CastTo(t, compute(vread)));
        var a = Addr(lv);
        return $"Mem.Set({a}, {CastTo(t, compute($"Mem.{Getter(t)}({a})"))})";
    }

    static string CastTo(CType t, string s) => $"({t.Cs})({s})";

    // ---- operators

    string Unary(JsonNode e)
    {
        var op = (string)e["opcode"]!;
        var x = Inner(e, 0);
        switch (op)
        {
            case "+": return Value(x);
            case "-":
            {
                var ut = TypeOf(e);
                return ut.IsInt && !ut.Signed ? $"({(ut.Size == 8 ? "0UL" : "0u")} - {Value(x)})" : $"(-{Value(x)})";
            }
            case "~": return $"(~{Value(x)})";
            case "!": return $"({Cond(x)} ? 0 : 1)";
            case "*": return Load(e);
            case "&": return Addr(x);
            case "++": case "--":
            {
                var t = TypeOf(x);
                var step = t.IsPtr ? $"{t.ElemSize}u" : "1";
                var sign = op == "++" ? "+" : "-";
                bool post = e["isPostfix"]?.GetValue<bool>() == true;
                if (IsLocalRef(x) && !post) return t.IsPtr ? $"({Value(x)} {sign}= {step})" : $"({op}{Value(x)})";
                if (IsLocalRef(x) && post)
                    return t.IsPtr ? $"(({Value(x)} {sign}= {step}) {(op == "++" ? "-" : "+")} {step})" : $"({Value(x)}{op})";
                var newv = Modify(x, old => $"{old} {sign} {step}");
                return post ? $"({newv} {(op == "++" ? "-" : "+")} {step})" : newv;
            }
            default: throw new Unsupported($"unary {op}");
        }
    }

    string Binary(JsonNode e)
    {
        var op = (string)e["opcode"]!;
        var l = Inner(e, 0);
        var r = Inner(e, 1);
        switch (op)
        {
            case "=":
            {
                var t = TypeOf(l);
                if (t.Kind == K.Record)
                    return $"Mem.Copy({Addr(l)}, {Addr(StripValueCast(r))}, {t.Size})";
                if (t.Kind == K.Array) throw new Unsupported("array assignment");
                if (IsLocalRef(l)) return $"({Value(l)} = {Value(r)})";
                if (Views != null && TryViewAccess(l, t, out _, out var vw)) return vw(Value(r));
                return $"Mem.Set({Addr(l)}, {Value(r)})";
            }
            case "==": case "!=": case "<": case ">": case "<=": case ">=": case "&&": case "||":
                return $"({Cond(e)} ? 1 : 0)";
            case ",":
                return $"C.Seq((object?)({Value(l)}), {Value(r)})";
        }
        var lt = TypeOf(l);
        var rt = TypeOf(r);
        if (op is "+" or "-" && (lt.IsPtr || rt.IsPtr))
        {
            if (lt.IsPtr && rt.IsPtr)
                return lt.ElemSize == 1 ? $"((int)({Value(l)} - {Value(r)}))" : $"((int)({Value(l)} - {Value(r)}) / {lt.ElemSize})";
            var (p, pt, n) = lt.IsPtr ? (l, lt, r) : (r, rt, l);
            var sz = pt.ElemSize;
            var nv = Value(n);
            var scaled = sz == 1 ? $"(uint){nv}" : $"(uint)({nv} * {sz})";
            return $"({Value(p)} {op} {scaled})";
        }
        if (op is "<<" or ">>") return $"({Value(l)} {op} (int){Value(r)})";
        return $"({Value(l)} {op} {Value(r)})";
    }

    string CompoundAssign(JsonNode e)
    {
        var op = ((string)e["opcode"]!)[..^1];   // "+=" -> "+"
        var l = Inner(e, 0);
        var r = Inner(e, 1);
        var lt = TypeOf(l);
        var comp = e["computeResultType"] != null ? CType.Parse((string)e["computeResultType"]!["qualType"]!, ix) : lt;
        string rv = Value(r);
        string Compute(string old)
        {
            if (lt.IsPtr) return $"{old} {op} (uint)({rv} * {lt.ElemSize})";
            if (op is "<<" or ">>") return $"{CastTo(comp, old)} {op} (int){rv}";
            return $"{CastTo(comp, old)} {op} {rv}";
        }
        return Modify(l, Compute);
    }

    string Cast(JsonNode e)
    {
        var kind = (string?)e["castKind"] ?? "NoOp";
        var x = Inner(e, 0);
        var t = TypeOf(e);
        switch (kind)
        {
            case "LValueToRValue":
                // a struct passed by value travels as the address of the caller's copy
                return TypeOf(x).Kind == K.Record ? Addr(x) : Load(x);
            case "ArrayToPointerDecay": return Addr(x);
            case "FunctionToPointerDecay":
            {
                var f = StripParens(x);
                if (KindOf(f) == "DeclRefExpr" && KindOf(f["referencedDecl"]) == "FunctionDecl")
                {
                    var fname = Norm((string)f["referencedDecl"]!["name"]!);
                    var a = symbolAddress(fname) ?? FuncAddrFromName(fname) ?? throw new Unsupported($"address of function {fname}");
                    return $"0x{a:X8}u";
                }
                throw new Unsupported("function pointer decay");
            }
            case "NoOp": case "BitCast": case "LValueBitCast":
                return t.Kind == K.Void || t.Cs == TypeOf(x).Cs ? Value(x) : CastTo(t, Value(x));
            case "ToVoid": return Value(x);
            case "NullToPointer": return "0u";
            case "IntegralToBoolean": case "PointerToBoolean": case "FloatingToBoolean":
                return $"({Cond(e)} ? 1 : 0)";
            case "FloatingToIntegral":
                // the MIPS code converts through trunc.w.s: a negative float keeps its two's complement bits as an unsigned integer and narrow types wrap,
                // where C#'s casts saturate ((uint)-1.5f is 0, (byte)300f is 255)
                if (t.Cs == "uint") return $"Cpu.TruncU32({Value(x)})";
                if (t.Cs is "short" or "ushort" or "sbyte" or "byte") return $"({t.Cs})(int)({Value(x)})";
                return CastTo(t, Value(x));
            case "IntegralCast": case "FloatingCast": case "IntegralToFloating":
            case "PointerToIntegral": case "IntegralToPointer":
                return CastTo(t, Value(x));
            default: throw new Unsupported($"cast {kind}");
        }
    }

    static uint? FuncAddrFromName(string n) =>
        n.StartsWith("func_") && n.Length == 13 && uint.TryParse(n[5..], System.Globalization.NumberStyles.HexNumber, null, out var a) ? a : null;

    string Call(JsonNode e)
    {
        var callee = StripParens(Inner(e, 0));
        var argNodes = Kids(e).Skip(1).ToList();
        if (KindOf(callee) == "ImplicitCastExpr" && (string?)callee["castKind"] == "BuiltinFnToFnPtr")
        {
            var bn = (string)StripParens(Inner(callee, 0))["referencedDecl"]!["name"]!;
            if (bn.StartsWith("__builtin_")) bn = bn["__builtin_".Length..];
            return bn switch
            {
                "fabsf" => $"MathF.Abs({Value(argNodes[0])})",
                "fabs" => $"Math.Abs({Value(argNodes[0])})",
                "sqrt" => $"Math.Sqrt({Value(argNodes[0])})",
                _ => throw new Unsupported($"builtin {bn}"),
            };
        }
        if (KindOf(callee) == "ImplicitCastExpr" && (string?)callee["castKind"] == "FunctionToPointerDecay")
        {
            var d = StripParens(Inner(callee, 0));
            if (KindOf(d) == "DeclRefExpr" && KindOf(d["referencedDecl"]) == "FunctionDecl")
                return DirectCall(e, d["referencedDecl"]!, argNodes);
            // (*fp)(...) : the callee is a function lvalue; its address is the pointer value
            if (KindOf(d) == "UnaryOperator" && (string)d["opcode"]! == "*")
                return IndirectCall(e, Value(Inner(d, 0)), TypeOf(d), argNodes);
        }
        // fp(...) where fp is a pointer-to-function value
        var ct = TypeOf(callee);
        if (ct.IsPtr && ct.Elem?.Kind == K.Func)
            return IndirectCall(e, Value(callee), ct.Elem, argNodes);
        throw new Unsupported("indirect call");
    }

    string DirectCall(JsonNode call, JsonNode fnDecl, List<JsonNode> argNodes)
    {
        bool skipRetCast = noRetCast; noRetCast = false;
        var name = FnName(fnDecl);
        if (name == "__builtin_next_arg") throw new Unsupported("variadic function (uses __builtin_next_arg)");
        lock (Called) Called.Add(name);
        var vals = new List<string>();
        sigs.TryGetValue(name, out var sig);
        bool recRet = TypeOf(call).Kind == K.Record;
        int shift = recRet ? 1 : 0;      // a struct result travels through a hidden first parameter
        var raw = argNodes.Select(Value).ToList();
        if (sig != null && NeedsWordMarshal(sig, argNodes, shift))
            vals = MarshalByWords(name, sig, argNodes, raw, shift);
        else
            for (int i = 0; i < argNodes.Count; i++)
            {
                var v = raw[i];
                var at = TypeOf(argNodes[i]);
                if (sig != null && i + shift < sig.Params.Length && at.IsScalarMem && at.Cs != sig.Params[i + shift]) v = CoerceArg(v, at.Cs, sig, i + shift, argNodes, recRet);
                vals.Add(v);
            }
        // no call has run yet (arguments included), so a0-a3 still hold this function's own incoming words
        bool noCallYet = callsEmitted == 0 && loopDepth == 0 && labels.Count == 0 && !flatMode;
        // a stale prototype may pass more arguments than the definition has: the extras were never read, so drop them
        if (sig != null)
        {
            int keep = Math.Max(0, sig.Params.Length - shift);
            if (vals.Count > keep) vals.RemoveRange(keep, vals.Count - keep);
        }
        string? tmp = null;
        if (recRet)
        {
            var off = callTemps.TryGetValue(call, out var o) ? o : throw new Unsupported("struct-returning call without a result slot");
            tmp = off == 0 ? "_fp" : $"(_fp + {off}u)";
            vals.Insert(0, tmp);
        }
        // the definition may take more parameters than a stale prototype passes; the missing ones are whatever the caller left in registers
        if (sig != null)
            for (int i = vals.Count; i < sig.Params.Length; i++) vals.Add(LeftInRegister(name, sig, i, noCallYet));
        var text = $"{Id(name)}({string.Join(", ", vals)})";
        callsEmitted++;
        if (recRet) return $"C.Seq({text}, {tmp})";
        var want = TypeOf(call);
        if (!skipRetCast && want.IsScalarMem) lock (ValueUsed) ValueUsed.Add((name, want.Cs));
        if (!skipRetCast && sig != null && sig.Ret != "void" && want.IsScalarMem && want.Cs != sig.Ret) text = $"({want.Cs})({text})";   // stale prototype disagrees with the definition
        return text;
    }

    /// <summary>
    /// Does the call site disagree with the definition about which parameters are structs passed by value? (A prototype in a header can say
    /// <c>f(p, float x, float y, float z)</c> where the definition says <c>f(p, Vec3 v)</c>: both put the three words in a1..a3.)
    /// </summary>
    bool NeedsWordMarshal(Sig sig, List<JsonNode> argNodes, int shift)
    {
        for (int i = 0; i < argNodes.Count && i + shift < sig.Params.Length; i++)
            if ((sig.Structs[i + shift] > 0) != (TypeOf(argNodes[i]).Kind == K.Record)) return true;
        return false;
    }

    /// <summary>
    /// Passes the arguments the way the registers do: the call site's values are laid out as o32 argument words, and each parameter of the
    /// definition is built from the words it occupies (a struct from consecutive words, a float from the bits of its word).
    /// </summary>
    List<string> MarshalByWords(string callee, Sig sig, List<JsonNode> argNodes, List<string> raw, int shift)
    {
        // site words
        var words = new Dictionary<int, string>();
        var fpArgs = new Dictionary<int, string>();                      // F12/F14 arguments by site argument index
        var types = argNodes.Select(a => { var t = TypeOf(a); return t.Kind == K.Record ? "uint" : t.IsScalarMem ? t.Cs : throw new Unsupported($"argument of type {t.Kind}"); }).ToArray();
        var sizes = argNodes.Select(a => { var t = TypeOf(a); return t.Kind == K.Record ? t.Size : 0; }).ToArray();
        int word = 0;
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i]; var v = raw[i];
            bool inFpReg = Abi.IsFp(t) && (i == 0 || (i == 1 && Abi.IsFp(types[0])));
            if (t == "double" || Abi.IsWide(t)) word = (word + 1) & ~1;
            if (inFpReg) { fpArgs[i] = v; word += t == "double" ? 2 : 1; }
            else if (t == "float") { words[word] = $"Cpu.F2U({v})"; word++; }
            else if (t == "double") { words[word] = $"Cpu.D2Hi({v})"; words[word + 1] = $"Cpu.D2Lo({v})"; word += 2; }
            else if (Abi.IsWide(t)) { words[word] = $"(uint)((ulong)({v}) >> 32)"; words[word + 1] = $"(uint)({v})"; word += 2; }
            else if (sizes[i] > 0) { int n = (sizes[i] + 3) / 4; for (int k = 0; k < n; k++) words[word + k] = $"Mem.U32({v} + {4 * k}u)"; word += n; }
            else { words[word] = t == "uint" ? v : $"(uint)({v})"; word++; }
        }
        string W(int k) => words.TryGetValue(k, out var s) ? s : "0u";

        // definition parameters from those words
        var (first, _) = Abi.Layout(sig.Params, sig.Structs);
        var res = new List<string>();
        for (int p = shift; p < sig.Params.Length; p++)
        {
            var t = sig.Params[p]; int w = first[p];
            if (w < 0) { res.Add(fpArgs.TryGetValue(p - shift, out var fv) ? $"({t})({fv})" : $"default({t})"); continue; }
            if (sig.Structs[p] > 0)
            {
                int n = (sig.Structs[p] + 3) / 4;
                if (n > 16) throw new Unsupported($"struct of {sig.Structs[p]} bytes rebuilt from scalar arguments");
                res.Add($"C.Tmp({string.Join(", ", Enumerable.Range(w, n).Select(W))})");
            }
            else if (t == "float") res.Add($"Cpu.U2F({W(w)})");
            else if (t == "double") res.Add($"Cpu.U2D({W(w)}, {W(w + 1)})");
            else if (t == "long") res.Add($"Cpu.Long({W(w)}, {W(w + 1)})");
            else if (t == "ulong") res.Add($"(ulong)Cpu.Long({W(w)}, {W(w + 1)})");
            else res.Add(t == "uint" ? W(w) : $"({t})({W(w)})");
        }
        return res;
    }

    /// <summary>
    /// A parameter the call site does not pass: on the console the callee reads whatever the caller left in that register. That is the caller's own
    /// incoming argument when nothing ran in between (a stub that is only <c>jal f</c> forwards its a0..a3). Anywhere else the register content cannot
    /// be named in C#, so the function is recompiled from the MIPS instead (<see cref="Unsupported"/> -> needs_mips).
    /// </summary>
    string LeftInRegister(string callee, Sig sig, int i, bool noCallYet)
    {
        var t = sig.Params[i];
        var (first, _) = Abi.Layout(sig.Params, sig.Structs);
        int w = first[i];
        if (w < 0 || w >= 4 || !romWords.TryGetValue(callee, out var rw) || w >= rw || Abi.IsWide(t) || t == "double")
            return $"default({t})";                                                              // F12/F14, stack words, or a word the callee never reads
        if (noCallYet)
            foreach (var (pn, word) in fwdParams)
                if (word == w) return t == "float" ? $"Cpu.U2F((uint)({pn}))" : t == "uint" ? $"(uint)({pn})" : $"({t})(uint)({pn})";
        if (curIsStatic) return $"default({t})";                                                 // statics have no MIPS twin to fall back to
        throw new Unsupported($"argument register a{w} of the call to {callee} is read by the callee but holds a value this function cannot name");
    }

    /// <summary>
    /// A call-site argument whose C type differs from the definition's parameter type. Between two integer types (or two float types) a numeric
    /// conversion is right. Between an integer and a float travelling in the same general register the callee reads the raw bits
    /// (<c>lw a1; jal f</c> / <c>mtc1 a1, f0</c>), so the value must be reinterpreted, not converted.
    /// </summary>
    string CoerceArg(string v, string from, Sig sig, int p, List<JsonNode> argNodes, bool recRet)
    {
        var to = sig.Params[p];
        if (Abi.IsFp(from) == Abi.IsFp(to)) return $"({to})({v})";
        var siteTypes = (recRet ? new[] { "uint" } : Array.Empty<string>()).Concat(argNodes.Select(a => { var t = TypeOf(a); return t.IsScalarMem ? t.Cs : "uint"; })).ToArray();
        var siteStructs = (recRet ? new[] { 0 } : Array.Empty<int>()).Concat(argNodes.Select(a => { var t = TypeOf(a); return t.Kind == K.Record ? t.Size : 0; })).ToArray();
        var (calleeFirst, _) = Abi.Layout(sig.Params, sig.Structs);
        var (siteFirst, _2) = Abi.Layout(siteTypes, siteStructs);
        bool sameWord = p < siteFirst.Length && calleeFirst[p] >= 0 && calleeFirst[p] == siteFirst[p]
                        && from != "double" && to != "double" && !Abi.IsWide(from) && !Abi.IsWide(to);
        if (!sameWord) return $"({to})({v})";                // F12/F14 slot, double or shifted layout: no per-argument typed equivalent
        return to == "float" ? $"Cpu.U2F((uint)({v}))" : to == "uint" ? $"Cpu.F2U({v})" : $"({to})Cpu.F2U({v})";
    }

    /// <summary>A call through a code address: arguments are marshalled into the register convention (see <see cref="Abi"/>).</summary>
    string IndirectCall(JsonNode call, string addr, CType fn, List<JsonNode> argNodes)
    {
        if (fn.Kind != K.Func) throw new Unsupported("indirect call of an unknown function type");
        bool discard = noRetCast; noRetCast = false;      // statement context: the result is not converted
        var retT = TypeOf(call);
        string? hidden = null;
        if (retT.Kind == K.Record)
        {
            var off = callTemps.TryGetValue(call, out var o) ? o : throw new Unsupported("struct-returning call without a result slot");
            hidden = off == 0 ? "_fp" : $"(_fp + {off}u)";
        }
        var types = argNodes.Select(a => { var t = TypeOf(a); return t.Kind == K.Record ? "uint" : t.IsScalarMem ? t.Cs : throw new Unsupported($"argument of type {t.Kind}"); }).ToArray();
        var structs = argNodes.Select(a => { var t = TypeOf(a); return t.Kind == K.Record ? t.Size : 0; }).ToArray();
        var values = argNodes.Select(Value).ToArray();
        // an argument of a promoted type is already converted by sema; use the call's own argument types (prototype or not)
        var text = Abi.IndirectCall(addr, discard || retT.Kind is K.Void ? "void" : retT.Kind == K.Record ? "uint" : retT.Cs, types, values, structs, hidden);
        callsEmitted++;
        return hidden != null ? $"C.Seq({text}, {hidden})" : text;
    }
}








