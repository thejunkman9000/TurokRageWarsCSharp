using System.Collections.Concurrent;
#if TRW_JSNETWORK
using jsNetwork.Client;
using jsNetwork.Protocol;
#endif

namespace Trw.Game.Net;

/// <summary>A lobby as the browser shows it.</summary>
public sealed record LobbyInfo(string Id, string Name, string Host, int Players, int MaxPlayers, bool HasPassword, bool Joinable, string State);

/// <summary>Where to reach a host: try <see cref="Lan"/>, then <see cref="Direct"/>, then the relay.</summary>
public sealed record JoinRoute(string LobbyName, (string host, int port)? Lan, (string host, int port)? Direct, (string host, int port, string ticket)? Relay);

/// <summary>
/// The game's side of the jsNetwork master server (D:\Projects\jsLibs, jsNetwork.md) under product code <see cref="Product"/>: accounts
/// (shared with the other jsNetwork games), the lobby listing (create, heartbeat, browse, join) and the realtime event stream, of which
/// only <c>relay_request</c> matters here (a joiner asked for the relay: the host opens its leg). Every call runs on the thread pool and
/// its result is queued for the game thread (<see cref="Pump"/>); nothing here throws. Without jsLibs at build time (TRW_JSNETWORK
/// undefined) the class exists but every call fails with a clear message, and only direct connections (--net-direct) work.
/// </summary>
public sealed class MasterLink : IDisposable
{
    /// <summary>The product code this game uses on the master (2-8 upper-case letters or digits).</summary>
    public const string Product = "TRW";

    private readonly ConcurrentQueue<Action> inbox = new();
    public string Url { get; }
    public string Username { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public long UserId { get; private set; }
    public bool SignedIn { get; private set; }
    public bool Busy => Volatile.Read(ref busy) > 0;
    private int busy;
    private string? lobbyId;
    private Timer? heartbeat;
    private volatile int players = 1, maxPlayers = 4;
    private volatile string state = "open";
    /// <summary>The host's side of a relayed join: (relay host, port, ticket, joiner name). Raised on the game thread.</summary>
    public Action<string, int, string, string>? RelayRequested;

    public static bool Available =>
#if TRW_JSNETWORK
        true;
#else
        false;
#endif

#if TRW_JSNETWORK
    private readonly MasterApi api;
    private MasterEvents? events;

    public MasterLink(string url, string version, string romSha)
    {
        Url = url.TrimEnd('/');
        api = new MasterApi(Url, Product, NetWire.ProtocolVersion, version, romSha);
    }

    /// <summary>Runs one call; onDone gets (ok, message, value) on the game thread.</summary>
    private void Run<T>(Func<MasterApi, Task<ApiResult<T>>> call, Action<bool, string, T?> onDone)
    {
        Interlocked.Increment(ref busy);
        _ = Task.Run(async () =>
        {
            ApiResult<T> r;
            try { r = await call(api); }
            catch (Exception e) { r = new ApiResult<T>(false, 0, "error", e.Message, default); }
            Interlocked.Decrement(ref busy);
            inbox.Enqueue(() => onDone(r.Ok, r.Ok ? "" : (r.Message.Length > 0 ? r.Message : $"The online service answered {r.Status}."), r.Value));
        });
    }

    public void SignIn(string user, string password, bool register, Action<bool, string> done) =>
        Run(a => register ? a.Register(user, password) : a.Login(user, password), (ok, msg, auth) =>
        {
            if (ok && auth != null)
            {
                api.Token = auth.Token;
                Username = auth.User.Username; DisplayName = auth.User.DisplayName; UserId = auth.User.Id; SignedIn = true;
                events?.Dispose();
                events = new MasterEvents(api, auth.EventSeq) { Received = OnEvent };
            }
            done(ok, ok ? "" : msg);
        });

    private void OnEvent(ServerEventDto e)
    {
        if (e.Type == EventTypes.RelayRequest && lobbyId != null)
        {
            try
            {
                var d = e.Data;
                if (d.GetProperty("lobbyId").GetString() != lobbyId) return;
                string host = d.GetProperty("host").GetString() ?? "", ticket = d.GetProperty("ticket").GetString() ?? "";
                int port = d.GetProperty("port").GetInt32();
                string who = d.TryGetProperty("user", out var u) && u.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "?" : "?";
                inbox.Enqueue(() => RelayRequested?.Invoke(host, port, ticket, who));
            }
            catch (Exception) { }
        }
        else if (e.Type == EventTypes.SignedOut) inbox.Enqueue(() => { SignedIn = false; });
    }

    public void SignOut()
    {
        CloseLobby();
        events?.Dispose(); events = null;
        if (SignedIn) _ = api.Logout();
        SignedIn = false; api.Token = null;
    }

    /// <summary>Lists the host's game: name, player limit, password, game port; heartbeats it every 10 s until <see cref="CloseLobby"/>.</summary>
    public void CreateLobby(string name, int max, string? password, int port, Action<bool, string> done)
    {
        maxPlayers = max; players = 1; state = ListingState.Open;
        var body = new LobbyCreateRequest(name, "versus", "Rage Wars", null, max, 1, string.IsNullOrEmpty(password) ? null : password, Visibility.Public, null, api.Version,
            api.DataHash, port, NetLink.LanAddress());
        Run(a => a.CreateLobby(body), (ok, msg, env) =>
        {
            if (ok && env != null)
            {
                lobbyId = env.Lobby.Id;
                heartbeat?.Dispose();
                heartbeat = new Timer(_ => Heartbeat(), null, 10000, 10000);
                Console.Error.WriteLine($"[net] listed on {Url} as {lobbyId} (reachable from outside: {env.Lobby.Reachable})");
            }
            done(ok, msg);
        });
    }

    /// <summary>The roster changed or the match started: the listing follows (now, not at the next heartbeat).</summary>
    public void UpdateLobby(int nowPlayers, bool playing)
    {
        players = nowPlayers; state = playing ? ListingState.Playing : ListingState.Open;
        if (lobbyId != null) ThreadPool.QueueUserWorkItem(_ => Heartbeat());
    }

    private void Heartbeat()
    {
        var id = lobbyId;
        if (id == null) return;
        _ = api.UpdateLobby(id, new LobbyUpdateRequest(null, null, null, null, players, state, null));
    }

    public void CloseLobby()
    {
        heartbeat?.Dispose(); heartbeat = null;
        var id = lobbyId; lobbyId = null;
        if (id != null) _ = api.DeleteLobby(id);
    }

    public void Browse(Action<bool, string, List<LobbyInfo>> done) =>
        Run(a => a.Lobbies(new LobbyQuery { HideDedicated = true }), (ok, msg, list) =>
            done(ok, msg, list?.Lobbies.Select(l => new LobbyInfo(l.Id, l.Name, l.Host.DisplayName, l.Players, l.MaxPlayers, l.HasPassword, l.Joinable, l.State)).ToList() ?? new()));

    public void Join(string id, string? password, Action<bool, string, JoinRoute?> done) =>
        Run(a => a.Join(id, string.IsNullOrEmpty(password) ? null : password), (ok, msg, j) =>
            done(ok, msg, j == null ? null : new JoinRoute(j.Lobby.Name,
                j.Lan is { } lan ? (lan.Host, lan.Port) : null,
                j.Direct is { } dir ? (dir.Host, dir.Port) : null,
                j.Relay is { } rel ? (rel.Host, rel.Port, rel.Ticket) : null)));
#else
    public MasterLink(string url, string version, string romSha) { Url = url; }
    private const string NoMaster = "This build has no jsNetwork (D:\\Projects\\jsLibs was missing at build time): use --net-direct / --net-listen.";
    public void SignIn(string user, string password, bool register, Action<bool, string> done) => inbox.Enqueue(() => done(false, NoMaster));
    public void SignOut() { }
    public void CreateLobby(string name, int max, string? password, int port, Action<bool, string> done) => inbox.Enqueue(() => done(false, NoMaster));
    public void UpdateLobby(int nowPlayers, bool playing) { }
    public void CloseLobby() { }
    public void Browse(Action<bool, string, List<LobbyInfo>> done) => inbox.Enqueue(() => done(false, NoMaster, new()));
    public void Join(string id, string? password, Action<bool, string, JoinRoute?> done) => inbox.Enqueue(() => done(false, NoMaster, null));
    private void Heartbeat() { }
#endif

    /// <summary>Delivers finished calls and events on the calling (game) thread.</summary>
    public void Pump()
    {
        while (inbox.TryDequeue(out var a)) a();
    }

    public void Dispose()
    {
        heartbeat?.Dispose();
#if TRW_JSNETWORK
        CloseLobby();
        events?.Dispose();
        api.Dispose();
#endif
    }
}
