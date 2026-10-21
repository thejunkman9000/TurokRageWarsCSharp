(This is the one you want!)

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
| `tests/` | unit tests for the platform layer and the port HUD's layout (`Trw.Game.Tests`) |
| `docs/TRANSPILER.md` | design notes and status |

## Running
You need your own copy of the ROM (`baserom.us.v11.z64`); it is never stored in the repo.

```
dotnet run --project src/Trw.Game -- --rom path\to\baserom.us.v11.z64              # window
dotnet run --project src/Trw.Game -- --rom path\to\baserom.us.v11.z64 --headless --seconds 10
```
Keys: arrows = stick, `C` = A, `X` = B, `Z` = Z, `Enter` = Start, `W A S D` = D-pad, `I J K L` = C buttons, `Q`/`E` = L/R; F11 = fullscreen; in a multiplayer match Tab = scoreboard, F2 = split screen / only your own view (`TRW_VIEW=local`, `TRW_LOCAL_PAD=0..3`, see `docs/TRANSPILER.md`); the 3D is widescreen at the window's shape (resize freely; `TRW_WIDESCREEN=0` for the original 4:3); gamepads work too (left stick, A/B, triggers = Z, bumpers = L/R, right stick = C buttons).
On the Controller Pak's NEW PLAYER name screen the keyboard types the name (letters, digits, space and the game's punctuation; Backspace, Enter = done,
Escape = back where the game allows it); the keys turn into the game's own letter-cycler presses, so the letters can be seen stepping into place.
The in-game HUD is drawn by the port from a layout file (`Content/Hud/hud.json`, or your own `hud.json` next to the executable / `TRW_HUD_LAYOUT`): the default reproduces the original HUD with the game's own sprites, and elements can be moved, recoloured, removed or added by editing the file (it reloads when saved; F6 reloads, F7 switches to the game's own HUD draws, `TRW_HUD=game` starts with them). See `docs/HUD.md`.
Status: boots to the menus and plays the 1-player trials and multiplayer matches with 3D graphics and audio (see `docs/TRANSPILER.md`).

## Android
`src/Trw.Android` builds the same game for Android tablets with on-screen touch controls (gamepads and keyboards work too); the ROM is picked
on the first start. See `docs/ANDROID.md`.

## Playing online
```
dotnet run --project src/Trw.Game -- --rom path\to\baserom.us.v11.z64 --netplay
```
`--netplay` opens the online screens instead of booting straight into the game (plain `dotnet run` is unchanged). Sign in with your
jsNetwork account or create one (the same account works in the other jsNetwork games), then **Host a game** (name, 2-4 players, optional
password) or **Find a game** and join. In the lobby everyone sees the players in controller order: the host is player 1, the others are
players 2-4 in the order they joined. Joiners press **Ready**; the host presses **Start**, and every machine boots the game together.
From then on it is ordinary split-screen multiplayer on every machine: you drive your own controller with your keyboard + mouse or gamepad,
and go through the game's own multiplayer menus together (each player confirms with their own controller).
Everyone needs the same ROM (checked), Controller Paks are off in online sessions (no saves), nobody can join a session that has started,
and a player who leaves keeps their character standing still. Settings: `--master <url>` or `TRW_MASTER` (the master server), `TRW_NET_DELAY`
(input delay in 60 Hz ticks, default 3; raise it for long distances), `TRW_NET_PORT` (default 7810; open it in your router to let players
connect directly, otherwise the master's relay carries the game). Details: `docs/NETPLAY.md`.

## Regenerating the translation
`tools/regen.ps1` needs the C port repository (default `D:\Projects\TurokRageWars`), Python with `ziglang` (clang) and the .NET SDK.
Once the translation is edited by hand, do not regenerate over it.
