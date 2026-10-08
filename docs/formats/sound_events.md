# Gameplay sound events -> sound ids -> SoundArray slots (US v1.1)

Findings from the C port `D:\Projects\TurokRageWars` (read-only) cross-referenced with `extracted\audio\sfx_ids.tsv`
(`key -> slot / start delay (frames) / priority`; wav = `extracted\audio\sfx\sfx_slot_NNN.wav`). **V** = traced to a call site / ROM table row,
**H** = hypothesis (semantics of the trigger guessed). "no key" = the id is not in the 245-entry `keys` table, so `func_00257DF4` returns -1 and nothing plays.
Several keys have random variants (shown as `a/b/c`; the game picks by weight, avoiding an immediate repeat). Priority is the SndInfo byte (higher = harder to steal).

## 0. How the game plays a sound (V)
* `func_00257DF4(SndMgr*, id, pos, a, b)` = positional play by game sound id (3D wrapper `func_0025DE74(id, pos, srcPtr/-1)` or `(id,x,y,z,src,-1)`);
  `func_0025DF54(id)` = non-positional (2D: forces mgr flag 1, pos 0,0,0) - used for HUD / menu / own-weapon cues; `func_0025DEE0(id,pos,a,src,vol)` = 3D with volume scale (used by hit-kind 0);
  `func_0025828C` = by tree index, wrappers `func_0025E008` / `func_0025E06C` (no live caller found with a literal). All are gated by `D_80146890 == 0` (sound enabled).
* Four sound **sources** exist: (1) literal ids in code (below); (2) **animation events** with effect id 0 (hit-kind 0 = `func_00267324` -> `func_0025DEE0(part.b, pos)`): the sound id is the **low s16 of event word +0x14**
  (`events[].raw` bytes 20..23 of the 28-byte event; in `extracted\creatures\obj_N.json`); (3) the **particle effect record (section 4, 60 bytes) s16 at +0xC** = spawn sound,
  played by `func_00280094` whenever an effect with that effectId is spawned (high half of `word3` in `defs\particle_effects.json`); (4) the **impact table** (`spec\impacts.json`, field `Sound`) played by `func_0027C324/0027D47C/0027CCD0` per impact state.
* Ids `5005..6915` are **per-character voice banks**: `id = base + off(character)` (section 2).


## 1. Weapons

### 1a. Weapon select (viewmodel select clip animId 20000, event effect 0) - V for ids; object->weapon mapping (obj 152+i = weapon i) is the C# project's unverified guess

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| select WARHAMMER (obj 152) | 1100 (0x44C) | 20 \| d0 p80 | anim event, clip 0 | V |
| select BOOMERANG (153) | 2200 (0x898) | 86 \| d0 p60 | anim event, clip 0 | V |
| select TEK CROSSBOW (154) | 1800 (0x708) | 269 \| d0 p60 | anim event, clip 0 | V |
| select MAG 60 (155) | 2800 (0xAF0) | 244 \| d0 p60 | anim event, clip 0 | V |
| select SHOTGUN (156) | 2400 (0x960) | 77 \| d0 p70 | anim event, clip 0 | V |
| select ASSAULT RIFLE (157) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select MINIGUN (158) | 2600 (0xA28) | 74 \| d0 p70 | anim event, clip 0 | V |
| select PLASMA RIFLE (159) | 1200 (0x4B0) | 70 \| d0 p80 | anim event, clip 0 | V |
| select FREEZE GUN (160) | 2500 (0x9C4) | 83 \| d0 p60 | anim event, clip 0 | V |
| select FLARE GUN (161) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select GRENADES (162) | 1000 (0x3E8) | 19 \| d0 p80 | anim event, clip 0 | V |
| select SCORPION (163) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select EMACIATOR (165) | 2300 (0x8FC) | 285 \| d0 p60 | anim event, clip 0 | V |
| select INFLATOR (166) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select CHEST BURSTER (167) | 1500 (0x5DC) | 21 \| d0 p80 | anim event, clip 0 | V |
| select PFM MINE (169) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select obj 170 | 1310 (0x51E) | 68/18 \| d0/4 p80 | anim event, clip 0 | V |
| select IRON CLAW (171) | 1600 (0x640) | 16 \| d4 p40 | anim event, clip 0 | V |
| select SENTRY (172) | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |
| select obj 173 | 3400 (0xD48) | 243 \| d0 p70 | anim event, clip 0 | V |

3400 (0xD48, slot 243) is the generic select sound on 9 viewmodels and on the world-object copies 231-253. NAPALM (164) and CEREBRAL BORE (168) have no select sound.

### 1b. Fire / alt-fire / special events inside viewmodel clips (animId 20210 fire, 20550 fire_alt, 20200 special) - V

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| obj 157 fire_alt f0 | 2980 (0xBA4) | 272 \| d0 p70 | anim event (creatures/obj_N.json) | V |
| obj 159 fire f13, fire_alt f11 | 1230 (0x4CE) | 22 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 160 fire_alt f5 | 2521 (0x9D9) | 82 \| d0 p50 | anim event (creatures/obj_N.json) | V |
| obj 162 fire f2 | 1030 (0x406) | 2 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 162 fire_alt f0 | 1090 (0x442) | 96 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 164 fire f5,f7 | 3312 (0xCF0) | 273 \| d0 p70 | anim event (creatures/obj_N.json) | V |
| obj 164 fire_alt f0 | 3310 (0xCEE) | 287 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 165 fire f6 | 2340 (0x924) | 255 \| d0 p60 | anim event (creatures/obj_N.json) | V |
| obj 168 fire f0 / alt f6,f7 | 3315 (0xCF3) | 273 \| d0 p70 | anim event (creatures/obj_N.json) | V |
| obj 168 fire f2 / alt f7 | 3316 (0xCF4) | 275/274 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 170 fire f17 | 1340 (0x53C) | 18 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 171 special f7,17,25,34 | 1630 (0x65E) | 16 \| d4 p40 | anim event (creatures/obj_N.json) | V |
| obj 171 fire f0 | 1620 (0x654) | 14 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 171 fire f8 | 1621 (0x655) | 16 \| d4 p40 | anim event (creatures/obj_N.json) | V |
| obj 173 fire / fire_alt f0 | 2020 (0x7E4) | 13/11/12 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 241 (set B) fire f2 | 1030 (0x406) | 2 \| d0 p80 | anim event (creatures/obj_N.json) | V |
| obj 239 (set B) fire f1 | 2521 (0x9D9) | 82 \| d0 p50 | anim event (creatures/obj_N.json) | V |

Events whose id has no key (silent): 1032 (obj 162 alt f3), 1610 (obj 171 deselect), 1622 (obj 171 fire f3), 3120 (obj 169 deselect), 810 (obj 172 deselect), 2510 (obj 239 deselect).

### 1c. Shot spawn sound = effect record +0xC of the weapon's shot effectId (`func_0021A9A4` -> `func_00280094`) - V (values from `particle_effects.json`; weapon/effect ids from GAMEPLAY_SPEC)

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| TEK CROSSBOW primary shot (1000/1200) | 820 (0x334) | 0 \| d0 p80 | effect record +0xC via func_00280094 | V |
| TEK CROSSBOW secondary shot (1003/1201) | 900 (0x384) | 90 \| d0 p80 | effect record +0xC via func_00280094 | V |
| PLASMA RIFLE primary (1013/1221) | 1520 (0x5F0) | 4 \| d0 p80 | effect record +0xC via func_00280094 | V |
| PLASMA RIFLE secondary (1046) | 1560 (0x618) | 98 \| d0 p80 | effect record +0xC via func_00280094 | V |
| SHOTGUN secondary (1081/1220) | 1620 (0x654) | 14 \| d0 p80 | effect record +0xC via func_00280094 | V |
| ASSAULT RIFLE record 1217 | 1110 (0x456) | 3 \| d0 p80 | effect record +0xC via func_00280094 | V |
| MAG 60 secondary (1212) | 1090 (0x442) | 96 \| d0 p80 | effect record +0xC via func_00280094 | V |
| GRENADES shot (1015, 1061, 1225, 1226) | 1220 (0x4C4) | 5 \| d0 p80 | effect record +0xC via func_00280094 | V |
| SCORPION shot (1004/1204) | 1330 (0x532) | 6 \| d0 p80 | effect record +0xC via func_00280094 | V |
| NAPALM GEL (1007) | 2320 (0x910) | 256 \| d0 p80 | effect record +0xC via func_00280094 | V |
| FLARE GUN primary (1025) | 2920 (0xB68) | 85 \| d0 p80 | effect record +0xC via func_00280094 | V |
| FLARE GUN secondary (1027) | 2990 (0xBAE) | 80 \| d0 p80 | effect record +0xC via func_00280094 | V |
| EMACIATOR secondary (1022/1232) | 2450 (0x992) | 78 \| d0 p80 | effect record +0xC via func_00280094 | V |
| INFLATOR primary (1032/1248) | 2520 (0x9D8) | 81 \| d0 p80 | effect record +0xC via func_00280094 | V |
| INFLATOR secondary (1074/1251) | 2540 (0x9EC) | 81/67 \| d0 p80 | effect record +0xC via func_00280094 | V |
| CHEST BURSTER primary (1023/1233) | 2820 (0xB04) | 87 \| d0 p80 | effect record +0xC via func_00280094 | V |
| CHEST BURSTER secondary (1049/1235) | 2850 (0xB22) | 88 \| d0 p80 | effect record +0xC via func_00280094 | V |
| FREEZE GUN secondary (1051/1245) | 2700 (0xA8C) | 94 \| d0 p80 | effect record +0xC via func_00280094 | V |
| CEREBRAL BORE (297) | 1820 (0x71C) | 267 \| d0 p80 | effect record +0xC via func_00280094 | V |
| PFM MINE (1034) | 3220 (0xC94) | 262 \| d3 p50 | effect record +0xC via func_00280094 | V |
| IRON CLAW (1038) | 3275 (0xCCB) | 262 \| d0 p80 | effect record +0xC via func_00280094 | V |
| BOOMERANG (1044/1069) | 905 (0x389) | no key (silent) | effect record +0xC via func_00280094 | V |
| effects 1006/1016/1070/1072 | 3302 (0xCE6) | 67/67/67 \| d0 p80 | effect record +0xC via func_00280094 | V |

MAG 60 primary (1009/1073/1211), assault rifle primary (1075/1079/1218), minigun (1017/1227), shotgun primary (7), freeze primary (1029), emaciator primary (1019/1229), sentry (1036 -> 3130, no key) have **s16 = 0 or 1 or an unkeyed id -> no spawn sound**: their audible fire cue was **not found** (not in the records, not in the weapon state functions). Hypothesis: child/muzzle effects, or silent in this build.

### 1d. Weapon state functions (literal ids) - V

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| MINIGUN spin-down / stop firing | 1940 (0x794) | 8/10 \| d0 p90/100 | func_00230BB8 (state 4 update, 0x794) | V |
| MINIGUN spin-up (trigger pressed) | 1980 (0x7BC) | 7 \| d0 p80 | func_00230D94 (0x7BC) | V |
| EMACIATOR beam end / release | 2424 (0x978) | 103/104 \| d0 p70/90 | func_002312B8 (0x978) | V |
| MAG 60 charge shot start (energy >= 25) | 1070 (0x42E) | 97 \| d5 p80 | func_0023195C (0x42E) | V |
| CHEST BURSTER secondary start (energy >= 50) | 2851 (0xB23) | 89 \| d6 p60 | func_00231F5C (0xB23) | V |
| weapon 5 (ASSAULT RIFLE) secondary, Plr+0x62E==5 | 1120 (0x460) | no key (silent) | func_00232394 (0x460), also func_004391F8/4394E8 (overlay) | V |
| weapon 8 (FREEZE GUN) end of fire (0xA3E = 2622) | 2622 (0xA3E) | no key (silent) | func_00232CDC | V |
| **empty / not enough ammo click (+ HUD text)** | 3405 (0xD4D) | 291 \| d0 p80 | func_00230390, 002307EC, 00230FCC, 00231654, 0023195C, 00231D38, 00231F5C, 002321B8, 00232394, 0022FAE8, 00230D94, 00217D74 (0xD4D, 2D) | V |
| 'ATTRACT/REPULSE MARKED' toggle (text) | 1400 (0x578) | 254 \| d0 p70 | func_0022B1A8 (0x578, 2D) | V |
| 'Embryo Implanted' (CHEST BURSTER hit text) | 2885 (0xB45) | 248 \| d0 p60 | func_0022B7E8 (0xB45, 2D) | V |
| special form ready (state 1/7 update, stats+0x8F != 0, D_801468A0+0x70=0) | 6305 (0x18A1) | 283/284 \| d0 p80 | func_00230620 (0x18A1) | H |
| same, +0x70 = 1 | 6505 (0x1969) | 281/280 \| d0 p80 | func_00230620 (0x1969) | H |
| same, +0x70 = 2 | 6405 (0x1905) | 278/277 \| d0 p80 | func_00230620 (0x1905); also func_0022CA04/0022EDEC (state 5 enter) | H |

Reload: **no reload mechanic** in Rage Wars (ammo pools, no clip) - no sound. Weapon switch = select events (1a); deselect events are mostly silent.

### 1e. Impact sounds (`spec\impacts.json`; state 3 = wall/default, 4 = character, 5 = character-2) - V

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| bullet (MAG 60/AR/MINIGUN) on wall st3 | 1040 (0x410) | 43/44/32/33/34/35/36/37/38/42 \| d0 p10 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| bullet on character st4/st5 | 1060 (0x424) | 24/25/26/27 \| d0 p10 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| crossbow wall/character st3/4 | 850 (0x352) | 72 \| d0 p80 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| crossbow st11 | 840 (0x348) | 1 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| shotgun secondary impact | 1623 (0x657) | 66 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| plasma primary impact st3/4 | 1530 (0x5FA) | 17 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| plasma secondary impact | 1570 (0x622) | 17 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| grenade impact st3 | 1221 (0x4C5) | 73 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| scorpion impact st4 | 3302 (0xCE6) | 67/67/67 \| d0 p80 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| freeze impact st3 | 2630 (0xA46) | 76 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| freeze character st4 | 2650 (0xA5A) | 245 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| napalm impact | 2370 (0x942) | 258 \| d0 p80 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| emaciator impact | 2422 (0x976) | 79 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| inflator impact | 2523 (0x9DB) | 242 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| chest burster impact | 2860 (0xB2C) | 246 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| chest burster B st4 | 2880 (0xB40) | 247 \| d0 p60 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| cerebral bore character st4 | 1850 (0x73A) | 268 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| PFM mine impact | 3230 (0xC9E) | 263 \| d0 p50 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| iron claw impact | 3280 (0xCD0) | 260 \| d0 p80 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| iron claw st10 | 3285 (0xCD5) | 261 \| d0 p80 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| sentry turret impact | 3100 (0xC1C) | 266 \| d0 p50 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| boomerang wall st3 | 2240 (0x8C0) | 93 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |
| boomerang character st4 | 2260 (0x8D4) | 92 \| d0 p70 | impact table (func_0027C324/0027D47C/0027CCD0) | V |

### 1f. Explosions
No dedicated 'explosion' id: grenade/scorpion use their shot-record spawn sound (1220 -> slot 5, 1330 -> slot 6) and impact sounds (1221 -> slot 73, 3302 -> slot 67). Gib effect strips (objects 124-127, animIds 7043-7046) have no sound of their own (not found).

## 2. Player events

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| **Pain / taking damage** (50 % of hits, only if the voice is not already playing, health > 0) | 5005 + off(char) | see voice table | func_0021BFBC (`func_00229BE0(p, 0x138D)`) | V |
| **Death voice** | 5015 + off(char) | see voice table | func_0021B468 (`func_00229BE0(p, 0x1397)`) | V |
| death voice replacement when the death damage-kind argument == 31 | 700 (0x2BC) | 126 \| d0 p50 | func_0021B468 | H (meaning of kind 31 unknown) |
| death, 'gibbed' variant (flag Plr+0x13C8, set for weapon/damage ids 17-18) | 2555 (0x9FB) | 251 \| d0 p40 | func_0021B468 (3D) | V |
| death thump (2D, always on death) | 530 (0x212) | 135 \| d24 p70 | func_0021B468 | V |
| on death while stats+0x8F != 0 (form revert) 2D | 720 (0x2D0) | 223 \| d0 p70 | func_0021B468 | H |
| death animation fall (obj 174 clip 19 / animId 7043, frame 13) | 2890 (0xB4A) | 293 \| d0 p90 | anim event | V |
| knockdown (obj 174 clip 27 / animId 23301, frame 1) | 6990 (0x1B4E) | 136 \| d0 p80 | anim event | V |
| **Teleport** (teleporter touched; played at the departure position, then func_0021B1E4 warps; no arrival sound) | 560 (0x230) | 133 \| d0 p70 | func_00220EB0 (0x230) | V |
| **Respawn** | - | - | func_0021B1E4 | not found (no sound call at respawn) |
| **Water: entering** (state 11/12 enter, state flag 0x8000) | 730 (0x2DA) | 129 \| d0 p50 | func_0022CF28 (0x2DA) | H (swim states per C# doc) |
| water: entering, other case | 732 (0x2DC) | 253 \| d0 p70 | func_0022CF28 (0x2DC) | H |
| water: leaving/surfacing (swim state, timer > 2) | 734 (0x2DE) | 252 \| d0 p70 | func_00224F38 (0x2DE) | H |
| **Swimming** stroke while moving (every ~45-60 ticks; 4 variants) | 736 (0x2E0) | 289/288/288/289 \| d0 p40 | func_00224F38 (0x2E0) | H |
| swim state second cue (D_800CE872 / D_800CE84E) | s16 = 1 in ROM | no key (silent) | func_00224F38, func_00224C28 | V: table value is 1 |
| **Footsteps** (state 15 update `func_002251B8`, step phase 8.4375; surface flags 0x200 / 0x400 / 0x800 / index Plr+0x5E0 into table VA 0x800CF1EE+4*idx) | s16 = 1 for every surface | no key -> silent | func_002251B8 | V: all table reads give 1 |
| **Jump** | none (only the special-form voice 6305/6505/6405 at state 5 enter) | - | func_0022CA04 | V |
| **Landing** | none found | - | - | not found |
| **Lava burn** | pain voice through the normal damage path (environment damage kinds 0x4600 ...) | see Pain | func_0021BFBC | H: no lava-specific call found |

### Voice bank offsets (func_00229BE0): pain = 5005+off (2 variants), death = 5015+off - V

| selector | off | pain key: slots | death key: slot |
|---|---|---|---|
| stats+0x80 = 0 | 0 | 5005: 239/238 | 5015: 236 |
| 1 | 1100 | 6105: 172/173 | 6115: 170 |
| 2 | 200 | 5205: 161/160 | 5215: 158 |
| 3 | 300 | 5305: 185/184 | 5315: 182 |
| 4 | 1200 | 6205: 220/221 | 6215: 218 |
| 5 | 100 | 5105: 142/141 | 5115: 140 |
| 6 | 600 | 5605: 196/197 | 5615: 194 |
| 7 | 900 | 5905: 178/179 | 5915: 176 |
| 8 | 800 | 5805: 154/155 | 5815: 152 |
| 9, 15 | 700 | 5705: 190/191 | 5715: 188 |
| 10 | 500 | 5505: 166/167 | 5515: 164 |
| 11 | 1600 | 6605: 149/148 | 6615: 146 |
| 12 | 1700 | 6705: 215/214 | 6715: 212 |
| 13, 16 | 1800 | 6805: no key (silent) | 6815: no key (silent) |
| 14 | 1900 | 6905: 233/232 | 6915: 230 |
| Plr+0xE4 == D_800CE474[0] | 400 | 5405: 205/204 | 5415: 206 |
| Plr+0xE4 == D_800CE474[1] | 1000 | 6005: 202/203 | 6015: 200 |
| Plr+0xE4 == D_800CE474[2], D_80146910=0 | 1300 | 6305: 283/284 | 6315: 282 |
| ... =1 | 1500 | 6505: 281/280 | 6515: 279 |
| ... =2 | 1400 | 6405: 278/277 | 6415: 276 |

Keys `5020+off` exist (e.g. 5020 slot 237) but no caller uses them. Priority 75 (special 6305-6505: 80).

## 3. Pickups (row table at ROM 0xD3DD0: `{msgPtr, u16 itemType, u16 sound, s16 c, ..., handlerFn}`; played as `(sound, pos, 0, -1)` by func_002AC96C / ADB08 / ADBF4 / ADD18 / ADE28 / ADFA0 / AE0D4 / ACBCC / AD65C) - V

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| Special weapon pickups (items 2123 CEREBRAL BORE, 2124 PFM MINE, 2125 IRON CLAW, 2127 SENTRY) | 3596 (0xE0C) | 127 \| d0 p50 | func_002AD65C | V |
| Energy rounds (items 3012, 3013) | 3510 (0xDB6) | 131 \| d0 p50 | func_002ADB08 | V |
| Bullet rounds (3014, 3015) | 3500 (0xDAC) | 16 \| d0 p50 | func_002ADB08 | V |
| Explosive rounds (3016, 3017) | 3520 (0xDC0) | 125 \| d0 p50 | func_002ADB08 | V |
| Ammo pack group (items 3033-3048) | 3580 (0xDFC) | 120 \| d0 p50 | func_002ADFA0 | V |
| 'Retaliation !' event (3030) | 3598 (0xE0E) | 130 \| d0 p50 | func_002ACBCC | V |
| Flag claimed (3031) | 710 (0x2C6) | 208 \| d0 p70 | func_002ACBCC | V |
| Power-up 'The Power is Yours' (3032) | 3530 (0xDCA) | 132 \| d0 p50 | func_002ACBCC | V |
| Talisman / key / feather / flashlight rows (items 1800-1807, 4380-4500; func_002ADE28) | 1 | no key (silent) | func_002ADE28 | V (`c = 299` goes to func_0025E13C, the music-table stinger player; 299 is not a music key -> inaudible) |
| Power-up items 0x1388-0x13C1 | data-driven (descriptor via o->unk18) | - | func_002ACBCC | descriptor table not located |
| Boss-form pickup (item 0xBD6 with stats+0x8F == 1): music switches to key 61 | - | - | func_002ACBCC | V; previous key saved in D_800CE408, restored in func_00227014/00227160 |

Health pack and plain weapon pickups (items 1219-1241) were **not found** in that table region; 3597 (0xE0D, slot 128) is played by func_0020402C for object 0x64E (bonus item) - H.

## 4. Announcer / mode cues - V

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| Joined Team Coyote (stats+0x92 = 0) | 742 (0x2E6) | 224 \| d0 p70 | func_00218B84, func_0020402C (flag bases 0x653-0x656) | V |
| Joined Team Bear (1) | 740 (0x2E4) | 222 \| d0 p70 | same | V |
| Joined Team Cougar (2) | 744 (0x2E8) | 207 \| d0 p70 | same | V |
| Joined Team Viper (3) | 748 (0x2EC) | 211 \| d0 p70 | same | V |
| team 4 / none | 746 (0x2EA) | 210 \| d0 p70 | func_0020402C | V |
| bonus item object 0x64E | 3597 (0xE0D) | 128 \| d0 p50 | func_0020402C | V |

## 5. Lifts / doors / ambient
Platform classes (state machine D_800CD8F0, funcs 207A90..207E74) contain **no sound calls**; the event-rule sound byte (+0x10) is 0 in ROM. Object classes play `info->unk20.h[1]` / `info->unk34.h[1]` (func_00205324 / func_002054E0, mode 4, when != -1): values live in object-type data (not decoded). Lift moving/stop sounds: **not found, probably none**. Level ambient emitters use the playlist `func_0025C97C/func_0025CB2C` with ids from level data (not decoded).

## 6. Menu / UI sounds (2D, overlay A, `func_0025DF54`) - ids V, semantics H

| event | game sound id | slot(s) \| delay \| priority | source | status / note |
|---|---|---|---|---|
| screen/bar confirm (func_0041AA30, 0041B020, 0041B5E4, 00430120, 00430208, 004302F8, 004305D8) | 3700 (0xE74) | 109 \| d0 p50 | ovl_a | V |
| button state 2 (func_0041A4B0) | 3702 (0xE76) | 108 \| d0 p50 | ovl_a | V |
| button state 1 (func_0041A4B0) | 3703 (0xE77) | 110 \| d0 p50 | ovl_a | V |
| item select (func_00419B8C) | 3701 (0xE75) | 112 \| d0 p50 | ovl_a | V |
| go into screen / accept (func_0041DC3C, 00420BC0, 0042FAFC, 0043A7D8, 0041F738, 004264E4, 00429C04) | 3704 (0xE78) | 115 \| d0 p50 | ovl_a | V |
| alternative accept (func_0042FAFC, 0043A7D8, 0041F738, 0041D1A4) | 3705 (0xE79) | 111 \| d4 p50 | ovl_a | V |
| list activate (func_0043BEA0..0043C06C) | 3707 (0xE7B) | 106 \| d0 p50 | ovl_a | V |
| grid activate (func_00420BE8, 004289D8, 0042B5BC, 0043BD90) | 3708 (0xE7C) | 113 \| d0 p50 | ovl_a | V |
| grid select (func_00428A30..00428E5C, 0042B6B4, 0042B824, 0042BAE4) | 3709 (0xE7D) | 114 \| d0 p50 | ovl_a | V |
| cursor change (func_0041F738) | 3711 (0xE7F) | 105 \| d0 p50 | ovl_a | V |
| scroll left (func_0041EE78, 0041EF40) | 3712 (0xE80) | 109 \| d0 p50 | ovl_a | V |
| scroll right / value increment (func_0041B078, 0041B0DC, 0041E170, 0042DEE0, 0042DF88, 00436E74) | 3713 (0xE81) | 105 \| d0 p50 | ovl_a | V |
| menu object init (func_0043C458) | 3715 (0xE83) | 209 \| d0 p70 | ovl_a | V |

## 7. Music (`func_0025E2F4(key)` sets the requested song key; the sequencer `func_0025D470` looks it up with the func_0025DA30 logic; 0 = fade out, unknown key = silence) - V

| trigger | song key | source |
|---|---|---|
| **Main menu / front end** | 52 (0x34) | func_00422728; also after a mission/match (func_00286254, func_002943B8, ovl func_004391F8/4394E8/00436224) |
| **Gameplay, every level** (match start) | **random 56..59** (`0x38 + rand % 4`), only if music is on (current key != 0) | func_0042E080, func_00422C20 case 1 (if the key saved when the menu opened, `w[0x19]`, is 0; else the saved key is restored) |
| Boss-form power-up active (item 0xBD6) | 61 | func_002ACBCC |
| music off / stop | 0 | func_00293EAC, func_004391F8 |
| func_00294378 | 16 (not a song key -> silence) | |
| enable marker | 1 / -1 | func_00445B98 / func_00445200 |
| keys 50, 60, 65 | not started by any literal in the code | H: only reachable via level-event ids (func_002790EC / func_002791C8 pass `Evt.id` to func_0025E2F4 when < 0x100, to the stinger player func_0025E13C when >= 0x100); data-driven, not decoded |

**There is no per-level song table**: all 37 levels share the 56..59 gameplay pool and the menu uses 52. The `value8 = 601+n` field of the level meta table is a text/message id (`func_004030E0`), not a song (corrects the levels_records.md hypothesis).
Debug code (func_00445E28, 004459D0, 00445F3C, 0043EE3C) steps `D_800E63B8` through 0..0x16 into func_0025E2F4 (no valid song keys there).


## 8. Not found / open
* Fire cue of MAG 60 primary, shotgun primary, assault rifle primary, minigun, freeze primary, emaciator primary (no record sound).
* Jump, landing, footsteps (ROM table all `1` = silent), respawn, lava-specific, lift and door sounds.
* Health pack and plain weapon pickup sounds, power-up descriptor sounds, level-event music ids (keys 50/60/65), object-type `info->unk20/unk34` sounds, ambient playlists.
* Meaning of death arg 31 (700) and form revert (720); viewmodel object -> weapon index (assumed 152 + i); exact semantics of swim states 11/12 (water entry/exit assumed from the C# state-role guess).
