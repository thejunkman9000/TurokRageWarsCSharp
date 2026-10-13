using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Widescreen (Hor+) for the 3D views, applied where the game builds each view's projection: func_00234FDC (recompiled) takes a horizontal field of view
/// and an aspect (the view rectangle's w/h when split, 2.667 for 2-player halves, 1.333 otherwise), turns them into tan extents (horizontal = tan(fov) * 1.279,
/// vertical = horizontal / aspect), builds the projection with guPerspectiveF and keeps the extents in the camera (+0x6C..+0x78), which the culling uses.
/// The hooks (tools/mips2cs/hooks.txt) replace the aspect and widen the horizontal extent by the same factor, so the vertical view stays exactly the game's
/// and the culling widens with the picture: nothing pops in at the edges. The weapon viewmodel's projection (the second guPerspectiveF of the function,
/// aspect 1.0 stretched over the 4:3 screen) gets the same factor, so the gun keeps its shape.
/// <para>The aspect wanted for a view: <see cref="ScreenAspect"/> (the window's picture) when the view fills the picture (one view, or <see cref="LocalView"/>:
/// every camera, so all netplay peers compute the same thing), otherwise the view's own aspect times ScreenAspect / (4/3) (each split view keeps its share of
/// a wider screen). At 4:3 nothing changes: the ROM's values, bit for bit (the oracle runs and plain 4:3 windows are unaffected).</para>
/// <para>The values land in guest RAM (camera, matrices), so netplay must agree on <see cref="ScreenAspect"/> and the view mode: the session sets them for
/// everyone (see Net). Offline the window sets them.</para>
/// </summary>
public static class Widescreen
{
    public const float Native = 4f / 3f;

    /// <summary>Aspect of the picture the 3D is presented in (window width / height, or the netplay session's). 4/3 = the game's own projection.</summary>
    public static volatile float ScreenAspect = float.TryParse(Environment.GetEnvironmentVariable("TRW_ASPECT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ea) && ea > 0.5f && ea < 4f ? ea : Native;     // TRW_ASPECT: initial value (headless tests; the window replaces it)

    /// <summary>TRW_WIDESCREEN=0 keeps the game's 4:3 projection whatever the window shape (the picture is then pillarboxed).</summary>
    public static readonly bool Allowed = Environment.GetEnvironmentVariable("TRW_WIDESCREEN") != "0";

    /// <summary>Set by the window (offline) or by the netplay session (shared by every peer): the aspect the 3D views should be built for.</summary>
    public static void SetScreenAspect(float a) { if (!Locked) ScreenAspect = Allowed && a > 0.5f && a < 4f ? a : Native; }

    /// <summary>A netplay session fixed the aspect and the view mode for every peer (the window can no longer change them: the picture letterboxes instead).</summary>
    public static bool Locked { get; private set; }

    /// <summary>Netplay: the host's aspect and view mode, applied on every peer (host included) before the boot.</summary>
    public static void ApplySession(float aspect, bool localView)
    {
        ScreenAspect = aspect > 0.5f && aspect < 4f ? aspect : Native;
        LocalView.Enabled = localView;
        Locked = true;
        Console.Error.WriteLine($"[view] session: aspect {ScreenAspect:F3}, {(localView ? "own view" : "split screen")}");
    }

    /// <summary>True when the 3D is built wider than the game's 4:3 (the renderer then maps 3D to the full picture width and keeps 2D at 4:3).</summary>
    public static bool Active => Math.Abs(ScreenAspect - Native) > 0.01f || LocalFill;

    /// <summary>The game is in a level (8 = recorded demo, 13 = playing): only then are cameras widened and frames presented wide; menus stay 4:3.
    /// The hook and the renderer use the same test, so a frame is never built wide and shown narrow.</summary>
    public static bool InLevel => Mem.S32(0x80146878) is 8 or 13;       // D_80146878 = the game state (read from guest RAM: the game thread's own view)

    /// <summary>Local view: every camera is built for the whole picture.</summary>
    public static bool LocalFill => LocalView.Enabled;

    [ThreadStatic] private static float lastK;

    /// <summary>Hook after func_00234FDC 0x002355B0 (f20 = horizontal tan extent, f23 = the game's aspect, r19 = the camera).</summary>
    public static void CamAspect(uint cam)
    {
        lastK = 1f;
        if (!Active || !InLevel) return;
        float a = Cpu.S(23);
        if (!(a > 0.1f && a < 10f)) return;
        float want;
        bool single = Math.Abs(a - Native) < 0.01f;
        if (single || LocalFill) want = ScreenAspect;
        else want = a * (ScreenAspect / Native);
        if (Math.Abs(want - a) < 1e-4f) return;
        lastK = want / a;
        Cpu.SetS(20, Cpu.S(20) * lastK);
        Cpu.SetS(23, want);
    }

    /// <summary>Hook after func_00234FDC 0x00235910 (a3 = the viewmodel projection's aspect, about to be passed to guPerspectiveF).</summary>
    public static uint ViewmodelAspect(uint a3)
    {
        if (Math.Abs(lastK - 1f) < 1e-6f) return a3;
        return Cpu.F2U(Cpu.U2F(a3) * lastK);
    }
}
