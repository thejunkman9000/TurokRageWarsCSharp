# Regression oracle

Proves that a change to the translated code (regeneration, new struct access) leaves the game bit-identical: the graphics command stream, the audio PCM
and the game state of a scripted run are hashed and compared with a recording of the baseline.

```
export ROM=/path/baserom.us.v11.z64
tools/oracle/run.sh out/new boot            # ~2 s;  1 200 retraces: logos, title, menus
tools/oracle/run.sh out/new trial           # ~35 s; 30 000 retraces: menus, level load, ~6 min of 3D play (14 222 frames)
python3 tools/oracle/compare.py baseline/trial out/new [-v]
```
`run.sh` environment: `NOBUILD=1`, `BUILD_WRAP="flock lock"`, `TRW_TICKS=n` (scenario length), `ORACLE_DETAIL=0`, `ORACLE_DUMP_FRAME=a,b`, `ORACLE_DUMP_TICK=a,b`, `ORACLE_RAM_EVERY=n`.
The game must be run headless (`--headless`): no GPU or window is needed; display lists are interpreted by `DisplayListInterpreter` exactly as in the window.

## What is recorded (`TRW_ORACLE_OUT=<file>`, `src/Trw.Platform/Oracle.cs`)
| line | meaning |
|---|---|
| `F n cmds= draws= verts= hash=` | one per display list. FNV-1a 64 over every command: kind, all `DrawState` fields (geometry mode, other modes, combiner words, prim/env/fog colours, fog flags, scissor, address modes, screen size, `Mode`, `TextureOn`, `Linear`), the content hash (RGBA bytes + key + size) of both textures, vertex count and every vertex float bit pattern |
| `A n bytes= peak= hash=` | one per `osAiSetNextBuffer`: the PCM block (big-endian 16-bit stereo) the audio HLE produced; hash and peak absolute sample |
| `[tick n] state= d= [full=]` | after every retrace: `D_80146878` and a hash of the game-state regions 0x80146800+0x800 (game state, fade, controller block), 0x8014D000+0x200 (UI screen manager), 0x80110600+0x80 (display-list/batch counters), 0x80115000+0x800 (model batch and resource counters); `full=` every 30 ticks is a hash of all 8 MB of RAM |

Side files, for diagnosing: `.detail.gz` (every command with every state field in clear and a vertex hash; per-tick hash of each region), `.frames` (every vertex of
`TRW_ORACLE_DUMP_FRAME` frames), `.ticks` (the raw RAM words of the digest regions at `TRW_ORACLE_DUMP_TICK` ticks, all RAM with `TRW_ORACLE_DUMP_FULL=1`).
`compare.py -v` uses them: it names the first differing frame, command, state field, vertex component, region and RAM word.

## Determinism (`TRW_DETERMINISTIC=1`) - what had to be pinned
Found by diffing runs of the same scenario; each item below produced real divergence:
1. **Retrace/thread race.** In the window the retrace is delivered by the UI thread while the game threads run concurrently, so what a game thread sees at the
   tick depends on timing. `Game.LockstepTick` delivers a tick only when every game thread is blocked (`WaitQuiescent`: no thread ready/running, and the boot thread
   has registered), and `ViTick` holds `kernelLock` while it posts the tick's messages (SP/DP done, VI, timers), otherwise the thread woken by the first post ran
   concurrently with the remaining posts (observed: swapped queue entries, a counter off by one clock query).
2. **Clock.** `Host.Counter` (osGetCount/osGetTime/timers) is derived from the tick (781 250 counts per tick) plus 16 counts per query within the tick, instead of the
   wall clock. Busy-wait loops on the clock still terminate.
3. **Pad script clock.** The scripted input (`TRW_PAD_SCRIPT`, same syntax) uses the tick counter; the headless runner updates it in the same order as `TrwWindow.Update`.
4. **Long computations.** With ticks only at quiescence the level load happens inside one tick and the game deadlocks (the pad poll thread waits for a controller
   message that the interrupted sequence never produces; in real time the load spans ~12 ticks and works). `TRW_TICK_OPS=n` (run.sh uses 400) parks the running game thread
   at its n-th scheduler-visible call (message send/receive, clock read) since the last tick until the tick was delivered, so ticks interleave with long computations at
   a deterministic point. 0 = off (the boot and menu scenarios would work, the trial does not).
6. **Op budget reset race.** The per-tick op count of `TRW_TICK_OPS` is reset by the host before the tick's messages are posted (`Game.ResetOps`, while every game
   thread is quiescent). Resetting it after the post let the thread woken by the first message run ahead of the reset, which moved the park point of the tick by a few
   ops: a one-tick digest divergence (observed at ticks 2091 and 5947 of the trial) in about one run in five under CPU load.
5. Boot: the first tick must wait until the boot thread exists (otherwise the first ticks raced with thread creation: 31 vs 32 audio blocks).
Not a source of divergence (checked): graphics/audio run synchronously on the game thread that starts the task, there is no RNG seeded from time in the code base, collections
are keyed by integers. The window front end also uses lockstep when `TRW_DETERMINISTIC=1` (but still draws on the UI thread).

Caveat: lockstep with a tick budget is a different (but legal) interleaving from the real-time window: the timeline differs by a few ticks (the match starts at tick 7776
instead of ~7780). The oracle compares builds against each other, not against the window.
