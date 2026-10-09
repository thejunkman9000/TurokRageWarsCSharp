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
    readonly List<(string id, string[] chain)> indirect = new();

    public AstIndex(JsonNode root)
    {
        Walk(root);
        foreach (var r in byId.Values) Layout(r);
        foreach (var (id, chain) in indirect)
            if (chain.All(c => FieldsById.ContainsKey(c)))
                FieldsById[id] = new Field { Id = id, Offset = chain.Sum(c => FieldsById[c].Offset), Name = FieldsById[chain[^1]].Name, Type = FieldsById[chain[^1]].Type, Bitfield = chain.Any(c => FieldsById[c].Bitfield) };
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
                        indirect.Add(((string)c["id"]!, c["chain"]!.AsArray().Select(x => (string)x!["id"]!).ToArray()));
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
                        var key = f.Type.StartsWith("struct ") ? f.Type[7..] : f.Type.StartsWith("union ") ? f.Type[6..] : f.Type;
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
            if ((string?)c["kind"] == "EnumDecl") continue;
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





