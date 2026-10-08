using System.Text.Json;
using Trw.Tools.Levels;

namespace Trw.Tools.Models;

/// <summary>
/// "trw texanim [rom] [levelgeoDir] [texturesDir]": exports how the game animates textures and blends materials
/// (docs/formats/transparency_and_animation.md) to <c>extracted/levelgeo/texture_anim.json</c> and writes the palette-cycled PNG
/// variants (<c>tex_NNN_f{frame}_p{palette}.png</c>) that the classic exporter does not produce.
/// Program.cs hook: <c>if (cmd == "texanim") return Trw.Tools.Models.TextureAnim.Run(args.Skip(1).ToArray());</c>
/// </summary>
public static class TextureAnim
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public enum BlendMode { Opaque, Cutout, AlphaBlend }

    /// <summary>What the material setup (func_00269A80 + func_00268CE0 + func_0026925C + func_0026C020) does for a material.</summary>
    public sealed record MaterialLook(
        string Blend,            // opaque | cutout | alpha
        bool TranslucentPass,    // flags & 0x700 != 0: drawn after all opaque batches, ascending Field8 order
        bool DepthWrite,
        bool Decal,              // flags & 0x200: depth mode DECAL
        bool SkipIfAlpha0,       // flags & 0x100 and colour1.a == 0 -> part not drawn
        string AlphaSource,      // "texel*colour1.a" | "texel*vertex.a"
        string ColorMode,        // tex | tex*shade | lerp(colour2,colour1,tex) | lerp(colour2,colour1,tex)*shade
        bool Fog,                // geometry-mode fog bit set unless flags & 0x1000
        bool ShadeIgnored,       // flags & 0x2: combiner does not multiply the vertex colour
        int SortKey);

    public static MaterialLook Classify(uint flags, byte[] c1, short field8)
    {
        bool xlu = (flags & 0x100) != 0, dec = (flags & 0x200) != 0, v1000 = (flags & 0x1000) != 0;
        bool two = (flags & 0x400) != 0, unlitCombiner = (flags & 0x2) != 0;
        string blend = xlu ? "alpha" : "cutout";   // textured opaque materials use ALPHA_CVG_SEL: texel alpha 0 is not written
        string color = two ? (unlitCombiner ? "lerp(colour2,colour1,tex)" : "lerp(colour2,colour1,tex)*shade")
                           : (unlitCombiner ? "tex" : "tex*shade");
        // 0x200 non-translucent = decal depth mode, still writes no depth (render mode 7); 0x80 = AA cutout with depth write (mode 6)
        bool depthWrite = !xlu && !dec;
        int sort = (field8 << 1) | ((flags & 0x40) != 0 ? 1 : 0);
        return new MaterialLook(blend, (flags & 0x700) != 0, depthWrite, dec, xlu, v1000 ? "texel*vertex.a" : "texel*colour1.a",
            color, !v1000, unlitCombiner, sort);
    }

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    public static int Run(string[] args)
    {
        string rom = DefaultRom;
        var a = args.ToList();
        if (a.Count > 0 && File.Exists(a[0])) { rom = a[0]; a.RemoveAt(0); }
        string lvDir = Path.GetFullPath(a.Count > 0 ? a[0] : Path.Combine("extracted", "levelgeo"));
        string texDir = Path.GetFullPath(a.Count > 1 ? a[1] : Path.Combine("extracted", "models", "textures"));
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }
        var ix = RomIndex.Load(File.ReadAllBytes(rom));

        // --- textures ------------------------------------------------------------------------------------------------
        var texNode = ix.Find(ModelsCommand.MasterTextureSection);
        var textures = new List<CiTexture?>();
        if (texNode != null) foreach (var c in texNode.Children) textures.Add(CiTexture.Parse(ix.Read(c)));

        // --- level placements (object -> keys, levels) ------------------------------------------------------------------
        var keysByObj = new SortedDictionary<int, SortedSet<int>>();
        var levelsByObj = new SortedDictionary<int, SortedSet<int>>();
        if (Directory.Exists(lvDir))
            foreach (var f in Directory.GetFiles(lvDir, "objects.json", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(Path.GetDirectoryName(f)!);
                if (!int.TryParse(name.AsSpan(0, 2), out int lvl)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var o in doc.RootElement.EnumerateArray())
                {
                    int id = o.GetProperty("ObjectId").GetInt32();
                    int key = o.TryGetProperty("textureKey", out var k) ? k.GetInt32() : 0;
                    if (!keysByObj.TryGetValue(id, out var ks)) keysByObj[id] = ks = new();
                    ks.Add(key);
                    if (!levelsByObj.TryGetValue(id, out var ls)) levelsByObj[id] = ls = new();
                    ls.Add(lvl);
                }
            }

        // --- materials of every object model ---------------------------------------------------------------------------
        var objects = new List<object>();
        var animatedMaterials = new List<object>();
        // texture -> (objects using it, with which keys)
        var texObjects = new SortedDictionary<int, SortedSet<int>>();
        var texAnimatedObjects = new SortedDictionary<int, SortedSet<int>>();
        var combos = new SortedDictionary<string, (uint Flags, MaterialLook Look, SortedSet<int> Tex, SortedSet<int> Alphas, int Count, SortedSet<int> Field8)>();
        var objRoot = ix.Find(0);
        int objCount = objRoot?.Children.Count ?? 0;
        for (int id = 0; id < objCount; id++)
        {
            var mnode = ix.Find(0, id, 1);
            if (mnode == null) continue;
            var mats = new List<object>();
            for (int mi = 0; mi < mnode.Children.Count; mi++)
            {
                var node = mnode.Children[mi];
                if (node.Kind != NodeKind.Rnc) continue;
                var model = Model.Parse(ix.Read(node));
                if (model == null) continue;
                for (int p = 0; p < model.Parts.Length; p++)
                {
                    var m = model.Parts[p].Material;
                    var look = Classify(m.Flags, m.Color1, m.Field8);
                    keysByObj.TryGetValue(id, out var keys);
                    bool anim = keys?.Contains(-1) == true;
                    if (m.TextureId >= 0)
                    {
                        if (!texObjects.TryGetValue(m.TextureId, out var so)) texObjects[m.TextureId] = so = new();
                        so.Add(id);
                        if (anim)
                        {
                            if (!texAnimatedObjects.TryGetValue(m.TextureId, out var sa)) texAnimatedObjects[m.TextureId] = sa = new();
                            sa.Add(id);
                        }
                    }
                    if (anim && m.TextureId >= 0 && m.TextureId < textures.Count && textures[m.TextureId] is { } at)
                        animatedMaterials.Add(new
                        {
                            objectId = id, model = mi, part = p, textureId = m.TextureId, flags = "0x" + m.Flags.ToString("X"),
                            frames = at.Frames.Length, palettes = at.Palettes.Length, stepTicks = (int)at.Param1,
                            colour1 = m.Color1.Select(b => (int)b).ToArray(), colour2 = m.Color2.Select(b => (int)b).ToArray(),
                            blend = look.Blend, colorMode = look.ColorMode, sortKey = look.SortKey,
                            levels = levelsByObj.TryGetValue(id, out var lv2) ? lv2.ToArray() : []
                        });
                    string ck = $"{m.Flags:X}";
                    if (!combos.TryGetValue(ck, out var cv)) cv = (m.Flags, look, new(), new(), 0, new());
                    cv.Tex.Add(m.TextureId); cv.Alphas.Add(m.Color1[3]); cv.Field8.Add(m.Field8); cv.Count++;
                    combos[ck] = cv;
                    mats.Add(new
                    {
                        model = mi, part = p, textureId = m.TextureId, flags = "0x" + m.Flags.ToString("X"),
                        field8 = m.Field8, fieldA = m.FieldA,
                        colour1 = m.Color1.Select(b => (int)b).ToArray(), colour2 = m.Color2.Select(b => (int)b).ToArray(), alpha = Math.Round(m.Color1[3] / 255.0, 3),
                        scaleS = m.ScaleS, scaleT = m.ScaleT,
                        look
                    });
                }
            }
            if (mats.Count == 0) continue;
            objects.Add(new
            {
                objectId = id,
                textureKeys = keysByObj.TryGetValue(id, out var kk) ? kk.ToArray() : [],
                levels = levelsByObj.TryGetValue(id, out var ll) ? ll.ToArray() : [],
                uvScroll = false,
                materials = mats
            });
        }

        // --- texture table -----------------------------------------------------------------------------------------------
        var texList = new List<object>();
        int written = 0, rewritten = 0;
        Directory.CreateDirectory(texDir);
        for (int id = 0; id < textures.Count; id++)
        {
            var t = textures[id];
            if (t == null) continue;
            int F = t.Frames.Length, P = t.Palettes.Length;
            string kind = F == 1 && P == 1 ? "static"
                : P == 1 ? "frames"
                : F == 1 ? "palette"
                : F == P ? "frames+palette(paired)" : "frames+palettes";
            if (t.FormatByte == 3)   // IA16 palettes were decoded as RGBA5551 before: rewrite every PNG of this texture that already exists
                for (int fr = 0; fr < F; fr++)
                {
                    string old = Path.Combine(texDir, $"tex_{id:D3}_f{fr}.png");
                    if (File.Exists(old)) { PngWriter.WriteRgba(old, t.Width, t.Height, t.ToRgba(fr)); rewritten++; }
                }
            int lcm = F * P / Gcd(F, P);
            int period = 256 % lcm == 0 ? lcm : 256;     // k is masked with 255, so the cycle restarts every 256 steps unless lcm divides 256
            bool timeAnimated = texAnimatedObjects.ContainsKey(id);
            var look = t.ReachableLooks();
            // PNG variants not produced by the classic exporter (palette != min(frame, P-1))
            if (P > 1)
                foreach (var (fr, pal) in look)
                {
                    string fn = t.FileName(id, fr, pal);
                    string path = Path.Combine(texDir, fn);
                    if (pal != Math.Min(fr, P - 1) && !File.Exists(path))
                    {
                        PngWriter.WriteRgba(path, t.Width, t.Height, t.ToRgba(fr, pal));
                        written++;
                    }
                }
            if (F == 1 && P == 1 && !timeAnimated) continue;   // static textures are not listed
            var seq = Enumerable.Range(0, period).Select(k => new[] { k % F, k % P }).ToArray();
            texList.Add(new
            {
                textureId = id,
                format = t.FormatByte,
                p1 = (int)t.Param1,
                width = t.Width, height = t.Height,
                frames = F, palettes = P,
                kind,
                timeAnimated,                        // used by a placement with draw key -1
                animatedByObjects = texAnimatedObjects.TryGetValue(id, out var ao) ? ao.ToArray() : [],
                usedByObjects = texObjects.TryGetValue(id, out var uo) ? uo.ToArray() : [],
                stepTicks = (int)t.Param1,           // k = (tick / p1) & 255
                period,
                header = Convert.ToHexString(t.Header),
                // (frame, palette) per step k for k in [0, period); step k = (tick / p1) & 255, viewer index = k % period
                sequence = timeAnimated ? seq : null,
                fixedKeyLook = "frame = key % frames, palette = key % palettes",
                pngNaming = "tex_NNN_f{frame}.png when palette == min(frame, palettes-1), else tex_NNN_f{frame}_p{palette}.png"
            });
        }

        var comboList = combos.Select(kv => new
        {
            flags = "0x" + kv.Key,
            look = kv.Value.Look,
            materials = kv.Value.Count,
            colour1Alphas = kv.Value.Alphas.ToArray(),
            field8Values = kv.Value.Field8.ToArray(),
            textures = kv.Value.Tex.Count
        }).ToList();

        var result = new
        {
            note = "see docs/formats/transparency_and_animation.md",
            tick = "global frame counter D_800D2980, +1 per rendered game frame (func_00293038), reset to 0 on level start; animation step k = (tick / p1) & 255",
            drawKeyMeaning = "per-placement object definition byte +0x12 (objects.json textureKey): >=0 fixed look, -1 time animated, -2 random flicker (not used by any level placement)",
            uvScroll = "none: no time dependent texture-coordinate offset exists in the material/draw path (G_TEXTURE scale and tile shifts are constants)",
            tickHzHypothesis = 30,
            animatedMaterials,
            textures = texList,
            materialFlagCombinations = comboList,
            objects
        };
        Directory.CreateDirectory(lvDir);
        string outPath = Path.Combine(lvDir, "texture_anim.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, Json));
        Console.WriteLine($"{textures.Count} textures ({texList.Count} listed as animated/variant), {objects.Count} objects, {comboList.Count} flag combos");
        Console.WriteLine($"wrote {outPath}; {written} new palette-variant PNGs, {rewritten} format-3 (IA16) PNGs rewritten in {texDir}");
        return 0;
    }
}
