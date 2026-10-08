namespace Trw.Tools.Levels;

public enum NodeKind { Container, Rnc, Raw }

/// <summary>One node of the ROM resource tree: an uncompressed container, an RNC-compressed leaf, or a raw leaf.</summary>
public sealed class RomNode
{
    public int[] Path = [];
    public int Offset;
    public int Size;
    public NodeKind Kind;
    public List<RomNode> Children = new();
    public string PathString => Path.Length == 0 ? "/" : string.Join('.', Path);
    public override string ToString() => $"{PathString} 0x{Offset:X6} {Size} {Kind}";
}

/// <summary>
/// The game's ROM resource index (all verified against the game code and by walking the ROM; see docs/formats/levels_records.md).
/// ROMLookupTable at ROM 0x28B244 (8 words) gives four regions [start,end): audio ctl bank, audio sample data, a small
/// container, and the main data archive at 0x50F768 (24 sections) in which every leaf is RNC compressed.
/// </summary>
public sealed class RomIndex
{
    public const int LookupTableOffset = 0x28B244;
    public const int ArchiveOffset = 0x50F768;

    public static readonly string[] RegionNames = ["audio ctl bank", "audio sample data", "audio/sequence container", "main data archive"];

    public byte[] Rom { get; }
    public (int Start, int End)[] Regions { get; }
    public RomNode Root { get; }

    RomIndex(byte[] rom, (int, int)[] regions, RomNode root) { Rom = rom; Regions = regions; Root = root; }

    public static RomIndex Load(byte[] rom)
    {
        // word i of the table is the start of region i/2's pair; regions are [w0,w1) [w2,w3) [w4,w5) [w6,w7)
        // in practice w1==w2, w3==w4, w5==w6 (see func_00265984: ROMLookupTable[idx]).
        var w = new int[8];
        for (int i = 0; i < 8; i++) w[i] = (int)Bin.U32(rom, LookupTableOffset + 4 * i);
        var regions = new (int, int)[]
        {
            (w[0], w[1]), (w[2], w[3]), (w[4], w[5]), (w[6], w[7]),
        };
        int start = regions[3].Item1, end = regions[3].Item2;
        var root = Walk(rom, start, end - start, []);
        return new RomIndex(rom, regions, root);
    }

    static RomNode Walk(byte[] rom, int off, int size, int[] path)
    {
        var n = new RomNode { Path = path, Offset = off, Size = size };
        if (size >= 3 && rom[off] == (byte)'R' && rom[off + 1] == (byte)'N' && rom[off + 2] == (byte)'C')
        {
            n.Kind = NodeKind.Rnc;
            return n;
        }
        if (Container.TryParse(rom.AsSpan(off, size), out var offs))
        {
            n.Kind = NodeKind.Container;
            for (int i = 0; i < offs.Length - 1; i++)
                n.Children.Add(Walk(rom, off + offs[i], offs[i + 1] - offs[i], [.. path, i]));
            return n;
        }
        n.Kind = NodeKind.Raw;
        return n;
    }

    public RomNode? Find(params int[] path)
    {
        var n = Root;
        foreach (int i in path)
        {
            if (i < 0 || i >= n.Children.Count) return null;
            n = n.Children[i];
        }
        return n;
    }

    public IEnumerable<RomNode> All()
    {
        var stack = new Stack<RomNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    /// <summary>Leaf bytes: unpacked for RNC leaves, the raw ROM slice otherwise (containers return their raw bytes too).</summary>
    public byte[] Read(RomNode n) => n.Kind == NodeKind.Rnc
        ? Rnc.Unpack(Rom, n.Offset)
        : Rom.AsSpan(n.Offset, n.Size).ToArray();

    public byte[] Read(params int[] path) => Read(Find(path) ?? throw new ArgumentException("no such node " + string.Join('.', path)));
}
