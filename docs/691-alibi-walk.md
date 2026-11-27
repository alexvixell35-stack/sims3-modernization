# 691. alibiwalk: unseen NPCs off the active lot skip the long walk ("alibi generation")

Status: built 2026-11-26, default off (`alibiwalk`, `alibiwalklog`). Proof: see §6.

## 1. What unseen NPCs off the active lot cost, and what is already cut

Sources: docs/322 (distance bands: beyond 150 m = 85-89 % of NPC sim-thread time, ~4 ms/s per far NPC, flat by distance;
far band = Sim task 122-150 + autonomy 30-40 + locomotion 25-28 + pose 6-9 ms/s), docs/243 / docs/211 (wakepark, wakepoll,
unseenpark measured ~0-5 ms/s, parked), docs/144 (script throttles rejected), docs/245 (motive decay: no gain), and the new
measurement in §4.

Already cut before this doc (not redone here): lot trips at their start (`visit`, `visitlot`, `gotolot`, `gotocommunity`,
`gohome`, `rabbithole`, ON: a townie unseen at the start of the trip is placed on the lot), staged arrivals, RabbitHoleDirect,
the docs/686 §33-37 waits (elevskip, fridgeff, rhskipunseen, bookskipunseen, gymtwin), unseen fallbacks (docs/202: plan
deadline, ghost walk, teleport recovery), lane / leg snaps, DoLoop caps (docs/632-634), unseen bar service (docs/628).

What none of them covers: a walk that is not one of the hooked trip interactions, or that started while the Sim was seen
(a trip replan, a walk to an object across a park or a big community lot, a GoHere from a situation, a walk out of a
rabbit hole to the next lot, a walk that was seen at its start and left the view). §4 counts them.

## 2. Design (as built)

`AlibiWalk.cs`, rules in `AlibiWalkCore.cs` (offline selftest `tools/selftest/alibiwalk`).

- **Where:** `SimRoutingComponent.DoRoute` with a successful plan, through BlockedUse's existing `BuRouteHook.RouteHook`
  (one more call before the game's body). A route there is about to be followed, so a plan made only to test
  reachability (autonomy, situations, `CanRoute` checks) never moves anyone.
- **Which walks:** a human NPC's route of at least `kAlibiWalkMinM` (60 m) made only of walking paths and portals (doors,
  stairs, elevators: `PathType` SimPath / PortalPath / PortalTraversalPath), ending in a walking path. A car, boat, ocean,
  wade, frozen pond, horse lane, stroller, UFO or elevated train leg carries a route action that changes the Sim's state on
  the way: such a route is never cut. At least half the minimum must be skipped.
- **The cut:** the tail start is a point of EA's own plan: `kAlibiWalkTailM` (10 m) before the end of the last walking
  path (`GetSegmentAtDistanceBeforeEnd` with a segment boundary, read by segment index), or the start of that path when
  it is shorter (where the last door or stair set the Sim down). A point that is not within the tail of the end (a long
  last path is often one segment) is not used: the Sim is then set on the route's end itself, EA's own goal spot, and
  the replan is 0 m. The Sim is set there facing the path (`PlaceSim`), the route is replanned from there (`Route.Replan`, EA's
  own pattern after a push in `DoRoute`), and EA walks the tail: doors, slots, arrival callbacks, lead-in overlays and the
  interaction after the walk are the game's. A failed tail replan, or one longer than `3 x tail + 10 m` (it went another
  way), puts the Sim back where it stood and replans the original walk (counted).
- **Scope:** never a household or selected Sim (`IsPlayerSim`; also with `[Experimental] HouseholdToo=1`, which never
  lifts teleports), never a pet or horse, a held / riding / carrying / carried Sim (`UfHeld`), a Sim with followers (GoHome,
  GoToLot, rabbit hole followers), in a GoHereWith group (`PartOfGoHereSituation`, `GoHereWithSituation`), or a route whose
  start, tail start or end is on the active lot or the active household's lot.
- **Camera:** decided once, at the cut, through camsafe's `CsDecide(sim, false, target, tailStart, end)`: TownieUnseen (on
  foot, standing, not in a hidden room, off screen, away from every viewer) plus the route's target off screen and both
  spots away from every viewer; with camsafe on, its margin / recently-seen hold / jump pause apply (docs/690). With camsafe
  off the same call is TownieUnseen + FarFromViewers. The cut is one tick and the Sim is then on EA's own walk, so there is
  no half state for `CsSnap` to finish; a camera that arrives later sees a Sim walking the last metres.
- **Not touched:** motives, skills, relationships, the interaction itself, saves (a Sim set somewhere is saved there like
  any Sim), EA tuning.

## 3. Tunables, counters, check

| Tunable | Default | Meaning |
|---|---|---|
| `kAlibiWalk` / `kAlibiWalkLog` | false / false | switches `alibiwalk` / `alibiwalklog` (log alone = compare mode: counts what it would cut) |
| `kAlibiWalkMinM` | 60 | shortest route cut, m |
| `kAlibiWalkTailM` | 10 | metres of the last walking path still walked |

Counters 4920-4939 (docs/registry.md). `AlibiWalkCheck result` prints them with the long NPC walks by interaction (n,
metres, cut or would, seen); `run` evaluates AlibiWalkCore. Repro `alibiwalk-check`.

## 4. Measurement: what unseen NPCs cost (s640a SV, s641c / s640c BP, off arms) [measured]

Setup: s606 (merged asi ci-45754579321 + its ini through storm-ab-ini.sh, heap 512, 30 min route storm seed 156, new game
from the world file), package alibi-1126 (combo a0ce5d1a + this switch in log mode), camsafe on, probes `[UnseenProbe]
Bands=50,150,400,1000` + `[Diag] TaskSeam` + `[AnimPhase] Split` (attribution shares with heavy probes on, docs/322 §1), camera
still for 15 min then s606's forced case. ts3agent `sim_health` every 3 min for the wall-time census.

Sim-thread ms/s by band (window = the storm, cumulative minute lines; total = loco + pose + Sim task + autonomy; plans,
waits, footprint are parts of those):

| band | SV loco | pose | Sim task | autonomy | plans | total | BP loco | pose | Sim task | autonomy | plans | total |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| < 50 m | 5.2 | 4.2 | 14.0 | 4.7 | 0.1 | 28.1 | 3.1 | 1.2 | 9.2 | 1.5 | 0.1 | 15.0 |
| 50-150 m | 14.8 | 2.5 | 35.6 | 8.5 | 0.5 | 61.4 | 11.8 | 3.6 | 42.6 | 3.6 | 0.7 | 61.6 |
| 150-400 m | 26.0 | 3.2 | 64.3 | 15.9 | 2.1 | 109.3 | 45.5 | 7.5 | 159.1 | 16.3 | 2.4 | 228.4 |
| 400-1000 m | 9.8 | 0.7 | 20.1 | 4.9 | 0.1 | 35.6 | 12.9 | 1.3 | 38.6 | 9.2 | 1.6 | 62.1 |
| >= 1000 m | 0.0 | 0.0 | 0.1 | 0.0 | 0.0 | 0.1 | 2.6 | 0.4 | 7.5 | 1.3 | 0.2 | 11.7 |
| household (not a band) | 2.5 | 4.7 | 25.5 | 11.1 | 0.0 | 43.9 | 3.0 | 6.7 | 25.8 | 13.1 | 0.3 | 48.5 |
| **NPC total** | **55.8** | **10.6** | **135.0** | **34.0** | **2.9** | **235.4** | **75.8** | **14.0** | **259.7** | **32.0** | **5.0** | **381.5** |

(BP = s640c, 11 min before the crash below; s641c 12 min gives the same shape.) Beyond 150 m: SV 62 %, BP 79 % of NPC time.
Sim task (interactions, idle loops, script route calls) is 57-68 %, locomotion 20-24 %, autonomy 8-14 %, sim-side plan work
1-1.3 %. Plans themselves run on the route worker; their sim-thread mark is the `+0x918` lock waits (lock918 hitches).

Wall time (sim_health census, unseen NPCs off the active lot): SV 547 samples: idle 56 %, routing 30 %, Go to Lot held median
56 sim-min (p90 498), Visit <lot> 60-230 sim-min, Go Home 6 (p90 50); BP 304 samples: idle 27 %, routing 48 %, Go Home 18
(p90 53), Go to Lot 10 (p90 477). Long NPC routes (>= 60 m, `alibiwalklog`, 30 min): SV 5,182 (2,659 km), BP ~4,200-4,600 a
12 min window; **79-90 % of them have a car leg** (SV 4,085, BP 3,704 of 4,230), the rest are walks: SV 283 cuttable
(110 km), BP 352-397 cuttable in 12 min (125-161 km); 11 % (SV) / 2 % (BP) judged seen by camsafe.

## 5. Candidates ranked

| # | Opportunity | Size (measured) | Status |
|---|---|---|---|
| 1 | **Unseen NPC car trips** (VisitCommunityLot / GoHome / GoToLot driven): the trip shortcuts act only at the trip's start; a Sim seen then, or a replan on the way, drives the whole way | 79-90 % of long NPC routes; locomotion 56-76 ms/s overall | not built: needs a car-aware cut (park or dispose the car the way EA's arrival does, NpcDriverPool, carpool / taxi riders). Biggest lever left; design next |
| 2 | **Unseen NPC walks off the active lot cut to the tail** (`alibiwalk`) | SV ~280-330, BP ~800-1,000 per 30 min; 54-160 km a run | **built** (§2), A/B §6 |
| 3 | Sim task of far NPCs (idle loops, long interactions): the largest share (135-260 ms/s) | far-Sim T3 / dormant lots (docs/324, docs/630); DoLoop caps exist (docs/632-634) | design exists (docs/332 FS-*), large; not in scope here |
| 4 | Off-lot NPC autonomy less often | 32-34 ms/s total, ~20-25 beyond 150 m | **rejected**: `unseenpark` measured -1.3 / +0.7 / -1.7 % (docs/243 §10); not rebuilt |
| 5 | Long unseen plans skipped by placing at the destination | sim-side plans 2.9-5.0 ms/s; worker time and lock918 waits | partly covered (docs/202 cuts, trip shortcuts, alibiwalk removes the walk after the plan); a plan-time cut cannot tell a followed plan from a reachability test, so not built |
| 6 | Walk to a far community lot set at the lot edge | already done, bolder: `gotolot` / `gotocommunity` / `visit` place the Sim on the lot at the start (ON), `stagedarrival` | existing |

## 6. A/B and proof status

Runs (one game at a time, loop-wait + random settle, "Bin pair restored" in every arm):

| run | world | build | arm | storm min | long routes | cut / would | skipped | undone | camsafe late-seen (all verdicts) | late-seen after a cut | game hitches (s) | NPC ms/s |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| s640a | SV | log | off | 30 | 5,182 | would 283 | 110 km | - | 10 | - | 1 (0.4) | 235.4 |
| s640b | SV | v1 | on | 30 | 6,984 | 166 | 54 km | 92 | 17 | - | 3 (1.2) | 283.5 |
| s642b | SV | v2 | on | 30 | 5,014 | 50 | 8 km | 0 | 18 | 0 | 2 (0.9) | 240.7 |
| s640c | BP | log | off | 11 (crash) | 4,230 | would 397 | 161 km | - | - | - | 0 | 381.5 |
| s641c | BP | v1b | off | 12 (crash) | 4,632 | would 352 | 125 km | - | - | - | 0 | - |
| s640d | BP | v1b | on | 30 | 8,897 | 325 | 115 km | 211 | 1 | 0 | 1 (0.4) | 367.3 |
| s641d | BP | v1b | on | 13 (crash) | 3,462 | 168 | 59 km | 128 | - | 0 | 0 | 374.7 |

Builds: v1 = first build (tail point read by segment id: it was the route start, the sanity check undid ~40 %); v1b = v1 +
reason counters and the cut audit; v2 = tail point by segment index with a distance check (no undo left, but a long last
path is one segment, so most routes had no tail point: 728 refused); v3 (70f312fb, final) = v2 + the route end itself as
the spot when no tail point lies near it. **v3 is not run in game** (game-slot fairness: no more runs queued).

Readings:
- **Safety:** errors 0 in every arm; late-seen after a cut (camsafe's 3 s audit on each cut Sim) 0 of 543 cuts (s640d,
  s641d, s642b); the global late-seen count stays at camsafe's level (1-18). Undo replans never failed. No household or
  selected Sim was cut (scope by IsPlayerSim; the counters have no household path).
- **Cost:** no measurable change: SV NPC 235 -> 241 ms/s (v2, 50 cuts), BP 381 -> 367-375 (v1b); run-to-run noise is
  larger than the cut volume (at most ~2 % of long NPC routes; cars are the rest). Game hitches 0-3 per run on both arms,
  all lock918 / job-wait / script frames, none in alibiwalk.
- **Crash:** 3 BP arms (s640c off/log, s641c off/log, s641d on) ended at 12-15 min with the same fault on the main thread
  at `0xF6B?804F` (outside TS3W and ts3native), after first-chance AVs in an autshadow worker thread; the same fault
  address is in 14 phase logs since 2026-11-24, before this code, and hit the log-only arms too. Not attributed to
  alibiwalk [inferred]; s640d (on) ran 30 min. Open for the stability owner.
- Other agents' root causes seen: `Query_CountObjectsOnLot` sim-cpu frames (s640b, 2 hitches, 0.7 s), not touched here.

Proof status: **built, safe in game (v1b / v2), gain not shown**; v3 (end-spot fallback, the version for the PR) unrun.
Next: one SV + BP pair of v3 when the slot is free, then the car-trip cut (#1) if the walk share stays this small.
