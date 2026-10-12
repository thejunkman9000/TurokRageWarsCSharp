using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Trw.Game.Net;

public enum NetScreen { SignIn, Menu, HostSetup, Browse, Room, InGame }

/// <summary>
/// Everything before the match: signing in, hosting or finding a game, the lobby room. The lobby screens (<see cref="NetUi"/>) show it and
/// call its actions; the automation switches of <see cref="NetConfig"/> call the same actions without a screen (headless tests).
/// All methods run on the game/window thread; network results come back through <see cref="Update"/>.
/// </summary>
public sealed class NetFlow : IDisposable
{
    public readonly NetConfig Cfg;
    public readonly MasterLink Master;
    private readonly string romSha, build;
    private readonly ConcurrentQueue<Action> inbox = new();

    public NetScreen Screen = NetScreen.SignIn;
    public string Status = "";
    public bool Busy;
    // what the screens edit
    public string User = "", Pass = "", LobbyName = "", LobbyPass = "", JoinPass = "";
    public int MaxPlayers = 4;
    public List<LobbyInfo> Lobbies = new();
    public int Selected = -1;
    public NetRoom? Room;
    public bool LocalReady;
    public NetIdentity? Me;

    private bool triedAuto, triedRegisterFallback;
    private long nextAutoBrowse, autoRetryAt;

    public NetFlow(NetConfig cfg, string romSha, string build)
    {
        Cfg = cfg;
        this.romSha = romSha; this.build = build;
        Master = new MasterLink(cfg.MasterUrl, build, romSha);
        Master.RelayRequested = OnRelayRequested;
        User = cfg.User ?? cfg.LastUser;
        MaxPlayers = cfg.MaxPlayers;
        LobbyPass = cfg.LobbyPassword ?? "";
        JoinPass = cfg.LobbyPassword ?? "";
        LobbyName = cfg.HostName ?? "";
    }

    private void Post(Action a) => inbox.Enqueue(a);

    // ------------------------------------------------------------------------------------------ actions

    public void SignIn(bool register)
    {
        if (Busy) return;
        if (User.Trim().Length == 0 || Pass.Length == 0) { Status = "Enter a username and a password."; return; }
        Busy = true; Status = register ? "Creating the account..." : "Signing in...";
        Master.SignIn(User.Trim(), Pass, register, (ok, msg) =>
        {
            Busy = false;
            if (!ok)
            {
                // automation: --net-register on an account that already exists signs in instead
                if (register && Cfg.User != null && !triedRegisterFallback) { triedRegisterFallback = true; Busy = false; SignIn(false); return; }
                Status = msg;
                // automation: a busy or unreachable master (rate limit, restart) is retried in 10 s; a wrong password is not
                if (Cfg.User != null && (msg.Contains("Too many") || msg.Contains("reach") || msg.Contains("down"))) { triedAuto = false; autoRetryAt = Environment.TickCount64 + 10000; }
                return;
            }
            Pass = "";
            Me = new NetIdentity(Master.DisplayName, Master.UserId, romSha, build);
            Cfg.LastUser = Master.Username;
            if (Cfg.User == null) Cfg.Save();
            Status = $"Signed in as {Master.DisplayName}.";
            Screen = NetScreen.Menu;
        });
    }

    public void SignOut()
    {
        Master.SignOut();
        Me = null; Screen = NetScreen.SignIn; Status = "Signed out.";
    }

    public void HostSetup() { Screen = NetScreen.HostSetup; if (LobbyName.Length == 0) LobbyName = (Me?.Name ?? "Turok") + "'s game"; Status = ""; }

    public void Host()
    {
        if (Busy || Me == null) return;
        var name = LobbyName.Trim();
        if (name.Length == 0) { Status = "Give the game a name."; return; }
        NetHostRoom room;
        try { room = OpenHostRoom(Me, name); }
        catch (SocketException e) { Status = "Cannot open the game port: " + e.Message; return; }
        Room = room;
        if (Cfg.Listen && Cfg.User == null) { EnterRoom(); return; }       // LAN only: no listing
        Busy = true; Status = "Listing the game...";
        Master.CreateLobby(name, MaxPlayers, LobbyPass, room.Port, (ok, msg) =>
        {
            Busy = false;
            if (!ok) { room.Leave("listing failed"); Room = null; Status = "Could not list the game: " + msg; return; }
            EnterRoom();
        });
    }

    private NetHostRoom OpenHostRoom(NetIdentity me, string name)
    {
        NetHostRoom room;
        try { room = new NetHostRoom(me, name, MaxPlayers, LobbyPass, Cfg.Port); }
        catch (SocketException) { room = new NetHostRoom(me, name, MaxPlayers, LobbyPass, 0); }     // port taken (another host on this machine): any free port
        room.Changed = () => Post(() => Master.UpdateLobby(room.Members.Count, room.Started));
        return room;
    }

    private void EnterRoom()
    {
        Screen = NetScreen.Room; LocalReady = Room!.IsHost; Status = Room.IsHost ? "Waiting for players. Start when everybody is ready." : "";
    }

    public void Browse() { Screen = NetScreen.Browse; Refresh(); }

    public void Refresh()
    {
        if (Busy) return;
        Busy = true; Status = "Looking for games...";
        Master.Browse((ok, msg, list) =>
        {
            Busy = false;
            if (!ok) { Status = msg; return; }
            Lobbies = list;
            if (Selected >= Lobbies.Count) Selected = Lobbies.Count - 1;
            if (Selected < 0 && Lobbies.Count > 0) Selected = 0;
            Status = Lobbies.Count == 0 ? "No games right now. Refresh, or host one." : $"{Lobbies.Count} game(s).";
        });
    }

    public void Join(LobbyInfo lobby)
    {
        if (Busy || Me == null) return;
        if (lobby.HasPassword && JoinPass.Length == 0) { Status = "That game has a password: type it in the password box."; return; }
        Busy = true; Status = $"Joining {lobby.Name}...";
        Master.Join(lobby.Id, JoinPass, (ok, msg, route) =>
        {
            if (!ok || route == null) { Busy = false; Status = msg; return; }
            Connect(route, JoinPass);
        });
    }

    /// <summary>Tries the routes in order (same network, direct, relay) on a worker thread and makes the room on the first that works.</summary>
    private void Connect(JoinRoute route, string? password)
    {
        var me = Me!;
        bool relayOnly = Cfg.ForceRelay;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            NetLink? link = null; string err = "";
            if (!relayOnly && route.Lan is { } lan) link = NetLink.ConnectDirect(lan.host, lan.port, 1500, "lan");
            if (link == null && !relayOnly && route.Direct is { } d) link = NetLink.ConnectDirect(d.host, d.port, 3000, "direct");
            if (link == null && route.Relay is { } r)
            {
                Post(() => Status = "Connecting through the relay...");
                link = NetLink.ConnectRelay(r.host, r.port, r.ticket, 30000, out err);
            }
            Post(() =>
            {
                Busy = false;
                if (link == null) { Status = "Cannot reach the host" + (err.Length > 0 ? ": " + err : "."); return; }
                Room = new NetClientRoom(link, me, password);
                Console.Error.WriteLine($"[net] connected to '{route.LobbyName}' via {link.Via} ({link.Remote})");
                EnterRoom();
                Status = $"Connected ({link.Via}). Press Ready when you are.";
            });
        });
    }

    private void OnRelayRequested(string host, int port, string ticket, string who)
    {
        if (Room is not NetHostRoom room || room.Started) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var link = NetLink.ConnectRelay(host, port, ticket, 35000, out var err);
            if (link != null) { Console.Error.WriteLine($"[net] relay leg for {who} connected"); room.Attach(link); }
            else if (!err.Contains("did not connect")) Console.Error.WriteLine($"[net] relay leg for {who}: {err}");      // a joiner who connected directly leaves our leg waiting: normal
        });
    }

    public void ToggleReady()
    {
        if (Room is not NetClientRoom c) return;
        LocalReady = !LocalReady;
        c.SetReady(LocalReady);
    }

    public void StartMatch()
    {
        if (Room is not NetHostRoom h) return;
        var why = h.Start(Cfg.InputDelay, Cfg.HashEvery);
        Status = why ?? "Starting...";
    }

    public void LeaveRoom()
    {
        Room?.Leave("left the lobby");
        Room = null;
        Master.CloseLobby();
        Screen = Me != null ? NetScreen.Menu : NetScreen.SignIn;
        if (Cfg.Direct != null || Cfg.Listen) Screen = NetScreen.SignIn;
    }

    // ------------------------------------------------------------------------------------------ per frame

    /// <summary>Delivers network results, follows the room, and runs the automation switches.</summary>
    public void Update()
    {
        Master.Pump();
        while (inbox.TryDequeue(out var a)) a();

        if (Room != null && Screen == NetScreen.Room)
        {
            if (Room.Started) { Screen = NetScreen.InGame; Master.UpdateLobby(Room.Members.Count, true); return; }
            if (Room.Ended is { } why) { Room.Leave(why); Room = null; Master.CloseLobby(); Screen = Me != null ? NetScreen.Menu : NetScreen.SignIn; Status = why; return; }
        }
        Automate();
    }

    private void Automate()
    {
        if (Busy) return;
        switch (Screen)
        {
            case NetScreen.SignIn when !triedAuto:
                if (Cfg.Direct != null || Cfg.Listen)
                {
                    triedAuto = true;
                    var name = Cfg.Name.Length > 0 ? Cfg.Name : Cfg.User ?? Environment.UserName;
                    if (Cfg.Listen && Cfg.User == null) { Me = new NetIdentity(name, 0, romSha, build); LobbyName = Cfg.HostName ?? name + "'s game"; Host(); }
                    else if (Cfg.Direct != null)
                    {
                        Me = new NetIdentity(name, 0, romSha, build);
                        var hp = Cfg.Direct.Split(':');
                        Busy = true; Status = $"Connecting to {Cfg.Direct}...";
                        Connect(new JoinRoute(Cfg.Direct, null, (hp[0], hp.Length > 1 ? int.Parse(hp[1]) : 7810), null), Cfg.LobbyPassword);
                    }
                }
                else if (Cfg.User != null && Cfg.Password != null && Environment.TickCount64 >= autoRetryAt)
                {
                    triedAuto = true;
                    User = Cfg.User; Pass = Cfg.Password;
                    SignIn(Cfg.Register);
                }
                break;
            case NetScreen.Menu:
                if (Cfg.HostName != null) { LobbyName = Cfg.HostName; Host(); }
                else if (Cfg.JoinName != null) Screen = NetScreen.Browse;
                break;
            case NetScreen.Browse when Cfg.JoinName != null:
                var match = Lobbies.FirstOrDefault(l => string.Equals(l.Name, Cfg.JoinName, StringComparison.OrdinalIgnoreCase) && l.Joinable);
                if (match != null) { Join(match); break; }
                if (Environment.TickCount64 >= nextAutoBrowse) { nextAutoBrowse = Environment.TickCount64 + 2000; Refresh(); }
                break;
            case NetScreen.Room when Room != null:
                if (Room is NetClientRoom c && Cfg.AutoReady && !LocalReady && c.Welcomed) ToggleReady();
                if (Room is NetHostRoom h && Cfg.AutoStart > 0)
                {
                    var m = h.Members;
                    if (m.Count >= Cfg.AutoStart && m.All(x => x.Ready)) StartMatch();
                }
                break;
        }
    }

    public void Dispose()
    {
        Room?.Leave("quit");
        Master.Dispose();
    }
}
