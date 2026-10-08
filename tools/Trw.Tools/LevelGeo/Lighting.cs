using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.LevelGeo;

// Lighting of the game (see docs/formats/lighting.md). Everything here mirrors game code:
//   func_00276D20  light-volume record (0x4C) -> runtime volume (matrix, inverse, AABB, colour)
//   func_00278530  per-placement vertex lighting of a static object (called through func_00250BD4 / func_00251F0C)
//   func_00278090  which volumes of the sector overlap the object bounds
//   func_00277620  per vertex: sum over volumes (func_00277444 -> func_00277198)
//   func_002782E4  clamp + brightness lift, written into the vertex colour bytes
//   func_0027899C  per-frame flicker of vertices that carry animation bits (applied by the viewer from the exported flags)

/// <summary>Runtime light volume (the 0xC0-byte record func_00276D20 builds).</summary>
public sealed class VolumeRt
{
    public int Sector, Record;
    public Vector3 Pos, AnglesRad, Extent;
    public float SoftEdge;        // runtime +0x00 = 2 * s32(+0x24) * 2^-31
    public float AmbientFraction; // runtime +0x04 = 2 * s32(+0x28) * 2^-31
    public Vector3 Color;         // runtime +0x08..+0x10 = u32(+0x3C,+0x40,+0x44) * 2.3748725652694702e-06 (0..255 scale)
    public byte Shape;            // +0x2F  (0 = ellipsoid, 1 = box with fixed direction, 2 = cone); 0 in all shipped data
    public byte Negative;         // +0x37  (runtime +0xB8) subtractive light
    public byte AnimKind;         // +0x3B  (runtime +0xBA) 0 none, 1 per-volume flicker phase, 2 position-dependent phase
    public byte Restrict;         // +0x4B  (runtime +0xBD) only applies to objects listed in sector array 4
    public bool Aligned;          // runtime +0xB9: all three angles within 0.01 rad -> rotation ignored in tests
    public float[] M = new float[16];     // local -> world, row-vector convention (v' = v*M), runtime +0x60
    public float[] Inv = new float[16];   // world -> local, runtime +0x20
    public Vector3 Min, Max;              // world AABB of the transformed [-1,1]^3 cube, runtime +0xA0 / +0xAC
    public byte[] Raw = [];

    const float PosScale = 100f / 4194304f;
    const float AngScale = 1f / 1073741824f;
    const float ColScale = 2.3748725652694702e-06f;   // D_800C9B88
    const float TwoM31 = 2f / 2147483648f;            // 2 * 2^-31 (D_800C9B78+4 is 2^-31)

    public static VolumeRt FromRecord(int sector, int record, byte[] r)
    {
        var v = new VolumeRt { Sector = sector, Record = record, Raw = r };
        float P(int o) => Bin.S32(r, o) * PosScale;
        float A(int o) => Bin.S32(r, o) * AngScale;
        v.Pos = new Vector3(P(0), P(4), P(8));
        v.AnglesRad = new Vector3(A(0xC), A(0x10), A(0x14));
        v.Extent = new Vector3(P(0x18), P(0x1C), P(0x20));
        v.SoftEdge = 2f * (float)Bin.S32(r, 0x24) * (1f / 2147483648f);
        v.AmbientFraction = 2f * (float)Bin.S32(r, 0x28) * (1f / 2147483648f);
        v.Shape = r[0x2F];
        v.Negative = r[0x37];
        v.AnimKind = r[0x3B];
        v.Restrict = r[0x4B];
        v.Color = new Vector3((float)(double)Bin.U32(r, 0x3C) * ColScale, (float)(double)Bin.U32(r, 0x40) * ColScale, (float)(double)Bin.U32(r, 0x44) * ColScale);
        v.Aligned = Math.Abs(v.AnglesRad.X) <= 0.01f && Math.Abs(v.AnglesRad.Y) <= 0.01f && Math.Abs(v.AnglesRad.Z) <= 0.01f;
        // matrix: identity, scale(extent), Rz(angle z), Ry(angle y), Rx(angle x), translate(pos)   (func_00273618/CD8/B08/930/4B8)
        var m = Mat.Identity();
        Mat.ScaleCols(m, v.Extent.X, v.Extent.Y, v.Extent.Z);
        Mat.RotZ(m, v.AnglesRad.Z);
        Mat.RotY(m, v.AnglesRad.Y);
        Mat.RotX(m, v.AnglesRad.X);
        m[12] += v.Pos.X; m[13] += v.Pos.Y; m[14] += v.Pos.Z;
        v.M = m;
        v.Inv = Mat.Invert(m);
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var p = Mat.Point(m, new Vector3((c & 1) != 0 ? -1f : 1f, (c & 2) != 0 ? -1f : 1f, (c & 4) != 0 ? -1f : 1f));
            mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
        }
        v.Min = mn; v.Max = mx;
        return v;
    }
}

/// <summary>4x4 float matrices stored row-major (index r*4+c), row-vector convention, exactly as the game's helpers use them.</summary>
public static class Mat
{
    public static float[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    public static void ScaleCols(float[] m, float sx, float sy, float sz)
    {
        for (int r = 0; r < 4; r++) { m[r * 4] *= sx; m[r * 4 + 1] *= sy; m[r * 4 + 2] *= sz; }
    }
    public static void RotZ(float[] m, float a)   // func_00273CD8
    {
        float s = MathF.Sin(a), c = MathF.Cos(a);
        for (int r = 0; r < 4; r++) { float x = m[r * 4]; m[r * 4] = x * c + m[r * 4 + 1] * -s; m[r * 4 + 1] = x * s + m[r * 4 + 1] * c; }
    }
    public static void RotY(float[] m, float a)   // func_00273B08
    {
        float s = MathF.Sin(a), c = MathF.Cos(a);
        for (int r = 0; r < 4; r++) { float x = m[r * 4]; m[r * 4] = x * c + m[r * 4 + 2] * s; m[r * 4 + 2] = x * -s + m[r * 4 + 2] * c; }
    }
    public static void RotX(float[] m, float a)   // func_00273930
    {
        float s = MathF.Sin(a), c = MathF.Cos(a);
        for (int r = 0; r < 4; r++) { float x = m[r * 4 + 1]; m[r * 4 + 1] = x * c + m[r * 4 + 2] * -s; m[r * 4 + 2] = x * s + m[r * 4 + 2] * c; }
    }
    /// <summary>func_00272908: v*M3x3 + translation row.</summary>
    public static Vector3 Point(float[] m, Vector3 v) => new(
        m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
        m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
        m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]);
    /// <summary>func_00272BA8: v*M3x3 (no translation).</summary>
    public static Vector3 Vector(float[] m, Vector3 v) => new(
        m[0] * v.X + m[4] * v.Y + m[8] * v.Z,
        m[1] * v.X + m[5] * v.Y + m[9] * v.Z,
        m[2] * v.X + m[6] * v.Y + m[10] * v.Z);
    public static float[] Invert(float[] m)
    {
        var n = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        if (!Matrix4x4.Invert(n, out var i)) i = Matrix4x4.Identity;
        return [i.M11, i.M12, i.M13, i.M14, i.M21, i.M22, i.M23, i.M24, i.M31, i.M32, i.M33, i.M34, i.M41, i.M42, i.M43, i.M44];
    }
    /// <summary>Game matrix of a placement: func_0024F940 / func_0024FD4C: rotation (yaw or quaternion), scale rows, translation, then func_00273DDC swaps rows 1 and 2 of the 3x3.</summary>
    public static float[] Placement(Placement p)
    {
        var rot = Matrix4x4.CreateFromQuaternion(p.Rotation);   // same convention as func_002742B4 / func_00273744
        float[] m = [rot.M11, rot.M12, rot.M13, 0, rot.M21, rot.M22, rot.M23, 0, rot.M31, rot.M32, rot.M33, 0, 0, 0, 0, 1];
        for (int c = 0; c < 3; c++) { m[c] *= p.Scale.X; m[4 + c] *= p.Scale.Y; m[8 + c] *= p.Scale.Z; }   // func_002734EC scales rows
        m[12] = p.Position.X; m[13] = p.Position.Y; m[14] = p.Position.Z;
        for (int c = 0; c < 3; c++) (m[4 + c], m[8 + c]) = (m[8 + c], m[4 + c]);   // func_00273DDC
        return m;
    }
}

/// <summary>The result of lighting one placed object: one entry per Model.Vertices element (copies made for ST overrides share the colour of their source vertex).</summary>
public sealed class LitPlacement
{
    public byte[] Rgb = [];          // 3 bytes per vertex: final vertex colour (for animated vertices: the clamped [30,225] base the flicker is added to)
    public byte[] Anim = [];         // per vertex: original flag bits 0x7C (0x70 phase*16, 0x0C shift*4) after lighting
    public int VolumeCount;          // volumes that overlapped the object bounds
    public string Mode = "";         // "volumes", "none" (lit level but no volume overlaps: baked offsets only) or "fallback"
}

public static class PieceLighting
{
    static readonly float[] FalloffTable = BuildTable();

    static float[] BuildTable()
    {
        // D_800D2648 (ROM 0xD3248): 65 floats, (i/64)^(1/4); entry 65 is 0
        var t = new float[66];
        for (int i = 0; i <= 64; i++) t[i] = MathF.Pow(i / 64f, 0.25f);
        return t;
    }

    public static sbyte[]? FlickerFromRom;

    const float ColorMin = 5f, ColorMax = 255f;

    /// <summary>
    /// func_00278530. levelHasVolumes = any sector of the level has a volume (the loop over D_8011FE88 entries).
    /// brightness = D_801462D8 (0..255, game option; 0 by default).
    /// </summary>
    public static LitPlacement Light(Placement pl, Model model, IReadOnlyList<VolumeRt> sectorVolumes, bool levelHasVolumes, Func<Placement, VolumeRt, bool> restrictedOk, float brightness = 0f)
    {
        var res = new LitPlacement();
        int n = model.Vertices.Length;
        int orig = model.OriginalVertexCount;
        var acc = new Vector3[orig];
        // baked per-vertex offsets: model section 1 (u32 per ROM vertex): R = signed byte 2 * 2, G = signed byte 1 * 2, B = signed byte 0 * 2
        if (CountedArray.TryParse(model.RawSections[1], out var offs) && offs.Count == orig && offs.ElementSize == 4)
        {
            for (int i = 0; i < orig; i++)
            {
                var e = offs.Element(i);
                acc[i] = new Vector3((sbyte)e[1] * 2f, (sbyte)e[2] * 2f, (sbyte)e[3] * 2f);
            }
        }
        var sel = new List<VolumeRt>();
        bool anim = false;
        foreach (var v in sectorVolumes)
        {
            if (!(pl.BoundsMax.X > v.Min.X && pl.BoundsMin.X < v.Max.X && pl.BoundsMax.Z > v.Min.Z && pl.BoundsMin.Z < v.Max.Z &&
                  pl.BoundsMax.Y > v.Min.Y && pl.BoundsMin.Y < v.Max.Y)) continue;
            if (v.Restrict != 0 && !restrictedOk(pl, v)) continue;
            sel.Add(v);
            if (v.AnimKind != 0) anim = true;
            if (sel.Count == 0x40) break;
        }
        res.VolumeCount = sel.Count;
        var flags = new ushort[orig];
        for (int i = 0; i < orig; i++) flags[i] = model.Vertices[i].Flag;
        var m = Mat.Placement(pl);
        if (sel.Count == 0)
        {
            res.Mode = levelHasVolumes ? "none" : "fallback";
            // fallback (func_00277B08) needs the runtime light D_8013B138; see docs: not reproduced here, baked offsets only
        }
        else
        {
            res.Mode = "volumes";
            for (int i = 0; i < orig; i++)
            {
                var vt = model.Vertices[i];
                if ((flags[i] & 0x80) != 0) continue;
                var pos = Mat.Point(m, new Vector3(vt.X, vt.Y, vt.Z));
                var nrm = Mat.Vector(m, new Vector3((sbyte)vt.R, (sbyte)vt.G, (sbyte)vt.B));
                byte kind = 0; float dist = 0; int idx = 0;
                var sum = Vector3.Zero;
                foreach (var v in sel)
                {
                    if (!(pos.X < v.Max.X && pos.Y < v.Max.Y && pos.Z < v.Max.Z && v.Min.X <= pos.X && v.Min.Y <= pos.Y && v.Min.Z <= pos.Z)) continue;
                    if (!VolumeLight(v, pos, nrm, out var contrib, out float d)) continue;
                    if (v.AnimKind != 0) { kind = v.AnimKind; idx = 0; dist = d; }
                    sum += contrib;
                }
                if (kind != 0)
                {
                    int k = 0;
                    if (dist < 0.6f) k = 1;
                    if (dist < 0.3f) k = 2;
                    ushort f = flags[i];
                    if (kind == 1) f = (ushort)((f & 0xFF8F) | ((((idx % 7) + 1) * 16) & 0x70));
                    if (kind == 2)
                    {
                        uint a = ToU32(pos.X) / 10, b = ToU32(pos.Y) / 10, c = ToU32(pos.Z) / 10;
                        uint s = (a + b + c) % 7;
                        f = (ushort)((f & 0xFF8F) | (int)(((s + 1) * 16) & 0x70));
                    }
                    f = (ushort)((f & 0xFFF3) | (k << 2));
                    if (dist < 0.1f) f = (ushort)(f & 0xFF83);
                    flags[i] = f;
                }
                acc[i] += sum;
            }
        }
        // func_002782E4: clamp [5,255], lift towards 255 by brightness/255, truncate
        float lift = MathF.Min(1f, brightness * 0.003921568859f);
        var rgb = new byte[n * 3];
        var an = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int s = i < orig ? i : model.SourceVertex[i];
            var t = acc[s];
            byte Conv(float x)
            {
                if (!(x <= ColorMax)) x = ColorMax;
                if (!(ColorMin <= x)) x = ColorMin;
                x = x * (1f - lift) + lift * 255f;
                int v = (int)x;
                return (byte)Math.Clamp(v, 0, 255);
            }
            byte r = Conv(t.X), g = Conv(t.Y), b = Conv(t.Z);
            ushort f = flags[s];
            if (anim && (f & 0x70) != 0)
            {
                // func_00278530: when any overlapping volume animates, the base for the flicker is the colour clamped to [0x1E,0xE1]
                byte Cl(byte c) => (byte)((c < 0xE1 && c < 0x1F) ? 0x1E : (c < 0xE1 ? c : 0xE1));
                r = Cl(r); g = Cl(g); b = Cl(b);
            }
            rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
            an[i] = (byte)(f & 0x7C);
        }
        res.Rgb = rgb; res.Anim = an;
        return res;
    }

    static uint ToU32(float x) => x >= 2147483648f ? 0x80000000u + (uint)(int)(x - 2147483648f) : unchecked((uint)(int)x);

    /// <summary>func_00277444 + func_00277198 (shape 0 = ellipsoid). Returns false when the point is outside the volume.</summary>
    public static bool VolumeLight(VolumeRt v, Vector3 pos, Vector3 nrm, out Vector3 contrib, out float d)
    {
        contrib = Vector3.Zero; d = 0;
        Vector3 wp, wd;
        if (v.Aligned)
        {
            wp = new Vector3(pos.X * v.Inv[0] + v.Inv[12], pos.Y * v.Inv[5] + v.Inv[13], pos.Z * v.Inv[10] + v.Inv[14]);
            wd = new Vector3(nrm.X * v.Inv[0], nrm.Y * v.Inv[5], nrm.Z * v.Inv[10]);
        }
        else
        {
            wp = Mat.Point(v.Inv, pos);
            wd = Mat.Vector(v.Inv, nrm);
        }
        wd = Norm(wd);
        // func_00277198
        if (!(wp.X < 1f && wp.X > -1f && wp.Y < 1f && wp.Y > -1f && wp.Z < 1f && wp.Z > -1f)) return false;
        float w, hitX, hitY, hitZ;
        switch (v.Shape)
        {
            case 0:
            {
                float d2 = wp.X * wp.X + wp.Y * wp.Y + wp.Z * wp.Z;
                if (d2 > 1f) return false;
                float tab = FalloffTable[(int)(d2 * 64f)];
                float t1 = 1f - tab;
                w = t1 < v.SoftEdge ? t1 / v.SoftEdge : 1f;
                var h = Norm(wp);
                hitX = h.X; hitY = h.Y; hitZ = h.Z;
                break;
            }
            default:
                throw new NotSupportedException($"light volume shape {v.Shape} not implemented (not present in shipped data)");
        }
        float t = -(hitX * wd.X + hitY * wd.Y + hitZ * wd.Z);
        if (t < 0f) t = 0f;
        float f4 = v.AmbientFraction;
        d = w * (f4 + t * (1f - f4));
        if (d <= 0f) d = 0f;
        if (v.Negative != 0) d = -d;
        contrib = v.Color * d;
        return true;
    }

    static Vector3 Norm(Vector3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (len == 0f) return v;
        float inv = 1f / len;
        return new Vector3(v.X * inv, v.Y * inv, v.Z * inv);
    }
}
