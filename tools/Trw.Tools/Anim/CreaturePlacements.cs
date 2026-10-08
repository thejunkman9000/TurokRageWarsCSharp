using System.Globalization;
using System.Text.Json;

namespace Trw.Tools.Anim;

/// <summary>
/// placements.json: per level the player start (level part 3 pawn actor, class 11 intelligence record 79), every other level-part-3 actor
/// (pickups, lights, movers, zones) with the object they use, the multiplayer spawn points of the level (archive section 8 "warp dests",
/// where every other character is placed) and the 44 pawn (character) records with their appearance bytes.
/// Reads the JSON written by "trw defs" (extracted/defs: levels/*.json, spawn_points.json, intelligence.json, characters.json);
/// coordinates are written raw (no X mirror, no Z-up conversion).
/// </summary>
public static class CreaturePlacements
{
    public static void Write(string outDir, HashSet<int> animatedObjects)
    {
        string defs = Path.GetFullPath(Path.Combine(outDir, "..", "defs"));
        string levelsDir = Path.Combine(defs, "levels");
        if (!Directory.Exists(levelsDir) || !File.Exists(Path.Combine(defs, "spawn_points.json")) || !File.Exists(Path.Combine(defs, "intelligence.json")))
        {
            Console.WriteLine($"placements.json skipped: run 'trw defs' first ({defs} missing)");
            return;
        }
        using var spawnDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(defs, "spawn_points.json")));
        using var intelDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(defs, "intelligence.json")));
        JsonDocument? charDoc = File.Exists(Path.Combine(defs, "characters.json")) ? JsonDocument.Parse(File.ReadAllText(Path.Combine(defs, "characters.json"))) : null;
        var names = new Dictionary<int, string>();
        if (charDoc != null) foreach (var c in charDoc.RootElement.EnumerateArray()) names[c.GetProperty("intelligenceIndex").GetInt32()] = c.GetProperty("nameGuess").GetString() ?? "";

        var groupsByLevel = new Dictionary<int, JsonElement>();
        foreach (var g in spawnDoc.RootElement.GetProperty("groups").EnumerateArray())
            if (g.TryGetProperty("levelId", out var li) && li.ValueKind == JsonValueKind.Number) groupsByLevel[li.GetInt32()] = g;

        using var fs = File.Create(Path.Combine(outDir, "placements.json"));
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("note", "Raw level coordinates (no X mirror, no Z-up conversion; the viewer mirrors X like for static pieces). yaw is radians as stored (s16 * 2pi/65536). Confidence per field is in docs/formats/creatures_export.md.");

        w.WriteStartArray("characters");
        foreach (var r in intelDoc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("class").GetInt32() != 11) continue;
            int idx = r.GetProperty("index").GetInt32();
            var f = r.GetProperty("fields");
            var a = f.GetProperty("appearanceA"); var b = f.GetProperty("appearanceB");
            w.WriteStartObject();
            w.WriteNumber("intelligence", idx);
            w.WriteString("nameGuess", names.GetValueOrDefault(idx, ""));
            w.WriteNumber("object", 174);
            // Model chunk of object 174 = appearanceA[2] (def +0xE): func_0021A2D4 sets actor+1 = def+0xE and func_0024BFC4 picks chunk (value % chunkCount).
            // The def is looked up by character id = s16(+0xC) = appearanceA[0..1] (func_0028CF7C); ids 17+ map to other objects (D_800CE430).
            // Old versions guessed appearanceA[1]; bodyChunkGuess now carries the code-derived chunk (values 16..36 are weapon chunks: those records are never selected by a menu character).
            int charId = (a[0].GetInt32() << 8) | a[1].GetInt32();
            int body = a[2].GetInt32();
            w.WriteNumber("characterId", charId);
            w.WriteNumber("bodyChunk", charId <= 16 ? body : -1);
            w.WriteNumber("bodyChunkGuess", charId <= 16 && body <= 15 ? body : -1);
            w.WriteString("bodyChunkConfidence", charId <= 16 && body <= 15 ? "likely (code: def+0xE = actor+1 = chunk index)" : "none");
            w.WriteNumber("skinIndexGuess", a[2].GetInt32());
            w.WritePropertyName("appearanceA"); a.WriteTo(w);
            w.WritePropertyName("appearanceB"); b.WriteTo(w);
            w.WriteNumber("field14", f.GetProperty("field14").GetInt32());
            w.WriteNumber("health", f.GetProperty("health").GetInt32());
            w.WriteNumber("speedScale", f.GetProperty("speedScale").GetDouble());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("levels");
        foreach (var file in Directory.GetFiles(levelsDir, "*.json").Order())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            int levelId = root.GetProperty("levelId").GetInt32();
            w.WriteStartObject();
            w.WriteNumber("levelId", levelId);
            w.WriteString("file", Path.GetFileNameWithoutExtension(file));
            w.WriteString("name", root.GetProperty("name").GetString());
            w.WriteStartArray("actors");
            foreach (var a in root.GetProperty("actors").EnumerateArray())
            {
                int obj = a.TryGetProperty("objectIndex", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : -1;
                string cls = a.GetProperty("className").GetString() ?? "";
                int intel = a.GetProperty("intelligence").GetInt32();
                bool isPlayer = cls == "pawn";
                w.WriteStartObject();
                w.WriteNumber("index", a.GetProperty("index").GetInt32());
                w.WriteString("kind", isPlayer ? "playerStart" : cls);
                w.WriteNumber("x", a.GetProperty("x").GetDouble()); w.WriteNumber("y", a.GetProperty("y").GetDouble()); w.WriteNumber("z", a.GetProperty("z").GetDouble());
                w.WriteNumber("yaw", a.GetProperty("yaw").GetDouble());
                w.WriteNumber("object", obj);
                w.WriteBoolean("objectAnimated", animatedObjects.Contains(obj));
                w.WriteNumber("actorType", a.GetProperty("actorType").GetInt32());
                w.WriteNumber("intelligence", intel);
                w.WritePropertyName("scale"); a.GetProperty("scale").WriteTo(w);
                w.WriteNumber("flags", a.GetProperty("flags").GetInt32());
                if (isPlayer)
                {
                    w.WriteString("character", names.GetValueOrDefault(intel, ""));
                    w.WriteString("confidence", "object 174 and record 79 verified (func_0044D408 finds the class-11 record, object id from the spawn record); character name is a hypothesis");
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("spawnPoints");
            if (groupsByLevel.TryGetValue(levelId, out var g))
                foreach (var p in g.GetProperty("points").EnumerateArray())
                {
                    w.WriteStartObject();
                    w.WriteNumber("x", p.GetProperty("x").GetDouble()); w.WriteNumber("y", p.GetProperty("y").GetDouble()); w.WriteNumber("z", p.GetProperty("z").GetDouble());
                    w.WriteNumber("yaw", p.GetProperty("yaw").GetDouble());
                    w.WriteNumber("team", p.GetProperty("team").GetInt32());
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        Console.WriteLine("placements.json written");
    }
}
