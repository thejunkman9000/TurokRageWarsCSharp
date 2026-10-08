# Creature / animated-object export (`extracted\creatures`)

Code: `tools/Trw.Tools/Anim/CreatureExport.cs` (+ `CreatureRoles.cs`, `CreaturePlacements.cs`), command `trw creatures [rom] [outdir]`
(default outdir `extracted\creatures`; placements need `trw defs` output in `extracted\defs`). Program.cs line to add:
`if (cmd == "creatures") return Trw.Tools.Anim.CreatureExport.Run(args.Skip(1).ToArray());`
Status tags: **verified** = proven by game code / data, **likely**, **hypothesis**.

## 1. Files
- `obj_<id>.json` for each of the 90 objects that have clips (408 clips). Model = the first model chunk of the object whose bone count matches the clips.
- `obj_<id>_m<k>.json` for the other model chunks of the object that share the identical skeleton (only object 174: 36 files; mesh and rest pose only, `clips` is empty, `clipsFrom` names the file with the clips).
- `placements.json`, `summary.tsv` (vertices, triangles, clips, frame-0 bounding box).
- `skins.json` + `skins/tex_NNN_s{0,1,2}.png` (object 174 skins, section 8); `placements.json` characters[] gained `characterId` and `bodyChunk`.
- Skinned models without clips: none exported (the bone rest pose lives in the clip data, the model only stores parents).

## 2. Schema of `obj_<id>.json` (numbers invariant culture, arrays flat)
```
object, model (chunk index inside the object), modelChunkCount, modelRom, boneCount, romVertexCount
bones[]      { parent (-1 root), id (hit volume body-part byte), restPos[3], restRot[4] x,y,z,w }   // restPos/Rot = rest pose from the clip data, LOCAL to the parent
positions[]  x,y,z per vertex   bone-LOCAL, raw model space (Z-up, same as extracted\levelgeo\_objects)
uvs[]        u,t per vertex: u = S/32*scaleS/65536/texW, t = T/32*scaleT/65536/texH  (plain t; flip v = 1-t like the _objects loader)
vertexBone[] bone index per vertex
materials[]  { texture, flags (material flags, see texture_mapping.md), color1, color2 (RGBA hex), triangles[] (vertex indices),
               part (part index in the chunk), fieldA (material byte +0xA), partGroup (= fieldA & 0x38: VISIBILITY, section 8), field8 (s16 sort id) }
bones[] also: hitMin[3], hitMax[3], hitRadius (the chunk's hit volume, bone-local, loose)
restWorld    base64 float32[boneCount][16]
clips[]      { index, animId, frames, rate (15), loopFrame, loopFrameRaw, holdsLastFrame,
               boneWorld (base64 float32[frames][boneCount][16]), events[] {frame, effect, raw (28-byte record, hex)},
               role, roleConfidence, roleEvidence }
```
- Vertex order = `Model.Vertices` (ROM vertices, then the ST-override copies, so the vertex count is larger than `romVertexCount`). UVs are computed with the material of the triangle's own part (the _objects loader uses the part that loaded the vertex): vertices whose UV would differ are appended as extra copies (same position/bone), so every triangle is correct.
- Matrices: little-endian float32, row-major M11..M44, **row-vector convention**: `p' = [x y z 1] * M`, translation in elements 12..14. `skinned = v_local * boneWorld[frame][vertexBone[v]]`. World = `local * parentWorld`, local = transposed quaternion matrix + translation (animation.md section 4), sampled **at integer frames only** (the file holds all frames; interpolate between consecutive frames if smoother playback is wanted: lerp translation, slerp rotation is not possible from matrices, so lerp the matrices or resample from `obj_*.json` of `trw anim`).
- The game zeroes x and y of the translation of **bone 0** at render time (root motion is applied to the actor instead): already applied, also in `restWorld`. The actor then positions the whole skeleton (no baked world transform).
- Coordinate frame: raw model/animation space, **Z-up** (player: feet z ~ 0, pelvis z 210, head z 345, top of the model z ~ 480); apply the same Z-up conversion / X mirror as for static pieces.
- Playback: `rate` = 15 frames per game tick unit, one tick = 1/15 s (GAMEPLAY_SPEC), so a clip advances 15 frames per second at dt 1/15, i.e. **15 fps**. When the position passes the last frame it wraps to `loopFrame` (0 for 223 clips). `holdsLastFrame` (loopFrameRaw == frames-1) = play once and stay on the last frame (deaths, jump start, etc.).
- `events[]`: effect/sound events fired when the playhead crosses `frame` (animation.md section 7), unparsed beyond `frame`/`effect` (the 28 raw bytes are included).

## 3. Verification
`python`-side check over all files (script not shipped): no NaN; parent-child bone distance constant except bones whose translation track animates (viewmodel arms, 174 clip 21/23 pelvis falls); mean triangle edge stretch versus frame 0 is 1.00-1.03 for 85 of 90 objects (155: 1.17, 156: 1.23 squash/stretch). Rendered frames: 133 (winged creature, coherent), 174 chunk 5 (humanoid with head, torso, arms, legs, tail; skeleton overlay fits the limbs), 176 and 174 chunk 0 (blocky but attached to the bones).
Objects that **look wrong or odd**: 124-127 (8-bone limb/gib effect strips, mesh stretch 2.0-2.3x, bone length varies 82-96 percent: these are growing/flying gib effects, they use animIds 7043-7046 = explosion death effects, not creatures); 174 chunk 0 and 176 have a large pole + disk shape on bone 9 (flag carrier attachment, hypothesis); objects 152-173, 231-253 are weapon viewmodels (first person space: bounding boxes are offset by -1000..-5000 in y); 148-151, 178, 184, 203, 263-266, 274, 297, 330 are 1-frame 1-bone props. 174 variants m16-m36 (about 50 triangles, bone 16 = hand) are hand-held weapon models, not characters.

## 4. What the 90 objects are
| objects | what | evidence |
|---|---|---|
| 174 | the character body: 16 full body meshes (chunks 0-15, 22 bones, 44 clips) + 21 hand-held weapon models (chunks 16-36, all on bone 16) | placed for every pawn (level part 3 `objectIndex` 174, `actorType` 1120) |
| 175, 176, 185 | other character-class skeletons using the same state ids (walk/jump/swim/attack/death) | same animIds as 174 |
| 124-127 | 8-bone effect strips, one clip each, animIds 7043-7046 | ids equal the death states 41-44 |
| 128-144 | 17 creature meshes, one 20-frame clip each, animId 24000 | 24000 (0x5DC0) is passed by the menu code (func_0041F298) when it spawns display models: character-select / boss display |
| 152-173 | 22 first-person weapon viewmodels (the 22 weapons) | state-table ids 20000-20550 |
| 231-253 | same ids, 1-2 bones: world / third person weapon pickups or attachments [H] | |
| 182, 214-218, 313 | ids 21500-21505, 12100-12120, 32000: unknown | |

## 5. Roles (evidence and confidence)
Mechanism [verified]: object table entry section 0 = container(2): `[0]` 32-byte block, `[1]` counted array of u32 **animation request ids**, sorted ascending (`func_0028FE1C` -> `func_002469F8`, binary search `func_00265508`; equal ids = random alternatives). **The index in this table is the clip index**; clips without a table entry (`animId -1`) can only be reached by explicit index. The state machines request ids: player state table at VA 0x800CE8C4 (24-byte rows `{enterFn, updateFn, ptr, flags, animId, ...}` indexed by state; alternate set D_800CED30 for the 0x5Exx ids), the walk update `func_002246E8`, idle update `func_002243E4`, fire function `func_0021A9A4`, weapon state tables (descriptor +0x54, default table at VA 0x800CF298, 32-byte entries `{state, enter, update, -, animId, 1.0, flags}`).
Per id (full per-clip text is in `roleEvidence`): 1 idle (verified); 2010 walk, 2015 walk_back, 2030 jump, 2050/2060 strafe (sign of lateral speed), 2210 swim, 24100/24105/24106/24107/24108/24109 = the same set for the alternate animation set (all verified from the state tables/code); 2011, 2040/24110 land, 2215 swim, 2600 swim_idle, 2615 swim_back, 2900/2910 idle fidgets, 4000/4100 melee attack, 24150-24158 firing poses of the alternate set (idle/walk/back/strafe/jump), all likely. Deaths: 7000-7505 are the animIds of states 22-33 (hit-reaction/death states with 10-70 tick timers); the clips are one-shot, hold the last frame and end with pelvis and head on the floor (z 13-34), so role `death` (likely; 7020-7026 hypothesis); 23300/23301 (state 0x2D entered when health reaches 0 with a 60-75 tick timer; kneeling pose) `kneel` (hypothesis, may be the normal death/knockdown); 9200 `frozen` (state 0x26, entered on effect 0x129 = petrify? hypothesis); 2620/2630 `special` (state 16, pelvis +90). There is **no separate run clip**: the game has a single walk clip, speed is movement only. No separate `pain` clips were identified (the 7xxx set are the only reaction clips).
Weapons: 20000 select (state 0, enter = equip function), 20010 deselect, 20100 idle, 20110 idle_variant, 20210 fire (primary random list 20210/20220/20230/20240), 20550 fire_alt (secondary, list 20550-20553), 20200/20270 special (reload/charge?). All likely, idle verified. The viewmodel object index for weapon n is not stored in the weapon descriptor (+0x00..0x5C checked); the order 152-173 = weapon order is an unverified guess.
Counts: 408 clips: verified 72, likely 262, hypothesis 25, static 24, unknown 25 (the remaining unknown ones are ids absent from every table: 21500-21505, 12100-12120, 32000, the extra clips beyond the id table).

## 6. placements.json
`characters[]`: the 44 class-11 pawn records (`intelligence` index 79-118, 124-127) with name guess, `appearanceA/B` bytes and `speedScale`. **Corrected 2026-10-07 (section 8):** `appearanceA[0..1]` (s16 at def +0xC) is the character id (index of the object-id table D_800CE430: ids 0..16 -> object id 1120 = object 174, 17/18/19 -> other objects) and `appearanceA[2]` (def +0xE) is the **body model chunk** (`bodyChunk`; `bodyChunkGuess` carries the same value; the old guess A[1] was wrong and `skinIndexGuess` is meaningless). TUROK (record 79) = chunk 1.
`levels[]` (all 37): `actors[]` = every level part 3 actor (raw x,y,z, yaw radians, object, actorType, intelligence, scale, flags, `kind` = `playerStart` for the class-11 pawn (record 79, object 174, actorType 1120, exactly one per level, verified), `pickup`, `light`, `mover`, `zone`, `marker`); `spawnPoints[]` = archive section 8 "warp dests" of the level (the multiplayer start positions, 8-14 per level; all other characters/bots appear there; level join verified in gameplay_defs.md). Rage Wars level data contains **no creature actors other than the player pawn**: bots are not placed in the level file, they are spawned at `spawnPoints` using pawn records (which record each bot uses is chosen by the game mode / menu code: not decoded). Coordinates are raw: the viewer mirrors X.

## 7. Not verified
Which bot uses which model chunk and skin; weapon index to viewmodel object; the 7xxx/23300/9200 semantics beyond ids and poses; strafe direction per id; events beyond frame/effect id; whether objects 128-144 are menu display models.


## Viewer findings (2026-10-07)
* **Face textures:** materials with the mirror flag (0x800, e.g. flags 0x808 used by the faces) need their texture V inverted relative to other materials. Checked on the portrait busts
  (objects 128-144): flipping V on those materials only puts the eyes/nose/mouth the right way up and leaves the body textures untouched; flipping V on all materials breaks the torso.
  Viewer default (`CreatureScene.FlipVMirror`); creature-mode keys 1-5 toggle other UV variants for experiments (flip U, swap, flip V on all), 0 resets.
* **Facing:** creatures and portraits already face the viewer with just the Z-up conversion; the extra 180 degree turn that static level pieces need must NOT be applied.
* Not applied to static level pieces (the user judged those fine); revisit if a static piece with an asymmetric mirror-flag texture looks upside down.
* (superseded, see section 8) "Object 174 renders purple": not a palette problem, it is the invulnerability-shell part (texture 176) drawn opaque.


## 8. Character skins, part visibility, "purple" and "distortion" (2026-10-07)
Evidence tags: [V] = read in game code and confirmed by rendering the export, [H] = hypothesis.

### 8.1 Root cause of the purple / lavender body [V]
It is NOT a palette. Every body chunk (0..15) contains a translucent **shield / ghost shell** part (texture 176, flags 0x108, colour1.a = 0x2A..0x4B, `partGroup` 0x38, 70-78 triangles, a puffy copy of the body). The game draws it only in a special state (`func_0026B6A0`: group 0x38 only when `var_s6` is set, i.e. `actor+0x122C & 0x8000`, which `func_0021A2D4` sets in the protected state `actor+0x11D8 > 0`). Drawn always and opaque (as the viewer did) it hides the whole body behind purple streaks and also produces the "blocky / stretched torso" look. Two more optional parts are hidden by default (8.2): the flag banner (tex 175, hovering above the head) and a status overlay (tex 270 or 366). With the three groups hidden, object 174 renders as 16 plausible, different characters (`preview_chunks_skin0.png`) and a plausible 9-frame walk (`preview_walk_chunk1.png`).

### 8.2 Part visibility [V `func_0026B6A0` asm, normal characters]
`g = material byte +0xA & 0x38` (exported `partGroup`; the low 3 bits are a limb/zone id that is not used for visibility):
| g | name | drawn when |
|---|---|---|
| 0, 8, 0x18, 0x20, 0x28 | always | always (all body parts) |
| 0x10 | status_overlay | only while `actor+0x11EC > 0` (timer set in `func_00282E6C` when an effect of type 0x40F hits the pawn; which status, e.g. frozen/burning, is [H]); textures 270 / 366 (50 triangles) |
| 0x30 | mode_item | only if `D_80146918 != 0` and the player's stats `+0x8F == 1` (game-mode specific; the banner on a pole = capture-the-flag flag [H]); tex 175 |
| 0x38 | shield | only in the protected/ghost state (8.1); tex 176, translucent |

(For character id 0x12 the groups 0x18/0x20/0x28 select the geometry by skin; that is another object, id 1123, not object 174.) The viewer should hide groups 0x10/0x30/0x38 by default; a "protected" toggle could draw 0x38 with blend src-alpha (alpha = colour1.a).

### 8.3 Skins = texture frame/palette selected by the skin key [V]
`func_00269A80(material, key)` -> `frame = key % frames`, `palette = key % palettes` (transparency_and_animation.md section 7). The actor draw passes `key = actor+3` (`func_0021A2D4`: `func_0026B6A0(..., actor+3)`; the weapon part uses `func_0026DA4C(..., actor+3)`). `actor+3 = player stats +0x81` (`func_0044AB34`, `func_0044A600`, `func_0022BB70`, `func_0021C9B4`) = the **skin 0..2** chosen in the menu (`func_0042E640`: `stats+0x93` clamped to 2, `stats+0x81 = stats+0x93`; `func_0042E7CC` random bots: `rand % 3`). The skin textures (ids 177..230; the shared 175, 176, 270, 366 have one frame) have 3 frames with 3 palettes (198/199/200 have 4: the 4th is never selected; a few have 2: skin 2 then wraps to frame 0). Frame k pairs with palette k, which is what the plain `tex_NNN_f{k}.png` files already contained. So **no runtime recolour exists for actors**: `D_800D2B04` / `func_00295B90` recolour (transparency_and_animation.md section 9) is a camera-wide screen effect (`func_00235AD0` passes the camera field `+0x120`; it also shifts the resource cache slot by `<<7`) and is 0 in normal play. The earlier hypothesis "team palette recolour" is **wrong**; team games only use a different skin index (`stats+0x93`, 0..2) [likely].
Verified by rendering (textured software rasteriser): chunk 5 with skin 0/1/2 = lava / ice / green scales, chunk 1 = three different outfits; they match the three names the ROM name table gives for the skins of those characters (FIREBORN / ICELORD / ENTRAIL for id 3, TUROK / WARRIOR / MASTER for id 0).

Files: `skins/tex_NNN_s{k}.png` (k = skin 0..2, frame k % frames, palette k % palettes; written for the 88 textures of all object-174 chunks; single-frame textures are identical for all k) and `skins.json`:
```
rule, skinCount (3), fileTemplate
textures[]   { texture, width, height, frames, palettes, formatByte, variesBySkin, files[3], frameForSkin[3], paletteForSkin[3] }
partGroups[] { group, name, defaultVisible, condition }
chunks[]     { chunk, file, kind (body|weapon), materials[] { part, texture, flags, partGroup, visibility, defaultVisible } }
characterNote, characters[] { menuRow, characterId, skinNames[3], objectId (D_800CE430), object (174 or -1), pawnIntelligence, bodyChunk (-1 if not object 174) }
```
Viewer recipe: character (menu row / id) -> `bodyChunk` -> file `obj_174_m{chunk}.json` (chunk 0 = `obj_174.json`); skin k -> replace every material texture `T` by `skins/tex_T_s{k}.png`; skip materials with `partGroup` 0x10/0x30/0x38 (`defaultVisible == false` in `skins.json`). No palette code is needed.

### 8.4 Character -> chunk mapping [V code, V visual]
`stats+0x80` = character id (menu entry - 1; 0x13 = random) -> `func_0028CF7C(world, 11, id)` returns the first class-11 pawn record whose s16 at +0xC equals id -> actor def (`actor+0x18`); `func_0021A2D4` sets `actor+1 = def+0xE` (appearanceA[2]); `func_0024BFC4(actor, actor+1)` selects model chunk `value % chunkCount` (37) of the object whose id is `D_800CE430[id]` (1120 -> object 174). Result (id: chunk, ids 0..16): 0:1, 1:3, 2:4, 3:5, 4:6, 5:2, 6:7, 7:8, 8:9, 9:10, 10:0, 11:11, 12:12, 13:13, 14:14, 15:15, 16:13; ids 17 (RAPTOR/SLAG/CLAW) and 18 (MITES/COMPIES/TEDDIES) are other objects (1121, 1122). Names per id and skin: `skins.json characters[].skinNames` (17 rows x 3 skins, ROM tables 0x800E3820 / 0x800E3A50). The 44 pawn records of the level data beyond the first of each id are never selected by a menu character (several have appearanceA[2] = 16..36, i.e. weapon chunks).
Weapon chunks 16..36: `func_0021A2D4` draws chunk `descriptor+4 (s16 id, 0x4C3..) - 0x4B3` (weapon index >= 16: `- 0x4B5`) with the same skin key (weapon index = actor+0x62E, descriptor table D_800D052C): WARHAMMER 16, RAPTOR CLAW 17, MITE BITE 18, BOOMERANG 19, TEK CROSSBOW 20, MAG 60 21, SHOTGUN 22, ASSAULT RIFLE 23, MINIGUN 24, PLASMA 25, FREEZE GUN 26, FLARE GUN 27, GRENADES 28, SCORPION 29, NAPALM 30, EMACIATOR 31, INFLATOR 30, CHEST BURSTER 31, CEREBRAL BORE 32, PFM MINE 33, IRON CLAW 34, TELEPORT MINE 36 [H: the duplicates for indices 16/17 show that the table order or the `-2` rule is not fully understood; the weapon order was not re-verified].

### 8.5 Skinning / "distortion" check [V]
Nothing is wrong in the skinning export; the distortion was the shell (8.1). Numbers over chunks 0..15 (rest pose vs all frames of locomotion clips 0,1,2,5,6,28,31,32): same-bone triangle edges are exactly rigid (ratio 1.000); mean cross-bone (joint) edge ratio 0.98..1.05 (max 1.6-5.6 at the widest limb joints = rigid skinning of a low-poly mesh; death clips open some joints up to 14x); all 22-bone parent tables are identical in the 16 chunks; `vertexBone` = bone of the last `G_MTX` (F3DEX2 `0xDA380003` = load, no push; already loaded vertices are never re-transformed, which `Model.BoneOfVertex` replays), and the limbs and textures land on the right bones in all 16 chunks and all 9 walk frames. ST-override copies keep position and bone of their source vertex; no UV or slot-reuse corruption found (no extra V flip was needed for body textures in the textured test render). The hit-volume boxes are bone-local but loose (about 55 % of vertices inside), so they are no tight check of the vertex-to-bone map; the visual and edge-length checks are. Variants m1..m15 need no own rest pose or remap: same skeleton, and the game uses one animation chunk for all chunks.
Previews: `preview_chunks_skin0.png` (all 16 body chunks, skin 0, idle frame 0), `preview_walk_chunk1.png` (chunk 1, walk clip index 1, 9 frames, side view).


## Viewer: shield shell, hidden parts and skins (2026-10-07)
The purple, distorted player was the translucent protection shield shell (texture 176, flags 0x108) drawn opaque over the body, plus the flag banner and a status overlay.
The viewer now skips materials whose `partGroup` is 0x10, 0x30 or 0x38 (key H shows them), draws flag-0x100 materials translucent (colour1 alpha), and applies `skins.json`
(`skins/tex_NNN_s{0,1,2}.png`; key K cycles the skin). Character id -> body chunk mapping is in `skins.json` (`characters[].bodyChunk`).


## Root cause of the upside-down creature textures (2026-10-07)
Not a property of the textures. Static pieces are exported as OBJ with `vt u (1 - t)` and their loader flips it back, so v = t. The creature JSON stores plain `t`, and the
creature loader applied `1 - t` once, i.e. the opposite V convention. With v = t (the loader fix) every creature texture is oriented like the level pieces: all portrait faces upright.
The earlier "mirror-flag materials need a flip" finding and the per-texture flips were symptoms of the loader inverting everything while noisy body textures hid it.
`uv_orientation.json` (analysis agent) mixes two baselines and should not be used. The creature-mode keys 1-5 and the P/V per-texture tool remain for experiments;
`creature_texture_flips.before_flipall.txt` holds the 20 per-texture flips made under the old baseline (all of them are now covered).
