namespace Trw.Game;

/// <summary>
/// Test switches the game code calls through tools/overrides (never active unless their environment variable is set).
/// <c>TRW_ARENA=n[tT]</c>: every match / trial uses arena (map) n, and with <c>tT</c> the arena type T (the C port's TUROK_ARENA: the game picks arenas at random,
/// so map-specific bugs need a fixed map); each pick is logged as <c>[arena]</c>.
/// n is the game's internal level number, not the menu order: Dire Straits 7, Grim Retreat 3, Warehouse 9, Water Temple 10, Earth Temple 13, Courtyard 34.
/// The pick that counts is the last one before the match starts (the menus call the setter while browsing). The main-menu attract demo picks its own
/// arena (AttractDemo.Setup, one of 0 / 3 / 27 / 1); the pin covers it too (its type stays 2), so headless runs can visit any map.
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
        Console.Error.WriteLine($"[arena] game chose arena {index} {(type < 0 ? "for the attract demo" : $"type {type}")}{(pinArena >= 0 ? $" -> pinned to {pinArena}" + (pinType >= 0 && type >= 0 ? $" type {pinType}" : "") : "")}");
        return pinArena >= 0 ? pinArena : index;
    }

    public static int trw_arena_type(int type) => pinArena >= 0 && pinType >= 0 ? pinType : type;
}
