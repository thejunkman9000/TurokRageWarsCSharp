namespace Trw.Tools.Anim;

/// <summary>
/// Clip roles. The game never stores "idle/walk/..." per clip: object table entry section 0 holds a sorted u32 table of
/// animation request ids, whose index is the clip index (func_002469F8 binary-searches it; equal ids = random alternatives).
/// The ids are requested by the state machines (player/pawn state table at VA 0x800CE8C4 / alt set 0x800CED30 with the animId in
/// each 24-byte row, weapon state tables at descriptor+0x54). Evidence per id is in docs/formats/creatures_export.md.
/// Confidence: "verified" = id is requested by identified code for that state, "likely" = id is requested by code whose
/// purpose is inferred, "hypothesis" = pose/metrics only.
/// </summary>
public static class CreatureRoles
{
    public static (string Role, string Confidence, string Evidence) Get(int obj, int clip, int animId, AnimClip c)
    {
        if (animId < 0)
        {
            if (c.Frames == 1) return ("static", "likely", "single-frame clip without an id table entry");
            return ("unknown", "none", "clip index beyond the object's animation id table (only reachable by explicit clip index)");
        }
        return animId switch
        {
            // ---- player / pawn family (objects 174, 175, 176, 185)
            1 => ("idle", "verified", "id 1 = animId of idle state 2 (update func_002243E4) and of the dead/respawn states 19-21 (func_0022D2F8 sets unk86C = 1)"),
            2 => ("unknown", "none", "id 2 appears only in object 185"),
            20 => ("unknown", "none", "id 20 appears only in object 176 (0x14 is used as a fire-while-X selector in func_0021A9A4: 0x14 -> 0x1144)"),
            2010 => ("walk", "verified", "0x7DA = walk/move state 3 (func_002246E8) forward"),
            2011 => ("walk", "likely", "0x7DB: variant chosen after 0x7DA when the timer unk104 >= D_800C7AC4 (func_002246E8)"),
            2015 => ("walk_back", "verified", "0x7DF chosen when forward velocity < 0 (func_002246E8)"),
            2030 => ("jump", "verified", "0x7EE = animId of states 5-8 (jump rise, air, air strafe, fall)"),
            2040 => ("land", "likely", "0x7F8 used in func_0022CC24 (air update); pelvis drops 92 units over 3 frames (crouch / landing)"),
            2050 => ("strafe", "verified", "0x802 chosen when |lateral| > |forward| and lateral velocity (unk6C4) < 0 (func_002246E8)"),
            2060 => ("strafe", "verified", "0x80C chosen when |lateral| > |forward| and lateral velocity (unk6C4) >= 0 (func_002246E8)"),
            2210 => ("swim", "verified", "0x8A2 = animId of the swim states 9-12 (func_00224C28 / func_00224F38)"),
            2215 => ("swim", "likely", "0x8A7 referenced by the same swim update functions (func_00224C28, func_00224F38, func_0022D030)"),
            2600 => ("swim_idle", "likely", "0xA28 chosen by the idle update when the water flags (unk38 & 0xC0000) are set"),
            2615 => ("swim_back", "likely", "0xA37 chosen by the walk update in water when forward velocity < 0 (0xA32 = forward, absent from the ROM tables)"),
            2620 => ("special", "hypothesis", "0xA3C = animId of state 16 (flag 0x100, update func_002251B8, pelvis raised 90 units: ladder / hover style state)"),
            2630 => ("special", "hypothesis", "0xA46 used by func_002251B8 (state 16); pelvis raised 90 units"),
            2900 => ("idle_variant", "likely", "0xB54 chosen by the idle update (func_002243E4) when out of water, timer dependent (fidget)"),
            2910 => ("idle_variant", "likely", "0xB5E chosen by the idle update (func_002243E4) when out of water, timer dependent (fidget)"),
            4000 => ("attack", "likely", "0xFA0 set by the fire function func_0021A9A4 when the current weapon is of kind 0x11 (melee), alternates with 0x1004; two clips = random alternatives"),
            4100 => ("attack", "likely", "0x1004: alternate melee swing (func_0021A9A4)"),
            >= 7000 and <= 7505 => Death(animId),
            9200 => ("frozen", "hypothesis", "0x23F0 = animId of state 38 (0x26), entered on effect 0x129 (func_0021C440) which also matches the Petrify effect; first clip has 1 frame"),
            23300 => ("kneel", "hypothesis", "0x5B04 = animId of state 45 (0x2D, entered from func_0021BFBC when health reaches 0 in some modes, with a 60-75 tick timer); pelvis stays 64 units low, holds last frame"),
            23301 => ("kneel", "hypothesis", "0x5B05 = animId of state 46; pelvis drops to 107"),
            24100 => ("idle", "verified", "0x5E24 = idle in the alternate animation set D_800CED30 (state 2) and dead state"),
            24101 or 24102 => ("idle_variant", "likely", "0x5E25/0x5E26: range 0x5E24..0x5E26 is treated as idle by the fire function (func_0021A9A4)"),
            24105 => ("walk", "verified", "0x5E29 = walk in the alternate set (state 3)"),
            24106 => ("walk_back", "verified", "0x5E2A chosen when moving backwards (alternate set)"),
            24107 => ("strafe", "verified", "0x5E2B (alternate set strafe, lateral < 0)"),
            24108 => ("strafe", "verified", "0x5E2C (alternate set strafe, lateral >= 0)"),
            24109 => ("jump", "verified", "0x5E2D = animId of the airborne states 5-8 in the alternate set"),
            24110 => ("land", "likely", "0x5E2E used in func_0022CC24 (air update); pelvis drops 92 units"),
            24150 => ("attack", "likely", "0x5E56: set by func_0021A9A4 when firing from idle 0x5E24..0x5E26 (alternate set), returns to idle afterwards"),
            24153 => ("attack_walk", "likely", "0x5E59: firing while walking (from 0x5E29); carries 3 effect events (id 15)"),
            24154 => ("attack_walk", "likely", "0x5E5A: firing while walking backwards (from 0x5E2A)"),
            24155 => ("attack_strafe", "likely", "0x5E5B: firing while strafing (from 0x5E2B)"),
            24156 => ("attack_strafe", "likely", "0x5E5C: firing while strafing (from 0x5E2C)"),
            24157 => ("attack_jump", "likely", "0x5E5D: firing while airborne (from 0x5E2D)"),
            24158 => ("attack", "likely", "0x5E5E: firing with weapon index 0 (WARHAMMER) selected while idle in the alternate set"),
            24000 => ("idle", "likely", "id 24000 (0x5DC0) is passed by the menu code (func_0041F298 -> func_0041CB48) when it spawns display models: character display pose"),

            // ---- weapon viewmodels (objects 152-173, 231-253): default weapon state table at VA 0x800CF298, 32-byte entries
            20000 => ("select", "likely", "weapon state 0 (animId 0x4E20), enter func_0022FEF4 = equip (sets the current weapon index)"),
            20010 => ("deselect", "likely", "weapon state 1 (animId 0x4E2A), enter func_00232DE4 = put away (stops looping sound)"),
            20100 => ("idle", "verified", "weapon state 2 (animId 0x4E84, update func_00230390 = idle); loops"),
            20110 => ("idle_variant", "likely", "weapon state 3 (animId 0x4E8E, shares the idle update func_00230390)"),
            20200 => ("special", "hypothesis", "weapon state 5 (animId 0x4EE8), enter func_00232E28 (plays sound 0x9E2 when ready): e.g. reload / charge"),
            20210 => ("fire", "likely", "weapon state 4 primary fire: random list {0x4EF2, 0x4EFC, 0x4F06, 0x4F10} at VA 0x800CF244 (enter func_00232C78 sets the cooldown from descriptor+0x18)"),
            20270 => ("special", "hypothesis", "0x4F4E: weapon specific state id not in the default table"),
            20550 => ("fire_alt", "likely", "weapon state 8: random list {0x5046..0x5049} at VA 0x800CF258 (enter func_002328C8 secondary cooldown)"),

            // ---- the remaining single purpose objects
            21500 or 21501 or 21502 or 21503 or 21504 or 21505 => ("unknown", "none", "ids 21500-21505 (0x53FC..): not found in game code (objects 182, 214-218)"),
            12100 or 12110 or 12120 => ("unknown", "none", "ids 12100-12120: not found in game code (object 313)"),
            32000 => ("unknown", "none", "id 32000 (0x7D00): referenced by func_00259280/func_0025B1E8 (objects 203, 313)"),
            _ => ("unknown", "none", "id not found in the player/weapon state tables"),
        };
    }

    /// <summary>Ids 7000..7505 are the animIds of states 22-33 (table rows at 0x800CE8C4 + 24*s, enter functions func_0022D5E0..D864 call func_00225B74(n) with n = 10..70 ticks).</summary>
    static (string, string, string) Death(int id) => id switch
    {
        7000 => ("death", "likely", "0x1B58 state 22 (timer 10 ticks); one-shot, ends with head and pelvis on the floor (z 16 / 17)"),
        7005 => ("death", "likely", "0x1B5D state 28 (timer 12); one-shot"),
        7010 or 7011 => ("death", "likely", "0x1B62/0x1B63: states 32/33 (timers 20/15); one-shot, ends on the floor"),
        7020 or 7021 or 7022 or 7026 => ("death", "hypothesis", "0x1B6C state 31 (timer 50, special case in func_00225B74); 7021 is a 31 frame lying/get-up clip, 7022/7026 are 2-3 frame poses (7022 pelvis below the floor)"),
        7043 or 7044 or 7045 or 7046 => ("death", "likely", "0x1B83-0x1B86 states 41-44; objects 124-127 (8 bone limb/gib effects) carry exactly these ids, 7043/7044 have spawn events"),
        7100 or 7101 => ("death", "likely", "0x1BBC/0x1BBD states 23/24 (timers 30/70); absent from the ROM clip tables"),
        7200 or 7201 => ("death", "likely", "0x1C20/0x1C21 states 25/26 (timers 11/60); one-shot, ends on the floor"),
        7400 => ("death", "likely", "0x1CE8 state 27 (timer 40); absent from the ROM clip tables"),
        7500 or 7505 => ("death", "likely", "0x1D4C/0x1D51 states 29/30 (timers 13/14); one-shot, ends on the floor"),
        _ => ("death", "hypothesis", "id in the 7000-7505 state animId range"),
    };
}
