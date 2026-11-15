# 564: Daily-life logic batch (service NPCs)

2026-11-15, static (decompiled EA scripts plus semgrep `tools/static/ea-gameplay-logic.semgrep.yml` /
`ea-script-logic.semgrep.yml` over 377 files: school/homework/carpool, cooking/fridge/serving, beds, bathroom and privacy,
maid/repairman/repo man, bills, phone/visits; then reads of the state machines). Built, NOT RUN. Off by default; EA bug
fixes on service NPCs only.

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `serviceleave` | 2517-2521 | `Ts3Fixes.Fixes.ServiceLeaveCheck` | `service-leave-reset` |

## 1. Service NPC timeouts lost on a reset; repo man with no mailbox (`serviceleave`, ServiceLeave.cs)

Verified in the decompiled scripts.

### (a) Timeouts deleted by a worker reset

Service states arm their one-shot timeouts as `AlarmType.DeleteOnReset` alarms, owned by the worker:

| State | Callback | What it does |
|---|---|---|
| `WaitToRoute` (generic, Maid, Repairman, Pizza, Repoman, Babysitter) | `TimeToRoute` | arrive |
| `HangAroundBeforeLeaving` (generic, Maid, Repairman, Pizza, Police) | `TimeToRoute` | leave |
| Firefighter wait | `TimeToGo` | leave |
| Repairman `Fixing` | `TimeUp` | stop after `MaxFixingTime` |
| Maid | `TimeToFinish` | stop after her cleaning time |

When a service Sim is reset (EA's stuck reset, or a reset from any mod), `Sim.DoReset` runs `base.DoReset` first.
`GameObject.DoReset` then calls `AlarmManager.OnReset(this)`, which deletes those alarms. Only after that does
`SituationComponent.OnReset` call `Situation.OnReset`, which no service situation overrides; only the repo man re-arms
his own alarm. The state then waits for an alarm that no longer exists. Player-visible results:

- a requested service that never comes, and cannot be requested again while it is "on its way";
- a maid or repairman who never stops while one dirty or broken object is out of reach;
- a service Sim who stays on the lot for good after finishing the job.

Fix: after a worker reset, the current state's timeout is armed again the way EA arms it: same callback, same alarm type
and owner, and EA's tunable delay. The delay comes from the state's `Tuning` field or the service's static
`DelayBeforeArriving` / `DelayBeforeLeaving` / `MaxFixingTime`. The maid's finish time is computed from her start time
and `Maid.MinCleaningTime`, at least 15 minutes, and only while she is still in a working state, because EA never clears
that handle after it fires. A state with no delay found is left as EA has it and counted (TimeTraveler,
ResortMaintenance); Babysitter / Butler are covered in §2. Bartender situations are left alone because another fix family owns them.

### (b) Repo man on a lot with no mailbox

`RepomanSituation.Leave` walks the repo man out through the lot's mailbox. When `Mailbox.GetMailboxOnLot` returns null,
it pushes nothing, so he never drives away. This happens on a lot whose mailbox was deleted in edit town, or a lot type
that `GetMailboxOnLot` skips. Fix: with no mailbox, EA's next step (`OnRouteOutOfHouse`: drive away, then Exit either
way) runs at once.

Counters: 2517 wait/leave re-armed, 2518 work limit re-armed, 2519 repo man with no mailbox, 2520 no delay found,
2521 errors.

## 2. Babysitter re-arm and repo-man no-op states (`serviceleave`, ServiceLeave.cs)

### (a) Babysitter and butler after a reset

`BabysitterSituationBase` (Babysitter, Butler) arms its own `DeleteOnReset` alarms, so a reset of the sitter loses them
the same way as in §1:

- `WaitToRoute`: `mAlarmHandle` / `TimeToRoute` after `parent.DelayBeforeArriving()` (a virtual method: Babysitter or
  Butler tuning), so the sitter never arrives.
- `ParentsAreStillHome`: a 3-minute repeating `CheckForParentsLeave`, and for a hired sitter `TimeToLeave` after
  `max(EndTime - hour of day, MinBabysittingTime)`.
- `ParentsHaveLeft`: a 3-minute repeating `CheckForParentsReturn` and `TimeToHunkerDown` after `ParentsAwayForRealTime`.
- `BabysitUntilParentsReturn`: a 3-minute repeating `CheckForParentsReturn`.

Without these the sitter never notices the parents leave or come back and never leaves. Fix: the §1 re-arm also covers
these fields, with EA's own expressions (the arrival delay calls the situation's `DelayBeforeArriving()` directly, which
is not swapped) and EA's 3-minute repeat. A handle EA never armed (live-in butler `TimeToLeave`) stays unarmed.

### (b) Repo-man states that push nothing

`RepoWhileRoutingThrough.Init` pushes the repossession only when the service is the repo man, the lot has a household and
there is an object; `RepoTeleport.Init` only with a household and an object. Otherwise nothing ever moves the situation
on and the repo man stands on the lot for good (the household moved out mid-visit, or the object was deleted). Fix:
after EA's Init, in exactly those cases and only while that state is still the current one, the repo man leaves through
EA's `SetToLeave`.

Counters: 2522 babysitter periodic checks re-armed (arrival / leave / hunker-down count in 2517 / 2518), 2523 repo-man
no-op states left; errors in 2521.

## Checked, not fixed

- Semgrep `reservation-left-on-exit` on the bathtub pet baths and pet `EatOffFloor` are false positives: `Cleanup`
  removes the use-list entry. The PicnicBasket `BeingCleaned` path is not confirmed as stuck in normal play: an
  interrupted clean-up is reset by `PicnicBasketSituation.AsyncCleanup` or by the basket reset.
- `PrivacySituation`: the lot counter and callbacks are non-persisted, and `PostLoad` re-registers them once.
- Visits: the `VisitSituation` timeouts are `AlwaysPersisted`, so a reset does not lose them.

## Leftovers (recorded, not fixed)

- Repo man `RepoWithRegularRouting` during a fire with `mbRescheduledVisit` set does nothing, and nothing re-checks
  after the fire.
- Phone invitations: `InviteToLotSituation` (meet-up) has persisted timeouts and retry limits on each step; no hang
  found. Phone "invite over" (`VisitSituation`) not read further.
- Not reached in this pass: school/homework/carpool beyond docs/560, beds, fridge/leftover spoilage, group meals,
  autonomy idle picks of unreachable/reserved objects, phone invite-over arrival.
