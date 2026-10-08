# Gameplay specification (weapons, damage, pickups, modes)

Source: C port in `D:\Projects\TurokRageWars` (read-only), ROM `baserom.us.v11.z64`. **[V]** = verified from code and/or ROM data,
**[H]** = hypothesis. Numbers are mirrored in `tools/Trw.Tools/Spec/SpecData.cs` (`Weapons`, `Impacts`, `HitKinds`, `BotFireTable`,
`Pickups`, `PowerUps`, `Const`); `SpecData.Run([outdir])` writes them as JSON (default `extracted\spec`).
ROM address rules: data VA -> ROM = VA - 0x80000000 + 0xC00; main-image code ROM = VA - 0x1FF400; overlay A ROM = VA - 0x400000 + 0x16E000.

**Correction to gameplay_defs.md:** the earlier doc said there is no weapon/damage table. There is: the weapon descriptor table
`D_800D052C[22]` (ROM 0xD112C) and per-projectile impact tables inside the particle-effect records (section 2). Also the weapon index order
(`Plr+0x62E`) is **not** the order of the menu name table at ROM 0xD7CEC; the real order is below (taken from each descriptor's name pointer).
The bot fire-timing records (section 23) are indexed by this real order.

## 0. Units [V]
- **Tick = 1/15 s.** Frame loop (asm 91DE8.s, ~0x292D78): `dt = vsyncs*15 / refreshRate` stored in `D_800D2988`; at 60 Hz one vsync = 0.25 tick.
  All timers (respawn, power-ups, cooldowns) are in ticks and decrease by `dt`. "Seconds" constants are multiplied by 15 (D_800C8104 = 15.0).
- Health and damage are fixed point: **1 hp = 256**. Pawn health 100 -> 25600 (func_002266E4).
- World units: position unit 25.6 (one level grid cell).

## 1. Weapons [V table, see SpecData.Weapons]
Descriptor (0x60 bytes): `+0` ptr to name pointer, `+4 u16 itemId` (1219+...), `+6 u16 message id`, `+0x14 flags` (1/2 bits queried by func_0022E8C0),
`+0x18 f32 seconds` (state cooldown = value*15 ticks; only CEREBRAL BORE = 4.0 s, func_002328C8), `+0x1C f32 radians` (0.873 BOOMERANG, 1.047 MAG 60, 0.785 BORE; [H] aim cone),
`+0x20/0x24/0x28` fire-mode records set A, `+0x2C/0x30/0x34` set B, `+0x54` state-table pointer, `+0x5C` per-weapon update function.
Fire-mode record (8 bytes): `s16 ammoType, s16 hint (100/200/75/20/1: meaning unverified), s16 shotEffectId, s16 ammoCost`.
Set A is used by a human player when `D_801450B8 == 1`, otherwise set B (func_0021A9A4, ROM 0x1B5A4). Ids differ only for a few weapons (B ids are 12xx variants; mostly equal stats, MAG 60 primary 10 hp vs 12 hp).
Firing (func_0021A9A4): subtract `ammoCost` from `Plr.ammo[ammoType]` (s16 at +0x5F4, clamped at 0; if it reaches 0 and the slot-priority byte is 3 or 7 the weapon is dropped), then spawn effect `shotEffectId` via func_00280094.
Boomerang ids 1044/1069 also decrement a boomerang-in-flight counter (Plr+0x59C).

| idx | weapon | item id | primary (ammo type, cost, effect id) | secondary |
|---|---|---|---|---|
| 0 | WARHAMMER | 1219 | melee, no ammo | - |
| 1 | BOOMERANG | 1220 | energy, 10, 1044 | energy, 20, 1069 |
| 2 | TEK CROSSBOW | 1221 | energy, 10, 1000 (B 1200) | energy, 25, 1003 (B 1201) |
| 3 | MAG 60 | 1222 | bullet, 1, 1009 (B 1211) | bullet, 25, 1073 (B 1212) |
| 4 | SHOTGUN | 1223 | bullet, 1, 7 | bullet, 12, 1081 (B 1220) |
| 5 | ASSAULT RIFLE | 1224 | bullet, 1, 1075 (B 1217) | bullet, 1, 1079 (B 1218) |
| 6 | MINIGUN | 1225 | bullet, 2, 1017 (B 1227) | - |
| 7 | PLASMA RIFLE | 1226 | energy, 10, 1013 (B 1221) | energy, 5, 1046 |
| 8 | FREEZE GUN | 1227 | energy, 3, 1029 (B 1242) | energy, 50, 1051 (B 1245) |
| 9 | FLARE GUN | 1228 | energy, 10, 1025 | energy, 50, 1027 |
| 10 | GRENADES | 1229 | explosive, 1, 1015 (B 1225) | explosive, 1, 1061 (B 1226) |
| 11 | SCORPION | 1230 | explosive, 1, 1004 (B 1204) | - |
| 12 | NAPALM GEL | 1231 | explosive, 1, 1007 | - |
| 13 | EMACIATOR | 1232 | energy, 10, 1019 (B 1229) | energy, 100, 1022 (B 1232) |
| 14 | INFLATOR | 1233 | energy, 10, 1032 (B 1248) | energy, 50, 1074 (B 1251) |
| 15 | CHEST BURSTER | 1234 | energy, 5, 1023 (B 1233) | energy, 50, 1049 (B 1235) |
| 16 | RAPTOR CLAW | 1235 | melee (not selectable) | - |
| 17 | MITE BITE | 1236 | melee (not selectable) | - |
| 18 | CEREBRAL BORE | 1237 | ammo3, 1, 297 | - |
| 19 | PFM MINE | 1238 | ammo4, 1, 1034 | - |
| 20 | IRON CLAW | 1239 | ammo6, 1, 1038 | - |
| 21 | SENTRY TURRET | 1241 | ammo5, 1, 1036 | - |

Ammo types (D_800D0598 names; talisman text confirms 0..2) [V]: 0 bullet, 1 energy round, 2 explosive round, 3 cerebral bore, 4 pfm mine, 5 sentry turret, 6 iron claw.
(There is no teleport mine in this build's weapon table; item 1240 is skipped.)

### Selecting a weapon / auto-switch (func_0022EAFC, func_0022F95C) [V]
Selectable if: weapons 1,2,5,6,7,8,9,10,11,13,14,15: `ammo[rec.ammoType] >= rec.cost` of the primary record; 3 (MAG 60): `ammo[0] >= 3`; 4 (SHOTGUN): `ammo[0] >= 5`;
12 (NAPALM): `ammo[2] != 0` or a live napalm exists; 0,18,19,20,21: always; 16,17: never. Auto-switch (func_0022F95C) walks the per-weapon priority bytes (Plr+0x603 + 2*w, ranks 1..7,
first upward, then downward); MINIGUN additionally needs `ammo[0] >= 11`.

### Ammo caps [V]
- Mode `D_801462D5 == 1`: caps `D_800CE3E8 = {50,50,5,1,3,1,1}` for types 0..6, plus per-player upgrade bytes (talisman bonuses: explosive +4, bullet +5, energy +6 of `D_80102B10[row]`).
- Other modes: `pawn.def +0x108 s32[type]`. Player records: bullet 100, energy 200, explosive 10, types 3..6 = 50 (other characters mostly 50; extracted/defs/intelligence.json `aiParam`).
- Adding ammo (func_00229530): `ammo = min(ammo + n, cap)`.

## 2. Damage model [V]
Projectiles are **particle effect records** (archive section 4, 60-byte records, `effectId`). Effect blob slot 6 (196 bytes, `rec+0x18` pointer) is the **impact table**
(`B18Tab`), indexed by impact state `st` (14 states):
`+0x00 [14] {u16 radiusRaw, u16 damageRaw, s32 param}`, `+0x70 u16 childEffect[14]` (spawned on impact), `+0x8C u16 hitKind[14]`, `+0xA8 u16 sound[14]` (0xFFFF = none).
hp = damageRaw/256 (so 1280 = 5 hp, 5120 = 20 hp); radius = radiusRaw * 0.04 world units. Read by func_0027CCD0 (ROM 0x7D8D0) which passes
`vec[st]` to `func_00265E30(target, projectile, hitKind, -1, pos, blk)`.
Impact state `st` from the surface material (func_00216108): materials 1-2 -> 0, 3-4 -> 1, 5-6 -> 2, 7-8 -> 3 (default), 9,10,0x64,... (character) -> **4**,
11-12 -> 5, 13-14 -> 6, 15-16 -> 7, 17-18 -> 8, 19,20,0x67 -> **9**; a character with a shield/invulnerability state gets 9 (table value is ~10% lower).
State 6 is used when the projectile's "exploded" flag is set while hitting; 10/11 appear as detonation / timer-end entries in explosion child effects.

### Hit kinds (`D_800D1380`, ROM 0xD1F80, {handler, flags}) [V]
Damage kinds map to `func_00266830(target, source, kind, pos, blk, falloff, f, damageClass)` (ROM 0x67430):
kind 2 = direct (no falloff), class 0x80; 3 = area w/ falloff, 0x80; 4 = same +f; 15/16 = falloff, class 0x100; 17/18 = falloff, class 0x200 (explosions); 19/20 = 0x400; 25 = 0x200 within a vertical window.
Kinds used by ROM data: 2 (97 entries), 3 (19), 17 (9), 15 (2); 9, 21, 28 are not damage (flash/blood/other: `damageRaw` field is a different parameter), 0 = sound, 1 = decal.
```
damage(target, src, st-entry {radiusRaw=a, damageRaw=b}, falloffFlag, class):
  m   = hitVolume.multiplier      // model hit-volume +0x68 f32: head bones 2.0, some limbs 0.25 (player model obj 174: bones 10,11 = 2.0; bone 16 = 0.25)
  R   = a*0.04 * srcScale; reach = R + target.radius(func_0024D388)
  if falloffFlag: d = distance(impact, clamped point on target's vertical segment)
                  m *= d > reach ? 0 : (1 - d/reach)
  dmg = b * m                                  // raw units, hp*256
  Blast Shield (0x2): class 0x200 -> 0 ; Bullet Shield (0x800): bullet-type effect -> 0 ; Invulnerability 0x400 -> 0
  Energy Absorption (0x1000): energy-type effect heals victim by dmg, dmg = 0 ; Massive Damage (0x200) attacker x11
  Damage Reversal (0x10): attacker also takes 2x ; Life Force Vampire (0x40): attacker heals dmg*2/3
```
Then func_00219A40 (hit handler, ROM 0x1A640): self-damage x(1/256*0.25*256)=0.25 (D_800C7DB4*DB8*DBC), bot difficulty scale (stats+0x93: 0 -> 0.6, 1 -> 0.8, 2 -> 1.0),
team games with friendly fire off (`G+0x24`, same stats+0x92) -> 0. func_0021BFBC (ROM 0x1CBBC) applies: ignored if invulnerable timer Plr+0x670 > 0 (unless damage type 0x20) or state < 2;
`health = max(0, health - dmg)` (dmg/5 if Plr+0x80C != 0 [H armour]); at 0 -> death (func_0021B468).
Environment damage (hit records built by func_00216488): fall/lava 0x4600 = 70 hp, 0x30200 = 770 hp, 0x40000 = 1024 hp (kill), 0x3E700 = 999 hp (out of bounds), poison tick (func_0022BE10) = `n = max(1, (150 - hp)/15)` hp per 300 accumulated ticks (H on rate).

### Per-weapon direct / splash damage (hp) [V from tables; set A/B differ where noted]
| weapon | primary | secondary |
|---|---|---|
| CROSSBOW | 20 direct (15 airburst) | 65 |
| MAG 60 | 10 (B: 12) | 50 (shielded 45) |
| SHOTGUN | 6 per pellet (radius 102) | 25 (kind 3, radius 102) |
| ASSAULT RIFLE | 17 | 5 |
| MINIGUN | 5 per bullet | - |
| PLASMA RIFLE | 35 direct, 40 splash r153.6 | 7 per hit |
| FREEZE GUN | 5 | 10 direct, 15 + 30 follow-up blasts |
| FLARE GUN | 20 direct, 5 splash r102 | 12-20 area r256 (kind 15) |
| GRENADES | 50 splash r307 | 60 splash r410 |
| SCORPION | 70 splash r307 | - |
| NAPALM GEL | 70 (r410), 45 burn | - |
| EMACIATOR | beam (no table, [H] ray) | 65 direct (shielded 25) + 25 splash r102 |
| INFLATOR | 10 | 10 |
| CHEST BURSTER | 10 (A) / 5 (B) | 5 |
| CEREBRAL BORE | 10 per pulse r102 | - |
| PFM MINE | 192 max-radius 614 (kind 2), 60 follow-up | - |
| IRON CLAW | 30 splash r307 | - |
Full tables: `SpecData.Impacts`. BOOMERANG, WARHAMMER, claws, MITE BITE and SENTRY TURRET have no impact table: their damage comes from object/animation code (not decoded).
Plausibility: MAG 60 10 < shotgun total < crossbow 65 < grenade 50-60 splash < SCORPION 70 < PFM 192. All consistent.

Unknown: fire cadence (rate of fire) of each weapon: it is driven by the viewmodel animation clips and their event ids (events 28 bytes, func_002476B8) plus the cooldown
`f18*15`; not extracted. Bot trigger timings are in `BotFireTable` (ticks; patterns: 0-3 func_0020A6D8, 4 func_0020A7B0, 5 func_0020A028, 6 func_0020A458, 7 func_0020A2D4, 8 hold).
Projectile speed/gravity/lifetime live in the effect emitter blobs (slots 7-14) and are not decoded.

## 3. Health, regen, score [V unless marked]
- Max health (func_0022ABF0): `pawn.health(100) << 8`; boss types (stats+0x80 in 11/12) 400 hp, (13/14) 300 hp; plus the human upgrade `D_80102B10[row].unk0` when `D_801462D5 == 1`. Frag Fest team flag: 10 hp (0xA00).
- Regeneration power-up: +68.2667/256 hp per tick (4 hp/s) until max (func_00220D20). Passive regen in missions: `pawn+0x24 * 256 * dt/15` [H].
- Low health warning below 15 hp (0xF00).
- Respawn: death state 0x15 accumulates dt, at > 75 ticks (5 s) -> respawn; spawn point = candidate (<= 32 from archive section 8 group of the level) maximising min distance to all live actors (func_0021B1E4). No spawn protection found (Plr+0x670 only set by Invulnerability).
- Score (func_00219A40): suicide: suicides+1, score -1; kill: score +1 (team games: +1 enemy, -1 teamkill). Frag Tag: killing the "it" player gives +1 score and +1 tag. Stats u16 at stats+0: suicides, +2 deaths, +4 score, +6 tags, +0xA bonus.
- Rank titles = number of set achievement bits (of 50): 0-1 NOVICE, then 2 per rank to 21 (rank 10), 3 per rank to 36 (rank 15), 40-44 -> 17, 45-49 -> 18, 50 -> 19 CHIEFTAIN (func_00424FC8).

## 4. Pickups [V]  (SpecData.Pickups, PowerUps)
Intelligence class-3 records are spawners (state table `D_800CD79C`, spawn func_00205720 -> actor via object-type table). Grants (func_002AC3F0 + row tables ROM 0xD3DD0..):
| rec | grant | respawn (ticks, random) |
|---|---|---|
| 122,123 | flag (CTF/tag), "Flag was claimed" | 27-54 |
| 140 | IRON CLAW + 1 ammo6 | 300-675 |
| 141 | CEREBRAL BORE + 1 ammo3 | 300-675 |
| 142 | SENTRY TURRET + 1 ammo5 | 300-675 |
| 143 | PFM MINE + 3 ammo4 | 300-675 |
| 144 | random power-up (1<<(rand%13)) | 300-675 |
| 145 | "Retaliation !" event | 450-750 |
| 146 / 147 / 148 | +100 energy / +50 bullet / +5 explosive | 105-225 |
| 149 / 150 | +25 / +50 health (refused at full health) | 300-600 / 450-750 |
First spawn after level start: rand(45,60) ticks. With D_801462C8[0x1D] and n >= 3 players the delay is scaled by (100-(n-2)*25)%. Weapons are only taken if not owned.
Power-ups (func_002ACBCC, bits of Plr+0x122C, one at a time, shared timer Plr+0x1230): see `PowerUps` (durations in ticks: Slow Time 105 (others slowed x0.25 for 195), Blast Shield 300, Regeneration 225,
Petrify 150 (victim frozen 75), Damage Reversal 150, Invisibility 180, Life Force Vampire 225, Speed Burst 225 (dt x1.333), Teleportation 450, Massive Damage 270 (x11), Invulnerability 180, Bullet Shield 300, Energy Absorption 300).

## 5. Game modes [V setup, H names]
Config struct `G = D_801468A0` set by func_0042EF50(time, fragLimit, tagLimit, captureLimit, tagFlag, teamFlag, ctfFlag): `+0x14` time (minutes*900 ticks; mission types 2/3: seconds*15; 0 -> no limit),
`+0x18` frag limit, `+0x1C` ended, `+0x24` team mode, `+0x2C[5]` team frags, `+0x40[5]` team captures, `+0x54` tag mode, `+0x58` tag limit, `+0x78` CTF, `+0x7C` capture limit.
| game type (D_80154028) | mode | win condition |
|---|---|---|
| 0 | CTF | captures[team] >= captureLimit (func_002282C8) |
| 1 | Frag Tag | sum of tags >= tagLimit (func_002281CC) |
| 2 | Deathmatch / Blood Lust | score >= fragLimit (func_0022804C) |
| 3 | Team Blood | team frags >= fragLimit |
Time limit: counts down only while a player is active; at <= 0 the match ends (func_0022A738). Teams: Coyote, Bear, Cougar, Viper (0..3, 0xFF none).
Campaign challenge type (D_801462D5): 1 lives/gauntlet [H], 3 Time Trial, 4 Frag Fest. CTF: capture within 122.88 of own base, carrier death resets the flag. Boss matches: `G+0x98`, boss hp absorbed first (100), defeat needs 3 kills of type 0xE.

## 6. Not determined
Weapon fire cadence and viewmodel timings; projectile speed/gravity/lifetime (effect blobs 7-14); boomerang/melee/beam/sentry damage; meaning of the second s16 of fire-mode records;
enemy-class (non-pawn) stats: Rage Wars has only pawns, all with health 100; campaign enemy AI; player movement constants (see below if present).

## 7. Player movement and bot AI (from a separate pass; V = verified from code/ROM constants, per tick, dt = 1.0)
State numbering: state s uses row `0x800CE8C4 + 24*s` (the row at 0x800CE8DC is state 1). Row = {kind, enterFn, updateFn, ptr, flags, animId}.
Roles: 0-1 intro/free move [H]; 2 idle (update func_002243E4); 3 walk/run (func_002246E8, anim 0x7DA); 4 crouch [H]; 5 jump rise (anim 0x7EE); 6-8 air/air-strafe/fall [H];
9-12 swim [H] (water blocks jumping); 13-14 deep swim [H]; 19-21 dead/respawn (0x15 update func_0022D2F8); 22-33 hit/death anims (0x1B58..0x1D51).
Alternate anim set (second table D_800CED30): 0x5E24 idle, 0x5E29 walk, 0x5E2A back, 0x5E2B/0x5E2C strafe, 0x5E2E fall.

Ground movement (func_002233CC; config struct fwdAccel, fwdMax, fwdDecel, strafeAccel, strafeMax, strafeDecel [V]):
| config | use | forward accel/max/decel | strafe accel/max/decel |
|---|---|---|---|
| D_800CE730 | normal ground and air | 10.24 / 30.72 / 13.44 | 10.24 / 35.84 / 13.44 |
| D_800CE754, D_800CE778 | slippery floors [H] | 4.608/34.56/1.728 ; 2.88/21.6/1.08 | 2.765/20.74/1.037 ; 1.728/12.96/0.648 |
| D_800CE7C0 | wading (p+0x718 > 5.12) [H] | 5.12 / 15.36 / 3.84 | 5.12 / 17.92 / 3.84 |
| D_800CE82C | surface swim | 7.68 / 23.04 / 5.76 | 7.68 / 26.88 / 5.76 |
| D_800CE8A4 | deep swim | 1.024 / 7.68 / 0.512 | - |
```
v += stick*accel*dt; clamp |v| <= |stick|*max; over-max decays 0.5*decel; reversing multiplies accel by 1.5; no input: v -= decel*dt
combined vector clamped to 30.72 (|fwd|>=|strafe|) else 35.84;  pos += vec * dt * pawn.timeScale
```
Pawn time scale (func_002228E4) = `def.speedScale` (1.0/1.1/1.2), bots additionally x0.833/0.917/1.0 applied twice (difficulty stats+0x93 = 0/1/2). Pawn +0xEC/+0xF0 (40.96/56.32) are not movement speeds: +0xF0 = hit radius 56.32, +0xF4 = height 184.32, eye height = 0.9*height = 165.89, head-bob amplitude 10.24*bob (bob capped 0.1).
Jump (func_002246E8/func_0022CA04): allowed when jump pressed and time since grounded <= 11.25 ticks, not in water, def flag 2; vy = 389.12 * def+0x20 (1.0; 481.28 on level 0x1DB1); human re-applies the impulse each tick until 3.0 ticks held (variable height); bots fixed 3 ticks.
Landing with vy < -51.2 calls a surface hook. Gravity and fall damage: not found. Dash disabled (speed 0), cooldown 7.5.
Look (func_002231B0): yaw target = stick*20 deg, moved by `(target-ang)*rate*dt`, `rate = ((sens/128-1)*0.5+0.875)*0.9*(1-0.99*zoom)`; pitch absolute = stick*90 deg, limits +-90 (wading -45..+90).

Bot AI (brain at Plr+0x1454): target candidates up to 8, excluding self, teammates, dead. Detection radius `f = skill[2]*0.01*diff` (diff 0.9/1.0/1.1): front (<=45 deg) 1536f, side (<=135) 921.6f, rear 512f; score = distance x1.1 side / x1.2 rear / x0.8 if human; needs line of sight (rays 102.4 above); lowest score wins.
State choice (func_0020FA10): seek health if hp% < (1 - skill[0]*0.01*diff)*80; target visible -> chase/attack; else wander. Weapon choice: random among pawn itemIds filtered by percent tables (+0x8C,+0xAC,+0xCC), 21% switch chance per call.
Combat movement: strafe magnitude `skill[3]*0.01*diff*0.6`; desired range random 600-780 units rerolled every 12-15 ticks; bots effectively never jump (jump chance truncates to 0).
Aim: random-walk error +-15 deg per call within +-70/50/30 deg (easy/normal/hard); turn stick 1.0 (>60 deg), 0.5 (>30), 0.25 (>10), 0.125 (>4), else 0.1, none within 2 deg. Trigger timing: `BotFireTable`.
Not determined: gravity, fall damage, knockback, wall-collision capsule, step height.
