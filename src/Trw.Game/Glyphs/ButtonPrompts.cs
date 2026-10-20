using Trw.Platform;
using Trw.Platform.Gfx;

namespace Trw.Game.Glyphs;

/// <summary>
/// Replaces the N64 buttons the game shows with the buttons of the device the player uses (docs/BUTTON_GLYPHS.md). A presentation change on the
/// display list's draw commands: guest memory is only read, never written, and the oracle / simulation never see it.
/// <para>What the game draws, and how it is found here:</para>
/// <list type="bullet">
/// <item>Text prompts ("PRESS START", "PRESS 'A' TO SELECT A MAP", "AND PRESS 'START'", "PRESS 'B' TO BYPASS", "MOVE STICK"): the frontend's text is drawn by
/// UiText_FlushGlyphBatches as one texture rectangle per character, the texture being the character's pixels inside its font (UiFont, pool at
/// 0x80153C20: per font the glyph data offsets at +0x304, glyph records {off, w, h} at +4; glyph slot = character - 32). A draw whose texture address
/// is one of those offsets is that character. The characters of a row are put back into strings, the button words found, their quads left out, and the
/// glyph drawn in their place; the rest of the row moves apart to make room (rows stay centred).</item>
/// <item>Button pictures: the frontend image archive's C-up (images 100, 101: "SKINS =", "= DEFAULT" on the character select / loadout) and A button
/// (image 183) pictures. A draw is one of them when the texels at its texture address equal the image's pixels in the ROM archive (ROM 0x200000 +
/// archive data base + the image record's offset), so the match follows the image, not where the game loaded it.</item>
/// </list>
/// The glyph is chosen by <see cref="GlyphSet"/> for <see cref="ActiveInput.Device"/> from the port's bindings, so it follows the device live.
/// <para>Switches: <c>TRW_GLYPHS=keyboard|xbox|playstation|n64|touch</c> pins a set (ActiveInput), <c>TRW_GLYPHS=off</c> leaves the game's buttons alone,
/// <c>TRW_GLYPHS_LOG=1</c> logs every prompt found (once per text).</para>
/// </summary>
public sealed class ButtonPrompts
{
    public static readonly bool Off = string.Equals(Environment.GetEnvironmentVariable("TRW_GLYPHS"), "off", StringComparison.OrdinalIgnoreCase);
    private static readonly bool log = Environment.GetEnvironmentVariable("TRW_GLYPHS_LOG") == "1";
    private readonly HashSet<string> logged = new();

    /// <summary>The frontend archive images that are N64 button pictures.</summary>
    private static readonly (int Image, N64Button Button)[] iconImages = { (100, N64Button.CUp), (101, N64Button.CUp), (183, N64Button.A) };

    private const uint FontPool = 0x80153C20, ImageCount = 0x80153C0C, ImageTable = 0x80153C10, ArchiveDataBase = 0x801539B0 + 0x248, RomArchive = 0x200000;

    private readonly struct CharSet
    {
        public readonly ulong Lo, Hi;      // bit i = glyph slot i (character 32 + i)
        public CharSet(ulong lo, ulong hi) { Lo = lo; Hi = hi; }
        public bool Has(char c)
        {
            foreach (var ch in new[] { char.ToUpperInvariant(c), char.ToLowerInvariant(c) })
            {
                int s = ch - 32;
                if (s >= 0 && s < 64 ? (Lo & (1UL << s)) != 0 : s >= 64 && s < 128 && (Hi & (1UL << (s - 64))) != 0) return true;
            }
            return false;
        }
        public char Show()
        {
            for (int s = 0; s < 128; s++) if (s < 64 ? (Lo & (1UL << s)) != 0 : (Hi & (1UL << (s - 64))) != 0) return char.ToUpperInvariant((char)(s + 32));
            return '?';
        }
    }

    private sealed class Quad
    {
        public int Cmd, Index;               // command index, quad index (6 vertices each)
        public float X0, Y0, X1, Y1;         // screen pixels of the command's frame
        public CharSet Chars;
        public N64Button? Icon;
        public bool Space;                   // a word gap before this character
    }

    private readonly Dictionary<uint, (CharSet set, int h)> fontMap = new();
    private readonly Dictionary<ulong, N64Button?> iconByKey = new();
    private readonly PortHud.HudCanvas canvas = new();

    /// <summary>Rebuilds the address -> characters map from the UI font pool (read only).</summary>
    private void ReadFonts()
    {
        fontMap.Clear();
        int count = Mem.S16(FontPool);
        uint table = Mem.U32(FontPool + 8);
        if (count <= 0 || count > 16 || !Mem.IsMapped(table)) return;
        var acc = new Dictionary<uint, (ulong lo, ulong hi, int h)>();
        for (int f = 0; f < count; f++)
        {
            uint o = table + (uint)(f * 1180);
            if (Mem.S32(o) == 0) continue;      // not loaded
            for (int s = 0; s < 96; s++)
            {
                uint a = Mem.U32(o + 0x304 + (uint)(s * 4));
                int h = Mem.S8(o + 4 + (uint)(s * 8) + 5);
                if (a == 0 || h <= 0) continue;
                acc.TryGetValue(a, out var e);
                if (s < 64) e.lo |= 1UL << s; else e.hi |= 1UL << (s - 64);
                e.h = h;
                acc[a] = e;
            }
        }
        foreach (var (a, e) in acc) fontMap[a] = (new CharSet(e.lo, e.hi), e.h);
    }

    /// <summary>Which N64 button picture a texture is (null: none). Compares the texels at its address with the archive images' pixels in the ROM.</summary>
    private N64Button? Icon(DecodedTexture t)
    {
        if (iconByKey.TryGetValue(t.Key, out var known)) return known;
        N64Button? found = null;
        int n = Mem.S16(ImageCount);
        uint table = Mem.U32(ImageTable);
        if (n <= 0 || !Mem.IsMapped(table) || Host.Rom == null || !Mem.IsMapped(t.Src)) return null;      // not decided yet: try again later
        foreach (var (img, button) in iconImages)
        {
            if (img >= n) continue;
            uint r = table + (uint)(img * 28);
            int bits = Mem.S16(r + 8), w = Mem.S16(r + 10), h = Mem.S16(r + 12);
            int pitch = bits == 8 ? (w + 7) & ~7 : bits == 4 ? (w + 15) & ~15 : bits == 16 ? (w + 3) & ~3 : w;
            int bytes = pitch * h * bits / 8;
            long rom = RomArchive + (long)Mem.S32(ArchiveDataBase) + Mem.S32(r + 20);
            if (bytes <= 0 || bytes > 8192 || rom < 0 || rom + bytes > Host.Rom.Length) continue;
            bool same = true;
            for (int i = 0; i < bytes && same; i++) same = Mem.U8(t.Src + (uint)i) == Host.Rom[rom + i];
            if (same) { found = button; break; }
        }
        iconByKey[t.Key] = found;
        return found;
    }

    /// <summary>The draw commands with the game's buttons replaced; the same list when there is nothing to replace.</summary>
    /// <param name="targetHeight">height in pixels of the target the frame renders into (the glyphs are rasterized at the size they are shown)</param>
    public IReadOnlyList<GfxCommand> Apply(IReadOnlyList<GfxCommand> cmds, int targetHeight)
    {
        if (Off) return cmds;
        float pxPerUnit = 1;
        ReadFonts();
        var quads = new List<Quad>();
        var spaces = new List<(float x0, float x1, float y0, float y1)>();
        for (int i = 0; i < cmds.Count; i++)
        {
            if (cmds[i] is not DrawCommand d || !d.State.TextureOn || d.State.Tex0 is not { } tex || d.Vertices.Length % 6 != 0) continue;
            bool isFont = fontMap.TryGetValue(tex.Src, out var fe);
            N64Button? icon = isFont ? null : (tex.Width <= 64 && tex.Height <= 64 ? Icon(tex) : null);
            if (!isFont && icon == null) continue;
            float sw = d.State.ScreenWidth, sh = d.State.ScreenHeight;
            pxPerUnit = targetHeight / Math.Max(1f, sh);
            for (int q = 0; q < d.Vertices.Length / 6; q++)
            {
                var v = d.Vertices.AsSpan(q * 6, 6);
                if (MathF.Abs(v[0].W - 1) > 1e-4f) break;      // 2D only
                float x0 = (v[0].X + 1) * 0.5f * sw, x1 = (v[2].X + 1) * 0.5f * sw, y0 = (1 - v[0].Y) * 0.5f * sh, y1 = (1 - v[2].Y) * 0.5f * sh;
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (y1 < y0) (y0, y1) = (y1, y0);
                if (isFont && fe.set.Show() == ' ') { spaces.Add((x0, x1, y0, y1)); continue; }       // the space is drawn too (blank): it marks word gaps
                // a whole character: drawn at its glyph's height from the top of its texture (the menus' wipe effects draw slices of glyphs, which say nothing)
                if (isFont && (MathF.Abs(y1 - y0 - fe.h) > 0.75f || MathF.Abs(v[0].V) > 1e-3f)) continue;
                quads.Add(new Quad { Cmd = i, Index = q, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Chars = fe.set, Icon = icon });
            }
        }
        if (quads.Count == 0) return cmds;

        bool gameplay = Widescreen.InLevel;
        var device = ActiveInput.Device;
        var removed = new Dictionary<int, HashSet<int>>();
        var shift = new Dictionary<(int, int), float>();
        var inserts = new Dictionary<int, List<DrawCommand>>();      // after command index

        // the button pictures
        foreach (var q in quads.Where(q => q.Icon != null))
        {
            var d = (DrawCommand)cmds[q.Cmd];
            float h = (q.Y1 - q.Y0) * 1.15f, cy = (q.Y0 + q.Y1) * 0.5f, cx = (q.X0 + q.X1) * 0.5f;
            if (Place(d, q.Icon!.Value, device, gameplay, cx, cy, h, pxPerUnit, Alpha(d, q), q.Cmd, inserts) is null) continue;
            Remove(removed, q.Cmd, q.Index);
            Log($"picture {q.Icon} at {q.X0:0},{q.Y0:0} -> {GlyphSet.Resolve(q.Icon.Value, device, gameplay)}");
        }

        // the text: rows of characters (same top and height), split where the gap is wider than a few characters
        foreach (var row in quads.Where(q => q.Icon == null).GroupBy(q => (MathF.Round(q.Y0 * 2), MathF.Round((q.Y1 - q.Y0) * 2))))
        {
            var sorted = row.OrderBy(q => q.X0).ToList();
            float lh = sorted[0].Y1 - sorted[0].Y0;
            // word gaps: a drawn space between two characters, else a gap clearly wider than the row's usual letter gap
            var gaps = sorted.Zip(sorted.Skip(1), (p, n) => n.X0 - p.X1).OrderBy(g => g).ToList();
            float usual = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0;
            bool SpaceBetween(Quad p, Quad n) => spaces.Any(sp => sp.x0 >= p.X1 - 1.5f && sp.x0 <= n.X0 + 0.5f && sp.y1 > p.Y0 && sp.y0 < p.Y1)
                                                 || n.X0 - p.X1 > usual + Math.Max(1.5f, lh * 0.3f);
            var run = new List<Quad>();
            for (int i = 0; i <= sorted.Count; i++)
            {
                if (i < sorted.Count && (run.Count == 0 || sorted[i].X0 - run[^1].X1 < lh * 2.5f))
                {
                    if (run.Count > 0) sorted[i].Space = SpaceBetween(run[^1], sorted[i]);
                    run.Add(sorted[i]);
                    continue;
                }
                if (run.Count > 0) Run(cmds, run, lh, device, gameplay, pxPerUnit, removed, shift, inserts);
                run = new List<Quad>();
                if (i < sorted.Count) i--;
            }
        }
        if (removed.Count == 0 && inserts.Count == 0) return cmds;

        var outList = new List<GfxCommand>(cmds.Count + 8);
        for (int i = 0; i < cmds.Count; i++)
        {
            var c = cmds[i];
            if (c is DrawCommand d && (removed.ContainsKey(i) || shift.Keys.Any(k => k.Item1 == i)))
            {
                removed.TryGetValue(i, out var gone);
                float sw = d.State.ScreenWidth;
                var verts = new List<GfxVertex>(d.Vertices.Length);
                for (int q = 0; q < d.Vertices.Length / 6; q++)
                {
                    if (gone != null && gone.Contains(q)) continue;
                    float dx = shift.TryGetValue((i, q), out var s) ? s * 2f / sw : 0f;
                    for (int k = 0; k < 6; k++) { var v = d.Vertices[q * 6 + k]; v.X += dx * v.W; verts.Add(v); }
                }
                if (verts.Count > 0) outList.Add(new DrawCommand(d.State, verts.ToArray()) { Hud = d.Hud, HudData = d.HudData });
            }
            else outList.Add(c);
            if (inserts.TryGetValue(i, out var extra)) outList.AddRange(extra);
        }
        return outList;
    }

    private static void Remove(Dictionary<int, HashSet<int>> removed, int cmd, int quad)
    {
        if (!removed.TryGetValue(cmd, out var s)) removed[cmd] = s = new HashSet<int>();
        s.Add(quad);
    }

    /// <summary>One run of text: finds the button words, takes their characters out, puts the glyph in and moves the rest of the run apart.</summary>
    private void Run(IReadOnlyList<GfxCommand> cmds, List<Quad> run, float lh, InputDeviceKind device, bool gameplay, float pxPerUnit,
                     Dictionary<int, HashSet<int>> removed, Dictionary<(int, int), float> shift, Dictionary<int, List<DrawCommand>> inserts)
    {
        var tokens = Find(run);
        if (log && Environment.GetEnvironmentVariable("TRW_GLYPHS_LOG_ALL") == "1" && logged.Add("all " + Text(run))) Console.Error.WriteLine($"[glyphs] text \"{Text(run)}\"");
        if (tokens.Count == 0) return;
        string text = Text(run);
        if (log && logged.Add(text + device)) Console.Error.WriteLine($"[glyphs] \"{text}\": {string.Join(", ", tokens.Select(t => $"{t.button} -> {GlyphSet.Resolve(t.button, device, gameplay)}"))}");
        var offsets = new float[run.Count];
        float total = 0;
        int last = run.Max(q => q.Cmd);
        var placed = new List<(float x, float w, N64Button b, float alpha, DrawCommand d)>();
        foreach (var (start, len, b) in tokens)
        {
            float tx0 = run[start].X0, tx1 = run[start + len - 1].X1;
            var d = (DrawCommand)cmds[run[start].Cmd];
            float gh = lh * 1.75f;
            var g = GlyphSet.Resolve(b, device, gameplay);
            var img = GlyphRaster.Get(g.Svg, g.Label, (int)MathF.Ceiling(gh * pxPerUnit));
            if (img == null) continue;
            float gw = gh * img.Aspect, pad = lh * 0.15f;
            float delta = gw + 2 * pad - (tx1 - tx0);
            for (int k = start; k < start + len; k++) Remove(removed, run[k].Cmd, run[k].Index);
            for (int k = start + len; k < run.Count; k++) offsets[k] += delta;
            placed.Add((tx0 + pad + total, gw, b, Alpha(d, run[start]), d));
            total += delta;
        }
        // the run was centred: it stays centred around the same point
        for (int k = 0; k < run.Count; k++)
        {
            float s = offsets[k] - total / 2;
            if (s != 0) shift[(run[k].Cmd, run[k].Index)] = s;
        }
        float cy = (run[0].Y0 + run[0].Y1) * 0.5f;
        foreach (var (x, w, b, alpha, d) in placed)
            Place(d, b, device, gameplay, x - total / 2 + w / 2, cy, lh * 1.75f, pxPerUnit, alpha, last, inserts);
    }

    /// <summary>Draws the glyph of <paramref name="b"/> centred at (cx, cy), <paramref name="h"/> screen pixels high, after command <paramref name="after"/>.</summary>
    private DrawCommand? Place(DrawCommand like, N64Button b, InputDeviceKind device, bool gameplay, float cx, float cy, float h, float pxPerUnit, float alpha,
                               int after, Dictionary<int, List<DrawCommand>> inserts)
    {
        var g = GlyphSet.Resolve(b, device, gameplay);
        var img = GlyphRaster.Get(g.Svg, g.Label, (int)MathF.Ceiling(h * pxPerUnit));
        if (img == null) return null;
        float w = h * img.Aspect;
        canvas.Clear();
        var st = like.State;
        canvas.ScreenW = st.ScreenWidth; canvas.ScreenH = st.ScreenHeight;
        canvas.ScissorX0 = st.ScissorX0; canvas.ScissorY0 = st.ScissorY0; canvas.ScissorX1 = st.ScissorX1; canvas.ScissorY1 = st.ScissorY1;
        canvas.Tag = like.Hud;
        canvas.Quad(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, img.Texture, img.Texture, PortHud.HudColor.White, alpha);
        canvas.Flush();
        if (canvas.Draws.Count == 0) return null;
        if (!inserts.TryGetValue(after, out var l)) inserts[after] = l = new List<DrawCommand>();
        var dc = canvas.Draws[0] with { HudData = like.HudData };
        l.Add(dc);
        return dc;
    }

    /// <summary>The opacity the game gave the replaced draw (fades, blinking): its alpha combiner for an opaque texel.</summary>
    private static float Alpha(DrawCommand d, Quad q)
    {
        var st = d.State;
        float shade = d.Vertices[q.Index * 6].A;
        uint w0 = st.CombineW0, w1 = st.CombineW1;
        float In(uint sel, bool isC, float comb) => sel switch
        {
            0 => isC ? 0 : comb, 1 or 2 => 1, 3 => st.Prim[3], 4 => shade, 5 => st.Env[3], 6 => isC ? 0 : 1, _ => 0,
        };
        float c1 = (In((w0 >> 12) & 7, false, 1) - In((w1 >> 12) & 7, false, 1)) * In((w0 >> 9) & 7, true, 1) + In((w1 >> 9) & 7, false, 1);
        bool twoCycle = ((st.OtherHigh >> 20) & 3) == 1;
        float a = c1;
        if (twoCycle) a = (In((w1 >> 21) & 7, false, c1) - In((w1 >> 3) & 7, false, c1)) * In((w1 >> 18) & 7, true, c1) + In(w1 & 7, false, c1);
        return Math.Clamp(a, 0f, 1f);
    }

    private static string Text(List<Quad> run) => string.Concat(run.Select(q => (q.Space ? " " : "") + q.Chars.Show()));

    private static readonly (string word, N64Button b)[] quoted =
        { ("START", N64Button.Start), ("A", N64Button.A), ("B", N64Button.B), ("Z", N64Button.Z), ("L", N64Button.L), ("R", N64Button.R) };

    /// <summary>The button words of a run: a quoted button name ('A', 'B', 'START' ...), START after PRESS, STICK after MOVE.</summary>
    private static List<(int start, int len, N64Button button)> Find(List<Quad> run)
    {
        var found = new List<(int, int, N64Button)>();
        bool At(int i, string w)
        {
            if (i < 0 || i + w.Length > run.Count) return false;
            for (int k = 0; k < w.Length; k++) if (!run[i + k].Chars.Has(w[k]) || (k > 0 && run[i + k].Space)) return false;
            return true;
        }
        bool WordEnds(int i) => i >= run.Count || run[i].Space;
        for (int i = 0; i < run.Count; i++)
        {
            if (At(i, "'"))
            {
                foreach (var (w, b) in quoted)
                    if (At(i + 1, w) && At(i + 1 + w.Length, "'") && !run[i + 1].Space && !run[i + 1 + w.Length].Space)
                    { found.Add((i, w.Length + 2, b)); i += w.Length + 1; break; }
                continue;
            }
            if (run[i].Space || i == 0)
            {
                // "PRESS START": the word START right after the word PRESS
                if (At(i, "START") && WordEnds(i + 5) && i >= 5 && At(i - 5, "PRESS") && (i - 5 == 0 || run[i - 5].Space)) { found.Add((i, 5, N64Button.Start)); i += 4; continue; }
                if (At(i, "STICK") && WordEnds(i + 5) && i >= 4 && At(i - 4, "MOVE") && (i - 4 == 0 || run[i - 4].Space)) { found.Add((i, 5, N64Button.Stick)); i += 4; continue; }
            }
        }
        return found;
    }

    private void Log(string s) { if (log && logged.Add(s)) Console.Error.WriteLine("[glyphs] " + s); }
}
