namespace Trw.Game;

/// <summary>The kinds of input device the button prompts can be drawn for (Hud: the on-screen button glyphs follow the device in use).</summary>
public enum InputDeviceKind { KeyboardMouse, Xbox, PlayStation, N64, Touch }

/// <summary>
/// Which input device the player is using right now: the last one that produced input. Input code reports every device that is actively used
/// (a key or mouse button, a gamepad button or stick past the dead zone, a touch); the button glyphs read <see cref="Device"/>.
/// <c>TRW_GLYPHS=keyboard|xbox|playstation|n64|touch</c> pins the glyph set (screenshots, tests).
/// </summary>
public static class ActiveInput
{
    public static readonly InputDeviceKind? Forced = (Environment.GetEnvironmentVariable("TRW_GLYPHS") ?? "").ToLowerInvariant() switch
    {
        "keyboard" or "kbm" => InputDeviceKind.KeyboardMouse,
        "xbox" => InputDeviceKind.Xbox,
        "playstation" or "ps" => InputDeviceKind.PlayStation,
        "n64" => InputDeviceKind.N64,
        "touch" => InputDeviceKind.Touch,
        _ => null,
    };

    private static volatile InputDeviceKind last = OperatingSystem.IsAndroid() ? InputDeviceKind.Touch : InputDeviceKind.KeyboardMouse;

    /// <summary>The device the prompts are drawn for.</summary>
    public static InputDeviceKind Device => Forced ?? last;

    /// <summary>Raised (on the reporting thread) when <see cref="Device"/> changes.</summary>
    public static event Action<InputDeviceKind>? Changed;

    /// <summary>Called by input code whenever <paramref name="kind"/> produced input this frame.</summary>
    public static void Report(InputDeviceKind kind)
    {
        if (kind == last) return;
        last = kind;
        Changed?.Invoke(Device);
    }
}
