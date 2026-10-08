# Lighting (static pieces, actors, fog)

Status tags: **[V]** verified in the game code (C port `D:\Projects\TurokRageWars`: m2c_out / src / port\nonmatching; function names and ROM offsets given) and/or in the data of all 37 levels,
**[H]** hypothesis. Code: `tools/Trw.Tools/LevelGeo/Lighting.cs` (the game algorithm), `LightingExport.cs` (writers), `LevelGeoCommand.cs` (`EmitLocalMesh`, lights.json).
Outputs: `extracted/levelgeo/lighting.json` (all levels), `extracted/levelgeo/NN_name/vertexlight.bin`, `lights.json` (decoded fields added), `_objects/obj_*.obj` (now with `vn`).

## 0. Summary

| thing | how it is lit | where |
|---|---|---|
| **Static level pieces** (10,169 placements, 11,481 parts, every one has material flag 0x40 = no RSP lighting) | **CPU vertex lighting once per placement** from the sector's *light volumes* (sector array 3), no directional light, no RSP lighting. The vertex RGB bytes in the ROM model are **signed-byte normals**, not colours; the game overwrites them in a per-placement copy of the vertex block. | `func_00278530` [V] |
| Actors / creatures / projectiles | RSP lighting (G_LIGHTING, vertex bytes = normals) with one `Lights1` (ambient + one directional light) recomputed **every frame** from the light volumes at the actor's position | `func_0028994C`, `func_0028C6B0` [V] |
| Fog | RSP fog (G_FOG) with colour = clear colour of the current zone; start is so late (near=995 of 1000) that it only fades the far fifth of the view | zone record, `func_00235AD0`, `func_002343B8` [V] |
| Dynamic point lights (muzzle flash, explosions) | up to 8 positional lights uploaded per view to the RSP | `func_00265A00` [V upload, H ucode semantics] |

Nothing else: no per-level ambient constant, no per-level directional light (see 5), no lightmaps. The level meta table (archive section 18, 37 x 76 bytes) holds no colours [V: name, ids, one float].

## 1. Light volumes (sector array 3, 0x4C bytes) [V: func_00276D20, func_0028795C]

701 records in 37 levels = **169 distinct volumes** (the same record is stored in every neighbouring sector it touches, 3 to 7 distinct volumes per level, one level has none: 36/37 SCRAP). All shipped volumes: shape 0, not negative, no animation, not restricted [V, data].
Byte layout (big endian, after the column-major de-transpose):

| off | meaning |
|---|---|
| +0x00,+4,+8 | s32 position x,y,z * 100/2^22 (game units) |
| +0x0C,+0x10,+0x14 | s32 * 2^-30 = rotation angles in radians about X, Y, Z |
| +0x18,+0x1C,+0x20 | s32 * 100/2^22 = half extents (the volume is the ellipsoid inscribed in this box) |
| +0x24 | s32 -> `softEdge` = 2 * s32 * 2^-31 (runtime +0x00). Old name Falloff0 |
| +0x28 | s32 -> `ambientFraction` = 2 * s32 * 2^-31 (runtime +0x04), 0.59 .. 1.0. Old name Falloff1 |
| +0x2F | shape: 0 ellipsoid (all data), 1 box with fixed direction, 2 cone (never used) |
| +0x30..0x33 | editor copy of the colour as bytes `00 B G R` (not read by the game) |
| +0x37 | `negative`: subtractive light (never set) |
| +0x3B | `animKind`: 0 none, 1 flicker with per-volume phase, 2 flicker with position phase (never set) |
| +0x3C,+0x40,+0x44 | u32 R,G,B; **colour = u32 * 2.3748725652694702e-06** (D_800C9B88) = 0..255 scale, values up to ~272 occur |
| +0x4B | `restricted`: volume only applies to objects listed in sector array 4 (never set; array 4 is the empty container in every sector) |

Runtime matrix (func_00276D20, row-vector convention `v' = v * M`): `M = S(extent) * Rz(az) * Ry(ay) * Rx(ax) * T(pos)` with the game's rotation helpers (`func_00273CD8` Z, `func_00273B08` Y, `func_00273930` X; formulas in `Mat` in Lighting.cs).
`Inv` = inverse (world -> local unit cube). AABB = bounds of the 8 transformed corners of [-1,1]^3. `axisAligned` (runtime +0xB9) = all three angles within 0.01 rad; then tests ignore the rotation.
lighting.json has `matrix`, `inverse`, `aabbMin/Max` in the raw game frame and `*MirrorX` copies (M' = D M D, D = diag(-1,1,1,1)).

### Weight of a volume at a world point `p` with unit direction `n` (func_00277444 + func_00277198) [V]
```
q  = p * Inv                     (local; if axisAligned: q = p * diag(Inv) + Inv.translation)
nl = normalize(n * Inv3x3)       (local; no translation; zero stays zero)
if any |q.xyz| >= 1: no contribution
d2 = |q|^2 ; if d2 > 1: no contribution
T  = (int(d2*64) / 64) ^ 0.25                       (table D_800D2648, ROM 0xD3248, 65 floats; coarse: 64 steps of d2)
w  = (1 - T) < softEdge ? (1 - T) / softEdge : 1    (1 in the core, ramps to 0 at the surface)
h  = normalize(q)                                    (outward from the centre, local space)
t  = max(0, -dot(h, nl))                             (= N.L with L pointing from the surface point to the centre)
d  = w * (ambientFraction + t * (1 - ambientFraction));  d = max(d, 0);  if negative: d = -d
contribution = colour * d          (RGB, 0..255 scale)
```
So a volume is a *point light at its centre* whose fraction `ambientFraction` (mostly 0.8..1.0) is non-directional and the rest follows N.L, faded by a soft ellipsoid edge. Contributions of all overlapping volumes are **added**.

## 2. Static pieces: per-placement vertex lighting [V]

Call chain: `func_00289054` (per sector entry, sets `actor.owner` = sector blob) -> `func_0025021C` -> `func_00251F0C` (instance cache keyed by `actor+0xD0 | D_800D2640`, D_800D2640 = brightness option so a change re-lights) -> `func_00250BD4` -> **`func_00278530(model, sectorBlob, actorBounds, actorMatrix, 0, actor)`**; the draw then uses the lit vertex block as segment 2 (`func_0026DC24` queues `node` = lit block, `lights = NULL`: `func_0026B504` loads nothing).
1. **Volumes**: `func_00278090` takes the volumes of *the placement's own sector blob* (sector array 3, record order), keeps those whose world AABB overlaps the placement bounds (actor +0xB8..+0xCC, strict `>`/`<`), at most 0x40. Restricted volumes (+0x4B) need array 4 membership [not needed: none in data].
2. **Accumulator** per ROM vertex starts at 0 (plus the optional baked offsets of model section 1: one u32 per vertex, R/G/B = signed bytes 2/1/0 times 2; **section 1 is empty (8 bytes) in all 10,169 placed models**, so no baked term exists in shipped data).
3. `D_800D2850` is constant 1 (never written). If the sector has overlapping volumes: `func_00277620` for every vertex with `(Vtx.flag & 0x80) == 0` (never set):
   `p = (s16 x,y,z) * M`, `n = (s8 R,G,B) * M3x3` where **`M` is the placement matrix with the Y/Z rows swapped (see 2.1)**, scale included, no inverse transpose; then for each selected volume with `p` strictly inside its AABB: add `colour * d` (section 1).
   If no volume overlaps the object but the level has volumes: nothing is added (**vertex colour = 5**, i.e. nearly black; happens for 0..47 placements per level). If the level has no volume at all (only level 36): fallback `func_00277B08`: grey `56 + 255*max(0, N.L)` with the runtime light `D_8013B138` (not recovered; 1 placement).
4. `func_002782E4`: per channel `c = clamp(acc, 5, 255)`, then `c = c*(1-f) + 255*f` with `f = min(1, D_801462D8/255)` (options *brightness*, default assumed 0) and truncated to byte. With global flag `D_801462C8 & 0x4000` (dark mode, H) all colours are 0. Alpha byte untouched.
5. If any overlapping volume has `animKind != 0` (never in shipped data) every vertex also gets a copy clamped to [30,225] and flagged vertices flicker: `func_0027899C`, every second tick `colour = clamp(base + (s8)T[(tick + ((flag&0x70)>>2)) & 0x3F] >> ((flag&0x0C)>>2))`, T = D_800D2854 (90 s8, in lighting.json). The exporter supports this (4th byte of vertexlight.bin) but all bytes are 0.
The result is shown with material flag 0x40 (`G_LIGHTING` off): combiner `texture * SHADE` (+ fog), SHADE = the vertex colour computed above.

### 2.1 The game's exact placement matrix (a simplification for the viewer) [V]
`func_0024F940` / `func_0024FD4C` build `M = (Rot * ...)`: yaw matrix `[c 0 -s; 0 1 0; s 0 c]` (compact, n*90 degrees) or the quaternion matrix `func_002742B4` (`q = bytes/127`, normalised), rows scaled by the scale (`func_002734EC`), translation in row 3, and finally **`func_00273DDC` swaps rows 1 and 2 of the 3x3**. Applied to the raw model vertices `(s16 x,y,z)` as `v' = v * M`. That single row swap is what the viewer rediscovered as "Z-up conversion + mirror": determinant -1. **Check: the transformed model bounds equal the game's stored placement AABB for 10,145 of 10,169 placements** (tolerance 12 units; `Mat.Placement` in Lighting.cs). Positions are then in the raw game frame; the viewer/baked frame is `x -> -x`.

### 2.2 Using it in the viewer
* **Easiest, exact: `vertexlight.bin`** (little endian): `"TRWL"`, `u32 version = 1`, `u32 placementCount`, then per placement **in `objects.json` order** `u32 vertexCount` and `vertexCount * 4` bytes `R,G,B,anim`. Vertex order = the vertex order of `_objects/obj_<id>_<modelIndex>.obj` (`v`/`vt`/`vn` index, including the vertices appended for ST overrides). `vertexCount` is 0 if the model is missing. `anim` = original flag bits (0x70 phase, 0x0C shift), 0 everywhere in shipped data. Use `rgb/255` as the vertex colour (`texture * colour`), do not use any other vertex colour for static pieces.
* **Re-lighting yourself** (e.g. other volumes, brightness option): use `vn` (unit model-space normals from the signed bytes; the game's normals are not unit but only their direction matters) and the volumes of `lighting.json`. Normals must be transformed with the same matrix as positions, **rotation+scale only, no inverse transpose** (as the game), e.g. in the raw frame `n_world = normalize(n_model * M3x3)` with the swapped-row matrix above; in the viewer's mirrored frame mirror `x` of the result. Everything is evaluated per vertex in world space against the volume's `inverse` (use the `*MirrorX` variants when positions are mirrored).

## 3. Actors [V code, H RSP behaviour]
Per frame and actor: `func_0028C6B0(level, point, out Lights1)` (called from `func_0021C9B4` players, `func_0028FFB0` projectile/effect actors, `func_0027DD1C`; point = position + collision-height * 0.5 (+ `unk70` for players)). If the level has no volume or `D_800D2850 == 0`: `out = D_800D0EE0` (ambient 56,56,56; light 255,255,255; dir s8 (-42,30,-36), length 63/127, not unit [H: the RSP does not normalise, so the default diffuse is weaker than the CPU fallback]). Otherwise `func_00289790` collects up to 10 volumes of the sector cell containing the point whose AABB contains it, then `func_0028994C` (`scale = D_801462D8/255`):
```
for each volume i that contains the point (func_00277198 gives w_i; world direction d_i = normalize(pos - M*(q + normalize(q))) = toward the centre, ellipsoid-scaled):
    s = ambientFraction_i / 2
    amb += colour_i * (w_i * s)          (negated for negative volumes)
    w_i' = w_i * (1 - s)
    Lacc += d_i * w_i' * (|r|+|g|+|b| of colour_i)
L = normalize(Lacc)                       -> light direction (s8 = L*127)
for each volume i:   k = max(0, dot(L, d_i));  e_i = (1 + k)/2;  a_i = 1 - e_i
    light += colour_i * (e_i * w_i');   amb2 += colour_i * (a_i * w_i')
ambient = amb + 0.5 * amb2
if ambient.c < 0: light.c += ambient.c
ambient, light: clamp each channel to [5,255], lift by scale (as 2.4)
Lights1 = { ambient (col=colc), light colour (col=colc), dir = L*127 }
```
Dark mode (`D_801462C8 & 0x4000`): ambient 0, light 0, dir (127,0,0). The pulsing term (`animKind`, `func_00278900`) is unused in data.
Draw: `func_0026B504` emits `G_NUMLIGHT 1` (`DB020000 00000018`), `MOVEMEM` light at DMEM 0x30 (`DC08060A`) and ambient at 0x48 (`DC08090A`) with the actor's `Lights1`; materials without flags 0x42 are lit by the RSP (vertex bytes = normals); flag 0x8000 + view mode uses the debug light `D_800D1600`. Expected RSP result (F3DEX2-like, H): `shade = ambient + light * max(0, N.L)`, clamped, N = vertex normal * model matrix, L = light direction. LookAt vectors for environment mapping (flag 0x10) are loaded per view (`DC08000A`, `DC08030A`).
Dynamic point lights (`func_00265A00`, list `D_8013B1A8`): 8 slots of 16 bytes written with `MOVEMEM` at offsets 0x50.. (`(i*2+10)*8`): `s16 x,y,z` (view space, -x, y, -z), colour bytes = definition bytes * `k`, `s16 w0, w2` (definition), `0x7F80`; unused slot has `wA = 0x8000`; geometry-mode bit 0x80 enables them (cleared with `D9FFFF7F`). Their effect belongs to the custom microcode (not decoded) [H].

## 4. Fog and clear colour [V]
Per-level *zone records* = **level part 1 section 0 (100 bytes each, 1..4 per level)** (the "100-byte records" of level_geometry.md section 3; selected by a u16 zone key, `func_0028B2D4`, default record 0; the ground triangles' `word0` 0..3 [H] are the zone index). Words (big endian), exported in `lighting.json levels[].zones`:

| word (off) | meaning |
|---|---|
| 0 (+0) | RGBA colour of the zone (normal): **clear colour and fog colour**, used as `rgb & 0xF8` (`func_00235AD0`: `G_SETFOGCOLOR` and the background fill) |
| 1 (+4) | RGBA colour while the player is submerged / hit by lava etc. (player flags `+0x664 & 0x40`, `+0x38 & 0x1000/0x8000/0x10000/0x20000`): blue `(0,42,190)` for water, `(255,0,0)` lava, `(164,164,0)`, ... |
| 2, 3 (+8,+0xC) | fog near for normal / alt state (s32, 998 or 1000 everywhere) |
| 4, 5 (+0x10,+0x14) | float far plane and field of view in degrees (normal): 1536..5120, 47.5 or 75 |
| 6, 7 (+0x18,+0x1C) | same for the alt state (far 1536 etc.) |
| 9 (+0x24) | transition time (1.0) |

`func_002343B8` fades between the previous and the target values (fade factor `unk51C`, speed 1/(`+0x24`*...)). The fog value is clamped to **995 (0x3E3)**: `G_MW_FOG` (`DB080000`): `mult = 128000/(1000-near)`, `offset = ((500-near) << 8)/(1000-near)`, geometry mode `G_FOG` (0x10000, `D9FFFFFF 00010000`) is set by the view setup and per material (all except flag 0x1000). With near = 995 the fog factor (F3DEX2: `fog = (zb - near)/(1000 - near)`, zb = depth buffer value 0..1000) is 0 until 99.5% of the depth range.
The projection is `guPerspective(fov, aspect, near = 16.0, far = zone far, scale)` (`func_00234FDC`, D_800C8378), so `zb = 1000 * far/(far-16) * (1 - 16/z)`; fog starts at `z = 16 / (1 - 0.995*(far-16)/far)` (about 1500 for far 2816, 1040 for far 1536) and is full at `z = far`: **fade towards the zone colour over the last ~45% of the view distance** (mostly black, hence "darker with distance"). Formula for a viewer: `f = clamp((zb - 995)/5, 0, 1); out = lerp(shaded, fogColour, f)`.
View flag `+0x120 != 0` (special mode) uses black fog at 995..1000 and monochrome colour; not used by levels.

## 5. What is *not* there [V]
* No per-level ambient colour or global directional light for static pieces; the only global lights are `D_800D0EE0` (actors outside any level with volumes) and the debug light.
* No lightmap: model section 1 (per-vertex offsets) is empty for all placed models, all `Vtx.flag` low bytes are 0.
* No RSP lighting of level pieces: `G_LIGHTING` is off for all 11,480 parts with flag 0x40 (the single 0x42 part is also unlit).

## 6. Uncertain
* RSP microcode semantics (custom ucode): lit actor shading formula, the 8 positional lights, fog factor details, `G_MW_FOG` units. Everything on the CPU side (static pieces, Lights1) is exact code translation except float rounding (the game converts the placement matrix to 16.16 fixed point before use: errors < 1e-4).
* Default for the brightness option (`D_801462D8`, 0 assumed) and the dark-mode flag meaning.
* Volume `animKind`, `negative`, `restricted`, shapes 1 and 2 are implemented per code only partly (exporter throws on shape != 0; none exist).
* Which zone record is active where (index source), and the fallback light `D_8013B138` (only level 36, 1 placement).
