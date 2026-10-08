using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>
/// The 24 sections of the main archive with the names the game itself gives them. Verified: the labels are the string arguments
/// of the loader calls in CScene::Construct (func_0044C410, overlay tail; asm in port/build/asm_func/func_0044C410.s), which loads
/// archive section N into a CScene field (offsets below are CScene offsets, D_8011FE88).
/// </summary>
static class ArchiveSections
{
    // index -> (game label, CScene field(s), post-load callback, meaning)
    static readonly (string Label, string Field, string Callback, string Meaning)[] Table =
    [
        ("objects index",            "+0x24 rom base / +0x54 header", "-", "347 object containers {extents, model list, animation list, events} (models agent)"),
        ("intelligence",             "+0x78",                         "func_0044E584", "169 behaviour/class definition records, variable size, first u32 = class (see Intelligence.cs)"),
        ("object types",             "+0x94",                         "-", "u16[347] per-object attribute (all 7 except a few)"),
        ("texture sets index",       "+0x20 / +0x50",                 "-", "507 3D textures (models agent)"),
        ("particle effects",         "+0xB4",                         "func_0044E454", "224 x 60-byte particle effect definitions + parameter blob"),
        ("levels index",             "+0x1C / +0x4C",                 "-", "level directory, 37 x container(13)"),
        ("(unnamed ROM-level load)", "+0x38 base, +0x44 size, buffer +0x80 = 0x800FD1F0", "-", "37 x container(3) of 32-byte blocks"),
        ("(unnamed ROM-level load)", "+0x3C base, +0x48 size, buffer +0x84 = 0x800F81F0", "-", "37 x container(1) of one 32-byte block"),
        ("warp dests",               "+0x10C ptr, +0x90 = first word", "-", "u32[321] group ids + 321 x 20-byte spawn points"),
        ("binaries index",           "+0x28 / +0x58",                 "-", "empty 8-byte stub in this ROM"),
        ("object types (2nd load)",  "+0x98",                         "-", "empty 8-byte array"),
        ("particle swooshes",        "+0x70",                         "-", "18 variable blobs (72 / 120 bytes)"),
        ("dynamic lights",           "+0xA0",                         "-", "typed array 22 x 8 bytes"),
        ("curves",                   "+0xA4",                         "-", "typed array 36 x 24 bytes"),
        ("rumbles",                  "+0xAC",                         "-", "u32[24]"),
        ("rumble types",             "+0xA8",                         "-", "u32[24]"),
        ("graphicset index",         "+0x2C / +0x5C",                 "-", "69 RNC blobs"),
        ("graphicset type",          "+0x9C",                         "-", "u32[69] ids for the graphicsets"),
        ("Cinemas index",            "+0x34 / +0x74",                 "-", "item 0 = level meta table 37 x 76, items 1..37 per-level cinema containers"),
        ("link group types",         "+0xB0",                         "-", "u32[1] = 1"),
        ("(not loaded)",             "-",                             "-", "empty stub"),
        ("BLLevelData Index",        "+0xC base, +0x10 size (func_0028D450 = CScene__GetBLLevelData)", "-", "36 x 3382-byte per-level campaign/progression records"),
        ("(not loaded)",             "-",                             "-", "16-byte 'NULL' string"),
        ("Bloodlust Weapon Data",    "func_0020A604 copies 44 x 28 bytes to D_800F7D20", "-", "46 x 32-byte bot weapon-fire configs (44 used)"),
    ];

    public static object Describe(RomIndex ix)
    {
        var list = new List<object>();
        for (int i = 0; i < ix.Root.Children.Count && i < Table.Length; i++)
        {
            var n = ix.Root.Children[i];
            var t = Table[i];
            list.Add(new
            {
                section = i,
                rom = "0x" + n.Offset.ToString("X6"),
                size = n.Size,
                kind = n.Kind.ToString(),
                children = n.Children.Count,
                gameLabel = t.Label,
                sceneField = t.Field,
                postLoadCallback = t.Callback,
                meaning = t.Meaning,
                status = "label+loader verified (func_0044C410); meaning of contents per gameplay_defs.md",
            });
        }
        return list;
    }
}
