# 642. Boat no-path memo (`boattaximemo`, any Boat subclass)

2026-11-18. Source: Twinbrook run `phase-20261118-0557` (`ts3native-full.log`, `src BoatTaxi` lines), decompiled
Sims3GameplaySystems. Built, default off, NOT RUN. Package r105-boattaxi (taxi only), r106-boatmemo (every Boat
subclass, §4). Counters 3864-3869.

## 1. What the log shows [verified, log]

- One NPC dog ("Rufus Dog") plans as a pet at tick 43816: `src pet`, Succeeded, dist 1002 m.
- 640 ticks later the same route replans with a BoatTaxi as follower and the dog as its driver: `kind 3`
  (ReplanFromPoint), 381 ms, FailedBecauseNoPathToDestination; `routefail job` names `GoHome target Lot`.
- From then until ~3 AM, the same BoatTaxi object (follower `14BC00F5765BB880`, one id all night) plans 22 times,
  all failing with no path. After the first GoHome attempts the plans come in pairs on the **same route handle**:
  ~270 ms, then 10-80 ticks later ~2000-2200 ms (`route phases`: candidate 1662 ms of 2041). The 2 s one is an A10
  foreign START job; the sim thread waits for it on the route lock (`sim waited 2003 ms on lock`), so other Sims'
  plans queue behind it: a ~2 s freeze (hitchwatch 1.7-2.0 s at 9:08 PM, 9:56, 10:49, 12:02 AM, 3:00 AM...).
- The pair repeats about every 50 sim-minutes. `routefail job` shows `none`: the dog has no CurrentInteraction at the
  plan.
- 3 AM stuck check (`src stuckcheck Rufus Dog`, ticks 69099 and 72429) fails as well; after a further round the dog
  plans as a pet again (tick 77629, Succeeded): the stuck checker's reset took it out of the boat [inferred: LotManager
  RunStuckCheck resets after round 3].
- Isla Paradiso BoatTaxi plans are short and succeed: the failure is this destination, not taxis in general.

## 2. Code path [verified, decompiled unless marked]

1. `SimRoutingComponent` route setup (EP10): `EnablePlanningAsBoat = boat != null || ChildOrAbove` (with posture
   exceptions). Adult pets count as ChildOrAbove, so a non-stray NPC dog may plan a water leg. Strays, wild animals
   and unicorns are turned off explicitly (`EnablePlanningAsBoat` false, `DestinationMustBeOnLand`).
2. Following the route, `GetInBoatRouteAction.PerformAction` -> `Sim.GetBoatForGetInBoatRouteAction` -> no owned
   boat, NPC -> `GetNpcBoat`, which returns `Vehicle.CreateTaxiBoat()` for **every pet** (and tourists). docs/639 covers
   the same `GetNpcBoat` for unseen human townies.
3. The action places the taxi at the boat leg's start, seats the rider with `PutInDriver`, sets `Follower = boat`,
   `BeginAsBoat`, and returns `ContinueAndPopPathAndReplan`.
4. `RoutingComponent` route loop: `ReplanFromPoint` from the boat position (the `kind 3` line). It fails ->
   `ReplanFailure`, route ends. Nothing on that path calls `GetOut`: `BoatTaxi.OnRouteFinished` (the only `GetOut`)
   belongs to the boat's own routes. The dog stays seated in the taxi [inferred from the code + the one follower id
   all night].
5. Every later route of the dog: `SimRoutingComponent` sees `OwnerSim.Parent is IBoat` and sets `Follower = boat`,
   `EnablePlanningAsBoat`, `BeginAsBoat`. Same spot, same destination -> same no-path result (the ~270 ms plans).
6. `SimRoutingComponent.DoRoute` on a failed plan -> `DoRouteFailureBehavior` (throttled by
   `CheckPlayRouteFailFrequency`) -> `GetRouteFailPath` re-plans **the same route object** toward the blocker
   (`PlanToPointRadialRange` 1-3 m near the blocker object, or `PlanToPointRadius` at the blocker location). That is
   the second plan of the pair: same route handle, heavy candidate phase (radial goals), ~2 s, also no path.
7. Hourly cadence: the dog's own task with no CurrentInteraction tries again (autonomy picks) [inferred; the exact
   caller is not traced].

Why the taxi cannot get there [inferred, native planner not traced]: the dog's own plan (dog species, boat leg
allowed) finds a water leg plus land legs; the replan with the taxi as follower (boat species and footprint, start as
boat) finds no water path to a get-out point for that destination. Twinbrook's narrow waterways vs IP's open water
fit this; nothing in managed code decides it.

## 3. Fix: `boattaximemo` (`BoatTaxiMemo.cs`, default off)

Tunables `kBoatTaxiMemo` (false), `kBoatTaxiMemoHours` (4 sim-hours). Hooked in `UfPlan` (every plan kind).

- **Record** (after a plan): follower is a `BoatTaxi`, its driver is set, every occupant passes `IsPlayerSim` = false,
  result FailedBecauseNoPathToDestination not blocked by a Sim or vehicle -> memo key (rider, destination lot id, or
  4 m cell outside lots) with the taxi's position and the plan's ms, for `kBoatTaxiMemoHours`.
- **Rider on land** (before a plan): follower is the rider Sim (not in a boat), `EnablePlanningAsBoat` on, not
  `EndAsBoat` / deep-water goal, live memo for (rider, destination) -> `EnablePlanningAsBoat` off for this route.
  The planner gives a walk/car route or EA's ordinary failure; the rider never boards a taxi that already failed
  there. This is the stray rule EA already applies to strays and wild animals.
- **Stranded taxi** (before a plan): follower is the taxi, live memo for (driver, destination), taxi within 0.5 m of
  the recorded spot -> the plan returns FailedBecauseNoPathToDestination without searching (FailFast, no blocker).
  With no blocker `GetRouteFailPath` returns null, so the 2 s route-fail replan is skipped too.
- Exempt: any occupant household, selected or of unknown scope (`IsPlayerSim`; never lifted by HouseholdToo since the
  answer is a skip). Successes are never memoed; an answered plan does not extend its entry, so after the hours a real
  plan proves the failure again. Clock going back (another save) drops an entry; 128 entries max (expired pruned,
  then cleared).
- Differs from vanilla only for: (a) a memoed NPC rider's route to the memoed destination (no boat leg), (b) a
  stranded NPC taxi's repeat plan from the same spot (answered failure, no route-fail replan). Not fixed: the rider
  stays stranded until EA's 3 AM stuck check resets it, as in vanilla (taking it out of the taxi is a possible follow-up).

Counters: [3864] boat no-path failures recorded, [3865] rider routes planned with the boat off, [3866] of those
succeeded, [3867] stranded boat plans answered, [3868] ms of the recorded plans those answers stand for, [3869]
refused (household, selected, unknown) or errors. Log lines (10 a minute in all): `boattaximemo: recorded <BoatType>, <rider>, boat plan N ms ...`,
`boattaximemo: answered <BoatType> #n, <rider>, stands for N ms ...` and `boattaximemo: boat off <BoatType> #n, <rider>, ...`
(`#n` = running hits for that boat type, answered + boat off).
Agent: switch `boattaximemo`, counters `boattaximemo_*`.

Expected in the Twinbrook night: the first taxi failure is still paid (~0.4 s + one route-fail pair at most); later
pairs (~10 x 2.3 s) become answered plans, and the dog's next GoHome from land walks.

## 4. Test

Twinbrook overnight with the switch off then on (same save): `src BoatTaxi` lines with `ufplan` >= 1 s per night and
hitchwatch >= 1.5 s, plus `boattaximemo_answered` > 0 and no new household-Sim lines. Repro entry still to add
(`tools/harness/repros.json`): seat an NPC dog with a home across Twinbrook water and push GoHome.

## 4. All boats (r106-boatmemo, 2026-11-19)

Next Twinbrook run `phase-20261119-0056` (`ufplansplit ... src Boat`): 39 plans, all kind 0 and
FailedBecauseNoPathToDestination, 200-760 ms each, 0 successes: BoatSailBoat x24 (one rider), BoatSpeedBoat x11 (one
rider), BoatSpeedFishingBoat x4 (one rider). The previous run had BoatTaxi x22. Isla Paradiso (`phase-20261118-2356`):
boat routes almost all succeed. Same shape as §1: a seated rider replanning from the same spot (kind 0 = the
`Parent is IBoat` route of §2.5).

How a boat gets into a route [verified, decompiled]: only `GetInBoatRouteAction`, which runs on a route planned with
`EnablePlanningAsBoat`. It calls `Sim.GetBoatForGetInBoatRouteAction`: `GetOwnedAndUsableBoat` (an owned boat or water
scooter, also for household Sims) -> for an NPC `GetNpcBoat` (BoatTaxi for pets and tourists; else a generated
`BoatOwnable`: water scooter for paper delivery, speed boat for mermaids and services, weighted pick otherwise) -> else
`Vehicle.CreateTaxiBoat`. Every kind is seated with `PutInDriver` (or passenger) and becomes the route's follower.
So turning `EnablePlanningAsBoat` off on the rider's route (§3) already keeps every boat kind away.

Change: record and answer now take any `Boat` follower (was `BoatTaxi`), same rules: NPC driver, every occupant passes
`IsPlayerSim` = false, failures only, 0.5 m start match. The entry keeps the boat's class name for the log lines above.
Check in a run: `grep "boattaximemo:" ts3native-full.log` (recorded / answered / boat off per type) and counters
`boattaximemo_*` (3867 answered, 3868 ms saved). Expected Twinbrook: one recorded line per stranded (rider, boat,
destination), then `answered BoatSailBoat #n` etc. for the repeats.


## In-game result (forced repro, Isla Paradiso, 2026-11-19)

The Twinbrook sweep save has no active household, so every Sim there is of unknown scope and exempt: the Twinbrook A/B runs (s108-s114) could not show the memo and are void. Forced repro `ip-boat-nopath-memo` (agent `boat_nopath_repro`, two NPC Sims plus one NPC dog as BoatTaxi drivers, farthest lot, boat-only planning, 4 tries each), one session, switch off then on:

| Arm | Try 1 (Plan) | Tries 2-4 (Replan / ReplanFromPoint) | Counters |
|---|---|---|---|
| off | 207-247 ms, no path | 1115-2130 ms each, no path | all 0 |
| on | 274-278 ms, no path (recorded) | 0 ms each, answered | 3864 +4, 3867 +6, 3868 +1644, 3869 +0 |

The dog's route had a path (Succeeded every try) and was left alone; the active household (Collins) was untouched. BtmPost now logs why a boat's no-path plan is not recorded (`boattaximemo: skip <type>, blocker | no driver | exempt rider | no destination`).

## 6. Houseboat boat-leg memo: `houseboatlegmemo` (`HouseboatLegMemo.cs`, default off, 2026-11-19, NOT RUN)

Log: Isla Paradiso phase-20261119-0746 (combo HEAD, TaskServe/PlayerFirst/RouteJobPhases/DoorMemo/KdCutCamRecheck=2). The
largest repeat hitch pattern (40 plans >= 300 ms): `ufplansplit: kind 2 ~600-680 ms, sync (START refused), why 0, result
FailedBecauseNoPathToDestination, dist 17 (sim 0 m away), <NPC> on ,` nested in a kind 3 (dist ~1194) or kind 0 plan of the
same Sim, with `route deadline: sync plan ... failed, ... past its deadline, entry Route_PlanLocked (no IC); world A* capped 0,
cell skipped 0, water skipped 0`. Britta Colombo 18x, Rubisel Ichtaca 6x, Linda Scott, Akoni Kahale (dist 22): NPCs whose home is
a houseboat (Fantasea), stranded ~17 m off it.

- **Caller [verified, decompiled]:** `SimRoutingComponent.PostPlanRouteCallback` -> `CheckAndUpdateForHouseboats` ->
  `EnterHouseboatThroughBoat` (router farther than `kBoatToHouseboatMaxRadius` 15 m from `RoutingSlot_0`): a new route,
  `EnablePlanningAsBoat`, `EndAsBoat`, `DestinationMustBeInDeepWater` (+ `DisablePlanningOnLand` in the same water body), a
  5-15 m `RadialRangeDestination`, `PlanFromPoint(startPos)`; on failure it returns false and the outer route stays as planned.
  It runs after every plan of that route (PostPlan sits in ScriptCore `Route.Plan*` after the IC), so each try home repeats it.
- **Why sync [verified, code]:** own-task kind 2 with `[RouteAsync] Kinds=0`: `A10StartBody` refuses NoStart (ts3native.cpp
  ~5620, `kRf3KindsOff`); only the watchdog-tagged kind 2 passes (`WatchdogKind2`). With WatchdogKind2=1 and no Kinds bit 2,
  `ArPlanFromPointHook` sends every other PlanFromPoint straight to the game IC (`ArWdPass`, a10.cpp:32), so no `SyncIc`: the
  UnseenFallback MARK is never taken, no unseen session, no `UfLockGate`. **Correction (2026-11-19):** the plan is not
  deadline-free. Its Route_PlanLocked goes through `CallPlan -> CallPlanBudget -> PdPlanBegin` and gets PlanDeadlineMs' own
  300 ms session (entry "Route_PlanLocked (no IC)", the `route deadline: sync plan` lines below) [verified, code + log].
- **Why no cut [code + inferred]:** only PlanDeadlineMs' generic session at Route_PlanLocked (`PdPlanBegin`, entry "no IC") is
  open. Its exits are entry checks (cell A* entry, `WaterPath_Plan` 0x671930 entry) and the world A* cost stubs; a pure water
  JPS that started before the deadline runs to its end. All counts 0 = the time is inside one water leg that exhausts the
  reachable grid (no-path boat plan; inferred from the options and the steady ~600 ms, not phase-split: SyncPlans=0).
- **No memo catches it:** UfPlanBody's no-retry window records only cut plans; `boattaximemo` skips Sim followers with
  `EndAsBoat`/`DestinationMustBeInDeepWater` and records only Boat followers; FailMemo (off) and DoorMemo are Plan IC / door only.

Fix (ts3fixes only): in `UfPlan`, a kind-2 plan with all three leg options from a non-exempt router (Sim, or a Boat whose
occupants all pass `Fixes.HouseholdExempt`; none aboard = unknown = exempt) is looked up by (router, start 0.5 m, goal 0.5 m).
A hit answers `FailFast(FailedBecauseNoPathToDestination)` (EA reads only `Succeeded()`); a plan that ran, was not cut and found
no path without a Sim/vehicle blocker is recorded for `kHouseboatLegMemoHours` (2) sim-hours. Successes never memoed, answers
never extend. Counters 3870 recorded, 3871 answered, 3872 ms answered stand for, 3873 exempt/errors; log `houseboatlegmemo:
recorded|answered`. Self-check `node tools/selftest/houseboatlegmemo/run.ts`. Expected in the same IP run with it on: each
repeat of those kind 2 legs ~0 ms (the outer plan keeps its own cost), ~20-25 hitches of 0.6 s gone.

Native follow-up below every caller (boat riders, houseboat legs, watchdog plans): docs/643 `[RouteAsync] NoPathMemo`.

## 7. Houseboat gangway rescue: `houseboatgangwayrescue` (`HouseboatGangwayRescue.cs`, default off, 2026-11-19, NOT RUN)

Contributor slots 4040-4049 (the contributor block 4000-4089; 4000-4039 and 4050-4089 are left to the native NoPathMemo work
taking slots in the same block). Builds on §6: `houseboatlegmemo` since e681da64 also recognizes EnterHouseboatThroughGangway's
leg (Sim follower, water and boat planning off, inside `CheckAndUpdateForHouseboats`).

**Root cause [verified, decompiled + IP logs s122/s123]:** an NPC rides a boat home to its DOCKED houseboat and the boat drops
it on shore ~15-22 m from the gangway, at a spot with no walkable path to it. After every plan of its route home,
`SimRoutingComponent.CheckAndUpdateForHouseboats` calls `EnterHouseboatThroughGangway`: when start and houseboat are connected by
land it turns water and boat planning off on a new route and runs `PlanToPointFromPoint(start, gangway RoutingSlot_0)`, which
fails after ~650 ms; the outer route is left as it was. `GoHome.Run` then tries `PlanRouteIntoLot`, `PlanToLot`, `PlanToLot` with
water (three `DoRouteWithFollowers`, each with EA's replans) and returns false; autonomy picks GoHome again. Nothing gives up.
EA's own way onto a houseboat for a Sim with no boat is `Houseboat.PlaceSimAtEntrance(sim)` (`RouteOntoHouseboat.Run`, the
gangway portal's no-boat branch; also used by `Boat.GetOut*` and the pet adoption social worker): `FindGoodLocationNearby` around
the jig's `RoutingSlot_2`, `SetPosition` / `SetForward`; `RouteOntoHouseboat` then turns `EnableWaterPlanning`,
`EnablePlanningAsBoat`, `DestinationMustBeInDeepWater` and `EndAsBoat` off on the Sim's current route.

**Fix (ts3fixes only, default off):**
- Mark: `HlmPost` (a gangway leg that ran, was not cut and found no path without a Sim/vehicle blocker) or `HlmPre` (an answered
  one) notes the Sim (`HouseboatGangwayRescueCore.Note`): its home houseboat (`LotHome` is a houseboat lot) must be Docked; the
  mark sums the legs' ms and count. Works with `houseboatlegmemo` off too: then UfPlan only watches the leg (no record, no answer).
- Rescue, never inside the routing callback: in `GoHomeHook` right after EA's `GoHome.Run` returned false, in the Sim's own task.
  Only when GoHome's target is that houseboat's lot, it is still Docked (gangway and jig present), the Sim is not on that lot, is
  within 40 m of the gangway, still not exempt, standing, not riding / carried / carrying / parented (`UfHeld`, as the unseen
  teleport), not in a tomb or hidden room, not cancelled (`Canceled`, `HigherPriorityNext`, `UserCanceled`), and its running
  interaction is this GoHome. Then EA's `PlaceSimAtEntrance(sim)` and `RouteOntoHouseboat`'s route-option cleanup. GoHome's own
  result (false, NumFail already counted by EA) is returned unchanged; the Sim is home, so autonomy has no reason to pick it again.
- At most one rescue per Sim per sim-hour (a mark is not taken inside the hour; a mark older than an hour is dropped).
- Household rule: `Fixes.HouseholdExempt` and `IsPlayerSim` (a teleport: HouseholdToo never lifts it, docs/271); unknown scope =
  household. Exempt routers are never marked (HlmRouter returns 0 for them), and the check runs again at rescue time.
- Counters 4040 marked, 4041 rescued, 4042 refused, 4043 errors; refusal reasons 4044 exempt, 4045 no / another / undocked
  houseboat, 4046 once-per-hour rule, 4047 busy, 4048 on the lot / far / stale mark, 4049 PlaceSimAtEntrance found no spot. Agent
  `patches` read-back: `houseboatgangwayrescue_*`.
- Self-check `node tools/selftest/houseboatgangwayrescue/run.ts` (marking, sums, once per hour, clock going back, exemption at mark
  and rescue time, refusals that drop or keep the mark, bounded table).

**Log lines:** `houseboatgangwayrescue: marked <name>, houseboat <lot>, gangway leg <ms> ms no path`, then
`houseboatgangwayrescue: <name> placed at <houseboat lot>, after <N> ms of failed legs (<k> legs, from x,z, lot now <lot>)`, or
`houseboatgangwayrescue: refused, <name>: <reason> (<detail>)`. Each capped at 10 lines a minute; counters are the measure.

**Proof (repro `ip-houseboat-gangway-rescue`, Baseline-IP, off arm then on arm, <= 3 min each):**
`houseboat_leg_repro {start, mode: "docked"}` (the default; `mode: "undock"` keeps §6's boat-leg repro). First version
(phase-20261119-1523, combo a5876c43): standing the NPC on shore 15-22 m off the gangway did NOT reproduce: every spot planned to
the gangway (Succeeded, 6-90 ms) and both NPCs walked home. In natural runs the stranding spot is where EA's boat trip home drops
the rider (the gangway leg follows a successful kind-3 boat replan). So the docked mode now runs that flow: each NPC owner of a
Docked houseboat goes by EA's Teleport Me Here to the nearest world-lot coastal land spot (80-480 m) not connected by land to the
houseboat, then EA's `Sim.MakeSimGoHome(sim, true)` (its own boat when usable there, else GoHome's boat planning). The result
watches `waitS` (150) s: the boat, the get-out spot and its distance to the gangway, the first gangway leg (boat and water
planning off inside `CheckAndUpdateForHouseboats`: result, ms, Sim position), whether the rescue fired, the lot reached.
Second version (phase-20261119-1608, combo f0f8943a): every NPC was home in 0.2-5 s with no boat and no get-out: unseen, the
unseen shortcuts (GoHome shortcut, unseenboat, unseen-fallback teleports) placed them [inferred from the timing]. The run is now
SEEN: the camera follows the first Sim (`EnableObjectFollow`) from before `MakeSimGoHome`, and `shortcutsOff` (default true)
forces `gohome`, `unseenboat`, `unseenboatstart`, `unseenboatland`, `unseenfbteleport`, `unseenfbpre`, `unseenfbtrip`,
`unseenfbarrive` and `unseenroute` off through `ts3fixes.switches` for the run, restored at the end or on stop. The docked result
field `start_needs_boat` (was `probe_failed` in v2) is true when the start is not connected by land, which is the wanted case. Expected:
OFF: a get-out ~15-22 m off, the first gangway leg FailedBecauseNoPathToDestination ~650 ms, `reached_home` false, 4040-4049 +0.
ON: 4040 +1 per Sim, 4041 +1 after its first failed GoHome, the `placed at` line, `reached_home` true; no new exceptions. A trip
whose get-out spot walks to the gangway is not triggered on that save: N/A, not FAIL. For natural runs the `houseboatlegmemo`
`recorded` / `skip` lines and the `houseboatgangwayrescue: marked` line of a gangway leg now carry the Sim position, the plan
start and the gangway slot (`sim x,z, gangway x,z (d m, status)`), so each stranding spot is logged exactly.

## 8. Generated NPC boats block the unseen boat skip: `unseenboatgenboat` (default off, 2026-11-19, NOT RUN)

**Finding [IP logs phase-20261119-0746 / -1139 / -1243 + decompiled]:** NPC boat trips that succeed cost far more than the
no-path failures: 429-521 logged boat plans of ~150-330 ms per ~55 min session (98-117 s of rider-task wait; the single route
worker is busy that long, so other Sims' plans queue behind it). Every boat trip is two plans by EA design (the rider's kind-0
plan with a boat leg, then `GetInBoatRouteAction` swaps the follower to the boat and replans the whole trip, kind 3). Most riders
are unseen. `unseenboat` should skip those trips, but refuses an NPC that owns a boat (`UnseenBoat.cs`, counter 844), and
`Sim.GetBoatForGetInBoatRouteAction` -> `GetNpcBoat` makes a boat for an NPC that has none (`GeneratedOwnableForNpc`), which
`BoatOwnable.Park` then puts into the driver's inventory instead of mooring it (BoatOwnable.cs:389-406). After its first boat
trip every NPC "owns" a boat, so unseenboat (and unseenboatland's "own boat must come along") refuse it for good [inferred:
the refusal itself is not yet seen in a log; check counter 844 vs 4060].

**Fix:** with `unseenboatgenboat` on, `UbOwnsBoat` does not count a boat that EA generated for an NPC
(`GeneratedOwnableForNpc`) and that sits in that NPC's own inventory, for a non-household, non-selected NPC of known scope.
Any other boat (bought, reserved, temporary, a household's) still counts. EA would put that boat back in the inventory after
the trip anyway, so skipping the trip loses nothing. Counters [4060] generated boats ignored, [4061] errors.

