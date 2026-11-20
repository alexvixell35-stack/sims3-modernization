# 851. Boat no-path memo (`boattaximemo`, any Boat subclass)

2026-11-18. Source: Twinbrook run `phase-20261118-0557` (`ts3native-full.log`, `src BoatTaxi` lines), decompiled
Sims3GameplaySystems. Built, proven in game 2026-11-19 (forced repro, see the end), default on. Package r105-boattaxi
(taxi only), r106-boatmemo (every Boat subclass, §5). Counters 1264-1269.

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
   boat, NPC -> `GetNpcBoat`, which returns `Vehicle.CreateTaxiBoat()` for **every pet** (and tourists); unseen human
   townies take the same `GetNpcBoat`.
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

## 3. Fix: `boattaximemo` (`BoatTaxiMemo.cs`, default on)

Tunables `kBoatTaxiMemo` (true since the in-game pass), `kBoatTaxiMemoHours` (4 sim-hours). Hooked in `UfPlan` (every plan kind).

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

Counters: [1264] boat no-path failures recorded, [1265] rider routes planned with the boat off, [1266] of those
succeeded, [1267] stranded boat plans answered, [1268] ms of the recorded plans those answers stand for, [1269]
refused (household, selected, unknown) or errors. Log lines (10 a minute in all): `boattaximemo: recorded <BoatType>, <rider>, boat plan N ms ...`,
`boattaximemo: answered <BoatType> #n, <rider>, stands for N ms ...` and `boattaximemo: boat off <BoatType> #n, <rider>, ...`
(`#n` = running hits for that boat type, answered + boat off).
Agent: switch `boattaximemo`, counters `boattaximemo_*`.

Expected in the Twinbrook night: the first taxi failure is still paid (~0.4 s + one route-fail pair at most); later
pairs (~10 x 2.3 s) become answered plans, and the dog's next GoHome from land walks.

## 4. Test

Twinbrook overnight with the switch off then on (same save): `src BoatTaxi` lines with `ufplan` >= 1 s per night and
hitchwatch >= 1.5 s, plus `boattaximemo_answered` > 0 and no new household-Sim lines. Forced repro:
`tools/harness/repros.json` `ip-boat-nopath-memo` (agent `boat_nopath_repro`, results at the end).

## 5. All boats (r106-boatmemo, 2026-11-19)

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
`boattaximemo_*` (1267 answered, 1268 ms saved). Expected Twinbrook: one recorded line per stranded (rider, boat,
destination), then `answered BoatSailBoat #n` etc. for the repeats.

## 6. In-game result (forced repro, Isla Paradiso, 2026-11-19)

The Twinbrook sweep save has no active household, so every Sim there is of unknown scope and exempt: the Twinbrook A/B runs (s108-s114) could not show the memo and are void. Forced repro `ip-boat-nopath-memo` (agent `boat_nopath_repro`, two NPC Sims plus one NPC dog as BoatTaxi drivers, farthest lot, boat-only planning, 4 tries each), one session, switch off then on:

| Arm | Try 1 (Plan) | Tries 2-4 (Replan / ReplanFromPoint) | Counters |
|---|---|---|---|
| off | 207-247 ms, no path | 1115-2130 ms each, no path | all 0 |
| on | 274-278 ms, no path (recorded) | 0 ms each, answered | 1264 +4, 1267 +6, 1268 +1644, 1269 +0 |

The dog's route had a path (Succeeded every try) and was left alone; the active household (Collins) was untouched. BtmPost now logs why a boat's no-path plan is not recorded (`boattaximemo: skip <type>, blocker | no driver | exempt rider | no destination`).
