> **Superseded in part:** the winding conclusion below was derived before the Z-up model rotation was found (see `level_geometry.md`, "Correction"). The finding that the game applies no per-part or per-side filter still stands.

# Level visibility: what the game really draws of a placed static object

Status: **[V]** verified in game code (`D:\Projects\TurokRageWars`, m2c output / asm) and against the data of all 37 levels, **[H]** hypothesis.
Code: `tools/Trw.Tools/LevelGeo/Visibility.cs` (pure functions). Builds on `level_geometry.md` (placements) and `models_textures.md` (materials).

## 0. Result
There is **no** per-part, per-triangle, per-side or per-flag selection of static level geometry in the ROM data. Every placement of every
level and every part of every model passes all tests the game applies; the only culling left is view dependent (frustum / sector rectangle).
The "walls on all sides" look is explained by **one-sided quads + back-face culling with the wrong winding**: every wall, floor and ramp
is a single quad (a placement is mostly one 2-triangle part), the game draws it with back-face culling, and in our exported coordinates the
front face is **clockwise** [V, section 4]. With the wrong (counter-clockwise) front, floors are culled and back faces kept.

## 1. Draw path of static objects of a sector [V]
`func_00289054(scene, camera)` loops the sectors overlapping the camera box (list built by `func_00288440`) and, per static actor
(0xE8-byte records of sector arrays 0/1):
1. `actor.def->+4 (u32) & D_801462D0.mask` must be non-zero. The mask is a mode/detail word that only takes 0x8, 0x10, 0x20
   (`func_0044438C`, `func_00444484`). All static definitions 0..52 have 0x139 (def 51: 0x39), which contains all three: passes in every mode.
2. Optional PVS: `scene+0x1B928 != 0` -> `func_00297390` consults a per-camera-cell visibility table. Only active when level part 12 is
   non-zero; **part 12 is all zero in all 37 levels**, so it never runs.
3. Actor AABB (+0xB8..+0xCC) vs camera box and frustum (`func_00297B74`): view dependent only.
4. `func_002500A4` distance LOD: `dist^2 >= radius^2*2` selects far model index (def `+0xF`) else near (`+0xE`); index -1 drops the
   object. For every static definition used in the ROM `+0xE == +0xF == 0` (radius 512.0): nothing is dropped, nothing switches.
5. `func_0025021C`: `if (actor.flags (+0xD8) & 0x40) return;` = hidden. **Bit 0x40 is not set in any of the 10,169 placements.**
6. Parts are walked by `func_0026DA4C` (or queued by `func_0026DC24` into the sorted batch of `func_0026BC60`); each part's material
   goes through `func_00269A80`, whose return value says whether the part's display list is called.

## 2. Per-part rule (material flags) [V]
`func_00269A80(material, frameSel)` returns 0 (part skipped) **only** if `material.flags & 0x100` and `colour1 alpha (+0xF) == 0`; all other
paths return 1. No static part used in any level satisfies this (0 of 1,401 distinct (level, object) parts). So all parts and triangles are
drawn; there are no collision-only, lighting-only, LOD or damage-state parts among the placed static objects (extra parts are other
textures/materials of the same object).

Material flag meaning as far as the code shows:
| bit | effect | status |
|---|---|---|
| 0x4 together with 0x2 or 0x40 | no back-face culling, no lighting (two sided, unlit). Culling+lighting (G_CULL_BACK\|G_LIGHTING, 0x20400) is set when `(flags & 0x42) == 0 \|\| (flags & 4) == 0`. **0x4 is never set on a static material in the ROM** -> every static part is back-face culled | V code, V data |
| 0x10 | environment mapping (adds lighting + texgen, 0x60000) | V code |
| 0x20 | texture clamp (S and T) | V code, name H |
| 0x800 | texture mirror (S and T); both = clamp+mirror | V code, name H |
| 0x100 | alpha blended; skipped when alpha 0; `flags & 0x700` sends the part to the depth sorted translucent batch | V code; 7 parts with alpha 178 (flags 0x148) |
| 0x40 | changes how material +8 is stored (`\|1`); no visibility effect | V |
| 0x1, 0x2, 0x8, 0x400, 0x10000000/0x20000000 | present in data (0x48, 0x49, 0x4A, 0x548, 0x30000048); no visibility effect found | H (texture/combiner selection) |
Flag combinations over all used parts: 0x48 x1007, 0x848 x251, 0x49 x109, 0x30000048 x21, 0x148 x7, 0x548 x5, 0x4A x1.

## 3. Placement flags and word0 [V that they do not select drawing; H meaning]
`flags` (compact +0xC / full +0x2E, actor +0xD8): 0,1,2,3 (about a quarter each), a few 4,5,6, one 0x10. In the draw path: 0x40 hide (never
set); 0x4 = deferred/fade state (sector loop puts such actors in a second list at `scene+0x10C0`, drawn by `func_00286424`; `func_002500A4` also
sets it at run time); 0x8 collects the actor for the ground list; 0x200 is a per-frame "was culled" bit. Bits 0x1/0x2 are read by **no**
draw or collision code in the decompilation, and they do not correlate with object, definition or facing (each of flags 0..3 has +Y, +Z, -Z, +X, -X
facing quads in similar proportion over all levels) - probably editor/lighting groups. `word0` (actor +0xD4) is 0xFFFFFFFF everywhere (collision mask).
Neither is usable as a visibility filter.

## 4. Winding and the "all sides" look [V data]
* Exported world: right-handed, +Y up, world.obj vertex order. Front normal := `-normalize(cross(v1-v0, v2-v0))` of the first triangle of every placement.
  For axis-aligned horizontal quads over all 37 levels: **+Y 3,900 times, -Y 0 times** (flags 0..3 alike). Floors are clockwise seen from above; the data has
  no ceilings. A counter-clockwise-front renderer culls every floor: the observed "floors vanish, some walls vanish" symptom.
  Fix: treat clockwise as front (`Visibility.FrontFaceIsClockwise`) or swap two indices (`Visibility.FrontFaceOrder`).
* Walls are single quads. Back-to-back opposite-facing pairs in the same plane: 14 in Cathedral (0-54 per level), same-facing duplicates 0-31 per level:
  there is no "other side" copy the game could select.
* Statistical check of the wall side: for each wall triangle, is there a floor triangle 40 units in front (-n) / behind (+n)? Over all levels
  1,128 walls have floor only in front, 399 only behind (Cathedral 98 vs 10, Pit 64 vs 3, Fallout 34 vs 0); the rest have both/neither. So the front
  side (clockwise rule) is mostly the walkable side. [V statistically, not per wall]
* A wall is invisible from the neighbouring room but remains in the collision data (zero-thickness AABB, `level_geometry.md` section 5).
  **[H]** openings between tiles are made by leaving the wall out of the data; no "open side" bit exists.
* Negative result: straight nav edges (part 10) at node height +40 cross 55-70 % of wall quads regardless of flags, definition and side, so they are not
  straight corridors (or walls are not solid for the AI); they cannot validate solidity.

## 5. Not verified / open
* Models with several LOD/variant entries (objects 174/185/203) are not selected statically (unchanged).
* Material bits 0x1, 0x2, 0x8, 0x400, 0x30000000 and the animated-texture frame selector (`frameSel` = def `+0x12`, cases -2 / -1 / index, uses `D_800D2980`) are not decoded.
* None of the recorded `port_dl*.txt` files is a level frame (all ~21 KB menu/UI frames), so the real draw list could not be compared with the mesh.
* Dynamic actors use the same loop with other mask tests (`unk1C0`); not covered.

## 6. Integration (LevelGeoCommand.AddInstance, not edited here)
```csharp
var def = p.DefIndex >= 0 && p.DefIndex < ctx.Defs.Count ? ctx.Defs[p.DefIndex] : null;
var partDrawn = m.Parts.Select(pt => Visibility.PartDrawn(p, pt.Material, def)).ToArray();   // all true in the shipped data
foreach (var t in lm.Tris)
{
    if (!partDrawn[t.Part]) continue;
    int a = baseIdx + t.V0, b = baseIdx + t.V1, c = baseIdx + t.V2;
    if (flip) (b, c) = (c, b);                              // existing negative-scale handling stays
    if (Visibility.FrontFaceIsClockwise) (b, c) = (c, b);   // emit CCW-front triangles; floors then face +Y
    wm.Tris.Add((a, b, c, matOfPart[t.Part], p.Sector));
}
```
Then enable back-face culling for every material (`Visibility.BackFaceCulled(material)` is true for all static materials). Do not drop
placements by flags or neighbour logic. Note: the swap changes `world.obj` winding, so `TopDown.cs`/viewers that assume the old order need checking.
