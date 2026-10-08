using System.Globalization;
using System.Text;
using System.Text.Json;
using Trw.Tools.Models;

namespace Trw.Tools.Anim;

/// <summary>
/// "trw anim [rom] [outdir] [--obj N] [--all]": decode skeletons and animation clips of every animated object in the
/// object table and dump JSON (skeleton + clips, per-frame local transforms) and posed OBJ snapshots.
/// Also prints validation statistics (stream bit consumption, bone length constancy, mesh stretch).
/// </summary>
public static class AnimCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

    sealed class AnimObject
    {
        public int Index;
        public int ModelRom;
        public Model Model = null!;
        public Skeleton Skeleton = null!;
        public List<AnimClip> Clips = [];
        public int ModelChunkCount;
    }

    public static int Run(string[] args)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToList();
        bool all = args.Contains("--all");
        int only = -1;
        int oi = Array.IndexOf(args, "--obj");
        if (oi >= 0 && oi + 1 < args.Length && int.TryParse(args[oi + 1], out var ov)) { only = ov; pos.Remove(args[oi + 1]); }
        string romPath = pos.Count > 0 ? pos[0] : DefaultRom;
        string outDir = Path.GetFullPath(pos.Count > 1 ? pos[1] : Path.Combine("extracted", "anim"));
        byte[] rom = File.ReadAllBytes(romPath);
        Directory.CreateDirectory(outDir);

        var objects = LoadObjects(rom, only);
        Console.WriteLine($"{objects.Count} animated objects");
        var inv = CultureInfo.InvariantCulture;
        int totalClips = 0, okClips = 0, badStreams = 0;
        var summary = new StringBuilder("object\tmodelRom\tbones\tclips\tframes(min-max)\tcompressions\tmaxBoneLenVar\tmeshStretch\ttopological\n");
        foreach (var o in objects)
        {
            totalClips += o.Clips.Count;
            foreach (var c in o.Clips)
            {
                bool ok = c.StreamsExact;
                if (ok) okClips++; else badStreams++;
            }
            var (lenVar, stretch) = Validate(o);
            summary.AppendFormat(inv, "{0}\t0x{1:X6}\t{2}\t{3}\t{4}-{5}\t{6}\t{7:0.####}\t{8:0.##}\t{9}\n", o.Index, o.ModelRom, o.Skeleton.Parent.Length, o.Clips.Count,
                o.Clips.Min(c => c.Frames), o.Clips.Max(c => c.Frames), string.Join(",", o.Clips.Select(c => c.Compression).Distinct().Order()), lenVar, stretch, o.Skeleton.IsTopological());
            WriteJson(Path.Combine(outDir, $"obj_{o.Index:D3}.json"), o);
            WriteObjs(outDir, o, all || only >= 0);
        }
        File.WriteAllText(Path.Combine(outDir, "summary.tsv"), summary.ToString());
        Console.WriteLine($"clips decoded: {okClips}/{totalClips} consume their streams exactly (bad: {badStreams}); output in {outDir}");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------------
    static List<AnimObject> LoadObjects(byte[] rom, int only)
    {
        var res = new List<AnimObject>();
        var master = ModelsCommand.MasterOffset;
        if (!SectionContainer.TryParse(rom.AsMemory(master, Math.Min(0x291380, rom.Length - master)), out var m) || m is null) return res;
        var table = m.Sub(0);
        if (table == null) return res;
        int tableRom = master + m.SectionOffset(0);
        for (int i = 0; i < table.Count; i++)
        {
            if (only >= 0 && i != only) continue;
            var entry = table.Sub(i);
            if (entry == null || entry.Count != 4) continue;
            var anims = entry.Sub(2);
            if (anims == null) continue;
            var models = entry.Sub(1);
            if (models == null) continue;
            int entryRom = tableRom + table.SectionOffset(i);
            var clips = new List<AnimClip>();
            for (int k = 0; k < anims.Count; k++)
            {
                int off = entryRom + entry.SectionOffset(2) + anims.SectionOffset(k);
                try
                {
                    var c = AnimClip.Parse(Rnc.Unpack(rom, off), off);
                    if (c != null) clips.Add(c);
                }
                catch (InvalidDataException) { }
            }
            if (clips.Count == 0) continue;
            // pick the first model chunk whose bone count matches the clips
            for (int k = 0; k < models.Count; k++)
            {
                int off = entryRom + entry.SectionOffset(1) + models.SectionOffset(k);
                Model? model;
                try { model = Model.Parse(Rnc.Unpack(rom, off)); } catch (InvalidDataException) { continue; }
                if (model == null || !model.Skinned || model.HitVolumes.Length != clips[0].BoneCount) continue;
                res.Add(new AnimObject { Index = i, ModelRom = off, Model = model, Skeleton = Skeleton.FromModel(model), Clips = clips, ModelChunkCount = models.Count });
                break;
            }
        }
        return res;
    }

    // ---------------------------------------------------------------------------------------------------------------
    /// <summary>Returns (largest relative variation of a non-root bone's local translation length over all frames of all clips,
    /// largest mean-edge stretch of the skinned mesh versus frame 0 of the first clip).</summary>
    static (double, double) Validate(AnimObject o)
    {
        double maxVar = 0;
        foreach (var c in o.Clips)
            for (int b = 1; b < o.Skeleton.Parent.Length && b < c.BoneCount; b++)
            {
                int pt = c.BonePosTrack[b];
                if (pt < 0) continue;
                double mn = double.MaxValue, mx = 0;
                for (int f = 0; f < c.Frames; f++) { var p = c.PosTracks[pt][f]; double l = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]); mn = Math.Min(mn, l); mx = Math.Max(mx, l); }
                if (mx > 0) maxVar = Math.Max(maxVar, (mx - mn) / mx);
            }
        var tris = o.Model.BuildTriangles();
        double MeanEdge(float[][] v)
        {
            double s = 0; int n = 0;
            foreach (var t in tris)
                foreach (var (a, b) in new[] { (t.V0, t.V1), (t.V1, t.V2), (t.V2, t.V0) })
                {
                    double dx = v[a][0] - v[b][0], dy = v[a][1] - v[b][1], dz = v[a][2] - v[b][2];
                    s += Math.Sqrt(dx * dx + dy * dy + dz * dz); n++;
                }
            return n == 0 ? 0 : s / n;
        }
        double ref0 = MeanEdge(Skin(o, o.Clips[0], 0));
        double maxStretch = 1;
        foreach (var c in o.Clips)
            for (int f = 0; f < c.Frames; f += Math.Max(1, c.Frames / 6))
                if (ref0 > 0) maxStretch = Math.Max(maxStretch, MeanEdge(Skin(o, c, f)) / ref0);
        return (maxVar, maxStretch);
    }

    /// <summary>Rigid skinning: every vertex belongs to one bone (the G_MTX active when it was loaded) and is stored in that bone's local space.</summary>
    static float[][] Skin(AnimObject o, AnimClip clip, float frame)
    {
        var world = Pose.World(clip, o.Skeleton, frame, false, out _, out _);
        var m = o.Model;
        var res = new float[m.Vertices.Length][];
        for (int i = 0; i < res.Length; i++)
        {
            var v = m.Vertices[i];
            int b = Math.Min(m.BoneOfVertex[i], world.Length - 1);
            var (x, y, z) = world[b].Transform(v.X, v.Y, v.Z);
            res[i] = [x, y, z];
        }
        return res;
    }

    // ---------------------------------------------------------------------------------------------------------------
    static void WriteJson(string path, AnimObject o)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        w.WriteNumber("object", o.Index);
        w.WriteString("modelRom", $"0x{o.ModelRom:X6}");
        w.WriteNumber("modelChunksInObject", o.ModelChunkCount);
        w.WriteStartArray("bones");
        for (int b = 0; b < o.Skeleton.Parent.Length; b++)
        {
            w.WriteStartObject();
            w.WriteNumber("index", b);
            w.WriteNumber("parent", o.Skeleton.Parent[b]);
            w.WriteNumber("id", o.Skeleton.BoneId[b]);
            w.WriteNumber("vertexCount", o.Model.BoneOfVertex.Take(o.Model.OriginalVertexCount).Count(x => x == b));
            var c0 = o.Clips[0];
            w.WritePropertyName("restPos"); WriteArr(w, c0.RestPos[b]);
            w.WritePropertyName("restRot"); WriteArr(w, c0.RestRot[b]);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("clips");
        for (int ci = 0; ci < o.Clips.Count; ci++)
        {
            var c = o.Clips[ci];
            w.WriteStartObject();
            w.WriteNumber("index", ci);
            w.WriteString("rom", $"0x{c.RomOffset:X6}");
            w.WriteNumber("frames", c.Frames);
            w.WriteNumber("compression", c.Compression);
            w.WriteNumber("smooth", c.Smooth);
            w.WriteNumber("loopFrame", c.LoopFrame == 0xFFFF ? -1 : c.LoopFrame);
            w.WriteNumber("rate", c.PlaybackRate);
            w.WriteNumber("posTracks", c.PosTrackCount);
            w.WriteNumber("rotTracks", c.RotTrackCount);
            w.WriteBoolean("streamsConsumedExactly", c.StreamsExact);
            w.WriteNumber("quatNormError", c.QuatNormError);
            w.WritePropertyName("bounds"); WriteArr(w, c.Bounds);
            w.WriteStartArray("rootDelta"); foreach (var d in c.RootDelta) w.WriteNumberValue(d); w.WriteEndArray();
            w.WriteStartArray("boneTracks");
            for (int b = 0; b < c.BoneCount; b++) { w.WriteStartArray(); w.WriteNumberValue(c.BonePosTrack[b]); w.WriteNumberValue(c.BoneRotTrack[b]); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteStartArray("events");
            foreach (var e in c.Events)
            {
                w.WriteStartObject();
                w.WriteNumber("frame", e.Frame); w.WriteNumber("id", e.EffectId); w.WriteNumber("bone", e.Bone);
                w.WritePropertyName("pos"); WriteArr(w, [e.X, e.Y, e.Z]);
                w.WriteNumber("p0", e.Param0); w.WriteNumber("p1", e.Param1);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // fully resolved local transforms: frames[f][bone] = [px,py,pz,qx,qy,qz,qw]
            w.WriteStartArray("local");
            int nb = Math.Min(o.Skeleton.Parent.Length, c.BoneCount);
            for (int f = 0; f < c.Frames; f++)
            {
                w.WriteStartArray();
                for (int b = 0; b < nb; b++)
                {
                    Pose.SampleBone(c, b, f, out var p, out var q);
                    w.WriteStartArray();
                    foreach (var x in p) w.WriteNumberValue((float)Math.Round(x, 4));
                    foreach (var x in q) w.WriteNumberValue((float)Math.Round(x, 5));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteArr(Utf8JsonWriter w, float[] a) { w.WriteStartArray(); foreach (var x in a) w.WriteNumberValue(x); w.WriteEndArray(); }

    static void WriteObjs(string outDir, AnimObject o, bool many)
    {
        var tris = o.Model.BuildTriangles();
        int clips = many ? o.Clips.Count : Math.Min(2, o.Clips.Count);
        for (int ci = 0; ci < clips; ci++)
        {
            var c = o.Clips[ci];
            var frames = many ? Enumerable.Range(0, 4).Select(k => c.Frames * k / 4).Distinct() : new[] { 0, c.Frames / 2 }.Distinct();
            foreach (int f in frames)
            {
                var v = Skin(o, c, f);
                var sb = new StringBuilder();
                sb.AppendLine($"# object {o.Index} clip {ci} frame {f}/{c.Frames}, rigid skinning, model units");
                foreach (var p in v) sb.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", p[0], p[1], p[2]);
                foreach (var t in tris) sb.AppendFormat("f {0} {1} {2}\n", t.V0 + 1, t.V1 + 1, t.V2 + 1);
                File.WriteAllText(Path.Combine(outDir, $"obj_{o.Index:D3}_c{ci}_f{f:D3}.obj"), sb.ToString());
            }
        }
    }
}
