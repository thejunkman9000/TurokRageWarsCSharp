namespace Trw.Game.Glyphs;

/// <summary>
/// Which glyph shows an N64 button for a device: the SVG name under <c>Content/Glyphs</c> (and, for a keyboard key, the label printed on the keycap).
/// Keyboard + mouse and the gamepads go through the port's bindings (<see cref="Bindings"/>): the glyph is the key / pad control the player presses for
/// that N64 button. The N64 set shows the original buttons; the touch set the on-screen control of that N64 button.
/// </summary>
public static class GlyphSet
{
    /// <summary>A glyph to draw: the SVG and the keycap label (keyboard keys only).</summary>
    public readonly record struct Glyph(string Svg, string? Label);

    public static string Folder(InputDeviceKind k) => k switch
    {
        InputDeviceKind.KeyboardMouse => "keyboard", InputDeviceKind.Xbox => "xbox", InputDeviceKind.PlayStation => "playstation",
        InputDeviceKind.N64 => "n64", _ => "touch",
    };

    /// <summary>The glyph of <paramref name="b"/> for <paramref name="kind"/> in the menus or in free play (<paramref name="gameplay"/>).</summary>
    public static Glyph Resolve(N64Button b, InputDeviceKind kind, bool gameplay)
    {
        if (kind is InputDeviceKind.N64 or InputDeviceKind.Touch) return new(Folder(kind) + "/" + N64Name(b), null);
        if (b == N64Button.Stick)
        {   // the whole stick: the control that moves it right
            var r = Bindings.Best(N64Button.StickRight, kind, gameplay);
            if (kind == InputDeviceKind.KeyboardMouse) return new("keyboard/arrows", null);
            if (r is { } p && p.Name.Length > 2) return new(Folder(kind) + "/" + p.Name[..2].ToLowerInvariant(), null);
            return new(Folder(kind) + "/ls", null);
        }
        var input = Bindings.Best(b, kind, gameplay);
        if (input is not { } pi) return new("n64/" + N64Name(b), null);      // the device cannot press it: the N64 button itself
        return kind == InputDeviceKind.KeyboardMouse ? Keyboard(pi) : Pad(pi, kind);
    }

    private static Glyph Keyboard(PhysicalInput p) => p.Source switch
    {
        InputSource.Mouse => new("keyboard/mouse_" + p.Name.ToLowerInvariant(), null),
        InputSource.Wheel => new("keyboard/mouse_wheel_" + p.Name.ToLowerInvariant(), null),
        _ => p.Name switch
        {
            "Up" or "Down" or "Left" or "Right" => new("keyboard/key_" + p.Name.ToLowerInvariant(), null),
            _ => new("keyboard/keycap", KeyLabel(p.Name)),
        },
    };

    private static Glyph Pad(PhysicalInput p, InputDeviceKind kind)
    {
        string n = p.Name;
        string file = n switch
        {
            "DUp" => "dpad_up", "DDown" => "dpad_down", "DLeft" => "dpad_left", "DRight" => "dpad_right",
            _ when n.StartsWith("LS") || n.StartsWith("RS") => n[..2].ToLowerInvariant() + "_" + n[2..].ToLowerInvariant(),
            _ => n.ToLowerInvariant(),
        };
        if (kind == InputDeviceKind.PlayStation)
            file = file switch
            {
                "a" => "cross", "b" => "circle", "x" => "square", "y" => "triangle", "lb" => "l1", "rb" => "r1", "lt" => "l2", "rt" => "r2",
                "start" => "options", "back" => "share", _ => file,
            };
        return new(Folder(kind) + "/" + file, null);
    }

    /// <summary>The SVG name of an N64 button in the n64 / touch sets.</summary>
    public static string N64Name(N64Button b) => b switch
    {
        N64Button.CUp => "c_up", N64Button.CDown => "c_down", N64Button.CLeft => "c_left", N64Button.CRight => "c_right",
        N64Button.DUp => "dpad_up", N64Button.DDown => "dpad_down", N64Button.DLeft => "dpad_left", N64Button.DRight => "dpad_right",
        N64Button.StickUp => "stick_up", N64Button.StickDown => "stick_down", N64Button.StickLeft => "stick_left", N64Button.StickRight => "stick_right",
        _ => b.ToString().ToLowerInvariant(),
    };

    /// <summary>The text on a keycap for a MonoGame key name.</summary>
    public static string KeyLabel(string key) => key switch
    {
        "Space" => "SPACE", "Enter" => "ENTER", "Escape" => "ESC", "Back" => "BKSP", "Tab" => "TAB", "Delete" => "DEL", "Insert" => "INS",
        "LeftControl" or "RightControl" => "CTRL", "LeftShift" or "RightShift" => "SHIFT", "LeftAlt" or "RightAlt" => "ALT",
        "PageUp" => "PG UP", "PageDown" => "PG DN", "Home" => "HOME", "End" => "END", "CapsLock" => "CAPS",
        "OemMinus" => "-", "OemPlus" => "=", "OemComma" => ",", "OemPeriod" => ".", "OemQuestion" => "/", "OemSemicolon" => ";", "OemQuotes" => "'",
        "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", "OemPipe" => "\\", "OemTilde" => "`",
        "Multiply" => "*", "Add" => "+", "Subtract" => "-", "Divide" => "/", "Decimal" => ".",
        _ when key.StartsWith("NumPad") => "NUM " + key[6..],
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ => key.ToUpperInvariant(),
    };
}
