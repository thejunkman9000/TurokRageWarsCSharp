using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Native FPS mouse look, the port of the C port's native_controls (port/overrides/Player_UpdateViewTurn.c there): the mouse turns the player's view by exactly the motion
/// and sets the view tilt to an absolute, 1:1 pitch, instead of being converted into analogue-stick values (whose cubic turn rate, dead zone and speed cap the
/// stick path imitates). Called from the game's view update Player_UpdateViewTurn (tools/overrides/Player_UpdateViewTurn.c): <see cref="trw_mouse_reset"/> at its start,
/// <see cref="trw_mouse_look"/> after the game's own turn / tilt.
/// <para>Determinism (netplay): the motion is latched with each controller read (<see cref="Host.ReadMouse"/>, part of the controller sample and of the netplay
/// frame) and consumed by the view update on the game thread; the pitch kept per controller slot is derived from that input only.</para>
/// <para>Only actors driven by a real controller slot get the mouse (controller objects 0x224 bytes from Gv.Pad_Slots, slot -1 = the merged slot of 1-player
/// modes): bots carry their own AI controller object and were turned by the player's mouse in the C port before it checked this.</para>
/// </summary>
public static partial class Game
{
    private const uint CtlBase = Gv.Pad_Slots, CtlStride = 0x224;
    private const float Deg = 0.0174532942f;

    // motion latched at the controller reads, not yet applied: the last 4 reads per pad. While the game skips its view update (some player states do for a few
    // hundred ms) older motion is dropped, as a real stick deflection would be, instead of landing in one frame as a sudden jump (the C port's 4590778)
    private static readonly float[,] latchYaw = new float[4, 4], latchPitch = new float[4, 4];
    private static readonly int[] latchCount = new int[4], latchNext = new int[4];
    private static readonly float[] lookPitch = new float[5];                               // degrees, + = up, per slot -1..3

    /// <summary>Speed slider factor (the game's options: turn speed b79 / look speed b7A, 0..255, centre 128): 1/8 x at 0, 1 x at 128, 8 x at 255.
    /// A table, so every machine uses the same float values.</summary>
    private static readonly float[] sliderScale = Enumerable.Range(0, 256).Select(b => (float)Math.Pow(8.0, (b - 128) / 128.0)).ToArray();

    private static readonly bool[] mouseActive = new bool[4];      // the pad is played with the mouse (from its latest controller read)

    internal static void MouseLatch(int pad, float yaw, float pitch, bool active)
    {
        if ((uint)pad >= 4) return;
        mouseActive[pad] = active;
        if (yaw == 0 && pitch == 0 && latchCount[pad] == 0) return;
        int k = latchNext[pad];
        latchYaw[pad, k] = yaw; latchPitch[pad, k] = pitch;
        latchNext[pad] = (k + 1) & 3;
        if (latchCount[pad] < 4) latchCount[pad]++;
    }

    private static void TakeLatched(int pad, out float yaw, out float pitch)
    {
        yaw = pitch = 0;
        for (int i = 0, k = latchNext[pad]; i < latchCount[pad]; i++) { k = (k + 3) & 3; yaw += latchYaw[pad, k]; pitch += latchPitch[pad, k]; }
        latchCount[pad] = 0;
    }

    /// <summary>The controller slot (-1 merged, 0..3) of the player record <paramref name="p"/>, or -2 when its controller is not a real slot (bots).</summary>
    private static int HumanSlot(uint p)
    {
        uint ctl = Mem.U32(p + 0x698);
        if (ctl + CtlStride < CtlBase || ctl >= CtlBase + 4 * CtlStride) return -2;
        uint off = ctl + CtlStride - CtlBase;
        if (off % CtlStride != 0) return -2;
        return (int)(off / CtlStride) - 1;
    }

    /// <summary>Start of Player_UpdateViewTurn: dead or in a cut-scene (the view update is skipped) - the next life starts with a level view.</summary>
    public static void trw_mouse_reset(uint p)
    {
        if (Mem.S32(p + 0x11B4) == 0 && !(Mem.F32(p + 0x11D8) > 0f)) return;
        int sl = HumanSlot(p);
        if (sl >= -1) { lookPitch[sl + 1] = 0; TakeLatched(Math.Max(sl, 0), out _, out _); }
    }

    /// <summary>After the game's own turn and tilt in Player_UpdateViewTurn (p = player record, a = actor, input = the pad output, h = what the actor stands on).</summary>
    public static void trw_mouse_look(uint p, uint a, uint input, uint h)
    {
        int sl = HumanSlot(p);
        if (lookLog && Interlocked.Increment(ref lookLogN) < 400 && (sl >= -1 || lookLogN < 20))
            Console.Error.WriteLine($"[look] p {p:X8} ctl {Mem.U32(p + 0x698):X8} slot {sl} active {(sl >= -1 && mouseActive[Math.Max(sl, 0)])} latched {(sl >= -1 ? latchCount[Math.Max(sl, 0)] : -1)}");
        if (sl < -1) return;
        int pad = Math.Max(sl, 0);
        TakeLatched(pad, out float dyaw, out float dtilt);
        if (!NativeMouse || !mouseActive[pad]) { lookPitch[sl + 1] = 0; return; }        // a pad / stick player: the game's own look, untouched
        uint st = Mem.U32(p + 0x5D8);
        dyaw *= sliderScale[Mem.U8(st + 0x79)];
        dtilt *= sliderScale[Mem.U8(st + 0x7A)];
        if (h == 0 || (Mem.U32(h + 0x50) & 0x6000) != 0x2000)                    // not on a surface that steers the actor itself (the game's own condition)
            Mem.Set(a + 0x6C, Mem.F32(a + 0x6C) + dyaw * Deg);
        if (lookLog && (dyaw != 0 || dtilt != 0)) Console.Error.WriteLine($"[look] tick {CurrentTick}: actor {a:X8} yaw {dyaw:F2} -> ang {Mem.F32(a + 0x6C) * 57.29578f:F2}, tilt {dtilt:F2}");
        // view tilt (degrees, + = up) = -f6A0 * 80 * 0.71619713 * (f14 above the level, f10 below): the game rebuilds f6A0 / f68C from the pad every frame
        // (in the default control style they spring back to the centre), so the pitch is kept here and the view is set to it, within the game's limits
        float f10 = Mem.F32(input + 0x10), f14 = Mem.F32(input + 0x14);
        float up = 80f * 0.716197133f * MathF.Abs(f14), dn = 80f * 0.716197133f * MathF.Abs(f10);
        ref float pp = ref lookPitch[sl + 1];
        pp -= dtilt;
        if (pp > up) pp = up;
        if (pp < -dn) pp = -dn;
        float t = pp >= 0 ? up : dn;
        t = t > 0.001f ? -pp / t : 0f;                                           // the matching tilt input, for the code that reads it (aiming)
        Mem.Set(p + 0x6A0, t);
        Mem.Set(p + 0x68C, t);
        Mem.Set(p + 0x724, pp * Deg);
    }

    /// <summary>TRW_NATIVE_MOUSE=0 turns the native look off (the mouse then drives the analogue stick, as on a pad).</summary>
    private static readonly bool lookLog = Environment.GetEnvironmentVariable("TRW_LOOKLOG") == "1";      // TRW_LOOKLOG=1: what the look hook sees
    private static int lookLogN;

    public static readonly bool NativeMouse = Environment.GetEnvironmentVariable("TRW_NATIVE_MOUSE") != "0";
}
