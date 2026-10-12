# Modernization run, 2026-10-11

What changed, how it was checked, and what still needs a Windows machine.

## 1. Generated code now comes from the matched C
`tools/regen.ps1` was made to work against the current decompilation (it also runs on Linux/PowerShell 7).
Before: 3107 functions from C, 1379 translated from MIPS. After: 3584 `func_*.cs` from C, 956 from MIPS
(163 untranslatable C, 17 void-in-C-but-used, the rest hand-written assembly of the original and helpers without C).
The previous output is kept in `_old_generated_oct9/` (not part of any project) for rollback.

Real translation bugs found and fixed in the generators (the old output had them):
* array of anonymous struct elements had stride 4 instead of 8 (text width in `func_0040F1E0`, so every text quad);
* statements silently dropped from the 'their' (other release) sources: missing include paths/defines;
* names from other releases (`*_de`) resolved to wrong symbols; static inline helpers turned into stubs returning 0;
* C block-scoped locals shadowing outer ones (C# error CS0136); short/ushort arguments;
* `retuse.py` read an ROM listing that no longer exists, so functions that return a value the C drops were not found.
Five stubs remain (kernel/boot code: `__osEnqueueAndYield`, `__osEnqueueThread`, `__osProbeTLB`, `func_002023DC`, `func_0041D694`);
none is hit in a 30000-tick play-through.

## 2. Typed struct views (`src/Trw.Structs`, `docs/STRUCTS.md`)
`p->pos.x` is now `new Actor(p).pos.x` instead of `Mem.F32(Mem.U32(p + 0x1C) + 0x20)`. 2.6k struct views; Mem.* calls went from 87,879 to 56,909.
Views are `readonly struct` wrappers over guest addresses (the memory model is unchanged: display lists, ROM blobs and saves are still bytes).
Optional: `regen.ps1 -StructViews`. Without it the output is the old style.

## 3. Regression oracle (`docs/ORACLE.md`, `tools/oracle`)
Deterministic headless runs (`TRW_DETERMINISTIC=1`) record per-frame graphics hashes, per-audio-block PCM hashes and per-tick state digests.
`tools/oracle/run.sh <dir> boot|trial` and `tools/oracle/compare.py <a> <b>`. Scenarios: boot (1200 ticks), trial (30000 ticks of the scripted 1-player route).
Result: struct-views build vs. no-views build: identical in 14321 frames, 15679 audio blocks and 30000 ticks (and the full 8 MB RAM hash).
The C-derived build is NOT bit-identical to the Oct 9 build; the differences are the bug fixes above, confirmed against the ROM's machine code
with the differential tester (`TRW_DIFF`, which was also fixed: it compared no game RAM before).

## 4. Renderer / audio / input fidelity (against the C port, which was validated on the real game)
Fixed: fog (new), G_TEXTURE scale applied at vertex load, tile shift fields, partial z-buffer clears (4-player), 1:1 texrect point sampling,
display-list branch depth, audio AI FIFO model (osAiGetStatus was always 0), 100 ms audio ring buffer, keyboard+mouse layout (kbm_fps) and dual-stick pad layout,
mouse look through the stick, controller errno. Audio microcode was checked command by command against the C port with a golden test: no differences.

## Needs checking on Windows (nothing here could run on a GPU)
1. Build: `dotnet build` (shader `Combiner.fx` has fog additions; Trw.Structs builds as its own assembly, about 40 s).
2. A foggy level: distance fades to the fog colour. 3. 4-player split screen: no stale depth. 4. Tile-shift textures and G_MODIFYVTX UVs.
5. Keyboard+mouse in a level (F1 captures the mouse, Esc releases). 6. Audio: no gaps; about 100 ms prebuffer.
## Not done
Controller Pak / rumble (the SI model), controls.ini + mapping GUI, DirectInput pads, typed locals/parameters, typed global scalars.
