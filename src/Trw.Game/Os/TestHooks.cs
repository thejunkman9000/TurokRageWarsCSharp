namespace Trw.Game;

/// <summary>
/// Test switches the game code calls through tools/overrides (never active unless their environment variable is set).
/// <c>TRW_ARENA=n[tT]</c>: every match / trial uses arena (map) n, and with <c>tT</c> the arena type T (the C port's TUROK_ARENA: the game picks arenas at random,
/// so map-specific bugs need a fixed map); each pick is logged as <c>[arena]</c>. The menu's Water Temple is <c>10t3</c>.
/// </summary>
public static partial class Game
{
    private static readonly int pinArena = ArenaEnv(0), pinType = ArenaEnv(1);
    private static int ArenaEnv(int part)
    {
        var parts = (Environment.GetEnvironmentVariable("TRW_ARENA") ?? "").ToLowerInvariant().Split('t');
        return parts.Length > part && int.TryParse(parts[part], out var v) ? v : -1;
    }

    public static int trw_arena_index(int type, int index)
    {
        Console.Error.WriteLine($"[arena] game chose arena {index} type {type}{(pinArena >= 0 ? $" -> pinned to {pinArena}" + (pinType >= 0 ? $" type {pinType}" : "") : "")}");
        return pinArena >= 0 ? pinArena : index;
    }

    public static int trw_arena_type(int type) => pinArena >= 0 && pinType >= 0 ? pinType : type;
}
