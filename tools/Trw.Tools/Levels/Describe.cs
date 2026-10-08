using System.Text;

namespace Trw.Tools.Levels;

/// <summary>Generic structural dump (text) of an unpacked chunk: containers, typed arrays or raw words. Used for undecoded records.</summary>
public static class Describe
{
    public static string Tree(byte[] data, int maxDepth = 4, int maxChildren = 12, int maxRecords = 3)
    {
        var sb = new StringBuilder();
        Walk(sb, data, 0, maxDepth, maxChildren, maxRecords, "");
        return sb.ToString();
    }

    static void Walk(StringBuilder sb, byte[] d, int depth, int maxDepth, int maxChildren, int maxRecords, string label)
    {
        string pad = new(' ', depth * 2);
        if (depth < maxDepth && Container.Sections(d) is { } secs)
        {
            sb.Append(pad).Append(label).Append($"container({secs.Length}) {d.Length} bytes\n");
            for (int i = 0; i < secs.Length && i < maxChildren; i++)
                Walk(sb, secs[i], depth + 1, maxDepth, maxChildren, maxRecords, $"[{i}] ");
            if (secs.Length > maxChildren) sb.Append(pad).Append($"  ... {secs.Length - maxChildren} more\n");
            return;
        }
        if (TypedArray.TryParse(d, out var a) && (a.Count > 0 || d.Length == 8))
        {
            sb.Append(pad).Append(label).Append($"array stride={a.Stride} count={a.Count} ({d.Length} bytes)\n");
            for (int i = 0; i < a.Count && i < maxRecords; i++)
                sb.Append(pad).Append($"  #{i}: {Bin.Words(a.Record(i))}\n");
            if (a.Count > maxRecords) sb.Append(pad).Append($"  ... {a.Count - maxRecords} more\n");
            return;
        }
        sb.Append(pad).Append(label).Append($"raw {d.Length} bytes: {Bin.Words(d, 24)}\n");
    }
}
