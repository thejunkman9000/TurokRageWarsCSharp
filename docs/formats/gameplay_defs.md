# Gameplay definition data (weapons, characters, pickups, spawns, effects, modes)

Big-endian. **[V]** = verified against game code (C port in `D:\Projects\TurokRageWars`, asm in `port/build/asm_func/`, m2c in `m2c_out/`) or by
checking every instance with the C# parser; **[H]** = hypothesis (plausibility only). Code: `tools/Trw.Tools/Defs/` (`DefsCommand.Run`,
output `extracted/defs/*.json`). ROM data VA rule for the main image: **VA = ROM - 0xC00 + 0x80000000** (data symbols `D_800Dxxxx`).

## 0. Key finding: the archive sections have names  [V]
`CScene::Construct` = **func_0044C410** (overlay tail, ROM 0x1BA410; asm `port/build/asm_func/func_0044C410.s`, m2c draft `port/build/ll/nm_func_0044C410__m2c.c`)
loads archive section N into a CScene field and passes a label string. Labels (strings at ROM 0xCAC40..0xCB0xx, VA 0x800CA040..) give every section its game name:

| sec | game label | CScene | callback | content in this ROM |
|---|---|---|---|---|
| 0 | objects index | +0x24/+0x54 | - | 347 object containers (models agent) |
| 1 | **intelligence** | +0x78 | func_0044E584 | 169 definition records of 9 classes (section 2 below) |
| 2 | object types | +0x94 | - | u16[347] attribute per object (all 7 except a few; [H] flags) |
| 3 | texture sets index | +0x20/+0x50 | - | 507 textures |
| 4 | **particle effects** | +0xB4 | func_0044E454 | 224 x 60-byte effects (section 6) |
| 5 | levels index | +0x1C/+0x4C | - | level directory |
| 6, 7 | (unnamed ROM-level loads) | +0x38/+0x44, +0x3C/+0x48 | - | 37 x container of 32-byte blocks |
| 8 | **warp dests** | +0x10C, +0x90 | - | **deathmatch spawn points** (section 5) |
| 9, 10 | binaries index / object types (2nd) | +0x28/+0x58, +0x98 | - | empty stubs in this ROM |
| 11 | particle swooshes | +0x70 | - | 18 blobs (72/120 bytes) [H: trail/beam params] |
| 12 | dynamic lights | +0xA0 | - | 22 x 8 bytes (RGBA-ish + radius) [H] |
| 13 | curves | +0xA4 | - | 36 x 24 bytes [H] |
| 14, 15 | rumbles, rumble types | +0xAC, +0xA8 | - | u32[24] each |
| 16, 17 | graphicset index / type | +0x2C/+0x5C, +0x9C | - | 69 blobs + 69 ids |
| 18 | Cinemas index | +0x34/+0x74 | - | item 0 level meta (levels agent), items 1..37 cinemas |
| 19 | link group types | +0xB0 | - | u32[1] |
| 21 | **BLLevelData Index** | +0xC/+0x10 | - | 36 x 3382 bytes: campaign progression (section 7) |
| 23 | **Bloodlust Weapon Data** | func_0020A604 | - | bot weapon-fire configs (section 4) |

"BL" = **Blood Lust**, the game's multiplayer deathmatch mode name (Rage Wars' campaign/multiplayer is "Blood Lust" based).

## 1. What does NOT exist as data  [V by absence]
There is **no weapon damage / ammo / projectile table and no enemy health table in the archive or in an obvious .data array**. The archive sections
above are everything the scene loader reads. Damage, ammo counts, projectile speeds and rate of fire are code constants and per-class behaviour tables
(`D_800D0680[class]`, class < 15, selected in func_00246690) inside overlay A, not yet extracted. The only per-weapon numeric tables found are the bot
fire-timing configs (section 4). Rage Wars has no single-player enemy waves: "enemies" are bots using the *pawn* definitions (section 3).

## 2. Section 1 "intelligence": the definition table  [V chain, H field names]
Container of 169 variable-size records. First u32 = **class**. Common header (all classes): `+0 u32 class`, `+4 u32 flags (0x139 / 0x39 / 0x239 / 0x13D)`,
`+8 f32 activationRadius` (512.0; 307.2 for pawns) **[V]**: the spawn code stores `radius^2` in the actor class block (`cls->w24 = f8*f8`, func_0024FD4C / func_0024F940).

Chain [V]: placement u16 -> `func_0028CF48(scene, idx)` = `intelligence[idx]` -> stored as actor `def` (actor+0x18) -> `*def` is the class, `D_800D0680[class]`
selects the class function table (func_00246690). Level loader func_0044D408 checks `*def == 11` to find the player pawn. Loader func_0044E584 multiplies
class-1 floats (+0x28,0x30,0x34,0x50,0x54,0x58) and class-11 floats (+0xEC,+0xF0) by the constant at D_800CA3F4 which is **1.0 in the US ROM**
(speeds; PAL frame-rate fixup) - so +0xEC/+0xF0 of the pawn are speeds **[V]**.

| class | n | size | indices | guess [H] | notes |
|---|---|---|---|---|---|
| 0 | 60 | 40 | 0-52 etc | prop / decoration | extentA/B 61.44 at +0x18/+0x1C; every one of the 10169 sector placements in all levels uses a class-0 record |
| 2 | 6 | 120 | 151-156 | mover (door/platform) | only referenced by level 36 (cinema) |
| 3 | 15 | 64 | 119,120,122,123,140-150 | **pickup** | +0x2C f32 scale 0.35 (the same scale sector objects use), u16 pair at +0x28 (object ref, 0x35), paramA/B floats (27/54, 300/675, 450/750) |
| 4 | 4 | 136 | 157-160 | turret | angles 2pi, 0.2618 rad (15 deg), 768, 3.41 |
| 8 | 2 | 40 | 53, 138 | trigger | range 900.0 |
| 9 | 23 | 32 | 54-74,121,130 | marker/script event | +0x18 id e.g. 0x1B86 |
| 10 | 3 | 48 | 76,77,129 | light | RGBA ffff00ff, radius 512 |
| 11 | 44 | 392 | 79-118, 124-127 | **pawn (player / bot character)** | next section |
| 13 | 12 | 40 | 134-137, 161-168 | zone / trigger | targetId (0xD35.., 0x1770..) |

Per-level usage [V, from the decoded levels]: every multiplayer level has exactly one pawn placement (index 79 = the player start; 37 uses) and 4..13
pickup placements; the most used pickup records are 146-149 (73/71/67/60 placements), then 144 (34), 150, 140-143, 145. Which pickup index grants what
(health vs ammo vs weapon) is **not decoded** (the effect lives in code: strings `2 health` ... `ammo pack` etc. at ROM 0xD4290.., pointer table 0xD7C70).

## 3. Pawn definition (class 11, 392 bytes)  [V size/count, H names]
| off | type | name | notes |
|---|---|---|---|
| 0x0C / 0x10 | u8[4] x2 | appearanceA/B | 43 distinct values - per-character look/colour selectors [H] |
| 0x14 | u32 | field14 | 27 / 43 / 59 (bone or part count? [H]) |
| 0x18 | s32 | **health** | 100 in all 44 records (constant; word does not vary) [H] |
| 0x1C | f32 | speedScale | 1.0 / 1.2 / 1.1 [H] |
| 0x28 | f32[9] | skill | 40..85 per character, AI aggression/accuracy by category [H] |
| 0x4C | s32[7] | itemIds | ids in 0x4C3..0x4D1 (1219-1233) or -1 / 100; the same id space the progression header uses [H: weapon/talisman ids] |
| 0x6C | f32[8] | multiplier | 1.0 each |
| 0x8C | s32[24] | percent | 0 or 100 per damage class (resistance mask) [H] |
| 0xEC / 0xF0 | f32 | speedA / speedB | 40.96 / 56.32 (walk / run units per tick?) **[V that they are speeds]** |
| 0xF4,0xF8 | f32 | rangeA/B | 184..300 |
| 0xFC | f32[3] | aiScale | 0.5 / 1.0 / 1.5 |
| 0x108 | s32[32] | aiParam | 50/100/200/10 |
The ROM character-name table (ROM 0xD7E84: TUROK, WARRIOR, MASTER, ADON, SEEKER, HUNTRESS, CAMPAIN, SLAVER, TYRANT, FIREBORN, ICELORD, ENTRAIL, MITES,
COMPIES, TEDDIES, JUGGNAUT, SLOTH, MAULER, DRONE, GRUNT, SLAVE, SOLDIER, CAPTAIN, KING, BASTILLE, TAL SET, SYRA, SYMBIONT, GUARDIAN, RAVAGE, CYCLOPS,
LORDDEAD, MAGGOT, VILE, ELITE, BIOBOT, KILLBOT, DEATH, REAPER, MAW, RAPTOR, SLAG, CLAW, WARCLUBS) has 44 distinct names = 44 pawn records. Pairing in order is **[H]**
(`characters.json` `nameGuess`).

## 4. Section 23 "Bloodlust Weapon Data": bot weapon-fire configs  [V]
`func_0020A604` copies records 0..43 (28 of the 32 bytes) into `D_800F7D20` = `B2Cfg[22 weapons][2 fire slots]`. `func_00209E80` (bot fire controller) reads
`owner->weaponIndex` (actor+0x62E, s16) and `slot = actor+0x594 - 1`, then switches on `D_800CDA40[w][slot]` (u32 table at ROM 0xCE640) and calls one of the
handlers: 0-3 -> func_0020A6D8, 4 -> func_0020A7B0, 5 -> func_0020A028, 6 -> func_0020A458, 7 -> func_0020A2D4, 8 -> hold trigger.
Record = 7 x s32 in this order **[V from handlers: value = base + rand()*spread]**: `delay, delaySpread, burst, burstSpread, pause, pauseSpread, threshold`.
Weapon index 0..21 = WARHAMMER, RAPTOR CLAW, MITE BITE, BOOMERANG, TEK CROSSBOW, MAG 60, SHOTGUN, ASSAULT RIFLE, MINIGUN, PLASMA RIFLE, FREEZE GUN, FLARE GUN, GRENADES,
SCORPION, NAPALM GEL, EMACIATOR, INFLATOR, CHEST BURSTER, CEREBRAL BORE, PFM MINE, IRON CLAW, TELEPORT MINE, SENTRY TURRET (ROM 0xD7CEC pointer table; 22 matches the
table size so the indexing is solid; the 4 extra names are attract/repulse messages). Records 13, 17, 23 (SHOTGUN slot 2, MINIGUN slot 2, TELEPORT MINE slot 2) are all zero.
These are tick counts at the game's logic rate (not seconds).

## 5. Section 8 "warp dests": multiplayer spawn points  [V layout, H yaw/team]
`container(2)`: `u32 groupId[321]` + `20-byte points[321]`: `f32 x,y,z; f32 yaw (0, +-pi/2, pi) [H]; u16 team/zone (0/1) [H]; u16 0xFFFF`.
Group ids are `3010 + 10*n` with 8 points each (3090, 3110, 3160, 3170, 3210, 3260, 3270, 3280 have 9-14); `n` = level slot, matching level-meta
`field8 = 601 + n` **[V join; all 36 levels match]**. Extra singletons: 3101, 3370 (cinema), 6000, 6010, 6011, 6015, 6019, 6020, 6100, 6105 (campaign/cinema warps).

## 6. Section 4 "particle effects"  [V loader fixups, H rest]
`container(3)`: `[0]` parameter blob (31 040 bytes), `[1]` `u32 effectId[224]` (ascending 1..1287), `[2]` 224 x 60-byte records.
func_0044E454 (asm ok): word 5 (+0x14) = texture id (-1 none) resolved by func_0028C174; words 6..14 (+0x18..+0x38) = byte offsets into the blob turned into pointers.
Words 0..4: flags (`0x47000`...), `u16 + 2 bytes`, bytes, bytes, `0x300/0x700` [H]. The blob pieces are small keyframe/parameter arrays (floats 1.0, 10.24, 0.3, colour bytes `ffffff..`,
`0x41230000`=10.25..). Emitted as `blobRefs` with sizes; not further decoded.

## 7. Section 21 "BLLevelData": campaign progression  [V access, H meaning]
36 blobs x 3382 bytes (index = level id). 22-byte header (`u32 0`, `u16 0x0100`, `u16 ids[8]`) + `tbl[4][20]` x 42-byte entries. Verified from func_00425E50 (accessor
`tbl[b][c]` memcpy 0x2A), func_004280F0, func_00427690, func_004263BC: entry byte[1] = 1-based id of the next level, bytes 2..4 fallbacks; `tbl[0][c]` indexed by selected
character (player entry +0x80), `tbl[1]/tbl[3]/tbl[2]` for mode cfg 2/3/4; header ids >= 0x4C3 set `player.flag[id-0x4C3] = {1, slot}` (talisman/unlock flags).
Entries 11/12 of group 0 contain stale host memory ("Users...") and are skipped when empty. Rest of the entry: unknown (objective type/params; strings `capture the flag %d times`,
`acquire %d frags`, `in %02d seconds or less`, `with %d lives`, `defeat %s`, `every man for themselves` at ROM 0xD51D8.. are the objective formats).

## 8. Level placement  [V]
**Part 3** (stride 0x28, column-major) = `B11Spawn` consumed by func_0024B398 -> func_00246690:
`+0 s32 -1 | +4 f32 pos[3] | +0x10 f32 scale[3] (0.5) | +0x1C u16 objectIndex (archive sec 0, 0xFFFF = look up by type) | +0x1E u16 actorType (actor+0xE4) |
+0x20 u16 eventRef (0xFFFF none, else 32-byte entry via func_0028B370) | +0x22 u16 intelligence index | +0x24 s16 yaw (* 9.5877e-5 = 2pi/65536) | +0x26 u8 flags (actor+0x100)
| +0x27 u8 link (0xFF none)`. Decoded content of all 37 levels: 1 pawn (intelligence 79, objectIndex 174, actorType 1120, flags 4) + 4-12 pickups (objectIndex 274, actorType 1604, flags 1).
**Sector section 0** (stride 0x1C, func_0024F940): `+0 s32 collisionId | +4 f32 scale | +8 u16 objectIndex | +0xA u16 eventRef | +0xC u16 flags (0x40 = fixed 102.4 bounds) | +0xE u16 intelligence |
+0x10 s16 y (* 5.12) | +0x12 s8 bbox[6] (min x,y,z then max x,y,z interleaved: 12=minX 13=maxX 14=minY 15=maxY 16=minZ 17=maxZ; * 10.24 +-0.5) | +0x18 u8 rot (bits0-1 quarter turns about Y, 0x10 negate x, 0x20 negate z) | +0x19 u8 x | +0x1A u8 z`
with x,z multiplied by the **position unit 25.6 = first float of level part 0** (func_0028795C passes it as arg4).
**Sector section 1** (stride 0x34, func_0024FD4C): `+0 s32 collisionId | +4 f32 pos[3] | +0x10 f32 scale[3] | +0x1C u16[6] bounds (high halves of floats: Trunc16ToF32) | +0x28 u16 objectIndex |
+0x2A u16 eventRef | +0x2C u16 intelligence | +0x2E u8 flags (0x40 = fixed 102.4 half-extent box) | +0x2F s8 quat[4] (/127)`.
All 10169 sector section-0/1 placements use class-0 (prop) intelligence records. Part 9 nav nodes: see levels_records.md (objectType values still unexplained).
Not decoded: parts 1, 6, 7 (callbacks func_0044D024 / scene+0x1B300 / func_0044CD58), sector section 3 (0x4C records -> func_00276D20).

## 9. ROM name tables (`rom_string_tables.json`)  [V]
Pointer tables at ROM 0xD7C70..0xD8300: pickup/event messages (31), weapon names (26), power-up messages (16: Slow Time, Blast Shield, Regeneration, Petrify, Damage Reversal,
Invisibility, Life Force Vampire, Speed Burst, Teleportation, Massive Damage, Invulnerability, Bullet Shield, Energy Absorption, ... Shield), team/frag messages, ammo labels (Energy/Bullet/Explosive
rounds, Ammo pack), 19 rank titles (NOVICE ... CHIEFTAIN), character names, enemy display names (MANTID DRONE ... MANTID MITES), objective formats, 36 level achievements + 14 others, mode labels
(CTF, TBL, BL, FT, GAUNTLET, FRAG FEST, TIME TRIAL), 37 ACQUIRED messages (talismans, weapon upgrades). Direct menu strings 0xD6F80..: modes `deathmatch`, `blood lust`, `frag tag`, `team blood`,
`training` (archery, shooting, decathalon), 19 multiplayer level names + 9 texture sets; weapon toggles (charge dart rifle, firestorm, assault rifle, plasma rifle, cerebral bore, grenade launcher,
scorpion launcher, torpedo launcher). Cheat strings (all weapons, unlimited ammo, big head, ...) at 0xD64B0.. .
Game modes (`game_modes.json`): per-level mode parsed from the original map file name [H]: BL 11, TBL 10, FT 4, CTF 2, BOSS 3, 7 unknown.

## 10. Next steps
1. Extract the per-class behaviour tables `D_800D0680[15]` (VA 0x800D0680 = ROM 0xD1280) and the overlay-A constants for damage/ammo/fire rate; best entry points: functions that read `actor+0x62E`
   (src/ovl_a: func_00444848/0044489C/00444910/00444960/00445AB8/00445C08) and the weapon switch code.
2. Link the 15 pickup records (class 3) to effects by reading the pickup class handler.
3. Decode level parts 1/6/7 (read func_0044D024, func_0044CD58) and sector section 3.
