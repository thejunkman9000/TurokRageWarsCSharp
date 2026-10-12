# Online play (netplay)

Every player runs the whole game; only controller input crosses the network (lockstep). The game is bit-deterministic given identical input in
deterministic mode (`TRW_DETERMINISTIC=1` + `TRW_TICK_OPS`, docs/ORACLE.md), so if every machine feeds the same pad values into the same tick,
every machine computes the same match. A RAM digest exchange checks that this holds.

Code: `src/Trw.Game/Net/` (no changes to the translated game code).

| file | what |
|---|---|
| `NetPlay.cs` | entry point (`--netplay`), game boot, the tick loop (window and headless), `ReadPad` from the frame, digests |
| `NetRoom.cs` | the session: lobby roster, frame building (host), frame receiving (joiner), drops, desync referee |
| `NetWire.cs` | wire format, `NetFrame`, `PadInput` |
| `NetLink.cs` | one framed TCP connection (reader/writer threads), direct connect, jsNetwork relay leg |
| `NetDigest.cs` | the RAM digest (oracle regions + rolling 256 KB slice) |
| `MasterLink.cs` | jsNetwork master: accounts, lobby listing, browse, join routes, relay requests |
| `NetFlow.cs` | the pre-game state machine (sign in, host, browse, join, room) and the automation switches |
| `NetUi.cs` | the lobby screens and the in-game status line (SpriteBatch + `Content/NetFont.spritefont` / `NetTitle.spritefont`, the game's Peaceful Wars font) |
| `NetConfig.cs` | settings: command line, environment, `netplay.json` |
| `NetSession.cs` | read-only API for the rest of the front end (local pad, names, pings) |

## Flow
1. `dotnet run --project src/Trw.Game -- --rom <rom> --netplay`. `Program.cs` calls `NetPlay.Configure` first thing: it forces
   `TRW_DETERMINISTIC=1`, `TRW_TICK_OPS=400` (unless set), `TRW_PAK=0`. The ROM and guest memory are loaded, but the game is **not** started.
2. The window shows the lobby screens (`TrwWindow.Update`/`Draw` hand the frame to `NetPlay.LobbyUpdate`/`LobbyDraw` until the game boots).
   Sign in / create account (jsNetwork master, product code `TRW`), then host (name, 2-4 players, optional password) or browse and join.
3. The room lists the members in pad order (host = pad 0, then join order; when somebody leaves the lobby the later ones move up) with ready
   state and ping. The host's Start is enabled once there are at least two players and all are ready.
4. START: every peer calls `GameBoot.Start()` from power-on, sets `Host.ReadPad` to the session's frame reader and waits for the boot thread to
   reach its first retrace wait (`Game.WaitQuiescent`, as the oracle runner does). There is no drop-in: the host refuses HELLOs after the start.
5. Lockstep (below) until the players quit. In the match everyone goes through the game's own multiplayer menus with their own pad.

## Topology: host-relayed star
Joiners connect only to the host; the host merges the inputs of a tick into one frame and sends it to everyone (itself included).
Why not peer-to-peer:
* the jsNetwork relay pairs exactly two legs per join (host <-> joiner tickets); a mesh would need joiner <-> joiner pairs the master does not issue;
* one referee makes the hard decisions trivially consistent: pad order, the start, and the tick from which a dropped player reads "no input"
  (the host simply builds the frames that way, so every peer sees the same frames by construction);
* 2-4 players: the extra hop for joiner-to-joiner latency costs less than N^2 connections behind NAT.
Cost: the host is a single point of failure (if it leaves, the session ends for everyone), and a joiner's input reaches the other joiners
after two one-way trips.

## Transport
TCP with `TCP_NODELAY`. A joiner gets the routes from the master's join call and tries them in order: same network (`lan`, 1.5 s),
direct (`direct`, the master's reachability probe of the host's port, 3 s), then the master's TCP relay (`JSNRELAY1 <ticket>\n`, waits for `OK\n`).
The host opens its relay leg when the master's event stream says `relay_request` (a leg the joiner never uses times out on the relay by itself).
The relay copies bytes blindly, so the same framing runs over both. `--net-relay` makes a joiner skip the direct routes (testing).
Without the master: `--net-listen` (host) / `--net-direct host:port` (join) on a LAN.

## Wire format
Frame = `[u16 LE body length][body]`, body = `[u8 type][payload]`, little-endian integers, strings `[u16 len][UTF-8]`. `NetWire.ProtocolVersion` = 1.

| type | dir | payload |
|---|---|---|
| 1 HELLO | j->h | u16 protocol, str ROM SHA-256, str build id, str display name, i64 account id, str lobby password |
| 2 WELCOME | h->j | u8 member id, str lobby name, u8 max players, str host build id |
| 3 REFUSE | h->j | str reason (protocol, ROM mismatch, password, full, started); then the host closes |
| 4 ROSTER | h->* | u8 n, n x (u8 member id, u8 ready, i16 ping ms, str name); index = pad |
| 5 READY | j->h | u8 ready |
| 6 START | h->* | u8 input delay, u16 digest interval, u8 n, n x (u8 member id, str name): boot now |
| 7 INPUT | j->h | i32 tick, u16 buttons, i8 stick x, i8 stick y (the sender's pad for that tick) |
| 8 FRAME | h->* | i32 tick, u8 connected mask, u8 dropped mask, 4 x (u16 buttons, i8 x, i8 y) |
| 9 DIGEST | j->h | i32 tick, u8 n, n x u64 |
| 10 DESYNC | h->* | i32 tick, u8 pad, str what differs |
| 11 PING / 12 PONG | h->j / j->h | i64 host timestamp (echoed; answered on the reader thread) |
| 13 STATS | h->* | 4 x i16 ping ms (in the match, once a second) |
| 14 DROP | h->* | u8 pad, i32 from tick, str reason |
| 15 BYE | any | str reason |

Bandwidth in the match: per joiner ~60 x (INPUT 12 + DIGEST 47) bytes/s up and 60 x FRAME 23 bytes/s down, about 6 KB/s with framing.

## Lockstep
* A **frame** is the input of all four pads for one tick (`NetFrame`). The game's controller reads during tick t (`osContInit`,
  `osContGetQuery`, `osContGetReadData` through `Host.ReadPad`) answer from frame t. Frame 0 (power-on) and frames 1..D are empty by definition.
* **Input delay D** (default 3 ticks = 50 ms, `TRW_NET_DELAY` / `--net-delay`, 1..30): just before running tick t a peer samples its own pad
  and submits it for tick t + D. Nobody waits as long as the round trip to the host stays below about D x 16.7 ms; pick D ~ RTT/16.7 + 1.
* Tick t runs only once frame t is known. The host builds frame k as soon as it has every live pad's input for k. A peer that falls behind
  runs up to three ticks per window update while frames are queued.
* Local input: the window's own mapping (`TrwWindow.ReadPad(0)`: keyboard + mouse through `InputMapper`, the first gamepad, as offline) drives the
  local pad whatever its index. When `TRW_PAD_SCRIPT` / `TRW_KBM_SCRIPT` is set, only the script is used (tests are not disturbed by the machine's devices).
* Everything that could differ between machines is pinned: deterministic scheduler and clock, no Controller Paks (`TRW_PAK=0`: pak contents
  differ per machine; online sessions have no saves), pad presence comes from the frame (`connected` mask = the occupied pads, fixed for the
  session), not from the machine's gamepads; the audio output does not feed back in deterministic mode.

## Disconnects
When a joiner's link closes (BYE, socket error, or 15 s of silence), the host marks the pad dropped from `nextBuild` (the first frame it has not
built yet): from that frame on the pad reads zero input but stays plugged in (an unplugged controller could pause or reconfigure the game).
Because only the host builds frames, every peer applies it at the same tick; DROP tells the others for the scoreboard. If the host leaves,
the joiners' game stops (no frames) and they are told "The host ended the session".

## Desync detection
After every tick (every `TRW_NET_HASH_EVERY` ticks, default 1), while the game threads are quiescent, each peer hashes the oracle's game-state
regions (one u64 each: 0x80146800+0x800, 0x8014D000+0x200, 0x80110600+0x80, 0x80115000+0x800) and one 256 KB slice of RAM (slice = digest
number mod 32, so all 8 MB every 32 digests), ~0.1 ms. Joiners send theirs to the host, which compares with its own. The first mismatch is
logged and shown to every player: `DESYNC at tick 511: Joe (player 2) differs from the host in RAM 807C0000-807FFFFF (this slice was last compared
32 digests earlier)`; for a game-state region the tick is exact. A build id (the Trw.Game assembly MVID) travels in HELLO; a different build
is allowed but flagged in the lobby, a different ROM is refused.

## Window performance
The window ticks through `Game.LockstepBegin` / `LockstepEnd` (the two halves of `LockstepTick`): `WindowTick` finishes the previous tick
(waits for quiescence, takes the digest), then delivers the next one and returns, so the game computes the tick while the window draws.
The host-side sequence the game threads see is unchanged, so the simulation is identical; the oracle (boot and trial) is bit-identical before
and after the split. `TRW_NET_PIPELINE=0` waits inside Update instead (for comparisons), `TRW_NET_PERF=1` logs every 5 s.
Measured (this machine, 2-player match, window host + headless joiner, 9000 ticks): 60.0 ticks/s either way; time the window thread waits for the
game per tick 0.4-3.8 ms avg without pipelining, 0.1-1.0 ms with it. Level loads still have one long tick (~190 ms) - it is the game's computation.

## Configuration
| setting | where | default |
|---|---|---|
| master URL | `--master`, `TRW_MASTER`, `netplay.json` `masterUrl` | `https://poly64.duckdns.org` (the shared jsNetwork master; `TRW` must be in its products, or the master must be open) |
| input delay | `--net-delay`, `TRW_NET_DELAY`, `netplay.json` `inputDelay` | 3 |
| game port (host) | `--net-port`, `TRW_NET_PORT`, `netplay.json` `port` | 7810 (a taken port falls back to any free one) |
| digest interval | `--net-hash-every`, `TRW_NET_HASH_EVERY` | 1 |
| settings file | `TRW_NET_SETTINGS` | `%LOCALAPPDATA%\TurokRageWars\netplay.json` (master URL and last username; never a password) |

jsNetwork is referenced as a ProjectReference to `..\..\..\jsLibs\jsNetwork.Client` (property `JsLibsDir`); without it the game builds
without `TRW_JSNETWORK` and netplay works only with `--net-listen` / `--net-direct`.

Automation switches (tests, scripts; also work with the window): `--net-user U --net-pass P` (or `TRW_NET_PASS`) `[--net-register]`,
`--net-host "<name>" [--net-max N] [--net-password X] [--net-start N]` (host, start once N players are in and ready), `--net-join "<name>" --net-ready`,
`--net-listen --net-name N` / `--net-direct host:port --net-name N`, `--net-relay`, `--net-ticks N` (leave and exit after N ticks; headless exit code
0 ok, 4 desync, 5 host gone), `--net-digest-log <file>`, `--net-fast` (headless: unpaced), `--headless`.
Debug: `TRW_NET_DUMP=a,b` (all RAM after those ticks to `net_ram_<pad>_<tick>.bin`), `TRW_NET_TEST_DESYNC=<tick>` (corrupts one byte on this
peer to see the check fire), `TRW_NET_SHOT=<dir>` (PNG of each lobby screen), `TRW_NET_PERF=1`.

## API for the rest of the front end (`Trw.Game.Net.NetSession`, read-only)
`Active` (a netplay match has booted), `LocalPad` (0 host, 1..3 joiners), `PlayerCount`, `PlayerNames[4]` ("" = empty pad), `PingMs[4]`
(host <-> that pad's machine round trip, host 0, -1 unknown/gone), `Dropped[4]`, `Tick`, `Status` (one line: waiting / desync / host gone, "" normally),
`Desynced`. `LocalView` / the scoreboard use it.

## How to test
Local master (from a scratch folder, so the database is not written into jsLibs; no `appsettings.json` in the working folder = open to any product):
```
dotnet build D:\Projects\jsLibs\jsNetwork.Master -c Release --artifacts-path <scratch>\master_build
cd <scratch>\master_run
$env:MasterServer__AllowUnkeyedServers="true"; $env:MasterServer__DatabasePath="<scratch>\master_run\master.db"
$env:MasterServer__AuthRequestsPerMinute="1000"     # many test sign-ins from one address
dotnet <scratch>\master_build\bin\jsNetwork.Master\release\jsNetwork.Master.dll      # http://127.0.0.1:5080, relay on 7790
```
Three headless peers into a 3-player match (pads: host = alice, bob, carol), the route uses event-relative scripted input per peer:
```
D5="R8+300:DOWN:6,R8+340:DOWN:6,R8+380:DOWN:6,R8+420:DOWN:6,R8+460:DOWN:6"
HOST="150:SDOWN:5,200:A:6,R24+500:START:12,R3+300:A:12,R26+300:SRIGHT:6,R26+360:SRIGHT:6,R26+500:A:12,R7+300:A:12,$D5,R8+600:A:12,R20+300:A:12,R21+300:A:12,R25+300:A:12,R10+300:A:12"
JOIN="R7+350:A:12,$D5,R8+620:A:12,R25+400:A:12"
export TRW_MASTER=http://127.0.0.1:5080 TRW_NET_PASS=secret123
TRW_PAD_SCRIPT="$HOST,T13+2000:CUP:400" dotnet Trw.Game.dll --rom $ROM --headless --netplay --net-user alice --net-register --net-host TestGame --net-max 3 --net-start 3 --net-ticks 10400 --net-fast --net-digest-log alice.dig &
TRW_PAD_SCRIPT="$JOIN,T13+3000:CUP:400" dotnet Trw.Game.dll ... --net-user bob --net-register --net-join TestGame --net-ready --net-ticks 10400 --net-fast --net-digest-log bob.dig &
TRW_PAD_SCRIPT="$JOIN,T13+4000:CUP:400" dotnet Trw.Game.dll ... --net-user carol ... --net-digest-log carol.dig
cmp alice.dig bob.dig && cmp alice.dig carol.dig
```
(For two players drop the second `SRIGHT`. In a match C-up walks; the stick turns. Screens: 24 title, 3 main menu, 26 how many players,
7 character select, 8 loadout, 20/21 match options, 25 rumble prompt, then state 13.)

Results (2026-10-12): 3 peers through the local master, 10 400 ticks into the match: all digests equal (the host compared every tick of both joiners), RAM dumps
identical on the three machines; with `TRW_NET_DUMP` the players' positions (actor +0x08/+0x0C/+0x10, actors from `MatchInfo.Players()`)
change only while their own machine presses C-up (P1 in alice's window, P2 in bob's, P3 in carol's). Also passed: a joiner through the relay;
a joiner leaving gracefully at tick 7000 and one killed mid-match (the others continue, digests equal); `TRW_NET_TEST_DESYNC=500` reported at tick 511;
window host + headless joiner, 9000 ticks, digests equal, 60 ticks/s.

## Open issues
* Joiners are identified by what they say in HELLO; the host checks the lobby password itself, but the master issues no join ticket for hosted
  lobbies, so the account name is not verified (a joiner who connects directly can claim any name).
* The host is a single point of failure; there is no host migration and no rejoin.
* The shared master must list `TRW` in `MasterServer:Products` (or run open); otherwise sign-in works but listing fails with `unknown_product`.
* The Trw.Game build id is the assembly MVID: identical sources built in different folders get different ids, so the lobby shows a warning, not a refusal.
* A level load still costs one ~190 ms tick (one game computation); every peer stalls at the same point, so it does not desync.
