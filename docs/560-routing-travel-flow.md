# 560: routing and travel flow, script side (static, built, proven in game)

**In-game status:** `gohomewalk`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-14. Static review of EA's script-side travel code (decompiled 1.67, not committed) with the
gameplay semgrep rules from docs/559, extended for travel flows. Existing fixes checked
first (docs/103-116, 513, 514, 544, 546, 551-553, 558; Fixes.cs switches `gohome`, `visit*`, `goto*`,
`carnoroute`, `npcdriverpool`, `veh*`, `boat*`, `eventrouteretry`, `birthroute`, `putdownplace`).

## 1. Tools

`tools/static/ea-gameplay-logic.semgrep.yml` gets four travel rules: `hidden-left-on-exit` (hidden or
faded out, then `return false` before it is shown again), `reservation-left-on-exit` (Reserve/Claim/
AddToUseList, then `return false` before the release), `teleport-on-route-fail` (failed route then
`SetPosition`), `route-result-ignored` (route or get-in result dropped inside `Run`).
`unbounded-route-retry` now uses a metavariable regex: the old `RouteTo$M` pattern did not parse, so the
rule had never run. The parse error in `random-inclusive-index` is fixed the same way (the grep in
`gameplay-scan.sh` still covers it). Scan of Sims3GameplaySystems + Sims3GameplayObjects: route-result-ignored
164, hidden-left-on-exit 148, reservation-left-on-exit 35, unbounded-route-retry 5. Most hits are
by design (a jig hidden on purpose, a route whose failure is checked through the exit reason).
The scan finds candidates; the travel files listed below were read by hand.

A second scan rule, run with grep: a direct `Sim.RequestWalkStyle` (the per-Sim count in
`SimRoutingComponent`) in a file with no `UnrequestWalkStyle` / `ClearWalkStyle`. Requests made through
`InteractionInstance.RequestWalkStyle` are released by the interaction's cleanup and are not counted.
The grep (`grep-walkstyle-unreleased.txt` in `gameplay-scan.sh`) lists 10 calls. In travel code these are GoHome (§2, a bug), Firefighter `FastRun` (by design,
service Sim), BoardingSchool `GhostWalk` (by design).

## 2. `gohomewalk`: GoHome leaves its walk style requested (fixed, off by default)

`GoHome.Run` calls `Actor.RequestWalkStyle(WalkStyle)` when the caller set a walk style, and nothing
calls `UnrequestWalkStyle` afterwards: not Run, not `Cleanup`. `Sim.MakeSimGoHome(..., WalkStyle.Run, ...)`
is used by every party that gets busted (teen party, juice kegger, LAN, bonfire, tailgating, masquerade,
victory party). Each guest's Run request count stays at 1 after the trip, so these Sims keep running
for as long as they stay in the world, and also if the trip is cancelled or fails on the lot.

Fix: `GoHomeHook.RunHook` (Fixes.cs; GoHome.Run is already swapped there, so no new swap) reads the
Sim's request count for that walk style before Run. After Run returns (arrived, failed, cancelled or
thrown), if the count went up it takes back one request. Run's RideHere and carpool exits make no request,
so they are left alone. Only GoHome's own request is affected; a request made by anything else stays.
The fix is exact for every Sim: a Sim the player sent home with no walk style is unchanged.
Counters 2477 (released) and 2478 (errors). Switch `gohomewalk`, tuning `kGoHomeWalk`.

Check `Ts3Fixes.Fixes.GoHomeWalkCheck("run" | "result")`: the selected Sim gets GoHome's request, then
the hook's step. Switch off: BROKEN (count is before + 1). Switch on: FIXED. The check takes its own
request back in both arms. Repro `gohome-walkstyle-left` (any world, a Sim selected).

## 3. Read, not bugs

- `CarUFO` landing retry loops (`TryLandUFOAndExitSims`, 4 sites): limited to 30 sim-minutes, then
  `ForceExitUFODueToLandingFailure`.
- `CarpoolManager.WaitForCarpool`: limited to `kMaxCarpoolRouteWaitInMinutes`, then the Sim gets out.
  The carpool's unique-reference wait is limited to `kExtraMinutesToWait`.
- `ElevatorDoors.Open/CloseElevatorDoors`: the `mIsAnimating` spin is cleared in `finally`.
- `Subway` travel: the use list and muggable list are restored on cancel and on a destroyed destination.
- `GoHome`, `GoToLot`, `GoToCommunityLot`, `VisitCommunityLot`: at most three planned routes. No teleport
  fallback, and a cancel stops the retries.
- `VisitLot` callbox path: the `RouteToPointRadialRange` result is ignored, but the next step does not
  depend on it.

## 4. Leads, not fixed (ranked)

1. `GoHereWithHoldingInteraction` (hover-train station): a follower spins
   `while (leader.IsRouting && leader.RabbitHoleCurrent != null) Sleep(0)` without checking its own exit
   reason. Cancelling the follower does nothing until the leader moves. Needs a Future/hover-train world
   to show.
2. `CarUFO.UpgradeLaserCannons` / `UpgradePlating`: the Sim is faded out and given
   `HiddenFlags.Everything` before the animation. Restoring visibility on a cancel happens in the
   animation callback (event 101), so a cancel before that event may leave the Sim hidden. Needs an
   in-game cancel test; the read is not finished.
3. The rest of the route-result-ignored hits in career, situation and object code (164) were not
   reviewed. Run `tools/static/gameplay-scan.sh` and read the travel-adjacent files first.

Not covered in this pass: walk styles beyond the request/unrequest pairing, bicycle/horse/scooter
get-in, pets in cars, carrying a child into a car, situation go-to-lot steps. Native routing: no handoff
was read in REA/Ghidra.

## 5. Also in this pass

`BowlingLaneCheck` (docs/559 §1) returned an empty result in game, in both arms (Bridgeport,
phase-20261114-2326). Calling the swapped `ReadyToStart` by reflection on an uninitialized lane is the
likely cause. Part (a) now runs the hook's ghost drop and EA's ReadyToStart rule directly on the
throwaway lane's lists. The whole check is wrapped so that an exception is printed with its type,
message and stack instead of an empty result.

## 6. Second pass (same day): leads and the areas not reached

No new bug was confirmed, so no new switch.

- Lead 1, hover-train follower (§4.1), ruled out. The spin reads `leader` from the top of the follower
  loop, and that local keeps the Sim even after the situation ends, so it never throws. `IsRouting` turns
  false when the leader's route ends or the leader is destroyed (`SimRoutingComponent` null). A cancel is
  delayed only until the leader leaves the station.
- Lead 2, UFO upgrades (§4.2), ruled out. `Run` is linear: a cancel ends `DoLoop` and goes on through
  `Exit Upgrade` and `UpgradePostExitAnimFadeIn` (unparent, place, `FadeIn`). Only an exception would skip
  that, and a reset handles it.
- Pets / child in cars: `CarOwnable` visit-lot-with-stroller and `Stroller` handling are fine. A
  reserved vehicle left set after a failed plan is by design (`ReservedVehicle` is the Sim's car;
  `SetReservedVehicle` refuses some changes).
- Bicycle: `GetOut` / `OnRouteFinished` / `Dispose` clear the reservation. Horse heroic pose ignores its
  route result, which only affects how it looks.
- Situation go-to-lot: the 5 `ForceSituationSpecificInteraction(... GoToLot/VisitLot/GoHome ..., null,
  null, null)` sites (burglar, house party host, NPC party guest, prom date, social worker pet) are
  fire-and-forget. Each situation moves on by alarm, so a failed trip does not hang it.
- Script to native hand-off: every script test of a plan result uses `Succeeded()` or compares to
  `Succeeded` / `FailedBecauseDestinationPortalLocked`. None treats a failure code as success.
- InferSharp on Sims3GameplaySystems: not run (TS3W.exe running).
