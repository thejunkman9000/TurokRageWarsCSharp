using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>Archive section 4, label "particle effects": container(3) {parameter blob, u32 effect ids[224], 60-byte records[224]}.
/// Verified loader fixups (func_0044E454): record word 5 (+0x14) is a texture id (-1 = none) resolved with func_0028C174,
/// words 6..14 (+0x18..+0x38) are byte offsets into the blob and are turned into pointers. Everything else is hypothesis.</summary>
static class ParticleEffects
{
    public static object Load(RomIndex ix)
    {
        var node = ix.Find(4) ?? throw new InvalidDataException("section 4");
        var s = Container.Sections(ix.Read(node))!;
        var blob = s[0];
        TypedArray.TryParse(s[1], out var ids);
        TypedArray.TryParse(s[2], out var recs);
        var allOffs = new SortedSet<int> { blob.Length };
        for (int i = 0; i < recs.Count; i++)
            for (int w = 6; w < 15; w++) { int o = (int)Bin.U32(recs.Data, i * 60 + w * 4); if (o != 0) allOffs.Add(o); }
        var list = new List<object>();
        for (int i = 0; i < recs.Count; i++)
        {
            var r = recs.Data.AsSpan(i * 60, 60);
            var refs = new List<object>();
            for (int w = 6; w < 15; w++)
            {
                int o = (int)Bin.U32(r, w * 4);
                if (o == 0) continue;
                int next = allOffs.GetViewBetween(o + 1, int.MaxValue).Min;
                refs.Add(new { slot = w, offset = o, size = next - o, hex = J.Hex(blob.AsSpan(o, Math.Min(next - o, 512))) });
            }
            int tex = Bin.S32(r, 0x14);
            list.Add(new
            {
                index = i,
                effectId = ids.Count > i ? Bin.U32(ids.Data, i * 4) : 0,
                flags = J.H8(Bin.U32(r, 0)),
                word1 = J.H8(Bin.U32(r, 4)),
                word2 = J.H8(Bin.U32(r, 8)),
                word3 = J.H8(Bin.U32(r, 12)),
                word4 = J.H8(Bin.U32(r, 16)),
                textureId = tex == -1 ? (int?)null : tex,
                blobRefs = refs,
            });
        }
        return new { blobSize = blob.Length, recordCount = recs.Count, recordStride = recs.Stride, records = list };
    }
}

/// <summary>Archive section 8, label "warp dests": container(2) {u32 group id[321], 20-byte points[321]}. Group ids are 3010 + 10*n (n = level
/// slot; 8 points per group = the 8 deathmatch spawn points of the level) plus a few 6000.. singletons. Verified: layout (f32 x,y,z) and
/// that positions lie inside the level extents; hypothesis: f32 at +0xC is the facing yaw in radians (values are 0, +-pi/2, pi), u16 at +0x10
/// a team/zone flag (0/1), u16 at +0x12 = 0xFFFF (link).</summary>
static class SpawnPoints
{
    public static object Load(RomIndex ix)
    {
        var node = ix.Find(8) ?? throw new InvalidDataException("section 8");
        var s = Container.Sections(ix.Read(node))!;
        TypedArray.TryParse(s[0], out var ids);
        TypedArray.TryParse(s[1], out var pts);
        var meta = LevelCatalog.LoadMeta(ix);
        var groups = new SortedDictionary<uint, List<object>>();
        for (int i = 0; i < pts.Count; i++)
        {
            uint g = Bin.U32(ids.Data, i * 4);
            var r = pts.Data.AsSpan(i * 20, 20);
            if (!groups.TryGetValue(g, out var l)) groups[g] = l = [];
            l.Add(new
            {
                index = i,
                x = J.F(Bin.F32(r, 0)), y = J.F(Bin.F32(r, 4)), z = J.F(Bin.F32(r, 8)),
                yaw = J.F(Bin.F32(r, 12)),
                team = (int)Bin.U16(r, 16),
                link = (int)Bin.U16(r, 18),
            });
        }
        var outList = new List<object>();
        foreach (var (g, l) in groups)
        {
            bool slotId = g >= 3010 && g < 3400 && (g - 3010) % 10 == 0;
            int n = slotId ? (int)(g - 3010) / 10 : -1;
            var m = slotId ? meta.FirstOrDefault(x => x.Field8 == 601 + n) : null;
            outList.Add(new { groupId = g, slotIndexGuess = slotId ? n : (int?)null, levelId = m?.LevelId, levelMapName = m?.MapName, pointCount = l.Count, points = l });
        }
        return new { totalPoints = pts.Count, groups = outList };
    }
}

/// <summary>Archive section 23, label "Bloodlust Weapon Data" (46 x 32-byte records, 44 used). func_0020A604 copies records 0..43 (28 bytes each)
/// to D_800F7D20 as a [22 weapons][2 fire modes] table; func_00209E80 (bot fire controller) indexes it with the owner's current weapon (actor+0x62E)
/// and fire slot (actor+0x594 - 1) and dispatches on the u32 pattern table D_800CDA40[22][2] (ROM 0xCE640). Verified that these are bot
/// trigger-timing configs, not damage data. Field names (from func_0020A6D8/0020A028/0020A2D4/0020A458): delay, burst and pause tick counts
/// with random spread (value = base + rand()*spread).</summary>
static class BotWeaponFire
{
    static readonly string[] PatternNames =
    [
        "repeat-burst A (func_0020A6D8)", "repeat-burst B (func_0020A6D8)", "repeat-burst C (func_0020A6D8)", "repeat-burst D (func_0020A6D8)",
        "delay-then-burst (func_0020A7B0)", "alternating primary/secondary with random switch (func_0020A028)",
        "charge/hold with distance gate (func_0020A458)", "needs-line-of-sight burst (func_0020A2D4)", "hold trigger (sets unk240=1, no config)",
    ];

    public static object Load(RomIndex ix, byte[] rom)
    {
        var node = ix.Find(23) ?? throw new InvalidDataException("section 23");
        var s = Container.Sections(ix.Read(node))!;
        var names = RomNames.ReadPointerTable(rom, 0xD7CEC, 26);
        int patRom = J.VaToRom(0x800CDA40);
        var list = new List<object>();
        for (int i = 0; i < 44; i++)
        {
            var r = s[i];
            uint pat = Bin.U32(rom, patRom + 4 * i);
            list.Add(new
            {
                record = i,
                weapon = i / 2,
                weaponName = i / 2 < names.Count ? names[i / 2] : null,
                fireSlot = i % 2 + 1,
                pattern = pat,
                patternName = pat < PatternNames.Length ? PatternNames[pat] : "?",
                delayTicks = Bin.S32(r, 0), delaySpread = Bin.S32(r, 4),
                burstTicks = Bin.S32(r, 8), burstSpread = Bin.S32(r, 12),
                pauseTicks = Bin.S32(r, 16), pauseSpread = Bin.S32(r, 20),
                threshold = Bin.S32(r, 24),
            });
        }
        return new { status = "layout V (func_0020A604, func_00209E80), field names H from the pattern handlers", extraUnusedRecords = s.Length - 44, records = list };
    }
}

/// <summary>Archive section 21, label "BLLevelData Index": container of 36 RNC blobs of 3382 bytes, one per level (index = level id).
/// Layout (verified from func_00425E50 / func_004263BC / func_004280F0 / func_00427690): 22-byte header (u32, u16, u16 ids[8] at +6) then
/// tbl[4][20] of 42-byte entries at +0x16. tbl[0][c] is indexed by the selected character (player entry +0x80), tbl[1] / tbl[3] / tbl[2]
/// are used for cfg modes 2 / 3 / 4. In an entry byte[1] is the 1-based next level id (0 = none) and bytes 2..4 are fallbacks (V).
/// The header ids (&gt;= 0x4C3) set per-player flags (talisman / unlock ids, V); everything else in the entry is unknown.</summary>
static class LevelProgression
{
    public static object Load(RomIndex ix)
    {
        var n = ix.Find(21) ?? throw new InvalidDataException("section 21");
        var levels = new List<object>();
        for (int li = 0; li < n.Children.Count; li++)
        {
            var d = ix.Read(n.Children[li]);
            if (d.Length < 22) continue;
            var ids = new List<int>();
            for (int k = 0; k < 8; k++) ids.Add(Bin.U16(d, 6 + 2 * k));
            var groups = new List<object>();
            for (int g = 0; g < 4; g++)
            {
                var entries = new List<object>();
                for (int c = 0; c < 20; c++)
                {
                    int o = 22 + (g * 20 + c) * 42;
                    if (o + 42 > d.Length) break;
                    var e = d.AsSpan(o, 42);
                    if (IsEmpty(e)) continue;
                    var next = new List<int>();
                    for (int k = 1; k <= 4; k++) if (e[k] > 0) next.Add(e[k] - 1);
                    entries.Add(new { slot = c, nextLevels = next, hex = J.Hex(e) });
                }
                groups.Add(new { group = g, entries });
            }
            levels.Add(new { levelId = li, headerWord0 = Bin.U32(d, 0), headerU16 = (int)Bin.U16(d, 4), headerIds = ids, groups });
        }
        return levels;
    }

    static bool IsEmpty(ReadOnlySpan<byte> e)
    {
        for (int i = 0; i < 38; i++) if (e[i] != 0) return false;
        return true;
    }
}

/// <summary>Remaining small global tables (all hypothesis for meaning; layouts read from the container / typed array headers).</summary>
static class MiscTables
{
    public static object Load(RomIndex ix)
    {
        var o = new Dictionary<string, object?>();
        // section 2 "object types": typed array stride 2 count 347
        if (TypedArray.TryParse(ix.Read(ix.Find(2)!), out var ot))
        {
            var v = new int[ot.Count];
            for (int i = 0; i < v.Length; i++) v[i] = Bin.U16(ot.Data, 2 * i);
            o["objectTypeAttr_section2"] = new
            {
                note = "u16 per object index of archive section 0 (347)",
                values = v,
                histogram = v.GroupBy(x => x).OrderBy(g => g.Key).ToDictionary(g => "0x" + g.Key.ToString("X4"), g => g.Count()),
            };
        }
        o["levelBlocks_section6"] = Flatten(ix.Read(ix.Find(6)!));
        o["levelBlocks_section7"] = Flatten(ix.Read(ix.Find(7)!));
        var sw = ix.Find(11);
        if (sw != null)
        {
            var secs = Container.Sections(ix.Read(sw))!;
            o["particleSwooshes_section11"] = secs.Select((b, i) => new
            {
                index = i, size = b.Length,
                f0 = J.F(Bin.F32(b, 0)), f4 = J.F(Bin.F32(b, 4)), f8 = J.F(Bin.F32(b, 8)), hex = J.Hex(b),
            }).ToList();
        }
        o["dynamicLights_section12"] = Typed(ix, 12);
        o["curves_section13"] = Typed(ix, 13);
        o["rumbles_section14"] = Typed(ix, 14);
        o["rumbleTypes_section15"] = Typed(ix, 15);
        o["graphicSetTypeIds_section17"] = Typed(ix, 17);
        o["linkGroupTypes_section19"] = Typed(ix, 19);
        var gs = ix.Find(16);
        if (gs != null)
        {
            var l = new List<object>();
            for (int i = 0; i < gs.Children.Count; i++)
            {
                var b = ix.Read(gs.Children[i]);
                l.Add(new { index = i, unpacked = b.Length, head = J.Hex(b.AsSpan(0, Math.Min(32, b.Length))) });
            }
            o["graphicSets_section16"] = l;
        }
        return o;
    }

    static object? Typed(RomIndex ix, int sec)
    {
        var n = ix.Find(sec);
        if (n == null || !TypedArray.TryParse(ix.Read(n), out var t)) return null;
        var rows = new List<string>();
        for (int i = 0; i < t.Count; i++) rows.Add(J.Hex(t.Data.AsSpan(i * t.Stride, t.Stride)));
        return new { stride = t.Stride, count = t.Count, rowsHex = rows };
    }

    /// <summary>Recursively expand containers into nested lists, other blocks become hex strings.</summary>
    static object Flatten(byte[] d, int depth = 0)
    {
        if (depth < 4 && Container.TryParse(d, out var offs))
        {
            var l = new List<object>();
            for (int i = 0; i < offs.Length - 1; i++) l.Add(Flatten(d.AsSpan(offs[i], offs[i + 1] - offs[i]).ToArray(), depth + 1));
            return l;
        }
        return J.Hex(d);
    }
}
