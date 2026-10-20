# Level objects: what a loaded level contains and where (the level inspector, Window/DebugOverlay.cs, F3)

How to read every kind of placed ("editor") object of a loaded level from guest RAM, and how to project world positions with the game's cameras. Found by reading the code (2026-10-19); names may have been renamed since, addresses and offsets identify things.

## Using the level inspector

* **F3** opens the inspector menu (and turns the markers on). Up / Down choose a line, Enter / Space / Left / Right switch it; F3 closes the menu
  (the markers stay; F3 again reopens it). While the menu is open the arrows, Enter and Space do not reach the game.
* Categories: player starts, spawn / warp points (with their facing), item spawners (what they spawn), objectives (flag bases, tag points, monkey
  escape points), other entities (platforms, turrets, switches, destructibles, animated props ... by class), AI nav nodes and links (arrows = one-way),
  each bot's planned route and goal, level paths, triggers (source -> target), placed pickups, props, light volumes, players (name, health, spawn key),
  live actors and pickups, point lights, loop sounds, the collision faces. Labels use the game's own type names (the ROM keeps the original build's
  symbol listing, `AI_OBJECT_*`), corrected from the pickup tables where they differ. LABELS switches the text, RANGE limits the distance.
* **NEXT MATCH BOTS** (menu) or `TRW_BOTS=none` / `TRW_BOTS=ally`: the next match starts without computer players (walk the level alone) or with only
  one bot, on the players' team (team play on, so it leaves you alone). Applied when the match starts (hook after MatchSetup.StartMatch, Os/TestHooks.cs);
  the attract demo and netplay are never changed. Pick the level in the menus, or pin it with `TRW_ARENA=n` (Os/TestHooks.cs lists the level numbers).
* Unattended: `TRW_INSPECTOR=1` (markers on), `=menu`, `=all`, `=only:NavNodes,Spawns` (category names of DebugOverlay.Cat); `TRW_INSPECT_AT=<tick>`
  prints what the inspector finds at that retrace (headless runs too).
* Read only (except the bot option): the inspector draws over the picture and writes nothing to guest RAM, so recordings and netplay are unaffected.

## How the data is found

Research for the level inspector.
Addresses are the C# port's own (`Gv.*` in `src/Trw.Game/Generated/_Globals.cs`, US v1.1 RAM). Offsets are hex.
Function names are current tools/names names. If they get renamed, the address in brackets still identifies them.
The decompiled C in D:/Projects/TurokRageWars uses German-version (`_de`) addresses inside its text, so do not copy addresses out of it.

Status labels: **verified** means read in code. *(uncertain)* means inferred and not proven.

---

## 0. Common building blocks

### The Level object is a global, not a pointer
`Gv.LevelMgr = 0x8011FE88`. This is the Level struct itself (inline, about 0x1B930 bytes), not a pointer to it. Every `Level_*` function receives this address. Several other globals are fields inside it:

| global | = LevelMgr + | meaning |
|---|---|---|
| `Gv.Level_EntityArrayPtr 0x8011FFC0` | 0x138 | pointer to the entity (placed actor) array |
| `Gv.Level_EntityCount 0x8011FFC8` | 0x140 | entity count |
| `Gv.PickupPool 0x80131600` | 0x11778 | dynamic pickup pool |
| `Gv.ActorPool 0x80135210` | 0x15388 | dynamic actor pool |
| `Gv.Lighting_SunDirPtr 0x8013B138` | 0x1B2B0 | LevelHeader pointer (section 0) |
| `Gv.World_FloorMinY 0x8013B184` | 0x1B2FC | lowest collision vertex y |
| (D_8013B188) | 0x1B300 | section-6 records (region-layer classes, see section 11) |
| `Gv.Light_Pool 0x8013B1A8` | 0x1B320 | point-light pool |
| `Gv.Level_CurrentId 0x8013B294` | 0x1B40C | LevelIndex (current level number) |
| `Gv.Level_StartPointId 0x8013B2BC` | 0x1B434 | start point / warp key used for this load |
| `Gv.AiNavGraph 0x8013B364` | 0x1B4DC | AI navigation graph |
| `Gv.AiNavGraph_NodeCount 0x8013B368` | 0x1B4E0 | nav node count |

### Validity guard (verified)
- `Mem.S32(Gv.LevelMgr + 0x0)` (Level.LoadState) is set to 0, 1, 2, 3 and then 4 during `Level_Load` [0x00286A78]. Only 4 means fully loaded.
- `Mem.S32(Gv.Level_DataLoaded 0x800D29B4) != 0`.
- `Gv.GameState 0x80146878` is 13 (play) or 8 (recorded demo). In state 20 (transition) a new `Level_Load` runs. It first frees the old level through `Scene_Shutdown` [func_0044D794], so every pointer below dies during state 20.
- Recommended guard: `GameState is 8 or 13 && LoadState == 4 && Level_DataLoaded != 0`.

### Chunked resource tables (verified, `Chunk.Get` [0x0028FD94])
Most level data is a "chunk table": word 0 is the chunk count, word 1+i is the byte offset of chunk i.
`Chunk(t, i) = t + Mem.S32(t + 4 + 4*i)`.

Many chunks are "pools" of the form `{ +0 s32 stride-or-unused, +4 s32 count, +8 records... }`.
IntelligenceDefs is a chunk table where each chunk is one definition.

### Intrusive lists (verified, `ListUtil.Init` [0x00255C40])
List header (20 bytes): `+0 head, +4 tail, +8 prevOff, +0xC nextOff, +0x10 count`.
To walk a list: `for (n = U32(list); n != 0; n = U32(n + nextOff))`.

### Object type ids and names
- **Type id** (u16, for example 0x653) of a level type index: `U16(U32(LevelMgr+0x94) + 8 + 2*idx)` (`Level_GetTypeIdByIndex` [0x0028B238]). The count is at `U32(LevelMgr+0x94)+4`.
- **Definitions** ("intelligence", per-cartridge): `def(i) = Chunk(U32(LevelMgr+0x78), i)`. This is `Level_GetDefinition` [0x0028B13C], and idx -1 means none. The count is `S32(U32(LevelMgr+0x78))`. Fields: `+0 s32 Kind` (class), `+4 Flags`, `+0xC s16 variant id`, `+0x14..` per-class parameter block (ActorDefParams).
- **There is no string name table for objects.** No resident human-readable names were found for types or defs. Labels have to be built from the class kind, the type id (hex), the index, and the hand table of known ids below. The only resident-ish strings are flyby record names (section 10) and player names (section 13).

**Def Kind enum** (`Actor_ClassTable 0x800D0680` read from the ROM: one callback block per kind, plus the Actor_GetHeight/Radius switches):

| Kind | meaning | evidence |
|---|---|---|
| 0 | generic object (height from def+0x1C) | Actor_GetHeight |
| 1 | creature / AI actor (radius +0x28, height +0x30, sight +0x3C) | Actor_GetRadius, Class1Actors list |
| 2 | **Platform** (moving platform; params: travel height +0x2C, distance +0x30, yaw +0x34) | ClassTable[2] = Platform_Init/Restore/MsgRaise/MsgLower |
| 3 | **ItemSpawner** (pickup spawn point; spawned item type index = `S16(def+0x28)`, def index `S16(def+0x2A)`) | ClassTable[3] = ItemSpawner_* |
| 4 | **Turret** | ClassTable[4] = Turret_* |
| 5 | special target class (Class5Actors list, max 16) *(meaning uncertain)* | BuildActiveActorLists |
| 6 | **Toggle** (switch / door-like, height 102.4) | ClassTable[6] = Toggle_* |
| 7 | **Destructible** | ClassTable[7] = Destructible_* |
| 8 | dropped pickup (PickupPool default def) | PickupPool_Init sets Kind 8 |
| 9 | **AnimProp** (animated prop) | ClassTable[9] = AnimProp_* |
| 10 | **Objective** (tag points, flag bases, escape points) | ClassTable[10] = Objective_* |
| 11 | player character | Actor_GetHeight case 11, SpawnActors player starts |
| 12 | placed pickup (spins, speed def+0x20) | fields ActorDef 0x20 |
| 13 | arena-variant objects (def+0xC 3371..3374 shown/hidden by arena options) | Level_CollectClass13Entities |
| 14 | marker entities (Class14 list) *(purpose uncertain)* | Level_CollectClass14Entities |

**Known type ids** (Actor TypeId +0xE4, or pickup/item type ids):

| id | meaning |
|---|---|
| 0x642 | moving platform (AiNav_LinkPlatformNodes) |
| 0x643, 0x644 | item-spawner entity types. Their spawned item type is the label (AiNav_LinkPickupNodes) |
| 0x64B, 0x64C | objective variants (Objective_Restore) *(uncertain)* |
| 0x64D | special actors, max 4 (Type64DActors list) *(uncertain)* |
| 0x64E (1614) | Frag Tag **monkey escape point** (class 10, Level_EscapePoints) |
| 0x64F (1615) | special target actors, max 32 (Ai_ClassifyTarget kind 7) *(uncertain)* |
| 0x653..0x656 (1619..1622) | **tag points / flag bases** (class 10, Level_TagObjectives[0..3]). Bots carrying the flag route to 0x653 |
| 0xBD6 (3030) | monkey item (escape spawner, Level_EscapeSpawnerPtr) |
| 0xBD7 (3031) | **the CTF flag / objective item** (ItemSpawner, Level_TagObjectiveEntityPtr) |
| 0xBD8 | random power-up special pickup |
| 0xBD9..0xBE8 | ammo pickups (Pickup_ApplyAmmo) |
| 0x708.. | invincibility/plane-walker power-ups |

---

## 1. Placed entities (every level actor from the placement section: spawners, objectives, platforms, props with logic, player starts)

**Where (verified):**
- `ents = Mem.U32(Gv.LevelMgr + 0x138)` (= `Mem.U32(Gv.Level_EntityArrayPtr)`).
- `n = Mem.S32(Gv.LevelMgr + 0x140)`.
- Stride **0x2E8 (744)**: each entity is a full Actor.
- The first `S32(LevelMgr+0x13C)` (PlayerEntityCount) entries are player-start entities: def kind 11, or index 0.
- Built by `Level_SpawnActors` [0x0044D408], the section-3 load callback of `Level_Load`. Init and restore happen in Level_Load. The array header sits just before the entities: `U32(ents-8)=744` stride, `U32(ents-4)=count`.

**Entry layout (Actor header, verified):**

| off | field |
|---|---|
| +0x00 u8 | Type (1 = character) |
| +0x04 s16 | TypeIndex (level type index) |
| +0x08 Vec3f | **Position** (x, y, z floats at +0x8, +0xC, +0x10). Live, so moving platforms update it |
| +0x14 ptr | current floor triangle (collision face, section 7) |
| +0x18 ptr | **DefPtr**. `S32(def)` = Kind (table above) |
| +0x1C Vec3f | velocity |
| +0x50 Vec3f | scale |
| +0x5C quat | rotation |
| +0x6C f32 | **Yaw** (radians) |
| +0x74 Mtx | float transform (row-vector, translation in row 3) |
| +0xE4 u16 | **TypeId** (the "known ids" table) |
| +0xE8 6xf32 | world AABB min xyz, max xyz |
| +0x100 u32 | flags |
| +0x174 s32 | hit points (1/256) |
| +0x1D8 ptr | owner |
| +0x204 | path cursor: `+0x205 u8` = LevelPath index, `+0x206 s16` = current point (see section 4) |
| +0x210 Vec3f | home position |

**Placement record** (40 bytes, `B11Spawn`/`S6Rec`, verified in `Level_SpawnActorFromRecord` [0x0024B398]). It is freed after the load, but its values live on in the actor:
`+0 region id, +4 pos, +0x10 scale, +0x1C u16 typeIndex, +0x1E u16 typeId, +0x20 u16 floor-tri index (0xFFFF none), +0x22 u16 def index, +0x24 s16 yaw (x 2pi/65536), +0x26 u8 flags, +0x27 u8 path index (255 none)`.

**Readers:**
- Level_FindNearestEntity [0x0028CABC]
- Level_GetEntityIndex
- Level_FindTagObjectives [0x0028E0E4]
- Level_FindEscapePoints [0x0028E260]
- AiNav_LinkPickupNodes [0x0020AA40]
- Level_BuildActiveActorLists [0x00287EE8], which walks it every frame
- Entity_Spawn [0x00278C80] (trigger targets: `ents + idx*744`)

**Valid:** the whole time LoadState == 4, until the next Level_Load. The array is not culled.

**Recipe:**
```csharp
uint L = Gv.LevelMgr;
if (Mem.S32(L) != 4) return;
uint ents = Mem.U32(L + 0x138); int n = Mem.S32(L + 0x140), nPlayers = Mem.S32(L + 0x13C);
uint typeTbl = Mem.U32(L + 0x94);
for (int i = 0; i < n; i++) {
    uint e = ents + (uint)(i * 0x2E8);
    float x = Mem.F32(e + 8), y = Mem.F32(e + 0xC), z = Mem.F32(e + 0x10);
    uint def = Mem.U32(e + 0x18); int kind = def != 0 ? Mem.S32(def) : -1;
    ushort typeId = Mem.U16(e + 0xE4);
    string label = i < nPlayers ? $"start#{i}" : $"{KindName(kind)} 0x{typeId:X3} #{i}";
    if (kind == 3) {                        // ItemSpawner: show what it spawns
        short ti = Mem.S16(def + 0x28);
        if (ti >= 0) label += $" -> item 0x{Mem.U16(typeTbl + 8 + (uint)(2 * ti)):X3}";
    }
    // AABB at e+0xE8..0xFC for a box
}
```

---

## 2. Spawn points, start points, warp destinations ("warp dests")

**Where (verified, `Level_FindPlacedObjects` [0x0044DE70], `Level_FindNearestPlacedObject` [0x0044E038]):**
- `wd = Mem.U32(LevelMgr + 0x90)` is a chunk table for the whole cartridge, all levels together.
  - `k = Chunk(wd, 0)`: `S32(k+4)` = count, `k+8` = s32 **keys[count]` (sorted).
  - `r = Chunk(wd, 1) + 8`: 20-byte records.
- Record layout (`B04Spawn`/`S6Obj`):
  - `+0 Vec3f pos`
  - `+0xC f32 yaw`
  - `+0x10 s16 kind` = **level index** the point belongs to
  - `+0x12 u16` = floor-triangle index (used as `Level_GetFloorTriangle(idx)`)

**Meaning:**
- key = spawn/warp key.
- Players respawn at every record with `key == player+0x5EC` (SpawnPointId). In multiplayer, `Player_PlaceAtSpawn` [0x0021B1E4] picks the record farthest from the other players.
- Level start uses `key == Level_StartPointId` (LevelMgr+0x1B434).
- Draw only the records with `kind == Mem.S32(LevelMgr + 0x1B40C)` (LevelIndex).
- Also in the Level: `+0x1B41C` HasWarpDest and `+0x1B420` WarpDestRecord, a copy of the 20-byte record used for this load. Globals: `Gv.Level_HasStartPoint 0x8013B2A4` and `Gv.Level_StartPointPos 0x8013B2A8`.

**Valid:** at all times in a level. The data is loaded by Scene_LoadLevelData [0x0044C410] for the whole cartridge.

```csharp
uint wd = Mem.U32(L + 0x90); uint k = wd + (uint)Mem.S32(wd + 4), r = wd + (uint)Mem.S32(wd + 8) + 8;
int cnt = Mem.S32(k + 4), lvl = Mem.S32(L + 0x1B40C);
for (int i = 0; i < cnt; i++) {
    uint p = r + (uint)(i * 20);
    if (Mem.S16(p + 0x10) != lvl) continue;
    int key = Mem.S32(k + 8 + (uint)(4 * i));
    // pos F32(p),F32(p+4),F32(p+8); yaw F32(p+0xC); label $"spawn key {key}"
}
```

---

## 3. AI navigation graph (AiNavNode / AiNavGraph)

**Where (verified, Level_Load sections 9/10/11, `AiNav_*` in Ai/Navigation.cs):**
`G = Gv.AiNavGraph = 0x8013B364` (LevelMgr+0x1B4DC).

| off | field |
|---|---|
| G+0x0 ptr | node pool `{+0 s32 elemSize, +4 count, +8 records}` (section 9) |
| G+0x4 s32 | NodeCount |
| G+0x8 ptr | edge pool `{+0 elemSize, +8 records}` (section 10) |
| G+0xC s32 | EdgeCount |
| G+0x10 ptr | visibility pool: byte matrix `vis[i*NodeCount + j]` (section 11, may be absent when the level has fewer than 12 sections) |
| G+0x14 s32 | its count |
| G+0x18 | FoundGoalId |
| G+0x24 | list (prevOff 0xC, nextOff 0x10) of 0x50-byte AiNavNode **search nodes**, one per nav node (runtime A* state) |
| G+0x38 | GoalIds[64] |

- **Node record**: `node(i) = U32(G) + 8 + i * S32(U32(G))`. This is AiNav_GetNode [0x0020C994].
  - `+0 Vec3f pos`
  - `+0xC u16 flags`: 1 = pickup node (id is the item type id it waits at), 2 = branch offset, 0x40/0x80 = timer nodes, 0x200 = platform node, others unknown
  - `+0xE u16 id` (item type id for flag-1 nodes, for example 0xBD7 flag, 0x653 flag base, 0x64E escape point)
- **Edge record**: `edge(k) = U32(G+8) + 8 + k * S32(U32(G+8))` (AiNav_GetEdge [0x0020C9B0]).
  - `+0 u16 a`, `+2 u16 b` (node indices), `+4 u8 kind`.
  - Kinds 1, 4 and 7 are two-way (`AiNav_IsEdgeTwoWay`). The rest are one-way a->b.
  - Known kinds: 5 = ride platform, 6 = branch, 8 = jump, 2 = walk. 9, 10 and 11 are special *(uncertain)*.
- **Search node** (0x50): `+0 NodeId, +0x10 next, +0x18 ExtraCost, +0x1C g, +0x20 h, +0x24 f, +0x28 state (0/1 open/2 closed), +0x2C IsGoal, +0x30 parent, +0x34 linked pickup actor, +0x40 platform actor, +0x44 Vec3f platform stop position`.
- **Bots' use**: `brain = U32(player + 0x1454)`.
  - brain+0x4 = CurrentNode, +0x8 = Previous, +0xC = GoalNode.
  - brain+0x14 = s32 PathNodes[4] (-1 terminated).
  - brain+0x2C = Vec3f MoveTarget, +0x64 = Target actor, +0x21C = AI state id.
  - Drawing a line through PathNodes shows each bot's route.

**Readers:** AiNav_FindNearestNode [0x0020CB3C], AiNav_GetAllNeighbors [0x0020CCE8], AiNav_IsNodeVisible [0x0020D1CC], AiNav_AStar, AiNav_LinkPickupNodes/LinkPlatformNodes (called by Level_Load).

**Valid:** whole level. Sections 9 to 11 are loaded in Level_Load when the section index has at least 9 or 12 entries. Check `U32(G) != 0`.

```csharp
uint G = Gv.AiNavGraph, np = Mem.U32(G), ep = Mem.U32(G + 8);
int nn = Mem.S32(G + 4), ne = Mem.S32(G + 0xC);
uint Node(int i) => np + 8 + (uint)(i * Mem.S32(np));
for (int i = 0; i < nn; i++) { uint r = Node(i); /* pos F32(r..r+8), flags U16(r+0xC), id U16(r+0xE) */ }
for (int k = 0; k < ne; k++) { uint e = ep + 8 + (uint)(k * Mem.S32(ep));
    int a = Mem.U16(e), b = Mem.U16(e + 2), kind = Mem.U8(e + 4); /* line Node(a)->Node(b), arrow when not 1/4/7 */ }
```

---

## 4. Level paths (patrol/platform/roam paths)

**Where (verified, `Level_OnLoadPaths` [0x0044E6A4], `LevelPath_GetHeader` [0x0028D1F4], `LevelPath_GetPoint` [0x0028D220], `LevelPath_FindNearestPoint` [0x0028D268]):**
- `P = U32(LevelMgr + 0x7C)` (section 2). Path count = `S32(P)`. `path(i) = Chunk(P, i)`.
- `hdr = Chunk(path, 0)`: `+0 s16 mode` (0 = ping-pong, 1 = loop), `+2 s16 count`.
- `pts = Chunk(path, 1)`: `+0 s32 stride (56)`, `+4 s32 count`, points from +8.
- Each 56-byte point: `+0 Vec3f pos`, `+0xC ptr` = floor triangle (relocated at load, 0 when none). The rest is unknown.

**Users:**
- Actor PathCursor +0x204 (byte +0x205 = path index, set from the placement record +0x27)
- AI roam (brain+0x224 RoamPathId, +0x228 point)
- Platforms

**Valid:** whole level.

```csharp
uint P = Mem.U32(L + 0x7C); int np = Mem.S32(P);
for (int i = 0; i < np; i++) { uint path = P + (uint)Mem.S32(P + 4 + (uint)(4 * i));
  uint pts = path + (uint)Mem.S32(path + 8);           // chunk 1
  int stride = Mem.S32(pts), cnt = Mem.S32(pts + 4); bool loop = Mem.S16(path + (uint)Mem.S32(path + 4)) == 1;
  for (int j = 0; j < cnt; j++) { uint q = pts + 8 + (uint)(j * stride); /* F32(q), F32(q+4), F32(q+8) */ } }
```

---

## 5. Objective points: CTF flag bases, Frag Tag tag points, monkey escape points

These are class-10 / ItemSpawner entities from section 1. The game caches pointers to them during `Level_Load` (verified, Level_FindTagObjectives [0x0028E0E4] and Level_FindEscapePoints [0x0028E260]):

| global | content |
|---|---|
| `Gv.Level_TagObjectives 0x800F7CF8` | 4 entity ptrs for TypeId 0x653, 0x654, 0x655, 0x656 (tag points / flag bases; [i] = 0x653+i) |
| `Gv.Level_TagObjectiveEntityPtr 0x800F7D08` | ItemSpawner entity whose item is 0xBD7 (the flag / objective item spawn) |
| `Gv.Level_EscapePoints 0x800F7CD0` | up to 10 entity ptrs for TypeId 0x64E (monkey escape points) |
| `Gv.Level_EscapeSpawnerPtr 0x800F7D0C` | ItemSpawner whose `S16(def+0x28) == 3030` (0xBD6, monkey) |

The position is the entity's `+0x8`.

Related: in Frag Tag, Objective_UpdateTriggered [0x0020402C] uses a touch radius of 122.88 around tag points.

Match type: `Gv.ArenaLevel 0x8015402C`. Game type is at `D_80154028` (0 CTF, 1 frag tag, 2 bloodlust, 3 team), from the facts file.

Monkey player: player `U32(player+0x5D8)+0x8F == 1` (Match_FindMonkeyPlayer [0x0022A410]).

---

## 6. Triggers ("link groups"): entity triggers and region triggers

**Where (verified, section-7 callback [0x0044CD58], misnamed `Collision_BindMesh` in Render/Lighting.cs):**

| off | field |
|---|---|
| LevelMgr+0x11C0 s32 | EntityTriggerCount |
| +0x11C8 ptr | EntityTriggerKeys (s32[], sorted; key = **entity index** of the source) |
| +0x11D0 ptr | EntityTriggerRecords (20 bytes each, parallel to the keys) |
| +0x11C4 / +0x11CC / +0x11D4 | the same for **region** triggers. Key = region index, the `u16` at +0 of a collision face. They fire when an actor enters a face of that region |

- **Record** (`Rec14`, 20 bytes, read by `Entity_Spawn` [0x00278C80] and `Level_ActivateTriggerRecord`):
  - `+4 u16` = target entity index (into section 1)
  - `+8 u16` = flyby id to start (>= 2)
  - `+0xA u16` = target level id. Equal to Level_CurrentId means local, else a cross-level message
  - `+0xE u8 flags`: 0x10 = fired, 2 = delayed, bit 0 cleared when hidden, 0xF0 set by group options
  - `+0xF u8` key
  - `+0x10 u8` = HUD message index
  - `+0x11 u8` = target class (into `Level_SpawnClassTable 0x800D28B8`)
  - `+0x12 u8` = message index within that class (8-byte {fn0, fn1} entries)
- **Overlay idea:** for record k of the entity table, draw a line from `ents + key[k]*0x2E8` to `ents + U16(rec+4)*0x2E8`. For region triggers, draw from the faces whose region index equals key[k] (section 7) to the target entity.
- Delayed spawns: list at LevelMgr+0x11D8 (nodes: +0x8 record ptr, +0xC timer).

**Valid:** whole level.

---

## 7. Collision mesh, regions, collision cells

**Where (verified, section-1 callback `Collision_BuildSurfaceMesh` [0x0044D024], `Collision_FindFloorTriangle` [0x002866F8], `Level_GetRegion` [0x0028B2D4], `Level_GetFloorTriangle` [0x0028B370]):**
`C = U32(LevelMgr + 0x6C)`.

- **Regions**: `Chunk(C,0)+8`, 100 bytes each. `region(face) = Chunk(C,0) + 8 + U16(face+0)*100`.
  - Known fields: `+0x20 f32` surface height (valid when `U32(+0x44) & 1`), `+0x44` flags, `+0x58/+0x59` floor/wall impact type.
  - Other bits control fog, water and region layers *(not fully mapped)*.
- **Vertices**: `Chunk(C,1)+8`, 16 bytes each: `x, y, z, w` floats (w = ceiling y for volume faces). They are decoded from ints x 0.029296875 at load.
- **Faces (floor/wall triangles)**: `Chunk(C,2)`: `+4 count`, from +8, 32 bytes each.
  - `+0 u16 region index`
  - `+2 u16 flags` (0x40 = volume face, 0x400, 0x2000)
  - `+4 ptr v0, +8 ptr v1, +0xC ptr v2` (absolute pointers into the vertex chunk)
  - `+0x10 ptr adj[3]`
  - `+0x1C s32 attr` (zone id, NoRegionId -1 when none)
- **Collision cells**: `Chunk(C,3)`: `+4 count`, from +8, 20 bytes each `{x0, z0, x1, z1 f32, u32 n}`. The faces are stored in cell order: cell c owns the next n faces.
- Actor/Prop/Pickup `+0x14` = current face pointer. Camera `+0x58` = face under the camera.

**Valid:** whole level.

```csharp
uint C = Mem.U32(L + 0x6C); uint F = C + (uint)Mem.S32(C + 4 + 2 * 4);
int nf = Mem.S32(F + 4);
for (int i = 0; i < nf; i++) { uint f = F + 8 + (uint)(i * 32);
  uint a = Mem.U32(f + 4), b = Mem.U32(f + 8), c = Mem.U32(f + 0xC);  // each: F32(v), F32(v+4), F32(v+8)
  int region = Mem.U16(f); }
```

---

## 8. Streamed grid cells: placed props, placed pickups, light volumes

**Grid (verified, `Level_StreamVisibleCells` [0x00288440], `Level_LoadGridCells` [0x0044D9DC]):**
- `G4 = U32(LevelMgr + 0x68)` (section 4).
- Chunk 0: column min-x floats, chunk 1: column max-x, chunk 2: row min-z, chunk 3: row max-z. cols = `S32(Chunk(G4,0)+4)`, rows = `S32(Chunk(G4,2)+4)`.
- Chunk 4 `+8`: cells of 20 bytes `{x0, z0, x1, z1, n}` at `(col + row*cols)`.
- PVS: LevelMgr+0x1B910 origin x/z, +0x1B918 cell size x/z, +0x1B92C HasPvsGrid.

**Loaded cells:**
- `LevelMgr+0x1504` LoadedCellCount (max 128).
- `+0x1508`: 12-byte entries `{+0 resource handle, +4 ptr to the grid cell, +8 s16 col, +0xA s16 row}`.
- `blob = U32(handle)`.
- The cell blob is a chunk table built by the cell-load callback [0x0028795C]:

| chunk | content |
|---|---|
| 0 | **Props**: `+4 count`, from +8 stride **0xE8** |
| 1 | **Pickups (placed)**: `+4 count`, from +8 stride **0x1C8** |
| 2 | **Light volumes**: `+4 count`, from +8 stride **0xC0** |
| 3 | aux table (copied) |

- **Prop** (0xE8):
  - `+0 Type 0`, `+4 Id`, `+8 Vec3f Position`, `+0x14` face
  - `+0x18 DefPtr` (`def+8` radius, `+0xE/+0xF` near/far model index)
  - `+0x68` float placement matrix
  - `+0xB8/+0xC4` AABB min/max, `+0xD4` region id, `+0xD8` flags
- **Placed pickup** (0x1C8, `Pickup_InitFromRecord` [0x0024E980]):
  - `+0 Type 3`, `+4 s16 TypeIndex` (type id via the type table), `+8 Vec3f Position`
  - `+0x18 DefPtr`, `+0x174 yaw`, `+0x17C/+0x188` bounds
  - `+0x19C flags`: bit 1 = already collected (saved flag), 0x10 = growing in
- **Light volume** (0xC0, `Light_InitVolume` [0x00276D20]):
  - `+0x8/+0xC/+0x10` colour floats
  - `+0x20 Mtx` world->unit, `+0x60 Mtx` unit->world. Its translation row `+0x90/+0x94/+0x98` is the **centre**
  - `+0xA0` AABB (6 floats)
  - `+0xB8` negative (dark) light, `+0xB9` axis aligned, `+0xBA` flickering
  - `+0xBB mode` (0 sphere, 1 box, 2 cone, per `Light_EvalVolumeAtPoint`)

**Validity caveat (verified):**
- `Level_UpdateFrame` [0x00286050] rebuilds the loaded-cell list every frame: `StreamVisibleCells` near the start, `ReleaseLoadedCells` at the end.
- The release sets LoadedCellCount to 0, copies the old count to `+0x1500` (PrevLoadedCellCount) and unrefs the handles. The entries at +0x1508 stay in memory.
- So outside Level_UpdateFrame, read `S32(L+0x1500)` entries from +0x1508. *(The blobs normally stay in the resource cache after the unref, but this is not guaranteed. Safest is a hook between `Level_StreamVisibleCells` and `Level_ReleaseLoadedCells`, for example right after `View.UpdateAll` in UpdateFrame.)*
- Only cells overlapping the view bounds (`Gv.View_UpdateBounds 0x801031F0`) are loaded. Arenas are small, so this usually means all cells.
- Static props baked into one display list: LevelMgr+0x88 StaticPropCache and +0xF8 blob (Level_BuildStaticPropDisplayList). No positions were found there; use the cell props instead.

```csharp
int nc = Mem.S32(L + 0x1504); if (nc == 0) nc = Mem.S32(L + 0x1500);
for (int c = 0; c < nc; c++) { uint h = Mem.U32(L + 0x1508 + (uint)(c * 12)); if (h == 0) continue; uint blob = Mem.U32(h);
  uint props = blob + (uint)Mem.S32(blob + 4), picks = blob + (uint)Mem.S32(blob + 8), vols = blob + (uint)Mem.S32(blob + 12);
  for (int i = 0; i < Mem.S32(props + 4); i++) { uint p = props + 8 + (uint)(i * 0xE8); /* F32(p+8..) */ }
  for (int i = 0; i < Mem.S32(picks + 4); i++) { uint p = picks + 8 + (uint)(i * 0x1C8); /* type via S16(p+4) */ }
  for (int i = 0; i < Mem.S32(vols + 4); i++) { uint v = vols + 8 + (uint)(i * 0xC0); /* centre F32(v+0x90..0x98) */ } }
```

---

## 9. Lights

- **Point lights (runtime)** (verified, Light_Acquire [0x00268C1C], Light_First/Next, Light_EmitPointLights):
  - `pool = Gv.Light_Pool 0x8013B1A8` (LevelMgr+0x1B320). Active list head = `U32(pool+0)`, next at `node+4`. The free list is at pool+0x14.
  - Node: `+8 def ptr` (`def+4..+7` colour bytes), `+0xC f32 intensity`, `+0x10/+0x12/+0x14 s16 x, y, z` (world, integer), `+0x16 s16 enabled`.
  - These lights come from particles and flashlights (dynamic). At most 8 are emitted per draw.
- **Dynamic light definitions**: `U32(LevelMgr+0xA0)` stride table, `def(i) = t + 8 + i*S32(t)` (Level_GetLightDef [0x0028AFE8]). No positions.
- **Light volumes**: placed, per cell (section 8).
- Scene light: `Gv.Light_AmbientColor 0x800D0EE0`, directional colour 0x800D0EE8, direction 0x800D0EF0 (bytes). The LevelHeader is `U32(0x8013B138)`.

---

## 10. Flyby / cinema camera points

- Control block: `ctl = U32(Gv.FlybyCtlPtr 0x800E2830)`.
  - `+0x38` IsActive, `+0x4` PathDataPtr (camera spline tree, valid **only while active**), `+0x1C` clock.
  - `+0x70` RecordId, `+0x80` RecordName (string), `+0xF0` AnchorPos, `+0xFC` AnchorYaw.
- The index (76-byte rows: `+0 level id, +4 trigger key, +8 flyby id, +0xC flags, +0x18 name`) is **not resident**. `Flyby_QueueById` [Flyby.cs] loads it on demand through `ResourceCache.Hash` from `U32(0x8011FEFC)` (LevelMgr+0x74, CinemasIndex). An overlay can only show the current flyby, unless it calls the loader (side effects).
- Trigger records can start flybys (trigger record +8, section 6).
- Track events (`FlybyTrackEvent`: `+0 time, +4 table (0 entity / 1 region), +8 trigger record index`) live inside PathData.

---

## 11. Region layers (water/fog surface layers)

- Classes: section 6 of the level. `U32(LevelMgr+0x1B300)` points to the records (section data + 8), and `S32(LevelMgr+0x1B304)` is the count. RegionLayer_UpdateForCamera [0x0023AFE0] reads them as `D_8013B188[i]` *(record stride not checked)*.
- Class fields (`RegionLayerClass`): `+0 MaterialId, +8 ScrollSpeed, +0x10 AnimRate, +0x14 BaseHeight, +0x20 FadeRate, +0x29 HeightFlags`.
- The active layers are per camera (Camera_InitRegionLayerPool). They have only a height (`+0x210` HeightKey), no xyz position, so they are hardly useful as markers. The region record +0x20 (surface height) is the better water marker.

---

## 12. Sound

- No placed sound-emitter objects were found. Ambient sound is a per-zone id (sound manager `0x8010C080` +0x134) that comes from region events. Positional sounds come from actor/anim events.
- Running loop sounds: `Gv.LoopSound_Pool 0x8011F260`. Active list head `U32(0x8011F260 + 0x14)`, next at `node+4`. 32-byte nodes: `+8 handle, +0xC sound id, +0x10 Vec3f pos` (position when started).
- Voices: `SoundVoice +0x44 Vec3f Position` (voice array inside the sound manager; layout not traced).

---

## 13. Live actors, players, pickups

| list | how to walk | entry |
|---|---|---|
| **Players** | `p = U32(Gv.PlayerListHead 0x80145060)`, `p = U32(p + 0x16E0)` | stride 0x16E8. The array is `U32(0x80145044)`, count `S32(0x80145048)` |
| **Entities** | section 1 | 0x2E8 |
| **ActorPool** (dynamic: drops, projectile-like actors, spawned items) | `a = U32(Gv.ActorPool_UsedList 0x8013B124)` (= ActorPool+0x5F14), `a = U32(a + 0x2EC)`. 32 slots of 0x2F8 at 0x80135210 | Actor header |
| **PickupPool** (dynamic pickups from ItemSpawners and drops) | `e = U32(0x80131600 + 0x3C04)` (active head), `e = U32(e + 0x1DC)`. 32 slots of 0x1E0 at 0x80131600 | `+4 s16 TypeIndex, +8 pos, +0x18 def, +0x1C8 TimeLeft, +0x1D0 bit 1 in use` |
| per-frame culled lists in Level | `+0xC50` Actors[128] / count `+0xE50`. `+0xE54` Class1 (AI) [64] / `+0xF54`. `+0xF5C` colliders [32] / `+0xFDC`. `+0x144` always-active [512] / `+0x944`. `+0x948` visible [128] / `+0xB48` | pointers. Reset at the start of UpdateFrame. AlwaysActive, Prop and Translucent counts are zeroed again at its end |
| Class 13 / 14 entity ptr lists | `+0x1B620`[16] / count `+0x1B660`, `+0x1B664`[16] / count `+0x1B6A4` | entity ptrs |

**Player fields:**

| off | field |
|---|---|
| +0x8 | Position |
| +0x6C | yaw |
| +0x2E8 | the player's weapon/body actor (second Actor) |
| +0x5D0 | CameraMode (1 = eye, 2 = orbit) |
| +0x5D8 | settings/entry record (`+0x80` class, `+0x84` **name string** (Settings_PlayerRecords_Name = record+0x84 *(uncertain that +0x5D8 points at exactly that 150-byte record, the offsets match)*, `+0x8F` monkey, `+0x92` team) |
| +0x5DC | **ViewPtr (Camera)** |
| +0x5E0 | character id |
| +0x5E4 | health (1/256) |
| +0x5EC | spawn key |
| +0x6F4 | body height |
| +0x1450 | IsComputer |
| +0x1454 | AI brain (section 3) |

Pickup type label: `typeId = U16(U32(L+0x94) + 8 + 2*S16(pick+4))`.

---

## 14. Camera, view and projection (for world -> screen)

**Which cameras exist (verified):**
- `Gv.Camera_ListHead 0x801450A8` = first active view camera, next at `cam+0x4`. Level_StreamVisibleCells and Video_SetViModeAndViewports walk this list.
- `Gv.ViewMgr 0x80145088`: `+4` Views array (n x 0xED8 = 3800 bytes), `+8` view count. `+0x40` = `Gv.Camera_MainView 0x801450C8` is the default / single camera.
- Per player: `cam = U32(player + 0x5DC)`.
- `cam+0x7C` IsActive.

**Camera fields (verified in Camera_UpdateQuaternion [0x002349B0], Camera_SetupProjection [0x00234FDC], C of func_00234FDC, Camera_ProjectPoint [0x00239234]):**

| off | content |
|---|---|
| +0x28/+0x2C/+0x30/+0x34 | Yaw, Pitch, YawOffset, Roll (radians) |
| +0x38 Vec3f | tracker position (eye base), +0x44 EyeHeight, +0x48 quat Rotation |
| +0x58 | floor face under the camera |
| +0x68 u16 | perspNorm. **+0x6C fovy (degrees)**, **+0x70 aspect** (= +0x29C / +0x2A0) |
| +0x128 Vec3f | **EyePosition** (world, extracted from +0x160) |
| +0x140/+0x150 | view quaternions |
| +0x160 Mtx (float 4x4) | **camera -> world** (rotation x translation(eye)). Camera space looks down -Z |
| +0x1A0 Mtx | inverse of +0x160 *(called with an inverse helper, not separately checked)* |
| +0x1E0 Mtx (float 4x4) | **world -> clip** = view x guPerspectiveF(fovy +0x6C, aspect +0x70, near **16.0**, far +0x528, scale) x scale(-1, 1, 1). This is exactly what the RSP gets |
| +0x220 Mtx | world -> eye view matrix (copy) |
| +0x260 | frustum pyramid points (5 Vec3f, apex first). +0x2F0 planes, +0x368..+0x37C AABB |
| +0x29C/+0x2A0/+0x2A4/+0x2A8 | viewport width, height, x, y (pixels, floats) |
| +0x2B0 | `Vp vp[2]` (N64 viewport, double-buffered by `Gv.FrameParity 0x800D297C`): `s16 vscale[4]`, `s16 vtrans[4]`, in 1/4 pixels |
| +0x380 | `Mtx[2]` N64 s15.16 fixed version of +0x1E0 (index FrameParity), loaded with gSPMatrix(PROJECTION) |
| +0x528 | far distance |
| +0x110/+0x114 | viewport scale factors multiplied into vscale *(normally 1)* |

**Matrix layout (verified, `MathUtil.MtxTransformPoint` [0x00272908], `MtxMakeTranslate` [0x00272CD0], `MtxTransformPointProject` [0x00272A80]):**
- Float `m[row][col]` at `+row*16 + col*4`, row-vector convention.
- `p' = [x y z 1] * M`, with the translation in row 3 (`+0x30, +0x34, +0x38`).
- Projection: `clip = [x y z 1] * M(+0x1E0)`, then divide by w (column 3).

**Projection recipe:**
- The game's own `Camera_ProjectPoint` uses the whole screen (`Gv.ScreenWidth 0x800E28D0`, `ScreenHeight 0x800E28D4`).
- For split screen, use the view's viewport. View_SetupViewport [0x00235AD0] sets vscale = 2*w*s and vtrans = (vscale/4 + x)*4.
```csharp
static bool Project(uint cam, float x, float y, float z, out float sx, out float sy) {
    uint m = cam + 0x1E0; float F(int r, int c) => Mem.F32(m + (uint)(r * 16 + c * 4));
    float cx = x*F(0,0) + y*F(1,0) + z*F(2,0) + F(3,0);
    float cy = x*F(0,1) + y*F(1,1) + z*F(2,1) + F(3,1);
    float cw = x*F(0,3) + y*F(1,3) + z*F(2,3) + F(3,3);
    sx = sy = 0; if (cw <= 0.01f) return false;            // behind the eye
    float nx = cx / cw, ny = cy / cw;
    uint vp = cam + 0x2B0 + (uint)(Mem.S32(Gv.FrameParity) * 16);   // or use +0x29C..+0x2A8 floats
    sx = (Mem.S16(vp + 8) + nx * Mem.S16(vp + 0)) / 4f;     // N64 RSP mapping, framebuffer pixels
    sy = (Mem.S16(vp + 10) - ny * Mem.S16(vp + 2)) / 4f;
    return nx >= -1 && nx <= 1 && ny >= -1 && ny <= 1;
}
```
- Equivalent with the floats: `sx = vpX + vpW/2 * (1 + nx)` and `sy = vpY + vpH/2 * (1 - ny)`.
- Coordinates are in **framebuffer pixels** of the game's 4:3 screen.
- The C# presentation layer (Runtime/Widescreen.cs + GfxRenderer) re-scales every perspective draw about the centre of its view (scissor rectangle) to the window aspect (Hor+/Vert+). A 2D overlay must apply the same rescale, or be drawn through the renderer's perspective path with the camera's +0x380/+0x1E0 matrix.

**World units:**
- +Y is up: gravity, eye height and floor y all act along y.
- Constants built on 10.24 are everywhere: 10.24, 20.48, 51.2, 102.4 (prop default half-box and Toggle height), 122.88 (pickup / tag touch radius), 204.8 (camera placed 204.8 above the start entity), 512, 1024.
- In the Acclaim Turok engine this is the usual "1 foot = 10.24 units" scale *(not proven from this code; check by reading a player's BodyHeight at +0x6F4 at runtime)*.
- Angles are radians in floats, and s16 angles x 2pi/65536.
- Collision vertices are stored as ints x 0.029296875 (= 30/1024).

---

## 15. Other level resources (no positions, or not useful as markers)

| where | content |
|---|---|
| +0x70 | particle swooshes |
| +0x74 | cinemas index |
| +0x98 | binaries keys |
| +0x9C | graphic-set types |
| +0xA4 | curves (rumble envelopes) |
| +0xA8/+0xAC | rumble types / rumble defs (RumbleSource at runtime: +0xC Vec3f position, +0x18 radius) |
| +0xB0 | link-group types (trigger counters) |
| +0xB4 | particle effects |
| +0x80 | save-data bitmaps |
| +0x84 | entity flag bitmaps |
| +0x8C | PVS index |
| +0x1B2B0 | level header |
| +0x1B450 | MapModels (5 x 28) |
| +0x1B08 | particle pool (runtime effects) |
| +0x128 | matrix stack |

**Level section map** (Level_Load, `Chunk(+0x60)` index):

| section | content |
|---|---|
| 0 | header |
| 1 | collision |
| 2 | paths |
| 3 | entities |
| 4 | cell grid |
| 5 | cells index |
| 6 | region-layer classes |
| 7 | triggers |
| 9 | nav nodes |
| 10 | nav edges |
| 11 | nav visibility |
| 12 | PVS |
