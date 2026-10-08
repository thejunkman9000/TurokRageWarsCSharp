using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>A teleport zone (docs/formats/dynamic_objects.md section 3): touching it (3D distance below the touch radius) moves the player to the warp destination.
/// It is drawn as the pillar of light (object 330).</summary>
public sealed class Teleporter
{
    public Vector3 Position, Destination;        // world frame (x mirrored)
    public float Radius = 143.36f, DestinationFacing;
    public List<Batch> Batches = new();

    static readonly Matrix ZUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    public bool Touches(Vector3 feet) => Vector3.Distance(feet, Position) < Radius;

    public static List<Teleporter> Load(GraphicsDevice gd, string dataRoot, string levelDir)
    {
        var list = new List<Teleporter>();
        string path = Path.Combine(dataRoot, "baked", levelDir, "dynamics.json");
        if (!File.Exists(path)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var o in doc.RootElement.GetProperty("dynamics").EnumerateArray())
        {
            if (o.GetProperty("kind").GetString() != "teleporter" || !o.TryGetProperty("teleport", out var tp)) continue;
            var dests = tp.GetProperty("destinations");
            if (dests.GetArrayLength() == 0) continue;
            string flags = tp.TryGetProperty("definitionFlags", out var df) ? df.GetString() ?? "0" : "0";
            if ((Convert.ToInt32(flags.Replace("0x", ""), 16) & 4) != 0) continue;                  // definition flag 4 = disabled
            float[] p = o.GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            float[] d = dests[0].GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var t = new Teleporter
            {
                Position = new Vector3(-p[0], p[1], p[2]), Destination = new Vector3(-d[0], d[1], d[2]), Radius = tp.GetProperty("touchRadius").GetSingle(),
                DestinationFacing = MathF.PI - dests[0].GetProperty("yawRadians").GetSingle(),     // raw yaw -> the baked facing convention (-yaw + pi)
            };
            float scale = o.GetProperty("scale")[0].GetSingle(); if (scale <= 0) scale = 0.5f;
            var first = o.GetProperty("model").GetProperty("models")[0];
            string obj = Path.Combine(dataRoot, "dynamics", first.GetProperty("file").GetString()!);
            if (File.Exists(obj)) t.Batches = LoadParts(gd, dataRoot, obj, ZUp * Matrix.CreateScale(scale) * Matrix.CreateRotationY(o.GetProperty("facingYaw").GetSingle()) * Matrix.CreateTranslation(t.Position));
            list.Add(t);
        }
        return list;
    }

    /// <summary>Reads a model-space OBJ whose usemtl names look like tex_80_f8 / tex_372_f50A (texture id, flags in hex).</summary>
    static List<Batch> LoadParts(GraphicsDevice gd, string dataRoot, string obj, Matrix toWorld)
    {
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var parts = new List<(string name, List<VertexPositionColorTexture> v)>();
        foreach (string line in File.ReadLines(obj))
        {
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t[0] == "v") pos.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
            else if (t[0] == "vt") uv.Add(new Vector2(F(t[1]), 1f - F(t[2])));
            else if (t[0] == "usemtl") parts.Add((t[1], new()));
            else if (t[0] == "f" && parts.Count > 0)
                for (int i = 1; i < 4; i++) { var ix = t[i].Split('/'); int vi = int.Parse(ix[0]) - 1, ti = ix.Length > 1 && ix[1].Length > 0 ? int.Parse(ix[1]) - 1 : vi; parts[^1].v.Add(new VertexPositionColorTexture(Vector3.Transform(pos[vi], toWorld), Color.White, uv[ti])); }
        }
        var batches = new List<Batch>();
        foreach (var (name, v) in parts)
        {
            string[] n = name.Split('_');                                                   // tex, id, fFLAGS
            int tex = int.Parse(n[1], CultureInfo.InvariantCulture), flags = Convert.ToInt32(n[2][1..], 16);
            string file = Path.Combine(dataRoot, "models", "textures", $"tex_{tex:D3}_f0.png");
            Texture2D? texture = null;
            if (File.Exists(file)) { using var s = File.OpenRead(file); texture = Texture2D.FromStream(gd, s); }
            bool translucent = (flags & 0x100) != 0;
            batches.Add(new Batch { Name = name, Vertices = v.ToArray(), Texture = texture, Translucent = translucent, Alpha = translucent ? 0.55f : 1f, Sampler = (flags & 0x20) != 0 ? 2 : 0, SortKey = 1000 });
        }
        return batches;
    }
}
