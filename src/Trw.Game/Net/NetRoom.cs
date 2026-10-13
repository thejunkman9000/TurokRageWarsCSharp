using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Trw.Game.Net;

/// <summary>A player in the lobby; the index in <see cref="NetRoom.Members"/> is the pad (0 = the host, then join order).</summary>
public sealed class RosterEntry
{
    public int MemberId;
    public string Name = "";
    public bool Ready;
    public int PingMs = -1;
    public string Build = "";
    public long UserId;
    internal NetLink? Link;
    internal int Pad = -1;
}

/// <summary>What a joiner tells the host.</summary>
public sealed record NetIdentity(string Name, long UserId, string RomSha, string Build);

/// <summary>
/// One netplay session, before and during the match (docs/NETPLAY.md). Host-relayed star: every joiner has one link to the host;
/// the host merges everybody's pad input for a tick into a <see cref="NetFrame"/> and sends it to all, and every peer (the host too)
/// runs tick t only once frame t is known. A pad's input for tick t is sampled when its owner runs tick t - <see cref="InputDelay"/>,
/// so frames 1..InputDelay are empty by definition and nobody waits as long as the round trip to the host stays under the delay.
/// The host is also the referee: it alone decides membership, pad order, the start, and from which tick a dropped player's pad
/// reads "no input" (it simply builds the frames that way), and it compares every joiner's RAM digest with its own.
/// </summary>
public abstract class NetRoom : IDisposable
{
    protected const int Ring = 1024;
    protected readonly object gate = new();
    protected readonly NetFrame?[] frames = new NetFrame?[Ring];
    protected readonly List<RosterEntry> members = new();
    private readonly ConcurrentQueue<string> notices = new();

    public abstract bool IsHost { get; }
    public string LobbyName = "";
    public int MaxPlayers = 4;
    public int LocalMemberId;
    /// <summary>Set once the host has said go: <see cref="Occupied"/>, <see cref="Names"/>, <see cref="InputDelay"/> and <see cref="HashEvery"/> are final.</summary>
    public volatile bool Started;
    /// <summary>Why the session is over for this peer (host left, refused, lost connection); null while it lives.</summary>
    public volatile string? Ended;
    /// <summary>The first desync found (the host compares; joiners are told).</summary>
    public volatile string? DesyncText;
    public int InputDelay = 3, HashEvery = 1;
    public byte Occupied, DroppedMask;
    public readonly string[] Names = { "", "", "", "" };
    public readonly int[] PingMs = { -1, -1, -1, -1 };
    public int DigestsCompared;
    public string HostBuild = "";

    /// <summary>Copy of the roster in pad order (for the lobby screen).</summary>
    public List<RosterEntry> Members { get { lock (gate) return members.Select(m => new RosterEntry { MemberId = m.MemberId, Name = m.Name, Ready = m.Ready, PingMs = m.PingMs, Build = m.Build, UserId = m.UserId }).ToList(); } }

    public int LocalPad { get { lock (gate) { int i = members.FindIndex(m => m.MemberId == LocalMemberId); return i < 0 ? 0 : i; } } }

    /// <summary>Messages for the players (a drop, a desync); drained by the front end.</summary>
    public bool TryNotice(out string s) => notices.TryDequeue(out s!);
    protected void Notice(string s) { notices.Enqueue(s); Console.Error.WriteLine("[net] " + s); }

    /// <summary>The merged input of <paramref name="tick"/>, if known yet. Ticks 0..InputDelay are empty frames (0 = power-on, before the first retrace).</summary>
    public bool TryGetFrame(int tick, out NetFrame frame)
    {
        lock (gate)
        {
            if (tick <= InputDelay) { frame = NetFrame.Empty(tick, Occupied); return true; }
            var f = frames[tick % Ring];
            if (f != null && f.Tick == tick) { frame = f; return true; }
        }
        frame = null!;
        return false;
    }

    /// <summary>The local player's pad for <paramref name="tick"/> (sampled while running tick - InputDelay).</summary>
    public abstract void SubmitLocalInput(int tick, PadInput input);
    /// <summary>The local RAM digest after <paramref name="tick"/>.</summary>
    public abstract void SubmitDigest(int tick, ulong[] digest);
    /// <summary>Who the session is waiting for (names), for the "waiting" notice.</summary>
    public abstract string WaitingFor(int tick);
    public abstract void Leave(string reason);
    public virtual void Dispose() => Leave("closed");

    protected static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    protected static byte[] RosterMessage(List<RosterEntry> list)
    {
        var w = new WireWriter(NetWire.Roster).U8(list.Count);
        foreach (var m in list) w.U8(m.MemberId).U8(m.Ready ? 1 : 0).I16(Math.Clamp(m.PingMs, -1, short.MaxValue)).Str(m.Name);
        return w.ToArray();
    }

    protected static byte[] DigestMessage(int tick, ulong[] d)
    {
        var w = new WireWriter(NetWire.Digest).I32(tick).U8(d.Length);
        foreach (var x in d) w.U64(x);
        return w.ToArray();
    }
}

/// <summary>The host's side: the listener, the roster, the frame builder and the desync referee.</summary>
public sealed class NetHostRoom : NetRoom
{
    public override bool IsHost => true;
    private readonly TcpListener? listener;
    private readonly NetIdentity me;
    private readonly string password;
    private readonly List<NetLink> links = new();
    private readonly Timer pinger;
    private int nextMemberId = 1;
    // match state (under gate)
    private readonly PadInput[,] inputs = new PadInput[4, Ring];
    private readonly int[,] inputTag = new int[4, Ring];
    private readonly int[] dropTick = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
    private int nextBuild;
    private readonly ulong[]?[] myDigest = new ulong[Ring][];
    private readonly int[] myDigestTag = new int[Ring];
    private readonly ulong[]?[,] theirDigest = new ulong[4, Ring][];
    private readonly int[,] theirDigestTag = new int[4, Ring];
    private bool desynced;
    public int Port { get; }
    /// <summary>The roster changed (someone joined, left or got ready): the master listing's player count follows it.</summary>
    public Action? Changed;
    public int TimeoutMs = 15000;

    public NetHostRoom(NetIdentity me, string lobbyName, int maxPlayers, string? password, int port)
    {
        this.me = me;
        this.password = password ?? "";
        LobbyName = lobbyName;
        MaxPlayers = Math.Clamp(maxPlayers, 2, 4);
        HostBuild = me.Build;
        members.Add(new RosterEntry { MemberId = 0, Name = me.Name, Ready = true, PingMs = 0, Build = me.Build, UserId = me.UserId });
        LocalMemberId = 0;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "net accept" }.Start();
        pinger = new Timer(_ => Heartbeat(), null, 1000, 1000);
        Console.Error.WriteLine($"[net] hosting '{lobbyName}' on port {Port} (up to {MaxPlayers} players)");
    }

    private void AcceptLoop()
    {
        try
        {
            for (;;)
            {
                var c = listener!.AcceptTcpClient();
                Attach(NetLink.FromAccepted(c));
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>A new connection (accepted, or the host's leg of a relay pair): it becomes a member once its HELLO checks out.</summary>
    public void Attach(NetLink link)
    {
        lock (gate) links.Add(link);
        link.Received = OnMessage;
        link.Closed = OnClosed;
        link.Start();
    }

    private void Broadcast(byte[] body)
    {
        foreach (var l in links) if (l.Tag != null) l.Send(body);
    }

    private void Refuse(NetLink link, string reason)
    {
        Console.Error.WriteLine($"[net] refused {link.Remote}: {reason}");
        link.Send(new WireWriter(NetWire.Refuse).Str(reason).ToArray());
        ThreadPool.QueueUserWorkItem(_ => link.Close("refused: " + reason, 500));
    }

    private void OnMessage(NetLink link, byte[] body)
    {
        try
        {
            var r = new WireReader(body);
            var who = link.Tag as RosterEntry;
            switch (r.Type)
            {
                case NetWire.Pong:
                    if (who != null) { long stamp = r.I64(); lock (gate) who.PingMs = (int)Math.Clamp(NowMs() - stamp, 0, 9999); }
                    break;
                case NetWire.Hello when who == null:
                {
                    int proto = r.U16(); string rom = r.Str(), build = r.Str(), name = r.Str(); long uid = r.I64(); string pass = r.Str();
                    if (proto != NetWire.ProtocolVersion) { Refuse(link, $"different netplay version (host {NetWire.ProtocolVersion}, you {proto}): update the game"); break; }
                    if (!string.Equals(rom, me.RomSha, StringComparison.OrdinalIgnoreCase))
                    { Refuse(link, $"ROM mismatch: the host's ROM is {Short(me.RomSha)}, yours is {Short(rom)}. Everyone needs the same Turok: Rage Wars (US 1.1) ROM."); break; }
                    if (password.Length > 0 && pass != password) { Refuse(link, "wrong game password"); break; }
                    lock (gate)
                    {
                        if (Started) { Refuse(link, "the game has already started"); break; }
                        if (members.Count >= MaxPlayers) { Refuse(link, "the game is full"); break; }
                        name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
                        var e = new RosterEntry { MemberId = nextMemberId++, Name = name, Build = build, UserId = uid, Link = link };
                        link.Tag = e;
                        members.Add(e);
                        link.Send(new WireWriter(NetWire.Welcome).U8(e.MemberId).Str(LobbyName).U8(MaxPlayers).Str(me.Build).ToArray());
                        Broadcast(RosterMessage(members));
                        Console.Error.WriteLine($"[net] {name} joined via {link.Via} ({link.Remote}) as player {members.Count}{(build != me.Build ? $" (different build {build})" : "")}");
                    }
                    Changed?.Invoke();
                    break;
                }
                case NetWire.Ready when who != null:
                    lock (gate) { who.Ready = r.U8() != 0; if (!Started) Broadcast(RosterMessage(members)); }
                    Changed?.Invoke();
                    break;
                case NetWire.Input when who != null:
                {
                    int tick = r.I32(); var p = new PadInput((ushort)r.U16(), (sbyte)r.I8(), (sbyte)r.I8(), BitConverter.Int32BitsToSingle(r.I32()), BitConverter.Int32BitsToSingle(r.I32()), r.U8() != 0);
                    lock (gate)
                    {
                        if (!Started || who.Pad < 0 || (DroppedMask & (1 << who.Pad)) != 0 || tick < nextBuild || tick >= nextBuild + Ring / 2) break;
                        inputs[who.Pad, tick % Ring] = p; inputTag[who.Pad, tick % Ring] = tick;
                        BuildFrames();
                    }
                    break;
                }
                case NetWire.Digest when who != null:
                {
                    int tick = r.I32(); int n = r.U8(); var d = new ulong[n];
                    for (int i = 0; i < n; i++) d[i] = r.U64();
                    lock (gate)
                    {
                        if (who.Pad < 0) break;
                        theirDigest[who.Pad, tick % Ring] = d; theirDigestTag[who.Pad, tick % Ring] = tick;
                        if (myDigestTag[tick % Ring] == tick && myDigest[tick % Ring] is { } mine) Compare(who.Pad, tick, mine, d);
                    }
                    break;
                }
                case NetWire.Bye:
                    link.Close(who != null ? $"{who.Name} left: {r.Str()}" : "left");
                    break;
            }
        }
        catch (InvalidDataException e) { link.Close("bad message: " + e.Message); }
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;

    private void OnClosed(NetLink link, string reason)
    {
        bool changed = false;
        lock (gate)
        {
            links.Remove(link);
            if (link.Tag is not RosterEntry who) return;
            if (!Started)
            {
                members.Remove(who);
                Broadcast(RosterMessage(members));
                Console.Error.WriteLine($"[net] {who.Name} left the lobby ({reason})");
                changed = true;
            }
            else DropLocked(who.Pad, reason);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>A player is gone mid-match: from the first frame not built yet, their pad reads no input (still plugged in), the same on every peer
    /// because only the host builds frames. Everybody is told for the scoreboard.</summary>
    private void DropLocked(int pad, string reason)
    {
        if (pad <= 0 || (DroppedMask & (1 << pad)) != 0) return;
        DroppedMask |= (byte)(1 << pad);
        dropTick[pad] = nextBuild;
        Notice($"{Names[pad]} (player {pad + 1}) left at tick {nextBuild}: {reason}");
        Broadcast(new WireWriter(NetWire.Drop).U8(pad).I32(nextBuild).Str(reason).ToArray());
        BuildFrames();
    }

    /// <summary>Starts the match if everybody is ready; returns why not otherwise.</summary>
    public string? Start(int inputDelay, int hashEvery)
    {
        lock (gate)
        {
            if (Started) return null;
            if (members.Count < 2) return "Wait for at least one more player.";
            var notReady = members.Where(m => !m.Ready).Select(m => m.Name).ToList();
            if (notReady.Count > 0) return "Not ready: " + string.Join(", ", notReady);
            InputDelay = Math.Clamp(inputDelay, 1, 30);
            HashEvery = Math.Clamp(hashEvery, 1, 3600);
            Occupied = 0;
            for (int i = 0; i < members.Count; i++) { members[i].Pad = i; Names[i] = members[i].Name; Occupied |= (byte)(1 << i); PingMs[i] = members[i].PingMs; }
            nextBuild = InputDelay + 1;
            var w = new WireWriter(NetWire.Start).U8(InputDelay).U16(HashEvery).U8(members.Count);
            foreach (var m in members) w.U8(m.MemberId).Str(m.Name);
            Broadcast(w.ToArray());
            Started = true;
            Console.Error.WriteLine($"[net] start: {members.Count} players ({string.Join(", ", Names.Take(members.Count))}), input delay {InputDelay}, digest every {HashEvery} tick(s)");
        }
        Changed?.Invoke();
        return null;
    }

    public override void SubmitLocalInput(int tick, PadInput input)
    {
        lock (gate)
        {
            inputs[0, tick % Ring] = input; inputTag[0, tick % Ring] = tick;
            BuildFrames();
        }
    }

    /// <summary>Builds and sends every frame whose inputs are all known (the caller holds the lock).</summary>
    private void BuildFrames()
    {
        for (;;)
        {
            int k = nextBuild, slot = k % Ring;
            var f = new NetFrame { Tick = k, Connected = Occupied };
            for (int p = 0; p < 4; p++)
            {
                if ((Occupied & (1 << p)) == 0) continue;
                if (k >= dropTick[p]) { f.Dropped |= (byte)(1 << p); continue; }       // no input from the drop on
                if (inputTag[p, slot] != k) return;                                    // still waiting for this pad
                f.Pads[p] = inputs[p, slot];
            }
            frames[slot] = f;
            nextBuild = k + 1;
            Broadcast(f.Encode());
        }
    }

    public override void SubmitDigest(int tick, ulong[] digest)
    {
        lock (gate)
        {
            myDigest[tick % Ring] = digest; myDigestTag[tick % Ring] = tick;
            for (int p = 1; p < 4; p++)
                if (theirDigestTag[p, tick % Ring] == tick && theirDigest[p, tick % Ring] is { } d) Compare(p, tick, digest, d);
        }
    }

    private void Compare(int pad, int tick, ulong[] mine, ulong[] theirs)
    {
        DigestsCompared++;
        theirDigest[pad, tick % Ring] = null;
        if (desynced) return;
        var what = NetDigest.Describe(tick / HashEvery, mine, theirs);
        if (what == null) return;
        desynced = true;
        DesyncText = $"DESYNC at tick {tick}: {Names[pad]} (player {pad + 1}) differs from the host in {what}";
        Notice(DesyncText);
        Broadcast(new WireWriter(NetWire.Desync).I32(tick).U8(pad).Str(what).ToArray());
    }

    public override string WaitingFor(int tick)
    {
        lock (gate)
        {
            var names = new List<string>();
            for (int p = 1; p < 4; p++)
                if ((Occupied & (1 << p)) != 0 && nextBuild < dropTick[p] && inputTag[p, nextBuild % Ring] != nextBuild) names.Add(Names[p]);
            return string.Join(", ", names);
        }
    }

    /// <summary>Once a second: ping everyone (the round trip shows in the lobby and the scoreboard) and drop links that went silent.</summary>
    private void Heartbeat()
    {
        List<NetLink> snapshot;
        lock (gate) snapshot = links.ToList();
        long now = NowMs();
        foreach (var l in snapshot)
        {
            if (Environment.TickCount64 - l.LastHeardMs > TimeoutMs) { l.Close("timed out"); continue; }
            l.Send(new WireWriter(NetWire.Ping).I64(now).ToArray());
        }
        lock (gate)
        {
            if (!Started) { Broadcast(RosterMessage(members)); return; }
            foreach (var m in members) if (m.Pad > 0) PingMs[m.Pad] = (DroppedMask & (1 << m.Pad)) != 0 ? -1 : m.PingMs;
            PingMs[0] = 0;
            Broadcast(new WireWriter(NetWire.Stats).I16(PingMs[0]).I16(PingMs[1]).I16(PingMs[2]).I16(PingMs[3]).ToArray());
        }
    }

    public override void Leave(string reason)
    {
        pinger.Dispose();
        try { listener?.Stop(); } catch (Exception) { }
        List<NetLink> snapshot;
        lock (gate) { snapshot = links.ToList(); Ended ??= reason; }
        var bye = new WireWriter(NetWire.Bye).Str(reason).ToArray();
        foreach (var l in snapshot) { l.Send(bye); }
        foreach (var l in snapshot) l.Close(reason, 300);
    }
}

/// <summary>A joiner's side: one link to the host.</summary>
public sealed class NetClientRoom : NetRoom
{
    public override bool IsHost => false;
    private readonly NetLink link;
    private readonly Timer watchdog;
    public bool Welcomed;
    public string Via => link.Via;
    public int TimeoutMs = 15000;

    /// <summary>Takes over a connected link and introduces itself.</summary>
    public NetClientRoom(NetLink link, NetIdentity me, string? password)
    {
        this.link = link;
        link.Received = OnMessage;
        link.Closed = (_, reason) => { Ended ??= Started ? "Lost the connection to the host: " + reason : "Disconnected: " + reason; };
        link.Start();
        link.Send(new WireWriter(NetWire.Hello).U16(NetWire.ProtocolVersion).Str(me.RomSha).Str(me.Build).Str(me.Name).I64(me.UserId).Str(password ?? "").ToArray());
        watchdog = new Timer(_ => { if (Environment.TickCount64 - link.LastHeardMs > TimeoutMs) link.Close("the host stopped answering"); }, null, 1000, 1000);
    }

    private void OnMessage(NetLink l, byte[] body)
    {
        try
        {
            var r = new WireReader(body);
            switch (r.Type)
            {
                case NetWire.Ping: l.Send(new WireWriter(NetWire.Pong).I64(r.I64()).ToArray()); break;     // answered on the reader thread: the ping is the network's
                case NetWire.Welcome:
                    LocalMemberId = r.U8(); LobbyName = r.Str(); MaxPlayers = r.U8(); HostBuild = r.Str();
                    Welcomed = true;
                    break;
                case NetWire.Refuse: Ended = "The host refused: " + r.Str(); break;
                case NetWire.Roster:
                {
                    int n = r.U8(); var list = new List<RosterEntry>();
                    for (int i = 0; i < n; i++) list.Add(new RosterEntry { MemberId = r.U8(), Ready = r.U8() != 0, PingMs = r.I16(), Name = r.Str() });
                    lock (gate) { members.Clear(); members.AddRange(list); }
                    break;
                }
                case NetWire.Start:
                {
                    int delay = r.U8(), every = r.U16(), n = r.U8();
                    lock (gate)
                    {
                        InputDelay = delay; HashEvery = Math.Max(1, every);
                        members.Clear(); Occupied = 0;
                        for (int i = 0; i < n; i++) { members.Add(new RosterEntry { MemberId = r.U8(), Name = r.Str(), Ready = true }); Names[i] = members[i].Name; Occupied |= (byte)(1 << i); }
                    }
                    Started = true;
                    Console.Error.WriteLine($"[net] start: {n} players ({string.Join(", ", Names.Take(n))}), I am player {LocalPad + 1}, input delay {delay}");
                    break;
                }
                case NetWire.Frame:
                {
                    var f = NetFrame.Decode(ref r);
                    lock (gate) { frames[f.Tick % Ring] = f; DroppedMask |= f.Dropped; }
                    break;
                }
                case NetWire.Stats:
                    lock (gate) for (int i = 0; i < 4; i++) PingMs[i] = r.I16();
                    break;
                case NetWire.Drop:
                {
                    int pad = r.U8(), from = r.I32(); string why = r.Str();
                    lock (gate) DroppedMask |= (byte)(1 << pad);
                    Notice($"{Names[pad]} (player {pad + 1}) left at tick {from}: {why}");
                    break;
                }
                case NetWire.Desync:
                {
                    int tick = r.I32(), pad = r.U8(); string what = r.Str();
                    DesyncText = $"DESYNC at tick {tick}: {Names[pad]} (player {pad + 1}) differs from the host in {what}";
                    Notice(DesyncText);
                    break;
                }
                case NetWire.Bye: Ended = "The host ended the session: " + r.Str(); break;
            }
        }
        catch (InvalidDataException e) { l.Close("bad message: " + e.Message); }
    }

    public void SetReady(bool ready) => link.Send(new WireWriter(NetWire.Ready).U8(ready ? 1 : 0).ToArray());

    public override void SubmitLocalInput(int tick, PadInput input) =>
        link.Send(new WireWriter(NetWire.Input).I32(tick).U16(input.Buttons).I8(input.X).I8(input.Y).I32(BitConverter.SingleToInt32Bits(input.Yaw)).I32(BitConverter.SingleToInt32Bits(input.Pitch)).U8(input.Mouse ? 1 : 0).ToArray());

    public override void SubmitDigest(int tick, ulong[] digest) => link.Send(DigestMessage(tick, digest));

    public override string WaitingFor(int tick) => "the host";

    public override void Leave(string reason)
    {
        watchdog.Dispose();
        Ended ??= reason;
        link.Send(new WireWriter(NetWire.Bye).Str(reason).ToArray());
        link.Close(reason, 300);
    }
}
