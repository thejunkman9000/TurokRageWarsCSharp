namespace Trw.Tools.Levels;

// Level records. Archive section 5 is the level directory: 37 levels, each a 13-part container (parts 0..12).
// How each part is consumed was read from CScene load code (func_00286A78 = "load level"; see docs/formats/levels_records.md).

public readonly record struct NavNode(int Index, float X, float Y, float Z, ushort Flags, ushort ObjectType);
/// <summary>Part 3 record (stride 0x28, stored column-major). Hypothesis: a placed actor/pickup instance. Verified: position floats, scale 0.5.</summary>
public readonly record struct ActorPlacement(int Index, uint Word0, float X, float Y, float Z, float ScaleX, float ScaleY, float ScaleZ, uint Word1C, uint Word20, uint Word24);
public readonly record struct NavEdge(int Index, int From, int To, byte Type, byte Extra);
public readonly record struct GridCell(int Column, int Row, float XMin, float ZMin, float XMax, float ZMax, uint Flag)
{
    /// <summary>cell flag 0 = a sector chunk is loaded for this cell (func_00288908: only cells with flag==0 are loaded).</summary>
    public bool HasSector => Flag == 0;
}
public sealed record LevelGrid(int Columns, int Rows, float[] ColXMin, float[] ColXMax, float[] RowZMin, float[] RowZMax, GridCell[] Cells);

/// <summary>A sector ("grid section") chunk: five sections; arrays 0..3 are stored column-major (func_0028795C / func_0028FEEC).</summary>
public sealed class Sector
{
    public int Index;
    public RomNode Node = null!;
    public TypedArray StaticPlacements;   // section 0: stride 0x1C, u16 objectId at +8 (index into archive section 0)
    public TypedArray Placements34;       // section 1: stride 0x34, u16 objectId at +0x28, position floats at +4..
    public TypedArray Records20;          // section 2: stride 0x20 (usually empty), objectId at +0x14
    public TypedArray Records4C;          // section 3: stride 0x4C (converted to 0xC0-byte runtime records by func_00276D20)
    public byte[] Section4 = [];          // section 4: copied verbatim
}

public sealed class Level
{
    public int Id;
    public string Name = "";
    public RomNode Node = null!;
    public RomIndex Index = null!;

    public RomNode Part(int i) => Node.Children[i];
    public byte[] ReadPart(int i) => Index.Read(Part(i));

    /// <summary>Part 0, raw 24 bytes (not compressed). Field meanings are hypotheses.</summary>
    public byte[] Params24() => ReadPart(0);

    /// <summary>Part 9: typed array stride 16: f32 x,y,z, u16 flags, u16 objectType. Verified layout (x,y,z in level bounds).</summary>
    public NavNode[] NavNodes()
    {
        var d = ReadPart(9);
        if (!TypedArray.TryParse(d, out var a) || a.Stride != 16) return [];
        var r = new NavNode[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            int o = i * 16;
            r[i] = new NavNode(i, Bin.F32(a.Data, o), Bin.F32(a.Data, o + 4), Bin.F32(a.Data, o + 8),
                Bin.U16(a.Data, o + 12), Bin.U16(a.Data, o + 14));
        }
        return r;
    }

    /// <summary>Part 10: typed array stride 6: u16 from, u16 to (node indices), u8 type, u8 0. Verified: indices &lt; node count.</summary>
    public NavEdge[] NavEdges()
    {
        var d = ReadPart(10);
        if (!TypedArray.TryParse(d, out var a) || a.Stride != 6) return [];
        var r = new NavEdge[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            int o = i * 6;
            r[i] = new NavEdge(i, Bin.U16(a.Data, o), Bin.U16(a.Data, o + 2), a.Data[o + 4], a.Data[o + 5]);
        }
        return r;
    }

    /// <summary>Part 11: stride 1, count = nodeCount^2: row-major N x N byte matrix (0/1), verified size for all levels.</summary>
    public byte[] NavMatrix(out int n)
    {
        n = 0;
        var d = ReadPart(11);
        if (!TypedArray.TryParse(d, out var a) || a.Stride != 1) return [];
        n = (int)Math.Round(Math.Sqrt(a.Count));
        return a.Data;
    }

    /// <summary>Part 4: sector grid description (verified against func_00288908): 5 sections.</summary>
    public LevelGrid? Grid()
    {
        var s = Container.Sections(ReadPart(4));
        if (s == null || s.Length != 5) return null;
        if (!TypedArray.TryParse(s[0], out var cx0) || !TypedArray.TryParse(s[1], out var cx1) ||
            !TypedArray.TryParse(s[2], out var rz0) || !TypedArray.TryParse(s[3], out var rz1) ||
            !TypedArray.TryParse(s[4], out var cells)) return null;
        float[] F(TypedArray a) { var f = new float[a.Count]; for (int i = 0; i < f.Length; i++) f[i] = Bin.F32(a.Data, i * 4); return f; }
        int cols = cx0.Count, rows = rz0.Count;
        var c = new GridCell[cells.Count];
        for (int i = 0; i < c.Length; i++)
        {
            int o = i * cells.Stride;
            c[i] = new GridCell(cols == 0 ? 0 : i % cols, cols == 0 ? 0 : i / cols,
                Bin.F32(cells.Data, o), Bin.F32(cells.Data, o + 4), Bin.F32(cells.Data, o + 8), Bin.F32(cells.Data, o + 12),
                Bin.U32(cells.Data, o + 16));
        }
        return new LevelGrid(cols, rows, F(cx0), F(cx1), F(rz0), F(rz1), c);
    }

    /// <summary>Part 5: the sector chunks (one RNC container per grid cell with flag 0).</summary>
    public IEnumerable<Sector> Sectors()
    {
        var p5 = Part(5);
        for (int i = 0; i < p5.Children.Count; i++)
        {
            var node = p5.Children[i];
            var s = Container.Sections(Index.Read(node));
            if (s == null || s.Length != 5) continue;
            TypedArray T(byte[] b) => TypedArray.TryParse(b, out var a) ? a.Untransposed() : default;
            yield return new Sector
            {
                Index = i, Node = node,
                StaticPlacements = T(s[0]), Placements34 = T(s[1]), Records20 = T(s[2]), Records4C = T(s[3]),
                Section4 = s[4],
            };
        }
    }

    /// <summary>Part 3 (callback D_44D408): stride-0x28 array, stored column-major; untransposed here.</summary>
    public ActorPlacement[] Actors()
    {
        if (!TypedArray.TryParse(ReadPart(3), out var t) || t.Stride != 0x28) return [];
        var a = t.Untransposed();
        var r = new ActorPlacement[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            int o = i * 0x28;
            r[i] = new ActorPlacement(i, Bin.U32(a.Data, o), Bin.F32(a.Data, o + 4), Bin.F32(a.Data, o + 8), Bin.F32(a.Data, o + 12),
                Bin.F32(a.Data, o + 16), Bin.F32(a.Data, o + 20), Bin.F32(a.Data, o + 24),
                Bin.U32(a.Data, o + 28), Bin.U32(a.Data, o + 32), Bin.U32(a.Data, o + 36));
        }
        return r;
    }

    /// <summary>Part 6: typed array stride 44, 16 records (hypothesis: ambient/light-source parameter sets).</summary>
    public TypedArray Part6() => TypedArray.TryParse(ReadPart(6), out var a) ? a : default;
}

/// <summary>Per-level metadata record from archive section 18 item 0 (typed array stride 76, 37 records), hypothesis on most fields.</summary>
public sealed record LevelMeta(int Slot, uint LevelId, uint Field4, uint Field8, uint FieldC, string MapName, float Float38);

public static class LevelCatalog
{
    public const int LevelDirectorySection = 5;

    public static List<Level> Load(RomIndex ix)
    {
        var dir = ix.Find(LevelDirectorySection) ?? throw new InvalidDataException("archive section 5 missing");
        var list = new List<Level>();
        for (int i = 0; i < dir.Children.Count; i++)
        {
            var n = dir.Children[i];
            var lv = new Level { Id = i, Node = n, Index = ix };
            if (n.Kind == NodeKind.Container && n.Children.Count >= 9 && n.Children[8].Kind == NodeKind.Raw)
                lv.Name = Bin.CString(ix.Rom.AsSpan(n.Children[8].Offset, n.Children[8].Size));
            list.Add(lv);
        }
        return list;
    }

    public static List<LevelMeta> LoadMeta(RomIndex ix)
    {
        var res = new List<LevelMeta>();
        var n = ix.Find(18, 0);
        if (n == null) return res;
        var d = ix.Read(n);
        if (!TypedArray.TryParse(d, out var a) || a.Stride != 76) return res;
        for (int i = 0; i < a.Count; i++)
        {
            var r = a.Data.AsSpan(i * 76, 76);
            res.Add(new LevelMeta(i, Bin.U32(r, 0), Bin.U32(r, 4), Bin.U32(r, 8), Bin.U32(r, 12),
                Bin.CString(r[24..56]), Bin.F32(r, 60)));
        }
        return res;
    }
}
