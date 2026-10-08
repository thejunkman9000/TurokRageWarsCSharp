# Audio data (US v1.1) - working notes

Everything big-endian. **verified** = checked against the C port source and by decoding real data with `tools/Trw.Tools/Audio`;
**hypothesis** = plausible, not proven. Code: `tools/Trw.Tools/Audio/` (`AudioCommand.Run` = `trw audio [rom] [outdir]`).

## 1. Where the audio lives (verified)
The game reads one 0x20-byte table at **ROM 0x28B244** (`func_00265984(off)`: `func_002518DC(0, 0x28B244, 0x28B244, 0x20, ...)`, returns word at `off`).
It is 8 x u32 ROM offsets:

| word | ROM offset | meaning |
|---|---|---|
| 0 | 0x28B264 | start of the **bank block** (ALBankFile images, 0xE2AC bytes) |
| 1 | 0x299510 | end of bank block |
| 2 | 0x299510 | **sample region** start (VADPCM / wave data), also `D_8010B6A0` = DMA base for wave tables |
| 3 | 0x503CB8 | sample region end = start of the **sound/sequence root** |
| 4 | 0x503CB8 | root container start (`func_002576AC` loads `[0x10]..[0x14]`) |
| 5 | 0x50F768 | root end (0xBAB0 bytes, not compressed) |
| 6, 7 | 0x50F768, 0x7A0AE8 | not audio (asset archive with the RNC chunks, hypothesis) |

So the hypothesis "ROM 0x240000-0x500000 is sample data" is only partly right: samples are **0x299510-0x503CB8** (2.6 MB, uncompressed, no RNC).
0x240000-0x28B244 is not referenced by the audio loader (still unknown).
Sound manager init: `func_00257380` (ai/synth freq 0x5622 = 22050 Hz, alSndpNew 17 voices/0xC0 events, `alCSPNew` 20 voices/0xC0 events/20 channels).

## 2. Bank block @ 0x28B264 (verified)
Header: `u32 2; ptr bankFile0 (=0x800014); u32 0; ptr bankFile1 (=0x8038EC); u32 0x143340 ...`.
**Pointer convention:** every pointer is `0x00800000 + offset from block start` (pre-relocated N64 ALBankFile image). Offset 0 = NULL.

* **bankFile0** @ block+0x14: music instrument bank. rev 0x4231 ("B1"), 1 bank @ +0x3884: 21 instruments, `sampleRate` 44100, no percussion.
  Used by `alCSPSetBank(csp, s->unk8[1])` (the bank pointer). Music programs (MIDI program change n) = instrument n.
  Instrument 4 is a drum kit (13 sounds, keymap keyBase 48..72 step 2); instruments 12, 13 are looped sustained voices (10-13 sounds each).
* **bankFile1** @ block+0x38EC: SFX bank. 1 bank @ +0xE294 with exactly **1 instrument** whose `soundArray` has **294 ALSound** entries
  (the game's `unk7C[3]` = instArray[0]; `alSndpAllocate(player, instrument->soundArray[n])`, code reads `*(bank + n*4 + 0x10)`).
  `func_00257380` sorts the soundArray pointers ascending at init; they are already ascending in ROM (0 inversions), so
  **sound slot = file order**.

Standard libultra structs (all verified by parsing):
```
ALBankFile  { s16 revision(0x4231); s16 bankCount; ptr bank[] }
ALBank      { s16 instCount; u8 flags; u8 pad; s32 sampleRate; ptr percussion; ptr inst[] }
ALInstrument{ u8 vol,pan,priority,flags; u8 tremType,Rate,Depth,Delay; u8 vibType,Rate,Depth,Delay; s16 bendRange; s16 soundCount; ptr sound[] }
ALSound     { ptr envelope; ptr keyMap; ptr wavetable; u8 samplePan, sampleVolume, flags, pad }
ALEnvelope  { s32 attackTime, decayTime, releaseTime (us); u8 attackVol, decayVol; pad[2] }
ALKeyMap    { u8 velMin, velMax, keyMin, keyMax, keyBase; s8 detune; pad[2] }
ALWaveTable { s32 base; s32 len; u8 type (0=ADPCM,1=RAW16); u8 flags; pad[2]; ptr loop; ptr book (ADPCM only) }
ALADPCMloop { u32 start, end, count; s16 state[16] }  ALADPCMBook { s32 order; s32 npredictors; s16 book[order*npred*8] }
```
`wavetable.base` is a byte offset from the sample region start (ROM 0x299510). Observed: order 2, 4 predictors for every table.
All 353 distinct wave tables (294 SFX slots + 59 music) decode with no failure.

## 3. Sample decoding (verified plausible)
Standard VADPCM: 9-byte frame = header (scale = hi nibble, predictor = lo nibble) + 8 data bytes -> 16 samples; `Vadpcm.cs`
(order-2 codebook, vector 0 weights sample n-2, vector 1 weights n-1, plus intra-frame term, `acc>>11`, clamp).
Checks: `len` always a multiple of 9; predictor indices always < 4; scale <= 12; RMS shows silent->decay shape for percussive sounds
(e.g. slot 3: rms 143 133 79 39 14 9 6 2 /100), looped sustained instruments hold level, loop end ~ sample count (loop 6716..23939 of 23968),
clipped samples 0-10 per file (noise bursts a few hundred max in long sustained loops). Slots 1 and 200 are noise-like (high diff energy) - plausible noise effects.
**Not yet listened to** - no ear in the loop; sample **rate** is ambiguous: bank says 44100 Hz; the AI/synth runs at 22050 and sndp pitch defaults to 1.0
(ratio relative to output rate). WAVs are written at 44100; if they sound an octave low/long, use 22050 (hypothesis).
SFX slots 28-66, 69, 71, 84, 95, 101, 118-124, 134, 137, 143-145 ... share a 32-sample silent wavetable (base 0x1781F0) = unused placeholders.

## 4. Root container @ 0x503CB8 (verified structure)
Uses the generic section container (`u32 count; u32 offset[count+1]` relative to the container; `func_0028FD94(tbl, idx) = tbl + tbl[idx+1]`).
```
root (2 sections)
 [0] sound tables s3 (6 sections, loaded by func_002576AC)
      [0] keys    : 8-byte header, then u32 key[N] sorted ascending (N = 245)  -> game sound ids (195 distinct; duplicates = random variants)
      [1] weights : 8-byte header, then s32 weight[N] (chooser uses weight*10; all 100 seen)
      [2] tree    : container of N entries; each entry = container(2): [0] small header blob, [1] {s32 stride; s32 count; byte rec[count][stride]}
      [3],[4],[5] : three more 8-byte-header tables (tab70/74/78, purpose unknown)
 [1] music (2 sections)
      [0] key table: 8-byte header (word 1 = count 9), then u32 songKey[9]  (50 52 56 57 58 59 60 61 65), sorted
      [1] container of 2*9 sections: [2i] = {s32 volume (60); u16 tempo-float-hi (0x3F80=1.0, 0x4020=2.5) ...}, [2i+1] = compact sequence
```
**Sound effect selection** (`func_00257DF4` / `func_0025828C`): game id -> binary search in `keys` (`func_00265570` finds the equal-key range lo..hi, picks one by
weight, avoids repeating the last variant) -> tree entry -> for each of its `count` records call `func_00257BD4` (a SndInfo, `stride` bytes, typically 0x16):
`s16 w0` = **soundArray slot** (verified, max 293), `s16 w2` = start delay (frames), `s8 b4` = priority, `u16 bits6` (bits 14-15 / low 14 = flags), `s8 bE` etc.
Ids < 0x100 are the "speech/limited-concurrency" class (max 6 identical). Ids >= 0x100 normal. `sfx_ids.tsv` lists key -> slot/delay/priority for all 245 entries
(`trw audio` writes it). Direct tree index (`func_0025828C`) also exists.
Remaining SndInfo fields (pitch/volume/pan) are not decoded yet (hypothesis: w8/wA/wC/w10-w14 = volume/pitch variance, see `SndInfo` in include/b13_types.h).

## 5. Sequences (verified structure, converted to MIDI)
Each song = libultra **compact sequence** (ALCSeq), as consumed by `alCSeqNew`:
```
u32 trackOffset[16]   ; relative to sequence start, 0 = unused
u32 division          ; ticks per quarter (480 in all 9 songs)
track data: <varlen delta> <event> ...
```
Event bytes (`__alCSeqGetTrackEvent`, `__getTrackByte`, `__readVarLen`; all reimplemented in `CSeq.cs`):
* `FF 51 tt tt tt` tempo (us/quarter); `FF 2F` end of track; `FF 2E cnt x` loop start; `FF 2D cnt counter off32` loop end (6 raw bytes, off32 = backward jump distance; 0xFF count = infinite).
* channel messages use running status; `Cn`/`Dn` have 1 data byte, everything else 2; **`9n kk vv` is followed by a varlen note duration** (no note-off events).
* Byte `0xFE` is a back-reference: `FE FE` = literal 0xFE; `FE hi lo len` copies `len` bytes starting at `(pos_after_code - (hi*256+lo) - 4)` (LZ-style, bytes fetched raw).
Songs: 9, 5-16 tracks, 44-3321 notes, tempos 81-170 bpm, `song_<key>.cseq` raw + `.mid` (format 1, conductor track + one track per sequence track; note-offs synthesised, loops become text markers `loopStart xN` / `loopEnd xN`).
MIDI program number = instrument index in bankFile0's bank (0..20). The player is `alCSP` with 20 channels (channels 16-19 cannot be expressed in the 16 tracks).
Song selection: `func_0025DA30(seq, key)` binary-searches the song key table, sets volume (`unk24`) and tempo scale (`unk30`, float from the high half), starts `alCSeqNew`+`alCSPSetSeq`. Mapping of level -> song key not traced yet.
Song 57 reports 568k ticks (suspiciously long final delay); not investigated.

## 6. Unknown / next
* Listening test of WAVs and MIDI; confirm 22050 vs 44100 playback rate and correct pitch scale (keyBase 41 for SFX).
* Remaining SndInfo fields, tables [3..5], and what game code passes as sound ids per event.
* Which level / menu uses each song key (callers of `func_0025DA30`).
* Envelope/tremolo/vibrato (ALSynth effects) not needed for plain sample playback but needed for faithful music rendering.
