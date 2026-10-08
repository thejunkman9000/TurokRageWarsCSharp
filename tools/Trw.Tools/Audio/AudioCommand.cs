using System.Text;

namespace Trw.Tools.Audio;

public static class AudioCommand
{
    const string DefaultRom = @"D:\Projects\TurokRageWars\baserom.us.v11.z64";

    /// <summary>args: [rom] [outdir]  (rom defaults to the repo ROM; outdir defaults to ./extracted/audio)</summary>
    public static int Run(string[] args)
    {
        string romPath = args.Length > 0 ? args[0] : DefaultRom;
        string outDir = args.Length > 1 ? args[1] : Path.Combine("extracted", "audio");
        if (!File.Exists(romPath)) { Console.Error.WriteLine("ROM not found: " + romPath); return 1; }
        var a = new AudioRom(File.ReadAllBytes(romPath));
        string sfxDir = Path.Combine(outDir, "sfx"), instDir = Path.Combine(outDir, "instruments"), seqDir = Path.Combine(outDir, "seq");
        Directory.CreateDirectory(sfxDir); Directory.CreateDirectory(instDir); Directory.CreateDirectory(seqDir);

        var report = new StringBuilder();
        report.AppendLine($"sample region ROM 0x{a.SampleBase:X6}-0x{a.SampleEnd:X6}");
        var mb = a.MusicBanks.Banks[0];
        report.AppendLine($"music bank: {mb.Instruments.Count} instruments @ {mb.SampleRate} Hz");
        var sb0 = a.SfxBanks.Banks[0];
        report.AppendLine($"sfx bank: instruments={sb0.Instruments.Count} sounds={a.SfxInstrument.Sounds.Count} rate={sb0.SampleRate}");
        { var so = a.SfxInstrument.Sounds.Select(x => x.Offset).ToList(); int inv = 0; for (int q = 1; q < so.Count; q++) if (so[q] < so[q - 1]) inv++; report.AppendLine($"sfx soundArray order inversions (runtime sorts ascending): {inv}"); }

        // sound id table: game sound id -> sound-array slot
        var tsv = new StringBuilder("key\ttreeIndex\tweight\trecord\tslot\tdelay\tpriority\n");
        foreach (var e in a.Sfx)
            for (int r = 0; r < e.Records.Count; r++)
            {
                var rec = e.Records[r];
                tsv.AppendLine($"{e.Key}\t{e.TreeIndex}\t{e.Weight}\t{r}\t{Be.S16(rec, 0)}\t{Be.S16(rec, 2)}\t{(sbyte)rec[4]}");
            }
        File.WriteAllText(Path.Combine(outDir, "sfx_ids.tsv"), tsv.ToString());

        int wrote = 0, failed = 0;
        var sounds = a.SfxInstrument.Sounds;
        for (int i = 0; i < sounds.Count; i++)
            if (ExportSound(a, sounds[i], sb0.SampleRate, Path.Combine(sfxDir, $"sfx_slot_{i:D3}.wav"), report, "sfx slot " + i)) wrote++; else failed++;

        // music instrument samples (for later synth work)
        var seen = new HashSet<int>();
        for (int ii = 0; ii < mb.Instruments.Count; ii++)
        {
            var ins = mb.Instruments[ii];
            if (ins == null) continue;
            for (int si = 0; si < ins.Sounds.Count; si++)
            {
                var s = ins.Sounds[si];
                if (s.Wave == null || !seen.Add(s.Wave.Offset)) continue;
                if (ExportSound(a, s, mb.SampleRate, Path.Combine(instDir, $"inst{ii:D2}_{si:D2}.wav"), report, $"inst {ii}/{si}")) wrote++; else failed++;
            }
        }

        // sequences
        foreach (var s in a.Songs)
        {
            var raw = a.Rom[s.SeqOffset..(s.SeqOffset + s.SeqLength)];
            File.WriteAllBytes(Path.Combine(seqDir, $"song_{s.Key}.cseq"), raw);
            try
            {
                var mid = CSeq.ToMidi(raw, out var info);
                File.WriteAllBytes(Path.Combine(seqDir, $"song_{s.Key}.mid"), mid);
                report.AppendLine($"song {s.Key} (vol {s.Volume}, tempoParam {s.TempoParam}): {s.SeqLength} bytes, {info}");
            }
            catch (Exception ex) { report.AppendLine($"song {s.Key}: midi conversion failed: {ex.Message}"); }
        }
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"wrote {wrote} wav files ({failed} failed), {a.Songs.Count} songs -> {Path.GetFullPath(outDir)}");
        return 0;
    }

    static bool ExportSound(AudioRom a, AlSound s, int rate, string path, StringBuilder report, string label)
    {
        if (s.Wave == null) { report.AppendLine(label + ": no wavetable"); return false; }
        var pcm = a.Decode(s.Wave);
        if (pcm == null) { report.AppendLine($"{label}: undecodable (type {s.Wave.Type})"); return false; }
        int peak = 0, clipped = 0; foreach (var v in pcm) { peak = Math.Max(peak, Math.Abs((int)v)); if (v >= 32767 || v <= -32768) clipped++; }
        string env = Envelope(pcm);
        int ls = -1, le = -1;
        if (s.Wave.Loop != null && s.Wave.Loop.Count != 0) { ls = (int)s.Wave.Loop.Start; le = (int)s.Wave.Loop.End; }
        WavWriter.Write(path, pcm, rate, ls, le, s.KeyMap?.KeyBase ?? 60);
        report.AppendLine($"{label}: {pcm.Length} samples, {pcm.Length * 1000.0 / rate:F0} ms @ {rate} Hz, peak {peak}, clipped {clipped}, rms[{env}], base 0x{s.Wave.Base:X}, key {(s.KeyMap?.KeyBase ?? -1)}, loop {(ls >= 0 ? ls + ".." + le : "none")}");
        return true;
    }

    /// <summary>RMS in 8 equal segments, /100, as a quick silence-to-decay sanity check.</summary>
    static string Envelope(short[] pcm)
    {
        var parts = new List<string>();
        int seg = Math.Max(1, pcm.Length / 8);
        for (int i = 0; i < 8; i++)
        {
            double s = 0; int n = 0;
            for (int k = i * seg; k < Math.Min(pcm.Length, (i + 1) * seg); k++) { s += (double)pcm[k] * pcm[k]; n++; }
            parts.Add(n == 0 ? "0" : ((int)(Math.Sqrt(s / n) / 100)).ToString());
        }
        return string.Join(" ", parts);
    }
}
