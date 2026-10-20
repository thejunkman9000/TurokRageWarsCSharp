# Button glyphs: the game's buttons drawn as the player's device

Turok: Rage Wars names the N64 controller's buttons on screen: "PRESS START", "PRESS THE 'A' BUTTON TO CONTINUE", "PRESS 'B' TO BYPASS", the C-up
picture next to "SKINS =" on the character select. The port draws, in their place, the button the player actually presses on the device in use:

| device | what the prompt shows |
|---|---|
| keyboard + mouse | the key bound to that N64 button, as a keycap with its name in the game's font (`ENTER`, `E`, `SPACE`), or the mouse button / wheel |
| Xbox-style pad | the pad control bound to it: A / B / X / Y in their colours, LB / RB, LT / RT, the menu and view buttons, the d-pad, the sticks |
| PlayStation-style pad | the same bindings with PlayStation shapes: cross / circle / square / triangle, L1 / R1 / L2 / R2, options / share, L3 / R3 |
| N64 controller (adapters, Switch Online N64 pad) | the N64's own buttons, redrawn: blue A, green B, red START, yellow C-buttons, Z, L, R, the stick |
| touch screen (Android) | the on-screen control of that N64 button (translucent rings, like the touch overlay) |

It switches live: the last device that produced input wins (`ActiveInput`). A presentation change only: the game's memory is read, never written; the
simulation, netplay and the oracle hashes do not change (checked: a deterministic headless run's hash file is byte-identical with and without it).

Source: `src/Trw.Game/Glyphs/` (`ButtonPrompts.cs` finds and replaces, `GlyphSet.cs` picks the glyph, `Bindings.cs` asks the input mapping,
`GlyphRaster.cs` draws the SVGs), `src/Trw.Game/Window/ActiveInput.cs` (the device in use), `src/Trw.Game/Window/DeviceReport.cs` (desktop reporting),
assets `src/Trw.Game/Content/Glyphs/<set>/<button>.svg`, generator `tools/glyphs/make_svgs.py`. Tests: `tests/Trw.Game.Tests/ButtonGlyphTests.cs`.

## What the game draws, and where it is replaced

The game has no button font or button sprite sheet in its HUD. Every button reference is in the frontend (menus, pak screens, prompts), drawn in two ways:

1. **Text.** The prompts are plain strings of the frontend string bank ("PRESS START", "AND PRESS 'START'", "PRESS 'B' TO BYPASS", "PRESS 'A' TO SELECT A
   mAP", "PRESS 'A' TO / CHOOSE A / PLAYER SLOT", "PRESS THE 'A' BUTTON / TO CONTINUE", "mOVE STICK / <  OR  > / TO CHOOSE / A CHARACTER"; ROM
   0x202000-0x203900, mixed case picks alternate letter shapes). `UiNode_DrawText` queues one quad per character and `UiText_FlushGlyphBatches` draws
   them grouped by character, each as a texture rectangle whose texture is that character's pixels inside the font data. The UI font pool
   (`0x80153C20`: count, fonts of 0x49C bytes at `*0x80153C28`) gives, per font, the glyph data offsets (+0x304, 96 entries, glyph slot = character
   - 32) and the glyph records {offset, w, h} (+4). A draw command whose texture address (`DecodedTexture.Src`) is one of those offsets is that
   character (several slots can share data: a quad keeps the set of characters it can be).
2. **Pictures.** The frontend image archive (in the ROM file archive at 0x200000; image records `*0x80153C10`, 28 bytes each) has three button
   pictures: images 100 and 101 (C-up, 11x13, "SKINS =" / "= SKINS" on the character select, "DEFAULT =" on the loadout, "OPTIONS =" on the match
   options) and 183 (the A button, 13x13). A draw is one of them when the bytes at its texture address equal that image's pixels in the ROM
   (archive data base `*(0x801539B0 + 0x248)` + the record's offset), so the match follows the picture's content, not where it was loaded.

`ButtonPrompts.Apply` runs on every frame's command list in `GfxRenderer.Execute`, before the port HUD:

* the characters are put back into rows (same top), split into runs where the gap is wide, and word gaps are taken from the drawn space glyphs
  (or a gap clearly wider than the row's letter spacing);
* button words: a quoted name (`'A'`, `'B'`, `'START'`, `'Z'`, `'L'`, `'R'`), `START` right after `PRESS`, `STICK` right after `MOVE`. Their character
  quads are left out of their draw commands, the glyph is drawn in their place (1.75 x the letter height), and the rest of the run moves apart to make
  room, keeping the run's centre (the frontend centres these texts);
* a button picture's quad is left out and the glyph is drawn centred on it, slightly larger;
* the glyph draws are ordinary `DrawCommand`s in the frame's screen coordinates (the port HUD's canvas, `HudCanvas`), inserted right after the text /
  picture draws, with the same scissor and the opacity the game gave the replaced draw (its alpha combiner evaluated for an opaque texel: fades and
  blinking carry over). So they go through the same renderer path: widescreen 2D shape, local view, screenshots.

Draws of glyph slices (the menus' wipe effects draw characters in 2-pixel strips) are ignored: they are recognised by a height that is not the glyph's.

## Which button: the bindings

`Bindings` asks the port's own mapping (`InputMapper` in `Trw.Platform/InputModel.cs`): every key, mouse button, wheel direction and pad control is
fed alone through a private mapper, in the menu context and in the free-play context, and the N64 buttons (and stick directions) it produces are
recorded. So there is no second copy of the bindings, and a changed mapping changes the prompts. Menus use the menu bindings, a level the free-play
ones; a button the current context does not bind falls back to the other context's binding. When several keys bind a button, `E`, `Space` and `Enter`
come first, then single letters, then the rest (the numeric pad last); mouse buttons only when no key is bound. With the shipped mapping: A = `E`
(menus) / `E` (play), B = `X`, Z = `Z`, START = `ENTER`, L = `Q`, R = `SPACE`, C-buttons = `W A S D` (play), the stick = the arrow keys; Xbox: A = A,
B = B, Z = RT, START = menu, L = LB, R = RB, C-buttons = left stick (play), the stick = right stick.

`GlyphSet.Resolve(button, device, gameplay)` turns the binding into a glyph name: `keyboard/keycap` + label, `keyboard/key_<dir>`,
`keyboard/mouse_<button>`, `keyboard/arrows` (the whole stick); `xbox/<control>` and `playstation/<shape>` (XInput names mapped to PlayStation shapes:
A cross, B circle, X square, Y triangle, LB L1, RB R1, LT L2, RT R2, Start options, Back share); `n64/<button>` and `touch/<button>` directly.

## The device in use

`ActiveInput` (`Window/ActiveInput.cs`, shared with the Android build) holds the device of the last input. Desktop reporting (`Window/DeviceReport.cs`,
called from `TrwWindow.SampleDevices` and `TrwWindow.ReadPad`): a key or mouse button down, the wheel, or the pointer moving -> keyboard + mouse; a pad
button, the d-pad, a stick or trigger past 0.35 -> that pad's kind. The kind comes from the name SDL gives the pad: Sony names / vendor id 054c
(DualShock, DualSense, "PS4 Controller", "Wireless Controller") -> PlayStation; N64 pads and adapters (Switch Online N64, Mayflash / Retrolink /
raphnet / Tomee) -> N64; anything else -> Xbox. The Android touch controls call `ActiveInput.Report(InputDeviceKind.Touch)`.

## Assets: SVG, rasterized at runtime

The glyphs are SVG files, `Content/Glyphs/<set>/<name>.svg` (sets `keyboard`, `xbox`, `playstation`, `n64`, `touch`), 64 units high, any width,
copied next to the executable. MonoGame cannot draw SVG, so `GlyphRaster` renders them with **Svg.Skia** (SkiaSharp) at the pixel height they are
shown at (rounded up to 8 pixels, cached), into a texture: sharp at any window size and `TRW_GFX_SCALE`. Text in the SVGs uses
`font-family="'Peaceful Wars'"`; the rasterizer hands the game's font (`Content/Fonts/Peaceful Wars.ttf`) to the SVG renderer as a custom typeface.
`keyboard/keycap.svg` is a template: `{{LABEL}}`, `{{FONT_SIZE}}`, `{{BASE}}`, `{{CX}}`, `{{W}}`, `{{FACE_W}}`, `{{INNER_W}}` are filled in per key,
the cap as wide as the measured label needs (at least square).

Style (the game's menu look): dark bronze bodies, gold rims (`#E0A040`), warm white labels; pads' face buttons in their usual colours and shapes,
generic drawings, no logos. `tools/glyphs/make_svgs.py` writes all of them from shared shapes; edit the script and run it again, or edit an SVG by hand.

`TRW_GLYPHS_SHEET=<file.png> Trw.Game.exe` draws every glyph onto one sheet and exits (no ROM needed): the quickest way to look at a change.

## Adding a glyph or a device

* **A glyph**: add the SVG under its set's folder with the name `GlyphSet` asks for (missing files are logged once as `[glyphs] missing ...`, and that
  prompt keeps the game's own drawing).
* **A new button word or picture**: text words are in `ButtonPrompts.Find` (`quoted` names, the PRESS / MOVE rules); pictures in `iconImages`
  (archive image index -> N64 button). `TRW_GLYPHS_LOG=1` with `TRW_GLYPHS_LOG_ALL=1` logs every text run the frontend draws, which helps to find
  new ones.
* **A device**: add the value to `InputDeviceKind` (keep the existing ones), a folder of SVGs, its case in `GlyphSet.Folder` / `GlyphSet.Resolve`,
  and report it (`ActiveInput.Report`) from the input code; for a pad, add its names to `DeviceReport.Classify`.

## Switches

| variable | effect |
|---|---|
| `TRW_GLYPHS=keyboard\|xbox\|playstation\|n64\|touch` | pins the glyph set (screenshots, tests) |
| `TRW_GLYPHS=off` | leaves the game's own buttons |
| `TRW_PAD_KIND=xbox\|playstation\|n64` | the glyph set of every gamepad, overriding the name guess |
| `TRW_GLYPHS_LOG=1` | logs each prompt found and the glyph chosen, and screen changes (`[glyphs] ...`); `TRW_GLYPHS_LOG_ALL=1` also every text run |
| `TRW_GLYPHS_SHEET=<file.png>` | draws all glyphs to a PNG and exits |

## Known limits

* The C-buttons have no keyboard or pad binding in the **menu** context of the shipped mapping (`InputMapper`: W A S D and the left stick give the
  C-buttons only in free play; in menus they move the cursor). The character select's "SKINS = C-up" (and "DEFAULT =", "OPTIONS =") therefore shows
  the free-play binding (`W`, left stick up), which does not change the skin in a menu. A menu binding for the C-buttons in the mapping would fix
  both the control and the prompt (the prompt follows the mapping automatically).
* Prompts in rows the game draws as slices (the wipe effect while a menu item is highlighted) are left as they are during that effect.
* The rows are re-centred on the assumption that the game centred them (true for every prompt found); a left-aligned row with a button word would
  shift left by half the extra width.
