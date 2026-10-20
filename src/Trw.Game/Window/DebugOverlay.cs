using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Trw.Platform;

namespace Trw.Game;

/// <summary>
/// The level inspector: F3 opens a menu that shows the level's "editor objects" in 3D over the picture, each with a marker and a label: player starts,
/// spawn / warp points, placed entities by class (item spawners, objectives, platforms, turrets, switches, destructibles, animated props ...), the AI
/// navigation graph (nodes, links, each bot's planned route), level paths, triggers (source -> target), the placed pickups, props and light volumes of
/// the loaded grid cells, the live actors, pickups, players, point lights and loop sounds, and the collision mesh near the camera.
/// Everything is read from guest RAM while a level is loaded (game state 8 / 13, Level LoadState 4) and projected with each view camera's own
/// world -> clip matrix (camera +0x1E0, the matrix the RSP gets) and viewport, then through the presentation (widescreen / local view) of
/// <see cref="GfxRenderer.MapPerspectivePoint"/>. Read only: nothing is written to guest memory, so it changes nothing in the game (netplay safe).
/// Keys while the menu is open: Up / Down choose, Enter / Space / Left / Right switch (Left / Right change the range on the range line), F3 closes the menu
/// (the markers stay); the arrows, Enter and Space then do not reach the game. Layouts:
/// docs/LEVEL_OBJECTS.md.
/// </summary>
public sealed class DebugOverlay : IDisposable
{
    private readonly SpriteBatch? batch;
    private readonly SpriteFont? font;
    private readonly Texture2D? white;
    private readonly GfxRenderer? renderer;

    private enum Cat { Starts, Spawns, ItemSpawners, Objectives, OtherEntities, NavNodes, NavLinks, BotRoutes, Paths, Triggers, CellPickups, Props, LightVolumes,
                       Players, Actors, Pickups, PointLights, Sounds, Collision }

    private sealed record CatInfo(Cat Id, string Name, Color Color, bool On);
    private static readonly CatInfo[] Cats =
    {
        new(Cat.Starts, "PLAYER STARTS", new Color(80, 255, 80), true),
        new(Cat.Spawns, "SPAWN / WARP POINTS", new Color(140, 255, 200), true),
        new(Cat.ItemSpawners, "ITEM SPAWNERS", new Color(255, 220, 60), true),
        new(Cat.Objectives, "OBJECTIVES (FLAGS, TAG, ESCAPE)", new Color(255, 80, 200), true),
        new(Cat.OtherEntities, "OTHER ENTITIES", new Color(255, 150, 60), true),
        new(Cat.NavNodes, "AI NAV NODES", new Color(80, 170, 255), false),
        new(Cat.NavLinks, "AI NAV LINKS", new Color(60, 120, 220), false),
        new(Cat.BotRoutes, "BOT ROUTES", new Color(0, 255, 255), false),
        new(Cat.Paths, "LEVEL PATHS", new Color(200, 140, 255), false),
        new(Cat.Triggers, "TRIGGERS", new Color(255, 60, 60), false),
        new(Cat.CellPickups, "PLACED PICKUPS", new Color(255, 255, 140), false),
        new(Cat.Props, "PROPS", new Color(170, 170, 170), false),
        new(Cat.LightVolumes, "LIGHT VOLUMES", new Color(255, 240, 200), false),
        new(Cat.Players, "PLAYERS", new Color(255, 255, 255), false),
        new(Cat.Actors, "LIVE ACTORS", new Color(255, 110, 110), false),
        new(Cat.Pickups, "LIVE PICKUPS", new Color(230, 230, 90), false),
        new(Cat.PointLights, "POINT LIGHTS", new Color(255, 200, 120), false),
        new(Cat.Sounds, "LOOP SOUNDS", new Color(120, 255, 120), false),
        new(Cat.Collision, "COLLISION FACES", new Color(90, 200, 90), false),
    };
    private readonly bool[] on = Cats.Select(c => c.On).ToArray();
    private readonly int[] counts = new int[Cats.Length];
    private bool labels = true;
    private static readonly float[] Ranges = { 512, 1024, 2048, 4096, 8192, float.PositiveInfinity };
    private int range = 3;

    private static bool menuOpen, active;
    private int cursor;
    private KeyboardState prevKs;

    private const int MenuLines = 3;      // after the categories: labels, range, bots of the next match
    private int ItemCount => Cats.Length + MenuLines;

    private DebugOverlay() { Array.Fill(on, true); }      // Probe: no graphics

    /// <summary>TRW_INSPECT_AT=&lt;tick&gt; (headless runs too): at that retrace, prints what the inspector finds (counts per category, a few labels each).</summary>
    public static readonly int ProbeAt = int.TryParse(Environment.GetEnvironmentVariable("TRW_INSPECT_AT"), out var pa) ? pa : -1;
    public static void Probe()
    {
        var o = new DebugOverlay();
        var sb = new System.Text.StringBuilder($"[inspect] tick {Game.CurrentTick}: level on screen {LevelReady()}, state {Mem.S32(Gv.GameState)}, screen {Game.ScreenId}, load state {Mem.S32(Gv.LevelMgr)}\n");
        try
        {
            var views = Views();
            o.Collect();
            if (views.Count > 0) Try(() => o.Collision(Gv.LevelMgr, views[0].Eye, 4096));
            uint C = Mem.U32(Gv.LevelMgr + 0x6C);
            if (Ram(C))
            {
                sb.Append($"  collision table {C:X8}: chunks {Mem.S32(C)}");
                for (int k = 0; k < Math.Min(6, Mem.S32(C)); k++) { uint ch = Chunk(C, k); sb.Append($" | {k}: {ch:X8} words {Mem.S32(ch):X} {Mem.S32(ch + 4):X} {Mem.S32(ch + 8):X} {Mem.S32(ch + 12):X}"); }
                sb.Append('\n');
            }
            sb.Append($"  views {views.Count}: " + string.Join(", ", views.Select(v => $"cam {v.Cam:X8} eye {v.Eye} vp {v.Vx},{v.Vy} {v.Vw}x{v.Vh}")) + "\n");
            for (int i = 0; i < Cats.Length; i++)
            {
                var ms = o.marks.Where(m => m.Cat == Cats[i].Id).Take(4).Select(m => $"{m.Label} @ {m.P.X:0},{m.P.Y:0},{m.P.Z:0}");
                sb.Append($"  {Cats[i].Name}: {o.counts[i]} (lines {o.lines.Count(l => l.Cat == Cats[i].Id)}) {string.Join(" | ", ms)}\n");
            }
        }
        catch (Exception e) { sb.Append($"  failed: {e.GetType().Name} {e.Message}\n"); }
        Console.Error.Write(sb.ToString());
    }

    public DebugOverlay(GraphicsDevice device, SpriteFont? font, GfxRenderer renderer)
    {
        this.font = font;
        this.renderer = renderer;
        batch = new SpriteBatch(device);
        white = new Texture2D(device, 1, 1);
        white.SetData(new[] { Color.White });
        // TRW_INSPECTOR=1: markers on from the start (menu closed); =all: every category; =menu: with the menu open; =only:Cat,Cat (unattended screenshots)
        if (Environment.GetEnvironmentVariable("TRW_INSPECTOR") is { Length: > 0 } ins)
        {
            active = true; menuOpen = ins == "menu";
            if (ins == "all") Array.Fill(on, true);
            if (ins.StartsWith("only:"))           // only:Collision,NavNodes (Cat names)
            {
                Array.Fill(on, false);
                foreach (var n in ins[5..].Split(',')) if (Enum.TryParse<Cat>(n.Trim(), true, out var c)) on[(int)c] = true;
            }
        }
    }

    /// <summary>The menu takes these keys while it is open (TrwWindow keeps them from the game's key bindings).</summary>
    public static bool Swallows(Keys k) => menuOpen && k is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter or Keys.Space;

    public void Update(KeyboardState ks, bool focused)
    {
        bool Pressed(Keys k) => focused && ks.IsKeyDown(k) && !prevKs.IsKeyDown(k);
        if (Pressed(Keys.F3))
        {
            if (!active) { active = menuOpen = true; }
            else if (menuOpen) menuOpen = false;
            else menuOpen = true;
            Scoreboard.Toast(menuOpen ? "LEVEL INSPECTOR (F3 CLOSES THE MENU)" : "LEVEL INSPECTOR MENU CLOSED");
        }
        if (menuOpen)
        {
            if (Pressed(Keys.Up)) cursor = (cursor + ItemCount - 1) % ItemCount;
            if (Pressed(Keys.Down)) cursor = (cursor + 1) % ItemCount;
            bool toggle = Pressed(Keys.Enter) || Pressed(Keys.Space), left = Pressed(Keys.Left), right = Pressed(Keys.Right);
            if (toggle || left || right)
            {
                if (cursor < Cats.Length) on[cursor] = !on[cursor];
                else if (cursor == Cats.Length) labels = !labels;
                else if (cursor == Cats.Length + 1) range = (range + (left ? Ranges.Length - 1 : 1)) % Ranges.Length;
                else Game.Bots = (BotMode)(((int)Game.Bots + (left ? 2 : 1)) % 3);      // Os/TestHooks.cs, applied when the next match starts
            }
            if (focused && ks.IsKeyDown(Keys.Escape)) menuOpen = false;
        }
        prevKs = ks;
    }

    // ------------------------------------------------------------------------------------------------------------ guest reads

    private static bool Ram(uint a) => a >= 0x80000400 && a < Gv.Ram_End;
    private static float F(uint a) => Mem.F32(a);
    private static Vector3 V3(uint a) => new(Mem.F32(a), Mem.F32(a + 4), Mem.F32(a + 8));
    private static uint Chunk(uint t, int i) => t + (uint)Mem.S32(t + 4 + (uint)(4 * i));
    private static bool Sane(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && MathF.Abs(v.X) < 1e6f && MathF.Abs(v.Y) < 1e6f && MathF.Abs(v.Z) < 1e6f;

    /// <summary>A level is on screen and stable: playing or the recorded demo without a menu, the Level object fully loaded, its data present.</summary>
    private static bool LevelReady()
    {
        try { return Widescreen.InLevel && Mem.S32(Gv.LevelMgr) == 4 && Mem.S32(Gv.Level_DataLoaded) != 0; }      // InLevel: state 8 / 13 with only the HUD up (the title runs in state 8 over the last demo level)
        catch (Mem.GuestFault) { return false; }
    }

    // def kinds (class table Actor_ClassTable): docs/LEVEL_OBJECTS.md
    private static readonly string[] KindNames = { "OBJECT", "CREATURE", "PLATFORM", "ITEM SPAWNER", "TURRET", "TARGET", "SWITCH", "DESTRUCTIBLE", "DROP", "ANIM PROP",
                                                   "OBJECTIVE", "PLAYER", "PICKUP", "ARENA VARIANT", "MARKER" };
    private static string KindName(int k) => k >= 0 && k < KindNames.Length ? KindNames[k] : $"KIND {k}";

    // type ids (Actor +0xE4, item types): the game's own enum. The ROM keeps the assembler symbol listing of the original build (file offsets
    // 0x12A000..0x160000: records "<len><NAME>equ<s32 little endian>", len = name length + 3), with the AI_OBJECT_* type enum; a few names are
    // corrected from the pickup tables (Items_HealthTable gives 0x6BB 50 health, not "full"), the rest come from the listing
    private static readonly Dictionary<int, string> Curated = new()
    {
        [0x40B] = "TURRET", [0x40C] = "WALL TURRET", [0x44E] = "BOSS MOTHER", [0x451] = "BOSS QUEEN",
        [0x453] = "BOSS PRIMAGEN", [0x460] = "DM PLAYER", [0x461] = "DM RAPTOR", [0x462] = "DM MITE",
        [0x463] = "DM MONKEY", [0x642] = "MOVING PLATFORM", [0x643] = "ITEM SPAWNER", [0x644] = "ITEM SPAWNER (HIDDEN)",
        [0x64E] = "MONKEY ESCAPE POINT", [0x64F] = "TAKE COVER POINT", [0x653] = "FLAG BASE A", [0x654] = "FLAG BASE B",
        [0x655] = "FLAG BASE C", [0x656] = "FLAG BASE D", [0x6A5] = "2 HEALTH", [0x6A6] = "10 HEALTH",
        [0x6A7] = "FULL HEALTH", [0x6A8] = "ULTRA HEALTH", [0x6A9] = "1 LIFE FORCE", [0x6AA] = "10 LIFE FORCE",
        [0x6B8] = "10 HEALTH (MP)", [0x6B9] = "15 HEALTH", [0x6BA] = "25 HEALTH", [0x6BB] = "50 HEALTH",
        [0x708] = "INVINCIBILITY", [0x709] = "PLANE WALKER", [0x70A] = "FLASHLIGHT", [0x70B] = "FLASHLIGHT BATTERY",
        [0x70F] = "INFRARED GOGGLES", [0x84B] = "CEREBRAL BORE", [0x84C] = "PFM MINE", [0x84D] = "IRON CLAW",
        [0x84F] = "SENTRY TURRET", [0xBC4] = "ENERGY AMMO", [0xBC5] = "ENERGY AMMO PACK", [0xBC6] = "BULLETS",
        [0xBC7] = "BOX OF BULLETS", [0xBC8] = "EXPLOSIVES", [0xBC9] = "EXPLOSIVES CRATE", [0xBD6] = "MONKEY (RETALIATOR)",
        [0xBD7] = "CTF FLAG", [0xBD8] = "RANDOM POWER-UP", [0xBD9] = "AMMO BACKPACK 1A", [0xBDA] = "AMMO BACKPACK 1B",
        [0xBDB] = "AMMO BACKPACK 2A", [0xBDC] = "AMMO BACKPACK 2B", [0xBDD] = "AMMO BACKPACK 3A", [0xBDE] = "AMMO BACKPACK 3B",
        [0xBDF] = "AMMO BACKPACK 4A", [0xBE0] = "AMMO BACKPACK 4B", [0xBE1] = "AMMO BACKPACK 5A", [0xBE2] = "AMMO BACKPACK 5B",
        [0xBE3] = "AMMO BACKPACK 6A", [0xBE4] = "AMMO BACKPACK 6B", [0xBE5] = "AMMO BACKPACK 7A", [0xBE6] = "AMMO BACKPACK 7B",
        [0xBE7] = "AMMO BACKPACK 8A", [0xBE8] = "AMMO BACKPACK 8B", [0x1388] = "TRIAL REWARD (PLACEHOLDER)",
    };
    private static readonly Lazy<Dictionary<int, string>> RomTypes = new(ReadTypeEnum);
    private static Dictionary<int, string> ReadTypeEnum()
    {
        var d = new Dictionary<int, string>();
        try
        {
            var rom = Host.Rom;
            var key = "AI_OBJECT_"u8;
            int lo = 0x12A000, hi = Math.Min(rom.Length - 8, 0x160000);
            for (int i = lo; i < hi; i++)
            {
                if (rom[i] != (byte)'A' || !rom.AsSpan(i, key.Length).SequenceEqual(key)) continue;
                int len = rom[i - 1] - 3;                                          // the record's length byte counts "equ"
                if (len <= key.Length || i + len + 7 > rom.Length || rom[i + len] != (byte)'e' || rom[i + len + 1] != (byte)'q' || rom[i + len + 2] != (byte)'u') continue;
                string name = System.Text.Encoding.ASCII.GetString(rom, i + key.Length, len - key.Length);
                int v = BitConverter.ToInt32(rom, i + len + 3);
                if (name.EndsWith("_START") || name.EndsWith("_END") || v < 0 || v > 0xFFFF) continue;
                d.TryAdd(v, Pretty(name));
                i += len;
            }
        }
        catch (Exception) { }
        return d;
    }
    private static string Pretty(string n)
    {
        foreach (var pre in new[] { "PICKUP_", "DEVICE_ACTION_", "DEVICE_", "CHARACTER_" }) if (n.StartsWith(pre)) { n = n[pre.Length..]; break; }
        return n.Replace("MPLAYER_", "").Replace("BL_MP_", "").Replace("BL_", "").Replace("MP_", "").Replace('_', ' ');
    }
    private static string TypeName(int id) => Curated.TryGetValue(id, out var c) ? c : RomTypes.Value.TryGetValue(id, out var r) ? r : "";
    private static string TypeLabel(int id) { var n = TypeName(id); return n.Length > 0 ? $"{n} ({id:X3})" : $"TYPE {id:X3}"; }

    // definition pointer -> index in the level's definitions (Level +0x78, a chunk table: chunk i = definition i); rebuilt when the table moves
    private readonly Dictionary<uint, int> defIndex = new();
    private uint defTable;
    private int DefIndex(uint L, uint def)
    {
        uint t = Mem.U32(L + 0x78);
        if (t != defTable) { defIndex.Clear(); defTable = t; if (Ram(t)) { int n = Mem.S32(t); for (int i = 0; i < n && i < 4096; i++) defIndex[Chunk(t, i)] = i; } }
        return defIndex.TryGetValue(def, out var k) ? k : -1;
    }

    private static int ItemTypeId(uint level, int typeIndex)
    {
        uint t = Mem.U32(level + 0x94);
        if (!Ram(t) || typeIndex < 0 || typeIndex >= Mem.S32(t + 4)) return -1;
        return Mem.U16(t + 8 + (uint)(2 * typeIndex));
    }

    // ------------------------------------------------------------------------------------------------------------ the scene

    private struct Mark { public Cat Cat; public Vector3 P; public string Label; public float Size; }
    private struct Line { public Cat Cat; public Vector3 A, B; public bool Arrow; }
    private readonly List<Mark> marks = new();
    private readonly List<Line> lines = new();

    private void Add(Cat c, Vector3 p, string label, float size = 4) { if (on[(int)c] && Sane(p)) marks.Add(new Mark { Cat = c, P = p, Label = label, Size = size }); counts[(int)c]++; }
    private void AddLine(Cat c, Vector3 a, Vector3 b, bool arrow = false) { if (on[(int)c] && Sane(a) && Sane(b)) lines.Add(new Line { Cat = c, A = a, B = b, Arrow = arrow }); }

    private void Collect()
    {
        marks.Clear(); lines.Clear(); Array.Clear(counts);
        uint L = Gv.LevelMgr;
        Try(() => Entities(L));
        Try(() => Spawns(L));
        Try(Nav);
        Try(() => Paths(L));
        Try(() => Cells(L));
        Try(Live);
        Try(Lights);
        Try(Sounds);
    }

    private static void Try(Action a) { try { a(); } catch (Mem.GuestFault) { } catch (IndexOutOfRangeException) { } }

    private void Entities(uint L)
    {
        uint ents = Mem.U32(L + 0x138);
        int n = Mem.S32(L + 0x140), nStart = Mem.S32(L + 0x13C);
        if (!Ram(ents) || n <= 0 || n > 4096) return;
        uint E(int i) => ents + (uint)(i * 0x2E8);
        for (int i = 0; i < n; i++)
        {
            uint e = E(i);
            var p = V3(e + 8);
            uint def = Mem.U32(e + 0x18);
            int kind = Ram(def) ? Mem.S32(def) : -1;
            int typeId = Mem.U16(e + 0xE4);
            if (i < nStart || kind == 11) { Add(Cat.Starts, p, $"PLAYER START {i}"); continue; }
            if (kind == 3)
            {
                int ti = Ram(def) ? Mem.S16(def + 0x28) : -1;
                int item = ti >= 0 ? ItemTypeId(L, ti) : -1;
                Add(Cat.ItemSpawners, p, $"SPAWNER #{i}: " + (item >= 0 ? TypeLabel(item) : "?"));
                continue;
            }
            if (kind == 10 || typeId is 0x64E or >= 0x653 and <= 0x656) { Add(Cat.Objectives, p, $"{TypeLabel(typeId)} #{i}", 6); continue; }
            Add(Cat.OtherEntities, p, $"{KindName(kind)} #{i} {TypeLabel(typeId)}" + (Ram(def) ? $" DEF {DefIndex(L, def)}" : ""));
        }
        // triggers: entity -> target entity (key = source entity index), region -> target (from the region's first face)
        if (on[(int)Cat.Triggers])
        {
            int cnt = Mem.S32(L + 0x11C0);
            uint keys = Mem.U32(L + 0x11C8), recs = Mem.U32(L + 0x11D0);
            if (Ram(keys) && Ram(recs) && cnt > 0 && cnt < 4096)
                for (int k = 0; k < cnt; k++)
                {
                    int src = Mem.S32(keys + (uint)(4 * k)), dst = Mem.U16(recs + (uint)(20 * k) + 4);
                    if (src < 0 || src >= n) continue;
                    var a = V3(E(src) + 8);
                    counts[(int)Cat.Triggers]++;
                    if (dst < n && dst != src) { AddLine(Cat.Triggers, a, V3(E(dst) + 8), true); if (labels) Add(Cat.Triggers, a + new Vector3(0, 20, 0), $"TRIGGER -> #{dst}", 2); }
                    else Add(Cat.Triggers, a + new Vector3(0, 20, 0), $"TRIGGER #{src} (msg {Mem.U8(recs + (uint)(20 * k) + 0x12)})", 2);
                }
        }
    }

    private void Spawns(uint L)
    {
        uint wd = Mem.U32(L + 0x90);
        if (!Ram(wd)) return;
        uint k = Chunk(wd, 0), r = Chunk(wd, 1) + 8;
        int cnt = Mem.S32(k + 4), lvl = Mem.S32(L + 0x1B40C);
        if (cnt <= 0 || cnt > 8192) return;
        for (int i = 0; i < cnt; i++)
        {
            uint p = r + (uint)(i * 20);
            if (Mem.S16(p + 0x10) != lvl) continue;
            var pos = V3(p);
            int key = Mem.S32(k + 8 + (uint)(4 * i));
            Add(Cat.Spawns, pos, $"SPAWN KEY {key}");
            float yaw = F(p + 0xC);
            if (float.IsFinite(yaw)) AddLine(Cat.Spawns, pos, pos + new Vector3(MathF.Sin(yaw) * 40, 0, MathF.Cos(yaw) * 40));
        }
    }

    private static uint NavNode(uint np, int i) => np + 8 + (uint)(i * Mem.S32(np));

    private void Nav()
    {
        uint G = Gv.AiNavGraph, np = Mem.U32(G), ep = Mem.U32(G + 8);
        int nn = Mem.S32(G + 4), ne = Mem.S32(G + 0xC);
        if (!Ram(np) || nn <= 0 || nn > 4096) return;
        for (int i = 0; i < nn; i++)
        {
            uint r = NavNode(np, i);
            int flags = Mem.U16(r + 0xC), id = Mem.U16(r + 0xE);
            string what = (flags & 1) != 0 ? " " + TypeLabel(id) : (flags & 0x200) != 0 ? " PLATFORM" : (flags & 0xC0) != 0 ? " TIMER" : "";
            Add(Cat.NavNodes, V3(r), $"NODE {i}{what}" + (flags != 0 ? $" F{flags:X}" : ""), 3);
        }
        if (Ram(ep) && ne > 0 && ne < 16384)
            for (int k = 0; k < ne; k++)
            {
                uint e = ep + 8 + (uint)(k * Mem.S32(ep));
                int a = Mem.U16(e), b = Mem.U16(e + 2), kind = Mem.U8(e + 4);
                if (a >= nn || b >= nn) continue;
                counts[(int)Cat.NavLinks]++;
                AddLine(Cat.NavLinks, V3(NavNode(np, a)), V3(NavNode(np, b)), kind is not (1 or 4 or 7));
            }
        // bots: current node -> planned path (brain +0x14: 4 node ids, -1 terminated), goal node
        for (uint p = Mem.U32(Gv.PlayerListHead), guard = 0; Ram(p) && guard < 16; p = Mem.U32(p + 0x16E0), guard++)
        {
            if (Mem.S32(p + 0x1450) == 0) continue;          // IsComputer
            uint brain = Mem.U32(p + 0x1454);
            if (!Ram(brain)) continue;
            var at = V3(p + 8);
            int goal = Mem.S32(brain + 0xC);
            counts[(int)Cat.BotRoutes]++;
            for (int j = 0; j < 4; j++)
            {
                int id = Mem.S32(brain + 0x14 + (uint)(4 * j));
                if (id < 0 || id >= nn) break;
                var q = V3(NavNode(np, id));
                AddLine(Cat.BotRoutes, at, q, true); at = q;
            }
            if (goal >= 0 && goal < nn) Add(Cat.BotRoutes, V3(NavNode(np, goal)) + new Vector3(0, 30, 0), $"GOAL OF {PlayerName(p)} (AI STATE {Mem.S32(brain + 0x21C)})", 3);
        }
    }

    private void Paths(uint L)
    {
        uint P = Mem.U32(L + 0x7C);
        if (!Ram(P)) return;
        int np = Mem.S32(P);
        if (np <= 0 || np > 512) return;
        for (int i = 0; i < np; i++)
        {
            uint path = Chunk(P, i), hdr = Chunk(path, 0), pts = Chunk(path, 1);
            int stride = Mem.S32(pts), cnt = Mem.S32(pts + 4);
            if (stride < 12 || stride > 256 || cnt <= 0 || cnt > 1024) continue;
            bool loop = Mem.S16(hdr) == 1;
            counts[(int)Cat.Paths]++;
            Vector3 first = default, prev = default;
            for (int j = 0; j < cnt; j++)
            {
                var q = V3(pts + 8 + (uint)(j * stride));
                if (j == 0) { first = q; if (labels) Add(Cat.Paths, q, $"PATH {i} ({cnt} PTS{(loop ? ", LOOP" : "")})", 3); }
                else AddLine(Cat.Paths, prev, q);
                prev = q;
            }
            if (loop && cnt > 2) AddLine(Cat.Paths, prev, first);
        }
    }

    private void Cells(uint L)
    {
        int nc = Mem.S32(L + 0x1504);
        if (nc <= 0) nc = Mem.S32(L + 0x1500);          // between frames: the release step keeps the entries and copies the count
        if (nc <= 0 || nc > 128) return;
        var seen = new HashSet<uint>();
        for (int c = 0; c < nc; c++)
        {
            uint h = Mem.U32(L + 0x1508 + (uint)(c * 12));
            if (!Ram(h)) continue;
            uint blob = Mem.U32(h);
            if (!Ram(blob) || !seen.Add(blob)) continue;
            uint props = Chunk(blob, 0), picks = Chunk(blob, 1), vols = Chunk(blob, 2);
            int n0 = Mem.S32(props + 4), n1 = Mem.S32(picks + 4), n2 = Mem.S32(vols + 4);
            for (int i = 0; i < n0 && i < 1024; i++)
            {
                uint p = props + 8 + (uint)(i * 0xE8), def = Mem.U32(p + 0x18);
                Add(Cat.Props, V3(p + 8), Ram(def) ? $"PROP DEF {DefIndex(L, def)} R {F(def + 8):0}" : "PROP", 2);
            }
            for (int i = 0; i < n1 && i < 512; i++)
            {
                uint p = picks + 8 + (uint)(i * 0x1C8);
                int id = ItemTypeId(L, Mem.S16(p + 4));
                bool taken = (Mem.U32(p + 0x19C) & 2) != 0;
                Add(Cat.CellPickups, V3(p + 8), (id >= 0 ? TypeLabel(id) : "PICKUP") + (taken ? " (TAKEN)" : ""));
            }
            for (int i = 0; i < n2 && i < 256; i++)
            {
                uint v = vols + 8 + (uint)(i * 0xC0);
                string mode = Mem.U8(v + 0xBB) switch { 0 => "SPHERE", 1 => "BOX", 2 => "CONE", var m => $"MODE {m}" };
                Add(Cat.LightVolumes, V3(v + 0x90), $"LIGHT {mode}{(Mem.U8(v + 0xB8) != 0 ? " (DARK)" : "")}{(Mem.U8(v + 0xBA) != 0 ? " FLICKER" : "")}", 3);
            }
        }
    }

    private static string PlayerName(uint p)
    {
        try
        {
            uint rec = Mem.U32(p + 0x5D8);
            if (Ram(rec))
            {
                var sb = new System.Text.StringBuilder();
                for (uint i = 0; i < 16; i++) { byte b = Mem.U8(rec + 0x84 + i); if (b == 0) break; if (b >= 32 && b < 127) sb.Append((char)b); }
                if (sb.Length > 0) return sb.ToString().ToUpperInvariant();
            }
        }
        catch (Mem.GuestFault) { }
        return $"PLAYER {p:X8}";
    }

    private void Live()
    {
        for (uint p = Mem.U32(Gv.PlayerListHead), guard = 0; Ram(p) && guard < 16; p = Mem.U32(p + 0x16E0), guard++)
            Add(Cat.Players, V3(p + 8) + new Vector3(0, 10, 0), $"{PlayerName(p)}{(Mem.S32(p + 0x1450) != 0 ? " (BOT)" : "")}  HP {Mem.S32(p + 0x5E4) / 256}  SPAWN KEY {Mem.S32(p + 0x5EC)}", 5);
        uint a = Mem.U32(Gv.ActorPool_UsedList);
        for (int guard = 0; Ram(a) && guard < 64; a = Mem.U32(a + 0x2EC), guard++)
        {
            uint def = Mem.U32(a + 0x18);
            Add(Cat.Actors, V3(a + 8), $"{KindName(Ram(def) ? Mem.S32(def) : -1)} {TypeLabel(Mem.U16(a + 0xE4))}", 3);
        }
        uint e = Mem.U32(Gv.PickupPool + 0x3C04);
        for (int guard = 0; Ram(e) && guard < 64; e = Mem.U32(e + 0x1DC), guard++)
        {
            int id = ItemTypeId(Gv.LevelMgr, Mem.S16(e + 4));
            Add(Cat.Pickups, V3(e + 8), (id >= 0 ? TypeLabel(id) : "PICKUP") + $" {F(e + 0x1C8):0}", 3);
        }
    }

    private void Lights()
    {
        uint n = Mem.U32(Gv.Light_Pool);
        for (int guard = 0; Ram(n) && guard < 64; n = Mem.U32(n + 4), guard++)
            Add(Cat.PointLights, new Vector3(Mem.S16(n + 0x10), Mem.S16(n + 0x12), Mem.S16(n + 0x14)), $"POINT LIGHT {F(n + 0xC):0.##}", 2);
    }

    private void Sounds()
    {
        uint n = Mem.U32(Gv.LoopSound_Pool + 0x14);
        for (int guard = 0; Ram(n) && guard < 64; n = Mem.U32(n + 4), guard++)
            Add(Cat.Sounds, V3(n + 0x10), $"LOOP SOUND {Mem.S32(n + 0xC)}", 3);
    }

    /// <summary>The level's collision faces (Level +0x6C, the floor / region mesh; small in the arenas), wireframe, at most a few thousand.</summary>
    private void Collision(uint L, Vector3 eye, float maxD)
    {
        uint C = Mem.U32(L + 0x6C);
        if (!Ram(C)) return;
        // faces (32 bytes from chunk 2 + 8) are stored in cell order; the cells (chunk 3: +4 count, 20-byte cells from +8, +0x10 face count) give the total
        uint Fc = Chunk(C, 2), cells = Chunk(C, 3);
        int ncell = Mem.S32(cells + 4), nf = 0;
        if (ncell <= 0 || ncell > 65536) return;
        for (int k = 0; k < ncell; k++) nf += (int)Mem.U32(cells + 8 + (uint)(k * 20) + 0x10);
        if (nf <= 0 || nf > 200000) return;
        counts[(int)Cat.Collision] = nf;
        float r = MathF.Min(maxD, 1024), r2 = r * r;
        int drawn = 0;
        for (int i = 0; i < nf && drawn < 3000; i++)
        {
            uint f = Fc + 8 + (uint)(i * 32);
            uint va = Mem.U32(f + 4), vb = Mem.U32(f + 8), vc = Mem.U32(f + 0xC);
            if (!Ram(va) || !Ram(vb) || !Ram(vc)) continue;
            Vector3 a = V3(va), b = V3(vb), c = V3(vc);
            AddLine(Cat.Collision, a, b); AddLine(Cat.Collision, b, c); AddLine(Cat.Collision, c, a);
            drawn++;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ projection

    private struct View { public uint Cam; public float[] M; public float Vx, Vy, Vw, Vh, Sx, Sy, Tx, Ty; public Vector3 Eye; }

    private static List<View> Views()
    {
        var list = new List<View>();
        int parity = Mem.S32(Gv.FrameParity) & 1;
        for (uint cam = Mem.U32(Gv.Camera_ListHead), guard = 0; Ram(cam) && guard < 8; cam = Mem.U32(cam + 4), guard++)
        {
            var m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = Mem.F32(cam + 0x1E0 + (uint)(4 * i));
            if (!m.All(float.IsFinite) || m[15] == 0 && m[11] == 0) continue;
            uint vp = cam + 0x2B0 + (uint)(16 * parity);
            float vsx = Mem.S16(vp) / 4f, vsy = Mem.S16(vp + 2) / 4f, vtx = Mem.S16(vp + 8) / 4f, vty = Mem.S16(vp + 10) / 4f;
            if (vsx == 0 || vsy == 0) continue;
            list.Add(new View { Cam = cam, M = m, Vx = vtx - MathF.Abs(vsx), Vy = vty - MathF.Abs(vsy), Vw = 2 * MathF.Abs(vsx), Vh = 2 * MathF.Abs(vsy),
                                Sx = vsx, Sy = vsy, Tx = vtx, Ty = vty, Eye = V3(cam + 0x128) });
        }
        return list;
    }

    private static Vector4 Clip(in View v, Vector3 p)
    {
        var m = v.M;
        return new Vector4(p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12], p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13],
                           p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14], p.X * m[3] + p.Y * m[7] + p.Z * m[11] + m[15]);
    }

    private const float NearW = 8f;

    private bool ToWindow(in View v, Vector4 c, Rectangle dest, out Vector2 w)
    {
        w = default;
        if (c.W < NearW) return false;
        float nx = c.X / c.W, ny = c.Y / c.W;
        float fx = v.Tx + nx * v.Sx, fy = v.Ty - ny * v.Sy;                          // the RSP's viewport transform (vscale / vtrans), framebuffer pixels
        if (fx < v.Vx - 2 || fx > v.Vx + v.Vw + 2 || fy < v.Vy - 2 || fy > v.Vy + v.Vh + 2) return false;
        return renderer!.MapPerspectivePoint(fx, fy, v.Vx, v.Vy, v.Vw, v.Vh, dest, out w);
    }

    // ------------------------------------------------------------------------------------------------------------ drawing

    public void Draw(Rectangle dest, int w, int h)
    {
        if (!active || font == null) return;
        bool ready = LevelReady();
        List<View> views = new();
        if (ready)
        {
            try
            {
                views = Views();
                Collect();
                if (on[(int)Cat.Collision] && views.Count > 0) Try(() => Collision(Gv.LevelMgr, views[0].Eye, Ranges[range]));
            }
            catch (Mem.GuestFault) { ready = false; }
        }
        float s = Math.Max(0.5f, Math.Min(w / 960f, h / 720f));
        batch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        if (ready)
            foreach (var v in views)
            {
                float maxD = Ranges[range];
                foreach (var ln in lines)
                {
                    if (Vector3.Distance(v.Eye, ln.A) > maxD && Vector3.Distance(v.Eye, ln.B) > maxD) continue;
                    DrawWorldLine(v, ln.A, ln.B, Cats[(int)ln.Cat].Color * 0.85f, dest, ln.Arrow, s);
                }
                var shown = new List<(Mark m, Vector2 p, float r, float d)>();
                foreach (var mk in marks)
                {
                    float d = Vector3.Distance(v.Eye, mk.P);
                    if (d > maxD) continue;
                    if (!ToWindow(v, Clip(v, mk.P), dest, out var p)) continue;
                    var col = Cats[(int)mk.Cat].Color;
                    float r = MathF.Max(2, mk.Size * s * MathF.Min(2.5f, 600f / MathF.Max(60, d)));
                    Fill(p.X - r - 1, p.Y - r - 1, 2 * r + 2, 2 * r + 2, new Color(0, 0, 0, 160));
                    Fill(p.X - r, p.Y - r, 2 * r, 2 * r, col);
                    shown.Add((mk, p, r, d));
                }
                if (!labels) continue;
                // labels: the most important categories (menu order) and the nearest first; a label that would overlap one already drawn is left out
                var taken = new List<Rectangle>();
                float ls = 0.36f * s;
                foreach (var (mk, p, r, d) in shown.OrderBy(x => (int)x.m.Cat).ThenBy(x => x.d))
                {
                    if (mk.Label.Length == 0) continue;
                    var sz = font.MeasureString(mk.Label) * ls;
                    var rc = new Rectangle((int)(p.X + r + 3), (int)(p.Y - 6 * s), (int)sz.X + 2, (int)(sz.Y * 0.8f));
                    if (taken.Any(t => t.Intersects(rc))) continue;
                    taken.Add(rc);
                    Text(mk.Label, rc.X, rc.Y, Cats[(int)mk.Cat].Color, ls);
                }
            }
        if (menuOpen) DrawMenu(s, ready);
        batch!.End();
    }

    private void DrawWorldLine(in View v, Vector3 a, Vector3 b, Color col, Rectangle dest, bool arrow, float s)
    {
        Vector4 ca = Clip(v, a), cb = Clip(v, b);
        if (ca.W < NearW && cb.W < NearW) return;
        if (ca.W < NearW) ca = Vector4.Lerp(ca, cb, (NearW - ca.W) / (cb.W - ca.W));          // clip against the near plane in clip space
        else if (cb.W < NearW) cb = Vector4.Lerp(cb, ca, (NearW - cb.W) / (ca.W - cb.W));
        float nax = ca.X / ca.W, nay = ca.Y / ca.W, nbx = cb.X / cb.W, nby = cb.Y / cb.W;
        if ((nax < -1.5f && nbx < -1.5f) || (nax > 1.5f && nbx > 1.5f) || (nay < -1.5f && nby < -1.5f) || (nay > 1.5f && nby > 1.5f)) return;
        float fax = v.Tx + nax * v.Sx, fay = v.Ty - nay * v.Sy, fbx = v.Tx + nbx * v.Sx, fby = v.Ty - nby * v.Sy;
        renderer!.MapPerspectivePoint(fax, fay, v.Vx, v.Vy, v.Vw, v.Vh, dest, out var pa);
        renderer!.MapPerspectivePoint(fbx, fby, v.Vx, v.Vy, v.Vw, v.Vh, dest, out var pb);
        // keep inside the view's part of the picture
        renderer!.MapPerspectivePoint(v.Vx, v.Vy, v.Vx, v.Vy, v.Vw, v.Vh, dest, out var c0);
        renderer!.MapPerspectivePoint(v.Vx + v.Vw, v.Vy + v.Vh, v.Vx, v.Vy, v.Vw, v.Vh, dest, out var c1);
        if (!ClipSegment(ref pa, ref pb, Vector2.Max(c0, new Vector2(dest.X, dest.Y)), Vector2.Min(c1, new Vector2(dest.Right, dest.Bottom)))) return;
        Segment(pa, pb, col, Math.Max(1, 1.2f * s));
        if (arrow)
        {
            var dir = pb - pa; float len = dir.Length();
            if (len > 24 * s)
            {
                dir /= len; var mid = (pa + pb) * 0.5f; var nrm = new Vector2(-dir.Y, dir.X);
                Segment(mid, mid - dir * 7 * s + nrm * 4 * s, col, Math.Max(1, 1.2f * s));
                Segment(mid, mid - dir * 7 * s - nrm * 4 * s, col, Math.Max(1, 1.2f * s));
            }
        }
    }

    /// <summary>Liang-Barsky clip of a segment to a rectangle.</summary>
    private static bool ClipSegment(ref Vector2 a, ref Vector2 b, Vector2 lo, Vector2 hi)
    {
        float t0 = 0, t1 = 1; var d = b - a;
        bool Edge(float p, float q)
        {
            if (p == 0) return q >= 0;
            float t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (!Edge(-d.X, a.X - lo.X) || !Edge(d.X, hi.X - a.X) || !Edge(-d.Y, a.Y - lo.Y) || !Edge(d.Y, hi.Y - a.Y)) return false;
        var a0 = a; a = a0 + d * t0; b = a0 + d * t1;
        return true;
    }

    private void DrawMenu(float s, bool ready)
    {
        float fs = 0.5f * s, lh = font!.LineSpacing * fs * 1.1f;
        float x = 16 * s, y = 16 * s, pw = 430 * s, ph = lh * (ItemCount + 3) + 16 * s;
        Fill(x, y, pw, ph, new Color(10, 8, 4, 210));
        Fill(x, y, pw, Math.Max(1, 2 * s), new Color(214, 140, 36));
        float ty = y + 8 * s;
        Text("LEVEL INSPECTOR" + (ready ? $"  -  LEVEL {Mem.S32(Gv.Level_CurrentId)}" : "  -  NO LEVEL ON SCREEN"), x + 12 * s, ty, new Color(255, 200, 70), fs * 1.05f);
        ty += lh * 1.3f;
        for (int i = 0; i < ItemCount; i++)
        {
            bool sel = i == cursor;
            if (sel) Fill(x + 6 * s, ty - 2 * s, pw - 12 * s, lh, new Color(90, 52, 10, 170));
            string txt; Color col;
            if (i < Cats.Length)
            {
                col = on[i] ? Cats[i].Color : new Color(130, 120, 100);
                Fill(x + 14 * s, ty + lh * 0.25f, lh * 0.5f, lh * 0.5f, on[i] ? Cats[i].Color : new Color(60, 55, 45));
                txt = ready ? $"{Cats[i].Name}  ({counts[i]})" : Cats[i].Name;
            }
            else if (i == Cats.Length) { col = new Color(236, 226, 206); txt = $"LABELS: {(labels ? "ON" : "OFF")}"; }
            else if (i == Cats.Length + 1) { col = new Color(236, 226, 206); txt = $"RANGE: {(float.IsInfinity(Ranges[range]) ? "ALL" : Ranges[range].ToString("0"))}  (< >)"; }
            else { col = new Color(255, 200, 70); txt = "NEXT MATCH BOTS: " + Game.Bots switch { BotMode.None => "NONE", BotMode.Ally => "ONE ALLY", _ => "AS CHOSEN" } + "  (< >)"; }
            Text(txt, x + 14 * s + lh * 0.8f, ty, col, fs);
            ty += lh;
        }
        Text("UP/DOWN CHOOSE  -  ENTER SWITCH  -  F3 CLOSE MENU", x + 12 * s, ty + 4 * s, new Color(150, 136, 112), fs * 0.8f);
    }

    private void Text(string t, float x, float y, Color c, float scale)
    {
        batch!.DrawString(font!, t, new Vector2(x + 1, y + 1), new Color(0, 0, 0, 200), 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        batch!.DrawString(font!, t, new Vector2(x, y), c, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
    }
    private void Fill(float x, float y, float w, float h, Color c) => batch!.Draw(white!, new Rectangle((int)x, (int)y, Math.Max(1, (int)w), Math.Max(1, (int)h)), c);
    private void Segment(Vector2 a, Vector2 b, Color c, float width)
    {
        var d = b - a; float len = d.Length();
        if (len < 0.5f) return;
        batch!.Draw(white!, a, null, c, MathF.Atan2(d.Y, d.X), new Vector2(0, 0.5f), new Vector2(len, width), SpriteEffects.None, 0);
    }

    public void Dispose() { batch?.Dispose(); white?.Dispose(); }
}
