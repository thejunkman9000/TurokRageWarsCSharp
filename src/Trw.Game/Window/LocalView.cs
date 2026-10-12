using Trw.Game.Net;

namespace Trw.Game;

/// <summary>
/// Who "this machine" is in a multiplayer match, and how the match is presented. The single coupling point between the presentation features
/// (local view, scoreboard) and netplay: everything netplay-specific they need is read here from <see cref="NetSession"/>.
/// <list type="bullet">
/// <item><c>TRW_VIEW=local|split</c> (default split): the initial view mode; F2 toggles it while the window runs.</item>
/// <item><c>TRW_LOCAL_PAD=0..3</c> (default 0): the controller port of the local player when netplay is off (netplay: <see cref="NetSession.LocalPad"/>).</item>
/// <item><c>TRW_LOCAL_ASPECT=fit|stretch</c> (default fit): the local view keeps the view's own aspect (2-player halves are 8:3) and is letterboxed;
/// <c>stretch</c> fills the window and distorts.</item>
/// </list>
/// Purely presentation: none of this reaches the game's memory or input.
/// </summary>
public static class LocalView
{
    /// <summary>Show only the local player's view (true) or the game's split screen (false).</summary>
    public static volatile bool Enabled = string.Equals(Environment.GetEnvironmentVariable("TRW_VIEW"), "local", StringComparison.OrdinalIgnoreCase);

    private static readonly int envPad = int.TryParse(Environment.GetEnvironmentVariable("TRW_LOCAL_PAD"), out var p) && p >= 0 && p <= 3 ? p : 0;

    /// <summary>Controller port (0..3) of the player sitting at this machine.</summary>
    public static int Pad => NetSession.Active ? Math.Clamp(NetSession.LocalPad, 0, 3) : envPad;

    /// <summary>Letterbox (false) or stretch (true) the local view to the window.</summary>
    public static readonly bool Stretch = string.Equals(Environment.GetEnvironmentVariable("TRW_LOCAL_ASPECT"), "stretch", StringComparison.OrdinalIgnoreCase);

    /// <summary>Netplay is running: the scoreboard then shows ping and connection status.</summary>
    public static bool NetActive => NetSession.Active;

    /// <summary>Display name of the player on <paramref name="port"/> (the netplay account name), or null to use the game's own name.</summary>
    public static string? Name(int port) => NetSession.Active && port is >= 0 and < 4 && !string.IsNullOrWhiteSpace(NetSession.PlayerNames[port]) ? NetSession.PlayerNames[port] : null;

    /// <summary>Round trip to that port's machine in ms, -1 = unknown.</summary>
    public static int PingMs(int port) => NetSession.Active && port is >= 0 and < 4 ? NetSession.PingMs[port] : -1;

    /// <summary>Connection status of that port's player ("OK", "LEFT", "DESYNC"), null when netplay is off.</summary>
    public static string? Connection(int port)
    {
        if (!NetSession.Active || port is < 0 or > 3) return null;
        if (NetSession.Dropped[port]) return "LEFT";
        if (NetSession.Desynced) return "DESYNC";
        return port < NetSession.PlayerCount ? "OK" : null;
    }
}
