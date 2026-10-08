using System.Text;

namespace Trw.Tools.Audio;

/// <summary>Minimal 16-bit mono PCM WAV writer with optional 'smpl' loop chunk.</summary>
public static class WavWriter
{
    public static void Write(string path, short[] pcm, int sampleRate, int loopStart = -1, int loopEnd = -1, int midiNote = 60)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        bool loop = loopStart >= 0 && loopEnd > loopStart;
        int dataBytes = pcm.Length * 2;
        int smplBytes = loop ? 8 + 36 + 24 : 0;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(4 + 8 + 16 + smplBytes + 8 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        if (loop)
        {
            w.Write(Encoding.ASCII.GetBytes("smpl"));
            w.Write(36 + 24);
            w.Write(0); w.Write(0); w.Write(1_000_000_000 / sampleRate);
            w.Write(midiNote); w.Write(0); w.Write(0); w.Write(0);
            w.Write(1); w.Write(0);
            w.Write(0); w.Write(0); w.Write(loopStart); w.Write(loopEnd); w.Write(0); w.Write(0);
        }
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (short s in pcm) w.Write(s);
    }
}
