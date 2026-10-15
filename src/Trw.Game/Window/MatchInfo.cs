using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// Read-only view of the game's match data in guest RAM, for the presentation layer (local view, scoreboard). Never writes guest memory.
/// Layout (decompiled C: View_SetupViewport per-view setup, Hud_DrawPlayer HUD, Match_IsFragLimitReached frag-limit check, results-screen formatters Results_FormatStats_00443B08..CAC):
/// <list type="bullet">
/// <item>player actors: list head <c>D_80145060</c>, next pointer at +0x16E0, the score record pointer at +0x5D8 and the controller object at +0x698
/// (controller objects are 0x224 bytes from 0x8010F328: port = (ptr - 0x8010F328) / 0x224; bots have none).</item>
/// <item>cameras (the split-screen views): see <see cref="Views"/>.</item>
/// <item>score records: <c>D_80146398</c> = <c>D_801462C8</c> + 0xD0, 8 records of 0x96 bytes; s16 +0x04 frags (the frag limit compares it), s16 +0x02 deaths (killed by another player: the results screen's DEATHS column), s16 +0x00 suicides (a suicide: +0x00 +1 and frags -1), see <see cref="Fields"/>.</item>
/// </list>
/// </summary>
internal static class MatchInfo
{
    public const uint PlayerListHead = 0x80145060, RosterBase = 0x80146398, RosterStride = 0x96, ControllerBase = 0x8010F328, ControllerStride = 0x224;
    public const uint ActNext = 0x16E0, ActStats = 0x5D8, ActPad = 0x698;
    /// <summary>Offsets in a score record (s16 unless noted). Frags: compared with the frag limit (Match_IsFragLimitReached) and shown by the HUD (Hud_DrawPlayer).</summary>
    public static class Fields { public const uint Frags = 0x04, Deaths = 0x02, Suicides = 0x00, Name = 0x84 /* char[11] */, Team = 0x92 /* u8 */; }

    public struct Player
    {
        public int Index;          // order in the game's player list
        public int Port;           // controller port 0..3, -1 = bot / no controller
        public uint Actor, Stats;
        public int Frags, Deaths, Suicides, Team, Record;
        public string Name;
    }

    private static bool Ram(uint a, int n) => a >= 0x80000000u && a + (uint)n <= 0x80800000u && Mem.IsMapped(a) && Mem.IsMapped(a + (uint)n - 1);

    /// <summary>The players of the running match (empty outside a level). Safe to call from any thread; values may be a frame old.</summary>
    public static List<Player> Players()
    {
        var list = new List<Player>();
        try
        {
            uint a = Mem.U32(PlayerListHead);
            for (int i = 0; i < 8 && a != 0 && Ram(a, (int)ActNext + 4); i++, a = Mem.U32(a + ActNext))
            {
                var p = new Player { Index = i, Actor = a, Port = -1, Record = -1, Name = "" };
                uint pad = Mem.U32(a + ActPad);
                if (pad >= ControllerBase && pad < ControllerBase + 4 * ControllerStride && (pad - ControllerBase) % ControllerStride == 0) p.Port = (int)((pad - ControllerBase) / ControllerStride);
                uint st = Mem.U32(a + ActStats);
                if (st >= RosterBase && st < RosterBase + 8 * RosterStride && Ram(st, (int)RosterStride))
                {
                    p.Stats = st; p.Record = (int)((st - RosterBase) / RosterStride);
                    p.Frags = Mem.S16(st + Fields.Frags); p.Deaths = Mem.S16(st + Fields.Deaths); p.Suicides = Mem.S16(st + Fields.Suicides);
                    p.Team = Mem.U8(st + Fields.Team);
                    var name = new char[11]; int n = 0;
                    for (; n < 11; n++) { byte c = Mem.U8(st + Fields.Name + (uint)n); if (c == 0) break; name[n] = c >= 32 && c < 127 ? (char)c : '?'; }
                    p.Name = new string(name, 0, n).Trim();
                }
                list.Add(p);
            }
        }
        catch (Mem.GuestFault) { }
        catch (IndexOutOfRangeException) { }
        return list;
    }

    /// <summary>The cameras of the match (the game's split-screen views), in drawing order: list head at 0x801450A8 (D_80145088 + 0x20, walked by
    /// View_UpdateAll, which runs the per-view setup View_SetupViewport for each), next pointer at +4, view rectangle f32 width/height/x/y at +0x29C.</summary>
    public static List<(uint Cam, float X, float Y, float W, float H)> Views()
    {
        var list = new List<(uint, float, float, float, float)>();
        try
        {
            uint c = Mem.U32(CameraListHead);
            for (int i = 0; i < 8 && c != 0 && Ram(c, 0x2B0); i++, c = Mem.U32(c + 4))
                list.Add((c, Mem.F32(c + 0x2A4), Mem.F32(c + 0x2A8), Mem.F32(c + 0x29C), Mem.F32(c + 0x2A0)));
        }
        catch (Mem.GuestFault) { }
        return list;
    }
    public const uint CameraListHead = 0x801450A8;

    /// <summary>The view rectangle of controller port <paramref name="port"/>: the cameras are created for the human players in port order
    /// (port 0's camera first), so the n-th camera belongs to the n-th human player by port.</summary>
    public static bool ViewOf(int port, List<Player> players, out (float X, float Y, float W, float H) rect)
    {
        rect = default;
        var humans = players.Where(p => p.Port >= 0).Select(p => p.Port).Distinct().OrderBy(p => p).ToList();
        int n = humans.IndexOf(port);
        var views = Views();
        if (n < 0 || n >= views.Count) return false;
        var v = views[n];
        rect = (v.X, v.Y, v.W, v.H);
        return true;
    }

    /// <summary>A match is running: game state 13 (playing) and the HUD layer on top of the UI stack (screen 2, or -1 = no screen), with players in the list.</summary>
    public static bool InMatch => Game.StateId == 13 && (Game.ScreenId == 2 || Game.ScreenId == -1);

    // ---- debugging: TRW_MATCH_LOG=1 prints the player list every 30 s of a match and the raw score records whenever one changes
    private static readonly bool log = Environment.GetEnvironmentVariable("TRW_MATCH_LOG") == "1";
    private static int lastLog = -100000, lastCheck;
    private static string lastRecords = "";
    /// <summary>Incremented whenever a score record changes during a match (debug aid: TRW_SCORE_SHOT_DIR saves a screenshot then).</summary>
    public static volatile int ScoreChanges;
    private static readonly bool watchScores = log || Environment.GetEnvironmentVariable("TRW_SCORE_SHOT_DIR") != null;

    public static void DebugTick()
    {
        if (!watchScores || !InMatch || Game.CurrentTick - lastCheck < 30) return;
        lastCheck = Game.CurrentTick;
        var ps = Players();
        var sb = new System.Text.StringBuilder();
        foreach (var p in ps)
            if (p.Stats != 0) { sb.Append($"[match]   rec{p.Record}:"); for (uint k = 0; k < 0x10; k += 2) sb.Append($" {(ushort)Mem.S16(p.Stats + k):X4}"); sb.AppendLine(); }
        var rec = sb.ToString();
        bool changed = rec != lastRecords;
        if (changed && lastRecords.Length > 0 && rec.Length == lastRecords.Length) ScoreChanges++;
        if (log && (changed || Game.CurrentTick - lastLog >= 1800))
        {
            lastLog = Game.CurrentTick;
            Console.Error.WriteLine($"[match] tick {Game.CurrentTick}: {ps.Count} players{(changed ? " (score records changed)" : "")}; views: {string.Join(" ", Views().Select(v => $"[{v.X},{v.Y} {v.W}x{v.H}]"))}");
            foreach (var p in ps)
                Console.Error.WriteLine($"[match]   #{p.Index} actor {p.Actor:X8} port {p.Port} record {p.Record} '{p.Name}' frags {p.Frags} deaths {p.Deaths} suicides {p.Suicides} team {p.Team}");
            Console.Error.Write(rec);
        }
        lastRecords = rec;
    }

    private static int SafeS32(uint a) { try { return Mem.S32(a); } catch (Mem.GuestFault) { return int.MinValue; } }
}
