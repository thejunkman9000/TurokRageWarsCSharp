using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Viewer;

/// <summary>Orientation experiments for placed pieces. Rotations are in 90 degree steps.
/// Final matrix per piece (row vectors): [Z-up] * Scale * Pre(X,Y,Z) * [mirror X] * PlacementRotation(yaw or quaternion) * Post(X,Y,Z) * Translation.
/// Pre turns the model itself (in the converted Y-up frame) before it is placed; Post turns the whole placed piece about its own origin.</summary>
public sealed class RotationSettings
{
    public int PreX, PreY, PreZ, PostX, PostY, PostZ;
    public bool FlipPlacement;     // use the inverse of the placement rotation
    public bool SwapXZ;            // reflect the placement rotation through an X<->Z swap: q = (-z, -y, -x, w). Replaces FlipPlacement; verified on all 250 quaternion pieces
    public bool ZUpConversion = true;
    public bool MirrorX;
    public bool PostMirrorX, PostMirrorY, PostMirrorZ;       // mirrors in the final world frame (after the placement rotation and post turns)

    public void Reset() { PreX = PreY = PreZ = PostX = PostY = PostZ = 0; FlipPlacement = false; SwapXZ = false; ZUpConversion = true; MirrorX = false; PostMirrorX = PostMirrorY = PostMirrorZ = false; }

    public override string ToString() =>
        $"zup={(ZUpConversion ? "on" : "off")} pre=({PreX * 90},{PreY * 90},{PreZ * 90}) post=({PostX * 90},{PostY * 90},{PostZ * 90})" + (PostMirrorX ? " post-mirror-x" : "") + (PostMirrorY ? " post-mirror-y" : "") + (PostMirrorZ ? " post-mirror-z" : "") +
        $"{(FlipPlacement ? " placement-inverted" : "")}{(SwapXZ ? " placement-xz-swapped" : "")}{(MirrorX ? " mirror-x" : "")}";

    static Matrix Steps(int x, int y, int z) =>
        Matrix.CreateRotationX(x * MathHelper.PiOver2) * Matrix.CreateRotationY(y * MathHelper.PiOver2) * Matrix.CreateRotationZ(z * MathHelper.PiOver2);

    public Matrix Pre() =>
        (ZUpConversion ? new Matrix(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1) : Matrix.Identity) * Steps(PreX, PreY, PreZ) *
        (MirrorX ? Matrix.CreateScale(-1, 1, 1) : Matrix.Identity);

    /// <summary>The Z-up conversion alone. The placement's scale belongs right after this, in the converted frame (verified against the game's AABBs).</summary>
    public Matrix Base() => ZUpConversion ? new Matrix(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1) : Matrix.Identity;

    /// <summary>Extra 90 degree turns and the optional mirror, applied after the scale.</summary>
    public Matrix Tail() => Steps(PreX, PreY, PreZ) * (MirrorX ? Matrix.CreateScale(-1, 1, 1) : Matrix.Identity);

    public Matrix Post() => Steps(PostX, PostY, PostZ) * Matrix.CreateScale(PostMirrorX ? -1 : 1, PostMirrorY ? -1 : 1, PostMirrorZ ? -1 : 1);
}

/// <summary>Builds a level from objects.json plus the raw model-space meshes in levelgeo/_objects, so the orientation can be changed live.</summary>
public static class InstanceScene
{
    sealed class Local
    {
        public Vector3[] V = [];
        public Vector2[] UV = [];
        public Dictionary<(int Tex, int Flags), List<int>> TrisByTexture = new();     // (texture id, material flag 0x1 = reversed culling) -> flat vertex indices, 3 per triangle
    }

    static readonly Dictionary<string, Local?> Cache = new();

    /// <summary>Material flag bit 0x1 = reversed winding (front-face culling). G toggles it in the viewer so the affected parts are easy to spot.</summary>
    /// <summary>The level layout is mirrored on one world axis: piece POSITIONS are mirrored, while each piece keeps its own proper (non-mirroring)
    /// rotation chosen in RotationSettings. (Mirroring vertices as well would double-count the per-piece settings.)</summary>
    public static bool WorldMirrorX = true, WorldMirrorZ;      // the real maps are mirrored on X relative to the raw coordinates (confirmed by eye)

    /// <summary>Use the game's precomputed vertex lighting (vertexlight.bin) as the shade colour. Off = every vertex white (flat).</summary>
    public static bool Lighting = true;

    /// <summary>levelgeo/NN_name/vertexlight.bin: "TRWL", u32 1, u32 placement count, then per placement u32 n and n x (R, G, B, anim) in objects.json order.</summary>
    static List<Color[]?>? LoadVertexLight(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || b[0] != 'T' || b[1] != 'R' || b[2] != 'W' || b[3] != 'L') return null;
        int count = BitConverter.ToInt32(b, 8), p = 12;
        var result = new List<Color[]?>(count);
        for (int i = 0; i < count; i++)
        {
            int n = BitConverter.ToInt32(b, p); p += 4;
            var cols = new Color[n];
            for (int j = 0; j < n; j++, p += 4) cols[j] = new Color(b[p], b[p + 1], b[p + 2]);
            result.Add(cols);
        }
        return result;
    }

    public static int ReverseMode = 0;     // 0 off, 1 reverse parts with flag 0x1, 2 the opposite: reverse every part WITHOUT flag 0x1

    public static bool Available(string levelDir) =>
        File.Exists(Path.Combine(levelDir, "objects.json")) && Directory.Exists(Path.Combine(Path.GetDirectoryName(levelDir)!, "_objects"));

    public static ObjScene Build(GraphicsDevice gd, string levelDir, string dataRoot, RotationSettings compact, RotationSettings matrix, Dictionary<string, Texture2D?> textureCache, Dictionary<int, PlacementOverride>? overrides = null)
    {
        string objects = Path.Combine(Path.GetDirectoryName(levelDir)!, "_objects");
        var scene = new ObjScene { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        var perTexture = new Dictionary<(int Tex, int Frame, int Flags), List<VertexPositionColorTexture>>();
        var perTextureInfo = new Dictionary<(int Tex, int Frame, int Flags), List<TriInfo>>();
        string texDir = Path.Combine(dataRoot, "models", "textures");
        var frameCounts = FrameCounts(texDir);
        var vertexLight = Lighting ? LoadVertexLight(Path.Combine(levelDir, "vertexlight.bin")) : null;
        int placementIndex = -1;

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(levelDir, "objects.json")));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            placementIndex++;
            bool isMatrix = e.GetProperty("form").GetString() == "matrix34";     // quaternion records vs yaw-only (0/90/180/270) records
            var qa = e.GetProperty("rotationQuat").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var info = new PlacementInfo
            {
                Index = placementIndex, Sector = e.GetProperty("Sector").GetInt32(), Record = e.GetProperty("Record").GetInt32(),
                ObjectId = e.GetProperty("ObjectId").GetInt32(), ModelIndex = e.GetProperty("modelIndex").GetInt32(), Def = e.GetProperty("def").GetInt32(),
                Form = e.GetProperty("form").GetString() ?? "", Flags = e.GetProperty("flags").GetString() ?? "",
                Position = Vec(e.GetProperty("position")), Scale = Vec(e.GetProperty("scale")),
                Rotation = new Quaternion(qa[0], qa[1], qa[2], qa[3]),
                TextureKey = e.TryGetProperty("textureKey", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetInt32() : 0,
                YawDegrees = e.TryGetProperty("yawDegrees", out var yd) && yd.ValueKind == JsonValueKind.Number ? yd.GetSingle() : null,
            };
            scene.Placements.Add(info);
            var ov = overrides != null && overrides.TryGetValue(placementIndex, out var o0) ? o0 : null;
            if (ov is { Hidden: true }) continue;

            string key = info.Key;
            if (!Cache.TryGetValue(key, out var mesh)) Cache[key] = mesh = LoadLocal(Path.Combine(objects, key + ".obj"));
            if (mesh == null) continue;

            var rs = isMatrix ? matrix : compact;
            Matrix post = rs.Post();
            var q = info.Rotation;
            if (rs.SwapXZ) q = new Quaternion(-q.Z, -q.Y, -q.X, q.W);
            else if (rs.FlipPlacement) q = Quaternion.Conjugate(q);
            Matrix tail = rs.Tail();
            if (ov != null && ov.MirrorX) tail *= Matrix.CreateScale(-1, 1, 1);
            Matrix m = rs.Base() * Matrix.CreateScale(info.Scale) * tail * Matrix.CreateFromQuaternion(q) * post
                       * Matrix.CreateRotationY(((ov?.YawSteps ?? 0) & 3) * MathHelper.PiOver2) * Matrix.CreateTranslation(MirroredPosition(info.Position));
            bool flip = (m.Determinant() < 0) != (ov != null && ov.FlipWinding);

            // the game lights static pieces on the CPU once per placement (docs/formats/lighting.md): one RGB per vertex, in the vertex order of the local mesh
            Color[]? vl = vertexLight != null && placementIndex < vertexLight.Count && vertexLight[placementIndex] is { } cols && cols.Length == mesh.V.Length ? cols : null;
            var world = new Vector3[mesh.V.Length];
            for (int i = 0; i < world.Length; i++)
            {
                world[i] = Vector3.Transform(mesh.V[i], m);
                scene.Min = Vector3.Min(scene.Min, world[i]); scene.Max = Vector3.Max(scene.Max, world[i]);
            }
            foreach (var ((tex, flags), idx) in mesh.TrisByTexture)
            {
                bool reversed = (flags & 1) != 0;
                // texture look: def +0x12 (s8) % frame count; -1 means time-animated in the game (shown as frame 0 here)
                int fc = frameCounts.GetValueOrDefault(tex, 1);
                int frame = info.TextureKey >= 0 && fc > 0 ? info.TextureKey % fc : (info.TextureKey < 0 && fc > 1 ? -1 : 0);   // -1: animated in time
                var k3 = (tex, frame, flags);
                if (!perTexture.TryGetValue(k3, out var list)) { perTexture[k3] = list = new(); perTextureInfo[k3] = new(); }
                var infos = perTextureInfo[k3];
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    int a2 = idx[i], b2 = idx[i + 1], c2 = idx[i + 2];
                    if (flip != (ReverseMode == 0 ? false : ReverseMode == 1 ? reversed : !reversed)) (b2, c2) = (c2, b2);
                    Color Shade(int vi) => vl != null ? vl[vi] : Color.White;
                    list.Add(new VertexPositionColorTexture(world[a2], Shade(a2), mesh.UV[a2]));
                    list.Add(new VertexPositionColorTexture(world[b2], Shade(b2), mesh.UV[b2]));
                    list.Add(new VertexPositionColorTexture(world[c2], Shade(c2), mesh.UV[c2]));
                    infos.Add(new TriInfo(placementIndex, tex, reversed, i / 3, flags, frame));
                }
            }
        }

        var anim = AnimData(levelDir);
        foreach (var (k3, verts) in perTexture.OrderBy(k => k.Key.Tex).ThenBy(k => k.Key.Frame).ThenBy(k => k.Key.Flags))
        {
            Texture2D? LoadFrame(int frameIndex)
            {
                string file = Path.Combine(texDir, $"tex_{k3.Tex:D3}_f{frameIndex}.png");
                if (!File.Exists(file)) file = Path.Combine(texDir, $"tex_{k3.Tex:D3}_f0.png");
                if (!textureCache.TryGetValue(file, out var tx))
                {
                    if (File.Exists(file)) { using var s = File.OpenRead(file); tx = Texture2D.FromStream(gd, s); } else tx = null;
                    textureCache[file] = tx;
                }
                return tx;
            }
            Texture2D[]? frames = null; Texture2D? first;
            if (k3.Frame < 0)
            {
                int fc = frameCounts.GetValueOrDefault(k3.Tex, 1);
                var list = new List<Texture2D>();
                for (int i = 0; i < fc; i++) if (LoadFrame(i) is { } tx) list.Add(tx);
                frames = list.Count > 1 ? list.ToArray() : null; first = list.Count > 0 ? list[0] : null;
            }
            else first = LoadFrame(k3.Frame);
            int sampler = (k3.Flags & 0x20) != 0 ? 2 : (k3.Flags & 0x800) != 0 ? 1 : 0;     // clamp, else mirror, else wrap (from the game's setup code)
            bool translucent = (k3.Flags & 0x100) != 0;
            anim.Materials.TryGetValue((k3.Tex, k3.Flags), out var mp);
            scene.Batches.Add(new Batch
            {
                Name = $"tex_{k3.Tex:D3} {(k3.Frame < 0 ? "animated" : "look " + k3.Frame)} flags 0x{k3.Flags:X}{(translucent ? " TRANSLUCENT" : "")}",
                Sampler = sampler, Vertices = verts.ToArray(), Tris = perTextureInfo[k3].ToArray(), Texture = first, Frames = frames,
                StepTicks = Math.Max(1, anim.StepTicks.GetValueOrDefault(k3.Tex, 1)),
                Translucent = translucent, Alpha = translucent && mp != null ? mp.Alpha : 1f,
                Tint = (k3.Flags & 0x400) != 0 && mp != null ? mp.Colour1 : Vector3.One,
                SortKey = mp != null ? mp.SortKey : 0,
            });
        }
        return scene;
    }

    /// <summary>The model-space vertices (and UVs) of one triangle of an object, for the face inspector.</summary>
    public static string DescribeLocalTriangle(PlacementInfo p, TriInfo t)
    {
        if (!Cache.TryGetValue(p.Key, out var mesh) || mesh == null || !mesh.TrisByTexture.TryGetValue((t.Texture, t.MaterialFlags), out var idx)) return "(no local mesh)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            int v = idx[t.LocalTriangle * 3 + i];
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    local v{i}: ({mesh.V[v].X:F1}, {mesh.V[v].Y:F1}, {mesh.V[v].Z:F1})  uv ({mesh.UV[v].X:F3}, {mesh.UV[v].Y:F3})  vertex #{v}"));
        }
        return sb.ToString();
    }

    sealed class MaterialParams { public float Alpha = 1f; public Vector3 Colour1 = Vector3.One; public int SortKey; }
    sealed class AnimTables { public Dictionary<(int, int), MaterialParams> Materials = new(); public Dictionary<int, int> StepTicks = new(); }
    static AnimTables? _anim;

    /// <summary>Material translucency/tint and texture animation steps from levelgeo/texture_anim.json (see docs/formats/transparency_and_animation.md).</summary>
    static AnimTables AnimData(string levelDir)
    {
        if (_anim != null) return _anim;
        var tables = new AnimTables();
        string path = Path.Combine(Path.GetDirectoryName(levelDir)!, "texture_anim.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var tx in doc.RootElement.GetProperty("textures").EnumerateArray())
                tables.StepTicks[tx.GetProperty("textureId").GetInt32()] = tx.GetProperty("stepTicks").GetInt32();
            foreach (var obj in doc.RootElement.GetProperty("objects").EnumerateArray())
                foreach (var m in obj.GetProperty("materials").EnumerateArray())
                {
                    int tex = m.GetProperty("textureId").GetInt32();
                    int flags = Convert.ToInt32(m.GetProperty("flags").GetString()!, 16);
                    if (tables.Materials.ContainsKey((tex, flags))) continue;
                    var c1 = m.GetProperty("colour1").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    tables.Materials[(tex, flags)] = new MaterialParams
                    {
                        Alpha = m.GetProperty("alpha").GetSingle(),
                        Colour1 = new Vector3(c1[0], c1[1], c1[2]) / 255f,
                        SortKey = m.GetProperty("field8").GetInt32() * 2 | (flags & 0x40),
                    };
                }
        }
        return _anim = tables;
    }

    static Dictionary<int, int>? _frameCounts;
    /// <summary>Number of exported looks (frames) per 3D texture id, from the PNG file names tex_NNN_fK.png.</summary>
    static Dictionary<int, int> FrameCounts(string texDir)
    {
        if (_frameCounts != null) return _frameCounts;
        var d = new Dictionary<int, int>();
        if (Directory.Exists(texDir))
            foreach (string f in Directory.GetFiles(texDir, "tex_*_f*.png"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"tex_(\d+)_f(\d+)\.png");
                if (m.Success) { int id = int.Parse(m.Groups[1].Value), fr = int.Parse(m.Groups[2].Value) + 1; if (fr > d.GetValueOrDefault(id)) d[id] = fr; }
            }
        return _frameCounts = d;
    }

    static Vector3 MirroredPosition(Vector3 p) => new(WorldMirrorX ? -p.X : p.X, p.Y, WorldMirrorZ ? -p.Z : p.Z);

    static Vector3 Vec(JsonElement a) { var v = a.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new Vector3(v[0], v[1], v[2]); }

    static Local? LoadLocal(string path)
    {
        if (!File.Exists(path)) return null;
        var l = new Local(); var v = new List<Vector3>(); var uv = new List<Vector2>(); int tex = -1; int flags = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length < 2 || line[0] == '#') continue;
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v": v.Add(new Vector3(F(t[1]), F(t[2]), F(t[3]))); break;
                case "vt": uv.Add(new Vector2(F(t[1]), 1f - F(t[2]))); break;
                case "usemtl":                                 // tex_<id>_f<flags hex>
                    var parts = t[1].Split('_');
                    tex = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    flags = parts.Length > 2 ? int.Parse(parts[2][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
                    break;
                case "f":
                    if (!l.TrisByTexture.TryGetValue((tex, flags), out var list)) l.TrisByTexture[(tex, flags)] = list = new();
                    for (int i = 1; i < 4; i++) list.Add(int.Parse(t[i].Split('/')[0]) - 1);
                    break;
            }
        }
        l.V = v.ToArray(); l.UV = uv.ToArray();
        return l;
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
