using System.Numerics;
using System.Text.Json;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.LevelGeo;

/// <summary>Writes lighting.json (root) and vertexlight.bin (per level). Decoded in docs/formats/lighting.md.</summary>
public static class LightingExport
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    public static readonly List<object> Levels = [];

    static float[] Mirror(float[] m)
    {
        // M' = D M D with D = diag(-1,1,1,1) (X mirror, the baked frame of docs/formats/baked_levels.md)
        var r = (float[])m.Clone();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                if ((i == 0) != (j == 0)) r[i * 4 + j] = -r[i * 4 + j];
        return r;
    }

    static float[] V(Vector3 v) => [v.X, v.Y, v.Z];
    static float[] VM(Vector3 v) => [-v.X, v.Y, v.Z];

    public static void ExportLevel(Level lv, string dir, List<Sector> sectors, List<Placement> placements, CollisionMesh? coll, Func<Placement, Model?> getModel)
    {
        // volumes per sector, in record order (func_0028795C builds itemsD in record order; func_00278090 walks them forward)
        var bySector = new Dictionary<int, List<VolumeRt>>();
        var all = new List<VolumeRt>();
        foreach (var s in sectors)
        {
            var l = new List<VolumeRt>();
            var a = s.Records4C;
            for (int i = 0; i < a.Count && a.Stride == 0x4C; i++)
                l.Add(VolumeRt.FromRecord(s.Index, i, a.Data.AsSpan(i * 0x4C, 0x4C).ToArray()));
            bySector[s.Index] = l;
            all.AddRange(l);
        }
        var sectorSec4 = sectors.ToDictionary(s => s.Index, s => s.Section4);
        bool levelHas = all.Count > 0;
        int restrictedUsed = all.Count(v => v.Restrict != 0);
        // restricted volumes (+0x4B != 0) only apply to objects listed in sector array 4; every shipped array 4 is the empty container {0,8}
        bool Ok(Placement p, VolumeRt v) => false;
        int nonEmptySec4 = sectorSec4.Values.Count(b => b.Length > 8);

        int nVol = 0, nNone = 0, nFallback = 0;
        int totalVertices = 0;
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("TRWL"));
            w.Write((uint)1);
            w.Write((uint)placements.Count);
            foreach (var p in placements)
            {
                var model = getModel(p);
                if (model == null) { w.Write((uint)0); continue; }
                var lit = PieceLighting.Light(p, model, bySector.GetValueOrDefault(p.Sector) ?? [], levelHas, Ok);
                if (lit.Mode == "volumes") nVol++; else if (lit.Mode == "none") nNone++; else nFallback++;
                int n = lit.Anim.Length;
                totalVertices += n;
                w.Write((uint)n);
                for (int i = 0; i < n; i++) { w.Write(lit.Rgb[i * 3]); w.Write(lit.Rgb[i * 3 + 1]); w.Write(lit.Rgb[i * 3 + 2]); w.Write(lit.Anim[i]); }
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "vertexlight.bin"), ms.ToArray());

        // unique volumes (the same record is repeated in neighbouring sectors)
        var uniq = new List<(VolumeRt V, List<int> Sectors)>();
        var seen = new Dictionary<string, int>();
        foreach (var v in all)
        {
            string key = Convert.ToHexString(v.Raw);
            if (!seen.TryGetValue(key, out int ix)) { seen[key] = uniq.Count; uniq.Add((v, [v.Sector])); }
            else uniq[ix].Sectors.Add(v.Sector);
        }

        object? zones = null;
        float[]? fogRgb = null;
        if (coll != null)
        {
            zones = coll.Zones.Select((z, i) =>
            {
                var w = z.Words;
                int[] B(uint x) => [(byte)(x >> 24), (byte)(x >> 16), (byte)(x >> 8), (byte)x];
                return new
                {
                    index = i,
                    colorRgba = B(w[0]), altColorRgba = B(w[1]),
                    fogNear = (int)w[2], fogNearAlt = (int)w[3],
                    farPlane = z.Floats[4], fovDegrees = z.Floats[5], farPlaneAlt = z.Floats[6], fovDegreesAlt = z.Floats[7],
                    transitionTime = z.Floats[9],
                };
            }).ToArray();
            if (coll.Zones.Length > 0) { var b = coll.Zones[0].Words[0]; fogRgb = [(byte)(b >> 24) & 0xF8, (byte)(b >> 16) & 0xF8, (byte)(b >> 8) & 0xF8]; }
        }

        Levels.Add(new
        {
            level = lv.Id, name = lv.Name,
            clearAndFogColorRgb = fogRgb,
            zones,
            volumeRecords = all.Count, uniqueVolumes = uniq.Count,
            restrictedVolumes = restrictedUsed, nonEmptySectorArray4 = nonEmptySec4,
            placementLighting = new { withVolumes = nVol, noVolumeOverlap = nNone, fallbackNoVolumesInLevel = nFallback, vertices = totalVertices },
            volumes = uniq.Select(u => new
            {
                sectors = u.Sectors, record = u.V.Record,
                position = V(u.V.Pos), positionMirrorX = VM(u.V.Pos),
                anglesRad = V(u.V.AnglesRad), extent = V(u.V.Extent),
                softEdge = u.V.SoftEdge, ambientFraction = u.V.AmbientFraction,
                colorRgb = V(u.V.Color),
                shape = u.V.Shape, negative = u.V.Negative != 0, animKind = u.V.AnimKind, restricted = u.V.Restrict != 0, axisAligned = u.V.Aligned,
                matrix = u.V.M, inverse = u.V.Inv, matrixMirrorX = Mirror(u.V.M), inverseMirrorX = Mirror(u.V.Inv),
                aabbMin = V(u.V.Min), aabbMax = V(u.V.Max),
                aabbMinMirrorX = new[] { -u.V.Max.X, u.V.Min.Y, u.V.Min.Z }, aabbMaxMirrorX = new[] { -u.V.Min.X, u.V.Max.Y, u.V.Max.Z },
                rawHex = Convert.ToHexString(u.V.Raw),
            }).ToArray(),
        });
        Console.WriteLine($"    lighting: volumes={all.Count} (unique {uniq.Count}) placements: volumes={nVol} none={nNone} fallback={nFallback} restricted={restrictedUsed} sec4={nonEmptySec4}");
    }

    public static void WriteRoot(string outDir)
    {
        var root = new
        {
            note = "Lighting of the game, decoded in docs/formats/lighting.md. Positions/matrices are in the RAW game frame unless the name says MirrorX (x -> -x, the viewer/baked frame).",
            frames = "raw: positions as in objects.json before the viewer's X mirror. *MirrorX: x negated, matrices conjugated with diag(-1,1,1,1).",
            staticPieces = new
            {
                summary = "Static placed pieces are lit ON THE CPU once per placement (func_00278530): vertex colour = clamp(bakedOffset + sum over overlapping light volumes of colour*weight*(ambientFraction + (1-ambientFraction)*max(0, N.L)), 5, 255); the RSP does no lighting for them (material flag 0x40/0x2, G_LIGHTING off).",
                vertexColorBytesAreNormals = true,
                clampMin = 5, clampMax = 255,
                brightnessOptionDefault = 0, brightnessLift = "c = c*(1-b/255) + 255*b/255 (D_801462D8 = options brightness, 0..255)",
                softEdgeTableFormula = "T[i] = (i/64)^0.25 for i = int(d2*64); weight w = (1-T) < softEdge ? (1-T)/softEdge : 1",
                flickerTable = string.Join(",", PieceLighting.FlickerFromRom ?? []) + "   (90 s8 values, D_800D2854 = ROM 0xD3454)",
                flickerFormula = "every 2nd game tick: delta = (s8)table[(tick + ((flags & 0x70) >> 2)) & 0x3F] >> ((flags & 0x0C) >> 2); colour = clamp(base + delta, 0, 255) on R,G,B",
                animByte = "vertexlight.bin 4th byte: bits 0x70 phase, bits 0x0C shift; 0 = static",
            },
            actors = new
            {
                defaultLights = new { ambientRgb = new[] { 56, 56, 56 }, lightRgb = new[] { 255, 255, 255 }, lightDirS8 = new[] { -42, 30, -36 }, source = "D_800D0EE0 (ROM 0xD1AE0, 24 bytes), also loaded by the frame display list D_800D0F10" },
                summary = "Actors/characters get a Lights1 {ambient, one directional light} recomputed every frame from the volumes at their position (func_0028994C), then drawn with RSP lighting (G_LIGHTING) using the vertex bytes as normals.",
            },
            fog = new
            {
                summary = "Fog colour = zone colour & 0xF8 (also the clear colour). G_MW_FOG mult = 128000/(1000-near), offset = (500-near)*256/(1000-near); near = min(zone.fogNear, 995). Fog factor = clamp(zb*mult/256 ... ) over the depth-buffer range zb 0..1000 (near=16 plane).",
                nearPlane = 16.0f,
                effect = "near is 998..1000 in all shipped levels -> clamped to 995: fog only affects the last 0.5% of the depth range (z > ~1500 for far 2816), fading to the zone colour (black in most levels).",
            },
            levels = Levels,
        };
        File.WriteAllText(Path.Combine(outDir, "lighting.json"), JsonSerializer.Serialize(root, Json));
    }
}
