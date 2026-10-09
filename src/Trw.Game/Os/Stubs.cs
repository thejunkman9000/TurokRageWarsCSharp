namespace Trw.Game;

/// <summary>Functions that exist in the original build but are not translated yet (the C port stubs them too): calls are logged once and return 0.</summary>
public static partial class Game
{
    private static readonly HashSet<string> stubbed = new();

    /// <summary>Diagnostics hook of the C port's RNC decompressor; nothing to do here.</summary>
    public static void port_rnc_log(uint what, uint src, uint dst, int packed, int unpacked, int produced) { }

    public static void Stub(string name)
    {
        lock (stubbed)
            if (stubbed.Add(name)) Console.Error.WriteLine($"UNIMPLEMENTED: {name}");
    }
}
