using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>A skinned creature mesh (extracted/creatures/obj_N.json, docs/formats/creatures_export.md): every vertex is bound rigidly to one bone and each clip stores a world matrix per
/// bone per frame (row-major, row-vector convention). Mesh variants (obj_174_mK) borrow the base object's clips.</summary>
public sealed class Creature
{
    sealed class Material { public int Texture, Flags, PartGroup, Field8; public int[] Triangles = []; public float Alpha = 1f; public Vector3 Colour1 = Vector3.One; }
    public sealed class Clip { public int Frames, Rate = 15, LoopFrame; public string Role = ""; public Matrix[][] Bones = []; }

    public List<Clip> Clips = new();
    Vector3[] _pos = []; Vector2[] _uv = []; int[] _bone = [];
    readonly List<Material> _materials = new();
    Matrix[] _rest = [];
    int _boneCount;

    static readonly Dictionary<string, Creature?> Cache = new();
    static readonly Dictionary<string, Texture2D?> Textures = new();
    static Dictionary<int, string[]>? _skins;
    static HashSet<int>? _flipV;

    /// <summary>Loads creatures/obj_N.json (name without extension), or null when missing.</summary>
    public static Creature? Load(string dataRoot, string name)
    {
        if (Cache.TryGetValue(name, out var c)) return c;
        string path = Path.Combine(dataRoot, "creatures", name + ".json");
        try { c = File.Exists(path) ? Read(path) : null; } catch { c = null; }
        return Cache[name] = c;
    }

    static int I(JsonElement e) => e.TryGetInt32(out int v) ? v : unchecked((int)e.GetInt64());       // some flag words exceed Int32

    static Creature Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var c = new Creature { _boneCount = r.GetProperty("boneCount").GetInt32() };
        float[] pos = r.GetProperty("positions").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        float[] uv = r.GetProperty("uvs").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        c._pos = Enumerable.Range(0, pos.Length / 3).Select(i => new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2])).ToArray();
        c._uv = Enumerable.Range(0, uv.Length / 2).Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray();           // v = t, like the level geometry
        c._bone = r.GetProperty("vertexBone").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        foreach (var m in r.GetProperty("materials").EnumerateArray())
        {
            var mat = new Material { Texture = I(m.GetProperty("texture")), Flags = I(m.GetProperty("flags")), Triangles = m.GetProperty("triangles").EnumerateArray().Select(I).ToArray() };
            if (m.TryGetProperty("partGroup", out var pg) && pg.ValueKind == JsonValueKind.Number) mat.PartGroup = I(pg);
            if (m.TryGetProperty("field8", out var f8) && f8.ValueKind == JsonValueKind.Number) mat.Field8 = I(f8);
            if (m.TryGetProperty("color1", out var c1) && c1.ValueKind == JsonValueKind.String && c1.GetString() is { Length: 8 } hex)
            {
                byte B(int i) => Convert.ToByte(hex.Substring(i * 2, 2), 16);
                mat.Colour1 = new Vector3(B(0), B(1), B(2)) / 255f; mat.Alpha = B(3) / 255f;
            }
            c._materials.Add(mat);
        }
        if (r.TryGetProperty("restWorld", out var rw)) c._rest = Matrices(rw.GetString()!, 1, c._boneCount)[0];
        foreach (var k in r.GetProperty("clips").EnumerateArray())
        {
            int frames = k.GetProperty("frames").GetInt32();
            c.Clips.Add(new Clip
            {
                Frames = frames, Rate = k.TryGetProperty("rate", out var rt) ? rt.GetInt32() : 15, LoopFrame = k.TryGetProperty("loopFrame", out var lf) ? lf.GetInt32() : 0,
                Role = k.TryGetProperty("role", out var ro) ? ro.GetString() ?? "" : "", Bones = Matrices(k.GetProperty("boneWorld").GetString()!, frames, c._boneCount),
            });
        }
        if (c.Clips.Count == 0)                                                               // a mesh variant (obj_174_m5): same skeleton and animations as the base object
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int us = name.IndexOf("_m", StringComparison.Ordinal);
            string basePath = us > 0 ? Path.Combine(Path.GetDirectoryName(path)!, name[..us] + ".json") : "";
            if (basePath.Length > 0 && File.Exists(basePath))
            {
                var b = Read(basePath);
                if (b._boneCount == c._boneCount) { c.Clips = b.Clips; if (c._rest.Length == 0) c._rest = b._rest; }
            }
        }
        return c;
    }

    static Matrix[][] Matrices(string base64, int frames, int bones)
    {
        byte[] raw = Convert.FromBase64String(base64);
        var f = new float[raw.Length / 4]; Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        var result = new Matrix[frames][];
        int p = 0;
        for (int fr = 0; fr < frames; fr++)
        {
            result[fr] = new Matrix[bones];
            for (int b = 0; b < bones; b++, p += 16)
                result[fr][b] = new Matrix(f[p], f[p + 1], f[p + 2], f[p + 3], f[p + 4], f[p + 5], f[p + 6], f[p + 7], f[p + 8], f[p + 9], f[p + 10], f[p + 11], f[p + 12], f[p + 13], f[p + 14], f[p + 15]);
        }
        return result;
    }

    public int ClipByRole(string role)
    {
        for (int i = 0; i < Clips.Count; i++) if (Clips[i].Role == role) return i;
        return Clips.Count > 0 ? 0 : -1;
    }

    /// <summary>The game advances animations at 15 ticks/s and loops back to the clip's loop-restart frame.</summary>
    public int FrameAt(int clip, double seconds)
    {
        if (clip < 0) return 0;
        var k = Clips[clip];
        int tick = (int)(seconds * k.Rate);
        if (tick < k.Frames) return tick;
        return k.LoopFrame + (tick - k.Frames) % Math.Max(1, k.Frames - k.LoopFrame);
    }

    /// <summary>Skins one frame into drawable batches (parts in groups 0x10, 0x30, 0x38 - flag banner, status overlay, shield shell - are not drawn). Also returns the lowest point.</summary>
    public List<Batch> Pose(GraphicsDevice gd, string dataRoot, int clip, int frame, Matrix toWorld, int skin, out float minY)
    {
        Matrix[] bones = clip >= 0 && clip < Clips.Count ? Clips[clip].Bones[Math.Clamp(frame, 0, Clips[clip].Frames - 1)] : _rest;
        var world = new Vector3[_pos.Length];
        minY = float.MaxValue;
        for (int i = 0; i < world.Length; i++)
        {
            int b = _bone[i];
            world[i] = Vector3.Transform(Vector3.Transform(_pos[i], b >= 0 && b < bones.Length ? bones[b] : Matrix.Identity), toWorld);
            minY = MathF.Min(minY, world[i].Y);
        }
        bool flip = toWorld.Determinant() < 0;
        var list = new List<Batch>();
        foreach (var mat in _materials)
        {
            if (mat.PartGroup is 0x10 or 0x30 or 0x38) continue;
            var verts = new VertexPositionColorTexture[mat.Triangles.Length / 3 * 3];
            for (int i = 0, o = 0; i + 2 < mat.Triangles.Length; i += 3)
            {
                int a = mat.Triangles[i], b = mat.Triangles[i + 1], c = mat.Triangles[i + 2];
                if (flip) (b, c) = (c, b);
                verts[o++] = new VertexPositionColorTexture(world[a], Color.White, Uv(a, mat));
                verts[o++] = new VertexPositionColorTexture(world[b], Color.White, Uv(b, mat));
                verts[o++] = new VertexPositionColorTexture(world[c], Color.White, Uv(c, mat));
            }
            bool translucent = (mat.Flags & 0x100) != 0;
            list.Add(new Batch
            {
                Vertices = verts, Texture = TextureFor(gd, dataRoot, mat.Texture, skin), Sampler = (mat.Flags & 0x20) != 0 ? 2 : (mat.Flags & 0x800) != 0 ? 1 : 0,
                Translucent = translucent, Alpha = translucent ? mat.Alpha : 1f, SortKey = mat.Field8 * 2 | (mat.Flags & 0x40), Tint = (mat.Flags & 0x400) != 0 ? mat.Colour1 : Vector3.One,
            });
        }
        return list;
    }

    Vector2 Uv(int vertex, Material mat)
    {
        var uv = _uv[vertex];
        if (_flipV != null && _flipV.Contains(mat.Texture)) uv.Y = 1f - uv.Y;                 // per-texture T flips found by eye in the viewer
        return uv;
    }

    static Texture2D? TextureFor(GraphicsDevice gd, string dataRoot, int texture, int skin)
    {
        if (_skins == null)
        {
            _skins = new();
            string sp = Path.Combine(dataRoot, "creatures", "skins.json");
            if (File.Exists(sp))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sp));
                foreach (var t in doc.RootElement.GetProperty("textures").EnumerateArray())
                    _skins[t.GetProperty("texture").GetInt32()] = t.GetProperty("files").EnumerateArray().Select(x => x.GetString()!).ToArray();
            }
            _flipV = new();
            string fp = Path.Combine(dataRoot, "creature_texture_flips.txt");
            if (File.Exists(fp)) foreach (string line in File.ReadLines(fp)) if (int.TryParse(line.Trim(), out int t)) _flipV.Add(t);
        }
        string file = Path.Combine(dataRoot, "models", "textures", $"tex_{texture:D3}_f0.png");
        if (_skins.TryGetValue(texture, out var files) && skin >= 0 && skin < files.Length && File.Exists(Path.Combine(dataRoot, "creatures", files[skin])))
            file = Path.Combine(dataRoot, "creatures", files[skin]);
        if (!Textures.TryGetValue(file, out var tex))
        {
            if (File.Exists(file)) { using var s = File.OpenRead(file); tex = Texture2D.FromStream(gd, s); } else tex = null;
            Textures[file] = tex;
        }
        return tex;
    }
}

/// <summary>A bot standing at a deathmatch spawn point in its idle animation. It is solid for the player.</summary>
public sealed class Bot
{
    public const float Radius = 40f, Height = 150f;
    public Vector3 Feet;
    public float Facing;
    public int Skin;
    public Creature Mesh = null!;
    public int Clip;
    public Vector3 Light = Vector3.One;           // approximate ambient light from the level's baked vertex colours
    public Creature? Weapon;                      // hand-held weapon chunk (obj_174_m16..): shares the body's skeleton and clip
    public double TimeOffset;
    float _drop = float.NaN;
    readonly Dictionary<int, List<Batch>> _frames = new();

    static readonly Matrix ZUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);           // model Z-up -> world Y-up: (x, y, z) -> (x, z, -y)

    public List<Batch> Batches(GraphicsDevice gd, string dataRoot, double time)
    {
        int frame = Mesh.FrameAt(Clip, time + TimeOffset);
        if (_frames.TryGetValue(frame, out var cached)) return cached;
        Matrix toWorld = ZUp * Matrix.CreateScale(0.5f) * Matrix.CreateRotationY(Facing) * Matrix.CreateTranslation(Feet.X, 0, Feet.Z);
        if (float.IsNaN(_drop)) { Mesh.Pose(gd, dataRoot, Clip, 0, toWorld, Skin, out float m0); _drop = Feet.Y - m0; }          // lower the model until its lowest point touches the floor
        var placed = toWorld * Matrix.CreateTranslation(0, _drop, 0);
        var batches = Mesh.Pose(gd, dataRoot, Clip, frame, placed, Skin, out _);
        if (Weapon != null) batches.AddRange(Weapon.Pose(gd, dataRoot, Clip, frame, placed, 0, out _));
        foreach (var b in batches) b.Tint *= Light;
        return _frames[frame] = batches;
    }
}
