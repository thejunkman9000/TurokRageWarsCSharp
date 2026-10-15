using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game.PortHud;

/// <summary>
/// Marks the display-list commands the game's HUD code produced (<see cref="DrawCommand.Hud"/>), without touching any game function or guest memory.
/// <para>How the HUD is drawn (decompiled C): the per-frame race update View_UpdateAll walks the cameras (the split-screen views, list at Gv.Camera_ListHead) and
/// for each one runs the world pass (func_802362E8: loads the camera's 3D projection, camera + 0x380 + 0x40 * frame parity), then Hud_DrawViewOverlay -&gt;
/// Hud_DrawView2D, which emits <c>G_MTX(projection, load)</c> of the camera's 2D projection at camera + 0x448 + 0x40 * parity (Camera_LoadProjection2D) and then runs
/// the HUD of that view's player (Hud_DrawPlayer: crosshair, ammo, health, flash, counters, frags, ...), the on-screen messages (Hud_DrawMessages) and the
/// view's 2-pixel frame. After the last view the "own racer" at 0x80145000 gets the same treatment (the shared pass: the match clock Hud_DrawMatchClock).
/// No other code loads a projection from those offsets.</para>
/// <para>So the interpreter tags every draw after such a projection load with the view's index + 1 (<see cref="Shared"/> for the shared pass) until the
/// next projection load that is a camera's 3D projection (the next view's world pass) or the end of the list. Projections from anywhere else (models
/// drawn inside the HUD) keep the current tag. The frame border and other untextured fills inside a section are tagged too; the port HUD only ever
/// replaces 2D textured draws (<see cref="HudLayer"/>).</para>
/// The hook runs on the thread that starts the graphics task (the scheduler), while the list is interpreted. The window installs it (GfxRenderer -> HudLayer);
/// headless and oracle runs never do: a tag change flushes the interpreter's batch, which can split the command stream (the picture is the same).
/// </summary>
public static class HudTagger
{
    public const int Shared = 0x40;
    public const uint SharedRacer = 0x80145000;          // race->self: the full-screen pseudo view of the shared overlays
    public const uint Proj2D = 0x448, Proj3D = 0x380, Parity = 0x40;

    private static uint[] cams = Array.Empty<uint>();
    private static int camsTick = -1;

    public static void Install()
    {
        if (Oracle.Enabled) return;
        DisplayListInterpreter.HudMarker = Hook;
    }

    public static void Uninstall() => DisplayListInterpreter.HudMarker = null;

    /// <summary>The hook: the section's tag and, for a HUD section, a snapshot of its values taken now (while the list is being
    /// interpreted), so the port HUD shows what the game's HUD of this very list showed (animation frames, slide-in positions).</summary>
    private static (int Tag, object? Data)? Hook(uint addr)
    {
        try
        {
            int? tag = Marker(addr);
            if (tag is not int t) return null;
            return (t, t > 0 && Snapshots ? HudSnapshot.Take(t) : null);
        }
        catch (Exception e) when (e is Mem.GuestFault or IndexOutOfRangeException or InvalidOperationException) { return null; }
    }

    /// <summary>Whether the hook takes value snapshots (the port HUD is in use or may be switched on).</summary>
    public static volatile bool Snapshots = true;

    /// <summary>The tag for a projection matrix at <paramref name="addr"/>: view index + 1, <see cref="Shared"/>, 0 (a world pass: the section ends), null (keep).</summary>
    public static int? Marker(uint addr)
    {
        if (addr == 0) return null;
        var list = Cameras();
        for (int k = 0; k < 2; k++)
        {
            uint par = (uint)k * Parity;
            if (addr == SharedRacer + Proj2D + par) return Shared;
            for (int i = 0; i < list.Length; i++)
            {
                if (addr == list[i] + Proj2D + par) return i + 1;
                if (addr == list[i] + Proj3D + par) return 0;
            }
            if (addr == SharedRacer + Proj3D + par) return 0;
        }
        return null;
    }

    /// <summary>The camera addresses in list order (re-read once per retrace; the list changes only at match start / end).</summary>
    public static uint[] Cameras()
    {
        int t = Game.CurrentTick;
        if (t == camsTick) return cams;
        var l = new List<uint>();
        try
        {
            uint c = Mem.U32(MatchInfo.CameraListHead);
            for (int i = 0; i < 8 && c >= 0x80000000u && c < 0x807FF000u; i++, c = Mem.U32(c + 4)) l.Add(c);
        }
        catch (Mem.GuestFault) { }
        cams = l.ToArray(); camsTick = t;
        return cams;
    }
}
