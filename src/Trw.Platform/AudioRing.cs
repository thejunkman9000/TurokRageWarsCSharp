namespace Trw.Platform;

/// <summary>
/// The console's audio interface FIFO, modelled on the clock exactly like the C port's os_shim.c: every osAiSetNextBuffer extends the time at which the queued
/// buffers finish playing, and osAiGetStatus reports FULL (bit 31) while more than two frames of buffers plus the host output's 100 ms lead are pending. The
/// game's audio thread produces frames until FULL on every tick (it never reads AI_LEN), which is how it regulates its rate; with the status stuck at
/// "not full" the production rate is set by the task completions instead.
/// </summary>
public sealed class AiFifo
{
    public const ulong CountHz = 46875000;           // CP0 count ticks per second
    public uint Frequency { get; private set; } = 22050;
    private ulong end;                               // clock time at which the queued buffers finish

    public void SetFrequency(uint f) { if (f != 0) Frequency = f; }

    /// <summary>osAiSetNextBuffer: <paramref name="bytes"/> of 16-bit stereo (4 bytes per frame) queued at clock time <paramref name="now"/>.</summary>
    public void Queue(ulong now, uint bytes)
    {
        if (end < now) end = now;
        end += (ulong)(bytes / 4) * CountHz / Frequency;
        started = true;
    }
    private bool started;

    /// <summary>osAiGetStatus: FULL (0x80000000) while more than two frames plus 100 ms are still queued, else 0.</summary>
    public uint Status(ulong now)
    {
        ulong lead = end > now ? end - now : 0;
        return lead >= CountHz / 60 * 2 + CountHz / 10 ? 0x80000000u : 0;
    }

    /// <summary>The AI DMA-done interrupt (OS_EVENT_AI = 6) is posted on retraces while audio is queued (about one per frame).</summary>
    /// In the real-time window it is posted on every retrace once audio has started: when the host stalls for longer than the queued audio lasts (a busy PC)
    /// the queue runs dry between two retraces, and if the last interrupt came while the FIFO still reported FULL the game queued nothing; with the
    /// interrupt tied to queued audio none was posted again, the game's audio thread (which refills on it) waited forever and the sound stopped for good.
    /// Lockstep runs (oracle, netplay) keep the original rule, so their digests are unchanged.
    public bool DmaDoneDue(ulong now) => end + CountHz / 60 > now || (started && !Oracle.Deterministic);
}

/// <summary>
/// Host-side jitter buffer between the game's AI buffers (big-endian 16-bit stereo) and the sound device, the policy of the C port's audio_out.c: the output
/// starts consuming only once 100 ms are queued and falls back to waiting (silence meanwhile) after an underrun; more than 300 ms queued (the game ran ahead,
/// e.g. after a stall) drops back to 100 ms of latency. Samples are byte-swapped to little-endian on the way in.
/// </summary>
public sealed class AudioRing
{
    private const int RingBytes = 1 << 18;
    private readonly byte[] ring = new byte[RingBytes];
    private uint rd, wr;                             // byte counters, power-of-two ring
    private readonly object gate = new();
    private bool primed;

    public uint Frequency { get; set; } = 32000;
    public int Underruns { get; private set; }
    public int Drifted { get; private set; }         // sample frames skipped by the drift correction
    public long Pushed { get; private set; }

    /// <summary>100 ms of audio in bytes at the current rate.</summary>
    public uint PrebufBytes => Frequency / 10 * 4;
    public uint Queued { get { lock (gate) return wr - rd; } }
    public bool Primed { get { lock (gate) return primed; } }

    /// <summary>Queues big-endian stereo samples (a multiple of 4 bytes is used).</summary>
    public void Push(ReadOnlySpan<byte> bigEndian)
    {
        uint n = (uint)(bigEndian.Length & ~3);
        if (n == 0) return;
        lock (gate)
        {
            if ((wr - rd) + n > PrebufBytes * 3) rd = wr - PrebufBytes;      // more than 300 ms queued: back to 100 ms of latency
            for (uint i = 0; i < n; i += 2)
            {
                ring[(wr + i) & (RingBytes - 1)] = bigEndian[(int)i + 1];
                ring[(wr + i + 1) & (RingBytes - 1)] = bigEndian[(int)i];
            }
            wr += n; Pushed += n;
        }
    }

    /// <summary>Fills one device block (little-endian). Returns false when it had to be silence (not primed yet, or an underrun).</summary>
    public bool Read(Span<byte> dst)
    {
        lock (gate)
        {
            uint avail = wr - rd, want = (uint)dst.Length;
            if (!primed && avail >= PrebufBytes) primed = true;
            if (primed && avail >= want)
            {
                for (uint i = 0; i < want; i++) dst[(int)i] = ring[(rd + i) & (RingBytes - 1)];
                rd += want;
                // the producer follows the host clock, the device its own (usually a little slower): above 200 ms skip one sample frame per block
                // (0.2 %, inaudible) instead of letting the ring reach the 300 ms trim, which drops ~170 ms at once
                if (wr - rd > PrebufBytes * 2 + 4) { rd += 4; Drifted++; }
                return true;
            }
            dst.Clear();
            if (primed) { Underruns++; primed = false; }       // underrun: silence, and wait for the prebuffer again before resuming
            return false;
        }
    }
}
