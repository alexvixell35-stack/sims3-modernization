# 570: Ambitions active careers, logic pass (firefighter, maintenance, ghost hunter, stylist, interior designer)

2026-11-15, static (EA code read in `game-copy/decompiled`, local only; NRaas source cited, not copied). Built, NOT RUN.
Scope: the firefighter emergency code (`ActiveFireFighter`, `FireFightingJob`, `RabbitholeFirefightingJob`,
`RabbitholeRescue`, `RespondToEmegency`, `FirefighterEmergencySituation`), the firefighter service (`Firefighter`,
`FirefighterSituation`), the fire-gear maintenance component (`MaintenanceComponent`, `FireEngine`, `FireStationAlarm`),
then ghost hunter, stylist and interior designer. Already covered elsewhere and not redone: `awardkeep` (docs/546),
rabbit-hole rescue at combo lots (docs/551 row 21).

## 1. `fireactivejob`: the current emergency survives a world quit (fixed, off by default)

**[verified, decompiled]** `ActiveFireFighter.sActiveJob` is a plain static (no `[PersistableStatic]`). It is set by
`FireFightingJob.MakeMapTagForRandomJobVisible` (and the earthquake alarm) and cleared only by that job's `Cleanup` /
`OnCompletion`. Nothing clears it on world quit; the career's `OnDispose` does not end jobs, and
`CareerManager.CleanUpAnyStaticData` (which clears the Stylist / Interior Designer / Singer listener statics) has no
managed caller and does not touch it either. Statics keep their values across world loads [inferred, docs/192 §3; EA
marks the statics it wants saved or reset with `PersistableStatic`].

What the stale value does in the next world [verified, decompiled]:

- `FireFightingJob.PersistPostLoad` adopts a loaded emergency only `if (sActiveJob == null && MapTagEnabled)`, and
  `OnLoadFixup` subscribes the job tracker only if `sActiveJob == this`: a saved emergency is not adopted.
- `ActiveFireFighter.CurrentJob` returns `sActiveJob` for every selectable firefighter, so `IsWorkHour` is true at
  any hour ("On the job"), `RespondToEmegency` / `ExtinguishBase.Init` read a lot from the old world.
- `MakeMapTagForRandomJobVisible` starts with `if (sActiveJob != null) { LeaveJob(kDebug); return; }`: every new
  emergency is dropped. The firefighter gets no calls until the game is restarted.

Reach: an emergency announced (alarm ringing or crew on the way) when the player loads a save (the same or another),
quits to the menu and loads, or travels.

Fix (`FireActiveJob.cs`, tuning `kFireActiveJob`, counters 3953-3955): on world quit, `sActiveJob = null` (what the
job's own `Cleanup` does). At world load finished, a value that is not a `FireFightingJob` of any Sim in this world is
dropped and EA's `PersistPostLoad` / `OnLoadFixup` rule is replayed for the loaded job with its map tag on. A live
emergency is never touched. Check `FireActiveJobCheck` (`FireActiveJobChecks.cs`), repro `fire-active-job-stale`:
`run` puts an unregistered `FireFightingJob` through the drop decision (off `verdict: BROKEN`, on `verdict: FIXED`);
`result` also says whether `sActiveJob` is null, live or STALE right now. End to end needs a real emergency: announce
one, save, load, wait for the next call (off: none comes; on: 2570 or 2571 +1 and calls resume).

## 2. Leads checked, not fixed

| # | Where | Finding | Verdict |
|---|---|---|---|
| 1 | `RespondToEmegency.Run` | the driver waits for passengers with `while (any passenger still has GetInCar queued) Sleep(0)`, no timeout, and the driver is `CancellableByPlayer = false` | [inferred] could hang if a passenger's current interaction never yields; no run-log evidence; a fix means rewriting EA's whole Run. Lead only |
| 2 | `RespondToEmegency.Run` | `if (!(Actor.Occupation is ActiveFireFighter))` is always false there (cast earlier), so the car-route fallback always runs | harmless |
| 3 | `FirefighterEmergencySituation.OnSimLotChanged` | last firefighter leaving calls `ActiveFireFighter.sActiveJob.RestartTimer` (static, may be null) instead of `Job.RestartTimer` | [verified] NRE only if the situation outlives `sActiveJob`; with §1 that happens only after a load; covered by §1 |
| 4 | `FirefighterEmergencySituation.FirefighterAddedToLot` | `Occupation?.CurrentJob.RestartTimer`: `CurrentJob` is null for a visually pregnant firefighter; the constructor (unlike `OnSimLotChanged`) does not exclude pregnancy | [verified] NRE aborts the situation's constructor (emergency setup fails) when a pregnant household firefighter is on the lot at setup; rare. Fixed by `firenre` (docs/867), off by default |
| 5 | `FireFightingJob.CleanUpJob` (LargeFires) | sets every locked non-front door on the lot to Anybody, not only the doors the job locked (`LockDoorsInRoom` skips doors already locked) | [verified] player lock settings on the victim's home are lost; NPC lots only. Fixed, `firedoorkeep` (docs/864, default off) |
| 6 | `FireFightingJob.OnCompletion` | `(sim.Occupation as ActiveFireFighter).StartGratefulSimAlarm` with a Sim that left the career mid-call | [verified] NRE on the 60% thank-you roll when the pick has no fire career; [inferred] rare. Fixed by `firenre` (docs/867), off by default |
| 7 | `Firefighter.NeedsAssignment` / `FirefighterSituation.CleanUp` | `HasBeenRequestedOnce` is reset only by `ChargeForService`; a service firefighter who exits another way (fired, reset, route failure) leaves it set, and `num2 == 0` keeps the lot "needing" one | [verified] one extra (phantom) firefighter visit to a lot with no fire; it then charges if the request was not valid. Minor; lead |
| 8 | `ActiveFireFighter.OnSetupWorkDayAlarms` | tests `mRegularWorkDayEndHandle` before removing the two-hour handle | base always sets the end alarm: redundant, not a leak |
| 9 | `ActiveFireFighter` return-to-station / respond / grateful alarms | added on the Sim or `Global` with owner; all removed in `CleanUpFirefighterAlarmHandles` / `LeaveJobNow` | ruled out |
| 10 | `FireFightingJob.SetupTasks` | `item.LotCurrent.LotId` for every active household Sim | null only for a Sim with no lot; not seen |
| 11 | `GhostHunterJob.SetupTasks` | repeating `VerifyConsistency` alarm, handle not kept | removed through `AlarmManager.Global.OnDispose(this)` in the job's cleanup: ruled out |
| 12 | `Stylist.sTryCreateLowLevelStylistJobAlarm` | one static `[PersistableStatic(true)]` handle (loaded from the main menu only) shared by all stylists; day end removes by that handle | [inferred] after travel the handle can belong to the previous world; lead |
| 13 | `InteriorDesigner.sActiveRenovation` | static, subscribes `LotManager` build/buy events | set only during the renovation interaction, cleared in its `Cleanup`; rare across a quit. Lead |
| 14 | `Stylist.MakeoverSituation`, ghost hunter situations | `OnLotChangedEvent` += in ctor/PostLoad, -= in Dispose | matched; ruled out |

## 3. Fire-gear maintenance (`MaintenanceComponent`, item 1b)

Owners: `FireEngine` and `FireStationAlarm` only (grep over all decompiled assemblies; also read by
`FireFightingJob` and the D&P / challenge delegates).

- Decay: a global repeating alarm every 10 sim-minutes (`kTimerRepeatMinutes`), each pass `kPercentDecayPerHour / 6`
  = 3 %/h as tuned. Matches the comment [verified].
- Decay gating: `FireEngine` decays only while the active household has a Firefighter whose job lot or home lot is
  the engine's owner lot; otherwise the level is set to 100. `FireStationAlarm` decays whenever any household Sim has
  the Firefighter occupation, and all alarms in the world share one level (decay of the first, copy to the rest;
  `MaintainLoopCallback` copies to all; `OnStartup` copies from the first other alarm in the list) [verified]. Design,
  not a bug.
- Full maintenance: the loop sets the level to 120 (100 + `kFullMaintanceAddition`), and the interaction is offered
  only below 100, so +20 buys ~6.7 h before it can be maintained again. Effects are clamped at 100
  (`MathHelpers.LinearInterpolate` clamps): engine speed/acceleration multipliers, the job's alarm delay (`TimeTaken` +
  up to 15 min at level 0, a worse grade), the poorly-maintained alarm effect below 50, and below 40 the engine / alarm
  upgrades are greyed out. No career metric reads it directly [verified].
- `MaintainObject` on a `FireEngine` needs the engine parented to a `ParkingSpace` (driveways are `ParkingSpace`).
  An engine kept in a Sim's inventory (where `LeaveJobNow` and `GetVehicle` put the player's engine) cannot be
  maintained but still decays if its `mOwnerLot` is the home lot [inferred: `mOwnerLot` is not reset on inventory
  moves]. Lead: check in game (engine in inventory, level after a day).
- Component list: `sAllMaintenceComponents` is a static list; components add themselves in `OnStartup` and remove in
  `Dispose`; `PostWorldShutdown` (reflection-called, not hooked) removes the alarm but does not clear the list. If
  world shutdown does not dispose components, old-world components stay: the list grows per load, decay runs on dead
  objects, and a loaded alarm copies its level from an old-world alarm [inferred, not verified: whether objects are
  disposed at world shutdown]. Not fixed; measure first (count of entries whose `Owner.HasBeenDestroyed` after a
  reload, via `reflect_get` on the private static).
- Level persistence: `ObjectComponent` is `[Persistable]`; `AddComponent` keeps a loaded component (no new one at
  `OnStartup`), so the level survives save/load [verified, decompiled]; the delegates in `MaintenenceData` are
  persisted with it [inferred].

No maintenance fix in this pass.

## 4. "Route tantrum" when improving the alarm right after maintaining it (user report)

Solved by the user: moving the alarm (moveObjects) made Improve work, so that alarm's placement blocked its
`RoutingSlot_0`. Why it looked fine: `MaintainObject` and `UpgradeFireAlarm` reach the alarm the same way
(`RouteToSlotAndCheckInUse(Target, RoutingSlot_0)`), but the maintenance level is shared by every `FireStationAlarm` in
the world (`MaintainLoopCallback` copies it to all, decay and `OnStartup` copy it), so a blocked alarm reads 100 % after
another one is maintained [verified, decompiled]. No fix. Side note: `ReactToAlarm` holds the alarm in use
(`StandardEntry`) for 5-15 min while coworkers stand by the parking spaces, so maintain/upgrade end with
`ObjectInUse` during an alarm [verified].

## 5. `firetrapcount`: "free N Sims" counts Sims who are not there (user report, apartment lot; fixed, off by default)

How the count works [verified, decompiled]: `Job.SetupTasks` rolls a count per task type from the job data and calls
`SetupTask` that many times; a task is added only when `SetupTask` returns true, so a failed trap (no Sim, no spot)
does not inflate the count. `FireFightingJob.SetupTask(TrappedSims)` traps one Sim per task: a household Sim at home
(`Lot.Household.Sims`), else a random NPC from anywhere in the world (not on a lot where an active-household Sim is),
`SetObjectToReset`, `Sleep(0)`, `SetPosition` at a spot from `FindPossiblePositions`, then `LockDoorsInRoom`
(TrappedRubble: same picker, no locking; earthquakes only). A `TrappedSims` task is done only by `ConvinceToRun`,
`CarryOutSim` or `CarryOutChild` (`SimCompletedTask`), a `TrappedRubble` task by digging out the pile; only `SimDied`
removes one (and fails the job when no killable teen+ trapped Sim is left). The job times out (`TimeoutOccurred`, XP
loss) when tasks stay pending.

Where the count goes wrong:

- (a) A trapped Sim who leaves the lot unfreed, or is deleted, keeps its task. `OnSimLotChanged` just removes it from
  `Victims` (and `OnParticipantDeleted` too); nothing touches the task. Ways off the lot: the reset after the teleport
  (`AttemptToPutInSafeLocation` moves a Sim whose spot fails `IsPointInLotSafelyRoutable`, e.g. inside the room
  `LockDoorsInRoom` just set to Nobody; last fallback `lot.Position`) [inferred order: the reset may run after
  `SetPosition`], a GoHome/teleport of the NPC, NPC culling. [verified code; how often: needs a run]
- (b) Apartment lots: `Lot.GetRoutableIndoorPositions` skips only public rooms when the lot has virtual residential
  slots, so trap spots include the neighbours' hidden units. EA's own `FindIsolatedFirePosition` rejects hidden and
  fogged rooms for fires on those lots; the trap spots lack that test. A hidden room's doors are locked portals for
  every Sim (docs/552 §1), and `BreakDownDoor` only opens lock types, not hidden rooms: the victim is invisible and
  cannot be reached, convinced or carried [verified code; the route check in `GetRoutableIndoorPositions` should
  reject such spots unless stairs lead in, so (b) needs a unit reachable by stairs, inferred].
- Not a cause: `OnSimLotChanged` passers-by join as `ConvincedToRun` victims and add no task. Side effect noted, not
  fixed: a passer-by whose run fails indoors (`InteractionFail`) becomes `TrappedInRoom`, and freeing them completes a
  `TrappedSims` task that belonged to a really trapped Sim (the count gets easier, not harder). Fixed by `firecount`
  (docs/868, default off), with the other count errors found next to it (a freed Sim's death, a second rubble pile).

Fix (`FireTrapCount.cs`, tuning `kFireTrapCount`, counters 3956-3959): (a) a Sim in the job's `mTrappedSims` /
`mRubbleTrappedSims` that leaves the lot or is deleted (not dead) while still `TrappedInRoom` / `TrappedUnderRubble`
has its task dropped like `SimDied` does (removed from `Tasks`, then EA's `OnTaskComplete` takes it off
`PendingTasks` and completes the job when nothing else is left); once per Sim. (b) after `FindPossiblePositions`, on a
lot with virtual residential slots, spots in a hidden or fogged room are removed (EA's fire-spot test). Hooks:
`FirefighterEmergencySituation.OnSimLotChanged` (event handler) and `OnParticipantDeleted`,
`FireFightingJob.FindPossiblePositions` (private). Household Sims: a household Sim trapped and then leaving the lot is
treated the same (EA bug fix, the job outcome only).

Check `FireTrapCountCheck` (pure): off `verdict: BROKEN`, on `verdict: FIXED`; controls for a Sim being carried out, a
convinced passer-by, a Sim still on the lot, and spots on ordinary lots. Real emergency: with cheats on, the debug
`SpawnJob` interaction (`Sims3.Gameplay.Careers.SpawnJob`, lists the firefighter's job ids for its level, creates the
job on the clicked lot) can create `LargeFires` on an apartment lot for a household firefighter [verified code; how it
shows in the menu: inferred]; `result` shows 2573/2574 after the response.

## 6. Found while fixing `firetrapcount` (2026-11-15): bare `Job` inside `namespace Ts3Fixes` is our delegate

`EaSpreadCore.cs` declares `public delegate void Job()` in `namespace Ts3Fixes`, and a name in the enclosing namespace
wins over `using Sims3.Gameplay.Careers`. So `typeof(Job)` / `(Job)x` in mod code mean the delegate [verified: compile
error in `FireActiveJob.cs`]. Effects:

- `firetrapcount` as first built: `typeof(Job).GetField("Specification")` was null, so `ApplyFireTrapCount` logged
  "not patched" and the hooks never ran (in-game check: `members found=False`). Fixed (full name); the check now
  names each missing member and its verdict needs all members found and every control holding.
- `ComboRabbitHole.cs` (`combojob`, docs/551): `ComboRescueHook.SetupHook` and `ComboGhostHook.CreateHook` did
  `Job job = (Job)(object)this` after EA's body. That cast to the delegate type throws `InvalidCastException` every
  time, and the patches are applied whatever the switch, so every `RabbitholeFirefightingJob.SetupRabbitholeRescue` and
  `GhostHunterJob.CreateRabbitHoleInvestigation` threw out of EA's method once ts3fixes was installed [verified by
  reading; the caller's handling not traced]. Also `sRhJobSpec` was null, so the effect move never ran. Fixed: an `as`
  cast to `Sims3.Gameplay.Careers.Job` and the full type name for the field lookup.
