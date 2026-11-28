# 321. Unbounded-world sweep: code that assumes today's world (2026-11-22)

Status: static sweep; lanes 0 and B built 2026-11-22 on `feature/unbounded-caps-1122` (§7; correctness queue PASS on BP / IP / SV, fill in §7.3). Sweep: Read `native/ts3native` (*.cpp/*.h), `mods/ts3fixes/src` (*.cs) and `tools/harness`; no build,
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

## 7. Lanes 0, B and C built (2026-11-22, branch `feature/unbounded-caps-1122`, 0.36.299-ucap1 correctness PASS; lane C 0.36.299-ucap2)

Builds (docs/data/builds.json): `ts3native.asi.unbounded-caps-1122` (sha256 `672d3c2002ec`, 0.36.299-ucap1 = m1122 + this
lane), `ts3native.ini.unbounded-caps-1122` (= m1122 ini, no new keys), `ts3fixes.package.unbounded-caps-1122` (tuning
`7474070e74fe`), `ts3agent.package.unbounded-caps-1122` (names the new slots). No new switch: the four items were already ON
and stay ON; each fix changes only what happens past its cap. Native selftest and the tripstage / autshadow managed
self-checks pass. Queue: `tools/harness/queues/unbounded-caps-correct-1122.json` (correctness + fill, not timing).

### 7.1 Lane 0: fill counters (always on, measurement only)

- **Native:** new export block `ts3native_caps` (`native/ts3native/caps.h`, docs/registry.md): per table `cap`, `hw` (most
  rows at once) and `full` (an add refused, a live row evicted or a fallback taken). Rows: SlaveUnlink pairs (H4), KindDeadlines
  rows cut in the last 30 s (H2), the fullest AutShadow `AsSet` and `simIds` (M1), object-lot map (M2), Sim and lot tiers (M3,
  M4), UnseenTable rows and walk (M6), route jobs (M7), ReplanDedupe (M8), snap door lots and image MB (M9, M10), DoorMemoX ring
  and held lots (M11), UnseenAnim slots (L4), deferred unseen routes (L5). ReplanDedupe, DoorMemoX and UnseenAnim are
  set-associative or direct-mapped: they report `full` (a live row lost) but no high water. One line
  `caps (minute, docs/321 lane 0: name hw/cap full): slave .. kd .. ...` goes to ts3native.log at most once a minute and only
  after a change (checked at every route START and every `full`) [verified: code].
- **Managed:** ts3fixes slots 1518-1529 (`Caps.cs`): AutShadow requests capped (by objects, by pairs) and the most objects /
  pairs in one request (H1); NearVenue groups over 32 and the most open lots of one venue type (H3); spawnerskip, backoff and
  cookclicklistener high water and clears (L14). One `ts3fixes: caps (docs/321 lane 0: hw/cap): ...` line through the UserDiag
  text channel, at most once a minute after a change, sim thread only.
- Not covered: M5, M13-M16 and the L-items other than L4, L5, L14 (cost items, not tables), the OFF items.

### 7.2 Lane B: the four High items

| # | change | at or under the cap | past the cap, 4x / streamed world | self-test | V/I |
|---|---|---|---|---|---|
| H1 | `AddObj` marks the request `CapObjs` / `CapPairs` (`AutShadowCore.CapRoom`) instead of dropping silently; a go-live search whose request was capped is dropped to the game's own search (`LiveAbandon` + `kSDropOther`) right after the post, as a worker that never answered. Counters 1518-1522. | Unchanged: every object and pair is added as before, `Capped` stays false, no drop. | Exact: the game scores the whole set (vanilla) for that search; the go-live gain is lost only for capped requests, and fixes-1518 says how often. Not done: sizing the arrays per request (the docs/321 "then" step). The arrays are shared with the worker (`Result` arrays written there) and the units encode `o << 16 \| pair` with local units at `0x40000000` (AutShadowLocal.cs), so growth needs a worker-side review; lane 0 tells first whether any world gets near 1024 / 8192. | autshadow selftest: 1024 adds pass uncapped, the 1025th object and a pair past 8192 refused and capped | V |
| H2 | `KdFind(add)` takes a free row, else the row cut longest ago **if that cut is older than kKdStuckMs (30 s)**, else doubles the table (heap, under `g_kdLock` exclusive) up to 4,096 rows; only at that ceiling a live row is evicted (`CAP_KD` full). | Unchanged: a free row is taken exactly as before; with all rows stale the same victim is picked. | Every follower cut in the last 30 s keeps its backoff (doubled, then uncut) up to 4,096 such followers (64 times today's table). Keyed by follower id only: no lot or world dependence, so streaming changes nothing. | native selftest: stale 64 rows evict the oldest as before; 64 live rows grow to 128 and every row keeps its level; at the ceiling the oldest live row goes and counts full | V |
| H3 | `NvGroupOf` collects every open lot of the venue type, then keeps the nearest `kNvGroupMax` (32) by weight (`TripStageCore.KeepNearest`, ties to the earlier lot in the walk). Counters 1523-1524. | Unchanged: with 32 or fewer open lots every lot is kept, in walk order, with the same weights and sum. | The nearest 32 get the gravity weighting; lots past them keep EA's score, as before, but they are now the far ones instead of whatever came after the 32nd in `LotManager.AllLots`, so a streamed world that reorders `AllLots` no longer changes the pick. Cost: the walk no longer stops at the 32nd lot (one more `SharedVisitVenueTests` per extra open lot); M13 (cache per type) still applies. | tripstage selftest: 20 lots all kept; exactly 32 all kept; 100 lots in far-to-near order keep the last 32; ties keep the first 32 | V |
| H4 | SlaveUnlink pairs start at 64 in the image and double on the heap (under `g_slBusy`, copy then free) when every pair is live, up to 65,536 pairs (2.3 MB); `overflow` only at that ceiling. `ts3native_slavelink` gains `cap`, `hw`, `grows`; each grow logs `ea fix: SlaveUnlink: N live master/slave pairs, table grown to M`. | Unchanged: free or stale pairs are reused exactly as before. | Every live slave list is recorded and guarded, so the purecall crash at `0x58D996` cannot come back through a full table on a world with more boats, strollers and led horses. All Sims (a crash guard, no household change). | native selftest: stale pairs reused without growth; 64 live pairs grow to 128, old pairs keep their slots, the pair past the old cap unlinks; at the ceiling overflow as before | V |

Household: no household or selected-Sim code path changed. H1 only adds a drop to the game's search; H2 rows are only
written for cut jobs (KdExemptSeen exempts seen and unknown scope as before); H3 keeps `NvOutOfScope` (player, no household,
role, situation) untouched; H4 is the existing crash guard for all Sims.

### 7.3 Fill on today's worlds (`unbounded-caps-correct-1122`, run 2026-11-22 20:37-21:23 PHT)

Run on Apex + bridge, set unbounded-caps-1122, chain `chain-unbounded-caps-1122`: **all 4 jobs PASS** (bp-caps-smoke 4.7 min,
bp-caps 13.4, ip-caps 12.9, sv-caps 12.6; 0 script errors, household checks `autliveseen_hh_post<1`, `householdtoo_lift<1` and
`watchdogs<=0` held, both caps lines present, SlaveUnlink / KindDeadlines / kNearVenue read back). Timing VOID (noisy machine;
not a timing queue). No SlaveUnlink grow line in any log. Results: `docs/data/results/2026-11-22-queue-unbounded-caps-correct-1122.md`.
Values are the last minute line of each 10 min job, high water / cap (a table that never filled or was never used shows
`0/0`: its cap is recorded only once it is touched or full) [verified: measured].

| table | BP | IP | SV | full (BP / IP / SV) | verdict |
|---|---|---|---|---|---|
| slave (H4) | 0 / - | 7 / 64 | 0 / - | 0 | fine; IP boats use 11% at most |
| kd (H2) | never full | never full | never full | 0 | fine (under 64 rows cut in 30 s) |
| asSet (M1, fullest set) | 566 / 3072 | 368 / 3072 | 423 / 3072 | 0 | fine |
| asSimIds (M1) | 314 / 3072 | 256 / 3072 | 290 / 3072 | 0 | fine per session; grows with Sim churn and world switches (lane A) |
| asOl (M2) | 26,954 / 49,152 | **35,644 / 49,152 (73%)** | 23,084 / 49,152 | 0 | **closest to its cap**: IP at 73% after 10 min; a 4x world fills it (lane C first) |
| asTier (M3) | 314 / 1024 | 256 / 1024 | 290 / 1024 | 0 | fine today; ~3x headroom (lane A clear) |
| asLotTier (M4) | 29 / 512 | 21 / 512 | 28 / 512 | 0 | fine |
| utb / utbWalk (M6) | 111 / 230 | 76 / 155 | 91 / 141 | 0 | fine |
| jobs (M7) | 9 / 32 | 8 / 32 | 7 / 32 | 0 | fine |
| rd (M8) | - | - | - | 1 / 0 / 0 | rare live evictions |
| spDoor (M9) | not used | not used | not used | 0 | door channel saw no lot in these runs |
| spMB (M10) | 6 / 64 | 6 / 64 | 6 / 64 | 0 | fine (~10% of CapMB) |
| dmx (M11 ring) | - | - | - | **368 / 30 / 111** | **live rows overwritten every run**: the 256-row ring is already too small on BP (lane C) |
| dmxHold (M11) | 1 / 32 | 1 / 32 | 1 / 32 | 0 | fine |
| aa (L4) | - | - | - | **16,909 / 8,891 / 1,182** | **collision holds every run** (counted per pass, so one colliding pair counts many times); the direct-mapped table already costs gain today (lane C) |
| gvu (L5) | not used | not used | not used | 0 | GvuMsg off or idle in these runs |
| AutShadow objs / pairs (H1) | 55 / 1024, 87 / 8192 | 74 / 1024, 126 / 8192 | 60 / 1024, 91 / 8192 | capped 0 | far from the cap (7% / 1.5%): H1 array growth not needed for today's worlds |
| NearVenue group (H3) | 6 / 32 | 3 / 32 | 11 / 32 | over 0 | fine |
| spawnerskip (L14) | 184 / 4096 | 269 / 4096 | 353 / 4096 | clears 0 | fine |
| backoff (L14) | 39 / 2048 | 25 / 2048 | 14 / 2048 | clears 0 | fine |
| cookclick (L14) | 5 / 4096 | 3 / 4096 | 2 / 4096 | - | fine |

Today no cap is hit in the four High items (H1-H4). The caps already touched on today's worlds: the DoorMemoX ring (M11,
live rows overwritten), the UnseenAnim direct-mapped slots (L4, collision holds) and the AutShadow object-lot map (M2, 73%
on IP). Those three go first in lane C. Bin restore after the chain reported 4 sha changes, all logs written during the run
(pccheck-summary.txt, pccheck.csv, rtx-remix bridge32/64.log), every managed file read back.

### 7.4 Lane C: the three tables today's worlds already hit (0.36.299-ucap2, built 2026-11-22)

All three are fixes to default-ON items: they stay ON, no new switch. Under the old cap each table behaves as before. Native only
(ts3fixes, ini and agent unchanged from ucap1). Build `ts3native.asi.unbounded-caps-1122c` (sha256 eb590e1ce5ea). Self-tests
cover the overflow case and under-cap equality for each item.

| item | was | now | under the cap | 4x / streamed world |
|---|---|---|---|---|
| M2 AutShadow object-lot map | 65,536-row table, 49,152 usable, then refused (resync cuts) | the table doubles at a cut once it passes the old compaction mark (45,056), and while seeding at the old seed headroom; ceiling 262,144 rows (196,608 usable, 10 MB); log line `autonomy shadow: object-lot map grown to N rows` | same 65,536 table, same probe order; compaction still runs first, in place (same layout as before) | IP x4 is ~143k rows: fits under the ceiling; at the ceiling the old full path (resync) |
| M11 DoorMemoX table | 256-row ring: each put overwrote the next row, live or not | a put reuses the key's own row, else the oldest dead row (empty or past DoorMemoXMs) in ring order, else doubles the table (worker only) up to 4096 rows (240 KB); only at the ceiling does the ring evict a live row (`dmx` full) | every answer is the newest put of its key, as before (one row per key is that row); only the miss split counters can differ (a row past its age is reused, so a later miss counts as cold, not age) | live rows scale with door asks per DoorMemoXMs, not with world size; 4096 is 11x BP's need [inferred: BP lost 368 live rows in 10 min, i.e. more than 256 alive at once at peaks] |
| L4 UnseenAnim slots | `(actor >> 4) & 4095`, direct-mapped; a home slot another actor owed in held the call (AAH_SLOT) | Fibonacci hash of the address and a 16-slot probe chain: the actor's own slot anywhere in the chain first, else the first slot nobody owes in, else the home slot (a hold, as before); new caps row `aaDisp` = distinct actors whose home another actor owed in (the "real contention" count asked for) | an actor whose home is free or its own gets the slot the old table gave; skip phase and debt rules unchanged; a stale debt in the chain is dropped as before (the actor's own next lookup would drop it too) | ~4x actors still fit 4096 slots at 16-slot chains; `aa` full counts only a fully owed chain |

The ucap1 collision count (16,909 / 8,891 / 1,182 holds) was per call; `aaDisp` gives the distinct-actor count for the next
run. [inferred: the address bits 4-15 of actor objects repeat because they come from a few allocator size classes]: the rerun's
`aaDisp` with `aa` full 0 settles it either way.

Queue `unbounded-caps-correct-1122c` (smoke BP 2 min, then BP / IP / SV 10 min; Apex + bridge; same checks as §7.3 plus: the native
caps line has the lane C rows, and no caps line may show `asOl`, `dmx` or `aa` full > 0). Waiter
`probe-out/queue/wait-unbounded-caps-1122c.sh` (not `chain-*`): waits for `probe-out/queue/ovf-rerun-1122.done` and no chain listed.
Results: (pending, filled after the run).

## 8. Lane A hub built: CP-1 lot generation counter + world event (2026-11-23, branch `feature/cp1-lotgen-1123`, 0.36.299-cp1)

docs/332 package CP-1. Native only, `native/ts3native/lotstream.cpp`; no behaviour change, no consumer yet. Build
`ts3native.asi.cp1-lotgen-1123`; queue `cp1-lothub-correct-1123` (not run). ST-12 (hub coverage) is still open; the
coverage claims are docs/51 §591.

### 8.1 What it does

- `[LotHub] Enabled=1` (default 1, always on, measurement only; 0 = kill switch). `ApplyLotHub` checks both prologues
  (`0xABD3C0` add execute, `0xABD3E0` remove execute, the bytes `[LotStream]` already checks) and hooks both or none; it
  runs before `ApplyLotStream`, which now reuses those two detours (`LsAddHook` / `LsRemoveHook`) instead of hooking them
  again. A different prologue (another mod's hook) = `SKIP lot hub`, counters stay 0. Apex does not own these two sites
  (its `AXF_LOT` sites are `0xAC1130` and later; S3SS's object throttle detours `0xAC1130`, below the execute), so the hub
  does not stand down for Apex.
- Each add execute that runs fn `0xAC1130` and each remove execute that runs fn `0xAB4C00` bumps `lhAdds` / `lhRemoves`
  and `lhLotGen` after the game's call returns. Other fns on those executes are not lot events.
- The ts3fixes `world:` line (OnWorldLoadFinished, `userdiag.cpp` `UdWorldLoad`) and `world quit` line bump `lhWorldGen`
  and `lhLotGen`. Both need `[UserDiag] Enabled=1` (default) and ts3fixes; without them only lot events count.
- Read-back: `OK   lot hub: ready 0x3 (...)` at start, and `lot hub: <kind>, lot gen N (adds A, removes R), world gen W,
  subscribers S` at every world event, at powers of two and at every 64th lot event.
- `[Verify]`'s probe 65 `lotAddExec` (`0xABD3C0`, diagnostics, off) now finds the site taken and skips itself; the lot
  add cost it measured is still in `[LotStream]` (`lotAdds` / `lotAddUs`).

### 8.2 Handoff API for CP-2 and CP-3

Counters (v223, `ts3native_counters`, registry row v223; all interlocked, monotonic, never reset):

| Offset | Field | Meaning |
|---|---|---|
| +0x6B48 | `lhReady` | bit 1 add execute hooked, bit 2 remove execute hooked; 0 = no lot events (kill switch or another hook) |
| +0x6B4C | `lhLotGen` | lot generation: +1 per lot add, lot remove, world load, world quit |
| +0x6B50 | `lhWorldGen` | world generation: +1 per world load and world quit |
| +0x6B54 | `lhAdds`, `lhRemoves` | lot add / remove fn runs seen |
| +0x6B5C | `lhFired` | subscriber calls made |

Native subscription (CP-3, same translation unit, include order after `lotstream.cpp`):

```cpp
enum { LH_ADD = 1, LH_REMOVE = 2, LH_WORLD_LOAD = 3, LH_WORLD_QUIT = 4 };
typedef void (*LotHubFn)(int kind, const u8* lot, LONG gen, void* ctx);  // lot NULL for world kinds; gen = lhLotGen after the bump
static bool LotHubSubscribe(LotHubFn fn, void* ctx);                     // install time only, 16 rows, no unsubscribe
```

Rules for consumers:
- Subscribe from your own `Apply*` (loader thread, before the game runs), only when your own switch is on (default off).
  Your switch, not the hub's, gates the behaviour.
- The callback runs on the sim thread inside the game's lot add / remove execute (after the game's work) or inside the
  ts3fixes text-line handler. Keep it short: clear or mark rows, no waits, no game calls, no locks the sim thread can
  already hold.
- `lot` is the `this` the execute passed to the fn (the lot object; id at `+0x48..+0x4C`, as `[LotStream]` logs it).
- Threads other than the sim thread do not get callbacks: cache `lhLotGen` (or `lhWorldGen`) with your rows and drop them
  when the live value differs. `lhReady == 0` means no lot events will come: keep today's behaviour (no hub-based
  clearing) rather than assume "nothing changed".
- A world event bumps `lhLotGen` too, so a consumer that only checks `lhLotGen` also catches world changes.

Managed side (CP-2): ts3fixes already sees world load / quit itself (it sends the `world:` / `world quit` lines), so
`OnWorldChange` needs no native call. If `OnLotLoad` needs the native lot generation, add a `UserDiag` op (registry
"UserDiag ops") returning a byte of `lhLotGen` change since the last ask; the op answer is a `u8`, so a full counter read
does not fit one call. Managed lot add / remove can also come from the game's own lot events; pick whichever ST-12 shows
covers travel and Edit Town.

### 8.3 Tests

- Native selftest `TestLotHub` (`selftest.cpp`): both `[LotStream]` modes through the real hooks (return value kept, the
  game's call first, one event per call), other fns on the execute ignored, world events, no-subscriber path, full
  subscriber table refused, kill switch, install refused without the game's prologues.
- Queue `tools/harness/queues/cp1-lothub-correct-1123.json` (one BP load, ~15 min, repro `lothub-visit`: selected Sim to
  the farthest community lot, camera pan, home): `OK   lot hub: ready 0x3`, one world-load line, an add/remove line with
  adds and removes > 0 after the world load, no `SKIP|FAIL lot hub`, 0 script errors, 0 watchdogs, household untouched.
  Results: (pending).

### 8.4 No leak across worlds (added 2026-11-23 with CP-2)

`g_lhSubs` is a fixed 16-row table filled at install time only; `LotHubSubscribe` with an fn + ctx pair already in the
table returns true and keeps the one row (an `Apply*` that runs twice cannot grow it), a 17th distinct row is refused and
logged. Rows hold function pointers and the consumer's own static ctx, never a lot: the `lot` pointer is only lent to the
callback for the call. Counters are plain `LONG`s. Nothing in the hub grows with world loads, travel or lots
[verified: code read; selftest `TestLotHub`: duplicate subscribe keeps `g_lhSubN == 1`, full table refused]. The
duplicate check was added on the CP-2 branch (commit "CP-1 hub: duplicate subscribe keeps one row"); cherry-pick it to
`feature/cp1-lotgen-1123` if CP-1 merges alone.

## 9. Lane A managed hook built: CP-2 `OnLotLoad` / `OnWorldChange` (2026-11-23, branch `feature/cp2-lothook-1123`, 0.36.299-cp2)

docs/332 package CP-2, on CP-1. `mods/ts3fixes/src/LotHub.cs` (game side) + `LotHubCore.cs` (pure dispatch, offline
selftest); native op 49 in `lotstream.cpp`. The hook only dispatches; it has no switch of its own and no consumer is
subscribed in a release build, so nothing changes. It touches no Sim (dispatch only); each CP-4 consumer brings its own
switch (default off) and household rule.

### 9.1 Design choice: native queue polled once per sim frame (not a callback, not a bare generation compare)

- A native-to-managed callback bridge does not exist (ts3native can only answer `Route.GetOption2` calls with a bool), so a
  callback would need a new Mono icall path: heavier and riskier than a poll.
- A bare "did `lhLotGen` move?" ask (one bool per frame) is exact but says nothing about *which* lot, and CP-4's consumers
  (O18 rows keyed by lot id, DoorGen and GhostSweep per lot) need the lot. So native keeps a 64-entry queue of
  (kind, lot id low dword `[lot+0x48]`), filled in `LotHubFire` and drained by ts3fixes, both on the sim thread: no lock,
  no lost or doubled event.
- Cost: no lot subscriber = no task, and the native queue is never armed (the first pop arms it): zero work. With a
  subscriber: 1 call per frame when idle, 35 per event (pop, overflow?, remove?, 32 id bits); events are rare (camera
  streaming, travel, Edit Town), so a frame pays ~1 call [inferred, measure in CP-5].
- A full queue drops further events and marks overflow; the next pop hands out one `Overflow` event (kind 0, lot 0) with the
  queue cleared, which a consumer treats as "every lot changed". World load / quit lines clear the queue
  (`OnWorldChange` supersedes it).
- `OnWorldChange` comes straight from Instantiator's handlers (first statement of `DiagWorldLoad`, `DiagWorldQuit`), so it
  works without ts3native too. Lot events need ts3native with `[UserDiag] Enabled=1` and the hub installed
  (`lhReady != 0`, op 49 hi 4); otherwise `Fixes.LotHubLotsLive` is false and consumers keep today's behaviour.

### 9.2 API for CP-4

```csharp
// LotHubCore (mods/ts3fixes/src/LotHubCore.cs)
public const int Overflow = 0, Add = 1, Remove = 2;
public delegate void LotFn(int kind, uint lotLow);   // lotLow = low dword of the lot id (0 for Overflow)
public delegate void WorldFn(bool load);             // true = world load finished, false = world quit
// Fixes (LotHub.cs), sim thread, from the consumer's own switch-on path:
Fixes.LotHubOnLotLoad(LotHubCore.LotFn f);           // starts polling at once if a world is live
Fixes.LotHubOnWorldChange(LotHubCore.WorldFn f);
Fixes.LotHubOff(LotHubCore.LotFn lot, LotHubCore.WorldFn world);  // switch-off path; polling ends with the last lot subscriber
Fixes.LotHubLotsLive                                  // false = no lot events in this world (keep today's behaviour)
```

Rules: callbacks run on the sim thread (lot events: in a ts3fixes task, the frame after the game's execute; world events:
inside the world handlers). Order = arrival order, then subscribe order; each event once. A throwing consumer is counted
(`LotHubCore.Faults`) and skipped. Compare `lotLow` with `(uint)lot.LotId` (docs/51 I-593-1, inferred). Lot events
queued before a world load are not delivered: that world's `OnWorldChange(true)` stands for them. Use static methods or
long-lived delegates so a re-subscribe after reload matches the existing row.

### 9.3 No leak across worlds

The subscriber lists live for the session, hold distinct delegates only (equal method + target = one row, so a consumer
that subscribes again on every world load keeps one row) and are capped at 16 each; `LotHubOff` removes rows. The hub keeps
no per-lot state and no reference to a lot or object (ids only, as `uint`). One poll task per world: world quit bumps its
generation and it ends; a new one starts at the next load only with a lot subscriber. Native queue: fixed 64 entries of
ids. `LotHubCore.Subscribers` is the counter: it must stay flat across travel and return (queue below; selftest: 50
load / quit cycles with re-subscribe).

### 9.4 Tests

- Managed selftest `tools/selftest/lothub` (in `selftest-all.ts`): idle frame = 1 call, arrival + subscribe order, full
  32-bit id, 35 calls per event, overflow = one kind-0 event, per-frame cap 16 keeps order, world events once each,
  throwing consumer skipped, 50 load / quit cycles with re-subscribe leave the count flat, unsubscribe by an equal
  delegate, cap 16.
- Native selftest `TestLotHub` additions: queue off until the first pop, FIFO with kind and id bits, world event clears,
  overflow handed out first, duplicate `LotHubSubscribe` keeps one row.
- Queue `tools/harness/queues/cp2-lothook-correct-1123.json` (one BP load, ~25 min, repro `lothook-travel`): `arm` the
  Audit 2026-11-23: job `bp-lothook-edit-town` (repro `lothook-edit-town`, UNVERIFIED, never run): two Edit Town enter / exit rounds between pans; every `lothub: result` must read `polling True`. Code read [inferred]: `LhTask` ends only when `GameStates.IsWorldShuttingDown` is set or the world generation changes (`LotHubWorldQuit`), and `LhEnsureTask` runs only from `LotHubWorldLoad` and a new lot subscribe, so Edit Town survives if it neither sets `IsWorldShuttingDown` nor quits the world; if the job shows `polling False` after an exit, make `LhTask` skip the poll while shutting down but keep sleeping until the generation changes (no code change made: not decidable without the run).
  cp2b (2026-11-23, no run needed to decide): the task no longer depends on that. Per frame `LotHubCore.TaskStep(current, world, live)`:
  end only when the world goes (`LotHubWorldQuit` / `IsWorldShuttingDown`), the task is replaced or the last lot subscriber leaves; outside
  live mode (Edit Town, CAS, Build / Buy, any substate) it waits without popping, so the native queue keeps the events (overflow past 64 =
  one "every lot" event) and the first live frame drains them. `InWorldState.InWorldSubStateChanging` (fired before every in-world mode
  change [verified static, InWorldState.cs]) is subscribed at world load and dropped at quit; on the change to `LiveMode` it counts
  `mode returns` and calls `LhEnsureTask`, which re-arms (`rearms`) when the armed task's Simulator object is gone
  (`LotHubCore.ShouldArm`). Offline selftest `tools/selftest/lothub/run.ts` covers wait / drain / end / re-arm. The job now also requires
  one `lothub: result` line with `mode returns >= 1` (the hook fired). Build `ts3fixes.package.cp2b-lothook-1123`.
  test consumer (`Fixes.LotHubDiag`, reflect_call only), community lot + camera pan, vacation to France and home, `arm`
  again: `lothub:` add and remove lines with lot ids, world quit / load lines, `subscribers 2` at every read-back (flat
  across travel and the second `arm`), faults 0, 0 script errors, 0 watchdogs, household untouched. Results: (pending).

## 10. Lane A consumers built: CP-3 + CP-4 (2026-11-23, branch `feature/cp34-lanea-consumers-1123`, 0.36.299-cp34)

docs/332 packages CP-3 (native) and CP-4 (managed), on CP-2. Native `native/ts3native/lanea.cpp` (included after
`hostcalls.cpp`), managed `mods/ts3fixes/src/LaneA.cs` + `LaneACore.cs`. Every consumer has its own switch, default off;
each only clears its own cache rows or drops the rows of an unloaded lot, so no answer changes and household Sims see what
they see today (docs/332 H column). Builds `ts3native.asi.cp34-lanea-1123`, `ts3fixes.package.cp34-lanea-1123`.

### 10.1 Consumers

| Item | Switch (default) | Table | World load / quit | Lot remove | Why this is exact |
|---|---|---|---|---|---|
| M1 | `[LaneA] AsSimIds=0` | autshadow `simIds` | cleared at the next cut | - (Sim ids) | a Sim missing from the set gets the plain typed mark until its next `AddRow` (exact, docs/321 M1) |
| M3 | `[LaneA] AsTier=0` | `g_asTierTab` | cleared at the next cut | - | no tier = tier 1, refreshed every cut |
| M4 | `[LaneA] AsLotTier=0` | `g_asLotTier` | cleared at the next cut | the lot's row (rehash) | no tier = tier 1 |
| L1 | `[LaneA] AsLite=0` | `g_asLite` | cleared at the next cut | the lot's row (rehash) | no baseline = marked whenever prints run (as at an epoch) |
| M9 | `[LaneA] SpDoor=0` | snap `g_spDoorLots` | every slot emptied | the lot's slot emptied | an empty lot is invalid; ts3fixes re-pushes before use (`SDM_Q`), as after `SDM_INV_HI` |
| M11 | `[LaneA] DoorHold=0` | hostcalls `g_dmxHold` | cleared on quit, generation bumped | kept | DoorGen.cs re-sends every held lot at its arm; a hold is the script lot's handler list, which outlives streaming [inferred, §595] |
| M8 | `[LaneA] ReplanDedupe=0` | replandedupe `g_rdTab` | emptied (under `g_rdLock`) | - | rows serve within 500 ms only; nothing of the old world is served |
| O12 | `[LaneA] EarlyOut=0` (needs `[RouteEarlyOut]`) | earlyout labels | freed on quit | rebuilt when `lhLotGen` moved | a rebuild is the same union-find |
| O11 | none (diagnostics buffer) | `[LotStream]` pump buffer | - | - | 64 -> 256 events per pass |
| O18 | ts3fixes `laneaenvlist` | `sExLotOff` (envlistmemoexact) | cleared on quit already (docs/323 L19) | the lot's row | a lot that streams back in is tried again |
| L15 | ts3fixes `lanealoop` | gohome `sLastShortcut` | cleared on load and quit | - | ticks of another world are not this world's loop; a missing row = no loop mark (gohome is townie-only) |
| M15 | ts3fixes `laneatp` | `sUfTpPlaced` | kept | - | at the 4096 cap other worlds' rows go first; not cleared on travel, so the home world's load-time cleanup still finds our placements (rule "clean up our damage on load") |
| DoorGen per lot | ts3fixes `laneadoorgen` | - | - | add: the lot's hold re-sent (op 36) | same answer DoorGen sends at arm |

Not built here (both built since, §11): **L3 GhostSweep per lot.** The footprint map is world-wide (not keyed by lot), so a per-lot sweep is a whole-map
walk, and a removal needs the routing-world lock with no job running; during play that is a per-tick node budget plus the
existing recheck: a design of its own (decision for Rodney: build it as its own package or keep the load-screen pass).
**M8 per-lot validity** (route rows keyed by the lots they touch, a gain) is also not built: only the world-change reset is.

Thread rules: the hub calls `LaHub` on the sim thread. Autshadow tables change only at the next cut (`LaAsApply`, first
step of the cut, under `g_asDirtyLock` as the epoch path), the place that already owns them; the others change in the
callback under their own lock (`g_rdLock`, `g_eoCs`) or on the sim thread that already owns them (door lots, holds). The P4
door dump reads door lots from the worker under `__try` (diagnostic, documented as torn-tolerant); slots are emptied, never
freed, so it never reads freed memory.

Paused / no cut (audit 2026-11-23, code read [verified]): `LaAsApply` runs only from the autshadow cut; with no cut nothing is applied and `LaHub` only sets `g_laAsWorld` and appends to a 64-slot `g_laAsGone` (a 65th sets `g_laAsGoneOver`, which later clears the lot tables instead), so the pending state is bounded and does no work. With nothing pending the cut returns after three loads. When a cut does run while paused it applies at most one world reset and 64 `LaRehashDrop` passes (about 64 x 1024 rows, microseconds) under `g_asDirtyLock`: harmless, no code change.

### 10.2 No leak

- Every table is fixed-size or reuses its slots: autshadow and rd tables are static arrays; emptied door slots keep their
  heap buffers and `SpDoorLotFor` reuses them before appending, so `g_spDoorN` never passes the most lots one world held
  (selftest: 50 load / quit cycles of 10 lots, `g_spDoorN` 10 and the first slot's buffer unchanged); earlyout labels are
  freed on quit and rebuilt on demand.
- Rows hold ids (lot ids, Sim ids, keys), never an object the game frees.
- Managed tables: `sExLotOff` (ids, cleared on quit, rows of unloaded lots dropped), `sLastShortcut` (ids, cleared on every
  world change), `sUfTpPlaced` (ids + a point, capped 4096, other worlds first). Lot hub subscriptions are static delegates
  (one row each, `subscribers 2` flat).
- Counters: export `ts3native_lanea` (per table: on, rows now, high water, world clears, lot drops; docs/registry.md),
  ts3fixes slots 1699-1706 (rows now / high water per managed table, doorgen per-lot sends, envlist drops). Log lines:
  `lane a: world load|quit, spDoor a->b (slots n), doorHold .., rd .., eo .., reset ok|FAIL rows left`, `lane a: autshadow
  reset at cut, simIds a->b, tier .., lotTier .., lite .., reset ok`, `ts3fixes: lanea: world load|quit, envlist n/hw, tp
  n/hw, loop n/hw, doorgen lots n, envlist drops n, lots live b, subscribers n`.

### 10.3 Tests

- Native selftest `TestLaneA` (`lanea-test.h`): keys 0 = no subscriber, no hub = SKIP, duplicate install = one row,
  EarlyOut refused without `[RouteEarlyOut]`; drop of one of 300 lots keeps the other 299 findable by the slot finders;
  65 removes before a cut = lot tables cleared; door slot reuse with buffers; 50 world load / quit cycles with lot churn:
  every reset table 0 after the cut, door slots flat at 10, lot-tier high water 9; holds cleared on quit only, kept on lot
  remove; rd emptied; keys off = nothing changes.
- Managed selftest `tools/selftest/lanea` (in `selftest-all.ts`): drop by low dword, overflow clears, other-world pruning,
  high water, 50 load / quit cycles with re-subscribe (subscribers flat at 2, rows 0 after each quit, high water = one
  world's rows).
- Queue `tools/harness/queues/cp34-lanea-correct-1123.json` (one BP load, ~30 min, repro `lanea-travel-reload`:
  community lot + pan, France and home, pan, save to a scratch save `LaneA-cp34-1123` and reload it, pan), every key on
  but EarlyOut: `reset ok` at >= 3 world quits and loads, autshadow reset at the cut >= 3, managed world-load lines with
  envlist 0, loop 0, `lots live True`, `subscribers 2`; doorgen per-lot sends > 0; no `FAIL|SKIP lane a`, no `FAIL rows
  left`; 0 script errors, 0 watchdogs, household untouched. Results: (pending). CP-5 (the full save matrix) is next.

## 11. Lane A gaps built: L3 GhostSweepLot, M8 ReplanDedupeLot, shared game-state gate (2026-11-23, branch `feature/cp4b-ghost-m8-1123`, 0.36.299-cp4b1)

The two §10 "not built" items, on CP-3/CP-4. Native `lanea.cpp`, `replandedupe.cpp`, `lotstream.cpp`; managed `LotHub.cs`,
`LaneA.cs`. Builds `ts3native.asi.cp4b1-1123`, `ts3fixes.package.cp4b1-1123`; cp4b2 (`*.cp4b2-1123`) adds the M8 A vs B counters (§11.2). All new switches default off. Static
claims: docs/51 §597.

### 11.1 L3 GhostSweepLot (`[LaneA] GhostSweepLot=0`, 1 count only, 2 remove; `GhostSweepLotVerify=0`; `GhostSweepLotNodes=2048`)

**What "the full pass for that lot" is [verified disassembly].** The load-screen pass (`FpgCensus`) walks P's footprint map
`[P+0x24]`, keeps the ids that match `FpgEntry`'s ghost signature, and calls `LotRouting_RemoveObjectFootprints 0x635700` with
lot 0. With lot 0 that fn removes each map node of the id from the node's own quadtree Q (`FME+0xC`), skipping a node whose Q
is listed for the id in P's second map `[P+0x3C]` (`0x635794`), and erases Q-less nodes. With a lot id it checks the lot
(`0x628A90`, `ret 8`), takes the lot's record (`LotTable_GetOrInsert 0x62EAB0` on a hit, `ret 4`, record = node + 0x10) and
removes the id from each Q in the record's vector `+0x74..+0x78` (`0x635885..0x6358F0`). The lot's cells of an id are its
nodes whose Q is one of the lot's Qs. Lot ids: the hub's `[lot+0x48]` u64 (I-597-1, inferred, counted by `lotUnknown`).

**Per-lot pass.** On a hub lot remove the lot id goes to a 64-entry queue (bookkeeping, any game state). At the end of a
Simulate iteration (`GsTick`, sim thread, `SimLoopAdd`; the table grew from 8 to 16 callbacks), when the game is live and
nothing blocks, one slice runs: whole buckets up to `NodesPerTick` nodes, `FpgEntry` on each node, ids whose node Q is one of
the lot's Qs kept, then each kept id removed with `FpgRemove`'s rechecks (no object, not parked) and the lot form of the same
game call (and of `DiscPfSweep`'s private-footprint key). An id with any `[P+0x3C]` row is kept (`kept3C`) for the
load-screen pass, because the lot form has no such skip. Q-less nodes hold no cells and no lot; they stay for that pass too.
So the cells this pass clears are exactly the cells the full pass would clear for that lot, minus the kept ids (counted).

**Never blocks the sim thread.** A slice is skipped (`skipBusy`) when a discourage record is queued (the full pass drains
first), a route job runs, the world is closing, the sim thread holds `+0x918`, or the `+0x918` try (deadline 0, through
`LockAcquireHook`) fails. `g_rgOffSimOpen` is raised before the second job check (worker Dekker order, as `FpgCensus`). The
slice finishes its removals before it lets the lock go, so no id is carried across ticks. A rehash between slices restarts
the walk (`restarts`; more than 8 = given up, logged). More than 64 queued lots = one whole-map pass (lot 0: the load-screen
pass itself, sliced). World load / quit clears the queue (the load-screen pass runs at load).

**Verify (`GhostSweepLotVerify=1`).** Per removal: the id's nodes on the lot's Qs after = 0 (`verifyLeft`) and its other
nodes unchanged (`verifyOther`). After each lot job a dry pass of the same lot must find only the `kept3C` ids again
(`verifyLeft`). All gated at 0 by the queue.

**Household.** Footprint rows of objects only; the pass removes only footprints whose object is gone (the census signature).
No Sim is read or changed. A household lot never unloads while it is the active lot, and a household object is never a ghost
(it exists). The household lot staying loaded while live is [inferred]; it does not matter for exactness.

### 11.2 M8 ReplanDedupeLot (`[LaneA] ReplanDedupeLot=0`)

Each `g_rdTab` row keeps `lhLotGen` as read before its plan ran. A row is served only while `lhLotGen` is unchanged (and
`lhReady`); otherwise the plan runs, which is the answer without the dedupe (`rdLotDrops`). That is stricter than "drop the
lot's rows": rows do not record the lots they touch, so any lot event refuses every older row. Rows live at most 500 ms, so
the cost is a few extra plans after each lot event. One writer (`RdRun`) and one serve path (`RdRun`, used by Replan,
ReplanFromPosition and the FailMemo Plan path) exist [verified grep: `g_rdTab` is written only in `RdRun` and the `LaHub`
world reset], so there is no path that serves around the check. Any thread reads the interlocked counter (docs/321 §8.2).

Design A vs B data (cp4b2, Rodney 2026-11-23; A = the rule above, B = refuse only the rows of the lot that changed). Counted
while `ReplanDedupeLot=1`, in the `ts3native_lanea` `gs` block (v3) and logged as `m8: <why>, m8Hits H, m8Misses M,
m8LotDrops D, m8RowsDropped R, m8WouldHit W` plus `m8 B-gain: X% of lookups (W of H+M)` at each world load / quit and on
`Fixes.LaneAGate("m8")`:
- `m8Hits`: keyed lookups served from a row; `m8Misses`: keyed lookups that planned.
- `m8LotDrops` / `m8RowsDropped`: lot add / remove events (sim thread, `LaHub`) that refused at least one failed row still
  inside its 500 ms window, and how many such rows (each row counted once, at the first event after its plan). A refuses
  lazily, so "emptied the cache" means these rows became unservable, not a memset.
- `m8WouldHit` (= `rdLotDrops`): a miss whose row passed key, goal, start, window and the other invalidation checks and was
  refused only for a lot event since its plan. That is the most B could serve, since B still refuses rows of the changed lot.
Decision number: `m8 B-gain` = `m8WouldHit / (m8Hits + m8Misses)`. Selftest `TestGsLot` covers it (2 live failed rows counted,
success and expired rows not, second event not double-counted, the refused lookup counted once, switch off counts nothing).
The figure comes from queue `cp4b-correct-1123` job `ip-lanea-m8-pan` (Isla Paradiso, speed 3, ~8 min of camera pans, only
ReplanDedupe + ReplanDedupeLot on). Hollow floors (the row FAILs): raw lot events `lhLotGen >= 20` over the job window
(`nativeRequire`, /native delta after the load; tools/probe/agent.js reads the CP-1 block at +0x6B48) and `m8Hits + m8Misses
>= 200` lookups (the report's `m8 B-gain ... (W of L)` line, L >= 200). `m8LotDrops` and `m8WouldHit` stay informational, so a
run where B truly gains little can still say keep A. The BP job's m8 lines are informational.

### 11.3 Shared game-state gate (`SimLiveNow`, lotstream.cpp)

Rodney 2026-11-23: nothing of ours runs sim-state work while the game is paused, in CAS, Buy/Build, Edit Town, travel or load
screens or the main menu. No native helper existed (`UfcBuildBuy` reads only the Build/Buy byte), so `SimLiveNow()` is the
one shared helper: live = the last ts3fixes push says live (`GameStates.IsLiveState`, not travelling, CAS, Edit Town,
Build/Buy-like, not paused), the push is under 2 s old (ts3fixes re-sends every 500 ms; scripts stop in Build/Buy, so a
stale push closes the gate), and the routing world's Build/Buy byte `world+0x97B` is 0. Unknown counts as not live; world
load / quit reset it to unknown. ts3fixes pushes only when a native consumer asks (op 49 sub 6; `GhostSweepLot` on).
GhostSweepLot does no work outside live mode (`skipNotLive`; only the queue grows). ReplanDedupeLot has no event work of
its own: the compare runs inside a replan, and a refusal outside live mode is counted (`rdLotNotLive`, expected 0).

### 11.4 Counters, no leak

Export `ts3native_lanea` v2: rows `gsLot` (queued + running, high water, world clears, lot jobs done) and `rdLot`, then a
`gs` block (registry). Every table is fixed: queue 64 u64 ids, one job record, ids per slice 256 on the stack, 32 Qs. Ids
only, no pointer outlives a slice. World events clear the queue; 50 load / quit cycles in selftest leave it at 0, high water
<= 65.

### 11.5 Tests

- Native selftest `TestGsLot` (`lanea-test.h`) on a fake routing world laid out as the disassembly: a lot A sweep removes
  lot A's node of a two-lot ghost and keeps its lot B node, a lot B ghost, a `[P+0x3C]` ghost and a live object; paused, CAS
  and the Build/Buy byte turn work away with the queue kept; a running job, a failed lock try and a queued record skip the
  tick; a rehash mid-job restarts; count-only mode removes nothing; an unknown lot is counted; 65 lots = the whole-map pass;
  50 world cycles keep the queue flat; a lot-0 removal is caught by `verifyOther`; the op 49 sub 5 / 6 / 7 answers; a stale
  push is not live.
- Queue `tools/harness/queues/cp4b-correct-1123.json` (one BP load, ~35 min, repro `lanea-gate-travel-reload`): lot unloads
  while playing, one whole-map pass queued, ~60 s paused (no work, the gate turned it away), CAS and Build/Buy (no work),
  unpause (drained), France and home, save-reload, verify counters 0. Results: (pending).
