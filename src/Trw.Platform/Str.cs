using System.Text;

namespace Trw.Platform;

/// <summary>Guest copies of C string literals that appear in the translated code (the originals live in ROM .rodata at unknown addresses).</summary>
public static class Str
{
    public const uint PoolBase = 0x7E000000;
    private const int PoolSize = 0x100000;
    private static readonly Dictionary<string, uint> interned = new();
    private static uint next = PoolBase;

    public static void Init() { if (!Mem.IsMapped(PoolBase)) Mem.Map(PoolBase, PoolSize); }

    public static uint Lit(string s)
    {
        lock (interned)
        {
            if (interned.TryGetValue(s, out var a)) return a;
            var bytes = Encoding.Latin1.GetBytes(s);
            if (next + bytes.Length + 1 > PoolBase + PoolSize) throw new InvalidOperationException("string pool full");
            a = next;
            Mem.Write(a, bytes);
            Mem.Set(a + (uint)bytes.Length, (byte)0);
            next += (uint)((bytes.Length + 1 + 3) & ~3);
            interned[s] = a;
            return a;
        }
    }
}
