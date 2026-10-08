# Skeletons and animation

All values big-endian unless noted. **[V]** = verified (against the C port code and/or by decoding all instances), **[H]** = hypothesis.
Code: `tools/Trw.Tools/Anim/` (`AnimClip.cs` parser, `TrackDecoder.cs` stream decoders, `Pose.cs` matrices/skinning, `AnimCommand.cs` dumper).
Reference functions (C port, `D:\Projects\TurokRageWars`): loader `func_0025F58C`, stream expander `func_0025F0F4`, decoders `func_002611A0`/`func_0025FFD0`/`func_0025FD94`/`func_00260158` (cubic), `func_0025EF54` (linear), `func_00261258` (raw), bit reader `func_002607D8`, per-frame setup `func_00261EB8`, pose loop `func_002484A0` (bone loop around line 400 of its m2c output), advance `func_0026195C`, events `func_002476B8`, matrix concat `func_0026F690`.

## 1. Where things are [V]
- Object table entry (count 4): `sec1` = model chunk(s), `sec2` = container of **animation chunks** (RNC, count 7). 90 of the 347 objects have clips (408 clips total, 1 to 44 per object, 1-66 frames each).
- **Animation id = index in the object's `sec2` container** (`func_00261690` resolves id -> chunk with `func_0028FE1C(table, base, id)`). Object 174 (the player model, 22 bones) has 44 clips, 176 has 26.
- The skeleton itself is **not** stored in the animation chunk: the hierarchy comes from the model chunk's hit-volume array (model section 5, 112-byte records, one per bone): `u8 parent` at +0x64 (0xFF = root, always parent index < bone index, so one forward pass works), `u8 id` at +0x65 (gameplay body-part flags, e.g. 192, 71, 135 on the player; 0 elsewhere). Number of bones == `BoneCount` of every clip of the object [V, all 90 objects].
- Objects with several model chunks (174 has 37, weapon/LOD variants) share the one skeleton; the first chunk matches the clip bone count.

## 2. Animation chunk (count-7 RNC chunk) [V layout]
Note: counted arrays here are padded to 8 bytes, so `8 + size*count` may be 4 bytes shorter than the section.

| sec | content |
|--|--|
| 0 | counted array (elemSize 2, count = frames): per-frame `s16` heading angle, scaled by `9.5877e-5` (= 2pi/65536), used by `func_0026195C` (angular blend `func_00273F54`) to return a per-tick **turn delta**. All zero in all 408 clips (no turn-in-place data in the ROM) [V code, V all-zero] |
| 1 | counted array (elemSize 4): per bone `s16 posTrack, s16 rotTrack`; -1 = no track, use the rest pose [V `func_0025E5D0`, `func_00260570`] |
| 2 | counted array (elemSize 20): per bone rest pose `f32 x,y,z; s16 qx,qy,qz,qw` (quaternion = s16 / 32768, scale constant `D_800C925C` = 3.0518e-5). Equals frame 0 of the clip's tracks (246 of 247 tracked bones within 0.5 units) [V] |
| 3 | container(2): [0] 16-byte clip info (below), [1] 8-byte empty array (unknown) |
| 4 | counted array (elemSize 28): **events** (section 7) |
| 5 | container(3): [0] 32-byte header, [1] position streams, [2] rotation streams (section 3) |
| 6 | 6 floats, bounding box (min xyz, max xyz) of the animation [H] |

**Clip info (sec3[0], 16 bytes)**: `u16 +0` blend/transition length in ticks (copied to actor `+0x12C`, 0 or 1 in samples) [H], `u16 +2` **loop-restart frame** (`func_0026195C`: when the clip passes the last frame the position wraps to this frame; 0xFFFF = 0; value `frames-1` means "play once and hold the last frame" [V code]; in the ROM: 223 clips loop to 0, 182 hold the last frame, 2 mid, 1 none), `u8 +5`, `u8 +7` copied to actor `+0x138/+0x139` [H unknown meaning], `f32 +8` **playback rate = 15.0 in all 408 clips**; the actor advances `frame += dt * rate * (1/15)` per tick (`D_800C92B0 = 0.0666667`, `dt` = global tick scale `D_800D2988`) [V code], so the nominal speed is 15 frames per `dt` unit.

**Header (sec5[0], 32 bytes)**: `+0` u32 smoothing radius (0 in all clips) [V], `+4` u32 **frame count**, `+8` u32 compression type (0 cubic = 405 clips, 2 raw float = 3 clips, 1 never used), `+0xC` f32 0.001 (unused by the decoders in practice), `+0x10` u32 rotation track count, `+0x14` u32 position track count, `+0x18/+0x1C` junk in the ROM (the loader overwrites them with float frame count and 1.0).

## 3. Keyframe streams [V: all 408 clips decode, both streams consumed to within 16 bytes of padding]
`func_0025F0F4` expands streams into float arrays of `frames` vectors; the same code is the reference for the C#.
- Position streams (sec5[1]): `posTrackCount` tracks x **3 components** (x, y, z), ranges stored as raw 32-bit floats.
- Rotation streams (sec5[2]): `rotTrackCount` tracks x **4 components** (quaternion x,y,z,w), ranges stored as 6-bit values mapped to [-1, 1]. After decoding: optional circular box filter of radius `smooth` (applied in place, only if > 0), then every quaternion is normalised (`func_00274214`). Raw quaternion norm error before normalisation is below 0.35 (11 clips above 0.1; cubic overshoot/quantisation).
- Bit order: bits are read **LSB first from big-endian 32-bit words** (`func_002607D8`) = a little-endian bit stream over the byte-swapped data. Streams are not byte aligned between components.
- Per component (in order track 0 comp 0..n-1, track 1 ...): `skip 31 bits` (constant filler, always the same value in a stream), then the stream of the compression type:

**Type 0, cubic segments** (`func_002611A0` / `func_0025FFD0` / `func_0025FD94` / `func_00260158`):
```
nseg = bits(6)
position:  min = f32(32 bits), max = f32(32 bits)         range = max - min
rotation:  a = bits(6)/63, b = bits(6)/63; min = lerp(-1,1,a), max = lerp(-1,1,b)
nbits = bits(6)                        ; width of every control value
repeat nseg times:
    span = bits(6)                     ; frames covered by this segment (sums to frameCount)
    v1,v2,v3,v4 = bits(nbits) x4
    sc  = range / (2^nbits - 1)        ; nbits == 0: value is the constant min + range/2
    inv = span >= 2 ? 1/(span-1) : 1
    c0 = v1*sc + min
    c1 = (32*v2 - 21*v1 - 12*v3 + v4)/3 * inv*sc
    c2 = (14*v1 - 32*v2 + 20*v3 - 2*v4) * inv*inv*sc
    c3 = (64*v2 - 24*v1 - 48*v3 + 8*v4)/3 * inv^3*sc
    value(frame = start + d) = c0 + c1*d + c2*d^2 + c3*d^3      for d = 0..span-1
```
Constants: `D_800C9220 = 1/3`, `D_800C9224 = 0.5`, `D_800C9230/34 = 1.0`. Segment boundaries are frame-aligned; the polynomial is evaluated only at integer frames. **Type 1** (`func_0025EF54`): range as above, `nbits = bits(6)`, then `frames` values of `nbits` bits, `v/(2^nbits-1)*range + min` (never occurs in the ROM, implemented from the code). **Type 2** (`func_00261258`): `frames` raw 32-bit floats (3 clips, object 174).
The decoder output is stored with stride 16 bytes per frame (vec3 padded / vec4) at runtime; the C# keeps `float[track][frame][comp]`.

## 4. Pose evaluation [V code, V result]
Per bone `i` (bone loop in `func_002484A0`, `func_0025E5D0`, `func_00261EB8`):
1. Frame `f` (float) -> `f0 = floor(f)`, `t = f - f0`, `f1 = f0 + 1`, wrapping `f1` to the loop-restart frame when it reaches the frame count.
2. Position: `lerp(pos[f0], pos[f1], t)`; rotation: **slerp** with shortest-path sign flip (`func_00270B1C`: `sin((1-t)a)/sin(a)`, `sin(t a)/sin(a)`, copy if `sin(a) == 0`). Bones with track -1 use the rest pose (sec2).
3. Local matrix (row-vector convention, translation in row 3) = transposed quaternion rotation matrix + position:
   `m00 = w2+x2-y2-z2, m01 = 2xy+2wz, m02 = 2xz-2wy; m10 = 2xy-2wz, m11 = w2-x2+y2-z2, m12 = 2yz+2wx; m20 = 2xz+2wy, m21 = 2yz-2wx, m22 = w2-x2-y2+z2`.
4. World matrix `W[i] = local[i] * W[parent]` (`func_0026F690`: 3x3 product, `t = t_local * R_parent + t_parent`); the root has no parent (identity).
5. For the **root bone the game sets local x and y to 0** at render time (`sp20E8`): horizontal root motion is not rendered; it is re-applied to the actor from the bone-0 position delta (`func_0024C33C`: `(dx, 0, dy)`, i.e. animation x,y = ground plane and animation z = up; `func_0024C0F4` swaps y/z the same way).

Axes: animation and model space are **Z-up** (feet at z ~ 30, head at z ~ 480 on the player), units = model units (the player is ~480 tall; bone lengths 12-155) [V visual].

## 5. Skinning: rigid per bone [V]
Model vertices are stored in **bone-local space** and attached to exactly one bone: the bone of the last compact-DL `G_MTX` command (`0x40 m`) before the vertices are loaded (`Model.BoneOfVertex`, matrix index m == bone index; the game keeps the matrices at `actor+0xB4` indexed by bone and the display list points segment 1 at them). Vertex world position = `v * W[bone]` (row vector). No per-vertex weights exist. Checks: skinned objects keep their mean triangle edge length within 1.0-1.23x of frame 0 (sampled at ~6 frames per clip, all clips) for 86 of 90 objects (max 1.23 on object 156, 1.17 on 155: genuine squash/stretch) except objects 124-127 (8-bone pieces, mesh stretch 2.0-2.3x, bone translation lengths vary by 82-96 percent over the clip: these look like growing/flying limb-or-tentacle effects; unverified whether intentional, frame 0 is coherent); bone lengths (parent-child world distance) are constant over a walk/idle clip for the player (object 174: e.g. thigh 90.6, shin 111.5, always identical) except bones whose position track varies (bone 1: 38-41, bone 16: 152-156). Posed OBJ snapshots plot as an upright humanoid (174) / a winged creature (133) with plausible limb motion.

## 6. Hierarchy examples
Object 174 (22 bones): parents `-1,0,1,2,3,1,5,6,1,8,9,10,11,9,13,14,15,9,17,18,19,20`; ids `0,192,71,135,199,67,131,195,128,64,193,1,1,6,70,130,198,2,66,130,0,194` (bits 6/7/0/1 set patterns look like (limb group, segment) [H]). Legs are bones 1-2-3-4 / 1-5-6-7 (thigh 90, shin 111), spine 8-9, arms 9-10-11-12 / 9-13-14-15-16 / 9-17-...-21.

## 7. Events (sec4, 28-byte records) [V code `func_002476B8`; V frame range for all 336]
`u16 frame` (all < frame count), `u16 effectId` (spawn/sound id passed to `func_00265E30`; seen: 21 x37, 11 x32, 0 x22, 3 x8, 1 x7), `s16 bone` (always -1 in the ROM: use the position as is, otherwise the offset is transformed by that bone's matrix), `u16 +6` unknown, `f32 x` (+8), `f32 +0xC`, `f32 +0x10` (the game passes +8, +0x10, +0xC i.e. file y/z are swapped, matching the Z-up/Y-up swap above), `s32 +0x14`, `s32 +0x18` extra spawn parameters (e.g. 2200, 1044; packed colour/size [H]). Fired when `frame` lies in `[oldFrame, newFrame)` of the tick, only when the actor flag `0x04000000` is set or it is the primary animation slot (the 0x104 anim). 135 clips (objects 153-176 etc.) have 336 events in total.

## 8. Clip selection / roles
Animation id = actor `+4`/`+6` (`s16` next/current id), set by the actor state machine (`func_00214178` looks up state-table entries `{id, fn, flags}`; the state id is mapped to an animation id inside the per-class AI code). A transition loads the new clip (`func_00261690`) and blends the old one out over `h0` ticks (`actor+0x130` blend factor, slerp/lerp of the two poses in the bone loop).
**Not decoded**: which animation id means idle/walk/attack/death for each object. That mapping lives in the per-class state tables and AI code (Defs tables). Data hints: one-shot clips (loop = frames-1) are deaths/hits/transitions, loop-to-0 clips are locomotion/idle cycles; e.g. object 176 clips 18/22 (28 frames, one-shot) drop the root by 130 units (a death/fall), clip 15 moves the root 430 units horizontally (a lunge/charge), objects 153-176 carry footstep/effect events in the locomotion clips.

## 9. Tool
`AnimCommand.Run(args)`: `[rom] [outdir] [--obj N] [--all]` (default outdir `extracted/anim`). Writes `obj_NNN.json` (bones with parent/id/rest pose/vertex count; clips with frames, loop frame, rate, bounds, rootDelta, bone->track tables, events, and **all frames' resolved local transforms** `[px,py,pz,qx,qy,qz,qw]` per bone), `summary.tsv` (validation metrics per object) and posed `obj_NNN_cK_fFFF.obj` snapshots (first 2 clips at frames 0 and mid; `--obj N` or `--all`: all clips at 4 frames). Prints how many clips consume their streams exactly (408/408 in the shipped ROM).

## 10. Unknown
- Roles of animation ids per object (see 8); sec3[0] `u8 +5/+7`, event `u16 +6`, extra spawn params, sec3[1], sec6 meaning (bounding box presumed), `rootDelta` data (all zero).
- Frame time basis: `dt` (`D_800D2988`) is a global tick scale set by the main loop; the effective wall-clock rate (15 frames per `dt` unit) was not tied to seconds.
- Model section 3 (attachment points, 20-byte records: flag mask, bone index `u16` at +4, 12-byte offset; used by `func_0026E5E0` for muzzle flashes / effects) is non-empty in the loaded model but not parsed here (the ROM copy is empty except in three chunks).
