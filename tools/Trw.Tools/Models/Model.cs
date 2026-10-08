using System.Buffers.Binary;

namespace Trw.Tools.Models;

/// <summary>
/// N64 vertex as stored in the model (16 bytes): position, flag, s/t (s10.5 texel coords BEFORE the material's G_TEXTURE scale), colour/alpha.
/// S/T are floats only because copies created for G_MODIFYVTX ST overrides store (final value * 65536 / scale) so that every consumer can
/// keep using u = S/32 * scaleS/65536 / texWidth for all vertices (see docs/formats/texture_mapping.md). ROM vertices are exact integers.
/// </summary>
public readonly record struct Vtx(short X, short Y, short Z, ushort Flag, float S, float T, byte R, byte G, byte B, byte A);

/// <summary>24-byte material record at the start of every part (see docs/formats/models_textures.md).</summary>
public sealed class ModelMaterial
{
    public int TextureId;      // index into the 507-entry 3D texture table, -1 = untextured
    public uint Flags;         // +4
    public short Field8;       // +8
    public byte FieldA;        // +0xA
    public byte[] Color1 = new byte[4];   // +0xC  (RGBA)
    public byte[] Color2 = new byte[4];   // +0x10 (RGBA)
    public ushort ScaleS;      // +0x14  texture coordinate scale (0x2000 = x1/8)
    public ushort ScaleT;      // +0x16
}

/// <summary>ModifyVtxSt = F3DEX2 G_MODIFYVTX with offset 0x14 (G_MWO_POINT_ST): replaces the S,T of one vertex-buffer slot (A = slot, B = S, C = T, s10.5, final values: NOT multiplied by the G_TEXTURE scale).</summary>
public enum DlKind { Vtx, Tri, Matrix, ModifyVtxSt }

/// <summary>One decoded command of the compact display-list byte code.</summary>
public readonly record struct DlCommand(DlKind Kind, int A, int B, int C, uint Rgba = 0);

public sealed class ModelPart
{
    public ModelMaterial Material = new();
    public List<DlCommand> Commands = [];
}

/// <summary>Per-bone collision volume of a skinned model (112 bytes).</summary>
public sealed class HitVolume
{
    public float[] Corners = new float[24];   // 8 corner points (x,y,z)
    public float Radius;
    public byte Parent;                       // 0xFF = root (hypothesis: parent bone index)
    public byte Byte1;
    public ushort Flags;                      // 0x0400 in all samples
    public float Scale;                       // 1.0
    public uint Tail;                         // 1
}

public sealed class MeshTriangle
{
    public int V0, V1, V2;   // indices into Model.Vertices
    public int Part;
}

/// <summary>
/// Model chunk: 3 sections (static) or 6 sections (skinned, with hit volumes). Loader: func_0026AD8C.
/// </summary>
public sealed class Model
{
    public Vtx[] Vertices = [];
    public ModelPart[] Parts = [];
    public HitVolume[] HitVolumes = [];
    public bool Skinned;
    /// <summary>Matrix (bone) index that was active when each vertex was loaded; 0 for static models.</summary>
    public int[] BoneOfVertex = [];
    /// <summary>Number of vertices stored in the ROM array; entries from here on are copies created for G_MODIFYVTX ST overrides.</summary>
    public int OriginalVertexCount;
    /// <summary>Part (material) that each entry of <see cref="Vertices"/> belongs to (copies inherit the part that issued the override).</summary>
    public int[] PartOfVertex = [];
    /// <summary>For every entry of <see cref="Vertices"/> the index of the ROM vertex it was copied from (itself for ROM vertices).</summary>
    public int[] SourceVertex = [];
    List<MeshTriangle> _tris = [];
    public byte[][] RawSections = [];

    public static Model? Parse(byte[] chunk)
    {
        if (!SectionContainer.TryParse(chunk, out var c) || c is null) return null;
        if (c.Count != 3 && c.Count != 6) return null;
        if (!CountedArray.TryParse(c[0], out var va) || va.ElementSize != 16) return null;
        var parts = c.Sub(2);
        if (parts == null) return null;
        var m = new Model { Skinned = c.Count == 6 };
        m.RawSections = Enumerable.Range(0, c.Count).Select(i => c[i].ToArray()).ToArray();
        m.Vertices = new Vtx[va.Count];
        for (int i = 0; i < va.Count; i++)
        {
            var e = va.Element(i);
            m.Vertices[i] = new Vtx(
                BinaryPrimitives.ReadInt16BigEndian(e), BinaryPrimitives.ReadInt16BigEndian(e[2..]), BinaryPrimitives.ReadInt16BigEndian(e[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(e[6..]), BinaryPrimitives.ReadInt16BigEndian(e[8..]), BinaryPrimitives.ReadInt16BigEndian(e[10..]),
                e[12], e[13], e[14], e[15]);
        }
        m.BoneOfVertex = new int[va.Count];
        m.Parts = new ModelPart[parts.Count];
        int cursor = 0, bone = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var pc = parts.Sub(i);
            if (pc == null || pc.Count != 2 || pc.SectionLength(0) != 24) return null;
            var part = new ModelPart { Material = ReadMaterial(pc[0].Span) };
            if (!DecodeDisplayList(pc[1].Span, part.Commands)) return null;
            foreach (var cmd in part.Commands)
            {
                if (cmd.Kind == DlKind.Matrix) bone = cmd.A;
                else if (cmd.Kind == DlKind.Vtx)
                {
                    for (int k = 0; k < cmd.B; k++)
                    {
                        if (cursor >= va.Count) return null;
                        m.BoneOfVertex[cursor++] = bone;
                    }
                }
            }
            m.Parts[i] = part;
        }
        if (cursor != va.Count) return null;
        m.OriginalVertexCount = va.Count;
        m.ExpandStOverrides();
        if (m.Skinned && CountedArray.TryParse(c[5], out var hv) && hv.ElementSize == 112)
        {
            m.HitVolumes = new HitVolume[hv.Count];
            for (int i = 0; i < hv.Count; i++)
            {
                var e = hv.Element(i);
                var h = new HitVolume();
                for (int k = 0; k < 24; k++) h.Corners[k] = BinaryPrimitives.ReadSingleBigEndian(e[(4 * k)..]);
                h.Radius = BinaryPrimitives.ReadSingleBigEndian(e[96..]);
                h.Parent = e[100]; h.Byte1 = e[101];
                h.Flags = BinaryPrimitives.ReadUInt16BigEndian(e[102..]);
                h.Scale = BinaryPrimitives.ReadSingleBigEndian(e[104..]);
                h.Tail = BinaryPrimitives.ReadUInt32BigEndian(e[108..]);
                m.HitVolumes[i] = h;
            }
        }
        return m;
    }

    static ModelMaterial ReadMaterial(ReadOnlySpan<byte> s) => new()
    {
        TextureId = BinaryPrimitives.ReadInt32BigEndian(s),
        Flags = BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
        Field8 = BinaryPrimitives.ReadInt16BigEndian(s[8..]),
        FieldA = s[10],
        Color1 = s.Slice(12, 4).ToArray(),
        Color2 = s.Slice(16, 4).ToArray(),
        ScaleS = BinaryPrimitives.ReadUInt16BigEndian(s[20..]),
        ScaleT = BinaryPrimitives.ReadUInt16BigEndian(s[22..]),
    };

    /// <summary>
    /// Decode the compact display list (verified from the expander in func_0026AD8C, which turns it into F3DEX2 commands):
    /// 0x00 terminates (the expander appends G_ENDDL).
    /// bit7 set: u16 = 1ttt tttt tttt tttt, a=bits14-10, b=bits9-5, c=bits4-0 are vertex-buffer slots of one triangle
    ///   (two consecutive triangles become one G_TRI2).
    /// 0x20|n: u16, G_VTX with count = u16 and 31 (0 means 32), first buffer slot = (u16 shr 5) and 31;
    ///   vertices are consumed sequentially from the vertex array.
    /// 0x40: 2 bytes, G_MTX of matrix index byte1 (segment 1 + index*64).
    /// 0x60|v: 5 bytes (op, r, g, b, a), G_MODIFYVTX colour of buffer slot v.
    /// </summary>
    static bool DecodeDisplayList(ReadOnlySpan<byte> s, List<DlCommand> output)
    {
        int i = 0;
        while (i < s.Length)
        {
            byte b = s[i];
            if (b == 0) return true;
            if ((b & 0x80) != 0)
            {
                if (i + 2 > s.Length) return false;
                int u = (s[i] << 8) | s[i + 1];
                output.Add(new DlCommand(DlKind.Tri, (u >> 10) & 31, (u >> 5) & 31, u & 31));
                i += 2;
                continue;
            }
            switch (b & 0x60)
            {
                case 0x20:
                {
                    if (i + 2 > s.Length) return false;
                    int u = (s[i] << 8) | s[i + 1];
                    int n = u & 31; if (n == 0) n = 32;
                    output.Add(new DlCommand(DlKind.Vtx, (u >> 5) & 31, n, 0));
                    i += 2;
                    break;
                }
                case 0x40:
                    if (i + 2 > s.Length) return false;
                    output.Add(new DlCommand(DlKind.Matrix, s[i + 1], 0, 0));
                    i += 2;
                    break;
                case 0x60:
                    if (i + 5 > s.Length) return false;
                    {
                        uint w1 = ((uint)s[i + 1] << 24) | ((uint)s[i + 2] << 16) | ((uint)s[i + 3] << 8) | s[i + 4];
                        output.Add(new DlCommand(DlKind.ModifyVtxSt, b & 31, (short)(w1 >> 16), (short)w1, w1));
                    }
                    i += 5;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Replays the display list like the RSP vertex buffer: G_VTX loads ROM vertices into slots, G_MODIFYVTX(ST) replaces S,T of a slot
    /// (the expander in func_0026AD8C emits 0x02,0x14 for the 5-byte "0x60|slot" command; the payload is s16 S, s16 T, NOT a colour).
    /// A triangle uses whatever each slot currently holds, so a vertex shared by several faces can carry different UVs: every distinct
    /// (vertex, S, T) combination becomes its own entry appended to <see cref="Vertices"/> (same position/colour).
    /// </summary>
    void ExpandStOverrides()
    {
        var verts = new List<Vtx>(Vertices);
        var bones = new List<int>(BoneOfVertex);
        var parts = new List<int>(OriginalVertexCount);
        var sources = new List<int>(Enumerable.Range(0, OriginalVertexCount));
        var slots = new int[64];
        var cache = new Dictionary<(int, float, float), int>();
        int cursor = 0;
        _tris = [];
        for (int pi = 0; pi < Parts.Length; pi++)
        {
            foreach (var cmd in Parts[pi].Commands)
            {
                if (cmd.Kind == DlKind.Vtx)
                {
                    for (int k = 0; k < cmd.B; k++) { slots[(cmd.A + k) & 63] = cursor++; parts.Add(pi); }
                }
                else if (cmd.Kind == DlKind.ModifyVtxSt)
                {
                    int cur = slots[cmd.A & 63];
                    // G_MODIFYVTX writes the vertex-buffer ST directly: it is the FINAL value (the RSP applies the G_TEXTURE scale when
                    // G_VTX loads a vertex, not afterwards). Convert back to the "pre-scale" domain used by Vtx.S/T.
                    var mat = Parts[pi].Material;
                    float ks = mat.ScaleS == 0 ? 1f : 65536f / mat.ScaleS, kt = mat.ScaleT == 0 ? 1f : 65536f / mat.ScaleT;
                    float rs = (short)cmd.B * ks, rt = (short)cmd.C * kt;
                    int src = cur < OriginalVertexCount ? cur : cacheSrc[cur];
                    if (!cache.TryGetValue((src, rs, rt), out int ni))
                    {
                        ni = verts.Count;
                        verts.Add(verts[src] with { S = rs, T = rt });
                        bones.Add(bones[src]);
                        parts.Add(pi);
                        sources.Add(src);
                        cacheSrc[ni] = src;
                        cache[(src, rs, rt)] = ni;
                    }
                    slots[cmd.A & 63] = ni;
                }
                else if (cmd.Kind == DlKind.Tri)
                    _tris.Add(new MeshTriangle { V0 = slots[cmd.A], V1 = slots[cmd.B], V2 = slots[cmd.C], Part = pi });
            }
        }
        Vertices = verts.ToArray();
        BoneOfVertex = bones.ToArray();
        PartOfVertex = parts.ToArray();
        SourceVertex = sources.ToArray();
    }
    readonly Dictionary<int, int> cacheSrc = new();

    /// <summary>All triangles with slot indices resolved to <see cref="Vertices"/> entries (including ST-override copies).</summary>
    public List<MeshTriangle> BuildTriangles() => _tris.Select(t => new MeshTriangle { V0 = t.V0, V1 = t.V1, V2 = t.V2, Part = t.Part }).ToList();
}
