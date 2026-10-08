using System.Globalization;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Viewer;

/// <summary>The animated things placed in a level: the player start (a body chunk plus a hand-held weapon), bots at the deathmatch spawn points,
/// and pickups/movers as props. Data comes from extracted/creatures/placements.json and the creature packages (docs/formats/creatures_export.md).</summary>
public sealed class ActorLayer
{
    public sealed class Actor
    {
        public string Kind = "";
        public CreatureScene Body = null!;
        public CreatureScene? Weapon;
        public Vector3 Position;              // raw level coordinates (X is mirrored when drawn)
        public float Yaw, Scale = 0.5f;
        public int Clip, Skin;
        public double TimeOffset;
        public LiftMotion? Lift;              // set for lifts decoded in extracted/dynamics (docs/formats/dynamic_objects.md)
        public int Index;                     // actor index in the level's part 3
        public float FloorY = float.NaN;      // floor height under the spawn point (set by the viewer from the level geometry)
        public float Drop = float.NaN;        // vertical shift that puts the model's lowest point on that floor (computed on first pose)
    }

    /// <summary>A lift oscillating between two stations: hold at A, travel, hold at B, travel back (ticks are 1/15 s). The game starts the trip only while a rider stands on it;
    /// the viewer has no rider, so it runs continuously.</summary>
    public sealed class LiftMotion
    {
        public Vector3 Offset;                // B - A in raw coordinates
        public float TravelTicks = 15, HoldA = 7.5f, HoldB = 15;
        public bool Cosine = true;

        public Vector3 OffsetAt(double seconds)
        {
            double tick = seconds * 15.0, cycle = HoldA + TravelTicks + HoldB + TravelTicks;
            double c = cycle > 0 ? tick % cycle : 0, p;
            if (c < HoldA) p = 0;
            else if (c < HoldA + TravelTicks) p = (c - HoldA) / TravelTicks;
            else if (c < HoldA + TravelTicks + HoldB) p = 1;
            else p = 1 - (c - HoldA - TravelTicks - HoldB) / TravelTicks;
            double e = Cosine ? (1 - Math.Cos(Math.PI * p)) / 2 : p;
            return Offset * (float)e;
        }
    }

    public List<Actor> Actors { get; } = new();
    public int WeaponChunk = 21;               // 16 WARHAMMER, 19 BOOMERANG, 20 TEK CROSSBOW, 21 MAG 60, 22 SHOTGUN, 23 ASSAULT RIFLE, 24 MINIGUN, 25 PLASMA ... (docs)

    static readonly Dictionary<string, CreatureScene?> Cache = new();

    public static readonly string[] WeaponNames =
    {
        "WARHAMMER", "RAPTOR CLAW", "MITE BITE", "BOOMERANG", "TEK CROSSBOW", "MAG 60", "SHOTGUN", "ASSAULT RIFLE", "MINIGUN", "PLASMA", "FREEZE GUN",
        "FLARE GUN", "GRENADES", "SCORPION", "NAPALM", "EMACIATOR", "CEREBRAL BORE", "PFM MINE", "IRON CLAW", "(34)", "TELEPORT MINE",
    };

    static CreatureScene? Load(string creaturesDir, string name)
    {
        if (Cache.TryGetValue(name, out var c)) return c;
        string path = Path.Combine(creaturesDir, name + ".json");
        try { c = File.Exists(path) ? CreatureScene.Load(path) : null; } catch { c = null; }
        return Cache[name] = c;
    }

    static int IdleClip(CreatureScene c)
    {
        for (int i = 0; i < c.Clips.Count; i++) if (c.Clips[i].Role == "idle") return i;
        return 0;
    }

    /// <summary>Builds the actors of one level. <paramref name="levelDirName"/> is e.g. "00_proving_grounds".</summary>
    static Dictionary<int, LiftMotion> LoadLifts(string dataRoot, string levelDirName)
    {
        var d = new Dictionary<int, LiftMotion>();
        string path = Path.Combine(dataRoot, "dynamics", levelDirName + ".json");
        if (!File.Exists(path)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var o in doc.RootElement.GetProperty("dynamics").EnumerateArray())
        {
            if (o.GetProperty("kind").GetString() != "lift" || !o.TryGetProperty("motion", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            var off = m.GetProperty("offset").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            d[o.GetProperty("actorIndex").GetInt32()] = new LiftMotion
            {
                Offset = new Vector3(off[0], off[1], off[2]), TravelTicks = m.GetProperty("travelTicks").GetSingle(),
                HoldA = m.GetProperty("holdAtFromTicks").GetSingle(), HoldB = m.GetProperty("holdAtToTicks").GetSingle(),
                Cosine = (m.GetProperty("easing").GetString() ?? "").StartsWith("cosine"),
            };
        }
        return d;
    }

    public static ActorLayer Build(string dataRoot, string levelDirName, bool pickups, bool bots)
    {
        var layer = new ActorLayer();
        var lifts = LoadLifts(dataRoot, levelDirName);
        string dir = Path.Combine(dataRoot, "creatures");
        string placements = Path.Combine(dir, "placements.json");
        if (!File.Exists(placements)) return layer;
        using var doc = JsonDocument.Parse(File.ReadAllText(placements));
        var characters = doc.RootElement.TryGetProperty("characters", out var ch) ? ch.EnumerateArray().ToList() : [];
        foreach (var lv in doc.RootElement.GetProperty("levels").EnumerateArray())
        {
            if (lv.GetProperty("file").GetString() != levelDirName) continue;
            foreach (var a in lv.GetProperty("actors").EnumerateArray())
            {
                string kind = a.GetProperty("kind").GetString() ?? "";
                int obj = a.GetProperty("object").GetInt32();
                if (kind == "playerStart")
                {
                    var body = Load(dir, "obj_174_m1");                      // Turok (character id 0 = chunk 1)
                    if (body != null) layer.Actors.Add(Make(a, kind, body, true));
                }
                else if ((pickups && kind is "pickup" or "light" or "zone") || kind == "mover")      // F4 also shows the pad beacons (light) and teleporter zones
                {
                    var prop = Load(dir, $"obj_{obj}");
                    if (prop == null) continue;
                    var actor = Make(a, kind, prop, false);
                    if (a.TryGetProperty("index", out var ix)) { actor.Index = ix.GetInt32(); if (lifts.TryGetValue(actor.Index, out var lm)) actor.Lift = lm; }
                    layer.Actors.Add(actor);
                }
            }
            if (bots && lv.TryGetProperty("spawnPoints", out var sps))
            {
                int n = 0;
                foreach (var sp in sps.EnumerateArray())
                {
                    // vary the character and skin so the spawns are easy to tell apart: characterId -> body chunk from skins.json / placements
                    int chunk = characters.Count > 0 ? ChunkFor(n % 9) : 1;
                    var body = Load(dir, $"obj_174_m{chunk}") ?? Load(dir, "obj_174");
                    if (body == null) { n++; continue; }
                    layer.Actors.Add(new Actor
                    {
                        Kind = "spawn", Body = body, Position = new Vector3(sp.GetProperty("x").GetSingle(), sp.GetProperty("y").GetSingle(), sp.GetProperty("z").GetSingle()),
                        Yaw = sp.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0, Scale = 0.5f, Clip = IdleClip(body), Skin = n % 3, TimeOffset = n * 0.13,
                    });
                    n++;
                }
            }
            break;
        }
        return layer;
    }

    // character id -> body chunk (docs/formats/creatures_export.md): 0:1, 1:3, 2:4, 3:5, 4:6, 5:2, 6:7, 7:8, 8:9
    static int ChunkFor(int id) => new[] { 1, 3, 4, 5, 6, 2, 7, 8, 9 }[id % 9];

    static Actor Make(JsonElement a, string kind, CreatureScene body, bool armed)
    {
        var sc = a.TryGetProperty("scale", out var s) && s.ValueKind == JsonValueKind.Array ? s[0].GetSingle() : 0.5f;
        return new Actor
        {
            Kind = kind, Body = body, Position = new Vector3(a.GetProperty("x").GetSingle(), a.GetProperty("y").GetSingle(), a.GetProperty("z").GetSingle()),
            Yaw = a.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0, Scale = sc <= 0 ? 0.5f : sc, Clip = IdleClip(body),
        };
    }

    /// <summary>Skins every actor at <paramref name="time"/> into one drawable scene.</summary>
    public ObjScene Pose(GraphicsDevice gd, double time, Matrix zUp, float yawOffset, bool yawSign, bool mirrorX, Dictionary<string, Texture2D?> textures, string texDir, string creaturesDir)
    {
        var scene = new ObjScene { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        CreatureScene.CreaturesDir = creaturesDir;
        foreach (var a in Actors)
        {
            var p = a.Position; if (a.Lift != null) p += a.Lift.OffsetAt(time); if (mirrorX) p.X = -p.X;
            float facing = (yawSign ? -a.Yaw : a.Yaw) + yawOffset;
            Matrix toWorld = zUp * Matrix.CreateScale(a.Scale) * Matrix.CreateRotationY(facing) * Matrix.CreateTranslation(p);
            if (!float.IsNaN(a.FloorY))
            {
                if (float.IsNaN(a.Drop))                                      // lower the model until its feet touch the floor found under the spawn point
                {
                    var probe = a.Body.Pose(gd, a.Clip, 0, toWorld, textures, texDir, 0, a.Skin);
                    a.Drop = a.FloorY - probe.Min.Y;
                }
                toWorld *= Matrix.CreateTranslation(0, a.Drop, 0);
            }
            Pose(scene, a.Body, a, time, toWorld, gd, textures, texDir, a.Skin);
            if (a.Kind == "playerStart" || (a.Kind == "spawn" && false))
            {
                var weapon = Load(creaturesDir, $"obj_174_m{WeaponChunk}");
                if (weapon != null) Pose(scene, weapon, a, time, toWorld, gd, textures, texDir, 0, body: a.Body);
            }
        }
        return scene;
    }

    static void Pose(ObjScene into, CreatureScene mesh, Actor a, double time, Matrix toWorld, GraphicsDevice gd, Dictionary<string, Texture2D?> textures, string texDir, int skin, CreatureScene? body = null)
    {
        // a weapon shares the body's skeleton and clip: use the body's clip list when the weapon package has none
        var clips = mesh.Clips.Count > 0 ? mesh.Clips : (body ?? mesh).Clips;
        int clipIndex = Math.Min(a.Clip, Math.Max(0, clips.Count - 1));
        int frame = clips.Count > 0 ? CreatureScene.FrameAt(clips[clipIndex], time + a.TimeOffset) : 0;
        var posed = mesh.Pose(gd, mesh.Clips.Count > 0 ? clipIndex : -1, frame, toWorld, textures, texDir, 0, skin);
        into.Min = Vector3.Min(into.Min, posed.Min); into.Max = Vector3.Max(into.Max, posed.Max);
        into.Batches.AddRange(posed.Batches);
    }
}
