using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>A lift / moving platform (docs/formats/dynamic_objects.md): oscillates between station A (its placement) and B. Wait at A, travel, hold at B, travel back.
/// The "rider" trigger starts the trip only after something has stood on it for the hold time; "always" lifts (the ferry on level 25) run on their own.</summary>
public sealed class Lift
{
    public Vector3 Base, Offset, Position, Delta;                 // world frame (x already mirrored)
    public float Facing, TravelTicks = 15, HoldA = 7.5f, HoldB = 15;
    public bool Cosine = true, NeedsRider = true;
    public float HalfX = 102.4f, HalfZ = 102.4f, TopRel = 25.6f, BottomRel = -25.6f;     // collision box relative to the position (hull AABB * scale)
    public Vector3[] ModelPositions = [];                         // model space, Z-up, three per triangle
    public Vector2[] ModelUvs = [];
    public Texture2D? Texture;
    int _phase; float _timer;

    static readonly Matrix ZUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);

    public float Top => Position.Y + TopRel;
    public float Bottom => Position.Y + BottomRel;

    /// <summary>Advances by <paramref name="dtTicks"/> game ticks (1/15 s); <paramref name="rider"/> = something stands on the lift.</summary>
    public void Update(float dtTicks, bool rider)
    {
        float p;
        switch (_phase)
        {
            case 0: _timer = (!NeedsRider || rider) ? _timer + dtTicks : 0; if (_timer >= HoldA) { _phase = 1; _timer = 0; } p = 0; break;
            case 1: _timer += dtTicks; if (_timer >= TravelTicks) { _phase = 2; _timer = 0; p = 1; } else p = _timer / TravelTicks; break;
            case 2: _timer += dtTicks; if (_timer >= HoldB) { _phase = 3; _timer = 0; } p = 1; break;
            default: _timer += dtTicks; if (_timer >= TravelTicks) { _phase = 0; _timer = 0; p = 0; } else p = 1 - _timer / TravelTicks; break;
        }
        float e = Cosine ? (1 - MathF.Cos(MathF.PI * p)) / 2 : p;
        var next = Base + Offset * e;
        Delta = next - Position; Position = next;
    }

    public bool Covers(float x, float z, float margin = 0) => MathF.Abs(x - Position.X) <= HalfX + margin && MathF.Abs(z - Position.Z) <= HalfZ + margin;

    public Batch Mesh()
    {
        var m = ZUp * Matrix.CreateScale(0.5f) * Matrix.CreateRotationY(Facing) * Matrix.CreateTranslation(Position);
        var v = new VertexPositionColorTexture[ModelPositions.Length];
        for (int i = 0; i < v.Length; i++) v[i] = new VertexPositionColorTexture(Vector3.Transform(ModelPositions[i], m), Color.White, ModelUvs[i]);
        return new Batch { Vertices = v, Texture = Texture };
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Reads the lifts of a baked level (dynamics.json); the mesh comes from extracted/dynamics/_objects.</summary>
    public static List<Lift> Load(GraphicsDevice gd, string dataRoot, string levelDir)
    {
        var lifts = new List<Lift>();
        string path = Path.Combine(dataRoot, "baked", levelDir, "dynamics.json");
        if (!File.Exists(path)) return lifts;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var o in doc.RootElement.GetProperty("dynamics").EnumerateArray())
        {
            if (o.GetProperty("kind").GetString() != "lift" || !o.TryGetProperty("motion", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            float[] pos = o.GetProperty("position").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            float[] off = m.GetProperty("offset").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var lift = new Lift
            {
                Base = new Vector3(-pos[0], pos[1], pos[2]), Offset = new Vector3(-off[0], off[1], off[2]),                  // dynamics.json is in raw coordinates: mirror X
                Facing = o.GetProperty("facingYaw").GetSingle(), TravelTicks = m.GetProperty("travelTicks").GetSingle(),
                HoldA = m.GetProperty("holdAtFromTicks").GetSingle(), HoldB = m.GetProperty("holdAtToTicks").GetSingle(),
                Cosine = (m.GetProperty("easing").GetString() ?? "").StartsWith("cosine"), NeedsRider = m.GetProperty("trigger").GetString() == "rider",
            };
            lift.Position = lift.Base;
            float scale = o.GetProperty("scale")[0].GetSingle(); if (scale <= 0) scale = 0.5f;
            var model = o.GetProperty("model");
            if (model.TryGetProperty("hullAabb", out var hull))
            {
                float[] mn = hull.GetProperty("min").EnumerateArray().Select(x => x.GetSingle()).ToArray(), mx = hull.GetProperty("max").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                lift.HalfX = MathF.Max(-mn[0], mx[0]) * scale; lift.HalfZ = MathF.Max(-mn[1], mx[1]) * scale;              // model X/Y become world X/Z
                if (MathF.Abs(MathF.Sin(lift.Facing)) > 0.5f) (lift.HalfX, lift.HalfZ) = (lift.HalfZ, lift.HalfX);
                lift.TopRel = mx[2] * scale; lift.BottomRel = mn[2] * scale;
            }
            var first = model.GetProperty("models")[0];
            string obj = Path.Combine(dataRoot, "dynamics", first.GetProperty("file").GetString()!);
            int texId = first.GetProperty("parts")[0].GetProperty("textureId").GetInt32();
            string tex = Path.Combine(dataRoot, "models", "textures", $"tex_{texId:D3}_f0.png");
            if (File.Exists(tex)) { using var s = File.OpenRead(tex); lift.Texture = Texture2D.FromStream(gd, s); }
            if (File.Exists(obj))
            {
                var pv = new List<Vector3>(); var uv = new List<Vector2>(); var mp = new List<Vector3>(); var mu = new List<Vector2>();
                foreach (string line in File.ReadLines(obj))
                {
                    string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0) continue;
                    if (t[0] == "v") pv.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    else if (t[0] == "vt") uv.Add(new Vector2(F(t[1]), 1f - F(t[2])));
                    else if (t[0] == "f")
                        for (int i = 1; i < 4; i++) { var ix = t[i].Split('/'); int vi = int.Parse(ix[0]) - 1, ti = ix.Length > 1 && ix[1].Length > 0 ? int.Parse(ix[1]) - 1 : vi; mp.Add(pv[vi]); mu.Add(uv[ti]); }
                }
                lift.ModelPositions = mp.ToArray(); lift.ModelUvs = mu.ToArray();
            }
            lifts.Add(lift);
        }
        return lifts;
    }
}
