# ROM data formats (working notes)

Everything big-endian. Findings are marked **verified** (checked in code/tests) or **hypothesis**.

## RNC1 containers - verified
- 2050 chunks tagged `RNC\x01`, ROM 0x50FD90 to 0x7A09B0. Implemented in `tools/Trw.Tools/Rnc.cs`; every chunk unpacks and
  matches the CRC16 in its header. `trw scan` re-checks this.
- Header (18 bytes): magic, unpacked size u32, packed size u32, unpacked CRC16, packed CRC16, leeway u8, chunk count u8.
- Gotchas found while porting: literal bytes are read from the byte stream *at the current word pointer* (not +2), after which the
  bit buffer's top word is reloaded from the new pointer.
- Unpacked total: 5,457,832 bytes. Median chunk 1,120 bytes, largest 160,368.

## Section container inside chunks - verified (corrected header size: 8-byte aligned, not always count+2 words)
```
u32 count
u32 offset[count + 1]     ; first = header size, last = total chunk length, non-decreasing
u32 0 (and one more pad word if needed) ; header size = ((count + 3) & ~1) * 4, i.e. rounded up to 8 bytes
sections...
```
Chunks by section count: 3 -> 765, 4 -> 74, 5 -> 240, 6 -> 129, 7 -> 408 (plus a few outliers: 1, 2, 18, 46).
Meaning: the master container's object table entries are count-4 containers (extents, model chunks, animation chunks); count-3 chunks are
static meshes, count-6 skinned meshes (see docs/formats/models_textures.md). Other counts are handled by the levels notes.

## Other chunk kinds - hypothesis
(Resolved: they are the 37 levels' parts; the leading words are array strides.) The 429 non-conforming chunks sit in repeating groups near the end of the ROM (0x7340F8 onward), with leading words 0, 40, 44, 16, 6, 1
and fixed sizes (e.g. 712 bytes for the 44 kind, 8 bytes for the 0 kind). They look like per-level record sets (level header, actor
placement, trigger/path data), where the leading word is a record or field count rather than a section count.

## Not RNC
ROM 0x299510-0x503CB8 is raw VADPCM sample data (see audio.md). ROM 0x100902 onwards has repeated `TYP` strings (purpose unknown).

## Next questions
1. Animation keyframe streams and skeletons (func_0025F0F4 and its stream decoders).
2. How levels reference objects and geometry cells (levels agent).
3. Material flag bits.

## Detailed format notes (written by the analysis agents; verified unless marked otherwise)
- `docs/formats/models_textures.md`: master container at ROM 0x50F768 (24 sections, 347-entry object table, 507-entry 3D texture table),
  static (257) and skinned (129) meshes with compact display-list bytecode, CI4/CI8 textures with RGBA5551 palettes, tiled UI textures.
  Not yet decoded: animation keyframe streams, skeleton/bone matrices, material flag bits.
- `docs/formats/audio.md`: layout table at ROM 0x28B244, two ALBankFiles (music 21 instruments, SFX 294 sounds), VADPCM samples at
  0x299510-0x503CB8 (the earlier "0x240000-0x500000 unknown" hypothesis was mostly right: samples start at 0x299510), 9 compact
  sequences with 480 ticks per quarter.
- `docs/formats/levels_records.md`: ROM lookup table at 0x28B244 (4 regions), main archive at 0x50F768 (24 sections, 5118 container nodes,
  every RNC chunk assigned a role in `extracted/levels/chunks.tsv`), 37-level directory (section 5, 13 parts per level, names and map files),
  column-major typed arrays, sector grid, nav nodes/edges, level meta. Not decoded: gameplay fields in level parts 1/3/6/7, weapon/enemy/mode
  definition tables (candidates: archive sections 1, 4, 8, 11, 23).

## Converter status
`trw scan | extract | models | levels | levelgeo | anim | defs | audio` (see Program.cs). Verified output: 2050 RNC chunks, 507 3D textures + 55 UI textures as PNG,
386 models as OBJ+MTL, 294 sound effects as WAV, 9 songs as MIDI.

Further notes: `docs/formats/animation.md` (skeleton from hit-volume records, rigid one-bone-per-vertex binding, 408 clips with bit-packed
quaternion/position tracks), `docs/formats/gameplay_defs.md` (archive section names, 169 intelligence records, pickups, spawns, particles,
bot weapon timing, name tables; weapon damage/ammo and enemy health are NOT in data tables, they are overlay-A code constants),
`docs/formats/level_geometry.md` (levels are grids of sectors holding 10,169 static-object placements; collision is one AABB per
placement plus a coarse ground-triangle set; sector array 3 is lighting).
