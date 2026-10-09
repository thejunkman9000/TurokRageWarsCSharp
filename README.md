# Turok: Rage Wars in C#

The game's own code, translated to C# and running on MonoGame. This is not a reimplementation: the C# is generated from the
decompiled C (and, for the functions that only exist as machine code, from the ROM's MIPS instructions), then edited by hand.

## Layout
| path | what |
|---|---|
| `src/Trw.Platform` | the console as a library: guest memory (`Mem`), guest stack, code-address table (`Funcs`), FPU/CP0 state (`Cpu`), RSP audio emulation (`AudioHle`) |
| `src/Trw.Game` | the game. `Generated/` is the translated code (one `.cs` per `.c`, plus `Recompiled/` for MIPS-only functions); `Os/` replaces libultra's operating system; `Runtime/` boots the machine; `Window/` is the MonoGame front end (input, audio output) |
| `tools/Trw.Transpile` | C -> C# (clang AST, MIPS o32 layouts) |
| `tools/mips2cs` | MIPS -> C# for functions without C source (reuses the C port's asm parser and analysis) |
| `tools/regen.ps1` | regenerates `src/Trw.Game/Generated` end to end (`arity.py`, `retuse.py`: what the ROM does with registers that the C does not say) |
| `tools/run_window.ps1` | unattended runs: scripted input, screenshots, logs |
| `tests/` | unit tests for the platform layer |
| `docs/TRANSPILER.md` | design notes and status |

## Running
You need your own copy of the ROM (`baserom.us.v11.z64`); it is never stored in the repo.

```
dotnet run --project src/Trw.Game -- --rom path\to\baserom.us.v11.z64              # window
dotnet run --project src/Trw.Game -- --rom path\to\baserom.us.v11.z64 --headless --seconds 10
```
Keys: arrows = stick, `C` = A, `X` = B, `Z` = Z, `Enter` = Start; gamepads work too.
Status: boots to the menus and plays the 1-player trials and multiplayer matches with 3D graphics and audio (see `docs/TRANSPILER.md`).

## Regenerating the translation
`tools/regen.ps1` needs the C port repository (default `D:\Projects\TurokRageWars`), Python with `ziglang` (clang) and the .NET SDK.
Once the translation is edited by hand, do not regenerate over it.
