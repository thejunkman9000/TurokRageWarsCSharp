using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>
/// Archive section 1, label "intelligence" (CScene+0x78): a container of 169 variable-size definition records. The first u32 of every record
/// is a class id (0,2,3,4,8,9,10,11,13 occur). Level placements (part 3 u16 at +0x22, sector section 0 u16 at +0xE, sector section 1 u16 at +0x2C)
/// index this table through func_0028CF48(scene, idx) and the spawn code stores the record as the actor's `def` (actor+0x18);
/// func_00246690 then selects the per-class behaviour table D_800D0680[*def] (class &lt; 15). Verified: that chain, the class-11 check in the
/// level loader (func_0044D408) and the record sizes. Field names below are hypotheses unless marked [V].
/// </summary>
sealed class IntelRecord
{
    public int Index;
    public uint Class;
    public byte[] Raw = [];

    public string ClassName => Class switch
    {
        0 => "prop",
        2 => "mover",
        3 => "pickup",
        4 => "turret",
        8 => "trigger8",
        9 => "marker",
        10 => "light",
        11 => "pawn",
        13 => "zone",
        _ => "class" + Class,
    };

    public Dictionary<string, object?> ToJson()
    {
        var d = new Dictionary<string, object?>
        {
            ["index"] = Index,
            ["class"] = Class,
            ["className"] = ClassName,
            ["size"] = Raw.Length,
            ["flags"] = J.H8(Bin.U32(Raw, 4)),
            ["activationRadius"] = J.F(Bin.F32(Raw, 8)),
        };
        d["fields"] = Intelligence.DecodeFields(this);
        d["raw"] = J.Hex(Raw);
        return d;
    }
}

static class Intelligence
{
    // (offset, kind, name, count). kinds: u32 s32 f32 u16x2 u8x4
    static readonly Dictionary<uint, (int Off, string Kind, string Name, int Count)[]> Specs = new()
    {
        [0] = [(0x0C, "u8x4", "bytesC", 1), (0x10, "u8x4", "bytes10", 1), (0x14, "u32", "field14", 1), (0x18, "f32", "extentA", 1), (0x1C, "f32", "extentB", 1), (0x20, "u32", "field20", 1), (0x24, "u32", "field24", 1)],
        [2] = [(0x0C, "u8x4", "bytesC", 1), (0x10, "u8x4", "bytes10", 1), (0x14, "u32", "field14", 1), (0x18, "u16x2", "field18", 1), (0x1C, "f32", "distA", 1), (0x20, "f32", "distB", 1), (0x24, "f32", "distC", 1), (0x28, "f32", "distD", 1), (0x30, "f32", "angleA", 1), (0x38, "u8x4", "bytes38", 1), (0x3C, "u8x4", "bytes3C", 1), (0x40, "f32", "distE", 1), (0x44, "f32", "distF", 1), (0x48, "f32", "angleB", 1), (0x4C, "f32", "angleC", 1), (0x5C, "f32", "field5C", 1), (0x60, "f32", "field60", 1), (0x64, "f32", "field64", 1), (0x68, "f32", "field68", 1), (0x6C, "f32", "field6C", 1), (0x70, "f32", "field70", 1)],
        [3] = [(0x0C, "u32", "field0C", 1), (0x10, "u8x4", "bytes10", 1), (0x14, "u32", "kind14", 1), (0x18, "u32", "field18", 1), (0x1C, "u16x2", "field1C", 1), (0x20, "f32", "paramA", 1), (0x24, "f32", "paramB", 1), (0x28, "u16x2", "refs28", 1), (0x2C, "f32", "scale", 3), (0x38, "u32", "field38", 1), (0x3C, "u32", "field3C", 1)],
        [4] = [(0x0C, "u8x4", "bytesC", 1), (0x14, "u32", "field14", 1), (0x18, "u32", "field18", 1), (0x28, "f32", "dimA", 1), (0x2C, "f32", "dimB", 1), (0x30, "f32", "dimC", 1), (0x34, "f32", "dimD", 1), (0x3C, "f32", "field3C", 1), (0x40, "u32", "field40", 1), (0x58, "f32", "turnRateA", 1), (0x5C, "f32", "twoPi", 1), (0x60, "f32", "field60", 1), (0x64, "f32", "twoPi2", 1), (0x68, "f32", "field68", 1), (0x6C, "f32", "angleLimit", 1), (0x7C, "f32", "field7C", 1)],
        [8] = [(0x0C, "u8x4", "bytesC", 1), (0x10, "u8x4", "bytes10", 1), (0x18, "f32", "extentA", 1), (0x1C, "f32", "extentB", 1), (0x20, "f32", "range", 1)],
        [9] = [(0x0C, "u8x4", "bytesC", 1), (0x10, "u8x4", "bytes10", 1), (0x14, "u32", "field14", 1), (0x18, "u32", "eventId", 1), (0x1C, "u32", "field1C", 1)],
        [10] = [(0x18, "f32", "extentA", 1), (0x1C, "f32", "extentB", 1), (0x20, "u8x4", "colorA", 1), (0x24, "f32", "radius", 1), (0x28, "u8x4", "colorB", 1), (0x2C, "u8x4", "bytes2C", 1)],
        [11] = [(0x0C, "u8x4", "appearanceA", 1), (0x10, "u8x4", "appearanceB", 1), (0x14, "u32", "field14", 1), (0x18, "s32", "health", 1), (0x1C, "f32", "speedScale", 1), (0x20, "f32", "field20", 1), (0x24, "u32", "field24", 1),
                (0x28, "f32", "skill", 9), (0x4C, "s32", "itemIds", 7), (0x68, "s32", "field68", 1), (0x6C, "f32", "multiplier", 8), (0x8C, "s32", "percent", 24),
                (0xEC, "f32", "speedA", 1), (0xF0, "f32", "speedB", 1), (0xF4, "f32", "rangeA", 1), (0xF8, "f32", "rangeB", 1), (0xFC, "f32", "aiScale", 3), (0x108, "s32", "aiParam", 32)],
        [13] = [(0x0C, "u8x4", "bytesC", 1), (0x10, "u8x4", "bytes10", 1), (0x14, "u32", "field14", 1), (0x18, "f32", "extentA", 1), (0x1C, "f32", "extentB", 1), (0x20, "u32", "targetId", 1)],
    };

    public static List<IntelRecord> Load(RomIndex ix)
    {
        var node = ix.Find(1) ?? throw new InvalidDataException("archive section 1 missing");
        var secs = Container.Sections(ix.Read(node)) ?? throw new InvalidDataException("intelligence is not a container");
        var list = new List<IntelRecord>();
        for (int i = 0; i < secs.Length; i++)
            list.Add(new IntelRecord { Index = i, Class = Bin.U32(secs[i], 0), Raw = secs[i] });
        return list;
    }

    public static object? DecodeFields(IntelRecord r)
    {
        if (!Specs.TryGetValue(r.Class, out var spec)) return null;
        var d = new Dictionary<string, object?>();
        foreach (var (off, kind, name, count) in spec)
        {
            var vals = new List<object?>();
            for (int i = 0; i < count; i++)
            {
                int o = off + 4 * i;
                if (o + 4 > r.Raw.Length) break;
                vals.Add(kind switch
                {
                    "u32" => Bin.U32(r.Raw, o),
                    "s32" => Bin.S32(r.Raw, o),
                    "f32" => J.F(Bin.F32(r.Raw, o)),
                    "u16x2" => new[] { (int)Bin.U16(r.Raw, o), Bin.U16(r.Raw, o + 2) },
                    "u8x4" => new[] { (int)r.Raw[o], r.Raw[o + 1], r.Raw[o + 2], r.Raw[o + 3] },
                    _ => null,
                });
            }
            d[name] = count == 1 ? vals[0] : vals;
        }
        return d;
    }

    /// <summary>Class-11 records: 44 of them (indices 79..118 and 124..127). Names: the ROM table at 0xD7E84 has 44 character names;
    /// pairing them in record order is a hypothesis.</summary>
    public static object Characters(List<IntelRecord> all, byte[] rom)
    {
        var names = RomNames.ReadPointerTable(rom, 0xD7E84, 44);
        var list = new List<object>();
        int k = 0;
        foreach (var r in all.Where(x => x.Class == 11))
        {
            var f = (Dictionary<string, object?>)DecodeFields(r)!;
            list.Add(new
            {
                intelligenceIndex = r.Index,
                nameGuess = k < names.Count ? names[k] : null,
                nameGuessBasis = "hypothesis: k-th class-11 record = k-th entry of the character name table at ROM 0xD7E84",
                health = f["health"],
                speedScale = f["speedScale"],
                skill = f["skill"],
                itemIds = f["itemIds"],
                speedA = f["speedA"],
                speedB = f["speedB"],
            });
            k++;
        }
        return list;
    }
}
