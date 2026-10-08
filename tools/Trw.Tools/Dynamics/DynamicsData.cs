using System.Text.Json;
using Trw.Tools.Defs;
using Trw.Tools.Levels;

namespace Trw.Tools.Dynamics;

/// <summary>One part-3 actor record (stride 0x28, column-major). Layout verified against func_0024B398 -> func_00246690.</summary>
sealed class ActorRecord
{
    public int Index;
    public uint Marker;            // +0  s32 -1
    public float X, Y, Z;          // +4  position
    public float SX, SY, SZ;       // +0x10 scale
    public int ObjectId;           // +0x1C u16 index into archive section 0 (0xFFFF = look up by type)
    public int ActorType;          // +0x1E u16 -> actor+0xE4 (key into the "object types" table, section 2)
    public int RegionTriangle;     // +0x20 u16 index of the part-1 ground triangle (0xFFFF none); NOT an event id
    public int IntelIndex;         // +0x22 u16 -> archive section 1 record
    public int YawRaw;             // +0x24 s16, * 2pi/65536
    public int Flags;              // +0x26 u8 -> actor+0x100
    public int Link;               // +0x27 u8 (0xFF none)
    public uint Class;
    public byte[] IntelRaw = [];
    public double Yaw => YawRaw * (2 * Math.PI / 65536.0);
    public string ClassName => Class switch { 0 => "prop", 2 => "mover", 3 => "pickup", 4 => "turret", 8 => "trigger8", 9 => "marker", 10 => "indicator", 11 => "pawn", 13 => "zone", _ => "class" + Class };
}

static class ActorRecords
{
    public static List<ActorRecord> Read(Level lv, List<IntelRecord> intel)
    {
        var res = new List<ActorRecord>();
        if (!TypedArray.TryParse(lv.ReadPart(3), out var t) || t.Stride != 0x28) return res;
        var a = t.Untransposed();
        for (int i = 0; i < a.Count; i++)
        {
            var r = a.Data.AsSpan(i * 0x28, 0x28);
            var rec = new ActorRecord
            {
                Index = i, Marker = Bin.U32(r, 0),
                X = Bin.F32(r, 4), Y = Bin.F32(r, 8), Z = Bin.F32(r, 12),
                SX = Bin.F32(r, 16), SY = Bin.F32(r, 20), SZ = Bin.F32(r, 24),
                ObjectId = Bin.U16(r, 0x1C), ActorType = Bin.U16(r, 0x1E), RegionTriangle = Bin.U16(r, 0x20),
                IntelIndex = Bin.U16(r, 0x22), YawRaw = (short)Bin.U16(r, 0x24), Flags = r[0x26], Link = r[0x27],
            };
            if (rec.IntelIndex < intel.Count) { rec.Class = intel[rec.IntelIndex].Class; rec.IntelRaw = intel[rec.IntelIndex].Raw; }
            res.Add(rec);
        }
        return res;
    }
}

/// <summary>Archive section 8 "warp dests": container(2): u32 groupId[321] (sorted), 20-byte records {f32 x,y,z, f32 yaw, u16 level, u16 0xFFFF}.</summary>
sealed class WarpDests
{
    public readonly List<(uint Group, float X, float Y, float Z, float Yaw, int Level)> All = [];
    public static WarpDests Load(RomIndex ix)
    {
        var w = new WarpDests();
        var s = Container.Sections(ix.Read(ix.Find(8)!))!;
        TypedArray.TryParse(s[0], out var ids);
        TypedArray.TryParse(s[1], out var rs);
        for (int i = 0; i < ids.Count; i++)
        {
            var r = rs.Data.AsSpan(i * 20, 20);
            w.All.Add((Bin.U32(ids.Data, i * 4), Bin.F32(r, 0), Bin.F32(r, 4), Bin.F32(r, 8), Bin.F32(r, 12), Bin.U16(r, 16)));
        }
        return w;
    }
    public IEnumerable<(uint Group, float X, float Y, float Z, float Yaw, int Level)> Group(uint id) => All.Where(x => x.Group == id);
}

/// <summary>Archive section 6 ("ROM-level load" -> scene+0x80): per level a container(3) of three {8-byte header, 8-byte bitmask}:
/// [0] actor active bits (bit = part-3 actor index; header word = actor count), [1] sector-array-2 object bits (all zero), [2] part-7 record "preset" bits.</summary>
sealed class LevelMasks
{
    public sealed class Entry { public int ActorCount; public byte[] ActorBits = new byte[8], ObjBits = new byte[8], PresetBits = new byte[8]; }
    readonly List<Entry> _e = [];
    public Entry For(int level) => level < _e.Count ? _e[level] : new Entry();
    public static LevelMasks Load(RomIndex ix)
    {
        var m = new LevelMasks();
        var top = Container.Sections(ix.Read(ix.Find(6)!))!;
        var lv = Container.Sections(top[0])!;
        foreach (var blob in lv)
        {
            var sub = Container.Sections(blob)!;
            var e = new Entry();
            byte[] Bits(byte[] c) { var s = Container.Sections(c)!; return s[1]; }
            e.ActorCount = (int)Bin.U32(Container.Sections(sub[0])![0], 0);
            e.ActorBits = Bits(sub[0]); e.ObjBits = Bits(sub[1]); e.PresetBits = Bits(sub[2]);
            m._e.Add(e);
        }
        return m;
    }
    public static bool Bit(byte[] bits, int i) => i >= 0 && i / 8 < bits.Length && (bits[i / 8] & (1 << (i & 7))) != 0;
}

/// <summary>One 20-byte "Rec14" event rule of level part 7 (layout from the Rec14 accessors func_00278C80, func_00279274, func_0028C34C).</summary>
sealed class EventRule
{
    public bool RegionSet;         // false = set A (keyed by emitting actor index), true = set B (keyed by region id)
    public int Index;              // index inside its set
    public uint Key;               // sorted key of the set (actor index for A, region id for B)
    public uint W0;                // +0 trigger mask
    public int Target;             // +4 u16 target actor index
    public int Message;            // +8 u16 voice/message id (>= 2 played in mode 4)
    public int Level;              // +A u16 level id of the target
    public int WordC;              // +C u16 (0x3F00 on the "pulse off" records)
    public int FlagsE;             // +E
    public int KeyByte;            // +F condition key (0 in shipped data)
    public int Sound;              // +0x10
    public int Type;               // +0x11 = actor class of the target (3 pickup, 10 indicator)
    public int Op;                 // +0x12 operation index in that class's handler table
    public int Bit;                // +0x13 bit number in the per-level "preset" mask
}

sealed class EventRules
{
    public readonly List<EventRule> Rules = [];
    public byte[] PresetBits = new byte[8];

    public static EventRules Read(Level lv, LevelMasks.Entry m)
    {
        var er = new EventRules { PresetBits = m.PresetBits };
        var s = Container.Sections(lv.ReadPart(7));
        if (s == null || s.Length != 4) return er;
        for (int g = 0; g < 2; g++)
        {
            if (!TypedArray.TryParse(s[g * 2], out var ids) || !TypedArray.TryParse(s[g * 2 + 1], out var rs) || ids.Count == 0) continue;
            for (int i = 0; i < ids.Count; i++)
            {
                var r = rs.Data.AsSpan(i * 20, 20);
                er.Rules.Add(new EventRule
                {
                    RegionSet = g == 1, Index = i, Key = Bin.U32(ids.Data, i * 4), W0 = Bin.U32(r, 0), Target = Bin.U16(r, 4),
                    Message = Bin.U16(r, 8), Level = Bin.U16(r, 10), WordC = Bin.U16(r, 12), FlagsE = r[14], KeyByte = r[15],
                    Sound = r[16], Type = r[17], Op = r[18], Bit = r[19],
                });
            }
        }
        return er;
    }

    public IEnumerable<uint> UsedRegionIds() => Rules.Where(r => r.RegionSet).Select(r => r.Key).Distinct();

    public static string OpName(int type, int op) => (type, op) switch
    {
        (10, 1) => "indicatorOn", (10, 2) => "indicatorOff",
        (3, 1) => "spawnerEnable", (3, 2) => "spawnerDisable",
        _ => $"op{op}",
    };

    public static string[] MaskNames(uint w0, bool region)
    {
        var l = new List<string>();
        if (!region)
        {
            if ((w0 & 0x80000) != 0) l.Add("indicatorEnteredOn(0x80000, emitted by func_00204658)");
            if ((w0 & 0x200000) != 0) l.Add("indicatorEnteredOff(0x200000, emitted by func_002045CC)");
            if ((w0 & 0x400000) != 0) l.Add("exact-match bit 0x400000");
            if ((w0 & 0x2) != 0) l.Add("bit 0x2 (emitted by the pickup/flag code, not traced)");
        }
        else
        {
            if ((w0 & 1) != 0) l.Add("0x1 emitted for the triangle the player came from");
            if ((w0 & 2) != 0) l.Add("0x2 emitted for the triangle the player is now on");
            if ((w0 & 0x2000) != 0) l.Add("0x2000 required caller kind bit (func_00278E74 m5=0x3800)");
            if ((w0 & 0x80000000) != 0) l.Add("0x80000000 (not tested by func_00279274)");
        }
        return l.ToArray();
    }

    public object ToJson(List<ActorRecord> actors)
    {
        object One(EventRule r)
        {
            string src = r.RegionSet ? $"region id {r.Key} (part-1 triangles whose word0 == {r.Key})"
                : $"actor {r.Key}" + (r.Key < actors.Count ? $" ({actors[(int)r.Key].ClassName}, object {actors[(int)r.Key].ObjectId})" : "");
            return new Dictionary<string, object?>
            {
                ["set"] = r.RegionSet ? "B-region" : "A-actor",
                ["source"] = src,
                ["sourceKey"] = r.Key,
                ["triggerMask"] = "0x" + r.W0.ToString("X8"),
                ["triggerBits"] = MaskNames(r.W0, r.RegionSet),
                ["targetActor"] = r.Target,
                ["targetClass"] = r.Type,
                ["operation"] = r.Op,
                ["operationName"] = OpName(r.Type, r.Op),
                ["targetLevel"] = r.Level,
                ["presetBit"] = r.Bit,
                ["appliedAtLevelStart"] = LevelMasks.Bit(PresetBits, r.Bit),
                ["flagsE"] = "0x" + r.FlagsE.ToString("X2"),
                ["wordC"] = "0x" + r.WordC.ToString("X4"),
                ["wordCNote"] = r.WordC == 0x3F00 ? "0x3F00 = high half of float 0.5 [H: delayed (pulse) execution]" : null,
                ["conditionKey"] = r.KeyByte,
                ["message"] = r.Message,
                ["sound"] = r.Sound,
                ["confidence"] = "layout verified; trigger semantics hypothesis",
            };
        }
        return Rules.Select(One).ToList();
    }
}

/// <summary>Part 1 section 2 triangles ("coarse ground/region triangles"): u16 word0 is the region id used by event set B.</summary>
sealed class TriggerRegions
{
    public readonly List<(int Word0, int Flags, float[] V)> Tris = [];
    public static TriggerRegions Read(Level lv)
    {
        var tr = new TriggerRegions();
        var s = Container.Sections(lv.ReadPart(1));
        if (s == null || s.Length < 3) return tr;
        if (!TypedArray.TryParse(s[1], out var vt) || !TypedArray.TryParse(s[2], out var tt)) return tr;
        vt = vt.Untransposed(); tt = tt.Untransposed();
        float[][] verts = new float[vt.Count][];
        for (int i = 0; i < vt.Count; i++)
        {
            verts[i] = new float[3];
            for (int c = 0; c < 3; c++) verts[i][c] = Bin.S32(vt.Data, i * 16 + 4 * c) * 0.029296875f;
        }
        for (int i = 0; i < tt.Count; i++)
        {
            var r = tt.Data.AsSpan(i * tt.Stride, tt.Stride);
            int v0 = Bin.U16(r, 4), v1 = Bin.U16(r, 6), v2 = Bin.U16(r, 8);
            if (v0 >= verts.Length || v1 >= verts.Length || v2 >= verts.Length) continue;
            tr.Tris.Add((Bin.U16(r, 0), Bin.U16(r, 2), [.. verts[v0], .. verts[v1], .. verts[v2]]));
        }
        return tr;
    }

    public int? Word0(int triangle) => triangle >= 0 && triangle < Tris.Count ? Tris[triangle].Word0 : null;

    public object ToJson(IEnumerable<uint> used)
    {
        var ids = used.ToHashSet();
        return new Dictionary<string, object?>
        {
            ["note"] = "Event set B fires when a player steps between ground triangles; the key is the triangle's word0 (region id). Triangles listed are those of the region ids used by this level's B rules; vertices are raw coordinates x,y,z for each of the 3 corners.",
            ["allRegionIdsInLevel"] = Tris.Select(t => t.Word0).Distinct().OrderBy(x => x).ToArray(),
            ["regions"] = ids.OrderBy(x => x).Select(id => new Dictionary<string, object?>
            {
                ["regionId"] = id,
                ["triangles"] = Tris.Where(t => t.Word0 == id).Select(t => new
                {
                    flags = "0x" + t.Flags.ToString("X4"),
                    vertices = new[] { new[] { R(t.V[0]), R(t.V[1]), R(t.V[2]) }, new[] { R(t.V[3]), R(t.V[4]), R(t.V[5]) }, new[] { R(t.V[6]), R(t.V[7]), R(t.V[8]) } },
                }).ToList(),
            }).ToList(),
        };
    }
    static double R(float f) => Math.Round(f, 3);
}

/// <summary>Placement AABBs of the static level (objects.json) for lift end-point checks. Floors are usually the bottom or top face of a piece's AABB.</summary>
sealed class FloorIndex
{
    readonly List<(double X0, double X1, double Z0, double Z1, double Y0, double Y1)> _f = [];
    public bool Available => _f.Count > 0;
    public static FloorIndex Load(string geoDir, int level)
    {
        var fi = new FloorIndex();
        if (!Directory.Exists(geoDir)) return fi;
        var dir = Directory.GetDirectories(geoDir, $"{level:D2}_*").FirstOrDefault();
        if (dir == null || !File.Exists(Path.Combine(dir, "objects.json"))) return fi;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "objects.json")));
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var mn = p.GetProperty("boundsMin"); var mx = p.GetProperty("boundsMax");
            fi._f.Add((mn[0].GetDouble(), mx[0].GetDouble(), mn[2].GetDouble(), mx[2].GetDouble(), mn[1].GetDouble(), mx[1].GetDouble()));
        }
        return fi;
    }

    /// <summary>Horizontal AABB face (bottom or top of a piece whose footprint touches the platform footprint x,z +- half) nearest to y within [y-30, y+5].</summary>
    public double? FaceNear(double x, double z, double y, double half)
    {
        double? best = null;
        foreach (var f in _f)
        {
            if (x + half < f.X0 - 1 || x - half > f.X1 + 1 || z + half < f.Z0 - 1 || z - half > f.Z1 + 1) continue;
            foreach (var e in new[] { f.Y0, f.Y1 })
                if (e <= y + 6 && e >= y - 30 && (best == null || Math.Abs(y - e) < Math.Abs(y - best.Value))) best = e;
        }
        return best;
    }

    /// <summary>All distinct face heights (rounded to 1) under x,z within [y0,y1].</summary>
    public double[] FacesBetween(double x, double z, double y0, double y1)
    {
        var s = new SortedSet<double>();
        foreach (var f in _f)
        {
            if (x < f.X0 - 1 || x > f.X1 + 1 || z < f.Z0 - 1 || z > f.Z1 + 1) continue;
            foreach (var e in new[] { f.Y0, f.Y1 }) if (e >= y0 && e <= y1) s.Add(Math.Round(e, 0));
        }
        return s.ToArray();
    }
}

/// <summary>Initial indicator / spawner states: actor active bit, then the preset event rules applied in order (A first, then B) as func_0044E214 does.</summary>
sealed class StateSim
{
    readonly Dictionary<int, bool> _on = [];
    public readonly List<string> Log = [];
    public StateSim(List<ActorRecord> actors, LevelMasks.Entry m, EventRules rules)
    {
        foreach (var a in actors) _on[a.Index] = LevelMasks.Bit(m.ActorBits, a.Index);
        foreach (var r in rules.Rules)   // set A records come first in the list, then set B, matching func_0044E214
        {
            if (!LevelMasks.Bit(m.PresetBits, r.Bit) || r.KeyByte != 0) continue;
            if (r.Type != 10 && r.Type != 3) continue;
            if (r.Op == 1) _on[r.Target] = true; else if (r.Op == 2) _on[r.Target] = false;
            Log.Add($"preset bit {r.Bit}: actor {r.Target} -> {(r.Op == 1 ? "on/enabled" : "off/disabled")}");
        }
        Bits = m.ActorBits;
    }
    public byte[] Bits;
    public bool Initial(int actor) => _on.GetValueOrDefault(actor, false);
    public bool BitAtLoad(int actor) => LevelMasks.Bit(Bits, actor);
}
