using System.Text;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.Dynamics;

/// <summary>Builds the JSON entries of the dynamic objects. Field provenance is documented in docs/formats/dynamic_objects.md.</summary>
static class DynamicBuilders
{
    static object F(double v) => DynamicsCommand.F(v);
    static object[] V3(double x, double y, double z) => DynamicsCommand.V3(x, y, z);

    static Dictionary<string, object?> Common(ActorRecord a, string kind, DynModels models, string confidence)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = $"actor{a.Index}",
            ["kind"] = kind,
            ["actorIndex"] = a.Index,
            ["objectId"] = a.ObjectId,
            ["actorType"] = a.ActorType,
            ["intelligence"] = a.IntelIndex,
            ["model"] = models.Use(a.ObjectId),
            ["position"] = V3(a.X, a.Y, a.Z),
            ["yawRadians"] = F(a.Yaw),
            ["scale"] = V3(a.SX, a.SY, a.SZ),
            ["regionTriangle"] = a.RegionTriangle,
            ["placementFlags"] = "0x" + a.Flags.ToString("X2"),
            ["confidence"] = confidence,
        };
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Lifts: intelligence class 2 (120-byte record). State machine D_800CD8F0, update functions func_00207ABC/00207BB8/00207C94/00207D90,
    // pose function func_00206DD4 (see the doc for the offset table).
    public static object Lift(ActorRecord a, Level lv, FloorIndex floors, DynModels models, StateSim sim)
    {
        var d = a.IntelRaw;
        uint info0 = Bin.U32(d, 0x14);
        int hasHoriz = Bin.U16(d, 0x18);
        uint flags24 = Bin.U32(d, 0x38);
        int bobMode = d[0x3C], easeByte = d[0x3D], yawMode = d[0x3E];
        float rise = Bin.F32(d, 0x40), horiz = Bin.F32(d, 0x44), heading = Bin.F32(d, 0x48);
        float travel = Bin.F32(d, 0x4C), yawSweep = Bin.F32(d, 0x50), bobPhase = Bin.F32(d, 0x54), bobAmp = Bin.F32(d, 0x58), bobRate = Bin.F32(d, 0x5C);
        float holdStart = Bin.F32(d, 0x60), holdEnd = Bin.F32(d, 0x64);
        double dx = hasHoriz != 0 ? horiz * Math.Sin(heading) : 0, dz = hasHoriz != 0 ? horiz * Math.Cos(heading) : 0;
        double toX = a.X + dx, toY = a.Y + rise, toZ = a.Z + dz;

        bool needsRider = (flags24 & 0x100) != 0;
        var cond = new List<string>();
        if ((flags24 & 0x10) != 0) cond.Add("start wait needs actor flag 0x10000");
        if ((flags24 & 0x100) != 0) cond.Add("start wait counts only while a rider stands on it (actor+0x38 bit 0x40)");
        if ((flags24 & 0x400) != 0) cond.Add("start wait counts only while NOT ridden");
        if ((flags24 & 0x10000) != 0) cond.Add("rising needs no rider");
        if ((flags24 & 0x4000) != 0) cond.Add("rising needs a rider");
        if ((flags24 & 0x20) != 0) cond.Add("end wait needs actor flag 0x20000");
        if ((flags24 & 0x200) != 0) cond.Add("end wait needs a rider");
        if ((flags24 & 0x800) != 0) cond.Add("end wait counts only while NOT ridden");
        if ((flags24 & 0x20000) != 0) cond.Add("lowering needs no rider");
        if ((flags24 & 0x8000) != 0) cond.Add("lowering needs a rider");

        var e = Common(a, "lift", models, "verified (layout, state machine, motion formula, end points vs level floors); rider test is hypothesis");
        e["activeAtStart"] = sim.BitAtLoad(a.Index);
        e["motion"] = new Dictionary<string, object?>
        {
            ["type"] = "oscillate",
            ["description"] = "Two stations A (= placement position, t=0) and B (t=1). Cycle: wait at A, travel A->B, wait at B, travel B->A, repeat.",
            ["from"] = V3(a.X, a.Y, a.Z),
            ["to"] = V3(toX, toY, toZ),
            ["offset"] = V3(dx, rise, dz),
            ["horizontalDistance"] = F(hasHoriz != 0 ? horiz : 0),
            ["headingRadians"] = F(heading),
            ["travelTicks"] = F(travel),
            ["travelSeconds"] = F(travel / 15.0),
            ["averageSpeedUnitsPerTick"] = F(Math.Sqrt(dx * dx + rise * rise + dz * dz) / travel),
            ["easing"] = easeByte == 0 ? "cosine (p = (1 - cos(pi t)) / 2)" : "linear",
            ["holdAtFromTicks"] = F(holdStart),
            ["holdAtToTicks"] = F(holdEnd),
            ["trigger"] = needsRider ? "rider" : "always",
            ["triggerNote"] = needsRider ? "leaves A only after a rider has stood on it for holdAtFromTicks (timer resets when the rider leaves); B->A needs nothing" : "runs continuously",
            ["triggerConditionsDecoded"] = cond,
            ["yawSweepRadians"] = F(yawSweep),
            ["yawMode"] = yawMode,
            ["bob"] = bobAmp == 0 ? null : new { amplitude = F(bobAmp), radiansPerTick = F(bobRate), phase = F(bobPhase), mode = bobMode },
            ["bobNote"] = bobRate != 0 && bobAmp == 0 ? $"radiansPerTick={bobRate:0.####} present but amplitude 0: no visible bob" : null,
            ["raw"] = new { info0 = "0x" + info0.ToString("X8"), flags24 = "0x" + flags24.ToString("X8"), wordUnk28 = Bin.Hex(d.AsSpan(0x3C, 4)), horizontalEnabledWord = hasHoriz },
        };
        e["hull"] = "object sec0 holds the collision AABB in model space (see model.hullAabb); standable top surface = hull max z * scale";

        // verification against the static level: the platform rests ~10-15 units above a floor face at both stations
        if (floors.Available)
        {
            double? f0 = floors.FaceNear(a.X, a.Z, a.Y, 110), f1 = floors.FaceNear(toX, toZ, toY, 110);
            double? o0 = f0 == null ? null : a.Y - f0, o1 = f1 == null ? null : toY - f1;
            bool ok = o0 != null && o1 != null && o0 >= -6 && o0 <= 30 && o1 >= -6 && o1 <= 30;
            e["verification"] = new Dictionary<string, object?>
            {
                ["faceBelowStartY"] = f0 == null ? null : F(f0.Value),
                ["startHeightAboveFace"] = o0 == null ? null : F(o0.Value),
                ["faceBelowEndY"] = f1 == null ? null : F(f1.Value),
                ["endHeightAboveFace"] = o1 == null ? null : F(o1.Value),
                ["facesBetweenStations"] = floors.FacesBetween(a.X, a.Z, Math.Min(a.Y, toY) - 2, Math.Max(a.Y, toY) + 2),
                ["alignedWithFloors"] = ok,
                ["method"] = "bottom/top faces of the static placement AABBs (extracted/levelgeo objects.json) whose footprint touches the platform footprint; aligned = at both stations a face of a piece touching the platform footprint (+-110) lies -6..+30 units below the platform origin (face up to 6 above is accepted)",
            };
        }
        return e;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Teleporters: intelligence class 13 + object 330. Touch test in the pawn update (func_00220EB0 ~0x22 loop over scene+0x1B620 zone list).
    public static object Teleporter(ActorRecord a, WarpDests warps, DynModels models, int level)
    {
        var d = a.IntelRaw;
        uint target = Bin.U32(d, 0x20);
        float radius = Bin.F32(d, 0x18), extentB = Bin.F32(d, 0x1C);
        uint defFlags = Bin.U32(d, 4);
        var dests = warps.Group(target).Select(w => new Dictionary<string, object?>
        {
            ["position"] = V3(w.X, w.Y, w.Z), ["yawRadians"] = F(w.Yaw), ["level"] = w.Level,
        }).ToList();
        var e = Common(a, "teleporter", models, "verified (zone list, touch radius, warp-group lookup); pick rule for several destinations verified in func_0021B1E4");
        e["teleport"] = new Dictionary<string, object?>
        {
            ["touchRadius"] = F(radius),
            ["touchTest"] = "pawn position within touchRadius (3D distance) of the zone position; skipped when definition flags & 4 (disabled)",
            ["secondExtent"] = F(extentB),
            ["secondExtentNote"] = "definition +0x1C, not read by the touch test [H: height/visual size]",
            ["targetGroup"] = target,
            ["destinations"] = dests,
            ["destinationLevel"] = dests.Count > 0 ? dests[0]["level"] : null,
            ["pickRule"] = "single destination: that point; several: the one with the largest minimum distance to all live pawns (multiplayer, func_0021B1E4)",
            ["effect"] = "plays effect 0x230 at the old position, sets the pawn position + yaw from the warp record, resets ground triangle",
            ["definitionFlags"] = "0x" + defFlags.ToString("X8"),
        };
        e["activeAtStart"] = true;
        e["activeNote"] = "the part-6 actor active bit is NOT consulted by the zone loop";
        return e;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Two-state indicators: intelligence class 10 (48 bytes), state table D_800CD4C4. actor+1 selects the model of the object's model list.
    public static object Indicator(ActorRecord a, StateSim sim, DynModels models, TriggerRegions regions, EventRules rules)
    {
        var d = a.IntelRaw;
        uint info0 = Bin.U32(d, 0x14);
        var e = Common(a, "indicator", models, "layout verified; use as an on/off beacon is hypothesis");
        int? reg = regions.Word0(a.RegionTriangle);
        e["regionId"] = reg;
        var trig = rules.Rules.Where(r => r.RegionSet && r.Type == 10 && r.Target == a.Index && r.Op == 1 && reg != null && r.Key == reg).ToList();
        e["trigger"] = trig.Count > 0 ? "stepRegion" : "none";
        e["triggerNote"] = trig.Count > 0 ? $"event set B: a human player leaving/entering region {reg} (ground triangles with word0 == {reg}; the indicator stands on one of them) runs 'indicatorOn' on this actor" : null;
        e["initialState"] = sim.Initial(a.Index) ? "on" : "off";
        e["activeBitAtLoad"] = sim.BitAtLoad(a.Index);
        e["stateModelIndex"] = new { off = (int)d[0x22], on = (int)d[0x2E] };
        e["stateLook"] = new { off = (int)d[0x23], on = (int)d[0x2F] };
        e["stateNote"] = "state 0 (off) sets actor+1/+3 to def bytes +0x22/+0x23, states 1/2 (on) to +0x2E/+0x2F (func_002043E0). actor+1 = index into the object's model list (func_00250950) [V]; actor+3 = look [H]. State changes are made by event-rule operations 1 (on) and 2 (off).";
        e["visibleFlagAtSpawn"] = (info0 & 1) != 0;
        e["colorA"] = Bin.Hex(d.AsSpan(0x20, 4));
        e["colorB"] = Bin.Hex(d.AsSpan(0x28, 4));
        e["radius"] = F(Bin.F32(d, 0x24));
        e["activeAtStart"] = sim.Initial(a.Index);
        return e;
    }

    public static object Marker(ActorRecord a, DynModels models)
    {
        var d = a.IntelRaw;
        var e = Common(a, "marker", models, "hypothesis");
        e["idAt0x18"] = Bin.U32(d, 0x18);
        e["idAt0x0E"] = (int)Bin.U16(d, 0x0E);
        e["definitionBytes"] = Bin.Hex(d);
        e["note"] = "class 9 'marker'; its vtable (D_800D0680[9]) has the common on/off operations; the model has animation clips (see model.animationClips). Marker records 54-57 keep an id (0x1B83..0x1B86) at +0x18, records 58-74 (incl. 70 used here) at +0x0E (0x38F..0x3A1); meaning not traced.";
        return e;
    }

    public static object FlagSpawner(ActorRecord a, DynModels models, StateSim sim)
    {
        var d = a.IntelRaw;
        var e = Common(a, "flagSpawner", models, "layout verified; role (CTF flag) from gameplay_defs.md / GAMEPLAY_SPEC.md");
        e["initiallyEnabled"] = sim.Initial(a.Index);
        e["activeBitAtLoad"] = sim.BitAtLoad(a.Index);
        e["respawnTicksMin"] = F(Bin.F32(d, 0x20));
        e["respawnTicksMax"] = F(Bin.F32(d, 0x24));
        e["note"] = "enabled/disabled by event rules (class 3 operations 1/2); the model is an untextured quad (the flag itself is drawn by the pickup code).";
        return e;
    }
}

/// <summary>Model info + raw model-space OBJ export for the objects used by dynamic actors (same convention as extracted/levelgeo/_objects: raw Z-up model space, material tex_&lt;id&gt;_f&lt;flags&gt;).</summary>
sealed class DynModels
{
    readonly RomIndex _ix;
    readonly string _dir;
    readonly Dictionary<int, Dictionary<string, object?>> _info = [];
    readonly Dictionary<string, string> _obj = [];
    List<CiTexture?>? _tex;

    public DynModels(RomIndex ix, string dir) { _ix = ix; _dir = dir; }

    List<CiTexture?> Textures()
    {
        if (_tex != null) return _tex;
        _tex = [];
        var n = _ix.Find(ModelsCommand.MasterTextureSection);
        if (n != null) foreach (var c in n.Children) _tex.Add(CiTexture.Parse(_ix.Read(c)));
        return _tex;
    }

    public object Use(int objectId)
    {
        if (_info.TryGetValue(objectId, out var cached)) return cached;
        var o = _ix.Find(0, objectId);
        var info = new Dictionary<string, object?> { ["objectId"] = objectId };
        _info[objectId] = info;
        if (o == null || o.Children.Count < 3) return info;
        var ml = o.Children[1];
        info["modelCount"] = ml.Children.Count;
        info["modelIndexRule"] = "model = actor+1 % modelCount (actor+1 = 0 unless a class sets it, see indicators)";
        var hull = o.Children[0].Kind == NodeKind.Container || o.Children[0].Kind == NodeKind.Raw ? _ix.Read(o.Children[0]) : null;
        if (hull != null)
        {
            var hs = Container.Sections(hull);
            if (hs != null && hs.Length > 0 && hs[0].Length >= 24)
                info["hullAabb"] = new { min = DynamicsCommand.V3(Bin.F32(hs[0], 0), Bin.F32(hs[0], 4), Bin.F32(hs[0], 8)), max = DynamicsCommand.V3(Bin.F32(hs[0], 12), Bin.F32(hs[0], 16), Bin.F32(hs[0], 20)), space = "model space (Z-up)" };
        }
        info["animationClips"] = o.Children[2].Children.Count;
        var models = new List<object>();
        for (int k = 0; k < ml.Children.Count; k++)
        {
            var m = Model.Parse(_ix.Read(ml.Children[k]));
            if (m == null) continue;
            var tris = m.BuildTriangles();
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue, mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            foreach (var v in m.Vertices) { mnx = Math.Min(mnx, v.X); mxx = Math.Max(mxx, v.X); mny = Math.Min(mny, v.Y); mxy = Math.Max(mxy, v.Y); mnz = Math.Min(mnz, v.Z); mxz = Math.Max(mxz, v.Z); }
            var texInfo = m.Parts.Select(p => new
            {
                textureId = p.Material.TextureId,
                flags = "0x" + p.Material.Flags.ToString("X"),
                frames = p.Material.TextureId >= 0 && p.Material.TextureId < Textures().Count ? Textures()[p.Material.TextureId]?.Frames.Length : null,
            }).ToList();
            string key = $"obj_{objectId}_{k}";
            _obj[key] = Obj(m, tris, key);
            models.Add(new { index = k, file = $"_objects/{key}.obj", vertices = m.Vertices.Length, triangles = tris.Count, skinned = m.Skinned, boundsMin = DynamicsCommand.V3(mnx, mny, mnz), boundsMax = DynamicsCommand.V3(mxx, mxy, mxz), parts = texInfo });
        }
        info["models"] = models;
        return info;
    }

    string Obj(Model m, List<MeshTriangle> tris, string key)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# model-space mesh (Z-up, unplaced) for ").Append(key).Append('\n');
        foreach (var v in m.Vertices) sb.Append("v ").Append(((float)v.X).ToString("R", ic)).Append(' ').Append(((float)v.Y).ToString("R", ic)).Append(' ').Append(((float)v.Z).ToString("R", ic)).Append('\n');
        for (int i = 0; i < m.Vertices.Length; i++)
        {
            var v = m.Vertices[i];
            var pm = m.Parts[m.PartOfVertex[i]].Material;
            var tex = pm.TextureId >= 0 && pm.TextureId < Textures().Count ? Textures()[pm.TextureId] : null;
            double w = tex?.Width ?? 32, h = tex?.Height ?? 32;
            float u = (float)(v.S / 32.0 * pm.ScaleS / 65536.0 / w), t = (float)(v.T / 32.0 * pm.ScaleT / 65536.0 / h);
            sb.Append("vt ").Append(u.ToString("R", ic)).Append(' ').Append((1f - t).ToString("R", ic)).Append('\n');
        }
        for (int part = 0; part < m.Parts.Length; part++)
        {
            var pt = tris.Where(t => t.Part == part).ToList();
            if (pt.Count == 0) continue;
            sb.Append("usemtl tex_").Append(m.Parts[part].Material.TextureId).Append("_f").Append(m.Parts[part].Material.Flags.ToString("X")).Append('\n');
            foreach (var t in pt) sb.Append($"f {t.V0 + 1}/{t.V0 + 1} {t.V1 + 1}/{t.V1 + 1} {t.V2 + 1}/{t.V2 + 1}\n");
        }
        return sb.ToString();
    }

    public void WriteAll()
    {
        Directory.CreateDirectory(_dir);
        foreach (var (k, text) in _obj) File.WriteAllText(Path.Combine(_dir, k + ".obj"), text);
    }

    public object Describe() => _info.OrderBy(k => k.Key).Select(k => k.Value).ToList();
}
