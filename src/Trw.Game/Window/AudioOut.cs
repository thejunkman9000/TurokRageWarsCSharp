using System.Collections.Concurrent;
using Microsoft.Xna.Framework.Audio;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Plays the game's audio interface output. The game (through the RSP audio HLE) produces big-endian 16-bit stereo buffers at its own
/// sample rate; they are converted to little-endian and streamed through a <see cref="DynamicSoundEffectInstance"/>.
/// Game threads only enqueue; the window thread owns the device (<see cref="Pump"/> is called every frame).
/// </summary>
public sealed class AudioOut : IDisposable
{
    private readonly ConcurrentQueue<byte[]> queue = new();
    private DynamicSoundEffectInstance? sound;
    private uint frequency = 32000;
    private long pushedBytes;
    private static readonly bool statusLog = Environment.GetEnvironmentVariable("TRW_STATUS") != null;
    private int peak, pushes;
    private const int MaxQueuedBuffers = 16;       // far behind: drop the oldest audio instead of lagging

    public bool Muted { get; set; }

    public void Hook()
    {
        Host.AudioSetFrequency = f => { if (f is >= 8000 and <= 48000 && sound == null) frequency = f; };
        Host.AudioPush = Push;
    }

    private void Push(byte[] bigEndian, int count)
    {
        count &= ~3;
        if (count <= 0 || Muted) return;
        var le = new byte[count];
        for (int i = 0; i < count; i += 2) { le[i] = bigEndian[i + 1]; le[i + 1] = bigEndian[i]; }
        queue.Enqueue(le);
        pushedBytes += count;
        if (statusLog)
        {
            for (int i = 0; i < count; i += 2) { int v = Math.Abs((short)((le[i + 1] << 8) | le[i])); if (v > peak) peak = v; }
            if (++pushes % 64 == 0) { Console.Error.WriteLine($"[audio] {pushes} buffers, {pushedBytes} bytes, peak {peak} over the last 64 buffers, queued {queue.Count}"); peak = 0; }
        }
        while (queue.Count > MaxQueuedBuffers) queue.TryDequeue(out _);
    }

    /// <summary>Called on the window thread each frame: feeds the device.</summary>
    public void Pump()
    {
        if (sound == null)
        {
            if (queue.IsEmpty) return;
            try
            {
                sound = new DynamicSoundEffectInstance((int)frequency, AudioChannels.Stereo);
                sound.Play();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"audio: cannot open the audio device ({e.Message}); continuing without sound");
                Muted = true;
                queue.Clear();
                return;
            }
        }
        while (sound.PendingBufferCount < 6 && queue.TryDequeue(out var buf))
            sound.SubmitBuffer(buf);
    }

    public void Dispose() => sound?.Dispose();
}
