using System.Text;
using System.Text.Json;

namespace Trw.Tools.Levels;

/// <summary>
/// `trw levels [rom] [outdir]` - prints the ROM resource index and level list and dumps readable text/JSON for level records.
/// args may include a leading "levels"; the first existing-file argument is the ROM, the next is the output directory.
/// </summary>
public static class LevelsCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

    public static int Run(string[] args)
    {
        var a = args.Where((x, i) => !(i == 0 && x.Equals("levels", StringComparison.OrdinalIgnoreCase))).ToList();
        string rom = DefaultRom;
        if (a.Count > 0 && File.Exists(a[0])) { rom = a[0]; a.RemoveAt(0); }
        string outDir = a.Count > 0 ? a[0] : Path.Combine("extracted", "levels");

        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }
        var ix = RomIndex.Load(File.ReadAllBytes(rom));
        var levels = LevelCatalog.Load(ix);
        var meta = LevelCatalog.LoadMeta(ix);
        Directory.CreateDirectory(outDir);

        var idx = new StringBuilder();
        idx.AppendLine("ROMLookupTable @ 0x28B244 -> regions");
        for (int i = 0; i < ix.Regions.Length; i++)
            idx.AppendLine($"  region {i} [0x{ix.Regions[i].Start:X6}, 0x{ix.Regions[i].End:X6}) {ix.Regions[i].End - ix.Regions[i].Start,8} bytes  {RomIndex.RegionNames[i]}");
        idx.AppendLine();
        idx.AppendLine($"Main archive @ 0x{RomIndex.ArchiveOffset:X6}: {ix.Root.Children.Count} sections");
        for (int i = 0; i < ix.Root.Children.Count; i++)
        {
            var n = ix.Root.Children[i];
            idx.AppendLine($"  section {i,2} @0x{n.Offset:X6} {n.Size,8} bytes {n.Kind,-9} children={n.Children.Count,3}  {SectionNames.Describe(i)}");
        }
        idx.AppendLine();
        idx.AppendLine("Levels (archive section 5):");
        idx.AppendLine("  id  rom       name               menuSlot  map file");
        foreach (var lv in levels)
        {
            var m = meta.FirstOrDefault(x => x.LevelId == lv.Id);
            idx.AppendLine($"  {lv.Id,2}  0x{lv.Node.Offset:X6}  {lv.Name,-18} {(m == null ? "-" : m.Slot.ToString()),3}       {(m == null ? "" : m.MapName)}");
        }
        Console.Write(idx.ToString());
        File.WriteAllText(Path.Combine(outDir, "resource_index.txt"), idx.ToString());

        DumpChunkTable(ix, Path.Combine(outDir, "chunks.tsv"));
        DumpGlobals(ix, Path.Combine(outDir, "globals"));
        foreach (var lv in levels)
        {
            if (lv.Node.Kind != NodeKind.Container || lv.Node.Children.Count < 13) continue;
            DumpLevel(lv, meta.FirstOrDefault(x => x.LevelId == lv.Id), Path.Combine(outDir, "levels", $"{lv.Id:D2}_{Safe(lv.Name)}"));
        }
        Console.WriteLine($"\nwrote {outDir}");
        return 0;
    }

    static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

        static readonly JsonWriterOptions Jo = new() { Indented = true };

    static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, Jo);
        body(w);
    }

    static void DumpChunkTable(RomIndex ix, string path)
    {
        var sb = new StringBuilder("rom\tpath\tkind\trole\tpacked\tunpacked\n");
        foreach (var n in ix.All())
        {
            if (n.Kind != NodeKind.Rnc) continue;
            var h = Rnc.ReadHeader(ix.Rom, n.Offset);
            sb.Append($"0x{n.Offset:X6}\t{n.PathString}\t{n.Kind}\t{SectionNames.Role(n.Path)}\t{h.PackedSize}\t{h.UnpackedSize}\n");
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void DumpGlobals(RomIndex ix, string dir)
    {
        Directory.CreateDirectory(dir);
        for (int s = 0; s < ix.Root.Children.Count; s++)
        {
            if (s == 5) continue; // levels are dumped separately
            var n = ix.Root.Children[s];
            var sb = new StringBuilder();
            sb.AppendLine($"# archive section {s}: {SectionNames.Describe(s)}  (ROM 0x{n.Offset:X6}, {n.Size} bytes, {n.Kind})");
            if (n.Kind == NodeKind.Container)
            {
                int shown = 0;
                foreach (var c in n.Children)
                {
                    if (shown++ >= 8) { sb.AppendLine($"... {n.Children.Count - 8} more children"); break; }
                    sb.AppendLine($"## child {c.PathString} @0x{c.Offset:X6} {c.Size} bytes {c.Kind}");
                    sb.Append(Describe.Tree(ix.Read(c), 3, 8, 3));
                }
            }
            else sb.Append(Describe.Tree(ix.Read(n), 4, 12, 4));
            File.WriteAllText(Path.Combine(dir, $"sec{s:D2}.txt"), sb.ToString());
        }
        // fully decoded per-level metadata table
        var meta = LevelCatalog.LoadMeta(ix);
        WriteJson(Path.Combine(dir, "level_meta.json"), w =>
        {
            w.WriteStartArray();
            foreach (var m in meta)
            {
                w.WriteStartObject();
                w.WriteNumber("menuSlot", m.Slot); w.WriteNumber("levelId", m.LevelId);
                w.WriteNumber("value4", m.Field4); w.WriteNumber("value8", m.Field8); w.WriteNumber("value12", m.FieldC);
                w.WriteString("mapName", m.MapName); w.F("float60", m.Float38);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    static void DumpLevel(Level lv, LevelMeta? meta, string dir)
    {
        Directory.CreateDirectory(dir);
        // generic structural text of every part
        var sb = new StringBuilder();
        sb.AppendLine($"# level {lv.Id} \"{lv.Name}\"  ROM 0x{lv.Node.Offset:X6}  {lv.Node.Size} bytes");
        for (int p = 0; p < lv.Node.Children.Count; p++)
        {
            var c = lv.Part(p);
            sb.AppendLine($"## part {p}: {SectionNames.LevelPart(p)}  @0x{c.Offset:X6} {c.Size} bytes {c.Kind}");
            if (p == 5) { sb.AppendLine($"  {c.Children.Count} sector chunks (see sectors.json)"); continue; }
            sb.Append(Describe.Tree(lv.ReadPart(p), 3, 8, 4));
        }
        File.WriteAllText(Path.Combine(dir, "parts.txt"), sb.ToString());

        WriteJson(Path.Combine(dir, "level.json"), w =>
        {
            w.WriteStartObject();
            w.WriteNumber("id", lv.Id); w.WriteString("name", lv.Name);
            w.WriteString("romOffset", $"0x{lv.Node.Offset:X6}");
            if (meta != null) { w.WriteString("mapFile", meta.MapName); w.WriteNumber("menuSlot", meta.Slot); }
            w.WriteString("params24", Bin.Hex(lv.Params24()));
            var p6 = lv.Part6();
            w.WriteStartArray("part6_stride44");
            for (int i = 0; i < p6.Count; i++) w.WriteStringValue(Bin.Words(p6.Record(i)));
            w.WriteEndArray();
            w.WriteEndObject();
        });

        WriteJson(Path.Combine(dir, "actors.json"), w =>
        {
            w.WriteStartArray();
            foreach (var x in lv.Actors())
            {
                w.WriteStartObject(); w.WriteNumber("i", x.Index);
                w.F("x", x.X); w.F("y", x.Y); w.F("z", x.Z);
                w.F("sx", x.ScaleX); w.F("sy", x.ScaleY); w.F("sz", x.ScaleZ);
                w.WriteString("word1C", $"0x{x.Word1C:X8}"); w.WriteString("typeWord20", $"0x{x.Word20:X8}"); w.WriteString("word24", $"0x{x.Word24:X8}");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        var nodes = lv.NavNodes();
        var edges = lv.NavEdges();
        var mat = lv.NavMatrix(out int n);
        WriteJson(Path.Combine(dir, "nav.json"), w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("nodes");
            foreach (var x in nodes)
            {
                w.WriteStartObject(); w.WriteNumber("i", x.Index);
                w.F("x", x.X); w.F("y", x.Y); w.F("z", x.Z);
                w.WriteString("flags", $"0x{x.Flags:X4}"); w.WriteString("objectType", $"0x{x.ObjectType:X4}");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("edges");
            foreach (var e in edges)
            {
                w.WriteStartObject(); w.WriteNumber("from", e.From); w.WriteNumber("to", e.To);
                w.WriteNumber("type", e.Type); w.WriteNumber("extra", e.Extra); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("matrixSize", n);
            w.WriteStartArray("matrixRows");
            for (int r = 0; r < n; r++)
            {
                var sbr = new StringBuilder(n);
                for (int c = 0; c < n; c++) sbr.Append(mat[r * n + c] != 0 ? '1' : '0');
                w.WriteStringValue(sbr.ToString());
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        var grid = lv.Grid();
        WriteJson(Path.Combine(dir, "grid.json"), w =>
        {
            w.WriteStartObject();
            if (grid != null)
            {
                w.WriteNumber("columns", grid.Columns); w.WriteNumber("rows", grid.Rows);
                void Arr(string name, float[] f) { w.WriteStartArray(name); foreach (var v in f) w.FV(v); w.WriteEndArray(); }
                Arr("colXMin", grid.ColXMin); Arr("colXMax", grid.ColXMax); Arr("rowZMin", grid.RowZMin); Arr("rowZMax", grid.RowZMax);
                w.WriteStartArray("cells");
                foreach (var c in grid.Cells)
                {
                    w.WriteStartObject();
                    w.WriteNumber("col", c.Column); w.WriteNumber("row", c.Row);
                    w.F("xMin", c.XMin); w.F("zMin", c.ZMin); w.F("xMax", c.XMax); w.F("zMax", c.ZMax);
                    w.WriteBoolean("hasSector", c.HasSector);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });

        WriteJson(Path.Combine(dir, "sectors.json"), w =>
        {
            w.WriteStartArray();
            foreach (var s in lv.Sectors())
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index); w.WriteString("romOffset", $"0x{s.Node.Offset:X6}");
                w.WriteStartArray("placements1C"); // section 0, stride 0x1C, objectId u16 at +8
                for (int i = 0; i < s.StaticPlacements.Count; i++)
                {
                    var r = s.StaticPlacements.Record(i);
                    w.WriteStartObject(); w.WriteNumber("objectId", Bin.U16(r, 8)); w.WriteString("raw", Bin.Words(r)); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("placements34"); // section 1, stride 0x34
                for (int i = 0; i < s.Placements34.Count; i++)
                {
                    var r = s.Placements34.Record(i);
                    w.WriteStartObject(); w.WriteNumber("objectId", Bin.U16(r, 0x28));
                    w.F("x", Bin.F32(r, 4)); w.F("y", Bin.F32(r, 8)); w.F("z", Bin.F32(r, 12));
                    w.WriteString("raw", Bin.Words(r)); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("records20Count", s.Records20.Count);
                w.WriteStartArray("records4C");
                for (int i = 0; i < s.Records4C.Count; i++) w.WriteStringValue(Bin.Words(s.Records4C.Record(i)));
                w.WriteEndArray();
                w.WriteString("section4", Bin.Hex(s.Section4));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }
}

internal static class JsonExt
{
    public static void F(this Utf8JsonWriter w, string name, float v) { if (float.IsFinite(v)) w.WriteNumber(name, v); else w.WriteString(name, v.ToString()); }
    public static void FV(this Utf8JsonWriter w, float v) { if (float.IsFinite(v)) w.WriteNumberValue(v); else w.WriteStringValue(v.ToString()); }
}

/// <summary>Names/roles (verified vs hypothesis noted in docs/formats/levels_records.md).</summary>
public static class SectionNames
{
    public static string Describe(int s) => s switch
    {
        0 => "static objects: 347 x container(4) {?,models,?,events} [verified: loader labels 'stat object index'/'stat model'/'static events']",
        1 => "table of 169 x 40-byte records ('bllevel data index' loader, hypothesis)",
        2 => "u16[347] per static-object attribute (hypothesis)",
        3 => "507 RNC model-like containers (3 x 169) (hypothesis)",
        4 => "object-type tables: raw blob, u32[224], 60-byte x 224 (hypothesis)",
        5 => "LEVEL DIRECTORY: 37 levels x 13 parts [verified: func_00286A78]",
        6 => "per-level small container tables (37) + 2 (hypothesis)",
        7 => "per-level small container tables (37) (hypothesis)",
        8 => "u32[321] (ids from 0xBC2) + 321 x 20-byte records with x,y,z (hypothesis)",
        9 => "empty stub (8 bytes)",
        10 => "empty stub (RNC, 8 bytes)",
        11 => "18 variable blobs (120/72 bytes) (hypothesis)",
        12 => "22 x 8-byte records (colours?) (hypothesis)",
        13 => "36 x 24-byte records (hypothesis)",
        14 => "u32[24]",
        15 => "u32[24]",
        16 => "69 RNC blobs",
        17 => "u32[69] (index for section 16?) (hypothesis)",
        18 => "item 0: LEVEL META 37 x 76 bytes [verified layout]; items 1..37: per-level containers",
        19 => "u32[1]",
        20 => "empty stub",
        21 => "36 RNC blobs of ~3.4 KB (hypothesis)",
        22 => "one 16-byte record 'NULL' (string)",
        23 => "46 small containers/records (hypothesis)",
        _ => "?",
    };

    public static string LevelPart(int p) => p switch
    {
        0 => "params (24 raw bytes)  [loader: raw -> scene+0x1B2B0]",
        1 => "container(4) {raw 0x70 header, array16, array0x14, raw 0x20} (callback D_44D024) [verified loader, semantics hypothesis]",
        2 => "empty array in all shipped levels (callback D_44E6A4)",
        3 => "placed actors: array stride 0x28, column-major (callback D_44D408) [layout verified, meaning hypothesis]",
        4 => "grid description: 4 float arrays (col/row extents) + cell rects [verified]",
        5 => "grid sector chunks (1 per cell, container(5)) [verified]",
        6 => "array stride 44 x 16 (scene+0x1B300/0x1B304)",
        7 => "container(4) of empty/small 0x14-stride arrays (callback D_44CD58)",
        8 => "level name (ASCII, NUL padded) [verified]",
        9 => "nav node array stride 16 [verified layout]",
        10 => "nav edge array stride 6 [verified layout]",
        11 => "nav node N x N byte matrix [verified]",
        12 => "container(1) of one 24-byte block (6 words copied to scene+0x1B910..)",
        _ => "?",
    };

    public static string Role(int[] p)
    {
        if (p.Length == 0) return "archive";
        switch (p[0])
        {
            case 0:
                if (p.Length >= 3) return p[2] switch { 0 => "stat-object part0", 1 => "stat-object model", 2 => "stat-object part2", 3 => "stat-object events", _ => "stat-object" };
                return "stat-object";
            case 3: return "section3 container (model-like)";
            case 5:
                if (p.Length >= 4 && p[2] == 5) return "level sector";
                if (p.Length >= 3) return "level part " + p[2];
                return "level";
            default: return "section " + p[0];
        }
    }
}
