using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Trw.Walk;

/// <summary>What a projectile weapon fires. Damage numbers and splash radii are from docs/GAMEPLAY_SPEC.md section 2; speed, gravity and fuse live in effect blobs that are not decoded, so they are assumed.</summary>
public sealed record ProjectileDef(float Speed, float Gravity, float Direct, float Splash, float SplashRadius, float Fuse, Color Colour, float Size, int FireSound, int ImpactSound);

/// <summary>A flying shot: straight (crossbow, plasma, scorpion) or arcing with a fuse (grenades). Explodes on a wall, the floor, a bot or when its fuse ends, damaging everything in the splash radius.</summary>
public sealed class Projectile
{
    public readonly ProjectileDef Def;
    public Vector3 Position, Velocity;
    public float Age;
    public bool Exploded;
    public Vector3 ExplosionAt;

    public Projectile(ProjectileDef def, Vector3 position, Vector3 velocity) { Def = def; Position = position; Velocity = velocity; }

    /// <summary>Advances the shot; returns true when it exploded this step (<see cref="ExplosionAt"/> is set and <paramref name="hit"/> is the bot it struck directly, if any).</summary>
    public bool Update(float dt, GameLevel level, IReadOnlyList<Bot> bots, out Bot? hit)
    {
        hit = null;
        Age += dt;
        Velocity.Y -= Def.Gravity * dt;
        var step = Velocity * dt; float len = step.Length();
        if (len > 1e-4f)
        {
            var dir = step / len;
            float limit = Collision.RayLevel(level, Position, dir, len) ?? float.MaxValue;
            foreach (var b in bots)
            {
                if (b.Dead) continue;
                if (Collision.RayCylinder(Position, dir, b.Feet, Bot.Radius, Bot.Height) is float d && d <= len && d < limit) { limit = d; hit = b; }
            }
            if (limit <= len) { Exploded = true; ExplosionAt = Position + dir * limit; return true; }
        }
        Position += step;
        if (Age >= Def.Fuse) { Exploded = true; ExplosionAt = Position; return true; }
        return false;
    }

    static readonly Vector3[] Corners = [new(0, 1, 0), new(0, -1, 0), new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];
    static readonly int[] Faces = [0, 4, 2, 0, 3, 4, 0, 5, 3, 0, 2, 5, 1, 2, 4, 1, 4, 3, 1, 3, 5, 1, 5, 2];

    /// <summary>A coloured octahedron (stand-in for the effect objects, which are not decoded).</summary>
    public static Batch Gem(Vector3 centre, float size, Color colour, float alpha = 1f, float spin = 0f)
    {
        var rot = Matrix.CreateRotationY(spin) * Matrix.CreateTranslation(centre);
        var v = new VertexPositionColorTexture[Faces.Length];
        for (int i = 0; i < v.Length; i++) v[i] = new VertexPositionColorTexture(Vector3.Transform(Corners[Faces[i]] * size, rot), Color.White, Vector2.Zero);
        return new Batch { Vertices = v, Tint = colour.ToVector3(), Translucent = alpha < 1f, Alpha = alpha, SortKey = 2000 };
    }

    public Batch Mesh() => Gem(Position, Def.Size, Def.Colour, 1f, Age * 10f);
}

/// <summary>A short-lived expanding ball drawn where a shot exploded.</summary>
public sealed class Explosion
{
    public Vector3 Position; public float Radius, Age; public Color Colour;
    const float Life = 0.45f;
    public bool Done => Age >= Life;
    public Batch Mesh() => Projectile.Gem(Position, Radius * (0.3f + 0.7f * Age / Life), Colour, 0.6f * (1f - Age / Life), Age * 6f);
}
