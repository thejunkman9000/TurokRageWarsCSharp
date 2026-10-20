using Microsoft.Xna.Framework.Input;
using Trw.Platform;

namespace Trw.Game.Glyphs;

/// <summary>A button (or the stick) of the N64 controller, as the game's prompts name it.</summary>
public enum N64Button { A, B, Z, Start, L, R, CUp, CDown, CLeft, CRight, DUp, DDown, DLeft, DRight, Stick, StickUp, StickDown, StickLeft, StickRight }

/// <summary>What a physical input is: a keyboard key, a mouse button / wheel notch, or a gamepad control (XInput layout names).</summary>
public enum InputSource { Key, Mouse, Wheel, Pad }

/// <summary>
/// One physical input. <see cref="Name"/>: a MonoGame <see cref="Keys"/> name ("Space", "E", "Enter"), a mouse button ("Left", "Right", "Middle", "X1",
/// "X2"), a wheel direction ("Up", "Down"), or a pad control in the XInput layout ("A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Start", "Back", "DUp" ...,
/// "LSUp" ..., "RSUp" ...).
/// </summary>
public readonly record struct PhysicalInput(InputSource Source, string Name)
{
    public override string ToString() => $"{Source}:{Name}";
}

/// <summary>
/// Answers "which key / mouse button / pad button does the player press for N64 button X?" by asking the port's own input mapping
/// (<see cref="InputMapper"/>, Trw.Platform/InputModel.cs): every candidate input is fed alone, through a private mapper, in the menu or the gameplay
/// context, and the N64 buttons (and stick directions) it produces are recorded. So the prompts follow the mapping as it is, including any later change
/// to it (a controls file), without a second copy of the bindings that could drift. Presentation only: the game's mappers are not touched.
/// </summary>
public static class Bindings
{
    private static readonly string[] padControls =
        { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Start", "Back", "DUp", "DDown", "DLeft", "DRight", "LSUp", "LSDown", "LSLeft", "LSRight", "RSUp", "RSDown", "RSLeft", "RSRight" };
    private static readonly string[] mouseButtons = { "Left", "Right", "Middle", "X1", "X2" };

    // keys a prompt prefers when several bind the same button: the main keys of the layout (E use, Space, Enter), then single letters, then the rest,
    // the numeric pad last
    private static int KeyRank(string k) => k is "E" or "Space" or "Enter" ? 0 : k.Length == 1 ? 1 : k.StartsWith("NumPad") ? 3 : 2;

    private sealed class Table { public readonly Dictionary<N64Button, List<PhysicalInput>> Map = new(); }
    private static Table? menuTable, gameTable;

    /// <summary>Clears the cached tables (call after the mapping changed).</summary>
    public static void Invalidate() { menuTable = gameTable = null; }

    /// <summary>The inputs bound to <paramref name="b"/> in the menus (<paramref name="gameplay"/> false) or in free play, best first. Empty when unbound.</summary>
    public static IReadOnlyList<PhysicalInput> For(N64Button b, bool gameplay)
    {
        var t = gameplay ? (gameTable ??= Build(1)) : (menuTable ??= Build(0));
        return t.Map.TryGetValue(b, out var l) ? l : Array.Empty<PhysicalInput>();
    }

    /// <summary>The best input of <paramref name="kind"/> for <paramref name="b"/>: the current context first, else the other one (a prompt still shows
    /// something meaningful for a button the context does not bind). Null when the device cannot press it at all.</summary>
    public static PhysicalInput? Best(N64Button b, InputDeviceKind kind, bool gameplay)
    {
        foreach (var ctx in new[] { gameplay, !gameplay })
            foreach (var p in For(b, ctx))
                if (kind == InputDeviceKind.KeyboardMouse ? p.Source != InputSource.Pad : p.Source == InputSource.Pad) return p;
        return null;
    }

    private static Table Build(int gameplay)
    {
        var t = new Table();
        void Add(PhysicalInput p, ushort buttons, int x, int y)
        {
            for (int bit = 0; bit < 16; bit++)
                if ((buttons & (1 << bit)) != 0 && FromBit((ushort)(1 << bit)) is { } nb) Put(nb, p);
            if (x < 0) Put(N64Button.StickLeft, p); if (x > 0) Put(N64Button.StickRight, p);
            if (y > 0) Put(N64Button.StickUp, p); if (y < 0) Put(N64Button.StickDown, p);
        }
        void Put(N64Button b, PhysicalInput p) { if (!t.Map.TryGetValue(b, out var l)) t.Map[b] = l = new(); if (!l.Contains(p)) l.Add(p); }

        foreach (var k in Enum.GetNames<Keys>().Distinct().OrderBy(KeyRank))
        {
            var r = new InputMapper().Evaluate(new InputSample { Gameplay = gameplay, KeyDown = n => n == k });
            Add(new PhysicalInput(InputSource.Key, k), r.buttons, r.x, r.y);
        }
        for (int i = 0; i < mouseButtons.Length; i++)
        {
            var s = new InputSample { Gameplay = gameplay }; s.MouseButtons[i] = true;
            var r = new InputMapper().Evaluate(s);
            Add(new PhysicalInput(InputSource.Mouse, mouseButtons[i]), r.buttons, r.x, r.y);
        }
        foreach (var dir in new[] { "Up", "Down" })
        {
            var r = new InputMapper().Evaluate(new InputSample { Gameplay = gameplay, WheelUp = dir == "Up" ? 1 : 0, WheelDown = dir == "Down" ? 1 : 0 });
            Add(new PhysicalInput(InputSource.Wheel, dir), r.buttons, r.x, r.y);
        }
        foreach (var c in padControls)
        {
            var pd = new PadSnapshot { Connected = true };
            switch (c)
            {
                case "A": pd.A = true; break; case "B": pd.B = true; break; case "X": pd.X = true; break; case "Y": pd.Y = true; break;
                case "LB": pd.LB = true; break; case "RB": pd.RB = true; break; case "LT": pd.LT = 1; break; case "RT": pd.RT = 1; break;
                case "Start": pd.Start = true; break; case "Back": pd.Back = true; break;
                case "DUp": pd.DUp = true; break; case "DDown": pd.DDown = true; break; case "DLeft": pd.DLeft = true; break; case "DRight": pd.DRight = true; break;
                case "LSUp": pd.LY = 1; break; case "LSDown": pd.LY = -1; break; case "LSLeft": pd.LX = -1; break; case "LSRight": pd.LX = 1; break;
                case "RSUp": pd.RY = 1; break; case "RSDown": pd.RY = -1; break; case "RSLeft": pd.RX = -1; break; case "RSRight": pd.RX = 1; break;
            }
            var r = new InputMapper().Evaluate(new InputSample { Gameplay = gameplay, Pad = pd });
            Add(new PhysicalInput(InputSource.Pad, c), r.buttons, r.x, r.y);
        }
        return t;
    }

    /// <summary>The prompt button of one N64 button bit (Trw.Platform.N64).</summary>
    public static N64Button? FromBit(ushort bit) => bit switch
    {
        N64.A => N64Button.A, N64.B => N64Button.B, N64.Z => N64Button.Z, N64.Start => N64Button.Start, N64.L => N64Button.L, N64.R => N64Button.R,
        N64.CUp => N64Button.CUp, N64.CDown => N64Button.CDown, N64.CLeft => N64Button.CLeft, N64.CRight => N64Button.CRight,
        N64.DUp => N64Button.DUp, N64.DDown => N64Button.DDown, N64.DLeft => N64Button.DLeft, N64.DRight => N64Button.DRight,
        _ => null,
    };
}
