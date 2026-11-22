# 321. Unbounded-world sweep: code that assumes today's world (2026-11-22)

Status: static sweep only. Read `native/ts3native` (*.cpp/*.h), `mods/ts3fixes/src` (*.cs) and `tools/harness`; no build,
no game run, no code change. Follows the AGENTS.md rule "Build for unbounded worlds" and docs/320 (4x-area worlds,
streaming). Branch `docs/unbounded-sweep-1122`.

Question: which shipped or built code would break, give wrong results or get slower on a 4x-area world (4096 m,
16x16 cells of 256 m, ~4x lots, Sims and objects) or a streamed world (lots and regions loading and unloading during
play)?

Tags: **V** = the code path was read; **I** = inferred (cap or effect not followed to the end, or reach not measured).
Defaults: native keys from `native/ts3native/ts3native.ini` (the release ini); managed switches from the
`Instantiator.k*` `[Tunable]` defaults (`SwDefault`, Fixes.cs:1753); code without a switch counts as ON.
Severity: **High** = crash guard stops covering, or a wrong result, in ON code; **Med** = ON code gets slower or loses
its gain, or OFF code gives a wrong result; **Low** = minor, bounded, diagnostics, or fine.

## 0. Summary

- **No hard-coded world bounds, cell sizes or 2048 m constants** in our native or managed code. Every world grid we
  read (water grid G, routing graph R, heightmap cache, footprint grid, lot hash grid) is sized from the game at use
  [V]. This matches docs/320 §1 for the exe.
- **The risk is fixed-size tables sized for today's Sim, lot and object counts**, most of them with a safe fallback
  (lost gain), four without one (below), and **caches kept for the whole session** that are never cleared on world
  change or lot unload.
- **Streaming has no invalidation hub.** Our lot add/remove hook exists (`lotstream.cpp`, `0xABD3C0` / `0xABD3E0`,
  `[LotStream]`, OFF, measurement only) but nothing else listens to it. World-load-only work (`GhostSweep`,
  DoorGen arming) never runs for a lot that streams in later.
- Index widths: EA's own portal ids carry a 14-bit chunk (`id & 0x3FFF`, 16,384 chunks = exactly 128x128 at 4096 m
  with 32 m chunks); our `wastar.cpp` checks decode the same widths. Bigger than 4096 m needs an EA-side change, not
  ours [V].

| | High | Med | Low |
|---|---|---|---|
| Release default ON | 4 | 16 | 16 |
| OFF (built, test or diagnostics) | 0 | 7 | 16 |
| Harness | 0 | 0 | 1 |

## 1. Release-default-ON items

### 1.1 High

| # | file:line | switch = default | assumes | 4x / streamed world | fix | V/I |
|---|---|---|---|---|---|---|
| H1 | `mods/ts3fixes/src/AutShadow.cs:108, 1048, 1066` | `[AutShadow] Enabled=1 Unseen=1` (ON) | One autonomy request holds at most `kMaxObjs=1024` meta objects and `kMaxPairs=8192` ad pairs. `AddObj` silently stops adding past either cap; no abort, no counter (grep: no other `Pairs >=` / `Objs >=` check). Fed by `AutonomyT.PublicMetaObjects` plus every known lot (`:967-969`). | More public objects and lots push requests past the cap; the worker scores a subset, so an unseen NPC's pick can differ from vanilla without any sign. `Audit=50` only samples. | Now: abort to the sim thread with a counter when either cap is hit. Then: size the arrays per request (as `AutLive.cs:117-123` already does). | V cap and no check; I whether today's worlds get near it |
| H2 | `native/ts3native/plandeadline.cpp:99` `kKdRows=64` | `[RouteAsync] KindDeadlines=1`, `KdExemptSeen=1` (ON) | At most 64 Sims cut within 10 s; the oldest row is evicted. | Evicted Sims lose their backoff ("doubled, then uncut after 2 cuts"), so an unseen NPC whose route needs longer can be cut on every try and never path. NPC-only (household exempt) but a broken guarantee. | Hash table keyed by follower, sized from the active Sim count, or grow on eviction pressure; count evictions of rows still in backoff. | V |
| H3 | `mods/ts3fixes/src/NearVenue.cs:124-136`, `TripStageCore.cs:65` `kNvGroupMax=32` | `kNearVenue` (ON) | Fewer than 32 open community lots of one venue type. The group stops after 32 lots in `LotManager.AllLots` order, not by distance. | Lots past the 32nd keep EA's score (factor 1) while the first 32 are rescaled, so the gravity weighting is skewed for townies. A streamed world reorders `AllLots`, so which lots are cut changes during play. | Collect every open lot (or the nearest N by distance), then normalise. | V |
| H4 | `native/ts3native/eafix.cpp:830, 871-875` `SL_CAP=64` | `[EaCrashFixes] SlaveUnlink=1` (ON) | At most 64 live master/slave route pairs (boats with drivers, strollers, led horses). When full, a pair whose master no longer lists it is reused, else `overflow++` and the list is not recorded. | With 4x Sims and boats more pairs are live; an unrecorded list loses the guard and the purecall crash at `0x58D996` can return. | Growable table or hash keyed by master. | V |

### 1.2 Med

| # | file:line | switch = default | assumes | 4x / streamed world | fix | V/I |
|---|---|---|---|---|---|---|
| M1 | `native/ts3native/autshadow.cpp:718, 1127-1135, 1768` | `[AutShadow] Enabled=1 Mode=3 CutLean=2` (ON) | `AsSet` 4096 slots, at most 3072 ids. `simIds` is filled per Sim and never cleared (no `AsSetClear` anywhere, not on world change); add failure ignored. `listLots`, `t1Lots`, `lotDirty`, `reqLots` share the cap. | Sim churn and world switches fill it; new Sims are then not known as Sims and get the plain typed mark. Exact but slower. Also hits today's worlds in long multi-world sessions. | Clear on world load and Sim destroy; size from Sim and lot counts; log when full. | V |
| M2 | `autshadow.cpp:730, 1281, 2437, 2596-2598` | same (ON) | Object-lot map `AS_OL=65536`, full at 49,152 rows (sized from 17,655 rows on Bridgeport). | 4x objects fill it; every list row then goes through the print and a 64K-row compaction runs every 64 cuts on the sim thread. Slower, gain gone. | Size from the object count, or key by region and drop rows of unloaded lots. | V |
| M3 | `autshadow.cpp:2678-2686` | same, `TierMs=1500` (ON) | `g_asTierTab[1024]` Sim tiers, linear probe, never cleared. | Past 1024 Sims a miss probes 1024 slots and the Sim stays tier 1 (refreshed every cut). | Clear on world load and Sim gone; size from Sim count. | V |
| M4 | `autshadow.cpp:2694-2704` | same (ON) | `g_asLotTier[512]` lot tiers, never cleared. | ~400-500 lots at 4x is at the cap; full means every lot tier 1. | Size from lot count; clear on world load and lot unload. | V |
| M5 | `mods/ts3fixes/src/AutShadow.cs:977-978`, `AutShadowLocal.cs:186-187` | `autshadow` (ON) | One `AddRow` into native per Sim in `LotManager.Actors`, per autonomy request (and per local request). | Requests x Sims grows ~16x with 4x Sims. | Send only Sims near the actor or the scored lots, or one shared row set per tick. | V |
| M6 | `native/ts3native/unseentable.cpp:31, 150-163` `UTB_ROWS=1024`, `UTB_WALK=4096`; `UnseenTable.cs:33-34` | `[UnseenTable] Enabled=1 Mode=1` (ON) | At most 1024 rows; walk stops at 4096 owners; every generation (`RefreshTicks=1`) walks and qsorts all locomotion agents on the sim thread. | Full table falls back to the old rule (correct, slower); walk cost grows with agent count. | Size rows from the walk count; update incrementally or walk only near-camera agents. | V |
| M7 | `native/ts3native/ts3native.cpp:3946` `kJobs=32` | `[RouteAsync] Enabled=1 Mode=1 FallbackAsync=1` (ON) | At most 32 queued or done route jobs. | More `asyncQueueFull`; those routes plan on the sim thread. Bounded, slower. | Size the pool from the Sim count, or grow it. | V |
| M8 | `native/ts3native/replandedupe.cpp:27` `RD_N=256` | `[RouteAsync] ReplanDedupe=1` (ON) | At most 256 replanning followers per 1 s; world-level change counters gate every hit. | Lost repeats with more followers; any lot write or lot load anywhere invalidates all rows, so a streamed world gets far fewer hits. | Grow table; key validity on the lots the route touches. | V |
| M9 | `native/ts3native/snap.cpp:1127, 1134` `kSpDoorLots=512` | `[RouteSnap] Enabled=1` (ON, door channel for ts3fixes) | At most 512 lots per session; `g_spDoorN` never reset, only invalidated. | Lots from 4x worlds plus travel worlds pile up; once full, new lots never turn valid and DoorMirror re-pushes doors on every plan. Per-lot arrays leak across world switches. | Clear on world change; hash by lot key. | V |
| M10 | `snap.cpp:444-453, 1047, 1093` P3 cut | `[RouteSnap] Enabled=1 Mode=0 CutMs=250 CapMB=64` (ON, feeds AutShadow go-live) | Full lot-table walk plus qsort each cut (up to 4/s); walks refuse more than 4096 lots; image memory at most 64 MB. | Walk cost grows with lots. R, grid and G-tile images grow ~4x (R ~3 MB, G ~10 MB); past 64 MB P3 switches off for the session and silently cuts AutShadow's input. | Scale `CapMB` with world area and log the stand-down; cut only dirty lots. | V walk, I memory |
| M11 | `native/ts3native/hostcalls.cpp:110-130` | `[RouteSnap] DoorMemoX=3` (ON) | `g_dmx[256]` memo rows (250 ms life); `g_dmxHold[32]` held lots, overflow holds every lot. DoorGen.cs sends its ops only at world-load arm. | More than 32 lots with PortalPathPlan handlers turns the memo off entirely; streamed lots are never armed. Safe side, slower. | Hash-set holds sized from the lot count; larger set-associative ring; arm per lot on lot load. | V |
| M12 | `native/ts3native/discourage.cpp:186-193, 21-23` | `[Discourage] DeadSweep=1` (ON) | A dead id's Remove calls `LotRouting_RemoveObjectFootprints 0x635700` with lot 0 ("on all lots"). | Cost per dead object grows with lot count on the sim thread. | Sweep only the lot `DiscRec` already records; all lots as a fallback. | I (the all-lots loop is inside EA's function) |
| M13 | `mods/ts3fixes/src/NearVenue.cs:108-137` | `kNearVenue` (ON) | Walks all lots (`SharedVisitVenueTests`, `EntryPoint` each) per Sim x venue type (4-entry ring). | O(lots) per community score, ~4x slower. | Per-type open-lot list cached per sim-hour, keyed by region, cleared on lot load and build/buy. | V |
| M14 | `mods/ts3fixes/src/UnseenOutfitDefer.cs:72` `kOdCheck=128` per 125 ms | `kUnseenOutfitDefer` (ON) | At most ~128 Sims waiting; the comment promises "a seen one goes on within 250 ms". | A mass change (weather turning many unseen Sims to outerwear) with ~1000 waiting takes ~1 s per round, so a Sim panned into view can show the old outfit longer. | Check near-camera Sims first, or scale `kOdCheck` with the queue. | I |
| M15 | `mods/ts3fixes/src/UnseenTeleportGoal.cs:170` `sUfTpPlaced`, 4096 then `Clear()` | `unseenfbteleport` (ON) | Fewer than 4096 teleport placements per session. | More Sims roll the table over sooner; forgotten placements cannot be repaired by the load-time cleanup ("clean up our damage on load" rule). | Prune by age or world instead of clearing, or keep it in the sidecar. | V |
| M16 | `mods/ts3fixes/src/DeadRefs.cs:184-251` | `deadrefsweep` (ON) | Hourly pass; slices are timed but each phase gathers its list in one tick (`Objects<GameObject>()`, `Relationship.sAllRelationships`, all Sims). | 4x objects and up to ~16x relationships make the hourly gather spike 4-16x. | Gather per lot or region across ticks. | V code, I spike size |

### 1.3 Low

| # | file:line | switch = default | assumes | 4x / streamed world | fix | V/I |
|---|---|---|---|---|---|---|
| L1 | `autshadow.cpp:2291-2300` `g_asLite[1024]` | `[AutShadow]` (ON) | Lot print baselines, cleared only at epoch. | Fine at 4x; a streamed world that loads many lots fills it (full print, slower). | Key by region; drop on lot unload. | V |
| L2 | `autshadow.cpp:1189, 2193-2203, 2319-2323, 713` | same (ON) | Per-cut caps: class queue 512, gone-delta lots 64, typed-mark lots 256, payload pool 32,768 DWORDs, `AS_CAP=8192` rows. | A lot streaming in brings a burst that overflows them; falls back to untyped marks. Exact, slower. | Size per cut from the event count; spread a lot-load burst over cuts. | I |
| L3 | `discourage.cpp:1044, 1089-1110, 1153` | `[Discourage] GhostSweep=2` (ON) | Whole footprint map walked once per world load (~127k entries); at most 1024 ghosts. | Longer load screen at 4x; streamed lots never swept; ghosts past 1024 wait for the next load. | Also sweep on lot load/unload, limited to that lot. | V |
| L4 | `native/ts3native/unseenanim.cpp:31, 65` `AA_SLOTS=4096` direct-mapped | `[UnseenAnim] Enabled=1` (ON) | ~480 actors per pass (BP). | At ~2,000 actors colliding actors get a hold (vanilla pose); less gain, correct. | Open addressing, 2-4x actor count. | V |
| L5 | `native/ts3native/unseenfb.cpp:97` `GVU_SLOTS=32` | `[UnseenFallback] Enabled=1` (ON) | At most 32 unseen routes deferred at once. | Some streaks restart early (documented shortcut). | Grow with unseen count. | V |
| L6 | `snap.cpp:45, 493` `kSpTile=64`, `kSpTileMax=64` | `[RouteSnap]` (ON) | At most 4096 G cells (4 m) per axis = 16 km. | 4096 m = 1025 cells = 17 tiles. Fine. | none | V |
| L7 | `snap.cpp:503` `SpCellOf` | same (ON) | World coords >= 0 (`trunc(v)>>2`, as EA `0x670750`). | A generated world with a negative origin gets no dirty-tile marks (stale tiles). Only if the generator moves the origin. | Subtract `WM+0xB4/+0xB8` if EA's G does. | I |
| L8 | `native/ts3native/wastar.cpp:5, 17` | `[EaCrashFixes] WorldAStarRefs=1 PortalFlagRefs=1` (ON) | EA's 14-bit portal chunk (16,384 chunks). | 4096 m is exactly the limit; larger is EA's own wrong result; our checks decode the same widths. | none in our code; generator stays at <= 16,384 chunks. | V |
| L9 | `mods/ts3fixes/src/TripStageCore.cs:64` `kNvWaterPenaltyM=400` | `kNearVenue` (ON) | A water crossing costs ~400 m, tuned on 2048 m maps. | Far lots across water are under-penalised on a 4096 m map. | Scale from world bounds or a measured detour. | V |
| L10 | `mods/ts3fixes/src/ErrorTrapSpread.cs:139` | `errortrapspread` (ON) | `GetObjects<GameObject>()` plus all households gathered in one tick, then sliced. | Gather spike 4x. | Gather incrementally. | V |
| L11 | `mods/ts3fixes/src/RouteFixes.cs:222-243` | `watchdog`, `watchdogidle`, `stuckwatchscan` (ON) | Every 10 sim-minutes, all Sims in one pass. | ~4x per pass, rare. | Slice per tick, or unseen Sims in range only. | V |
| L12 | `HiddenRoom.cs:130`, `BuffSharedState.cs:176`, `DeadSimHolders.cs:48, 161`, `SimRefsDeadSim.cs:64`, `FightOrphan.cs:43` | `hiddenroomnpc`, `buffstate`, `*deadsim` (several ON) | Whole-population hourly or 10-min passes, unsliced. | Linear spike, 4x. | Sliced pass (`SlicedPass` in EaSpread). | V HiddenRoom, BuffSharedState; I rest |
| L13 | `mods/ts3fixes/src/Batch582fLogic.cs:140-155` `NcTargets` | `nocommercialcar` (ON) | Walks all lots on every `LiteDriver.Simulate` entry, before the switch check. | O(lots) per traffic-car spawn; small. | Count commercial lots once per world load or lot change. | V |
| L14 | `SpawnerMapTag79.cs:80` (4096 keys), `RouteBackoff.cs:825` (2048), `CookClickListener.cs:45` (4096) | `spawnerskip`, `backoff`, `cookclicklistener` (ON) | Fixed key counts; a full table starts over. | Fills and clears repeatedly at 4x: lost streaks and backoff, more repeated failed route floods. Correct. | Size from the live count at world load, or age-based pruning. | V |
| L15 | `mods/ts3fixes/src/Fixes.cs:2811` `sLastShortcut` | `gohome` (ON) | Keyed by SimDescriptionId, never pruned, not cleared on world quit. | Grows with population; after load or travel, old ticks can mark a Sim as "loop" (it walks, vanilla). | Clear in `HeldRefsWorldQuit`; prune past `kLoopTicks`. | V |
| L16 | `mods/ts3fixes/src/UnseenFallbackCore.cs:263, 572` `kMaxHoldMin=120` | Pre/Arrive/Leave (ON) | Hold capped at 2 sim-hours (Overwatch spacing, not world size). | Binds above ~230 m already; more long trips end early on 4096 m (unseen NPCs, allowed fake). | Fine; note only. | V |

## 2. OFF items (built, test-only or diagnostics)

| # | file:line | switch = default | assumes | 4x / streamed world | sev | fix | V/I |
|---|---|---|---|---|---|---|---|
| O1 | `native/ts3native/footprint.cpp:108-140` | `[FootprintIndex] Enabled=0` | Index rebuilt per lot by walking the whole footprint map. | Save cost lots x entries, ~16x. | Med | Build once per save, grouped by owner. | V |
| O2 | `native/ts3native/unseenfbc.cpp:47, 59` `UFC_MAX=256` | `[UnseenFallback] Sites=0` | At most 256 unseen Sims per push. | Over 256 the whole push is rejected (`ufcPushBad`): C1-C4 stop entirely. | Med | Grow, or accept a partial list. | V |
| O3 | `native/ts3native/unseen.cpp:83, 376, 527, 738, 797` five `[512]` direct-mapped per-agent tables | `[UnseenProbe]=0`, `[UnseenThrottle] Enabled=0` | A few hundred agents. | Heavy collisions; throttle state thrashes, reasons misattributed. | Med | Open addressing sized to agents. | V |
| O4 | `native/ts3native/p4.cpp:221, 227, 1112, 1139` (`kP4Doors=512`, `kP4Nodes=4096`, `kP4DdLots=256`, `kLaLots=128`; `P4LotAt` walks all lots) | `[RouteSnap] Mode=0` (P4/P5 off) | Per-job caps; linear lot lookup per call. | Captures refused (live fallback); O(lots) per call. | Med | Use EA's lot hash grid `0xC688A0`. | V |
| O5 | `native/ts3native/p4clone.cpp:123, 256, 512` (polys <= 4096, W/G <= 65,536, `keysAll[64]`) | `Mode=0` | Clone sizes for today's worlds. | Larger tables refused; clone not built. | Med | Size from source. | V |
| O6 | `native/ts3native/replay.cpp:834, 1355, 1379, 1397, 1403, 1483` (`kRpMaxLots=1024`, `[64]`, `[256]`) | `[RouteSnap] Lots=none` | <= 1024 lots, <= 64 forced lots per cut, linear key search. | Overflow sets "all" (full rebase); O(lots) lookups; per-lot clone is docs/320 §2 risk #3. | Med | Hash by lot key; size from lot count. | V |
| O7 | `mods/ts3fixes/src/UnseenThrottleCore.cs:14` `EveryN` 4096 keys | `unseenbroadcast` / `unseenenv` OFF | Fixed key count. | More keys than broadcasters: no throttle. | Med | Size at world load; prune by age. | V |
| O8 | `native/ts3native/doorshare-core.h:10, 26` | `[RouteSnap] DoorShare=0` | 1024 rows; stamps hashed into 256 lot and 2048 owner slots. | Thrashes; collisions over-invalidate. Correct, slower. | Low | Size from door and lot count. | V |
| O9 | `native/ts3native/ghost.cpp:15` `GH_MAX=64` | `[UnseenGhost] Enabled=0` | <= 64 ghost Sims. | Rest not ghosted. | Low | Size from Sim count. | V |
| O10 | `native/ts3native/lotuse.cpp:16-35, 65` | `LotUseReadout` (diag) | 512 lots, linear scan. | Lots past 512 dropped from readout. | Low | Hash map. | V |
| O11 | `native/ts3native/lotstream.cpp:62, 113` | `[LotStream] Enabled=0` | 64 events per pump pass. | Bursts drop events (`lsLogDropped`). | Low | Bigger buffer; this hook is the natural invalidation hub (§3 lane A). | V |
| O12 | `native/ts3native/earlyout.cpp:62-130` | `[RouteEarlyOut] Enabled=0` | Union-find over R rebuilt only when R's records, size or w/h change. | If streaming edits R in place, labels go stale; a Mode 2 proof could be wrong. | Low | Rebuild on a lot load/unload generation. | I |
| O13 | `native/ts3native/stalelot.cpp:28` `kStlKeys=32` | `[RouteSnap] StaleLot=0` | <= 32 lots per job. | Falls back to the global rule. | Low | Grow. | V |
| O14 | `native/ts3native/snap.cpp:597` `kFplGrid=8192` | `[RouteSnap] StaleLotFp=0` | <= 8192 footprint-grid records. | Falls back to the world-level rule. | Low | Heap-sized set. | V |
| O15 | `native/ts3native/routespeed-core.h:17` | `[RouteSpeed] NeighbourMemo=0` | Any quadtree or lot write clears the whole memo. | Streamed lot load/unload clears it constantly. | Low | Invalidate per lot or quadtree. | V |
| O16 | `native/ts3native/placero.cpp:72` (`pos & 0xFFF`) | `[PlaceTestRO] Enabled=0` | Mirrors EA's per-quadtree cell packing. | Same as EA; per quadtree. | Low | none | V |
| O17 | `native/ts3native/saveslim.cpp:29` `kSlimMaxBody=256 MB` | `[SaveSlim] Enabled=0` | ExportDB <= 256 MB (491-day save ~101 MB). | Long 4x saves can pass it; slim stands down (no damage). | Low | Stream instead of mapping the body. | V |
| O18 | `mods/ts3fixes/src/EnvListExact.cs:53` `sExLotOff` | `envlistmemoexact` OFF | Keyed by LotId, never cleared. | A lot id stays disabled across worlds or reloads (lost speedup). | Low | Clear on world load and lot unload. | V |
| O19 | `mods/ts3fixes/src/AutOffload.cs:730-745` `BcCandidates` | `autoffbcast` OFF | Every broadcaster request scans all `LotManager.Actors`. | O(broadcasters x Sims), ~16x. | Low | Spatial bucket of Sim positions per tick. | V |
| O20 | `AutOffloadCore.cs:147` `kMaxLots=1024`; `AutOffload.cs:66` `kLotSims=32, kLotHb=4` | `envlistmemo` OFF | Fixed lot and per-lot Sim caps. | Abort or clear; safe. | Low | Size at use. | V |
| O21 | `CandidateLoops.cs:95`, `TripStage.cs:125` | `farthestsorted`, `stagedarrival` OFF | All lots per call. | ~4x slower. | Low | Region index. | V |
| O22 | `mods/ts3fixes/src/UnseenFallbackSites.cs:28` `kUfSitesMax=256`; `UnseenFallbackCore.cs:513` `UrCCache` 2048 | `unseenfbsites`, `unseenroute` OFF | <= 256 unseen routing Sims listed; fixed cache. | Sims past 256 not skipped (lost gain). | Low | Grow the native list. | V |
| O23 | `RouteFailLog.cs:48-49`, `HhSearchProbe.cs:28` | diagnostics | Row caps. | Rows dropped. | Low | none | V |

## 3. Harness

| # | where | assumes | effect | sev | fix |
|---|---|---|---|---|---|
| T1 | `tools/harness/repros.json` (129 `"x"/"z"` positions, keyed per world) | Absolute positions inside today's worlds | Fine for the worlds they name; a generated 4096 m world needs its own repro set. Nothing in the harness hard-codes world bounds. | Low | Add repros per new world; take positions from lot ids where possible. |

## 4. Checked, fine

- Grids sized from the game at use: `heightcache.cpp` (grid dims read per call, 64x64 slot grid holds 16x16 cells,
  out-of-grid points not cached, keyed by grid pointer), `unseenfb.cpp:319` `UfColourAt` (R w/h live, origin from
  `WM+0xB4/0xB8`), `waterjps.cpp` (grid w/h from arguments).
- Direct-mapped caches with erase/age invalidation: `idcache.cpp`, `fsro.cpp`, `lotsincos.cpp`.
- Live lot index with dirty fallback: `lotfd.cpp`, `avoidro.cpp`, `epro.cpp`, `gvro.cpp`, `gvhits.cpp`, `fgro.cpp`,
  `gate0ro.cpp`, `idlero.cpp`. `connro.cpp` 128 rooms is per lot.
- Local gathers (density, not area): `steerro.cpp:87-91`, `rtoro.cpp`, `obstructro.cpp`, `boatprobe.cpp`.
- Camera- or viewer-relative distances (ghost tiers 200/120 m, 80 m viewer reach, `kZoomTierMeters` 250,
  `kShortcutMinDistance` 80 m, `kNpcExitFarMetres` 200, `unseen.cpp:82` 240 m wide-camera rule): independent of world
  size.
- Managed: AutLive per-search arrays grow at use; `SpawnerMapTag79` entries are checked against lot, position and
  build/buy time; `UfTpPlaced` entries carry the world file name; `HeldRefsWorldQuit` (OutfitWait.cs:363) and the
  other world-quit handlers clear their Sim and lot holders; `RouteBackoff` prunes before it clears; EaSpread,
  OverwatchSpread, ErrorTrapSpread work is sliced (only their gathers are not); `LotPick` and `Demographics.Collect`
  copy EA's own all-lots loops (EA's cost). `ts3native.cpp:5585` FbApplySplit repeats EA's all-lots pending loop.
- `played.cpp:14` 4096 households is ample; CAS/GC/JIT/diagnostics tables (`castrim`, `f5watch`, `crashfx`,
  `allocsites`, `gcroots`, `ilpatch`, `modtime`, `pccheck`, `memdiag`, `memexp`, `monothread`, `stallwatch`,
  `watchpt`) do not scale with world content.

Not checked: bodies of `*-test.h`, `steerro-test.cpp`, `selftest.cpp`; `ts3addr`, `ts3bytes`, `vcallsites` tables;
`ts3native.cpp` beyond the route job pool and FbLots; the native caps behind every managed IC; release tuning XML.
No cap in this doc has a measured fill level on today's worlds.

## 5. Fix lanes

Estimates are agent wall-clock time; game runs listed separately.

| Lane | Items | Work | Agent time | Game runs |
|---|---|---|---|---|
| 0. Measure fill | all ON caps | One "cap hit / high-water" counter per table (H1-H4, M1-M11, L4, L5, L14) in the existing counter block, read on IP and BP saves. Tells which caps today's worlds already touch. | ~1 h | 2 live jobs (IP, BP) |
| A. Lifecycle hub | M1, M3, M4, M9, M11, M15, L1, L3, L15, O11, O12, O18, M8 validity | Turn `lotstream.cpp` (`0xABD3C0`/`0xABD3E0`) into an always-on lot generation counter plus world-change event (native) and a managed `OnLotLoad`/`OnWorldChange` hook; consumers clear or drop their rows; DoorGen and GhostSweep run per lot. | ~2-3 h | 1 correctness job + travel/save matrix |
| B. ON correctness caps | H1, H2, H3, H4 | H1 abort-on-cap with counter first (~15 min), then per-request sizing; H2 hash by follower; H3 full collect or nearest N; H4 growable pair table. | ~1-2 h | 1 correctness job each (H1 with Audit) |
| C. Grow pools (perf) | M2, M6, M7, M10 (`CapMB` scale + log), L2, L4, L5, L14 | Size from live counts at world load or grow on pressure. | ~1-2 h | 1 timing A/B |
| D. Nearby-only work | M5, M6 walk, M10 walk, M12, M13, M16, L10-L13 | Sims near actor for AddRow; cached per-type lot lists; lot-scoped DeadSweep; dirty-lot P3 cuts; incremental gathers. | ~3-5 h | 2-3 timing A/Bs |
| E. OFF items | O1-O23 | Fix each when its switch comes up for testing; O2 and O3 first (they fail outright, not partly). | per item ~15-30 min | with that switch's tests |

Order: 0, then B (correctness), A (needed for any streamed world), C, D. Each change follows the household rule; none
of these fixes touch household Sims except H4 (a crash guard, all Sims).

## 6. How this behaves on a 4x or streamed world

Today, on a 4x world: no crash found in our ON code from world size alone; H1-H3 give NPC-only wrong results, H4 can
let an EA crash back in, and the Med items lose most of their gain or add sim-thread cost roughly in proportion to lots
or Sims. On a streamed world: everything in lane A goes stale or fills because nothing listens to lot unload; lane A
is the prerequisite for any streaming work (docs/320 §5).
