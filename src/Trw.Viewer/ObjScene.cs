using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Viewer;

/// <summary>Where a triangle in a batch came from (only filled when a level is assembled from per-object meshes).</summary>
public readonly record struct TriInfo(int Placement, int Texture, bool ReversedFlag, int LocalTriangle, int MaterialFlags = 0, int Frame = 0);

/// <summary>One placed piece as read from objects.json.</summary>
public sealed class PlacementInfo
{
    public int Index, Sector, Record, ObjectId, ModelIndex, Def, TextureKey;
    public string Form = "", Flags = "";
    public Vector3 Position, Scale;
    public Quaternion Rotation;
    public float? YawDegrees;
    public bool Matrix => Form == "matrix34";
    public string Key => $"obj_{ObjectId}_{ModelIndex}";
}

/// <summary>Manual per-piece corrections made in the viewer (saved to placement_overrides.txt).</summary>
public sealed class PlacementOverride
{
    public bool Hidden, FlipWinding, MirrorX;
    public int YawSteps;                       // extra 90 degree turns about the piece's up axis
    public bool IsDefault => !Hidden && !FlipWinding && !MirrorX && (YawSteps & 3) == 0;
}

/// <summary>One drawable batch: all triangles that share a material (and so a texture).</summary>
public sealed class Batch
{
    public string Name = "";
    public int Sampler;
    public Texture2D[]? Frames;                // time-animated texture (material draw key -1): frame = ((tick / StepTicks) & 255) % Frames.Length
    public int StepTicks = 1;
    public bool Translucent;                   // material flag 0x100: alpha blend, depth test on, depth write off, drawn after the opaque parts
    public float Alpha = 1f;                   // colour1 alpha for translucent materials
    public Vector3 Tint = Vector3.One;         // flag 0x400: lerp(colour2, colour1, texture) approximated by colour1 * texture
    public int SortKey;                        // translucent draw order (field8 * 2 | flags & 0x40)                        // 0 wrap, 1 mirror (material flag 0x800), 2 clamp (flag 0x20)
    public required VertexPositionColorTexture[] Vertices;
    public TriInfo[] Tris = [];                // one entry per triangle, parallel to Vertices (3 vertices each)
    public Texture2D? Texture;
}

/// <summary>A level mesh loaded from the converter's OBJ + MTL output (own parser, no content pipeline).</summary>
public sealed class ObjScene
{
    public List<Batch> Batches { get; } = new();
    public Vector3 Min, Max;
    public List<PlacementInfo> Placements { get; } = new();

    public static ObjScene Load(GraphicsDevice gd, string objPath, Dictionary<string, Texture2D?> textureCache)
    {
        string dir = Path.GetDirectoryName(objPath)!;
        var materials = new Dictionary<string, string>();               // material -> texture file
        var pos = new List<Vector3>(); var col = new List<Color>(); var uv = new List<Vector2>();
        var perMaterial = new Dictionary<string, List<VertexPositionColorTexture>>();
        List<VertexPositionColorTexture> current = new();
        perMaterial[""] = current;
        var scene = new ObjScene { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };

        foreach (string raw in File.ReadLines(objPath))
        {
            if (raw.Length < 2 || raw[0] == '#') continue;
            string[] t = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "mtllib":
                    ReadMtl(Path.Combine(dir, t[1]), materials);
                    break;
                case "v":
                    var p = new Vector3(F(t[1]), F(t[2]), F(t[3]));
                    pos.Add(p);
                    col.Add(t.Length >= 7 ? new Color(F(t[4]), F(t[5]), F(t[6])) : Color.White);
                    scene.Min = Vector3.Min(scene.Min, p); scene.Max = Vector3.Max(scene.Max, p);
                    break;
                case "vt":
                    uv.Add(new Vector2(F(t[1]), 1f - F(t[2])));
                    break;
                case "usemtl":
                    if (!perMaterial.TryGetValue(t[1], out current!)) perMaterial[t[1]] = current = new();
                    break;
                case "f":
                    for (int i = 2; i < t.Length - 1; i++)                       // triangle fan
                    {
                        foreach (string c in new[] { t[1], t[i], t[i + 1] })
                        {
                            string[] ix = c.Split('/');
                            int vi = int.Parse(ix[0]) - 1;
                            Vector2 tc = ix.Length > 1 && ix[1].Length > 0 ? uv[int.Parse(ix[1]) - 1] : Vector2.Zero;
                            current.Add(new VertexPositionColorTexture(pos[vi], col[vi], tc));
                        }
                    }
                    break;
            }
        }

        foreach (var (name, verts) in perMaterial)
        {
            if (verts.Count == 0) continue;
            Texture2D? tex = null;
            if (materials.TryGetValue(name, out string? file) && file != null)
            {
                if (!textureCache.TryGetValue(file, out tex))
                {
                    tex = File.Exists(file) ? LoadTexture(gd, file) : null;
                    textureCache[file] = tex;
                }
            }
            scene.Batches.Add(new Batch { Name = name + (materials.TryGetValue(name, out var tf) ? " " + Path.GetFileNameWithoutExtension(tf) : ""), Vertices = verts.ToArray(), Texture = tex });
        }
        return scene;
    }

    static Texture2D LoadTexture(GraphicsDevice gd, string path)
    {
        using var s = File.OpenRead(path);
        return Texture2D.FromStream(gd, s);
    }

    static void ReadMtl(string path, Dictionary<string, string> materials)
    {
        if (!File.Exists(path)) return;
        string dir = Path.GetDirectoryName(path)!, name = "";
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("newmtl ")) name = line[7..].Trim();
            else if (line.StartsWith("map_Kd ")) materials[name] = Path.GetFullPath(Path.Combine(dir, line[7..].Trim()));
        }
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
