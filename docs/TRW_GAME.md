# Trw.Game - the playable level walker

`src/Trw.Game` (namespace `Trw.Walk`) is the first playable part of the from-scratch C#/MonoGame rewrite. It loads the baked levels (`extracted/baked/NN_name`, see `docs/formats/baked_levels.md`)
and the converted creature/texture/audio assets, and lets you walk, swim, ride lifts, teleport, pick things up, shoot bots and be shot by them. Everything is in-house code on top of MonoGame (DesktopGL).

Run: `dotnet run --project src/Trw.Game -- [--level N] [--size WxH] [--nofog] [--nosound] [--nobots]` (finds `extracted/` by walking up from the executable).
Headless checks: `--autowalk --frames N --shot file.png` (scripted input), env `TRW_POS=x,y,z,yawDeg` (placement), `TRW_IDLE`, `TRW_LOG` (position log every 30 frames), `TRW_FIRE` (hold fire), `TRW_VM=obj,scale,x,y,z` (viewmodel tuning).

## Controls
WASD move, mouse look, Space jump (swim up in water), left mouse fire, F fog, M music on/off, N noclip, R respawn, PgUp/PgDn level, F12 screenshot, Esc quit.

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
| bots | `Creature.cs`, `BotAi.cs` | idle/walk clips, detection ranges from the spec; fire cadence, aim error, damage scale simplified |
| weapon | `TrwGame.Fire`, viewmodel in `TrwGame.Draw` | MAG 60 hitscan 10 hp verified; fire rate **assumed**; viewmodel object 157 = MAG 60 is an **unverified guess** |
| audio | `GameAudio.cs`, `tools/.../MusicRender.cs` | pipeline works; **not auditioned**; sound ids and level->song mapping provisional (`SoundIds.cs`, `SongForLevel`) |

## Not done
Other weapons and projectiles, power-up effects beyond regeneration/speed burst/invulnerability, flags/CTF, scoring and game modes, menus, bot weapon choice and item seeking,
actor lighting as the game does it, the pickup model, effect objects, real collision regions, decals/particles.
