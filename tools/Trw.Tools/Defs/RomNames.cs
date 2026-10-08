using System.Text;
using System.Text.RegularExpressions;
using Trw.Tools.Levels;

namespace Trw.Tools.Defs;

/// <summary>
/// Name tables compiled into the game's .data (verified: u32 pointer tables of 0x800Dxxxx KSEG0 addresses; main-image data VA = ROM - 0xC00 + 0x80000000).
/// The pointer tables sit at ROM 0xD7C70..0xD8300; the tables are indexed by game-internal ids (weapon index, pickup index, ...).
/// Which table is which is deduced from the strings; indices are verified for weapons (actor+0x62E indexes the 22-entry weapon tables in
/// func_00209E80, and the bot table names line up with the 22 weapon names).
/// </summary>
static class RomNames
{
    public static List<string> ReadPointerTable(byte[] rom, int romOff, int count)
    {
        var l = new List<string>();
        for (int i = 0; i < count; i++) l.Add(J.StringAt(rom, Bin.U32(rom, romOff + 4 * i)) ?? "");
        return l;
    }

    static readonly (int Rom, string Name, string Note)[] Known =
    [
        (0xD7C70, "pickupAndEventMessages", "health, life force, powerups, flashlight, eagle feathers, nuke part, flag events; index = pickup/event id (hypothesis)"),
        (0xD7CEC, "weaponNames", "indices 0..21 = the 22 weapons of the bot table (verified), 22..25 = attract/repulse messages"),
        (0xD7D54, "powerUpMessages", "multiplayer power-up pickups (Slow Time ... Shield)"),
        (0xD7D94, "teamAndFragMessages", "team join messages, death messages, embryo messages"),
        (0xD7DF0, "ammoMessages", "ammo type names and pickup labels"),
        (0xD7E2C, "rankNames", "multiplayer rank titles (19)"),
        (0xD7E7C, "characterNames", "<EMPTY>, <NEW PLAYER>, then 45 names (TUROK .. WARCLUBS, TUROK repeated)"),
        (0xD7F38, "enemyDisplayNames", "names used by 'defeat %s' objectives"),
        (0xD7F78, "objectiveFormats", "format strings for achievement/objective text"),
        (0xD7FC8, "achievementNames", "ACHIEVEMENT ON <level> x36 and multiplayer achievements"),
        (0xD8090, "settingsAndModeLabels", "difficulty/option labels, mode abbreviations CTF TBL BL FT, GAUNTLET, FRAG FEST, TIME TRIAL"),
        (0xD8110, "acquireMessages", "ACQUIRED ... pickup messages (talismans, weapons, upgrades)"),
        (0xD81A4, "uiMisc", "misc UI strings"),
    ];

    public static object Load(byte[] rom)
    {
        var tables = new List<object>();
        for (int k = 0; k < Known.Length; k++)
        {
            int start = Known[k].Rom;
            int end = k + 1 < Known.Length ? Known[k + 1].Rom : 0xD8300;
            var entries = new List<object>();
            for (int o = start; o < end; o += 4)
            {
                uint p = Bin.U32(rom, o);
                var s = J.StringAt(rom, p);
                entries.Add(new { index = (o - start) / 4, ptr = J.H8(p), text = s });
            }
            tables.Add(new { name = Known[k].Name, rom = "0x" + start.ToString("X6"), va = J.H8(J.RomToVa(start)), note = Known[k].Note, count = entries.Count, entries });
        }
        return new { note = "pointer tables of C strings in the main image .data; VA = ROM - 0xC00 + 0x80000000", tables };
    }
}

/// <summary>Game mode related strings and the per-level mode token parsed from the original map file name (section 18 item 0).</summary>
static class GameModes
{
    public static object Load(RomIndex ix)
    {
        var rom = ix.Rom;
        // Direct (non-pointer) menu strings between ROM 0xD6F80 and 0xD7800.
        var menu = new List<object>();
        var levelNames = new List<string>();
        var textureSets = new List<string>();
        var weaponToggles = new List<string>();
        int pos = 0xD6F80;
        while (pos < 0xD7800)
        {
            if (rom[pos] < 0x20 || rom[pos] > 0x7E) { pos++; continue; }
            int e = pos;
            while (e < rom.Length && rom[e] >= 0x20 && rom[e] <= 0x7E) e++;
            string s = Encoding.ASCII.GetString(rom, pos, e - pos);
            if (s.Length >= 3)
            {
                menu.Add(new { rom = "0x" + pos.ToString("X6"), text = s });
                if (s.StartsWith("level:")) levelNames.Add(s[6..].Trim());
                else if (s.StartsWith("texture set:")) textureSets.Add(s[12..].Trim());
                else if (Regex.IsMatch(s, @"^[a-z ]+ *: ?(on|off)$") && !s.Contains("crosshairs")) weaponToggles.Add(s);
            }
            pos = e + 1;
        }

        var meta = LevelCatalog.LoadMeta(ix);
        var perLevel = meta.Select(m => new
        {
            levelId = m.LevelId,
            menuSlot = m.Slot,
            mapName = m.MapName,
            modeToken = ModeToken(m.MapName),
            playersGuess = Regex.Match(m.MapName, @"(\d)\s?[pP][lL]?\b").Success ? int.Parse(Regex.Match(m.MapName, @"(\d)\s?[pP][lL]?\b").Groups[1].Value) : (int?)null,
        }).ToList();

        return new
        {
            note = "Mode token is parsed from the original dev map file name (hypothesis): BL=Blood Lust (deathmatch), TBL=Team Blood Lust, CTF=capture the flag, FT=frag tag, BOSS=boss/campaign arena.",
            multiplayerModes = new[] { "deathmatch (BL / blood lust)", "team blood (TBL)", "capture the flag (CTF)", "frag tag (FT)" },
            singlePlayerModes = new[] { "campaign / levels (BLLevelData progression)", "GAUNTLET", "FRAG FEST", "TIME TRIAL", "training: archery, shooting, decathalon" },
            multiplayerLevelNamesFromMenu = levelNames,
            textureSetsFromMenu = textureSets,
            weaponTogglesFromMenu = weaponToggles,
            perLevel,
            menuStrings = menu,
        };
    }

    static string ModeToken(string map)
    {
        var u = map.ToUpperInvariant();
        foreach (var t in Regex.Split(u, @"[^A-Z0-9]+"))
        {
            var m = Regex.Match(t, @"^(TBL|CTF|FTG|FT|BL)(\d?P?L?)$");
            if (m.Success) return m.Groups[1].Value == "FTG" ? "FT" : m.Groups[1].Value;
        }
        return u.Contains("BOSS") ? "BOSS" : "?";
    }
}
