using System.Text.Json;
using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>
/// `trw defs [rom] [outdir]` - dumps the gameplay definition data as readable JSON (default outdir extracted\defs).
/// See docs/formats/gameplay_defs.md for the verified / hypothesised meaning of every table.
/// </summary>
public static class DefsCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static int Run(string[] args)
    {
        var a = args.Where((x, i) => !(i == 0 && x.Equals("defs", StringComparison.OrdinalIgnoreCase))).ToList();
        string rom = DefaultRom;
        if (a.Count > 0 && File.Exists(a[0])) { rom = a[0]; a.RemoveAt(0); }
        string outDir = a.Count > 0 ? a[0] : Path.Combine("extracted", "defs");
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }

        var romBytes = File.ReadAllBytes(rom);
        var ix = RomIndex.Load(romBytes);
        Directory.CreateDirectory(outDir);

        void Write(string name, object o, bool pretty = true)
        {
            File.WriteAllText(Path.Combine(outDir, name), JsonSerializer.Serialize(o, pretty ? Pretty : Compact));
            Console.WriteLine($"  {name}");
        }

        Console.WriteLine($"writing {Path.GetFullPath(outDir)}");
        Write("archive_sections.json", ArchiveSections.Describe(ix));

        var intel = Intelligence.Load(ix);
        Write("intelligence.json", intel.Select(r => r.ToJson()).ToList());
        Write("characters.json", Intelligence.Characters(intel, romBytes));

        Write("particle_effects.json", ParticleEffects.Load(ix));
        Write("spawn_points.json", SpawnPoints.Load(ix));
        Write("bot_weapon_fire.json", BotWeaponFire.Load(ix, romBytes));
        Write("level_progression.json", LevelProgression.Load(ix));
        Write("misc_tables.json", MiscTables.Load(ix));
        Write("rom_string_tables.json", RomNames.Load(romBytes));
        Write("game_modes.json", GameModes.Load(ix));

        var levels = LevelCatalog.Load(ix);
        var summary = new List<object>();
        Directory.CreateDirectory(Path.Combine(outDir, "levels"));
        foreach (var lv in levels)
        {
            if (lv.Node.Kind != NodeKind.Container || lv.Node.Children.Count < 12) continue;
            var d = LevelActors.Decode(lv, intel);
            File.WriteAllText(Path.Combine(outDir, "levels", $"{lv.Id:D2}_{Safe(lv.Name)}.json"), JsonSerializer.Serialize(d.Full, Compact));
            summary.Add(d.Summary);
        }
        Write("level_actor_summary.json", summary);
        Console.WriteLine("done");
        return 0;
    }

    static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
}

/// <summary>Small helpers for reading big-endian data and producing JSON-safe values.</summary>
internal static class J
{
    /// <summary>Round-trippable-ish float for JSON (NaN/Inf become strings).</summary>
    public static object F(float f)
    {
        if (float.IsNaN(f) || float.IsInfinity(f)) return f.ToString();
        return double.Parse(f.ToString("G7", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    }
    public static string H8(uint v) => "0x" + v.ToString("X8");
    public static string H4(uint v) => "0x" + v.ToString("X4");
    public static string Hex(ReadOnlySpan<byte> d) => Bin.Hex(d);
    public static uint[] Words(ReadOnlySpan<byte> d)
    {
        var r = new uint[d.Length / 4];
        for (int i = 0; i < r.Length; i++) r[i] = Bin.U32(d, i * 4);
        return r;
    }
    /// <summary>The game's Trunc16ToF32: a u16 is the upper half of an IEEE float.</summary>
    public static float Trunc16(ushort v) => BitConverter.Int32BitsToSingle(v << 16);

    /// <summary>Virtual address (KSEG0 data alias) to ROM offset for the main image: ROM = (VA - 0x80000000) + 0xC00.</summary>
    public static int VaToRom(uint va) => (int)(va - 0x80000000u) + 0xC00;
    public static uint RomToVa(int rom) => 0x80000000u + (uint)(rom - 0xC00);
    public static string? StringAt(byte[] rom, uint va)
    {
        if ((va >> 24) != 0x80) return null;
        int o = VaToRom(va);
        if (o < 0 || o >= rom.Length) return null;
        int e = o;
        while (e < rom.Length && rom[e] != 0 && e - o < 80) e++;
        if (e == o || e >= rom.Length || rom[e] != 0) return null;
        for (int i = o; i < e; i++) if (rom[i] < 0x20 || rom[i] > 0x7E) return null;
        return System.Text.Encoding.ASCII.GetString(rom, o, e - o);
    }
}
