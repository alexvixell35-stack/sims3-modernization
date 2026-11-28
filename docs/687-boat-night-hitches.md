# 687. Night boat hitches on Isla Paradiso, and `unseenboatland`

2026-11-17, static read of run `phase-20261117-1940` (IP, r102-combo, `unseenboat` on) plus decompiled scripts. Fix built, default off; ran ON in one IP night run (§5), no off/on verdict. Counters [4642-4650] (`fixes-4642`). Builds on docs/247 §11-13 and docs/248.

## 1. What the run shows [measured]

`hitch-attr.ts`: 11 game hitches of 307-420 ms, 9 of them Sun 10:06-11:25 PM. 407 `ufplan: kind 3` lines; the boat source of the slow ones: speedboat 105, speed fishing boat 103, sail 75, water scooter 73, paddle 13, taxi 16. Top Sims: Tariq, Linda and Chandell Scott (a houseboat household), Ariel Walsh, Roberta Ruiz, Andrei Varadi. Each is `ufplansplit: ... pre 0 wait ~340 (1-4 polls)`: the plan ran on the worker (`route async: long job ~320 ms, A10 foreign START, IC kind 3`) while the Sim's task slept. The frame hitch is the sim thread waiting for that job: `route jobs: sim waited 306 ms for a job at disc` (4 of the 5 such lines), with `P5c park/final release` of a destroyed object while the job ran.

## 2. Cause [verified decompiled unless tagged]

1. **Vehicle choice is native, per plan.** `SimRoutingComponent.SetDefaultRouteOptions` turns on `EnablePlanningAsCar` and (EP10) `EnablePlanningAsBoat` + `EnableWaterPlanning` for every human child or older. No script picks boat over car: the planner's multi-mode search takes the cheapest legs. `Route.SetMinimumDistanceForBoatTravel` exists but no script calls it (native default).
2. **The boat is made per trip for townies.** `GetInBoatRouteAction` -> `Sim.GetBoatForGetInBoatRouteAction`: owned boat, else `GetNpcBoat` (a weighted random speed / sail / fishing / scooter / paddle boat, `GeneratedOwnableForNpc`), else `Vehicle.CreateTaxiBoat`. That is why the log shows private boat kinds for townies. The boat is created, placed, parked and purged at the end (docs/257): footprint add and remove, which queue discourage records.
3. **Kind 3 = the get-in replan.** `GetInBoatRouteAction` sets `Follower = boat.Proxy`, `DoRoute` calls `ReplanFromPoint` (ReplanFromPosition, IC kind 3) with the boat's profile: the whole remaining route again, water leg on the 4 m water grid (`WaterPath_Plan 0x671930`, docs/100). Mean ~90 ms, long crossings 300-460 ms (docs/247 §11.3, §12.3).
4. **"Foreign START" is our async path, not a miss.** The follower is the boat, not the task's Sim, so ts3fixes sends the foreign kind-3 START and ts3native serves it as an A10 foreign job (`ForeignKinds=4`, `KdA10ForeignMs=1000`, docs/176). The plan is off the sim thread; the hitch comes back through `DiscDrain(force)` (`WaitAsyncDone(SW_DISC)` then `+0x918`, docs/247 §13.4): a forced drain at a native call site (`FadeOut < GetInBoatRouteAction`, `PreRoute`, `GetNextEvent`) waits out the whole running job.
5. **Why at night [inferred].** 10-11:30 PM is when townies leave venues and go home; on IP many homes are houseboats or across water, so many kind-3 jobs overlap with boat create / purge footprint churn. Bridgeport has no water trips, hence none there.
6. **Why `unseenboat` did not take them [inferred, no split in this run].** Earlier splits (docs/247 §12.1, §12.10): owned boat, goal indoors / raised, resort or dive lots, callers, and `goal invalid or < 40 m` (28%). EA choosing a boat inside one land component was <= 1.2 s per run (I-482-1).

## 3. Fix `unseenboatland` (default off, `UnseenBoatLand.cs`)

Rodney's rule: an unseen townie takes a boat only when the boat trip is much shorter than the land trip. In `PlanBackoff`, after `UbTrip`, for a Route.Plan with `EnablePlanningAsBoat` on, not begun or ending as car / boat, trip >= 40 m, own task, not nested:

- Refused (EA plan as is): no household, `HouseholdExempt` (household, selected, unknown scope), seen (`UfWhy`, `TownieUnseen`), water posture or a water interaction (`UbCore.Denied`), an owned usable boat at the current lot (it must come along). Ends not in one land component (`UfSameComponent` != 1): water is needed, or unknown, and a land plan would fail after a full search.
- Otherwise plan with the boat off. Keep it when it succeeds with no boat, ocean or wade leg and `straight >= kUnseenBoatLandRatio x land length` (0.5): a boat route is never shorter than the straight line, so it cannot be under half the road. Else the option goes back on, backoff's memo for that goal is dropped, and EA's plan runs (the land plan ms are counted as wasted).
- Realistic: the Sim drives or walks a real route; with the boat not chosen, EA's multi-mode plan gives the same route [inferred].

Counters: 4642 tried, 4643 land kept, 4644 boat may be shorter, 4645 land failed or swims, 4646 not one land component, 4647 refused Sim, 4648 land plan ms, 4649 wasted ms, 4650 errors. Log: `unseenboatland: land, <Sim>, straight N m, land N m, ratio 0.NN, land plan N ms` (20 a minute).

**Expected reach [inferred].** By I-482-1 the one-land-area class is small, so this switch alone likely removes few of the night hitches; its counters (4646 vs 4642) measure that. Better levers by the cause above: (a) docs/247 §13.4 J1 (unseen STARTs park instead of a forced drain wait), which addresses the 300 ms sim wait itself; (b) a split of the `unseenboat` refusals in one IP night run with `boatreplanlog` on, then widen the biggest refusal for generated NPC boats (`rj_owned` cannot apply to them: `GetNpcBoat` boats are not owned).

## 4. Test

Package `ts3fixes.r103-boat.package`. IP, Sunday 9 PM to midnight, `unseenboatland` off then on. Check `unseenboatland_*` counters, the `unseenboatland:` lines, `ufplan: kind 3 ... src Boat` count and `hitch-attr.ts` hitches >= 300 ms between 10 and 11:30 PM; no new stuck Sims.

## 5. Run (2026-11-17, Isla Paradiso, night, `phase-20261117-2211`, run s99, `unseenboatland` on; the off reference is `phase-20261117-1940`)

34 `unseenboatland: land, ...` lines: townies whose trip stayed in one land component drove or walked instead of a boat,
for example straight 464 m against a land route of 767 m (ratio 0.61, land plan 98 ms), 437 / 771 m, 431 / 645 m. The
night hitches did not drop: 9 `route jobs: sim waited 303-382 ms for a job at disc` lines in this run against 5 in the off
reference (different nights, not an A/B; the cause of those is the forced drain wait of §2.4, not the boat choice). So:
it acts, as the expected reach of §3 said, on few trips; **not proven**, no same-load off/on pair.
