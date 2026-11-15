# 565: Daily-life logic batch 2 (visits, field trips, autonomy, spooning, homework help)

2026-11-15, static (decompiled EA scripts; `tools/static/gameplay-scan.sh` + Infer# report filtered to visits, school,
homework, beds, cooking/serving, interaction queue; then reads of each system's Run / Cleanup / alarm paths). Built,
NOT RUN. All three switches off by default; EA bug fixes only. Scope picked from docs/564 "Leftovers".

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `visitgreetalarm` | 2524-2526 | `Ts3Fixes.Fixes.VisitGreetAlarmCheck` | `visit-greet-alarm` |
| 2 | `fieldtripadult` | 2527-2529, 2533, 2540 | `Ts3Fixes.Fixes.FieldTripAdultCheck` | `field-trip-adult` |
| 3 | `autonomyfailblock` | 2530-2532 | `Ts3Fixes.Fixes.AutonomyFailBlockCheck` | `autonomy-fail-block` |
| 5 | `visittoolate` | 2534-2535 | `Ts3Fixes.Fixes.VisitTooLateCheck` | `visit-too-late` |
| 6 | `spoonreset` | 2536-2537 | `Ts3Fixes.Fixes.SpoonResetCheck` | `spoon-reset` |
| 7 | `homeworkhelp` | 2538-2539 | `Ts3Fixes.Fixes.HomeworkHelpCheck` | `homework-help-jig` |

The checks evaluate the fix decisions with controls (off prints BROKEN, on FIXED) and report live, read-only state; none
of them changes the world.

## 1. Leaked "waiting to be greeted" alarm (`visitgreetalarm`, VisitGreetAlarm.cs)

[verified, decompiled] `VisitSituation.VisitorWantsToComeIn.Init` arms `mWaitForGreetingHandle` (1 h, `AlwaysPersisted`,
owned by the guest) when an NPC host is pushed `InviteInOrRefuseEntrance`. If that fails, `InvitedInFailed` re-runs
`Init` up to `kInviteInNumRetries` (3) times, 2 sim-minutes apart, and each run overwrites the handle. `CleanUp` removes
only the last alarm; `AlarmManager.OnDispose(situation)` does not touch alarms owned by the guest. One hour after the
first try, a leaked alarm runs `WaitedTooLongForGreeting` on the dead state object, whose readonly `Parent` is still the
live visit:

- visit now `Socializing`: switched to `GuestLeavesAfterWaitingTooLong`, so an NPC guest who was let in walks home, and
  a selectable guest loses the hosts' wish to talk to them;
- visit already ended: a new child state is set on the exited visit and its NPC guest is sent home from wherever it is.

Only NPC hosts take the retry path (a selectable host never fails `InviteInOrRefuseEntrance` this way), so this hits the
player's Sim visiting an NPC home and NPC guests there. The leaked alarms are saved with the game.

Fix: a re-run `Init` keeps EA's first alarm while it is pending (the one that fires first in vanilla too) and drops the
new one; `WaitedTooLongForGreeting` does nothing when its state is not the visit's current child or the visit has ended
(covers alarms already in saves). Live-state timing is unchanged.

## 2. Field-trip chaperone search (`fieldtripadult`, FieldTripAdult.cs)

[verified, decompiled] `FieldTripSituation.BringAdultToSchool`, with no NPC educator at work on the school lot, runs
`while (list.Count > 0 && mAdult == null)` over a copy of `LotManager.Actors`, picking a random Sim and keeping it only if
`FindRandomAdultTest` passes. A rejected Sim is never removed, so when nobody passes (every instantiated NPC adult at work
or in `mAdultsThatFailedToShow`, which grows after each chaperone route failure: `OnAdultRouteFail` calls the search
again) the loop never ends and the game freezes on the school day of a field trip. Fix: before EA's search, if neither
EA's educator test nor its adult test can pass for anyone, the trip ends with EA's own `Exit` (what EA does when no adult
is found), so the loop is never entered with no candidate.

[verified, decompiled] `FindRandomAdultTest` checks NPC, not teen-or-below, not at work, not failed before. Adult pets
and horses, and service NPCs on the job (repairman mid-repair, maid, mail carrier) pass; EA then cancels their queue,
maxes their motives and walks them to school as chaperone. Fix: such Sims fail the test (human and not performing a
service), counted.

[verified, decompiled] `OnParticipantDeleted`: when the chaperone is deleted EA calls `Exit()` (whose `CleanUp` sets
`mAdultsThatFailedToShow = null`) and then reads that list: a NullReferenceException out of
`Situation.NotifyParticipantDeleted` during the Sim's deletion. Fix: return after `Exit`; calls on an exited trip do
nothing. Tidy in the same path: `RouteAdultToSchool.Init` calls `Exit()` with no lot and still runs
`BringAdultToSchool`; that call now does nothing on an exited trip.

[verified, decompiled] (§4 follow-up) `TriggerCarpoolHome`, called from every participant's rabbit-hole loop in the
last 15 minutes of school, sets `mTriggeredCarpoolHome` and then books the bus to `PlumbBob.SelectedActor.LotHome`. With
no selected Sim at that tick it throws out of the loop callback: that Sim's trip ends early (no `AttendedFieldTrip`, no
`kWentOnFieldTrip`) and, the flag being set, nobody is bussed home; a homeless selected household books the bus to a null
lot. Fix: with no selected Sim while a selectable participant has a home, the call waits for the next tick (EA calls it
every tick until school ends); with no selectable participant to bus (EA's `SimsGoingHome` would return nobody) or no
home, the bus is skipped and the flag set as EA would. The chaperone notice NRE in `InRabbitHole` (reads
`mFieldTripRabbithole` after nulling it) is confirmed unreachable: it needs a selectable chaperone and both EA chaperone
tests require `IsNPC`; not changed.

Counters: 2527 trips ended with no possible chaperone, 2528 unfit chaperones skipped, 2529 deleted-chaperone or
exited-trip calls handled, 2533 errors, 2540 carpool-home calls with no selected Sim / no home (counted per tick while
waiting).

## 3. No failure block after an unreachable posture (`autonomyfailblock`, AutonomyFailBlock.cs)

[verified, decompiled] EA's autonomy failure memory is `GameObject.UpdateBlockTime` (a 1 sim-minute hard block in
autonomous tests, then a score penalty that fades over 30 sim-minutes; docs/04a §3). `InteractionQueue.ProcessOneInteraction`
clears the Sim's exit reasons at the top of each posture-transition try. When the posture graph returns no transition
(no reachable or free part: every seat taken or cut off) or keeps offering the same one, the loop ends with no exit
reason, the posture check fails and `OnValidInteractionFailed` blocks the target only if `ShouldUpdateBlockTime` sees an
`ExitReason.CausesBlocking` reason, which this path never sets. The object stays at full score and the Sim picks it again
at the next autonomy pass (an NPC whose last autonomous action failed runs autonomy again at once,
`Sim.CanRunAutonomyImmediately`). Route failures inside a transition set `RouteFailed` and are blocked by EA already.

Fix: in `OnValidInteractionFailed`, before EA's body, for an autonomous interaction with a valid, non-Sim target, not
cancelled, whose Sim has no exit reason at all, and whose `Test()` still passes, EA's `UpdateBlockTime` is called on the
target. Player-directed actions are untouched (EA clears the block for them). Frequency in play [inferred]: not measured;
the repro is the decision check, a live A/B needs a lot with an unreachable table.

Counters: 2530 blocked, 2531 failures left to EA (exit reason set, test failed, Sim target), 2532 errors.

## 4. Checked, not fixed (leftovers)

- Visits: `GuestLeftAlone` and the arrival / leave alarms are `AlwaysPersisted` and removed in `CleanUp`; the
  constructor arms its two alarms before `mGuest` is set (owner null, harmless: removed in `CleanUp`). Phone invite-over
  (`CallInviteOver.GetSimToLot`): `SetInvitedOver` is time-bounded, failed queue adds show EA's dialog; no hang found.
  Too-late guests: fixed in §5.
- Homework: `GetHelpWithHomework` waits `while (HelpingSim != null) Sleep(0)` with no cancel test (helper's `Cleanup`
  clears it, so it ends). `HelpWithHomework.Cleanup` passes a null child to `RemoveRelationshipUpdate` when the child's
  Sim is gone [inferred, not read further]. `CopyHomework` leaves the copied homework on the floor (by design). Null jig:
  fixed in §7.
- Field trips: `RabbitHoleFieldTrip.ConfigureInteraction` reads `mFieldTripSituation` with no null check (only when the
  trip already ended before the interaction started) [inferred rare]. Carpool / chaperone notice: §2.
- Beds: spooning partner reset: fixed in §6. Normal wake / cancel paths end the pair in every path read.
- Meals: `GrabAServing` use-list entries are released by `CarrySystem.PutDown`; `CallToMeal` pushes `AddNext` per Sim
  with no failure path (by design). `PreparedFood.ImportContent` restarts spoilage from the full time for imported food.

## 5. NPC visitors left in the yard all night (`visittoolate`, VisitTooLate.cs)

[verified, decompiled] `CheckIfItsTimeToLeave` sets `TimeForGuestToLeave(TooLate)` once `IsTooLateToVisit()`. Both host
paths only move the guest's household to the yard (selectable host: `MakeGuestHouseholdExitToYard` -> `GoOutside`; NPC
host: `TurnToFaceAndAskToLeave` with `GoWhere.LeaveHouse` -> the same call). The state's 15-minute `RemindGuestsToLeave`
then handles "all outside" only when it is not too late (back to `ArrivedOnLot`); all outside and too late does nothing.
While in that state `CheckIfItsTimeToLeave` returns at once, and `CheckForArrivingAndLeaving` ends the visit only when the
guest is off the lot. Nothing in `VisitSituation` sends the guest home, and the visit's `OutsideLot` check scores every
off-lot action down [inferred: that autonomy then keeps them on the lot; not measured], so the NPC loiters in the yard
until morning. Fix: after EA's reminder, while that state is still current, an NPC guest (not selectable, not a service
Sim, on the lot, not already going home) whose household is all outside when it is too late is sent home with EA's
`Sim.MakeSimGoHome`, the step EA's `GuestLeavesAfterWaitingTooLong` takes. Selectable guests are untouched.

## 6. Spooning pair kept after the partner was reset (`spoonreset`, SpoonReset.cs)

[verified, decompiled] `BedSleep.LoopHandler` pairs two romantic partners in one bed (master / slave links on both
interactions); the master puts both posture machines into the spoon states and `FinishSpooning` puts them back to
`Sleep` and calls `ClearSpooningContext`. Only `BedSleep.Run`'s post-loop code and the master's loop ever end a pair. A
partner whose interaction ends without it (reset, deleted, aborted) leaves the other in the pair: a lone slave returns
from its loop callback at once every tick (`if (mSpooningSlave) return`), so it stays in the spoon pose and skips EA's
light-sleeper wake, `TiredFromMoving` / `NoMonsters` timers and the evil-gnome wake all night; a lone master calls
`FinishSpooning`, which requests a state on the gone partner's posture machine. Fix: in `LoopHandler`, before EA's body,
when the partner's `BedSleep` is gone (null, destroyed, or not in its queue: EA's own test in the slave's wake-up wait),
this Sim's side ends as `FinishSpooning` ends it (spoon machine `ToSleep` for the master, own posture `Sleep`, sync data
cleared, EA's `ClearSpooningContext`). Pairs with a live partner run EA's code unchanged. Cost: two field reads per sleep
tick while on.

## 7. Homework helper after the child finished (`homeworkhelp`, HomeworkHelp.cs)

[verified, decompiled] `GetHelpWithHomework.Run` queues `HelpWithHomework` on the helper with `AddNext` and, when its own
loop ends, destroys the two-person jig and nulls `Homework.HomeworkTwoPersonJig`. A helper who starts only then runs
`Target.HomeworkTwoPersonJig.RouteToJigA(Actor)` (Test always passes): NullReferenceException out of `Run`, a script error
on the helper, often a household parent; with the child's Sim gone, the next line throws the same way. Fix: with no jig or
no child Sim, `HelpWithHomework.Run` returns false before EA's body (the outcome EA's own "child is not waiting" check
gives); with a jig EA's code runs unchanged.
