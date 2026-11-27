# Routing prior art for a hook-only retrofit of TS3 routing

2026-11-20. Desk research only. The problem framing comes from docs/667, 669, 670, 671 (summaries). Source tags:
**[src]** means I read the source code or docs this session. **[doc]** means a search snippet of an official page.
**[mem]** means from memory of a known talk or paper; the claim was not re-read this session, so treat it as unverified.
Problem numbers are the brief's: P1 lock, P2 placement, P3 path search, P4 sync plans.

Retrofit ratings:
- **HOOK**: hooks and side structures only.
- **RE**: needs moderate reverse engineering of state or write paths.
- **REWRITE**: needs an engine rewrite.

## 1. Concurrency for navigation data (P1, P4)

**The common pattern: queries never share a lock with writers. Writes are deferred to a sync point.**
Every modern engine uses some form of this. None of them lets a long query and a write contend on one lock.

- **Recast/Detour.** Each thread gets its own `dtNavMeshQuery`, which owns its node pool and open list. The shared `dtNavMesh` is read-only during queries, and changes need outside synchronisation (Recast maintainer on the mailing list, [thread](https://groups.google.com/g/recastnavigation/c/irmJ5uonNnM)).
  - **Obstacles are deferred.** `dtTileCache::addObstacle` only appends to a fixed request array (`m_reqs[m_nreqs++]`, `MAX_REQUESTS`). The tiles are rebuilt later in `update()`, which runs on the owning thread ([DetourTileCache.cpp](https://github.com/recastnavigation/recastnavigation/blob/main/DetourTileCache/Source/DetourTileCache.cpp)) [src].
  - **Versioned handles.** Poly refs carry a per-tile *salt*. A ref that is stale after a tile rebuild fails validation instead of reading freed data [mem; the salt is in `DetourNavMesh.h`].
  - **Lessons for us.** (a) Mutable scratch per query object, not global. (b) Writes go into a queue. (c) Handles carry versions.
  - Rating: **RE**. Our "readers" mutate refcounts, pins and the global clip scratch `0x1158580`. That is exactly what Detour avoids by keeping scratch per query. We can give each thread its own clip scratch by redirecting the global in the hooked functions, but every writer of it has to be found.
- **Unreal.** `FindPathAsync` returns a handle. The queries run on a dedicated task, and the results are "computed by the dedicated task in the last frame and ready to dispatch in the next update" (`AsyncPathFindingCompletedQueries`, [API](https://docs.unrealengine.com/4.27/en-US/API/Runtime/NavigationSystem/UNavigationSystemV1/AsyncPathFindingCompletedQueries/index.html)) [doc]. So results arrive one frame later, by design.
  - Dynamic changes are collected as "dirty areas". The navmesh tiles are rebuilt by time-sliced or async generator tasks (`FNavRegenTimeSlicer` exists in the API), and the finished tiles are swapped in on the game thread [doc for the time slicer; mem for the dirty-area flow].
  - Forum reports say async queries are not fully thread-safe alongside crowd updates ([forum](https://forums.unrealengine.com/t/what-is-the-proper-way-to-use-async-pathfinding/467423)) [doc, anecdotal].
  - Rating for one-frame-late results: **RE** (P4). Rating for the dirty-area batching: see the shadow index below.
- **Unity (experimental `NavMeshQuery`, DOTS).** A job that uses `NavMeshQuery` must pass its `JobHandle` to `NavMeshWorld.AddDependency`. The NavMesh world then holds back changes until those jobs finish. Mutation happens only between job phases ([docs](https://docs.unity3d.com/2018.1/Documentation/ScriptReference/Experimental.AI.NavMeshWorld.AddDependency.html)) [doc]. The API is deprecated since 2023.2.
  - This is a reader/writer phase barrier where the writer waits. It is the opposite of TS3, where small readers wait behind one big reader-writer.
- **Godot NavigationServer.** It is thread-safe because every state-changing call goes into a queue that runs in one synchronisation phase per physics frame. Getters can return stale values until that sync ([docs](https://docs.godotengine.org/en/stable/tutorials/navigation/navigation_using_navigationservers.html)) [doc].
  - This is the cleanest statement of "defer writes to a sync point, readers see a stable world in between".
- **Cities: Skylines 2.** The modding docs list separate `PathfindJobs`, `ModificationJobs`, high-priority and normal action queues feeding worker jobs ([guide](https://ps1ke.github.io/Cities-Skylines-2-Modding-Guide/Game/Pathfind/PathfindQueueSystem)) [doc, names only; I could not read how they are scheduled].
  - CS1 ran pathfinding on its own thread, according to a Colossal Order developer quoted on Steam [doc, secondhand].
- **RimWorld 1.6.** "The pathfinding system is now fully multithreaded and batched" ([1.6 changelog](https://docs.google.com/document/d/e/2PACX-1vRCjqVtPQDFGu4POiKTUd_8o3U2Asdhx99SOvcgU66ABdYtk3Cgndd53yJ6BC4tZX530pp_m6lf4Z9P/pub)) [doc]. No details found.
- **Havok AI, Killzone 2/3, Company of Heroes, StarCraft II.** Known talks exist:
  - Havok AI: multithreaded queries and navmesh cutting.
  - StarCraft II: GDC 2011, "AI Navigation: It's Not a Zero Sum Game". Constrained triangulation plus steering.
  - Company of Heroes: GDC 2007, Jurney. Destruction and replanning.

  None of the scheduling details could be verified this session [mem, unverified]. Do not rely on them.

**Synchronisation primitives that fit "mostly-read spatial data with rare writes"** [mem; standard kernel docs: [seqlock](https://docs.kernel.org/locking/seqlock.html), [RCU](https://docs.kernel.org/RCU/whatisRCU.html)]:

- **Seqlock.** The writer bumps a counter to odd, writes, then bumps it to even. A reader copies the data and retries if the counter changed. There are no reader writes at all, so the refcount problem disappears, as long as the read is done on *our* structure and not the game's.
- **RCU / epoch.** Build a new immutable version, publish it with a pointer swap, and free the old version after every reader has left its epoch. This is good for a per-lot index rebuilt on a footprint change.
- **Version counter as cache key.** Bump a global or per-lot `footprintEpoch` on every footprint insert, move or remove. It invalidates every memo below for free.
- **Why snapshot planning failed and shadow indexes may not.** Snapshotting the *game's* structures copies pointers with refcounts and pins. A *shadow* index stores only plain values (polygon vertices, class bits, object ids). It is fed from the write hooks, and the readers that want it are answered from it without touching game memory. That is how Detour, Godot and Unreal separate the data a query reads from the data the simulation owns.

**Lock hold splitting (sliced planning).**
- Detour offers `initSlicedFindPath`, `updateSlicedFindPath(maxIter)` and `finalizeSlicedFindPath[Partial]`. `dtPathQueue` runs `MAX_ITERS_PER_UPDATE = 100` per crowd update ([DetourCrowd.cpp](https://github.com/recastnavigation/recastnavigation/blob/main/DetourCrowd/Source/DetourCrowd.cpp)) [src].
- Factorio caps the work: `max_steps_worked_per_tick = 1000`, `max_work_done_per_tick = 8000`, with overload multipliers ([PathFinderMapSettings](https://lua-api.factorio.com/latest/concepts/PathFinderMapSettings.html)) [doc].
- For us the analogue is not iteration slicing inside the game's A*, which would be a REWRITE. It is **releasing `+0x918` at quiescent points the plan already passes**: between candidate goals, and between world A* and cell A*. That extends what `TailUnlock` already does for post passes 16-19. Rating **RE**: we must prove that no planner state holds raw footprint pointers or pins across that point, or else re-validate them with an epoch check after reacquiring the lock.

## 2. Placement / free-spot queries (P2)

- **Negative-result cache with invalidation.** Factorio has a negative path cache with start/end distance tolerances (`negative_cache_accept_path_*_distance_ratio`) and score decay (`negative_path_cache_delay_interval`) [doc].
  - For FindGoodLocation: key on (object/footprint id, origin, constraint record bytes, lot id, `footprintEpoch`, routing-world epoch), and return the failure without running the search.
  - This addresses the 0.5-1.9 s "retry pairs" in docs/671 §1, which are unproven as retries.
  - Rating **HOOK**, with one condition: the record `0xB470C0` reads must be fully captured in the key. Any hidden input (Sim position, random facing start, time of day) breaks exactness. Run in shadow mode first.
- **Occupancy bitmaps / bit-parallel footprint tests.** Rasterise the static blocking footprints of each lot level into a 1-bit-per-tile (or per-quadrant) bitmap. Then test a candidate footprint's covered tiles with word-wide ANDs. Summed-area tables give O(1) "is this rectangle empty" checks.
  - These are standard in grid games. RimWorld's per-cell edifice grid and Factorio's collision masks are examples, but neither was verified for this exact use [mem].
  - For us it can only be a **conservative prefilter**. It may reject a tile only when every facing must fail, for example when the tile is wholly inside a blocking footprint of a class that always blocks. Everything else falls through to the game's test.
  - Rating **RE**: we need the footprint class semantics of `0xB41120` / `0x82F470`.
  - It also attacks the 35% of samples in the routing footprint gather and clip.
- **True clearance / HAA\*.** Harabor and Botea (CIG 2008) annotate each cell with the largest square agent that fits, per terrain capability. One abstract graph then serves every agent size ([paper page](https://researchportalplus.anu.edu.au/en/publications/hierarchical-path-planning-for-multi-size-agents-in-heterogeneous/); [Harabor](https://users.cecs.anu.edu.au/~dharabor/pathfinding.html)) [doc].
  - For placement, a clearance or distance map answers "can anything of radius r stand here" in O(1). That is **rotation-invariant**: if the footprint's circumscribed radius fits in free clearance, all 8 facings pass the static part.
  - Rating **RE**, and only as a prefilter. The game's polygon tests are not radius tests, so the clearance map can prove a pass only for the static-geometry part. The objects-on-tiles and physics-box tests still run.
- **Incremental dirty-rect updates.** Update the bitmap or clearance map only within the bounds of a moved footprint, like Unreal's dirty areas or Detour's per-tile rebuild. Rating **HOOK** once the write hook (`0x833680`-family) is known.
- **EQS-style generation, cheap tests first, time slicing.** Unreal's EQS generates candidates, then runs the tests. It respects a per-update budget, `MaxAllowedTestingTime` ([FEnvQueryManagerConfig](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/AIModule/FEnvQueryManagerConfig)) [doc], and is not multithreaded according to an Epic staff reply ([forum](https://forums.unrealengine.com/t/eqs-multithreading-questions/2580631)) [doc].
  - Reordering tests (physics box before routing gather) could change *which* candidate wins only if the tests have side effects. Since all must pass, the first passing candidate stays the same, so reordering is exact if the tests are pure.
  - Rating **RE**: we must confirm the tests are pure.
  - Time slicing FindGoodLocation is a **REWRITE**, because the managed callers expect a synchronous answer.
- **Spatial hashing.** The game already has quadtrees. A per-lot hash of footprint ids to polygons would only help as the store for the shadow index. Rating **HOOK**.

## 3. Path search (P3)

- **Region / connected-component ids for O(1) failure.**
  - RimWorld paths at the region level first, and Tynan describes "regions" as the basis for reachability checks ([forum, 2014](https://ludeon.com/forums/index.php?msg=34212)) [doc, secondhand; the "Reachability at last" blog post could not be found].
  - Dwarf Fortress keeps a per-tile "walkable" group id (DFHack `levelmap`), so "unreachable job" is a lookup. Bug reports show the groups going stale after construction ([bug 12894](https://dwarffortressbugtracker.com/view.php?id=12894)) [doc].
  - For us the cheap and exact form is the **lazy version of components**. A failed cell A* floods the start's whole reachable region. Store that flooded cell set, keyed by (lot, level, start cell, class/filter bits, `footprintEpoch`, door/portal state epoch). Any later candidate goal in the same plan, or a retry, whose goal cell is not in the set fails without a search.
  - This generalises the existing `FloodMemo` (docs/667 §4) from "same query" to "same start region". It turns the 95-136 floods per failed plan into about 1.
  - Rating **HOOK/RE**: the cell A* entry `0x61E500` and its filter are already understood. Doors that open for some Sims (the managed door ask) must be part of the key, or the memo must be disabled when a door is crossed.
- **Multi-goal / reverse search.** One Dijkstra from the start settles all candidate goals at once. Equivalently, search backwards from the goal set (Factorio's planner runs backward steps, `fwd2bwd_ratio` [doc]).
  - An exact retrofit is a **REWRITE** of the candidate loop, because the game picks candidates in its own order with its own scoring.
  - The reachability set above captures most of the win for the *failing* candidates, which are the expensive ones.
- **HPA\*** (Botea, Müller, Schaeffer 2004) [mem]. TS3 already is hierarchical (portals, then cells). Adding a level is a **REWRITE**.
  - Factorio FFF #317 added a chunk-level abstract search to guide A* around lakes ([FFF-317](https://www.factorio.com/blog/post/fff-317)) [doc]. It is not exact, which violates our result-identical rule.
- **JPS / JPS+** (Harabor and Grastien, AAAI 2011; Rabin, Game AI Pro 2) [mem]. These need uniform-cost grids. TS3 uses a quadtree with per-cell class costs. Rating **REWRITE** / not applicable.
- **Flow fields** (Emerson, "Crowd Pathfinding and Steering Using Flow Field Tiles", Game AI Pro 1, Supreme Commander 2) [mem]. One field per goal serves many agents. They are useful when many Sims walk to one target (a venue, a lot portal), but the paths are not identical to A*. Rating **REWRITE**.
- **Any-angle (Theta\*), D\* Lite / LPA\*** (Koenig and Likhachev 2002) [mem]. They give different paths, or need the planner's open/closed state to persist across world changes. Rating **REWRITE**.
- **Path caching and sharing.** Factorio keeps short (5) and long (25) path caches with start/end tolerance ratios [doc]. Tolerance makes the results non-exact.
  - The exact variant keys on the identical (start, goal set, class, epochs). It is **HOOK**, but the hit rate is probably low except for retries. Measure the duplicate rate first (see coalescing in §4).
- **Local avoidance instead of replanning.** Examples: RVO2/ORCA ([UNC](https://gamma.cs.unc.edu/RVO2/)) [mem], and DetourCrowd's steering plus `dtPathCorridor` local fixes [src].
  - This is a behaviour change and violates the bug-fix-only and no-visible-change rules. It is out of scope.
  - What *is* in scope: answering `RouteFollow_EditPath` / `Locomotion_ObstacleCheck` gathers from the shadow index (§1), which is P1 rather than P3.
- **Bounded / anytime search with partial paths.** Examples: Detour `finalizeSlicedFindPathPartial`, and Factorio's `start_to_goal_cost_multiplier_to_terminate_path_find` [doc]. These are non-exact, so they are out of scope except as a safety cap on pathological floods.

## 4. Scheduling (P4)

- **The Sims 4 moved planning off the script thread.**
  - In the decompiled TS4 scripts, `generate_path` sets `PLANSTATUS_PLANNING` and calls `path.nodes.make_path()`. It then yields a `BusyWaitElement` that polls `not path.nodes.plan_in_progress` every tick, counting `ticks`, before `finalize` ([interactions/utils/routing.py](https://github.com/johndpope/sims4-ai-engine/blob/master/simulation/interactions/utils/routing.py), lines about 544-605) [src].
  - So TS4's native planner runs asynchronously, and the scripted interaction *sleeps on its timeline* while other Sims keep simulating. That is the structural change versus TS3's blocking `Route.Plan`. TS4 also passes all goals in one plan (`for goal in self.route.goals: self.path.add_goal(goal)`) [src], which is the multi-goal pattern of §3.
  - No GDC talk on TS4 routing could be verified [unverified].
- **Results a frame later, with placeholder movement.** DetourCrowd runs a 20-iteration quick search immediately (`MAX_ITER = 20`, `finalizeSlicedFindPathPartial`), so the agent starts moving. The full request then joins a path queue of at most 8 agents per update, ordered by oldest replan (`addToPathQueue`, `DT_CROWDAGENT_TARGET_WAITING_FOR_QUEUE`) [src].
  - For TS3, the placeholder (turning toward the goal, a short idle) is a visible change. The usable part is: run the full plan on the worker and **yield the managed Simulator task** until it is done, as TS4 does.
  - Rating **RE**: we must find which managed callers can yield, because a synchronous caller that needs the result in the same call cannot.
- **Prioritisation and budgets.**
  - Factorio gives each tick a share of steps to short requests (`short_request_ratio`), accepts a limited number of new long requests per tick (`max_clients_to_accept_any_new_request = 10`), and scales its budget under load (`overload_levels/multipliers`) [doc].
  - CS2 has a separate high-priority queue [doc, names].
  - For us: two worker queues, with visible Sims first and unseen NPCs behind them, plus a cap on how long an unseen job may hold the lock before it yields at a quiescent point (§1). Rating **HOOK** for the queue order, **RE** for the yield.
- **Coalescing duplicate requests.** If a plan with the same key (Sim, goal set hash, epochs) is already queued or running, attach the second requester to it. This is standard in job systems and implied by Factorio's request acceptance limits [mem for "coalescing" as a named technique]. Rating **HOOK**, once the key is shown to be complete.

## 5. Ranked shortlist

Common verification: a 2-minute forced-routing storm (Isla Paradiso), run as off then on in one combined run. Each item also runs in shadow mode (compute both answers, serve the game's, log any mismatch) before it serves anything.

1. **Reachability-set memo for failed cell floods** (generalised `FloodMemo`, keyed by start region + class + footprint/door epochs).
   - Win: failed plans drop from 95-136 floods to about 1. Failed plans were 32% of slow-plan time (docs/667), and the worker lock hold shrinks with them (helps P1 too).
   - Risk: a key that misses the door ask or per-Sim filter bits. That gives a false "unreachable", which is a visible routing failure.
   - Verify: shadow mismatches = 0. Cell A* calls per failed plan, failed-plan ms, `long job` count and `lock918` frames all go down.
2. **Shadow footprint index read without `+0x918`**, for `RoutingWorld_PortalCheckLocked` (`0x82F4AB`, 44% of wait ms), then `EditPath` and `GoalPoint_Validate`.
   - Plain-value polygons per lot level, written from the footprint write hooks, published by seqlock or epoch pointer swap. Our clip uses our own scratch, not `0x1158580`.
   - Win: the largest wait key disappears without per-call-site clones.
   - Risk: a missed write path gives stale answers, and the clip must match the game's bit for bit.
   - Verify: shadow compare of every call against the locked answer. `route lock wait:` ms by key, where `0x82F4AB` should go to about 0.
3. **FindGoodLocation negative cache with epoch invalidation.**
   - Win: removes the second search of each retry pair (8 of 25 slow FGL frames, about 2.5 s in 13 phases), at about zero cost elsewhere.
   - Risk: hidden inputs in the `0xB470C0` record or the Sim state make a cached "fail" wrong.
   - Verify: log the key and the result per search with `[FglProf]`. Shadow compare, hit count, and FGL `sim-cpu` frames.
4. **Lock yield at candidate-goal boundaries** on the worker (extend the `TailUnlock` lending).
   - Win: the longest sim wait becomes one candidate's plan instead of the whole job (535 ms jobs leading to 170 ms waits).
   - Risk: planner state that holds footprint pointers or pins across the yield. Mitigate with an epoch check and restart the candidate if the epoch changed.
   - Verify: maximum and p99 `route lock wait`, crash-free storm, and identical plan results in shadow (with the yield counted but not taken).
5. **Conservative placement prefilter** (per-lot occupancy bitmap of certainly-blocked tiles, dirty-rect updated).
   - Win: a whole-lot failed search (15,000 tests, 115-145 ms) skips most tiles. It cuts the 35% routing-gather and 10% tile-quadrant shares.
   - Risk: exactness. It must reject only tiles where every facing provably fails.
   - Verify: shadow, where the prefilter verdict is always checked against the full test and must never reject a tile the game accepted. Tests per search and FGL ms.
6. **Worker priority queue plus duplicate coalescing.**
   - Win: visible Sims' plans no longer queue behind unseen NPC storms. Duplicate plans merge.
   - Risk: an incomplete coalescing key merges plans that differ. Starving unseen jobs could stall NPC behaviour.
   - Verify: queue latency by scope, the duplicate rate logged first, and unseen job completion counts unchanged.
7. **TS4-style yield for visible-Sim sync plans** (submit to the worker, sleep the managed task, resume on completion).
   - Win: removes the remaining sim-thread plan time (P4).
   - Risk: a start a few frames later is a visible change. It only applies to callers that can yield, and needs per-caller RE.
   - Verify: sim-thread plan ms for seen Sims, hitch count, and a side-by-side video for visible start delay.

Not recommended: JPS, flow fields, HPA\* changes, D\* Lite, any-angle, RVO/steering, tolerance-based path caches and partial paths. All of them change results or need the planner rewritten.
