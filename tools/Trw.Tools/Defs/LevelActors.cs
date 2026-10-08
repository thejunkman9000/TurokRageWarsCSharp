using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>
/// Decodes the placement data of one level (all layouts verified against the spawn code):
/// part 3 (stride 0x28) = B11Spawn consumed by func_0024B398 -> func_00246690: s32 w0 (-1), f32 pos[3] @4, f32 scale[3] @0x10, u16 objectIndex @0x1C
/// (index into archive section 0, -1 = look up by actorType), u16 actorType @0x1E (-&gt; actor+0xE4), u16 eventRef @0x20 (0xFFFF none, else a
/// 32-byte entry of the level's section-2-of-header table via func_0028B370), u16 intelligence @0x22 (archive section 1; class 11 = pawn,
/// 3 = pickup), s16 yaw @0x24 (* 2pi/65536 = 9.5877e-5 rad), u8 flags @0x26 (-&gt; actor+0x100), u8 link @0x27 (0xFF none).
/// Sector section 0 (stride 0x1C, func_0024F940) = compact static placements, section 1 (stride 0x34, func_0024FD4C) = quaternion placements.
/// </summary>
static class LevelActors
{
    public sealed record Result(object Full, object Summary);

    public static Result Decode(Level lv, List<IntelRecord> intel)
    {
        // part 0: first float is the position unit used by sector section 0 (scene+0x1B2B0 -> arg4 of func_0024F940); 25.6 in every level.
        float unit = 25.6f;
        var p0 = lv.ReadPart(0);
        if (p0.Length >= 4) unit = Bin.F32(p0, 0);

        string ClassOf(int idx) => idx >= 0 && idx < intel.Count ? intel[idx].ClassName : "?";

        // ---- part 3
        var actors = new List<object>();
        var classCount = new SortedDictionary<string, int>();
        var intelCount = new SortedDictionary<int, int>();
        if (TypedArray.TryParse(lv.ReadPart(3), out var t) && t.Stride == 0x28)
        {
            var a = t.Untransposed();
            for (int i = 0; i < a.Count; i++)
            {
                var r = a.Data.AsSpan(i * 0x28, 0x28);
                int objIdx = Bin.U16(r, 0x1C), type = Bin.U16(r, 0x1E), evt = Bin.U16(r, 0x20), ii = Bin.U16(r, 0x22);
                short yawRaw = (short)Bin.U16(r, 0x24);
                string cn = ClassOf(ii);
                classCount[cn] = classCount.GetValueOrDefault(cn) + 1;
                intelCount[ii] = intelCount.GetValueOrDefault(ii) + 1;
                actors.Add(new
                {
                    index = i,
                    x = J.F(Bin.F32(r, 4)), y = J.F(Bin.F32(r, 8)), z = J.F(Bin.F32(r, 12)),
                    scale = new[] { J.F(Bin.F32(r, 0x10)), J.F(Bin.F32(r, 0x14)), J.F(Bin.F32(r, 0x18)) },
                    objectIndex = objIdx == 0xFFFF ? (int?)null : objIdx,
                    actorType = type,
                    eventRef = evt == 0xFFFF ? (int?)null : evt,
                    intelligence = ii,
                    className = cn,
                    yaw = J.F(yawRaw * 9.5876727e-5f),
                    flags = r[0x26],
                    link = r[0x27] == 0xFF ? (int?)null : r[0x27],
                });
            }
        }

        // ---- nav nodes (part 9)
        var nav = lv.NavNodes().Select(n => new { n.Index, n.X, n.Y, n.Z, flags = "0x" + n.Flags.ToString("X4"), objectType = n.ObjectType }).ToList();

        // ---- sectors
        var sectors = new List<object>();
        var sectorClass = new SortedDictionary<string, int>();
        int total0 = 0, total1 = 0;
        foreach (var sec in lv.Sectors())
        {
            var props = new List<object>();
            if (sec.StaticPlacements.Data != null && sec.StaticPlacements.Stride == 0x1C)
            {
                var d = sec.StaticPlacements;
                for (int i = 0; i < d.Count; i++)
                {
                    var r = d.Data.AsSpan(i * 0x1C, 0x1C);
                    int rot = r[0x18];
                    float x = r[0x19] * unit * ((rot & 0x10) != 0 ? -1 : 1);
                    float z = r[0x1A] * unit * ((rot & 0x20) != 0 ? -1 : 1);
                    float y = (short)Bin.U16(r, 0x10) * 5.12f;
                    int ii = Bin.U16(r, 0x0E);
                    string cn = ClassOf(ii);
                    sectorClass[cn] = sectorClass.GetValueOrDefault(cn) + 1;
                    int evt = Bin.U16(r, 0x0A);
                    props.Add(new
                    {
                        x = J.F(x), y = J.F(y), z = J.F(z),
                        scale = J.F(Bin.F32(r, 4)),
                        quarterTurns = rot & 3,
                        objectIndex = (int)Bin.U16(r, 8),
                        eventRef = evt == 0xFFFF ? (int?)null : evt,
                        flags = "0x" + Bin.U16(r, 0x0C).ToString("X4"),
                        intelligence = ii,
                        className = cn,
                        collisionId = Bin.S32(r, 0),
                        bboxS8 = new int[] { (sbyte)r[0x12], (sbyte)r[0x13], (sbyte)r[0x14], (sbyte)r[0x15], (sbyte)r[0x16], (sbyte)r[0x17] },
                    });
                }
                total0 += d.Count;
            }
            var inst = new List<object>();
            if (sec.Placements34.Data != null && sec.Placements34.Stride == 0x34)
            {
                var d = sec.Placements34;
                for (int i = 0; i < d.Count; i++)
                {
                    var r = d.Data.AsSpan(i * 0x34, 0x34);
                    int ii = Bin.U16(r, 0x2C);
                    string cn = ClassOf(ii);
                    sectorClass[cn] = sectorClass.GetValueOrDefault(cn) + 1;
                    int evt = Bin.U16(r, 0x2A);
                    inst.Add(new
                    {
                        x = J.F(Bin.F32(r, 4)), y = J.F(Bin.F32(r, 8)), z = J.F(Bin.F32(r, 12)),
                        scale = new[] { J.F(Bin.F32(r, 0x10)), J.F(Bin.F32(r, 0x14)), J.F(Bin.F32(r, 0x18)) },
                        objectIndex = (int)Bin.U16(r, 0x28),
                        eventRef = evt == 0xFFFF ? (int?)null : evt,
                        intelligence = ii,
                        className = cn,
                        flags = "0x" + r[0x2E].ToString("X2"),
                        quaternion = new[] { J.F((sbyte)r[0x2F] / 127f), J.F((sbyte)r[0x30] / 127f), J.F((sbyte)r[0x31] / 127f), J.F((sbyte)r[0x32] / 127f) },
                        boundsMinOffset = new[] { J.F(J.Trunc16(Bin.U16(r, 0x1C))), J.F(J.Trunc16(Bin.U16(r, 0x1E))), J.F(J.Trunc16(Bin.U16(r, 0x20))) },
                        boundsMaxOffset = new[] { J.F(J.Trunc16(Bin.U16(r, 0x22))), J.F(J.Trunc16(Bin.U16(r, 0x24))), J.F(J.Trunc16(Bin.U16(r, 0x26))) },
                        collisionId = Bin.S32(r, 0),
                    });
                }
                total1 += d.Count;
            }
            sectors.Add(new
            {
                sector = sec.Index,
                rom = "0x" + sec.Node.Offset.ToString("X6"),
                staticProps = props,
                instances = inst,
                section2Records = sec.Records20.Data == null ? 0 : sec.Records20.Count,
                collisionRecords4C = sec.Records4C.Data == null ? 0 : sec.Records4C.Count,
            });
        }

        var full = new
        {
            levelId = lv.Id, name = lv.Name,
            positionUnit = J.F(unit),
            part0Hex = J.Hex(p0),
            actors, navNodes = nav, sectors,
        };
        var summary = new
        {
            levelId = lv.Id, name = lv.Name,
            actorCount = actors.Count,
            actorClassCounts = classCount,
            actorIntelligenceCounts = intelCount.ToDictionary(k => k.Key.ToString(), v => v.Value),
            navNodes = nav.Count,
            sectorCount = sectors.Count,
            sectorStaticProps = total0,
            sectorInstances = total1,
            sectorClassCounts = sectorClass,
        };
        return new Result(full, summary);
    }
}
