# Creature texture V orientation (investigation 2026-10-07)

Data: `extracted/creatures/uv_orientation.json` (per object/texture `flipV`, evidence, confidence, `dTdUp`, list `uncertain`).

## Code findings (C port recomp: func_00269A80, func_00295FB4, func_0026AD8C, func_00295B90) [V]
* One texture-setup path for static and skinned parts. `G_TEXTURE` word = `0xD7000002 | level<<11`, `sScale<<16 | tScale&0xFFFF` taken from material +0x14/+0x16 (or `1<<(log2+6)` with flag 0x10). No sign handling, no per-model T convention.
* Tile: cmS/cmT = (clamp 0x20 -> 2) | (mirror 0x800 -> 1), mask = log2 size (0 if clamp). Texture header bytes [4]/[5] and every header in the 507 textures are uniform (`..FF000000`), no discriminator.
* Table-entry 2-bit mode (func_00295B90) only recolours palettes (RGB565-style conversions), not geometry.
* The expander func_0026AD8C does not touch Vtx S/T for skinned models.
* Material ScaleT is never negative for creature materials (values 0x400..0x8000; 0x8000 = 0.5, appears on 142:33/34 and 174:201/206). Nothing in ScaleT, flags (0x8/0x808/0x4009/0x88), fmt or size separates "flip" from "no flip" (128:17 and 130:74 have identical flags and scales but opposite by-eye result).
So the game code does not explain the inversion.

## Geometry measure [V on data]
For each triangle solve dP/dt in rest-pose world space (restWorld, Z-up) and take the area-weighted mean Z component, `dTdUp`.
* Static level meshes (extracted/levelgeo/_objects, vertical faces): t increases DOWN (dTdUp < -0.3) in 91 of 94 objects (rest 3 positive, mostly decals).
* Creature materials: dTdUp > 0 (t increases UP) in most materials of 128-174 (e.g. 128:21 +0.71, 139 all >= 0.05, 130:13 +0.49).
Conclusion [H]: skinned-model T is stored inverted relative to the static convention. Viewer: v = 1 - t for creature materials.
Conflicts: by-eye says 128:17,18,22,66,67,145 (and 19/20/23 wood) look right unflipped, but their dTdUp is +0.2..+0.96 (17: +0.82, 67: +0.96). They are fur/cloth/wood with weak visual cues; the json keeps the by-eye answer with confidence "low (geometry disagrees)". Test 128:17 (white fur trim vs purple robe) and 128:18 first.
Weapon viewmodels (152-173) are in first-person space: Z is not up there, so their sign is only weak evidence (many entries marked low).
Handedness of UV mapping is useless (meshes are half mirrored copies: ~0 mean).
