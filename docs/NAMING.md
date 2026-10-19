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

## Static classes

Every `Subject_Verb` function is a member of a static class per subject: `Weapon_GetAmmoType` is declared as `GetAmmoType` in
`public static partial class Weapon` and called as `Weapon.GetAmmoType(...)` everywhere (generated and hand-written code). The tool decides this
on every run from the names in `functions.tsv`, so the list keeps the `Subject_Verb` spelling.

* What stays in class `Game`: functions without a subject prefix (SDK / library names, `_Recompiled` copies), the `Game_` subject (hand-written
  `Game` members could collide), functions defined outside the generated members (registration tables, `Preserved/`, the OS layer), and any
  function whose class name would equal a function that stays in `Game`.
* A function that lives in another function's member file (several functions of one C file) goes to that member's class with its full name
  (`View.Hud_PostMessage`).
* Subjects named like a Trw.Platform / MonoGame / System type or the `Trw.Game.Debug` namespace get another class name (`CLASS_OVERRIDE`:
  `Mem` -> `MemUtil`, `Math` -> `MathUtil`, `Debug` -> `DebugUtil`, `List` -> `ListUtil` ...).
* Struct view types named like a subject class get the suffix `View` (`Actor` -> `ActorView`, `Player` -> `PlayerView`); `objects.tsv` may keep
  the original names.
* Every generated file imports class `Game` (`using static Trw.Game.Game;`: the OS layer and helpers), and `Generated/_ClassUsings.cs` imports
  every subject class globally, so helpers that travel with a member (the recompiled `_rc` twins) stay reachable unqualified. A member is never
  named like a class (that would make `Class.X` ambiguous): it keeps its full name then.
* Debug paths are unchanged: `Dbg.Enter`, `TRW_TRACE_FN`, the call histogram and the differential tester use the original names; the write
  watch (`TRW_WATCH`) prints `Class.Member`.

## Conventions

* `Subject_VerbObject`: `Arena_Set`, `Flyby_IsInWindow`, `AttractDemo_Setup`. The prefix is the static class (`Arena_Set` -> `Arena.Set`).
* Library code keeps its SDK names (`osRecvMesg`, `alSynNew`, `sprintf`).
* Low-confidence names keep the address: `Actor_Think_0044E910`.
* A group file collects the functions of one subject in ROM order; one group should match one original source file where that can be recovered.

## Globals: `tools/names/globals.tsv`

Guest RAM addresses of the game's globals get names too: `address <TAB> name <TAB> confidence <TAB> note`. The tool replaces every exact literal of a
named address (`0x80146878u`, `0x80146878`) in `src/Trw.Game` with `Gv.<Name>` (`Mem.S32(Gv.GameState)`) and writes `Generated/_Globals.cs`
(`public static class Gv { public const uint GameState = 0x80146878u; ... }`, the note as the doc comment). A `const uint` folds to the same IL as the
literal. Renaming a global follows through (`tools/names/globals_applied.tsv`); removing one turns its uses back into the literal. The class is `Gv`,
not `G`: the decompiled C has locals named `G`.

Conventions: `Ptr` for a variable that holds a pointer (`FlybyCtlPtr`, `DisplayListPtr`), `Table` / `Array` for arrays, fields of a larger global
named as fields (`Settings` = 0x801462C8, `Settings_MusicVolume` = +0x18), SDK names for library globals (`__osPiDevMgr`); low-confidence names end
with the address (`Debug_Freeze_8013B29C`). Address + offset expressions (`Gv.Settings + 0x5D8`) stay as they are unless the field has a name.

## Struct fields: `tools/names/objects.tsv` + `tools/names/fields.tsv`

The decompilation describes one game object with many per-file C structs, so the generated struct views (`Generated/Structs`) have many
types for the same memory (19 views of the player: `Plr`, `Plr24d`, `B04Act`, `Actor` ...), each with C field names (`unk5E4`, `f5E4`). Names are
therefore given per *game object* and offset, not per type:

* `objects.tsv`: `object <TAB> alias view types <TAB> base object <TAB> note`. A base object's field names apply too (`Player` extends `Actor`:
  the player object starts with the 0x2E8-byte actor header).
* `fields.tsv`: `object <TAB> offset <TAB> name <TAB> confidence <TAB> note`.

`tools/rename/fields.py` (run by `apply_names.py`) renames, in every alias type, the member at each named offset (and its `_Set` method), and
every access in the code: the code reaches struct members only as `new Type(x).member`, chained through members of view type, `.At(i)` and array
view indexers, and the tool follows that chain through the struct definitions, so only accesses that really are that field are renamed. Padding
members (`padNN` byte arrays) are never renamed. Code then reads `new Actor(other).Health == 0` whichever alias type it came from.

`tools/rename/structs.py` lists every used view type with its layout and use count (`struct_types.tsv`); `tools/rename/field_evidence.py OUT.md
Type1,Type2` collects, per offset, the alias members there and the functions and lines that access them, for naming.

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
