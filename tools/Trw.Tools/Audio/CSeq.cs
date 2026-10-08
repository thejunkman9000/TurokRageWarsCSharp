using System.Text;

namespace Trw.Tools.Audio;

/// <summary>
/// libultra "compact sequence" (ALCSeq): u32 trackOffset[16], u32 division, then per-track compressed MIDI-like events.
/// Reimplements __getTrackByte / __alCSeqGetTrackEvent / __readVarLen and converts to a standard MIDI file.
/// </summary>
public static class CSeq
{
    sealed class Reader
    {
        readonly byte[] d;
        public int Pos;
        int buPtr, buLen;
        public Reader(byte[] data, int pos) { d = data; Pos = pos; }

        public int Byte()
        {
            if (buLen != 0)
            {
                int b = d[buPtr++];
                buLen--;
                return b;
            }
            int c = d[Pos++];
            if (c != 0xFE) return c;
            int b1 = d[Pos];
            Pos++;
            if (b1 == 0xFE) return 0xFE;                 // escaped literal
            int off = (b1 << 8) + d[Pos++];
            int len = d[Pos++];
            buPtr = Pos - (off + 4);                      // Pos now points past the 4 byte code
            buLen = len;
            int r = d[buPtr++];
            buLen--;
            return r;
        }

        public int VarLen()
        {
            int v = Byte();
            if ((v & 0x80) != 0)
            {
                v &= 0x7F;
                int c;
                do { c = Byte(); v = (v << 7) + (c & 0x7F); } while ((c & 0x80) != 0);
            }
            return v;
        }
    }

    sealed class Ev { public long Tick; public int Order; public byte[] Bytes = Array.Empty<byte>(); }

    public static byte[] ToMidi(byte[] seq, out string info)
    {
        int division = (int)Be.U32(seq, 64);
        var trackEvents = new List<List<Ev>>();
        var conductor = new List<Ev>();
        var programs = new SortedSet<int>();
        int firstTempo = 0;
        long maxTick = 0;
        int notes = 0, loops = 0, nTracks = 0;
        for (int t = 0; t < 16; t++)
        {
            int off = (int)Be.U32(seq, t * 4);
            if (off == 0) continue;
            nTracks++;
            var rd = new Reader(seq, off);
            var evs = new List<Ev>();
            long tick = 0;
            int lastStatus = 0;
            tick += rd.VarLen();
            while (true)
            {
                int b = rd.Byte();
                if (b == 0xFF)
                {
                    int type = rd.Byte();
                    if (type == 0x2F) { evs.Add(new Ev { Tick = tick, Order = 9, Bytes = new byte[] { 0xFF, 0x2F, 0 } }); break; }
                    if (type == 0x51)
                    {
                        int b0 = rd.Byte(), b1 = rd.Byte(), b2 = rd.Byte();
                        lastStatus = 0;
                        if (firstTempo == 0) firstTempo = (b0 << 16) | (b1 << 8) | b2;
                        conductor.Add(new Ev { Tick = tick, Order = 0, Bytes = new byte[] { 0xFF, 0x51, 3, (byte)b0, (byte)b1, (byte)b2 } });
                    }
                    else if (type == 0x2E)
                    {
                        int cnt = rd.Byte(); rd.Byte();
                        lastStatus = 0; loops++;
                        evs.Add(new Ev { Tick = tick, Order = 1, Bytes = Marker($"loopStart x{cnt}") });
                    }
                    else if (type == 0x2D)
                    {
                        int cnt = seq[rd.Pos];
                        rd.Pos += 6;                     // count, runtime counter, u32 backward offset (raw bytes)
                        lastStatus = 0; loops++;
                        evs.Add(new Ev { Tick = tick, Order = 1, Bytes = Marker($"loopEnd x{cnt}") });
                    }
                    else throw new InvalidDataException($"track {t}: unknown meta 0x{type:X2} at 0x{rd.Pos:X}");
                }
                else
                {
                    int status, d1;
                    if ((b & 0x80) != 0) { status = b; lastStatus = b; d1 = rd.Byte(); }
                    else { status = lastStatus; d1 = b; }
                    if (status == 0) throw new InvalidDataException($"track {t}: data byte without status at 0x{rd.Pos:X}");
                    int hi = status & 0xF0;
                    if (hi == 0xC0 || hi == 0xD0)
                    {
                        if (hi == 0xC0) programs.Add(d1);
                        evs.Add(new Ev { Tick = tick, Order = 2, Bytes = new[] { (byte)status, (byte)d1 } });
                    }
                    else
                    {
                        int d2 = rd.Byte();
                        if (hi == 0x90)
                        {
                            int dur = rd.VarLen();
                            notes++;
                            evs.Add(new Ev { Tick = tick, Order = 3, Bytes = new[] { (byte)status, (byte)d1, (byte)d2 } });
                            evs.Add(new Ev { Tick = tick + dur, Order = 1, Bytes = new[] { (byte)(0x80 | (status & 0xF)), (byte)d1, (byte)0 } });
                        }
                        else evs.Add(new Ev { Tick = tick, Order = 2, Bytes = new[] { (byte)status, (byte)d1, (byte)d2 } });
                    }
                }
                tick += rd.VarLen();
            }
            maxTick = Math.Max(maxTick, evs.Max(e => e.Tick));
            trackEvents.Add(evs);
        }

        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void W32(int v) { W16(v >> 16); W16(v & 0xFFFF); }
        ms.Write(Encoding.ASCII.GetBytes("MThd")); W32(6); W16(1); W16(trackEvents.Count + 1); W16(division);
        WriteTrack(ms, conductor, maxTick);
        foreach (var evs in trackEvents) WriteTrack(ms, evs, maxTick);
        double bpm = firstTempo > 0 ? 60_000_000.0 / firstTempo : 0;
        info = $"{nTracks} tracks, division {division}, {notes} notes, {loops} loop markers, {maxTick} ticks, tempo {bpm:F1} bpm, programs [{string.Join(",", programs)}]";
        return ms.ToArray();
    }

    static byte[] Marker(string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        var r = new byte[3 + t.Length];
        r[0] = 0xFF; r[1] = 0x06; r[2] = (byte)t.Length;
        t.CopyTo(r, 3);
        return r;
    }

    static void WriteTrack(MemoryStream outp, List<Ev> evs, long endTick)
    {
        var body = new MemoryStream();
        long last = 0;
        bool hasEnd = false;
        foreach (var e in evs.Where(e => !(e.Bytes.Length == 3 && e.Bytes[0] == 0xFF && e.Bytes[1] == 0x2F)).OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            VarLen(body, e.Tick - last); last = e.Tick;
            body.Write(e.Bytes);
        }
        VarLen(body, Math.Max(0, endTick - last));
        body.Write(new byte[] { 0xFF, 0x2F, 0 });
        _ = hasEnd;
        outp.Write(Encoding.ASCII.GetBytes("MTrk"));
        int len = (int)body.Length;
        outp.WriteByte((byte)(len >> 24)); outp.WriteByte((byte)(len >> 16)); outp.WriteByte((byte)(len >> 8)); outp.WriteByte((byte)len);
        body.WriteTo(outp);
    }

    static void VarLen(Stream s, long v)
    {
        Span<byte> buf = stackalloc byte[5];
        int n = 0;
        buf[n++] = (byte)(v & 0x7F);
        while ((v >>= 7) > 0) buf[n++] = (byte)((v & 0x7F) | 0x80);
        for (int i = n - 1; i >= 0; i--) s.WriteByte(buf[i]);
    }
}
