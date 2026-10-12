using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Trw.Transpile;

// Typed struct views: C# `readonly struct` wrappers around a guest address, generated from the clang record layouts (AstIndex).
// Two users share this file so that they always agree on names:
//   * StructViewGen    (`--struct-views-out <dir>`)  writes src/Trw.Game/Generated/Structs/*.cs and the list of usable structs
//   * Emitter          (`--emit-views <list>`)       emits `new Actor(p).health` instead of `Mem.S32(p + 28u)` for the structs on that list

/// <summary>Type of a view member, as plain data (no references into one translation unit's AST).</summary>
public sealed class TRef
{
    public char K;                  // S scalar, P pointer to struct, Q pointer to scalar, R other pointer (raw), N nested struct, A array
    public string Cs = "", Get = "";  // scalar: C# type, Mem getter suffix (S32, F32, ...)
    public int Size;                // scalar size / struct size
    public string Rec = "";         // P, N: struct name (the view of a conflicting name is a variant, see ViewSet)
    public string CsRec = "";       // generator: C# type name of Rec's view (variants differ from Rec)
    public TRef? Elem; public int Count;   // A
    /// <summary>Structural description: equal descriptions mean equal layout and equal generated members.</summary>
    public string Desc => K switch
    {
        'S' => Cs, 'Q' => "ptr:" + Get, 'R' => "ptr", 'P' => "ptr:" + Rec, 'N' => "rec:" + Rec + ":" + Fp,
        _ => $"{Elem!.Desc}[{Count}]",
    };
    public string Fp = "";          // N: fingerprint of the nested struct (it is part of the parent's layout)
    /// <summary>Name of the C# view type for a nested struct / array / typed pointer.</summary>
    public string ViewName => K switch
    {
        'N' => CsRec,
        'Q' => Get + "Ptr",
        'A' => (Elem!.K switch { 'S' => Elem.Get, 'N' => Elem.CsRec, 'A' => Elem.ViewName, _ => "U32" }) + "x" + Count,
        _ => throw new InvalidOperationException(),
    };
}

public sealed class VMember
{
    public string CName = "";       // the C field name
    public string Name = "", Raw = "";   // C# property (typed for pointers); Raw: uint accessor of pointer fields ("" otherwise)
    public int Off;
    public TRef T = null!;
    /// <summary>Name of the member that reads the value as the emitter's own type (uint for pointers).</summary>
    public string ValueName => T.K is 'P' or 'Q' ? Raw : Name;
    public string SetName(string n) => n.TrimStart('@') + "_Set";
}

public sealed class VInfo
{
    public string Name = "", Fp = "";
    public int Size;
    public bool IsUnion, Bad;
    public string BadWhy = "";
    public List<VMember> Members = new();
}

public static class StructViews
{
    static readonly HashSet<string> Keywords = new()
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate",
        "do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in",
        "int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private",
        "protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
        "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };
    // names a view must not use for itself: classes the generated code names directly (Trw.Platform, Trw.Game) and the member names of the views
    static readonly HashSet<string> ReservedTypes = new()
    {
        "Mem","Stack","Cpu","C","Funcs","Str","Dbg","Host","AudioHle","Game","Program","Gfx","Os","Runtime","Window","Debug",
        "Object","String","Math","Random","Task","Thread","Buffer","Array","Type","Enum","Delegate","Action","Func","Attribute","Exception",
    };
    static readonly HashSet<string> ReservedMembers = new() { "P", "IsNull", "At", "SizeOf", "Equals", "GetHashCode", "ToString", "GetType", "Length", "Set", "Add" };

    static readonly Regex Quals = new(@"\b(const|volatile|restrict|register|__restrict)\b\s*", RegexOptions.Compiled);
    static readonly ConditionalWeakTable<Record, string> AnonNames = new();
    static readonly ConditionalWeakTable<Record, VInfo> Cache = new();
    sealed class RecList : List<Record> { }
    static readonly ConditionalWeakTable<AstIndex, RecList> AnonList = new();

    /// <summary>Name of the view of a record: its typedef name (shortest), else its tag, else the name given by the field that holds it.</summary>
    public static string NameOf(AstIndex ix, Record r)
    {
        string n;
        if (AnonNames.TryGetValue(r, out var an)) n = an;
        else
        {
            var tds = ix.TypedefNamesOf(r);
            n = tds.Count > 0 ? tds.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal).First() : r.Name;
        }
        if (n == "") return "";
        if (ReservedTypes.Contains(n) || Keywords.Contains(n)) n += "_s";
        return n;
    }

    static string Getter(CType t) => t.Kind switch
    {
        K.Float => t.Size == 4 ? "F32" : "F64",
        K.Int => (t.Size, t.Signed) switch { (1, true) => "S8", (1, false) => "U8", (2, true) => "S16", (2, false) => "U16", (4, true) => "S32", (4, false) => "U32", (8, true) => "S64", _ => "U64" },
        _ => throw new InvalidOperationException(),
    };

    static TRef? Resolve(AstIndex ix, string q, string parentName, string propHint, Action<Record, string>? onAnon)
    {
        q = Quals.Replace(q, "").Trim();
        if (q.EndsWith(']') && !q.Contains("(*"))
        {
            int lb = q.IndexOf('['), rb = q.IndexOf(']', lb);
            var inner = q[(lb + 1)..rb];
            int cnt = int.TryParse(inner, out var c) ? c : 0;
            var elem = Resolve(ix, (q[..lb].TrimEnd() + q[(rb + 1)..]).Trim(), parentName, propHint + "_e", onAnon);
            return elem == null ? null : new TRef { K = 'A', Elem = elem, Count = cnt };
        }
        if (ix.Typedefs.TryGetValue(q, out var td) && td != q && ix.FindRecord(q) == null && td.Contains('[') && !td.Contains("(*"))
            return Resolve(ix, td, parentName, propHint, onAnon);
        var t = CType.Parse(q, ix);
        switch (t.Kind)
        {
            case K.Int: case K.Float: return new TRef { K = 'S', Cs = t.Cs, Get = Getter(t), Size = t.Size };
            case K.Record:
            {
                var rec = ix.FindRecord(t.Name)!;
                var nm = RecName(ix, rec, parentName, propHint, onAnon);
                return new TRef { K = 'N', Rec = nm, Size = rec.Size, Fp = Info(ix, rec).Fp };
            }
            case K.Ptr:
            {
                var e = t.Elem!;
                if (e.Kind == K.Record)
                {
                    var rec = ix.FindRecord(e.Name);
                    if (rec == null) return new TRef { K = 'R' };
                    var nm = NameOf(ix, rec);
                    return nm == "" ? new TRef { K = 'R' } : new TRef { K = 'P', Rec = nm };
                }
                if (e.Kind is K.Int or K.Float) return new TRef { K = 'Q', Get = Getter(e), Size = e.Size };
                return new TRef { K = 'R' };
            }
            default: return null;
        }
    }

    static string RecName(AstIndex ix, Record rec, string parentName, string propHint, Action<Record, string>? onAnon)
    {
        var nm = NameOf(ix, rec);
        if (nm != "") return nm;
        nm = parentName + "_" + propHint;
        AnonNames.AddOrUpdate(rec, nm);
        AnonList.GetOrCreateValue(ix).Add(rec);       // (the AST index keeps another copy of a nested record: it cannot be found through ix.Records)
        onAnon?.Invoke(rec, nm);
        return nm;
    }

    static string Sanitize(string n)
    {
        var b = new StringBuilder();
        foreach (var ch in n) b.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var s = b.ToString();
        return s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s;
    }

    /// <summary>Members of a record (anonymous members flattened in), names made unique, plus the fingerprint of the whole layout.</summary>
    public static VInfo Info(AstIndex ix, Record rec)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(rec, out var hit)) return hit;
            var vi = new VInfo { IsUnion = rec.IsUnion, Size = rec.Size };
            Cache.Add(rec, vi);      // before the members: a self-referential struct only holds pointers, which do not recurse
            vi.Name = NameOf(ix, rec);
            var used = new HashSet<string>(ReservedMembers) { vi.Name };
            Collect(ix, rec, 0, vi, used, vi.Name);
            var sb = new StringBuilder($"{vi.Size}|{(vi.IsUnion ? 'U' : 'S')}");
            foreach (var m in vi.Members) sb.Append($"|{m.Off:X}:{m.CName}:{m.T.Desc}");
            vi.Fp = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
            return vi;
        }
    }

    static void Collect(AstIndex ix, Record rec, int baseOff, VInfo vi, HashSet<string> used, string ownerName)
    {
        foreach (var f in rec.Fields)
        {
            if (f.Bitfield) { vi.Bad = true; vi.BadWhy = "bitfield " + f.Name; continue; }
            if (f.Name == "")
            {
                var key = f.Type.StartsWith("struct ") ? f.Type[7..] : f.Type.StartsWith("union ") ? f.Type[6..] : f.Type;
                var inner = ix.FindRecord(key);
                if (inner == null) { vi.Bad = true; vi.BadWhy = "unresolved anonymous member"; continue; }
                Collect(ix, inner, baseOff + f.Offset, vi, used, ownerName);
                continue;
            }
            var hint = Sanitize(f.Name);
            var tr = Resolve(ix, f.Type, ownerName, hint, null);
            if (tr == null) { vi.Bad = true; vi.BadWhy = $"field {f.Name}: {f.Type}"; continue; }
            var bn = hint;
            while (!used.Add(bn)) bn += "_";
            string raw = "";
            if (tr.K is 'P' or 'Q') { raw = bn + "_"; while (!used.Add(raw)) raw += "_"; }
            var setN = bn + "_Set"; while (!used.Add(setN)) setN += "_";
            if (raw != "") { var rs = raw + "_Set"; while (!used.Add(rs)) rs += "_"; }
            vi.Members.Add(new VMember { CName = f.Name, Name = Keywords.Contains(bn) ? "@" + bn : bn, Raw = raw, Off = baseOff + f.Offset, T = tr });
        }
    }

    /// <summary>The member of a record that stands for a MemberExpr's field (by C name and offset).</summary>
    public static VMember? Find(AstIndex ix, Record rec, Field f)
    {
        foreach (var m in Info(ix, rec).Members) if (m.CName == f.Name && m.Off == f.Offset) return m;
        return null;
    }

    /// <summary>Anonymous nested records that got a name from their field (they need a view of their own).</summary>
    public static List<Record> NamedAnonymous(AstIndex ix) { lock (Cache) return AnonList.TryGetValue(ix, out var l) ? l.ToList() : new(); }
}

/// <summary>
/// The structs the emitter may use, from the list `--struct-views-out` wrote: one line per (name, layout) `name|fingerprint|C# type|unique`.
/// The decompiled C declares module-local structs whose names repeat with different layouts (`Actor` has several): the most used layout
/// keeps the name, the others become `Name_v2`, ... and the emitter picks the one whose fingerprint equals its translation unit's layout.
/// </summary>
public sealed class ViewSet
{
    public readonly Dictionary<(string, string), string> Cs = new();
    public readonly HashSet<string> Unique = new();     // names with one layout in the whole game: pointers to them are typed views
    public static ViewSet Load(string path)
    {
        var v = new ViewSet();
        foreach (var l in File.ReadLines(path))
        {
            var p = l.Split('|');
            if (p.Length != 4 || l.StartsWith('#')) continue;
            v.Cs[(p[0], p[1])] = p[2];
            if (p[3] == "1") v.Unique.Add(p[0]);
        }
        return v;
    }
    /// <summary>True when this translation unit's layout of the record has a generated view; <paramref name="cs"/> is its C# type.</summary>
    public bool Avail(AstIndex ix, Record rec, out VInfo vi, out string cs)
    {
        vi = StructViews.Info(ix, rec); cs = "";
        return vi.Name != "" && !vi.Bad && Cs.TryGetValue((vi.Name, vi.Fp), out cs!);
    }
    public string? CsName(string name, string fp) => Cs.GetValueOrDefault((name, fp));
}

/// <summary>`--struct-views-out`: collects the records of every translation unit and writes the C# views.</summary>
public sealed class StructViewGen
{
    sealed class Seen { public VInfo Info = null!; public int Units; }
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Seen>> byName = new();    // name -> fingerprint -> info
    readonly HashSet<string> warn = new();

    public void Add(AstIndex ix)
    {
        var done = new HashSet<string>();
        void One(Record r)
        {
            if (StructViews.NameOf(ix, r) == "" || (r.Fields.Count == 0 && r.Size == 0)) return;     // (an unnamed nested record is named by its parent's members)
            var vi = StructViews.Info(ix, r);
            var key = vi.Bad ? "bad:" + vi.BadWhy : vi.Fp;
            if (!done.Add(vi.Name + "|" + key)) return;
            var s = byName.GetOrAdd(vi.Name, _ => new()).GetOrAdd(key, _ => new Seen { Info = vi });
            Interlocked.Increment(ref s.Units);
        }
        foreach (var r in ix.Records.ToList()) One(r);
        // anonymous nested records got their names while the parents' members were computed
        // (nested anonymous records can hold more of them: repeat until none is new)
        for (int n = -1; n != StructViews.NamedAnonymous(ix).Count;) { n = StructViews.NamedAnonymous(ix).Count; foreach (var r in StructViews.NamedAnonymous(ix)) One(r); }
        lock (warn) foreach (var w in ix.Warnings) warn.Add(w);
    }

    public void Write(string dir, string okList, TextWriter log)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.cs")) File.Delete(f);
        // (name, fp) -> C# type; the most used layout of a name keeps the plain name
        var cs = new Dictionary<(string, string), VInfo>();
        var csName = new Dictionary<(string, string), string>();
        var unique = new HashSet<string>();
        int variants = 0, badCount = 0;
        var bad = new List<string>();
        foreach (var (name, fps) in byName.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var good = fps.Where(kv => !kv.Value.Info.Bad).OrderByDescending(kv => kv.Value.Units).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            foreach (var kv in fps.Where(kv => kv.Value.Info.Bad)) { bad.Add($"{name}: {kv.Value.Info.BadWhy}"); badCount++; }
            for (int i = 0; i < good.Count; i++)
            {
                csName[(name, good[i].Key)] = i == 0 ? name : $"{name}_v{i + 1}";
                cs[(name, good[i].Key)] = good[i].Value.Info;
            }
            variants += Math.Max(0, good.Count - 1);
            if (good.Count == 1 && fps.Count == 1) unique.Add(name);
        }
        // a struct that holds an unusable struct by value is unusable too
        for (bool again = true; again;)
        {
            again = false;
            foreach (var (k, vi) in cs.ToList())
                if (vi.Members.Any(m => Nested(m.T).Any(n => !csName.ContainsKey((n.Rec, n.Fp)))))
                {
                    cs.Remove(k); csName.Remove(k); again = true; bad.Add($"{k.Item1}: holds an unusable struct {string.Join(",", vi.Members.SelectMany(m => Nested(m.T)).Where(n => !csName.ContainsKey((n.Rec, n.Fp))).Select(n => n.Rec))}");
                    var other = cs.Keys.Count(x => x.Item1 == k.Item1); if (other != 1) unique.Remove(k.Item1);
                }
        }
        var arrays = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var files = new List<StringBuilder>();
        var cur = new StringBuilder();
        int inCur = 0;
        foreach (var (k, vi) in cs.OrderBy(kv => csName[kv.Key], StringComparer.Ordinal))
        {
            if (inCur >= 120) { files.Add(cur); cur = new StringBuilder(); inCur = 0; }
            RenderStruct(cur, vi, csName[k], csName, unique, arrays);
            inCur++;
        }
        files.Add(cur);
        const string head = "// <auto-generated> by Trw.Transpile --struct-views-out (tools/regen.ps1 -StructViews): typed views of the guest-memory structs (layouts from the clang AST, MIPS o32). </auto-generated>\n// DO NOT EDIT: regenerated by tools/regen.ps1 -StructViews. Names are stable (C name; layout variants Name_v2, Name_v3, ...). How to use them: docs/STRUCTS.md\n#pragma warning disable CS0108, CS8981, CS0649\nusing System.Runtime.CompilerServices;\nusing Trw.Platform;\nusing static Trw.Platform.ViewConst;\n\nnamespace Trw.Game;\n\n";
        for (int i = 0; i < files.Count; i++) File.WriteAllText(Path.Combine(dir, $"Structs{i:D2}.cs"), head + files[i]);
        var asb = new StringBuilder(head);
        foreach (var a in arrays.Values) asb.Append(a);
        File.WriteAllText(Path.Combine(dir, "ArrayViews.cs"), asb.ToString());
        File.WriteAllLines(okList, new[] { "# name|fingerprint|C# type|unique   (Trw.Transpile --struct-views-out)" }
            .Concat(cs.Keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => csName[k], StringComparer.Ordinal).Select(k => $"{k.Item1}|{k.Item2}|{csName[k]}|{(unique.Contains(k.Item1) ? 1 : 0)}")));
        log.WriteLine($"struct views: {cs.Count} structs ({variants} variants of names that repeat with other layouts), {arrays.Count} array views, {badCount} unusable layouts");
        foreach (var c in bad.OrderBy(x => x).Take(400)) log.WriteLine("  unusable " + c);
        foreach (var w in warn.OrderBy(x => x).Take(20)) log.WriteLine("  layout warning " + w);
    }

    static IEnumerable<TRef> Nested(TRef t)
    {
        if (t.K == 'N') yield return t;
        if (t.K == 'A') foreach (var n in Nested(t.Elem!)) yield return n;
    }

    static string Off(int o) => o == 0 ? "P" : $"P + 0x{o:X}u";

    static void Resolve(TRef t, Dictionary<(string, string), string> csName)
    {
        if (t.K == 'N') t.CsRec = csName[(t.Rec, t.Fp)];
        if (t.K == 'A') Resolve(t.Elem!, csName);
    }

    static string TypeText(TRef t, SortedDictionary<string, string> arrays)
    {
        switch (t.K)
        {
            case 'S': return t.Cs;
            case 'N': return t.CsRec;
            case 'Q': return t.ViewName;
            case 'A': EnsureArray(t, arrays); return t.ViewName;
            default: return "uint";
        }
    }

    static void EnsureArray(TRef t, SortedDictionary<string, string> arrays)
    {
        var name = t.ViewName;
        if (arrays.ContainsKey(name)) return;
        arrays[name] = "";   // recursion guard
        var e = t.Elem!;
        int stride = ElemSize(e);
        var sb = new StringBuilder();
        sb.Append($"/// <summary>{ElemDoc(e)}[{t.Count}] at a guest address.</summary>\npublic readonly struct {name}\n{{\n");
        sb.Append($"    public readonly uint P;\n    [MethodImpl(Inl)] public {name}(uint p) {{ P = p; }}\n");
        sb.Append($"    public const int Length = {t.Count};\n    public const int Stride = {stride};\n    public const int SizeOf = {t.Count * stride};\n");
        sb.Append($"    [MethodImpl(Inl)] public static implicit operator uint({name} v) => v.P;\n");
        if (e.K == 'S' || e.K is 'P' or 'Q' or 'R')
        {
            var ecs = e.K == 'S' ? e.Cs : "uint"; var g = e.K == 'S' ? e.Get : "U32";
            sb.Append($"    public {ecs} this[int i]\n    {{\n        [MethodImpl(Inl)] get => Mem.{g}(P + (uint)(i * {stride}));\n        [MethodImpl(Inl)] set => Mem.Set(P + (uint)(i * {stride}), value);\n    }}\n");
            sb.Append($"    /// <summary>Store usable on an rvalue view (C# forbids indexer setters on struct rvalues).</summary>\n    [MethodImpl(Inl)] public {ecs} Set(int i, {ecs} v) => Mem.Set(P + (uint)(i * {stride}), v);\n");
        }
        else
        {
            var et = TypeText(e, arrays);
            sb.Append($"    public {et} this[int i] {{ [MethodImpl(Inl)] get => new(P + (uint)(i * {stride})); }}\n");
        }
        sb.Append("}\n\n");
        arrays[name] = sb.ToString();
    }

    static string ElemDoc(TRef e) => e.K switch { 'S' => e.Cs, 'N' => e.CsRec, 'A' => e.ViewName, _ => "uint" };

    static int ElemSize(TRef t) => t.K switch
    {
        'S' => t.Size, 'N' => t.Size, 'A' => ElemSize(t.Elem!) * t.Count, _ => 4,
    };

    static void RenderStruct(StringBuilder sb, VInfo vi, string n, Dictionary<(string, string), string> csName, HashSet<string> unique, SortedDictionary<string, string> arrays)
    {
        foreach (var m in vi.Members) Resolve(m.T, csName);
        sb.Append($"/// <summary>{(vi.IsUnion ? "union" : "struct")} {vi.Name}, 0x{vi.Size:X} bytes, at a guest address.</summary>\npublic readonly struct {n}\n{{\n");
        sb.Append($"    public readonly uint P;\n    [MethodImpl(Inl)] public {n}(uint p) {{ P = p; }}\n");
        sb.Append($"    public const int SizeOf = {vi.Size};\n    public bool IsNull {{ [MethodImpl(Inl)] get => P == 0; }}\n");
        sb.Append($"    /// <summary>The i-th struct of an array that starts here.</summary>\n    [MethodImpl(Inl)] public {n} At(int i) => new(P + (uint)(i * {vi.Size}));\n");
        sb.Append($"    [MethodImpl(Inl)] public static implicit operator uint({n} v) => v.P;\n    [MethodImpl(Inl)] public static explicit operator {n}(uint p) => new(p);\n");
        foreach (var m in vi.Members)
        {
            var t = m.T; var a = Off(m.Off);
            sb.Append($"    // +0x{m.Off:X}\n");
            switch (t.K)
            {
                case 'S':
                    sb.Append($"    public {t.Cs} {m.Name}\n    {{\n        [MethodImpl(Inl)] get => Mem.{t.Get}({a});\n        [MethodImpl(Inl)] set => Mem.Set({a}, value);\n    }}\n");
                    sb.Append($"    [MethodImpl(Inl)] public {t.Cs} {m.SetName(m.Name)}({t.Cs} v) => Mem.Set({a}, v);\n");
                    break;
                case 'N':
                    sb.Append($"    public {t.CsRec} {m.Name} {{ [MethodImpl(Inl)] get => new({a}); }}\n");
                    break;
                case 'A':
                    sb.Append($"    public {TypeText(t, arrays)} {m.Name} {{ [MethodImpl(Inl)] get => new({a}); }}\n");
                    break;
                case 'P': case 'Q':
                {
                    bool typed = t.K == 'Q' || unique.Contains(t.Rec);
                    var tn = t.K == 'Q' ? t.ViewName : t.Rec;
                    if (typed)
                        sb.Append($"    public {tn} {m.Name}\n    {{\n        [MethodImpl(Inl)] get => new(Mem.U32({a}));\n        [MethodImpl(Inl)] set => Mem.Set({a}, value.P);\n    }}\n");
                    sb.Append($"    public uint {m.Raw}\n    {{\n        [MethodImpl(Inl)] get => Mem.U32({a});\n        [MethodImpl(Inl)] set => Mem.Set({a}, value);\n    }}\n");
                    sb.Append($"    [MethodImpl(Inl)] public uint {m.SetName(m.Raw)}(uint v) => Mem.Set({a}, v);\n");
                    break;
                }
                default:
                    sb.Append($"    public uint {m.Name}\n    {{\n        [MethodImpl(Inl)] get => Mem.U32({a});\n        [MethodImpl(Inl)] set => Mem.Set({a}, value);\n    }}\n");
                    sb.Append($"    [MethodImpl(Inl)] public uint {m.SetName(m.Name)}(uint v) => Mem.Set({a}, v);\n");
                    break;
            }
        }
        sb.Append("}\n\n");
    }
}
