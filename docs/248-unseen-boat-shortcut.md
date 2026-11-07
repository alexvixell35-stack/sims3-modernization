# Unseen boat trips (switch `unseenboat`)

Built 2026-11-06, branch `feature/unseen-boat-teleport`; the first run never fired, see §8 (`bugfix/unseenboat-route`, package `boattp3`, not run). Houseboat starts: §10. Tags: [verified] read in the decompiled scripts or the repo, [inferred] not proven, [measured] from a run.

## 1. Problem

docs/247: after `GetInBoatRouteAction` fades the Sim in, `DoRoute` calls `ReplanFromPoint` synchronously on the sim thread; plan time is 92% of those runs, 245 runs >= 100 ms, 37.4 s on IP in one run, worst 341 ms, same on Baseline [measured, docs/247]. The replan is sync because the release ini has `[RouteAsync] Kinds=0, ForeignKinds=0`.

What the replan is for [verified]: the Sim's route was planned as a Sim. `GetInBoatRouteAction` picks a boat (`GetBoatForGetInBoatRouteAction`: owned, reserved or chosen boat, else `GetNpcBoat`, else `Vehicle.CreateTaxiBoat`), places it, puts the Sim in as driver, sets the boat as follower and returns `ContinueAndPopPathAndReplan`. The replan makes the same route again with the boat's routing profile. On arrival `BoatScriptAdaptor.PostRoute` runs `boat.OnRouteFinished`, which gets everybody out and parks or releases the boat.

## 2. Design (Rodney, 2026-11-06, by purpose, unseen only)

| Trip | What happens |
|------|--------------|
| Transport: the water must be crossed, the boat is only the way there | No get-in, no boat. The Sim waits for the travel time where it stands, is placed at the goal, plans the last metres |
| Goal on a houseboat lot | The same wait, then EA's `Houseboat.PlaceSimAtEntrance`, then the route options off as `Houseboat.cs` does after that call |
| Anything else | Exact path |

An earlier build of this branch replanned from a water point near the landing and held the boat frozen. It still spawned the boat and did the get-in, and it was dropped for this design before any run.

### How the trip is classified

`UbCore.Trip` plus `UbGate`, before any plan, from the route and its goal:

- Transport needs proof: `Route.GetOption2(EnablePlanningAsBoat)`, no `BeginAsCar` / `BeginAsBoat` (`UfCarOrBoat`), goal on another land component (`UfSameComponent` -1, the game's flood colours through ts3native codes 11-13). Same colour: a walk, not ours, D handles it. Unknown colour: exact, `unseenboat_rj_unknown`.
- Houseboat: the goal's `Lot.IsHouseboatLot()`, docked or not. The goal lot is not the Sim's own current lot.
- A route that would swim across instead of taking a boat cannot be told apart without the plan. It counts as transport too: nobody sees which it would have been [inferred: harmless].

### What keeps the exact path

| Rule | Check | Counter |
|------|-------|---------|
| Trip under 40 m, a plan inside a plan, not the Sim's own task | `UbTrip` | none |
| Route ends as a boat or in deep water, start swimming or raised (outdoors off the ground), caller does not follow the route, task may not yield | `EndAsBoat`, `DestinationMustBeInDeepWater`, `UfOutdoorGround(start)` with room 0, `UbCore.Follows`, `MayYield` | `rj_route` |
| Pet, under teen, group or passenger, riding, carrying or carried, a social | `UfPreSim(sim, false)` | `rj_sim` |
| Interaction needs the boat or the water | `UbCore.Denied`: the finish list plus Boat, Waterski, Surf, Sail, Fish, Scuba | `rj_sim` |
| The trip has followers, or the start is indoors on a lot D's relaxed rule refuses (apartment, shell, moving houseboat, resort, dive lot, locked door) | `UbAlone`, `UfRelaxedLotWhy(start)` | `rj_sim` |
| Sim or goal in view | `UfWhy`, `TownieUnseen`, `UfGoalUnseen` | `released_seen` |
| An owned, reserved or chosen boat exists: it must end up at the goal | `sim.GetOwnedAndUsableBoat(...)` non-null or `disallowedOnlyTemporary` | `rj_owned` |
| Goal on the active household's lot, apartment, shell, moving-houseboat rule, resort, dive lot, a lock, indoors, raised, in the water | `UfRelaxedLotWhy`, `UfOutdoorGround(dest)` | `rj_goal` |
| Goal lot private with no SafeSpot verdict, or the verdict says no | `UfVerdict`; a community lot or the street passes without one | `rj_goal` |
| Goal not routable and no spot within 8 m | `IsPointRoutable`, `UfNearGoal` | `no_spot` |

A goal in the water (water destination of Rodney's table) is on this list: placing a Sim in water needs a swim posture and no tested path does that. Open question for Rodney, see §7.

Sims already on a boat never reach this code: the Sim is the route's follower only before the get-in, and `UfHeld` refuses riders. Passengers likewise: a group is refused, and a Sim driven by another Sim has no plan of its own.

### Failure behaviour

Nothing moves before the end of the wait, so every failure is the exact plan from where the Sim stands, late by at most the wait:

- Seen, moved, killed, exit reason, interaction ended, not live: `UfHold` returns false, the exact plan runs. The Sim is where it was, which is the correct state.
- `PlaceSimAtEntrance` or the routable recheck fails: exact plan, `no_spot`.
- A gate throws: exact plan, `rj_route`. Nothing around the wait swallows exceptions, so a task reset passes through as in D.
- After placing, the plan is the normal one from the goal; a transport placement enters `sUrPlaced`, so D's repair check runs when the Sim is seen and plans again.

## 3. Gate and saved stacks

The brief's hard rule is no swap on a method that can yield or sit on a saved stack. `UnseenBoat.cs` adds no `Patch`. The call is the first thing in `RouteBackoffHook.PlanBackoff`, under the existing `Route.Plan` hook. `tools/swap-stack-audit.ts` passes.

Why that spot and not `UfPre`, `UfPlanBody` or `UfPlan`: D's hold sleeps inside those, so a save can hold their frames, and a build that changes a hook-side body refuses such a frame once (docs/242). `PlanBackoff` is not on D's hold stack, and every refusal of D falls through to it. It only yields in async plans, and every release ini has `Kinds=0`. So those three keep their IL and this update refuses nothing there [verified: `git diff` empty for `UnseenFallback.cs`, stack read in the source].

The wait itself sleeps in the Sim's own task like D's `UfHold`, the same function. A save during it holds frames of `UbGo`, `UbTrip`, `PlanBackoff` and the hook: same class as D, and removing the mod resets that Sim once, same limit as docs/242. No state goes into saves.

`UbTrip` clears unseenfbplan's cut mark across the wait and sets it again after, or a marked plan would never reach the wait [inferred: the mark is only read by the plan; verify run shows `unseenfbplan` cuts still count].

The plan stays in `PlanBackoff`'s frame, so the trip adds no frames above it (issue #92).

### Reuse of the car path

`UfCarOrBoat` and D (`UfPre`) refuse boats for a reason that does not apply here: D wants a land proof, `same < 0` is its rejection 338. What is reused: `UfCarOrBoat`, `UfHold`, the ghost start and end, `UfPreSim`, `UfRelaxedLotWhy`, `UfOutdoorGround`, `UfVerdict`, `UfNearGoal`, `PlaceSim`, `sUrPlaced`. What differs: the proof is inverted (colours must differ), no group moves, `UfPreSim` non-relaxed, the owned-boat rule, the houseboat branch, and it runs after D refused rather than inside it. It does not depend on `unseenroute` or `unseenfbpre`.

## 4. Boat kinds

[verified decompiled] `Boat : Vehicle, IBoat`: BoatTaxi (water taxi), BoatSpeedBoat, BoatSpeedFishingBoat, BoatRowBoat, BoatSailBoat, BoatWaterScooter (jet ski), BoatPaddleBoat, WindSurfboard. `Houseboat : GameObject, IBoat` is not a `Boat`. No gondola class exists.

| Kind | How it behaves here |
|------|---------------------|
| Water taxi (BoatTaxi), NPC boats from `GetNpcBoat` | Transport: skipped, no boat is made |
| Owned speedboat, sailboat, rowboat, jet ski, paddleboat, windsurfboard | Exact (`rj_owned`): it must end up at the goal |
| Houseboat as goal | Teleport onto it |
| Houseboat moving under its own route, a Sim leaving a houseboat | Exact: not this route; the start colour is unknown from a houseboat lot. Leaving: §10, `unseenboatstart` |
| Paddleboat agents (`PaddleBoatBody`, every tick) | Untouched |

Skipping the taxi means `GetNpcBoat`'s boats are not used for this trip; nothing depends on that [inferred].

## 5. Counters

`[836-849]`, agent `unseenboat_*`: `gate`, `seen`, `unseen` (replans after a get-in, with the switch on or off: the real unseen share, the 89-99% of docs/247 is locomotion updates), `shortcuts`, `no_spot`, `rj_route`, `rj_sim`, `houseboat` (subset of shortcuts), `rj_owned`, `candidates` (trips classified as transport or houseboat), `released_seen`, `rj_goal`, `max_hold_min`, `rj_unknown`.

## 6. Existing fixes

NRaas: GoHere `mAllowBoatRouting` turns boat routing off; `RoutingComponentEx.DoRoute` still calls `ReplanFromPoint`; ErrorTrap has deref classes only. None covers this. Web search: docs/24, 2026-11-06 entry.

## 7. Not verified, game run needed

Queue `tools/harness/queues/verify-shortcut-boat.json` (IP): smoke, natural verify (`unseenboat_shortcuts>=5`, then `verify-shortcut.ts --name unseenboat`), both orders of `--ab unseenboat` for route bars R1-R6 (`gate2-report.ts`), forced repro `ip-unseen-boat-loop`.

- [inferred] How many real trips classify: `candidates` and `rj_*` will show it. If owned boats dominate on IP, `rj_owned` says so and Rodney decides.
- [verified] `SimRoutingComponent` sets `EnablePlanningAsBoat` by default for a child or older, or when a boat is given; several callers clear it. Not yet observed live on the routes docs/247 logged.
- [inferred] The flood colours put islands in different components on IP, and a goal on an island has a known colour.
- [inferred] `PlaceSimAtEntrance` is enough for a Sim on foot: EA uses it for a Sim beside the houseboat and after a boat get-out.
- [inferred] A waiting Sim on the shore shows nothing: same as D's hold.
- Open for Rodney: goals in the water stay exact.

## 8. First run: every candidate refused (2026-11-06, boattp2)

IP, natural 20 min: `candidates 646`, `rj_route 646`, `rj_unknown 288`, `shortcuts 0`, get-in replans `gate 598`, `unseen 596`. Forced repro: `candidates 419`, `rj_route 419`, `rj_unknown 148`, and it passed, since its pass line did not need a shortcut.

**Cause.** `rj_sim`, `rj_owned`, `released_seen` and `rj_goal` are all 0, so every candidate stopped on `UbGate`'s first line (or threw, also `rj_route`). Of that line, `UfFollows` is D's allow-list of `RoutingComponent` helpers, and it does not hold the plans that cross water:

- [verified, decompiled] `crowd_go` is Go Here. `Terrain.GoHere.RouteActorToDestination` plans in its own body, so that frame is what `UfFollows` sees, and it is not listed.
- [verified, decompiled] Lot trips plan in `Run` through `Lot.PlanToLotEx`, which `UfCaller` skips: the frame is the `Run`. `UfPreCore.PlanToLotFrame`'s own comment says these stay refused, on purpose for D.
- [measured, older runs] D's refused callers on IP (`unseenfbpre_callers`, 18 jobs): `VclSubHook0.Run` 2580, `RabbitHole.RouteOutside` 1546, `RabbitHoleHook` 1204, `Lot.PlanRouteIntoLot` 1023, `Lot.PlanToMailbox` 558, `GoToCommunityLotHook.Run` 91.
- [inferred] All 646 went that way. The run did not record callers (`fixesReport` was `unseenboat`, the list is `unseenfbpre_callers`), and an indoor start also counted as `rj_route` and hid under it. `unseenboat_log` now shows the split.

Ruled out from the source: the follower is not the boat (`UbTrip` only runs when `Follower` is a Sim, the boat replan is another plan kind and goes to `UbCountReplan`); the route has its goal (`candidates` needs a destination and a colour); colours are not always false (646 reached -1 or a houseboat goal); the hook sees the first plan of the trip, before `GetInBoatRouteAction` runs.

**`rj_unknown`.** `UfSameComponent` 0 on a route of 40 m or more that may take a boat: ts3native found no single flood colour within 12 m of the start or goal, nor of their lot's entry point (none coloured, or two colours). Exact plan, not a refusal of a crossing. The sample line says which end was unknown.

**Fix.** `UbCore.Follows` replaces `UfFollows` here, and D's rule is unchanged. It takes D's helpers plus bodies read as plan-then-`DoRoute`: `GoHome`, `GoToLot`, `GoToCommunityLot`, `VisitCommunityLot` `Run` (and their hook frames, and `VclSubHook*`, a mod's override [inferred: a copy of it]), `RabbitHole.RouteOutside`, each also through the Lot helpers (`PlanRouteIntoLot`, `RouteToDoor`, `RouteToFrontDoor`, mailbox). The frame above a Lot helper decides, because `RouteToDoor` with `doRoute` false only hands the route back. Left out: Go Here (`UfPreSim` refuses it, so the repro's `crowd_go` legs stay exact and give the boats `unseen_water_watch` needs), `VisitLot` (its followers are private), `RabbitHole.RouteNearEntranceAndEnterRabbitHole` (its plan picks a slot, `RouteOutside` plans again and follows).

Two rules changed with it. A trip with followers is refused (`UbAlone`): `VisitCommunityLot` picks followers without a grouping situation, which `UfPreSim` would not see. An indoor start now passes under D's relaxed rule, which is Rodney's design (unseen is the condition); the Sim stands indoors for the wait as D's hold does.

**Samples.** First 20 refusals per reason, `unseenboat_log` (agent `patches`, row `fixActivity.lists`), only with the switch on: reason, Sim, distance, start and room and lot, goal and lot, components, follower, interaction, caller. A refused caller is its own reason, so each frame gets 20 lines; 240 lines in all.

**Repro.** `fixesRequire` in a repros.json entry is now read by `profile-world.ts`, and `ip-unseen-boat-loop` needs `unseenboat_shortcuts>=1`. Leg 1 is `crowd_visit_lot {lot: island}`; agent `FindLotByName` knows `island`, the land community lot behind the most sea (no dive lot or resort).

## 9. Follows through hook stubs (2026-11-06, pair `hitchfix1`, not run)

Natural boattp3 run (docs/247 §11.3): 572 candidates, `rj_route` 544, 3 shortcuts. `Follows` tested the frame right after the Lot helpers, and for a hooked `Run` that is the stub (`GoHome.RunOrig`). It now skips `*Orig` frames, so the hook frame decides. `VisitLot` joined the followers (`UbAlone` reads `mFollowers` by reflection, `UfVerdict` stays), and the start rule of §2 is now a water posture, not a height. `unseenboat_ms`: boat replan ms per refusal reason. `kUnseenBoat` stays off. docs/247 §11.10.

## 10. Sims starting on a houseboat (switch `unseenboatstart`, 2026-11-07, pair `hbstart1`, not run)

Rodney 2026-11-07: Sims that start a route on a houseboat stayed exact (`rj_unknown`, §4). Teleport them all if unseen; visible Sims stay exact; snap back rules stay; safety guards stay (0 stuck, houseboats do not move, a valid end place, no Sim tied to a boat it left). Off by default, needs `unseenboat`.

**Are all 223 `rj_unknown` houseboat starts? No, about 93%.** In `verify-shortcut-boat-2026-11-06-ip-uboat-verify-r2` the log kept 201 of 240 lines, so a reason below the 20 line cap is a full count: `colours unknown, start known` 16, which leaves `start unknown` 207 of 223 [measured]. All 20 sampled `start unknown` lines stand on houseboat lots of that save's `houseboats.json`: Tide Runner, Our First Houseboat, Atlantis, Sea Spirit, Aquaholic, Fantasea; the other 187 are [inferred] the same. The 16 are not boat trips off a houseboat: land Sims, 12 of them from Holiday House to one street point (Meet Up, 69 m), goal colour unknown. They stay exact. `hitchfix1` run: 270, `start known` >= 11 (log full at 240, so a floor).

**Why the colour is unknown [measured].** A houseboat's lot does not stand where the boat shows: samples start at z ~2060, `houseboats.json` has the same boats at their ports (Tide Runner x 823, z 646). No waypoint is near the hidden lot, so no colour; `lot.EntryPoint()` is there too. EA maps between the two with `World.GetWorldToHouseboatDisplayPosition` [verified]. Same reason, two things the old code got wrong for any houseboat end, fixed here for all boat trips:

- The view test: a Sim's or a goal's position is the hidden lot, so a boat in the camera could read as unseen. Both ends now also need `UbBoatUnseen`: the boat's own position, 15 m (half the diagonal of the largest hull), at the gate and after the wait. That `IsObjectOnScreen` and `[UnseenTable]` read a boat's Sim wrongly is [inferred]; the extra test costs nothing either way.
- The wait: `UbTripM` measures boat to goal, not hidden lot to goal.

**What a Sim on a houseboat is tied to [verified, decompiled].** `ExitHouseboatThroughGangway`, `...Boat`, `...Ladder` act on the plan, in `CheckAndUpdateForHouseboats`, so nothing exists before it, and `UbTrip` runs first. `GetOccupants` reads `lot.GetSims()`. Gangway callbacks and `FreezeHouseboatMovement` belong to the planned route. `mDriver` and `mSeasicknessAlarms` remain; alarms only exist in `Status.Routing`. `Lot.GetSims` follows the lot change [inferred; `houseboat_leave {check}` tests it].

| Case | Handling | Reason row (`houseboat start: ...`) |
|---|---|---|
| In the cabin or below deck, on deck | Handled: `UbGate` asks the boat's state, not `UfRelaxedLotWhy(start)`, which would refuse every moored boat and any locked door | - |
| Docked, or at sea (moored) | Handled, both pass | `placed, Docked`, `placed, StationaryAtSea` |
| No gangway, no route off | Handled: the placement needs no route. EA's own exit would use a boat | `placed, ..., no gangway` |
| Boat moving (`Status.Routing`), tested again after the wait | Exact, the Sim would keep its alarm | `boat moving` |
| The boat's driver | Exact | `driver` |
| The active household's boat | Exact | `start:household lot` |
| Lot says houseboat, no `Houseboat` object | Not a leave: `UbBoatOf` is null, as before (`rj_unknown`) | - |
| Owned, reserved or chosen boat | Exact (`rj_owned`): it must end up at the goal | `owned boat` |
| Trip under 40 m from the boat, not the lot | Exact | `short trip` |
| Goal in water, on the household lot, resort, dive lot, apartment, shell, locked | Exact, §2 rules | as §2 |
| Caller does not follow, group, followers, child, pet | Exact, §2 rules | as §2 |
| Sim, boat or goal seen | Exact, `released_seen` | `seen` |

**Goal.** A lot goal: the lot's street side (`EntryPoint` on the terrain, dry, routable, unseen), the spot `UrRepairSim` trusts, and the plan walks the lot from there. So a door goal, an indoor goal or a private lot with no verdict need no refusal. No such spot: the §2 goal rules. A street goal: §2 rules. A houseboat goal: `PlaceSimAtEntrance`, §2. Placed Sims enter `sUrPlaced`, so D's repair runs when seen.

**Checks.** `[850-853]`, agent `unseenboatstart_*`: `candidates`, `shortcuts`, `no_spot` (verify-shortcut reads it), `left_dirty`: after placing, the Sim still on that lot, the driver, or in the alarm list; must stay 0, and a `left dirty` row names it. Rows carry the prefix, so `unseenboat_ms` splits too. With the switch on these trips leave `rj_unknown`, which then holds only the land cases above.

**Existing fixes.** NRaas: read for behaviour, nothing like it. GoHere's `SimRoutingComponentEx` has a commented-out block that would clear boat options off a houseboat, `VisitCommunityLotEx` only sets `EnablePlanningAsBoat` for mermaids on a houseboat; StoryProgression's `FishHereEx` and `SimEx` only map positions. Web: MTS 665907 stops NPCs moving houseboats, a different cause; forum advice removes the Scott household or the boats; no teleport. docs/24, 2026-11-07.

**Not verified (queue `verify-shortcut-houseboat.json`, not run).** Fires and how many refuse: no live data, all numbers above are the old rows; `Houseboat.Position` as the shown spot is [inferred] from `houseboats.json`, whose source field was not traced to the game's draw position; `EntryPoint` on land for lots on islands; a Sim leaving a cabin at level -1 ends with correct level state [inferred, same `SetPosition` as EA's `PlaceSimAtEntrance`]. Gain: not measured, and by rule not asked. The `ip-houseboat-leave-loop` forced repro covers docked boats; an at-sea boat only shows if the save has one, read the `placed` rows.
