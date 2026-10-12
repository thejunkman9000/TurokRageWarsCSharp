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

    /// <summary>The last rate the game gave osAiSetFrequency (0 = not set yet): the game sets it during boot, before the window installs its sound output.</summary>
    public static uint AudioFrequency;

    /// <summary>Audio interface: a buffer of big-endian 16-bit stereo samples was queued.</summary>
    public static Action<byte[], int>? AudioPush;

    /// <summary>The window's sound output: true while its ring is below the prebuffer, so the AI FIFO reports "not full" even when the clock model says full
    /// (null = no live output). It can only ask for more audio, never hold the game back: the AI interrupts are posted from the clock model.</summary>
    public static Func<bool>? AudioLow;

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
        Oracle.Deterministic ? DeterministicCounter() :
        (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (46875000.0 / System.Diagnostics.Stopwatch.Frequency));

    // TRW_DETERMINISTIC=1: the clock is derived from the retrace tick (60 Hz = 781250 counts per tick) plus one count per query within the tick,
    // so busy-wait loops on osGetCount still terminate and every run sees the same value at the same point of the same thread.
    private static long detTick, detCalls;
    public static void DetAdvance(int tick) { detTick = tick; detCalls = 0; }
    private static ulong DeterministicCounter() => (ulong)detTick * 781250UL + (ulong)Math.Min(++detCalls * 16, 781249);
}
