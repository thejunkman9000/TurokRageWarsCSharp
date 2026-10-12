namespace Trw.Game.Net;

/// <summary>
/// What the rest of the front end may ask about the online session (read-only for everybody but <see cref="NetPlay"/>; safe to read from the
/// window thread at any time, values change between ticks). Offline, <see cref="Active"/> is false, <see cref="LocalPad"/> is 0 and the
/// arrays hold empty names / -1.
/// </summary>
public static class NetSession
{
    /// <summary>True from the moment a netplay match has booted until the process ends (also after a desync or when the host left).</summary>
    public static bool Active { get; internal set; }
    /// <summary>The pad this machine's player drives: 0 = host (player 1), joiners 1..3 in join order.</summary>
    public static int LocalPad { get; internal set; }
    /// <summary>Players in the match (pads 0..PlayerCount-1 are occupied).</summary>
    public static int PlayerCount { get; internal set; } = 1;
    /// <summary>Account display name per pad ("" = no player on that pad).</summary>
    public static readonly string[] PlayerNames = { "", "", "", "" };
    /// <summary>Round trip between the host and that pad's machine in ms (host's own pad: 0; -1 = unknown or gone). Measured by the host once a second.</summary>
    public static readonly int[] PingMs = { -1, -1, -1, -1 };
    /// <summary>The player of that pad has left; their pad reads "no input" (it stays plugged in, so the game does not pause).</summary>
    public static readonly bool[] Dropped = new bool[4];
    /// <summary>The lockstep tick last run (1-based).</summary>
    public static int Tick { get; internal set; }
    /// <summary>A one-line status for the players: "" normally, else e.g. "Waiting for Bob", "DESYNC at tick ...", "The host ended the session".</summary>
    public static string Status { get; internal set; } = "";
    /// <summary>True once a desync was detected (Status says where).</summary>
    public static bool Desynced { get; internal set; }
}
