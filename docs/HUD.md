# The HUD: the game's and the port's

The in-game HUD can be drawn two ways:

* `TRW_HUD=game`: the game's own HUD draws, exactly as before.
* `TRW_HUD=port` (the default): a port-drawn HUD laid out by a layout file (`Content/Hud/hud.json`). It is editable: move, scale, recolour, hide or add
  elements by editing the file. The shipped layout reproduces the original HUD at 4:3, and for every element it replaces it draws the same texels at the
  same place in the same colour as the game (checked quad by quad, see "Verification").

**F7** switches between the two while the window runs. **F6** reloads the layout. The layout also reloads by itself about a second after the file is saved.

Either way the game's HUD code keeps running unchanged: it updates timers, animations and movers in guest RAM, so the simulation is the same and netplay
peers stay identical. The port HUD only reads guest RAM. It never writes it, and nothing of it reaches the game's input or the oracle's command hash.

Source: `src/Trw.Game/Hud/` (namespace `Trw.Game.PortHud`). Tests: `tests/Trw.Game.Tests`.

## What the game's HUD draws (func_0021EED8 and callees)

Call path per frame (decompiled C): `func_00236F0C` (race update) goes through the cameras (the split-screen views, list at `0x801450A8`, next at +4). For
each one it runs the world pass, then `func_00233C78` -> `func_0022A37C`, which loads the view's 2D projection (`func_0023941C`), then
`func_0021EED8(player, view)`, the on-screen messages (`func_00238304`) and the view's 2-pixel frame (`func_002372C4`). After the last view the "own racer"
at `0x80145000` gets a shared pass over the whole screen, which also draws the match clock (`func_00228AD0`).

Every HUD picture is a sprite of the HUD library `D_8011FE88`, drawn by `func_002ABC18(id, frame, x, y, sx, sy)` as texture rectangles. Kind 0 sprites
have an RGBA 5551 colour plane and a 4-bit alpha plane, combined as TEXEL0 * ENV (colour) and TEXEL1 * ENV (alpha). Numbers use sprite 4 (frames 0-9 are
the digits, 10 is the minus sign), drawn by `func_002A921C`. Text (the names ticker, messages, the clock's colon) uses the game's text font
(`func_002A9F18`), which is not a library sprite.

`view` is the camera's rectangle (f32 w/h/x/y at +0x29C). `sx = view.w / 480` and `sy = view.h / 360`, so a 2-player half draws everything at half
height: that is the original look. In hi-res mode (`D_800E28D8` = 1, the game's default here) `kh = 1.5` and `ks = 1`; otherwise `kh = 1` and `ks = 0.75`.
`P` is the player (`Shared_HudPlayer`; the player list is at `D_80145060` with next at +0x16E0, and the player whose +0x5DC is the camera owns the view).
`S` is `D_801462C8` (match settings, `HudStatusShared_Settings`) and `R` is `S + 0x5D8` (rules).

| element | code, sprite | RAM source | position (screen px) |
|---|---|---|---|
| health | `func_002A921C`, sprite 4, scale kh | `(s32 P+0x5E4 + 0xFF) >> 8`; alpha `u8 S+0x16` (HUD fade, 128) | mover `P+0xEE0`: `(f32 +0xEF0 * kh * sx + view.x - 3 kh sx, f32 +0xEF4 * kh * sy + view.y + view.h - 4 kh sy)`; rest (28, -32) |
| hurt flash | sprite 0x208, 4 corners, env colour fades | `P+0x1104` state, `P+0x1130` alpha, `P+0x11D8` hurt | around the view's centre |
| frags (score) | number + animated skull 0x1FA (team games: 0x204; markers 0x202) | `s16 owner+0x04` (owner = `P+0x5D8`, the score record), or the team score `R+0x2C + team * 4`; clamped at -99; skull frame `P+0x11CC` while `P+0x1050` >= 2 | mover `P+0x1048`: `x = +0x1058 * ks * sx + view.x + view.w`, minus 12 when x > 200; `y = +0x105C * ks * sy + view.y`; skull at (x + 8 ks sx, y + 16 ks sy), scale (ks, 1.5 ks); number at (x + 48 ks sx, y + 22 ks sy); rest (-82, 16) |
| trials: tries left | number + hourglass 0x1F6 (markers 0x203) | `s32 P+0x133C - 1`; when `S+0x1D` (HUD shown) and trial kind `S+0xD` is 1 or 4 and `P+0x1450` is 0 | mover `P+0xF1C`: `(+0xF2C * ks * sx + view.x, +0xF30 * ks * sy + view.y)`; number at +(0, 22 ks), hourglass at +(20 ks, 16 ks) scale (ks, 1.5 ks); rest (16, 16) |
| lives | icons 0x12C, 0x1F6 + number | `s16 P+0x5EA`; when `S+0x1D` is 0 and the mover is active | lives mover; icons at +(18, 28) and +(0, 0), number at +(30, 24) |
| count rule / squad score | number + 0x1FD / 0x201 | `s16 owner+0x06` / `R+0x40 + team * 4`; `R+0x54` / `R+0x78` | movers `P+0x1084` / `P+0x10C0`, like the frags; rest (-82, 68) |
| ammo (3 slots) | `func_0021EA30`: icons 0xBEA, 0xBEB, 0xBEC + numbers | `s16 P+0x5F4 / 0x5F6 / 0x5F8`; a slot shows while its mover `P+0xE2C + i * 0x3C` has +0 and +8 set, the weapon `s16 P+0x62E` < 0x12 and the weapon has an ammo type (`func_0022AB8C`: table `D_800D052C`, then `D_800D0588`) | `x = f32 +0x10 * sx + view.x + 10`, `y = f32 +0x14 * sy + view.y + view.h`; icon at (x - 32 sx, y - 8 sy), number at (x - 6 sx, y - 4 sy); rest x 120 / 190 / 260, y -46 while shown (458 = slid out below the screen); a slot slides in when its count changes |
| crosshair | `func_0021E27C` -> `func_0021E068`: sprite `s32 P+0x798` (0x2EF etc.), frame `f32 P+0x7BC` | shown while `P+0x788` is set (and more conditions) | the view's centre minus `scale * size - 1` (`D_800C767C/80/84` by sprite class, size `D_800C7688` = 1): 16x16 at its own size in every view. A second marker (`P+0x7B8`) is projected from the world |
| zoom gauge | 0x20A / 0x20B, frame = zoom level | `f32 P+0x12C0` | `(view.x + 10 ks sx, view.y + view.h - 140 ks sy)` |
| power-up / status | 0x209, 0x20C | `P+0x122C` flags, `P+0x1230` time, `P+0x11E4` boost | centre - (31, 32); `(view.x + view.w - 70, view.y + view.h - 140)` (scaled) |
| ammo pips (weapon 12) | 0x2F4 x 3 | `P+0x5F8`, `func_002831FC` | centre + (`D_800CAB78[i]`, 64) |
| names ticker | text, `func_002A8F28` | `P+0x12CC` names, `+0x12F4` fades | left of the frags |
| messages (pickups etc.) | text, `func_00238304` | the view's message list `+0xE44` | centred, 80% down the view |
| team / squad overlays | 0x1FC, 0x201 (`func_0022C1D8`, `func_0022C36C`) | `P+0x7BC` | view's lower right |
| match clock (shared pass) | `func_00228AD0`: numbers + the text ":" | `f32 0x801468B4` (15 per second): minutes, seconds; blinks in the last 30 s (`D_800D2980 % 15 >= 5`) | one view: (240, 20) at scale 1; several: (240, (360 - 10) / 2) at the first view's (sx, sy): minutes end at x - 7 sx, seconds (2 digits) start at x + 2 sx, colon at (x - 6 sx, y - 12 sy) |
| 3D effect | `func_00219490` (`P+0x878`) | | a model drawn inside the HUD section (perspective: always kept) |

Measured positions (top-left of the first quad, hi-res, the movers at rest; 1P and 2P confirmed by `TRW_HUD_LOG`, 4P from the formulas):

| view (rect) | health digits | skull | frags | trial number / hourglass | ammo 1 icon / digits | crosshair |
|---|---|---|---|---|---|---|
| 1P (0,0 480x360) | 37,306 (24x24 each) | 394,32 (32x30) | 434,38 | 16,38 / 36,32 | 98,306 / 124,310 | 233,173 (16x16) |
| 2P top (0,0 480x178.5) | 37,151 (24x11.75) | 394,15 (32x14.75) | 434,18 | 16,18 / 36,15 | 98,151 / 124,153 | 233,82 |
| 2P bottom (0,178.5 480x178.5) | 37,330 | 394,194 | 434,197 | 16,197 / 36,194 | 98,330 / 124,332 | 233,260 |
| 4P top left (0,0 240x178.5) | 18,151 (12x11.75) | 203,15 (16x14.75) | 223,18 | 8,18 / 18,15 | 54,151 / 67,153 | 113,82 |
| 4P top right (240,0) | 258,151 | 431,15 | 451,18 | 248,18 / 258,15 | 294,151 / 307,153 | 353,82 |
| 4P bottom left (0,178.5) | 18,330 | 203,194 | 223,197 | 8,197 / 18,194 | 54,330 / 67,332 | 113,260 |
| 4P bottom right (240,178.5) | 258,330 | 431,194 | 451,197 | 248,197 / 258,194 | 294,330 / 307,332 | 353,260 |

3 players use the 4-player quadrants (the fourth stays empty). Note the game's quirk in the left quadrants: their score x is 199, not more than 200, so
those skulls are not moved 12 pixels left.

## How the HUD's draws are found (`HudTagger.cs`)

The HUD's display-list commands are found from the display list itself, so no game function is changed. The game's HUD starts every section by loading
a projection matrix that nothing else loads: `G_MTX(projection, load)` of camera + 0x448 (+0x40 on odd frames, `D_800D297C`), from `func_0023941C`. The
world pass of the next view loads camera + 0x380 (+0x40). The interpreter calls a hook, `DisplayListInterpreter.HudMarker`, with the address of every
projection matrix. The hook returns:

* view index + 1 for a camera's 2D projection, or `0x40` (`HudTagger.Shared`) for the own racer's (`0x80145000`): the HUD section starts;
* 0 for a camera's 3D projection: the section ends;
* null for any other projection, such as a model inside the HUD: the tag does not change.

Every `DrawCommand` then carries `Hud` (the tag) and `HudData`, a snapshot of the section's values (`HudSnapshot`). The hook takes the snapshot while the
list is interpreted (on the thread that starts the graphics task, `osSpTaskStartGo`), so the port HUD shows the frame the game's HUD drew (animation frames,
slide-ins), not the newer RAM of the moment the window presents it. The game thread can already be a frame further when the task starts; then an element
that is moving is one frame ahead (seen once in 26 checks of the 2-player run: an ammo slot 3 px further into its slide-in).

This works on the display list as the game built it, so it does not depend on which thread builds the list or when, and it needs no write pointer.
`D_80110634` ranges would need a hook inside the generated code. The hook is not installed in oracle runs and headless runs, and the oracle does not hash
`Hud` / `HudData`. A tag change flushes the interpreter's batch, which can split a draw in two in window runs; the picture is the same.

Which game draw is which element: a draw's texture remembers the RDRAM address it was loaded from (`DecodedTexture.Src`). `GameSprites` finds each HUD
sprite's data in RAM with a read-only copy of the game's lookup (`func_0028BE88`): the library's key table at +0x9C (binary search), the offset table at
+0x5C and the base at +0x2C give the cartridge key, the resource cache's hash (`*0x80105194`, mask `*0x80105190`, chained at +0xC) gives the cache entry,
and the entry's node points to the data. A draw whose texels lie inside sprite N's data belongs to sprite N. Draws from no library sprite are the text font.

## The port HUD layer (`HudLayer.cs`)

Per frame, for every HUD section in the frame (one per view, plus the shared pass), the layer:

1. lays the layout's elements out in that view's rectangle, from the snapshot's values, as ordinary `DrawCommand`s in N64 screen coordinates
   (`HudCanvas`: 480x360, W = 1, the game's own sprite render state). They are inserted after the section's last command, so they keep the game HUD's
   place in the draw order, and they go through the same renderer path as the game's HUD: the RDP combiner, the own-view remap (the HUD at its 1-player
   proportions), and the 4:3 shape in a wide picture (`GfxRenderer.Keep2DShape`: the HUD frame is the centred 4:3 area);
2. decides which of the game's own HUD draws stay. Perspective draws (models) and untextured draws (the views' frames, fades) always stay. A 2D textured
   draw (sprites, digits, text) stays only if a `game` element of the layout keeps its category, optionally moved. All other game HUD draws are left out:
   they are the ones the port draws itself.

Only frames of a level are touched (game state 8 = attract demo, 13 = playing). Menus are left alone.

Sprites (`GameSprites.cs`) are decoded from guest RAM into exactly the texels the interpreter decodes for the game's draws (RGBA 5551 and I4 planes). They
are cached while the resource stays where it is. Quad corners are truncated to quarter pixels and the texel step to 1/1024, as `func_002AA724` does, and
1:1 rectangles are point sampled like the interpreter's draws. Text elements use the Peaceful Wars sprite fonts of `Content/` (`Scoreboard`,
`ScoreboardTitle`, `NetFont`, `NetTitle`) as a white coverage atlas tinted by the element's colour.

Why port is the default: for every element the shipped layout replaces (health, frags and skull, trial and lives counters, ammo, clock) the port draws are
quad-for-quad identical to the game's (same texels, same quarter-pixel box, same tint) in the 1-player trial and the 2-player split (`TRW_HUD_VERIFY`).
Everything else (crosshair, hurt flash, zoom, status, messages, names ticker, team overlays, the clock's colon) is the game's own draw. So the default looks
like the original, and the HUD becomes editable at no cost. `TRW_HUD=game` stays as the reference and as the fallback.

Options:

| variable / key | effect |
|---|---|
| `TRW_HUD=game\|port` | the game's HUD draws / the port HUD (default port) |
| `TRW_HUD_LAYOUT=<file>` | the layout file. Default: `hud.json` next to the executable (a player's own copy), else `Content/Hud/hud.json` (shipped) |
| `TRW_HUD_LOG=1` | every 150 frames: each HUD draw (tag, sprite, box, tint) and each view's values |
| `TRW_HUD_VERIFY=1` | every 150 frames: compares the port's quads with the game draws they replace (`[hud-verify] ... quads identical`) |
| F6 / F7 | reload the layout / switch game and port HUD |

## The layout format

JSON. Comments (`//`, `/* */`) and trailing commas are allowed. An element with an error is left out and reported on stderr (`[hud] ...`). A file that
does not parse keeps the previous layout.

```json
{
  "version": 1,
  "elements": [
    { "id": "health", "kind": "number", "value": "health", "origin": "health", "x": "-3*kh", "y": "-4*kh", "scale": "kh",
      "alpha": "hudFade", "when": "!hideHud" },
    { "id": "frags-icon", "kind": "sprite", "sprite": "teams ? 0x204 : 0x1FA", "frame": "score.state < 2 ? 0 : score.frame",
      "origin": "score", "x": "8*ks", "y": "16*ks", "scale": ["ks", "ks*1.5"], "alpha": "alpha", "when": "!hideHud && hudShown" },
    { "id": "my-bar", "kind": "bar", "value": "health", "max": 100, "anchor": "bottom-left", "x": 16, "y": -14,
      "width": 80, "height": 5, "color": "#E04020", "back": "#00000080", "players": [1, 2] },
    { "id": "limit", "kind": "text", "text": "LIMIT {fraglimit}", "font": "Scoreboard", "size": 14, "align": "right",
      "anchor": "top-right", "x": -16, "y": 56, "color": "gold", "when": "fraglimit > 0" },
    { "kind": "game", "match": ["crosshair", "flash", "text", "other"] }
  ]
}
```

Common fields (all optional except `kind`):

| field | meaning |
|---|---|
| `kind` | `number`, `text`, `sprite`, `bar`, `rect`, `crosshair`, `game` |
| `id` | a name (messages, your own reference) |
| `show` | `false` turns the element off |
| `players` | the player counts it shows in: `[1]`, `[2, 3, 4]`, `"all"` (default) |
| `when` | an expression; the element shows while it is true |
| `pass` | `"view"` (default: in every view) or `"shared"` (once over the whole screen, like the clock) |
| `anchor` | `top-left` (default), `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, `bottom-right` of the view's 4:3 HUD frame |
| `origin` | instead of an anchor, a position the game computes, which follows the game's slide-ins: `health`, `score`, `count`, `squad`, `lives`, `ammo1`..`ammo3`, `crosshair` (the view's centre, not scaled), `clock`, `center` |
| `x`, `y` | offset in HUD frame units (480 x 360 per view; a 2-player half maps them at half height) |
| `scale` | a number or `[x, y]` |
| `color` | `"#RRGGBB"`, `"#RRGGBBAA"`, a name (`white`, `gold`, `orange`, ...) or `[r, g, b, a]` |
| `alpha` | 0..255 (default 255), multiplied with the colour's alpha |
| `snap` | `false` keeps sub-pixel positions (default: whole pixels, like the game) |

Every number can also be an expression (a string).

Kinds:

| kind | fields |
|---|---|
| `number` | `value`, `digits` (minimum, zero padded), `align` (`left` = starts at x, `right` = ends at x); the game's digit sprites (13 units per digit, '1' 0.7 of that), or `font` + `size` for a sprite font |
| `text` | `text` (format string), `font` (default `Scoreboard`), `size` (line height in frame units, default 16), `align` (`left`, `center`, `right`), `shadow` (default true) |
| `sprite` | `sprite` (library id, e.g. `"0x1FA"`, or an expression), `frame` (expression; wraps around the frame count) |
| `bar` | `value`, `max`, `width`, `height`, `back` (background colour), `direction` (`right`, `left`, `up`, `down`), `border`, `borderColor` |
| `rect` | `width`, `height` |
| `crosshair` | `style`: `sprite` (the game's: sprite `crosshair.id`, frame `crosshair.frame`; place it with `"x": "-crosshair.half", "y": "-crosshair.half"`), `cross` (`size`, `gap`, `thickness`), `dot` |
| `game` | `match`: categories of the game's HUD draws to keep: `crosshair`, `flash`, `frags`, `lives`, `ammo`, `zoom`, `status`, `pips`, `team`, `digits`, `sprite:0x...`, `text` (the game's text font), `other`, `all`. `x` / `y` move them |

Expressions: numbers (`12`, `0x1FA`, `0.75`), value names, `true` / `false`, `+ - * / %`, `== != < <= > >=`, `! && ||`, `c ? a : b`, parentheses,
`min max abs floor ceil round clamp`. Unknown names are 0. Format strings: `{expr}` (an integer), `{expr:0.0}` (a .NET numeric format), `{expr:time}`
(seconds as m:ss), `{expr:htime}`; `{{` and `}}` are braces.

Values (`HudValues.cs`; per view, read from the snapshot):

| name | meaning |
|---|---|
| `players`, `view`, `views`, `view.x/y/w/h`, `screen.w/h` | player count, this view's number (1..4), the game's view count `D_801450B8`, the view's rectangle, the game screen (480x360) |
| `hires`, `kh`, `ks`, `localView`, `state`, `frame` | hi-res mode and the game's two HUD scales; own view on; game state; frame counter |
| `health`, `health.raw`, `lives`, `weapon`, `kind` | health (displayed), the raw 1/256 value, lives, weapon index, character |
| `frags`, `frags.raw`, `deaths`, `suicides`, `count`, `team`, `active`, `squadScore`, `fraglimit` | from the score record and the rules |
| `ammo1..3`, `ammo1..3.shown`, `ammo1..3.x/y`, `ammo.type` | ammo counters, whether the game shows the slot, the slot movers, the weapon's ammo type (-1 = none) |
| `health.x/y`, `score.x/y`, `lives.x/y`, `count.x/y`, `squad.x/y` | the movers (frame units) |
| `score.state`, `score.frame`, `count.state/frame`, `squad.state/frame`, `alpha`, `lives.anim`, `lives.shown` | animation state |
| `hudShown`, `hideHud`, `hudFade`, `trialKind`, `trialCount`, `hideLives`, `teams`, `countRule`, `squads`, `markers` | settings and rules |
| `clock`, `clock.min`, `clock.sec`, `clock.shown`, `clock.raw` | seconds left, its parts, the game's show / blink rule |
| `crosshair.id`, `crosshair.frame`, `crosshair.on`, `crosshair.half`, `crosshair.size` | the game's crosshair |
| `flash.state`, `flash.alpha`, `hurt`, `boost`, `fx.flags`, `fx.time`, `zoom` | effects |

A new kind is a small class: derive from `HudElement`, read your fields in `Parse(JsonElement)`, draw with `Emit(HudContext)` (`c.Canvas.Quad / Fill`,
`SpriteElement.DrawSprite`, `NumberElement.DrawDigits`, `TextElement.DrawText`; `Place(c)` gives the screen position and the size of a frame unit), and add
one line to `HudLayout.Kinds`. A new value is one line in `HudValues.Read`.

## The default layout and its choices

`Content/Hud/hud.json` replaces the health, frags (with the animated skull and the team / marker variants), the trial and lives counters, the count and
squad scores, the three ammo counters and the clock's digits with port elements. The positions are the game's own formulas: `origin` plus the game's
offsets, with `kh` / `ks` so that the low-res mode works too. They use the game's digit and icon sprites, not the Peaceful Wars fonts: the original
look is those bitmaps, and a TrueType font would not match it.

The crosshair, the hurt flash, zoom, status and pips icons, team overlays and everything in the game's text font (names ticker, messages, the clock's
colon) are kept as the game draws them (`game` elements). They are stateful or projected effects whose exact behaviour lives in the game's code, and
`game` keeps them exact while still letting a layout hide or move them. A port crosshair (`kind: crosshair`) and the Peaceful Wars text are there
for layouts that want them. The file ends with three examples that are turned off (a health bar, a frag-limit line, a drawn crosshair).

## Verification

* `TRW_HUD_VERIFY=1`: the port's quads are compared with the game draws they replace, by texel hash, quarter-pixel box and tint, every 150 frames.
  1-player trial (Proving Grounds: trial counter, frags, health, ammo slide-in, the warhammer without ammo): 104 quads identical, 1 not (the skull's
  animation frame one ahead). 2-player split (the attract demo and a match, both halves, the clock): 340 identical, 4 not (one ammo slot in mid
  slide-in one frame ahead), and 575 of 575 in a second run. Those leftovers are the snapshot timing described above. Before the values were snapshotted
  and the corners truncated to quarter pixels, the differences were the skull's animation frame and 0.1 px heights in the halves.
* Screenshots side by side (window 1280x720, `TRW_SHOT_DIR`, the routes of docs/TRANSPILER.md): game and port HUD look the same in the 1-player trial and in both 2-player halves, including the clock.
* `tests/Trw.Game.Tests`: expressions, formats, colours, the shipped layout, error reporting, a new kind, player counts and conditions, anchors and
  origins in 1P / 2P halves / quadrants, the categories, and on a hand-built guest memory the section tags, the sprite lookup through the
  resource cache, the sprite decode and its quarter-pixel placement.
* Oracle: the headless runs never install the hook. `tools/oracle` boot (and the trial) stay bit-identical with `TRW_HUD=game`.

## Open points

* The movers' slide-ins follow the game only with an `origin`; a fixed `anchor` has no animation.
* The lives mode (no score HUD) uses `ks` for its mover position, the game uses 1; both are 1 in hi-res.
* 3 and 4 players and the team / count / squad modes use the same formulas but were not run (the scripted 4-player route ended in a 2-player match).
* An element that is moving can be one frame ahead of the game's draw (the snapshot is taken when the graphics task starts).
