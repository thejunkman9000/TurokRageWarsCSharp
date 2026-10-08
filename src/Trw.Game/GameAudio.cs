using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace Trw.Walk;

/// <summary>Sound effects and music. Effects are the WAV slots exported from the game sound bank (extracted/audio/sfx); a game sound id is resolved through
/// sfx_ids.tsv (key -> variants -> slot records, docs/formats/audio.md). Music is the offline-rendered stereo WAV of a song (extracted/audio/music), streamed
/// through a DynamicSoundEffectInstance and looped between the frames in song_KEY.loop.txt.</summary>
public sealed class GameAudio : IDisposable
{
    sealed record Record(int Slot, int Delay, int Priority);
    readonly string _root;
    readonly Dictionary<int, SoundEffect?> _slots = new();
    readonly Dictionary<int, List<(int weight, List<Record> records)>> _ids = new();
    readonly Random _rng = new();
    readonly List<(double at, int slot, float volume, float pitch, float pan)> _delayed = new();
    readonly Dictionary<int, int> _lastVariant = new();
    public float SfxVolume = 0.8f, MusicVolume = 0.5f;
    public bool Enabled = true;

    // music streaming
    DynamicSoundEffectInstance? _music;
    byte[] _musicPcm = [];
    int _musicPos, _loopStartByte = -1, _loopEndByte;
    public int CurrentSong = -1;
    const int ChunkBytes = 32000 * 4 / 4;           // a quarter second of 32 kHz stereo 16-bit

    public GameAudio(string dataRoot)
    {
        _root = Path.Combine(dataRoot, "audio");
        string tsv = Path.Combine(_root, "sfx_ids.tsv");
        if (!File.Exists(tsv)) return;
        var raw = new Dictionary<int, SortedDictionary<int, (int weight, List<Record> recs)>>();
        foreach (string line in File.ReadLines(tsv).Skip(1))
        {
            string[] t = line.Split('\t');
            if (t.Length < 7) continue;
            int key = int.Parse(t[0], CultureInfo.InvariantCulture), tree = int.Parse(t[1], CultureInfo.InvariantCulture), weight = int.Parse(t[2], CultureInfo.InvariantCulture);
            var rec = new Record(int.Parse(t[4], CultureInfo.InvariantCulture), int.Parse(t[5], CultureInfo.InvariantCulture), int.Parse(t[6], CultureInfo.InvariantCulture));
            if (!raw.TryGetValue(key, out var variants)) raw[key] = variants = new();
            if (!variants.TryGetValue(tree, out var entry)) variants[tree] = entry = (weight, new List<Record>());
            entry.recs.Add(rec);
        }
        foreach (var (key, variants) in raw) _ids[key] = variants.Values.ToList();
    }

    SoundEffect? Slot(int slot)
    {
        if (_slots.TryGetValue(slot, out var s)) return s;
        string path = Path.Combine(_root, "sfx", $"sfx_slot_{slot:D3}.wav");
        try { using var fs = File.OpenRead(path); s = SoundEffect.FromStream(fs); } catch { s = null; }                  // also covers machines without an audio device
        return _slots[slot] = s;
    }

    /// <summary>Plays a sound-bank slot directly.</summary>
    public void PlaySlot(int slot, float volume = 1f, float pitch = 0f, float pan = 0f)
    {
        if (!Enabled) return;
        Slot(slot)?.Play(Math.Clamp(volume * SfxVolume, 0f, 1f), Math.Clamp(pitch, -1f, 1f), Math.Clamp(pan, -1f, 1f));
    }

    /// <summary>Plays a game sound id (picks a weighted variant that is not the one played last, then starts each of its records after its delay).</summary>
    public void Play(int id, double now, float volume = 1f, float pan = 0f)
    {
        if (!Enabled || !_ids.TryGetValue(id, out var variants) || variants.Count == 0) return;
        int pick = 0;
        if (variants.Count > 1)
        {
            int total = variants.Sum(v => v.weight), tries = 0;
            do
            {
                int r = _rng.Next(Math.Max(1, total)), acc = 0;
                for (pick = 0; pick < variants.Count; pick++) { acc += variants[pick].weight; if (r < acc) break; }
                pick = Math.Min(pick, variants.Count - 1);
            } while (_lastVariant.TryGetValue(id, out int last) && last == pick && ++tries < 6);
            _lastVariant[id] = pick;
        }
        foreach (var rec in variants[pick].records)
        {
            if (rec.Delay <= 0) PlaySlot(rec.Slot, volume, 0f, pan);
            else _delayed.Add((now + rec.Delay / 15.0, rec.Slot, volume, 0f, pan));                // delay unit assumed to be game ticks (1/15 s)
        }
    }

    public void Update(double now)
    {
        for (int i = _delayed.Count - 1; i >= 0; i--)
            if (_delayed[i].at <= now) { var d = _delayed[i]; _delayed.RemoveAt(i); PlaySlot(d.slot, d.volume, d.pitch, d.pan); }
        if (_music != null && _music.State == SoundState.Playing)
        {
            _music.Volume = Enabled ? MusicVolume : 0f;
            while (_music.PendingBufferCount < 4 && Feed()) { }
        }
    }

    bool Feed()
    {
        if (_music == null || _musicPcm.Length == 0) return false;
        int end = _loopStartByte >= 0 ? _loopEndByte : _musicPcm.Length;
        if (_musicPos >= end)
        {
            if (_loopStartByte < 0) return false;                         // a jingle: plays once
            _musicPos = _loopStartByte;
        }
        int n = Math.Min(ChunkBytes, end - _musicPos) & ~3;
        if (n <= 0) return false;
        _music.SubmitBuffer(_musicPcm, _musicPos, n);
        _musicPos += n;
        return true;
    }

    /// <summary>Starts a song by its game key (50, 52, 56, 57, 58, 59, 60, 61, 65); -1 stops the music.</summary>
    public void PlayMusic(int key)
    {
        if (key == CurrentSong || !Enabled) return;                 // --nosound: never opens the audio stream
        StopMusic();
        CurrentSong = key;
        if (key < 0) return;
        string wav = Path.Combine(_root, "music", $"song_{key}.wav");
        if (!File.Exists(wav)) return;
        byte[] file = File.ReadAllBytes(wav);
        int p = 12, dataOff = -1, dataLen = 0, rate = 32000, channels = 2;
        while (p + 8 <= file.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(file, p, 4);
            int len = BitConverter.ToInt32(file, p + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(file, p + 10); rate = BitConverter.ToInt32(file, p + 12); }
            else if (id == "data") { dataOff = p + 8; dataLen = Math.Min(len, file.Length - dataOff); break; }
            p += 8 + len + (len & 1);
        }
        if (dataOff < 0) return;
        _musicPcm = new byte[dataLen]; Buffer.BlockCopy(file, dataOff, _musicPcm, 0, dataLen);
        _musicPos = 0; _loopStartByte = -1;
        string loop = Path.Combine(_root, "music", $"song_{key}.loop.txt");
        if (File.Exists(loop))
        {
            string[] t = File.ReadAllText(loop).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (t.Length >= 2) { _loopStartByte = int.Parse(t[0], CultureInfo.InvariantCulture) * channels * 2; _loopEndByte = Math.Min(dataLen, int.Parse(t[1], CultureInfo.InvariantCulture) * channels * 2); }
        }
        try
        {
            _music = new DynamicSoundEffectInstance(rate, channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono) { Volume = MusicVolume };
            for (int i = 0; i < 4 && Feed(); i++) { }
            _music.Play();
        }
        catch (NoAudioHardwareException) { _music = null; Enabled = false; }
    }

    public void StopMusic()
    {
        _music?.Stop(); _music?.Dispose(); _music = null; _musicPcm = []; CurrentSong = -1;
    }

    public void Dispose() => StopMusic();
}
