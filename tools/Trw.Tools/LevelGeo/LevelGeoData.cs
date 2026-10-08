using System.Numerics;
using Trw.Tools.Levels;

namespace Trw.Tools.LevelGeo;

// Decoders for the level geometry records (see docs/formats/level_geometry.md). All values big-endian in the ROM.

/// <summary>A placed static object ("simple object"): one record of sector array 0 (stride 0x1C) or array 1 (stride 0x34).</summary>
public sealed class Placement
{
    public int Sector;               // index of the sector chunk (grid cell index)
    public int Record;               // index inside the sector array
    public bool Matrix;              // false: compact record (stride 0x1C, yaw only), true: full record (stride 0x34, quaternion + 3 scales)
    public int ObjectId;             // index into archive section 0 (347 static objects)
    public int DefIndex;             // index into the object definition table (archive section 1), -1 = none
    public Vector3 Position;
    public Vector3 Scale = Vector3.One;
    public Quaternion Rotation = Quaternion.Identity;
    public float YawDegrees;         // compact records: 0/90/180/270
    public uint Word0;               // +0: u32, copied to actor +0xD4 (collision group / flags)  [H]
    public ushort Flags;             // compact +0xC / full +0x2E, copied to actor +0xD8 (bit 0x40: fixed 204.8 box instead of the stored bounds)
    public ushort RegionTri = 0xFFFF; // compact +0xA / full +0x2A: index of a level part 1 triangle (0xFFFF = none); max over all shipped objects == triangleCount-1  [V that it indexes part 1; H meaning: region/ground triangle the object belongs to]
    public byte CompactFlags;        // compact +0x18: bits 0-1 rotation, 0x10 negate x, 0x20 negate z
    public Vector3 BoundsMin, BoundsMax;   // world AABB used by the game for actor collision (actor +0xB8..+0xCC)
    public int ModelIndex = -1;      // index into the object's model list (LOD) that the exporter used
    public byte[] Raw = [];

    /// <summary>Local-to-world matrix, row-vector convention (v' = v * M).</summary>
    public Matrix4x4 ToWorld()
    {
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(Rotation);
        // System.Numerics uses the same row-vector convention (v' = v * M) as the game's matrices.
        return Matrix4x4.CreateScale(Scale) * r * Matrix4x4.CreateTranslation(Position);
    }
}

/// <summary>One 0x4C-byte record of sector array 3 (converted by func_00276D20 into a 0xC0-byte light volume).</summary>
public sealed class LightVolume
{
    public int Sector, Record;
    public Vector3 Position;       // +0,+4,+8   s32 * 100/2^22
    public Vector3 AnglesRad;      // +C,+10,+14 s32 * 2^-30 (rotation about x, y, z)  [H: units]
    public Vector3 Extent;         // +0x18,+0x1C,+0x20 s32 * 100/2^22  (ellipsoid / box radii)  [H]
    public float Falloff0, Falloff1; // +0x24,+0x28  s32 * 2 * 2^-31   [H]
    public byte Type;              // +0x2F (1 = special, see doc)
    public byte[] Raw = [];
}

/// <summary>Level part 1: world triangle mesh (with adjacency) used for ground/"void" hit tests.</summary>
public sealed class CollisionMesh
{
    public Vector4[] Vertices = [];
    public CollisionTri[] Tris = [];
    public ZoneRecord[] Zones = [];            // part 1 section 0: 100-byte records (1..4)
    public float[][] Rects = [];               // part 1 section 3: xMin, zMin, xMax, zMax, + u32 flag
    public uint[] RectFlags = [];
}

public sealed class CollisionTri
{
    public ushort Word0, Flags;                // +0, +2 (bit 0x400 = set at load from a per-level bitmask, bit 0x2000 = reset surface)
    public int V0, V1, V2;
    public int N0, N1, N2;                     // neighbour triangle across edge i (0xFFFF = none)
    public uint Surface;                       // +0x10  (0xFFFFFFFF in every shipped triangle)
}

public sealed class ZoneRecord
{
    public byte[] Raw = [];
    public uint[] Words = [];
    public float[] Floats = [];
}

public sealed record ObjectDef(int Index, byte NearLod, byte FarLod, sbyte Part, float Radius, byte[] Raw);

public static class LevelGeoDecode
{
    const float PosScale = 25.6f;      // D_800C8F20.. : compact x/z byte unit
    const float YScale = 5.12f;        // D_800C8F00+4 : compact y s16 unit
    const float BoundUnit = 10.24f;    // D_800C8F18   : compact bound s8 unit
    const float BoundPad = 0.5f;       // D_800C8F18+4
    const float FixedBox = 102.4f;     // D_800C8F10+4 / D_800C8F28 : half-size when flag 0x40 is set
    public const float VertexScale = 0.029296875f;   // D_800C9ADC: collision vertex s32 -> float
    const float LightPos = 100f / 4194304f;          // 2.384185791015625e-05
    const float LightAng = 1f / 1073741824f;         // 2^-30

    /// <summary>Trunc16ToF32: a float stored as its upper 16 bits.</summary>
    static float Trunc16(ushort v) => BitConverter.Int32BitsToSingle(v << 16);

    static Quaternion YawQuat(float rad) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, rad);

    public static List<Placement> Placements(Sector s, int sectorIndex, IReadOnlyList<ObjectDef> defs)
    {
        var list = new List<Placement>();
        var a = s.StaticPlacements;
        for (int i = 0; i < a.Count && a.Stride == 0x1C; i++)
        {
            var r = a.Data.AsSpan(i * 0x1C, 0x1C);
            var p = new Placement { Sector = sectorIndex, Record = i, Matrix = false, Raw = r.ToArray() };
            p.Word0 = Bin.U32(r, 0);
            float scale = Bin.F32(r, 4);
            p.ObjectId = Bin.U16(r, 8);
            p.RegionTri = Bin.U16(r, 0xA);
            p.Flags = Bin.U16(r, 0xC);
            p.DefIndex = Bin.U16(r, 0xE);
            short y = (short)Bin.U16(r, 0x10);
            byte fl = r[0x18];
            p.CompactFlags = fl;
            float x = r[0x19] * PosScale, z = r[0x1A] * PosScale;
            if ((fl & 0x10) != 0) x = -x;
            if ((fl & 0x20) != 0) z = -z;
            p.Position = new Vector3(x, y * YScale, z);
            p.Scale = new Vector3(scale);
            p.YawDegrees = (fl & 3) * 90f;
            p.Rotation = YawQuat((fl & 3) * MathF.PI / 2);
            if ((p.Flags & 0x40) != 0)
            {
                p.BoundsMin = p.Position - new Vector3(FixedBox);
                p.BoundsMax = p.Position + new Vector3(FixedBox);
            }
            else
            {
                p.BoundsMin = p.Position + new Vector3((sbyte)r[0x12], (sbyte)r[0x14], (sbyte)r[0x16]) * BoundUnit - new Vector3(BoundPad);
                p.BoundsMax = p.Position + new Vector3((sbyte)r[0x13], (sbyte)r[0x15], (sbyte)r[0x17]) * BoundUnit + new Vector3(BoundPad);
            }
            list.Add(p);
        }
        a = s.Placements34;
        for (int i = 0; i < a.Count && a.Stride == 0x34; i++)
        {
            var r = a.Data.AsSpan(i * 0x34, 0x34);
            var p = new Placement { Sector = sectorIndex, Record = i, Matrix = true, Raw = r.ToArray() };
            p.Word0 = Bin.U32(r, 0);
            p.Position = new Vector3(Bin.F32(r, 4), Bin.F32(r, 8), Bin.F32(r, 0xC));
            p.Scale = new Vector3(Bin.F32(r, 0x10), Bin.F32(r, 0x14), Bin.F32(r, 0x18));
            p.ObjectId = Bin.U16(r, 0x28);
            p.RegionTri = Bin.U16(r, 0x2A);
            p.DefIndex = Bin.U16(r, 0x2C);
            // quaternion: four signed bytes at +0x2F..+0x32, x 1/127 (D_800C8F20+4), order x y z w, normalised
            var q = new Quaternion(((sbyte)r[0x2F]) / 127f, ((sbyte)r[0x30]) / 127f, ((sbyte)r[0x31]) / 127f, ((sbyte)r[0x32]) / 127f);
            p.Rotation = q.LengthSquared() > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;
            p.Flags = r[0x2E];   // a single byte (copied to actor +0xD8); the quaternion bytes follow at +0x2F
            if ((p.Flags & 0x40) != 0)
            {
                p.BoundsMin = p.Position - new Vector3(FixedBox);
                p.BoundsMax = p.Position + new Vector3(FixedBox);
            }
            else
            {
                p.BoundsMin = p.Position + new Vector3(Trunc16(Bin.U16(r, 0x1C)), Trunc16(Bin.U16(r, 0x1E)), Trunc16(Bin.U16(r, 0x20))) - new Vector3(BoundPad);
                p.BoundsMax = p.Position + new Vector3(Trunc16(Bin.U16(r, 0x22)), Trunc16(Bin.U16(r, 0x24)), Trunc16(Bin.U16(r, 0x26))) + new Vector3(BoundPad);
            }
            list.Add(p);
        }
        foreach (var p in list)
        {
            if (p.DefIndex >= 0 && p.DefIndex < defs.Count)
            {
                // func_0025021C / func_00250754: the model list entry is (def.unkE) % listCount (near) or def.unkF (far)
                sbyte near = (sbyte)defs[p.DefIndex].NearLod, far = (sbyte)defs[p.DefIndex].FarLod;
                p.ModelIndex = near >= 0 ? near : far >= 0 ? far : 0;
            }
        }
        return list;
    }

    public static List<LightVolume> Lights(Sector s, int sectorIndex)
    {
        var res = new List<LightVolume>();
        var a = s.Records4C;
        for (int i = 0; i < a.Count && a.Stride == 0x4C; i++)
        {
            byte[] r = a.Data.AsSpan(i * 0x4C, 0x4C).ToArray();
            float P(int o) => Bin.S32(r, o) * LightPos;
            float A(int o) => Bin.S32(r, o) * LightAng;
            res.Add(new LightVolume
            {
                Sector = sectorIndex, Record = i,
                Position = new Vector3(P(0), P(4), P(8)),
                AnglesRad = new Vector3(A(0xC), A(0x10), A(0x14)),
                Extent = new Vector3(P(0x18), P(0x1C), P(0x20)),
                Falloff0 = Bin.S32(r, 0x24) * 2f * (1f / 2147483648f),
                Falloff1 = Bin.S32(r, 0x28) * 2f * (1f / 2147483648f),
                Type = r[0x2F],
                Raw = r,
            });
        }
        return res;
    }

    /// <summary>Level part 1 (callback func_0044D024): header record array, vertex array, triangle array, rect array.</summary>
    public static CollisionMesh? Collision(Level lv)
    {
        var secs = Container.Sections(lv.ReadPart(1));
        if (secs == null || secs.Length != 4) return null;
        if (!TypedArray.TryParse(secs[0], out var hdr) || !TypedArray.TryParse(secs[1], out var va) ||
            !TypedArray.TryParse(secs[2], out var ta) || !TypedArray.TryParse(secs[3], out var ra)) return null;
        va = va.Untransposed(); ta = ta.Untransposed(); ra = ra.Untransposed(); hdr = hdr.Untransposed();
        var m = new CollisionMesh();
        m.Vertices = new Vector4[va.Count];
        for (int i = 0; i < va.Count && va.Stride == 16; i++)
        {
            var r = va.Data.AsSpan(i * 16, 16);
            m.Vertices[i] = new Vector4(Bin.S32(r, 0), Bin.S32(r, 4), Bin.S32(r, 8), Bin.S32(r, 12)) * VertexScale;
        }
        m.Tris = new CollisionTri[ta.Count];
        for (int i = 0; i < ta.Count && ta.Stride == 0x14; i++)
        {
            var r = ta.Data.AsSpan(i * 0x14, 0x14);
            m.Tris[i] = new CollisionTri
            {
                Word0 = Bin.U16(r, 0), Flags = Bin.U16(r, 2),
                V0 = Bin.U16(r, 4), V1 = Bin.U16(r, 6), V2 = Bin.U16(r, 8),
                N0 = Bin.U16(r, 10), N1 = Bin.U16(r, 12), N2 = Bin.U16(r, 14),
                Surface = Bin.U32(r, 16),
            };
        }
        m.Zones = new ZoneRecord[hdr.Count];
        for (int i = 0; i < hdr.Count; i++)
        {
            var r = hdr.Data.AsSpan(i * hdr.Stride, hdr.Stride);
            var z = new ZoneRecord { Raw = r.ToArray() };
            z.Words = new uint[hdr.Stride / 4];
            z.Floats = new float[hdr.Stride / 4];
            for (int k = 0; k < z.Words.Length; k++) { z.Words[k] = Bin.U32(r, k * 4); z.Floats[k] = Bin.F32(r, k * 4); }
            m.Zones[i] = z;
        }
        m.Rects = new float[ra.Count][];
        m.RectFlags = new uint[ra.Count];
        for (int i = 0; i < ra.Count && ra.Stride == 0x14; i++)
        {
            var r = ra.Data.AsSpan(i * 0x14, 0x14);
            m.Rects[i] = [Bin.F32(r, 0), Bin.F32(r, 4), Bin.F32(r, 8), Bin.F32(r, 12)];
            m.RectFlags[i] = Bin.U32(r, 16);
        }
        return m;
    }

    /// <summary>Archive section 1: the object definition table (container of 40/32-byte records).</summary>
    public static List<ObjectDef> ObjectDefs(RomIndex ix)
    {
        var res = new List<ObjectDef>();
        var node = ix.Find(1);
        if (node == null) return res;
        var secs = Container.Sections(ix.Read(node));
        if (secs == null) return res;
        for (int i = 0; i < secs.Length; i++)
        {
            var r = secs[i];
            if (r.Length < 0x14) { res.Add(new ObjectDef(i, 0, 0, 0, 0, r)); continue; }
            res.Add(new ObjectDef(i, r[0xE], r[0xF], (sbyte)r[0x12], Bin.F32(r, 8), r));
        }
        return res;
    }
}
