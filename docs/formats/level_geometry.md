# Level geometry, collision, lights and spawn data

Status per claim: **[V]** verified (against the game code in `D:\Projects\TurokRageWars` and/or by decoding all 37 levels and
checking consistency), **[H]** hypothesis. All values big-endian. Code: `tools/Trw.Tools/LevelGeo/` (`LevelGeoCommand.Run(args)`;
`trw levelgeo [rom] [outdir] [--level N] [--no-topdown]`, default outdir `extracted/levelgeo`; the dispatch line
`if (cmd == "levelgeo") return Trw.Tools.LevelGeo.LevelGeoCommand.Run(args.Skip(1).ToArray());` still has to be added to Program.cs).
Builds on `levels_records.md` (level directory, parts, grid) and `models_textures.md` (model chunks, materials, textures).

## 0. The big picture [V]

There is **no monolithic world mesh**. A level is a grid of *sectors* (level part 4 = grid, part 5 = sector chunks) and every sector is
mostly a list of **placements of static objects** (archive section 0, 347 objects, models are the small 3-section "stat model" chunks).
The maps are blocky multiplayer arenas built from tiles (floors, wall planes, stairs, pillars), about 280 placements and 500-1100
triangles per level (all 37 levels: 10,169 placements of 133 distinct objects, 28,074 triangles). Vertex colours are baked lighting.
Coordinates are in game units, +Y up, levels span about 2,000-5,000 units (level 22 has one stray far object, level 35 a sky shell).
There is **no per-triangle collision mesh for the level**: actor-vs-level collision is made of (1) an AABB per placed object,
(2) a tiny coarse "ground/region" triangle mesh (part 1, 1-18 triangles per level), (3) the objects' own definitions.

How the geometry gets to the game (func_0028795C "grid section" callback, called once per sector; `scene = arg0`):

| sector array | stride | runtime | consumer |
|---|---|---|---|
| 0 | 0x1C | one 0xE8-byte actor per record | func_0024F940 (compact placement) |
| 1 | 0x34 | one 0xE8-byte actor per record | func_0024FD4C (full placement) |
| 2 | 0x20 | 0x1C8-byte actors (func_0024E980) | empty in all shipped sectors |
| 3 | 0x4C | 0xC0-byte records | func_00276D20, used by func_0028994C = **lighting**, not collision (see 4) |
| 4 | - | copied verbatim | empty (`{0,8}`) in all inspected sectors |

All four arrays are stored column-major (record i, byte j = `data[i + j*count]`) and de-transposed by func_0028FEEC. [V]

## 1. Placement records [V from func_0024F940 / func_0024FD4C, constants read from ROM rodata]

Both forms produce an actor with `+4 = objectId` (u16, index into archive section 0), `+0x1C` = ROM address of that object container,
`+0x18 = definition` (see 1.3), a 4x4 matrix at `+0x28` (row-vector convention `v' = v*M + t`), position at `+8/+0xC/+0x10`,
bounds at `+0xB8..+0xCC`, `+0xD4 = word0`, `+0xD8 = flags`, `+0x14 = region triangle` (below).

### 1.1 Compact record, sector array 0, stride 0x1C
| off | type | meaning |
|---|---|---|
| +0 | u32 | `word0` -> actor +0xD4 (0xFFFFFFFF in all 10,169 records) |
| +4 | f32 | uniform scale (0.35 and the like) |
| +8 | u16 | objectId (0..319 used, 127 distinct) |
| +A | u16 | **region triangle** index into level part 1 triangles (see 3); 0xFFFF = none; observed range always `< triangleCount` of that level [V] |
| +C | u16 | `flags` -> actor +0xD8 (values 0..6 seen; bit 0x40 would select the fixed 204.8 cube bounds, never set in the ROM) |
| +E | u16 | object-definition index (see 1.3), 0..52 used |
| +10 | s16 | y = value * 5.12 |
| +12..+17 | s8 x6 | bounds: min = pos + (s8[+12], s8[+14], s8[+16]) * 10.24 - 0.5, max = pos + (s8[+13], s8[+15], s8[+17]) * 10.24 + 0.5 |
| +18 | u8 | bits 0-1: yaw = n * 90 degrees (0, pi/2, pi, 3pi/2); bit 0x10 negates x; bit 0x20 negates z |
| +19 | u8 | x = value * 25.6 (sign from +18 bit 0x10) |
| +1A | u8 | z = value * 25.6 (sign from +18 bit 0x20) |

Yaw matrix (func_00273744): `[c 0 -s; 0 1 0; s 0 c]`, i.e. `x' = c x + s z`, `z' = -s x + c z`; scale (func_002734EC) then translate. [V]
Compact positions are therefore on a 25.6-unit grid in x/z and 5.12 in y. Verified by the top-down renders (tiles line up, 0 of 10,169
placements fall outside their grid cell by more than 300 units).

### 1.2 Full record, sector array 1, stride 0x34 (7 records in level 0; 250 over all levels, 9,919 compact)
`+0 word0`, `+4/+8/+C f32 position`, `+10/+14/+18 f32 scale x/y/z`, `+1C..+26 six s16 *truncated floats*` (the upper 16 bits of an
IEEE float, `Trunc16ToF32`) = bounds offsets min(x,y,z), max(x,y,z) relative to the position, `+28 u16 objectId`, `+2A u16 region
triangle`, `+2C u16 definition index`, `+2E u8 flags`, `+2F..+32 s8 x4 = quaternion (x,y,z,w) * 1/127`, normalised, converted by
func_002742B4 to the same row-vector matrix convention as above (yaw q = (0, sin, 0, cos) gives the same matrix as the compact form). [V]
Bytes seen: mostly pure yaw `(0,±90,0,±90)`, `(0,127,0,0)` (180 degrees), a few pitched pieces like `(-90,0,90,0)`.

### 1.3 Object definitions: archive section 1 [H that scene+0x78 is this section; the use of the fields is V]
RNC -> container(169) of 40-byte (entries 0..52 and one more group) or 32-byte records. The placement's definition index selects one;
used fields (func_0025021C, func_00250754, func_0024F940): `+8 f32 radius` (512.0 for all static defs; squared radius written to `+0x24`),
`+0xE s8` near model index, `+0xF s8` far model index (distance switch), `+0x12 s8` passed to the model draw function. The model of an object is
`modelList[lodIndex % modelList.Count]` (func_00250754: `arg1 % count`) where the model list is object-container section 1. For all
placements in the ROM `+0xE == 0`, so the first model is used (object 174 has 37 models, 185 has 3, 203 has 2: variants the levels do
not select statically; probably chosen at run time).

### 1.4 World mesh construction (exporter) [V]
For each placement: vertices of the model `(s16 x,y,z)` scaled by the record scale, rotated, translated; UVs from the material scale and the
texture size exactly as in `models_textures.md` (`u = s/32 * scaleS/65536 / texWidth`; checked: all 37 levels have UVs in [-5, 7],
none outside +-64); vertex colour = model vertex RGB; materials de-duplicated by (textureId, flags, colour1) and named `m<tex>_<n>`;
textures are referenced as `../../models/textures/tex_NNN_f0.png` (frame 0). Triangles of one sector are grouped `g sector_<cell>`.
All 28,074 faces, vertex/UV indices, material names and texture files resolve in all 37 `world.obj/mtl`.
Animated textures (water, lava...) show up as multi-frame textures (10 / 19 / 20 frames) on ordinary objects; `summary.json` lists them
as `animatedTextureObjects` (objects 384/351/506/160/162/90... use 10-frame textures): **water and lava are normal placed objects
with animated textures** [H, no separate surface-type data exists].

## 2. Sector grid [V, from levels_records.md]
Part 4: column x-min/x-max, row z-min/z-max, cells `xMin,zMin,xMax,zMax,flag`; sector chunk *i* of part 5 belongs to cell *i*.
Placement positions are world coordinates (not cell-relative). Cells with an 8-byte placeholder chunk have no data.

## 3. Part 1: coarse ground/region triangles ("collision mesh") [V structure, H meaning]
Loader func_0044D024 (overlay code, source in `port/build/m2c_valid/func_0044D024.c`). Container(4):
| sec | format | notes |
|---|---|---|
| 0 | typed array stride 100 (0x64) x 1..4 | 100-byte records, floats at +0x10.. (2816.0, 75.0, 1536.0, 47.5 ...), `002ABE7F` colour-like word, `000003E6` twice, `00969F03`, `27100000` ... Unknown [H: environment/fog or liquid layer definition, one per layer]. First word `000000FF`/`020000FF` (level 18 THE PIT), count 1..4 per level |
| 1 | typed array stride 16 | **vertices**: 4 x s32, each scaled by `0.029296875` (D_800C9ADC; func_00275AC8) -> x, y, z, w (w = 204.8 in level 0). Column-major in the file |
| 2 | typed array stride 0x14 | **triangles** (column-major): `u16 word0 (0..3)`, `u16 flags (0x41, 0x40, 0x49, 0x4040, 0x0 seen)`, `u16 v0,v1,v2`, `u16 n0,n1,n2` neighbour triangle across edge (0xFFFF none), `u32 surface` (0xFFFFFFFF in all 173 shipped triangles) |
| 3 | typed array stride 0x14 | `f32 xMin, zMin, xMax, zMax; u32 flag (1,2,3,17,18)`: 1..6 rectangles (probably the xz extents of the zones in sec 0) |
Runtime triangles are 0x20 bytes: `+4,+8,+C` vertex pointers, `+0x10,+0x14,+0x18` neighbour pointers, `+0x1C` surface word: the loader replaces it
with global `D_800D2B40` when it does not intersect that mask, and sets `flags |= 0x400` from a per-level bit table
(`scene+0x84` section) and resets the surface when `flags & 0x2000` [V code]. The point-in-triangle test in func_00275B80 works in the
xz plane and the plane normal comes from func_00275C38 (cached normal), used by func_0024BCF8 (facing/slope test) and the plane-height
helper func_00240A88. The loader also keeps `scene+0x1B2FC = min(vertex y)` over all triangles, i.e. the lowest ground height of the level
(level 18 THE PIT: y = -3738, a single big triangle far below the arena => fall-out/kill height). [V]
Counts: 1-18 triangles, 3-22 vertices per level; every placement references one of its level's triangles via +A / +2A. The meshes are
convex-ish partitions of the playfield at coarse height (see `topdown_collision.png`): **not** a detailed floor. [H: used for region
tracking / coarse ground normal, not for exact walking]. Triangle-level surface types (water/lava/slime) do **not** exist in the data:
the only flag bits are listed above (`surface_types.json` has the full histogram).

## 4. Sector array 3: light volumes [V that it is lighting; fully decoded in lighting.md]
**Update 2026-10-07: all fields are decoded in `lighting.md` (soft ellipsoid point lights, colour u32 at +0x3C/+0x40/+0x44, ambient fraction at +0x28; the game lights every placed piece per vertex on the CPU from them).** The notes below are the first-pass guesses.
func_0028994C(lights[], count, point, out) accumulates, for an actor at `point`, an ambient colour (6 bytes), three directional-light
colours and a light direction (3 s8) from the light volumes that contain the point (func_00277198 = point-in-volume with falloff).
Record (0x4C): `+0,+4,+8 s32 position * 100/2^22` (e.g. -385, 122, 1245 in level 0), `+C,+10,+14 s32 * 2^-30` rotation angles (radians, H),
`+18,+1C,+20 s32 * 100/2^22` extents (~800-1600 units, H: ellipsoid/box radii), `+24/+28 s32 * 2^-30` falloff factors (H), `+2F u8 type`
(1 would select the "directed" branch; **0 in all 701 shipped records**), `+30.. colour bytes`, `+37,+3B,+4B u8` copied to runtime
flags. 701 volumes in 37 levels (0..50 per level). Exported with raw hex in `lights.json`; the exact byte-level colour layout is not decoded.

## 5. Actor-vs-level collision data actually present [V that bounds are built this way, H that they are all there is]
func_00240250 (swept actor test) loops the actor array of the scene (+0x948), rejects with the AABB test on actor `+0x84..+0x98` against
`+0xE8..+0xFC` (the bounds above), then func_0023DA90 / func_0023FD08. For a static object whose table section 0 is the empty
`container(2) of 8-byte arrays` (257 of 347 objects, i.e. all static ones) there is no detailed hull; the 90 others (skinned) carry a
`[32 or more bytes of floats, ...]` hull block (func_0023FD08 reads 6 floats = min/max, builds 8 corners) and skinned models have the
hit volumes of `models_textures.md`. So **collision of the static world = the per-placement AABB** (walls are zero-thickness boxes, e.g.
`(-2458,-154,-1332)..(-2355,0,-1331)`): exported as `collision.obj` (g object_boxes) and `collision.json` (objectBoxes).
The AABBs are tighter than the model (about 307 units narrower per side over a whole level in level 0) because they are given in
10.24-unit steps and padded by only 0.5. [H that this is intentional (wall/floor planes are thin)].

## 6. Spawn points and nav graph (exported, meaning not decoded here)
`nav.json` per level: nodes (part 9: x, y, z, flags u16, objectType u16), edges (part 10), N x N reachability (part 11) and the actor
placements of part 3 (position, scale, word at +0x1C/+0x20/+0x24). 2,166 nodes and 430 actors in total. The overlay shows nodes on floor tiles.
Semantics (spawn / pickup / waypoint) belong to the gameplay-definition work.

## 7. Output files per level (`extracted/levelgeo/NN_name/`)
`world.obj/.mtl` (textured, vertex colours as `v x y z r g b`), `collision.obj` + `collision.json` (triangles with adjacency, zone words,
rects, object boxes with word0/flags), `lights.json`, `objects.json` (every placement: sector, record, form, objectId, def, position, scale,
quaternion, region triangle, bounds), `nav.json`, `summary.json` (counts, world and placement bounds, UV range, animated texture objects),
`topdown.png` (world, z-buffered orthographic) and `topdown_collision.png` (plus object boxes green, triangles red, rects yellow, nav nodes
cyan). Root: `surface_types.json`, `levels_summary.json`.

## 8. Unknown / next steps
* part 1 section 0 100-byte records and the flag/word0 values of triangles and rectangles;
* meaning of placement `word0` (always 0xFFFFFFFF), `flags` (0..6) and light bytes at +0x30..+0x4B;
* who selects models other than index 0 (objects 174/185/203), and level part 7 (moving objects), part 12 (box), sector array 4;
* whether the AABBs plus part-1 triangles are really all that the game collides with (verify with the oracle traces).


## Correction (2026-10-07): model space is Z-up
Static-object models are authored Z-up. Placing them with only the placement transform left every piece turned 90 degrees about X
(floors stood up as walls). The correct local-to-world step is a fixed rotation (x, y, z) -> (x, z, -y) applied to model vertices *before*
the placement's yaw/quaternion, scale and translation. Verified against the game's own per-placement AABBs: mesh extents and centre match
for 284 of 290 placements in level 0 (the rotation (x, -z, y) matches only 149). The rotation has determinant +1, so winding is unchanged.
`LevelGeoCommand.ModelToWorld` implements it, and all levels are re-exported.

Winding: the earlier claim "front face is clockwise, no ceilings" in `level_visibility.md` was derived from the wrongly rotated geometry and
is superseded. After the rotation fix there are 5,598 up-facing and 4,581 down-facing horizontal triangles (floors and ceilings) under the
standard CCW-front OBJ convention. The viewer's "cull clockwise" mode (keeps counter-clockwise faces) gives the correct picture. The game
still draws every placement and part (no per-part or per-side filter), as `level_visibility.md` found.


## Orientation search against the game's AABBs (2026-10-07)
Searching all 48 signed axis permutations (with and without inverting the placement rotation) against the game's own per-placement AABB
(extents and centre within 4 units) over all 37 levels:
* yaw-only (compact, 9,919 placements): `(x, y, z) -> (-x, z, y)` matches 9,860 (99.4%). This is the Z-up conversion followed by 180 degrees about Y.
  Plain Z-up `(x, z, -y)` scores lower. Not inverting the placement yaw (9,860) beats inverting it (9,773).
* quaternion (matrix34, 250 placements): plain Z-up `(x, z, -y)` 246, Z-up + 180 degrees about Y 243. Too few to separate them.
The AABB cannot tell a rotation from its mirror image (models are symmetric about x), so handedness was decided visually.
The viewer applies these defaults per record type (Tab switches which type the rotation keys edit); the exporter bake-in is still pending.


## Material flag 0x1 reverses the winding (2026-10-07)
Triangles of parts whose material flags have bit 0x1 set (0x49, 0x4B...) must be drawn with the opposite winding. Evidence: for the
`0x49` material all 718 horizontal triangles face down under the standard counter-clockwise convention, while ordinary `0x48` parts are mostly
up-facing floors; flipping them turns the 718 into up-facing floors, restores missing floor tiles and hides perimeter walls from outside.
Probably the display-list "cull front" geometry-mode bit; the decoder's loader path was not read to confirm. Five static objects use it
(88, 111-114, about 6 to 20 placements per level). Materials with bit 0x100 (0x148, 0x548) hold both windings (equal up/down counts), i.e. double-sided.
Implemented in `LevelGeoCommand.AddInstance` (baked world.obj) and the viewer (key G toggles it); local meshes now name materials `tex_<id>_f<flags>`.


## Orientation settings found by eye in the viewer (2026-10-07), now the viewer defaults
* yaw-only (compact) records: Z-up conversion, `pre` 180 degrees about Y, **mirrored in X**, winding mode none.
* quaternion (matrix34) records: Z-up conversion, `pre` (270, 270, 90) degrees about X/Y/Z, `post` 270 degrees about Y, placement rotation inverted.
These supersede the "plain Z-up / Z-up + 180" results from the AABB search, which cannot distinguish a rotation from its mirror image. The
mirror in X is the same thing as the game's left-handed coordinate system. The flag-1 reversed-winding rule is not needed with these settings
(the viewer shows it under winding mode G; default none). The exporter's baked `world.obj` still uses plain Z-up and needs these settings applied.
Two spots in Proving Grounds were still off; the viewer's face selector (P / middle click) and per-piece fixes (H hide, V flip winding, L mirror, N turn)
write `placement_overrides.txt` so they can be diagnosed and turned into rules.


## Scale axis order (2026-10-07)
Non-uniform placement scales (quaternion records, e.g. object 92 scale 0.2625 / 0.2625 / 0.175) must be applied in the *converted* frame, i.e.
`v -> Z-up conversion -> Scale -> extra rotation steps / mirror -> placement rotation -> post -> translation`. Scaling in raw model space or
after the extra rotation steps puts the narrow factor on the wrong axis (pieces came out 1.5x too wide). Checked against the game's own AABB
sizes for the 250 quaternion placements: 247 match with this order, 146 with scale-after-steps, 67 with scale in raw model space.
Yaw-only records have a uniform scale and are unaffected. Implemented in the viewer's `InstanceScene` (`RotationSettings.Base()` / `Tail()`).


## Quaternion pieces need a final-frame X mirror (2026-10-07)
After the scale-order fix, 16 quaternion pieces still had to be flipped by hand (levels 00, 02, 03, 04, 07; objects 22, 56, 92, 95, 104).
All 16 face along the world X axis (9 along -X, 7 along +X); no piece facing along Y or Z needed a flip. A mirror in X in the final world frame
(after the placement rotation and the post turns, before translation) reverses exactly those pieces and leaves Z-facing ones alone. With it:
the 16 manual fixes become unnecessary (Dire Straight screenshots differ by 246 px between "manual flips" and "rule only"), and the game's AABB
size/centre test improves from 243 to 247 of 250. It is the default for quaternion records in the viewer (`RotationSettings.PostMirrorX`, key M).
This is the same handedness difference the yaw-only records show with their mirror-x. Manual flips are kept in `placement_overrides.before_xmirror.txt`.
Open: sloped ceilings (mostly yaw-only objects 85, 86 and quaternion 100, 86, 98: downward-facing sloped triangles) are not changed by the X mirror;
the viewer now has final-frame mirrors on Y and Z (keys Y, Z) for either record type to find the axis they need.


## Quaternion pieces: the placement rotation is reflected through an X<->Z swap (2026-10-07)
The sloped ceilings in Dire Straight (objects 98 and 27) needed an extra Z mirror on top of the X mirror. All quaternion pieces that are not
turned about Y alone are rare (25 of 250: object 100 x20 in Cathedral, objects 98 and 27 in Dire Straight, one object 102 in Spirit Temple),
and the three groups behaved differently, which a global mirror cannot explain. One rule explains all of them: use the placement quaternion
as `q' = (-z, -y, -x, w)` (rotation reflected through an X<->Z swap) instead of its conjugate. For a rotation about Y this is identical to the
conjugate (so the 225 yaw-style pieces are unchanged), a 180 degree turn about the XZ diagonal (object 100) maps to itself (Cathedral unchanged),
and rotations about X or Z swap axes (objects 98 and 27 come out exactly as in the hand-tuned "X mirror + Z mirror" state).
Checked numerically: new rule vs Cathedral-approved state, 225/225 Y-only pieces identical and 22/25 tilted ones identical; vs the Dire Straight-approved
state, 4/4 identical. Implemented as `RotationSettings.SwapXZ` (key J, default for quaternion records). The final-frame X mirror stays.
Unverified by eye: object 102 (Spirit Temple, level 32).


## The levels are mirrored on X (confirmed by eye, 2026-10-07)
The raw level coordinates give a layout that is a mirror image of the real maps. Mirroring X confirmed it (F1 in the viewer). All the earlier
per-piece mirrors (mirror-x on yaw-only pieces, final-frame X mirror on quaternion pieces) were this single mirror leaking into each piece.
Cleaner formulation, pixel-identical to the previous tuned state on levels 0, 4, 7, 9 and 18 (0 differing pixels):
* **Piece positions:** `x -> -x` (`InstanceScene.WorldMirrorX`, on by default). Vertices are not mirrored.
* **Yaw-only (compact) pieces:** `v -> Z-up -> Scale -> Ry(180) -> placement yaw INVERTED (conjugate quaternion) -> translate`. A proper rotation.
* **Quaternion (matrix34) pieces:** `v -> Z-up -> Scale -> pre(270,270,90) -> q' = (-z,-y,-x,w) -> post Ry(270) -> translate`. A proper rotation.
No piece needs a mirrored (improper) transform and no manual per-piece flips are needed. AABB checks (which cannot see mirroring) are unaffected.
Not yet done: the exporter's baked outputs (world.obj, objects.json positions, nav.json, collision.json, lights.json, top-down PNGs) still use the raw,
unmirrored coordinates and the old orientation; the viewer (which assembles levels from `_objects` + `objects.json`) is the reference for now.
When baking: mirror x in every exported coordinate, not only in the meshes.


## Textures per placement, UV overrides, samplers (2026-10-07) - details in texture_mapping.md
* The collapsed texture coordinates on walkways and crates were a decoder gap: the compact display-list command `0x60|slot` is a per-vertex ST override
  (`G_MODIFYVTX`), now decoded in `Models/Model.cs` (extra vertices appended; `Model.PartOfVertex`). `LevelGeoCommand` now uses it and 11 of 133 object meshes changed.
* A texture's several "frames" are per-placement looks, not per-level themes: look = `ObjectDef.Part` (def +0x12, s8) `% frameCount` (`-1` = time-animated in the game, shown as look 0).
  `objects.json` has it as `textureKey`. The viewer builds batches per (texture, look, material flags) and loads `tex_NNN_f<look>.png`.
* Sampler from the material flags: clamp if `0x20`, else mirror if `0x800`, else wrap. Other flags: `0x10` environment map (constant UVs by design), `0x4` double-sided,
  `0x2`/`0x40` unlit (vertex RGB are normals otherwise). The earlier "flag 0x1 = reversed winding" idea is not confirmed by the game code and is off by default (viewer key G).
* Point filtering is the viewer default.
Not done: time-animated looks (key -1), the post-load palette recolour callback (func_00295B90), environment-map rendering, palette selection when paletteCount differs from frameCount.
