# 642. BoatTaxi no-path memo (`boattaximemo`)

2026-11-18. Source: Twinbrook run `phase-20261118-0557` (`ts3native-full.log`, `src BoatTaxi` lines), decompiled
Sims3GameplaySystems. Built, default off, NOT RUN. Package r105-boattaxi. Counters 3864-3869.

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

Counters: [3864] taxi no-path failures recorded, [3865] rider routes planned with the boat off, [3866] of those
succeeded, [3867] stranded taxi plans answered, [3868] ms of the recorded plans those answers stand for, [3869]
refused (household, selected, unknown) or errors. Log line `boattaximemo: recorded ...` (10 a minute).
Agent: switch `boattaximemo`, counters `boattaximemo_*`.

Expected in the Twinbrook night: the first taxi failure is still paid (~0.4 s + one route-fail pair at most); later
pairs (~10 x 2.3 s) become answered plans, and the dog's next GoHome from land walks.

## 4. Test

Twinbrook overnight with the switch off then on (same save): `src BoatTaxi` lines with `ufplan` >= 1 s per night and
hitchwatch >= 1.5 s, plus `boattaximemo_answered` > 0 and no new household-Sim lines. Repro entry still to add
(`tools/harness/repros.json`): seat an NPC dog with a home across Twinbrook water and push GoHome.
