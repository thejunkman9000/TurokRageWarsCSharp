using Microsoft.Xna.Framework.Input;

namespace Trw.Game;

/// <summary>
/// Desktop side of <see cref="ActiveInput"/>: tells it which device the player just used, so the button glyphs (docs/BUTTON_GLYPHS.md) follow the
/// device in hand. The keyboard / mouse counts when a key or mouse button is down, the wheel turns or the mouse moves; a gamepad when a button is down
/// or a stick / trigger is clearly off its rest. A gamepad is told apart by the name SDL gives it (<see cref="GamePadCapabilities.DisplayName"/> /
/// <see cref="GamePadCapabilities.Identifier"/>): Sony pads (DualShock, DualSense, "PS4 Controller", "Wireless Controller") -> PlayStation glyphs,
/// N64 pads and adapters (Nintendo Switch Online N64, Mayflash / Retrolink / raphnet / Tomee N64 adapters) -> the N64's own buttons, anything else -> Xbox.
/// <c>TRW_PAD_KIND=xbox|playstation|n64</c> overrides the guess for every pad.
/// </summary>
public static class DeviceReport
{
    private static readonly InputDeviceKind?[] kinds = new InputDeviceKind?[4];
    private static readonly bool[] wasConnected = new bool[4];
    private static readonly InputDeviceKind? forcedPad = (Environment.GetEnvironmentVariable("TRW_PAD_KIND") ?? "").ToLowerInvariant() switch
    {
        "xbox" => InputDeviceKind.Xbox, "playstation" or "ps" => InputDeviceKind.PlayStation, "n64" => InputDeviceKind.N64, _ => null,
    };

    /// <summary>The glyph set for a gamepad of this name / SDL identifier.</summary>
    public static InputDeviceKind Classify(string? name, string? id)
    {
        string s = ((name ?? "") + " " + (id ?? "")).ToLowerInvariant();
        if (s.Contains("n64") || s.Contains("nintendo 64") || s.Contains("retrolink") || s.Contains("raphnet") || s.Contains("mayflash n64") || s.Contains("tomee"))
            return InputDeviceKind.N64;
        if (s.Contains("dualshock") || s.Contains("dualsense") || s.Contains("playstation") || s.Contains("ps3") || s.Contains("ps4") || s.Contains("ps5")
            || s.Contains("wireless controller") || s.Contains("sony") || s.Contains("054c"))      // 054c: Sony's USB vendor id (SDL GUIDs carry it)
            return InputDeviceKind.PlayStation;
        return InputDeviceKind.Xbox;
    }

    /// <summary>The glyph set of pad <paramref name="i"/> (asked once per connection).</summary>
    public static InputDeviceKind PadKind(int i, bool connected)
    {
        i &= 3;
        if (!connected) { wasConnected[i] = false; kinds[i] = null; return InputDeviceKind.Xbox; }
        if (forcedPad is { } f) return f;
        if (!wasConnected[i] || kinds[i] == null)
        {
            wasConnected[i] = true;
            try
            {
                var caps = GamePad.GetCapabilities(i);
                kinds[i] = Classify(caps.DisplayName, caps.Identifier);
                Console.Error.WriteLine($"[input] pad {i + 1}: \"{caps.DisplayName}\" ({caps.Identifier}) -> {kinds[i]} glyphs");
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { kinds[i] = InputDeviceKind.Xbox; }
        }
        return kinds[i] ?? InputDeviceKind.Xbox;
    }

    /// <summary>A gamepad poll: reports the pad when it is in use.</summary>
    public static void Pad(int i, in GamePadState gp)
    {
        if (!gp.IsConnected) { PadKind(i, false); return; }
        const float dz = 0.35f;
        var b = gp.Buttons; var d = gp.DPad; var t = gp.ThumbSticks;
        bool used = b.A == ButtonState.Pressed || b.B == ButtonState.Pressed || b.X == ButtonState.Pressed || b.Y == ButtonState.Pressed
            || b.LeftShoulder == ButtonState.Pressed || b.RightShoulder == ButtonState.Pressed || b.Start == ButtonState.Pressed || b.Back == ButtonState.Pressed
            || b.LeftStick == ButtonState.Pressed || b.RightStick == ButtonState.Pressed
            || d.Up == ButtonState.Pressed || d.Down == ButtonState.Pressed || d.Left == ButtonState.Pressed || d.Right == ButtonState.Pressed
            || t.Left.Length() > dz || t.Right.Length() > dz || gp.Triggers.Left > dz || gp.Triggers.Right > dz;
        if (used) ActiveInput.Report(PadKind(i, true));
    }

    /// <summary>A keyboard / mouse sample of the focused window: reports the keyboard + mouse when either is in use.</summary>
    public static void KeyboardMouse(in KeyboardState ks, bool anyMouseButton, bool wheel, float moved)
    {
        if (ks.GetPressedKeyCount() > 0 || anyMouseButton || wheel || moved > 6f) ActiveInput.Report(InputDeviceKind.KeyboardMouse);
    }
}
