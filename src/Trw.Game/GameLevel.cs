using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>One drawable batch: all triangles of one material (texture look + flags).</summary>
public sealed class Batch
{
    public string Name = "";
    public VertexPositionColorTexture[] Vertices = [];
    public Texture2D? Texture;
    public Texture2D[]? Frames;               // time-animated texture: frame = ((tick / StepTicks) & 255) % Frames.Length
    public int StepTicks = 1;
    public int Sampler;                       // 0 wrap, 1 mirror, 2 clamp
    public bool Translucent;
    public float Alpha = 1f;
    public Vector3 Tint = Vector3.One;
    public int SortKey;
}

public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Vector3 Min, Vector3 Max);

/// <summary>A level loaded from extracted/baked/NN_name (docs/formats/baked_levels.md): meshes with the game's vertex lighting, spawn, fog.</summary>
public sealed class GameLevel
{
    public string Name = "";
    public List<Batch> Batches = new();
    public Triangle[] Triangles = [];
    List<Triangle> _lavaRaw = new();
    public Triangle[] Water = [], Lava = [];     // up-facing surface triangles of the water (flags 0x548) and lava (0x148) materials; not solid for water
    public Vector3 PlayerStart;
    public float PlayerFacing;                // radians about +Y; forward = (sin, 0, cos)
    public List<Lift> Lifts = new();
    public List<Teleporter> Teleporters = new();
    public List<Bot> Bots = new();
    public Color FogColour = Color.Black;
    public float FogFar = 2816f;

    public static GameLevel Load(GraphicsDevice gd, string dataRoot, string levelDirName)
    {
        string dir = Path.Combine(dataRoot, "baked", levelDirName);
        var level = new GameLevel { Name = levelDirName };
        var materials = LoadMaterials(Path.Combine(dir, "materials.json"));
        var mtl = LoadMtl(Path.Combine(dir, "world.mtl"));
        var cache = new Dictionary<string, Texture2D?>();

        var pos = new List<Vector3>(); var col = new List<Color>(); var uv = new List<Vector2>();
        var perMaterial = new Dictionary<string, List<VertexPositionColorTexture>>();
        List<VertexPositionColorTexture>? current = null;
        foreach (string line in File.ReadLines(Path.Combine(dir, "world.obj")))
        {
            if (line.Length < 2 || line[0] == '#') continue;
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v":
                    pos.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    col.Add(t.Length >= 7 ? new Color(F(t[4]), F(t[5]), F(t[6])) : Color.White);      // the game's vertex lighting
                    break;
                case "vt": uv.Add(new Vector2(F(t[1]), 1f - F(t[2]))); break;                         // OBJ v = 1 - t; the game uses t
                case "usemtl":
                    if (!perMaterial.TryGetValue(t[1], out current!)) perMaterial[t[1]] = current = new();
                    break;
                case "f":
                    for (int i = 1; i < 4; i++)
                    {
                        string[] ix = t[i].Split('/');
                        int vi = int.Parse(ix[0]) - 1, ti = ix.Length > 1 && ix[1].Length > 0 ? int.Parse(ix[1]) - 1 : vi;
                        current!.Add(new VertexPositionColorTexture(pos[vi], col[vi], uv[ti]));
                    }
                    break;
            }
        }

        string texDir = Path.Combine(dataRoot, "models", "textures");
        foreach (var (name, verts) in perMaterial.OrderBy(k => k.Key))
        {
            materials.TryGetValue(name, out var mi);
            mtl.TryGetValue(name, out var mt);
            Texture2D? Load(string file)
            {
                if (!cache.TryGetValue(file, out var tex))
                {
                    tex = File.Exists(file) ? Texture2D.FromStream(gd, File.OpenRead(file)) : null;
                    cache[file] = tex;
                }
                return tex;
            }
            string baseFile = mt.File != null ? Path.GetFullPath(Path.Combine(dir, mt.File)) : "";
            var batch = new Batch
            {
                Name = name, Vertices = verts.ToArray(), Texture = baseFile.Length > 0 ? Load(baseFile) : null,
                Sampler = mi?.Sampler ?? 0, Translucent = mi?.Translucent ?? false, Alpha = mi?.Alpha ?? 1f, SortKey = mi?.SortKey ?? 0,
                StepTicks = Math.Max(1, mi?.StepTicks ?? 1), Tint = mt.Tint,
            };
            if (mi is { Animated: true, Frames: > 1 })
            {
                var frames = new List<Texture2D>();
                for (int k = 0; k < mi.Frames; k++) if (Load(Path.Combine(texDir, $"tex_{mi.Texture:D3}_f{k}.png")) is { } f) frames.Add(f);
                if (frames.Count > 1) { batch.Frames = frames.ToArray(); batch.Texture = frames[0]; }
            }
            level.Batches.Add(batch);
        }

        // collision: the visible geometry itself (the game uses a coarse box/region scheme that is not fully decoded)
        var tris = new List<Triangle>();
        // opaque parts are solid; translucent lava (flags 0x148, no 0x400) is solid ground too (it hurts in the game); translucent water (0x548) is not (you swim in it)
        static bool Solid(Batch b) => !b.Translucent || ((int.TryParse(b.Name[(b.Name.LastIndexOf("_f") + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int fl) ? fl : 0) & 0x400) == 0;
        foreach (var b in level.Batches.Where(Solid))
            for (int i = 0; i + 2 < b.Vertices.Length; i += 3)
            {
                Vector3 a = b.Vertices[i].Position, bb = b.Vertices[i + 1].Position, c = b.Vertices[i + 2].Position;
                var n = Vector3.Cross(bb - a, c - a);
                if (n.LengthSquared() < 1e-9f) continue;
                n.Normalize();
                tris.Add(new Triangle(a, bb, c, n, Vector3.Min(a, Vector3.Min(bb, c)), Vector3.Max(a, Vector3.Max(bb, c))));
            }
        level.Triangles = tris.ToArray();
        var water = new List<Triangle>(); var lava = new List<Triangle>();
        foreach (var b in level.Batches.Where(b => b.Translucent))
        {
            int fl = int.TryParse(b.Name[(b.Name.LastIndexOf("_f") + 2)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x) ? x : 0;
            var target = (fl & 0x400) != 0 ? water : lava;
            for (int i = 0; i + 2 < b.Vertices.Length; i += 3)
            {
                Vector3 a = b.Vertices[i].Position, bb = b.Vertices[i + 1].Position, c = b.Vertices[i + 2].Position;
                var n = Vector3.Cross(bb - a, c - a);
                if (n.LengthSquared() < 1e-9f) continue;
                n.Normalize();
                if (n.Y > 0.5f) target.Add(new Triangle(a, bb, c, n, Vector3.Min(a, Vector3.Min(bb, c)), Vector3.Max(a, Vector3.Max(bb, c))));
            }
        }
        level.Water = water.ToArray(); level._lavaRaw = lava;

        // spawn
        string spawns = Path.Combine(dir, "spawns.json");
        if (File.Exists(spawns))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(spawns));
            foreach (var a in doc.RootElement.GetProperty("actors").EnumerateArray())
                if (a.GetProperty("kind").GetString() == "playerStart")
                {
                    level.PlayerStart = new Vector3(a.GetProperty("x").GetSingle(), a.GetProperty("y").GetSingle(), a.GetProperty("z").GetSingle());
                    level.PlayerFacing = a.GetProperty("facingYaw").GetSingle();
                    break;
                }
        }

        level.Lifts = Lift.Load(gd, dataRoot, levelDirName);
        level.Teleporters = Teleporter.Load(gd, dataRoot, levelDirName);
        foreach (var l in level.Lifts) l.Light = level.LightNear(l.Base + new Vector3(0, 40, 0), 300f);
        LoadBots(level, dataRoot, spawns);

        // fog / clear colour of zone 0
        string lighting = Path.Combine(dataRoot, "levelgeo", "lighting.json");
        if (File.Exists(lighting) && int.TryParse(levelDirName.Split('_')[0], out int id))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(lighting));
            foreach (var lv in doc.RootElement.GetProperty("levels").EnumerateArray())
            {
                if (lv.GetProperty("level").GetInt32() != id) continue;
                foreach (var z in lv.GetProperty("zones").EnumerateArray())
                {
                    var c = z.GetProperty("colorRgba").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    level.FogColour = new Color(c[0] & 0xF8, c[1] & 0xF8, c[2] & 0xF8);
                    level.FogFar = z.GetProperty("farPlane").GetSingle();
                    break;
                }
            }
        }
        level.Lava = LargeRegions(level._lavaRaw, 400f, level.PlayerStart);
        return level;
    }

    /// <summary>Keeps only the triangles belonging to connected regions wider than <paramref name="minExtent"/>: small lava-textured patches are decoration, and a region the player starts on (level 14's floor) is not lava.</summary>
    static Triangle[] LargeRegions(List<Triangle> tris, float minExtent, Vector3 start)
    {
        int n = tris.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var byCorner = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < n; i++)
            foreach (var p in new[] { tris[i].A, tris[i].B, tris[i].C })
            {
                var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                if (byCorner.TryGetValue(key, out int other)) parent[Find(i)] = Find(other); else byCorner[key] = i;
            }
        var min = new Dictionary<int, Vector3>(); var max = new Dictionary<int, Vector3>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            min[r] = min.TryGetValue(r, out var a) ? Vector3.Min(a, tris[i].Min) : tris[i].Min;
            max[r] = max.TryGetValue(r, out var b) ? Vector3.Max(b, tris[i].Max) : tris[i].Max;
        }
        return Enumerable.Range(0, n).Where(i => { int r = Find(i); var e = max[r] - min[r]; bool hasStart = start.X >= min[r].X && start.X <= max[r].X && start.Z >= min[r].Z && start.Z <= max[r].Z; return MathF.Max(e.X, e.Z) > minExtent && !hasStart; }).Select(i => tris[i]).ToArray();
    }

    /// <summary>Bots idling at the deathmatch spawn points, characters cycling through the body chunks (character id -> chunk, docs/formats/creatures_export.md), skins 0..2.</summary>
    static void LoadBots(GameLevel level, string dataRoot, string spawnsPath)
    {
        if (!File.Exists(spawnsPath)) return;
        int[] chunkOf = [1, 3, 4, 5, 6, 2, 7, 8, 9];
        using var doc = JsonDocument.Parse(File.ReadAllText(spawnsPath));
        if (!doc.RootElement.TryGetProperty("spawnPoints", out var points)) return;
        int n = 0;
        foreach (var sp in points.EnumerateArray())
        {
            var mesh = Creature.Load(dataRoot, $"obj_174_m{chunkOf[n % 9]}");
            if (mesh != null)
            {
                var p = new Vector3(sp.GetProperty("x").GetSingle(), sp.GetProperty("y").GetSingle(), sp.GetProperty("z").GetSingle());
                if (Collision.GroundHeight(level, p, 8f, p.Y + 100f) is float floor) p.Y = floor;
                level.Bots.Add(new Bot { Mesh = mesh, Feet = p, Facing = sp.GetProperty("facingYaw").GetSingle(), Skin = n % 3, Clip = mesh.ClipByRole("idle"), Weapon = Creature.Load(dataRoot, "obj_174_m21"), TimeOffset = n * 0.13, Light = level.LightNear(p + new Vector3(0, 90, 0)) });
            }
            n++;
        }
    }

    /// <summary>Approximate lighting for dynamic objects: the average baked vertex colour of the level geometry within 200 units (the game's real actor lighting is not decoded).</summary>
    public Vector3 LightNear(Vector3 p, float radius = 200f)
    {
        Vector3 sum = default; int n = 0; float r2 = radius * radius;
        foreach (var b in Batches)
            foreach (var v in b.Vertices)
                if (Vector3.DistanceSquared(v.Position, p) < r2) { sum += v.Color.ToVector3(); n++; }
        return n > 0 ? Vector3.Clamp(sum / n * 1.4f, new Vector3(0.25f), Vector3.One) : Vector3.One;
    }

    sealed record MatInfo(int Texture, int Sampler, bool Translucent, float Alpha, int SortKey, bool Animated, int Frames, int StepTicks);
    readonly record struct MtlInfo(string? File, Vector3 Tint);

    static Dictionary<string, MatInfo> LoadMaterials(string path)
    {
        var d = new Dictionary<string, MatInfo>();
        if (!File.Exists(path)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            string s = m.GetProperty("sampler").GetString() ?? "wrap";
            d[m.GetProperty("name").GetString()!] = new MatInfo(m.GetProperty("texture").GetInt32(), s == "clamp" ? 2 : s == "mirror" ? 1 : 0,
                m.GetProperty("translucent").GetBoolean(), m.GetProperty("alpha").GetSingle(), m.GetProperty("sortKey").GetInt32(),
                m.GetProperty("animated").GetBoolean(), m.GetProperty("frames").GetInt32(), m.GetProperty("stepTicks").GetInt32());
        }
        return d;
    }

    static Dictionary<string, MtlInfo> LoadMtl(string path)
    {
        var d = new Dictionary<string, MtlInfo>();
        if (!File.Exists(path)) return d;
        string? name = null, file = null; var tint = Vector3.One;
        void Flush() { if (name != null) d[name] = new MtlInfo(file, tint); }
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("newmtl ")) { Flush(); name = line[7..].Trim(); file = null; tint = Vector3.One; }
            else if (line.StartsWith("map_Kd ")) file = line[7..].Trim();
            else if (line.StartsWith("Kd ")) { var p = line.Split(' '); tint = new Vector3(F(p[1]), F(p[2]), F(p[3])); }
        }
        Flush();
        return d;
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
