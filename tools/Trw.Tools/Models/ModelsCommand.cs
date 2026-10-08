using System.Globalization;
using System.Text;

namespace Trw.Tools.Models;

/// <summary>
/// "trw models &lt;outDir&gt; [rom] [--all]": decode 3D textures, UI textures and models from the ROM and dump
/// PNG / OBJ+MTL files plus a summary. Without --all only a sample is written.
/// </summary>
public static class ModelsCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

    /// <summary>ROM address of the master container (24 sections) that holds every asset (verified, see docs).</summary>
    public const int MasterOffset = 0x50F768;
    /// <summary>Master section 3 = 3D texture table (507 RNC chunks).</summary>
    public const int MasterTextureSection = 3;
    public const int RncRegionStart = 0x50FD90, RncRegionEnd = 0x7A09B0;

    public static int Run(string[] args)
    {
        bool all = args.Contains("--all");
        var pos = args.Where(a => !a.StartsWith("--")).ToList();
        if (pos.Count == 0)
        {
            Console.Error.WriteLine("usage: trw models <outDir> [rom] [--all]");
            return 1;
        }
        string outDir = Path.GetFullPath(pos[0]);
        string romPath = pos.Count > 1 ? pos[1] : DefaultRom;
        byte[] rom = File.ReadAllBytes(romPath);
        Directory.CreateDirectory(outDir);

        // --- master container -------------------------------------------------------------------------------------
        if (!SectionContainer.TryParse(rom.AsMemory(MasterOffset, rom.Length - MasterOffset)[..Math.Min(0x291380, rom.Length - MasterOffset)], out var master) || master is null)
        {
            Console.Error.WriteLine("master container at 0x50F768 not found (wrong ROM?)");
            return 1;
        }
        Console.WriteLine($"master container: {master.Count} sections");
        for (int i = 0; i < master.Count; i++)
            Console.WriteLine($"  section {i,2}: ROM 0x{MasterOffset + master.SectionOffset(i):X6}  length 0x{master.SectionLength(i):X}");

        // --- 3D textures ------------------------------------------------------------------------------------------
        var texTable = master.Sub(MasterTextureSection);
        var textures = new List<CiTexture?>();
        if (texTable != null)
        {
            int baseOff = MasterOffset + master.SectionOffset(MasterTextureSection);
            for (int i = 0; i < texTable.Count; i++)
            {
                byte[] chunk = Rnc.Unpack(rom, baseOff + texTable.SectionOffset(i));
                textures.Add(CiTexture.Parse(chunk));
            }
        }
        int texOk = textures.Count(t => t != null);
        Console.WriteLine($"3D textures: {texOk}/{textures.Count} decoded; CI4 {textures.Count(t => t?.BitsPerPixel == 4)}, CI8 {textures.Count(t => t?.BitsPerPixel == 8)}");
        string texDir = Path.Combine(outDir, "textures");
        Directory.CreateDirectory(texDir);
        for (int i = 0; i < textures.Count; i++)
        {
            var t = textures[i];
            if (t == null || (!all && i >= 32)) continue;
            for (int f = 0; f < t.Frames.Length && (all || f < 2); f++)
                PngWriter.WriteRgba(Path.Combine(texDir, $"tex_{i:D3}_f{f}.png"), t.Width, t.Height, t.ToRgba(f));
        }

        // --- scan all RNC chunks ----------------------------------------------------------------------------------
        var models = new List<(int Off, Model M)>();
        var ui = new List<(int Off, UiTexture T)>();
        int chunks = 0;
        for (int off = RncRegionStart; off + Rnc.HeaderSize <= rom.Length && off <= RncRegionEnd; off++)
        {
            if (!Rnc.IsRnc(rom, off)) continue;
            Rnc.Header h;
            byte[] data;
            try { h = Rnc.ReadHeader(rom, off); data = Rnc.Unpack(rom, off); }
            catch (InvalidDataException) { continue; }
            chunks++;
            var m = Model.Parse(data);
            if (m != null) models.Add((off, m));
            else
            {
                var u = UiTexture.Parse(data);
                if (u != null) ui.Add((off, u));
            }
            off += Rnc.ChunkLength(h) - 1;
        }
        Console.WriteLine($"scanned {chunks} RNC chunks: {models.Count} models ({models.Count(m => !m.M.Skinned)} static, {models.Count(m => m.M.Skinned)} skinned), {ui.Count} UI textures");

        // --- UI textures ------------------------------------------------------------------------------------------
        string uiDir = Path.Combine(outDir, "ui");
        Directory.CreateDirectory(uiDir);
        foreach (var (off, t) in ui.Take(all ? int.MaxValue : 12))
            PngWriter.WriteRgba(Path.Combine(uiDir, $"ui_{t.Id:D4}_{off:X6}.png"), t.Width, t.Height, t.Rgba);

        // --- models -----------------------------------------------------------------------------------------------
        string modelDir = Path.Combine(outDir, "models");
        Directory.CreateDirectory(modelDir);
        var picked = all ? models : models.Where(m => !m.M.Skinned).Take(8).Concat(models.Where(m => m.M.Skinned).Take(8)).ToList();
        foreach (var (off, m) in picked)
        {
            string name = $"{(m.Skinned ? "skinned" : "static")}_{off:X6}";
            WriteObj(Path.Combine(modelDir, name + ".obj"), name, m, textures);
        }
        Console.WriteLine($"wrote {picked.Count} models to {modelDir}");
        return 0;
    }

    /// <summary>Write a Wavefront OBJ with per-vertex colours (v x y z r g b), UVs and a .mtl referencing the texture PNGs.</summary>
    public static void WriteObj(string path, string name, Model m, IReadOnlyList<CiTexture?> textures)
    {
        var inv = CultureInfo.InvariantCulture;
        var tris = m.BuildTriangles();
        var sb = new StringBuilder();
        sb.AppendLine($"# Turok: Rage Wars model {name}; units are raw model units, skinned models are in bone-local space");
        sb.AppendLine($"mtllib {name}.mtl");
        // vertex -> part (to derive UVs from that part's texture size/scale)
        var partOf = m.PartOfVertex;
        for (int i = 0; i < m.Vertices.Length; i++)
        {
            var v = m.Vertices[i];
            sb.AppendFormat(inv, "v {0} {1} {2} {3:0.###} {4:0.###} {5:0.###}\n", v.X, v.Y, v.Z, v.R / 255.0, v.G / 255.0, v.B / 255.0);
        }
        for (int i = 0; i < m.Vertices.Length; i++)
        {
            var v = m.Vertices[i];
            var mat = m.Parts[partOf[i]].Material;
            var tex = mat.TextureId >= 0 && mat.TextureId < textures.Count ? textures[mat.TextureId] : null;
            double w = tex?.Width ?? 32, h = tex?.Height ?? 32;
            // s,t are s10.5 texel coordinates; the material scale is the G_TEXTURE scale (x/65536). Verified statistically.
            double u = v.S / 32.0 * mat.ScaleS / 65536.0 / w;
            double t = v.T / 32.0 * mat.ScaleT / 65536.0 / h;
            sb.AppendFormat(inv, "vt {0:0.####} {1:0.####}\n", u, 1.0 - t);
        }
        int lastPart = -1;
        foreach (var tr in tris.OrderBy(t => t.Part))
        {
            if (tr.Part != lastPart)
            {
                sb.AppendLine($"usemtl part{tr.Part}");
                lastPart = tr.Part;
            }
            sb.AppendFormat("f {0}/{0} {1}/{1} {2}/{2}\n", tr.V0 + 1, tr.V1 + 1, tr.V2 + 1);
        }
        File.WriteAllText(path, sb.ToString());

        var mtl = new StringBuilder();
        string texRel = Path.Combine("..", "textures");
        for (int pi = 0; pi < m.Parts.Length; pi++)
        {
            var mat = m.Parts[pi].Material;
            mtl.AppendLine($"newmtl part{pi}");
            mtl.AppendFormat(inv, "Kd {0:0.##} {1:0.##} {2:0.##}\n", mat.Color1[0] / 255.0, mat.Color1[1] / 255.0, mat.Color1[2] / 255.0);
            if (mat.TextureId >= 0) mtl.AppendLine($"map_Kd {texRel.Replace('\\', '/')}/tex_{mat.TextureId:D3}_f0.png");
            mtl.AppendLine($"# textureId={mat.TextureId} flags=0x{mat.Flags:X}");
        }
        File.WriteAllText(Path.ChangeExtension(path, ".mtl"), mtl.ToString());
    }
}
