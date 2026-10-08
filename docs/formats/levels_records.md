# Resource index, level records and global tables

Everything big-endian. **[V]** = verified (against the game code in `D:\Projects\TurokRageWars` and/or by checking all 37 levels / all
2050 chunks with the C# parser), **[H]** = hypothesis. Code: `tools/Trw.Tools/Levels/` (`trw levels [rom] [outdir]`).

## 1. How the game finds assets  [V]

### ROMLookupTable (ROM 0x28B244, 8 words, read by func_00265984(idx) = `table[idx/4]`)
| word | value | meaning |
|---|---|---|
| 0,1 | 0x28B264 .. 0x299510 | region 0: audio ctl bank (N64 `ALBankFile`-like, 58,028 bytes) |
| 2,3 | 0x299510 .. 0x503CB8 | region 1: audio sample data (2.5 MB; this is the "high entropy, no RNC" area) |
| 4,5 | 0x503CB8 .. 0x50F768 | region 2: container(2) (offsets 0x10, 0x42B0, 0xBAB0) - sequence/audio side data (audio agent) |
| 6,7 | 0x50F768 .. 0x7A0AE8 | region 3: **main data archive** (everything else; all 2050 RNC chunks live here) |

Callers: func_00257380 (regions 0,1 and 2), func_002576AC (region 2), func_00291404 (region 3 start `& 0x3FFFFFFF` -> scene+0x3C4).
The game never embeds ROM offsets in code: everything is `archive base + offset` found through nested containers.

### Generic loader
`func_002518DC(?, romAddr, romAddr, size, flags, user, callback, "label", keep)` = cached ROM read keyed by ROM address
(DMA through the PI thread, func_0025651C). Flag 4/8/0x10/0x1B select "unpack with RNC and run callback". The `label` strings give every
resource a name (see below). `func_00285150(handle, keep)` = RNC unpack wrapper (RNC1 -> rncUnpackMethod1, header byte 3 == 2 -> method 2).
`func_00254224(.., &hdr, romAddr, label)` reads the first u32 (the container count) then the header
(`offset[0]` bytes) of an *uncompressed ROM container*; `func_002543A8(.., hdr, sectionIdx, romBase, user, callback, label)` then loads
section `sectionIdx` (`romBase + hdr[sectionIdx+1]`, size = difference of consecutive offsets) = `func_0028FE1C`.

### Container layout  [V]
`u32 count; u32 offset[count+1]; (pad to 8)`; first offset = `align8(4*(count+2))`, last offset = total size. Helpers:
func_0028FD94(tbl, i) = `tbl + tbl[i+1]`, func_0028FDA8 (+size), func_0028FE08 (`base + tbl[i+1]`), func_0028FE1C (rom addr + size).
**Same layout for the ROM-level (uncompressed) containers and for the *contents* of RNC chunks.** (FORMATS.md says "u32 0 padding": it is
just alignment to 8.)

### Typed array  [V]
`u32 stride; u32 count; stride*count bytes` (func_0028FE98 = stride*count+8; padded to 8 in files). Many arrays are stored **column-major**
(record i, byte j = `data[i + j*count]`) and de-transposed at load by **func_0028FEEC** (this is why the data looks like byte planes
`FFFFFFFF FFFFFFFF ...`). Transposed in files: sector arrays 0..3, level part 3, part-1 sub-arrays 1/2/3. Plain: parts 6, 9, 10, 11 and
grid arrays.

### The main archive tree (ROM 0x50F768): 24 sections  [V]
The whole tree (5118 nodes: 1360 containers, 2050 RNC leaves, 1708 raw leaves) parses with the container rule; every RNC chunk has a path
(`chunks.tsv` written by `trw levels`, column `path`, e.g. `5.12.5.3`).

| sec | ROM | content | status |
|---|---|---|---|
| 0 | 0x50F7D0 | **347 static objects**, each container(4): [0] container(2) of two 8-byte arrays, [1] **model list** (container of 1..N RNC "stat model" chunks; one is picked as `index % count`), [2] container or 8 bytes, [3] "static events" array (stride 0x1C, 8 bytes = empty in most) | V: func_002505CC/00250754, labels `stat object index`, `stat model`, `static events` |
| 1 | 0x5F8720 | one RNC chunk -> container(169) of 40-byte records (`00000000 00000139 44000000 ...`) | structure V, meaning H ("bllevel data index", label `CScene__GetBLLevelData`) |
| 2 | 0x5F8F28 | RNC -> typed array stride 2 x 347 (u16 `0x0007` mostly): per-static-object attribute | H (count = sec 0) |
| 3 | 0x5F90E8 | **507 RNC chunks** (= 3 x 169), each a model-like container(3) `{0x18,0x20,...}` | structure V; owner agent: models |
| 4 | 0x731E68 | RNC -> container(3): raw 31048 B, u32[224], 60-byte records x 224 | H (224 object-type definitions?) |
| 5 | 0x734000 | **LEVEL DIRECTORY**: 37 levels x container(13) | V (see 2) |
| 6 | 0x768B98 | RNC -> container(2): [37 x container(3) of 32-byte blocks (first word 0x0B/0x08/.. in sub 0)], [empty] | H per-level data (37) |
| 7 | 0x768CE0 | RNC -> container(1): 37 x container(1) of one 32-byte block | H per-level (37) |
| 8 | 0x768DB8 | RNC -> container(2): u32[321] (0xBC2,0xBCC,...) + 321 x 20-byte records (f32 x,y,z; u32; u16,u16) | H (321 ids/positions) |
| 9,10,20 | | 8-byte empty stubs | V |
| 11 | 0x769860 | RNC -> container(18): variable 120/72-byte blobs (floats + colour bytes) | H (effect / light defs) |
| 12 | 0x769A48 | typed array 22 x 8 | H (colour pairs) |
| 13 | 0x769AC8 | typed array 36 x 24 (floats 0.3, 0xFFFFFFFF ...) | H |
| 14,15 | | typed array 24 x u32 each | H (index tables) |
| 16 | 0x769C98 | **69 RNC blobs** (5640, 36728, ... bytes), header `00400001 00000000 offsets..` | H (unknown; animation/effect streams?) |
| 17 | 0x793AA0 | u32[69] ascending (1,2,4,0x67,0x12C...) | H (id table for sec 16) |
| 18 | 0x793B38 | item 0: **level meta table 37 x 76 B**; items 1..37: container(4) per level (RNC with float arrays; first holds container(5) of 12-byte/4-byte float arrays) | meta layout V, rest H |
| 19 | | u32[1] = 1 | |
| 21 | 0x79CC78 | 36 RNC blobs of 3382 B (`00000000 010004C3 04C80000 ...`) | H (one per level except scrap) |
| 22 | 0x7A0988 | one 16-byte record "NULL" | |
| 23 | 0x7A09B0 | RNC -> container(46) of small 10-byte/32-byte arrays (`00000004 00000003 0000001E`) | H |

Scene object (`D_8011FE88`, "CScene") keeps pairs *(archive-section ROM base, header pointer)*: (+0x1C,+0x4C) = section 5 [V]; others
`(+0x20,+0x50)` = "Artsend Texture" (func_00294CA0), `(+0x24,+0x54)` = anim-object index (func_002627C0, Cinema func_0041C67C),
`(+0x28,+0x58)` = "binary block" (func_0028BDE4), `(+0x2C,+0x5C)` = "graphicset" (func_0028BE88), `(+0x34,+0x74)` = "Cinema" (func_004009F4).
The mapping of these pairs to sections 6..11 is **[H]** (consecutive from section 5 upward); `unk0C/unk10` = base/size of section 1
(func_0028D450). Loader labels seen: `stat object index`, `stat model`, `static events`, `simp object index`, `simp part object index`,
`anim object index`, `anim model index`, `animation`, `header`, `grid section`, `binary block`, `graphicset`,
`CScene__GetBLLevelData ...`, `level index`, `Artsend Texture`, `Cinema` (Turok 2 engine terminology: stat = static, simp = simple, anim = animated).

### The 429 "non-conforming" chunks  [V]
They are the **level parts** (contents of section 5 children) and their leading words are the typed-array `stride`:
`0` = empty 8-byte array (part 2 / sub-arrays), `40` = part 3 (stride 0x28), `44` = part 6 (stride 0x2C), `16` = part 9 (nav nodes),
`6` = part 10 (nav edges), `1` = part 11 (matrix), `1` also level sub-blocks. 0x5F8720 and 0x5F8F28 are archive sections 1 and 2.

## 2. Levels  [V unless noted]

Level directory = archive section 5 (ROM 0x734000, 37 children, header read as 0xA0 bytes). `func_00286A78(scene, levelId, flag)` loads
level *L* (clamps `L >= *hdr` to count-1): `rom = func_0028FE08(scene+0x4C, scene+0x1C, L)`; header via func_00254224; each part by
func_002543A8. The level id is the index (0..36). Names are in part 8; menu order and map file names in section 18 item 0.

| id | name | id | name | id | name | id | name |
|---|---|---|---|---|---|---|---|
| 0 | PROVING GROUNDS | 10 | WATER TEMPLE | 20 | WICKED DANCE | 30 | THE PEDESTAL |
| 1 | BASIC TRAINING | 11 | BOMB SHELTER | 21 | BUNKER | 31 | DEFCON 5 |
| 2 | CROSSROADS | 12 | TEMPERED | 22 | AMPHITHEATER | 32 | SPIRIT TEMPLE |
| 3 | GRIM RETREAT | 13 | EARTH TEMPLE | 23 | FIRE WALKER | 33 | STRONGHOLD |
| 4 | CATHEDRAL | 14 | FIRE TEMPLE | 24 | CRYPTS | 34 | COURTYARD |
| 5 | FALLOUT | 15 | MYSTIC | 25 | BREAKDOWN | 35 | STARLIGHT |
| 6 | BOXED SET | 16 | BOTTLENECK | 26 | RETALIATION | 36 | 37_scrap (cinema) |
| 7 | DIRE STRAIGHT | 17 | TIGHTROPE | 27 | HOPELESS | | |
| 8 | PREY | 18 | THE PIT | 28 | MATRIX | | |
| 9 | WAREHOUSE | 19 | LEAP OF FAITH | 29 | FATHOM | | |

Level ROM offsets are in `resource_index.txt` (level 0 = 0x7340A0, each 5-30 KB). Level 5 FALLOUT / 12 TEMPERED / ... appear in a different
order in the menu: section 18 item 0 word 0 = level id, array position = menu slot.

### Level container(13) parts
| part | storage | format | status |
|---|---|---|---|
| 0 | raw 24 B | 6 words. `f32 25.6 (all levels)`, `u32 FFFFFFFF`, `u32 FFFFFFFF`, `FF 38 38 38`, `u8 flags (bit2 -> D_800D15D0; 00/04) + 3 colour bytes B0 39 BC`, `0`. Copied to scene+0x1B2B0 | loader V; fields H (draw distance/fog + colours) |
| 1 | RNC -> container(4) | callback func_0044D024. [0] 112-byte header (`00000064 00000001 000000FF ...`, floats 2816, 75, 1536 ...); [1] array stride 16 x 3-4 (colour bytes, column-major, copied raw to runtime); [2] array stride 0x14 (column-major) -> 0x20-byte runtime nodes via func_00275B14; [3] array stride 0x14: bounding rect `f32 x0,z0?,x1,z1?` + u32 flag | loader V, semantics H (environment/zones) |
| 2 | RNC -> `{stride 0, count 8}` | empty in all 37 levels (callback func_0044E6A4) | V |
| 3 | RNC -> array stride 0x28, column-major | callback func_0044D408 -> 0x2E8-byte entities (scene+0xD0/0x138). Record: `+0 s32 -1`, `+4/8/C f32 x,y,z`, `+10/14/18 f32 scale (0.5)`, `+1C u32 (bytes 01 12 06 44 / 00 AE 04 60)`, `+20 u32 type (0x4F, 0x8F..0x96)`, `+24 u32 flags (0x1FF/0x4FF)`. Level 0 has 13 | layout V, meaning H (placed actors / pickups) |
| 4 | RNC -> container(5) | **grid description** (func_00288908): [0] f32 per-column x-min, [1] per-column x-max, [2] per-row z-min, [3] per-row z-max (arrays stride 4, count = columns / rows), [4] cells: stride 0x14 = `f32 xMin, zMin, xMax, zMax; u32 flag` (flag 0 = a sector chunk exists, 1 = none). Cell index = col + row*columns. `±inf` appear in level 35 | V (level 0: 2x2 grid, level 35: 6x6) |
| 5 | **ROM container** of N RNC chunks | one **sector** per cell with flag 0, `func_0028FE1C(hdr(scene+0x64), base(scene+0x30), cellIndex)`, label `grid section`, callback func_0028795C (see 3) | V |
| 6 | RNC -> array stride 44 x 16 | -> scene+0x1B300 (data), +0x1B304 (count). Record (11 words): `u32 FFFF0000`, `u32 00FFFFFF`, `f32 1,1,1`, `f32 286.2`, `f32 61.4`, `f32 0.5`, `f32 0.0667`, `f32 0.0667`, `u32 80010000` | loader V; semantics H (light/emitter parameter sets) |
| 7 | RNC -> container(4) | callback func_0044CD58; four 0x14-stride arrays (mostly empty) with flag bytes at +0xE/+0xF/+0x13 and a per-level bitmask check | V loader, H meaning (moving/triggered objects) |
| 8 | raw 8..16 B | ASCII level name, NUL padded | V |
| 9 | RNC -> array stride 16 | **nav nodes**: `f32 x,y,z; u16 flags (hi); u16 objectType (lo)`; count 2..100 (level 0: 41). Scene+0x1B4DC/0x1B4E0; each node spawns a 0x50-byte runtime object (func_0020CA10). Coordinates lie inside the level extents | V layout; H meaning (spawn / pickup / AI waypoints). objectType values repeat across levels (0x6B9..0x6BB, 0x84B.., 0xBC4..) |
| 10 | RNC -> array stride 6 | **nav edges**: `u16 from, u16 to (< node count, checked in all levels)`, `u8 type (0..11, seen 1,2,3,4,5,6,7,8,9,10,11), u8 0`. Scene+0x1B4E4/E8 | V layout, H meaning |
| 11 | RNC -> array stride 1, count = nodeCount^2 | N x N byte matrix of 0/1 (reachability/visibility between nodes), row-major. Scene+0x1B4EC/F0 | V size; H meaning |
| 12 | container(1) of a 24-byte raw block | six u32 copied to scene+0x1B910..0x1B924 (box?); flag scene+0x1B92C = (+0x10 or +0x14 non-zero). Only loaded when header count >= 13; all-zero in level 0 | V loader |

### 3. Sector (grid cell) chunk: RNC -> container(5)  [V loader, H semantics]
Consumed by func_0028795C ("grid section"). All four arrays are column-major (de-transposed with func_0028FEEC):
| sec | stride | runtime | fields |
|---|---|---|---|
| 0 | 0x1C | 0xE8-byte objects (func_0024F940) | `u16 objectId at +8` (index into archive section 0, max 319 < 347, V); rest H (compact placement: f32 0.35 at +4, s16 position bytes ...) |
| 1 | 0x34 | 0xE8-byte objects (func_0024FD4C) | `f32 x,y,z at +4/+8/+0xC` (V: e.g. -2048,153.6,-1024), f32 scale x3 at +0x10..0x18 (0.2625,0.35,0.2625), `u16 objectId at +0x28` (V) |
| 2 | 0x20 | 0x1C8-byte objects (func_0024E980) | `objectId at +0x14`; empty in all shipped sectors |
| 3 | 0x4C | 0xC0-byte records (func_00276D20, then func_0028994C against the cell) | H collision/surface records (5 per cell in level 0) |
| 4 | - | copied verbatim | `{0,8}` empty in level 0 |
Sector arrays are *small*: 43..175 stride-0x1C records per cell, 0..4 stride-0x34, 2..5 stride-0x4C. Some cells are an 8-byte empty
placeholder chunk (`00000000 00000008`; 64 in level 22 AMPHITHEATER (9x9 cells), 26 in level 35, 1 each in levels 18 and 26) - not containers,
nothing to load. [V]

### 4. Section 18 item 0 - level meta, 37 x 76 bytes  [V layout]
`u32 levelId`, `u32 value4` (1..13: probably player count/difficulty bucket), `u32 value8` (0x259+n = 601+n: sound/track id; loader does
`func_4037E8((id-0xBC2)/10 + 0x259)` for the music cue), `u32 8`, `u32 0x01000000`, `u32 0x9F090000`, `char mapName[32]` at +24 (original
dev map file name with stale bytes after the NUL, e.g. `Map01_MH-BL-2PL-EASY`), `u32 0`, `f32 2.2` at +60, zeros, `u16 0xFF00` at +74.
Array position = menu slot.

## 4. What is unknown / suggested next steps
* Meaning of level parts 1, 3, 6, 7 (fields), sector section 3 (0x4C records), sector section 0 compact placements.
* Mapping of archive sections 6..23 to engine subsystems (read callers of the CScene base/hdr pairs; the CScene init that fills `+0x08..+0x7C`
  lives in overlay code, not yet located).
* Game modes / weapons / enemy definition tables are not in a single obvious chunk: candidates are section 4 (224 x 60 B), section 1
  (169 x 40 B), section 8 (321 x 20 B), section 23 (46 small blocks) and section 11 (18 blobs). Region 2 (container(2) at 0x503CB8) is the
  audio sequence side.
* The data are not in `rodata`: no ROM-offset tables exist besides the container headers and ROMLookupTable.
