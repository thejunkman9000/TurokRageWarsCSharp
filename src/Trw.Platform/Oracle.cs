using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Trw.Platform.Gfx;

namespace Trw.Platform;

/// <summary>
/// Regression oracle: deterministic recording of everything observable (graphics command stream, audio PCM, game-state digest).
///   TRW_ORACLE_OUT=&lt;file&gt;       enables recording; one line per event, in event order:
///       F &lt;frame&gt; cmds=&lt;n&gt; draws=&lt;n&gt; verts=&lt;n&gt; hash=&lt;fnv1a64&gt;       one per display list the interpreter produced
///       A &lt;block&gt; bytes=&lt;n&gt; peak=&lt;max |sample|&gt; hash=&lt;fnv1a64&gt;       one per osAiSetNextBuffer (PCM handed to the audio interface)
///       [tick N] state=&lt;D_80146878&gt; d=&lt;hash of the game-state regions&gt; [full=&lt;hash of all RAM&gt;]
///   TRW_ORACLE_DETAIL=1          also writes &lt;file&gt;.detail.gz (gzip): per frame one line per command with every DrawState field and a vertex hash
///   TRW_ORACLE_DUMP_FRAME=a,b    also writes &lt;file&gt;.frames: every vertex float of those frames
///   TRW_ORACLE_DUMP_TICK=a,b     also writes &lt;file&gt;.ticks: the raw words of every digest region at those ticks (and with TRW_ORACLE_DUMP_FULL=1 all RAM)
///   TRW_ORACLE_RAM_EVERY=N       full RAM hash every N ticks (default 30, 0 = never)
///   TRW_DETERMINISTIC=1          lockstep scheduling and a tick-derived clock (see docs/ORACLE.md)
/// Off by default: every hook is guarded by <see cref="Enabled"/>.
/// </summary>
public static class Oracle
{
    public static readonly string? OutPath = Environment.GetEnvironmentVariable("TRW_ORACLE_OUT");
    public static readonly bool Enabled = !string.IsNullOrEmpty(OutPath);
    public static readonly bool Deterministic = Environment.GetEnvironmentVariable("TRW_DETERMINISTIC") == "1";
    private static readonly bool Detail = Enabled && Environment.GetEnvironmentVariable("TRW_ORACLE_DETAIL") == "1";
    private static readonly HashSet<int> DumpFrames = (Environment.GetEnvironmentVariable("TRW_ORACLE_DUMP_FRAME") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var n) ? n : -1).ToHashSet();
    private static readonly HashSet<int> DumpTicks = (Environment.GetEnvironmentVariable("TRW_ORACLE_DUMP_TICK") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var n) ? n : -1).ToHashSet();
    private static readonly int RamEvery = int.TryParse(Environment.GetEnvironmentVariable("TRW_ORACLE_RAM_EVERY"), out var re) ? re : 30;

    private static readonly object gate = new();
    private static StreamWriter? main, detail, frames;
    private static int frameIdx, audioIdx;

    private static void Open()
    {
        if (main != null) return;
        main = new StreamWriter(OutPath!, false, new UTF8Encoding(false)) { NewLine = "\n" };
        if (Detail) detail = new StreamWriter(new System.IO.Compression.GZipStream(File.Create(OutPath + ".detail.gz"), System.IO.Compression.CompressionLevel.Fastest), new UTF8Encoding(false)) { NewLine = "\n" };
        if (DumpFrames.Count > 0) frames = new StreamWriter(OutPath + ".frames", false, new UTF8Encoding(false)) { NewLine = "\n" };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
    }

    public static void Close()
    {
        lock (gate) { main?.Flush(); main?.Dispose(); detail?.Dispose(); frames?.Dispose(); main = detail = frames = null; }
    }

    // ---------------------------------------------------------------------------------------------- FNV-1a 64

    private const ulong Basis = 14695981039346656037UL, Prime = 1099511628211UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bytes(ulong h, ReadOnlySpan<byte> b) { foreach (var x in b) h = (h ^ x) * Prime; return h; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong U32(ulong h, uint v) { h = (h ^ (v & 0xFF)) * Prime; h = (h ^ ((v >> 8) & 0xFF)) * Prime; h = (h ^ ((v >> 16) & 0xFF)) * Prime; return (h ^ (v >> 24)) * Prime; }
    private static ulong F(ulong h, float f) => U32(h, (uint)BitConverter.SingleToInt32Bits(f));
    private static ulong Fs(ulong h, float[] a) { h = U32(h, (uint)a.Length); foreach (var f in a) h = F(h, f); return h; }

    private static readonly ConditionalWeakTable<DecodedTexture, object> texHashes = new();
    private static ulong TexHash(DecodedTexture? t)
    {
        if (t == null) return 0;
        if (texHashes.TryGetValue(t, out var o)) return (ulong)o;       // textures are immutable once decoded; the interpreter caches the objects
        ulong h = Basis; h = U32(h, (uint)t.Width); h = U32(h, (uint)t.Height); h = U32(h, (uint)t.Key); h = U32(h, (uint)(t.Key >> 32));
        h = Bytes(h, t.Rgba);
        texHashes.Add(t, h);
        return h;
    }

    private static ulong VertsHash(GfxVertex[] v) => Bytes(Basis, MemoryMarshal.AsBytes(v.AsSpan()));

    private static ulong StateHash(DrawState s)
    {
        ulong h = Basis;
        h = U32(h, s.Geom); h = U32(h, s.OtherLow); h = U32(h, s.OtherHigh); h = U32(h, s.CombineW0); h = U32(h, s.CombineW1);
        h = Fs(h, s.Prim); h = Fs(h, s.Env); h = Fs(h, s.FogColor);
        h = U32(h, (s.FogMix ? 1u : 0) | (s.FogAlphaFromColor ? 2u : 0) | (s.TextureOn ? 4u : 0) | (s.Linear ? 8u : 0) | ((uint)s.Mode << 8));
        h = U32(h, TexHash(s.Tex0) == 0 ? 0u : 1u); h = U32(h, (uint)TexHash(s.Tex0)); h = U32(h, (uint)(TexHash(s.Tex0) >> 32));
        h = U32(h, (uint)TexHash(s.Tex1)); h = U32(h, (uint)(TexHash(s.Tex1) >> 32));
        h = U32(h, (uint)s.AddressU); h = U32(h, (uint)s.AddressV);
        h = U32(h, (uint)s.ScissorX0); h = U32(h, (uint)s.ScissorY0); h = U32(h, (uint)s.ScissorX1); h = U32(h, (uint)s.ScissorY1);
        h = F(h, s.ScreenWidth); h = F(h, s.ScreenHeight);
        return h;
    }

    private static string Fl(float[] a) => string.Join(",", a.Select(f => BitConverter.SingleToInt32Bits(f).ToString("x8")));

    // ---------------------------------------------------------------------------------------------- graphics

    /// <summary>One finished display list = one frame of the oracle (the window may drop frames; the oracle does not).</summary>
    public static void Frame(IReadOnlyList<GfxCommand> cmds)
    {
        if (!Enabled) return;
        lock (gate)
        {
            Open();
            int idx = frameIdx++;
            ulong h = Basis; int draws = 0, verts = 0, ci = 0;
            bool dump = DumpFrames.Contains(idx);
            if (dump && Environment.GetEnvironmentVariable("TRW_ORACLE_DUMP_FULL") == "1") { var ram = new byte[0x800000]; Mem.Read(0x80000000, ram); DumpWords(100000 + idx, 0x80000000, ram); }      // all RAM as it is while the frame is rendered
            if (detail != null) detail.WriteLine($"F {idx}");
            if (dump) frames!.WriteLine($"F {idx}");
            foreach (var c in cmds)
            {
                switch (c)
                {
                    case ClearColorCommand cc:
                        h = U32(h, 1); h = F(h, cc.R); h = F(h, cc.G); h = F(h, cc.B);
                        detail?.WriteLine($"c {ci} ClearColor {Fl(new[] { cc.R, cc.G, cc.B })}");
                        break;
                    case ClearDepthCommand:
                        h = U32(h, 2);
                        detail?.WriteLine($"c {ci} ClearDepth");
                        break;
                    case DrawCommand d:
                        draws++; verts += d.Vertices.Length;
                        h = U32(h, 3); ulong sh = StateHash(d.State), vh = VertsHash(d.Vertices);
                        h = U32(h, (uint)sh); h = U32(h, (uint)(sh >> 32)); h = U32(h, (uint)d.Vertices.Length); h = U32(h, (uint)vh); h = U32(h, (uint)(vh >> 32));
                        if (detail != null)
                        {
                            var s = d.State;
                            detail.WriteLine($"c {ci} Draw n={d.Vertices.Length} mode={(int)s.Mode} geom={s.Geom:x8} otherL={s.OtherLow:x8} otherH={s.OtherHigh:x8} cmb0={s.CombineW0:x8} cmb1={s.CombineW1:x8} " +
                                $"prim={Fl(s.Prim)} env={Fl(s.Env)} fog={Fl(s.FogColor)} fogmix={(s.FogMix ? 1 : 0)} fogacol={(s.FogAlphaFromColor ? 1 : 0)} texon={(s.TextureOn ? 1 : 0)} " +
                                $"tex0={TexHash(s.Tex0):x16}/{s.Tex0?.Key ?? 0:x16}/{s.Tex0?.Width ?? 0}x{s.Tex0?.Height ?? 0} tex1={TexHash(s.Tex1):x16}/{s.Tex1?.Key ?? 0:x16}/{s.Tex1?.Width ?? 0}x{s.Tex1?.Height ?? 0} " +
                                $"linear={(s.Linear ? 1 : 0)} addr={s.AddressU},{s.AddressV} scissor={s.ScissorX0},{s.ScissorY0},{s.ScissorX1},{s.ScissorY1} screen={Fl(new[] { s.ScreenWidth, s.ScreenHeight })} verts={vh:x16}");
                        }
                        if (dump)
                        {
                            frames!.WriteLine($"c {ci} Draw n={d.Vertices.Length}");
                            for (int i = 0; i < d.Vertices.Length; i++)
                            {
                                var v = d.Vertices[i];
                                frames.WriteLine($"v {ci} {i} " + Fl(new[] { v.X, v.Y, v.Z, v.W, v.R, v.G, v.B, v.A, v.U, v.V }));
                            }
                            if (d.State.Tex0 is { } t0) frames.WriteLine($"t {ci} {t0.Width}x{t0.Height}@{t0.Src:X8} {Convert.ToHexString(t0.Rgba)}");      // the decoded texel bytes of the first texture
                        }
                        break;
                }
                ci++;
            }
            main!.WriteLine($"F {idx} cmds={ci} draws={draws} verts={verts} hash={h:x16}");
        }
    }

    // ---------------------------------------------------------------------------------------------- audio

    /// <summary>PCM (big-endian 16-bit stereo) handed to the audio interface.</summary>
    public static void AudioBlock(byte[] pcm, int n)
    {
        if (!Enabled) return;
        lock (gate)
        {
            Open();
            int peak = 0;
            for (int i = 0; i + 1 < n; i += 2) { int v = Math.Abs((short)((pcm[i] << 8) | pcm[i + 1])); if (v > peak) peak = v; }
            main!.WriteLine($"A {audioIdx++} bytes={n} peak={peak} hash={Bytes(Basis, pcm.AsSpan(0, n)):x16}");
        }
    }

    // ---------------------------------------------------------------------------------------------- game state per tick

    // Regions hashed every tick (all in the game's .data/.bss, KSEG0):
    //   0x80146800..0x80147000  game-state block: D_80146878 (game state), D_80146884 (fade), the pad/controller and timing globals around them
    //   0x8014D000..0x8014D200  UI screen manager (D_8014D080 pointer, D_8014D098 screen id)
    //   0x80110600..0x80110680  display-list / batch counters (D_80110624..D_80110640)
    //   0x80115000..0x80115800  model batch and resource counters (D_801153D0 ...)
    // Additionally every RamEvery ticks the whole 8 MB RAM is hashed.
    private static readonly (uint addr, int len)[] Regions = { (0x80146800, 0x800), (0x8014D000, 0x200), (0x80110600, 0x80), (0x80115000, 0x800) };
    private static byte[]? ramBuf;

    /// <summary>The game-state regions of the per-tick digest (netplay's desync check hashes the same ones).</summary>
    public static IReadOnlyList<(uint addr, int len)> DigestRegions => Regions;
    /// <summary>The digest's word hash of a block of guest memory (the same function as the <c>d=</c> / <c>full=</c> values).</summary>
    public static ulong HashBlock(ReadOnlySpan<byte> b) => Words(Basis, b);

    private static ulong Words(ulong h, ReadOnlySpan<byte> b)
    {
        var w = MemoryMarshal.Cast<byte, ulong>(b);
        foreach (var x in w) { h = (h ^ x) * Prime; h ^= h >> 29; }
        return h;
    }

    private static StreamWriter? ticks;
    private static void DumpWords(int tick, uint addr, ReadOnlySpan<byte> b)
    {
        ticks ??= new StreamWriter(OutPath + ".ticks", false, new UTF8Encoding(false)) { NewLine = "\n" };
        for (int i = 0; i < b.Length; i += 4) ticks.WriteLine($"{tick} {addr + (uint)i:X8} {System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[i..]):X8}");
        ticks.Flush();
    }

    public static void Tick(int tick)
    {
        if (!Enabled) return;
        lock (gate)
        {
            Open();
            ulong h = Basis;
            ramBuf ??= new byte[0x800000];
            var parts = detail != null ? new StringBuilder($"T {tick}") : null;
            foreach (var (a, n) in Regions)
            {
                Mem.Read(a, ramBuf.AsSpan(0, n));
                h = U32(h, a); h = Words(h, ramBuf.AsSpan(0, n));
                if (DumpTicks.Contains(tick)) DumpWords(tick, a, ramBuf.AsSpan(0, n));
                parts?.Append($" {a:X8}={Words(Basis, ramBuf.AsSpan(0, n)):x16}");
            }
            if (parts != null) detail!.WriteLine(parts.ToString());
            if (DumpTicks.Contains(tick) && Environment.GetEnvironmentVariable("TRW_ORACLE_DUMP_FULL") == "1") { Mem.Read(0x80000000, ramBuf); DumpWords(tick, 0x80000000, ramBuf); }
            string full = "";
            if (RamEvery > 0 && tick % RamEvery == 0)
            {
                Mem.Read(0x80000000, ramBuf);
                full = $" full={Words(Basis, ramBuf):x16}";
            }
            main!.WriteLine($"[tick {tick}] state={Mem.S32(0x80146878)} d={h:x16}{full}");
        }
    }

    public static void Note(string s) { if (!Enabled) return; lock (gate) { Open(); main!.WriteLine("# " + s); } }
}
