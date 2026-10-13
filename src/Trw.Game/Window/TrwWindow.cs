using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game;

/// <summary>
/// The MonoGame front end: a 60 Hz window that ticks the console's retrace, feeds controller input into the game's PIF emulation
/// and (later) draws the display lists the game builds.
/// </summary>
public sealed class TrwWindow : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly AudioOut audio = new();
    private readonly DisplayListInterpreter interpreter = new();
    private volatile IReadOnlyList<GfxCommand>? pendingFrame;
    private GfxRenderer? renderer;
    private Scoreboard? scoreboard;      // Tab scoreboard + F2 split / local view (Scoreboard.cs, LocalView.cs, ViewLayout.cs)
    private int frame, drawnFrames, shotsTaken;
    private bool fullscreenKeyDown;
    // previews: TRW_SHOT_DIR=<dir> [TRW_SHOT_EVERY=<display lists>] [TRW_SHOT_MAX=<files>] saves PNGs of the rendered frames
    private readonly string? shotDir = Environment.GetEnvironmentVariable("TRW_SHOT_DIR");
    private readonly int shotEvery = int.TryParse(Environment.GetEnvironmentVariable("TRW_SHOT_EVERY"), out var se) && se > 0 ? se : 30;
    private readonly int shotMax = int.TryParse(Environment.GetEnvironmentVariable("TRW_SHOT_MAX"), out var sm) && sm > 0 ? sm : 20;
    private readonly double autoExitSeconds = double.TryParse(Environment.GetEnvironmentVariable("TRW_EXIT_AFTER"), out var ea) ? ea : 0;

    public TrwWindow()
    {
        // TRW_WINDOW=<w>x<h> sets the initial client size (default 960x720)
        int ww = 960, wh = 720;
        if (Environment.GetEnvironmentVariable("TRW_WINDOW") is { } ws && ws.Split('x') is { Length: 2 } wp && int.TryParse(wp[0], out var pw) && int.TryParse(wp[1], out var ph) && pw >= 160 && ph >= 120) { ww = pw; wh = ph; }
        graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = ww, PreferredBackBufferHeight = wh };
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        if (double.TryParse(Environment.GetEnvironmentVariable("TRW_FAST"), out var speed) && speed > 1)   // test runs: retrace ticks at <speed> x 60 Hz
        {
            TargetElapsedTime = TimeSpan.FromSeconds(1.0 / (60 * speed));
            graphics.SynchronizeWithVerticalRetrace = false;
        }
        Window.Title = "Turok: Rage Wars";
        Content.RootDirectory = "Content";
        Window.AllowUserResizing = true;
        // the display list is interpreted the moment the game starts the task (its memory is consistent then); the window thread draws the result
        Host.GraphicsTask = task => { var cmds = interpreter.Run(task); Oracle.Frame(cmds); RecordSignature(cmds); pendingFrame = cmds; };
    }

    protected override void LoadContent()
    {
        renderer = new GfxRenderer(GraphicsDevice, Content.Load<Microsoft.Xna.Framework.Graphics.Effect>("Combiner"));
        scoreboard = new Scoreboard(GraphicsDevice, Content.Load<SpriteFont>("Scoreboard"), Content.Load<SpriteFont>("ScoreboardTitle")); renderer.Overlay = scoreboard.Draw;
        base.LoadContent();
    }

    protected override void Initialize()
    {
        Host.ReadPad = ReadPad;
        Host.ReadMouse = ReadMouse;
        ApplyMouseSettings();
        Window.TextInput += (_, e) => TextEntry.OnTextInput(e.Character, e.Key);     // typed characters (shift and layout applied) for the name entry
        audio.Hook();
        base.Initialize();
    }

    // scripted input for unattended runs: TRW_PAD_SCRIPT="<tick>:<BUTTON>[+<BUTTON>...]:<ticks held>,..." drives pad 0 (ticks are 60 Hz window updates).
    // BUTTON = A B Z START UP DOWN LEFT RIGHT L R CUP CDOWN CLEFT CRIGHT SUP SDOWN SLEFT SRIGHT (S* = analogue stick at full tilt)
    private static readonly (int start, int len, ushort buttons, int sx, int sy)[] allSteps = ParseScript(Environment.GetEnvironmentVariable("TRW_PAD_SCRIPT"));
    private static readonly (int start, int len, ushort buttons, int sx, int sy)[] script = allSteps.Where(x => x.start >= 0).ToArray();     // absolute ticks
    private static readonly (int start, int len, ushort buttons, int sx, int sy)[] settledSteps = allSteps.Where(x => x.start < 0).ToArray(); // in order, each after the screen settled
    // event-relative steps, robust against slow loads: "R<screen>+<delay>:BUTTON[:hold]" presses once <delay> ticks after the UI screen became <screen>,
    // "T<state>+<delay>:BUTTON[:hold]" once <delay> ticks after the game state became <state> (state 13 = playing)
    private sealed class EventStep { public bool Screen; public int Id, Delay, Len; public ushort Buttons; public int Sx, Sy, FiredAt = -1; }
    private static readonly EventStep[] eventSteps = ParseEventSteps(Environment.GetEnvironmentVariable("TRW_PAD_SCRIPT"));

    private static EventStep[] ParseEventSteps(string? s)
    {
        var list = new List<EventStep>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            var m = System.Text.RegularExpressions.Regex.Match(p[0], @"^([RT])(-?\d+)\+(\d+)$");
            if (!m.Success || p.Length < 2) continue;
            var one = ParseScript("0:" + p[1] + ":" + (p.Length > 2 ? p[2] : "6"));
            if (one.Length == 0) continue;
            list.Add(new EventStep { Screen = m.Groups[1].Value == "R", Id = int.Parse(m.Groups[2].Value), Delay = int.Parse(m.Groups[3].Value), Len = one[0].Item2, Buttons = one[0].Item3, Sx = one[0].Item4, Sy = one[0].Item5 });
        }
        return list.ToArray();
    }

    internal static int scriptTick;
    private static int settledIndex, settledPressFrom = -1, settledNextAllowed;
    // screen signatures of the last display lists: the screen counts as settled when it hardly changes (animations aside) over ~40 lists
    private static readonly long[] recentSignatures = new long[40];
    private static int signatureCount;

    internal static void RecordSignature(IReadOnlyList<GfxCommand> cmds)
    {
        long sig = cmds.Count * 1000003L + cmds.OfType<DrawCommand>().Sum(c => (long)c.Vertices.Length);
        lock (recentSignatures) { recentSignatures[signatureCount++ % recentSignatures.Length] = sig; lastSignature = sig; }
    }

    private static int settledSince = -1;
    private static readonly int SettleDelay = int.TryParse(Environment.GetEnvironmentVariable("TRW_PAD_SETTLE_DELAY"), out var sd) ? sd : 150;
    private static long lastSignature, signatureBeforePress;
    private static bool waitingForChange;
    private static int changeDeadline;

    private static bool Settled()
    {
        lock (recentSignatures)
        {
            if (signatureCount < recentSignatures.Length) return false;
            var distinct = recentSignatures.Distinct().ToList();
            return distinct.Count <= 2 && distinct.All(x => x >= 1000003L * 3);     // a drawn screen (not a black frame), at most two states (blinking text)
        }
    }

    /// <summary>The settled-step press (if any) that applies this tick.</summary>
    private static bool SettledPress(out ushort buttons, out int sx, out int sy)
    {
        buttons = 0; sx = sy = 0;
        if (settledIndex >= settledSteps.Length) return false;
        var step = settledSteps[settledIndex];
        if (settledPressFrom < 0)
        {
            if (scriptTick < Math.Max(settledNextAllowed, -1 - step.start)) return false;
            if (waitingForChange)
            {
                // the press should lead to another screen: wait for the display to differ from the one that was pressed on (a cursor move may not, hence the deadline)
                if (lastSignature == signatureBeforePress && scriptTick < changeDeadline) return false;
                waitingForChange = false;
                lock (recentSignatures) signatureCount = 0;
                return false;
            }
            if (!Settled()) { settledSince = -1; return false; }
            if (settledSince < 0) settledSince = scriptTick;
            if (scriptTick < settledSince + SettleDelay) return false;        // a new screen ignores input for a moment while it animates in
            settledSince = -1;
            settledPressFrom = scriptTick;
            Console.Error.WriteLine($"[pad] tick {scriptTick}: screen settled, step {settledIndex + 1}/{settledSteps.Length}");
        }
        if (scriptTick < settledPressFrom + step.len) { buttons = step.buttons; sx = step.sx; sy = step.sy; return true; }
        settledPressFrom = -1; settledIndex++; settledNextAllowed = scriptTick + 60;        // let the press take effect before looking for the next screen
        signatureBeforePress = lastSignature; waitingForChange = true; changeDeadline = scriptTick + (int.TryParse(Environment.GetEnvironmentVariable("TRW_PAD_CHANGE_WAIT"), out var cw) ? cw : 900);
        return false;
    }

    private static (int, int, ushort, int, int)[] ParseScript(string? s)
    {
        var list = new List<(int, int, ushort, int, int)>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            // S[@<tick>]:<BUTTON>[:hold] = press once the screen has settled (see Settled), not before <tick>; encoded as start = -1 - <tick>
            bool settled = p.Length >= 2 && p[0].StartsWith('S');
            if (p.Length < 2 || (!settled && !int.TryParse(p[0], out _))) continue;      // (R<screen>+<delay> / T<state>+<delay> items are ParseEventSteps')
            int start = settled ? -1 - (p[0].Length > 2 && p[0][1] == '@' && int.TryParse(p[0][2..], out var mt) ? mt : 0) : int.Parse(p[0]);
            int len = p.Length > 2 && int.TryParse(p[2], out var l) ? l : 6, sx = 0, sy = 0; ushort b = 0;
            foreach (var name in p[1].Split('+'))
                switch (name.ToUpperInvariant())
                {
                    case "A": b |= 0x8000; break; case "B": b |= 0x4000; break; case "Z": b |= 0x2000; break; case "START": b |= 0x1000; break;
                    case "UP": b |= 0x0800; break; case "DOWN": b |= 0x0400; break; case "LEFT": b |= 0x0200; break; case "RIGHT": b |= 0x0100; break;
                    case "L": b |= 0x0020; break; case "R": b |= 0x0010; break;
                    case "CUP": b |= 0x0008; break; case "CDOWN": b |= 0x0004; break; case "CLEFT": b |= 0x0002; break; case "CRIGHT": b |= 0x0001; break;
                    case "SUP": sy = 80; break; case "SDOWN": sy = -80; break; case "SLEFT": sx = -80; break; case "SRIGHT": sx = 80; break;
                }
            list.Add((start, len, b, sx, sy));
        }
        return list.ToArray();
    }

    // devices sampled by the window thread once per update and consumed by the game's pad poll (the C port samples once per poll: nothing is queued across frames,
    // except mouse motion, which accumulates until the game polls)
    private static readonly InputMapper[] mappers = { new(), new(), new(), new() };
    private static readonly object deviceLock = new();
    private static KeyboardState keyboard;
    private static readonly bool[] mouseButtons = new bool[5];
    private static float mouseDx, mouseDy;
    private static int wheelUp, wheelDown;
    private static volatile bool mouseCaptured;
    private static long lastPoll;

    /// <summary>1 free play (state 13 with only the HUD layer on the UI stack), 0 menus, -1 before the game state is known: the two binding layers (game: / menu:) of the mapping.</summary>
    private static int GameplayContext()
    {
        int st = global::Trw.Game.Game.StateId, scr = global::Trw.Game.Game.ScreenId;
        // state 13 with the HUD layer = free play (1); state 13 with any other screen id is not known to be a menu (pause screens differ per mode): -1 keeps BOTH binding layers live, so WASD still moves
        return st < 0 ? -1 : st == 13 ? ((scr == 2 || scr == -1) ? 1 : -1) : 0;
    }

    /// <summary>N64 controller bits: A 8000, B 4000, Z 2000, Start 1000, D-pad 0800/0400/0200/0100, L 0020, R 0010, C-buttons 0008/0004/0002/0001.</summary>
    private static readonly bool padLog = Environment.GetEnvironmentVariable("TRW_STATUS") != null;
    private static ushort lastLoggedButtons; private static sbyte lastLoggedStickY;

    /// <summary>The TRW_PAD_SCRIPT part of pad 0 (also used by the headless runner, which has no keyboard or gamepad).</summary>
    internal static bool ScriptedPad(ref ushort buttons, ref int x, ref int y)
    {
        bool connected = false;
            foreach (var (start, len, b, sx, sy) in script)
                if (scriptTick >= start && scriptTick < start + len) { buttons |= b; x += sx; y += sy; connected = true; }
            if (SettledPress(out var sb, out var ssx, out var ssy)) { buttons |= sb; x += ssx; y += ssy; connected = true; }
            foreach (var es in eventSteps)
            {
                if (es.FiredAt < 0 && (es.Screen ? global::Trw.Game.Game.ScreenId == es.Id && global::Trw.Game.Game.CurrentTick - global::Trw.Game.Game.ScreenSince >= es.Delay : global::Trw.Game.Game.StateId == es.Id && global::Trw.Game.Game.CurrentTick - global::Trw.Game.Game.StateSince >= es.Delay))
                    es.FiredAt = scriptTick;
                if (es.FiredAt >= 0 && scriptTick >= es.FiredAt && scriptTick < es.FiredAt + es.Len) { buttons |= es.Buttons; x += es.Sx; y += es.Sy; connected = true; }
            }
        return connected;
    }

    // TRW_KBM_SCRIPT="T<state>+<delay>:<key>[+<key>..]|-:<hold>[:<mouse dx>[:<mouse dy>]],..." (R<screen>+<delay> too): keyboard keys (MonoGame names: W, A, Space, ...)
    // and mouse counts per poll fed through the window's keyboard + mouse mapping (InputMapper), so the player's real input path runs in headless tests
    private sealed class KbmStep { public bool Screen; public int Id, Delay, Len, FiredAt = -1; public string[] Keys = Array.Empty<string>(); public float Dx, Dy; }
    private static readonly KbmStep[] kbmSteps = ParseKbm(Environment.GetEnvironmentVariable("TRW_KBM_SCRIPT"));

    private static KbmStep[] ParseKbm(string? s)
    {
        var list = new List<KbmStep>();
        foreach (var item in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split(':');
            var m = System.Text.RegularExpressions.Regex.Match(p[0], @"^([RT])(-?\d+)\+(\d+)$");
            if (!m.Success || p.Length < 2) continue;
            list.Add(new KbmStep
            {
                Screen = m.Groups[1].Value == "R", Id = int.Parse(m.Groups[2].Value), Delay = int.Parse(m.Groups[3].Value),
                Keys = p[1] == "-" ? Array.Empty<string>() : p[1].Split('+'), Len = p.Length > 2 && int.TryParse(p[2], out var l) ? l : 6,
                Dx = p.Length > 3 && float.TryParse(p[3], System.Globalization.CultureInfo.InvariantCulture, out var dx) ? dx : 0,
                Dy = p.Length > 4 && float.TryParse(p[4], System.Globalization.CultureInfo.InvariantCulture, out var dy) ? dy : 0,
            });
        }
        return list.ToArray();
    }

    private static void ScriptedKbm(ref ushort buttons, ref int x, ref int y)
    {
        if (kbmSteps.Length == 0) return;
        var keys = new HashSet<string>(); float dx = 0, dy = 0;
        foreach (var es in kbmSteps)
        {
            if (es.FiredAt < 0 && (es.Screen ? global::Trw.Game.Game.ScreenId == es.Id && global::Trw.Game.Game.CurrentTick - global::Trw.Game.Game.ScreenSince >= es.Delay : global::Trw.Game.Game.StateId == es.Id && global::Trw.Game.Game.CurrentTick - global::Trw.Game.Game.StateSince >= es.Delay))
                es.FiredAt = scriptTick;
            if (es.FiredAt >= 0 && scriptTick >= es.FiredAt && scriptTick < es.FiredAt + es.Len) { keys.UnionWith(es.Keys); dx += es.Dx; dy += es.Dy; }
        }
        bool native = NativeRoute(dx, dy, true);
        var r = mappers[0].Evaluate(new InputSample { Gameplay = GameplayContext(), KeyDown = k => keys.Contains(k) && !TextEntry.Active, MouseDx = dx, MouseDy = dy, MouseValid = !native });
        buttons |= r.buttons; x += r.x; y += r.y;
        wheelOpen = (r.buttons & 0x8000) != 0;
    }

    // native mouse look (Os/MouseLook.cs): pad 0's mouse motion turns the view directly (degrees: InputMapper.DegPerCount x MouseSens per count) instead of driving
    // the stick, except while the weapon button is held (the wheel is open: the mouse then moves the stick to pick a weapon, as in the C port) and outside free play
    private static float nativeYaw, nativePitch;
    private static bool wheelOpen;
    private static bool NativeRoute(float dx, float dy, bool valid)
    {
        if (!global::Trw.Game.Game.NativeMouse || !valid || wheelOpen || GameplayContext() != 1) return false;
        var m = mappers[0];
        lock (deviceLock) { nativeYaw += dx * InputMapper.DegPerCount * m.MouseSens; nativePitch += dy * InputMapper.DegPerCount * m.MouseSens * (m.MouseInvertY ? -1f : 1f); }
        return true;
    }

    private static bool NativeRouteLocked(float dx, float dy)      // NativeRoute for a caller that already holds deviceLock
    {
        if (!global::Trw.Game.Game.NativeMouse || wheelOpen || GameplayContext() != 1) return false;
        var m = mappers[0];
        nativeYaw += dx * InputMapper.DegPerCount * m.MouseSens; nativePitch += dy * InputMapper.DegPerCount * m.MouseSens * (m.MouseInvertY ? -1f : 1f);
        return true;
    }

    /// <summary><see cref="Host.ReadMouse"/>: the native mouse look of pad 0 since the previous controller read (degrees right / down).</summary>
    internal static void ReadMouse(int pad, out float yawDeg, out float pitchDeg, out bool active)
    {
        yawDeg = pitchDeg = 0; active = false;
        if (pad != 0 || !global::Trw.Game.Game.NativeMouse) return;
        // played with the mouse: captured in free play (the window), or a test script that moves the mouse
        active = (mouseCaptured || kbmSteps.Any(s => s.Dx != 0 || s.Dy != 0)) && GameplayContext() == 1;
        lock (deviceLock) { yawDeg = nativeYaw; pitchDeg = nativePitch; nativeYaw = nativePitch = 0; }
    }

    internal static bool ReadPadHeadless(int pad, out ushort buttons, out sbyte stickX, out sbyte stickY)
    {
        buttons = 0; stickX = 0; stickY = 0;
        if (pad != 0) { int ex = 0, ey = 0; bool c = ExtraPadScripts.Read(pad, ref buttons, ref ex, ref ey); stickX = (sbyte)Math.Clamp(ex, -80, 80); stickY = (sbyte)Math.Clamp(ey, -80, 80); return c; }   // TRW_PAD2..4_SCRIPT
        int x = 0, y = 0;
        ScriptedPad(ref buttons, ref x, ref y);
        ScriptedKbm(ref buttons, ref x, ref y);
        buttons |= TextEntry.Poll();                        // TRW_TYPE_SCRIPT
        stickX = (sbyte)Math.Clamp(x, -80, 80); stickY = (sbyte)Math.Clamp(y, -80, 80);
        return true;
    }

    internal static bool ReadPad(int pad, out ushort buttons, out sbyte stickX, out sbyte stickY)
    {
        buttons = 0; stickX = 0; stickY = 0;
        int x = 0, y = 0;
        bool connected = false;
        if (pad == 0 && ScriptedPad(ref buttons, ref x, ref y)) connected = true;
        if (pad == 0 && kbmSteps.Length > 0) { ScriptedKbm(ref buttons, ref x, ref y); connected = true; }
        if (pad != 0 && ExtraPadScripts.Read(pad, ref buttons, ref x, ref y)) connected = true;      // TRW_PAD2..4_SCRIPT (ExtraPadScripts.cs)
        // real devices (port of the C port's input_core mapping: kbm_fps keyboard + mouse layout and the dual-stick pad layout, see InputMapper); scripted input wins:
        // the devices are read for a pad only while no scripted input is active on it
        if (buttons == 0 && x == 0 && y == 0)
        {
            var gp = GamePad.GetState(pad, GamePadDeadZone.None);
            var sample = new InputSample { Gameplay = GameplayContext() };
            if (pad == 0)
            {
                connected = true;
                lock (deviceLock)
                {
                    var ks = keyboard; sample.KeyDown = name => Enum.TryParse<Keys>(name, out var key) && ks.IsKeyDown(key) && !TextEntry.Swallows(key);   // keys typing a name are not bindings
                    Array.Copy(mouseButtons, sample.MouseButtons, 5);
                    sample.MouseDx = mouseDx; sample.MouseDy = mouseDy; sample.MouseValid = mouseCaptured;
                    if (mouseCaptured && NativeRouteLocked(mouseDx, mouseDy)) sample.MouseValid = false;     // native look: the motion turns the view, not the stick
                    sample.WheelUp = wheelUp; sample.WheelDown = wheelDown;
                    mouseDx = mouseDy = 0; wheelUp = wheelDown = 0;
                }
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastPoll != 0) sample.Dt = (now - lastPoll) / (double)System.Diagnostics.Stopwatch.Frequency;
                lastPoll = now;
            }
            if (gp.IsConnected)
            {
                connected = true;
                var b = gp.Buttons; var d = gp.DPad;
                sample.Pad = new PadSnapshot
                {
                    Connected = true, A = b.A == ButtonState.Pressed, B = b.B == ButtonState.Pressed, X = b.X == ButtonState.Pressed, Y = b.Y == ButtonState.Pressed,
                    LB = b.LeftShoulder == ButtonState.Pressed, RB = b.RightShoulder == ButtonState.Pressed, Start = b.Start == ButtonState.Pressed, Back = b.Back == ButtonState.Pressed,
                    DUp = d.Up == ButtonState.Pressed, DDown = d.Down == ButtonState.Pressed, DLeft = d.Left == ButtonState.Pressed, DRight = d.Right == ButtonState.Pressed,
                    LX = gp.ThumbSticks.Left.X, LY = gp.ThumbSticks.Left.Y, RX = gp.ThumbSticks.Right.X, RY = gp.ThumbSticks.Right.Y, LT = gp.Triggers.Left, RT = gp.Triggers.Right,
                };
            }
            if (connected)
            {
                var r = mappers[pad & 3].Evaluate(sample);
                buttons |= r.buttons; x += r.x; y += r.y;
                if (pad == 0) wheelOpen = (r.buttons & 0x8000) != 0;
            }
        }
        if (pad == 0) buttons |= TextEntry.Poll();         // typed name: the letter-cycler presses (TextEntry.cs)
        stickX = (sbyte)Math.Clamp(x, -80, 80);
        stickY = (sbyte)Math.Clamp(y, -80, 80);
        if (pad == 0 && padLog && (buttons != lastLoggedButtons || stickY != lastLoggedStickY))
        {
            lastLoggedButtons = buttons; lastLoggedStickY = stickY;
            Console.Error.WriteLine($"[pad] tick {scriptTick}: buttons {buttons:X4} stick ({stickX},{stickY})");
        }
        return connected;
    }

    private bool escapeDown, f1Down, captureSuspended, haveMouseRef, relMouse, sensDownHeld, sensUpHeld, clickResume;

    private static void ApplyMouseSettings()
    {
        foreach (var m in mappers) { m.MouseSens = UserSettings.Current.MouseSensitivity; m.MouseInvertY = UserSettings.Current.MouseInvertY; }
    }

    /// <summary>SDL2 (MonoGame DesktopGL's platform layer) relative mouse mode, called directly: MonoGame does not expose it.</summary>
    private static class Sdl
    {
        [System.Runtime.InteropServices.DllImport("SDL2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern int SDL_SetRelativeMouseMode(int enabled);
        [System.Runtime.InteropServices.DllImport("SDL2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] private static extern uint SDL_GetRelativeMouseState(out int x, out int y);
        private static bool broken = Environment.GetEnvironmentVariable("TRW_MOUSE_WARP") == "1";      // TRW_MOUSE_WARP=1: the old centre-warp capture
        public static bool SetRelative(bool on)
        {
            if (broken) return false;
            try { bool ok = SDL_SetRelativeMouseMode(on ? 1 : 0) == 0; if (on && ok) SDL_GetRelativeMouseState(out _, out _); return ok; }   // drop the motion gathered before
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { broken = true; Console.Error.WriteLine($"[mouse] SDL relative mode unavailable ({e.GetType().Name}): centre-warp capture"); return false; }
        }
        public static void RelativeMotion(out int x, out int y) { x = y = 0; if (!broken) try { SDL_GetRelativeMouseState(out x, out y); } catch (Exception) { broken = true; } }
    }
    private int lastWheel;

    /// <summary>Samples keyboard, mouse and wheel; the mouse is captured (hidden, centred, relative motion) while a level runs in the active window (mouse_capture = auto).</summary>
    private void SampleDevices(KeyboardState ks)
    {
        var ms = Mouse.GetState();
        // a click into the window during free play takes the mouse back after Esc / F1 released it (that click does not reach the game)
        if (captureSuspended && IsActive && GameplayContext() == 1 && ms.LeftButton == ButtonState.Pressed && Window.ClientBounds.Contains(Window.ClientBounds.X + ms.X, Window.ClientBounds.Y + ms.Y))
        { captureSuspended = false; clickResume = true; }
        if (clickResume && ms.LeftButton == ButtonState.Released) clickResume = false;
        bool capture = IsActive && !captureSuspended && GameplayContext() == 1;
        int cx = Window.ClientBounds.Width / 2, cy = Window.ClientBounds.Height / 2;
        lock (deviceLock)
        {
            keyboard = ks;
            // an unfocused window takes no input: clicks and keys typed into another program must not become presses (MonoGame reports the global state)
            bool act = IsActive;
            if (!act) keyboard = default;
            mouseButtons[0] = act && !clickResume && ms.LeftButton == ButtonState.Pressed; mouseButtons[1] = act && ms.RightButton == ButtonState.Pressed; mouseButtons[2] = act && ms.MiddleButton == ButtonState.Pressed;
            mouseButtons[3] = act && ms.XButton1 == ButtonState.Pressed; mouseButtons[4] = act && ms.XButton2 == ButtonState.Pressed;
            if (haveMouseRef && act)
            {
                int notches = (ms.ScrollWheelValue - lastWheel) / 120;
                if (notches > 0) wheelUp += notches; else wheelDown -= notches;
            }
            lastWheel = ms.ScrollWheelValue; haveMouseRef = true;
            if (capture)
            {
                // SDL relative mode (the usual FPS capture): the cursor is hidden and held in the window, the motion is the raw device motion (no pointer
                // acceleration) and cannot leave the window between two frames; without SDL the cursor is warped back to the centre every frame
                if (!mouseCaptured) { relMouse = Sdl.SetRelative(true); Console.Error.WriteLine($"[mouse] tick {global::Trw.Game.Game.CurrentTick}: captured ({(relMouse ? "SDL relative mode" : "centre warp")}){(global::Trw.Game.Game.NativeMouse ? ", native look" : ", mouse drives the stick")}"); }
                if (relMouse) { Sdl.RelativeMotion(out int rx, out int ry); if (mouseCaptured) { mouseDx += rx; mouseDy += ry; } }
                else { if (mouseCaptured) { mouseDx += ms.X - cx; mouseDy += ms.Y - cy; } Mouse.SetPosition(cx, cy); }
            }
            else if (mouseCaptured) { mouseDx = mouseDy = 0; foreach (var m in mappers) m.ResetMouse(); if (relMouse) { Sdl.SetRelative(false); relMouse = false; Mouse.SetPosition(cx, cy); } }
            if (capture != mouseCaptured) { mouseCaptured = capture; IsMouseVisible = !capture; if (!capture) Console.Error.WriteLine($"[mouse] tick {global::Trw.Game.Game.CurrentTick}: released"); }
        }
    }

    protected override void Update(GameTime gameTime)
    {
        if (Net.NetPlay.LobbyUpdate(this, gameTime)) { base.Update(gameTime); return; }      // --netplay: the lobby runs until the session boots the game
        var ks = Keyboard.GetState();
        SampleDevices(ks);
        scoreboard?.Update(ks, IsActive);
        TextEntry.Sample(ks, gameTime.TotalGameTime.TotalSeconds);      // Backspace / Enter on the name entry screen
        bool esc = ks.IsKeyDown(Keys.Escape);
        if (esc && !escapeDown && !TextEntry.Active) { if (mouseCaptured) { captureSuspended = true; } else Exit(); }      // Esc releases a captured mouse, otherwise it quits
        escapeDown = esc;
        bool f1 = ks.IsKeyDown(Keys.F1);
        if (f1 && !f1Down) captureSuspended = !captureSuspended;                                         // F1 captures / releases the mouse
        f1Down = f1;
        // - / = : mouse sensitivity down / up by 1.25 x (saved in the user settings; shown on screen)
        bool sDown = IsActive && ks.IsKeyDown(Keys.OemMinus), sUp = IsActive && (ks.IsKeyDown(Keys.OemPlus));
        if ((sDown && !sensDownHeld) || (sUp && !sensUpHeld))
        {
            var us = UserSettings.Current;
            us.MouseSensitivity = Math.Clamp(us.MouseSensitivity * (sUp ? 1.25f : 0.8f), 0.05f, 50f);
            us.Save(); ApplyMouseSettings();
            Scoreboard.Toast($"MOUSE SENSITIVITY {us.MouseSensitivity:0.##}");
        }
        sensDownHeld = sDown; sensUpHeld = sUp;
        bool f11 = ks.IsKeyDown(Keys.F11) || (ks.IsKeyDown(Keys.Enter) && (ks.IsKeyDown(Keys.LeftAlt) || ks.IsKeyDown(Keys.RightAlt)));
        if (f11 && !fullscreenKeyDown) graphics.ToggleFullScreen();       // F11 or Alt+Enter
        fullscreenKeyDown = f11;
        if (autoExitSeconds > 0 && gameTime.TotalGameTime.TotalSeconds > autoExitSeconds) Exit();
        if (Net.NetPlay.Enabled) Net.NetPlay.WindowTick(); else if (Oracle.Deterministic) Game.LockstepTick(); else Game.ViTick();      // netplay: the session's next tick(s)
        audio.Pump();
        frame++;
        scriptTick = frame;
        base.Update(gameTime);
    }

    protected override void UnloadContent()
    {
        audio.Dispose();
        renderer?.Dispose(); scoreboard?.Dispose();
        base.UnloadContent();
    }

    protected override void Draw(GameTime gameTime)
    {
        if (Net.NetPlay.LobbyDraw(GraphicsDevice)) { base.Draw(gameTime); return; }
        var cmds = Interlocked.Exchange(ref pendingFrame, null);
        if (cmds != null)
        {
            renderer!.Execute(cmds, ViewLayout.LocalRegion(cmds));
            drawnFrames++;
            if (shotDir != null && drawnFrames % shotEvery == 0 && shotsTaken < shotMax)
            {
                Directory.CreateDirectory(shotDir);
                renderer.SavePng(Path.Combine(shotDir, $"frame_{drawnFrames:D5}.png"));
                shotsTaken++;
            }
        }
        renderer!.Present(Window.ClientBounds.Width, Window.ClientBounds.Height);
        Net.NetPlay.OverlayDraw(GraphicsDevice);       // netplay status line (waiting, desync, a player left)
        base.Draw(gameTime);
    }
}


