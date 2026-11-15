# 555. EA script logic-bug audit: rule scan of the gameplay assemblies (2026-11-14)

Static audit of `Sims3GameplaySystems` and `Sims3GameplayObjects` (local decompile, 8,729 files) for code that will
probably cause logic bugs. Every candidate below was read in context; tuning values come from the game's XML tuning
resources (GameplayData + every EP FullBuild/DeltaBuild package, instance id = FNV-64 of the class name). No game run.
Prior finds of the same families: `shuffleboardbcast`, `cookclicklistener` (docs/546 §17).

## 1. Method and rules

`tools/static/ea-script-logic.semgrep.yml` (our own rules; rerun command in its header, ~30 s):

| Rule | Raw hits | Real | Notes |
|---|---:|---:|---|
| `broadcaster-overwrite` (field = new ReactionBroadcaster without a dispose first) | 166 | 0 new | joined with the tuning `TimeDuration`: 110 never expire (-1); read every one created outside Run/Init: disposed or `EndBroadcast` in Cleanup, or per-instance once |
| `listener-overwrite` (field = EventTracker.AddListener without RemoveListener) | 86 | 2 (§4, §5) | |
| `foreach-modify` (Remove/Add/Clear on the list being iterated) | 3 | 1 (§3) | AlarmManager and AdjustTraitChipModel break right after the change |
| `self-compare` / `same-cond-else-if` / `same-branches` / `ternary-same` | 219 / 35 / 38 / 64 | 2 (§6; row 6) | semgrep's C# literal matching is loose: most hits are `x == null` lines or different literals |
| `null-check-after-deref`, `self-assign`, `float-exact-compare` | 17 / 39 / 5 | 1 (§7) | self-assign hits are chained assignments |

Extra one-off scans (scratchpad scripts, method recorded here): `+=` event subscriptions with no `-=` in the same file
(114 hits), and flags set `true` in an interaction's Run without a reset in Cleanup (67 hits). Read: beds
(`ClaimOwnership` += is balanced by `RelinquishOwnership` and `Bed.Dispose`), BoxStall clean (reset in `finally`),
band instruments (inherited Cleanup clears it): no bug.

## 2. Ranked findings

| # | Where | What | Impact / frequency | Verdict |
|---:|---|---|---|---|
| 1 | `ResortManager.RefreshVIPRooms` | removes an empty VIP room inside `foreach (VIPRooms)` | exception; later rooms keep stale locks; closing a resort stops before guests are sent home; rare (EP10 resorts, a VIP door gone outside the door-delete hook) | **fixed: `viproomrefresh`** (§3) |
| 2 | `AcademicCareer+CourseCreationSpec` | rabbit-hole-disposed listener never removed | ~5 listeners per university term per student; a later rabbit-hole delete shows "course location destroyed" for an old course | **fixed: `courselistener`** (§4) |
| 3 | `StellarObservatory` (Simulon V trip) | `num3 > 0 \|\| num3 > 0` (should test stardust `num2`) | stardust-only find shows the "found nothing" notice, items still added | **fixed: `observatorymsg`** (§6) |
| 4 | `DynamicChallengeManager.OnWorldLoadFinishedHander` | same field assigned twice; world quit nulls it without RemoveListener | lost handle only: no leak (§5) | no fix needed |
| 5 | `BubbleBar` smoke interaction Run | null check of the pipe prop after `SetHiddenFlags` on it | NRE only if prop creation fails | **fixed: `bubblepipeguard`** (§7) |
| 6 | `CASLogic.SetupSupernaturalData(bool)` | `CurrentOccultType != CurrentOccultType` | private overload with no callers: dead | no impact |
| 7 | `PerformanceMeter` | `num3 < 0 ? num3 : num3` clamp no-op | input already clamped to >= 0 | no impact |

Checked and not bugs: `GnubbField+Play.OnHit` and `HorseJump+DoJump.TriggerReactions` overwrite their broadcaster
without Dispose (shuffleboard shape) but their tuning `TimeDuration` is 2 s, so the lot drops them; `Urnstone` heartbroken
broadcaster (-1, never ended) only reacts for Sims heartbroken over that grave: intended; `Medical.OnLoadFixup`,
`Job.PersistPostLoad`, `SocialInteraction` sleeping listener (removed in Cleanup).

## 3. viproomrefresh [verified, decompiled; built, NOT RUN]

`RefreshVIPRooms` visits each VIP room: clears its guest list when the owner is gone or no longer checked in, relocks
its doors, drops door ids with no object, and when none is left calls `VIPRooms.Remove(room)` inside the foreach. The
enumerator's next `MoveNext` throws `InvalidOperationException` (Collection was modified). Callers: the `Closed` setter
(after evictions; the throw skips sending non-staff Sims home), check-out (after the guest was dropped), and the resort
import in `ResortFrontDesk`. The normal VIP-door delete path removes ids by index outside any foreach, so the throw
needs a door that vanished another way.

Fix `VipRoomRefresh.cs`, tuning `kVipRoomRefresh` (off): `RefreshVIPRooms` is swapped (stub `RefreshOrig`); with the
switch on and a room with no door left, EA's loop body runs unchanged over `VIPRooms.ToArray()` (same rooms, same
order, same calls, the empty room removed from the real list). Exact for every Sim. Counters 2417 calls where EA would
throw (also off), 2418 rooms removed, 2419 errors. Check `VipRoomCheck run`: a ResortManager from its private
constructor (no lot, nothing registered) with two door-less VIP rooms; off BROKEN (exception, 1 of 2 rooms left), on
FIXED (no exception, 0 left, 2418 +2). Repro `vip-room-refresh-modified` (any world). Swap-stack row: no yield.

## 4. AcademicCareer course listeners [verified, decompiled]

`CreateCoursesSchedule` (career start) makes 4-5 `CourseCreationSpec`s and each adds a `kEventObjectDisposed` listener
on its rabbit hole. The listener is removed only when it fires. Nothing removes it when the term ends or the career is
left, and the specs pin the `AcademicCareer` (and its owner). If that rabbit hole is ever disposed, `CourseInvalidated`
shows the "course location destroyed" notice (and can show "forced to leave university" when the count is 0) for a spec
that is no longer scheduled. Same code: a level-up moves generic courses to another rabbit hole (`ChangeRabbitHoleTo`) but the listener stays on the
old building, so deleting the class's new building never cancels the course. The leak lasts the world session (the
EventTracker is rebuilt per world, §5; the listener field is not re-added after load).

**courselistener [built, NOT RUN]** (`CourseListener.cs`, tuning `kCourseListener`, off): EA's `RemoveListener` for every
scheduled course in LeaveJobNow's `RemoveStudent` step (its only caller; LeaveJobNow itself is invoked by reflection in
`LeaveJobDispose.cs`, so it is not swapped) and before `CreateCoursesSchedule`; `ChangeRabbitHoleTo` re-adds a moved
course's listener on the new rabbit hole with EA's `SetRabbitHoleDisposedEventListener`. Counters 2420 removed, 2421
moved, 2422 stale moves (also off), 2423 errors. Check `CourseListenerCheck run` (throwaway uninitialized course spec and
career on two world rabbit holes; listener removed at the end): off BROKEN, on FIXED. Repro `course-listener-leak`.

## 5. DynamicChallengeManager listeners [verified code, impact inferred]

`OnWorldLoadFinishedHander` stores the `kHouseholdSelected` listener and then overwrites the field with the
`kPassportSentSimReturned` one; `OnWorldQuitEventHandler` sets the field to null without `RemoveListener`. Checked: `EventTracker.PreWorldStartup` makes a new
tracker per world and `PreWorldShutdown` drops it [verified], and the manager is a singleton subscribed once at startup,
so only the handle is lost: no leak, no fix.

## 6. StellarObservatory notice [verified, decompiled]

After the Simulon V trip, `num2` stardust and `num3` dark matter are found and inventoried, then the notice tests
`num3 > 0 || num3 > 0`: a stardust-only result shows the "found nothing" message. Items are not lost.

**observatorymsg [built, NOT RUN]** (tuning `kObservatoryMsg`, off): `Sim.ShowTNSIfSelectable(TNSNames, actor, target,
params)` turns `SummaryOfNothingFoundOnSimulonV` (only the observatory sends it) into `SummaryOfEverythingFoundOnSimulonV`
when the stardust argument is above 0. Counters 2424 corrected (also off), 2425 errors. Check `ObservatoryMsgCheck run`
(pure notice choice): off BROKEN, on FIXED. Repro `observatory-stardust-msg`.

## 7. BubbleBar [verified, decompiled]

The pipe prop is used (`SetHiddenFlags`) before its null check, so the check's StandardExit path is dead and a failed
prop creation throws instead of exiting cleanly. Only on a missing resource.

**bubblepipeguard [built, NOT RUN]** (tuning `kBubblePipeGuard`, off): `BlowBubbles.Run` wrapped; a
NullReferenceException with the pipe still null and the state machine entered (the only point between EnterStateMachine
and the pipe's first use) takes EA's `StandardExit` and returns false; anything else is rethrown. Counters 2426 exits,
2427 errors. Check `BubblePipeCheck run` (throwaway uninitialized interaction; must decline before the state machine):
off BROKEN, on FIXED. Repro `bubblebar-pipe-null`.
