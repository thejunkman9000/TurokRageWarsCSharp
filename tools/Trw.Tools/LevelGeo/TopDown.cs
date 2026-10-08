using System.Numerics;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.LevelGeo;

/// <summary>Software top-down orthographic projection (x right, z down, highest surface wins) written as PNG with the in-house writer.</summary>
static class TopDown
{
    const int Size = 1024;

    public static void Render(Func<int, (byte R, byte G, byte B)> avgColor, List<Vector3> pos, List<(byte R, byte G, byte B)> col,
        List<(int A, int B, int C, int Mat, int Sector)> tris, List<int> matTex, List<Placement> placements, CollisionMesh? coll,
        NavNode[] nodes, string dir)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in pos) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        float span = Math.Max(Math.Max(maxX - minX, maxZ - minZ), 1f);
        float scale = (Size - 8) / span;
        float ox = 4 - minX * scale + (Size - 8 - (maxX - minX) * scale) / 2;
        float oz = 4 - minZ * scale + (Size - 8 - (maxZ - minZ) * scale) / 2;
        var img = new byte[Size * Size * 4];
        var depth = new float[Size * Size];
        Array.Fill(depth, float.MinValue);
        for (int i = 0; i < img.Length; i += 4) { img[i] = 18; img[i + 1] = 20; img[i + 2] = 26; img[i + 3] = 255; }

        var texAvg = new Dictionary<int, (byte, byte, byte)>();
        foreach (var t in tris)
        {
            var a = pos[t.A]; var b = pos[t.B]; var c = pos[t.C];
            int tex = matTex[t.Mat];
            if (!texAvg.TryGetValue(tex, out var ta)) texAvg[tex] = ta = avgColor(tex);
            var ca = col[t.A]; var cb = col[t.B]; var cc = col[t.C];
            float vr = 0.5f + (ca.R + cb.R + cc.R) / 1530f, vg = 0.5f + (ca.G + cb.G + cc.G) / 1530f, vb = 0.5f + (ca.B + cb.B + cc.B) / 1530f;
            float h = maxY > minY ? ((a.Y + b.Y + c.Y) / 3 - minY) / (maxY - minY) : 0.5f;
            float shade = 0.55f + 0.45f * h;
            // face normal brightness: up-facing faces brighter
            var n = Vector3.Cross(b - a, c - a);
            float nl = n.Length();
            float up = nl > 1e-6f ? Math.Abs(n.Y) / nl : 0f;
            shade *= 0.6f + 0.4f * up;
            byte R = (byte)Math.Clamp(ta.Item1 * vr * 1.3f * shade, 0, 255), G = (byte)Math.Clamp(ta.Item2 * vg * 1.3f * shade, 0, 255), B = (byte)Math.Clamp(ta.Item3 * vb * 1.3f * shade, 0, 255);
            Fill(img, depth, a, b, c, scale, ox, oz, R, G, B);
        }
        PngWriter.WriteRgba(Path.Combine(dir, "topdown.png"), Size, Size, img);

        // overlay: object AABBs (dim), collision triangles (red), nav nodes (cyan), light volumes omitted
        foreach (var p in placements)
            Rect(img, p.BoundsMin.X, p.BoundsMin.Z, p.BoundsMax.X, p.BoundsMax.Z, scale, ox, oz, 70, 120, 70);
        if (coll != null)
        {
            foreach (var t in coll.Tris)
            {
                var a = coll.Vertices[t.V0]; var b = coll.Vertices[t.V1]; var c = coll.Vertices[t.V2];
                Line(img, a.X, a.Z, b.X, b.Z, scale, ox, oz, 255, 60, 60);
                Line(img, b.X, b.Z, c.X, c.Z, scale, ox, oz, 255, 60, 60);
                Line(img, c.X, c.Z, a.X, a.Z, scale, ox, oz, 255, 60, 60);
            }
            foreach (var r in coll.Rects) Rect(img, r[0], r[1], r[2], r[3], scale, ox, oz, 255, 200, 40);
        }
        foreach (var n in nodes)
        {
            int px = (int)(n.X * scale + ox), pz = (int)(n.Z * scale + oz);
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) Put(img, px + dx, pz + dy, 0, 230, 255);
        }
        PngWriter.WriteRgba(Path.Combine(dir, "topdown_collision.png"), Size, Size, img);
    }

    static void Put(byte[] img, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size) return;
        int i = (y * Size + x) * 4;
        img[i] = r; img[i + 1] = g; img[i + 2] = b; img[i + 3] = 255;
    }

    static void Rect(byte[] img, float x0, float z0, float x1, float z1, float s, float ox, float oz, byte r, byte g, byte b)
    {
        Line(img, x0, z0, x1, z0, s, ox, oz, r, g, b); Line(img, x1, z0, x1, z1, s, ox, oz, r, g, b);
        Line(img, x1, z1, x0, z1, s, ox, oz, r, g, b); Line(img, x0, z1, x0, z0, s, ox, oz, r, g, b);
    }

    static void Line(byte[] img, float x0, float z0, float x1, float z1, float s, float ox, float oz, byte r, byte g, byte b)
    {
        float ax = x0 * s + ox, az = z0 * s + oz, bx = x1 * s + ox, bz = z1 * s + oz;
        int steps = (int)Math.Max(Math.Abs(bx - ax), Math.Abs(bz - az)) + 1;
        if (steps > 4000) return;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            Put(img, (int)(ax + (bx - ax) * t), (int)(az + (bz - az) * t), r, g, b);
        }
    }

    static void Fill(byte[] img, float[] depth, Vector3 a, Vector3 b, Vector3 c, float s, float ox, float oz, byte r, byte g, byte bl)
    {
        float x0 = a.X * s + ox, y0 = a.Z * s + oz, x1 = b.X * s + ox, y1 = b.Z * s + oz, x2 = c.X * s + ox, y2 = c.Z * s + oz;
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        int minx = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), maxx = Math.Min(Size - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int miny = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), maxy = Math.Min(Size - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (Math.Abs(area) < 1e-6f)
        {
            // degenerate in projection (vertical wall): draw its edges so walls remain visible
            float y = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            LineDepth(img, depth, x0, y0, x1, y1, y, r, g, bl); LineDepth(img, depth, x1, y1, x2, y2, y, r, g, bl);
            return;
        }
        float zAvg = (a.Y + b.Y + c.Y) / 3;
        for (int py = miny; py <= maxy; py++)
            for (int px = minx; px <= maxx; px++)
            {
                float fx = px + 0.5f, fy = py + 0.5f;
                float w0 = ((x1 - fx) * (y2 - fy) - (x2 - fx) * (y1 - fy)) / area;
                float w1 = ((x2 - fx) * (y0 - fy) - (x0 - fx) * (y2 - fy)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float h = w0 * a.Y + w1 * b.Y + w2 * c.Y;
                int idx = py * Size + px;
                if (h < depth[idx]) continue;
                depth[idx] = h;
                int i4 = idx * 4;
                img[i4] = r; img[i4 + 1] = g; img[i4 + 2] = bl; img[i4 + 3] = 255;
            }
    }

    static void LineDepth(byte[] img, float[] depth, float ax, float ay, float bx, float by, float h, byte r, byte g, byte b)
    {
        int steps = (int)Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)) + 1;
        if (steps > 4000) return;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            int x = (int)(ax + (bx - ax) * t), y = (int)(ay + (by - ay) * t);
            if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
            int idx = y * Size + x;
            if (h < depth[idx]) continue;
            depth[idx] = h;
            int i4 = idx * 4;
            img[i4] = r; img[i4 + 1] = g; img[i4 + 2] = b; img[i4 + 3] = 255;
        }
    }
}
