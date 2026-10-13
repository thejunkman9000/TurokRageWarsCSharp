using System.Text.Json;

namespace Trw.Game.PortHud;

/// <summary>What an element draws with: the view, its values, the canvas (screen coordinates) and the fonts.</summary>
public sealed class HudContext
{
    public required HudView View;
    public required HudValues Values;
    public required HudCanvas Canvas;
    public HudFonts? Fonts;
    public HudView? FirstView;              // the shared pass lays the clock out from the first view (func_00228AD0)
    public float FrameW = 480, FrameH = 360; // the HUD frame of a view (D_800E28D0 x D_800E28D4)
    public float Sx => View.W / FrameW;      // frame units -> screen pixels of this view (2-player halves: 1 x 0.5, the game's look)
    public float Sy => View.H / FrameH;
}

/// <summary>
/// A HUD element of the layout. Common fields (all optional except <c>kind</c>):
/// <c>id</c>, <c>show</c> (false = off), <c>players</c> ([1,2,3,4] = the player counts it shows in), <c>when</c> (expression), <c>pass</c> ("view" = every
/// view, "shared" = once over the whole screen: the clock), <c>anchor</c> (top-left, top, top-right, left, center, right, bottom-left, bottom, bottom-right of the
/// view's 4:3 HUD frame) or <c>origin</c> (a position the game computes: health, score, lives, count, squad, ammo1..3, crosshair, clock, center),
/// <c>x</c>/<c>y</c> (offset in frame units: 480x360 per view), <c>scale</c> (number or [x, y]), <c>color</c>, <c>alpha</c> (0..255, expression), <c>snap</c>
/// (round positions down to whole pixels like the game, default true). Numbers can be expressions over the values (<see cref="HudValues"/>).
/// </summary>
public abstract class HudElement
{
    public string Id = "", Kind = "";
    public bool Show = true, Shared, Snap = true;
    public int Players = 0b11110;           // bit n = shown with n players
    public HudExpr? When;
    public string Anchor = "top-left";
    public string? Origin;
    public HudExpr X = HudExpr.Constant(0), Y = HudExpr.Constant(0), ScaleX = HudExpr.Constant(1), ScaleY = HudExpr.Constant(1), Alpha = HudExpr.Constant(255);
    public HudColor Color = HudColor.White;

    public void ParseCommon(JsonElement e)
    {
        Show = HudLayout.Bool(e, "show", true);
        Snap = HudLayout.Bool(e, "snap", true);
        string pass = HudLayout.Str(e, "pass") ?? "view";
        Shared = pass.Equals("shared", StringComparison.OrdinalIgnoreCase);
        if (!Shared && !pass.Equals("view", StringComparison.OrdinalIgnoreCase)) throw new FormatException($"\"pass\": \"view\" or \"shared\", not \"{pass}\"");
        if (e.TryGetProperty("players", out var pl))
        {
            if (pl.ValueKind == JsonValueKind.Array) { Players = 0; foreach (var x in pl.EnumerateArray()) { int n = x.GetInt32(); if (n is < 1 or > 4) throw new FormatException("\"players\": 1..4"); Players |= 1 << n; } }
            else if (pl.ValueKind == JsonValueKind.Number) Players = 1 << pl.GetInt32();
            else if (pl.ValueKind == JsonValueKind.String && pl.GetString() == "all") Players = 0b11110;
            else throw new FormatException("\"players\": a list like [1, 2] or \"all\"");
        }
        if (HudLayout.Str(e, "when") is string w) When = HudExpr.Parse(w);
        Anchor = (HudLayout.Str(e, "anchor") ?? "top-left").ToLowerInvariant();
        if (AnchorPoint(Anchor) == null) throw new FormatException($"\"anchor\": unknown \"{Anchor}\"");
        Origin = HudLayout.Str(e, "origin")?.ToLowerInvariant();
        if (Origin != null && !Origins.Contains(Origin)) throw new FormatException($"\"origin\": unknown \"{Origin}\" (known: {string.Join(", ", Origins)})");
        X = HudLayout.Expr(e, "x", 0); Y = HudLayout.Expr(e, "y", 0);
        if (e.TryGetProperty("scale", out var sc))
        {
            if (sc.ValueKind == JsonValueKind.Array) { var a = sc.EnumerateArray().ToArray(); if (a.Length != 2) throw new FormatException("\"scale\": a number or [x, y]"); ScaleX = HudLayout.ExprOf(a[0]); ScaleY = HudLayout.ExprOf(a[1]); }
            else ScaleX = ScaleY = HudLayout.ExprOf(sc);
        }
        Alpha = HudLayout.Expr(e, "alpha", 255);
        Color = HudLayout.Color(e, "color", HudColor.White);
    }

    /// <summary>Kind-specific fields.</summary>
    public abstract void Parse(JsonElement e);

    /// <summary>Draws the element into <c>c.Canvas</c> (screen coordinates).</summary>
    public abstract void Emit(HudContext c);

    /// <summary>Shown in this view with this player count (does not evaluate kind-specific conditions).</summary>
    public bool Visible(HudContext c, int players) => Show && (Players & (1 << Math.Clamp(players, 0, 4))) != 0 && (When == null || When.True(c.Values));

    public static readonly string[] Origins = { "health", "score", "count", "squad", "lives", "ammo1", "ammo2", "ammo3", "crosshair", "clock", "center" };

    public static (float ax, float ay)? AnchorPoint(string a) => a switch
    {
        "top-left" => (0, 0), "top" => (0.5f, 0), "top-right" => (1, 0),
        "left" => (0, 0.5f), "center" => (0.5f, 0.5f), "right" => (1, 0.5f),
        "bottom-left" => (0, 1), "bottom" => (0.5f, 1), "bottom-right" => (1, 1),
        _ => null,
    };

    /// <summary>Where the element is: screen position (top-left of what it draws) and the screen size of one frame unit.</summary>
    public (float x, float y, float sx, float sy) Place(HudContext c)
    {
        var (bx, by, ux, uy) = Basis(c, Origin, Anchor);
        float x = bx + (float)X.Eval(c.Values) * ux, y = by + (float)Y.Eval(c.Values) * uy;
        return (x, y, ux * (float)ScaleX.Eval(c.Values), uy * (float)ScaleY.Eval(c.Values));
    }

    /// <summary>The base point and the frame-unit size of an origin / anchor. The origins follow the game's formulas (func_0021EED8, func_0021EA30,
    /// func_00228AD0), including its quirks (a right-hand counter more than 200 pixels in moves 12 pixels left).</summary>
    public static (float x, float y, float ux, float uy) Basis(HudContext c, string? origin, string anchor)
    {
        var v = c.View; var val = c.Values;
        float sx = c.Sx, sy = c.Sy;
        bool hires = val["hires"] != 0;
        float kh = hires ? 1.5f : 1f, k2 = hires ? 1f : 0.75f;
        switch (origin)
        {
            case "health": return ((float)val["health.x"] * kh * sx + v.X, (float)val["health.y"] * kh * sy + v.Y + v.H, sx, sy);
            case "score": case "count": case "squad":
            {
                float x = (float)val[origin + ".x"] * k2 * sx + v.X + v.W;
                if (x > 200f) x -= 12f;
                return (x, (float)val[origin + ".y"] * k2 * sy + v.Y, sx, sy);
            }
            case "lives": return ((float)val["lives.x"] * k2 * sx + v.X, (float)val["lives.y"] * k2 * sy + v.Y, sx, sy);
            case "ammo1": case "ammo2": case "ammo3":
                return ((float)val[origin + ".x"] * sx + v.X + (hires ? 10f : -9f), (float)val[origin + ".y"] * sy + v.Y + v.H, sx, sy);
            case "crosshair": return (v.X + v.W * 0.5f, v.Y + v.H * 0.5f, 1, 1);        // func_0021E068: centred, drawn at its own size in every view
            case "center": return (v.X + v.W * 0.5f, v.Y + v.H * 0.5f, sx, sy);
            case "clock":
            {   // func_00228AD0, from the first view's size: one view: (W/2, 20) hi-res, (W/2, 10) else; several: (W/2, (H-10)/2)
                var f = c.FirstView ?? v;
                float W = val["screen.w"] > 0 ? (float)val["screen.w"] : 480, H = val["screen.h"] > 0 ? (float)val["screen.h"] : 360;
                float fs0 = f.W / W, fs1f = f.H / H;
                if (val["views"] == 1)
                {
                    if (!hires) return (W * 0.5f, 10f, fs0 * 0.8f, fs1f * 0.8f);
                    return (W * 0.5f, 20f, fs0, fs1f);
                }
                return (W * 0.5f, (H - 10) * 0.5f, fs0, fs1f);
            }
        }
        var (ax, ay) = AnchorPoint(anchor) ?? (0, 0);
        return (v.X + ax * v.W, v.Y + ay * v.H, sx, sy);
    }

    protected float AlphaOf(HudContext c) => Math.Clamp((float)Alpha.Eval(c.Values) / 255f, 0f, 1f);
    protected float S(float p) => Snap ? MathF.Floor(p) : p;
    protected static float TruncS16(float p) => (short)(int)p;      // the game passes positions as s16 after a float->int conversion
}

/// <summary>A sprite of the game's HUD library: <c>sprite</c> (id, e.g. "0x1FA"), <c>frame</c> (expression; wraps around the sprite's frame count).</summary>
public sealed class SpriteElement : HudElement
{
    public HudExpr SpriteId = HudExpr.Constant(0), FrameNo = HudExpr.Constant(0);
    public override void Parse(JsonElement e)
    {
        SpriteId = e.TryGetProperty("sprite", out var s) ? (s.ValueKind == JsonValueKind.String && s.GetString()!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? HudExpr.Parse(s.GetString()!) : HudLayout.ExprOf(s)) : throw new FormatException("\"sprite\" missing");
        FrameNo = HudLayout.Expr(e, "frame", 0);
    }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        DrawSprite(c, (int)SpriteId.Eval(c.Values), (int)FrameNo.Eval(c.Values), S(x), S(y), sx, sy, Color, AlphaOf(c));
    }

    /// <summary>One frame of a game sprite at screen (x, y), cells laid out as func_002ABED4 does (scaled path).</summary>
    public static bool DrawSprite(HudContext c, int id, int frame, float x, float y, float sx, float sy, HudColor col, float alpha)
    {
        var fr = GameSprites.Get(id)?.Get(frame);
        if (fr == null) return false;
        // func_002AA724: the texture rectangle's corners are truncated to the RDP's quarter pixels, the texel step to 1/1024 (dsdx = (int)(1024 / scale))
        int dsdx = sx != 0 ? (int)(1024f / sx) : 1024, dtdy = sy != 0 ? (int)(1024f / sy) : 1024;
        foreach (var cell in fr.Cells)
        {
            float cx = x + cell.X * sx, cy = y + cell.Y * sy;
            float x0 = (int)(cx * 4f) / 4f, y0 = (int)(cy * 4f) / 4f, x1 = (int)((cx + cell.W * sx) * 4f) / 4f, y1 = (int)((cy + cell.H * sy) * 4f) / 4f;
            float u1 = (x1 - x0) * dsdx / 1024f / cell.W, v1 = (y1 - y0) * dtdy / 1024f / cell.H;
            // 1:1 rectangles are point sampled (the interpreter's rule for the game's own draws, RdpMath.ForcePointForOneToOneRect)
            if (cell.Tex0 != null && cell.Tex1 != null) c.Canvas.Quad(x0, y0, x1, y1, cell.Tex0, cell.Tex1, col, alpha, !(dsdx == 1024 && dtdy == 1024), 0, 0, u1, v1);
        }
        return true;
    }
}

/// <summary>A number in the game's digit sprites (sprite 4, func_002A921C): <c>value</c>, <c>digits</c> (minimum digits, zero padded), <c>align</c> ("left" =
/// starts at x, "right" = ends at x). Digits advance 13 frame units ('1' 0.7 of that), a minus sign precedes negative values.
/// With <c>font</c> set to a sprite font (e.g. "Scoreboard") the number is drawn as text instead.</summary>
public sealed class NumberElement : HudElement
{
    public HudExpr Value = HudExpr.Constant(0);
    public int Digits; public bool Right; public string? Font; public float Size = 16;
    public override void Parse(JsonElement e)
    {
        Value = e.TryGetProperty("value", out var v) ? HudLayout.ExprOf(v) : throw new FormatException("\"value\" missing");
        Digits = HudLayout.Int(e, "digits", 0);
        string al = (HudLayout.Str(e, "align") ?? "left").ToLowerInvariant();
        if (al is not ("left" or "right")) throw new FormatException("\"align\": left or right");
        Right = al == "right";
        Font = HudLayout.Str(e, "font");
        if (Font == "game") Font = null;
        Size = (float)(HudLayout.Expr(e, "size", 16).Eval(new HudValueTable()));
    }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        int n = (int)Value.Eval(c.Values);
        if (Font != null)
        {
            string s = Math.Abs(n).ToString().PadLeft(Math.Max(Digits, 1), '0');
            if (n < 0) s = "-" + s;
            TextElement.DrawText(c, Font, s, x, y, sx, sy, Size, Right ? 1 : 0, Color, AlphaOf(c), false);
            return;
        }
        DrawDigits(c, n, x, y, sx, sy, Right, Digits, Color, AlphaOf(c));
    }

    /// <summary>func_002A921C, digit for digit (positions truncated to whole pixels as the game passes them).</summary>
    public static void DrawDigits(HudContext c, int n, float x, float y, float sx, float sy, bool rev, int mindig, HudColor col, float alpha)
    {
        var spr = GameSprites.Get(GameSprites.DigitFont);
        if (spr == null) return;
        bool neg = n < 0; if (neg) n = -n;
        int d3 = (int)(n * 0.001f), r1 = n - d3 * 1000, d2 = (int)(r1 * 0.01f), r2 = r1 - d2 * 100, d1 = (int)(r2 * 0.1f), d0 = r2 - d1 * 10;
        int top = 3;
        if (d3 == 0) { top = 2; if (d2 == 0) top = d1 != 0 ? 1 : 0; }
        if (top < mindig - 1) top = mindig - 1;
        if (neg)
        {
            SpriteElement.DrawSprite(c, GameSprites.DigitFont, 10, (int)x, (int)y, sx, sy, col, alpha);
            x += sx * 13f;
        }
        for (int i = top; i >= 0; i--)
        {
            int v = rev ? top - i : i;
            int d = v switch { 3 => d3, 2 => d2, 1 => d1, _ => d0 };
            float w = (d == 1 ? 0.7f : 1f) * (sx * 13f);
            if (rev) x -= w;
            if ((uint)d < 10) SpriteElement.DrawSprite(c, GameSprites.DigitFont, d, TruncS16(x), TruncS16(y), sx, sy, col, alpha);
            if (!rev) x += w;
        }
    }
}

/// <summary>Text in a sprite font: <c>text</c> (format string, e.g. "FRAGS {frags}/{fraglimit}" or "{clock:time}"), <c>font</c> (Scoreboard, ScoreboardTitle,
/// NetFont, NetTitle), <c>size</c> (line height in frame units), <c>align</c> (left / center / right of x), <c>shadow</c> (bool).</summary>
public sealed class TextElement : HudElement
{
    public HudFormat Text = new("");
    public string Font = "Scoreboard"; public float Size = 16; public int Align; public bool Shadow = true;
    public override void Parse(JsonElement e)
    {
        Text = new HudFormat(HudLayout.Str(e, "text") ?? throw new FormatException("\"text\" missing"));
        Font = HudLayout.Str(e, "font") ?? "Scoreboard";
        Size = (float)HudLayout.Expr(e, "size", 16).Eval(new HudValueTable());
        Align = (HudLayout.Str(e, "align") ?? "left").ToLowerInvariant() switch { "left" => 0, "center" => 2, "right" => 1, var a => throw new FormatException($"\"align\": left, center or right, not \"{a}\"") };
        Shadow = HudLayout.Bool(e, "shadow", true);
    }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        DrawText(c, Font, Text.Format(c.Values), x, y, sx, sy, Size, Align, Color, AlphaOf(c), Shadow);
    }

    /// <summary>align: 0 left, 1 right, 2 centre.</summary>
    public static void DrawText(HudContext c, string font, string s, float x, float y, float sx, float sy, float size, int align, HudColor col, float alpha, bool shadow)
    {
        var f = c.Fonts?.Get(font);
        if (f == null || s.Length == 0) return;
        float k = size / Math.Max(1, f.LineSpacing);
        float gx = sx * k, gy = sy * k;
        float w = HudFonts.Measure(f, s) * gx;
        if (align == 1) x -= w; else if (align == 2) x -= w / 2;
        if (shadow) HudFonts.Draw(c.Canvas, f, s, x + Math.Max(1, gx * 1.5f), y + Math.Max(1, gy * 1.5f), gx, gy, new HudColor(0, 0, 0, 255), alpha * 0.7f);
        HudFonts.Draw(c.Canvas, f, s, x, y, gx, gy, col, alpha);
    }
}

/// <summary>A filled bar: <c>value</c>, <c>max</c>, <c>width</c>, <c>height</c> (frame units), <c>back</c> (background colour, optional), <c>direction</c>
/// (right, left, up, down: where it grows), <c>border</c> (frame units, drawn in <c>borderColor</c>).</summary>
public sealed class BarElement : HudElement
{
    public HudExpr Value = HudExpr.Constant(0), Max = HudExpr.Constant(100), Width = HudExpr.Constant(60), Height = HudExpr.Constant(6);
    public HudColor? Back; public HudColor BorderColor = new(0, 0, 0, 255); public float Border; public string Dir = "right";
    public override void Parse(JsonElement e)
    {
        Value = e.TryGetProperty("value", out var v) ? HudLayout.ExprOf(v) : throw new FormatException("\"value\" missing");
        Max = HudLayout.Expr(e, "max", 100); Width = HudLayout.Expr(e, "width", 60); Height = HudLayout.Expr(e, "height", 6);
        if (e.TryGetProperty("back", out _)) Back = HudLayout.Color(e, "back", new HudColor(0, 0, 0, 128));
        BorderColor = HudLayout.Color(e, "borderColor", BorderColor);
        Border = (float)HudLayout.Expr(e, "border", 0).Eval(new HudValueTable());
        Dir = (HudLayout.Str(e, "direction") ?? "right").ToLowerInvariant();
        if (Dir is not ("right" or "left" or "up" or "down")) throw new FormatException("\"direction\": right, left, up or down");
    }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        float w = (float)Width.Eval(c.Values) * sx, h = (float)Height.Eval(c.Values) * sy, a = AlphaOf(c);
        double max = Max.Eval(c.Values);
        float f = max > 0 ? (float)Math.Clamp(Value.Eval(c.Values) / max, 0, 1) : 0;
        if (Border > 0) { float bx = Border * sx, by = Border * sy; c.Canvas.Fill(x - bx, y - by, x + w + bx, y + h + by, BorderColor, a); }
        if (Back is HudColor b) c.Canvas.Fill(x, y, x + w, y + h, b, a);
        switch (Dir)
        {
            case "right": c.Canvas.Fill(x, y, x + w * f, y + h, Color, a); break;
            case "left": c.Canvas.Fill(x + w * (1 - f), y, x + w, y + h, Color, a); break;
            case "up": c.Canvas.Fill(x, y + h * (1 - f), x + w, y + h, Color, a); break;
            default: c.Canvas.Fill(x, y, x + w, y + h * f, Color, a); break;
        }
    }
}

/// <summary>A plain rectangle (<c>width</c>, <c>height</c> in frame units): panels behind other elements.</summary>
public sealed class RectElement : HudElement
{
    public HudExpr Width = HudExpr.Constant(10), Height = HudExpr.Constant(10);
    public override void Parse(JsonElement e) { Width = HudLayout.Expr(e, "width", 10); Height = HudLayout.Expr(e, "height", 10); }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        c.Canvas.Fill(x, y, x + (float)Width.Eval(c.Values) * sx, y + (float)Height.Eval(c.Values) * sy, Color, AlphaOf(c));
    }
}

/// <summary>The crosshair. <c>style</c> "sprite" (default): the game's crosshair sprite (id <c>crosshair.id</c>, animation frame <c>crosshair.frame</c>, func_0021E27C /
/// func_0021E068: centred on the view, drawn at its own size); "cross": four bars of <c>size</c>, <c>gap</c> and <c>thickness</c> (screen pixels of the
/// 480x360 picture) in <c>color</c>; "dot": a square of <c>thickness</c>. Use <c>origin</c> "crosshair" (the view's centre, unscaled).</summary>
public sealed class CrosshairElement : HudElement
{
    public string Style = "sprite"; public HudExpr SpriteId = HudExpr.Parse("crosshair.id"), FrameNo = HudExpr.Parse("crosshair.frame");
    public float Size = 6, Gap = 2, Thickness = 1;
    public override void Parse(JsonElement e)
    {
        Style = (HudLayout.Str(e, "style") ?? "sprite").ToLowerInvariant();
        if (Style is not ("sprite" or "cross" or "dot")) throw new FormatException("\"style\": sprite, cross or dot");
        if (e.TryGetProperty("sprite", out var s)) SpriteId = HudLayout.ExprOf(s);
        if (e.TryGetProperty("frame", out var f)) FrameNo = HudLayout.ExprOf(f);
        var none = new HudValueTable();
        Size = (float)HudLayout.Expr(e, "size", 6).Eval(none); Gap = (float)HudLayout.Expr(e, "gap", 2).Eval(none); Thickness = (float)HudLayout.Expr(e, "thickness", 1).Eval(none);
        if (Origin == null) Origin = "crosshair";
    }
    public override void Emit(HudContext c)
    {
        var (x, y, sx, sy) = Place(c);
        float a = AlphaOf(c);
        if (Style == "sprite")
        {
            int id = (int)SpriteId.Eval(c.Values);
            if (id <= 0) return;
            SpriteElement.DrawSprite(c, id, (int)FrameNo.Eval(c.Values), TruncS16(x), TruncS16(y), sx, sy, Color, a);
            return;
        }
        float t = Thickness * sx, th = Thickness * sy;
        if (Style == "dot") { c.Canvas.Fill(x - t / 2, y - th / 2, x + t / 2, y + th / 2, Color, a); return; }
        float g = Gap * sx, s = Size * sx, gy = Gap * sy, syz = Size * sy;
        c.Canvas.Fill(x - g - s, y - th / 2, x - g, y + th / 2, Color, a);
        c.Canvas.Fill(x + g, y - th / 2, x + g + s, y + th / 2, Color, a);
        c.Canvas.Fill(x - t / 2, y - gy - syz, x + t / 2, y - gy, Color, a);
        c.Canvas.Fill(x - t / 2, y + gy, x + t / 2, y + gy + syz, Color, a);
    }
}

/// <summary>
/// Keeps some of the game's own HUD draws (everything the port does not draw itself): <c>match</c> lists categories — sprite groups ("crosshair", "flash",
/// "frags", "lives", "ammo", "zoom", "status", "pips", "team", "digits"), single sprites ("sprite:0x208"), "text" (the game's text font: names ticker,
/// pickup messages, the clock's colon) and "other" (anything else the HUD draws), or "all". A matching draw is kept as the game drew it, moved by
/// <c>x</c>/<c>y</c> frame units. Draws no <c>game</c> element keeps are left out in port mode.
/// </summary>
public sealed class GameElement : HudElement
{
    public readonly HashSet<string> Match = new(StringComparer.OrdinalIgnoreCase);
    public override void Parse(JsonElement e)
    {
        if (!e.TryGetProperty("match", out var m)) throw new FormatException("\"match\" missing");
        var list = m.ValueKind == JsonValueKind.String ? new List<string> { m.GetString()! } : m.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        foreach (var k in list)
        {
            if (!HudCategories.IsKnown(k)) throw new FormatException($"\"match\": unknown category \"{k}\" (known: {string.Join(", ", HudCategories.Groups.Keys)}, text, other, all, sprite:0x...)");
            if (k.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase) && HudCategories.TryId(k, out int id)) { Match.Add($"sprite:0x{id:X}"); SpriteIds.Add(id); }
            else Match.Add(k);
        }
    }
    /// <summary>The single sprites named in <c>match</c>.</summary>
    public readonly List<int> SpriteIds = new();
    public override void Emit(HudContext c) { }      // nothing to draw: the layer keeps the matching game draws
    public bool Matches(IReadOnlyList<string> cats) => Match.Contains("all") || cats.Any(Match.Contains);
}

/// <summary>Categories of the game's HUD draws (by the sprite whose texels a draw uses).</summary>
public static class HudCategories
{
    public static readonly Dictionary<string, int[]> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["digits"] = new[] { GameSprites.DigitFont },
        ["crosshair"] = new[] { 0x2DA, 0x2E4, 0x2BC, 0x2EF, 0x2F0, 0x2F1, 0x2F2, 0x302, 0x320, 0x32A, 0x32B, 0x35C, 0x35D, 0x2C0, 0x2C1 },
        ["flash"] = new[] { 0x208 },
        ["frags"] = new[] { 0x1FA, 0x204, 0x202 },
        ["lives"] = new[] { 0x12C, 0x1F6, 0x203 },
        ["ammo"] = new[] { 0xBEA, 0xBEB, 0xBEC },
        ["zoom"] = new[] { 0x20A, 0x20B },
        ["status"] = new[] { 0x209, 0x20C },
        ["pips"] = new[] { 0x2F4 },
        ["team"] = new[] { 0x1FC, 0x201, 0x1FD },
    };
    public static readonly int[] AllIds = Groups.Values.SelectMany(x => x).Distinct().ToArray();

    public static bool IsKnown(string k) => Groups.ContainsKey(k) || k is "text" or "other" or "all" || (k.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase) && TryId(k, out _));
    public static bool TryId(string k, out int id)
    {
        id = 0; string s = k[7..];
        try { id = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s); return true; } catch (FormatException) { return false; } catch (OverflowException) { return false; }
    }

    /// <summary>The categories of a sprite id: "sprite:0x..", its group, or "other" (-1 = not a library sprite: "text").</summary>
    public static List<string> Of(int id)
    {
        var l = new List<string>();
        if (id < 0) { l.Add("text"); l.Add("other"); return l; }
        l.Add($"sprite:0x{id:X}");
        foreach (var (g, ids) in Groups) if (ids.Contains(id)) l.Add(g);
        if (l.Count == 1) l.Add("other");
        return l;
    }
}
