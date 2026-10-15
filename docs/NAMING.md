# Readable names and grouped files

The generators (`Trw.Transpile`, `tools/mips2cs`) emit every function under its **original name** (`func_0042EB80`, the C port's name) in a file of its
own. `tools/rename/apply_names.py` turns that into readable code: it renames identifiers and gathers functions into one file per subject. It runs as the
last step of `tools/regen.ps1`, and by hand after editing the name list. It is idempotent: a run that has nothing to do writes nothing.

## The name list: `tools/names/functions.tsv`

One tab-separated row per registered function (`--template` adds rows for functions that are new in the registry and keeps every existing row):

| column | meaning |
|---|---|
| orig | original name: the key, never changes (also the name the debug paths use) |
| address | ROM-relative code address (`0x00200500` = KSEG0 `0x80200500`; overlay A starts at `0x00400000`) |
| name | readable name; empty = keep the original |
| group | file under `src/Trw.Game/Generated/`, e.g. `Render/Water.cs`; empty = the function keeps a file of its own |
| confidence | `high` (proven: traced, tested or obvious from the code) / `medium` (strong evidence) / `low` (a guess: keep the address in the name) |
| note | the evidence, one line: what the function does and how we know |

`python tools/rename/apply_names.py --check` validates the list (identifiers, duplicates, collisions with names that already exist in `src/`).

## Conventions

* `Subject_VerbObject`: `Arena_Set`, `Water_IsCameraUnderwater`, `AttractDemo_Setup`. The prefix is the class the function moves to once the code is split
  into real static classes (`Arena_Set` -> `Arena.Set`); until then everything stays in `public static partial class Game`.
* Library code keeps its SDK names (`osRecvMesg`, `alSynNew`, `sprintf`).
* Low-confidence names keep the address: `Actor_Think_0044E910`.
* A group file collects the functions of one subject in ROM order; one group should match one original source file where that can be recovered.

## Naming in bulk

* `python tools/rename/graph.py` writes `tools/names/graph.tsv` (per function: callers, callees, the guest data addresses its code uses, its height in
  the call graph) and `tools/names/clusters.tsv` (ROM-contiguous runs tied by calls or file-local data). Re-run it after renaming.
* Proposals come as `orig, name, group, confidence, evidence` rows. `python tools/rename/merge_rows.py rows.tsv ...` checks every row (the function
  exists and has no name yet, the name is a valid identifier, unique, not already an identifier in `src/`, low names carry the address, the group is a
  relative `.cs` path), reports the bad ones and merges the rest. `--replace` lets rows correct names that already exist (the old name is noted in the
  evidence column). Then `apply_names.py`, build, and the behaviour check below.
* Every function was named this way (2026-10-15) by agents reading the decompiled C (or the recompiled C#), the call graph, ROM tables (state tables,
  class vtables, menu message tables `{msg, screen, fn}`, weapon and pickup tables) and ROM strings. Things learned on the way:
  * ROM data: main-segment address - 0x7FFFF400 = file offset; overlay A VA 0x4xxxxx -> ROM 0x16E000 + (VA - 0x400000).
  * Split pieces (a few instructions that fall into the next function, or a body without a prologue) are named after their function with `Head`,
    `Tail` or `Entry_<addr>`; a recompiled copy a few bytes from its C version is `<Name>Twin`; a recompiled copy of an SDK function is
    `<SdkName>_Recompiled` (the port's own OS layer owns the plain SDK names).
  * Overlay A still contains Turok 2's front end (old main menu, cheat list with Turok 2 level warps, in-game option pages): `LegacyMenu_`,
    `LegacyCheats_`, `InGameMenu_`.
  * Comments from another decompilation sometimes describe things this game does not have (vehicles, wheels, drivers): trust the code.

## What the tool does

* Renames identifiers in every `.cs` under `src/` (generated and hand-written code, comments included). **String literals are not renamed**: `Dbg.Enter`,
  `Funcs.Add`, the call histogram and the differential tester (`Generated/sigs.txt`) keep identifying functions by their original names.
  `TRW_TRACE_FN` accepts either name and the trace prints both (`Dbg.ReadableName`, fed by the generated `Generated/_NameMap.cs`).
* Generation inputs keep the original names: `tools/overrides/*.c`, `tools/mips2cs/hooks.txt`, `tools/mips2cs/force_mips.txt`.
* Moves grouped functions into their group file. Every member of a group file is preceded by a marker, `// ==== func_0042EB80 @ 0x0042EB80 [C]`
  (`[MIPS]` for a function recompiled from the machine code), which lets the tool split and regroup its own output. Do not edit the markers.
* Records the names in effect in `tools/names/applied.tsv`, so hand-written code that already uses an older name follows a later rename.
* A C source file can sit next to a MIPS twin of the same name (its static helpers stay C while the function itself is recompiled); they are kept as two
  members.

## Checking that a rename changed nothing

Renaming and moving code must not change behaviour. A deterministic headless run records a hash of every frame, audio block and the game state
(`TRW_ORACLE_OUT`, `docs/ORACLE.md`); it must be byte-identical before and after:

```
TRW_PAK=1 TRW_PAK1=<fresh copy of a save .mpk> TRW_ORACLE_OUT=out.txt TRW_DETERMINISTIC=1 TRW_TICK_OPS=400 TRW_TICKS=6000 TRW_ARENA=10 Trw.Game.exe --headless
```

(The pak must be on and a fresh copy each run: an oracle run disables the pak by default and the boot then waits at state 0, and the game writes to the save.)

## Functions regeneration does not produce

`src/Trw.Game/Preserved/` holds functions the game reaches that the current generators no longer emit (six recompiled functions reached only through
data tables, and the stub `func_0041D694`). They live outside `Generated/` so a regeneration cannot drop them; if a regeneration produces one of them
again, the build fails with a duplicate definition: delete the copy in `Preserved/`.
