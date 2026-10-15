using Trw.Platform.Gfx;

namespace Trw.Game.PortHud;

/// <summary>An RGBA colour of the layout (<c>"#RRGGBB"</c>, <c>"#RRGGBBAA"</c>, <c>"white"</c>, ... or <c>[r, g, b, a]</c> 0..255).</summary>
public readonly record struct HudColor(byte R, byte G, byte B, byte A)
{
    public static readonly HudColor White = new(255, 255, 255, 255);

    public static HudColor Parse(string s)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "white": return White;
            case "black": return new(0, 0, 0, 255);
            case "red": return new(255, 0, 0, 255);
            case "green": return new(0, 255, 0, 255);
            case "blue": return new(0, 0, 255, 255);
            case "yellow": return new(255, 255, 0, 255);
            case "orange": return new(240, 130, 30, 255);
            case "gold": return new(255, 200, 70, 255);
            case "grey": case "gray": return new(128, 128, 128, 255);
            case "transparent": return new(0, 0, 0, 0);
        }
        if (s.StartsWith('#')) s = s[1..];
        if ((s.Length == 6 || s.Length == 8) && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            if (s.Length == 6) v = (v << 8) | 0xFF;
            return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        throw new FormatException($"colour \"{s}\": use #RRGGBB, #RRGGBBAA or a name");
    }
}

/// <summary>
/// Collects the port HUD's draws as ordinary <see cref="DrawCommand"/>s in N64 screen coordinates (480x360, W = 1, like the game's own 2D), so they go
/// through exactly the renderer path of the game's HUD: the RDP combiner, the local-view remap, the 4:3 shape in a wide picture (GfxRenderer.Keep2DShape).
/// The render state is the game's sprite state (Sprite_SetRenderState: 2-cycle, TEXEL0 * ENV colour, TEXEL1 * ENV alpha, translucent surface, no z),
/// so a game sprite drawn here looks exactly like the game's draw of it.
/// </summary>
public sealed class HudCanvas
{
    // the game's HUD sprite state, as the interpreter captures it from Sprite_SetRenderState's display list
    public const uint OtherHighLinear = 0x00102C10, OtherHighPoint = 0x00100C10, OtherLow = 0x0C184240, CombineW0 = 0x0012ABFF, CombineW1 = 0xFFFFFE38;

    public readonly List<DrawCommand> Draws = new();
    public float ScreenW = 480, ScreenH = 360;
    public int ScissorX0, ScissorY0, ScissorX1 = 479 * 4, ScissorY1 = 359 * 4;
    public int Tag;            // DrawCommand.Hud of the draws (the section they replace)

    private static readonly DecodedTexture WhiteTex = new() { Width = 1, Height = 1, Rgba = new byte[] { 255, 255, 255, 255 }, Key = 0x4855440000000001UL };

    private DrawState? st;
    private readonly List<GfxVertex> verts = new();

    public void Clear() { Draws.Clear(); verts.Clear(); st = null; }

    /// <summary>A textured rectangle (screen pixels) with the colour / alpha planes and a tint (env colour; alpha 0..1).</summary>
    public void Quad(float x0, float y0, float x1, float y1, DecodedTexture tex0, DecodedTexture tex1, HudColor c, float alpha, bool linear = true,
                     float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
    {
        if (x1 <= x0 || y1 <= y0 || alpha <= 0) return;
        var env = new[] { c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f * Math.Clamp(alpha, 0f, 1f) };
        if (st == null || st.Tex0 != tex0 || st.Tex1 != tex1 || st.Linear != linear || !st.Env.AsSpan().SequenceEqual(env)) { Flush(); st = State(tex0, tex1, env, linear); }
        float X0 = x0 / ScreenW * 2 - 1, X1 = x1 / ScreenW * 2 - 1, Y0 = 1 - y0 / ScreenH * 2, Y1 = 1 - y1 / ScreenH * 2;
        GfxVertex Q(float x, float y, float u, float v) => new() { X = x, Y = y, Z = 0, W = 1, R = 1, G = 1, B = 1, A = 1, U = u, V = v };
        var q0 = Q(X0, Y0, u0, v0); var q1 = Q(X1, Y0, u1, v0); var q2 = Q(X1, Y1, u1, v1); var q3 = Q(X0, Y1, u0, v1);
        verts.Add(q0); verts.Add(q1); verts.Add(q2); verts.Add(q0); verts.Add(q2); verts.Add(q3);
    }

    /// <summary>A plain rectangle in a colour (bars, backgrounds).</summary>
    public void Fill(float x0, float y0, float x1, float y1, HudColor c, float alpha = 1) => Quad(x0, y0, x1, y1, WhiteTex, WhiteTex, c, alpha, false);

    private DrawState State(DecodedTexture t0, DecodedTexture t1, float[] env, bool linear) => new()
    {
        Geom = 0, OtherLow = OtherLow, OtherHigh = linear ? OtherHighLinear : OtherHighPoint, CombineW0 = CombineW0, CombineW1 = CombineW1,
        Prim = new[] { 0.5f, 0.5f, 0.5f, 1f }, Env = env, Mode = DrawMode.Combiner, TextureOn = true, Tex0 = t0, Tex1 = t1,
        Linear = linear, AddressU = 2, AddressV = 2,
        ScissorX0 = ScissorX0, ScissorY0 = ScissorY0, ScissorX1 = ScissorX1, ScissorY1 = ScissorY1, ScreenWidth = ScreenW, ScreenHeight = ScreenH,
    };

    public void Flush()
    {
        if (st != null && verts.Count > 0) Draws.Add(new DrawCommand(st, verts.ToArray()) { Hud = Tag });
        verts.Clear(); st = null;
    }
}
