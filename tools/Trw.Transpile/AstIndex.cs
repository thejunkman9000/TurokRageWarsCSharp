using System.Text.Json.Nodes;

namespace Trw.Transpile;

public sealed class Field
{
    public string Name = "";
    public string Type = "";
    public string Id = "";
    public int Offset;
    public bool Bitfield;
}

public sealed class Record
{
    public string Id = "";
    public string Name = "";
    public bool IsUnion;
    public List<Field> Fields = new();
    public int Size, Align = 1;
    public bool LaidOut;
}

/// <summary>One pass over the AST: struct layouts (o32), typedef -> record, enum constant values, field offsets by decl id.</summary>
public sealed class AstIndex
{
    readonly Dictionary<string, Record> byId = new();
    readonly Dictionary<string, Record> byName = new();
    readonly Dictionary<string, string> typedefToRecordId = new();
    public readonly Dictionary<string, string> Typedefs = new();
    public readonly Dictionary<string, long> EnumValues = new();
    /// <summary>Names of functions with internal linkage in this translation unit, and the bodies of those defined here or in headers.</summary>
    public readonly HashSet<string> StaticFunctions = new();
    /// <summary>`f32 sqrtf_(f32) __asm__("sqrtf");` : C name -> linker symbol (the AST does not carry the label text).</summary>
    public readonly Dictionary<string, string> AsmLabels = new();
    public readonly Dictionary<string, JsonNode> StaticDefinitions = new();
    public readonly Dictionary<string, Field> FieldsById = new();
    readonly Dictionary<string, Record> byQual = new();       // "struct (unnamed struct at f:l:c)" -> record
    readonly List<(string id, string[]? chain, Record parent, string name)> indirect = new();

    public AstIndex(JsonNode root)
    {
        Walk(root);
        foreach (var r in byId.Values) Layout(r);
        foreach (var (id, chain, parent, iname) in indirect)
            if (chain == null)
            {
                // newer clang omits "chain": find the member by name inside the anonymous struct/union members of the parent
                var hit = FindInAnon(parent, iname);
                if (hit != null) FieldsById[id] = new Field { Id = id, Offset = hit.Value.off, Name = hit.Value.f.Name, Type = hit.Value.f.Type, Bitfield = hit.Value.f.Bitfield };
            }
            else if (chain.All(c => FieldsById.ContainsKey(c)))
                FieldsById[id] = new Field { Id = id, Offset = chain.Sum(c => FieldsById[c].Offset), Name = FieldsById[chain[^1]].Name, Type = FieldsById[chain[^1]].Type, Bitfield = chain.Any(c => FieldsById[c].Bitfield) };
    }

    public IEnumerable<Record> Records => byId.Values;

    /// <summary>Typedef names that stand for the record (typedefs of anonymous structs are the only name those have).</summary>
    public List<string> TypedefNamesOf(Record r)
    {
        var l = typedefToRecordId.Where(kv => kv.Value == r.Id).Select(kv => kv.Key).ToList();
        if (r.Name != "")      // `typedef struct Tag Name;` before the definition: the typedef refers to the forward declaration
            foreach (var kv in Typedefs)
                if ((kv.Value == "struct " + r.Name || kv.Value == "union " + r.Name) && !l.Contains(kv.Key)) l.Add(kv.Key);
        return l;
    }

    (Field f, int off)? FindInAnon(Record rec, string name)
    {
        foreach (var f in rec.Fields)
        {
            if (f.Name != "") continue;
            var key = f.Type.StartsWith("struct ") ? f.Type[7..] : f.Type.StartsWith("union ") ? f.Type[6..] : f.Type;
            if (!byQual.TryGetValue(key, out var nested)) continue;
            foreach (var g in nested.Fields)
                if (g.Name == name) return (g, (int)(f.Offset + g.Offset));
            var deeper = FindInAnon(nested, name);
            if (deeper != null) return (deeper.Value.f, (int)(f.Offset + deeper.Value.off));
        }
        return null;
    }

    void Walk(JsonNode n)
    {
        var kind = (string?)n["kind"];
        switch (kind)
        {
            case "RecordDecl" when n["completeDefinition"]?.GetValue<bool>() == true:
            {
                var r = new Record { Id = (string)n["id"]!, Name = (string?)n["name"] ?? "", IsUnion = (string?)n["tagUsed"] == "union" };
                Record? lastNested = null;
                foreach (var c in n["inner"]?.AsArray() ?? new JsonArray())
                {
                    if ((string?)c?["kind"] == "RecordDecl" && (string?)c["name"] == null) { Walk(c); lastNested = byId.GetValueOrDefault((string)c["id"]!); continue; }
                    if ((string?)c?["kind"] == "IndirectFieldDecl")
                    {
                        indirect.Add(((string)c["id"]!, c["chain"]?.AsArray().Select(x => (string)x!["id"]!).ToArray(), r, (string?)c["name"] ?? ""));
                        continue;
                    }
                    if ((string?)c?["kind"] != "FieldDecl") continue;
                    var f = new Field
                    {
                        Id = (string)c!["id"]!, Name = (string?)c["name"] ?? "",
                        Type = (string)c["type"]!["qualType"]!, Bitfield = c["isBitfield"]?.GetValue<bool>() == true,
                    };
                    if (lastNested != null && (f.Type.Contains("(unnamed") || f.Type.Contains("(anonymous")))
                    {
                        var ft = System.Text.RegularExpressions.Regex.Replace(f.Type, @"(\s*\*|\s*\[\d*\])+$", "");       // "struct (unnamed struct at f:l:c)[8]" is an array of the record
                        var key = ft.StartsWith("struct ") ? ft[7..] : ft.StartsWith("union ") ? ft[6..] : ft;
                        byQual[key] = lastNested;       // CType.Parse looks the type up without its struct/union tag
                    }
                    r.Fields.Add(f);
                    FieldsById[f.Id] = f;
                }
                byId[r.Id] = r;
                if (r.Name != "") byName[r.Name] = r;
                break;
            }
            case "FunctionDecl" when (string?)n["storageClass"] == "static":
            {
                var fname = (string)n["name"]!;
                StaticFunctions.Add(fname);
                if (n["inner"]?.AsArray().Any(c => (string?)c?["kind"] == "CompoundStmt") == true) StaticDefinitions[fname] = n;
                break;
            }
            case "TypedefDecl":
            {
                var tdq = (string?)n["type"]?["qualType"]; if (tdq != null) Typedefs[(string)n["name"]!] = tdq;
                var rid = FindRecordRef(n);
                if (rid != null) typedefToRecordId[(string)n["name"]!] = rid;
                break;
            }
            case "EnumConstantDecl":
            {
                var v = FindConstValue(n);
                if (v != null) EnumValues[(string)n["id"]!] = v.Value;
                break;
            }
        }
        if (n["inner"] is JsonArray inner)
            foreach (var c in inner) if (c != null) Walk(c);
    }

    static string? FindRecordRef(JsonNode n)
    {
        if ((string?)n["kind"] == "RecordType") return (string?)n["decl"]?["id"];
        foreach (var c in n["inner"]?.AsArray() ?? new JsonArray())
        {
            if (c == null) continue;
            // a typedef of a pointer, function pointer or array is not the record it mentions ("typedef int (*Fn)(Msg *)" is 4 bytes, not sizeof(Msg))
            if ((string?)c["kind"] is "EnumDecl" or "PointerType" or "FunctionProtoType" or "FunctionNoProtoType" or "ConstantArrayType" or "IncompleteArrayType" or "VariableArrayType" or "BlockPointerType" or "ReferenceType") continue;
            var r = FindRecordRef(c);
            if (r != null) return r;
        }
        return null;
    }

    static long? FindConstValue(JsonNode n)
    {
        foreach (var c in n["inner"]?.AsArray() ?? new JsonArray())
        {
            if (c == null) continue;
            if (c["value"] != null && long.TryParse((string?)c["value"], out var v)) return v;
            var r = FindConstValue(c);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Debug aid: every complete record with its size, alignment and field offsets (compare with clang's -fdump-record-layouts).</summary>
    public IEnumerable<string> DumpLayouts()
    {
        foreach (var r in byId.Values)
        {
            yield return $"record {(r.Name == "" ? "<unnamed " + r.Id + ">" : r.Name)} {(r.IsUnion ? "union" : "struct")} size={r.Size} align={r.Align}";
            foreach (var f in r.Fields) yield return $"  +{f.Offset:X} {f.Name} : {f.Type}";
        }
    }

    public Record? FindRecord(string name)
    {
        if (byName.TryGetValue(name, out var r)) return r;
        if (byQual.TryGetValue(name, out r)) return r;
        if (typedefToRecordId.TryGetValue(name, out var id) && byId.TryGetValue(id, out r)) return r;
        return null;
    }

    /// <summary>Problems met while computing layouts (a field type that could not be resolved would silently shrink a struct).</summary>
    public readonly List<string> Warnings = new();

    public void EnsureLayout(Record r) => Layout(r);

    void Layout(Record r)
    {
        if (r.LaidOut) return;
        r.LaidOut = true;
        int off = 0, align = 1, size = 0;
        foreach (var f in r.Fields)
        {
            var t = CType.Parse(f.Type, this);
            if (t.Kind == K.Unknown || (t.Kind == K.Array && t.Elem!.Kind == K.Unknown))
                Warnings.Add($"{(r.Name == "" ? "<unnamed " + r.Id + ">" : r.Name)}.{f.Name}: unresolved type '{f.Type}'");
            int fa = AlignOf(t);
            int fs = t.Kind == K.Record ? FindRecord(t.Name)!.Size : t.Size;      // the record may only have been laid out by the AlignOf call above
            if (f.Bitfield) { f.Offset = -1; continue; }   // bitfields are reported by the emitter when used
            align = Math.Max(align, fa);
            if (r.IsUnion) { f.Offset = 0; size = Math.Max(size, fs); }
            else { off = (off + fa - 1) / fa * fa; f.Offset = off; off += fs; size = off; }
        }
        r.Align = align;
        r.Size = (size + align - 1) / align * align;
    }

    int AlignOf(CType t)
    {
        switch (t.Kind)
        {
            case K.Array: return AlignOf(t.Elem!);
            case K.Record:
                var rec = FindRecord(t.Name)!;
                Layout(rec);
                return rec.Align;
            default: return Math.Max(1, Math.Min(t.Size, 8));
        }
    }
}





