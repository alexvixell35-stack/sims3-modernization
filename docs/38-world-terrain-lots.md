# 38. World, terrain and lots (M-CORE1)

PLAN item 7, row M-CORE1. Static map of the world manager, the terrain (height field, collision cell grid, chunk LOD) and the lot object in Steam TS3W.exe 1.67 (image base `0x400000`, absolute addresses). Written 2026-10-15 from a Ghidra headless pass on a private copy of the M-PHY project (`game-copy/ghidra-core1`, our symbol CSV imported first); the game was not run. Costs come only from the existing `probe-out/profile/*simstack*.jsonl` files.

Tags: **[verified]** = read in the decompile or disassembly; **[measured]** = from the sample files; **[inferred]** = reasoning, still to confirm. Builds on docs/28 §5 (lot streaming, G13), docs/20 §4b (height cache, H5) and docs/33 (physics). Nothing here repeats their tables; it adds the world and terrain side and corrects two old names.

## 1. Summary

- **Two owners, two threads.** `WorldManager::Update` `0xC6D570` runs on the **main thread** (docs/28 §5.2) and does three things each frame: terrain chunk LOD and rebuilds (`WorldTerrain_Update` `0xC845C0`), lot scoring and `SetActive` posts (`0xC6C290`), and the sky/sea/weather/cloud updates. The lot objects themselves are loaded, saved and filled on the **sim thread** (IC calls, remote-call pump). World load and lot removal hop to the main thread with **synchronous** remote calls [verified].
- **The terrain collision cells are swapped on the main thread with no lock, while the sim thread reads them.** `WorldTerrain_Update` -> `0xC83200`/`0xC84090` -> `WorldCellGrid_SetCellShapes` `0xC2A840` releases the cell's shape (`cell+0x38`) and installs a new one; `WorldCellGrid_Raycast` `0xC2A570` (route planning, sim thread) reads `cell+0x38` and calls it through its vtable with no lock and no reference [verified code; the overlap in time is inferred, see §6]. This is a use-after-free window and a possible crash source while the camera moves.
- **The collision shape of a terrain cell depends on the chunk's LOD:** the heightfield builder `0xC83200` installs a `HeightFieldShape` (`0x101 x 0x101` grid over the shared height map), the kd-tree builder `0xC84090` a `KDTreeShape` over two freshly built kd-trees [verified]. So the cost of a route-planning height query depends on where the camera is [inferred], and `[HeightCache]` sees a rebuild each time a chunk changes LOD (64 in a 7-minute BP run, PLAN H5 [measured]).
- **Sim-thread cost of this area is small and spread out:** ~4.0% self of sim samples in all world/lot/terrain code, of which the named hot spots are lot world-to-local transform `0xAB8540` 0.60% (two x87 `fsin`/`fcos` per call), lot find-by-id `0xC60D30` + `SceneNode_FindRecursive` 0.38% (linear walk over the world's lot tree), lot grid lookup 0.22%, room/tile lookup 0.15% [measured, §7]. Almost all of it is reached from route planning and C# world queries, not from streaming.
- **Lot streaming moves objects in and out of the scene only** (docs/28 §5.1, confirmed): every lot's objects stay loaded from `Lot::Load` until the world unloads.

## 2. Function list (named in this pass)

New names (rows tagged `<docs/38>` in `tools/ghidra/symbols/ts3w-1.67.symbols.csv`). Thread = where the verified call chain runs.

| Address | Name | Thread | What it does |
|---|---|---|---|
| `0xC7E350` | `WorldService_Register` | main (app init `0xEC9680` <- `App_StartupFlags 0xECB600`) | Allocates `World/Service`, registers hash `0x9B8E862F`, vtable `0x107A358` [verified] |
| `0xC7E3C0` | `WorldService_Update` | main | Vtable slot `+0x1C`. If `[0x11ECBC4]+0x41` (world loaded): `WorldManager_Update`, then `0xC7CEA0` (world render manager), locomotion `0x83ABC0`, routing world `0x818EA0`, `0x740100`, SpeedTree wind `0x712F00` [verified] |
| `0xC6D570` | `WorldManager_Update` (existing) | main | §4 |
| `0xC6C290` | `WorldManager_ScoreAndStreamLots` | main | Lot scoring and `SetActive` (docs/28 §5.2 step 2) |
| `0xC62D80` | `WorldManager_LotScore` | main | Squared distance camera-to-lot (lot centre, or lot box when `[0x11843F8]` is set), minus `Active Lot Bias` `WM+0xE0` when the lot is active [verified] |
| `0xC69FF0` | `WorldManager_AnyActiveLotPending` | main | True when an active lot still waits on the renderer (`0xD432E0`, `0x700DC0`) [verified calls; meaning inferred] |
| `0xC6A080` | `WorldManager_ActivateAllLots` | main | Taken instead of scoring when `WM+0xD8` is set; calls `Lot_SetActive` per lot [verified call; trigger inferred] *[Corrected 2026-11-26, Boundless Atlas docs/021 §11: `0xC6A080` deactivates all lots; "ActivateAllLots" is the wrong name]* |
| `0xC6CF80` | `WorldManager_LoadWorld` | sim (from `IC_LoadSaveManager_LoadGame_Impl 0x742AC0` -> `0xAB14B0` -> `0xAACF60` -> `0xAABCF0`), or main at startup (`0xEC7140`) | §5 step 1 |
| `0xC65E00` | `WorldManager_CreateLotsFromLayer` | caller | Reads the layer file, allocates each `World/Lot` (0x4C0 bytes) and `Lot/ObjMgr - Layer` groups, pushes them into `WM+0x9C` [verified] |
| `0xC678D0` | `WorldManager_LoadLots` | caller | Per lot: `Lot::Load` through vtable `+0x18`, stream adaptors, missing object managers [verified] |
| `0xC65A20` | `WorldManager_RemoveLot` | caller | Removes the lot from `WM+0x9C`, then `WorldManager_DetachLot` when asked [verified] |
| `0xC65830` | `WorldManager_DetachLot` | caller, two hops to main | World event `0xE` (lot removed), lot vtable `+0x14` (unload), posts `WorldTerrain_OnLotChanged` and runs `WorldTerrain_ForceUpdate` on the main thread **synchronously** [verified] |
| `0xC6B780` | `WorldManager_Shutdown` | caller | World unload: weather off, `World/ShutdownFence`, removes every lot from the back, releases the world DBs [verified] |
| `0xC60D30` | `WorldManager_FindLotById` | any | `SceneNode_FindRecursive` over every root in `WM+0x9C`; linear, recursive through vtable `+0x40` (type), `+0x58` (child count), `+0x4C` (child) [verified] |
| `0xC688A0` | `WorldManager_LotGridLookup` | any | Lot at (x, z) through a hash grid (`WM+0x1D0` cell size, origin `WM+0xB4/+0xB8`, table `WM+0x1D8`), rebuilt lazily when `WM+0x1CC` is set [verified] |
| `0xC6A250` | `World_GetHeightAt` | any | General height: lot floor `0xAB8EE0`, foundations `0xB06A10`, water `0xB793F0`/`0xB745A0`, then terrain `0xC2A570` (docs/20) |
| `0xC671E0` | `WorldManager_Init` | main (`0xEC7140`) | Constructor-side setup: lot lists, partitions, houseboat lots, "Streaming" live settings (docs/28 §5.4) [verified tags] |
| `0xC6AEB0` | `World_PostLoadInit` | main (posted sync by `0xC6CF80`) | Forced terrain update `0xC845C0(..., 1)`, ponds, world render manager `0xC65030` [verified] |
| `0xC64950`, `0xC64B90` | `RemoteCall_PostSync`, `RemoteCall_PostSyncRelease` | caller | Remote method call to selector `arg0` (0 = main) with async 0, so the caller waits (`0x7D98C0`) [verified] |
| `0xC649A0` | `RemoteCall_PostAsync` | caller | Same with async 1 [verified] |
| `0xABE970` | `Lot_PostRemoteCallSync` | caller | Selector + sync wait; used for `Lot_CreateRenderer` and `WorldTerrain_ForceUpdate` [verified] |
| `0xABE9C0` | `Lot_PostRemoteCallAsync` | caller | `Services/RemoteMethodCall` (0x20 bytes, vtable `0x10650C4`), async 1 (docs/28 step 3) [verified] |
| `0xAC20E0` | `Lot_SetActive` | main (scoring) or tools | docs/28 step 3. Other caller: `0xAE6110` <- `0xD59C10` (vtable `0x1088F48`) [verified xref; what it is: to find] |
| `0xAB4C00` | `Lot_RemoveLotObjectsFromScene` | sim | Object event `0xC` per object [verified] |
| `0xABFAC0` | `Lot_UpdateObjectSceneNode` | sim | docs/28 step 6 |
| `0xAB1860`, `0xAB1870` | `Lot_IsActive` (`+0xC1`), `Lot_GetStreamPriority` (`+0xC4`) | any | Getters [verified] |
| `0xAC19F0` | `Lot_ctor` | caller | §3 [verified] |
| `0xAC1EB0` | `Lot_dtor` | caller | Stores the lot vtables back [verified data refs; role inferred] |
| `0xACF9D0` | `Lot_Load_Thunk` | caller | Lot vtable `0x1065268` slot `+0x18`; jumps to `Lot_Load` [verified] |
| `0xACF520` | `Lot_Load` | caller (sim at world load) | §5 step 3 |
| `0xACF170` | `Lot_FinishLoad` | caller | Under the lot's critical section `+0xD0`, once (`+0xC0`): object layer, managers, routing lot setup, lot message `0x421EECC` [verified] |
| `0xACE0D0` | `Lot_LoadObjectLayer` | caller | `Lot/ObjectLayer` (0x58 bytes) at `+0x2D0`, objects through vtable `+0x24` (`0xC70320`, "World/ObjMgr/objs"), then `Lot_AddLotObjectsToScene` inline [verified] |
| `0xAB4A20` | `Lot_CreateRenderer` | **main** (posted sync from `Lot_Load` and lot creation ICs) | `Lot/LotRenderer` at `+0x364` (docs/28 §5.3) |
| `0xACBF50` | `Lot_Save_Slot` | sim (save) | Lot vtable `+0x1C`; calls `Lot_Save` [verified] |
| `0xACB8A0` | `Lot_Save` | sim | Header, floor manager, imposter (`0xAE0540`, docs/25), data layers `0xAC5E30`, objects `0xC6ED40`/`0xC6EFA0` [verified] |
| `0xAB8540` | `Lot_WorldToLocal2D` | any | (x, z) into lot space: origin `+0x190`/`+0x198`, angle `+0x1A0`, one `fsin` and one `fcos` per call [verified] |
| `0xC82330` | `WorldTerrain_ctor` | main (world init) | `Terrain/WorldTerrain` (0x1B0 bytes) at `WM+0x58`; chunks, collision shapes, lot coverage map [verified tags] |
| `0xC845C0` | `WorldTerrain_Update` | main | §4.2 |
| `0xC85250` | `WorldTerrain_ForceUpdate` | main | `WorldTerrain_Update(camera, forced flag +0xF4)` [verified] |
| `0xC85270` | `WorldTerrain_RebuildAllChunks` | main (world init, `0xC85360`) | Every chunk: kd-tree build, or physics shape swap through `PhysicsWorld_RemoveShape`/`AddShape` (marshalled, docs/33) [verified] |
| `0xC60CF0` | `WorldTerrain_OnLotChanged` | main (posted) | Lot coverage `0xC80DB0`, `0xC7E680`, optional forced update [verified] |
| `0xC83200` | `TerrainChunk_BuildHeightFieldShape` | main | Chunk mesh buffers + `Terrain/Normal` texture; cell shape = height field (`0xC2A840(x, z, 0, 0, bounds)`) [verified] |
| `0xC84090` | `TerrainChunk_BuildKDTreeShape` | main | Two `Terrain/KDTree` (0xB0 each) built by `0xC26810`, vertex/index buffers; cell shape = kd-tree [verified] |
| `0xC2A840` | `WorldCellGrid_SetCellShapes` | main (live), main or sim (world init) | §4.3 |
| `0xC2A040` | `WorldCellGrid_CloneCellShape` | main | Cell shape vtable `+0x08` into a caller pointer (used to feed the physics world) [verified] |
| `0xC2A4B0` | `WorldCellGrid_MarkCellDirty` | main | `cell+0x4C = 1` [verified] |
| `0xC2A290` | `TerrainData_LoadHeightMap` | main | `Terrain/TerrainData/HeightMap` (100 bytes, refcounted) at `+0x08`, read from resource `0x2AD195F2` [verified] |
| `0xC2CCD0` | `TerrainData_ctor` | main (world init) | Chunk grid, chunks, colour maps, terrain types [verified tags] |

**Old names corrected (kept to avoid churn; comments only):**
- `World_LotStreaming` `0xC622D0` is **world init on the main thread** (weather, `Terrain/WorldTerrain`, sea, sky, clouds, terrain data, road network, routing setup), posted synchronously by `WorldManager_LoadWorld` at `0xC6D16E` [verified]. It does no lot streaming.
- `WorldManager_LotRebuildCaller` `0xC6BCC0` is the **terrain and routing part of the world save** (called from `World_Save` and `IC_World_BuildRoutingDataImpl`): terrain lot data `0xC2CE90`, terrain write `0xC2AEF0`, routing world save, rebuild of dirty lots' routing, then two synchronous calls to the main thread (`0x7D98C0(0, 0)`) [verified].

## 3. The lot object (`World/Lot`, 0x4C0 bytes)

Allocated in `WorldManager_CreateLotsFromLayer` `0xC65E00`; vtable `0x1065268` (second vtable `0x106525C` at `+0xA0`). Offsets from `Lot_ctor`, `Lot_Load`, `Lot_FinishLoad`, scoring and save [verified unless marked].

| Offset | Field |
|---|---|
| `+0x00` | vtable: `+0x14` unload `0xAC01E0` (called by `WorldManager_DetachLot`; role inferred), `+0x18` load thunk `0xACF9D0`, `+0x1C` save `0xACBF50`, `+0x24` load objects `0xC70320`, `+0x28` object groups save `0xC6F520` (docs/28 E3), `+0x40` node type (1 = lot, 2 = group) `0x619EF0`; `+0x5C` is a folded no-op stub |
| `+0x0C` | World/log owner (save log `0xC67EA0`) |
| `+0x14`..`+0x18` | Object pointer vector (streaming adds/removes these) |
| `+0x48`/`+0x4C` | Lot id (64-bit) |
| `+0x70` | Lot package/DB handle (refcounted); `+0x74` loaded flag |
| `+0x78` | Lot file version (`u16`; gates upgrades in `Lot_FinishLoad`, e.g. `< 0x39`, `< 0x45`) |
| `+0xC0` | Finish-load done (once); `+0xC1` active (streamed in); `+0xC4` stream priority (3 = camera lot, 4 = current lot, scoring); `+0xC9` excluded from streaming |
| `+0xD0`, `+0xF0`, `+0x110` | Three `CRITICAL_SECTION`s; `+0xD0` guards `Lot_FinishLoad` [verified], the users of the other two are not traced |
| `+0x134`..`+0x148` | Lot bounds (min x/z, max x/z) used by scoring |
| `+0x174`/`+0x178` | Lot size in tiles (int) |
| `+0x190`/`+0x198`/`+0x1A0` | Lot origin x, z and rotation angle (`Lot_WorldToLocal2D`) |
| `+0x290` | `Lot/FloorManager` (0x230 bytes) |
| `+0x298`, `+0x2A8`, `+0x2B4`, `+0x2BC`, `+0x2C0`, `+0x2C8`, `+0x2D8`, `+0x2E4`, `+0x2E8` | Per-lot managers (partition, terrain/height, modular stairs, pool, platform, pond, room, ...) [inferred from the tags of the functions called on them] |
| `+0x2D0` | `Lot/ObjectLayer` |
| `+0x2DC` | `Lot/mpPendingSceneNodeManager` (0x20 bytes) |
| `+0x364` | `Lot/LotRenderer` (main thread; imposter while inactive) |
| `+0x39C` | Physics groups per level |
| `+0x3A8` | Lot resource key/DB for the object load |
| `+0x408`/`+0x40C` | Lot type pair checked before routing setup (`1`, `0x50`) [verified; meaning to find] |
| `+0x4BC` | Skip flag in the world terrain save |

**World manager fields** (besides docs/28 §5.4): `+0x41` world loaded, `+0x46` save dirty, `+0x58` world terrain, `+0x60` sea, `+0x64` sky, `+0x68` clouds, `+0x74` weather, `+0x80`/`+0x84` world and global DBs, `+0x98` pending command (exchanged atomically each update), `+0x9C`..`+0xA0` all lot roots, `+0xAC`/`+0xB0` current lot, `+0x1B4` mode (0 live, 1/2 edit modes), `+0x1CC` lot grid dirty, `+0x204`..`+0x208` streamable lot list, `+0x310` 10-entry camera speed ring, `+0x3A0` last camera position [verified offsets; mode values inferred].

*Correction 2026-11-26 (Boundless Atlas `docs/020-p1-paged-lot-probe.md`): `WM+0xB0` is the world's global object layer, not a lot; the "`+0xAC`/`+0xB0` current lot" reading above is stale. Lot id sits at `lot+0x48`, object pointers at `lot+0x14..+0x18`.*

## 4. Terrain: data structures and queries

### 4.1 Objects

| Object | Where | Contents |
|---|---|---|
| Height map | `TerrainData+0x08`, `Terrain/TerrainData/HeightMap` | Shared height samples; height-field shapes point into it, no copy (`0xC29B80`) [verified] |
| World cell grid | `ecx` of `0xC2A570`/`0xC2A840` | `+0x34` world top (int), `+0xBC` cell pointer array, `+0xCC` x `+0xD0` cells, cell = 256 world units (`>> 8`) [verified, docs/20] |
| Cell | `[grid+0xBC][cz*W+cx]` | `+0x00`/`+0x04` origin, `+0x20`..`+0x2C` bounds, `+0x30` and `+0x34` render kd-trees (refcounted), `+0x38` collision shape (`KDTreeShape` 0x70 bytes, vtable `0xFEFA3C`, or `HeightFieldShape` 0xA0 bytes over `0x101 x 0x101` samples), `+0x4C` dirty [verified] |
| World terrain | `WM+0x58`, 0x1B0 bytes | Chunk vector `+0xB0`..`+0xB4`, camera position `+0x110`, LOD ring sizes `+0xF8`/`+0x100`, dirty rect `+0x148`..`+0x154`, QPC timer `+0x158`..`+0x174`, flags `+0x140` [verified] |
| Chunk | element of `+0xB0` | `+0x04`/`+0x08` cell coords, `+0x0C`/`+0x10` world position, `+0x5C`/`+0x60` current/target LOD (1, 3, 4), `+0x70` physics-world shape, `+0x78` medium-LOD render target [verified offsets; LOD numbering inferred] |

### 4.2 `WorldTerrain_Update` `0xC845C0` (main thread, every frame)

Called from `WorldManager_Update` when `WM+0x58` exists, with "forced" when the camera is below `Terrain Height Thresh` `WM+0xE8` or `WM+0xDA` is set [verified]. It snaps the camera to the chunk grid, picks a LOD per chunk by distance, and for chunks whose LOD changed builds new meshes and, through `0xC83200` (height field) or `0xC84090` (kd-tree), a new cell collision shape; it also marks routing cells dirty (`0xB789B0`, `0xB78AA0`, `0xB7F640`) [verified calls]. A QPC timer (`+0x158`..`+0x174`) rate-limits one branch [verified QPC read; what it limits not traced]. All allocations (`Terrain/VertexBuffer`, `IndexBuffer`, `KDTree`, `Texture`, `Normal`) happen here, on the main thread, as the camera moves [verified tags].

### 4.3 `WorldCellGrid_SetCellShapes` `0xC2A840`

For cell (x >> 8, z >> 8): release the old collision shape (`PhysShape_Release`, `cell+0x38 = 0`), swap the two render kd-trees with refcounts, then allocate and store the new shape: `KDTreeShape` when a kd-tree is given, else a `HeightFieldShape` from the height map [verified]. No lock, no generation; readers see the slot empty, the old pointer (possibly freed) or the new shape.

### 4.4 Queries

| Query | Path | Threads | Notes |
|---|---|---|---|
| Terrain height | `WorldCellGrid_Raycast` `0xC2A570`: ray down from the world top through `cell+0x38` (vtable `+0x10`) | sim (route planner, C# ICs), main (weather `0xD6E350` -> `0xD669D0` -> `0xC61200`, kd-tree builder `0xC26810` -> `0xC87420`) [verified xrefs] | `[HeightCache]` covers it (docs/20 §4b) |
| Height at a point | `World_GetHeightAt` `0xC6A250`, `Terrain_GetHeight` `0xC6A540` | sim mostly | Lot floor -> foundation -> water -> terrain; each lot step first runs `WorldManager_LotGridLookup` and `Lot_WorldToLocal2D` |
| Lot at a point | `WorldManager_LotGridLookup` `0xC688A0`, `World_LotLookup` `0xC68990` | any | Hash grid, lazy rebuild under `WM+0x1CC` (no lock) [verified] |
| Lot by id | `WorldManager_FindLotById` `0xC60D30` | any | Linear recursive walk of the whole lot tree per call [verified] |

## 5. Lot lifecycle

| Step | Function | Thread | Notes |
|---|---|---|---|
| 1. World load | `IC_LoadSaveManager_LoadGame_Impl` `0x742AC0` -> `0xAB14B0` -> `0xAACF60` -> `0xAABCF0` -> `WorldManager_LoadWorld` `0xC6CF80` | **sim** (IC) [verified chain] | Startup and front-end path through `0xEC7140` (`0xC6D970`) runs on main [inferred thread]. Travel reloads through the same `LoadGame` path [inferred] |
| 2. World init | `0xC6CF80` posts `World_LotStreaming` (= world init) `0xC622D0` with `RemoteCall_PostSync(0, ...)` | **main, sim waits** [verified sync] | Terrain, sea, sky, weather, road network; `WorldTerrain_RebuildAllChunks` builds every chunk's kd-tree here |
| 3. Lot create + load | `WorldManager_CreateLotsFromLayer` `0xC65E00`, `WorldManager_LoadLots` `0xC678D0` -> vtable `+0x18` -> `Lot_Load` `0xACF520` -> `Lot_FinishLoad` `0xACF170` -> `Lot_LoadObjectLayer` `0xACE0D0` | sim | `Lot_Load` posts `Lot_CreateRenderer` to the main thread and **waits** (`0xABE970(0, ...)`, `0xACF862`) [verified]; the object layer adds all objects to the scene inline (`Lot_AddLotObjectsToScene`, docs/28 §5.1). One lot at a time, all lots of the world in one load |
| 4. Post-load | `World_PostLoadInit` `0xC6AEB0`, posted sync | main, sim waits | Forced terrain update, ponds, world render manager |
| 5. Activate / deactivate (live) | `WorldManager_ScoreAndStreamLots` `0xC6C290` -> `Lot_SetActive` `0xAC20E0` -> async post | main posts, **sim** runs adds/removes in the pump (`0xD81FC3`) | docs/28 §5.2-5.3; no time budget in the pump |
| 6. Live edits | Build/buy and lot ICs (`IC_World_CreateHouseboatLotImpl`, `IC_World_BulldozeLotImpl`, ...) -> lot rebuild paths -> `Lot_CreateRenderer` sync to main | sim, main waits | Not traced further |
| 7. Save | `Save_WritePackage` `0xAAC110` -> `World_Save` `0xC6D460` -> lot vtable `+0x1C` -> `Lot_Save` `0xACB8A0`; terrain/routing `0xC6BCC0` | sim (docs/25), with two sync hops to main inside `0xC6BCC0` [verified] | ~4 s per save (docs/28 E3) |
| 8. Remove / unload | `WorldManager_RemoveLot` `0xC65A20` -> `WorldManager_DetachLot` `0xC65830` (event `0xE`, lot `+0x14` unload, terrain update sync on main); world unload `WorldManager_Shutdown` `0xC6B780` removes every lot | caller (sim for ICs and world unload), main for the terrain part | Each removal waits on a main-thread terrain update [verified sync] |

## 6. Locks and shared state

| State | Writer | Readers | Protection | Risk |
|---|---|---|---|---|
| Cell collision shape `cell+0x38` | main: `WorldTerrain_Update` -> `0xC2A840` (live, on terrain LOD change) | sim: route planner and C# height queries through `0xC2A570`; main: weather, kd-tree builder | **none** (no lock, no reference held by readers; `0xC2A570` dereferences the vtable directly) [verified in `0xC845C0`, `0xC83200`, `0xC84090`, `0xC2A840`, `0xC2A570`; callees not followed] | Use-after-free if a sim-thread ray runs while the main thread replaces the same cell [inferred: needs both threads active at once, which is the normal multithreaded mode]. Window is short (one release/alloc), frequency = chunk LOD changes near routing Sims (64 per 7 min in BP [measured, H5]) |
| Cell render kd-trees `+0x30`/`+0x34` | main | main (render), the `KDTreeShape` (refcount via `0x67DF00`) | Interlocked refcounts | Safe as long as the shape holds its reference [inferred] |
| Physics-world terrain shapes (`chunk+0x70`) | main | sim | `PhysicsWorld_AddShape`/`RemoveShape` marshal to the sim thread (docs/33) [verified] | None |
| Lot grid `WM+0x1D8` (lazy rebuild on `WM+0x1CC`) | whoever queries first after a lot change (`WorldManager_DetachLot` sets the flag) | sim and main | rebuild `0xC68680` takes `[WM+0x228]`; its getter `WorldManager_GetLotList 0xC68960`, the dirty check, `0xC688A0` and the other readers take no lock [verified; corrects the earlier "no lock in `0xC68680`", docs/60 §1] | Readers race a rebuild on the other thread (vectors swapped under `[WM+0x228]` while an unlocked reader iterates) [inferred, rare: only right after a lot change; docs/60] |
| Lot `+0xC1` active flag | main (`Lot_SetActive`) | sim (add/remove run) | none; sim re-checks `+0xC9` only | Benign (docs/28) |
| Lot `+0xD0` critical section | `Lot_FinishLoad` | — | `EnterCriticalSection` | Only lot-local |
| World load / save / lot removal | sim | main | Synchronous remote calls (`0x7D98C0(0, 0)`) | The sim thread blocks until the main thread pumps; fine during loads, a hitch for live lot removals [inferred] |

## 7. Sim-thread cost (existing samples)

Method: docs/33 §8.7 sample split. 16 non-empty `probe-out/profile/*simstack*.jsonl` files, 10,251 samples (BP 4,077, SV 1,723, IP 4,451); top frame = self, mapped with the function list exported from the Ghidra project. 1% is about 10 ms/s of sim-thread time (docs/28). Small sample counts (11-62 per row), so treat each row as +/- 30% [measured].

| Block / function | Self BP / SV / IP | Pooled | Reached from |
|---|---|---|---|
| WorldManager/World code `0xC5F000-0xC7E700` | 0.61 / 0.70 / 0.92 | 0.76% | lot lookups below |
| Terrain data + cell grid `0xC22000-0xC32000` | 0.12 / 0.12 / 0.11 | 0.12% | (the ray itself is in physics, docs/33: 1.8%) |
| World terrain chunks `0xC7E700-0xC88800` | 0 | 0% | main thread only, as expected |
| Lot core `0xAA8000-0xAE8000` | 1.03 / 0.75 / 1.42 | 1.15% | `Lot_WorldToLocal2D` |
| Lot managers `0xA40000-0xAA8000`, `0xAE8000-0xC22000` (floors, rooms, roofs, roads, partitions) | 1.72 / 1.33 / 1.53 | 1.57% | C# world queries, routing |
| Build tools + catalog `0xC88800-0xD4B000` | 0.44 / 0.46 / 0.29 | 0.38% | event handlers |
| `Lot_WorldToLocal2D` `0xAB8540` | 0.42 / 0.41 / 0.85 | **0.60%** | `0xB06A10` <- route cost `0x627780`, `World_LotLookup`, height queries |
| `WorldManager_FindLotById` + `SceneNode_FindRecursive` | 0.29 / 0.29 / 0.49 | **0.38%** | routing `0x635440`/`0x6376D0`/`0x80C550`, tool events |
| `WorldManager_LotGridLookup` + `World_LotLookup` | 0.25 / 0.17 / 0.22 | 0.22% | height queries |
| Room/tile lookup `0xA8DF80` + `0xAB7ED0` | 0.20 / 0.12 / 0.11 | 0.15% | C# ICs |
| Height query wrappers `0xC6A250`/`0xC6A540`/`0xC2A570` | 0.05 / 0.17 / 0.20 | 0.14% | route planner |
| Lot streaming (`0xAC1130`, `0xABFAC0`, `0xAB4C00`, `0xABD3C0`) | 0 | 0% | none of the profiles panned the camera; still unmeasured (docs/28 §5.5, `[LotStream]` parked) |

Total self in world/lot/terrain code ~4.0% (~40 ms/s) [measured], spread over many small functions; no single fix moves more than ~0.6%.

## 8. Candidates

| # | Candidate | Covers | Effort | Risk | Verdict |
|---|---|---|---|---|---|
| C1 | **Guard the cell-shape swap**: detour `0xC2A840` to defer the release of the old `cell+0x38` shape by a frame or two (keep a small graveyard, free it later), or take a reader/writer lock shared with `0xC2A570` | crash on use-after-free while panning near routing Sims | small (two detours next to `[HeightCache]`, which already hooks both) | low | **Do first if a crash signature fits**: a crash in `0x67EDE0`/`0x67E2B0`/`0x6795D0` or a `PhysShape` vtable call with a freed `ecx`. Check `crash-ledger.ts` for such stacks before building [inferred] |
| C2 | Cache sin/cos in `Lot_WorldToLocal2D` `0xAB8540` (per-lot entry keyed on the angle bits at `+0x1A0`) | 0.60% (IP 0.85%) | small (one detour, pure function) | low | Good small win; same class as `[HeightCache]`. ~4-8 ms/s [inferred from the samples] |
| C3 | Id -> lot hash for `WorldManager_FindLotById` `0xC60D30`, rebuilt when `WM+0x9C` changes (`0xC65E00`, `0xC65A20`, lot creation ICs) | 0.38% | small-medium | low (invalidate on every lot add/remove) | Worth it with C2; ~3-5 ms/s [inferred] |
| C4 | Lot add budget: time-budgeted pump or object-chunked `AddLotObjectsToScene` (docs/28 §5.5) | unmeasured spike per activation | medium | medium (ordering, S3SS overlap) | Measure with the parked `[LotStream]` build first (queue `lotstream.json`) |
| C5 | Terrain LOD churn: hysteresis on chunk LOD so a camera hovering at a ring edge does not rebuild kd-trees back and forth | main-thread hitches (kd-tree build `0xC26810` and buffer allocs per change), fewer `0xC2A840` swaps (also shrinks C1's window and keeps `[HeightCache]` warm) | medium | low | Needs a main-thread sampler and a rebuild counter per second (the `[HeightCache]` `heightRebuilds` counter already exists) |
| C6 | Memory: terrain vertex/index buffers and kd-trees are allocated and freed on every chunk LOD change, on the main thread [verified tags] | address-space churn (Error 12 class) | measure | — | Count with `/rescache`-style memory lines while panning (M10 showed +22 MB worst case for a town pan, PLAN); only act if fragmentation shows |
| C7 | Lot object memory | — | — | — | Nothing to save: every lot's objects stay loaded by design; inactive lots only lose scene nodes. 0x4C0 bytes per lot is noise |

Offload view (docs/23 #17): the world/lot code has no work of its own to move off the sim thread; its sim cost is query work that moves with routing (N3). The main-thread terrain work is already off the sim thread. The synchronous hops (world load, lot removal, save) are waits on the main thread, not sim work.

## 9. Open items

- Runtime check for C1: count `0xC2A840` calls per thread id and overlap with sim-thread `0xC2A570` calls (a `[HeightCache]` counter extension), then try to reproduce with a camera pan over a busy lot.
- The other `Lot_SetActive` caller `0xAE6110` (vtable `0x1088F48`), the trigger of `WM+0xD8` (`WorldManager_ActivateAllLots`), and the users of the lot critical sections `+0xF0`/`+0x110`.

*Correction 2026-11-26 (Boundless Atlas `docs/021-s1-imposter-fog-camera.md` §1, §11 item 1): `0xC6A080` deactivates all lots (`Lot_SetActive(0)`; only the lot at `WM+0x170` gets 1, and only when `WM+0xDA` is set). The name "ActivateAllLots" is wrong.*
- What the QPC timer in `WorldTerrain_Update` limits, and the exact LOD ring rule (`+0xF8`/`+0x100`).
- Main-thread cost of `WorldManager_Update` and `WorldTerrain_Update` (needs the main-thread sampler, PLAN item 7 priority 0).

## 10. Method notes

- Tag scan first (`StringXrefs.java` with `World/|Lot/|Terrain/`): 969 distinct tags; grouping them by the second path segment gave the subsystems and their address bands in one pass (lot code `0xA44000-0xB7F000`, terrain `0xC22000-0xC32000` and `0xC7E700-0xC88800`, world manager `0xC5F000-0xC7E700`, build tools `0xC88800-0xD4B000`).
- New `tools/ghidra/CallerTree.java <out> <depth> <fn>...`: indented caller tree with call sites and vtable data refs, stopping at named functions; for an address with no function it prints the raw references (that found the lot vtable through `0xACF9D0`). It replaced most `ExtCallers` runs here.
- Threads came from the verified roots in docs/28 (main loop, IC = sim) plus the selector/async arguments of every remote-call wrapper (`0xC64950`, `0xC64B90`, `0xC649A0`, `0xABE970`, `0xABE9C0`): reading the wrapper once settles every post site.
- Several `-postScript` steps per headless run (strings, range map, decompile, caller trees) keep a run at ~2-3 min; the project open dominates.
