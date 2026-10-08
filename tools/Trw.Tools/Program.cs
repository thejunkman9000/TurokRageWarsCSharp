using System.Text;
using Trw.Tools;

const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        trw <command> [rom] [outdir]

          scan     [rom]            list every RNC chunk in the ROM and verify it unpacks (CRC checked)
          extract  [rom] [outdir]   unpack every chunk to <outdir>/rnc/<offset>.bin plus catalog.tsv
          models   <outdir> [rom] [--all]  3D textures and UI textures (PNG), models (OBJ+MTL)
          levels   [rom] [outdir]   resource index, level list, level records (text/JSON), chunks.tsv
          creatures [rom] [outdir]  skinned creature packages with every clip (run `defs` first)
          texanim  [rom] [levelgeoDir] [texturesDir]  texture animation + translucency data (texture_anim.json)
          bake     [extractedDir]   baked levels in the final world frame (extracted/baked): needs levelgeo, models, texanim, creatures
          dynamics [rom] [outdir]   lifts, teleporters, beacons, flag spawners per level (extracted/dynamics)
          anim     [rom] [outdir]   skeletons and animation clips (JSON), posed OBJ snapshots
          defs     [rom] [outdir]   gameplay definition tables (JSON)
          levelgeo [rom] [outdir] [--level N]  textured level meshes, collision, nav and object JSON, top-down PNGs
          spec     [outdir]         weapon, damage, pickup and rule numbers (JSON)
          audio    [rom] [outdir]   sound effects (WAV), music instruments, sequences (.mid), sfx_ids.tsv
        """);
    return args.Length == 0 ? 1 : 0;
}

string cmd = args[0];
string romPath = args.Length > 1 ? args[1] : DefaultRom;
string outDir = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extracted");
if (cmd == "creatures") return Trw.Tools.Anim.CreatureExport.Run(args.Skip(1).ToArray());
if (cmd == "texanim") return Trw.Tools.Models.TextureAnim.Run(args.Skip(1).ToArray());
if (cmd == "bake") return Trw.Tools.Bake.BakeCommand.Run(args.Skip(1).ToArray());
if (cmd == "dynamics") return Trw.Tools.Dynamics.DynamicsCommand.Run(args.Skip(1).ToArray());
if (cmd == "spec") return Trw.Tools.Spec.SpecData.Run(args.Skip(1).ToArray());
if (cmd == "levelgeo") return Trw.Tools.LevelGeo.LevelGeoCommand.Run(args.Skip(1).ToArray());
if (cmd == "defs") return Trw.Tools.Defs.DefsCommand.Run(args.Skip(1).ToArray());
if (cmd == "anim") return Trw.Tools.Anim.AnimCommand.Run(args.Skip(1).ToArray());
if (cmd == "levels") return Trw.Tools.Levels.LevelsCommand.Run(args.Skip(1).ToArray());
if (cmd == "models") return Trw.Tools.Models.ModelsCommand.Run(args.Skip(1).ToArray());
if (cmd == "audio") return Trw.Tools.Audio.AudioCommand.Run(args.Skip(1).ToArray());
byte[] rom = File.ReadAllBytes(romPath);

switch (cmd)
{
    case "scan": return Run(rom, null);
    case "extract": return Run(rom, Path.GetFullPath(outDir));
    default:
        Console.Error.WriteLine($"unknown command '{cmd}'");
        return 1;
}

static int Run(byte[] rom, string? outDir)
{
    var catalog = new StringBuilder("offset\tpacked\tunpacked\tkind\thead\n");
    var kinds = new SortedDictionary<string, (int Count, long Bytes)>();
    int ok = 0, bad = 0;
    if (outDir != null) Directory.CreateDirectory(Path.Combine(outDir, "rnc"));

    for (int off = 0; off + Rnc.HeaderSize <= rom.Length; off++)
    {
        if (!Rnc.IsRnc(rom, off)) continue;
        try
        {
            var h = Rnc.ReadHeader(rom, off);
            byte[] data = Rnc.Unpack(rom, off);
            string kind = Classify(data);
            string head = Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)));
            catalog.Append($"0x{off:X6}\t{h.PackedSize}\t{data.Length}\t{kind}\t{head}\n");
            kinds[kind] = kinds.TryGetValue(kind, out var k) ? (k.Count + 1, k.Bytes + data.Length) : (1, data.Length);
            if (outDir != null) File.WriteAllBytes(Path.Combine(outDir, "rnc", $"{off:X6}.bin"), data);
            ok++;
            off += Rnc.ChunkLength(h) - 1;
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine($"0x{off:X6}: {e.Message}");
            bad++;
        }
    }

    Console.WriteLine($"{ok} chunks unpacked, {bad} failed");
    foreach (var (kind, (count, bytes)) in kinds) Console.WriteLine($"  {kind,-12} {count,5} chunks {bytes,10:N0} bytes");
    if (outDir != null) File.WriteAllText(Path.Combine(outDir, "catalog.tsv"), catalog.ToString());
    return bad == 0 ? 0 : 2;
}

/// <summary>Rough first-pass classification so we can see what kinds of assets exist. Refined as formats are understood.</summary>
static string Classify(byte[] d)
{
    if (d.Length < 4) return "tiny";
    uint be = (uint)((d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]);
    if (be == 0x52494646) return "riff";
    if (d.Length >= 8 && d[0] == 'R' && d[1] == 'N' && d[2] == 'C') return "nested-rnc";
    int printable = 0, n = Math.Min(d.Length, 64);
    for (int i = 0; i < n; i++) if (d[i] is >= 0x20 and < 0x7F) printable++;
    if (printable > n * 0.9) return "text";
    return "unknown";
}
