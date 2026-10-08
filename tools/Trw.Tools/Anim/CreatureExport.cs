using System.Globalization;
using System.Text;
using System.Text.Json;
using Trw.Tools.Models;

namespace Trw.Tools.Anim;

/// <summary>
/// "trw creatures [rom] [outdir]": self-contained per-object export of every animated (skinned) object for the viewer.
/// Writes obj_&lt;id&gt;.json (skeleton, bone-local mesh, per-frame bone world matrices for every clip, roles) plus
/// obj_&lt;id&gt;_m&lt;k&gt;.json for alternative model chunks that share the skeleton (mesh only), and placements.json.
/// Format and conventions: docs/formats/creatures_export.md.
/// </summary>
public static class CreatureExport
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed class Obj
    {
        public int Index;
        public List<AnimClip> Clips = [];
        public List<(int ModelIndex, int Rom, Model Model)> Models = [];   // every model chunk of the object (all skinned ones)
        public int ModelChunkCount;
        public int[] AnimIds = [];   // object sec0 [1]: sorted animation request ids, index = clip index
    }

    public static int Run(string[] args)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToList();
        string romPath = pos.Count > 0 ? pos[0] : DefaultRom;
        string outDir = Path.GetFullPath(pos.Count > 1 ? pos[1] : Path.Combine("extracted", "creatures"));
        byte[] rom = File.ReadAllBytes(romPath);
        Directory.CreateDirectory(outDir);

        var master = ModelsCommand.MasterOffset;
        if (!SectionContainer.TryParse(rom.AsMemory(master, Math.Min(0x291380, rom.Length - master)), out var m) || m is null) return 1;
        var texTable = m.Sub(ModelsCommand.MasterTextureSection);
        var textures = new List<CiTexture?>();
        if (texTable != null)
        {
            int baseOff = master + m.SectionOffset(ModelsCommand.MasterTextureSection);
            for (int i = 0; i < texTable.Count; i++) textures.Add(CiTexture.Parse(Rnc.Unpack(rom, baseOff + texTable.SectionOffset(i))));
        }

        var objects = Load(rom, m);
        Console.WriteLine($"{objects.Count} animated objects");
        int files = 0;
        var report = new StringBuilder("object\tmodel\tbones\tverts\ttris\tmaterials\tclips\tvariants\tbboxFrame0\n");
        foreach (var o in objects)
        {
            var sk0 = Skeleton.FromModel(o.Models[0].Model);
            WriteObject(Path.Combine(outDir, $"obj_{o.Index}.json"), o, o.Models[0], sk0, textures, withClips: true);
            files++;
            int variants = 0;
            foreach (var mv in o.Models.Skip(1))
            {
                var sk = Skeleton.FromModel(mv.Model);
                if (!sk.Parent.SequenceEqual(sk0.Parent)) continue;
                WriteObject(Path.Combine(outDir, $"obj_{o.Index}_m{mv.ModelIndex}.json"), o, mv, sk, textures, withClips: false);
                files++; variants++;
            }
            var mm = o.Models[0].Model;
            report.Append(string.Join('\t', o.Index, o.Models[0].ModelIndex, sk0.Parent.Length, mm.Vertices.Length, mm.BuildTriangles().Count, mm.Parts.Length, o.Clips.Count, variants, BBox(o, sk0))).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "summary.tsv"), report.ToString());
        CreaturePlacements.Write(outDir, objects.Select(o => o.Index).ToHashSet());
        var o174 = objects.FirstOrDefault(o => o.Index == 174);
        if (o174 != null)
            SkinExport.Write(outDir, rom, textures, o174.Models.Select(mv => (mv.ModelIndex, mv.Model)).ToList(), Path.GetFullPath(Path.Combine(outDir, "..", "defs")));
        Console.WriteLine($"{files} files written to {outDir}");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------------------
    static List<Obj> Load(byte[] rom, SectionContainer m)
    {
        var res = new List<Obj>();
        int master = ModelsCommand.MasterOffset;
        var table = m.Sub(0);
        if (table == null) return res;
        int tableRom = master + m.SectionOffset(0);
        for (int i = 0; i < table.Count; i++)
        {
            var entry = table.Sub(i);
            if (entry == null || entry.Count != 4) continue;
            var anims = entry.Sub(2); var models = entry.Sub(1);
            if (anims == null || models == null) continue;
            int entryRom = tableRom + table.SectionOffset(i);
            var o = new Obj { Index = i, ModelChunkCount = models.Count };
            for (int k = 0; k < anims.Count; k++)
            {
                int off = entryRom + entry.SectionOffset(2) + anims.SectionOffset(k);
                try { var c = AnimClip.Parse(Rnc.Unpack(rom, off), off); if (c != null) o.Clips.Add(c); }
                catch (InvalidDataException) { }
            }
            if (o.Clips.Count == 0) continue;
            o.AnimIds = ReadAnimIds(entry);
            for (int k = 0; k < models.Count; k++)
            {
                int off = entryRom + entry.SectionOffset(1) + models.SectionOffset(k);
                Model? model;
                try { model = Model.Parse(Rnc.Unpack(rom, off)); } catch (InvalidDataException) { continue; }
                if (model == null || !model.Skinned || model.HitVolumes.Length != o.Clips[0].BoneCount) continue;
                o.Models.Add((k, off, model));
            }
            if (o.Models.Count > 0) res.Add(o);
        }
        return res;
    }

    /// <summary>Object table entry section 0 = container(2): [0] 32-byte block, [1] counted array of u32 animation request ids (sorted; func_002469F8 binary-searches it and returns the INDEX, which is the clip index in sec2).</summary>
    static int[] ReadAnimIds(SectionContainer entry)
    {
        var s0 = entry.Sub(0);
        if (s0 == null || s0.Count < 2) return [];
        if (!Counted.TryParse(s0[1], out var a) || a.ElementSize != 4) return [];
        var r = new int[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(a.Element(i));
        return r;
    }

    // ------------------------------------------------------------------------------------------------------------
    static string BBox(Obj o, Skeleton sk)
    {
        var (mn, mx) = Bounds(o.Models[0].Model, Pose.World(o.Clips[0], sk, 0, true, out _, out _));
        return $"[{mn[0]:0},{mn[1]:0},{mn[2]:0}]-[{mx[0]:0},{mx[1]:0},{mx[2]:0}]";
    }

    public static (float[] Min, float[] Max) Bounds(Model mdl, Mat4[] world)
    {
        float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < mdl.Vertices.Length; i++)
        {
            var v = mdl.Vertices[i];
            var (x, y, z) = world[Math.Min(mdl.BoneOfVertex[i], world.Length - 1)].Transform(v.X, v.Y, v.Z);
            mn[0] = Math.Min(mn[0], x); mn[1] = Math.Min(mn[1], y); mn[2] = Math.Min(mn[2], z);
            mx[0] = Math.Max(mx[0], x); mx[1] = Math.Max(mx[1], y); mx[2] = Math.Max(mx[2], z);
        }
        return (mn, mx);
    }

    static void WriteObject(string path, Obj o, (int ModelIndex, int Rom, Model Model) mv, Skeleton sk, List<CiTexture?> textures, bool withClips)
    {
        var model = mv.Model;
        int nb = sk.Parent.Length;
        var clip0 = o.Clips[0];

        // ---- mesh: vertices keep the Model.Vertices order (ROM vertices, then ST-override copies); extra copies are appended
        // only where a triangle's material has different UV scaling / texture size than the part that loaded the vertex.
        var pos = new List<float>(); var uvs = new List<float>(); var vbone = new List<int>();
        var tris = model.BuildTriangles();
        (float u, float t) UvOf(int vi, int part)
        {
            var v = model.Vertices[vi];
            var pm = model.Parts[part].Material;
            var tex = pm.TextureId >= 0 && pm.TextureId < textures.Count ? textures[pm.TextureId] : null;
            double w = tex?.Width ?? 32, h = tex?.Height ?? 32;
            return ((float)(v.S / 32.0 * pm.ScaleS / 65536.0 / w), (float)(v.T / 32.0 * pm.ScaleT / 65536.0 / h));
        }
        for (int i = 0; i < model.Vertices.Length; i++)
        {
            var v = model.Vertices[i];
            pos.Add(v.X); pos.Add(v.Y); pos.Add(v.Z);
            var (u, t) = UvOf(i, model.PartOfVertex[i]);
            uvs.Add(u); uvs.Add(t);
            vbone.Add(Math.Min(model.BoneOfVertex[i], nb - 1));
        }
        var extra = new Dictionary<(int, float, float), int>();
        int[] Remap(MeshTriangle tr)
        {
            var idx = new[] { tr.V0, tr.V1, tr.V2 };
            for (int k = 0; k < 3; k++)
            {
                var (u, t) = UvOf(idx[k], tr.Part);
                int vi = idx[k];
                if (u == uvs[vi * 2] && t == uvs[vi * 2 + 1]) continue;
                if (!extra.TryGetValue((vi, u, t), out int ni))
                {
                    ni = pos.Count / 3;
                    pos.Add(pos[vi * 3]); pos.Add(pos[vi * 3 + 1]); pos.Add(pos[vi * 3 + 2]);
                    uvs.Add(u); uvs.Add(t); vbone.Add(vbone[vi]);
                    extra[(vi, u, t)] = ni;
                }
                idx[k] = ni;
            }
            return idx;
        }

        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        w.WriteNumber("object", o.Index);
        w.WriteNumber("model", mv.ModelIndex);
        w.WriteNumber("modelChunkCount", o.ModelChunkCount);
        w.WriteString("modelRom", $"0x{mv.Rom:X6}");
        w.WriteNumber("boneCount", nb);
        if (!withClips) w.WriteString("clipsFrom", $"obj_{o.Index}.json");
        w.WriteStartArray("bones");
        for (int b = 0; b < nb; b++)
        {
            w.WriteStartObject();
            w.WriteNumber("parent", sk.Parent[b]);
            w.WriteNumber("id", sk.BoneId[b]);
            w.WritePropertyName("restPos"); Arr(w, clip0.RestPos[b]);
            w.WritePropertyName("restRot"); Arr(w, clip0.RestRot[b]);
            // hit volume of this chunk (model section 5): bone-local axis-aligned min/max of the 8 corner points + radius (additive fields)
            var hv = model.HitVolumes[b];
            float[] hmn = [float.MaxValue, float.MaxValue, float.MaxValue], hmx = [float.MinValue, float.MinValue, float.MinValue];
            for (int c = 0; c < 8; c++) for (int a = 0; a < 3; a++) { hmn[a] = Math.Min(hmn[a], hv.Corners[c * 3 + a]); hmx[a] = Math.Max(hmx[a], hv.Corners[c * 3 + a]); }
            w.WritePropertyName("hitMin"); Arr(w, hmn);
            w.WritePropertyName("hitMax"); Arr(w, hmx);
            w.WriteNumber("hitRadius", hv.Radius);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // materials first (so extra vertices are known before positions are written)
        var mats = new List<(int tex, uint flags, byte[] c1, byte[] c2, List<int> tri, int part, int fieldA, int field8)>();
        for (int p = 0; p < model.Parts.Length; p++)
        {
            var list = new List<int>();
            foreach (var tr in tris) if (tr.Part == p) list.AddRange(Remap(tr));
            if (list.Count == 0) continue;
            var pm = model.Parts[p].Material;
            mats.Add((pm.TextureId, pm.Flags, pm.Color1, pm.Color2, list, p, pm.FieldA, pm.Field8));
        }
        w.WritePropertyName("positions"); w.WriteStartArray(); foreach (var f in pos) w.WriteNumberValue(f); w.WriteEndArray();
        w.WritePropertyName("uvs"); w.WriteStartArray(); foreach (var f in uvs) w.WriteNumberValue(f); w.WriteEndArray();
        w.WritePropertyName("vertexBone"); w.WriteStartArray(); foreach (var b in vbone) w.WriteNumberValue(b); w.WriteEndArray();
        w.WriteNumber("romVertexCount", model.OriginalVertexCount);
        w.WriteStartArray("materials");
        foreach (var mt in mats)
        {
            w.WriteStartObject();
            w.WriteNumber("texture", mt.tex);
            w.WriteNumber("flags", mt.flags);
            w.WriteString("color1", Convert.ToHexString(mt.c1));
            w.WriteString("color2", Convert.ToHexString(mt.c2));
            w.WriteNumber("part", mt.part);                      // part index inside the model chunk (draw order)
            w.WriteNumber("fieldA", mt.fieldA);                  // material byte +0xA = runtime +6: part group, (fieldA & 0x38) selects visibility (creatures_export.md section 8)
            w.WriteNumber("partGroup", mt.fieldA & 0x38);
            w.WriteNumber("field8", mt.field8);                  // material s16 +8 (draw sort id)
            w.WritePropertyName("triangles"); w.WriteStartArray(); foreach (var i in mt.tri) w.WriteNumberValue(i); w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // rest pose world matrices
        w.WriteString("restWorld", Convert.ToBase64String(ToBytes(WorldRest(clip0, sk))));

        w.WriteStartArray("clips");
        if (withClips)
            for (int ci = 0; ci < o.Clips.Count; ci++)
            {
                var c = o.Clips[ci];
                w.WriteStartObject();
                w.WriteNumber("index", ci);
                w.WriteNumber("frames", c.Frames);
                w.WriteNumber("rate", c.PlaybackRate);
                w.WriteNumber("loopFrame", c.LoopFrame == 0xFFFF || c.LoopFrame >= c.Frames ? 0 : c.LoopFrame);
                w.WriteNumber("loopFrameRaw", c.LoopFrame);
                w.WriteBoolean("holdsLastFrame", c.LoopFrame == c.Frames - 1);
                var buf = new float[c.Frames * nb * 16];
                for (int f = 0; f < c.Frames; f++)
                {
                    var wm = Pose.World(c, sk, f, true, out _, out _);
                    for (int b = 0; b < nb; b++) Array.Copy(wm[b].M, 0, buf, (f * nb + b) * 16, 16);
                }
                w.WriteString("boneWorld", Convert.ToBase64String(ToBytes(buf)));
                w.WriteStartArray("events");
                foreach (var e in c.Events)
                {
                    w.WriteStartObject();
                    w.WriteNumber("frame", e.Frame);
                    w.WriteNumber("effect", e.EffectId);
                    w.WriteString("raw", Convert.ToHexString(e.Raw));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                int animId = ci < o.AnimIds.Length ? o.AnimIds[ci] : -1;
                w.WriteNumber("animId", animId);
                var (role, conf, why) = CreatureRoles.Get(o.Index, ci, animId, c);
                w.WriteString("role", role);
                w.WriteString("roleConfidence", conf);
                w.WriteString("roleEvidence", why);
                w.WriteEndObject();
            }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static float[] WorldRest(AnimClip c, Skeleton sk)
    {
        int nb = sk.Parent.Length;
        var world = new Mat4[nb];
        var buf = new float[nb * 16];
        for (int i = 0; i < nb; i++)
        {
            var p = c.RestPos[i]; var q = c.RestRot[i];
            var len = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (len > 0) q = [q[0] / len, q[1] / len, q[2] / len, q[3] / len];
            var local = Mat4.FromQuatPos(q[0], q[1], q[2], q[3], i == 0 ? 0 : p[0], i == 0 ? 0 : p[1], p[2]);
            world[i] = sk.Parent[i] < 0 ? local : Mat4.Concat(local, world[sk.Parent[i]]);
            Array.Copy(world[i].M, 0, buf, i * 16, 16);
        }
        return buf;
    }

    static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        if (!BitConverter.IsLittleEndian) for (int i = 0; i < b.Length; i += 4) Array.Reverse(b, i, 4);
        return b;
    }

    static void Arr(Utf8JsonWriter w, float[] a) { w.WriteStartArray(); foreach (var x in a) w.WriteNumberValue(x); w.WriteEndArray(); }
}
