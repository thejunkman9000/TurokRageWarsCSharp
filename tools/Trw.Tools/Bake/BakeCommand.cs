using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trw.Tools.Bake;

/// <summary>
/// "Baked" level exports in the final world frame (Y up, the raw X axis mirrored), written to extracted/baked/NN_name/.
/// The raw exports (extracted/levelgeo) stay in the game's own coordinates; this command applies everything learned about orientation:
///   position       (x, y, z) -> (-x, y, z)                                     (the maps are mirrored on X relative to the raw coordinates)
///   yaw-only piece Z-up * Scale * Ry(180) * Rq(conjugate q) * T                 (proper rotation)
///   quaternion     Z-up * Scale * Rx(270)Ry(270)Rz(90) * Rq(-z,-y,-x,w) * Ry(270) * T   (proper rotation)
/// plus texture looks (textureKey % frames), samplers and translucency from the material flags. See docs/formats/baked_levels.md.
/// </summary>
public static class BakeCommand
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Matrix4x4 ZUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed class Local { public List<Vector3> V = new(); public List<Vector2> UV = new(); public Dictionary<(int Tex, int Flags), List<int>> Tris = new(); }
    sealed record MatInfo(float Alpha, Vector3 Colour1, int SortKey);

    public static int Run(string[] args)
    {
        string extracted = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("extracted");
        string raw = Path.Combine(extracted, "levelgeo"), baked = Path.Combine(extracted, "baked");
        string texDir = Path.Combine(extracted, "models", "textures");
        if (!Directory.Exists(raw)) { Console.Error.WriteLine($"{raw} not found: run `trw levelgeo` first"); return 1; }
        Directory.CreateDirectory(baked);

        var frameCounts = FrameCounts(texDir);
        var (stepTicks, materials) = LoadAnim(raw);
        var locals = new Dictionary<string, Local?>();
        JsonNode? placements = File.Exists(Path.Combine(extracted, "creatures", "placements.json")) ? JsonNode.Parse(File.ReadAllText(Path.Combine(extracted, "creatures", "placements.json"))) : null;

        int levels = 0; long tris = 0;
        foreach (string dir in Directory.GetDirectories(raw).Where(d => char.IsDigit(Path.GetFileName(d)[0])).OrderBy(d => d))
        {
            string name = Path.GetFileName(dir);
            string outDir = Path.Combine(baked, name);
            Directory.CreateDirectory(outDir);
            tris += BakeWorld(dir, outDir, raw, texDir, frameCounts, stepTicks, materials, locals);
            BakeCollision(dir, outDir, name);
            BakeNav(dir, outDir);
            BakeLights(dir, outDir);
            BakeSpawns(placements, name, outDir);
            BakeDynamics(Path.Combine(extracted, "dynamics", name + ".json"), outDir);
            levels++;
        }
        File.WriteAllText(Path.Combine(baked, "README.txt"),
            "Baked levels in the final world frame (Y up). Raw X is mirrored (x -> -x) in every coordinate here; see docs/formats/baked_levels.md.\n" +
            "world.obj/mtl: assembled pieces (textures: ../../models/textures). objects.json: per piece 4x4 matrix (row-major, row-vector convention). collision/nav/lights/spawns: mirrored.\n");
        Console.WriteLine($"baked {levels} levels, {tris} triangles -> {baked}");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------ world mesh + objects
    static long BakeWorld(string dir, string outDir, string raw, string texDir, Dictionary<int, int> frameCounts, Dictionary<int, int> stepTicks,
                          Dictionary<(int, int), MatInfo> materials, Dictionary<string, Local?> locals)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "objects.json")));
        var groups = new Dictionary<(int Tex, int Look, int Flags), List<(Vector3, Vector2, Vector3, Vector2, Vector3, Vector2)>>();
        var objectsOut = new JsonArray();
        var verts = new StringBuilder(); var uvs = new StringBuilder(); var faces = new Dictionary<(int, int, int), StringBuilder>();
        int vertBase = 0; int index = -1; long triCount = 0;
        var shade = LoadVertexLight(Path.Combine(dir, "vertexlight.bin"));      // the game's CPU vertex lighting: one colour per vertex per placement
        var minB = new Vector3(float.MaxValue); var maxB = new Vector3(float.MinValue);
        var used = new SortedDictionary<(int, int, int), (int frames, bool animated)>();

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            index++;
            bool isMatrix = e.GetProperty("form").GetString() == "matrix34";
            int objectId = e.GetProperty("ObjectId").GetInt32(), modelIndex = e.GetProperty("modelIndex").GetInt32();
            int key = e.TryGetProperty("textureKey", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetInt32() : 0;
            var pos = Vec(e.GetProperty("position")); var scale = Vec(e.GetProperty("scale"));
            var qa = e.GetProperty("rotationQuat").EnumerateArray().Select(x => x.GetSingle()).ToArray();
            var q = new Quaternion(qa[0], qa[1], qa[2], qa[3]);
            Matrix4x4 m = PieceMatrix(isMatrix, scale, q, pos);
            var bmin = Vec(e.GetProperty("boundsMin")); var bmax = Vec(e.GetProperty("boundsMax"));
            var (fmin, fmax) = (new Vector3(-bmax.X, bmin.Y, bmin.Z), new Vector3(-bmin.X, bmax.Y, bmax.Z));

            string lkey = $"obj_{objectId}_{modelIndex}";
            if (!locals.TryGetValue(lkey, out var local)) locals[lkey] = local = LoadLocal(Path.Combine(raw, "_objects", lkey + ".obj"));
            bool flip = m.GetDeterminant() < 0;
            int firstLook = 0;
            if (local != null)
            {
                var world = local.V.Select(v => Vector3.Transform(v, m)).ToArray();
                foreach (var w in world) { minB = Vector3.Min(minB, w); maxB = Vector3.Max(maxB, w); }
                int baseIdx = vertBase;
                var cols = shade != null && index < shade.Count && shade[index] is { } c0 && c0.Length == world.Length ? c0 : null;
                for (int wi = 0; wi < world.Length; wi++)
                {
                    var w = world[wi];
                    verts.Append("v ").Append(F(w.X)).Append(' ').Append(F(w.Y)).Append(' ').Append(F(w.Z));
                    if (cols != null) verts.Append(' ').Append(F(cols[wi].X)).Append(' ').Append(F(cols[wi].Y)).Append(' ').Append(F(cols[wi].Z));      // OBJ colour extension: v x y z r g b
                    verts.Append('\n');
                }
                foreach (var t in local.UV) uvs.Append("vt ").Append(F(t.X)).Append(' ').Append(F(t.Y)).Append('\n');
                vertBase += world.Length;
                foreach (var ((tex, flags), idx) in local.Tris)
                {
                    int fc = frameCounts.GetValueOrDefault(tex, 1);
                    bool animated = key < 0 && fc > 1;
                    int look = key >= 0 && fc > 0 ? key % fc : 0;
                    firstLook = look;
                    var gk = (tex, look, flags);
                    used[gk] = (fc, animated);
                    if (!faces.TryGetValue(gk, out var sb)) faces[gk] = sb = new StringBuilder();
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                    {
                        int a = idx[i] + 1 + baseIdx, b = idx[i + 1] + 1 + baseIdx, c = idx[i + 2] + 1 + baseIdx;
                        if (flip) (b, c) = (c, b);
                        sb.Append($"f {a}/{a} {b}/{b} {c}/{c}\n");
                        triCount++;
                    }
                }
            }
            var mat = new JsonArray(); foreach (float f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) mat.Add(f);
            objectsOut.Add(new JsonObject
            {
                ["index"] = index, ["sector"] = e.GetProperty("Sector").GetInt32(), ["record"] = e.GetProperty("Record").GetInt32(),
                ["objectId"] = objectId, ["modelIndex"] = modelIndex, ["form"] = e.GetProperty("form").GetString(),
                ["textureKey"] = key, ["look"] = firstLook, ["flags"] = e.GetProperty("flags").GetString(),
                ["position"] = new JsonArray(-pos.X, pos.Y, pos.Z), ["matrix"] = mat,
                ["boundsMin"] = new JsonArray(fmin.X, fmin.Y, fmin.Z), ["boundsMax"] = new JsonArray(fmax.X, fmax.Y, fmax.Z),
            });
        }

        // world.obj / world.mtl
        var mtl = new StringBuilder("# baked level materials. textures: ../../models/textures. sampler: wrap unless noted. translucent: d < 1.\n");
        var obj = new StringBuilder("# baked level (final world frame, Y up, raw X mirrored). v = standard OBJ (vt v = 1 - t). 'v x y z r g b' = the game's vertex lighting (shade colour, multiply with the texture).\nmtllib world.mtl\n");
        obj.Append(verts).Append(uvs);
        var matsOut = new JsonArray();
        foreach (var (gk, sb) in faces.OrderBy(k => k.Key))
        {
            string mname = $"tex_{gk.Item1:D3}_look{gk.Item2}_f{gk.Item3:X}";
            var (fc, animated) = used[gk];
            bool translucent = (gk.Item3 & 0x100) != 0;
            materials.TryGetValue((gk.Item1, gk.Item3), out var mi);
            string file = File.Exists(Path.Combine(texDir, $"tex_{gk.Item1:D3}_f{gk.Item2}.png")) ? $"tex_{gk.Item1:D3}_f{gk.Item2}.png" : $"tex_{gk.Item1:D3}_f0.png";
            string sampler = (gk.Item3 & 0x20) != 0 ? "clamp" : (gk.Item3 & 0x800) != 0 ? "mirror" : "wrap";
            var tint = (gk.Item3 & 0x400) != 0 && mi != null ? mi.Colour1 : Vector3.One;
            mtl.Append($"newmtl {mname}\nKd {F(tint.X)} {F(tint.Y)} {F(tint.Z)}\nmap_Kd ../../models/textures/{file}\n");
            if (translucent && mi != null) mtl.Append($"d {F(mi.Alpha)}\n");
            mtl.Append($"# texture {gk.Item1} look {gk.Item2} flags 0x{gk.Item3:X} sampler {sampler}{(animated ? $" ANIMATED frames={fc} stepTicks={stepTicks.GetValueOrDefault(gk.Item1, 1)} files tex_{gk.Item1:D3}_f0..{fc - 1}.png" : "")}\n");
            obj.Append($"usemtl {mname}\n").Append(sb);
            matsOut.Add(new JsonObject
            {
                ["name"] = mname, ["texture"] = gk.Item1, ["look"] = gk.Item2, ["flags"] = $"0x{gk.Item3:X}", ["sampler"] = sampler, ["translucent"] = translucent,
                ["alpha"] = translucent && mi != null ? mi.Alpha : 1f, ["sortKey"] = mi?.SortKey ?? 0, ["animated"] = animated, ["frames"] = fc,
                ["stepTicks"] = stepTicks.GetValueOrDefault(gk.Item1, 1), ["doubleSided"] = (gk.Item3 & 0x4) != 0, ["environmentMap"] = (gk.Item3 & 0x10) != 0,
            });
        }
        File.WriteAllText(Path.Combine(outDir, "world.obj"), obj.ToString());
        File.WriteAllText(Path.Combine(outDir, "world.mtl"), mtl.ToString());
        File.WriteAllText(Path.Combine(outDir, "objects.json"), objectsOut.ToJsonString(Json));
        File.WriteAllText(Path.Combine(outDir, "materials.json"), matsOut.ToJsonString(Json));
        File.WriteAllText(Path.Combine(outDir, "meta.json"), new JsonObject
        {
            ["level"] = Path.GetFileName(dir), ["frame"] = "world: Y up; the raw game X axis is mirrored (x -> -x) in every coordinate in this folder",
            ["unitNote"] = "raw game units (static wall height about 307)", ["triangles"] = triCount, ["placements"] = index + 1,
            ["worldMin"] = new JsonArray(minB.X, minB.Y, minB.Z), ["worldMax"] = new JsonArray(maxB.X, maxB.Y, maxB.Z),
            ["pieceTransforms"] = "yaw-only: ZUp*Scale*Ry(180)*Rq(conj q)*T; quaternion: ZUp*Scale*Rx(270)Ry(270)Rz(90)*Rq(-z,-y,-x,w)*Ry(270)*T; row-vector convention; positions x mirrored",
            ["winding"] = "counter-clockwise front faces; one-sided",
        }.ToJsonString(Json));
        return triCount;
    }

    static Matrix4x4 PieceMatrix(bool quaternionRecord, Vector3 scale, Quaternion q, Vector3 pos)
    {
        float d = MathF.PI / 2;
        var t = Matrix4x4.CreateTranslation(-pos.X, pos.Y, pos.Z);
        if (!quaternionRecord)
            return ZUp * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(2 * d) * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(q)) * t;
        var steps = Matrix4x4.CreateRotationX(3 * d) * Matrix4x4.CreateRotationY(3 * d) * Matrix4x4.CreateRotationZ(1 * d);
        var swapped = new Quaternion(-q.Z, -q.Y, -q.X, q.W);
        return ZUp * Matrix4x4.CreateScale(scale) * steps * Matrix4x4.CreateFromQuaternion(swapped) * Matrix4x4.CreateRotationY(3 * d) * t;
    }

    // ------------------------------------------------------------------------------------------------------ collision
    static void BakeCollision(string dir, string outDir, string name)
    {
        var c = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "collision.json")))!.AsObject();
        c["note"] = "BAKED: x mirrored (x -> -x), triangle winding and neighbour order permuted accordingly (v1/v2 swapped). " + c["note"];
        foreach (var v in c["vertices"]!.AsArray()) { var a = v!.AsArray(); a[0] = -(float)a[0]!; }
        foreach (var t in c["triangles"]!.AsArray())
        {
            var vv = t!["v"]!.AsArray(); var nb = t["neighbours"]!.AsArray();
            int v1 = (int)vv[1]!, v2 = (int)vv[2]!; vv[1] = v2; vv[2] = v1;
            int n0 = (int)nb[0]!, n2 = (int)nb[2]!; nb[0] = n2; nb[2] = n0;      // edges (v0v1, v1v2, v2v0) -> (v0v2, v2v1, v1v0)
        }
        foreach (var r in c["rects"]!.AsArray()) { float x0 = (float)r!["xMin"]!, x1 = (float)r["xMax"]!; r["xMin"] = -x1; r["xMax"] = -x0; }
        foreach (var b in c["objectBoxes"]!.AsArray())
        {
            var mn = b!["min"]!.AsArray(); var mx = b["max"]!.AsArray(); float a = (float)mn[0]!, z = (float)mx[0]!; mn[0] = -z; mx[0] = -a;
        }
        File.WriteAllText(Path.Combine(outDir, "collision.json"), c.ToJsonString(Json));

        var sb = new StringBuilder($"# baked collision for {name} (final world frame)\ng world_tris\n");
        var verts = c["vertices"]!.AsArray();
        foreach (var v in verts) { var a = v!.AsArray(); sb.Append($"v {F((float)a[0]!)} {F((float)a[1]!)} {F((float)a[2]!)}\n"); }
        foreach (var t in c["triangles"]!.AsArray()) { var vv = t!["v"]!.AsArray(); sb.Append($"f {(int)vv[0]! + 1} {(int)vv[1]! + 1} {(int)vv[2]! + 1}\n"); }
        File.WriteAllText(Path.Combine(outDir, "collision.obj"), sb.ToString());
    }

    // ------------------------------------------------------------------------------------------------------ nav, lights, spawns
    static void BakeNav(string dir, string outDir)
    {
        var n = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "nav.json")))!.AsObject();
        n["note"] = "BAKED: x mirrored. " + n["note"];
        foreach (var node in n["nodes"]!.AsArray()) node!["x"] = -(float)node["x"]!;
        foreach (var a in n["actors"]!.AsArray()) a!["x"] = -(float)a["x"]!;
        File.WriteAllText(Path.Combine(outDir, "nav.json"), n.ToJsonString(Json));
    }

    static void BakeLights(string dir, string outDir)
    {
        var l = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "lights.json")))!.AsArray();
        foreach (var light in l)
        {
            var p = light!["position"]!.AsArray(); p[0] = -(float)p[0]!;
            var ang = light["anglesRad"]!.AsArray(); ang[1] = -(float)ang[1]!; ang[2] = -(float)ang[2]!;      // rotation angles under an X mirror: about Y and Z negate [H]
        }
        File.WriteAllText(Path.Combine(outDir, "lights.json"), l.ToJsonString(Json));
    }

    static void BakeSpawns(JsonNode? placements, string levelName, string outDir)
    {
        if (placements == null) return;
        foreach (var lv in placements["levels"]!.AsArray())
        {
            if ((string?)lv!["file"] != levelName) continue;
            var o = new JsonObject
            {
                ["note"] = "final world frame: x mirrored; facingYaw = -rawYaw + pi radians about +Y (the viewer's tuned convention; creature models face +Z at facingYaw 0 after the Z-up conversion)",
                ["level"] = levelName,
            };
            var actors = new JsonArray();
            foreach (var a in lv["actors"]!.AsArray())
            {
                var c = JsonNode.Parse(a!.ToJsonString())!.AsObject();
                float yaw = c.ContainsKey("yaw") ? (float)c["yaw"]! : 0;
                c["x"] = -(float)c["x"]!; c["facingYaw"] = -yaw + MathF.PI;
                actors.Add(c);
            }
            o["actors"] = actors;
            var spawns = new JsonArray();
            foreach (var s in lv["spawnPoints"]!.AsArray())
            {
                var c = JsonNode.Parse(s!.ToJsonString())!.AsObject();
                float yaw = c.ContainsKey("yaw") ? (float)c["yaw"]! : 0;
                c["x"] = -(float)c["x"]!; c["facingYaw"] = -yaw + MathF.PI;
                spawns.Add(c);
            }
            o["spawnPoints"] = spawns;
            File.WriteAllText(Path.Combine(outDir, "spawns.json"), o.ToJsonString(Json));
            return;
        }
    }

    /// <summary>Lifts, teleporters, beacons and flag spawners (docs/formats/dynamic_objects.md) with x mirrored: positions, motion endpoints and offsets, facing.</summary>
    static void BakeDynamics(string path, string outDir)
    {
        if (!File.Exists(path)) return;
        var d = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        d["coordinates"] = "BAKED: final world frame, x mirrored (x -> -x); facingYaw = -yawRadians + pi about +Y. Times in ticks (1/15 s). " + d["coordinates"];
        void MirrorX(JsonNode? arr) { if (arr is JsonArray a && a.Count >= 3) a[0] = -(float)a[0]!; }
        foreach (var o in d["dynamics"]!.AsArray())
        {
            MirrorX(o!["position"]);
            if (o["yawRadians"] is JsonNode y) o["facingYaw"] = -(float)y + MathF.PI;
            if (o["motion"] is JsonObject m) { MirrorX(m["from"]); MirrorX(m["to"]); MirrorX(m["offset"]); }
            if (o["teleport"] is JsonObject t) foreach (var kv in t.ToList()) if (kv.Value is JsonArray arr && arr.Count == 3 && kv.Key.Contains("osition", StringComparison.OrdinalIgnoreCase)) MirrorX(arr);
        }
        File.WriteAllText(Path.Combine(outDir, "dynamics.json"), d.ToJsonString(Json));
    }

    // ------------------------------------------------------------------------------------------------------ helpers
    /// <summary>vertexlight.bin: "TRWL", u32 1, u32 count, then per placement u32 n and n x (R, G, B, anim). Returns colours 0..1.</summary>
    static List<Vector3[]?>? LoadVertexLight(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || b[0] != 'T' || b[1] != 'R' || b[2] != 'W' || b[3] != 'L') return null;
        int count = BitConverter.ToInt32(b, 8), p = 12;
        var r = new List<Vector3[]?>(count);
        for (int i = 0; i < count; i++)
        {
            int n = BitConverter.ToInt32(b, p); p += 4;
            var cols = new Vector3[n];
            for (int j = 0; j < n; j++, p += 4) cols[j] = new Vector3(b[p], b[p + 1], b[p + 2]) / 255f;
            r.Add(cols);
        }
        return r;
    }

    static Local? LoadLocal(string path)
    {
        if (!File.Exists(path)) return null;
        var l = new Local(); int tex = -1, flags = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length < 2 || line[0] == '#') continue;
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v": l.V.Add(new Vector3(P(t[1]), P(t[2]), P(t[3]))); break;
                case "vt": l.UV.Add(new Vector2(P(t[1]), P(t[2]))); break;                      // already (u, 1 - t) = standard OBJ
                case "usemtl":
                    var parts = t[1].Split('_');
                    tex = int.Parse(parts[1], Inv);
                    flags = parts.Length > 2 ? int.Parse(parts[2][1..], NumberStyles.HexNumber, Inv) : 0;
                    break;
                case "f":
                    if (!l.Tris.TryGetValue((tex, flags), out var list)) l.Tris[(tex, flags)] = list = new();
                    for (int i = 1; i < 4; i++) list.Add(int.Parse(t[i].Split('/')[0]) - 1);
                    break;
            }
        }
        return l;
    }

    static Dictionary<int, int> FrameCounts(string texDir)
    {
        var d = new Dictionary<int, int>();
        if (!Directory.Exists(texDir)) return d;
        foreach (string f in Directory.GetFiles(texDir, "tex_*_f*.png"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"tex_(\d+)_f(\d+)\.png");
            if (m.Success) { int id = int.Parse(m.Groups[1].Value), fr = int.Parse(m.Groups[2].Value) + 1; if (fr > d.GetValueOrDefault(id)) d[id] = fr; }
        }
        return d;
    }

    static (Dictionary<int, int>, Dictionary<(int, int), MatInfo>) LoadAnim(string raw)
    {
        var steps = new Dictionary<int, int>(); var mats = new Dictionary<(int, int), MatInfo>();
        string path = Path.Combine(raw, "texture_anim.json");
        if (!File.Exists(path)) return (steps, mats);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var tx in doc.RootElement.GetProperty("textures").EnumerateArray()) steps[tx.GetProperty("textureId").GetInt32()] = tx.GetProperty("stepTicks").GetInt32();
        foreach (var obj in doc.RootElement.GetProperty("objects").EnumerateArray())
            foreach (var m in obj.GetProperty("materials").EnumerateArray())
            {
                int tex = m.GetProperty("textureId").GetInt32(); int flags = Convert.ToInt32(m.GetProperty("flags").GetString()!, 16);
                if (mats.ContainsKey((tex, flags))) continue;
                var c1 = m.GetProperty("colour1").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                mats[(tex, flags)] = new MatInfo(m.GetProperty("alpha").GetSingle(), new Vector3(c1[0], c1[1], c1[2]) / 255f, m.GetProperty("field8").GetInt32() * 2 | (flags & 0x40));
            }
        return (steps, mats);
    }

    static Vector3 Vec(JsonElement a) { var v = a.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new Vector3(v[0], v[1], v[2]); }
    static float P(string s) => float.Parse(s, Inv);
    static string F(float v) => v.ToString("0.####", Inv);
}
