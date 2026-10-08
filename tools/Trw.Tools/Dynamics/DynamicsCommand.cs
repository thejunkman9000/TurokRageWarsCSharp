using System.Globalization;
using System.Text.Json;
using Trw.Tools.Defs;
using Trw.Tools.Levels;
using Trw.Tools.Models;

namespace Trw.Tools.Dynamics;

/// <summary>
/// `trw dynamics [rom] [outdir]` (default outdir extracted\dynamics) - exports the moving / interactive map objects of every level:
/// lifts (intelligence class 2), teleporter zones (class 13), two-state indicators (class 10), CTF flag spawners (class 3 rec 122),
/// markers (class 9) plus the event rules of level part 7 and the floor "trigger regions" they use.
/// See docs/formats/dynamic_objects.md for the verified / hypothesised meaning of every field.
/// Program.cs line: if (cmd == "dynamics") return Trw.Tools.Dynamics.DynamicsCommand.Run(args.Skip(1).ToArray());
/// </summary>
public static class DynamicsCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        var a = args.Where((x, i) => !(i == 0 && x.Equals("dynamics", StringComparison.OrdinalIgnoreCase))).ToList();
        string rom = DefaultRom;
        if (a.Count > 0 && File.Exists(a[0])) { rom = a[0]; a.RemoveAt(0); }
        string outDir = a.Count > 0 ? a[0] : Path.Combine("extracted", "dynamics");
        if (!File.Exists(rom)) { Console.Error.WriteLine($"ROM not found: {rom}"); return 1; }
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var ix = RomIndex.Load(File.ReadAllBytes(rom));
        var levels = LevelCatalog.Load(ix).Where(l => l.Node.Kind == NodeKind.Container && l.Node.Children.Count >= 13).ToList();
        var intel = Intelligence.Load(ix);
        var warps = WarpDests.Load(ix);
        var masks = LevelMasks.Load(ix);
        var models = new DynModels(ix, Path.Combine(outDir, "_objects"));
        string geoDir = Path.GetFullPath(Path.Combine(outDir, "..", "levelgeo"));

        var summary = new List<object>();
        var classHist = new SortedDictionary<string, int>();
        int sectorNonClass0 = 0, sectorPlacements = 0, animatedStaticPlacements = 0, nonEmptySec2 = 0, nonEmptySec4 = 0;
        var animObjects = new HashSet<int>();
        var missingObjects = new List<string>();
        int actorsChecked = 0;

        foreach (var lv in levels)
        {
            var actors = ActorRecords.Read(lv, intel);
            var rules = EventRules.Read(lv, masks.For(lv.Id));
            var regions = TriggerRegions.Read(lv);
            var floors = FloorIndex.Load(geoDir, lv.Id);

            // ---- sanity statistics over the static sector data (negative results) ----
            foreach (var sc in lv.Sectors())
            {
                if (sc.Section4.Length > 8) nonEmptySec4++;
                if (sc.Records20.Count > 0) nonEmptySec2++;
                for (int i = 0; i < sc.StaticPlacements.Count; i++)
                {
                    sectorPlacements++;
                    int def = Bin.U16(sc.StaticPlacements.Data, i * 0x1C + 0xE);
                    if (def >= intel.Count || intel[def].Class != 0) sectorNonClass0++;
                    int oid = Bin.U16(sc.StaticPlacements.Data, i * 0x1C + 8);
                    if (HasAnimation(ix, oid)) { animatedStaticPlacements++; animObjects.Add(oid); }
                }
                for (int i = 0; i < sc.Placements34.Count; i++)
                {
                    sectorPlacements++;
                    int def = Bin.U16(sc.Placements34.Data, i * 0x34 + 0x2C);
                    if (def >= intel.Count || intel[def].Class != 0) sectorNonClass0++;
                    int oid = Bin.U16(sc.Placements34.Data, i * 0x34 + 0x28);
                    if (HasAnimation(ix, oid)) { animatedStaticPlacements++; animObjects.Add(oid); }
                }
            }

            var dyn = new List<object>();
            var counts = new SortedDictionary<string, int>();
            var sim = new StateSim(actors, masks.For(lv.Id), rules);
            foreach (var act in actors)
            {
                actorsChecked++;
                var on = ix.Find(0, act.ObjectId);
                if (on == null || on.Children.Count < 3 || on.Children[1].Children.Count == 0) missingObjects.Add($"level {lv.Id} actor {act.Index} object {act.ObjectId}");
                classHist[$"class{act.Class}:{act.ClassName}"] = classHist.GetValueOrDefault($"class{act.Class}:{act.ClassName}") + 1;
                object? entry = act.Class switch
                {
                    2 => DynamicBuilders.Lift(act, lv, floors, models, sim),
                    13 => DynamicBuilders.Teleporter(act, warps, models, lv.Id),
                    10 => DynamicBuilders.Indicator(act, sim, models, regions, rules),
                    9 => DynamicBuilders.Marker(act, models),
                    3 when act.IntelIndex == 122 || act.IntelIndex == 123 => DynamicBuilders.FlagSpawner(act, models, sim),
                    _ => null,
                };
                if (entry == null) continue;
                dyn.Add(entry);
                string k = (string)((IDictionary<string, object?>)entry)["kind"]!;
                counts[k] = counts.GetValueOrDefault(k) + 1;
            }
            int pickups = actors.Count(x => x.Class == 3 && x.IntelIndex != 122 && x.IntelIndex != 123);
            counts["pickupSpawners(not listed, see nav.json actors)"] = pickups;

            var doc = new Dictionary<string, object?>
            {
                ["level"] = lv.Id,
                ["name"] = lv.Name,
                ["coordinates"] = "raw game coordinates (+Y up, same as extracted/levelgeo objects.json/nav.json); the viewer mirrors X for positions. yaw = radians about +Y as stored (viewer: mirror/negate like other pieces). All times in ticks (1 tick = 1/15 s), speeds/distances in game units.",
                ["counts"] = counts,
                ["dynamics"] = dyn,
                ["eventRules"] = rules.ToJson(actors),
                ["triggerRegions"] = regions.ToJson(rules.UsedRegionIds()),
                ["initialActorBits"] = new { activeMaskHex = Bin.Hex(masks.For(lv.Id).ActorBits), actorCount = masks.For(lv.Id).ActorCount, presetRecordMaskHex = Bin.Hex(masks.For(lv.Id).PresetBits) },
            };
            string file = $"{lv.Id:D2}_{Safe(lv.Name)}.json";
            File.WriteAllText(Path.Combine(outDir, file), JsonSerializer.Serialize(doc, Pretty));
            summary.Add(new { level = lv.Id, name = lv.Name, file, counts });
            Console.WriteLine($"{lv.Id,2} {lv.Name,-18} " + string.Join(", ", counts.Select(c => $"{c.Key.Split('(')[0]}={c.Value}")));
        }

        models.WriteAll();
        var global = new Dictionary<string, object?>
        {
            ["levels"] = summary,
            ["partThreeClassHistogram"] = classHist,
            ["negativeResults"] = new
            {
                sectorPlacementsTotal = sectorPlacements,
                sectorPlacementsWithNonPropDefinition = sectorNonClass0,
                staticPlacementsWhoseObjectHasAnimationClips = animatedStaticPlacements,
                staticObjectsWithAnimation = animObjects.OrderBy(x => x).ToArray(),
                sectorArray2NonEmpty = nonEmptySec2,
                sectorArray4NonEmpty = nonEmptySec4,
                partThreeActorsChecked = actorsChecked,
                partThreeActorsWithMissingObject = missingObjects,
                note = "Sector arrays 2 (animated objects) and 4 are empty in all shipped sectors; part 12 is all zero in all levels; every sector placement is a class-0 prop. Doors, drawbridges, crushers, jump pads, rotating or swinging objects do not exist as data.",
            },
            ["models"] = models.Describe(),
        };
        File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(global, Pretty));
        Console.WriteLine("wrote " + outDir);
        return 0;
    }

    static bool HasAnimation(RomIndex ix, int oid)
    {
        var n = ix.Find(0, oid, 2);
        return n != null && n.Children.Count > 0;
    }

    internal static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    internal static object F(double v) => Math.Round(v, 4);
    internal static object[] V3(double x, double y, double z) => [F(x), F(y), F(z)];
}
