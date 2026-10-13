using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// The port-drawn match scoreboard: while Tab is held during a match (game state 13 with the HUD on top of the UI stack) a translucent panel lists every
/// player with frags and deaths read live from the game's score records (<see cref="MatchInfo"/>), plus ping and connection status when netplay is active.
/// Also owns the presentation hotkeys: Tab (scoreboard), F2 (split screen / local view, see <see cref="LocalView"/>). Neither key reaches the game's input
/// mapping (InputModel binds neither). <c>TRW_SCOREBOARD=1</c> shows the panel for the whole match (unattended screenshots).
/// </summary>
public sealed class Scoreboard : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly SpriteBatch batch;
    private readonly SpriteFont? font, titleFont;      // the game's font (Peaceful Wars) and its italic for the title and headings
    private readonly Texture2D white;
    private bool tabHeld, f2Down;
    private static readonly bool alwaysOn = Environment.GetEnvironmentVariable("TRW_SCOREBOARD") == "1";

    // the game's palette: dark translucent panel, orange / gold text
    private static readonly Color Panel = new(12, 8, 4, 200), Edge = new(214, 140, 36), Gold = new(255, 200, 70), Orange = new(240, 130, 30),
                                  Text = new(236, 226, 206), Dim = new(150, 136, 112), LocalRow = new(90, 52, 10, 150);

    public Scoreboard(GraphicsDevice device, SpriteFont? font, SpriteFont? titleFont = null)
    {
        this.device = device;
        this.font = font;
        this.titleFont = titleFont ?? font;
        batch = new SpriteBatch(device);
        white = new Texture2D(device, 1, 1);
        white.SetData(new[] { Color.White });
    }

    /// <summary>Samples the presentation keys (once per window update). Keys only count while the window has the focus.</summary>
    public void Update(KeyboardState ks, bool active)
    {
        tabHeld = active && ks.IsKeyDown(Keys.Tab);
        bool f2 = active && ks.IsKeyDown(Keys.F2);
        if (f2 && !f2Down)
        {
            LocalView.Enabled = !LocalView.Enabled;
            Console.Error.WriteLine($"[view] {(LocalView.Enabled ? "local view (pad " + LocalView.Pad + ")" : "split screen")}");
        }
        f2Down = f2;
        MatchInfo.DebugTick();
    }

    public bool Visible => (tabHeld || alwaysOn) && MatchInfo.InMatch;

    private struct Row { public string Name; public int Frags, Deaths, Port, Index, Team; public bool Bot; }

    /// <summary>Draws the panel into the current render target (the back buffer, or a screenshot target) of <paramref name="w"/> x <paramref name="h"/> pixels.</summary>
    private static string? toastText;
    private static long toastUntil;
    /// <summary>A short notice at the top of the picture for about two seconds (e.g. the mouse sensitivity after - / =).</summary>
    public static void Toast(string text) { toastText = text; toastUntil = Environment.TickCount64 + 2000; }

    public void Draw(Rectangle picture, int w, int h)
    {
        if (font != null && toastText != null && Environment.TickCount64 < toastUntil)
        {
            batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            var sz = font.MeasureString(toastText);
            var pos = new Vector2(picture.X + (picture.Width - sz.X) / 2, picture.Y + picture.Height * 0.12f);
            batch.Draw(white, new Rectangle((int)pos.X - 12, (int)pos.Y - 6, (int)sz.X + 24, (int)sz.Y + 12), Panel);
            batch.DrawString(font, toastText, pos, Gold);
            batch.End();
        }
        if (font == null || w < 64 || h < 64 || !MatchInfo.InMatch) return;
        if (ViewLayout.Last != null) DrawClock(picture);
        if (!Visible) return;
        var players = MatchInfo.Players();
        if (players.Count == 0) return;
        var rows = players.Select(p => new Row
        {
            Name = LocalView.Name(p.Port) ?? (p.Name.Length > 0 ? p.Name : p.Port >= 0 ? $"PLAYER {p.Port + 1}" : $"BOT {p.Index + 1}"),
            Frags = p.Frags, Deaths = p.Deaths, Port = p.Port, Index = p.Index, Team = p.Team, Bot = p.Port < 0,
        }).OrderByDescending(r => r.Frags).ThenBy(r => r.Deaths).ThenBy(r => r.Index).ToList();
        bool net = LocalView.NetActive;

        float s = Math.Min(w / 960f, h / 720f);                 // 1.0 at the default 960x720 window
        float fs = 0.75f * s;                                   // font scale (the sprite font is 28 px)
        float lineH = font.LineSpacing * fs * 1.25f;
        float panelW = Math.Min(w - 32 * s, (net ? 760 : 600) * s);
        float panelH = lineH * (rows.Count + 2) + 40 * s;
        float px = (w - panelW) / 2, py = Math.Max(16 * s, (h - panelH) / 2.6f);
        // columns: name | frags | deaths [| ping | status]
        float cName = px + 28 * s, cFrags = px + panelW * (net ? 0.48f : 0.62f), cDeaths = px + panelW * (net ? 0.62f : 0.82f), cPing = px + panelW * 0.76f, cStat = px + panelW * 0.88f;

        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        Fill(px, py, panelW, panelH, Panel);
        float t = Math.Max(1, 2 * s);
        Fill(px, py, panelW, t, Edge); Fill(px, py + panelH - t, panelW, t, Edge); Fill(px, py, t, panelH, Edge); Fill(px + panelW - t, py, t, panelH, Edge);

        float y = py + 14 * s;
        Str(MatchTitle(), cName, y, Gold, fs * 1.1f, titleFont);
        string right = ClockText();
        if (right.Length > 0) { var m = font.MeasureString(right) * fs; Str(right, px + panelW - 28 * s - m.X, y + 2 * s, Orange, fs); }
        y += lineH * 1.25f;
        Header("PLAYER", cName, y, fs); Header("FRAGS", cFrags, y, fs); Header("DEATHS", cDeaths, y, fs);
        if (net) { Header("PING", cPing, y, fs); Header("STATUS", cStat, y, fs); }
        y += lineH * 0.9f;
        Fill(px + 20 * s, y, panelW - 40 * s, Math.Max(1, s), new Color(214, 140, 36, 160));
        y += 8 * s;
        foreach (var r in rows)
        {
            bool me = r.Port == LocalView.Pad && !r.Bot;
            if (me) Fill(px + 14 * s, y - 3 * s, panelW - 28 * s, lineH, LocalRow);
            Str(Clip(r.Name.ToUpperInvariant() + (r.Bot ? "  (BOT)" : ""), cFrags - cName - 16 * s, fs), cName, y, me ? Gold : Text, fs);
            Num(r.Frags, cFrags, y, Gold, fs);
            Num(r.Deaths, cDeaths, y, Text, fs);
            if (net)
            {
                int ping = LocalView.PingMs(r.Port);
                Str(r.Bot ? "-" : ping >= 0 ? $"{ping} ms" : me ? "LOCAL" : "-", cPing, y, Dim, fs * 0.85f);
                string? st = LocalView.Connection(r.Port);
                Str((r.Bot ? "BOT" : st ?? "-").ToUpperInvariant(), cStat, y, st != null && st != "OK" ? Orange : Dim, fs * 0.85f);
            }
            y += lineH;
        }
        batch.End();
    }

    /// <summary>Local view: the match clock, which the game draws across the border between the views (left out of the local view), at the bottom centre of the picture.</summary>
    private void DrawClock(Rectangle pic)
    {
        string c = ClockText();
        if (c.Length == 0 || pic.Width < 64) return;
        float fs = 0.62f * Math.Min(pic.Width / 960f, Math.Max(pic.Height, pic.Width * 3 / 8) / 360f);
        var m = font!.MeasureString(c) * fs;
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        Str(c, pic.X + (pic.Width - m.X) / 2, pic.Bottom - m.Y - 6 * fs, new Color(220, 220, 220, 230), fs);
        batch.End();
    }

    private void Header(string s, float x, float y, float fs) => Str(s, x, y, Orange, fs * 0.8f, titleFont);
    private void Num(int v, float x, float y, Color c, float fs) => Str(v.ToString(), x, y, c, fs);
    private void Str(string s, float x, float y, Color c, float fs, SpriteFont? f = null)
    {
        f ??= font!;
        batch.DrawString(f, s, new Vector2(x + 1.5f * fs, y + 1.5f * fs), new Color(0, 0, 0, 180), 0, Vector2.Zero, fs, SpriteEffects.None, 0);
        batch.DrawString(f, s, new Vector2(x, y), c, 0, Vector2.Zero, fs, SpriteEffects.None, 0);
    }
    private void Fill(float x, float y, float w, float h, Color c) => batch.Draw(white, new Rectangle((int)x, (int)y, Math.Max(1, (int)w), Math.Max(1, (int)h)), c);

    private string Clip(string s, float maxW, float fs)
    {
        if (font!.MeasureString(s).X * fs <= maxW) return s;
        while (s.Length > 1 && font.MeasureString(s + "..").X * fs > maxW) s = s[..^1];
        return s + "..";
    }

    // match settings (D_801462C8 + 0x5D8 = D_801468A0, the match rules): +0x14 f32 clock (time left, 15 per second: minutes * 900, counted down in
    // func_00228394), +0x18 s32 frag limit (func_0022804C)
    private const uint Rules = 0x801468A0;

    private static string MatchTitle()
    {
        try { int lim = Mem.S32(Rules + 0x18); return lim > 0 ? $"SCOREBOARD  -  FRAG LIMIT {lim}" : "SCOREBOARD"; }
        catch (Mem.GuestFault) { return "SCOREBOARD"; }
    }

    private static string ClockText()
    {
        try
        {
            float c = Mem.F32(Rules + 0x14);
            if (!(c > 0 && c < 1e7f)) return "";
            int sec = (int)MathF.Ceiling(c / 15f);
            return $"{sec / 60}:{sec % 60:D2}";
        }
        catch (Mem.GuestFault) { return ""; }
    }

    public void Dispose() { batch.Dispose(); white.Dispose(); }
}
