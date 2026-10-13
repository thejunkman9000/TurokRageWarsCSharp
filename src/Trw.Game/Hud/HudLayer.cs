using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game.PortHud;

/// <summary>
/// The port-drawn HUD (docs/HUD.md). Per frame, for every HUD section of the display list (<see cref="HudTagger"/>: one per split-screen view plus the
/// shared pass), it lays the layout's elements out in that view's rectangle from the section's values (<see cref="HudSnapshot"/>, taken while the list was
/// interpreted; live guest RAM when there is none), and decides which of the game's
/// own HUD draws stay: perspective and untextured draws (models, the views' frames, fades) always stay, 2D textured draws (sprites, digits, text) only when a
/// <c>game</c> element of the layout keeps their category. The port draws are inserted where the section ends, so they sit where the game's HUD sat in the
/// draw order and go through the same renderer path (local view, 4:3 shape in a wide picture).
/// <para>Presentation only: reads guest RAM, never writes it; the game's HUD code keeps running unchanged (its timers and animations live in RAM).</para>
/// <para>Options: <c>TRW_HUD=game|port</c> (the game's HUD draws as they are / the port HUD), <c>TRW_HUD_LAYOUT=&lt;file&gt;</c> (default: <c>hud.json</c> next to the
/// executable, else <c>Content/Hud/hud.json</c>), <c>TRW_HUD_LOG=1</c> (lists the HUD draws now and then), <c>TRW_HUD_VERIFY=1</c> (compares the port's quads with the
/// game draws they replace). Keys: F6 reloads the layout (it also reloads by
/// itself when the file changes), F7 switches between the game's and the port HUD.</para>
/// </summary>
public sealed class HudLayer : IDisposable
{
    public enum HudMode { Game, Port }

    /// <summary>The HUD in use (F7 switches). Default: port (see docs/HUD.md for why); <c>TRW_HUD=game</c> keeps the game's own draws.</summary>
    public static HudMode Mode = string.Equals(Environment.GetEnvironmentVariable("TRW_HUD"), "game", StringComparison.OrdinalIgnoreCase) ? HudMode.Game : HudMode.Port;
    private static readonly bool log = Environment.GetEnvironmentVariable("TRW_HUD_LOG") == "1";

    private readonly HudFonts fonts;
    private HudLayout? layout;
    private string? path;
    private DateTime fileTime;
    private long nextFileCheck;
    private bool f6Down, f7Down;
    private int frames;
    private int[] spriteIds = HudCategories.AllIds;

    private readonly Dictionary<int, List<DrawCommand>> after = new();
    private readonly Dictionary<DrawCommand, DrawCommand?> subst = new(ReferenceEqualityComparer.Instance);
    private readonly HudCanvas canvas = new();

    public HudLayer(GraphicsDevice? device)
    {
        fonts = new HudFonts(device);
        Reload();
        if (!Oracle.Enabled) HudTagger.Install();      // tags only: the stream stays as the game drew it unless the port HUD replaces draws
    }

    /// <summary>The layout file in use: TRW_HUD_LAYOUT, else hud.json next to the executable (a player's own), else the shipped Content/Hud/hud.json.</summary>
    public static string LayoutPath()
    {
        var env = Environment.GetEnvironmentVariable("TRW_HUD_LAYOUT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var own = Path.Combine(AppContext.BaseDirectory, "hud.json");
        return File.Exists(own) ? own : Path.Combine(AppContext.BaseDirectory, "Content", "Hud", "hud.json");
    }

    public void Reload()
    {
        path = LayoutPath();
        try
        {
            fileTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            var l = HudLayout.Load(path);
            foreach (var w in l.Warnings) Console.Error.WriteLine($"[hud] {path}: {w}");
            layout = l;
            var ids = new HashSet<int>(HudCategories.AllIds);
            foreach (var e in l.Elements)
            {
                if (e is GameElement g) foreach (var id in g.SpriteIds) ids.Add(id);
                if (e is SpriteElement s && s.SpriteId.Names.Count == 0) ids.Add((int)s.SpriteId.Eval(new HudValueTable()));
            }
            spriteIds = ids.ToArray();
            GameSprites.Clear();
            Console.Error.WriteLine($"[hud] layout {path}: {l.Elements.Count} elements, mode {Mode}");
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[hud] layout {path}: {e.Message}{(layout != null ? " (keeping the previous layout)" : " (using the game's HUD)")}");
        }
    }

    private void Poll()
    {
        var ks = Keyboard.GetState();
        bool f6 = ks.IsKeyDown(Keys.F6), f7 = ks.IsKeyDown(Keys.F7);
        if (f6 && !f6Down) Reload();
        if (f7 && !f7Down) { Mode = Mode == HudMode.Port ? HudMode.Game : HudMode.Port; Console.Error.WriteLine($"[hud] {Mode} HUD"); }
        f6Down = f6; f7Down = f7;
        long now = Environment.TickCount64;
        if (now >= nextFileCheck)
        {
            nextFileCheck = now + 1000;
            try { if (path != null && File.Exists(path) && File.GetLastWriteTimeUtc(path) != fileTime) Reload(); } catch (IOException) { }
        }
    }

    /// <summary>Prepares a frame. False = draw the commands as they are (game mode, no HUD in the frame, no layout).</summary>
    public bool Begin(IReadOnlyList<GfxCommand> cmds)
    {
        after.Clear(); subst.Clear();
        Poll();
        frames++;
        if (log && frames % 150 == 0) LogFrame(cmds);
        if (Mode != HudMode.Port || layout == null || !Widescreen.InLevel) return false;     // a level (8 = attract demo, 13 = playing); menus are left alone
        // the sections in this frame: tag -> index of its last command, and the scissor / screen size its 2D draws use
        var last = new Dictionary<int, int>();
        var first2D = new Dictionary<int, DrawCommand>();
        var snaps = new Dictionary<int, HudSnapshot>();
        for (int i = 0; i < cmds.Count; i++)
            if (cmds[i] is DrawCommand d && d.Hud != 0)
            {
                last[d.Hud] = i;
                if (!first2D.ContainsKey(d.Hud) && d.State.TextureOn && !Perspective(d)) first2D[d.Hud] = d;
                if (d.HudData is HudSnapshot hs && !snaps.ContainsKey(d.Hud)) snaps[d.Hud] = hs;
            }
        if (last.Count == 0) return false;
        // the values as the game's HUD saw them (taken while the list was interpreted); live RAM only when a section has no snapshot
        var views = snaps.Values.FirstOrDefault()?.Views ?? HudValues.Views();
        if (views.Count == 0) return false;
        int players = views.Count;
        foreach (var id in spriteIds) GameSprites.Get(id);         // where the HUD sprites are, for classifying the game's draws
        if (GameSprites.Get(GameSprites.DigitFont) == null) return false;     // the sprite lookup found nothing (resources not loaded / unknown layout): the game's HUD as it is
        var ctxs = new Dictionary<int, HudContext>();
        foreach (var (tag, idx) in last)
        {
            HudView? view; HudValues vals;
            if (snaps.TryGetValue(tag, out var snap)) { view = snap.View; vals = snap.Values; }
            else
            {
                view = tag == HudTagger.Shared ? HudValues.SharedView(views) : views.FirstOrDefault(v => v.Index == tag - 1);
                if (view == null) continue;
                vals = new HudValues(view, players);
            }
            canvas.Clear(); canvas.Tag = tag;
            if (first2D.TryGetValue(tag, out var f))
            {
                canvas.ScissorX0 = f.State.ScissorX0; canvas.ScissorY0 = f.State.ScissorY0; canvas.ScissorX1 = f.State.ScissorX1; canvas.ScissorY1 = f.State.ScissorY1;
                canvas.ScreenW = f.State.ScreenWidth; canvas.ScreenH = f.State.ScreenHeight;
            }
            else
            {
                canvas.ScreenW = (float)Math.Max(1, vals["screen.w"]); canvas.ScreenH = (float)Math.Max(1, vals["screen.h"]);
                canvas.ScissorX0 = canvas.ScissorY0 = 0; canvas.ScissorX1 = (int)(canvas.ScreenW - 1) * 4; canvas.ScissorY1 = (int)(canvas.ScreenH - 1) * 4;
            }
            var ctx = new HudContext { View = view, Values = vals, Canvas = canvas, Fonts = fonts, FirstView = views[0], FrameW = (float)Math.Max(1, vals["screen.w"]), FrameH = (float)Math.Max(1, vals["screen.h"]) };
            ctxs[tag] = ctx;
            bool shared = tag == HudTagger.Shared;
            foreach (var e in layout.Elements)
            {
                if (e.Shared != shared || e is GameElement || !e.Visible(ctx, players)) continue;
                try { e.Emit(ctx); }
                catch (Exception ex) when (ex is Mem.GuestFault or IndexOutOfRangeException) { }
            }
            canvas.Flush();
            if (canvas.Draws.Count > 0) after[idx] = canvas.Draws.ToList();
        }
        // which of the game's 2D textured HUD draws stay
        foreach (var c in cmds)
        {
            if (c is not DrawCommand d || d.Hud == 0 || !d.State.TextureOn || Perspective(d)) continue;
            if (!ctxs.TryGetValue(d.Hud, out var ctx)) continue;
            var cats = HudCategories.Of(GameSprites.Owner(d.State.Tex0?.Src ?? 0, spriteIds));
            GameElement? keep = null;
            foreach (var e in layout.Elements)
                if (e is GameElement g && g.Shared == (d.Hud == HudTagger.Shared) && g.Matches(cats) && g.Visible(ctx, players)) { keep = g; break; }
            if (keep == null) { subst[d] = null; continue; }
            float dx = (float)keep.X.Eval(ctx.Values) * ctx.Sx, dy = (float)keep.Y.Eval(ctx.Values) * ctx.Sy;
            if (dx != 0 || dy != 0) subst[d] = Shift(d, dx, dy);
        }
        if (verify && frames % 150 == 0) Verify(cmds);
        return true;
    }

    // ---------------------------------------------------------------------------------------------- TRW_HUD_VERIFY=1: port draws vs the game draws they replace

    private static readonly bool verify = Environment.GetEnvironmentVariable("TRW_HUD_VERIFY") == "1";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DecodedTexture, object> texHash = new();
    public static int VerifyMatched, VerifyGameOnly, VerifyPortOnly, VerifyFrames;

    private static ulong Hash(DecodedTexture? t)
    {
        if (t == null) return 0;
        if (texHash.TryGetValue(t, out var o)) return (ulong)o;
        ulong h = 1469598103934665603UL;
        foreach (byte b in t.Rgba) h = (h ^ b) * 1099511628211UL;
        h = (h ^ (ulong)t.Width) * 1099511628211UL; h = (h ^ (ulong)t.Height) * 1099511628211UL;
        texHash.AddOrUpdate(t, h);
        return h;
    }

    /// <summary>Every quad as (textures, screen box, tint): exact equality means the port drew the same texels at the same place in the same colour.</summary>
    private static IEnumerable<string> Quads(DrawCommand d)
    {
        var st = d.State; var v = d.Vertices;
        string tint = string.Join(",", st.Env.Select(e => (int)MathF.Round(e * 255)));
        for (int i = 0; i + 5 < v.Length; i += 6)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int k = i; k < i + 6; k++)
            {
                float x = (v[k].X / v[k].W + 1) * 0.5f * st.ScreenWidth, y = (1 - v[k].Y / v[k].W) * 0.5f * st.ScreenHeight;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            yield return $"{Hash(st.Tex0):X16}/{Hash(st.Tex1):X16} ({x0:F1},{y0:F1})-({x1:F1},{y1:F1}) env {tint}";
        }
    }

    private void Verify(IReadOnlyList<GfxCommand> cmds)
    {
        var game = new List<string>(); var port = new List<string>();
        foreach (var c in cmds) if (c is DrawCommand d && subst.TryGetValue(d, out var r) && r == null) game.AddRange(Quads(d));
        foreach (var l in after.Values) foreach (var d in l) port.AddRange(Quads(d));
        var left = new List<string>(port);
        var gameOnly = new List<string>();
        foreach (var q in game) { int i = left.IndexOf(q); if (i >= 0) left.RemoveAt(i); else gameOnly.Add(q); }
        int matched = game.Count - gameOnly.Count;
        VerifyMatched += matched; VerifyGameOnly += gameOnly.Count; VerifyPortOnly += left.Count; VerifyFrames++;
        var sb = new System.Text.StringBuilder($"[hud-verify] frame {frames}: {matched} quads identical, {gameOnly.Count} only in the game's HUD, {left.Count} only in the port's " +
                                               $"(total {VerifyMatched} / {VerifyGameOnly} / {VerifyPortOnly} over {VerifyFrames} checks)");
        foreach (var q in gameOnly.Take(8)) sb.Append("\n[hud-verify]   game: " + q);
        foreach (var q in left.Take(8)) sb.Append("\n[hud-verify]   port: " + q);
        Console.Error.WriteLine(sb.ToString());
    }

    /// <summary>The draw to execute in place of <paramref name="d"/> (itself, a moved copy) or null to leave it out.</summary>
    public DrawCommand? Map(DrawCommand d) => subst.TryGetValue(d, out var r) ? r : d;

    /// <summary>Port draws that follow command <paramref name="index"/> (the end of a HUD section), or null.</summary>
    public List<DrawCommand>? After(int index) => after.TryGetValue(index, out var l) ? l : null;

    private static bool Perspective(DrawCommand d)
    {
        foreach (ref readonly var p in d.Vertices.AsSpan()) if (MathF.Abs(p.W - 1f) > 1e-4f) return true;
        return false;
    }

    private static DrawCommand Shift(DrawCommand d, float dx, float dy)
    {
        var v = (GfxVertex[])d.Vertices.Clone();
        float kx = dx / d.State.ScreenWidth * 2, ky = dy / d.State.ScreenHeight * 2;
        for (int i = 0; i < v.Length; i++) { v[i].X += kx * v[i].W; v[i].Y -= ky * v[i].W; }
        return d with { Vertices = v };
    }

    private void LogFrame(IReadOnlyList<GfxCommand> cmds)
    {
        foreach (var id in spriteIds) GameSprites.Get(id);
        var sb = new System.Text.StringBuilder($"[hud] frame {frames} (tick {Game.CurrentTick}): ");
        int n = 0;
        foreach (var c in cmds)
        {
            if (c is not DrawCommand d || d.Hud == 0) continue;
            n++;
            var st = d.State; var v = d.Vertices;
            float x0 = v.Min(q => (q.X / q.W + 1) * 0.5f * st.ScreenWidth), x1 = v.Max(q => (q.X / q.W + 1) * 0.5f * st.ScreenWidth);
            float y0 = v.Min(q => (1 - q.Y / q.W) * 0.5f * st.ScreenHeight), y1 = v.Max(q => (1 - q.Y / q.W) * 0.5f * st.ScreenHeight);
            int owner = GameSprites.Owner(st.Tex0?.Src ?? 0, spriteIds);
            sb.Append($"\n[hud]   tag {d.Hud,2} {(Perspective(d) ? "3D" : "2D")} {(st.TextureOn ? $"tex {st.Tex0?.Width}x{st.Tex0?.Height} src {st.Tex0?.Src:X8}" : "untextured")} " +
                      $"{(owner >= 0 ? $"sprite 0x{owner:X}" : "-")} box ({x0:F1},{y0:F1})-({x1:F1},{y1:F1}) env ({string.Join(",", st.Env.Select(e => e.ToString("F2")))}) n={v.Length}");
        }
        if (n == 0) return;
        foreach (var view in HudValues.Views())
        {
            var vals = new HudValues(view, 0);
            sb.Append($"\n[hud]   view {view.Index} cam {view.Camera:X8} player {view.Player:X8} rect {view.X},{view.Y} {view.W}x{view.H}: " +
                      string.Join(" ", vals.All.Where(k => !k.Key.StartsWith("view")).Select(k => $"{k.Key}={k.Value:0.###}")));
        }
        Console.Error.WriteLine(sb.ToString());
    }

    public void Dispose() { fonts.Dispose(); HudTagger.Uninstall(); }
}
