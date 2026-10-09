# Translating the game to C#

The C# game is the original game code translated, not a look-alike. Two generators feed `src/Trw.Game/Generated`:

* `tools/Trw.Transpile` reads the decompiled C (`D:\Projects\TurokRageWars\src\{game,ovl_a,libultra}`), asks clang for the AST
  (MIPS o32 target, so struct layouts, 4-byte pointers and sign rules are the real ones) and writes one C# file per C file.
* `tools/mips2cs` recompiles the functions that only exist as machine code (and the C functions the transpiler cannot express,
  or whose C draft loses information) from the ROM listing into `Generated/Recompiled/`.

The generated files are the source from now on; edit them freely (a regeneration overwrites hand edits, so make fixes in the
generators or in `tools/overrides/<function>.c` while the translation is still being corrected).

## Status (2026-10-09)
Boots the real game code: logos, title, menus, 1-player trial and multiplayer match with the **3D world, weapon, HUD and audio**
(the 1-player "Proving Grounds" frame is identical to the C port's frame). 21 700 sampled calls of 4 000+ functions run through the
differential tester without a mismatch. Open: stalls of the graphics thread (rare), gameplay beyond walking/firing is untested.

## Memory model (`src/Trw.Platform`)
* Guest addresses are `uint`; memory is big-endian bytes in a few mapped regions (`Mem.Map`), so ROM data loads unchanged.
  The process is an ordinary 64-bit .NET process. The game's own page-in (TLB region list at 0x80103F88) is serviced by `Mem.MapPaged`.
* `p->field` becomes `Mem.S32(p + offset)`; globals `D_8012ABCD` become their fixed address; pointer arithmetic scales by element size.
* Structs / arrays / address-taken locals live in a per-thread guest stack frame (`Stack.Alloc` in the function prologue).
  Struct parameters travel as the address of the caller's copy (the callee copies it); struct results use a hidden first `uint _ret` parameter.
* Function pointers are guest code addresses; `Funcs` maps them to methods (`Game.RegisterFunctions()` is generated).
  `Funcs.CallReg(addr, a0..a3)` is the register-ABI entry used for indirect calls and by recompiled MIPS code.
* The register file for recompiled code is `Cpu` (FPU registers in FR=1 layout, `V1`, the stack argument words `Stk`).
* String literals are copied into a guest pool on first use (`Str.Lit`).

## The register ABI problem and how the generator copes
A matching C decompilation compiles to the same bytes, but it does not say what the machine code *means* by the registers it leaves around.
The generator has to restore that information:
| class | what the C says | what the ROM does | fix |
|---|---|---|---|
| hidden parameters | `f(void)` / fewer parameters | reads a0..a3 it was never "passed" | `tools/arity.py` counts the argument words each function reads; `Emitter.Extend` adds trailing `_aN` parameters and forwards the caller's registers |
| indirect-call wrappers | `void f(void){ fp(); }` | forwards a0..a3 and v0 | recompiled from the MIPS |
| void results | `void f(){ g(); }` | returns g's value in $v0/$f0 | `tools/retuse.py` scans the assembly of every function (including tail-call chains) for callers that read $v0/$f0 after `jal f`; those functions are recompiled from the MIPS (`regen.ps1`) |
| int vs float in one register | `f(int)` | the callee reads the register as a float | `CoerceArg` reinterprets the bits (`Cpu.U2F/F2U`) |
| struct by value vs scalars | `f(Vec3 v)` | callers pass three words | `MarshalByWords` rebuilds the struct in a `C.Tmp` slot |
| gotos into sibling blocks, asm, statement expressions | | | flat label emission, otherwise recompiled from the MIPS |

## Where C# and the original machine code differ (found the hard way)
* `(u32)float`: the original goes through `trunc.w.s`, so negative values keep their two's complement bits. C# 9's `(uint)` saturates to 0.
  `Cpu.TruncU32` implements the original; narrow integer casts of floats wrap via `int`. Before the fix every negative matrix element
  written by `func_002702EC` became 0 and the whole world was black.
* Division: C# throws for `/0` and `int.MinValue / -1`; `Cpu.SDiv/SRem` follow the MIPS results.
* Shifts mask the count like the hardware; `int` shifts of negative values are arithmetic.

## Regenerating
`tools/regen.ps1` (about a minute with the clang AST cache in `%TEMP%\trw_ast`): arity scan -> transpiler pass 1 (signatures) ->
mips2cs (MIPS-only functions) -> pass 2 (C# for the C functions; functions pass 2 cannot express join the MIPS list and the two steps repeat) ->
`make_stubs.ps1`. Needs the C port repository, Python and the .NET SDK. The transpiler has an AST dump gap: records declared inside a cast
expression are missing (`tools/overrides/func_0040DB84.c` uses a named struct).

## Debugging aids (environment variables, all off by default)
| variable | effect |
|---|---|
| `TRW_STATUS=1` | `[state]` (game state D_80146878), `[screen]` (UI screen id), `[pad]` (pressed buttons), `[status]`, `[audio]` lines on stderr |
| `TRW_PAD_SCRIPT` / `tools/run_window.ps1 -Pad` | scripted input: `<tick>:<BUTTON>[+..]:<hold>` or `S[@tick]:<BUTTON>:<hold>` (press once the screen settled) |
| `TRW_SHOT_DIR/EVERY/MAX`, `TRW_EXIT_AFTER` | periodic screenshots, automatic exit (`tools/run_window.ps1`) |
| `TRW_GFX_LOG`, `TRW_GFX_DUMP_RUN=<n>`, `TRW_GFX_DL=1` | per display list statistics (ops, triangles culled/drawn), full command dump of list `n` |
| `TRW_TRACE_FN=a,b,prefix*`, `TRW_TRACE_LIMIT`, `TRW_TRACE_ARM_STATE=<n>`, `TRW_TRACE_DUMP=<bytes>` | log calls (and results of functions that call `Dbg.Leave`, see `tools/dbg_addleave.py`) from the moment the game reaches state `n` |
| `TRW_WATCH="addr:len;addr:len"` (+`TRW_WATCH_MAX`) | print the writer call chain (and the old bytes) of every write into the ranges |
| `TRW_PEEK="addr:len[@off:len];..."` | dump guest memory once a match runs |
| `TRW_DIFF=name,..\|*`, `TRW_DIFF_N`, `TRW_DIFF_EVERY`, `TRW_DIFF_RET`, `TRW_DIFF_VERBOSE` | differential test: run the function on the MIPS interpreter (`Debug/MipsInterp`) and as translated C# from the same memory and compare every byte written |
| `TRW_HEAPCHECK=1`, `TRW_BATCHLOG=1` | resource-cache overlap check, model-batch flush log |
| `TRW_WATCHDOG=<file>` | thread dump when graphics tasks stop for 8 s |

## Finding a translation defect: the method that worked
1. Reproduce the scenario in both ports. The C port is only run from a private copy (never from the repository directory: other sessions use it).
2. Find the first observable divergence: a display list that is empty (`TRW_GFX_DL`), a global that differs (`TRW_PEEK` here, `ReadProcessMemory` on the
   C port: guest address == host address there), a function that never runs (`TRW_TRACE_FN`).
3. Walk back with `TRW_WATCH` / `Dbg.Leave` tracing to the first function whose result or output differs, then compare it with the ROM
   (`asm/*.s` of the C port, `Debug/MipsInterp`) instruction by instruction.
4. Fix the generator rule (not the generated file), regenerate, rerun the scenario, run `TRW_DIFF=*` over a play session.
