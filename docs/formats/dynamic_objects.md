# Dynamic (moving / interactive) map objects

Status tags: **[V]** verified (game code in `D:\Projects\TurokRageWars` read and, where possible, checked against the data of all 37 levels),
**[H]** hypothesis. Code: `tools/Trw.Tools/Dynamics/` (`DynamicsCommand.Run`), output `extracted/dynamics/NN_name.json`, `summary.json`, `_objects/obj_<id>_<model>.obj`.
Program.cs line: `if (cmd == "dynamics") return Trw.Tools.Dynamics.DynamicsCommand.Run(args.Skip(1).ToArray());`
Units: world units, **tick = 1/15 s** (`D_800D2988` = dt in ticks, all timers are in ticks). Coordinates are raw game coordinates (the viewer mirrors X).
ROM offsets: main-image code `ROM = VA - 0x1FF400`, overlay A `ROM = VA - 0x400000 + 0x16E000`, data `ROM = VA - 0x80000000 + 0xC00`.

## 0. Result in one page

The shipped multiplayer levels contain **no doors, drawbridges, crushers, jump pads, rotating or swinging objects** as data: every one of the 10,169 sector
placements is a class-0 prop (`intelligence` record class 0), sector array 2 ("animated objects", 0x1C8-byte actors) and sector array 4 are empty in all 203
sectors, no placed static object has animation clips, level part 12 is all zero in all 37 levels. Everything dynamic is a **part-3 actor** (the stride-0x28
array the levels agent called "placed actors / pickups"), whose behaviour comes from its *intelligence* record (archive section 1) class:

| class | what | count (37 levels) | where |
|---|---|---|---|
| 2 "mover" | **lift / ferry platform** (object 297) | 6 | levels 7 (2), 9, 25 (2), 32 |
| 13 "zone" | **teleporter** (object 330) | 8 | levels 16 (2), 19 (2), 20, 25, 26 (2) |
| 10 | **two-state indicator / pad beacon** (objects 148, 149, 203), switched ON by stepping on its floor region | 23 | levels 6, 7, 15, 16, 21, 23, 26, 29, 33, 34 |
| 3 rec 122 | CTF flag spawner (object 184), enabled/disabled by event rules | 4 | levels 7, 16, 23, 33 |
| 9 "marker" | animated prop (object 140) | 1 | level 22 |
| 3 rec 140-150 | item pickups (object 274) | 355 | all (already in `nav.json` actors; not repeated here) |
| 11 | pawn start (object 174) | 37 | all |

Level part 7 (previously "moving objects") is **not** a moving-object list: it is the **event rule table** (section 4) that switches indicators and flag spawners.
Water and lava are ordinary placed objects with animated textures (see `transparency_and_animation.md`), not dynamic objects.

## 1. Part-3 actor record (stride 0x28, column-major)  [V from func_0024B398 -> func_00246690]
`+0 s32 -1 | +4 f32 pos[3] | +0x10 f32 scale[3] (0.5) | +0x1C u16 objectId (archive section 0; 0xFFFF = look up by type) | +0x1E u16 actorType (-> actor+0xE4) |
+0x20 u16 regionTriangle | +0x22 u16 intelligence index | +0x24 s16 yaw (* 2pi/65536) | +0x26 u8 flags (-> actor+0x100) | +0x27 u8 link (0xFF)`.
**Corrections** to `gameplay_defs.md`/`levels_records.md`: `+0x20` is *not* an event id: `func_0024B398` passes it to `func_0028B370` which returns the 0x20-byte runtime
ground triangle with that index (part 1 section 2), stored as `actor+0x14`; the values 0..17 are triangle indices. `+0x24` is a signed 16-bit yaw (values 49153 = -16383 = -90 degrees).
The part-3 **index** of an actor is its identity everywhere (event rules, active bits): actors are numbered in file order, the pawn is 0.
Model of an actor: `objectModelList[actor+1 % count]`; `actor+1` is 0 unless the class sets it (indicators do, section 5). Every referenced object exists (430 of 430 checked).

## 2. Lifts (class 2, 120-byte intelligence record, object 297)  [V]
Object 297: one 94-vertex skinned slab, model space ±205 x ±205 x ±51 (Z-up), hull AABB ±204.8 x ±204.8 x ±51.2 in object section 0, texture 292, 1 clip. Placed with scale 0.5
(204.8 units wide, fits the 307-wide shafts of Dire Straight). Records 151..156; part-3 `intelligence` selects one.

Definition fields (`info = def + 0x14`), numbers per record 151 / 152 / 153 / 154 / 155 / 156:
| def off | name | meaning | values |
|---|---|---|---|
| +0x14 | info flags | bit 4 would clear actor flag 0x2000 (unused) | 2 / 3 |
| +0x18 u16 | hasHorizontal | non-zero: horizontal offset applies (func_00206DD4) | 2 |
| +0x38 u32 | flags24 | trigger conditions (below) | 0x04000000 (151), 0x04000100 (152-156) |
| +0x3C..0x3E | bobMode, easingByte, yawMode | easingByte 0 = cosine ease, 1 = linear | 151: 0,1,0; others 0,0,0 |
| +0x40 f32 | rise (dy) | **153.6 / 286.72 / 921.6 / 450.56 / 460.8 / 614.4** | |
| +0x44 f32 | horizontal distance | 317.44 (151 only), else 0 | |
| +0x48 f32 | heading | dx = d sin(h), dz = d cos(h); -pi/2 for 151 | |
| +0x4C f32 | travel ticks | 52.5 / 15 / 37.5 / 22.5 / 15 / 22.5 | |
| +0x50 f32 | yaw sweep | 0 in all | |
| +0x54 f32 | bob phase (x 2pi) | 0 | |
| +0x58 f32 | bob amplitude | 0 in all (no visible bob; 151/152 carry rate 0.2094 rad/tick at +0x5C) | |
| +0x60 f32 | wait at A (ticks) | 15 / 7.5 / 15 / 15 / 7.5 / 7.5 | |
| +0x64 f32 | wait at B (ticks) | 15 in all | |

State machine `D_800CD8F0` (ROM 0xCE4F0): id 0 wait at A (update func_00207ABC, enter func_00207A90), id 1 rising (func_00207BB8: `p += dt / travel`, enter emits event mask 0x10000),
id 2 wait at B (func_00207C94), id 3 lowering (func_00207D90: `p -= dt / travel`, enter emits 0x20000), id 4 disabled. Transitions are the event numbers passed to `func_00214178`
(0 -> 1 after waitA, 1 -> 2 at p >= 1, 2 -> 3 after waitB, 3 -> 0 at p <= 0). The wait timer `state+0x40` counts dt and is reset to 0 whenever the condition fails.
Pose (func_00206DD4): `p' = ease(clamp(p,0,1))`, `ease(p) = (1 - cos(pi p)) / 2` (func_00265734) unless easingByte == 1 (linear);
`pos = start + p' * (dx, rise, dz)`, `dx = d sin(h)`, `dz = d cos(h)`; yaw = start yaw + p' * yawSweep (yawMode 0). Bob: `y += amp * sin(phase)` with `phase += rate * dt` (amp = 0 here).
Trigger bits of flags24 (func_00207ABC / 00207BB8 / 00207C94 / 00207D90): 0x100 = "wait at A only counts while `actor+0x38 & 0x40` is set" (a rider; **[H]** that this flag is set by a player pawn
standing on the platform: func_00241B9C sets `0x60` in the contacted object's +0x3C word when a pawn with `flags100 & 0x300000` touches it), 0x400 = counts only while not ridden, 0x4000/0x10000 rising needs/forbids a rider,
0x200/0x800 wait-at-B needs/forbids a rider, 0x8000/0x20000 lowering needs/forbids a rider, 0x10/0x20 need actor flag 0x10000/0x20000.
**Level 25 record 151 is a continuous patrol** (no condition bits, linear); the other five are **ride-activated** (leave A after a rider has stood on them for waitA, wait waitB, come back).
An actor whose active bit (section 6) is clear does nothing (func_002079B0); all six have the bit set.

Verification against the level (`verification` in the JSON, 6/6 pass the automated test, 4 with identical offsets at both stations): Dire Straight: shafts `x[-154,154] z[1996,2304]`
(centre 2150.4), platform rest y 10.24 = 10.7 above the floor face y -0.5; end y 471.04 = 10.7 above the upper floor face 460.3; second lift 624.64 vs face 613.9. Spirit Temple 174.08 / 1095.68
vs faces 153.1 / 1074.7 (+20.98 both). Warehouse 174.08 / 624.64 vs 153.1 / 613.9. Breakdown #1 (ferry) from (153.6, 0, 358.4) to (-163.84, 153.6, 358.4): platform edge meets the floor tiles
at x 51.7 (top 0.5) and the raised floor at x -256 (top 154.1). Breakdown #2 ends 4.6 below the upper floor face 153.1. The platform rests 10-21 above floor faces (the slab origin is at its middle:
model z ±51 * 0.5 = ±25.6, so the standable top is ~25 above the origin -> the visual top sits ~35 above the floor face; unresolved whether the physical top is the hull top).
Collision/carrying of riders is done by the generic hull code (func_0023DA90 family), not decoded.

## 3. Teleporters (class 13, 40-byte record, object 330)  [V]
Object 330: 82-vertex skinned model, 172 wide, 614 high (z up), 2 parts (textures 80 opaque, 372 translucent) = a pillar/column of light. Records 161..168 (`targetId` at def +0x20):
161 -> 6000, 162 -> 6010, 163 -> 6011, 164 -> 6015, 165 -> 6020, 166 -> 6100, 167 -> 6105, 168 -> 6019; def +0x18 f32 143.36 = touch radius; def +0x1C f32 204.8 is not read by the touch test.
(Records 134-137 are campaign zones with `targetId` 0xD34..0xD37 gated by campaign flags `D_800E4680`; none placed in the shipped levels.)
* `func_00286920` (ROM 0x87520) builds the **zone list** (max 16 actors whose def class == 13) at level start.
* Each frame the pawn update `func_00220EB0` (ROM 0x21AB0, loop over scene+0x1B620, count scene+0x1B660) tests, for every zone whose def flags & 4 is clear,
  `distance3D(pawn, zone) < def+0x18`; on a hit it plays effect 0x230 and calls `func_0021B1E4(pawn, targetId, 0, 0)`.
* `func_0021B1E4` (ROM 0x1BDE4) looks up the **warp group** `targetId` with `func_0044DE70` (ROM 0x1BBE70) in archive section 8; with several records (spawn groups 3010+10n) the one farthest from all live
  pawns is chosen; a single record is used as is. Record `+0x10 u16` is the **level id** of the destination (the earlier "team/zone 0/1" reading was the level id: group 3010+10n has level n); if it differs from the
  current level the game switches level (func_0044E178). All eight shipped targets are in the same level as their zone (6000/6010 level 16, 6011 level 20, 6015/6020 level 26, 6019 level 25, 6100/6105 level 19).
* The part-6 active bit of a zone actor is *not* consulted (level 16 zones have their bit clear and still work) [V: loop has no such test].
Examples: Bottleneck zones at (0, -665.6, +-614.4), i.e. the bottom of the pit, send you to (0, 51.2, -+614.4) facing 0 / pi (the opposite end on the surface): a **fall-back-up teleport**; Leap of Faith zones at
(563.2, 0, 0) / (-1894.4, 460.8, -307.2) go to (-51.2, 1689.6, 921.6) / (-358.4, 1996.8, -1228.8). No teleport cool-down, no visible effect object other than the zone model.

## 4. Level part 7 = event rules (Rec14)  [V layout and dispatch, H trigger semantics]
`container(4)` = two (sorted key array, rule array) pairs, **not** column-major: `[0] u32 key[nA]`, `[1] Rec14[nA]` (set A, loaded to scene+0x11C8/0x11D0, count 0x11C0), `[2] u32 key[nB]`, `[3] Rec14[nB]` (set B, 0x11CC/0x11D4,
count 0x11C4). Loader `func_0044CD58` (ROM 0x1BACD8) also sets `rule.flagsE |= 0xF0` for rules whose bit `rule.bit` is set in the level's *preset mask* (section 6, third bitmask; **zero for every shipped level**, so no rule is armed at
level start) and clears bit 0 of flagsE when `keyByte != 0`. Rec14 (20 bytes, big-endian):
| off | field | meaning |
|---|---|---|
| +0 u32 | triggerMask | event bits that fire the rule (below) |
| +4 u16 | target | **actor index** (part 3) the operation is applied to (`D_8011FFC0 + idx * 0x2E8`) |
| +8 u16 | message | voice/message id, played via `func_004030E0` when >= 2 in mode 4 (always 1 in the ROM) |
| +A u16 | level | level id of the target; equals the current level (`D_8013B294`) in all shipped rules. If it differs, only the persistent state bit of that other level is written (`func_0028B64C`) |
| +C u16, +E u8 flags, +F u8 keyByte | wordC 0x3F00 on "pulse" rules ([H] high half of float 0.5 = delay); flagsE bits: 1 = armed, 2 = deferred (func_0028C544), 0x10 fired, 0x20 released, 0x40 armed-for-release, 0x80 counted; keyByte = condition key (0 in ROM) |
| +0x10 u8 | sound | `D_800D7F84` table id, played in mode 4 (0 in ROM) |
| +0x11 u8 | type | **actor class of the target**: selects `D_800D28B8[type]` (ROM 0xD34B8) = class vtable + 8 |
| +0x12 u8 | op | handler pair in that table: `{fn0(actor, state, rec), fn1(rec)}`; fn0 when `rec.level == current level`, otherwise fn1 |
| +0x13 u8 | bit | index of this rule in the preset mask |
`func_00278C80` (ROM 0x79880) executes a rule. Operations seen in the ROM: type 10 (indicator) op 1 = `func_00214178(state 1)` (ON; fn1 writes the persistent bit, func_00204770/0020478C), op 2 = state 0 (OFF,
func_002047BC/002047D8); type 3 (spawner) op 1 = `func_00285D80(actor, 1)` (enable, func_00206230/00206258), op 2 = disable (func_00206288/002062B0). Other class tables (4, 6, 7, 9) have more ops (common ops 0x279024.. etc.), unused by the data.
**Sources of events.** *Set A* is keyed by the **index of the emitting actor** (`func_0028C248`, ROM 0x8CE48): classes call `func_00278DE8(actor, mask, actor)` (ROM 0x799E8) in their state-enter functions, which runs
`func_00279274` (ROM 0x79E74) over that actor's rules. Emitted masks traced: indicator state 1 (ON) enter func_00204658 emits **0x80000** (only when `D_800CD4C0 == 0`), indicator state 0 (OFF) enter func_002045CC emits **0x200000**,
lift state 1 / state 3 enter emit **0x10000 / 0x20000**; the flag spawner uses 0x2 / 0x400002 (not traced). *Set B* is keyed by the **region id** = `word0` of the part-1 ground triangle (`func_0028C1B0` reads the u16 at offset 0 of
the triangle): the pawn update (`func_00220EB0`, calls to `func_00278E74` at 0x221B88/0x221B9C) emits mask bit 0x2 for the triangle now under the pawn and 0x1 for the one it left, OR'ed with the actor-kind bits 0x800/0x1000/0x2000
(human = 0x2000 [H]); a rule needs `mask & 0x7FF` to share a bit with `triggerMask` and its own 0x3800 bits to be contained in the emitter's (so `0x80002001` = human leaves/steps off... **[H]** which of enter/leave).
**Verified:** in all 30 type-10 set-B rules of the 10 levels the rule key equals `word0` of the triangle the target indicator stands on (`actor.regionTriangle`), i.e. *the beacon lights when a player steps on its floor region*.

Shipped rule patterns (all `message=1, level=current`):
* Non-CTF levels 6, 15, 21, 26, 29, 34 (2-4 indicators, objects 203): set A pairs (mask 0x80000 -> another indicator OFF, mask 0x200000 -> indicator ON) make the beacons mutually exclusive; set B: each pad's region -> `indicatorOn` for its indicator.
* CTF levels 7, 16, 23, 33 (indicators 148/149 + flag spawner 184): set B per pad region: `indicatorOn`, a delayed (wordC 0x3F00, flagsE 3) `indicatorOff` (pulse), and `spawnerEnable` of the flag; set A on the flag: mask 0x2 (/0x400002) -> `spawnerDisable` (itself).
Full decoded rules with targets are in the `eventRules` array of each level JSON; `triggerRegions` gives the triangles (raw x,y,z of the 3 corners) of the region ids used.

## 5. Indicators (class 10, 48-byte record; objects 148, 149, 203)  [V layout, H role]
Records 76 (objects 148/149), 129 (203). State table `D_800CD4C4` (ROM 0xCE0C4): 0 OFF (enter func_002045CC: clears the persistent active bit, emits 0x200000), 1 ON (func_00204658: sets the bit, emits 0x80000, clears
actor flags 0x2000/0x100 for static defs), 2 (func_00204728). Update func_002043E0 sets `actor+1` (model index [V: func_00250950 uses `o->unk1` as the model key]) and `actor+3` (look [H]) from def bytes `+0x22/+0x23` while state 0 and
`+0x2E/+0x2F` otherwise. Object 203 has two models: 0 = 4-vertex untextured quad, 1 = 24-vertex translucent beam (texture 365); 129 has OFF -> model 1 (beam), ON -> model 0; objects 148/149 have one translucent cube model
(8 vertices, texture 158, z 156..385). `visibleFlagAtSpawn` = def flag bit 0 (0 in the ROM). Initial state = part-6 active bit: **all 23 start OFF** (bits clear). Colours/radius (`colorA/colorB/radius` 512) are exported raw, [H] unused by the
renderer for these models. Reading: a pad beacon in a "king of the hill / bonus / flag-activation" sense; the role is a hypothesis, the on/off/transition data are verified.

## 6. Section 6 bitmasks (scene+0x80)  [V]
`archive section 6` = container(2): `[0]` 37 x `container(3)`, each of three `container(2) {8-byte header, 8-byte bitmask}`: `[0]` **actor active bits** (header word = actor count; bit i = part-3 actor i, level 0: `FE1F` = actors 1..12, the pawn
bit is clear), `[1]` sector-array-2 object bits (0), `[2]` rule preset bits (0). Accessors func_00285F28 (get), func_00285D80 (set; ROM 0x86B28/0x86980), func_0028B64C (other level), func_0028BAF8/BBE8 (preset bit).
Active bits are the *persistent* on/off state (indicator/spawner states write them); classes with state machines consult them (lift: inactive = frozen). Section 7 (`37 x container(1)` of 32 bytes) is the same idea for another table (scene+0x84, accessors func_0028B3C0/B4AC/B59C) and is not used by any shipped object.

## 7. Other previously undecoded items  [V]
* **Level part 12** (24-byte box, scene+0x1B910): `f32 originX, originZ, cellW, cellD` + flag; used by func_00288440 to stream one extra "grid section" for the camera cell on a uniform grid. All zero in all 37 levels -> unused.
* **Sector array 2** (stride 0x20, "anim objects", 0x1C8-byte actors, func_0024E980) and **sector array 4** (copied verbatim): empty in all 203 sectors.
* **Level part 6** (16 x 44-byte records at scene+0x1B300, count 0x1B304): read only by `func_0023AFE0`: a bank of 16 particle-emitter parameter sets (`+0 u16 effect id`, `+0x20/+0x10` rates) selected by the u16 mask at +0x56 of the part-1 *layer* record (100-byte, `func_0028B2D4`) of the
  ground triangle an actor stands on, with a fade factor: **wading/surface effects (ripples), not map objects** [H on details].
* Static events (object table `[3]`): empty for all 347 objects.
* Section 8 "warp dests": see section 3; spawn groups 3010+10n (level n), `+0x10` = level id.

## 8. JSON schema (`extracted/dynamics/NN_name.json`)
```
level, name, coordinates (note), counts { lift, teleporter, indicator, flagSpawner, marker, "pickupSpawners(...)" }
dynamics[] common: id "actorN", kind, actorIndex, objectId, actorType, intelligence, model{objectId, modelCount, hullAabb, animationClips, models[{index,file,vertices,triangles,boundsMin,boundsMax,parts[{textureId,flags,frames}]}]},
                   position[3] (raw), yawRadians, scale[3], regionTriangle, placementFlags, confidence, activeAtStart
  kind "lift":        motion{ type "oscillate", from[3], to[3], offset[3], travelTicks, easing ("cosine..."|"linear"), holdAtFromTicks, holdAtToTicks, trigger ("always"|"rider"), yawSweepRadians, bob, raw }, verification{...}
  kind "teleporter":  teleport{ touchRadius, targetGroup, destinations[{position, yawRadians, level}], pickRule, effect }
  kind "indicator":   initialState "on"|"off", activeBitAtLoad, regionId, trigger "stepRegion", stateModelIndex{off,on}, stateLook{off,on}, colorA, colorB, radius
  kind "flagSpawner": initiallyEnabled, respawnTicksMin/Max
  kind "marker":      idAt0x0E, idAt0x18, definitionBytes
eventRules[]  { set "A-actor"|"B-region", source, sourceKey, triggerMask, triggerBits[], targetActor, targetClass, operation, operationName, presetBit, appliedAtLevelStart, flagsE, wordC, ... }
triggerRegions { allRegionIdsInLevel[], regions[{ regionId, triangles[{flags, vertices[3][3]}] }] }
initialActorBits { activeMaskHex, actorCount, presetRecordMaskHex }
```
Animating a lift: let `u = ((tick - t0) mod cycle)` over the phases `[holdAtFrom, travel, holdAtTo, travel]`; `p` = 0 .. 1 (rising) / 1 .. 0 (lowering); `p' = (1 - cos(pi p)) / 2` (or `p` when `easing` is linear); `pos = from + p' * offset`.
For `trigger = "rider"` the cycle only leaves phase 0 after a rider has been on the platform for `holdAtFromTicks` continuous ticks (the timer resets when it steps off). Lifts are active from level start.
Teleporter: when the pawn is within `touchRadius` of `position` (3D), place it at `destinations[0].position` with yaw `yawRadians` (or farthest-from-others when several).
Indicator: ON when a player steps onto/off the region `regionId` (triangles in `triggerRegions`), model `stateModelIndex[state]`.

## 9. Not decoded
Riders' physical carrying and the exact `actor+0x38 & 0x40` setter; the enter/leave meaning of set-B mask bits 0x1/0x2 and the bit 0x2000/0x80000000 roles; flag-spawner events (0x2, 0x400002) and the flag capture logic; class 6/7/4 vtables (no placed
instances); the marker (object 140, level 22) behaviour and its animation; indicator colour use; the platform object's one animation clip (object 297); sound ids of lifts (state sounds are in the state-table `anim` words).
