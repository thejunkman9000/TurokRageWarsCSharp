# Turok: Rage Wars - C# / MonoGame rewrite plan

## Goal
A modern, readable, moddable version of the game, written from scratch in C# on MonoGame. It is **not** a translation of the
decompiled C. The decompiled C port (`D:\Projects\TurokRageWars\port`) is a *reference implementation* that tells us what the
original game does; the C# engine reimplements that behaviour with its own architecture.

Principles
- **In-house code.** MonoGame is the only runtime dependency. Everything else (asset loaders, math, collision, animation, UI) is ours.
- **No original assets in the repo.** Converters read the user's own ROM (`baserom.us.v11.z64`) and write modern formats into a
  git-ignored `extracted/` or `content/` folder.
- **Fidelity of behaviour, not of rendering.** Movement feel, hit detection, AI timing, damage and level logic should match the
  original. Rendering is modernised (4K, high refresh, optional N64-style look).
- **Ported libultra: none.** No scheduler, message queues or RDP emulation exist in the new code.

## Pipeline

```
ROM ──► tools (offline, run once) ──► content/ (PNG, WAV/OGG, in-house mesh/level formats)
                                            │
C reference port ──► oracle traces ─────────┼──► compare ──► C# engine + game (MonoGame)
```

## Phase A - finish the C port as a reference (in the C repo)
Needs to be correct and deterministic, not pretty. Only change to the "unmodified" port is an **oracle mode** behind a flag:
- input record / replay (controller state per frame)
- fixed, reproducible timing and RNG seed
- per-frame state dump (player position/velocity/health, actor list with type/position/state, RNG state, level id)
- headless run (no window) so traces can be produced in batch
- the same C port renders **music** headless to PCM for the converter (see Phase B)

## Phase B - offline converters (`tools/Trw.Tools`)
| Source | Output | Status |
|---|---|---|
| RNC1 chunks (2050 in the ROM) | raw decompressed files | **done** - all 2050 unpack, CRC-verified |
| chunk containers, object table | split sections | **done** (see docs/formats/models_textures.md) |
| textures (CI4/CI8 + RGBA5551 palettes, tiled UI textures) | PNG (in-house writer) | **done** - 507 3D + 55 UI, checked visually |
| models (static + skinned meshes) | OBJ now, in-house binary later | **done**: 386 meshes, skeletons, 408 animation clips; animation-id roles (idle/walk/attack) unknown |
| levels: index, sector grid, nav graph, level meta | text/JSON now, in-house binary later | **done for geometry**: 37 levels export as textured OBJ + collision + nav + objects JSON; gameplay fields in a few parts still unknown |
| sound effects (VADPCM) | WAV | **done** - 294 decoded, sanity-checked, not yet listened to |
| music (sequences + instrument banks) | MIDI + instrument WAVs with loops; play with a small in-house sampler in C# (no need to render through the C port) | **extraction done** (9 songs, 21 instruments); sampler not written |

Prefer WAV (or OGG via an optional tool step) over MP3: no encoder dependency.

## Phase C - engine (`src/Trw.Engine`) and game (`src/Trw.Game`)
Grown from a **vertical slice**, not designed in the abstract.
1. Platform: window, fixed-timestep simulation (original rate) with interpolated rendering so 4K / 144 Hz works, input mapping, save data.
2. Renderer: our own mesh/material pipeline on MonoGame; optional N64-style look (point-sampled textures, vertex lighting, fog) as a shader mode.
3. Core systems: scene and actors, collision and movement, animation, particles/effects, camera, UI/text, audio playback.
4. **Vertical slice**: one level, the player, one weapon, one enemy, HUD. Compare with oracle traces.
5. Port the rest of the game one subsystem at a time (enemies, weapons, items, bosses, game modes, menus), each checked against the oracle.
6. Mod support: data-driven definitions, hot reload, debug overlay.

## Phase 0 - inventory (can run in parallel with B)
Classify the ~3,900 functions in the C repo (`docs/game_funcs.txt`) into platform (libultra), engine and game logic, and write the
list of systems the engine must provide. Output: `docs/ENGINE_SPEC.md`.

## Where the data work stands (2026-10-07)
Textures, models, skeletons, animation, levels (geometry, collision, nav, spawns), sound effects, music and name tables are all decoded and
exported by `trw`. Missing for the game logic: weapon damage/ammo/fire-rate and enemy stats/behaviour (code constants in overlay A and the
per-class tables at ROM 0xD1280), animation-id roles, the lighting colour bytes, and the remaining unknown level parts. These need reading
the decompiled game code, not more format work, and are best done together with the oracle traces.

## Viewer (first MonoGame code, `src/Trw.Viewer`)
Loads the exported level OBJ/MTL/PNG files (own parser, no content pipeline) and flies a camera around: `dotnet run --project src/Trw.Viewer`
(WASD, Q/E, right-mouse look, PgUp/PgDn level, F filtering, C vertex colours; `--level N --shot file.png` for headless screenshots).
Orientation tools: the viewer assembles levels from `levelgeo/_objects/obj_<id>_<model>.obj` (raw model-space meshes) plus `objects.json`, so
rotation can be changed live (number keys 1-9/0, I to compare with the baked `world.obj`, F9 saves the settings to `extracted/rotation_settings.txt`).
Once the right orientation is settled it gets baked into the exporter (`LevelGeoCommand.ModelToWorld`, env `TRW_MODEL_YAW` for experiments).
Known issues: the OBJ vertex "colours" look like N64 vertex normals, not colours, so they are off by default; lighting and material
colours are not applied yet; static objects use the world mesh only (no creatures, no animation, no collision yet).

## Water, texture animation and creatures in the viewer (2026-10-07)
* Translucency and texture animation follow `docs/formats/transparency_and_animation.md`: material flag 0x100 = alpha blend after the opaque pass (sorted by field8*2|flags&0x40),
  opaque parts alpha-tested at 0.5, format-3 textures are CI4 + IA16 palettes (water now has real alpha), only textures 83 (lava), 91 and 161 (water) animate by frame cycling
  (frame = ((tick / p1) & 255) % frames, tick rate assumed 30 Hz), no UV scrolling. Viewer: `DrawScene` in two passes, `Batch.Frames` for animated looks.
* Creatures: `trw creatures` writes skinned packages with every clip as per-frame bone matrices (`extracted/creatures`, schema in `docs/formats/creatures_export.md`).
  Viewer creature mode: F3, PgUp/PgDn creature, `,` `.` clip, Space pause, Home restart; clips run at 15 ticks/s and loop to the loop frame. Mesh variants (`obj_174_mK`) borrow the base clips.
  Known rough spots: creature texture looks/palettes (the game recolours actor palettes by team; the viewer always uses look 0), some objects face sideways (object 133),
  objects 124-127 are gib strips, not creatures, 174 chunk 0 has an attached pole-and-disk shape.
* Exporter bake (world.obj, nav, spawns, collision with the mirror/orientation fixes) is still pending.

## Actors layer and water flicker (2026-10-07)
* Viewer actors layer (`ActorLayer.cs`): per level, the player start is Turok (character 0 = body chunk 1) holding a weapon (hand-held chunks 16-36 share the body's skeleton and clip; F7 cycles),
  deathmatch spawn points get bots (rotating through the body chunks and skins), movers (and optionally pickups) are props. Positions are raw level coordinates with X mirrored like the static
  pieces, scale from the record (0.5), animated with the idle clip. Facing (yaw) convention found by eye: stored yaw with the sign flipped (mirrored world) plus 180 degrees (F5 turns all actors 90 degrees, F6 flips the sign; remembered in extracted/actor_facing.txt).
  Actors are dropped onto the floor found by a ray cast under the spawn point (the bot spawn points are recorded about 50 units above the floor); this is a viewer heuristic, to be checked against the C port once it reaches that part.
* Water flicker: the water textures (91, 161) change about 50% more per frame step than lava (mean 6 to 9 per pixel against about 4) and were played at an assumed 30 Hz. The real tick (the
  rendered-frame counter D_800D2980) rate is not known; the viewer now defaults to 20 Hz and `-` / `=` pick 10, 15, 20, 30 or 60 Hz.
* Elevators and other moving map objects are being decoded (level part 7 etc.): see docs/formats/dynamic_objects.md when it exists.

## Baked levels (2026-10-07)
`trw bake` writes `extracted/baked/NN_name/` in the final world frame with all orientation, scale, mirror, texture-look and sampler findings applied (format: docs/formats/baked_levels.md).
Validated by loading the baked world.obj in the viewer next to the live assembly: identical geometry (0.1-0.8 percent of pixels differ, only at texture edges from the OBJ's plain wrap sampler).

## Moving objects (2026-10-07)
The data holds 6 lifts, 8 teleporter zones, 23 pad beacons and 4 flag spawners (docs/formats/dynamic_objects.md); there are no doors, crushers, drawbridges, jump pads or swinging objects.
Lifts oscillate between two stations (cosine easing, hold times; the game starts the trip while a rider stands on it). The viewer runs them continuously (`ActorLayer.LiftMotion`);
F4 also shows the beacons and teleporter zones. `trw dynamics` exports them; `trw bake` mirrors them into `extracted/baked/NN/dynamics.json`.

## Lighting (2026-10-07) - docs/formats/lighting.md
Static pieces are lit on the CPU by the game (func_00278530): per placement, per vertex, a sum over the sector's light volumes of colour * softEdgeWeight * (ambient + (1 - ambient) * max(0, N.L)); the result is the shade colour
(material flag 0x40 = RSP does no lighting). Exported as `levelgeo/NN/vertexlight.bin` and applied by the viewer (key C) and as OBJ vertex colours in `baked/NN/world.obj`.
Fog (key F11): colour = zone colour & 0xF8 (also the clear colour), starts where the depth value passes 995/1000, full at the zone's far plane (1536..5120); viewer default off because overview shots sit beyond the far plane.
Not done: actor lighting (a per-frame ambient 56 plus one directional light from the volumes at the actor's position; actors are drawn unlit/white for now), dynamic point lights (muzzle flash, explosions),
the underwater/lava alternative zone colours, the game's own camera (fov 75 or 47.5 degrees, near 16, far per zone).

## First game project (2026-10-07)
`src/Trw.Game` loads the baked levels and lets you walk through them with the game's movement constants (docs/WALKABLE.md). Verified headless: speed plateaus at the spec's 30.72 units per tick (461 u/s),
jumps work, walls and ramps stop and carry the player. Assumed constants (gravity, step height, wall shape) are listed in WALKABLE.md.

## Risks
- **Behavioural drift.** Mitigation: oracle traces, compare early and often.
- **Audio.** Sequence/instrument formats are the least understood; rendering music through the C port avoids a synth for now. Dynamic music is lost.
- **Format reverse engineering.** Model/level/texture formats must be derived from the C loaders; budget most of Phase B for this.
- **Scope.** The vertical slice is the safeguard against building an engine nobody needs.

## Repository layout
```
docs/                this plan, format notes, engine spec
tools/Trw.Tools/     offline converters and analysis tools (net9.0 console)
src/                 engine and game (to come)
extracted/           git-ignored output of the tools
```
