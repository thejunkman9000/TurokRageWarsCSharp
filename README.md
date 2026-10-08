# Turok: Rage Wars - C# / MonoGame rewrite

A from-scratch, readable, moddable rewrite of the N64 game *Turok: Rage Wars* (US v1.1). The decompiled C port (`D:\Projects\TurokRageWars`) is used only as a behaviour and data reference.
All runtime code is in-house; MonoGame (DesktopGL) is the only runtime dependency. Game assets are **never committed**: they are converted offline from your own ROM into `extracted/`.

See `docs/PORT_PLAN.md` for the plan, `docs/TRW_GAME.md` for the playable game (controls, status, assumptions), `docs/FORMATS.md` and `docs/formats/` for the data formats, `docs/GAMEPLAY_SPEC.md` for numbers.

## Layout
| path | what |
|---|---|
| `tools/Trw.Tools` | offline converters (`trw <command>`): ROM chunks, textures, models, levels, creatures and animation, dynamics, baking, audio, music rendering |
| `src/Trw.Viewer` | MonoGame viewer for levels, creatures and actors (debugging and format work) |
| `src/Trw.Game` | the playable level walker: movement, lifts, teleporters, pickups, bots, weapons, audio |
| `docs/` | plan, formats, spec, status |

## Building the assets (once, needs your ROM at the path in `Program.cs` or passed as an argument)
```
dotnet build tools/Trw.Tools
trw models extracted        # textures + models
trw levels                  # level records
trw levelgeo                # level meshes, collision, lighting data
trw defs                    # gameplay definition tables
trw anim                    # skeletons and clips
trw creatures               # skinned creature packages
trw texanim                 # texture animation and translucency
trw dynamics                # lifts, teleporters, ...
trw spec                    # weapon/pickup/rule numbers
trw bake                    # baked levels (final world frame, lighting)
trw audio                   # sound effects, sequences
trw music                   # render songs to WAV
```

## Running
```
dotnet run --project src/Trw.Game -- --level 7
dotnet run --project src/Trw.Viewer
```
Controls and options: `docs/TRW_GAME.md`.
