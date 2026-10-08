using System.Text;

namespace Trw.Tools.Audio;

/// <summary>Offline software synthesiser: renders the game sequences to stereo WAV files with the game's own instrument samples (no N64 audio emulation).
/// Playback model: per note the keymap picks the sound, pitch = 2^((key - keyBase)/12) (+ detune cents), the ALEnvelope drives attack / decay / release,
/// sustained sounds loop between their loop points while the note is held. Controllers 7 (volume), 10 (pan) and 11 (expression) are honoured; pitch bend and the
/// libultra tremolo/vibrato are not. Output: song_KEY.wav (intro + one loop body) and song_KEY.loop.txt (loop start/end frame, written only when the song loops).</summary>
public static class MusicRender
{
    const int OutRate = 32000;
    const double SynthRate = 22050.0;                     // the game audio interface / synth frequency

    sealed class Note { public long Tick, EndTick; public int Channel, Key, Velocity; }
    sealed record Controller(long Tick, int Number, int Value);

    /// <summary>Debug listing of the music bank: instruments, sounds, keymaps, envelopes, loops.</summary>
    public static void Dump(AudioRom rom)
    {
        var bank = rom.MusicBanks.Banks[0];
        for (int i = 0; i < bank.Instruments.Count; i++)
        {
            var ins = bank.Instruments[i];
            if (ins == null) { Console.WriteLine($"inst {i}: null"); continue; }
            Console.WriteLine($"inst {i}: vol {ins.Volume} pan {ins.Pan} pri {ins.Priority} bend {ins.BendRange} sounds {ins.Sounds.Count}");
            for (int k = 0; k < ins.Sounds.Count; k++)
            {
                var s = ins.Sounds[k]; var km = s.KeyMap; var e = s.Envelope; var w = s.Wave;
                Console.WriteLine($"   [{k}] keys {km?.KeyMin}-{km?.KeyMax} base {km?.KeyBase} detune {km?.Detune} vel {km?.VelMin}-{km?.VelMax} pan {s.Pan} vol {s.Volume} | env a={e?.Attack} d={e?.Decay} r={e?.Release} av={e?.AttackVol} dv={e?.DecayVol} | wave len {w?.Len} type {w?.Type} loop {(w?.Loop != null ? w.Loop.Start + ".." + w.Loop.End + " x" + w.Loop.Count : "-")}");
            }
        }
        if (bank.Percussion != null) Console.WriteLine($"percussion instrument with {bank.Percussion.Sounds.Count} sounds");
    }

    /// <summary>args: [rom] [outdir]  (outdir defaults to ./extracted/audio; reads outdir/seq/*.mid, writes outdir/music/)</summary>
    public static int Run(string[] args)
    {
        string romPath = args.Length > 0 ? args[0] : @"D:\Projects\TurokRageWars\baserom.us.v11.z64";
        string outDir = args.Length > 1 ? args[1] : Path.Combine("extracted", "audio");
        if (!File.Exists(romPath)) { Console.Error.WriteLine("ROM not found: " + romPath); return 1; }
        var rom = new AudioRom(File.ReadAllBytes(romPath));
        if (args.Contains("--dump")) { Dump(rom); return 0; }
        string musicDir = Path.Combine(outDir, "music"); Directory.CreateDirectory(musicDir);
        var bank = rom.MusicBanks.Banks[0];
        var waves = new Dictionary<int, float[]>();
        foreach (var song in rom.Songs)
        {
            string mid = Path.Combine(outDir, "seq", $"song_{song.Key}.mid");
            if (!File.Exists(mid)) { Console.WriteLine($"song {song.Key}: {mid} missing (run `trw audio` first)"); continue; }
            var (left, right, loopStart, loopEnd) = Render(rom, bank, waves, File.ReadAllBytes(mid));
            string wav = Path.Combine(musicDir, $"song_{song.Key}.wav");
            WriteStereo(wav, left, right, OutRate);
            string loopFile = Path.Combine(musicDir, $"song_{song.Key}.loop.txt");
            if (loopStart >= 0) File.WriteAllText(loopFile, $"{loopStart} {loopEnd}\n"); else if (File.Exists(loopFile)) File.Delete(loopFile);
            Console.WriteLine($"song {song.Key}: {left.Length / (double)OutRate:F1} s, loop {(loopStart >= 0 ? loopStart + ".." + loopEnd : "none")}");
        }
        return 0;
    }

    static (float[] l, float[] r, int loopStart, int loopEnd) Render(AudioRom rom, AlBank bank, Dictionary<int, float[]> waves, byte[] midi)
    {
        // ---- parse the format 1 MIDI written by CSeq.ToMidi
        int division = (midi[12] << 8) | midi[13];
        int tracks = (midi[10] << 8) | midi[11], pos = 14;
        var notes = new List<Note>(); var controllers = new List<Controller>[16];
        for (int i = 0; i < 16; i++) controllers[i] = new();
        var tempos = new List<(long tick, int us)>(); long loopStartTick = -1, loopEndTick = -1; int loopCount = 0;
        var programs = new List<(long tick, int channel, int program)>();
        for (int t = 0; t < tracks; t++)
        {
            pos += 4; int len = (midi[pos] << 24) | (midi[pos + 1] << 16) | (midi[pos + 2] << 8) | midi[pos + 3]; pos += 4;
            int end = pos + len; long tick = 0; int status = 0;
            var open = new Dictionary<(int, int), Queue<Note>>();
            while (pos < end)
            {
                int v = 0, b; do { b = midi[pos++]; v = (v << 7) | (b & 0x7F); } while ((b & 0x80) != 0);
                tick += v;
                int first = midi[pos];
                if (first == 0xFF)
                {
                    int type = midi[pos + 1], n = midi[pos + 2]; pos += 3;
                    if (type == 0x51) tempos.Add((tick, (midi[pos] << 16) | (midi[pos + 1] << 8) | midi[pos + 2]));
                    else if (type == 0x06)
                    {
                        string text = Encoding.ASCII.GetString(midi, pos, n);
                        if (text.StartsWith("loopStart") && loopStartTick < 0) loopStartTick = tick;
                        else if (text.StartsWith("loopEnd") && loopEndTick < 0) { loopEndTick = tick; int x = text.IndexOf('x'); if (x > 0) int.TryParse(text[(x + 1)..], out loopCount); }
                    }
                    pos += n; continue;
                }
                if ((first & 0x80) != 0) { status = first; pos++; }
                int hi = status & 0xF0, ch = status & 0xF;
                if (hi == 0xC0 || hi == 0xD0) { int d1 = midi[pos++]; if (hi == 0xC0) programs.Add((tick, ch, d1)); continue; }
                int a = midi[pos++], c2 = midi[pos++];
                if (hi == 0x90 && c2 > 0)
                {
                    var note = new Note { Tick = tick, EndTick = tick, Channel = ch, Key = a, Velocity = c2 };
                    notes.Add(note);
                    if (!open.TryGetValue((ch, a), out var q)) open[(ch, a)] = q = new Queue<Note>();
                    q.Enqueue(note);
                }
                else if (hi == 0x80 || hi == 0x90) { if (open.TryGetValue((ch, a), out var q) && q.Count > 0) q.Dequeue().EndTick = tick; }
                else if (hi == 0xB0) controllers[ch].Add(new Controller(tick, a, c2));
            }
            pos = end;
        }
        tempos.Sort((x, y) => x.tick.CompareTo(y.tick));
        if (tempos.Count == 0 || tempos[0].tick > 0) tempos.Insert(0, (0, 500000));
        double Seconds(long tick)
        {
            double s = 0; long last = 0; int us = tempos[0].us;
            foreach (var (t, u) in tempos) { if (t >= tick) break; s += (t - last) * us / 1e6 / division; last = t; us = u; }
            return s + (tick - last) * us / 1e6 / division;
        }

        long endTick = loopEndTick > 0 ? loopEndTick : (notes.Count > 0 ? notes.Max(n => n.EndTick) : 0);
        int endFrame = (int)(Seconds(endTick) * OutRate);
        int tail = OutRate * 3;
        var L = new float[endFrame + tail]; var R = new float[endFrame + tail];
        programs.Sort((x, y) => x.tick.CompareTo(y.tick));
        int ProgramAt(int ch, long tick) { int p = 0; foreach (var e in programs) { if (e.tick > tick) break; if (e.channel == ch) p = e.program; } return p; }
        int Cc(int ch, int number, long tick, int dflt) { int v = dflt; foreach (var e in controllers[ch]) { if (e.Tick > tick) break; if (e.Number == number) v = e.Value; } return v; }
        foreach (var l in controllers) l.Sort((x, y) => x.Tick.CompareTo(y.Tick));

        foreach (var note in notes)
        {
            if (note.Tick >= endTick) continue;
            int prog = ProgramAt(note.Channel, note.Tick);
            var inst = prog < bank.Instruments.Count ? bank.Instruments[prog] : null;
            if (inst == null || inst.Sounds.Count == 0) continue;
            AlSound? snd = null; int bestDist = int.MaxValue;
            foreach (var s in inst.Sounds)
            {
                var km = s.KeyMap; if (km == null || s.Wave == null) continue;
                int d = note.Key < km.KeyMin ? km.KeyMin - note.Key : note.Key > km.KeyMax ? note.Key - km.KeyMax : 0;
                if (d == 0 && (note.Velocity < km.VelMin || note.Velocity > km.VelMax)) d = 1;
                if (d < bestDist) { bestDist = d; snd = s; }
            }
            if (snd?.Wave == null || snd.KeyMap == null) continue;
            if (!waves.TryGetValue(snd.Wave.Offset, out var pcm))
            {
                var raw = rom.Decode(snd.Wave);
                if (raw == null) continue;
                waves[snd.Wave.Offset] = pcm = raw.Select(x => x / 32768f).ToArray();
            }
            double pitch = Math.Pow(2, ((note.Key - snd.KeyMap.KeyBase) * 100.0 + snd.KeyMap.Detune) / 1200.0);
            double step = pitch * SynthRate / OutRate;                 // ratio 1.0 consumes samples at the synth output rate (22050 Hz, func_00257380), not the bank's nominal 44100
            double startSec = Seconds(note.Tick), noteSec = Math.Max(0.02, Seconds(Math.Max(note.EndTick, note.Tick + 1)) - startSec);
            float chanVol = Cc(note.Channel, 7, note.Tick, 100) / 127f, expr = Cc(note.Channel, 11, note.Tick, 127) / 127f, panCc = Cc(note.Channel, 10, note.Tick, 64) / 127f;
            float pan = Math.Clamp(panCc + (snd.Pan - 64) / 127f, 0, 1);
            float gain = 0.30f * (note.Velocity / 127f) * chanVol * expr * (snd.Volume / 127f) * (inst.Volume / 127f);
            float gl = gain * MathF.Cos(pan * MathF.PI / 2), gr = gain * MathF.Sin(pan * MathF.PI / 2);
            var env = snd.Envelope;
            double attack = Math.Max(0.001, (env?.Attack ?? 0) / 1e6), decay = (env?.Decay ?? 0) / 1e6, release = Math.Clamp((env?.Release ?? 0) / 1e6, 0.02, 3.0);
            if (env != null && env.Attack < 0) attack = 0.001;
            double aVol = (env?.AttackVol ?? 127) / 127.0, dVol = (env?.DecayVol ?? 127) / 127.0;
            bool loops = snd.Wave.Loop != null && snd.Wave.Loop.Count != 0 && snd.Wave.Loop.End > snd.Wave.Loop.Start;
            double ls = loops ? snd.Wave.Loop!.Start : 0, le = loops ? snd.Wave.Loop!.End : 0;
            int first = (int)(startSec * OutRate), frames = (int)((noteSec + release) * OutRate);
            double p = 0;
            for (int i = 0; i < frames; i++)
            {
                int f = first + i;
                if (f >= L.Length) break;
                if (loops) { while (p >= le) p -= le - ls; } else if (p >= pcm.Length - 1) break;
                int i0 = (int)p; double fr = p - i0; int i1 = Math.Min(i0 + 1, pcm.Length - 1);
                float sample = (float)(pcm[i0] * (1 - fr) + pcm[i1] * fr);
                double t = i / (double)OutRate, amp;
                if (t < attack) amp = aVol * t / attack;
                else if (decay > 0 && decay < 20 && t < attack + decay) amp = aVol + (dVol - aVol) * ((t - attack) / decay);
                else amp = decay > 0 && decay < 20 ? dVol : aVol;
                if (t > noteSec) { double held = noteSec < attack ? aVol * noteSec / attack : amp; amp = (t - noteSec) >= release ? 0 : amp * (1 - (t - noteSec) / release); _ = held; }
                L[f] += sample * gl * (float)amp; R[f] += sample * gr * (float)amp;
                p += step;
            }
        }
        // normalise to a comfortable peak
        float peak = 0.0001f;
        for (int i = 0; i < endFrame; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(L[i]), MathF.Abs(R[i])));
        float norm = Math.Min(4f, 0.85f / peak);
        var ol = new float[endFrame]; var orr = new float[endFrame];
        for (int i = 0; i < endFrame; i++) { ol[i] = L[i] * norm; orr[i] = R[i] * norm; }
        int ls0 = loopStartTick >= 0 && loopEndTick > loopStartTick ? (int)(Seconds(loopStartTick) * OutRate) : -1;
        return (ol, orr, ls0, ls0 >= 0 ? endFrame : -1);
    }

    static void WriteStereo(string path, float[] l, float[] r, int rate)
    {
        using var fs = File.Create(path); using var w = new BinaryWriter(fs);
        int bytes = l.Length * 4;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        for (int i = 0; i < l.Length; i++) { w.Write((short)Math.Clamp(l[i] * 32767f, -32768, 32767)); w.Write((short)Math.Clamp(r[i] * 32767f, -32768, 32767)); }
    }
}
