# 568: Burglar idle, Ambitions / Bridgeport activity batch

**In-game status:** `burglaridle`, `gigwaitleave`, `sculptpose`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-15, static (decompiled EA scripts: BurglarSituation, Situation / InteractionQueue add and reset paths, FireSource
React To Fire; Ambitions / Showtime / Late Night activity code: Stylist, Singer, Firefighter and Interior Designer gigs,
sculpting, inventing, tattooing, consignment, day spa, restaurants; an alarm add/remove census of those files, then reads
of Run / Init / alarm paths). Built, proven in game (status above). All switches off by default; EA bug fixes only, using EA's own methods.

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `burglaridle` | 2556-2558 | `Ts3Fixes.Fixes.BurglarIdleCheck` | `burglar-idle` |
| 2 | `gigwaitleave` | 2559-2561 | `Ts3Fixes.Fixes.GigWaitLeaveCheck` | `gig-wait-leave` |
| 3 | `sculptpose` | 2562-2563 | `Ts3Fixes.Fixes.SculptPoseCheck` | `sculpt-pose` |

The checks evaluate the fix decisions with controls (off prints BROKEN, on FIXED); none changes the world.

## 1. Burglar stands in the house (`burglaridle`, BurglarIdle.cs)

[verified, decompiled] The docs/567 §3 guess (steal interaction not queued) is not the cause:
`Situation.ForceSituationSpecificInteraction` always returns the instance, and `InteractionQueue.InsertInteraction` calls
the failure callback (StealFailed, which re-arms the 1-minute StealAgain alarm) when the queue test fails. A Sim reset
does not strand it either: the queue reset stops entries with their failure callbacks, and the burglar's alarms live in
`AlarmManager.Global`, which a Sim reset does not clear. The real idle paths in `BurglarSituation.Stealing.Init`:

- Fire on the lot: Init only calls `FireManager.PushReactToFire(worker)`, relying on `FireSource.ReactToFire`'s tail
  (`DoHumanReactions` ends with `burglarSituation.SetToLeave()`). That tail is skipped when the reaction returns early:
  Coward or Mummy burglar (`CowardReaction`, two places), witch burglar at a witch-started fire; and never reached when the
  push is skipped or the reaction is cancelled. No alarm, no callback: the burglar stays in Stealing.
- `Lot.EffectiveHousehold == null` (household moved out while the burglar routed): Init returns at once.

There is no overall service leave timer, so the burglar stands until the player resets it.
Fix, after EA's Init while that Stealing state is still `Parent.Child`:
- no household and nothing stolen: EA's `SetToLeave` (LeaveSuccessful) at once;
- fire: a non-persisted 3-sim-minute watch (EA's CheckForSteal interval). It waits while ReactToFire is queued or
  running. Once that is gone and the state is unchanged, it calls EA's `SetToLeave`, which is what the reaction's tail does.
  It stops when EA already changed state.

Counters: 2556 no-household leaves, 2557 fire-watch leaves, 2558 errors. Reach: rare, but a stuck burglar
is very visible.

## 2. Gig customer still pushed after the gig timed out (`gigwaitleave`, GigWaitLeave.cs)

[verified, decompiled] `Stylist.MakeoverSituation.WaitAround.PushWaitIfNecessary` and
`SingerCareer.WaitForSingagramSituation.WaitAround.PushWaitIfNecessary` (5-minute repeating pulses) call
`Parent.ForceLeaveJob()` when `WaitedTooLong()` is true, then continue with no `return`. The Stylist limit is 4 h to reach the
lot and 20 h waiting; the Singer limit is 20 h. `ForceLeaveJob` shows the "cancel" TNS and runs `Job.LeaveJob`, whose
`ShutDown` exits the situation. The same pulse then still force-pushes a CriticalNPCBehavior `WaitForSim` on the
customer: the customer routes to the makeover lot (Stylist `RouteToGoodLocationForWait`) or follows the singer for a gig
that no longer exists. Its `WaitBehavior` then either runs `ForceLeaveJob` again (second cancel TNS; Stylist's
`Makeover.ShutDown` counts the job twice in `mNumJobsCreatedToday`, so fewer gigs are offered that day) or calls
`Exit()` on the finished situation before going home. `WaitBehavior` itself breaks after its own `ForceLeaveJob`; only the
pulse lacks the stop.

Fix: with the switch on and EA's `WaitedTooLong()` true, the pulse runs EA's `ForceLeaveJob()` and returns (the
first two statements of EA's body plus the missing return); otherwise EA's body is unchanged. Counters 2559 Stylist,
2560 Singer, 2561 errors. Reach: a Stylist client who cannot get to the lot within 4 h, e.g. busy at work or school.

## 3. Sculpt Sim failures leave the subject posing (`sculptpose`, SculptPose.cs)

[verified, decompiled] This is the docs/566 §4 easel defect on `SculptingStation.SculptSim.Run` (private). It queues
`PoseForSculpture` (Sculptor = actor) and then routes and syncs. Only two branches end the pose: no sculpture made or
exit reason, and the end of the sculpting loop. Three paths do not:

- A failed route to the station: the subject walks over and waits up to 30 sim-minutes per sync step.
- `SculptingSkill.CanAfford` false after both reached Committed (funds spent since the material was picked).
- A sculptor reset there.

The last two leave the subject in `DoLoop(ExitReason.Default)`, posing until the player cancels it or a motive fails.
Fix: Run inside try/finally; when it did not succeed, the subject's `PoseForSculpture` entries for this sculptor are
cancelled with `InteractionQueue.CancelInteraction`. Counters 2562 cancelled, 2563 errors.

## 4. Read, no bug / left

- Burglar: the reset, queue-test-failure and ReactToPet paths all reach a callback or a state change. With no household
  and goods already stolen, both EA leave states dereference the null household
  (`LeaveSuccessful.DealWithStolenGoods`, `ReturnStolenGoods`). The fix leaves this to EA; it is very rare.
- Restaurants (`EatHere`, `RestaurantInteraction`): cancelled meals are not charged and give no buff (design). A vampire
  follower gets `Meal` instead of `Sated` in `AddCostAndBuffsForOtherSims`; this is cosmetic.
- Day spa: relaxation and spray tan charge up front and keep the charge on cancel (design, same for every treatment).
  `GetTattooInRabbitHole` shows CAS before charging, so a failed charge keeps the tattoo; the definition test gates on funds.
- Tattoo chair: the action alarm is removed on a failed loop, and `mWaitForFailureTattoo` is guarded by try/finally.
  No bug found.
- Consignment: the daily sale only runs for Sims in the active household with a CreatedSim. Items consigned by a
  Sim who leaves the household stay listed until that Sim is played again [inferred design, not traced].
- Firefighter, Interior Designer: alarm add/remove pairs are matched in CleanUp / Shutdown; not traced further.
- Inventing (Widget, DrillHole), gym WorkOut, bookstore and library reading: no candidates found in the census; not
  read in depth.
