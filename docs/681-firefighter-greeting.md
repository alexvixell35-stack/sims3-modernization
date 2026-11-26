# 681: Firefighter locked out of the emergency lot (`firegreet`)

2026-11-20 (UTC). Player report: "the lot that has the fire doesn't open so i cant go inside to put out fire or rescue sims /
i had to click on the door to visit the household". A second report from the same emergency: the firefighter carried a
crying toddler outside the main door and kept trying to bring the child back in.

## 1. EA's greeting rules [verified, decompiled]

- A household's greeting is one saved list of lots (`Household.mGreetedLots`; `AddGreetedLotToHousehold`,
  `IsGreetedOnLot`). One greeted family Sim greets the whole family on that lot. It survives save and load.
- `Sim.UpdateVisibleLotsForActiveHousehold` / `UngreetLotIfNoOneIsInside` (via `RemoveGreetedLotForSim`) drop the lot from
  the list as soon as no Sim of the household is left inside it. This applies to every residential lot, not only apartments
  (`GreetSimOnLot` only adds a HUD refresh for apartments).
- The firefighter is greeted for an emergency in three places only: the Respond to Emergency interaction (`Target`),
  `FireFightingJob` setup for large fires and earthquakes (`Sim.ActiveActor`, the selected Sim), and
  `FindIsolatedFirePosition` (small fires, when a fire is placed).

So the doors lock when the firefighter arrives by another path (walks or drives there, a save loaded mid-emergency), or
steps out once (a child carried out, a hose or a back door) while no other family Sim is inside: the greeting is gone for
the rest of the emergency. "Visit household" works because it greets again.

## 2. Is it ours?

Not found to be. The three door caches cannot hold a door shut after a greeting: DoorShare and DoorMemoX store only
"unlocked" answers (a "locked" answer is never kept), DoorMemoX does not serve without `doorgen` armed and never for household
Sims, and `[RouteAsync] DoorMemo` lives inside one route plan. `doorgen` also bumps the door generation on every greet and
ungreet. `fireactivejob` and `firetrapcount` do not touch greetings. Not ruled out: other mods in the player's set.

## 3. Fix (`firegreet`, ts3fixes `FireGreet.cs`, default off)

Once a sim-minute (a NeverPersisted alarm made at world load; the switch is read on each tick), while
`ActiveFireFighter.sActiveJob` is set up (`HasBeenSetup`): its firefighter (`Occupation.OwnerDescription.CreatedSim`) is
greeted with EA's own `Sim.GreetSimOnLot(lot)` when in the world, not greeted, on the job lot or within 8 m of its edge,
and the lot is not their home. Nothing happens outside an emergency. EA bug fix for a household Sim (CLAUDE.md: EA bug fixes
stay); the result is the one Respond to Emergency gives.

Counters: [1494] greetings given, [1495] errors. Repro `fire-greet-lockout` (`FireGreetCheck run`: the decision for the
reported case with controls; off BROKEN, on FIXED; prints the live emergency when one is on).

Not covered: why the firefighter kept carrying the toddler (the child rescue task or autonomy); needs the player's save.
Not run in game yet with a live emergency.

## 4. The same pattern elsewhere [verified, decompiled; not built]

- A second ungreet path: EA's `GoOutside` (Sims3.Gameplay.Core) calls `UngreetLotIfNoOneIsInside` once the Sim is outside,
  so stepping out of the front door is enough (no lot change needed). `firegreet` covers it (on the lot or near it).
- The other active careers greet once per trip: private eye (`SimArrivedAtLot`), ghost hunter (before arrival), interior
  designer (renovation visit `Run`), daycare transport situations. Each trip greets again, so they are only exposed when the
  Sim steps outside mid-job with no family Sim inside. Not reported by players: documented, not fixed (confirmed bugs only).
- `FireFightingJob` large-fire / earthquake setup greets `Sim.ActiveActor`. Because a greeting is per household, this only
  matters when the selected Sim is not of the firefighter's household (the only `ActiveActor` greeting in the game).

## 5. Respond to Emergency starts late: what the Sim waits on (2026-11-20, static, no game run)

Player report: "when the firefighter emergency triggers, and i make the sim go do it, he just stands there. he might do
idle animations but he is also clearly waiting on something before he actually responds to the emergency."

### 5.1 The chain [verified, decompiled unless marked]

1. Emergency raised: `ActiveFireFighter.StartAlarms` -> `FireStationAlarm.StartAlarms` -> `UpdateMapTag` (map tag on, game
   speed to Normal, `StartRespondToEmergencyAlarm` = one alarm 60 sim-minutes later that queues the trip by itself). The
   alarm's `ReactToAlarm` (run to a parking space, walkie-talkie / map / point for 5-15 minutes) is pushed only on
   non-selectable firefighters, so it never holds the player's Sim.
2. The player's command: `ActiveFireFighter.GoToJob` / `GoToJobWith` on the station alarm (or the job's own go-to-work
   definition). Its `Run` only pushes `RespondToEmegency` on the job lot as a must-run continuation; no wait of its own.
   Before it: the Sim's current interaction has to end (EA queue rule).
3. `RespondToEmegency.Run`, in order, before the Sim moves:
   - `EnsureSimHasOccupationOutfit`: builds the gear outfit (AuxCareer) only when the Sim has none (first emergency at this
     level; kept afterwards). Synchronous, so a hitch, not a visible wait.
   - `GreetSimOnLot`, then `Lot.PlanToLot(route)` with `EnablePlanningAsCar`: one world-scale plan through `Route.Plan`.
   - `Sim.SwitchToOutfitWithSpin(GoingToFightFire)`: `SwitchOutfitHelper.Start` hands the gear outfit to the native delayed
     swap, then `SwitchOutfitHelper.Wait(idleIfNecessary: true)` calls `Sim.LoopIdle()` and sleeps one tick at a time until
     the native side calls back. Then the spin animation.
   - Fire engine: `ActiveFireFighter.GetVehicle` (reserved, inventory, home lot, station lot) or a new community engine;
     `PlaceAt` the plan's vehicle start point (an owned engine plays the key-chirp animation first).
   - Other household firefighters picked with "go with" get `GetInCar` queued next.
   - `Vehicle.RouteToAndGetIn`: `PrePutIn` plans and walks a route to the engine (`CreateGetInRoute` -> `Route.Plan`), a second
     `SwitchToOutfitWithSpin(GoingOffLot)` (normally no change for a firefighter already in gear), `FadeOut(yield)`, seat.
   - A loop with no exit of its own: `Simulator.Sleep(0)` until no listed firefighter has the queued `GetInCar` left.
   - Siren, `CarNpcManager.DriveToLotInCar` (more plans), get out, `PushOnLotInteraction`.

### 5.2 What can make the Sim stand, ranked for a multi-second stand-still

| # | Wait | Whose | Evidence |
|---|---|---|---|
| 1 | Gear outfit build: `SwitchOutfitHelper.Wait` loops idle animations until the native swap is ready | EA design | the only step that plays idles while waiting, which is what the report describes [verified code; length inferred] |
| 2 | Route plans as worker jobs: the Sim's task sleeps tick by tick in `AsyncWait` until the job is done | ours | `[RouteAsync]` is on for every Sim's own plans, household included [verified code; length from other logs] |
| 3 | Spin, key chirp, run to the engine, fade | EA design | fixed animations, a few seconds, the Sim visibly acts |
| 4 | The interaction before it in the queue | EA design | not specific to this job |
| 5 | The wait for the other firefighters' `GetInCar` | EA, no-exit loop | only with "go with"; the driver already sits in the engine, so not this report |
| 6 | First gear outfit creation | EA design | a hitch, once per level |

**1, the outfit build.** Nothing in script can end it sooner: `mReady` is set only by the native callback (docs/224 §5: the
swap message `0x5ADD40` re-posts itself while the outfit is still building or the Sim has no animation instance). The build
runs resource loads on job threads, then the model build and the timesliced texture compositor on the main thread (docs/42
§4). The SimService builds one job at a time and picks the ready job of the Sim nearest the camera first (thumbstall.cpp
header, verified there) [inferred: the live swap's build stands in that same queue]. So the wait grows with the number of
Sims being built around the camera, with the camera away from the Sim (map view, looking at the burning lot), and with
slower compositing. The gear is a full outfit the session has usually not loaded yet. Our hooks on this path: the `Wait`
hook (`OutfitWait.cs`) runs EA's loop unchanged for a household Sim (same `LoopIdle`, same one-tick sleep; its switches act
only after 60 sim-minutes or on unseen NPCs); `unseenoutfitdefer` never defers a household Sim; `[ThumbStall]` as shipped
(Action 1) only ends portrait requests. None found that slows the build.

**2, our route jobs.** `RouteBackoff.cs` `AsyncEligible` has no household exemption (the result is the game's own plan,
only the thread changes), so the job-lot plan, the walk to the engine and the drive each become a job. In the game without
us the same plan freezes the whole simulation for its length instead; with us the game keeps running and this Sim waits
for: the jobs queued before it (one worker, oldest first; docs/631 §1: household jobs seen queued behind townie jobs of
mean 353 ms, max 1965 ms), its own job (household long jobs in the latest 38 phase logs: 72 of >= 200 ms, median 280 ms,
max 817 ms), and inside the job one sim tick per door question to the sim thread (docs/637: 92% of slow plan time). A plan
out of a station and into a house asks several doors. Usually well under a second per plan; seconds in a busy world.
Built for exactly this, both off and not run: `playerfirst` + `[RouteAsync] PlayerFirst=1` (docs/631, the player's route
jumps the queue), `[RouteAsync] TaskServe=1` (docs/637, door questions answered after every script task).

Not on the path before the Sim leaves: `firegreet`, `fireactivejob`, `firetrapcount` (no hooks in `RespondToEmegency`),
the door caches (only inside a plan, household never served a stored "locked"), `nearestlotlazy`, FindGoodLocation (the
engine is put at the route's own start point, no placement search).

### 5.3 Verdict

No bug found in EA's script on this path, and none of ours that changes the result. The stand-still is the sum of EA's
outfit build wait (their design; its length is native) and the time our async plans add in front of a player command (ours;
order and door-ask fixes exist, off). Which one dominates in the player's game is not known without a timed run, so no
new fix is built here. Lines that show each: `route async: long job N ms, own START ... scope household-or-seen`, the minute
line `route async: mode ... started ... refused ...`, counters [56] async plans started, [57] polls slept, [58] poll cap;
`[ThumbCallers]` lists `outfitswap 0x5A9E90` calls with their script stack; `outfitwaitpoll:` lines only past 60 sim-minutes.
The per-interaction delay probe (separate work) gives queued -> Run -> moving.

Leads, not built (no report, or no confirmed cause):
- The loop in 5.1 waits for good while a listed firefighter keeps a queued `GetInCar` it never starts (a long
  player-ordered interaction in front of it); the driver cannot be cancelled meanwhile (`CancellableByPlayer` is off).
- `GoingToFightFire` is not in EA's list of reasons that override a recent player-chosen outfit, so a firefighter the
  player dressed within the sticky hours responds in those clothes.

### 5.4 Play test

1. As is: send the Sim, note whether the wait is before the spin (outfit build or first plan) or after it (walk / engine).
   A second emergency in the same session with a much shorter wait before the spin points to the outfit build (resources
   now loaded).
2. `routeasync=0` (`/native?routeasync=0`, or `[RouteAsync] Mode=0`): the game's own synchronous plans. Shorter wait, with
   a short freeze instead = our jobs were the wait.
3. `[RouteAsync] PlayerFirst=1` + ts3fixes `playerfirst`, and `[RouteAsync] TaskServe=1`: the wait of step 1 against these.
