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

## 7. Generated NPC boats block the unseen boat skip: `unseenboatgenboat` (default off, 2026-11-19; acts in game, see below)

**Finding [IP logs phase-20261119-0746 / -1139 / -1243 + decompiled]:** NPC boat trips that succeed cost far more than the
no-path failures: 429-521 logged boat plans of ~150-330 ms per ~55 min session (98-117 s of rider-task wait; the single route
worker is busy that long, so other Sims' plans queue behind it). Every boat trip is two plans by EA design (the rider's kind-0
plan with a boat leg, then `GetInBoatRouteAction` swaps the follower to the boat and replans the whole trip, kind 3). Most riders
are unseen. `unseenboat` should skip those trips, but refuses an NPC that owns a boat (`UnseenBoat.cs`, counter 844), and
`Sim.GetBoatForGetInBoatRouteAction` -> `GetNpcBoat` makes a boat for an NPC that has none (`GeneratedOwnableForNpc`), which
`BoatOwnable.Park` then puts into the driver's inventory instead of mooring it (BoatOwnable.cs:389-406). After its first boat
trip every NPC "owns" a boat, so unseenboat (and unseenboatland's "own boat must come along") refuse it for good [inferred:
the refusal itself is not yet seen in a log; check counter 844 vs 4640].

**Fix:** with `unseenboatgenboat` on, `UbOwnsBoat` does not count a boat that EA generated for an NPC
(`GeneratedOwnableForNpc`) and that sits in that NPC's own inventory, for a non-household, non-selected NPC of known scope.
Any other boat (bought, reserved, temporary, a household's) still counts. EA would put that boat back in the inventory after
the trip anyway, so skipping the trip loses nothing. Counters [4640] generated boats ignored, [4641] errors.

Counters moved: the combo branch had them at 4060-4061 (main uses those), here 4640-4641. **In game** (2026-11-19,
`phase-20261119-1837`, Isla Paradiso, about 20 min, `unseenboat` + `unseenboatstart` + `unseenboatgenboat` on, docs/248
§12.1): 84 generated boats let through (4640), and the "owned boat" refusals (844) were 3, against 82 in the run before the
switch existed. One arm only, not a same-load off/on pair.


## 8. The 2 s plan is EA's route-fail path; a seated rider never passes `failreplan`: `failreplanboat` (`FailReplanBoat.cs`, default off, 2026-11-21, NOT RUN)

Counters 4320-4327 (block 4320-4329, `kCounterSlots` 4330). Source: storm s347 (arm G of the combo branch's docs/670 §27.8, Isla Paradiso,
`ts3native-full.log`, play window), s309 for the baseline, decompiled C#, Ghidra decompile of `0x65BB90` / `0x6595B0`.
The full read of the run (the combo branch's docs/670 §28) is summed up at the end of this section.

**What the run shows [verified, log].** One NPC (driver of a `BoatPaddleBoat`, follower id constant) fails 41 of 43 route
jobs with no path in about a minute, from the same spot. 17 of its 24 routes have two jobs on the **same route handle**:
first 0-69 ms (mostly `nodes 0`), then at once a second one of 0.4-3.6 s. All 18 jobs of the run that took 1 s or more are these
second jobs (44.4 s of worker time with the routing lock held; 15 of the 18 are followed by this boat's `ufplan` line, the
other three have no managed line left in the minute's log budget). Their `route candidate split` line: `closestWps
0.6-2.1 s / 84-94 calls` (17 of the 18 have the line), in 9 of them also `world` 1.0-2.1 s.

**Why two plans [verified, decompiled].** `SimRoutingComponent.DoRoute` on a failed plan calls `DoRouteFailureBehavior`,
which calls `GetRouteFailPath` (up to twice): the same route object is planned again toward the blocker
(`PlanToPointRadialRange` 1-3 m around the blocker object, else `PlanToPointRadius` at the blocker's spot), so the Sim can
go there and play the route-fail animation. §2.6 already named it for the taxi. `failreplan` (default on) skips that path
for an unseen townie, but its test is `TownieUnseen`, which answers false for any Sim that does not stand: a rider seated in
a boat always gets EA's fail path, seen or not.

**Why that plan is slow from a boat [verified structure, decompile; cost split inferred].** The planner snaps each radial
goal to waypoints with `0x65BB90`: up to three tries of `0x6595B0`, each over a wider area (1-4, 9-16, 25-36 chunks of the
waypoint grid, the combo branch's docs/679 §1). Inside one try the candidates are sorted and then tested one by one with the routability test
`0x659390` until the wanted number pass. For a boat on open water no land waypoint passes, so every try tests **every**
candidate of its area and the call answers "none" after the third: about 22 ms a call, 84-94 calls a plan. The game's ten
cached areas (its §5.1) save the gather, not the tests, so `[WaypointSnap] Gather` (which also leaves water areas to
the game) would not change it. The planner has no deadline test in that loop; `[RouteAsync] CandidateCut=1` (the combo branch's docs/644 §3; not on main)
answers the snap "not found" once the plan is past its deadline, which bounds a cut plan at its deadline plus one call.

**Fix (ts3fixes only).** `RouteBackoffHook.FrbPost`, called from `UfPlan` after every plan: for a failed plan whose
follower is a `Boat` with a driver, when the driver is unseen by the rule native's `KindDeadlines` already uses for boats
(`UfWhy` = held: not household, not selected, known scope, not on an active lot, off screen and away from the viewers;
`UfVehicleNpcOnly`: held only by the boat, no household or selectable occupant), the route's `DoRouteFail` is set false.
That is the game's own "no route-fail show" flag: `DoRouteFailureBehavior` tests it first and returns. The plan's result,
the caller's failure branch and the `RouteFailed` exit reason are unchanged. The flag stays on that route object; if the
route cleared last is planned again and succeeds, the flag is set back (4327). Not changed: seen, household, selected and
unknown-scope riders, boats without a driver, routes whose flag is off already. The rule is `FailReplanBoatCore.Decide`.

Differs from EA only in: an unseen NPC boat does not drive toward the blocker of its failed route and its rider does not
turn or play the fail animation (which EA plays only for a Sim on screen). The rider stays where it is, as in EA when the
fail path finds no path (every one of the 18 in s347 did not). Nothing is moved or teleported.

Counters: [4320] failed plans with a Boat follower, [4321] kept (no driver, or the rule did not hold), [4322] would switch
the behaviour off (switch off), [4323] switched off, [4324] flag off already, [4325] errors, [4326] lines, [4327] flag set back. Agent `patches`
read-back `failreplanboat_*`. Log (10 a minute): `failreplanboat: <BoatType> <rider>, <result> in N ms, blocker <type>:
route-fail behaviour off (#n)` / `would be off (switch off, #n)` / `DoRouteFail off already ...` / `kept (<why>)`.
Self-check `node tools/selftest/failreplanboat/run.ts` (all 128 inputs of the rule: one acts, one would act).

**Not done, not known.**
- NOT RUN in the game. No forced repro: `ip-boat-nopath-memo` seats its riders next to the selected Sim (seen, so `kept`)
  and plans without `DoRoute`; it shows the counters 4320 / 4321 only.
- Whether a blocker was named in the failed plans of s347 is not in the log (`routefail:` lines are written for household
  Sims only); that `GetRouteFailPath` planned is inferred from the same-handle pair and the code. The off arm's `would be
  off` lines with a `blocker` field settle it.
- A **seen** NPC rider keeps EA's fail path, so the 2 s plan can still happen in view; `CandidateCut=1` bounds it for the
  deadlines of 200 / 400 ms, not for the uncut retry (`KdUncutCeilMs`, 10 of the 18).
- The rider stays stranded until EA's 3 AM stuck check, as before (§3). `boattaximemo` answers the repeat plans to a
  destination already proven; it and this switch are independent.

### 8.1 The storm read (combo docs/670 §28): s344-s355 read: the s347 outlier is a stranded NPC boat's route-fail plans, not arm G (2026-11-21, `fix/boatwp-1121`)

Arms: s344-s355 alternate baseline and G (its §27.8 arm E's keys without the RO clones). s347 (G): 29 `hitch:` frames in the
play window, 43.7 s, 18 `route async: long job` lines of 1 s or more. No other run of s310-s355 has one.

**The hypothesis tested** ("`ForeignKindsUnseen=2` sends the boat's plan to the worker, `KindDeadlines` cuts it late, the
cut returns as no-path and the interaction retries") **does not hold** [verified, logs]:

- The slow jobs are `foreign START, IC kind 0` (a Plan sent from the rider's task with the boat as follower). Kind 0 foreign
  jobs run on the worker without the G keys too: s309 has the same job three times (`closestWps 488/91`,
  `1288/92`, `509/91`, cut by `KindDeadlines`, another follower). `ForeignKindsUnseen=2` only adds kind 2.
- 10 of the 18 are **not cut** (`uncut: backoff (KdUncutCeilMs)`, deadline 30000 ms) and take 2.2-3.6 s anyway; 8 are cut
  (deadline 200 / 400 ms, `capAt` 1.5-1.6 s, tail 0.5 s). The cut is late, as its §27.3 said, but it is not what repeats.
- The boat's first failure is an uncut 26 ms plan (`VisitCommunityLot`, result 16). Every later plan of that follower
  fails as well, cut or not (41 of 43 jobs, the other two "no destination"): nothing here is a cut read as a failure.
- The repeats are pairs on one route handle: a 0-69 ms plan, then at once the slow one. That is EA's route-fail path
  (`DoRouteFailureBehavior` -> `GetRouteFailPath`: the same route planned again toward the blocker), §2.6 and
  §10. The rider stays seated, so each new route the storm or autonomy gives it (24 handles in a minute) fails and pays
  the pair again. In natural play §1-§2 measured the same pair about every 50 sim-minutes.
- What G changes is who waits: 10 frames are `lock918` (sim-thread writers at `0x834201` behind the worker's hold), 17
  `job-wait` (forced drains at `disc`); 13 of the 29 frames name one of the 18 jobs (29.9 s). Without G the same jobs
  would hold the lock as long.

**Why the closest-waypoint phase takes 2 s:** §8 (every candidate of up to 36 chunks gets the routability test,
none passes from open water, 84-94 snaps a plan). Not the gather, so not `[WaypointSnap]`.

**Answers.** `failreplanboat` (ts3fixes, §8, default off) removes the slow plan for unseen NPC riders.
`[RouteAsync] CandidateCut=1` (combo branch only, its docs/644; off in these arms) makes a cut take effect within one snap for the plans
that are cut. No ts3native change.

**Next storm.** Managed switch `failreplanboat` on top of arm G, and separately `RouteAsync.CandidateCut=1`. A stranded
boat is chance (1 run in 26), so read the counters first: `failreplanboat: ... would be off` lines in an off arm give the
number of plans the switch would have spared; on arm: no `route candidate split` line with `closestWps` over 500 ms for a
boat follower, `failreplanboat_off` > 0, no `ufplansplit ... src Boat... FailedBecauseNoPathToDestination` line of 1 s or
more with `scope unseen`.
