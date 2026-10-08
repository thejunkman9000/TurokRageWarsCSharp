using System.Buffers.Binary;
using System.Text.Json;
using Trw.Tools.Defs;
using Trw.Tools.Models;

namespace Trw.Tools.Anim;

/// <summary>
/// Character skins of object 174 (the player / bot body). Writes <c>skins/tex_NNN_s{k}.png</c> and <c>skins.json</c> next to the creature export.
/// There is NO runtime palette recolour for actors: the draw call passes the actor byte +3 (= player stats +0x81 = skin 0..2) as the
/// texture "key" of func_00269A80, which selects frame = key % frames and palette = key % palettes of every multi-frame texture.
/// Part visibility (shield, flag, status overlay) is selected by (material byte +0xA &amp; 0x38) in func_0026B6A0.
/// Format and evidence: docs/formats/creatures_export.md section 8.
/// </summary>
public static class SkinExport
{
    public const int SkinCount = 3;
    const uint NameTableVa = 0x800E3820, CharRowsVa = 0x800E3A50, ObjIdTableVa = 0x800CE430;
    const int CharRowStride = 0x70, CharRowCount = 17;

    /// <summary>Visibility rule of a part group (material byte +0xA &amp; 0x38), func_0026B6A0 (normal characters).</summary>
    public static (string Name, bool DefaultVisible, string Condition) Group(int g) => g switch
    {
        0x38 => ("shield", false, "drawn only while the actor is in the protected/ghost state (actor+0x11D8 > 0 draws it via func_0021A2D4, or actor+0x122C & 0x8000): translucent aura shell, material alpha 0x2A..0x4B"),
        0x30 => ("mode_item", false, "drawn only when D_80146918 != 0 and the player's stats +0x8F == 1 (game mode specific carried item; the textured banner on a pole is the flag, hypothesis)"),
        0x10 => ("status_overlay", false, "drawn only while actor+0x11EC > 0 (timer set in func_00282E6C when an effect of type 0x40F hits the pawn; which status this is was not identified)"),
        _ => ("always", true, "always drawn (low 3 bits of the byte are a limb/zone id, not used for visibility)"),
    };

    public static void Write(string outDir, byte[] rom, List<CiTexture?> textures, List<(int ModelIndex, Model Model)> models174, string defsDir)
    {
        string skinDir = Path.Combine(outDir, "skins");
        Directory.CreateDirectory(skinDir);
        var used = new SortedSet<int>();
        foreach (var (_, m) in models174)
            foreach (var p in m.Parts) if (p.Material.TextureId >= 0) used.Add(p.Material.TextureId);

        using var fs = File.Create(Path.Combine(outDir, "skins.json"));
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("rule", "skin k (0..2) -> for every material texture: frame = k % frames, palette = k % palettes (func_00269A80 with key = actor+3 = stats+0x81). Textures with 1 frame and 1 palette look the same for every skin. No other recolour is applied to actors.");
        w.WriteNumber("skinCount", SkinCount);
        w.WriteString("fileTemplate", "skins/tex_{texture:D3}_s{skin}.png");

        w.WriteStartArray("textures");
        foreach (int id in used)
        {
            var t = id < textures.Count ? textures[id] : null;
            if (t == null) continue;
            w.WriteStartObject();
            w.WriteNumber("texture", id);
            w.WriteNumber("width", t.Width); w.WriteNumber("height", t.Height);
            w.WriteNumber("frames", t.Frames.Length); w.WriteNumber("palettes", t.Palettes.Length);
            w.WriteNumber("formatByte", t.FormatByte);
            bool varies = t.Frames.Length > 1 || t.Palettes.Length > 1;
            w.WriteBoolean("variesBySkin", varies);
            w.WriteStartArray("files");
            for (int k = 0; k < SkinCount; k++)
            {
                var (f, p) = t.Select(k);
                string name = $"tex_{id:D3}_s{k}.png";
                PngWriter.WriteRgba(Path.Combine(skinDir, name), t.Width, t.Height, t.ToRgba(f, p));
                w.WriteStringValue("skins/" + name);
            }
            w.WriteEndArray();
            w.WriteStartArray("frameForSkin"); for (int k = 0; k < SkinCount; k++) w.WriteNumberValue(t.Select(k).Frame); w.WriteEndArray();
            w.WriteStartArray("paletteForSkin"); for (int k = 0; k < SkinCount; k++) w.WriteNumberValue(t.Select(k).Palette); w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // visibility groups
        w.WriteStartArray("partGroups");
        foreach (int g in new[] { 0, 0x10, 0x30, 0x38 })
        {
            var (name, vis, cond) = Group(g);
            w.WriteStartObject();
            w.WriteNumber("group", g); w.WriteString("name", name); w.WriteBoolean("defaultVisible", vis); w.WriteString("condition", cond);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // per chunk materials
        w.WriteStartArray("chunks");
        foreach (var (idx, m) in models174)
        {
            w.WriteStartObject();
            w.WriteNumber("chunk", idx);
            w.WriteString("file", idx == 0 ? "obj_174.json" : $"obj_174_m{idx}.json");
            w.WriteString("kind", idx <= 15 ? "body" : "weapon");
            w.WriteStartArray("materials");
            for (int p = 0; p < m.Parts.Length; p++)
            {
                var mt = m.Parts[p].Material;
                var (name, vis, _) = Group(mt.FieldA & 0x38);
                w.WriteStartObject();
                w.WriteNumber("part", p); w.WriteNumber("texture", mt.TextureId); w.WriteNumber("flags", mt.Flags);
                w.WriteNumber("partGroup", mt.FieldA & 0x38); w.WriteString("visibility", name); w.WriteBoolean("defaultVisible", vis);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // characters: menu name table x pawn records
        WriteCharacters(w, rom, defsDir);
        w.WriteEndObject();
        Console.WriteLine($"skins: {used.Count} textures x {SkinCount} skins written to {skinDir}");
    }

    static uint U32(byte[] rom, uint va) => BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(J.VaToRom(va)));

    static void WriteCharacters(Utf8JsonWriter w, byte[] rom, string defsDir)
    {
        // pawn records (class 11) from trw defs: the game finds the def for character id c with func_0028CF7C(world, 11, c): first class-11 record whose s16 at +0xC (appearanceA[0..1]) == c
        var pawns = new List<(int Intel, int[] A)>();
        string intelPath = Path.Combine(defsDir, "intelligence.json");
        if (File.Exists(intelPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(intelPath));
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("class").GetInt32() != 11) continue;
                var a = r.GetProperty("fields").GetProperty("appearanceA");
                pawns.Add((r.GetProperty("index").GetInt32(), [a[0].GetInt32(), a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32()]));
            }
        }
        w.WriteString("characterNote", "Menu character c (17 entries, 3 skin names each, ROM name table 0x800E3820 + row table 0x800E3A50) has character id = stats+0x80. The actor def is the first class-11 pawn record with s16(+0xC) == id (func_0028CF7C); its byte +0xE (appearanceA[2]) is actor+1 = the model chunk of object 174 (func_0021A2D4 -> func_0024BFC4: chunk = value % chunkCount). The object id comes from D_800CE430[id] (1120 = object 174, 1121..1123 = other objects). Skin = stats+0x81 in 0..2 (func_0042E640 clamps to 2).");
        w.WriteStartArray("characters");
        for (int row = 0; row < CharRowCount; row++)
        {
            int id = (int)U32(rom, (uint)(CharRowsVa + row * CharRowStride + 4));
            var names = new string[SkinCount];
            for (int k = 0; k < SkinCount; k++)
            {
                uint p1 = U32(rom, (uint)(NameTableVa + (k * CharRowCount + row) * 8));
                names[k] = J.StringAt(rom, U32(rom, p1)) ?? "";
            }
            uint objId = id >= 0 && id < 24 ? U32(rom, (uint)(ObjIdTableVa + id * 4)) : 0;
            int pawn = -1, chunk = -1;
            foreach (var (intel, a) in pawns)
                if (((a[0] << 8) | a[1]) == id) { pawn = intel; chunk = a[2]; break; }
            w.WriteStartObject();
            w.WriteNumber("menuRow", row);
            w.WriteNumber("characterId", id);
            w.WriteStartArray("skinNames"); foreach (var n in names) w.WriteStringValue(n); w.WriteEndArray();
            w.WriteNumber("objectId", objId);
            w.WriteNumber("object", objId == 1120 ? 174 : -1);
            w.WriteNumber("pawnIntelligence", pawn);
            w.WriteNumber("bodyChunk", objId == 1120 ? chunk : -1);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
