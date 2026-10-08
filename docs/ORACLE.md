# Oracle mode (C reference port)

The C port in `D:\Projects\TurokRageWars` can run as a reproducible reference. Code: `port/oracle.c` plus three small hooks
(`counter_now` and `osContGetReadData` and the barrier in `port_vi_tick` in `port/os_shim.c`, the frame loop in `port/win32_main.cpp`).
Everything is inert unless an environment variable is set. Check script: `port/oracle_check.ps1`.

| Variable | Effect |
|---|---|
| `TUROK_ORACLE=1` | virtual time (the libultra counter advances 1/60 s per game frame) and a per-frame barrier: each frame waits until all game threads are blocked |
| `TUROK_FRAMES=n` | headless: run n frames without drawing or pacing, then exit (implies oracle) |
| `TUROK_REPLAY=file` | controller input from a file, lines `<frame> <buttons hex> <stick_x> <stick_y>`, last value held |
| `TUROK_RECORD=file` | write the input the game read, same format, only on change |
| `TUROK_TRACE=file` | per-frame line `<frame> <crc32 of guest RAM>`; words in the host-only range 0x04000000-0x2FFFFFFF are zeroed first (they are host pointers) |
| `TUROK_TRACE_PAGES=1` | append the CRC of every 4 KB page that changed (find where two runs diverge) |
| `TUROK_DUMPRAM=file` | write the 8 MB guest RAM when the frame limit is reached |

## Determinism status (2026-10-07)
- Frame 0 is now reproducible: the barrier also waits for the boot thread to make its first OS call.
- With the barrier, most runs are bit-identical over 300 frames, but some diverge from about frame 49 in a few display-list pages
  near 0x806D0000: a `G_TEXRECT` scale word (`dsdx` 0xAAAB0000 vs 0). That is render data, not game state, but it shows a real
  race or uninitialised read in the port that still needs to be found.
- Independent of oracle mode, about 3 of 10 launches die within 5 s (access violation or exit -1). Pre-existing; not caused by these hooks.
- Not yet done: a semantic state dump (player position, health, actor list) once the relevant RAM structures are identified; the
  CRC trace only tells *that* two runs differ.

## Next steps
1. Find the cause of the frame-49 display-list difference and the startup crashes (both need the port's own owner session).
2. Identify player/actor structures in RAM and add a text state dump to the trace.
3. Record a short real input sequence (menu to first level) with `TUROK_RECORD` and store it as the first regression input.
