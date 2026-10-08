using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.LevelGeo;

/// <summary>
/// `trw levelgeo [rom] [outdir] [--level N]` (called as LevelGeoCommand.Run(args) with the arguments after the command name).
/// Per level writes: world.obj/.mtl (placed static objects, textured), collision.obj + collision.json, lights.json,
/// objects.json (placement list), nav.json (nav graph + actor placements), summary.json, topdown*.png; plus surface_types.json at the root.
/// </summary>
public static class LevelGeoCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    public static int Run(string[] args)
    {
        var a = args.Where((x, i) => !(i == 0 && x.Equals("levelgeo", StringComparison.OrdinalIgnoreCase))).ToList();
        int only = -1;
        int li = a.IndexOf("--level");
        if (li >= 0 && li + 1 < a.Count) { only = int.Parse(a[li + 1]); a.RemoveRange(li, 2); }
        bool noTop = a.Remove("--no-topdown");
        string rom = DefaultRom;
        if (a.Count > 0 && File.Exists(a[0])) { rom = a[0]; a.RemoveAt(0); }
        string outDir = a.Count > 0 ? a[0] : Path.Combine("extracted", "levelgeo");
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var ix = RomIndex.Load(File.ReadAllBytes(rom));
        var levels = LevelCatalog.Load(ix);
        var defs = LevelGeoDecode.ObjectDefs(ix);
        var ctx = new WorldContext(ix, defs, outDir);
        Console.WriteLine($"{levels.Count} levels, {ctx.Textures.Count} 3D textures, {ctx.ObjectCount} static objects, {defs.Count} object defs");

        var surfaceTally = new SortedDictionary<string, int>();
        var allSummaries = new List<object>();
        PieceLighting.FlickerFromRom = Enumerable.Range(0, 90).Select(i => (sbyte)ix.Rom[0xD3454 + i]).ToArray();   // D_800D2854
        LightingExport.Levels.Clear();
        foreach (var lv in levels)
        {
            if (only >= 0 && lv.Id != only) continue;
            if (lv.Node.Kind != NodeKind.Container || lv.Node.Children.Count < 13) continue;
            var dir = Path.Combine(outDir, $"{lv.Id:D2}_{Safe(lv.Name)}");
            Directory.CreateDirectory(dir);
            var sum = ExportLevel(ctx, lv, dir, surfaceTally, noTop);
            allSummaries.Add(sum);
        }
        var localDir = Path.Combine(outDir, "_objects"); Directory.CreateDirectory(localDir);
        foreach (var (k, text) in ctx.LocalMeshes) File.WriteAllText(Path.Combine(localDir, k + ".obj"), text);
        File.WriteAllText(Path.Combine(outDir, "levels_summary.json"), JsonSerializer.Serialize(allSummaries, Json));
        if (only < 0) LightingExport.WriteRoot(outDir);
        File.WriteAllText(Path.Combine(outDir, "surface_types.json"), JsonSerializer.Serialize(SurfaceTable(surfaceTally), Json));
        Console.WriteLine($"wrote {outDir}");
        return 0;
    }

    static object SurfaceTable(SortedDictionary<string, int> tally) => new
    {
        note = "Distinct collision-mesh triangle (surface word, flags) pairs over all levels, with counts. The shipped data contains a single surface word (0xFFFFFFFF); the game replaces it at load time with global D_800D2B40 when it does not intersect that mask. Water/lava/kill semantics are NOT stored in the level triangles (see docs/formats/level_geometry.md).",
        triangleKinds = tally,
    };

    static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
    static string F(float v) => v.ToString("0.###", Inv);

    // ------------------------------------------------------------------------------------------------------------------
    sealed class LoadedModel
    {
        public Model Model = null!;
        public List<MeshTriangle> Tris = [];
        public int[] PartOfVertex = [];
    }

    sealed class WorldContext
    {
        public readonly RomIndex Ix;
        public readonly IReadOnlyList<ObjectDef> Defs;
        public readonly List<CiTexture?> Textures = [];
        public readonly string OutDir;
        /// <summary>Each distinct (object, model) mesh in raw model space, for viewers that assemble levels themselves: key obj_&lt;ObjectId&gt;_&lt;ModelIndex&gt;.</summary>
        public readonly Dictionary<string, string> LocalMeshes = new();
        public readonly string TextureDir;
        readonly Dictionary<(int, int), LoadedModel?> _models = new();
        readonly Dictionary<int, (byte R, byte G, byte B)> _avg = new();
        public int ObjectCount => Ix.Find(0)?.Children.Count ?? 0;

        public WorldContext(RomIndex ix, IReadOnlyList<ObjectDef> defs, string outDir)
        {
            Ix = ix; Defs = defs; OutDir = outDir;
            var tex = ix.Find(ModelsCommand.MasterTextureSection);
            if (tex != null) foreach (var c in tex.Children) Textures.Add(CiTexture.Parse(ix.Read(c)));
            // Textures are exported by `trw models <dir> --all` (extracted/models/textures); reference them from there.
            string guess = Path.GetFullPath(Path.Combine(outDir, "..", "models", "textures"));
            TextureDir = guess;
        }

        public int ModelCount(int objectId) => Ix.Find(0, objectId, 1)?.Children.Count ?? 0;

        public LoadedModel? GetModel(int objectId, int lod)
        {
            int n = ModelCount(objectId);
            if (n == 0) return null;
            int idx = lod < 0 ? 0 : lod % n;
            if (_models.TryGetValue((objectId, idx), out var m)) return m;
            LoadedModel? lm = null;
            var node = Ix.Find(0, objectId, 1, idx);
            if (node != null && node.Kind == NodeKind.Rnc)
            {
                var model = Model.Parse(Ix.Read(node));
                if (model != null)
                {
                    lm = new LoadedModel { Model = model, Tris = model.BuildTriangles() };
                    lm.PartOfVertex = model.PartOfVertex;      // includes the vertices added for UV overrides (Models/Model.cs)
                }
            }
            _models[(objectId, idx)] = lm;
            return lm;
        }

        public (byte R, byte G, byte B) AverageColor(int texId)
        {
            if (texId < 0 || texId >= Textures.Count || Textures[texId] == null) return (160, 160, 160);
            if (_avg.TryGetValue(texId, out var c)) return c;
            var t = Textures[texId]!;
            var px = t.ToRgba(0);
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i < px.Length; i += 4) if (px[i + 3] != 0) { r += px[i]; g += px[i + 1]; b += px[i + 2]; n++; }
            c = n == 0 ? ((byte)160, (byte)160, (byte)160) : ((byte)(r / n), (byte)(g / n), (byte)(b / n));
            _avg[texId] = c;
            return c;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    sealed class WorldMesh
    {
        public readonly List<Vector3> Pos = [];
        public readonly List<(byte R, byte G, byte B)> Col = [];
        public readonly List<(float U, float V)> Uv = [];
        public readonly List<(int A, int B, int C, int Mat, int Sector)> Tris = [];
        public readonly List<string> MatNames = [];
        public readonly List<(int Tex, uint Flags, byte[] C1)> MatInfo = [];
        public readonly Dictionary<string, int> MatIndex = new();
        public int MissingModels;
        public float MinU = float.MaxValue, MaxU = float.MinValue, MinV = float.MaxValue, MaxV = float.MinValue;
        public int UvOutOfRange;
        public readonly SortedSet<(int Obj, int Tex, int Frames)> Animated = new();
    }

    static object ExportLevel(WorldContext ctx, Level lv, string dir, SortedDictionary<string, int> surfaceTally, bool noTop)
    {
        var grid = lv.Grid();
        var sectors = lv.Sectors().ToList();
        var placements = new List<Placement>();
        var lights = new List<LightVolume>();
        foreach (var s in sectors)
        {
            placements.AddRange(LevelGeoDecode.Placements(s, s.Index, ctx.Defs));
            lights.AddRange(LevelGeoDecode.Lights(s, s.Index));
        }
        var coll = LevelGeoDecode.Collision(lv);

        // ---- world mesh -----------------------------------------------------------------------------------------
        var wm = new WorldMesh();
        var usage = new Dictionary<int, int>();
        foreach (var p in placements)
        {
            var lm = ctx.GetModel(p.ObjectId, p.ModelIndex);
            if (lm == null) { wm.MissingModels++; continue; }
            usage[p.ObjectId] = usage.GetValueOrDefault(p.ObjectId) + 1;
            AddInstance(ctx, wm, lm, p);
        }
        WriteWorldObj(ctx, wm, dir, lv);

        // ---- collision ------------------------------------------------------------------------------------------
        if (coll != null)
        {
            foreach (var t in coll.Tris)
            {
                string k = $"surface=0x{t.Surface:X8} flags=0x{t.Flags:X4} word0=0x{t.Word0:X4}";
                surfaceTally[k] = surfaceTally.GetValueOrDefault(k) + 1;
            }
        }
        WriteCollision(dir, lv, coll, placements);

        // ---- lights, objects, nav --------------------------------------------------------------------------------
        File.WriteAllText(Path.Combine(dir, "lights.json"), JsonSerializer.Serialize(lights.Select(l => new
        {
            l.Sector, l.Record, position = V(l.Position), anglesRad = V(l.AnglesRad), extent = V(l.Extent),
            l.Falloff0, l.Falloff1, l.Type, rawHex = Convert.ToHexString(l.Raw),
            // decoded (docs/formats/lighting.md): Falloff0 = soft edge width, Falloff1 = ambient fraction; colour = u32(+0x3C,+0x40,+0x44) * 2.3748725652694702e-06
            colorRgb = new[] { Bin.U32(l.Raw, 0x3C), Bin.U32(l.Raw, 0x40), Bin.U32(l.Raw, 0x44) }.Select(u => (float)(double)u * 2.3748725652694702e-06f).ToArray(),
            negative = l.Raw[0x37] != 0, animKind = (int)l.Raw[0x3B], restricted = l.Raw[0x4B] != 0,
        }), Json));
        LightingExport.ExportLevel(lv, dir, sectors, placements, coll, (pl) => ctx.GetModel(pl.ObjectId, pl.ModelIndex)?.Model);
        File.WriteAllText(Path.Combine(dir, "objects.json"), JsonSerializer.Serialize(placements.Select(p => new
        {
            p.Sector, p.Record, form = p.Matrix ? "matrix34" : "compact1C", p.ObjectId, def = p.DefIndex, modelIndex = p.ModelIndex,
            textureKey = p.DefIndex >= 0 && p.DefIndex < ctx.Defs.Count ? (int)ctx.Defs[p.DefIndex].Part : 0,   // def +0x12 (s8): texture frame/palette selector, -1 = animated
            position = V(p.Position), scale = V(p.Scale),
            rotationQuat = new[] { p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W },
            yawDegrees = p.Matrix ? (float?)null : p.YawDegrees,
            word0 = $"0x{p.Word0:X8}", flags = $"0x{p.Flags:X2}", regionTriangle = p.RegionTri == 0xFFFF ? -1 : p.RegionTri,
            boundsMin = V(p.BoundsMin), boundsMax = V(p.BoundsMax),
        }), Json));
        var nodes = lv.NavNodes();
        var edges = lv.NavEdges();
        var matrix = lv.NavMatrix(out int mn);
        File.WriteAllText(Path.Combine(dir, "nav.json"), JsonSerializer.Serialize(new
        {
            note = "nodes: part 9, edges: part 10, reachability: part 11 (N x N, row-major), actors: part 3. Meanings of flags/objectType belong to the gameplay-definitions work.",
            nodes = nodes.Select(n => new { n.Index, x = n.X, y = n.Y, z = n.Z, flags = $"0x{n.Flags:X4}", objectType = $"0x{n.ObjectType:X4}" }),
            edges = edges.Select(e => new { e.Index, e.From, e.To, e.Type }),
            reachabilitySize = mn,
            reachabilityRows = Enumerable.Range(0, mn).Select(r => string.Concat(Enumerable.Range(0, mn).Select(c => matrix[r * mn + c] != 0 ? '1' : '0'))),
            actors = lv.Actors().Select(ac => new
            {
                ac.Index, x = ac.X, y = ac.Y, z = ac.Z, scale = ac.ScaleX,
                word1C = $"0x{ac.Word1C:X8}", type = $"0x{ac.Word20:X8}", flags = $"0x{ac.Word24:X8}",
            }),
        }, Json));

        // ---- statistics + verification -------------------------------------------------------------------------
        var wmin = wm.Pos.Count == 0 ? Vector3.Zero : wm.Pos.Aggregate(Vector3.Min);
        var wmax = wm.Pos.Count == 0 ? Vector3.Zero : wm.Pos.Aggregate(Vector3.Max);
        var bmin = placements.Count == 0 ? Vector3.Zero : placements.Select(p => p.BoundsMin).Aggregate(Vector3.Min);
        var bmax = placements.Count == 0 ? Vector3.Zero : placements.Select(p => p.BoundsMax).Aggregate(Vector3.Max);
        int outsideCell = 0;
        if (grid != null)
        {
            foreach (var p in placements)
            {
                if (p.Sector >= grid.Cells.Length) { outsideCell++; continue; }
                var c = grid.Cells[p.Sector];
                const float pad = 300f;
                if (p.Position.X < c.XMin - pad || p.Position.X > c.XMax + pad || p.Position.Z < c.ZMin - pad || p.Position.Z > c.ZMax + pad) outsideCell++;
            }
        }
        var summary = new
        {
            level = lv.Id, name = lv.Name,
            grid = grid == null ? null : new { grid.Columns, grid.Rows, cells = grid.Cells.Length, sectorsWithData = sectors.Count },
            placements = placements.Count, compact = placements.Count(p => !p.Matrix), matrix = placements.Count(p => p.Matrix),
            distinctObjects = usage.Count, missingModels = wm.MissingModels,
            worldVertices = wm.Pos.Count, worldTriangles = wm.Tris.Count, materials = wm.MatNames.Count,
            worldMin = V(wmin), worldMax = V(wmax), placementBoundsMin = V(bmin), placementBoundsMax = V(bmax),
            uvRange = wm.Pos.Count == 0 ? null : new { minU = wm.MinU, maxU = wm.MaxU, minV = wm.MinV, maxV = wm.MaxV, outsideMinus64To64 = wm.UvOutOfRange },
            placementsOutsideTheirCell300 = outsideCell,
            lights = lights.Count,
            animatedTextureObjects = wm.Animated.Select(a => new { objectId = a.Obj, texture = a.Tex, frames = a.Frames }).ToArray(),
            collisionTris = coll?.Tris.Length ?? 0, collisionVerts = coll?.Vertices.Length ?? 0, zones = coll?.Zones.Length ?? 0,
            navNodes = nodes.Length, navEdges = edges.Length, actors = lv.Actors().Length,
        };
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(summary, Json));
        if (!noTop && wm.Pos.Count > 0) TopDown.Render(ctx.AverageColor, wm.Pos, wm.Col, wm.Tris, wm.MatInfo.Select(m => m.Tex).ToList(), placements, coll, nodes, dir);
        Console.WriteLine($"  {lv.Id:D2} {lv.Name,-18} placements={placements.Count,4} tris={wm.Tris.Count,7} verts={wm.Pos.Count,7} lights={lights.Count,3} coll={coll?.Tris.Length ?? 0,2} missing={wm.MissingModels}");
        return summary;
    }

    static float[] V(Vector3 v) => [v.X, v.Y, v.Z];

    /// <summary>Models are authored Z-up; the world is Y-up. A proper rotation (determinant +1), so winding is unchanged.</summary>
    static readonly Matrix4x4 ZUp = new(1, 0, 0, 0,  0, 0, -1, 0,  0, 1, 0, 0,  0, 0, 0, 1);

    /// <summary>Extra turn of each piece about its own up axis, in degrees (env TRW_MODEL_YAW, default 0), applied before the placement rotation.</summary>
    static readonly Matrix4x4 ModelToWorld = ZUp * Matrix4x4.CreateRotationY(
        float.Parse(Environment.GetEnvironmentVariable("TRW_MODEL_YAW") ?? "0", System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180f);


    /// <summary>Raw model-space mesh (no Z-up conversion, no placement) so a viewer can try different orientations. Materials are named tex_&lt;textureId&gt;.</summary>
    static void EmitLocalMesh(WorldContext ctx, Placement p, LoadedModel lm)
    {
        string key = $"obj_{p.ObjectId}_{p.ModelIndex}";
        if (ctx.LocalMeshes.ContainsKey(key)) return;
        var m = lm.Model;
        var sb = new System.Text.StringBuilder();
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("# model-space mesh (Z-up, unplaced) for ").Append(key).Append('\n');
        foreach (var v in m.Vertices) sb.Append("v ").Append(((float)v.X).ToString("R", ic)).Append(' ').Append(((float)v.Y).ToString("R", ic)).Append(' ').Append(((float)v.Z).ToString("R", ic)).Append('\n');
        foreach (var (v, i) in m.Vertices.Select((v, i) => (v, i)))
        {
            var pm = m.Parts[lm.PartOfVertex[i]].Material;
            var tex = pm.TextureId >= 0 && pm.TextureId < ctx.Textures.Count ? ctx.Textures[pm.TextureId] : null;
            double w = tex?.Width ?? 32, h = tex?.Height ?? 32;
            float u = (float)(v.S / 32.0 * pm.ScaleS / 65536.0 / w), t = (float)(v.T / 32.0 * pm.ScaleT / 65536.0 / h);
            sb.Append("vt ").Append(u.ToString("R", ic)).Append(' ').Append((1f - t).ToString("R", ic)).Append('\n');
        }
        // vn: the vertex RGB bytes of a static model are signed-byte NORMALS (func_00277620 reads them with lb), model space, same index as v/vt
        // (unit length; (0,0,0) stays zero). The colours the game really draws are computed per placement: vertexlight.bin / docs/formats/lighting.md.
        foreach (var v in m.Vertices)
        {
            float nx = (sbyte)v.R, ny = (sbyte)v.G, nz = (sbyte)v.B;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            sb.Append("vn ").Append(nx.ToString("0.#####", ic)).Append(' ').Append(ny.ToString("0.#####", ic)).Append(' ').Append(nz.ToString("0.#####", ic)).Append('\n');
        }
        for (int part = 0; part < m.Parts.Length; part++)
        {
            var tris = lm.Tris.Where(t => t.Part == part).ToList();
            if (tris.Count == 0) continue;
            sb.Append("usemtl tex_").Append(m.Parts[part].Material.TextureId).Append("_f").Append(m.Parts[part].Material.Flags.ToString("X")).Append('\n');
            foreach (var t in tris) sb.Append($"f {t.V0 + 1}/{t.V0 + 1}/{t.V0 + 1} {t.V1 + 1}/{t.V1 + 1}/{t.V1 + 1} {t.V2 + 1}/{t.V2 + 1}/{t.V2 + 1}\n");
        }
        ctx.LocalMeshes[key] = sb.ToString();
    }

    static void AddInstance(WorldContext ctx, WorldMesh wm, LoadedModel lm, Placement p)
    {
        var m = lm.Model;
        var mat = p.ToWorld();
        int baseIdx = wm.Pos.Count;
        // per-part material ids
        var matOfPart = new int[m.Parts.Length];
        for (int pi = 0; pi < m.Parts.Length; pi++)
        {
            var pm = m.Parts[pi].Material;
            string key = $"{pm.TextureId}_{pm.Flags:X}_{Convert.ToHexString(pm.Color1)}";
            if (!wm.MatIndex.TryGetValue(key, out int id))
            {
                id = wm.MatNames.Count;
                wm.MatNames.Add($"m{pm.TextureId:D3}_{id}");
                wm.MatInfo.Add((pm.TextureId, pm.Flags, pm.Color1));
                wm.MatIndex[key] = id;
            }
            matOfPart[pi] = id;
            if (pm.TextureId >= 0 && pm.TextureId < ctx.Textures.Count && ctx.Textures[pm.TextureId] is { } at && at.Frames.Length > 1)
                wm.Animated.Add((p.ObjectId, pm.TextureId, at.Frames.Length));
        }
        for (int i = 0; i < m.Vertices.Length; i++)
        {
            var v = m.Vertices[i];
            wm.Pos.Add(Vector3.Transform(Vector3.Transform(new Vector3(v.X, v.Y, v.Z), ModelToWorld), mat));   // model space is Z-up: (x,y,z) -> (x,z,-y), verified against the game's own AABBs (284/290 in level 0)
            wm.Col.Add((v.R, v.G, v.B));
            var pm = m.Parts[lm.PartOfVertex[i]].Material;
            var tex = pm.TextureId >= 0 && pm.TextureId < ctx.Textures.Count ? ctx.Textures[pm.TextureId] : null;
            double w = tex?.Width ?? 32, h = tex?.Height ?? 32;
            float u = (float)(v.S / 32.0 * pm.ScaleS / 65536.0 / w);
            float t = (float)(v.T / 32.0 * pm.ScaleT / 65536.0 / h);
            wm.Uv.Add((u, 1f - t));
            if (tex != null)
            {
                wm.MinU = Math.Min(wm.MinU, u); wm.MaxU = Math.Max(wm.MaxU, u);
                wm.MinV = Math.Min(wm.MinV, t); wm.MaxV = Math.Max(wm.MaxV, t);
                if (Math.Abs(u) > 64 || Math.Abs(t) > 64) wm.UvOutOfRange++;
            }
        }
        EmitLocalMesh(ctx, p, lm);
        // uniform negative scale flips winding; keep triangles consistent with the transform
        bool flip = (p.Scale.X * p.Scale.Y * p.Scale.Z) < 0;
        foreach (var t in lm.Tris)
        {
            int a = baseIdx + t.V0, b = baseIdx + t.V1, c = baseIdx + t.V2;
            if (flip != ((m.Parts[t.Part].Material.Flags & 1) != 0)) (b, c) = (c, b);   // material flag bit 0x1 = reversed (front-face) culling, see level_geometry.md
            wm.Tris.Add((a, b, c, matOfPart[t.Part], p.Sector));
        }
    }

    static void WriteWorldObj(WorldContext ctx, WorldMesh wm, string dir, Level lv)
    {
        var sb = new StringBuilder(1 << 20);
        sb.AppendLine($"# Turok: Rage Wars level {lv.Id} {lv.Name}: placed static objects (world units, +Y up). Textures: ../../models/textures/");
        sb.AppendLine("mtllib world.mtl");
        foreach (var (p, c) in wm.Pos.Zip(wm.Col)) sb.Append("v ").Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(F(p.Z))
            .Append(' ').Append(F(c.R / 255f)).Append(' ').Append(F(c.G / 255f)).Append(' ').Append(F(c.B / 255f)).Append('\n');
        foreach (var uv in wm.Uv) sb.Append("vt ").Append(uv.U.ToString("0.####", Inv)).Append(' ').Append(uv.V.ToString("0.####", Inv)).Append('\n');
        int lastMat = -1, lastSec = -1;
        foreach (var t in wm.Tris.OrderBy(t => t.Sector).ThenBy(t => t.Mat))
        {
            if (t.Sector != lastSec) { sb.Append("g sector_").Append(t.Sector).Append('\n'); lastSec = t.Sector; lastMat = -1; }
            if (t.Mat != lastMat) { sb.Append("usemtl ").Append(wm.MatNames[t.Mat]).Append('\n'); lastMat = t.Mat; }
            sb.Append("f ").Append(t.A + 1).Append('/').Append(t.A + 1).Append(' ').Append(t.B + 1).Append('/').Append(t.B + 1)
              .Append(' ').Append(t.C + 1).Append('/').Append(t.C + 1).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, "world.obj"), sb.ToString());

        var mtl = new StringBuilder();
        string texRel = Path.GetRelativePath(dir, ctx.TextureDir).Replace('\\', '/');
        for (int i = 0; i < wm.MatNames.Count; i++)
        {
            var (tex, flags, c1) = wm.MatInfo[i];
            mtl.AppendLine($"newmtl {wm.MatNames[i]}");
            mtl.AppendLine($"Kd {F(c1[0] / 255f)} {F(c1[1] / 255f)} {F(c1[2] / 255f)}");
            if (tex >= 0) mtl.AppendLine($"map_Kd {texRel}/tex_{tex:D3}_f0.png");
            mtl.AppendLine($"# textureId={tex} flags=0x{flags:X}");
        }
        File.WriteAllText(Path.Combine(dir, "world.mtl"), mtl.ToString());
    }

    // ------------------------------------------------------------------------------------------------------------------
    static void WriteCollision(string dir, Level lv, CollisionMesh? coll, List<Placement> placements)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Level {lv.Id} {lv.Name} collision: g world_tris = level part 1 triangle mesh; g object_boxes = actor-collision AABBs of placed objects");
        int vbase = 0;
        if (coll != null && coll.Tris.Length > 0)
        {
            sb.AppendLine("g world_tris");
            foreach (var v in coll.Vertices) sb.Append("v ").Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z)).Append('\n');
            foreach (var t in coll.Tris) sb.Append("f ").Append(t.V0 + 1).Append(' ').Append(t.V1 + 1).Append(' ').Append(t.V2 + 1).Append('\n');
            vbase = coll.Vertices.Length;
        }
        sb.AppendLine("g object_boxes");
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        foreach (var p in placements)
        {
            var a = p.BoundsMin; var b = p.BoundsMax;
            for (int c = 0; c < 8; c++)
                sb.Append("v ").Append(F((c & 1) != 0 ? b.X : a.X)).Append(' ').Append(F((c & 2) != 0 ? b.Y : a.Y)).Append(' ').Append(F((c & 4) != 0 ? b.Z : a.Z)).Append('\n');
            foreach (var f in faces)
                sb.Append("f ").Append(vbase + f[0] + 1).Append(' ').Append(vbase + f[1] + 1).Append(' ').Append(vbase + f[2] + 1).Append(' ').Append(vbase + f[3] + 1).Append('\n');
            vbase += 8;
        }
        File.WriteAllText(Path.Combine(dir, "collision.obj"), sb.ToString());

        var json = new
        {
            note = "triangles: level part 1 (coarse ground/region triangles with edge adjacency; every placed object stores the index of one of them); zones: part 1 section 0 100-byte records (unknown, see doc); rects: part 1 section 3 (xMin,zMin,xMax,zMax,flag); objectBoxes: AABB the game builds for each placed object (actor +0xB8..+0xCC).",
            vertices = coll?.Vertices.Select(v => new[] { v.X, v.Y, v.Z, v.W }),
            triangles = coll?.Tris.Select(t => new { v = new[] { t.V0, t.V1, t.V2 }, neighbours = new[] { t.N0, t.N1, t.N2 }.Select(n => n == 0xFFFF ? -1 : n), flags = $"0x{t.Flags:X4}", word0 = $"0x{t.Word0:X4}", surface = $"0x{t.Surface:X8}" }),
            zones = coll?.Zones.Select(z => new { words = z.Words.Select(w => $"0x{w:X8}"), floats = z.Floats.Select(f => float.IsFinite(f) && Math.Abs(f) < 1e7 && Math.Abs(f) > 1e-3 ? (float?)f : null) }),
            rects = coll?.Rects.Select((r, i) => new { xMin = r[0], zMin = r[1], xMax = r[2], zMax = r[3], flag = coll.RectFlags[i] }),
            objectBoxes = placements.Select(p => new { sector = p.Sector, record = p.Record, objectId = p.ObjectId, min = V(p.BoundsMin), max = V(p.BoundsMax), word0 = $"0x{p.Word0:X8}", flags = $"0x{p.Flags:X2}", fixedBox = (p.Flags & 0x40) != 0 }),
        };
        File.WriteAllText(Path.Combine(dir, "collision.json"), JsonSerializer.Serialize(json, Json));
    }
}
