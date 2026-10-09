namespace Trw.Platform;

/// <summary>
/// What the game runtime needs from the machine it runs on: the cartridge image and the devices behind the console's
/// interfaces. The MonoGame front end fills these in; headless tests can leave the defaults.
/// </summary>
public static class Host
{
    /// <summary>The cartridge image, big-endian (.z64) byte order.</summary>
    public static byte[] Rom = Array.Empty<byte>();

    /// <summary>Audio interface: sample rate changed.</summary>
    public static Action<uint>? AudioSetFrequency;

    /// <summary>Audio interface: a buffer of big-endian 16-bit stereo samples was queued.</summary>
    public static Action<byte[], int>? AudioPush;

    /// <summary>An RSP audio task was started (guest address and size of its command list).</summary>
    public static Action<uint, uint>? AudioTask;

    /// <summary>A graphics task was started (guest address of the OSTask); the renderer consumes the display list.</summary>
    public static Action<uint>? GraphicsTask;

    /// <summary>Controller state for pad 0-3: buttons (N64 bit layout), stick x/y (-80..80); null when no pad is connected.</summary>
    public delegate bool PadReader(int pad, out ushort buttons, out sbyte stickX, out sbyte stickY);
    public static PadReader? ReadPad;

    /// <summary>High-resolution clock in CP0 counter ticks (46.875 MHz).</summary>
    public static Func<ulong> Counter = DefaultCounter;

    private static ulong DefaultCounter() =>
        (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (46875000.0 / System.Diagnostics.Stopwatch.Frequency));
}
