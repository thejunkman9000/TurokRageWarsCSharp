using System.Text;
using System.Text.Json.Nodes;

namespace Trw.Transpile;

public sealed class Unsupported(string what) : Exception(what) { }

/// <summary>C# parameter/return types of a function definition, used to coerce arguments at call sites.</summary>
public sealed record Sig(string Ret, string[] Params);

/// <summary>
/// Turns one C function (clang JSON AST, MIPS o32 target) into a C# static method. All game memory goes through
/// <c>Mem</c>; scalar locals are C# locals; pointers are <c>uint</c> guest addresses. Locals that need an address
/// (structs, arrays, anything whose address is taken) live in a guest stack frame (<c>Stack.Alloc</c>).
/// </summary>
public sealed class Emitter
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

    public Emitter(AstIndex ix, Func<string, uint?> symbolAddress, IReadOnlyDictionary<string, Sig> sigs)
    {
        this.ix = ix; this.symbolAddress = symbolAddress; this.sigs = sigs;
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

    public string FnName(JsonNode fn) =>
        (string?)fn["storageClass"] == "static" ? StaticPrefix + (string)fn["name"]! : (string)fn["name"]!;

    /// <summary>The C# view of a definition: a struct result becomes a hidden leading <c>uint</c> parameter, struct parameters become addresses.</summary>
    public Sig ToSig(JsonNode fn)
    {
        var (ret, ps) = SignatureOf(fn);
        string Cs(CType t) => t.Kind == K.Record ? "uint" : t.Cs;
        var list = ps.Select(Cs).ToList();
        if (ret.Kind == K.Record) list.Insert(0, "uint");
        return new Sig(ret.Kind is K.Void or K.Record ? "void" : Cs(ret), list.ToArray());
    }

    public (CType ret, List<CType> ps) SignatureOf(JsonNode fn)
    {
        var ft = CType.Parse((string?)fn["type"]?["desugaredQualType"] ?? (string)fn["type"]!["qualType"]!, ix);
        var ps = Kids(fn).Where(x => KindOf(x) == "ParmVarDecl").Select(TypeOf).ToList();
        return (ft.Ret ?? CType.S32, ps);
    }

    public string EmitFunction(JsonNode fn)
    {
        sb.Clear(); indent = 1; frame = new(); frameSize = 0; labels = new(); lastTerminates = false;
        localIds = new(); callTemps = new(ReferenceEqualityComparer.Instance);
        var name = FnName(fn);
        var (ret, _) = SignatureOf(fn);
        var body = Kids(fn).First(x => KindOf(x) == "CompoundStmt");
        var ps = Kids(fn).Where(x => KindOf(x) == "ParmVarDecl").ToList();
        foreach (var p in ps) localIds.Add((string)p["id"]!);
        returnsRecord = ret.Kind == K.Record ? ret : null;

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

        string retCs = ret.Kind is K.Void or K.Record ? "void" : ret.Cs;
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
        foreach (var s in body["inner"]?.AsArray() ?? new JsonArray()) Stmt(s!);
        if (ret.Kind is not (K.Void or K.Record) && !EndsWithReturn(body)) Line($"return {ret.Zero};");
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
                    Line("return;");
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
                Line($"while ({Cond(Inner(s, 0))})");
                Block(Inner(s, 1));
                break;
            case "DoStmt":
                Line("do");
                Block(Inner(s, 0));
                Line($"while ({Cond(Inner(s, 1))});");
                break;
            case "ForStmt":
            {
                var kids = s["inner"]!.AsArray().Select(x => x!).ToList();   // init, (condVar), cond, inc, body; absent parts are "<<<NULL>>>"
                if (kids.Count != 5) throw new Unsupported("for layout");
                string init = IsNull(kids[0]) ? "" : KindOf(kids[0]) == "DeclStmt"
                    ? DeclList(kids[0]) : ForList(kids[0]);
                string cond = IsNull(kids[2]) ? "" : Cond(kids[2]);
                string inc = IsNull(kids[3]) ? "" : ForList(kids[3]);
                Line($"for ({init}; {cond}; {inc})");
                Block(kids[4]);
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
            case "CaseStmt": case "DefaultStmt":
                throw new Unsupported("case label outside the top level of a switch");
            default:
                Line(StmtExpr(s) + ";");
                break;
        }
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
    string ForList(JsonNode e)
    {
        e = StripParens(e);
        if (KindOf(e) == "BinaryOperator" && (string)e["opcode"]! == ",")
            return ForList(Inner(e, 0)) + ", " + ForList(Inner(e, 1));
        return StmtExpr(e);
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
        var name = Id((string)d["name"]!);
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
                return Id(name);
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
            case "-": return $"(-{Value(x)})";
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
                    var fname = (string)f["referencedDecl"]!["name"]!;
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
            case "IntegralCast": case "FloatingCast": case "IntegralToFloating": case "FloatingToIntegral":
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
        if (KindOf(callee) == "ImplicitCastExpr" && (string?)callee["castKind"] == "FunctionToPointerDecay")
        {
            var d = StripParens(Inner(callee, 0));
            if (KindOf(d) == "DeclRefExpr" && KindOf(d["referencedDecl"]) == "FunctionDecl")
                return DirectCall(e, d["referencedDecl"]!, argNodes);
            // (*fp)(...) : the callee is a function lvalue; its address is the pointer value
            if (KindOf(d) == "UnaryOperator" && (string)d["opcode"]! == "*")
                return IndirectCall(Value(Inner(d, 0)), TypeOf(d), argNodes);
        }
        // fp(...) where fp is a pointer-to-function value
        var ct = TypeOf(callee);
        if (ct.IsPtr && ct.Elem?.Kind == K.Func)
            return IndirectCall(Value(callee), ct.Elem, argNodes);
        throw new Unsupported("indirect call");
    }

    string DirectCall(JsonNode call, JsonNode fnDecl, List<JsonNode> argNodes)
    {
        bool skipRetCast = noRetCast; noRetCast = false;
        var name = FnName(fnDecl);
        var vals = new List<string>();
        sigs.TryGetValue(name, out var sig);
        bool recRet = TypeOf(call).Kind == K.Record;
        int shift = recRet ? 1 : 0;      // a struct result travels through a hidden first parameter
        for (int i = 0; i < argNodes.Count; i++)
        {
            var v = Value(argNodes[i]);
            var at = TypeOf(argNodes[i]);
            if (sig != null && i + shift < sig.Params.Length && at.IsScalarMem && at.Cs != sig.Params[i + shift]) v = $"({sig.Params[i + shift]})({v})";
            vals.Add(v);
        }
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
            for (int i = vals.Count; i < sig.Params.Length; i++) vals.Add($"default({sig.Params[i]})");
        var text = $"{Id(name)}({string.Join(", ", vals)})";
        if (recRet) return $"C.Seq<uint>((object?){text}, {tmp})";
        var want = TypeOf(call);
        if (!skipRetCast && sig != null && sig.Ret != "void" && want.IsScalarMem && want.Cs != sig.Ret) text = $"({want.Cs})({text})";   // stale prototype disagrees with the definition
        return text;
    }

    string IndirectCall(string addr, CType fn, List<JsonNode> argNodes)
    {
        if (fn.Kind != K.Func || fn.Variadic) throw new Unsupported("indirect call of a variadic/unknown function");
        var vals = argNodes.Select(Value).ToList();
        string ret = fn.Ret!.Kind == K.Void ? "" : fn.Ret.Cs;
        var ptypes = fn.Params.Count == argNodes.Count ? fn.Params.Select(p => p.Cs).ToList()
                   : argNodes.Select(a => TypeOf(a).Cs).ToList();   // unprototyped pointer type: use what the call passes
        string dt = ret == "" ? (ptypes.Count == 0 ? "Action" : $"Action<{string.Join(", ", ptypes)}>")
                              : $"Func<{string.Join(", ", ptypes.Append(ret))}>";
        return $"Funcs.Get<{dt}>({addr})({string.Join(", ", vals)})";
    }
}




