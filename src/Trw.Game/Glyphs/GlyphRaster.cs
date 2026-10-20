using SkiaSharp;
using Svg.Skia;
using Trw.Platform.Gfx;

namespace Trw.Game.Glyphs;

/// <summary>
/// Turns the glyph SVGs of <c>Content/Glyphs/&lt;set&gt;/&lt;name&gt;.svg</c> into textures. MonoGame cannot draw SVG, so each glyph is rasterized at runtime
/// (Svg.Skia over SkiaSharp) at the pixel height it is shown at, and cached: sharp at any window size, nothing to rebuild when an SVG changes (a new
/// run picks it up). Text inside the SVGs uses the game's font, Peaceful Wars (<c>Content/Fonts</c>), which is handed to the SVG renderer as a custom
/// typeface. <c>keyboard/keycap.svg</c> is a template: the key's label is measured with that font and the cap is made as wide as the label needs.
/// </summary>
public static class GlyphRaster
{
    /// <summary>A rasterized glyph: RGBA (straight alpha), its size in pixels, and the shape it was drawn for (width / height).</summary>
    public sealed record Image(DecodedTexture Texture, float Aspect);

    private static readonly Dictionary<(string, string?, int), Image?> cache = new();
    private static readonly Dictionary<string, string?> sources = new();
    private static SKTypeface? font;
    private static readonly object gate = new();
    private static ulong keySerial;

    /// <summary>The folder of the glyph SVGs (next to the executable).</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Content", "Glyphs");

    private static SKTypeface Font()
    {
        if (font != null) return font;
        var path = Path.Combine(AppContext.BaseDirectory, "Content", "Fonts", "Peaceful Wars.ttf");
        font = (File.Exists(path) ? SKTypeface.FromFile(path) : null) ?? SKTypeface.Default;
        return font;
    }

    private static byte[]? fontBytes;
    private static byte[] FontBytes() => fontBytes ??= File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Content", "Fonts", "Peaceful Wars.ttf"));

    private static string? Source(string name)
    {
        if (sources.TryGetValue(name, out var s)) return s;
        var path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar) + ".svg");
        try { s = File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { s = null; }
        if (s == null) Console.Error.WriteLine($"[glyphs] missing {path}");
        sources[name] = s;
        return s;
    }

    /// <summary>The glyph <paramref name="name"/> ("xbox/a", "keyboard/keycap" with <paramref name="label"/>) rasterized <paramref name="heightPx"/> pixels
    /// high (rounded up to a multiple of 8, so a few sizes cover every frame). Null when the SVG is missing or cannot be drawn.</summary>
    public static Image? Get(string name, string? label, int heightPx)
    {
        heightPx = Math.Clamp((heightPx + 7) / 8 * 8, 16, 512);
        lock (gate)
        {
            if (cache.TryGetValue((name, label, heightPx), out var hit)) return hit;
            Image? img = null;
            try { img = Render(name, label, heightPx); }
            catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"[glyphs] {name}: {e.Message}"); }
            if (cache.Count > 600) cache.Clear();
            cache[(name, label, heightPx)] = img;
            return img;
        }
    }

    /// <summary>The SVG text of a glyph, with the keycap template filled in for <paramref name="label"/>.</summary>
    public static string? Svg(string name, string? label)
    {
        var src = Source(name);
        if (src == null || label == null || !src.Contains("{{")) return src;
        // the keycap: label at 30 units (24 when long), the cap 20 units wider than the label, at least square
        float size = label.Length > 2 ? 24 : 30;
        using var f = new SKFont(Font(), size);
        float tw = f.MeasureText(label);
        float w = MathF.Max(64, MathF.Ceiling(tw + 26));
        return src.Replace("{{W}}", F(w)).Replace("{{FACE_W}}", F(w - 3)).Replace("{{INNER_W}}", F(w - 14)).Replace("{{CX}}", F(w / 2))
                  .Replace("{{BASE}}", F(28 + size * 0.36f)).Replace("{{FONT_SIZE}}", F(size)).Replace("{{LABEL}}", System.Security.SecurityElement.Escape(label));
    }

    private static string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static Image? Render(string name, string? label, int heightPx)
    {
        var text = Svg(name, label);
        if (text == null) return null;
        using var svg = new SKSvg();
        svg.Settings.TypefaceProviders = new List<Svg.Skia.TypefaceProviders.ITypefaceProvider>
        {
            new Svg.Skia.TypefaceProviders.CustomTypefaceProvider(new MemoryStream(FontBytes())),
            new Svg.Skia.TypefaceProviders.DefaultTypefaceProvider(),
        };
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var pic = svg.Load(ms);
        if (pic == null) return null;
        var box = pic.CullRect;
        if (box.Width <= 0 || box.Height <= 0) return null;
        float scale = heightPx / box.Height;
        int w = Math.Max(1, (int)MathF.Ceiling(box.Width * scale)), h = heightPx;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-box.Left, -box.Top);
            canvas.DrawPicture(pic);
        }
        var rgba = bmp.Bytes;
        var tex = new DecodedTexture { Width = w, Height = h, Rgba = rgba, Key = 0x474C595000000000UL | ++keySerial };
        return new Image(tex, w / (float)h);
    }

    /// <summary>Test / preview aid (<c>TRW_GLYPHS_SHEET=&lt;file.png&gt;</c>): every glyph of every set, plus sample keycaps, on one sheet.</summary>
    public static void WriteSheet(string path, int height = 64)
    {
        var names = Directory.GetFiles(Root, "*.svg", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')[..^4]).Where(n => n != "keyboard/keycap").OrderBy(n => n).ToList();
        var items = names.Select(n => (n, (string?)null)).ToList();
        foreach (var l in new[] { "E", "Q", "SPACE", "ENTER", "CTRL", "BKSP", "NUM 8", "X" }) items.Add(("keyboard/keycap", l));
        var imgs = items.Select(i => (i.Item1, i.Item2, Get(i.Item1, i.Item2, height))).ToList();
        int cols = 12, cell = height * 2 + 16, rows = (imgs.Count + cols - 1) / cols;
        using var bmp = new SKBitmap(cols * cell, rows * (height + 30));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(70, 58, 44));
        using var label = new SKFont(SKTypeface.Default, 10);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (int i = 0; i < imgs.Count; i++)
        {
            var (n, l, img) = imgs[i];
            float x = i % cols * cell + 4, y = i / cols * (height + 30) + 4;
            if (img != null)
            {
                var t = img.Texture;
                using var gb = new SKBitmap(new SKImageInfo(t.Width, t.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                System.Runtime.InteropServices.Marshal.Copy(t.Rgba, 0, gb.GetPixels(), t.Rgba.Length);
                canvas.DrawBitmap(gb, x, y);
            }
            canvas.DrawText(l != null ? $"key {l}" : n, x, y + height + 14, label, paint);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }
}
