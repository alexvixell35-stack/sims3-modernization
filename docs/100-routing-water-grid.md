# 100. Routing I/O study G: the water grid, its marks, frozen ponds and houseboats

Written 2026-10-18. Static only: the game was not run, no probe, no native code. Follow-up to docs/91 §3.2 and §5 questions 1-2 and to the docs/94 gaps E26 (frozen ponds) and E28 (houseboat sailing). Goal: no unknowns left about the routing water/cell grid **G** (`world+0x8C`), so our own off-thread search can work on an exact snapshot of it.

Tools: Ghidra headless decompiles on a private copy of the docs/89 project (`game-copy/ghidra-io-g`, copied from `ghidra-usf`, read-only runs); capstone 5 on `game-copy/bin/TS3W.exe` (Steam 1.67) for code Ghidra never disassembled; a raw `.text` scan for `E8`/`E9` rel32 calls and for 32-bit displacements and immediates (scripts in the session scratchpad, not committed); the decompiled script DLLs under `game-copy/decompiled` for the managed side.

Tags as in AGENTS.md: **[verified]** = read in the disassembly or decompile (address given); **[inferred]** = reasoning, with the live check that closes it in §9.

## 1. Answers in short

> **Changes 2026-10-18** (docs/112 §3.1, §5.4; docs/108 §7): the load-time build runs on the **sim** thread in the BP run, not the main thread (item 1); the shoreline step rewrites R's **type bits** in the flag byte `+1`, not the surface byte (§3.2 step 8, §10); L1, L3 and L10 answered live (§9).

1. **G is built at every world load, on the main thread, by a message the static index missed** [verified]. (corrected 2026-10-18, docs/112 §5.4: the BP value-probe log shows the message path `0x835390` and the build on the **sim** thread, 688 ms BP / 505 ms IP, once per load [verified live, BP thread; IP time only]; `World_PostLoadInit` or at least its message send runs on the sim thread in this build.) The routing world has an EAMessage handler `RoutingWorld_OnMessage 0x835300` (vtable `0x102267C` slot `+4`) that Ghidra never turned into a function, so docs/91 found no load-time caller. `World_LotStreaming 0xC622D0` sends message `0x38E3C0E` at world-init start (handler: flag `world+0x983` = 1, "G needs a build"); `World_PostLoadInit 0xC6AEB0` (main thread, the sim waits, docs/38) **sends** `0x38E3C1E` at its end (`0xC6B076`, `g_MessageServer` vtable `+0x18`, synchronous on the caller); the handler then calls `WaterGrid_BuildFromWorld 0x82A030 -> WaterGrid_Build 0x6734A0` and clears the flag. Inputs: world bounds and sea height (world manager), terrain heightmap, road low bridges, world ponds, lot rectangles and corners, non-lot world objects' footprint polygons, R's waypoints (shoreline pass), and a hard-coded patch list for Al Simhara (§3). Docs/91 open question 1 and ledger row I-91-6 are answered.
2. **Later rebuilds are rare and flag-driven** [verified]: the same `0x82A030` runs on build/buy exit (`IC_RouteManager_OnExitBuildBuy -> 0x835240`), on message `0x6EF1610` (posted to the script message server from `ScriptHost_ScriptTick`, sim thread) and on the next `0x38E3C1E`, each **only while `world+0x983` is set**. After load the flag is set again only by message `0xCB484F51` from `0xC61FE0`, which lot and terrain edits reach (lot place, bulldoze, houseboat create/delete, `IC_World_LotSetTerrainHeightImpl`, `Lot_FinishLoad`, terrain tools) when the world manager's mode word `WM+0x1B4` is non-zero, once per latch `WM+0x110`. The older path `0x8349C0` (world save, `BuildRoutingData`, world-file save) rebuilds G only after an Edit Town lot change set `world+0x97C`.
3. **The "mark grids" are high-cost grids written by object footprints, and their state depends on history** [verified]: `0x6733C0` (from footprint insert `0x636BF0`) rasterises the outline of every polygon an object's footprint shape returns into 10 per-agent-class byte grids (`G+0x7C0`) and records the water cells it newly set; `0x670510` (from footprint remove `0x62D9F0`) clears only the recorded cells. Lot outlines are stamped into all 10 grids at build and never recorded. A G rebuild keeps the grid bytes (same size) but drops the records, so marks set before a rebuild are never cleared afterwards. A snapshot must copy these bytes; it cannot recompute them.
4. **The "boat-class grids" are five passability maps** [verified]: map k is 1 where the cell's class is above k; the class comes from the cell flags (land 0, shallow 1, near shore or medium depth or under a low bridge 2, large-houseboat clearance 4, open deep water 5). The water planner picks map 0 for swimmers, 1 for boats, 2-4 for small/medium/large houseboats (route option word `& 0xCF00` = `0x4100/0x4200/0x4300`), stepping down if the start cell is closed in the chosen map.
5. **A second footprint-driven structure is a search input too**: the `blockedNodeMap` hash at `G+0x35C` (reference count per 4 m cell of non-Sim footprints that block every agent), written by `0x66FB40`/`0x66F6D0` from the same insert/remove paths and read by the world A\* (`0x66F890`) and the shoreline exit lookup (`0x66F8D0`) [verified].
6. **Frozen ponds change no routing structure** [verified]. Walking on ice is a per-route choice: the script sets route option `EnablePondPlanning` (bit 61 of the 64-bit option) when `PondManager.ArePondsFrozen()` (`SeasonsManager` pond state `FrozenSolid`, freeze level >= 95 of 100) for human child-or-older Sims; skating uses a scripted custom path, not the search; native code only types path segments as `FrozenPondPath` (`RoutingJob_SplitSubPaths 0x81A340`) from search-node flag `0x200`. The weather IC that carries `PondFreezeAmount` (`IC_WeatherControl_SetSettings 0x7846E0`) reaches no routing code. Docs/94 gap E26 / I-94-8: closed (the season is a per-request input, not a world epoch).
7. **Houseboat lots never move in routing space** [verified]. A houseboat lot is created off the map, in 72 m slots past the world's far Z edge (`0xC6ACB0`), outside G; sailing only changes the lot's display transform (`Lot+0x444..+0x4AC`, written by `Lot_SetHouseboatParent 0xAB9C40`, read by the `World_*HouseboatDisplay*` ICs). Routes on a houseboat run in the lot's fixed frame. Docs/94 E28 "rebuild a sailing houseboat lot at each cut" is not needed; only create/delete (E15) and the boat object's own footprint events matter.
8. **Other water/terrain state in play**: no sea-level or tide setter exists in script (`World.GetSeaLevel` only); snow and weather settings do not touch routing (docs/94 E25 plus the `0x7846E0` closure); in-play terrain edits (`LotSetTerrainHeight`, used by the meteor crater) do not rebuild G by themselves (§6).

## 2. G layout (`world+0x8C`, at least 0x7EC bytes)

All offsets relative to G. Constructor `WaterGrid_ctor 0x671E30`, clear `WaterGrid_Clear 0x6714A0`, destructor `0x671FF0` [verified].

| Offset | Field | Notes |
|---|---|---|
| `+0x00` | built (byte) | 0 at clear, 1 after body labelling `0x671580` |
| `+0x01` | shorelines built (byte) | set by `0x66FD00` |
| `+0x04` / `+0x08` | width / height in cells | width = (maxX - minX) / 4 + 1, height = (maxZ - minZ) / 4, from `WM+0xB4..+0xC0` |
| `+0x0C` | cell count | width x height |
| `+0x10` | water cell count | decremented as cells turn to land |
| `+0x14` | water body count | 0 if no water |
| `+0x18` | shoreline entry count | copy of `+0x40` |
| `+0x1C..+0x28` | cell array (EASTL vector of u16, tag "Routing/waterMap") | see the cell format below |
| `+0x2C..+0x44` | `shorelineMap`: tree keyed by R waypoint (`chunk << 16 \| waypoint`) to (body id, deep water cell, water cell) | count `+0x40` |
| `+0x48..+0x60` | `reverseShorelineMap`: tree keyed by body id, same payload | count `+0x5C` |
| `+0x64..+0x7C` | `lotInternalShorelineMaps`: tree keyed by lot id to a list of (land cell, water cell) pairs | ponds and water inside lots |
| `+0x80`, `+0x174`, `+0x268` | three fixed vectors of 0x1C-byte cell-offset sets (`nodeOffsetsToCheckHouseboatSmall/Medium/Large`) | filled by `0x672480`; only the large set is read by the builder |
| `+0x35C..+0x378` | `blockedNodeMap`: hash from cell index to a byte reference count | buckets `+0x360`, bucket count `+0x364`, size `+0x368` |
| `+0x37C..+0x398` | 8 neighbour index deltas | `-w`, `-1`, `+1`, `+w`, `-w-1`, `1-w`, `w-1`, `w+1` |
| `+0x39C + 8 x id` | per body: seed cell, cell count | ids 1-0x7E; slot 0x7F (`+0x794/+0x798`) holds the overflow bucket; counts read by route cost modes via `0x66D8D0` |
| `+0x79C` | lot planner P | from the builder's caller |
| `+0x7A0` / `+0x7A4` | passability width / height | same as `+0x04` / `+0x08` |
| `+0x7A8` / `+0x7AC` | passability map count (5) / high-cost map count (10) | |
| `+0x7B0..+0x7BC` | passability maps: vector of 16-byte byte-vectors ("PathPlan/mPassabilityMaps") | §4 |
| `+0x7C0..+0x7CC` | high-cost maps: vector of 16-byte byte-vectors ("PathPlan/mHighCostMap") | §5 |
| `+0x7D0..+0x7E8` | `highCostFootprints`: tree keyed by (64-bit owner id, 64-bit footprint id) to the list of cells that footprint marked | count `+0x7E4` |

Cell format (u16) [verified in `0x6729D0`, `0x671580`, `0x66E040`, `0x66FD00`]:

- low byte: 0 = land; `0x80` = water not yet labelled (only during a build); 1-`0x7E` = water body id (flood fill, 8-connected, the smallest bodies merge into `0x7F` once 126 ids are used); `0x7F` = excluded (overflow bodies, and land cells next to a shoreline waypoint, set by `0x66FD00`).
- high byte flags: `0x01` lot covers the cell; `0x02` shallow (less than 0.75 below sea level); `0x04` and `0x08` (always set together by the builder) medium depth (0.75-1.5) or shallow, under a low bridge, or next to land; `0x10` the same cases plus land inside the large-houseboat clearance.

## 3. Who builds G, when, from what

### 3.1 Callers and triggers

> **Changes 2026-10-18:** load row thread corrected to sim (docs/112 §5.4).

| Trigger | Thread | Path | Condition |
|---|---|---|---|
| World load, end of post-load init | main (sim waits) (corrected 2026-10-18: **sim**, BP live, docs/112 §5.4) | `World_PostLoadInit 0xC6AEB0` sends `0x38E3C1E` -> `RoutingWorld_OnMessage 0x835300` -> `0x82A030` | `world+0x983` (set at construction and by `0x38E3C0E` from `World_LotStreaming 0xC622D0`) |
| Same message from `WorldManager_LoadWorld 0xC6CF80` (`0xC6D299`, `0xC6D335`, `0xC6D3B9`) | caller of the load | same | same |
| Build/buy exit | sim (script IC) | `LotManager.ExitBuildBuyMode -> Route.OnExitBuildBuy -> IC_RouteManager_OnExitBuildBuy 0x74B6B0 -> 0x835240 -> 0x82A030` | `world+0x97B` (in build/buy, set by message `0x4E11976`), planners ready (`+0x974`), `+0x983` |
| Message `0x6EF1610` | sim (script message server queue) | posted by `0x769950` from `ScriptHost_ScriptTick 0xD80BC0` together with World event 7 when a script-host counter (`+0xA2C`) runs out | `+0x983` |
| World routing data rebuild | sim / tool | `0x8349C0` from `WorldManager_LotRebuildCaller 0xC6BCC0` (world save, `IC_World_BuildRoutingDataImpl`), world-file save `0xC6CC40`, message `0x7FDA974` | `world+0x97C`, set only by `0x829500` (Edit Town lot place `0xD41630`, bulldoze and houseboat delete via `0xB04AF0`) and by that message |

What sets `world+0x983` after load: message `0xCB484F51`, posted to the script message server by `WorldManager_PostWaterGridDirty 0xC61FE0` (only if `WM+0x1B4` is non-zero and the latch `WM+0x110` was clear). Its callers [verified, caller tree]: `0xA94AB0` from `IC_World_LotSetTerrainHeightImpl`, lot create `0xACEB20` (Edit Town place, houseboat create, bulldoze, houseboat delete), lot load `Lot_FinishLoad 0xACF170`, and the terrain tool chain `0xD21F50`; plus `0xABCA60`. Which value `WM+0x1B4` holds in a normal game is open (§9 L2).

The raw call scan found every `E8`/`E9` caller of `0x6734A0`, `0x82A030`, `0x8349C0`, `0x6729D0`, the mark writers, the passability writers `0x66E040`/`0x66D920`, the cell-vector resize `0x6712C0` and the map resize `0x61F5C0`: three callers of `0x82A030` (`0x8352F1`, `0x835390`, `0x8358B5`) and three of `0x8349C0` (`0xC6BE53`, `0xC6CD02`, `0x835A27`); everything else matches the Ghidra index [verified]. The cells are sized only by `0x6729D0`, so G is never loaded from a resource or a save [verified].

### 3.2 Build steps (`0x6734A0` -> `0x6729D0`, then `0x671580`, then shorelines)

> **Changes 2026-10-18:** step 8 corrected (the rewritten field is R's type bits) and the cost note's thread corrected (docs/112 §3.1, §5.4).

1. Clear G (`0x6714A0`), store P at `+0x79C`, size the grid from the world bounds, fill every cell with `0x0080` (unlabelled water), zero the last column, and size the 5 passability and 10 high-cost maps (`0x61F5C0`). Existing map bytes are **kept** when the size is unchanged (the resize only grows or truncates) [verified]; the `highCostFootprints` record tree is freed.
2. For every 1 m sample point (x and z from the world minimum + 0.5, step 1), written into its 4 m cell, so 16 samples per cell: a land sample clears the cell's body byte and nothing sets it back, flags are OR-ed over the samples, so a cell stays water only if all 16 samples are water [verified]. A sample is land if `0xC6BBC0` says no (outside the world bounds, or the terrain query `0xC22050` returns 1 or more at that point and no lot covers it). Else sample the heightmap ("Terrain/TerrainData/HeightMap" via `0xC2A370`/`0xC29EE0`) against the sea height (`WM+0x60` object `+0x14`, default -100 without a sea). At or below sea level: depth < 0.75 -> `0x02` and then also `0x04|0x08|0x10` (= `0x1E`: the 1.5 test follows, corrected 2026-11-22, docs/100 §14.2 [verified `0x672D15..0x672EBE`]); depth < 1.5 -> `0x04|0x08|0x10`; deeper -> nothing, unless a road low bridge (`0xB74D10` over "Routing/lowBridges") covers it -> `0x04|0x08|0x10`. Above sea level: land unless a world pond contains the point (`WorldManager_FindPondAtPoint 0xC60E70`, `WM+0x100` list). Any lot at the point adds `0x01`.
3. Shore pass (bodies are not labelled yet, so "different" means land): every water cell without `0x04` whose 8 neighbours include land or a `0x04` cell gets `0x04|0x08|0x10`; otherwise, if a cell in the large-houseboat offset set is land or has `0x10`, the cell gets `0x10`.
4. Lot outlines: for every lot except type pair (`+0x408` = 1, `+0x40C` = `0x50`), the 4 edges between the corners at `Lot+0x134` are rasterised into all 10 high-cost maps with value 1 and no record (`0x670750`).
5. Non-lot world objects (`WM+0x9C` list, walked recursively, `0x671000`): objects with a physics footprint whose polygons are at most 100 units across, or of three object kinds (`0x66D5A0` bits 6, 9, 16) or flag `+0xB8 & 0x20`, have their footprint cut out of the water (`0x670C70`: unlabelled water -> land, inside the polygon).
6. `0x670F50`: in world id `0x859DB48` (Al Simhara, `WorldName.Egypt`, from `World_GetWorldNameId 0x5A2110`) three hard-coded quads near (1010-1029, 666-679) and (1827-1920, 1150-1215) are cut the same way.
7. `WaterGrid_LabelBodies 0x671580`: flood fill of the `0x80` cells into body ids with seed and size per body; sets built = 1.
8. If built: `0x672440` -> `0x66FD00` builds `shorelineMap`/`reverseShorelineMap` from R's waypoints of surface classes 2, `0x10`, `0x13`, `0x15`, probing 8 directions (table `0x115865C`) for water or a pond; it **rewrites R's waypoint surface byte** (`0x10` -> `0x13`, `0x02` -> `0x15`) and marks unlotted land cells under those waypoints `0x7F`. (corrected 2026-10-18, docs/112 §3.1: the rewritten field is the waypoint **type** in bits `0x1F` of the flag byte `+1`, keeping bits `0x20`-`0x80`; each build first reverts earlier rewrites (`0x13`->`0x10`, `0x15`->`0x02`, store at `0x66FE5A`), then rewrites (store at `0x670310`) [verified]. Live R therefore differs from the `.world` data in these type bits after every G build, which the value probe saw as its one "R change" [verified live, BP].) Then `0x672110` builds `lotInternalShorelineMaps` (lot land cells next to lot water of the same lot, height checks).

Every cell touched calls `0x66E040`, which rewrites that cell in the 5 passability maps (§4) [verified].

Cost [inferred]: one full pass of (world size)^2 samples (4.2 million for a 2,048 m world, into 262,144 cells) with a heightmap sample, a lot lookup and a terrain query each (plus a pond test above sea level), the flood fill, the world-object walk and the shoreline probes of every R waypoint; tens to hundreds of ms on the main thread inside the load screen. Not in play unless a rebuild trigger fires. (2026-10-18, docs/108 §7: 688 ms BP, 505 ms IP, on the **sim** thread inside the load screen; no rebuild in 10 min of play [measured].)

## 4. Passability maps (`G+0x7B0`, the "boat-class grids")

- Writer: only `0x66E040 -> 0x66D920` (and `0x66D960`), from the builder family [verified by raw call scan]. Class of a cell: 0 for land or `0x7F`; else 1 if `0x02`, 2 if `0x04`, 3 if `0x08`, 4 if `0x10`, else 5. Map k (k = 0..4) holds 1 where k < class.
- Reader: `WaterPath_Plan 0x671930` (and `0x670750`'s record test reads map 0). The map index comes from the route's option word `& 0xCF00`: houseboat kinds (`0x624150`) `0x4100` -> 2, `0x4200` -> 3, `0x4300` -> 4; boats (`0x55DA60`) -> 1; anything else (swimmers) -> 0; then it steps down while the start cell is 0 in the chosen map. The builder sets `0x04` and `0x08` together, so maps 2 and 3 are equal in practice [verified].
- Meaning [inferred from the tags and the route kinds]: 0 = any water, 1 = not shallow, 2/3 = small and medium houseboats keep one cell from shore and avoid medium depth and low bridges, 4 = large houseboats also keep the large clearance.
- Changes in play: none outside a G build [verified].

## 5. High-cost maps (`G+0x7C0`, the "mark grids") and their writers

- **Classes** [verified]: 10 maps. A route reads map `0x66D6E0(option word)`: the route's 64-bit agent mask (`0x624180`) is matched against 9 table entries at `0x1158610` (high words `0x804`, `0x8`, `0x10`, `0x600`, `0x1`, `0x180`, `0x40`, `0x2`; one low word `0xC0000000`); a mask contained in entry k selects map k+1, else map 0. A footprint writes map 0 only if its agent mask is all ones, else every map whose entry intersects its mask (`0x66D5E0`).
- **`0x670750`** (line rasteriser, 4 m cells, DDA): writes a value into one map along a segment; with a record list and value non-zero it records each cell that was 0 before and is water in passability map 0.
- **Writers** [verified, raw call scan complete]:

| Writer | Caller | Cells | Recorded | When / thread |
|---|---|---|---|---|
| `0x670750` value 1, all 10 maps | `0x6729D0` (G build) | lot outlines | no | G builds only |
| `WaterGrid_MarkFootprintPolygon 0x6733C0` -> `0x670750` value 1 | `0x636BF0` (footprint insert into the lot quadtrees; callers `LotGraph_Build`, `0x6376D0`, `0x639DE0`, `0x63A1C0`, `0x63AFD0`) | outline of every polygon the footprint shape returns from vtable `+0xDC(0, list)` ("Routing/HangingFootprints"), once per overlapped quadtree cell (repeats find the existing record) | water cells that were 0 | every object footprint insert, sim thread, also inside lazy lot builds in a plan (docs/91) |
| `0x670510` | `0x62D9F0` (twice; from `LotRouting_RemoveObjectFootprints 0x635700`, `0x637900`, `0x62E3E0`) | sets the recorded cells back to 0 in the recorded maps, then drops the record (key: owner id and the footprint id or its complement) | - | every footprint remove, sim thread |

- **History effects** [verified code, effects inferred]: a cell already marked by another footprint or a lot edge is not recorded, so the first remover clears a cell a later footprint still covers; land cells are marked but never recorded, so they stay 1 (harmless: map 0 of the passability grid is 0 there); a G rebuild drops all records but keeps the bytes, so marks of footprints present before the rebuild stay forever (the footprints are not re-inserted by the build). The live map can therefore differ from any recomputation from current footprints.
- **Reader**: the water JPS (`WaterPath_Plan` passes map `0x66D6E0`'s index as g2; `native/ts3native/waterjps.cpp`: a marked cell is not "open" in the forced-neighbour test and is a jump point).
- **Rate** [inferred]: one call per footprint insert or remove of any object (docs/89 footprint writer rates: 15-19 ms/s of waits in `env-on`), but writes only for footprints that return polygons from `+0xDC`; which shapes do is open (§9 L4).

## 6. blockedNodeMap (`G+0x35C`)

- Add `0x66FB40` from `0x636BF0` when the footprint shape kind (`+0x98`) is 4, its owner is not a Sim and its agent mask is all ones: +1 for every 4 m cell under the footprint's bounding box. Remove `0x66F6D0` from `0x62D9F0` for records that are not "Sim" (`+0x30` = 0), have no extra key (`+0x28/+0x2C` = 0) and an all-ones mask: -1, erase at 0 [verified].
- Readers: `WorldAStar_Search 0x676260` (4 sites, `0x66F890`: cell blocked?) and `0x66F8D0` (shoreline exit cell for a waypoint, from `PathPlanner_PlanHierarchical` and `IC_World_FindPlacesToDisembarkOnShoreline` via `0x65CAD0`) [verified].
- Same thread and rate as the footprint writers; the bounding box (not the polygon) is counted [verified].

## 7. Frozen ponds (Seasons)

- State lives in script only: `SeasonsManager.mPondFreezeLevel` (0-100, temperature-driven rate from `TemperatureManager`), `mPondState` (`Liquid` < 25, `FreezingMelting`, `FrozenSolid` >= 95, `TryingToMelt` at 78) [verified, C#].
- Effects: `PondManager.OnPondStateChanged` cancels pond interactions and posture; `FinalizePondChangingToMeltingState` destroys world-pond skating areas (`WorldPondSkatingArea` objects, so their footprints come and go through normal object events); `WeatherControl.SetTerrainCoverAmounts` passes `PondFreezeAmount` to native weather settings (`0x7846E0` closure: 7 functions, weather manager copy under a critical section, no routing) [verified].
- What makes a pond walkable: the route option `EnablePondPlanning`, set by `SimRoutingComponent` when ponds are frozen (human, child or older, standing posture), by the pond-walk and swim actions in `PondManager` and by `SwimHereWith`; ice skating builds a custom path (`InsertCustomPathAtIndex`, plan forced to Succeeded, no replan) [verified, C#]. Native: `RoutingJob_SplitSubPaths 0x81A340` types a subpath `FrozenPondPath` (`0x89A9F6D7`) for search nodes of type 0 or 6 with node flag `0x200` [verified]; the search itself reads no frozen flag.
- Snapshot consequence: nothing to track. The pond geometry (world ponds in G, lot ponds in the lot graphs) is static between G builds; the frozen choice arrives with each request's option word.

## 8. Houseboats (Island Paradise)

- Create: `IC_World_CreateHouseboatLotImpl 0x792CA0` clamps the size to 4-64, finds a slot with `WorldManager_FindHouseboatLotSlot 0xC6ACB0` (x from minX + 8 in 72 m steps, z from maxZ + 8 upward, at sea height + 3, first slot without a lot), creates the lot, fires World event `0x10` and builds its routing (`RoutingSystem_RebuildLotRouting 0x835110`) [verified]. These slots lie outside G's rectangle, so houseboat lots never touch G.
- Sail: `IC_World_ParentLotToHouseboatImpl 0x7888B0 -> Lot_SetHouseboatParent 0xAB9C40` stores the boat object id (`Lot+0x460/+0x464`), the flag `+0x444` and display offsets `+0x470..+0x4AC`; `Lot_GetHouseboatDisplayTransform 0xAB33F0` combines them with the boat's transform component on demand. Nothing else in the lot or the routing world is written [verified]. Script parents the lot on load (`Lot.cs` 3982, 9547) and unparents it (9554); no script API moves a lot (`MoveLot`-style calls do not exist) [verified grep].
- Snapshot: a houseboat lot is a normal lot at a fixed off-map place. Track create/delete (docs/94 E15). The boat object itself is a world object; its footprint updates reach the high-cost maps and `blockedNodeMap` like any object (§5, §6).

## 9. Remaining unknowns and the live check that closes each

> **Changes 2026-10-18** (docs/108 §7, docs/112 §5.4): L1 answered (one build per load through the message path, sim thread on BP, 688 ms BP / 505 ms IP); L3 answered for plain play (no rebuild in 10 min BP or IP; build/buy not exercised); L10 answered (sea height 13.63 BP, 27.38 IP, unchanged). L5, L7 (frame half open) and L9: §12.

| # | Unknown | Live check |
|---|---|---|
| L1 | Load-time build actually happens through `0x835300` and its cost | Count and time `0x6734A0` with the caller (`0x8352F1`/`0x835390`/`0x8358B5`/`0x834A82`) and thread; read G `+0x00`, `+0x0C`, `+0x10`, `+0x14` right after the load screen. Expect one main-thread build during load, non-zero counts on water worlds |
| L2 | `WM+0x1B4` in a normal game and so whether lot/terrain edits re-arm `+0x983` | Read `WM+0x1B4` and `world+0x983` in live mode and after a build-mode terrain edit; count `0xC61FE0` posts |
| L3 | Rebuild frequency in play | Same counter over a 10 min BP/IP run with and without build/buy (expected 0 without) |
| L4 | Which footprint shapes return high-cost polygons (`+0xDC`) and how many cells they mark | Count `0x6733C0` calls, `0x670750` cell writes and recorded cells per minute, with the object's catalog name for the top 10 |
| L5 | Stale marks after a rebuild | After a build/buy exit with a rebuild, compare the high-cost maps with a fresh recomputation from current footprints; expect extra 1 bytes |
| L6 | `blockedNodeMap` churn | Counters on `0x66FB40`/`0x66F6D0` per minute and the hash size |
| L7 | Houseboat Sims route in the lot's off-map frame | Read a Sim's position on a sailing houseboat: z beyond `WM+0xC0`, constant lot rectangle |
| L8 | Sender of `0x7FDA974` (forced world routing rebuild) | Breakpoint-free counter on the handler block `0x835A1E` |
| L9 | The lot type pair skipped by the lot-outline stamp (`+0x408` = 1, `+0x40C` = `0x50`) | Read both fields for every lot of IP and BP after load |
| L10 | Sea height changes after load | Read `WM+0x60` object `+0x14` every minute (no script setter exists; native writers not traced) |

## 10. What this means for our snapshot

> **Changes 2026-10-18:** "R's surface bytes" corrected to R's type bits (docs/112 §3.1); the per-input copy rules and hooks now live in docs/94 §8.2.

- **Copy, not recompute.** Take G's cell array, the 5 passability maps, the 10 high-cost maps, the body table, the shoreline trees and `blockedNodeMap` from live memory (about 0.5 MB cells + 15 x 0.26 MB maps for a 2,048 m world, plus small trees) once after load, then again after each G build.
- **G build epoch**: hook (or count) `0x6734A0`; any build invalidates the whole water part of the snapshot and also R's surface bytes (step 8 of §3.2) (corrected 2026-10-18: R's type bits `0x1F` of the flag byte, docs/112 §3.1).
- **Per-footprint deltas**: the high-cost maps and `blockedNodeMap` change only in `0x6733C0`/`0x670510` and `0x66FB40`/`0x66F6D0`, all on the sim thread inside the footprint paths docs/94 already covers. The cheapest exact update is to copy the touched rows after each call (cells are known from the record lists and the bounding boxes), not to re-derive them.
- **Per-request inputs**: frozen-pond walking and the boat class come with the route's option word; nothing world-side.
- **Houseboats**: no per-cut rebuild; lot create/delete only.

## 11. Names added

Appended to `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (tag `<docs/100>`): `RoutingWorld_OnMessage 0x835300`, `WaterGrid_BuildFromWorld 0x82A030`, `WaterGrid_Clear 0x6714A0`, `WaterGrid_dtor 0x671FF0`, `WaterGrid_LabelBodies 0x671580`, `WaterGrid_InitNeighbourOffsets 0x672480`, `WaterGrid_UpdateCellPassability 0x66E040`, `PassMaps_SetCellClass 0x66D920`, `PassMaps_Resize 0x61F5C0`, `WaterGrid_MarkFootprintPolygon 0x6733C0`, `WaterGrid_GetFootprintRecord 0x672920`, `WaterGrid_FootprintMarkClasses 0x66D5E0`, `WaterGrid_RouteMarkClass 0x66D6E0`, `WaterGrid_BlockedNodesAdd 0x66FB40`, `WaterGrid_BlockedNodesRemove 0x66F6D0`, `WaterGrid_IsBlockedNode 0x66F890`, `WaterGrid_ShorelineWaterCell 0x66F8D0`, `WaterGrid_BodyCellCount 0x66D8D0`, `WaterGrid_BuildShorelines 0x672440`, `WaterGrid_BuildShorelineMap 0x66FD00`, `WaterGrid_BuildLotInternalShorelines 0x672110`, `WaterGrid_CutWorldObjects 0x671000`, `WaterGrid_CutHardcodedPatches 0x670F50`, `WaterGrid_CutPolygon 0x670C70`, `RoutingWorld_MarkWorldDataDirty 0x829500`, `WorldManager_PostWaterGridDirty 0xC61FE0`, `ScriptHost_PostWorldReady 0x769950`, `WorldManager_FindHouseboatLotSlot 0xC6ACB0`, `Lot_SetHouseboatParent 0xAB9C40`, `Lot_GetHouseboatDisplayTransform 0xAB33F0`, `World_GetWorldNameId 0x5A2110`, `WorldManager_FindPondAtPoint 0xC60E70`, `WorldManager_GetSeaHeight 0xC61670`, `WorldManager_IsRoutableTerrainPoint 0xC6BBC0`, `Stub_ReturnFalse 0x624F70`. Corrections to docs/91 names (kept as they are in the CSV, append-only): `WaterGrid_MarkA 0x670510` removes one footprint's recorded marks; `WaterGrid_MarkB 0x670750` is the line rasteriser; `LotRouting_FootprintWaterMarks 0x62D9F0` also decrements `blockedNodeMap`.

## 12. Offline closure of L5, L7, L9 (2026-10-18)

docs/108 §4 left these three for a recomputation, a Sim on a sailing houseboat and a walk over every lot. Offline only: the game's own functions run from the exe image under the emulator (docs/63) on constructed inputs, against Python models. Each spec fails on any mismatch and prints a tally that must not be empty (the self-check that the cases exercise the claim). Command (a worktree has no `game-copy/`):

`TS3W_EXE=<main checkout>/game-copy/bin/TS3W.exe python tools/emu/run.py tools/emu/specs/watergrid_mark_remove.py tools/emu/specs/houseboat_slot.py tools/emu/specs/lot_type_ics.py`

| Item | Verdict | Spec | Result (2026-10-18) |
|---|---|---|---|
| L5 stale marks | **closed: verified (emulator)**; the live magnitude is not needed (§10 copies the bytes) | `watergrid_mark_remove.py` | 262/262 cases, `0x670510` blocks 15/15, followed `0x66D5E0` 21/21, `0x66F390` 19/19, `0x66E120` 11/11; 49 rebuild cases keep marks that a remove would have cleared |
| L7 houseboat frame | **half verified (emulator)**: new houseboat lots are placed beyond G; the Sim-position half stays **open** (exact read below) | `houseboat_slot.py` | 307/307 cases, blocks 12/12; 215 slots returned, none with z at or below `WM+0xC0` |
| L9 skipped lot pair | **closed: verified (emulator + static)**: commercial dive lots | `lot_type_ics.py`, `node tools/portal-answer-check.ts` (L9 rows) | 206/206 cases; `kEP10_Diving` = 80 = `0x50` |

### 12.1 L5: a G rebuild leaves marks no remove can clear [verified, emulator]

- `WaterGrid_MarkA 0x670510` (thiscall(G; owner id, footprint id, 64-bit agent mask), ret 0x18) maps the agent mask to map classes with `WaterGrid_FootprintMarkClasses 0x66D5E0` (all-ones mask: class 0 only; else class k + 1 for each of the 9 table entries at `0x1158610` the mask meets), looks up the (owner, footprint) record in the `highCostFootprints` tree with a lower-bound search (`0x66F390`, key compare `0x66E120`, owner first, then footprint, 64-bit each), and only if found writes 0 to every recorded cell (16-byte entries, x and z floats, cell = truncate then `>> 2`; cells outside the width, height or map count are skipped) in each selected high-cost map, then unlinks and frees the record. A missing record writes nothing.
- The two G-build steps that touch these maps and records, run on the same constructed G: `PassMaps_Resize 0x61F5C0` with the unchanged size leaves every byte of the 5 passability and 10 high-cost maps as it was, and `0xC5E990` frees the record tree (then the build resets the tree header inline, `0x672ADB`-`0x672AED`).
- So after a rebuild the remove of a footprint marked before it finds no record and its 1 bytes stay [verified]. The live maps differ from a recomputation from current footprints by these stale marks plus the first-remover effect of §5. This confirms §10's choice (copy the bytes, never recompute) and closes I-100-4's "effect inferred". How many stale cells a real save carries was not measured and is not needed by the snapshot design.

### 12.2 L7: new houseboat lots sit beyond the world's far Z edge [verified, emulator]; the Sim frame stays open

- `WorldManager_FindHouseboatLotSlot 0xC6ACB0` (thiscall(WM; -, -, out), ret 0xC) walks slots at x = minX + 8 + 72 col, z = maxZ + 8 + 72 row (y = sea height + 3, sea `WM+0x60` object `+0x14`, -100 without one); columns = (`WM+0xCC` - 8) / 72 (signed integer division); a slot is free when `World_LotLookup 0xC68990` finds no lot at (x + 0.5, z + 0.5); a new row is tried only while minX <= x0 < maxX and maxZ < z + 72 < maxZ + `WM+0xC4`; at most 256 lookups; the result vector is (x, y, z, x). Every returned slot has z >= maxZ + 8, outside G (which ends at maxZ), as §8 said.
- **Open, exact read**: whether a Sim on a sailing houseboat routes in the lot's slot frame. At `IC_World_ParentLotToHouseboatImpl 0x7888B0` return, record the lot's corners at `Lot+0x134`, its flag `+0x444` and `WM+0xC0`. Then, while `+0x444` is set, at every `Route_PlanCommon 0x7606E0` entry whose follower's lot component holds that lot id, read the first start-point record of argument 2 (the start points vector, 64-byte records, x/y/z first, docs/15 §2) and the corners again. Expected: start z > `WM+0xC0`, the start point inside the recorded corners, the corners unchanged during the trip. Repro: Isla Paradiso, a household on a houseboat, sail it and give a move order on board. A start point near the boat's displayed position (inside G) would mean routes on board use the display frame and §8's "no per-cut rebuild" needs another look.

### 12.3 L9: the pair (1, `0x50`) is a commercial dive lot [verified]

- `IC_World_SetLotType 0x78DF80` writes `Lot+0x408` and clears `+0x40C` when the type changes; `IC_World_SetResidentialLotSubType 0x785540` writes `+0x40C` only when `+0x408` is 0, `IC_World_SetCommercialLotSubType 0x7855C0` only when it is 1 (emulator, random op sequences, also an unknown lot id and a NULL world manager). So `+0x408` is the script `LotType` (Residential 0, Commercial 1, Tutorial 2) and `+0x40C` the residential or commercial subtype. The other writers (raw scan of `.text` for the two displacements) are the lot create path (`0xACEC6A`, from its arguments), a save-stream read at `0xAC6C14` (type read, subtype set to 0 and then read for stream versions >= `0x23` [inferred from the read call and the version test]) and zeroing at `0xAC1D80` [inferred: constructor].
- In the game's `CommercialLotSubType` enum (no explicit values) member 80 = `0x50` is `kEP10_Diving` (`node tools/portal-answer-check.ts`, rows L9). So the G build skips the outline stamp only for Island Paradise dive lots, which lie in the sea [verified]; why EA skips them (keep swimmers and boats free to reach the dive spot) is [inferred]. No per-lot walk is needed: the rule is by type, and which worlds carry dive lots is world data (`LotManager.sWorldHasDiveLots`).

### 12.4 Names added

Appended to the symbols CSV (tag `<docs/100>`): `WaterGrid_FindFootprintRecord 0x66F390`, `FootprintRecordKey_Less 0x66E120`, `FootprintRecordTree_FreeNodes 0xC5E990`, `ByteMapVector_Resize 0x61F550`, `ByteMapVector_EraseTail 0x61F330`, `ByteMapVector_MoveRange 0x61F280`. Ledger rows I-100-11 to I-100-14 in docs/51 §50.

## 13. G build cost at world scale: phase probe and the two exact options (2026-11-22, `[WaterGrid] Prof`, run 2026-11-22: results in §14.1)

Lane `feature/world-scale-perf-1122` (docs/320 §3 item 4). Built offline, ts3native `0.36.299-wgprof1` (`game-copy/ts3native.asi.wsp1122`, sha256 `d31b5b1cc7c55546c752429131b7bd8136e43120873af3fa4286968617a1de5a`), native selftest `TestWaterGrid` ok; no game run. Ledger rows: docs/51 §574.

### 13.1 Why a probe first

The load-time build is 0.92-1.07 s on the sim thread per load (docs/39 §10.1) [measured], but nobody split it. The two ways to take it off the load have different preconditions, and which one is worth building depends on where the second goes:

| Option | Exact when | Gain if | Risk |
|---|---|---|---|
| A. **Cache G across loads**, keyed by world and lot/object layout | every input of §3.2 is in the key (bounds, sea height, heightmap, low bridges, world ponds, lot rectangles and corners and the dive-lot type pair, non-lot world object footprints, R's records), the output is replayed in full (cells, 5 passability maps, body table, both shoreline trees, lot-internal tree, the lot-outline 1s OR-ed into the 10 high-cost maps, R's type-bit rewrites) and `highCostFootprints` is freed as the build frees it | the whole build (~1 s) is the input hash plus the replay | a missing key input = a wrong G for every Sim, household included; the trees are EASTL nodes on the game heap (replay through the game's own insert). Needs the input list proven complete in the disassembly of `0x6729D0` and `0x66FD00`/`0x672110` |
| B. **Parallel sample pass**: the sim thread still waits, the 1 m sample loop (`0x672B34..0x673236`, ~4.2 M samples on a 2,048 m world, §3.2 step 2) runs on N workers by disjoint cell rows, then the rest as today | the per-sample calls (`0xC6BBC0` routable test, `0xC29EE0` heightmap, `0xC60E70` pond, `0xB74D10` low bridge, `0xC68990` lot lookup) only read and are thread-safe, and each cell's OR of its 16 samples is order-free [inferred: the flags are OR-ed, the land rule only clears] | the samples share is most of the build | thread safety of five game functions (locks, caches, TLS) unread; a missed writer = a race at load |

Option "build G concurrently with the rest of the load" is not exact without a gate: footprint inserts after the build write the high-cost maps and `blockedNodeMap` (§5, §6), so any overlap races them.

### 13.2 What was built (observe only, default off)

- `native/ts3native/watergrid.cpp`: eight call-site retargets inside the build (list in the file header and docs/68; each byte-checked E8 to its target, capstone on the Steam 1.67 exe [verified]): clear `0x6714A0`, cells `0x6729D0`, label `0x671580`, shorelines `0x672440` with its two parts `0x66FD00` / `0x672110`, world-object cuts `0x671000` (per root) and the patch cuts `0x670F50`. Each hook times one call and calls the game's function with the same arguments; samples = cells - objects - patches (the inline loop plus the lot-outline loop).
- Per build (after the shorelines, the message path's last step) two log lines: `water grid build N: T ms = clear + cells (samples, objects, patches) + label + shorelines (map, lot) ms; grid WxH water W bodies B` and `water grid build N hash: cells 0x.. pass 0x.. high 0x.. (cell bytes, maps), R 0x../0x.., trees a/b/c, H us`. Fingerprint parts: header + cell array + body table; the 5 passability maps; the 10 high-cost maps (kept apart: they carry history, §12.1); R's record sums with and without the type bits (`PvRSum`, values.cpp); the three tree sizes (`+0x40`, `+0x5C`, `+0x78`; the third offset [inferred] from the first two's layout).
- Ini `[WaterGrid] Prof=0`; export `ts3native_watergrid` (`on` runtime switch for the report, `ready`, `builds`, last build's phase us and fingerprint). Steam 1.67 only (no EA 1.69 / retail rows for the inner sites: SKIP line there).
- Not covered: a `BuildWorldRoutingData` build (`0x834A82`, world save / Edit Town) that skips `0x82A0B0` gets timed rows but no line (shortcut in the file).
- Found on the way: `[LoadProf]` zeroed **every counter after its block** (v60 to the end of `Ts3nCounters`) at each LoadWorld entry (`memset` to the end of the struct), which cleared later sections' ready flags and counters mid-run whenever `[LoadProf] Enabled=1`. Fixed to its own rows; selftest asserts the next section's first field survives.

### 13.3 Queues (prepared, dry-run ok, NOT RUN)

- **RUN FIRST** `tools/harness/queues/watergrid-prof-runfirst-1122.json`: 7 fresh loads (rewind off), 1 min each, Bin state `apex`: BP twice, BP-Spring once, IP-Spring twice, SV-Spring twice; `logRequire` version, `OK   water grid prof`, one build line and one hash line per job (regexCases tested in the dry run).
- Correctness `tools/harness/queues/watergrid-prof-correct-1122.json`: BP 10 min with the probe on, `scriptErrorsMax 0`, same read-backs. Observe only, so household Sims equal vanilla by construction.
- Timing: none for the probe (no gain claimed); the timing queue (ABBA load pairs, per-arm load ms, smoke first, "needs quiet machine") comes with option A or B.
- Decision rules: samples >= ~70% of the build and the five per-sample callees read-only on review = build B (default off, check: the fingerprint of a B build equals the game's on the same load, every load). Equal cells/pass hashes for two loads of one save and for BP vs BP-Spring, with a short input list = A is viable; any difference between two loads of one save = G depends on load order or history, A drops. Shorelines or labelling dominant = neither option, look at `0x66FD00` (probes of every R waypoint) instead.

### 13.4 On a 4x or streamed world

- The probe reads every size at use (grid width and height, cell and map vector lengths, map counts); nothing is a fixed array, so a 4,096 m world (4x cells, ~17 MB of G, docs/320 §1) is hashed the same way; the selftest runs a 2,048 m and a 4x grid. Hash cost is linear in G (FNV over dwords; [inferred] ~10-40 ms at 4x, on the sim thread, probe only). `PvRSum` refuses R grids over 256 x 256 chunks, so on a larger R the R part prints `(unread)` [verified code].
- Option A on a streamed world: the key must be per world **and** per region/lot layout, invalidated on every lot or region load/unload (the `0xC61FE0` dirty post already fires on lot create/delete/load, §3.1), sized from the grid at use; a per-region replay (rows under the changed lots) beats whole-world. Option B scales with area by construction (rows split over workers) and is the better fit for unbounded worlds if A's key cannot be made per region.

## 14. Water grid next: diff diagnostic, canonical hash, parallel sample pass (2026-11-22, `[WaterGrid] Diff` / `Par`, built, NOT RUN)

Lane `feature/watergrid-next-1122`. ts3native `0.36.299-wgnext1` (`game-copy/ts3native.asi.wgnext1122`, sha256 `6719f144ffcfd959b4f4abec71fad2f9da8bc5a6154fb0afa02c193af74fb24a`; ini `game-copy/ts3native.ini.wgnext1122`), native selftest ok (`TestWaterGrid` incl. the new diff and parallel cases); no game run. Ledger: docs/51 §579. Both switches default 0; Steam 1.67 only (EA 1.69 / retail table rows are 0 = SKIP).

### 14.1 What chain-watergrid-m1122 measured (§13 probe, all jobs PASS) [measured]

| World | Build ms | Samples | Objects | Shorelines (map + lot) | Water cells / bodies |
|---|---|---|---|---|---|
| BP (4 loads incl. BP-Spring) | 1238-1384 | 590-641 | 131-164 | 511-577 (242-284 + 260-292) | 29316 / 13 |
| IP-Spring (2) | 1095-1137 | 453-478 | 76-78 | 561-578 (306-316 + 254-262) | 189851 / 18 |
| SV-Spring (2) | 1068-1071 | 305-307 | 197-207 | 557-562 (237-239 + 317-325) | 11519 / 4 |

Clear and label take ~0-2 ms. Shorelines are about half of every build, samples 28-46%. Passability and high-cost hashes are equal across loads of one world (and BP vs BP-Spring); the cell hash (header bytes `+0x00..+0x1B` + cells + body table) differs on every load. `[RouteEarlyOut]` apply (docs/142) stays exact with ~0 saved. The work is proportional to world area (a 4x world: ~4-5 s per load [inferred, linear in samples, waypoints and lots]).

### 14.2 Static verdicts: the per-sample lookups and the shoreline probes [verified, capstone on the Steam 1.67 exe, 2026-11-22]

All thiscall. "Safe" = may run on N workers while the sim thread waits and nothing else writes.

| Lookup | Args, ret | Verdict |
|---|---|---|
| `WorldManager_IsRoutableTerrainPoint 0xC6BBC0` | WM; x, z; ret 8; al | **safe** on the `0xC68990` condition: reads bounds `WM+0xB4..+0xC0`, the type map through `0xC22050`/`0xC21AE0` (array read), then the lot lookup |
| heightmap `0xC29EE0` | `[WM+0x54]`; x, z, hm; ret 0xC; ST0 | **safe**: heightmap read + CRT `floor`. `0xC2A370` (resource load, allocates, `lock xadd` reference) runs once before the loop (`0x672B19`) and is released after it, sim thread only |
| `World_LotLookup 0xC68990` | WM; x, z; ret 8; Lot* | **conditional**: while the lot-grid dirty byte `WM+0x1CC` is set it first rebuilds the grid (`0xC68680`: mutex + critical section, allocations, stores to WM); clean = read-only hash lookup (`0xD4E550`) + lot transform (`0xAB8540`, FPU control word saved and restored). One lookup on the sim thread before fan-out cleans it; none of the 15 setters of `+0x1CC` is in the loop's callees (that no other thread sets it while the sim thread waits is [inferred]) |
| `WorldManager_FindPondAtPoint 0xC60E70` | WM; x, z, flag; ret 0xC; pond* | **not safe on a hit**: walks `WM+0x100..+0x104` calling `0xB5FC40` per pond; its polygon test `0xB5FAB0` (pond+0x480; x, z; ret 8) has no stores outside the stack, but a hit goes on to `0xC6A250` -> `0xAB8EE0` -> `0xA8DF80`, which writes a recursion guard into the lot object (`+0x116`) and makes unresolved virtual calls |
| low bridge `0xB74D10` | bridge; x, z, 1, 0, 0, 0; ret 0x18; al | **not safe on a box hit**: the box test (`[b+0x38]` +0x10..+0x1C, SSE) and `0xB71CF0` are read-only; outside 0..1 of the height it calls `0xB74A20` (global `0x11DC350` lookup, atomic reference add/drop, possible virtual delete, vector growth through a locked allocator) |

Shorelines: `0x66FD00` probes per R waypoint (`0xC614D0` -> `0xC2A570` virtual on a terrain tile, `0xC60E70` with flag 1 (reaches the `0xB793F0` bridge part), `0xC68990`, `0xC6AB90`, `0xC6BAB0`) and writes R's type byte, cell `0x7F` (only cells with low byte 0 and no lot flag, followed by `0x66E040`) and tree nodes (allocator `0x4E3F90`, insert `0x4E0C40`). The probes treat 0 and `0x7F` alike, so probes could be split from the writes. `0x672110` probes `0xC68990`, `0xC6A250`, `0xC60E70`, `0xC6AB90`, `0x66D740` -> `0xC6A540` (not walked) and writes only the `+0x64` tree. Not built here: the unsafe `0xC6A250` / virtual paths are on the common route of both passes, not rare (§14.6).

The sample loop itself (`0x672B75..0x672F54`; x outer from `WM+0xB4 + 0.5f` (`0xF9A5AC`) in steps of `1.0f` (`0x107A538`) while `WM+0xBC > x`, z inner likewise): per sample, cell index `(trunc z >> 2) * w + (trunc x >> 2)` (absolute coordinates); (a) not routable: low byte to 0 (water count `G+0x10` - 1 if it was non-zero), `0x66E040`, next sample (no lot lookup); (b) the height in ST0 is compared unrounded against the float sea height (`fcomi`; equal or unordered = not above) and stored as a float: above sea -> pond lookup, no pond = land as in (a) but then the lot lookup; at or below -> `d = sea - h` (SSE): `0.75 > d` -> `0x1E`, `1.5 > d` -> `0x1C`, else the bridge loop (vector order, first true -> `0x1C`); then the lot lookup (`0x01`). Every write is guarded by `idx < count` and followed by `0x66E040(idx)` (thiscall G; idx; ret 4), which rewrites that cell's 5 passability bytes from the cell alone. No neighbour reads; the only shared state is the cell and `G+0x10`. So every cell is the order-free result of its samples, and the passability maps follow from the final cells.

### 14.3 Why the cell hash differs, and a canonical hash (step 1)

No store from the ctor `0x671E30`, the clear `0x6714A0` or the labelling `0x671580` reaches body-table slot 0 (`G+0x39C`), slots above the body count, the overflow slot `0x7F` when there is no overflow (`0x6718C2`), or the padding `G+0x02/+0x03` [verified]. They keep whatever the allocation left, so they can differ per load [inferred]. The other candidate, cell bits the passability maps cannot see (flag `0x01`, body ids, `0` vs `0x7F`), would mean real input differences; the cuts (`0x670C70`, through `0x66E040` at `0x670EE1`) would have moved the passability hash and did not. No pointer or time sits in the hashed header or the body table [verified]. So a canonical hash looks possible statically: the value header fields (`+0x00`, `+0x01`, `+0x04..+0x1B`), the cells and body slots `1..bodies` (plus slot `0x7F` once 126 ids are used), `WgCanon` in `watergrid.cpp`.

`[WaterGrid] Diff` (needs `Prof=1`, default 0) proves it on real loads: each build writes `ts3native-wgdiff-<pass hash>.bin` (G's `0x7EC` header bytes + the cell vector, ~0.5 MB per 2,048 m world) next to the ini, keyed by the passability hash (equal across loads of a world, different between worlds); `Diff=2` writes only (a queue's first load, so no stale dump is diffed), `Diff=1` diffs against the previous dump and rewrites it. Lines: `water grid diff vs load of pid N: canon A -> B (equal|differ)`, then `water grid diff: K header fields differ (P pointer-only): <names>; body slots S (U unused); cells C (low byte only L, flags only F, flag bits 0xXX)` and up to 8 example cells `(x,z) flags|low before->after`. Fields are named from the §2 layout; pointer fields are tagged. Nothing in G is written. The sidecar is ours, never in the `.sims3`.

Cache consequence (not built): if the diff shows only unused slots / padding, a G cache keyed by world + region + lot layout can be exact against `WgCanon`; inputs and invalidation as §13.1 option A (lot create/delete/load, terrain edits: the `0xC61FE0` dirty post), stored as a sidecar file per world and region.

### 14.4 `[WaterGrid] Par`: parallel sample pass (steps 3-4)

- Hooks (docs/68): jmp at `0x672B75` (8 bytes, the `movss` is replayed) to an entry stub; in check mode also at `0x672F5A` (6 bytes replayed). Both stubs save the GPRs and xmm0-7. The pass reads every size at use (bounds, `G+0x04`/`+0x0C`, the cell vector); the x and z lists are built with the game's own float accumulation; columns are split into bands on cell-column boundaries (no shared cell); a column outside the grid width refuses (fallback).
- Workers run the read-only parts with the game's own callees (`0xC6BBC0`, `0xC29EE0` through an asm wrapper that keeps the unrounded x87 compare, `0xB5FAB0` per pond, the bridge box test inline, `0xC68990`) on the sim thread's x87 control word and MXCSR; a sample with a pond polygon hit or a bridge box hit is deferred before any write and run after the join on the sim thread with the game's full `0xC60E70` / `0xB74D10`. Workers count their own water decrements; nothing shared is written.
- `Par=1` check: the game's loop runs and is used; at its end our pass runs on a copy of the cells as they were at the loop entry and every cell plus the water-count delta is compared: `water grid par build N: check equal, C cells, serial S ms, parallel P ms (W workers), 0 mismatches, D deferred, water -X` or `check MISMATCH, ..., first (x,z) game->ours, ...`. No behaviour change either way.
- `Par=2` apply: one lot lookup on the sim thread cleans the lot grid (`WM+0x1CC` must then read 0, else fallback); the pass runs into the scratch copy, which is then copied into G; `G+0x10` -= the decrements; `0x66E040` runs once per written cell on the sim thread (the game calls it after every write, up to 5 times per sample; the last call sees the final cell, so once per cell is exact); then the game resumes at `0x672F5A`. Any failure (worker exception, 30 s timeout, inputs unreadable, dirty lot grid) runs the game's own loop on the untouched G: `water grid par build N: fallback to the serial loop (<why>)`.
- `ParWorkers` (default 0 = CPUs in the process affinity mask - 2, 1..8). Export `ts3native_watergrid` gains `parMode, parRuns, parMismatch, parFallback, parDeferred, parUs, parSerialUs, parWorkers`.
- Load-time water grid only: our code changes no Sim's state, so no household / selected-Sim / unknown-scope exemption applies; every Sim, household included, must see the same G as vanilla, which queues (b) and (b2) prove.
- Selftest (`watergrid-test.h`): fake callees with the game's conventions over a synthetic world: 1, 3, 5, 7 workers equal our serial run (cells, written set, water count) with deferrals taken; apply writes G + one `0x66E040` per written cell; a dirty lot grid falls back with G untouched; check mode passes when equal and flags one corrupted cell. It caught a real bug: the compiler kept a caller's z in an xmm register across the asm wrapper, which a callee's SSE use clobbers; the wrapper is now called through a volatile pointer (standard ABI).

### 14.5 Queues (prepared, dry-run ok, NOT RUN; Bin state apex, rewind off: every job is a fresh load)

- (a) **RUN FIRST** `tools/harness/queues/watergrid-diff-1122.json`: BP, IP-Spring, SV-Spring, two loads each (load 1 `Diff=2`, load 2 `Diff=1`), 1 min each, ~36 min. Decides the canonical hash / cache question. Independent of (b).
- (b) correctness `watergrid-par-check-1122.json`: `Par=1` check mode on BP, IP, SV fresh loads + BP 10 min; pass = one `check equal` line per build, `logForbid` on `check MISMATCH`, `scriptErrorsMax 0`, ~33 min.
- (b2) correctness `watergrid-par-apply-correct-1122.json` (after (b) passes): per world a serial load (`Diff=2`) then an apply load (`Par=2`, `Diff=1`): canonical hash `equal` required and the passability / high-cost hashes pinned to the m1122 serial values (BP `0x66775222`/`0x811DE52B`, IP `0x32E6F52A`/`0x9499519B`, SV `0x04C2E470`/`0x50774617`), then BP 10 min in apply mode, ~51 min. If (a) shows real cell differences between serial loads, read (b2)'s canonical line together with (a)'s.
- (c) timing `watergrid-par-time-1122.json` (needs quiet machine, after (b) and (b2)): smoke job first (apply load, read-back + both split lines), then ABBA fresh loads per world, `Par=0` vs `Par=2`, `Prof=1` in both arms (per-arm samples ms and total ms from the build line), ~78 min.

### 14.6 Expected gain, 4x / streamed worlds, what is left

- Gain [inferred]: the samples share (305-641 ms per 2,048 m load) shrinks to about serial / workers plus the sim-thread tail (deferred samples, one `0x66E040` per cell instead of one per write, two 0.5 MB copies): **~0.2-0.55 s off each load** (SV low, BP high), ~15-40% of the build. Nothing in play unless a G rebuild runs (build/buy exit with `world+0x983`, §3.1), which gets the same saving.
- 4x world: samples grow 4x (~1.2-2.6 s) and the pass splits the same way (bands by cell column, sizes read at use, no fixed array), so ~1-2 s off each load [inferred]. A streamed world that builds G per region would run the same code over that region's bounds; the diff sidecar is per world key, a region cache would key by world + region.
- Left: the shorelines (~half the build, ~0.5 s now, ~2 s at 4x) need their own lane: probes separate from the writes (§14.2), but `0xC60E70` with flag 1 and `0xC6A250` are on the common path there, so most probes would defer unless those calls are proven safe or run in a sim-thread pre-pass. The world-object cuts (76-207 ms) are next in size.
