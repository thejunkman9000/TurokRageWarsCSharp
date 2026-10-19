using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Widescreen and the full-height own view, chosen per machine like any PC game: nothing about the window reaches guest RAM, so every netplay peer can run
/// its own resolution, aspect and view mode while the simulation stays identical.
/// <list type="bullet">
/// <item>Game side (the same on every machine): Camera.SetupProjection (recompiled) builds each view's projection from a horizontal field of view and the view's aspect and
/// keeps the frustum extents at the far plane in the camera (+0x74 horizontal, +0x78 vertical), which the culling reads. A hook (tools/mips2cs/hooks.txt)
/// widens those two extents by fixed factors (<see cref="CullH"/> x <see cref="CullV"/>): enough for a 32:9 picture and for a 2-player half shown full height.
/// The projection matrix stays exactly the game's.</item>
/// <item>Presentation (per machine, GfxRenderer): in a level the frame renders into a target of the window's aspect, and every perspective draw is rescaled
/// about the centre of its view (the view = its scissor rectangle) from the game's projection aspect to the aspect the view has on this screen: Hor+ when
/// wider (more to the sides), Vert+ when taller (the own view of a 2-player half shows more above and below). 2D keeps its 4:3 shape.</item>
/// </list>
/// Oracle runs keep the ROM's culling (bit-identical recordings); <c>TRW_WIDESCREEN=0</c> presents the original 4:3 picture.
/// </summary>
public static class Widescreen
{
    public const float Native = 4f / 3f;
    public const float CullH = 3f, CullV = 2f;

    /// <summary>TRW_WIDE_CULL=0 keeps the ROM's culling (the picture edges then lose geometry in wide windows; for comparisons with the ROM). Netplay peers
    /// must agree on it, as on every setting that reaches guest RAM.</summary>
    public static readonly bool WideCulling = Environment.GetEnvironmentVariable("TRW_WIDE_CULL") != "0";

    /// <summary>TRW_WIDESCREEN=0: present the game's 4:3 picture (pillarboxed) whatever the window shape.</summary>
    public static readonly bool Allowed = Environment.GetEnvironmentVariable("TRW_WIDESCREEN") != "0";

    /// <summary>A level is on screen without a menu: game state 8 (recorded demo) or 13 (playing) AND the UI shows only the HUD layer (screen 2) or nothing (-1).
    /// Only then are frames presented wide; the title screen and the menus (which run inside state 8, the attract sequence: screens 24, 3, 19, ...) and menus
    /// opened over a match stay 4:3, pillarboxed.</summary>
    public static bool InLevel => Mem.S32(Gv.GameState) is 8 or 13 && Game.ScreenId is -1 or 2;       // D_80146878 = the game state

    /// <summary>Hook after Camera.SetupProjection 0x00235610 (camera r19: +0x74 just stored; $f0 = the +0x78 value stored in the next call's delay slot).
    /// Fixed factors, independent of this machine's window, so netplay peers stay identical.</summary>
    public static void WidenCulling(uint cam)
    {
        if (Oracle.Enabled || !WideCulling) return;
        Mem.Set(cam + 0x74, Mem.F32(cam + 0x74) * CullH);
        Cpu.SetS(0, Cpu.S(0) * CullV);
    }
}
