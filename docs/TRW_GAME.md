# Trw.Game - the playable level walker

`src/Trw.Game` (namespace `Trw.Walk`) is the first playable part of the from-scratch C#/MonoGame rewrite. It loads the baked levels (`extracted/baked/NN_name`, see `docs/formats/baked_levels.md`)
and the converted creature/texture/audio assets, and lets you walk, swim, ride lifts, teleport, pick things up, shoot bots and be shot by them. Everything is in-house code on top of MonoGame (DesktopGL).

Run: `dotnet run --project src/Trw.Game -- [--level N] [--size WxH] [--nofog] [--nosound] [--nobots]` (finds `extracted/` by walking up from the executable).
Headless checks: `--autowalk --frames N --shot file.png` (scripted input), env `TRW_POS=x,y,z,yawDeg` (placement), `TRW_IDLE`, `TRW_LOG` (position log every 30 frames), `TRW_FIRE` (hold fire), `TRW_VM=obj,scale,x,y,z` (viewmodel tuning), `TRW_WEAPON=n`, `TRW_RANDOM` (random walk to find collision holes) with `TRW_FAST` (unlimited frame rate, fixed 1/60 s steps; 6000 frames of a level run in a few seconds). The random walk over all 37 levels (6000 frames each) fell out of the level on 3, 7, 9, 14, 15, 18, 22, 23, 30 and 36 - these are ledges and pits, not confirmed collision holes.

## Controls
WASD move, mouse look, Space jump (swim up in water), left mouse fire, 1-4 weapons (MAG 60, shotgun, assault rifle, minigun - all hitscan approximations), F fog, M music on/off, N noclip, R respawn, PgUp/PgDn level, F12 screenshot, Esc quit. Match: first to 10 frags or 5 minutes (assumed limits; env `TRW_FRAGS`, `TRW_MINUTES`); the end screen is green for a win, red for a loss with your frags - bot best; Enter restarts.

## What is implemented (and how sure we are)
| area | file | status |
|---|---|---|
| level meshes, vertex lighting, fog, animated water/lava textures | `GameLevel.cs`, `TrwGame.cs` | matches the viewer (verified by eye) |
| movement: accel/max/decel, jump, wade/swim configs | `Player.cs` | constants verified from the code (spec section 7); gravity 1100, step 48, snap 24, buoyancy are **assumed** |
| collision | `Collision.cs` | the level triangles themselves (the game uses a coarse box/region scheme, not decoded) |
| lifts (rider / always, carry, solid) | `Lift.cs` | motion verified in `dynamic_objects.md`; box collision approximated from the hull AABB |
| teleporters | `Teleporter.cs` | touch radius, destination, yaw from the data; no effect object |
| pickups | `Pickup.cs` | grants and respawn times from the spec; **placeholder gem models**, touch radius assumed |
| lava / health | `Player.cs`, `GameLevel.cs` | 70 hp per hit verified; hit cadence (1/s) and "lava = large lava-textured region" **assumed** |
| bots | `Creature.cs`, `BotAi.cs` | idle/walk/attack/death clips, detection ranges from the spec; fire cadence, aim error, damage scale simplified |
| match | `TrwGame.cs` | frag limit 10 / 5 minutes (assumed), scores, timer, end screen, Enter restarts, respawn at the farthest spawn after 5 s (spec) |
| power-ups | `Pickup.cs`, `Player.cs`, `BotAi.cs` | durations and effect rules from the spec section 4; only the effects that matter against bullets are implemented |
| pickup model | - | object 274 is an untextured 102x102 quad (the item visual is chosen elsewhere, not decoded): gems are placeholders |
| weapon | `TrwGame.Fire`, viewmodel in `TrwGame.Draw` | MAG 60 hitscan 10 hp verified; fire rate **assumed**; viewmodel object 157 = MAG 60 is an **unverified guess** |
| audio | `GameAudio.cs`, `tools/.../MusicRender.cs` | pipeline works; **not auditioned**; ids from `docs/formats/sound_events.md` (pain/death/teleport/pickups/impacts/music verified; muzzle sound is a guess because the game has no fire cue we found; water cues are hypotheses; jump/land/footsteps are silent in the game) |

## Not done
Projectile weapons (only four hitscan weapons exist), Blast Shield and Energy Absorption (no explosive/energy damage yet), flags/CTF and other game modes, menus, bot weapon choice and item seeking,
actor lighting as the game does it, the pickup model, effect objects, real collision regions, decals/particles.
