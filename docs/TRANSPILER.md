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

## Struct views (typed access to guest structs)
`p->health` is `Mem.S32(p + 0x1C)` in the old output. The optional struct-view layer keeps the memory model (display lists, ROM data and save data are
binary blobs shared with the emulated hardware) and puts a typed lens on top of it:
```csharp
public readonly struct ActorB { public readonly uint P; ... public int unk5E4 { get => Mem.S32(P + 0x5E4u); set => Mem.Set(P + 0x5E4u, value); }
                                public int unk5E4_Set(int v) => Mem.Set(P + 0x5E4u, v); public ActorStats stats => new(P + 0x...u); ... }
```
* **Generator** (`dotnet run --project tools/Trw.Transpile -- ... --struct-views-out <dir> [--struct-views-list <file>] @all.txt`, about 15 s from the AST cache).
  Reads the o32 record layouts of every translation unit (`AstIndex`, the same code the emitter uses) and writes `Structs00.cs..` (120 structs per file) and
  `ArrayViews.cs` into `<dir>`, plus the list of structs that have a view (`name|fingerprint|C# type|unique`). `tools/regen.ps1 -StructViews` runs it
  and copies the result to `Generated/Structs/`; without the switch regen behaves as before and `Generated/Structs/` is not touched. The views are compiled as their own
  assembly (`src/Trw.Structs`, ~17k small types; one compilation unit with the 5000 translated files exceeds the 3 GB of the build VM). Naming rules and hand use: `docs/STRUCTS.md`.
* **Views** (`StructViews.cs`): one `readonly struct` per named struct/union/typedef (`P` = guest address, `IsNull`, `SizeOf`, `At(i)`, implicit conversion to `uint`).
  Scalar fields: typed get/set with the C name (keywords get `@`, clashes with `P`/`IsNull`/... a trailing `_`). Pointer fields: a typed view (`Foo next`)
  when the pointee struct has one global layout, scalar pointers as `S32Ptr`/`F32Ptr`/... (`Trw.Platform/Views.cs`), always the raw `uint next_`; function
  pointers and other pointers stay `uint`. Nested structs return their view, arrays return generated `F32x3`/`Vec3x4`-style views (`Length`, `Stride`, indexer; arrays of
  arrays nest). Unions expose all members at their offsets, anonymous members are flattened into the parent, unnamed nested records are named `Parent_field`.
  Bitfields: none exist in the headers; a record with one is reported and left out.
  C# forbids property/indexer setters on a struct *rvalue* (`new Foo(p).next.x = 1` does not compile), so every writable member also has a method `x_Set(v)` (arrays: `arr.Set(i, v)`), which is what the emitter writes.
* **Name clashes**: the decompiled headers are module-local, so `Actor`, `Job`, ... are declared with different layouts in different files. The most used layout keeps the name
  and the others become `Name_v2`, ...; the emitter picks the one whose fingerprint (size, field names/offsets/types) equals its translation unit's. Structs that hold an
  unresolved field type are left out (the emitter then keeps the `Mem.*` form).
* **Emitter** (`EmitterViews.cs`, enabled by `--emit-views <list>`; `--emit-views false` or no flag = old output): `p->f`, `s.f`, `p->a.b->c`, `p->arr[i]`, `&p->nested`
  of a struct with a view become `new Foo(p).f` / `.f_Set(v)` / `.arr[i]` / `.arr.Set(i, v)`; compound assignments and `++` use the same pair.
  Pointer chains go through typed pointer members, nested structs and array elements. Function signatures, locals and the register-ABI code stay `uint`:
  views are created at the point of use, so `Funcs.CallReg`, hidden parameters and struct-by-value marshalling are untouched. Recompiled MIPS code (`Generated/Recompiled`)
  has no types and keeps `Mem.*`.
* **Equivalence**: `tools/oracle/run.sh` (boot and trial) is bit-identical, including the hash of all 8 MB of RAM every 30 ticks, and `TRW_DIFF=*` reports no mismatch.

## Debugging aids (environment variables, all off by default)
| variable | effect |
|---|---|
| `TRW_STATUS=1` | `[state]` (game state D_80146878), `[screen]` (UI screen id), `[pad]` (pressed buttons), `[status]`, `[audio]` lines on stderr |
| `TRW_PAD_SCRIPT` / `tools/run_window.ps1 -Pad` | scripted input: `<tick>:<BUTTON>[+..]:<hold>`, `S[@tick]:<BUTTON>:<hold>` (press once the screen settled) or, best, `R<screen>+<delay>:<BUTTON>:<hold>` / `T<state>+<delay>:<BUTTON>:<hold>` (press once, `delay` ticks after the UI screen / game state became that id; ids are in the `[screen]`/`[state]` log) |
| `TRW_TYPE_SCRIPT="<delay>:<text>,..."` | types on the NEW PLAYER name screen, `delay` ticks after it opened (`{BS}`, `{ENTER}`, `{ESC}`, `{COMMA}`; `[text]` log lines with `TRW_STATUS`), see "Typing a player name" |
| `TRW_SHOT_DIR/EVERY/MAX`, `TRW_EXIT_AFTER` | periodic screenshots, automatic exit (`tools/run_window.ps1`) |
| `TRW_GFX_LOG`, `TRW_GFX_DUMP_RUN=<n>`, `TRW_GFX_DL=1` | per display list statistics (ops, triangles culled/drawn), full command dump of list `n` |
| `TRW_TRACE_FN=a,b,prefix*`, `TRW_TRACE_LIMIT`, `TRW_TRACE_ARM_STATE=<n>`, `TRW_TRACE_DUMP=<bytes>` | log calls (and results of functions that call `Dbg.Leave`, see `tools/dbg_addleave.py`) from the moment the game reaches state `n` |
| `TRW_WATCH="addr:len;addr:len"` (+`TRW_WATCH_MAX`) | print the writer call chain (and the old bytes) of every write into the ranges |
| `TRW_PEEK="addr:len[@off:len];..."` | dump guest memory once a match runs |
| `TRW_DIFF=name,..\|*`, `TRW_DIFF_N`, `TRW_DIFF_EVERY`, `TRW_DIFF_RET`, `TRW_DIFF_VERBOSE` | differential test: run the function on the MIPS interpreter (`Debug/MipsInterp`) and as translated C# from the same memory and compare every byte written |
| `TRW_HEAPCHECK=1`, `TRW_BATCHLOG=1` | resource-cache overlap check, model-batch flush log |
| `TRW_WATCHDOG=<file>` | thread dump when graphics tasks stop for 8 s |
| `TRW_PAD2_SCRIPT`..`TRW_PAD4_SCRIPT` | scripted input for pads 2-4 (`Window/ExtraPadScripts.cs`): the `<tick>:`, `R<screen>+<delay>:` and `T<state>+<delay>:` steps of `TRW_PAD_SCRIPT`; a pad with a script is plugged in for the whole run |
| `TRW_PAD2_HUNT=1`..`TRW_PAD4_HUNT=1` | in a match that pad turns to the nearest other player, walks up to it and fires (test aid) |
| `TRW_MATCH_LOG=1`, `TRW_VIEW_LOG=1`, `TRW_SCORE_SHOT_DIR=<dir>` | player list / score records of a match (`[match]`), local-view regions (`[view]`), a screenshot whenever a score record changes |
| `TRW_HUD=game\|port`, `TRW_HUD_LAYOUT=<file>`, `TRW_HUD_LOG=1`, `TRW_HUD_VERIFY=1` | the game's own HUD draws / the port-drawn HUD from a layout file (default port), the HUD draws and values (`[hud]`), port quads vs the game draws they replace (`[hud-verify]`); see `docs/HUD.md` |

### 1-player route that works unattended
`-Pad "150:SDOWN:5,200:A:6,R24+500:START:12,R3+300:DOWN:20,R3+700:A:12,R3+1100:A:12,R19+600:A:12,R19+1100:LEFT:20,R19+1400:A:12,R9+400:A:12,R7+500:DOWN:20,R7+800:A:12,R10+500:A:12,R10+900:A:12,R25+400:A:12,R25+800:A:12,T13+400:SUP:300,T13+1400:Z:10"`
(screens: 24 title, 3 main menu, 19 controller-pak notice, 9 new game, 7 skins, 10 level info, 25 rumble notice; the first A after a cursor move is swallowed by the menus, hence the doubled A's).
With the virtual Controller Pak (below) screen 19 asks CREATE PLAYER? (NO preselected); the route above answers NO / UNABLE TO SAVE: OK and still reaches the level.
Creating a player instead (writes the note `RAGE WARS.A`, game `NRWE`, publisher `51`, 7 pages): replace the three R19 steps by
`R19+600:LEFT:20,R19+900:A:12,R19+1300:A:20,R19+1700:LEFT:20,R19+2000:A:20,R19+2400:LEFT:20,R19+2700:A:20,R19+3100:LEFT:20,R19+3400:A:20,R19+3800:LEFT:20,R19+4100:A:20`
(YES, name "A", DONE, CREATE NEW PLAYER A? YES; LEFT moves the name cursor harmlessly while the dialogs open).

## Controller Pak (memory card)
The pak is emulated at the serial-interface level, like the C port's `port/pak.c`: `__osSiRawStartDma` (`Os/OsDevices.cs`) hands every 64-byte PIF RAM
transfer to the Joybus model in `Trw.Platform/Pif.cs` / `ControllerPak.cs` (identify, 0x02 / 0x03 32-byte block read / write with the 5-bit address CRC
and the CRC-8 data reply, the 0x8000+ accessory region, "new accessory" latch), and `osContInit` / `osContGetQuery` report the slot status from the same
model. Everything above it is the game's own libultra code, translated: `osPfsInitPak` / `osPfsIsPlug` (the "no pak" overrides in `tools/overrides`
are gone), `osMotorInit/Start/Stop` (no longer host shims: `shim_replaced.txt`), `__osContRamRead/Write`, the checker, allocation, read/write, delete.
Saves are raw 32 KiB `.mpk` images in Project64's layout and naming (`Turok; Rage Wars_Cont_<n>.mpk`); a new or blank file gets the image Project64's
`CMempak::Format` writes, byte for byte. Files are written 300 ms after the last change and at process exit, as a temp file renamed over the old one.
| variable | effect |
|---|---|
| `TRW_PAK=0\|1` | master switch (default 1; oracle runs, `TRW_ORACLE_OUT`, default to 0 like the C port's) |
| `TRW_PAK1_TYPE`..`TRW_PAK4_TYPE=none\|memory\|rumble` | accessory per port (default: memory pak in port 1, other slots empty; a port without a controller answers "no device") |
| `TRW_SAVEDIR=<dir>` | folder of the images (default `<exe folder>\saves`, else `%LOCALAPPDATA%\TurokRageWars\saves`) |
| `TRW_PJ64_SAVE=<dir>` | a Project64 `Save` folder: uses `<name>_Cont_<n>.mpk` there or in its per-game folder `<name>-<md5>` ("Unique Game Dir"); new files go to the per-game folder when one exists |
| `TRW_PAK1`..`TRW_PAK4=<file>` | explicit image file per port |
| `TRW_PAK_DELAY=<ms>`, `TRW_PAKLOG=1` | write-back debounce; log every accessory transaction |

## Mouse look (`Os/MouseLook.cs`)
A true FPS mouse, the port of the C port's native controls. Capture: in free play the window switches SDL2 to relative mouse mode (cursor hidden and held in the
window, raw device motion without pointer acceleration; Esc / F1 release it, `TRW_MOUSE_WARP=1` = the old centre-warp capture). Look: pad 0's mouse motion
(0.05 degrees per count x `MouseSens`) is not converted into stick values any more; the game's view update func_002231B0 (`tools/overrides/func_002231B0.c`)
calls `trw_mouse_look` after its own turn / tilt: the actor turns by exactly the motion (x the game's turn-speed slider: 1/8 x .. 8 x, centre 1 x) and the view
tilt is set to an absolute pitch kept per controller slot, within the game's look limits (x the look-speed slider); `trw_mouse_reset` clears it on death and
in cut-scenes. Only real controller slots get it (bots carry AI controller objects), and only while that pad is played with the mouse (a pad / stick player
keeps the game's own look). While the weapon button is held the mouse moves the stick instead (the weapon wheel). The motion is latched with each controller
read (`Host.ReadMouse`: the last 4 reads, so a skipped view update does not end in a jump) and travels in the netplay input (protocol 2), so every machine turns
every player identically (tested: 2 headless peers with mouse scripts, 9 500 ticks, digests equal). `TRW_NATIVE_MOUSE=0` restores the stick emulation,
`TRW_LOOKLOG=1` logs what the hook does. Test scripts: the `<mouse dx>:<mouse dy>` fields of `TRW_KBM_SCRIPT` steps (counts per poll).

## Typing a player name (`Window/TextEntry.cs`)
The game's only text entry is the NEW PLAYER name of the pak screen (UI screen 19): a letter cycler in the lobby block `*D_800E54A4`, player record
`+0x58 + pad * 0xB68` (`O05Plr`), active while its state (`+0`) is 12. The name is 7 slots of `{glyph, 0}` at `+0xB34`, the cursor slot at `+0xB44`; D-pad
up / down step the letter through 0x20..0x5A (space, punctuation, digits, A-Z; func_00435010 / func_004350D0), left / right move the cursor (a slot entered
for the first time becomes 'A'; func_00430120 / func_00430208), A = DONE (func_004308B8 copies the slots and trims trailing blanks), B (func_00432334)
returns to the main menu when the pak screen was opened from 1 PLAYER TRIALS (lobby `+0x54` = 7), otherwise blanks the letter.
The keyboard drives exactly those buttons on pad 0: each typed character becomes left / right and up / down presses chosen closed-loop from the cursor
and letter in guest RAM (one press, one release per step: about 15 steps a second, 'Z' takes 25), so the game's own handlers make every change.
Lower case is upper-cased, characters outside 0x20..0x5A are dropped, an 8th letter is ignored. Backspace steps the last letter to a blank and moves the
cursor back to it, Enter presses A (repeated if the menu swallows it), Escape presses B only where B is the game's cancel (phase 7); otherwise Escape
does nothing on this screen (it no longer quits the program there). While the screen is active the keyboard's menu / gameplay bindings are off, and keys
still held when it closes stay off until released (the Enter that confirmed does not become Start); mouse and gamepads keep working.
Characters come from MonoGame's `Window.TextInput` (shift and keyboard layout applied), Backspace (with repeat) / Enter / Escape from the key states.
Test (headless, writes `JOAY 7` into the note data of `Turok; Rage Wars_Cont_1.mpk` at 0x500, then plays the level):
`TRW_PAK=1 TRW_SAVEDIR=<dir> TRW_TYPE_SCRIPT="120:jOHN{BS}{BS}ay 7{ENTER}"` with the pak-route steps `R19+600:LEFT:20,R19+900:A:12` (YES opens the name entry)
followed by `R19+2400:LEFT:20,R19+2700:A:20,...` as in the create-player route above.


## Multiplayer presentation: local view and scoreboard (`Window/LocalView.cs`, `ViewLayout.cs`, `Scoreboard.cs`, `MatchInfo.cs`)
Purely presentation: nothing here writes guest memory or reaches the game's input, and the interpreter's command stream (what the oracle hashes) is unchanged.
* **Split screen** (the game's): every camera of the match (list at 0x801450A8, next at +4, f32 width/height/x/y at +0x29C) gets a `G_MOVEMEM` viewport, a `G_SETSCISSOR`
  and a FILL rectangle that clears its part of the z-buffer (func_00235AD0), plus a 2-pixel frame (func_002372C4). 2 players: 480x178.5 halves (player 1 top);
  3-4 players: 240x178.5 quadrants (1 top left, 2 top right, 3 bottom left, 4 bottom right). The match clock is drawn afterwards with the full-screen scissor across the border.
* **Widescreen, per machine** (default on; `TRW_WIDESCREEN=0` presents the original 4:3, `TRW_WINDOW=<w>x<h>` the starting window size): every player picks their
  own window size / aspect / view mode, nothing of it reaches guest RAM, so netplay peers stay identical whatever they choose. Game side, the same everywhere: the
  camera setup (recompiled func_00234FDC) keeps the frustum extents at the far plane in the camera (+0x74 horizontal, +0x78 vertical), which the culling reads; a
  hook (`tools/mips2cs/hooks.txt`, `Runtime/Widescreen.cs`) widens them by fixed factors (x3 horizontal = up to 32:9, x2 vertical = a 2-player half shown full height),
  the projection matrix stays the game's (oracle runs keep the ROM's culling). Presentation, per machine (`GfxRenderer`): in a level (state 8 / 13) the frame renders
  into a target of the window's aspect and each perspective draw is rescaled about the centre of its view (= its scissor) from the game's projection aspect to the
  aspect the view has on this screen: x when wider (Hor+), y when taller (Vert+: the own view of a 2-player half). 2D (W = 1: HUD, rectangles) keeps its shape,
  centred horizontally and anchored to the top / middle / bottom (glyph runs stay together); draws spanning the whole width stretch. Menus stay 4:3.
* `tools/mips2cs/hooks.txt`: `function|instruction address|C# statement` inserts port code after an instruction of a recompiled function (kept for presentation only).
* **Local view** (`TRW_VIEW=local`, **F2** toggles split / local while the window runs): only the local player's view is drawn, scaled to the window. The renderer takes that
  camera's scissor rectangle (the n-th camera belongs to the n-th human player by controller port) and remaps every vertex (clip space: `x' = kx*x - bx*w`) and every scissor
  into it; draws scissored to another view end up empty and are skipped; full-screen draws that cross the view's edge without covering it (the clock) are left out and the clock
  is drawn by the port at the bottom of the picture. The view is rendered into its own target at the window's resolution (at least the split-screen density): a quadrant in a
  960x720 window becomes 960x716 instead of 240x178 pixels. Aspect: the view keeps its own aspect and is letterboxed (2-player halves are 8:3, quadrants 4:3);
  `TRW_LOCAL_ASPECT=stretch` fills the window instead. Menus, results screens and single-view frames are drawn as before; the mode only applies to frames that contain the local view's scissor.
* **Local player**: `TRW_LOCAL_PAD=0..3` (default 0); with netplay `NetSession.LocalPad`. `LocalView` is the only file that reads `NetSession` (names, ping, dropped / desync status).
* **Scoreboard**: hold **Tab** in a match (state 13 with the HUD on top; never in menus): name, frags, deaths per player (ping and status when netplay is active), the frag limit
  and the clock, in the game's fonts (`Content/Fonts/Peaceful Wars*.ttf`, `Scoreboard.spritefont` / `ScoreboardTitle.spritefont`). `TRW_SCOREBOARD=1` keeps it up for the whole match (screenshots).
  Score data (guest RAM, read live): records of 0x96 bytes at 0x80146398 (= `D_801462C8` + 0xD0), one per player slot, reached from the player actor (list `D_80145060`,
  next +0x16E0, record pointer +0x5D8, controller object +0x698: port = (ptr - 0x8010F328) / 0x224, bots have none); s16 +0x04 = frags (the frag-limit test func_0022804C and the HUD read it),
  s16 +0x02 = deaths (killed by another player; the results screen's DEATHS column), s16 +0x00 = suicides (a suicide also takes a frag), name at +0x84. Verified in bot matches with `TRW_MATCH_LOG=1`: a kill raised the killer's +0x04 and the victim's +0x02 (results screen: the same numbers), a suicide raised +0x00 and dropped +0x04 to -1. Match rules at 0x801468A0: f32 +0x14 clock (15 per second), s32 +0x18 frag limit.

## Finding a translation defect: the method that worked
1. Reproduce the scenario in both ports. The C port is only run from a private copy (never from the repository directory: other sessions use it).
2. Find the first observable divergence: a display list that is empty (`TRW_GFX_DL`), a global that differs (`TRW_PEEK` here, `ReadProcessMemory` on the
   C port: guest address == host address there), a function that never runs (`TRW_TRACE_FN`).
3. Walk back with `TRW_WATCH` / `Dbg.Leave` tracing to the first function whose result or output differs, then compare it with the ROM
   (`asm/*.s` of the C port, `Debug/MipsInterp`) instruction by instruction.
4. Fix the generator rule (not the generated file), regenerate, rerun the scenario, run `TRW_DIFF=*` over a play session.
