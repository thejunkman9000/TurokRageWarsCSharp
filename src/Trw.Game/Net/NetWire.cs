using System.Buffers.Binary;
using System.Text;

namespace Trw.Game.Net;

/// <summary>
/// The netplay wire format (docs/NETPLAY.md). Every message is one frame on a TCP stream (direct, or through the jsNetwork relay, which
/// copies bytes blindly): <c>[u16 LE length of the body][body]</c>, body = <c>[u8 type][payload]</c>. Integers are little-endian,
/// strings are <c>[u16 length][UTF-8 bytes]</c>.
/// </summary>
public static class NetWire
{
    /// <summary>Bumped whenever a message changes; peers with a different version refuse each other in HELLO.</summary>
    public const int ProtocolVersion = 2;       // 2: pad input carries the native mouse look (yaw / pitch floats)
    public const int MaxBody = 16 * 1024;

    // lobby
    public const byte Hello = 1;      // c->h  u16 protocol, str romSha256, str build, str name, i64 userId, str password
    public const byte Welcome = 2;    // h->c  u8 memberId, str lobbyName, u8 maxPlayers, str hostBuild
    public const byte Refuse = 3;     // h->c  str reason (the host closes the link after it)
    public const byte Roster = 4;     // h->*  u8 n, n x (u8 memberId, u8 ready, i16 pingMs, str name)   index = pad
    public const byte Ready = 5;      // c->h  u8 ready
    public const byte Start = 6;      // h->*  u8 inputDelay, u16 hashEvery, u8 n, n x (u8 memberId, str name)   index = pad: boot now
    // match
    public const byte Input = 7;      // c->h  i32 tick, u16 buttons, i8 stickX, i8 stickY       the sender's pad for that tick
    public const byte Frame = 8;      // h->*  i32 tick, u8 connected mask, u8 dropped mask, 4 x (u16 buttons, i8 x, i8 y)
    public const byte Digest = 9;     // c->h  i32 tick, u8 n, n x u64                           RAM digest after that tick (NetDigest)
    public const byte Desync = 10;    // h->*  i32 tick, u8 pad, str detail
    public const byte Drop = 14;      // h->*  u8 pad, i32 fromTick, str reason                  that pad reads "no input" from fromTick on
    // both phases
    public const byte Ping = 11;      // h->c  i64 stamp
    public const byte Pong = 12;      // c->h  i64 stamp (echoed)
    public const byte Stats = 13;     // h->*  4 x i16 pingMs (host<->pad round trip, -1 unknown)
    public const byte Bye = 15;       // any   str reason (leaving)

    public static string Name(byte t) => t switch
    {
        Hello => "HELLO", Welcome => "WELCOME", Refuse => "REFUSE", Roster => "ROSTER", Ready => "READY", Start => "START", Input => "INPUT",
        Frame => "FRAME", Digest => "DIGEST", Desync => "DESYNC", Drop => "DROP", Ping => "PING", Pong => "PONG", Stats => "STATS", Bye => "BYE",
        _ => $"#{t}",
    };
}

/// <summary>Builds one message body (type byte first).</summary>
public sealed class WireWriter
{
    private byte[] buf = new byte[64];
    private int n;

    public WireWriter(byte type) => U8(type);

    private Span<byte> Take(int k)
    {
        if (n + k > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, n + k));
        var s = buf.AsSpan(n, k); n += k; return s;
    }

    public WireWriter U8(int v) { Take(1)[0] = (byte)v; return this; }
    public WireWriter I8(int v) { Take(1)[0] = (byte)(sbyte)v; return this; }
    public WireWriter U16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(Take(2), (ushort)v); return this; }
    public WireWriter I16(int v) { BinaryPrimitives.WriteInt16LittleEndian(Take(2), (short)v); return this; }
    public WireWriter I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), v); return this; }
    public WireWriter I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(Take(8), v); return this; }
    public WireWriter U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v); return this; }
    public WireWriter Str(string? s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > 1024) b = b[..1024];
        U16(b.Length); b.CopyTo(Take(b.Length)); return this;
    }

    public byte[] ToArray() => buf[..n];
}

/// <summary>Reads one message body; throws <see cref="InvalidDataException"/> on a short message (the link is then closed).</summary>
public ref struct WireReader
{
    private readonly ReadOnlySpan<byte> b;
    private int p;

    public WireReader(ReadOnlySpan<byte> body) { b = body; p = 1; }      // skip the type byte
    public byte Type => b[0];

    private ReadOnlySpan<byte> Take(int k)
    {
        if (p + k > b.Length) throw new InvalidDataException("short message");
        var s = b.Slice(p, k); p += k; return s;
    }

    public int U8() => Take(1)[0];
    public int I8() => (sbyte)Take(1)[0];
    public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public string Str() { int k = U16(); return Encoding.UTF8.GetString(Take(k)); }
}

/// <summary>One pad's input for one tick: exactly what <c>osContGetReadData</c> stores for it.</summary>
/// <summary>One pad's input for a tick: buttons, stick, and the native mouse look (degrees right / down since the previous tick, Os/MouseLook.cs).</summary>
public readonly record struct PadInput(ushort Buttons, sbyte X, sbyte Y, float Yaw = 0, float Pitch = 0, bool Mouse = false);

/// <summary>The merged input of one tick, the same on every peer: which pads are plugged in and what each reads.</summary>
public sealed class NetFrame
{
    public int Tick;
    public byte Connected;      // pads the game sees as plugged in (fixed for the session: a dropped player stays plugged in, with no input)
    public byte Dropped;        // informational: pads whose player has left (their input is zero)
    public readonly PadInput[] Pads = new PadInput[4];

    public static NetFrame Empty(int tick, byte connected) => new() { Tick = tick, Connected = connected };

    public byte[] Encode()
    {
        var w = new WireWriter(NetWire.Frame).I32(Tick).U8(Connected).U8(Dropped);
        foreach (var p in Pads) w.U16(p.Buttons).I8(p.X).I8(p.Y).I32(BitConverter.SingleToInt32Bits(p.Yaw)).I32(BitConverter.SingleToInt32Bits(p.Pitch)).U8(p.Mouse ? 1 : 0);
        return w.ToArray();
    }

    public static NetFrame Decode(ref WireReader r)
    {
        var f = new NetFrame { Tick = r.I32(), Connected = (byte)r.U8(), Dropped = (byte)r.U8() };
        for (int i = 0; i < 4; i++) f.Pads[i] = new PadInput((ushort)r.U16(), (sbyte)r.I8(), (sbyte)r.I8(), BitConverter.Int32BitsToSingle(r.I32()), BitConverter.Int32BitsToSingle(r.I32()), r.U8() != 0);
        return f;
    }
}
