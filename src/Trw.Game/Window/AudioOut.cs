using Microsoft.Xna.Framework.Audio;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Plays the game's audio interface output. The game (through the RSP audio HLE) produces big-endian 16-bit stereo buffers at its own
/// sample rate; they go through an <see cref="AudioRing"/> (the C port's jitter buffer: 100 ms prebuffer, underrun hysteresis, trim above 300 ms) and are
/// streamed to a <see cref="DynamicSoundEffectInstance"/> in fixed 512-frame blocks. Like the C port's waveOut loop, the device is kept fed with silence while
/// the ring is empty or priming. Game threads only push. The window thread opens the device (<see cref="Pump"/>), after which a small background thread keeps it
/// fed every few milliseconds: submitting only from the 60 Hz Update would starve the device whenever one frame runs late (a GC pause, a window drag, a slow
/// level load), because only <c>DeviceBlocks</c> x 512 frames are queued on it. Nothing on the game or window thread ever blocks on the sound device.
/// </summary>
public sealed class AudioOut : IDisposable
{
    private const int BlockFrames = 512;           // 23 ms device blocks at the game's 22.05 kHz
    private const int DeviceBlocks = 4;            // queued on the device (about 93 ms of device latency at 22.05 kHz)
    private readonly AudioRing ring = new();
    private DynamicSoundEffectInstance? sound;
    private static readonly bool statusLog = Environment.GetEnvironmentVariable("TRW_STATUS") != null;
    private int peak, pushes;
    private Thread? feeder;
    private volatile bool stop;
    private int silenceBlocks, blocksSent;
    private long lastFeed, maxGapTicks;

    public bool Muted { get; set; }

    public void Hook()
    {
        Host.AudioSetFrequency = f => { if (f is >= 8000 and <= 48000 && sound == null) ring.Frequency = f; };
        // the game usually sets its rate before this hook exists (boot runs before the window's Initialize): without this the device opened at the
        // ring's default 32 kHz and played the 22.05 kHz audio ~1.45x too fast
        if (Host.AudioFrequency is >= 8000 and <= 48000) ring.Frequency = Host.AudioFrequency;
        Host.AudioPush = Push;
        // the game produces audio frames until the AI reports FULL: a ring below the prebuffer asks for more (see osAiGetStatus)
        Host.AudioLow = () => !Muted && sound != null && ring.Queued < ring.PrebufBytes;
    }

    private void Push(byte[] bigEndian, int count)
    {
        count &= ~3;
        if (count <= 0 || Muted) return;
        ring.Push(bigEndian.AsSpan(0, count));
        if (statusLog)
        {
            for (int i = 0; i < count; i += 2) { int v = Math.Abs((short)((bigEndian[i] << 8) | bigEndian[i + 1])); if (v > peak) peak = v; }
            if (++pushes % 64 == 0) { Console.Error.WriteLine($"[audio] {pushes} buffers, {ring.Pushed} bytes, peak {peak} over the last 64 buffers, queued {ring.Queued * 1000L / (ring.Frequency * 4)} ms, underruns {ring.Underruns}, drift skips {ring.Drifted}, device blocks {blocksSent} ({silenceBlocks} silent), longest feeder gap {maxGapTicks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms"); peak = 0; maxGapTicks = 0; }
        }
    }

    /// <summary>Called on the window thread each frame: opens the device with the first audio the game produces and starts the feeder thread.</summary>
    public void Pump()
    {
        if (sound != null || Muted) return;
        if (ring.Pushed == 0) return;           // the device opens with the first audio the game produces
        try
        {
            sound = new DynamicSoundEffectInstance((int)ring.Frequency, AudioChannels.Stereo);
            sound.Play();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"audio: cannot open the audio device ({e.Message}); continuing without sound");
            Muted = true;
            return;
        }
        Feed();
        feeder = new Thread(FeedLoop) { IsBackground = true, Name = "audio feeder", Priority = ThreadPriority.AboveNormal };
        feeder.Start();
    }

    private void FeedLoop()
    {
        while (!stop)
        {
            try { Feed(); } catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { return; }
            Thread.Sleep(4);
        }
    }

    /// <summary>Tops the device queue up to <see cref="DeviceBlocks"/> blocks (feeder thread; the lock keeps the first call from the window thread apart).</summary>
    private void Feed()
    {
        var snd = sound;
        if (snd == null) return;
        lock (feedGate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastFeed != 0 && now - lastFeed > maxGapTicks) maxGapTicks = now - lastFeed;
            lastFeed = now;
            while (!stop && snd.PendingBufferCount < DeviceBlocks)
            {
                var block = new byte[BlockFrames * 4];
                if (!ring.Read(block)) silenceBlocks++;      // silence when priming or after an underrun
                blocksSent++;
                snd.SubmitBuffer(block);
            }
        }
    }
    private readonly object feedGate = new();

    /// <summary>Pauses / resumes the device (the app in the background); the feeder keeps the queue topped up, nothing is lost but the time away.</summary>
    public void Suspend(bool suspended)
    {
        lock (feedGate)
        {
            try { if (suspended) sound?.Pause(); else sound?.Resume(); }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        stop = true;
        feeder?.Join(200);
        lock (feedGate) sound?.Dispose();
    }
}
