using Trw.Platform;

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

/// <summary>
/// Bots of the next match (practice / inspection): <c>TRW_BOTS=none</c> starts every match without computer players (walk the level alone),
/// <c>TRW_BOTS=ally</c> keeps one bot and puts it on the human players' team (team play on, so it does not attack them). The level inspector's
/// menu (F3) switches it too. Applied by a hook at the end of MatchSetup.StartMatch (tools/mips2cs/hooks.txt, after the player records are set
/// and the rules applied); the game's own choice when unset. Changes guest RAM: never on in netplay.
/// </summary>
public enum BotMode { Game, None, Ally }

public static partial class Game
{
    public static BotMode Bots = (Environment.GetEnvironmentVariable("TRW_BOTS") ?? "").ToLowerInvariant() switch { "none" or "0" => BotMode.None, "ally" or "1" => BotMode.Ally, _ => BotMode.Game };

    private const uint PlayerRecords = Gv.Settings + 0xD0;      // 8 x 150 bytes: +0x78 IsActive, +0x91 IsBot, +0x92 TeamId (0xFF none)

    public static void trw_match_bots()
    {
        if (Bots == BotMode.Game || Net.NetSession.Active || Mem.S32(Gv.Demo_Active) != 0) return;      // the attract demo starts its matches through here too (AttractDemo.Setup sets Demo_Active first)
        int kept = 0;
        for (int i = 0; i < 8; i++)
        {
            uint r = PlayerRecords + (uint)(i * 150);
            if (Mem.U8(r + 0x78) != 1) continue;
            if (Mem.U8(r + 0x91) == 0) { if (Bots == BotMode.Ally) Mem.Set(r + 0x92, (byte)0); continue; }       // humans: team 0
            if (Bots == BotMode.Ally && kept == 0) { kept++; Mem.Set(r + 0x92, (byte)0); continue; }
            Mem.Set(r + 0x78, (byte)0);
        }
        if (Bots == BotMode.Ally) Mem.Set(Gv.TeamPlayEnabled, 1);
        Console.Error.WriteLine($"[bots] match starts with {(Bots == BotMode.None ? "no bots" : kept > 0 ? "one bot on the players' team" : "no bot to keep (none chosen)")}");
    }
}
