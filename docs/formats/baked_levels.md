# Baked levels (`extracted/baked/NN_name/`, command `trw bake`)

The raw exports in `extracted/levelgeo` keep the game's own coordinates and the first-pass orientation (they feed the viewer, which applies the corrections live).
`trw bake` writes a clean copy in the **final world frame** with every correction found in the viewer applied, so other programs (a MonoGame game, other viewers,
tools) can use it directly.

## Frame and conventions
* Y up. **The raw game X axis is mirrored** (`x -> -x`) in every coordinate of every baked file: positions, matrices, bounds, collision, nav, lights, spawns.
* Units are raw game units (a static wall is about 307 high; a character is about 260 at its record scale of 0.5).
* Triangles are counter-clockwise when facing the front; faces are one-sided (material flag `0x4` marks the double-sided ones, which already hold both windings).
* Texture V: `vt v = 1 - t` (standard OBJ), so a normal OBJ viewer shows textures upright. Textures are `../../models/textures/tex_NNN_f<look>.png`.

## Per-piece transform (row-vector convention, `M = ...`, positions mirrored)
| record type | matrix |
|---|---|
| yaw-only ("compact1C") | `ZUp * Scale * Ry(180 deg) * Rq(conjugate q) * Translate(-x, y, z)` |
| quaternion ("matrix34") | `ZUp * Scale * Rx(270) Ry(270) Rz(90) * Rq(-qz, -qy, -qx, qw) * Ry(270) * Translate(-x, y, z)` |
`ZUp` maps model `(x, y, z)` to `(x, z, -y)` (models are authored Z up). All rotations are proper, so no winding flips are needed except for negative scales.
The scale is applied right after `ZUp`, before the extra rotations (verified against the game's collision boxes).

## Files
| file | content |
|---|---|
| `world.obj`, `world.mtl` | the assembled level, with the game's vertex lighting as OBJ vertex colours (`v x y z r g b`, multiply with the texture; see lighting.md); one material per (texture, look, flags) named `tex_NNN_look<L>_f<FLAGSHEX>`; comments in the mtl give the sampler and animation info; `d` = alpha for translucent materials, `Kd` = tint for flag `0x400` |
| `materials.json` | per material: texture, look, flags, sampler (wrap / mirror / clamp), translucent, alpha, sortKey, animated, frames, stepTicks, doubleSided, environmentMap |
| `objects.json` | per placement: ids, `textureKey`, chosen `look`, flags, final `position`, 16 floats `matrix` (row-major, row-vector), final `boundsMin/boundsMax` |
| `collision.json`, `collision.obj` | level ground triangles, rects, zones, per-piece boxes, mirrored; triangle winding and `neighbours` order permuted to match |
| `nav.json` | nav nodes and actors with x mirrored; edges and reachability unchanged |
| `lights.json` | light volumes, position x mirrored, `anglesRad` (ay, az negated) [H] |
| `spawns.json` | player start, pickups, movers, bot spawn points; `facingYaw = -rawYaw + pi` about +Y (creature models face +Z at 0 after ZUp) |
| `dynamics.json` | lifts (two stations, cosine easing, hold times; the game starts the trip only with a rider), teleporter zones, pad beacons, flag spawners: positions, motion endpoints and offsets with x mirrored, `facingYaw` |
| `meta.json` | bounds, triangle count, transform recipe |

## Texture looks, samplers, alpha
* Look = `textureKey % frameCount` (`textureKey = ObjectDef.Part`, def +0x12). Key -1 = animated: `world.mtl` uses look 0 and says `ANIMATED frames=N stepTicks=T`; frame = `((tick / stepTicks) & 255) % N`.
* Sampler: clamp if flags `0x20`, else mirror if `0x800`, else wrap. Translucent (flags `0x100`): alpha blend after the opaque pass, sorted by `sortKey`, depth write off.
* Not baked: top-down PNGs, lighting, creature models (see creatures_export.md).

## Rebuilding
```
trw extract; trw models extracted/models --all; trw levels; trw levelgeo; trw defs; trw texanim; trw creatures; trw bake
```
(`trw bake` needs `extracted/levelgeo`, `extracted/models/textures`, `levelgeo/texture_anim.json` and, for spawns, `extracted/creatures/placements.json`.)
