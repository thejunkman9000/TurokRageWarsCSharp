using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Viewer;

/// <summary>A skinned, animated creature loaded from extracted/creatures/obj_N.json (see docs/formats/creatures_export.md).
/// Every vertex is rigidly bound to one bone; each clip stores world matrices per frame per bone (row-major, row-vector convention).</summary>
public sealed class CreatureScene
{
    public sealed class Material
    {
        public int Texture, Flags, PartGroup, Field8; public int[] Triangles = [];
        public float Alpha = 1f;                       // colour1 alpha (used by translucent materials, flag 0x100)
        public Vector3 Colour1 = Vector3.One;          // colour1 rgb (used with flag 0x400)
        /// <summary>Parts in groups 0x10, 0x30 and 0x38 are not drawn by default in the game (flag banner, status overlay, protection shield shell).</summary>
        public bool HiddenByDefault => PartGroup is 0x10 or 0x30 or 0x38;
    }
    public sealed class Clip { public int Index, Frames, Rate = 15, LoopFrame; public Matrix[][] BoneWorld = []; public string Role = "unknown"; }

    /// <summary>UV experiments for finding the real orientation rule (creature mode keys 1-5): flip V or U on every material or only on the
    /// mirror-addressed ones (flag 0x800, which the faces use), or swap U and V.</summary>
    public static bool FlipUAll, FlipUMirror, SwapUV;
    /// <summary>Experiment only (key 1). The baseline is v = t, the same as static pieces: an earlier version flipped creature V once too often, which made nearly every texture look upside-down.</summary>
    public static bool FlipVAll;
    /// <summary>Per-texture V flips for experiments (flag uv=vtex<N>).</summary>
    public static readonly HashSet<int> FlipVTextures = new();
    /// <summary>Default ON: materials with the mirror flag (0x800, used by the faces) have their T axis inverted relative to the others (verified by eye on the portraits).</summary>
    public static bool FlipVMirror;
    public static string UvDescription() => (FlipVAll ? "flipV(all) " : "") + (FlipVMirror ? "flipV(mirror-flag) " : "") + (FlipUAll ? "flipU(all) " : "") + (FlipUMirror ? "flipU(mirror-flag) " : "") + (SwapUV ? "swapUV " : "");

    /// <summary>Folder the package was loaded from (skins live in its skins/ subfolder).</summary>
    public static string CreaturesDir = "";
    public static bool ShowHiddenParts;
    static Dictionary<int, string[]>? _skins;

    /// <summary>Per-skin texture files (skins.json): texture id -> file names for skin 0, 1, 2 (relative to the creatures folder).</summary>
    static Dictionary<int, string[]> SkinFiles()
    {
        if (_skins != null) return _skins;
        var d = new Dictionary<int, string[]>();
        string path = Path.Combine(CreaturesDir, "skins.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var t in doc.RootElement.GetProperty("textures").EnumerateArray())
                d[t.GetProperty("texture").GetInt32()] = t.GetProperty("files").EnumerateArray().Select(x => x.GetString()!).ToArray();
        }
        return _skins = d;
    }

    public int ObjectId, BoneCount;
    public Vector3[] Positions = [];
    public Vector2[] Uvs = [];
    public int[] VertexBone = [];
    public List<Material> Materials = new();
    public List<Clip> Clips = new();
    public Matrix[] RestWorld = [];

    public static IEnumerable<string> Files(string dataRoot)
    {
        string dir = Path.Combine(dataRoot, "creatures");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "obj_*.json").OrderBy(f => SortKey(f)).ThenBy(f => f) : [];
    }

    /// <summary>obj_174.json sorts as (174, -1); obj_174_m5.json as (174, 5).</summary>
    static (int, int) SortKey(string f)
    {
        var parts = Path.GetFileNameWithoutExtension(f)[4..].Split('_');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 && parts[1].StartsWith('m') ? int.Parse(parts[1][1..], CultureInfo.InvariantCulture) : -1);
    }

    public static CreatureScene Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var c = new CreatureScene { ObjectId = r.GetProperty("object").GetInt32(), BoneCount = r.GetProperty("boneCount").GetInt32() };
        float[] pos = r.GetProperty("positions").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        float[] uv = r.GetProperty("uvs").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        c.Positions = Enumerable.Range(0, pos.Length / 3).Select(i => new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2])).ToArray();
        c.Uvs = Enumerable.Range(0, uv.Length / 2).Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray();      // v = t, exactly the static pieces' convention (their OBJ stores 1 - t and the loader flips it back; the creature JSON stores plain t)
        c.VertexBone = r.GetProperty("vertexBone").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        CreaturesDir = Path.GetDirectoryName(path)!;
        foreach (var m in r.GetProperty("materials").EnumerateArray())
        {
            var mat = new Material { Texture = I(m.GetProperty("texture")), Flags = I(m.GetProperty("flags")), Triangles = m.GetProperty("triangles").EnumerateArray().Select(x => I(x)).ToArray() };
            if (m.TryGetProperty("partGroup", out var pg) && pg.ValueKind == JsonValueKind.Number) mat.PartGroup = I(pg);
            if (m.TryGetProperty("field8", out var f8) && f8.ValueKind == JsonValueKind.Number) mat.Field8 = I(f8);
            if (m.TryGetProperty("color1", out var c1) && c1.ValueKind == JsonValueKind.String && c1.GetString() is { Length: 8 } hex)
            {
                byte B(int i) => Convert.ToByte(hex.Substring(i * 2, 2), 16);
                mat.Colour1 = new Vector3(B(0), B(1), B(2)) / 255f; mat.Alpha = B(3) / 255f;
            }
            c.Materials.Add(mat);
        }
        if (r.TryGetProperty("restWorld", out var rw)) c.RestWorld = Matrices(rw.GetString()!, 1, c.BoneCount)[0];
        foreach (var k in r.GetProperty("clips").EnumerateArray())
        {
            int frames = k.GetProperty("frames").GetInt32();
            c.Clips.Add(new Clip
            {
                Index = k.GetProperty("index").GetInt32(), Frames = frames,
                Rate = k.TryGetProperty("rate", out var rt) ? rt.GetInt32() : 15,
                LoopFrame = k.TryGetProperty("loopFrame", out var lf) ? lf.GetInt32() : 0,
                Role = k.TryGetProperty("role", out var ro) ? ro.GetString() ?? "unknown" : "unknown",
                BoneWorld = Matrices(k.GetProperty("boneWorld").GetString()!, frames, c.BoneCount),
            });
        }
        // mesh variants (obj_174_m5.json ...) share the base object's skeleton and animations
        if (c.Clips.Count == 0)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int us = name.IndexOf("_m", StringComparison.Ordinal);
            string basePath = us > 0 ? Path.Combine(Path.GetDirectoryName(path)!, name[..us] + ".json") : "";
            if (basePath.Length > 0 && File.Exists(basePath) && basePath != path)
            {
                var b = Load(basePath);
                if (b.BoneCount == c.BoneCount) { c.Clips = b.Clips; if (c.RestWorld.Length == 0) c.RestWorld = b.RestWorld; }
            }
        }
        return c;
    }

    /// <summary>Some exported integers (material flag words) exceed Int32; read them unchecked.</summary>
    static int I(JsonElement e) => e.TryGetInt32(out int v) ? v : unchecked((int)e.GetInt64());

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

    /// <summary>Skin one frame into drawable batches. <paramref name="toWorld"/> is applied after skinning (Z-up conversion etc.).</summary>
    public ObjScene Pose(GraphicsDevice gd, int clip, int frame, Matrix toWorld, Dictionary<string, Texture2D?> textureCache, string texDir, int look = 0, int skin = 0)
    {
        var scene = new ObjScene { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        Matrix[] bones = clip >= 0 && clip < Clips.Count ? Clips[clip].BoneWorld[Math.Clamp(frame, 0, Clips[clip].Frames - 1)] : RestWorld;
        var world = new Vector3[Positions.Length];
        for (int i = 0; i < world.Length; i++)
        {
            int b = VertexBone[i];
            var m = b >= 0 && b < bones.Length ? bones[b] : Matrix.Identity;
            world[i] = Vector3.Transform(Vector3.Transform(Positions[i], m), toWorld);
            scene.Min = Vector3.Min(scene.Min, world[i]); scene.Max = Vector3.Max(scene.Max, world[i]);
        }
        bool flip = toWorld.Determinant() < 0;
        foreach (var mat in Materials)
        {
            if (mat.HiddenByDefault && !ShowHiddenParts) continue;
            var verts = new List<VertexPositionColorTexture>();
            for (int i = 0; i + 2 < mat.Triangles.Length; i += 3)
            {
                int a = mat.Triangles[i], b = mat.Triangles[i + 1], c = mat.Triangles[i + 2];
                if (flip) (b, c) = (c, b);
                verts.Add(new VertexPositionColorTexture(world[a], Color.White, Variant(Uvs[a], mat.Flags, mat.Texture)));
                verts.Add(new VertexPositionColorTexture(world[b], Color.White, Variant(Uvs[b], mat.Flags, mat.Texture)));
                verts.Add(new VertexPositionColorTexture(world[c], Color.White, Variant(Uvs[c], mat.Flags, mat.Texture)));
            }
            string file = Path.Combine(texDir, $"tex_{mat.Texture:D3}_f{look}.png");
            if (!File.Exists(file)) file = Path.Combine(texDir, $"tex_{mat.Texture:D3}_f0.png");
            if (SkinFiles().TryGetValue(mat.Texture, out var skinFiles) && skin >= 0 && skin < skinFiles.Length && File.Exists(Path.Combine(CreaturesDir, skinFiles[skin])))
                file = Path.Combine(CreaturesDir, skinFiles[skin]);                  // the character's chosen skin (0..2)
            if (!textureCache.TryGetValue(file, out var tex))
            {
                if (File.Exists(file)) { using var s = File.OpenRead(file); tex = Texture2D.FromStream(gd, s); } else tex = null;
                textureCache[file] = tex;
            }
            int sampler = (mat.Flags & 0x20) != 0 ? 2 : (mat.Flags & 0x800) != 0 ? 1 : 0;
            bool translucent = (mat.Flags & 0x100) != 0;
            scene.Batches.Add(new Batch
            {
                Name = $"tex_{mat.Texture:D3} flags 0x{mat.Flags:X}", Sampler = sampler, Vertices = verts.ToArray(), Texture = tex,
                Translucent = translucent, Alpha = translucent ? mat.Alpha : 1f, SortKey = mat.Field8 * 2 | (mat.Flags & 0x40),
                Tint = (mat.Flags & 0x400) != 0 ? mat.Colour1 : Vector3.One,
            });
        }
        return scene;
    }

    static Vector2 Variant(Vector2 uv, int flags, int texture)
    {
        bool mirror = (flags & 0x800) != 0;
        if (FlipVTextures.Contains(texture)) uv.Y = 1f - uv.Y;
        if (FlipVAll || (FlipVMirror && mirror)) uv.Y = 1f - uv.Y;
        if (FlipUAll || (FlipUMirror && mirror)) uv.X = 1f - uv.X;
        if (SwapUV) uv = new Vector2(uv.Y, uv.X);
        return uv;
    }

    /// <summary>The game advances animations at 15 ticks/s and loops back to the clip's loop-restart frame.</summary>
    public static int FrameAt(Clip clip, double seconds)
    {
        int tick = (int)(seconds * clip.Rate);
        if (tick < clip.Frames) return tick;
        int loopLen = Math.Max(1, clip.Frames - clip.LoopFrame);
        return clip.LoopFrame + (tick - clip.Frames) % loopLen;
    }
}
