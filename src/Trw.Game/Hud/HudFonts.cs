using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Trw.Platform.Gfx;

namespace Trw.Game.PortHud;

/// <summary>
/// Text for the port HUD: the Peaceful Wars sprite fonts of <c>Content/</c> (the scoreboard's: <c>Scoreboard</c>, <c>ScoreboardTitle</c>, <c>NetFont</c>, <c>NetTitle</c>)
/// turned into a white coverage atlas, so glyphs are drawn as <see cref="HudCanvas"/> quads tinted by the element's colour, through the same path as the sprites.
/// </summary>
public sealed class HudFonts : IDisposable
{
    public sealed class Font
    {
        public required DecodedTexture Atlas;
        public required Dictionary<char, SpriteFont.Glyph> Glyphs;
        public int LineSpacing; public float Spacing; public char? Default;
    }

    private readonly ContentManager? content;
    private readonly Dictionary<string, Font?> fonts = new(StringComparer.OrdinalIgnoreCase);
    private static ulong key = 0x4855460000000000UL;

    public HudFonts(GraphicsDevice? device)
    {
        if (device == null) return;
        var services = new GameServiceContainer();
        services.AddService(typeof(IGraphicsDeviceService), new DeviceService(device));
        // Android: the content is in the APK's assets, which MonoGame opens only by a relative path
        content = new ContentManager(services, OperatingSystem.IsAndroid() ? "Content" : Path.Combine(AppContext.BaseDirectory, "Content"));
    }

    /// <summary>The font of that name (a .spritefont in Content), or null when it does not exist.</summary>
    public Font? Get(string name)
    {
        if (fonts.TryGetValue(name, out var f)) return f;
        f = null;
        try
        {
            if (content != null)
            {
                var sf = content.Load<SpriteFont>(name);
                var tex = sf.Texture;
                var px = new Color[tex.Width * tex.Height];
                tex.GetData(px);
                var rgba = new byte[px.Length * 4];
                for (int i = 0; i < px.Length; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = px[i].A; }   // premultiplied white: coverage = alpha
                f = new Font
                {
                    Atlas = new DecodedTexture { Width = tex.Width, Height = tex.Height, Rgba = rgba, Key = Interlocked.Increment(ref key) },
                    Glyphs = sf.GetGlyphs(), LineSpacing = sf.LineSpacing, Spacing = sf.Spacing, Default = sf.DefaultCharacter,
                };
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"[hud] font {name}: {e.Message}"); }
        fonts[name] = f;
        return f;
    }

    /// <summary>Width of <paramref name="s"/> in font pixels (one line).</summary>
    public static float Measure(Font f, string s)
    {
        float x = 0, w = 0; bool first = true;
        foreach (char ch in s)
        {
            if (!f.Glyphs.TryGetValue(ch, out var g) && !(f.Default is char d && f.Glyphs.TryGetValue(d, out g))) continue;
            if (first) { x = Math.Max(g.LeftSideBearing, 0); first = false; } else x += f.Spacing + g.LeftSideBearing;
            x += g.Width;
            w = Math.Max(w, x + Math.Max(g.RightSideBearing, 0));
            x += g.RightSideBearing;
        }
        return w;
    }

    /// <summary>Draws one line at screen (x, y) (top-left), glyph pixels scaled by (sx, sy).</summary>
    public static void Draw(HudCanvas c, Font f, string s, float x, float y, float sx, float sy, HudColor col, float alpha)
    {
        float ox = 0; bool first = true;
        float tw = f.Atlas.Width, th = f.Atlas.Height;
        foreach (char ch in s)
        {
            if (!f.Glyphs.TryGetValue(ch, out var g) && !(f.Default is char d && f.Glyphs.TryGetValue(d, out g))) continue;
            if (first) { ox = Math.Max(g.LeftSideBearing, 0); first = false; } else ox += f.Spacing + g.LeftSideBearing;
            var b = g.BoundsInTexture; var cr = g.Cropping;
            float gx = x + (ox + cr.X) * sx, gy = y + cr.Y * sy;
            if (b.Width > 0 && b.Height > 0)
                c.Quad(gx, gy, gx + b.Width * sx, gy + b.Height * sy, f.Atlas, f.Atlas, col, alpha, true, b.X / tw, b.Y / th, b.Right / tw, b.Bottom / th);
            ox += g.Width + g.RightSideBearing;
        }
    }

    public void Dispose() => content?.Dispose();

    private sealed class DeviceService : IGraphicsDeviceService
    {
        public DeviceService(GraphicsDevice d) { GraphicsDevice = d; }
        public GraphicsDevice GraphicsDevice { get; }
#pragma warning disable CS0067
        public event EventHandler<EventArgs>? DeviceCreated, DeviceDisposing, DeviceReset, DeviceResetting;
#pragma warning restore CS0067
    }
}
