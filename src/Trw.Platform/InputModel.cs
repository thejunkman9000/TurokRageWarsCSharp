namespace Trw.Platform;

/// <summary>N64 controller button bits (the order the game's OSContPad uses).</summary>
public static class N64
{
    public const ushort A = 0x8000, B = 0x4000, Z = 0x2000, Start = 0x1000, DUp = 0x0800, DDown = 0x0400, DLeft = 0x0200, DRight = 0x0100,
        L = 0x0020, R = 0x0010, CUp = 0x0008, CDown = 0x0004, CLeft = 0x0002, CRight = 0x0001;
    public const int StickMax = 80;            // a real stick reports about +-80 at the gate; the game clamps to it
    public const int GameStickDeadzone = 3;    // the game's own dead zone (func_00263E1C): |v| <= 3 reads as 0, otherwise 3 is subtracted
}

/// <summary>Gamepad state in the XInput layout (stick Y up is positive, triggers 0..1).</summary>
public struct PadSnapshot
{
    public bool Connected, A, B, X, Y, LB, RB, Start, Back, DUp, DDown, DLeft, DRight;
    public float LX, LY, RX, RY, LT, RT;
}

/// <summary>One sample of the player's physical devices, taken once per game poll.</summary>
public sealed class InputSample
{
    public Func<string, bool> KeyDown = _ => false;      // MonoGame Keys names ("W", "LeftControl", "Space", "Left", "NumPad8", ...)
    public bool[] MouseButtons = new bool[5];             // left, right, middle, X1, X2
    public float MouseDx, MouseDy;                        // counts since the previous sample
    public bool MouseValid;
    public int WheelUp, WheelDown;                        // notches since the previous sample
    public PadSnapshot Pad;
    public int Gameplay = -1;                             // 1 free play, 0 menus / pause, -1 unknown (both binding layers live)
    public double Dt = 1.0 / 60;
}

/// <summary>
/// The input mapping of the C port (input_core.c): the built-in <c>kbm_fps</c> keyboard + mouse layout and the <c>xbox</c> gamepad layout merged for player 1
/// with the default settings (<c>mouse_sens 1.0</c>, <c>stick_deadzone 0.2</c>, <c>axis_threshold 0.5</c>, <c>trigger_threshold 0.3</c>, <c>key_stick 80</c>),
/// including the mouse look through the stick: the game's turn rate is cubic in the stick value after its dead zone, so mouse motion is converted to the stick
/// value that turns the view by exactly the asked angle (1 count = 0.05 degrees).
/// </summary>
public sealed class InputMapper
{
    public const float YawRatePerE3 = 1.978e-4f, PitchRatePerE3 = 3.10e-4f, DegPerCount = 0.05f;
    public float MouseSens = 1f, MouseBacklog = 30f, StickDeadzone = 0.2f, TriggerThreshold = 0.3f, AxisThreshold = 0.5f;
    public int KeyStick = N64.StickMax;
    public bool MouseInvertY;

    private float pendYaw, pendPitch;
    private readonly int[] wheelPend = new int[2];      // up, down
    private readonly bool[] wheelOn = new bool[2];

    public void ResetMouse() { pendYaw = pendPitch = 0; }

    /// <summary>
    /// One axis of the mouse look. The motion is queued as degrees of turn; every sample delivers as much of it as one poll interval of the game's turn rate
    /// allows (the stick value n whose rate k * n^3 deg/s turns the view by the pending angle during dt), the rest stays queued up to <paramref name="backlog"/>.
    /// The game subtracts its dead zone of 3 from the stick, so the delivered n arrives as n + 3. Port of mouse_axis() (without smoothing, which defaults to off).
    /// </summary>
    public static int MouseAxis(ref float pend, float counts, float sens, float dt, float k, float backlog)
    {
        float q = counts * sens * DegPerCount;
        pend += q;
        if (pend > backlog) pend = backlog; else if (pend < -backlog) pend = -backlog;
        if (MathF.Abs(pend) < 1e-6f) { pend = 0; return 0; }
        int sign = pend < 0 ? -1 : 1;
        float w = MathF.Abs(pend) / dt;                          // degrees per second needed to deliver it within one interval
        float e = MathF.Cbrt(w / k);
        int n = (int)(e + 0.5f);
        if (n > N64.StickMax - N64.GameStickDeadzone) n = N64.StickMax - N64.GameStickDeadzone;
        if (n < 1) return 0;                                      // below the first stick step: stays queued until more motion arrives
        pend -= sign * k * n * n * n * dt;
        if (pend * sign < 0 && MathF.Abs(pend) < 1e-3f) pend = 0;
        return sign * (n + N64.GameStickDeadzone);
    }

    /// <summary>Processed gamepad stick (radial dead zone rescaled to 0..1, sensitivity 1): returns the component of the stick at (x, y).</summary>
    public static (float x, float y) RadialStick(float x, float y, float dz)
    {
        float m = MathF.Sqrt(x * x + y * y);
        if (m <= dz || m < 1e-6f) return (0, 0);
        float s = (MathF.Min(m, 1f) - dz) / (1f - dz);
        return (Math.Clamp(x / m * s, -1f, 1f), Math.Clamp(y / m * s, -1f, 1f));
    }

    private static bool Ctx(int source, int gameplay) => source == 0 || gameplay < 0 || (source > 0 ? gameplay == 1 : gameplay == 0);      // source: 0 any, +1 game:, -1 menu:

    public (ushort buttons, int x, int y) Evaluate(InputSample s)
    {
        float dt = (float)Math.Clamp(s.Dt > 0 ? s.Dt : 1.0 / 60, 1.0 / 120, 0.1);
        int gp = s.Gameplay;
        int mx = 0, my = 0;
        if (s.MouseValid)
        {
            mx = MouseAxis(ref pendYaw, s.MouseDx, MouseSens, dt, YawRatePerE3, MouseBacklog);
            my = MouseAxis(ref pendPitch, s.MouseDy * (MouseInvertY ? -1f : 1f), MouseSens, dt, PitchRatePerE3, MouseBacklog);
        }
        else { pendYaw = pendPitch = 0; }
        // wheel: every notch becomes one press of one sample, followed by a release so the game sees separate presses
        wheelPend[0] = Math.Min(wheelPend[0] + s.WheelUp, 4); wheelPend[1] = Math.Min(wheelPend[1] + s.WheelDown, 4);
        for (int d = 0; d < 2; d++)
        {
            if (wheelOn[d]) wheelOn[d] = false;
            else if (wheelPend[d] > 0) { wheelPend[d]--; wheelOn[d] = true; }
        }
        bool K(string k) => s.KeyDown(k);
        bool M(int b) => s.MouseButtons[b];
        bool game = Ctx(1, gp), menu = Ctx(-1, gp);
        var pd = s.Pad;
        var (lx, ly) = pd.Connected ? RadialStick(pd.LX, pd.LY, StickDeadzone) : (0f, 0f);
        var (rx, ry) = pd.Connected ? RadialStick(pd.RX, pd.RY, StickDeadzone) : (0f, 0f);
        bool Ax(float v) => v >= AxisThreshold;
        bool Trig(float v) => v >= TriggerThreshold;
        bool pc = pd.Connected;

        ushort b = 0;
        if ((game && M(0)) || K("Z") || K("LeftControl")) b |= N64.Z;
        if (pc && Trig(pd.RT)) b |= N64.Z;
        if (M(1) || K("X") || K("Back")) b |= N64.B;
        if (pc && (pd.B || Trig(pd.LT))) b |= N64.B;
        if (K("E") || K("C") || M(2) || (menu && M(0)) || (game && (wheelOn[0] || wheelOn[1]))) b |= N64.A;
        if (pc && ((menu && pd.A) || (game && (pd.X || pd.Y)))) b |= N64.A;
        if (K("Space") || M(3)) b |= N64.R;
        if (K("Q") || M(4)) b |= N64.L;
        if (K("Enter")) b |= N64.Start;
        if (pc && pd.Start) b |= N64.Start;
        if (pc && pd.LB) b |= N64.L;
        if (pc && (pd.RB || (game && pd.A))) b |= N64.R;
        if (game && K("W")) b |= N64.CUp;
        if (game && K("A")) b |= N64.CLeft;
        if (game && K("S")) b |= N64.CDown;
        if (game && K("D")) b |= N64.CRight;
        if (pc && game)
        {
            if (Ax(ly)) b |= N64.CUp; if (Ax(-ly)) b |= N64.CDown; if (Ax(-lx)) b |= N64.CLeft; if (Ax(lx)) b |= N64.CRight;
        }
        if ((menu && (K("W") || wheelOn[0])) || K("NumPad8")) b |= N64.DUp;
        if ((menu && (K("S") || wheelOn[1])) || K("NumPad2")) b |= N64.DDown;
        if ((menu && K("A")) || K("NumPad4")) b |= N64.DLeft;
        if ((menu && K("D")) || K("NumPad6")) b |= N64.DRight;
        if (pc)
        {
            if (pd.DUp || (menu && Ax(ly))) b |= N64.DUp;
            if (pd.DDown || (menu && Ax(-ly))) b |= N64.DDown;
            if (pd.DLeft || (menu && Ax(-lx))) b |= N64.DLeft;
            if (pd.DRight || (menu && Ax(lx))) b |= N64.DRight;
        }

        // stick: the strongest source per direction (keys give key_stick units, analogue sources move it proportionally)
        float left = 0, right = 0, up = 0, down = 0;
        if (K("Left")) left = Math.Max(left, KeyStick);
        if (K("Right")) right = Math.Max(right, KeyStick);
        if (K("Up")) up = Math.Max(up, KeyStick);
        if (K("Down")) down = Math.Max(down, KeyStick);
        if (mx < 0) left = Math.Max(left, -mx); else if (mx > 0) right = Math.Max(right, mx);
        if (my < 0) up = Math.Max(up, -my); else if (my > 0) down = Math.Max(down, my);       // mouse down = pitch down; MouseY- (up) = stick up
        if (pc)
        {
            if (rx < 0) left = Math.Max(left, -rx * N64.StickMax); else right = Math.Max(right, rx * N64.StickMax);
            if (ry > 0) up = Math.Max(up, ry * N64.StickMax); else down = Math.Max(down, -ry * N64.StickMax);
        }
        int x = (int)MathF.Floor(right - left + 0.5f), y = (int)MathF.Floor(up - down + 0.5f);
        return (b, Math.Clamp(x, -N64.StickMax, N64.StickMax), Math.Clamp(y, -N64.StickMax, N64.StickMax));
    }
}
