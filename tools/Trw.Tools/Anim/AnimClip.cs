using System.Buffers.Binary;
using Trw.Tools.Models;

namespace Trw.Tools.Anim;

/// <summary>One bone-attached event of a clip (28-byte records, see docs/formats/animation.md; layout from func_002476B8).</summary>
public sealed class AnimEvent
{
    public int Frame;        // +0 u16 (compared as float against the frame window of the current tick)
    public int EffectId;     // +2 u16 (spawn / sound id passed to func_00265E30)
    public int Bone;         // +4 s16, -1 = use the position X/Y/Z as given (otherwise the offset is transformed by that bone's matrix)
    public float X, Y, Z;    // +8, +0xC, +0x10 (the game passes +8, +0x10, +0xC to the spawner: Y/Z swapped)
    public int Param0, Param1; // +0x14, +0x18
    public byte[] Raw = [];
}

/// <summary>
/// A decoded animation chunk (count-7 RNC chunk, see docs/formats/animation.md). All tracks are fully expanded:
/// pos tracks are float[track][frame][3], rot tracks float[track][frame][4] (x,y,z,w quaternion).
/// </summary>
/// <summary>Counted array that tolerates trailing alignment padding (sections are 8-byte aligned, e.g. 8+4*21 = 92 -> 96).</summary>
public readonly struct Counted
{
    public int ElementSize { get; }
    public int Count { get; }
    readonly ReadOnlyMemory<byte> _e;
    Counted(int es, int n, ReadOnlyMemory<byte> e) { ElementSize = es; Count = n; _e = e; }
    public static bool TryParse(ReadOnlyMemory<byte> d, out Counted a)
    {
        a = default;
        var s = d.Span;
        if (s.Length < 8) return false;
        uint es = BinaryPrimitives.ReadUInt32BigEndian(s), n = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
        long need = 8 + (long)es * n;
        if (need > s.Length || s.Length - need >= 8) return false;
        a = new Counted((int)es, (int)n, d[8..]);
        return true;
    }
    public ReadOnlySpan<byte> Element(int i) => _e.Span.Slice(i * ElementSize, ElementSize);
}

public sealed class AnimClip
{
    public int RomOffset;
    public int Frames;
    public int Smooth;             // header +0: moving-average radius applied to rotation tracks (circular)
    public int Compression;        // header +8: 0 = cubic segments, 1 = linear quantised, 2 = raw float
    public float HeaderFloat;      // header +0xC (0.001 in all samples)
    public int RotTrackCount, PosTrackCount;
    public int BoneCount;
    public short[] BonePosTrack = [], BoneRotTrack = [];   // per bone track index, -1 = static (use rest pose)
    public float[][] RestPos = [];                          // per bone x,y,z
    public float[][] RestRot = [];                          // per bone x,y,z,w (s16 / 32768)
    public float[][][] PosTracks = [];                      // [track][frame][3]
    public float[][][] RotTracks = [];                      // [track][frame][4]
    public short[] RootDelta = [];                          // sec0: per-frame s16 forward-motion samples
    public int LoopFrame;                                   // sec3[0] u16 @+2 (0xFFFF = none)
    public float PlaybackRate;                              // sec3[0] f32 @+8 (15.0 in all samples)
    public int Sec3Word0;                                   // sec3[0] u32 @+0
    public float[] Bounds = new float[6];                   // sec6
    public List<AnimEvent> Events = [];                     // sec4
    public byte[] Sec0 = [], Sec3 = [];
    /// <summary>No overrun and fewer than 128 bits (16 bytes) of padding left in both streams.</summary>
    public bool StreamsExact => DecodeOk && PosBitsTotal - PosBitsUsed < 128 && RotBitsTotal - RotBitsUsed < 128;
    public bool DecodeOk;
    public float QuatNormError;                              // max | |q|-1 | of raw rotation samples (smooth == 0 only)
    public int PosBitsUsed, PosBitsTotal, RotBitsUsed, RotBitsTotal;

    public static AnimClip? Parse(byte[] chunk, int romOffset = 0)
    {
        if (!SectionContainer.TryParse(chunk, out var c) || c is null || c.Count != 7) return null;
        var clip = new AnimClip { RomOffset = romOffset };
        // sec0: counted array of s16
        if (Counted.TryParse(c[0], out var ra) && ra.ElementSize == 2)
        {
            clip.RootDelta = new short[ra.Count];
            for (int i = 0; i < ra.Count; i++) clip.RootDelta[i] = BinaryPrimitives.ReadInt16BigEndian(ra.Element(i));
        }
        // sec1: per-bone (posTrack, rotTrack) s16 pairs
        if (!Counted.TryParse(c[1], out var keys) || keys.ElementSize != 4) return null;
        clip.BoneCount = keys.Count;
        clip.BonePosTrack = new short[keys.Count];
        clip.BoneRotTrack = new short[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var e = keys.Element(i);
            clip.BonePosTrack[i] = BinaryPrimitives.ReadInt16BigEndian(e);
            clip.BoneRotTrack[i] = BinaryPrimitives.ReadInt16BigEndian(e[2..]);
        }
        // sec2: rest pose, 20 bytes per bone: f32 x,y,z; s16 qx,qy,qz,qw (/32768)
        if (!Counted.TryParse(c[2], out var rest) || rest.ElementSize != 20 || rest.Count != keys.Count) return null;
        clip.RestPos = new float[rest.Count][];
        clip.RestRot = new float[rest.Count][];
        for (int i = 0; i < rest.Count; i++)
        {
            var e = rest.Element(i);
            clip.RestPos[i] = [BinaryPrimitives.ReadSingleBigEndian(e), BinaryPrimitives.ReadSingleBigEndian(e[4..]), BinaryPrimitives.ReadSingleBigEndian(e[8..])];
            clip.RestRot[i] = new float[4];
            for (int k = 0; k < 4; k++) clip.RestRot[i][k] = BinaryPrimitives.ReadInt16BigEndian(e[(12 + 2 * k)..]) / 32768f;
        }
        // sec3: container(2): [0] 16-byte clip info, [1] 8-byte empty array (unknown)
        clip.Sec3 = c[3].ToArray();
        var s3 = c.Sub(3);
        if (s3 != null && s3.Count == 2)
        {
            var info = s3[0].Span;
            if (info.Length >= 12)
            {
                clip.Sec3Word0 = (int)BinaryPrimitives.ReadUInt32BigEndian(info);
                clip.LoopFrame = BinaryPrimitives.ReadUInt16BigEndian(info[2..]);
                clip.PlaybackRate = BinaryPrimitives.ReadSingleBigEndian(info[8..]);
            }
        }
        // sec4: counted array of 28-byte events (func_002625B8 / func_002625E4 read section 4)
        if (Counted.TryParse(c[4], out var ev) && ev.ElementSize == 28)
        {
            for (int i = 0; i < ev.Count; i++)
            {
                var e = ev.Element(i);
                clip.Events.Add(new AnimEvent
                {
                    Frame = BinaryPrimitives.ReadUInt16BigEndian(e),
                    EffectId = BinaryPrimitives.ReadUInt16BigEndian(e[2..]),
                    Bone = BinaryPrimitives.ReadInt16BigEndian(e[4..]),
                    X = BinaryPrimitives.ReadSingleBigEndian(e[8..]),
                    Y = BinaryPrimitives.ReadSingleBigEndian(e[12..]),
                    Z = BinaryPrimitives.ReadSingleBigEndian(e[16..]),
                    Param0 = BinaryPrimitives.ReadInt32BigEndian(e[20..]),
                    Param1 = BinaryPrimitives.ReadInt32BigEndian(e[24..]),
                    Raw = e.ToArray(),
                });
            }
        }
        clip.Sec0 = c[0].ToArray();
        // sec6: bounding box
        if (c.SectionLength(6) >= 24)
            for (int i = 0; i < 6; i++) clip.Bounds[i] = BinaryPrimitives.ReadSingleBigEndian(c[6].Span[(4 * i)..]);
        // sec5: container(3): header, position streams, rotation streams
        var s5 = c.Sub(5);
        if (s5 == null || s5.Count != 3 || s5.SectionLength(0) < 32) return null;
        var h = s5[0].Span;
        clip.Smooth = (int)BinaryPrimitives.ReadUInt32BigEndian(h);
        clip.Frames = (int)BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
        clip.Compression = (int)BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
        clip.HeaderFloat = BinaryPrimitives.ReadSingleBigEndian(h[12..]);
        clip.RotTrackCount = (int)BinaryPrimitives.ReadUInt32BigEndian(h[16..]);
        clip.PosTrackCount = (int)BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
        if (clip.Frames <= 0 || clip.Frames > 4096 || clip.RotTrackCount > 1000 || clip.PosTrackCount > 1000) return null;

        // position: 3 components, raw float min/max; rotation: 4 components, 6-bit quantised min/max in [-1,1]
        clip.PosTracks = TrackDecoder.DecodeSection(s5[1].Span, clip.PosTrackCount, 3, clip.Frames, clip.Compression, true, 0, 0, 0, out clip.PosBitsUsed, out clip.PosBitsTotal);
        clip.RotTracks = TrackDecoder.DecodeSection(s5[2].Span, clip.RotTrackCount, 4, clip.Frames, clip.Compression, false, -1f, 1f, clip.Smooth, out clip.RotBitsUsed, out clip.RotBitsTotal);
        clip.QuatNormError = TrackDecoder.LastMaxNormError;
        clip.DecodeOk = clip.PosBitsUsed >= 0 && clip.RotBitsUsed >= 0;
        return clip;
    }
}
