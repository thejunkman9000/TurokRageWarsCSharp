# Models, objects and textures

All values big-endian. Status per claim: **[V]** verified (against loader code and/or by decoding every instance), **[H]** hypothesis.
Code: `tools/Trw.Tools/Models/` (`trw models <outDir> [rom] [--all]`). Reference functions are in the C port (`D:\Projects\TurokRageWars`).

## 1. Section container (generic) [V]
Used everywhere (ROM data and inside RNC chunks). Accessors in the game: `func_0028FD94` (section address), `func_0028FDA8`/`func_0028FE1C`/`func_0028FE4C` (+size), `func_0028FE08` (address from base+table).
```
u32 count
u32 offset[count + 1]     ; relative to the container start; offset[0] == header size, offset[count] == total length
u32 0                     ; pad, present only when needed: header size = ((count + 3) & ~1) * 4  (8-byte aligned)
sections...
```
(The earlier note in FORMATS.md used count+2 words, which is only right for even counts.) Sections nest: a section can be another container.
Confirmed on all chunks: counts 3/4/5/6/7 and the 507- and 347-entry tables below; the file `5F8720` has 169 sections, `7A09B0` has 46.

**Counted array** [V] (`func_0028FE98`, `func_0028FED0`): `u32 elementSize, u32 count, elements[count]`; length = 8 + size*count. An empty array is the 8-byte section `00000000 00000000` (very common, this is the 8-byte sections seen everywhere).

**Resource addressing** [V]: the game maps ROM into virtual address space with a region list (`D_80103F88`, demand paged); assets store *container offsets*, not ROM addresses. An object is found as `func_0028FE08(table, base, index)` = `base + table[index+1]`. RNC chunks are always leaves of such containers; `func_00285150` (RNC unwrap) is applied to a section by `func_002518DC` + a post-load callback (`func_0026AD8C` for models, `func_0025F58C` for animations, `func_0028795C` for level geometry cells).

## 2. Master ROM container [V]
Everything from ROM 0x50F768 is one container (count = 24 = the first word, header 0x68 bytes). Section ROM addresses = 0x50F768 + offset:

| # | ROM | length | content |
|--|--|--|--|
| 0 | 0x50F7D0 | 0xE8F50 | **object table**, 347 entries (section 3 below) |
| 1 | 0x5F8720 | 0x808 | RNC chunk, 169 sections of 40 (or 32) bytes (unknown) |
| 2 | 0x5F8F28 | 0x1C0 | RNC chunk, 2 sections (unknown) |
| 3 | 0x5F90E8 | 0x138D80 | **3D texture table**, 507 entries (section 4) |
| 4 | 0x731E68 | 0x2198 | unknown |
| 5 | 0x734000 | 0x34B98 | per-level record chunks (levels agent) |
| 6-15 | 0x768B98.. | small | misc RNC chunks (2-, 1-, 18-section etc.) |
| 16 | 0x769C98 | 0x29E08 | container of 69 (UI textures live here, see 6) |
| 17-23 | 0x793AA0.. | | misc; section 23 starts at 0x7A09B0 |

The 2.8 MB at 0x240000-0x50F000 is not containers/textures in these formats (probably audio, other agent).

## 3. Object table (master section 0) [V structure, H semantics]
347 entries, each a count-4 container:
- sec0: container(2): a 32-byte block of floats (c3c6390b ... : looks like bounds/extents [H]) and a counted/other block (8 bytes empty for static objects).
- sec1: container(n): model chunk(s) (RNC). n = 1 for all but three entries (n = 2, 3, 37).
- sec2: container(n) of **animation chunks** (RNC, count-7 containers, section 5), or the 8-byte empty section for static objects.
- sec3: 8 bytes, empty.
Resolved by `func_00246BD8` (actor spawn): `unkC4/unkD0 = sec0 address/size`, `unkCC = sec1`, `unkC8 = sec2`. Entries 0..256 are static objects (model has 3 sections), the other 88 skinned (6 sections); ROM order is model chunk then its animation chunks (e.g. 517F18 model, 518398 animation).

## 4. Model chunk (3 or 6 sections) [V]
Loader: `func_0026AD8C(handle, skinned, extra)`; output container has 3 (static) or 6 sections. Parsed and validated for all 386 model chunks (257 static `C3`, 129 skinned `C6`): every triangle references loaded vertex slots and the sum of G_VTX counts equals the vertex count.

| sec | content |
|--|--|
| 0 | counted array, elementSize 16: **N64 Vtx** `s16 x,y,z; u16 flag; s16 s,t; u8 r,g,b,a` (s,t are s10.5 texel coords) |
| 1 | counted array, always empty (8 bytes) in the ROM |
| 2 | container of *parts*; each part is a container(2): [0] **material** (24 bytes), [1] **compact display list** bytecode |
| 3,4 | (6-section only) empty 8-byte arrays, except in 3 chunks (555AC0, 5577B0, 5592F0) whose sec3 is non-empty (unknown) |
| 5 | (6-section only) counted array, elementSize 112, one record per bone: **hit volume** |

### Material (24 bytes) [V layout from `func_00285540`; field meaning H]
```
+0  s32 textureId     index into the 3D texture table (0..506), -1 = untextured  [V: range of all models is -1,0..506]
+4  u32 flags         seen: 0x9, 0x48, 0x72?, 0x848, 0x2120...; bit 0x40 changes how +8 is stored [V], rest unknown
+8  s16 field         stored doubled (|1 if flags&0x40)
+A  u8  field
+C  u8[4] colour1     RGBA tint (e.g. 808080ff, 893232ff)
+10 u8[4] colour2     RGBA
+14 u16 scaleS        G_TEXTURE s scale, 0x2000/0x4000/0x1000 ...
+16 u16 scaleT
```
UV: `u = s/32 * scaleS/65536 / texWidth`, `v = t/32 * scaleT/65536 / texHeight` [V statistically: the maximum u over a part clusters at integers 1,2,3,4 for 1000+ textured parts].

### Compact display list [V, from the expander in func_0026AD8C]
Byte stream per part, terminated by 0x00 (expander appends `G_ENDDL`). Commands (first byte b):
- `b & 0x80`: 2 bytes `u16 = 1aaaaabbbbbccccc` -> triangle of vertex-buffer slots a,b,c (F3DEX2 `G_TRI1`; two consecutive ones are merged into `G_TRI2`).
- `(b & 0x60) == 0x20`: 2 bytes, `n = u16 & 31 (0 = 32)`, `first slot = (u16 >> 5) & 31` -> `G_VTX` of the next n vertices of sec0 (a global cursor across *all* parts, starting at 0), segment-2 address.
- `(b & 0x60) == 0x40`: 2 bytes `40 m` -> `G_MTX 0xDA380003` with segment-1 address `m*64`: bone matrix index m (static models never use it).
- `(b & 0x60) == 0x60`: 5 bytes `6v sHi sLo tHi tLo` -> `G_MODIFYVTX` (0x02, offset 0x14 = `G_MWO_POINT_ST`): **replaces S,T of vertex-buffer slot v** with the given s16 pair (NOT a colour; earlier versions of this note were wrong). The value is the final RSP buffer value, i.e. it is not multiplied by the material's G_TEXTURE scale. See texture_mapping.md. `Model.Parse` now expands these into extra vertex copies (`Vertices[OriginalVertexCount..]`).
The expander's output (F3DEX2) is what the renderer ends up with; it can be reproduced from the decoded commands.

### Hit volume (112 bytes, skinned only) [V layout, H meaning]
8 corner points (24 floats), `f32 radius`, `u8 parent` (0xFF root; values 00,01,02,03,04,04,04 in the sample => parent bone, H), `u8`, `u16 0x0400`, `f32 1.0`, `u32 1`. Number of records equals the number of bones (max matrix index used + 1 or slightly more) [V on 129 models].

## 5. Animation chunk (count 7, 408 chunks) [layout V, decoding H]
Loader `func_0025F58C`, which copies sections 0-4 and 6 verbatim and rebuilds section 5. Observed (chunk 518398, model 517F18 with 8 bones):
| sec | content |
|--|--|
| 0 | 64 bytes `00000002 0000001c 0...` (small header, H) |
| 1 | counted array (4, 8): 8 pairs `u16,u16` = (0,0),(1,1),(2,2),(3,3),(4,4),(5,-1),(6,-1),(-1,-1): bone mapping table, one per bone [H] |
| 2 | counted array (0x14, 8): per bone `f32 x,y,z` + 8 bytes (packed rotation/rest pose, H) |
| 3 | container(2): small tables (H) |
| 4 | 8-byte empty array |
| 5 | container(3): [0] 0x20-byte header: `+4 u32 frameCount (0x1c)`, `+0xC f32 0.001`, `+0x10 u32 rotTrackCount (5)`, `+0x14 u32 posTrackCount (7)`; [1],[2] **bit-packed keyframe streams** (the loader expands them with `func_0025F0F4` using `func_002611A0 / func_0025EF54 / func_00261258` by compression type = header `+8`, into float vec3 / vec4(quaternion) per frame; quaternions are normalised by `func_00274214`) |
| 6 | 24 bytes: 6 floats, a bounding box of the animation (H) |
The stream decoders (`func_002611A0` etc.) are not yet analysed: **animation sampling is still unknown**.

## 6. Textures
### 6.1 3D / model textures: master section 3 (ROM 0x5F90E8), 507 RNC chunks [V]
Each chunk is a 3-section container:
- sec0, 8 bytes: `u8 format` (0 = 8 bpp CI8, 1 or 3 = 4 bpp CI4; the exact meaning of 1 vs 3 is unknown), `u8 p1` (1..7, H: animation speed), `u8 log2Width`, `u8 log2Height`, `0xFF`, `00 00 00`.
- sec1: container of N image frames, row-major, linear (no TMEM swizzle): size = w*h (8 bpp) or w*h/2 (4 bpp, **high nibble = left pixel**).
- sec2: container of palettes: 512 bytes (256 x RGBA5551) or 32 bytes (16 entries). Usually one per frame or one shared; palettes are the TLUT, the image holds indices.
All 507 decode (235 CI4, 272 CI8; size rule verified for all; 5 CI8 textures have only 16-entry palettes). Frames are animation frames (e.g. water, 8-36 frames) or palette variants [H]. Rendered samples (tex 0, 10, 20) look correct.
Palette colour: RGBA5551 (`rrrrrgggggbbbbba`), alpha bit used as 0/255.

### 6.2 UI / HUD textures: 55 RNC chunks at ROM 0x76DED8-0x793xxx (master section 16 region) [V]
```
u16 1, u16 id (300..3103), u32 0, u32 0x10, u32 0
u16 cols, u16 rows, u16 width, u16 height     ; at +0x10
tiles, cols*rows, row-major, nominal 32x32, edge tiles smaller:
  u32 w, u32 h, w*h x RGBA5551 colour, then (w*h+1)/2 bytes of 4-bit alpha (high nibble first)
```
Walking the tiles consumes every file exactly (all 55). Stitching tile (c,r) at (c*32, r*32) gives correct portraits/icons (checked 0300, 0401 "BOSS", 0507). The alpha plane is a clear circle/silhouette mask, so it is the alpha channel [V visually]. ids 904/905 are 284x222 full-screen images.

## 7. Unknown / not done
- Material flag bits (0x9, 0x48, 0x848, 0x2120...) and fields +8/+A; blend/cull/fog setup per flag.
- Skeleton hierarchy source for skinned models: matrices come from the animation (sec 1/2 of anim chunks); bone parent table is probably anim sec1.
- Animation stream decoding (section 5 of count-7 chunks).
- Object table sec0 contents, master sections 1, 2, 4, 6-15; which object index a level actor uses (levels agent).
- Count-4/5 chunks near 0x734278 etc. are level records (not models).
