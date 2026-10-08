using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>Everything the player carries or has active: ammo counters (0 bullet, 1 energy, 2 explosive, 3..6 special), owned weapons, a timed power-up. Grants follow docs/GAMEPLAY_SPEC.md section 4.</summary>
public sealed class Inventory
{
    public int[] Ammo = [60, 0, 0, 0, 0, 0, 0, 0];                   // [assumed] starting bullet ammo
    public HashSet<string> Weapons = new() { "MAG 60" };
    public string PowerUp = "";
    public float PowerUpTicks;
}

/// <summary>A pickup spawner placed in the level (intelligence records 140-150). Touching it grants its item and hides it until the respawn timer ends.
/// The game pickup model (object 274) is a single textured quad whose look is not decoded, so a colour-coded spinning gem stands in for it.</summary>
public sealed class Pickup
{
    public Vector3 Position;
    public int Record;
    public bool Active = true;
    public float Timer;                              // ticks until it respawns
    public int RespawnMin, RespawnMax;
    public Color Colour;

    const float TouchRadius = 70f;                   // [assumed] the spec gives no touch radius for pickups
    static readonly Random Rng = new();

    public bool Touches(Vector3 feet) => Active && Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(Position.X, Position.Z)) < TouchRadius && feet.Y < Position.Y + Player.Height && feet.Y > Position.Y - 80f;

    /// <summary>Applies the pickup to the player; returns false (and leaves it in place) when it is refused, e.g. a health pack at full health.</summary>
    public bool Grant(Player player)
    {
        var inv = player.Items;
        switch (Record)
        {
            case 140: inv.Weapons.Add("IRON CLAW"); inv.Ammo[6]++; break;
            case 141: inv.Weapons.Add("CEREBRAL BORE"); inv.Ammo[3]++; break;
            case 142: inv.Weapons.Add("SENTRY TURRET"); inv.Ammo[5]++; break;
            case 143: inv.Weapons.Add("PFM MINE"); inv.Ammo[4] += 3; break;
            case 144:                                                  // random power-up, one at a time
                string[] names = ["Slow Time", "Blast Shield", "Regeneration", "Petrify", "Damage Reversal", "Invisibility", "Life Force Vampire", "Speed Burst", "Teleportation", "Massive Damage", "Invulnerability", "Bullet Shield", "Energy Absorption"];
                int[] ticks = [105, 300, 225, 150, 150, 180, 225, 225, 450, 270, 180, 300, 300];
                int i = Rng.Next(names.Length);
                inv.PowerUp = names[i]; inv.PowerUpTicks = ticks[i];
                break;
            case 146: inv.Ammo[1] += 100; break;
            case 147: inv.Ammo[0] += 50; break;
            case 148: inv.Ammo[2] += 5; break;
            case 149: case 150:
                if (player.Health >= Player.MaxHealth) return false;
                player.Health = MathF.Min(Player.MaxHealth, player.Health + (Record == 149 ? 25 : 50));
                break;
            default: return false;
        }
        Active = false;
        Timer = RespawnMin + (float)Rng.NextDouble() * (RespawnMax - RespawnMin);
        return true;
    }

    public void Update(float dtTicks)
    {
        if (Active) return;
        Timer -= dtTicks;
        if (Timer <= 0) Active = true;
    }

    // placeholder gem: an octahedron
    static readonly Vector3[] Corners = [new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];
    static readonly int[] Faces = [0, 4, 2, 0, 3, 4, 0, 5, 3, 0, 2, 5, 1, 2, 4, 1, 4, 3, 1, 3, 5, 1, 5, 2];

    public Batch Mesh(double time)
    {
        var rot = Matrix.CreateRotationY((float)time * 2f) * Matrix.CreateTranslation(Position + new Vector3(0, 60f + 8f * MathF.Sin((float)time * 3f), 0));
        var v = new VertexPositionColorTexture[Faces.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var c = Corners[Faces[i]];
            v[i] = new VertexPositionColorTexture(Vector3.Transform(new Vector3(c.X * 22f, c.Y * 32f, c.Z * 22f), rot), Color.White, Vector2.Zero);
        }
        return new Batch { Vertices = v, Tint = Colour.ToVector3() };
    }

    public static List<Pickup> Load(string dataRoot, string levelDir)
    {
        var list = new List<Pickup>();
        string path = Path.Combine(dataRoot, "baked", levelDir, "spawns.json");
        if (!File.Exists(path)) return list;
        var table = new Dictionary<int, (int min, int max, Color colour)>
        {
            [140] = (300, 675, new Color(230, 60, 60)), [141] = (300, 675, new Color(230, 60, 60)), [142] = (300, 675, new Color(230, 60, 60)), [143] = (300, 675, new Color(230, 60, 60)),
            [144] = (300, 675, new Color(190, 80, 230)), [146] = (105, 225, new Color(80, 160, 255)), [147] = (105, 225, new Color(240, 210, 70)), [148] = (105, 225, new Color(240, 140, 50)),
            [149] = (300, 600, new Color(70, 220, 90)), [150] = (450, 750, new Color(40, 255, 160)),
        };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("actors").EnumerateArray())
        {
            if (a.GetProperty("kind").GetString() != "pickup" || !table.TryGetValue(a.GetProperty("intelligence").GetInt32(), out var info)) continue;
            list.Add(new Pickup
            {
                Record = a.GetProperty("intelligence").GetInt32(), RespawnMin = info.min, RespawnMax = info.max, Colour = info.colour,
                Position = new Vector3(a.GetProperty("x").GetSingle(), a.GetProperty("y").GetSingle(), a.GetProperty("z").GetSingle()),
            });
        }
        return list;
    }
}
