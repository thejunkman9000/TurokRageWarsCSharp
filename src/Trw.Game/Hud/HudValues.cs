using Trw.Platform;

namespace Trw.Game.PortHud;

/// <summary>A split-screen view of the frame being drawn: its index (camera list order), the camera, its rectangle on the 480x360 screen and its player.</summary>
public sealed record HudView(int Index, uint Camera, float X, float Y, float W, float H, uint Player);

/// <summary>
/// The values the layout binds to, read from guest RAM for one view (read-only; values may be a frame newer than the drawn frame, like the scoreboard's).
/// Offsets are the decompiled C's (Hud_DrawPlayer <c>Shared_HudPlayer</c>, Hud_DrawAmmo ammo slots, Hud_DrawMatchClock clock, <c>HudStatusShared_Settings</c> at 0x801462C8).
/// <list type="table">
/// <item><term>health</term><description>displayed health: (s32 player+0x5E4 + 0xFF) &gt;&gt; 8 (the raw value is in 1/256)</description></item>
/// <item><term>frags</term><description>the HUD's score: team score (rules+0x2C + team*4) in team games, else the owner record's s16 +0x04 (clamped at -99)</description></item>
/// <item><term>ammo1..3, ammo1.shown..</term><description>the three ammo counters s16 +0x5F4/+0x5F6/+0x5F8 and whether the game shows that slot (mover +0xE2C + i*0x3C: +0 and +8 set, weapon &lt; 0x12)</description></item>
/// <item><term>clock</term><description>seconds left (f32 0x801468B4 / 15), clock.shown = the game's blink rule</description></item>
/// </list>
/// The full list is in docs/HUD.md.
/// </summary>
public sealed class HudValues : IHudValues
{
    public const uint Settings = 0x801462C8, Rules = Settings + 0x5D8, ClockAddr = Rules + 0x14, FragLimitAddr = Rules + 0x18;
    public const uint ScreenW = 0x800E28D0, ScreenH = 0x800E28D4, HiRes = 0x800E28D8, ViewCount = 0x801450B8, FrameCounter = 0x800D2980;

    private readonly Dictionary<string, double> v = new(StringComparer.OrdinalIgnoreCase);
    public HudView View { get; }
    public int Players { get; }

    public bool TryGet(string name, out double value) => v.TryGetValue(name, out value);
    public double this[string name] => v.TryGetValue(name, out var x) ? x : 0;
    public IEnumerable<KeyValuePair<string, double>> All => v;

    /// <summary>Values given directly (tests, tools): no guest RAM is read.</summary>
    public HudValues(HudView view, int players, IEnumerable<KeyValuePair<string, double>> values)
    {
        View = view; Players = players;
        v["players"] = players; v["view"] = view.Index + 1;
        v["view.x"] = view.X; v["view.y"] = view.Y; v["view.w"] = view.W; v["view.h"] = view.H;
        foreach (var (k, x) in values) v[k] = x;
    }

    public HudValues(HudView view, int players)
    {
        View = view; Players = players;
        v["players"] = players; v["view"] = view.Index + 1;
        v["view.x"] = view.X; v["view.y"] = view.Y; v["view.w"] = view.W; v["view.h"] = view.H;
        try { Read(view); } catch (Mem.GuestFault) { } catch (IndexOutOfRangeException) { }
    }

    private static bool Ram(uint a, int n) => a >= 0x80000000u && a + (uint)n <= 0x80800000u && Mem.IsMapped(a);

    private void Read(HudView view)
    {
        float sw = Mem.S32(ScreenW), sh = Mem.S32(ScreenH);
        if (sw <= 0) sw = 480; if (sh <= 0) sh = 360;
        v["screen.w"] = sw; v["screen.h"] = sh;
        int hires = Mem.S32(HiRes);
        v["hires"] = hires;
        // Hud_DrawPlayer's two scale factors: health and the hurt flash use kh, the counters ks (D_800E28D8 = the hi-res mode)
        v["kh"] = hires != 0 ? 1.5 : 1.0;
        v["ks"] = hires != 0 ? 1.0 : 0.75;
        v["localView"] = LocalView.Enabled ? 1 : 0;
        v["views"] = Mem.S32(ViewCount);
        v["state"] = Mem.S32(G.GameState);
        v["frame"] = Mem.U32(FrameCounter);
        // match settings / rules
        v["trialKind"] = Mem.U8(Settings + 0xD);
        v["hudFade"] = Mem.U8(Settings + 0x16);
        v["hudShown"] = Mem.U8(Settings + 0x1D);
        v["hideHud"] = Mem.S32(Rules + 0x20);
        v["teams"] = Mem.S32(Rules + 0x24);
        v["countRule"] = Mem.S32(Rules + 0x54);
        v["squads"] = Mem.S32(Rules + 0x78);
        v["markers"] = Mem.S32(Rules + 0x98);
        v["fraglimit"] = Mem.S32(FragLimitAddr);
        float clk = Mem.F32(ClockAddr);
        if (!(clk > 0 && clk < 1e8f)) clk = 0;
        double secs = clk / 15.0;
        v["clock.raw"] = clk;
        v["clock"] = secs;
        int min = (int)(secs / 60.0), sec = (int)(secs - min * 60.0);
        v["clock.min"] = min; v["clock.sec"] = sec;
        // Hud_DrawMatchClock: shown while the clock runs; in the last 30 s of the last minute it blinks (off for 5 of every 15 frames)
        v["clock.shown"] = clk > 0 && (min >= 1 || sec >= 30 || Mem.U32(FrameCounter) % 15 >= 5) ? 1 : 0;

        uint p = view.Player;
        v["player"] = p != 0 ? 1 : 0;
        if (p == 0 || !Ram(p, 0x1460)) return;
        int hp = Mem.S32(p + 0x5E4);
        v["health.raw"] = hp;
        v["health"] = (hp + 0xFF) >> 8;
        v["lives"] = Mem.S16(p + 0x5EA);
        v["weapon"] = Mem.S16(p + 0x62E);
        v["kind"] = Mem.U16(p + 0xE4);
        // Weapon_GetAmmoType: the current weapon's ammo type (-1 = none: the game shows no ammo counters); weapon table D_800D052C, +0x20 / +0x24 by the mode +0x594
        int ammoType = -1;
        {
            int w = Mem.S16(p + 0x62E);
            uint t = w >= 0 && w < 64 ? Mem.U32(0x800D052Cu + (uint)w * 4) : 0;
            if (Ram(t, 0x28))
            {
                uint q = Mem.S32(p + 0x594) == 1 ? Mem.U32(t + 0x20) : Mem.U32(t + 0x24);
                int idx = Ram(q, 2) ? Mem.S16(q) : -1;
                if (idx >= 0 && idx < 256) ammoType = Mem.S16(0x800D0588u + (uint)idx * 2);
            }
        }
        v["ammo.type"] = ammoType;
        for (int i = 0; i < 3; i++)
        {
            uint slot = p + 0xE2C + (uint)i * 0x3C;
            v[$"ammo{i + 1}"] = Mem.S16(p + 0x5F4 + (uint)i * 2);
            v[$"ammo{i + 1}.shown"] = ammoType != -1 && Mem.S32(slot) != 0 && Mem.S32(slot + 8) != 0 && Mem.S16(p + 0x62E) < 0x12 ? 1 : 0;
            v[$"ammo{i + 1}.x"] = Mem.F32(slot + 0x10); v[$"ammo{i + 1}.y"] = Mem.F32(slot + 0x14);
        }
        v["health.x"] = Mem.F32(p + 0xEF0); v["health.y"] = Mem.F32(p + 0xEF4);
        v["lives.anim"] = Mem.S32(p + 0xF1C); v["lives.shown"] = Mem.S32(p + 0xF24);
        v["lives.x"] = Mem.F32(p + 0xF2C); v["lives.y"] = Mem.F32(p + 0xF30);
        v["score.state"] = Mem.U32(p + 0x1050);
        v["score.x"] = Mem.F32(p + 0x1058); v["score.y"] = Mem.F32(p + 0x105C);
        v["alpha"] = Mem.F32(p + 0x107C);
        v["score.frame"] = Mem.S32(p + 0x11CC);
        v["count.state"] = Mem.U32(p + 0x108C); v["count.x"] = Mem.F32(p + 0x1094); v["count.y"] = Mem.F32(p + 0x1098); v["count.frame"] = Mem.S32(p + 0x11D4);
        v["squad.state"] = Mem.U32(p + 0x10C8); v["squad.x"] = Mem.F32(p + 0x10D0); v["squad.y"] = Mem.F32(p + 0x10D4); v["squad.frame"] = Mem.S32(p + 0x11D0);
        v["flash.state"] = Mem.S32(p + 0x1104); v["flash.alpha"] = Mem.F32(p + 0x1130);
        v["hurt"] = Mem.F32(p + 0x11D8);
        v["boost"] = Mem.F32(p + 0x11E4);
        v["fx.flags"] = Mem.S32(p + 0x122C); v["fx.time"] = Mem.F32(p + 0x1230);
        v["zoom"] = Mem.F32(p + 0x12C0);
        v["trialCount"] = Math.Max(0, Mem.S32(p + 0x133C) - 1);
        v["hideLives"] = Mem.S32(p + 0x1450);
        // crosshair (Hud_DrawCrosshair): sprite id +0x798 (-1 = none), animation frame f32 +0x7BC, shown while +0x788 is set
        v["crosshair.id"] = Mem.S32(p + 0x798);
        v["crosshair.frame"] = Mem.F32(p + 0x7BC);
        v["crosshair.on"] = Mem.S32(p + 0x788) != 0 && Mem.S16(p + 0x650) != 0x27 ? 1 : 0;
        {   // Hud_DrawIconSprite draws it at the centre minus (scale * size - 1), scale by the sprite's class (D_800C767C / 80 / 84), size D_800C7688
            int id = Mem.S32(p + 0x798);
            uint k = id is 0x2DA or 0x2EF or 0x2F0 ? 0x800C767Cu : id is 0x2BC or 0x2E4 or 0x2F1 or 0x2F2 or 0x302 or 0x320 ? 0x800C7680u : 0x800C7684u;
            float size = Mem.F32(0x800C7688);
            v["crosshair.size"] = size;
            v["crosshair.half"] = Mem.F32(k) * size - 1f;
        }
        // the score record (Hud_DrawPlayer player->owner, MatchInfo)
        uint own = Mem.U32(p + 0x5D8);
        if (Ram(own, 0x94))
        {
            int team = Mem.U8(own + 0x92);
            v["team"] = team;
            v["deaths"] = Mem.S16(own + 0x02);
            v["suicides"] = Mem.S16(own + 0x00);
            v["count"] = Mem.S16(own + 0x06);
            v["active"] = Mem.U8(own + 0x8F);
            int score = Mem.S32(Rules + 0x24) != 0 ? (team != 0xFF && team < 5 ? Mem.S32(Rules + 0x2C + (uint)team * 4) : 0) : Mem.S16(own + 0x04);
            v["frags.raw"] = Mem.S16(own + 0x04);
            v["frags"] = Math.Max(-99, score);
            v["squadScore"] = team != 0xFF && team < 5 ? Mem.S32(Rules + 0x40 + (uint)team * 4) : 0;
        }
    }

    // ---------------------------------------------------------------------------------------------- views and players of the current frame

    /// <summary>The views of the match with their players (player whose view pointer +0x5DC is the camera; Hud_DrawView2D).</summary>
    public static List<HudView> Views()
    {
        var list = new List<HudView>();
        try
        {
            var cams = HudTagger.Cameras();
            var players = new List<uint>();
            uint a = Mem.U32(MatchInfo.PlayerListHead);
            for (int i = 0; i < 8 && a != 0 && Ram(a, 0x16E4); i++, a = Mem.U32(a + MatchInfo.ActNext)) players.Add(a);
            for (int i = 0; i < cams.Length; i++)
            {
                uint c = cams[i];
                if (!Ram(c, 0x2B0)) continue;
                uint pl = players.FirstOrDefault(q => Mem.U32(q + 0x5DC) == c);
                list.Add(new HudView(i, c, Mem.F32(c + 0x2A4), Mem.F32(c + 0x2A8), Mem.F32(c + 0x29C), Mem.F32(c + 0x2A0), pl));
            }
        }
        catch (Mem.GuestFault) { }
        return list;
    }

    /// <summary>The shared pass: the own racer's full-screen view (the clock is laid out from the first view's size, see Hud_DrawMatchClock).</summary>
    public static HudView SharedView(List<HudView> views)
    {
        float w = 480, h = 360;
        try { w = Math.Max(1, Mem.S32(ScreenW)); h = Math.Max(1, Mem.S32(ScreenH)); } catch (Mem.GuestFault) { }
        return new HudView(HudTagger.Shared - 1, HudTagger.SharedRacer, 0, 0, w, h, 0);
    }
}

/// <summary>The HUD values of one section, taken by <see cref="HudTagger"/> while the game's display list is interpreted (osSpTaskStartGo), carried by the
/// section's draws (<c>DrawCommand.HudData</c>): the port HUD then draws exactly the frame the game's HUD drew, even when the window shows it later.</summary>
public sealed class HudSnapshot
{
    public required List<HudView> Views;
    public required HudView View;
    public required HudValues Values;

    public static HudSnapshot? Take(int tag)
    {
        var views = HudValues.Views();
        if (views.Count == 0) return null;
        HudView? v = tag == HudTagger.Shared ? HudValues.SharedView(views) : views.FirstOrDefault(x => x.Index == tag - 1);
        if (v == null) return null;
        return new HudSnapshot { Views = views, View = v, Values = new HudValues(v, views.Count) };
    }
}
