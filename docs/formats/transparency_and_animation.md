# Transparency, blending and texture animation

Status tags: **[V]** verified in game code (C port: `func_00269A80` material setup, `func_00295FB4` texture load, `func_00268CE0` combiner table, `func_0026925C` render-mode table,
`func_0026BC60` / `func_0026C020` batch sort+draw, `func_00295B90` post-load callback) and/or in the data, **[H]** hypothesis.
Machine-readable result: `extracted/levelgeo/texture_anim.json` (`trw texanim`, code `tools/Trw.Tools/Models/TextureAnim.cs`).

## 1. Material record at runtime (func_00285540 copies the 24 ROM bytes) [V]
`+0 flags, +4 u16 sort id (= Field8*2, |1 if flags&0x40), +6 FieldA, +7 cache byte, +8 texture handle, +0x10..13 colour1 RGBA (PRIM), +0x14..17 colour2 RGBA (ENV), +0x18/0x1A G_TEXTURE scale`.
`func_00269A80(material, key)` builds the RDP state for one part; `key` is the placement draw key (def byte +0x12, `objects.json textureKey`, s8).

## 2. Texture format byte (texture header byte 0) [V, was wrong before]
`func_00295FB4` jump table: 0 = CI8 + RGBA16 TLUT, 1 = CI4 + RGBA16 TLUT, **3 = CI4 + IA16 TLUT** (`G_TT_IA16`, 0xC000), 2/4/5 = direct RGBA32/RGBA16/IA16 (not present in the 507 tables).
Format 3 (30 textures: water 91/161, smoke, explosions, shadows) has palette entries `I8 (high byte), A8 (low byte)`: **8-bit alpha per palette entry**, grey RGB.
Old `CiTexture.ToRgba` decoded them as RGBA5551 (garbage colours, alpha 0/255). Fixed in `Textures.cs`; `trw texanim` rewrote the existing format-3 PNGs (352 files).
Water: tex 91 alpha 176, tex 161 alpha 208 (constant per texel, intensity 106..241). RGBA5551 palettes (formats 0/1) give alpha 0 or 255 (bit 0).

## 3. Combiner and alpha [V]
Combiner index chosen by `flags` (mode 0 = normal rendering; globals `D_800D15E0 != 0` are tint/fade effects used by dissolving objects and ghosts, not by level geometry):
| flags | combiner (RGB ; alpha) |
|--|--|
| no 0x400, bit 0x2 set | `T0` ; `T0a * PRIM.a` |
| no 0x400, bit 0x2 clear | `T0 * SHADE` ; `T0a * PRIM.a` |
| 0x400 and bit 0x2 | `lerp(ENV, PRIM, T0)` ; `T0a * PRIM.a` |
| 0x400, bit 0x2 clear | `lerp(ENV, PRIM, T0) * SHADE` ; `T0a * PRIM.a` |
| bit 0x1000 | alpha becomes `T0a * SHADE.a` (vertex alpha) in all four rows |
PRIM = colour1 (RGBA), ENV = colour2 RGB with ENV.a = colour1.a. So colour1.rgb is **only** used with 0x400; otherwise only colour1.a matters. `lerp(ENV,PRIM,T0)` is per channel with the texel RGB as factor
(grey water texture -> colour2 in the dark parts, colour1 in the light parts). `T0a` is the texel alpha (RGBA5551 bit -> 0/255, IA16 alpha -> 0..255).
Flag 0x2 (not 0x40) is what removes the vertex-colour multiply; 0x40/0x2 both clear G_LIGHTING (texture_mapping.md).
Untextured materials: `PRIM` (x SHADE unless 0x2), alpha PRIM.a; flag 0x8000 untextured = debug yellow/red.

## 4. Blend / depth / alpha-test: render mode choice [V]
| flags | mode | RDP state |
|--|--|--|
| **0x100** (translucent) | 1 (0x1000: 3, 0x200: 4) | `FORCE_BL`, blend `src*a + dst*(1-a)`, Z compare, **no Z write**; 0x200: decal depth mode; 0x1000: no AA |
| none of 0x100, **0x80** | 6 | AA, Z write, `CVG_X_ALPHA|ALPHA_CVG_SEL`: texel/prim alpha as coverage (cutout) |
| none of 0x100, 0x200 | 7 | decal depth mode, no Z write, alpha-as-coverage |
| everything else (opaque) | 8..11 | Z write, `ALPHA_CVG_SEL` (alpha-as-coverage) |
Consequences for the viewer:
* **Opaque and cutout**: all textured non-0x100 materials use alpha as coverage, so texels with alpha 0 are not written: **alpha test** (discard alpha < 0.5). colour1.a is 255 for every such material in the data.
* **Translucent = flags & 0x100**: standard alpha blend (`SrcAlpha, InvSrcAlpha`), depth test on, depth write off. Part is skipped if colour1.a == 0. No additive mode exists anywhere in the render-mode table (all 31 entries checked: only
  `A*src+(1-A)*dst` or opaque/coverage), so **no additive blending** [V].
* Fog: geometry mode fog bit (`D_800D15D4`) is set for every material **except** those with 0x1000. Fog colour/range are level state, not material state.
* 0x4000: after the normal batches the part is redrawn as a flat blob shadow (fixed IA texture `D_44BDD0`, render mode 29 = XLU decal, alpha = colour2.a). 0x4008/0x4009 materials. Optional.
* 0x2000 (detail/LOD second tile, `hdr[5]` selects the combiner) does not occur in any level object; 0x80, 0x10000000, 0x20000000, 0x40000000, 0x80000000 bits are not tested by these functions.
* Flag 0x400 and 0x200 also put the part in the translucent list (see 5) although only 0x100 changes blending; 0x400 materials with colour1.a = 255 are cutouts of the IA/RGBA alpha drawn late (0x50A, 0x530...).

## 5. Draw order [V]
Parts are collected per model batch (one model, <= 32 instances, `func_0026BC60`) into two lists sorted by `key = (rec.id << 8) | drawKey`, `rec.id = Field8*2 | (flags&0x40 ? 1 : 0)`:
list A (`flags & 0x700 == 0`: opaque), list B (`flags & 0x700 != 0`, i.e. 0x100/0x200/0x400). `func_0026C020` draws A ascending, then the 0x4000 shadow pass, then B ascending. The sort is per batch, not global
(a translucent part of batch 1 is drawn before opaque parts of batch 2; Z-compare without Z-write keeps it mostly correct). **Viewer: draw all opaque/cutout first, then all translucent parts ordered by `sortKey`
(= Field8*2|(flags&0x40), exported per material), back-to-front inside equal keys is not needed.**

## 6. Which objects are translucent / water [V, data]
Only three static objects in the 37 levels use translucent materials (all with draw key -1), all single-part:
| object | texture | flags | colour1 / colour2 | alpha | levels | meaning |
|--|--|--|--|--|--|--|
| 102 | 91 (20 frames, fmt 3) | 0x548 | (0,128,255,255) / black | texel 176/255 | 10, 28, 29, 32 | **water** (blue) |
| 97 | 161 (20 frames, fmt 3) | 0x548 | (0,255,128,255) / black | texel 208/255 | 10 (WATER TEMPLE) | water/slime (green-cyan) |
| 96 | 83 (19 frames, fmt 1) | 0x148 | (137,50,50,178) / white | colour1.a 178/255 = 0.70 | 12, 14, 16, 23, 25, 26, 35 | **lava** (red; texture frames carry the lava look, colour1.rgb unused because no 0x400) |
Colour of 0x548 water: `rgb = lerp(black, colour1, texelIntensity) * vertexColour`, alpha = texel alpha. Other translucent materials in the model tables (flags 0x108/0x110/0x10C/0x109/0x189/0x10A..., colour1.a 42..158: glass, shadows, energy, fog sheets)
belong to objects that no level places statically (actors, effects, pickups); `texture_anim.json materialFlagCombinations/objects` lists all of them with their `Look`.

## 7. Frame and palette selection [V]
`func_00269A80`: `frames = image count`, `palettes = palette count` (texture sections 1 and 2).
* key >= 0: `frame = key % frames`, `palette = key % palettes` (the load callback `func_00295B90` with page `pg >= 0` even keeps only that frame and palette in memory).
* **key -1 (animated)**: `k = (tick / header.p1) & 255; frame = k % frames; palette = k % palettes` (unsigned divide). p1 is byte 1 of the texture header = ticks per step.
* key -2 (random flicker, never used by a placement): when `k` changes a random frame is chosen once per material, other draws use `k % frames`.
* `tick` = `D_800D2980`: +1 once per rendered game frame (`func_00293038`), 0 at level start (`func_00294608`/`func_002946A8`). Frame-locked, not dt-based. Real frame rate [H] ~30 Hz (retrace arg 1 only
  says one message per VI retrace); expose it as a constant (`tickHzHypothesis` = 30 in the JSON).
* The 256 mask makes the cycle restart every 256 steps even if `frames` does not divide 256 (19 and 20 frames: a small hiccup at the wrap, reproduce it or ignore it).
Data: p1 = 1 for every animated texture used by a level (83: 19 frames, 91: 20, 161: 20 -> one step per tick, ~30 steps/s if 30 Hz). All 123 textures with more than one frame: p1 = 1 (108), 2 (3), 3 (2), 4 (2), 5 (4), 6 (2), 7 (1); the others are theme variants chosen
by the key (texture_mapping.md). Textures with several palettes: 86 have `palettes == frames` (PNG naming already right), tex 299 has 1 frame and 6 palettes (object 129, selected by key -> palette `key % 6`).
**Exactly 3 time-animated textures exist in level geometry** (83, 91, 161, the three objects above), no other texture of any level object uses key -1.

## 8. Palette-cycled PNG variants
Classic names: `tex_NNN_f{frame}.png` = frame `frame` with palette `min(frame, palettes-1)` (unchanged). Additional look needed when `palettes > 1` and the palette is not that one:
`tex_NNN_f{frame}_p{palette}.png` (written by `trw texanim` into `extracted/models/textures`; today only tex 299: `_f0_p1` .. `_f0_p5`). For a pair `(frame, palette)` from the formulas above the file name is
`palette == min(frame, palettes-1) ? "tex_NNN_f{frame}.png" : "tex_NNN_f{frame}_p{palette}.png"` (`CiTexture.FileName`). `trw models --all` only writes `f{n}` files; run `trw texanim` after it.

## 9. UV scrolling and other per-frame effects [V negative result]
* No time-based texture-coordinate offset exists in the model/level material path: `func_00295FB4` takes tile shifts as constant arguments (always 0 from `func_00269A80`), the per-part `G_TEXTURE` scale (`+0x14/+0x16`) is a
  constant, no vertex ST is modified per frame (G_MODIFYVTX overrides are static ROM data), and none of `func_00269A80/func_00295FB4/func_0026C020/func_0026BC60` read the tick except for the key -1/-2 frame choice.
  Water/lava motion is **only frame cycling**; `texture_anim.json` carries `uvScroll=false`. (Conveyors/lights/flickers in level objects are therefore either frame variants or actor logic, not scrolling.)
* Other tick users are unrelated to materials: pickup glow (`func_00233DEC`, sin of the tick), blinking player markers (`D_800D2980 % 15`), particle sprites (`func_0027ED40`, own frame logic).
* Palette recolour (`func_00295B90`, global `D_800D2B04`, set per actor from actor field `+0x120` in `func_00235AD0`): team/skin colours on actors, applied to the palettes when the mode is nonzero. For each RGBA5551 entry (r,g,b 5 bit):
  mode 1: swap r and g; mode 2: g = r; mode 3: r = g; mode >= 4: grey = (r+g+b)/3; alpha bit kept. Static level objects always draw with mode 0, so nothing to do for levels. The 2-bit value in `obj[2] >> 30` (resource entry) is an
  equivalent per-resource selector [H, unused in the texture table: all 507 offsets have top bits 0].

## 10. What the viewer must do (summary)
1. Parse flags from `tex_<id>_f<flagsHex>` (or `texture_anim.json`). `flags & 0x100` -> translucent: blend `SrcAlpha/InvSrcAlpha`, depth test on, depth write off, drawn after all opaque, ordered by `sortKey` (Field8*2|(flags&0x40)); skip when colour1.a == 0. Everything else: opaque with alpha test (discard alpha < 0.5), depth write on.
2. Output colour: `flags & 0x400` -> `lerp(colour2.rgb, colour1.rgb, tex.rgb)` else `tex.rgb`; multiply the vertex colour unless `flags & 0x2` (0x40 materials: vertex RGB are colours; lit materials: normals, use white). Alpha = `tex.a * colour1.a/255` (`tex.a * vertex.a` with 0x1000).
3. Animated placements (`textureKey == -1`): per frame `k = (tick / p1) & 255`, `frame = k % frames` (+ `palette = k % palettes`, file name by `FileName`); tick advances once per game frame (assume 30 Hz). Fixed keys: `frame = key % frames`, `palette = key % palettes`.
4. No UV scrolling. Re-run `trw models --all` then `trw texanim` so the format-3 PNGs (water, smoke) carry their real alpha.
