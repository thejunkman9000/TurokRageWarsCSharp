# Walkable level (`src/Trw.Game`)

First MonoGame game project: loads a baked level (`extracted/baked/NN_name`, see `docs/formats/baked_levels.md`) and lets you walk around it in first person.
Run: `dotnet run --project src/Trw.Game` (needs `trw bake`'s output). Options: `--level N`, `--size WxH`, `--nofog`, `--shot file.png --frames N`, `--autowalk` (scripted headless check, logs to `extracted/game_log.txt`).
Controls: WASD move, mouse look, Space jump, F fog, N noclip fly, R respawn, PgUp/PgDn change level, F12 screenshot, Esc quit.

## What it does
* **Rendering:** the game's vertex lighting (OBJ vertex colours), fog to the zone colour (also the clear colour), point filtering, sampler per material (wrap / mirror / clamp), alpha test on opaque parts,
  translucent water and lava after the opaque pass sorted by the game's key, animated water/lava (20 Hz, rate assumed).
* **Movement** (`Player.cs`): from `docs/GAMEPLAY_SPEC.md` section 7 [spec]: hit radius 56.32, height 184.32, eye 165.89; ground and air accel/max/decel (forward 10.24 / 30.72 / 13.44, strafe 10.24 / 35.84 / 13.44 units per
  1/15 s tick), reversing 1.5x, combined clamp; jump impulse 389.12 re-applied for 3 ticks, allowed up to 11.25 ticks after leaving the ground.
  **Assumed** (not in the data): gravity 1100 u/s^2, step height 48, snap-down 24, the wall collision shape (a vertical cylinder tested from step height to the head).
* **Collision** (`Collision.cs`): the level's own triangles. Upward-facing (normal.y >= 0.35) = ground (vertical probe at the centre and four points), downward-facing = ceiling, steep (|normal.y| < 0.7) = wall.
  Opaque parts and lava are solid; water is not. This is an approximation: the game's real scheme (per-piece boxes plus coarse ground regions) is not fully decoded.
* Spawn: the player start from `spawns.json`; if it is recorded off the floor (level 12 is below every floor) the player is placed on the nearest floor below, else the lowest floor above.

## Not done
No water physics (swimming), lava damage, lifts and teleporters, characters/bots, weapons, pickups, sound, menus. The view uses a fixed 60 degree vertical field of view (the game uses 47.5 or 75 per zone, near 16).
