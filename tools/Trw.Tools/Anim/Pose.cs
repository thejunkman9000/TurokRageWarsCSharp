using Trw.Tools.Models;

namespace Trw.Tools.Anim;

/// <summary>Row-major 4x4 float matrix, row-vector convention (v' = v * M, translation in row 3), as used by the game / F3DEX2.</summary>
public struct Mat4
{
    public float[] M;   // 16 floats
    public static Mat4 Identity() { var m = new float[16]; m[0] = m[5] = m[10] = m[15] = 1; return new Mat4 { M = m }; }

    /// <summary>Local bone matrix exactly as built in func_002484A0: rotation matrix of the quaternion (transposed for row vectors) + translation.</summary>
    public static Mat4 FromQuatPos(float qx, float qy, float qz, float qw, float px, float py, float pz)
    {
        float xx = qx * qx, yy = qy * qy, zz = qz * qz, ww = qw * qw;
        var m = new float[16];
        m[0] = ww + xx - yy - zz; m[1] = 2 * qx * qy + 2 * qw * qz; m[2] = 2 * qx * qz - 2 * qw * qy;
        m[4] = 2 * qx * qy - 2 * qw * qz; m[5] = ww - xx + yy - zz; m[6] = 2 * qy * qz + 2 * qw * qx;
        m[8] = 2 * qx * qz + 2 * qw * qy; m[9] = 2 * qy * qz - 2 * qw * qx; m[10] = ww - xx - yy + zz;
        m[12] = px; m[13] = py; m[14] = pz; m[15] = 1;
        return new Mat4 { M = m };
    }

    /// <summary>func_0026F690: out = local * parent (3x3 rotation product + translation).</summary>
    public static Mat4 Concat(Mat4 local, Mat4 parent)
    {
        var a = local.M; var b = parent.M; var o = new float[16];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                o[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c];
        for (int c = 0; c < 3; c++)
            o[12 + c] = a[12] * b[c] + a[13] * b[4 + c] + a[14] * b[8 + c] + b[12 + c];
        o[15] = 1;
        return new Mat4 { M = o };
    }

    public (float X, float Y, float Z) Transform(float x, float y, float z)
    {
        var m = M;
        return (x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);
    }
}

/// <summary>Bone hierarchy of a skinned model (taken from the per-bone hit volume records of the model chunk).</summary>
public sealed class Skeleton
{
    public int[] Parent = [];     // -1 = root
    public int[] BoneId = [];     // hit volume byte +0x65 (body-part id used by gameplay code)

    public static Skeleton FromModel(Model m)
    {
        var s = new Skeleton { Parent = new int[m.HitVolumes.Length], BoneId = new int[m.HitVolumes.Length] };
        for (int i = 0; i < s.Parent.Length; i++)
        {
            int p = m.HitVolumes[i].Parent;
            s.Parent[i] = p == 0xFF || p == i ? -1 : p;
            s.BoneId[i] = m.HitVolumes[i].Byte1;
        }
        return s;
    }

    /// <summary>True if every bone's parent has a smaller index (the game computes matrices in one forward pass).</summary>
    public bool IsTopological() { for (int i = 0; i < Parent.Length; i++) if (Parent[i] >= i) return false; return true; }
}

/// <summary>Samples a clip and builds the bone matrices (func_00261EB8 + the bone loop of func_002484A0).</summary>
public static class Pose
{
    /// <summary>Local transform of one bone at an (optionally fractional) frame. Position is linear, rotation spherical (func_00270B1C).</summary>
    public static void SampleBone(AnimClip clip, int bone, float frame, out float[] pos, out float[] rot)
    {
        int f0 = (int)MathF.Floor(frame);
        float t = frame - f0;
        int n = clip.Frames;
        int loop = clip.LoopFrame == 0xFFFF || clip.LoopFrame >= n ? 0 : clip.LoopFrame;
        f0 = Math.Clamp(f0, 0, n - 1);
        int f1 = f0 + 1;
        if (f1 >= n) f1 = loop;
        int pt = clip.BonePosTrack[bone], rt = clip.BoneRotTrack[bone];
        if (pt < 0 || pt >= clip.PosTracks.Length) pos = clip.RestPos[bone];
        else
        {
            var a = clip.PosTracks[pt][f0]; var b = clip.PosTracks[pt][f1];
            pos = [a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]), a[2] + t * (b[2] - a[2])];
        }
        if (rt < 0 || rt >= clip.RotTracks.Length) rot = clip.RestRot[bone];
        else rot = Slerp(clip.RotTracks[rt][f0], clip.RotTracks[rt][f1], t);
    }

    public static float[] Slerp(float[] a, float[] b, float t)
    {
        float dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
        float s = 1;
        if (dot < 0) { dot = -dot; s = -1; }       // shortest path (func_00270B1C compares dot against the negated quaternion)
        float theta = MathF.Acos(Math.Min(1f, dot));
        float sin = MathF.Sin(theta);
        if (sin == 0) return [a[0], a[1], a[2], a[3]];
        float w0 = MathF.Sin((1 - t) * theta) / sin, w1 = MathF.Sin(t * theta) / sin * s;
        return [w0 * a[0] + w1 * b[0], w0 * a[1] + w1 * b[1], w0 * a[2] + w1 * b[2], w0 * a[3] + w1 * b[3]];
    }

    /// <summary>World matrices of all bones at <paramref name="frame"/>. <paramref name="zeroRootXY"/> mimics the game's root-motion removal (translation x,y of bone 0 set to 0).</summary>
    public static Mat4[] World(AnimClip clip, Skeleton sk, float frame, bool zeroRootXY, out float[][] localPos, out float[][] localRot)
    {
        int nb = sk.Parent.Length;
        var world = new Mat4[nb];
        localPos = new float[nb][]; localRot = new float[nb][];
        for (int i = 0; i < nb; i++)
        {
            if (i >= clip.BoneCount) { localPos[i] = [0, 0, 0]; localRot[i] = [0, 0, 0, 1]; }
            else SampleBone(clip, i, frame, out localPos[i], out localRot[i]);
            var p = localPos[i];
            float px = p[0], py = p[1], pz = p[2];
            if (i == 0 && zeroRootXY) { px = 0; py = 0; }
            var q = localRot[i];
            var local = Mat4.FromQuatPos(q[0], q[1], q[2], q[3], px, py, pz);
            world[i] = sk.Parent[i] < 0 ? local : Mat4.Concat(local, world[sk.Parent[i]]);
        }
        return world;
    }
}
