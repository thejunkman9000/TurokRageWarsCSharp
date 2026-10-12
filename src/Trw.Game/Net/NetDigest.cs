using Trw.Platform;

namespace Trw.Game.Net;

/// <summary>
/// The RAM digest the peers compare to detect a desync: the oracle's game-state regions (<see cref="Oracle.DigestRegions"/>, one hash each, every
/// digest) plus one 256 KB slice of the 8 MB RAM (the n-th digest of a session hashes slice n mod 32, so all of RAM is covered every 32 digests). Taken right after a tick, while
/// every game thread is quiescent, so it is a pure function of the inputs (docs/ORACLE.md). About 0.1 ms.
/// </summary>
public static class NetDigest
{
    public const int Slices = 32;
    public const int SliceBytes = 0x800000 / Slices;
    public static int Count => Oracle.DigestRegions.Count + 1;
    private static readonly byte[] buf = new byte[SliceBytes];

    public static ulong[] Take(int sample)
    {
        var regions = Oracle.DigestRegions;
        var d = new ulong[regions.Count + 1];
        for (int i = 0; i < regions.Count; i++)
        {
            var (a, n) = regions[i];
            Mem.Read(a, buf.AsSpan(0, n));
            d[i] = Oracle.HashBlock(buf.AsSpan(0, n));
        }
        Mem.Read(SliceAddress(sample), buf);
        d[regions.Count] = Oracle.HashBlock(buf);
        return d;
    }

    public static uint SliceAddress(int sample) => 0x80000000u + (uint)((sample % Slices) * SliceBytes);

    /// <summary>What differs between two digests of the same sample, in words; null when they are equal.</summary>
    public static string? Describe(int sample, ulong[] a, ulong[] b)
    {
        var regions = Oracle.DigestRegions;
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] == b[i]) continue;
            if (i < regions.Count) parts.Add($"game-state region {regions[i].addr:X8}+{regions[i].len:X}");
            else parts.Add($"RAM {SliceAddress(sample):X8}-{SliceAddress(sample) + SliceBytes - 1:X8} (this slice was last compared {Slices} digests earlier)");
        }
        if (a.Length != b.Length) parts.Add("digest size");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
