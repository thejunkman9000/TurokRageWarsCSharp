using Trw.Platform.Gfx;

namespace Trw.Game;

/// <summary>A rectangle of the N64 screen (pixels of the colour image, 480x360 in matches).</summary>
public readonly record struct ViewRegion(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
    public float Aspect => Height > 0 ? (float)Width / Height : 4f / 3f;
}

/// <summary>
/// Split-screen layout of a multiplayer match and the local-view remap.
/// <para>How the game lays the views out (per-view setup View.SetupViewport: G_MOVEMEM viewport + G_SETSCISSOR from the player's view rectangle, then a
/// FILL-mode rectangle that clears that view's part of the z-buffer, and the 2-pixel frame of View.View_DrawFrame inside the view):
/// 2 players = two 480x180 halves (player 1 top, player 2 bottom), 3-4 players = 240x180 quadrants (1 top left, 2 top right, 3 bottom left,
/// 4 bottom right); the match clock and other shared overlays are drawn afterwards with the full-screen scissor across the view borders.</para>
/// <para>The local view takes the local player's scissor rectangle and maps it onto the whole render target: every vertex (already in clip space)
/// gets the affine remap x' = kx * x - bx * w (likewise y), every scissor is moved into the region and clamped. Draws whose scissor lies outside
/// the region (the other players' views) end up with an empty scissor and are skipped; full-screen draws are cut at the region's edge.
/// The interpreter's command stream (what the oracle hashes) is not touched: the remap happens in <see cref="GfxRenderer"/> only.</para>
/// </summary>
public static class ViewLayout
{
    private static readonly bool log = Environment.GetEnvironmentVariable("TRW_VIEW_LOG") == "1";
    private static ViewRegion? lastLogged;
    private static int lastMiss;

    /// <summary>The local player's region for this frame, or null when the frame is presented as the game drew it
    /// (mode off, not in a match, single view, the local pad has no view, or the frame does not contain that view's scissor).</summary>
    public static ViewRegion? LocalRegion(IReadOnlyList<GfxCommand> cmds) => Last = Compute(cmds);

    /// <summary>The result of the last <see cref="LocalRegion"/> (null = the frame on screen is the game's own layout).</summary>
    public static ViewRegion? Last { get; private set; }

    private static ViewRegion? Compute(IReadOnlyList<GfxCommand> cmds)
    {
        if (!LocalView.Enabled || !MatchInfo.InMatch) return null;
        var players = MatchInfo.Players();
        int pad = LocalView.Pad;
        if (!MatchInfo.ViewOf(pad, players, out var me)) return null;
        if (!(me.W > 8 && me.H > 8 && me.W < 4096 && me.H < 4096 && me.X >= 0 && me.Y >= 0)) return null;
        // the scissor View.SetupViewport sets: truncated to integers, clamped to the colour image
        float sw = 480, sh = 360;
        foreach (var c in cmds) if (c is DrawCommand d) { sw = d.State.ScreenWidth; sh = d.State.ScreenHeight; break; }
        int x0 = (int)me.X, y0 = (int)me.Y, x1 = (int)(me.X + me.W), y1 = (int)(me.Y + me.H);
        x1 = Math.Min(x1, (int)sw - 1); y1 = Math.Min(y1, (int)sh - 1);
        if (x0 <= 0 && y0 <= 0 && x1 >= sw - 2 && y1 >= sh - 2) return null;       // one view covering the screen: nothing to do
        // the frame must actually contain that view (protects the frames around the match start / end, where RAM and the drawn frame disagree)
        bool found = false;
        foreach (var c in cmds)
            if (c is DrawCommand d && Math.Abs(d.State.ScissorX0 - x0 * 4) <= 4 && Math.Abs(d.State.ScissorY0 - y0 * 4) <= 4 &&
                Math.Abs(d.State.ScissorX1 - x1 * 4) <= 4 && Math.Abs(d.State.ScissorY1 - y1 * 4) <= 4) { found = true; break; }
        if (!found)
        {
            if (log && Game.CurrentTick - lastMiss > 300) { lastMiss = Game.CurrentTick; Console.Error.WriteLine($"[view] pad {pad}: view {x0},{y0}-{x1},{y1} not in this frame's scissors: {Scissors(cmds)}"); }
            return null;
        }
        // the region spans the view to its last pixel (the scissor's lower-right corner is exclusive in the renderer's mapping, like the RDP's)
        var r = new ViewRegion(x0, y0, x1, y1);
        if (log && r != lastLogged) { lastLogged = r; Console.Error.WriteLine($"[view] pad {pad}: region {x0},{y0}-{x1},{y1} ({r.Width}x{r.Height}); frame scissors {Scissors(cmds)}"); }
        return r;
    }

    private static string Scissors(IReadOnlyList<GfxCommand> cmds) =>
        string.Join(" ", cmds.OfType<DrawCommand>().Select(d => (d.State.ScissorX0 / 4, d.State.ScissorY0 / 4, d.State.ScissorX1 / 4, d.State.ScissorY1 / 4)).Distinct().Select(s => $"[{s.Item1},{s.Item2}-{s.Item3},{s.Item4}]"));

    /// <summary>Size of the offscreen target for a local view presented in a window of <paramref name="winW"/> x <paramref name="winH"/>:
    /// the letterboxed destination size (so the view is rendered at the window's resolution), never below the split-screen density
    /// (<paramref name="scale"/> target pixels per N64 pixel), at most 4096 wide.</summary>
    public static (int w, int h) TargetSize(ViewRegion r, int winW, int winH, int scale, bool stretch, float pictureAspect = 0)
    {
        // the cameras are built for the whole picture in local view (Widescreen.LocalFill): pictureAspect; otherwise the region's own (or stretched to the window)
        float aspect = stretch ? (float)winW / Math.Max(1, winH) : pictureAspect > 0 ? pictureAspect : r.Aspect;
        float fitW = Math.Min(winW, winH * aspect);
        float w = Math.Max(fitW, r.Width * scale);
        w = Math.Min(w, 4096f);
        float h = w / aspect;
        if (h > 4096f) { h = 4096f; w = h * aspect; }
        return (Math.Max(16, (int)MathF.Round(w)), Math.Max(16, (int)MathF.Round(h)));
    }
}
