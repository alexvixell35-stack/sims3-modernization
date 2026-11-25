# 688 reachcache: refuse route plans into rooms the Sim cannot enter (2026-11-25)

Status: built 2026-11-25, default off. Proof status at the bottom.

## Question

A RimWorld-style "which rooms can I reach" cache for household Sims (and NPCs): do Sims still waste route plans, walks
and RouteFail balloons on targets behind locked doors, privacy-locked bathrooms, pet or ghost locks?

The world level is done (`[RouteEarlyOut]`, docs/142: exact, 0 ms saved). This is the lot / room level.

## Measured (storm logs s560-s594, 36 phase logs, 12.1 h wall, `routefaillog` on)

Every `ts3fixes: routefail:` line is a household Sim (the log is household-only). NPC failures are counted only in the
`route plans` / `worker hold` / `ufplansplit` summaries.

| | count | per hour | plan ms |
| --- | ---: | ---: | ---: |
| all failed plans (worker hold) | 14,923 of 751,315 | 1,233 | 159,569 of 7,502,881 (2.13 %) |
| household routefail lines | 1,907 | 158 | 94,438 (1.26 %) |
| household, `DestinationPortalLocked` | 252 | 21 | 13,543 |
| household, `NoPathToDestination` with a locked door asked | 152 | 13 | 7,155 |
| household, any locked portal involved (autonomous, not an order) | 649 | 54 | ~22,000 (0.3 %) |
| of those, the same Sim and target as its previous locked failure | 234 | 19 | |
| NPC `DestinationPortalLocked` (ufplansplit) | 20 | 1.7 | 6,358 (wall) |

Targets of the household locked failures: ServingContainer_CleanUp (plate; the route goes to a sink or dishwasher in
another room), UseToilet (privacy), MopPuddle, DollHouse (child, locked kids' room), SocialInteractionA (target Sim in a
private room), FoodTray_Prepare. Plan time is small (0.3 %); the visible part is not (about 54 failures and balloons an
hour, a third of them repeats).

NPC locked waste is negligible (20 in 12 h); NPC failures are no-destination (5,080) and no-path (2,112), not locks.

## What EA already has

EA ships the per-Sim room cache this question asks for: `Sim.IsAllowedInRoom(lot, room)` reads `mAllowedRooms[lot]`,
filled by `Sim.RecalculateAllowedRooms` (a flood over the lot's `IPortalConnectionObject` cache from the Sim's room,
through portals whose `IsAllowedThrough` is `None`, skipping `Lot.IsRoomRestricted` rooms = privacy). It is versioned by
`Lot.GetObjectCacheVersion<IPortalConnectionObject>()`, which EA bumps on every lock change
(`CommonDoor.SetLockTypeAndOwner` -> `OnPortalRestrictionChanged` -> `RefreshObjectCacheUsers`), on privacy start / end
(`Register/UnRegisterRoomRestrictionCheckCallback`) and on build / buy (`RefreshObjectCaches`). Autonomy checks it before
scoring (`Autonomy.CheckAvailabilityOnLot`), for the interaction **target's** room only.

Gaps that leave the failures above:
- the route's destination is not the target (CleanUp plate -> sink, pushed follow-ups);
- interactions with `AllowEvenIfNotAllowedInRoomAutonomous` / `IIgnoreIsAllowedInRoomCheck`;
- the planner's privacy lock (`PrivacySituation.OnPortalPathPlan`) depends on the current interaction's priority, the
  room cache does not;
- a lock that starts between the pick and the plan (privacy race), then replans of the same locked route.

So reachcache does not build a second cache of rooms; it asks the planner's own lock oracle at plan time and reuses
EA's portal cache version for invalidation.

## Design (ReachCache.cs, ReachCacheCore.cs)

- **Destinations**: BlockedUse.cs's existing `Route.AddDestination` / `ClearDestinationList` swaps also note each
  destination per route handle. Point destinations and slot destinations (each slot's position) are placed in a room
  (`World.GetRoomId(lot, pos, PlacementBlocking)`, the room definition of the portal cache). Any radial, lot or other
  destination, a point outdoors (room 0) or unplaced, or destinations on two lots: unknown, the plan runs.
- **Flood**: from the Sim's room (or the lot's outdoors when the Sim is elsewhere) over
  `Lot.GetObjectsInRoom<IPortalConnectionObject>` with `GetAdjoiningRoom` (and `ElevatorInterior`'s room list), as
  `RecalculateAllowedRooms` walks. A `Door` is locked when `Lot.OnPortalPathPlan(sim, door, from, to, false)` answers
  `PortalLocked`: the very callback the planner asks per door (`Sim.OnPortalPathPlan` + every `PrivacySituation`, with
  this Sim's current interaction); the side list `LockedDoorsDuringPlan` is trimmed back after. Every other portal is
  open. A Sim whose `RoomId` disagrees with the room at its position: unknown. The flood is kept per Sim for 5 s while
  lot, start room, portal cache version and interaction are the same.
- **Verdict**: refuse only when every destination room is placed, on that lot, not outdoors and not reached.
- **Act** (`reachcache` NPCs, `reachcachehh` household and selected Sims; a player's order, priority UserDirected or
  higher, is never refused): `ScriptCore.Route.Plan` / `Replan` (RouteBackoff.cs PlanHook / ReplanHook) return
  `FailedBecauseDestinationPortalLocked` without searching (RouteBackoff's `FailFast`), `DoRouteFail = false` (no
  balloon), and the (Sim, target, interaction definition) pair is recorded. Autonomy (AutRoomMemo.cs `ArmPairHook`,
  `Autonomy.CalculateScoreAndAddToCandidates`, search type Autonomy only) leaves that pair out while the lot's portal
  cache version is unchanged, for at most 1 sim-hour; so the Sim picks its next candidate.
- **Verify** (`reachcachelog`): every refuse verdict runs the real plan anyway; a plan that succeeds is WRONG (counter
  4824, one `reachcache: WRONG` line each) and EA's result is kept; a failed one is right (4823). Acting with the log on
  drops the balloon and records the pair after the real failure. EA locked results the verdict did not refuse are counted
  as missed (4825).

Better, not identical (user rule for routing): a refused plan is one EA would fail; the visible change is no balloon
and autonomy skipping the pair. No new swaps (rides on RouteBackoff, BlockedUse and AutRoomMemo's).

Counters 4820-4839: see docs/registry.md. Check: `reflect_call Ts3Fixes.Fixes.ReachCacheCheck run|result|lock|unlock|probe`.

## Proof status

See the end of this doc (updated with the A/B).
