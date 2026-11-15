# 580: Log-census batch (docs/579-log-issues.md): root causes, verdicts and routecauselog

Follow-up to the log census in `docs/579-log-issues.md` (cleanup branch, `tools/harness/log-issues.ts`, 152 logs).
It takes each listed lead to a root cause or to the data still needed. Run data: `phase-20261115-1146` (all switches on)
and `-0523` (all off), `leak-*.json` snapshots. "[verified, decompiled]" marks EA code that was read; "[measured]"
marks run data.

| # | Lead | Verdict | Next |
|---|------|---------|------|
| 1 | Event listeners kCaughtCheatingOnPartner / kRelationshipLTRChanged / kSawSim grow | Mostly household wish nodes, held by their managers: not a proven leak | Census now gives wish triggers and wishes about gone Sims |
| 2 | Broadcasters on one lot 12 -> 35, queued socials 16 -> 41 | Phase content, not a leak | none |
| 3 | School work-day alarms 65 -> 110 | Not a bug: count follows the number of school-age townies | none |
| 5 | DisposeOutside / GetInPool / PlaceWelcomeKit / RouteAway fail with no plan nodes | Cause not visible in current logs | `routecauselog` |
| 6 | Household ToiletExpensive DestinationPortalLocked / NoPath, same Sim 5+ times | Privacy locks during another Sim's toilet or shower use; the owner is not logged yet | `routecauselog` |
| 7 | ServingContainer locked / no path | Same shape as row 6 | `routecauselog` |
| 8 | Socials started on a "sleeping" partner | "sleeping" is EA's `SynchronizationSleeping` flag, not a sleeping Sim. 0.0-min failures end on an exit reason that was already set | syncwait lines now print exit reasons |
| 9 | RouteAway -> Terrain fails uncut | Radial wander with no reachable spot; parameters not logged | `routecauselog` |
| 10 | UniversityDiploma piling up in inventories | Not a bug: at most one per townie | none |
| 11 | NPC horses in Late Night apartment units (coordinator lead) | Vanilla can produce it | `PetHomeCensus` |

## §1 Listener growth

[measured] In 1146, total listeners went from 7629 to 8455 (+826). Of these:
- +705 are `ActiveDreamNode.RunFeedbackFunction`, the wish nodes of the 6 household Sims;
- every wish node is still held by its owner's `DreamsAndPromisesManager` (orphans 0);
- no listener has a gone owner or target (0).

[verified, decompiled] Each `ActiveDreamNode` adds one listener for its trigger event
(`ResetListener`). Notifications has a single kCaughtCheatingOnPartner listener. So the per-event growth follows the
size of the wish trees, which grows with the number of relationships.

The leak census (`LcDreamSubject`) now also reports:
- `dream_listeners.by_trigger`: wish nodes per trigger event;
- `dream_listeners.subject_gone_by_trigger`: wishes about a Sim that has neither a SimDescription nor a
  MiniSimDescription.

A non-zero `subject_gone` count would make this a leak.

## §2 Broadcasters and queued socials

[measured] `max_on_one_lot` goes up and down (5, 26, 15 across 1146's snapshots) as party food and Sims come and go.
`gone_object` and `on_other_lot` stay 0. `queued_social` and `queued_social_gone_target` are 0 or 2.

## §3 School alarms

[measured] The School.* alarm counts per snapshot over 40 runs are 0, 60, 65, 70, 75, 80 and 85: always a multiple of
5, one alarm per school day for each child. The count is 0 when the census runs outside the alarms' window. It rises as
spawn phases add school-age townies. `gone_owner` is 0 in every snapshot.

[verified, decompiled] `Career.OnSetupWorkDayAlarms` removes the old handles before it adds new ones.
`Occupation.CleanUpAlarmHandles` runs on Cleanup and on leaving the job.

## §5, §9 NPC zero-plan failures

[verified, decompiled] The code paths behind each failure:

- **DisposeOutside** (`TrashPile.PutHeldTrashInCan`, forceOutdoor): picks the closest `TrashcanOutside` on the actor's
  lot and routes to it. An outdoor failure drops the bag and returns false. The interaction is pushed by
  `TrashcanIndoor` when the indoor can is full.
- **GetInPool** (`GetInPool.Run`): calls `PlanToSlot` with the pool's Inside routing-slot group. Result 10
  (NoDestinationSpecified) means that group is empty. Humans are refused at fountains, pets are not.
  [measured] The Sims seen were "Clubber" NPCs, in `ufloop` restarts.
- **PlaceWelcomeKit** (University mascot): runs `PutDown.DisallowCarryButSameRoom`. If that fails, the kit is
  destroyed and the intro is not marked complete.
- **RouteAway**: runs `Sim.Wander` with a radial destination limited to the Sim's room and the rooms next to it.

The native job line has no goal, so the remaining step is data. **`routecauselog`** (RouteCauseLog.cs, log only, off by
default, counters 2709 lines / 2710 dropped) logs these failures for NPCs with:
- goal, the Sim's spot and the interaction target;
- for pools: the inside slot count, the fountain flag and the species;
- for trash: the lot's outdoor cans (in use / tipped), the carried object and whether the Sim is outside;
- for RouteAway: its distances and outdoor limit.

Check: `RouteCauseLogCheck`.

## §6, §7 Locked doors on the household lot

[measured] Of the UseToilet failures, the DestinationPortalLocked rows are mostly near-goal Plan/Replan rows from
several rooms. The far rows are the probe (docs/578 §4).

[verified, decompiled] `UseToilet.Run` queues only on the toilet's own `Line`. A Sim who is showering or bathing in
the same room holds a `PrivacySituation` that locks the door, so the toilet looks free and the route fails at the door.

`routecauselog` logs every DestinationPortalLocked failure, household included, with:
- the locked doors (`SimRoutingComponent.LockedDoorsDuringPlan`, rooms);
- the privacy situations of the target room (type, owner, the owner's interaction).

That shows whether the lock is legitimate (someone inside) or stale (docs/577 §3). Autonomy choices are not changed
here: a household fix needs the stale case proven first.

## §8 Social sync failures on a "sleeping" partner

[verified, decompiled] `Sim.WaitForSynchronizationLevelWithSim` sets `SynchronizationSleeping` on the waiter. When the
partner is already at the level, the loop body never runs, and the final `HasExitReason(exitReasonInterrupt)` decides.
So "failed, 0.0 min, checks 0" lines (PillowFight, TakeCellPhonePhotoPaired, ZombifiedSocials) failed on an exit reason
the initiator already had. Failed `syncwait:` lines now end with `own exit <reasons>, partner exit <reasons>` (synctime
switch, SyncWaitLog.cs). The Test-side question waits for those reasons.

## §10 Diplomas

[verified, decompiled] Only two paths give diplomas to NPCs:
- `StoryProgression.AddAcademicsDegree`, whose `IsValid` requires `!HasCompletedAnyDegree`, so once per Sim;
- `TravelUtil.SpecialStoryProgressionAtUniversity`.

Each graduate gets one `UniversityDiploma` (`AcademicDegree.GiveUniversityDiploma`, which destroys it when the
inventory refuses it). The count grows with spawned townies.

## §11 NPC horses in Late Night apartment units

[verified, decompiled] How vanilla gets there:
- `LotManager.SelectRandomLotForNPCMoveIn` (MoveToLargerHouse, MoveToSmallerHouse, MoveInHousehold) skips only
  `IsApartmentLot` (the EP9 University subtype) and dorms. It has no horse or box-stall rule.
- MergeHouseholds and MoveOutHousehold keep horses with their household.
- `AddHorse` alone requires a box stall per horse.
- Stairs refuse horses, puppies, kittens and wild animals. `ElevatorDoors` refuses horses, toddlers and wild animals.

So a horse household can be moved into a tower unit that only stairs or an elevator reach.

[measured] In 1335, Ariel stood idle on its own unit lot for over 2000 sim-min. Our stuck watchdog had moved it (it was
then on a floor tile).

`PetHomeCensus("list")` (read-only) prints, for every pet:
- its home lot: box stalls, elevators, shells, front-door level;
- where the pet stands: lot, level, room;
- its GoHome plans, made as EA makes them.

A fix waits for that data.

The spawner's `!IsApartmentLot` rule does not exclude Late Night towers. It should prefer a lot with a box stall, else
exclude lots with `ElevatorDoors`, `BuildableShells` or `GetLevelOfOutsideDoor() > 0`.

## Tests

- `routecauselog` on (log only): `routecause:` lines; counters 2709 / 2710.
- synctime on: failed `syncwait:` lines end with `own exit ..., partner exit ...`.
- Leak census: `events.dream_listeners.by_trigger` and `subject_gone_by_trigger`.
- `Ts3Fixes.Fixes.PetHomeCensus list` in a world with pets, and in Bridgeport for the horse units.
- Checks: `RouteCauseLogCheck run` (off: BROKEN, on: FIXED), `PuddleReachCheck run` (docs/578 §3).
