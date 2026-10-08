# Texture mapping: UV overrides, material flags, sampler modes, frame choice

Status tags: **[V]** verified in game code (C port: `func_0026AD8C`, `func_0026C020`, `func_00269A80`, `func_00295FB4`) and/or in data, **[H]** hypothesis.
Found while investigating "degenerate UVs" on walkways (tex 162, flags 0x848) and crates.

## 1. Root cause: the 5-byte display-list command is a vertex ST override, not a colour [V]
Compact display list command `0x60|slot, b1 b2 b3 b4` is expanded by `func_0026AD8C` to `w0 = 0x02140000 | slot*2`, `w1 = b1<<24|b2<<16|b3<<8|b4`.
That is F3DEX2 `G_MODIFYVTX` (0x02) with offset **0x14 = G_MWO_POINT_ST** (0x10 would be the colour offset): it writes `s16 S = w1>>16, s16 T = w1&0xFFFF`
into RSP vertex-buffer slot `slot`. Consequences:
* A ROM `Vtx` (section 0) holds ONE ST, but the same vertex is shared by faces that need different UVs. The converter handles this by emitting an ST override
  just before the triangles that need it; the slot keeps it until the next `G_VTX` reload. The old decoder ignored these commands, so shared vertices kept the ST of
  the first face: the "collapsed" UVs (e.g. obj_46: `v = 3.998` on the whole second half of the ramp). The game does NOT re-load vertices per face and does not
  patch Vtx in the expander (the expander only copies/translates commands).
* The override value is the *final* buffer value. The RSP multiplies a vertex's ST by the `G_TEXTURE` scale (material `+0x14/+0x16`) when `G_VTX` loads it, but
  `G_MODIFYVTX` bypasses that. So `u = Sovr/32/texWidth` for overrides, `u = S/32*scaleS/65536/texWidth` for ROM vertices.
  Check, obj 46 part 0 (scale 0x2000/0x4000): ROM vertex (S,T) = (16375,-24563); override (2046,-3) = (16375/8, -12/4), i.e. the same coordinate as the
  y=-878 edge. Each half of the ramp therefore maps v from 1.0015 to 3.998 (texture is 32x64; u 0..2, v 1..4).
* Data: 243 of 386 model chunks contain overrides (~25,700 commands). Textured triangles with collinear UVs (non-degenerate in 3D): 8,714 before, 6,225 after.
  Of the remaining, 4,441 have `flags & 0x10` (texture generation, UVs unused, section 2) and about 1,800 (4.9% of the others) are legitimate
  single-column/row mappings (colour strips). All triangles of objects 5, 7, 8, 25, 26, 46, 98, 110, 147, 225, 280 are non-degenerate after the fix.
* Decoder: `Model.ExpandStOverrides` replays the display list like the RSP buffer. Each distinct (source vertex, ST) becomes an extra entry appended to
  `Model.Vertices` (same position/colour/bone). `Vtx.S/T` are now `float`; copies store `finalST*65536/scale`, so **the old consumer formula
  `u = S/32*scaleS/65536/w` stays valid**. `Model.OriginalVertexCount` = ROM vertex count; `Model.PartOfVertex` = part (material) of every entry incl. copies.
  `BuildTriangles()` uses the copies. Skinned models get the same expansion (BoneOfVertex copied).

## 2. Material flags (u32 at material +4) [V = read from func_00269A80 / func_00295FB4 / func_0026C020]
| bit | meaning |
|--|--|
| 0x1 | not tested in the setup code; the documented winding reversal stays a hypothesis |
| 0x2, 0x40 | **unlit**: `(flags & 0x42) != 0` -> geometry mode without `G_LIGHTING`, vertex RGB is a colour. If both clear: **lit, vertex RGB bytes are normals**, not colours (flags 0x8/0x9/0x28... meshes). 0x40 also sets bit 0 of runtime `+4` (draw-order key = Field8*2 | 1) |
| 0x4 | double sided: `G_CULL_BACK` is set unless this bit is set |
| 0x8 | not tested in the setup code (on most textured parts) [H: just "textured"] |
| 0x10 | **texture coordinate generation**: geometry mode `G_TEXTURE_GEN|G_LIGHTING` (0x60000, spherical env map, not LINEAR) and the G_TEXTURE scale is replaced by `1 << (log2 size + 6)` per axis. Vertex ST are ignored: all 4,441 triangles with this flag are "degenerate" by design. Viewer: flat texture or normal-based UV |
| 0x20 | **clamp** (G_TX_CLAMP) on S and T; the tile mask is then 0 |
| 0x800 | **mirror** (G_TX_MIRROR) on S and T (mask = log2 size). With 0x20 as well: cm = 3 (clamp+mirror) |
| 0x100 | part is skipped when colour1 alpha (runtime +0x13) is 0 |
| 0x400 | uses colour2 (+0x14..0x16) as an extra combiner colour, alpha from +0x13 |
| 0x2000 | extra othermode-high write and `G_TEXTURE` level/detail (func_0026C020 emits 0xD7002802, 0xE3000F00) [H: detail/second pass] |
| 0x80, 0x200, 0x1000, 0x8000 | combiner / render-mode / blend selection (alpha, fog, z); not decoded |
Clamp/mirror use the same value for S and T: `cm = (flags&0x20 ? 2 : 0) | (flags&0x800 ? 1 : 0)` (F5 SETTILE cmS bits 8-9 and cmT bits 18-19) [V].
The walkway family `0x848` = unlit(0x40) + mirror(0x800) + 0x8: **mirrored on both axes**. Its UVs run u 0..2 and v 1..4, i.e. 2 and 3 tile periods; a wrap sampler
repeats instead of mirroring and shows the wrong orientation on every second period.
Collapsed UVs would NOT be explained by clamp vs wrap: a constant u/v samples the same texel column under every address mode. They were purely the missing overrides.

## 3. Sampler recipe for the viewer
Parse the hex after `_f` in the material name `tex_<id>_f<flags>`:
* `flags & 0x20` -> Clamp on U and V
* else `flags & 0x800` -> Mirror on U and V
* else Wrap
* `flags & 0x10` -> ignore UVs (env map); `(flags & 0x42) == 0` -> vertex RGB are normals: do not use them as colour (use colour1/white); `flags & 4` -> no culling.

## 4. Which texture frame / palette [V code, tick rate H]
`func_00269A80(material, key)` -> `func_00295FB4`. `key` is the low byte of the queue sort key = **object definition byte `+0x12`** (`ObjectDef.Part`; "s8 passed to
the model draw function" in level_geometry.md 1.3), taken as `s8`:
* `key >= 0`: fixed look: `frame = key % frameCount`, `palette = key % paletteCount`.
* `key == -1`: animated: `k = (globalTick / header.p1) & 255; frame = k % frameCount; palette = k % paletteCount` (`p1` = byte 1 of the texture header, so p1 is the animation divisor; tick rate H ~30 Hz).
Data (def +0x12 of the placements): obj 46 -> 1, obj 110 -> 6, obj 25 -> 6, obj 225 -> 4, obj 8 -> 2, obj 26 -> 2/3/7, obj 7 -> 1,2,3,4,6,7,8,17, obj 147 -> 1/3,
obj 98 -> 1..8, obj 280 -> 0. So texture 162's 10 frames are **per-placement theme variants**: use `frame = def.Part % frameCount` (not time-animated for these).
`CiTexture.ToRgba(frame)` defaults to palette `min(frame, pal-1)`, identical when paletteCount is 1 or equals frameCount; otherwise pass `palette: key % Palettes.Length`.
A post-load callback (func_00295B90) can additionally recolour palettes by a 2-bit mode (high bits of the texture table entry) [H, not decoded].

## 5. Tool changes and what the viewer must do
* `Model.cs`: overrides expanded (section 1). `ModelsCommand.WriteObj` uses `Model.PartOfVertex`. `AnimCommand` per-bone vertex count counts ROM vertices only.
* `levelgeo` re-run: 11 of 133 `_objects` meshes changed (extra v/vt appended, some faces re-pointed): 5, 7, 8, 25, 26, 46, 110, 147, 225, 280, 324.
* **LevelGeo to change**: in `LevelGeoCommand.GetModel`, replace the hand-built `lm.PartOfVertex` loop by `lm.PartOfVertex = model.PartOfVertex` (copies otherwise get part 0 = wrong
  texture size on multi-part models). Add the def byte (`ObjectDef.Part`) to `objects.json` (e.g. `textureKey`) and, ideally, a `# texkey` per placement so the viewer can pick the frame.
* **Viewer to change**: per-material sampler from the flags (section 3); texture frame per placement from the def byte (section 4); unlit/lit and texgen handling as in section 2.
