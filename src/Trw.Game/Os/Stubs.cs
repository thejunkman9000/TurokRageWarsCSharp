using Trw.Platform;

namespace Trw.Game;

/// <summary>Functions that exist in the original build but are not translated yet (the C port stubs them too): calls are logged once and return 0.</summary>
public static partial class Game
{
    private static readonly HashSet<string> stubbed = new();

    /// <summary>Diagnostics hook of the C port's RNC decompressor; nothing to do here.</summary>
    private static readonly bool rncLog = Environment.GetEnvironmentVariable("TRW_RNC_LOG") != null;
    /// <summary>RNC decompressor diagnostics (the C port's hook): TRW_RNC_LOG=1 prints every unpack and any overrun / size / CRC problem.</summary>
    public static void port_rnc_log(uint what, uint src, uint dst, int packed, int unpacked, int produced)
    {
        if (!rncLog) return;
        var w = Mem.CString(what);
        if (w == "rnc1 in" ) { Console.Error.WriteLine($"[rnc] unpack {packed} -> {unpacked} bytes src={src:X8} dst={dst:X8}"); return; }
        Console.Error.WriteLine($"[rnc] {w}: packed {packed} unpacked {unpacked} produced {produced:X} src={src:X8} dst={dst:X8}");
    }

    public static void Stub(string name)
    {
        lock (stubbed)
            if (stubbed.Add(name)) Console.Error.WriteLine($"UNIMPLEMENTED: {name}");
    }
}
