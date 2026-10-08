using Microsoft.Xna.Framework;

namespace Trw.Walk;

/// <summary>Collision against the level's own triangles: upward-facing triangles are ground (found by a vertical probe), downward-facing ones are ceilings, steep ones are walls
/// (the player is a vertical cylinder tested at several heights). The game's real scheme (per-piece boxes plus coarse ground regions) is not fully decoded; this is a first approximation.</summary>
public static class Collision
{
    const float GroundNormalY = 0.35f, WallNormalY = 0.7f;

    /// <summary>Highest ground surface under the player's feet (centre and four points around it) that is not above <paramref name="maxY"/>.</summary>
    public static float? GroundHeight(GameLevel level, Vector3 feet, float radius, float maxY)
    {
        float best = float.NegativeInfinity; bool found = false;
        Span<Vector2> probes = stackalloc Vector2[5];
        float r = radius * 0.6f;
        probes[0] = new Vector2(feet.X, feet.Z); probes[1] = new Vector2(feet.X + r, feet.Z); probes[2] = new Vector2(feet.X - r, feet.Z);
        probes[3] = new Vector2(feet.X, feet.Z + r); probes[4] = new Vector2(feet.X, feet.Z - r);
        foreach (var t in level.Triangles)
        {
            if (t.Normal.Y < GroundNormalY) continue;
            foreach (var p in probes)
            {
                if (p.X < t.Min.X || p.X > t.Max.X || p.Y < t.Min.Z || p.Y > t.Max.Z) continue;
                if (!HeightAt(t, p.X, p.Y, out float y)) continue;
                if (y <= maxY && y > best) { best = y; found = true; }
            }
        }
        foreach (var l in level.Lifts)                                                          // the top of a lift is ground
            foreach (var p in probes)
                if (l.Covers(p.X, p.Y) && l.Top <= maxY && l.Top > best) { best = l.Top; found = true; }
        return found ? best : null;
    }

    /// <summary>The lowest ground surface above <paramref name="minY"/> in the column (used when a spawn point is recorded below every floor).</summary>
    public static float? LowestGroundAbove(GameLevel level, Vector3 feet, float minY)
    {
        float best = float.PositiveInfinity;
        foreach (var t in level.Triangles)
        {
            if (t.Normal.Y < GroundNormalY) continue;
            if (feet.X < t.Min.X || feet.X > t.Max.X || feet.Z < t.Min.Z || feet.Z > t.Max.Z) continue;
            if (HeightAt(t, feet.X, feet.Z, out float y) && y > minY && y < best) best = y;
        }
        return float.IsPositiveInfinity(best) ? null : best;
    }

    /// <summary>Lowest ceiling surface at or above <paramref name="fromY"/> over the player's centre.</summary>
    public static float? CeilingHeight(GameLevel level, Vector3 feet, float radius, float fromY)
    {
        float best = float.PositiveInfinity;
        foreach (var t in level.Triangles)
        {
            if (t.Normal.Y > -GroundNormalY) continue;
            if (feet.X < t.Min.X || feet.X > t.Max.X || feet.Z < t.Min.Z || feet.Z > t.Max.Z) continue;
            if (HeightAt(t, feet.X, feet.Z, out float y) && y >= fromY - 1f && y < best) best = y;
        }
        foreach (var l in level.Lifts)
            if (l.Covers(feet.X, feet.Z) && l.Bottom >= fromY - 1f && l.Bottom < best) best = l.Bottom;
        return float.IsPositiveInfinity(best) ? null : best;
    }

    /// <summary>Pushes the player out of lifts (boxes) and bots (cylinders) unless it can step onto them.</summary>
    public static void PushOutOfObjects(GameLevel level, ref Vector3 feet, float radius, float height, float stepHeight)
    {
        foreach (var l in level.Lifts)
        {
            if (feet.Y + stepHeight >= l.Top || feet.Y + height <= l.Bottom) continue;
            float cx = Math.Clamp(feet.X, l.Position.X - l.HalfX, l.Position.X + l.HalfX), cz = Math.Clamp(feet.Z, l.Position.Z - l.HalfZ, l.Position.Z + l.HalfZ);
            float dx = feet.X - cx, dz = feet.Z - cz, d = MathF.Sqrt(dx * dx + dz * dz);
            if (d >= radius) continue;
            if (d > 1e-4f) { feet.X += dx / d * (radius - d); feet.Z += dz / d * (radius - d); continue; }
            // the centre is inside the box: leave through the nearest side
            float ex = l.HalfX - MathF.Abs(feet.X - l.Position.X), ez = l.HalfZ - MathF.Abs(feet.Z - l.Position.Z);
            if (ex < ez) feet.X += MathF.Sign(feet.X - l.Position.X + 1e-3f) * (ex + radius); else feet.Z += MathF.Sign(feet.Z - l.Position.Z + 1e-3f) * (ez + radius);
        }
        foreach (var b in level.Bots)
        {
            if (feet.Y >= b.Feet.Y + Bot.Height - stepHeight || feet.Y + height <= b.Feet.Y) continue;
            float dx = feet.X - b.Feet.X, dz = feet.Z - b.Feet.Z, d = MathF.Sqrt(dx * dx + dz * dz), r = radius + Bot.Radius;
            if (d >= r) continue;
            if (d < 1e-4f) { dx = 1; d = 1; }
            feet.X += dx / d * (r - d); feet.Z += dz / d * (r - d);
        }
    }

    /// <summary>Pushes the player horizontally out of any steep triangle that overlaps the cylinder above step height.</summary>
    public static void PushOutOfWalls(GameLevel level, ref Vector3 feet, float radius, float height, float stepHeight)
    {
        for (int iteration = 0; iteration < 3; iteration++)
        {
            bool moved = false;
            float y0 = feet.Y + stepHeight, y1 = feet.Y + height;
            foreach (var t in level.Triangles)
            {
                if (MathF.Abs(t.Normal.Y) >= WallNormalY) continue;                              // floors and ceilings are not walls
                if (t.Max.Y < y0 || t.Min.Y > y1) continue;
                if (t.Max.X < feet.X - radius || t.Min.X > feet.X + radius || t.Max.Z < feet.Z - radius || t.Min.Z > feet.Z + radius) continue;
                Vector3 bestPoint = default; float bestDist = float.MaxValue;
                for (int s = 0; s < 4; s++)                                                       // sample the axis from step height to the head
                {
                    var sample = new Vector3(feet.X, MathHelper.Lerp(y0, y1, s / 3f), feet.Z);
                    var c = ClosestPointOnTriangle(sample, t.A, t.B, t.C);
                    float d = Vector3.DistanceSquared(sample, c);
                    if (d < bestDist) { bestDist = d; bestPoint = c; }
                }
                var away = new Vector2(feet.X - bestPoint.X, feet.Z - bestPoint.Z);
                float dist = away.Length();
                if (dist >= radius) continue;
                if (dist < 1e-4f) away = new Vector2(t.Normal.X, t.Normal.Z).LengthSquared() > 1e-6f ? Vector2.Normalize(new Vector2(t.Normal.X, t.Normal.Z)) : Vector2.UnitX;
                else away /= dist;
                float push = radius - dist;
                feet.X += away.X * push; feet.Z += away.Y * push; moved = true;
            }
            if (!moved) break;
        }
    }

    /// <summary>Highest surface of <paramref name="surfaces"/> over (x, z) that is at least <paramref name="minY"/>, or null.</summary>
    public static float? SurfaceAbove(Triangle[] surfaces, float x, float z, float minY)
    {
        float best = float.NegativeInfinity; bool found = false;
        foreach (var t in surfaces)
        {
            if (x < t.Min.X || x > t.Max.X || z < t.Min.Z || z > t.Max.Z) continue;
            if (HeightAt(t, x, z, out float y) && y >= minY && y > best) { best = y; found = true; }
        }
        return found ? best : null;
    }

    static bool HeightAt(Triangle t, float x, float z, out float y)
    {
        y = 0;
        float d = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
        if (MathF.Abs(d) < 1e-6f) return false;
        float l0 = ((t.B.Z - t.C.Z) * (x - t.C.X) + (t.C.X - t.B.X) * (z - t.C.Z)) / d;
        float l1 = ((t.C.Z - t.A.Z) * (x - t.C.X) + (t.A.X - t.C.X) * (z - t.C.Z)) / d;
        float l2 = 1f - l0 - l1;
        if (l0 < -0.0005f || l1 < -0.0005f || l2 < -0.0005f) return false;
        y = l0 * t.A.Y + l1 * t.B.Y + l2 * t.C.Y;
        return true;
    }

    /// <summary>Closest point on a triangle to a point (Ericson, Real-Time Collision Detection).</summary>
    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
