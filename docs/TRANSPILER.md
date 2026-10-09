# Translating the game to C#

The C# game is the original game code translated, not a look-alike. `tools/Trw.Transpile` reads the decompiled C
(`D:\Projects\TurokRageWars\src\{game,ovl_a,libultra}`), asks clang for the AST (MIPS o32 target, so struct layouts,
4-byte pointers and sign rules are the real ones) and writes one C# file per C file into `src/Trw.Game/Generated`.
The generated files are the source from now on; edit them freely.

## Memory model (`src/Trw.Platform`)
* Guest addresses are `uint`; memory is big-endian bytes in a few mapped regions (`Mem.Map`), so ROM data loads unchanged.
  The process is an ordinary 64-bit .NET process.
* `p->field` becomes `Mem.S32(p + offset)`; globals `D_8012ABCD` become their fixed address; pointer arithmetic scales by element size.
* Structs / arrays / address-taken locals live in a per-thread guest stack frame (`Stack.Alloc` in the function prologue).
  Struct parameters travel as the address of the caller's copy (the callee copies it); struct results use a hidden first `uint _ret` parameter.
* Function pointers are guest code addresses; `Funcs` maps them to methods (`Game.RegisterFunctions()` is generated).
* String literals are copied into a guest pool on first use (`Str.Lit`).

## Running it
```
dotnet build tools/Trw.Transpile
# pass 1 builds the signature table (call sites are coerced to the definition's real parameter types), pass 2 emits
dotnet run --project tools/Trw.Transpile --no-build -- --root D:\Projects\TurokRageWars --syms D:\Projects\TurokRageWars\symbol_addrs.txt --sigs-out sigs.txt --report @all.txt
dotnet run --project tools/Trw.Transpile --no-build -- --root D:\Projects\TurokRageWars --syms D:\Projects\TurokRageWars\symbol_addrs.txt --sigs-in sigs.txt --out-dir src/Trw.Game/Generated @all.txt
```
`all.txt` lists the `src/...c` files. A full run takes about 2.5 minutes (clang is the slow part).

## Status (2026-10-08)
* 3548 of 3573 functions with matched C translate (99.3%).
* Not translated (25): switch cases nested inside blocks, a few GNU extensions (asm, statement expressions), libgcc 64-bit helpers.
* `dotnet build src/Trw.Game` still fails; what is left:
  * **~450 undefined names** (2016 references): functions that have no matched C. About 1150 of them are statically recompiled from the ROM's MIPS
    code in the C port (`port/native/recomp`, `port/gen_recomp.py`) and the rest are libultra assembly (`osSetIntMask`, `__osDisableInt`, ...).
    Next step: give `gen_recomp.py` a C# backend (same o32 register ABI, plus struct-by-address adapters at the boundary) or hand-translate the OS ones in the platform layer.
  * **Gotos into a sibling block** (63 references in 35 files): the matched C jumps into the middle of an `else`; C# cannot. Needs a flat (label-only) emission mode for those functions.
  * **~30 void/value mismatches**: matched C declares `void` but the original leaves a value in `v0` (same class as `func_0028FDD8` in the C port); fix by typing those functions.
* Libultra's OS layer (threads, message queues, timers, DMA, VI/AI/SI) is not translated; it is replaced by the platform layer, as `port/os_shim.c` does.

## Verification plan
The C port is the oracle: differential tests per function (like `port/gen_difftest.py`) and per-frame state traces from scripted input.
