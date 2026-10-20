using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// The on-screen controller for touch screens (the Android head; <c>TRW_TOUCH=1</c> turns it on on the desktop, where the mouse acts as one finger).
/// Landscape layout, multi-touch, semi-transparent, sized from the screen height:
/// <list type="bullet">
/// <item>two floating sticks (each appears where its thumb lands, like the touch layouts of Minecraft and other mobile shooters), the N64 controller's
///   two movement controls: the left stick is the C buttons in free play (walk, strafe: the game's default move), the right stick the analogue stick
///   (aim / turn). In menus the left stick moves the cursor (D-pad) and the right stick gives the C buttons (the menus' "SKINS = C-up" and friends);</item>
/// <item><c>"LookStick": false</c> in touch.json: the right thumb drags anywhere on the right part of the screen to look instead (the native FPS look,
///   <see cref="Host.ReadMouse"/>, like the mouse);</item>
/// <item>buttons FIRE (Z; dragging from it keeps aiming), JUMP (R), ALT (B, secondary fire / back in menus), WEAPON (A: tap = next weapon, hold + the right
///   stick or a drag = the weapon wheel; OK in menus), L; START at the top;</item>
/// <item>the system back key: START in free play (pause), B in menus.</item>
/// </list>
/// The pad bits go into pad 0 next to the keyboard / gamepad mapping (<see cref="Apply"/>, called from <see cref="TrwWindow.ReadPad"/>), the look into the
/// native look (<see cref="TakeLook"/>). Touches report <see cref="InputDeviceKind.Touch"/> to <see cref="ActiveInput"/>; a physical gamepad or keyboard
/// reports its own kind, and the controls fade out while another device is in use (they come back with the next touch).
/// Settings: <c>touch.json</c> next to the user settings (look sensitivity, invert, size, opacity); <c>TRW_TOUCH_SENS</c> overrides the sensitivity.
/// </summary>
public sealed class TouchControls : IDisposable
{
    public static readonly bool Enabled = OperatingSystem.IsAndroid() || Environment.GetEnvironmentVariable("TRW_TOUCH") == "1";

    public sealed class Settings
    {
        public float LookSensitivity { get; set; } = 1f;     // x 0.11 degrees per pixel at 1080 pixels of screen height (scaled with the height)
        public bool InvertY { get; set; }
        public float Size { get; set; } = 1f;                // control size factor (0.6 .. 1.6)
        public float Opacity { get; set; } = 1f;             // 0.2 .. 1.5 x the default transparency
        public bool Haptics { get; set; } = true;            // short vibration on a button press (Android)
        public bool LookStick { get; set; } = true;          // the right thumb: a stick = the N64 analogue stick (true), or drag-to-look (false)

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurokRageWars", "touch.json");

        public static Settings Load()
        {
            var s = new Settings();
            try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? s; }
            catch (Exception e) { Console.Error.WriteLine($"[touch] {FilePath}: {e.Message} (defaults used)"); }
            if (float.TryParse(Environment.GetEnvironmentVariable("TRW_TOUCH_SENS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts) && ts > 0) s.LookSensitivity = ts;
            s.LookSensitivity = Math.Clamp(s.LookSensitivity, 0.1f, 10f); s.Size = Math.Clamp(s.Size, 0.6f, 1.6f); s.Opacity = Math.Clamp(s.Opacity, 0.2f, 1.5f);
            return s;
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception e) { Console.Error.WriteLine($"[touch] cannot save {FilePath}: {e.Message}"); }
        }
    }

    public static readonly Settings Config = Settings.Load();

    /// <summary>A short vibration for a button press (set by the Android head).</summary>
    public static Action? Haptic;

    // ---- the state the game thread reads (pad poll, view update) ----
    private static readonly object gate = new();
    private static ushort outButtons;
    private static int outX, outY;
    private static float lookYaw, lookPitch;          // degrees right / down not yet taken by the game
    private static bool lookOn;                       // the touch look is the active look (free play, touch in use)
    private static float pendYaw, pendPitch;          // TRW_NATIVE_MOUSE=0: the look goes through the stick (InputMapper.MouseAxis)
    private static long lastApply;
    private static int backPulse;                     // window frames left of a system back press

    /// <summary>The system back key (Android): START in free play, B in menus, for a few frames.</summary>
    public static void PressBack() { lock (gate) backPulse = 6; ActiveInput.Report(InputDeviceKind.Touch); }

    /// <summary>Pad 0's touch buttons and stick, merged into the controller read (game thread).</summary>
    internal static void Apply(ref ushort buttons, ref int x, ref int y)
    {
        lock (gate)
        {
            buttons |= outButtons;
            x += outX; y += outY;
            if (!Game.NativeMouse && lookOn)
            {   // no native look: the drag turns the view through the stick, like the desktop's mouse-as-stick path
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float dt = lastApply == 0 ? 1f / 60 : (float)Math.Clamp((now - lastApply) / (double)System.Diagnostics.Stopwatch.Frequency, 1.0 / 120, 0.1);
                lastApply = now;
                int mx = InputMapper.MouseAxis(ref pendYaw, lookYaw / InputMapper.DegPerCount, 1f, dt, InputMapper.YawRatePerE3, 30f);
                int my = InputMapper.MouseAxis(ref pendPitch, lookPitch / InputMapper.DegPerCount, 1f, dt, InputMapper.PitchRatePerE3, 30f);
                lookYaw = lookPitch = 0;
                x += mx; y -= my;
            }
        }
    }

    /// <summary>The touch look since the previous controller read (game thread, <see cref="Host.ReadMouse"/> of pad 0).</summary>
    internal static void TakeLook(ref float yawDeg, ref float pitchDeg, ref bool active)
    {
        if (!Game.NativeMouse) return;
        lock (gate)
        {
            if (!lookOn) { lookYaw = lookPitch = 0; return; }
            active = true;
            yawDeg += lookYaw; pitchDeg += lookPitch;
            lookYaw = lookPitch = 0;
        }
    }

    // ---- layout ----
    private enum Kind { Stick, Look, Button, AimStick }
    private enum Btn { Fire, Jump, Alt, Weapon, L, Start, Count }
    private static readonly ushort[] BtnBits = { N64.Z, N64.R, N64.B, N64.A, N64.L, N64.Start };
    private static readonly string[] BtnLetter = { "Z", "R", "B", "A", "L", "START" };
    private static readonly string[] BtnName = { "FIRE", "JUMP", "ALT", "WEAPON", "", "" };
    private static readonly string[] BtnMenuName = { "", "", "BACK", "OK", "", "" };

    private struct Circle { public Vector2 C; public float R; public bool Contains(Vector2 p, float slack = 1.15f) => Vector2.DistanceSquared(p, C) <= R * R * slack * slack; }
    private readonly Circle[] buttons = new Circle[(int)Btn.Count];
    private Vector2 stickHome, aimHome; private float stickR, knobR;
    private float unit;
    private int layoutW, layoutH;
    private float StickZoneRight => layoutW * 0.40f;
    private float AimZoneLeft => layoutW * 0.55f;

    private sealed class Finger { public Kind Kind; public Btn Button; public Vector2 Start, Last, Pos, Origin; public bool Moved; }
    private readonly Dictionary<int, Finger> fingers = new();
    private readonly int[] heldUntil = new int[(int)Btn.Count];
    private int frame;
    private Vector2 stickCentre, stickKnob; private bool stickActive;
    private Vector2 aimCentre, aimKnob; private bool aimActive;
    private float fade;            // 0 hidden .. 1 shown

    private readonly GraphicsDevice device;
    private readonly SpriteBatch batch;
    private readonly SpriteFont? font, italic;
    private readonly Texture2D disc, ring, ringThin, white;
    private bool menuLabels;

    private static readonly Color Edge = new(214, 140, 36), Gold = new(255, 200, 70), Text = new(236, 226, 206), Fill = new(12, 8, 4);

    public TouchControls(GraphicsDevice device, SpriteFont? font, SpriteFont? italic)
    {
        this.device = device; this.font = font; this.italic = italic ?? font;
        batch = new SpriteBatch(device);
        disc = MakeCircle(device, 192, 0f, 1f);
        ring = MakeCircle(device, 192, 0.86f, 1f);
        ringThin = MakeCircle(device, 192, 0.93f, 1f);
        white = new Texture2D(device, 1, 1); white.SetData(new[] { Color.White });
        if (!OperatingSystem.IsAndroid()) TouchPanel.EnableMouseTouchPoint = true;     // desktop test mode (TRW_TOUCH=1): the mouse is a finger
        fade = ActiveInput.Device == InputDeviceKind.Touch ? 1 : 0;
        Console.Error.WriteLine($"[touch] on-screen controls (look sensitivity {Config.LookSensitivity:0.##}, size {Config.Size:0.##}; {Settings.FilePath})");
    }

    /// <summary>An anti-aliased white disc or ring (inner..outer radius as fractions), premultiplied.</summary>
    private static Texture2D MakeCircle(GraphicsDevice device, int size, float inner, float outer)
    {
        var px = new Color[size * size];
        float r = size / 2f, aa = 1.5f / r;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r, d = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp((outer - d) / aa, 0, 1) * (inner <= 0 ? 1 : Math.Clamp((d - inner) / aa, 0, 1));
                byte v = (byte)(a * 255 + 0.5f);
                px[y * size + x] = new Color(v, v, v, v);
            }
        var t = new Texture2D(device, size, size, false, SurfaceFormat.Color);
        t.SetData(px);
        return t;
    }

    private void Layout(int w, int h)
    {
        if (w == layoutW && h == layoutH) return;
        layoutW = w; layoutH = h;
        // one unit = 1 % of the screen height (landscape); on a very wide or a portrait screen the width limits it
        unit = Math.Min(h, w * 0.62f) / 100f * Config.Size;
        float u = unit, m = 3f * u;          // margin from the screen edges
        stickR = 14f * u; knobR = 6.5f * u;
        stickHome = new Vector2(m + stickR + 4 * u, h - m - stickR - 17 * u);     // above the HUD's health counter (bottom left)
        aimHome = new Vector2(w - m - 62f * u, h - m - stickR - 17 * u);          // left of the button cluster, mirroring the move stick
        Vector2 br = new(w - m, h - m);
        buttons[(int)Btn.Jump] = new Circle { C = br + new Vector2(-8.5f * u, -8.5f * u), R = 8.5f * u };
        buttons[(int)Btn.Fire] = new Circle { C = br + new Vector2(-29f * u, -14f * u), R = 10.5f * u };
        buttons[(int)Btn.Alt] = new Circle { C = br + new Vector2(-8.5f * u, -29.5f * u), R = 7f * u };
        buttons[(int)Btn.Weapon] = new Circle { C = br + new Vector2(-26f * u, -36f * u), R = 7f * u };
        buttons[(int)Btn.L] = new Circle { C = br + new Vector2(-8.5f * u, -47.5f * u), R = 5.5f * u };
        buttons[(int)Btn.Start] = new Circle { C = new Vector2(w / 2f, m + 4.5f * u), R = 4.5f * u };
        TouchPanel.DisplayWidth = w; TouchPanel.DisplayHeight = h;
    }

    /// <summary>Once per window update: reads the touches and the other devices, updates the pad state the game reads.</summary>
    public void Update(int w, int h, bool active)
    {
        Layout(w, h);
        frame++;
        int ctx = TrwWindow.GameplayContext();
        bool game = ctx != 0, menu = ctx != 1;           // -1 (state unknown, or a pause screen): both layers, like the keyboard / gamepad mapping
        menuLabels = ctx == 0;
        ReportOtherDevices();

        var touches = active ? TouchPanel.GetState() : default;
        bool any = false;
        var seen = new HashSet<int>();
        foreach (var t in touches)
        {
            seen.Add(t.Id);
            var p = t.Position;
            if (t.State == TouchLocationState.Pressed || (t.State == TouchLocationState.Moved && !fingers.ContainsKey(t.Id)))
            {
                any = true;
                var f = new Finger { Start = p, Last = p, Pos = p, Origin = p };
                int hit = -1;
                for (int i = 0; i < buttons.Length; i++) if (Visible(i, ctx) && buttons[i].Contains(p)) { hit = i; break; }
                if (hit >= 0) { f.Kind = Kind.Button; f.Button = (Btn)hit; heldUntil[hit] = frame + 4; if (Config.Haptics && fade > 0.5f) Haptic?.Invoke(); }
                else if (p.X < StickZoneRight && p.Y > h * 0.22f && !stickActive)
                {
                    f.Kind = Kind.Stick; stickActive = true;
                    // floating stick: the base comes to the thumb (kept fully on the screen)
                    stickCentre = new Vector2(Math.Clamp(p.X, stickR + unit, w - stickR), Math.Clamp(p.Y, stickR + unit, h - stickR - unit));
                    stickKnob = p;
                }
                else if (Config.LookStick && p.X > AimZoneLeft && p.Y > h * 0.22f && !aimActive)
                {
                    f.Kind = Kind.AimStick; aimActive = true;
                    aimCentre = new Vector2(Math.Clamp(p.X, stickR + unit, w - stickR - unit), Math.Clamp(p.Y, stickR + unit, h - stickR - unit));
                    aimKnob = p;
                }
                else f.Kind = Kind.Look;
                fingers[t.Id] = f;
            }
            else if (fingers.TryGetValue(t.Id, out var f) && (t.State == TouchLocationState.Moved || t.State == TouchLocationState.Pressed))
            {
                any = true;
                f.Pos = p;
            }
            else if (t.State == TouchLocationState.Released && fingers.TryGetValue(t.Id, out var fr)) { Release(t.Id, fr); any = true; }
        }
        foreach (var id in fingers.Keys.Where(k => !seen.Contains(k)).ToList()) Release(id, fingers[id]);     // lost touches (focus change)
        if (any) ActiveInput.Report(InputDeviceKind.Touch);
        bool shown = ActiveInput.Device == InputDeviceKind.Touch;
        fade = Math.Clamp(fade + (shown ? 0.12f : -0.06f), 0, 1);

        // the pad state from the fingers
        ushort b = 0; int sx = 0, sy = 0; float yaw = 0, pitch = 0;
        bool weaponHeld = false;
        foreach (var f in fingers.Values) if (f.Kind == Kind.Button && f.Button == Btn.Weapon) weaponHeld = true;
        if (frame < heldUntil[(int)Btn.Weapon]) weaponHeld = true;
        float degPerPx = 0.11f * 1080f / Math.Max(h, 1) * Config.LookSensitivity;
        foreach (var f in fingers.Values)
        {
            var d = f.Pos - f.Last;
            switch (f.Kind)
            {
                case Kind.Button:
                    b |= BtnBits[(int)f.Button];
                    if (f.Button == Btn.Fire && game) { yaw += d.X * degPerPx; pitch += d.Y * degPerPx; }      // aim while firing
                    break;
                case Kind.Stick:
                {
                    var v = (f.Pos - stickCentre) / stickR;
                    if (v.LengthSquared() > 1) v.Normalize();
                    stickKnob = stickCentre + v * stickR;
                    const float th = 0.38f;
                    if (game) { if (v.Y < -th) b |= N64.CUp; if (v.Y > th) b |= N64.CDown; if (v.X < -th) b |= N64.CLeft; if (v.X > th) b |= N64.CRight; }
                    if (menu) { if (v.Y < -0.5f) b |= N64.DUp; if (v.Y > 0.5f) b |= N64.DDown; if (v.X < -0.5f) b |= N64.DLeft; if (v.X > 0.5f) b |= N64.DRight; }
                    break;
                }
                case Kind.AimStick:
                {
                    var v = (f.Pos - aimCentre) / stickR;
                    if (v.LengthSquared() > 1) v.Normalize();
                    aimKnob = aimCentre + v * stickR;
                    if (ctx == 1)
                    {   // free play: the analogue stick (aim / turn; the weapon wheel while WEAPON is held); a small dead zone so a resting thumb does not drift
                        float len = v.Length(), dz = 0.12f;
                        var av = len > dz ? v * ((len - dz) / (1 - dz) / len) : Vector2.Zero;
                        sx += (int)(av.X * N64.StickMax); sy -= (int)((Config.InvertY ? -av.Y : av.Y) * N64.StickMax);
                    }
                    else { const float th = 0.5f; if (v.Y < -th) b |= N64.CUp; if (v.Y > th) b |= N64.CDown; if (v.X < -th) b |= N64.CLeft; if (v.X > th) b |= N64.CRight; }
                    break;
                }
                case Kind.Look:
                    if (weaponHeld && game)
                    {   // the weapon wheel: the drag from where the finger landed is the stick
                        var v = (f.Pos - f.Origin) / stickR;
                        if (v.LengthSquared() > 1) v.Normalize();
                        sx += (int)(v.X * N64.StickMax); sy -= (int)(v.Y * N64.StickMax);
                    }
                    else if (ctx == 1) { yaw += d.X * degPerPx; pitch += d.Y * degPerPx; }
                    break;
            }
            f.Last = f.Pos;
        }
        for (int i = 0; i < heldUntil.Length; i++) if (frame < heldUntil[i]) b |= BtnBits[i];      // a quick tap lasts a few frames, so the game's poll sees it
        lock (gate)
        {
            if (backPulse > 0) { backPulse--; b |= ctx == 1 ? N64.Start : N64.B; }
            outButtons = b; outX = Math.Clamp(sx, -N64.StickMax, N64.StickMax); outY = Math.Clamp(sy, -N64.StickMax, N64.StickMax);
            if (Config.InvertY) pitch = -pitch;
            lookOn = ctx == 1 && ActiveInput.Device == InputDeviceKind.Touch && !weaponHeld;
            if (lookOn) { lookYaw += yaw; lookPitch += pitch; } else { lookYaw = lookPitch = 0; }
        }
    }

    private void Release(int id, Finger f)
    {
        if (f.Kind == Kind.Stick) stickActive = false;
        if (f.Kind == Kind.AimStick) aimActive = false;
        fingers.Remove(id);
    }

    private bool Visible(int i, int ctx) => true;

    // physical devices: a gamepad or keyboard in use takes the prompts (and hides the touch controls) until the next touch
    private readonly bool[] padWasActive = new bool[4];
    private void ReportOtherDevices()
    {
        for (int i = 0; i < 4; i++)
        {
            var gp = GamePad.GetState(i, GamePadDeadZone.Circular);
            if (!gp.IsConnected) continue;
            var bt = gp.Buttons; var dp = gp.DPad;
            bool used = bt.A == ButtonState.Pressed || bt.B == ButtonState.Pressed || bt.X == ButtonState.Pressed || bt.Y == ButtonState.Pressed ||
                        bt.Start == ButtonState.Pressed || bt.LeftShoulder == ButtonState.Pressed || bt.RightShoulder == ButtonState.Pressed ||
                        dp.Up == ButtonState.Pressed || dp.Down == ButtonState.Pressed || dp.Left == ButtonState.Pressed || dp.Right == ButtonState.Pressed ||
                        gp.ThumbSticks.Left.LengthSquared() > 0.09f || gp.ThumbSticks.Right.LengthSquared() > 0.09f || gp.Triggers.Left > 0.3f || gp.Triggers.Right > 0.3f;
            if (used && !padWasActive[i])
            {
                var name = (GamePad.GetCapabilities(i).DisplayName ?? "").ToLowerInvariant();
                bool ps = name.Contains("sony") || name.Contains("dualshock") || name.Contains("dualsense") || name.Contains("playstation") || name.Contains("ps4") || name.Contains("ps5") || name.Contains("wireless controller");
                ActiveInput.Report(ps ? InputDeviceKind.PlayStation : InputDeviceKind.Xbox);
            }
            padWasActive[i] = used;
        }
        if (OperatingSystem.IsAndroid() && Keyboard.GetState().GetPressedKeys().Any(k => k is not (Keys.VolumeUp or Keys.VolumeDown or Keys.VolumeMute or Keys.None)))
            ActiveInput.Report(InputDeviceKind.KeyboardMouse);
    }

    // ---- drawing (GfxRenderer.Overlay: over the picture, into the back buffer of w x h) ----
    public void Draw(int w, int h)
    {
        if (fade <= 0.01f || w < 64 || h < 64) return;
        Layout(w, h);
        float aAll = fade * Config.Opacity, a = aAll;
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        int ctx = TrwWindow.GameplayContext();
        // move stick: the base at home (or where the thumb put it) and the knob
        var c = stickActive ? stickCentre : stickHome;
        var k = stickActive ? stickKnob : stickHome;
        Sprite(disc, c, stickR, Fill * (0.30f * a));
        Sprite(ring, c, stickR, Edge * (0.45f * a));
        for (int i = 0; i < 4; i++)
        {   // direction marks
            var dir = new Vector2(MathF.Cos(i * MathF.PI / 2), MathF.Sin(i * MathF.PI / 2));
            Sprite(disc, c + dir * stickR * 0.78f, unit * 0.9f, Edge * (0.45f * a));
        }
        Sprite(disc, k, knobR, (stickActive ? Edge : Fill) * ((stickActive ? 0.55f : 0.45f) * a));
        Sprite(ring, k, knobR, Gold * ((stickActive ? 0.8f : 0.55f) * a));
        Label(stickActive ? "" : "MOVE", c +new Vector2(0, stickR + 2.8f * unit), 2.4f * unit, Text * (0.6f * a), italic);
        if (Config.LookStick) DrawStick(aimActive ? aimCentre : aimHome, aimActive ? aimKnob : aimHome, aimActive, menuLabels ? "C" : "AIM", menuLabels, menuLabels && !aimActive ? a * 0.45f : a);      // menus: steps back like FIRE / JUMP (it covers the 4:3 menus; few screens use the C buttons)

        bool[] pressed = new bool[buttons.Length];
        foreach (var f in fingers.Values) if (f.Kind == Kind.Button) pressed[(int)f.Button] = true;
        for (int i = 0; i < buttons.Length; i++)
        {
            var bc = buttons[i];
            bool on = pressed[i] || frame < heldUntil[i];
            // menus: A / B / START / the stick do the work; FIRE, JUMP and L stay usable but step back (they cover menu text on the 4:3 screens)
            float ba = menuLabels && !on && i is (int)Btn.Fire or (int)Btn.Jump or (int)Btn.L ? aAll * 0.45f : aAll;
            Sprite(disc, bc.C, bc.R, (on ? Edge : Fill) * ((on ? 0.6f : 0.3f) * ba));
            Sprite(i == (int)Btn.Start ? ringThin : ring, bc.C, bc.R, (on ? Gold : Edge) * ((on ? 0.85f : 0.55f) * ba));
            string name = menuLabels ? BtnMenuName[i] : BtnName[i];
            if (i == (int)Btn.Start)
            {
                if (!menuLabels) Label("START", bc.C + new Vector2(0, bc.R + 2.2f * unit), 2.2f * unit, Text * (0.75f * ba), font);      // (menus: the icon only, the label would cover menu titles)
                PauseIcon(bc.C, bc.R, Gold * (0.85f * ba));
                continue;
            }
            float letterH = bc.R * (name.Length > 0 ? 0.95f : 1.15f);
            Label(BtnLetter[i], bc.C + new Vector2(0, name.Length > 0 ? -bc.R * 0.16f : 0), letterH, (on ? Color.White : Gold) * ((on ? 0.95f : 0.7f) * ba), font);
            if (name.Length > 0) Label(name, bc.C + new Vector2(0, bc.R * 0.5f), Math.Min(bc.R * 0.36f, 2.6f * unit) * Math.Min(1f, 6f / Math.Max(4, name.Length)), Text * (0.7f * ba), italic);
        }
        batch.End();
    }

    /// <summary>The right stick: base, direction marks, knob; in menus the knob carries a C (it gives the C buttons there).</summary>
    private void DrawStick(Vector2 c, Vector2 k, bool active, string label, bool cKnob, float a)
    {
        Sprite(disc, c, stickR, Fill * (0.30f * a));
        Sprite(ring, c, stickR, Edge * (0.45f * a));
        for (int i = 0; i < 4; i++)
        {
            var dir = new Vector2(MathF.Cos(i * MathF.PI / 2), MathF.Sin(i * MathF.PI / 2));
            Sprite(disc, c + dir * stickR * 0.78f, unit * 0.9f, (cKnob ? Gold : Edge) * (0.45f * a));
        }
        Sprite(disc, k, knobR, (active ? Edge : Fill) * ((active ? 0.55f : 0.45f) * a));
        Sprite(ring, k, knobR, Gold * ((active ? 0.8f : 0.55f) * a));
        if (cKnob) Label("C", k, knobR * 1.1f, Gold * (0.8f * a), font);
        if (!active) Label(label, c + new Vector2(0, stickR + 2.8f * unit), 2.4f * unit, Text * (0.6f * a), italic);
    }

    private void PauseIcon(Vector2 c, float r, Color col)
    {
        float bw = r * 0.22f, bh = r * 0.8f, gap = r * 0.16f;
        batch.Draw(white, new Rectangle((int)(c.X - gap - bw), (int)(c.Y - bh / 2), (int)bw, (int)bh), col);
        batch.Draw(white, new Rectangle((int)(c.X + gap), (int)(c.Y - bh / 2), (int)bw, (int)bh), col);
    }

    private void Sprite(Texture2D t, Vector2 c, float r, Color col) => batch.Draw(t, new Rectangle((int)(c.X - r), (int)(c.Y - r), (int)(2 * r), (int)(2 * r)), col);

    private void Label(string s, Vector2 centre, float height, Color col, SpriteFont? f)
    {
        if (f == null || s.Length == 0) return;
        var sz = f.MeasureString(s);
        float scale = height / Math.Max(1, f.LineSpacing);
        batch.DrawString(f, s, centre - sz * scale / 2 + new Vector2(0, scale * f.LineSpacing * 0.06f), col, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }

    public void Dispose()
    {
        batch.Dispose(); disc.Dispose(); ring.Dispose(); ringThin.Dispose(); white.Dispose();
    }
}
