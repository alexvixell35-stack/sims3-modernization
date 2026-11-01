# 224. Outfit waits that never end (#115, `outfitwaitpoll`)

Issue #115 (round 2, task 3) found one magical-makeover alarm on the day-406 save that spends all of its time in
`Sim.SwitchOutfitHelper.Wait`. It cost 3.0 ms of sim thread per sim-minute, in both LR arms. This doc explains why such
a wait never ends and describes the ts3fixes switch that makes it cheap. The switch is off by default. Status
(2026-10-31): built, self-tested and run in game (§4: the repro passes; the cost cut is not measured yet). Counters 680-682
(registry `fixes-680`).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`, method and IL offset given; no code reproduced
here); **[measured]** = from #115; **[inferred]** = our reading, not yet checked in game.

## 1. Why the wait never ends

All methods are in `Sims3GameplaySystems` unless named otherwise.

- `Sim+SwitchOutfitHelper::Wait(bool)` [verified]: it returns if `mDisposed` (IL_0001), calls `Start` if not `mStarted`
  (IL_0012), returns if `mNoChange` (IL_0018) and can call `Sim::LoopIdle` (IL_0031). Then it loops on
  `Simulator.Sleep(1)` until `mReady` is set (IL_0038-IL_0044). The loop reads no other field.
- `mReady` has two writers [verified]: `Ready()` sets it. `Start` passes `Ready` as the callback to
  `Sim::SwitchToOutfitDelayed`. `Abort()` clears it.
- `Abort()` [verified] sets `mAborted` and clears `mStarted` and `mReady`. If the helper is the Sim's active helper, it
  also clears `Sim.mActiveSwitchOutfitHelper` and calls `Sim::AbortOutfitDelayedSwap`, which cancels the delayed model
  swap (`CASUtils.AbortDelayedModelSwap`).
- So an `Abort` while a task is in the loop leaves it there: no callback for the cancelled swap sets `mReady` again
  [inferred: the callback was not traced in native code]. The task wakes every tick, 37.5 times per sim-minute
  (`SimClock.kSimulatorTicksPerSimMinute`) [verified], until the game closes.
- `Abort` has 12 callers (`tools/managed-callgraph.ts callers`) [verified]. They include `Start` of another helper on the
  same Sim, `Sim::DoReset`, `Dispose`, and the `Cleanup` of several interactions. `Wait` has 60 direct callers. One of
  them is `ChangeOutfit`, which has 40 callers of its own.
- The makeover path (`Sims3GameplayObjects`, `MagicMirror+MagicalMakeover`) [verified]:
  - `MagicalMakeoverDelegate`, an animation event, adds a one-sim-minute alarm with `ChangeOutfitCB` (AlarmType 1,
    DeleteOnReset).
  - `ChangeOutfitCB` calls `SwitchOutfitHelper::ChangeOutfit`, which calls `Wait(false)` while the helper is not ready.
    The alarm runs as a yielding alarm, in its own `AlarmManager+AlarmOneShot` task.
  - `Cleanup` aborts the helper and drops it, but it leaves the alarm in place. If the interaction ends after the alarm
    fired and before the swap was ready, the alarm task waits forever [inferred: the #115 case fits this].
- Open: how the stuck wait is present in both LR arms. Either the makeover state comes back from the save
  (`AlarmManager` is `IPersistable`), or a Sim starts it again soon after load [inferred, not checked].

## 2. The fix (`outfitwaitpoll`, `mods/ts3fixes/src/OutfitWait.cs`)

- **Switch off:** EA's `Wait` runs.
- **Switch on:** the hook runs EA's steps up to the loop in EA's order. It then polls `mReady` every tick for the first
  60 sim-minutes, and once per sim-minute (37 ticks) after that. It returns at the first poll that sees `mReady`, as
  EA's loop does.
- What the player sees does not change:
  - A swap that becomes ready in the first hour completes on the same tick as in EA's game.
  - A later swap completes less than one sim-minute after EA's.
  - A wait that never becomes ready never returns, as in EA's game, so `ChangeOutfit` never runs.
- Cost of a stuck wait: 53,280 wakes per sim-day become 3,600 (`tools/selftest/outfitwait`). That should bring #115's
  3.0 ms per sim-minute to about 0.08 ms [inferred: assumes a constant cost per wake].
- Not done: ending a stuck wait. If `Wait` returned without `mReady`, `ChangeOutfit` would go on to
  `Sim::DoOutfitDelayedSwap`, while the abort has already cleared the pending outfit [inferred]. Ending the wait safely
  needs a second hook that skips the rest of `ChangeOutfit`. That outcome also differs from EA's when a later `Start`
  makes the same helper ready again.
- The first stale poll writes a `ts3native.log` line. It names the Sim, `mAborted`, and the waiting task (for an alarm
  task, its callback).
- In-load A/B: the hook reads the switch at entry and again at each stale poll. A wait that started in an on arm drops
  back to one poll per tick in the off arms. A wait that started in an off arm stays in EA's loop.

| switch | tunable | default | counters |
|---|---|---|---|
| `outfitwaitpoll` | `kOutfitWaitPoll` | off | 680 `outfitwaitpoll_stale` (waits past 60 sim-minutes), 681 `_polls` (minute polls), 682 `_ended` (stale waits that later became ready) |

## 3. Checks

- **Self-test** (`node tools/selftest/outfitwait/run.ts`): a simulated wait loop, ready at a random tick or never. The
  off arm polls every tick. The on arm matches EA up to the stale mark and is at most 36 ticks late after it.
- **Repro** `outfit-wait-stale-check` (Baseline-BP, `Fixes.OutfitWaitCheck`): it puts the helper into EA's
  abort-in-the-loop state on an active-household Sim. One test task runs `Wait(false)`. A second task aborts the helper
  after `Start` and before `Ready`. The `ready` step then sets `mReady`, as a late callback would. The Sim's outfit does
  not change in either arm.
- **In-load A/B on the LR save** (to do, needs the game): load with the switch on, then read `outfitwaitpoll_stale` and
  the log line's task name. Then run the `--ab outfitwaitpoll` arms and compare the `SwitchOutfitHelper.Wait` row of
  #115 (ms per sim-minute).

## 4. Game runs (2026-10-31, queue `outfit-wait`, results `docs/data/results/2026-10-31-queue-outfit-wait.md`)

- **Repro `bp-outfit-wait-check` passes** [verified]. Off: the check's wait stayed in EA's loop for more than 60
  sim-minutes after the abort, with counters 0. It returned within a tick of the `ready` step (`aborted True`). On: the
  same wait went stale (`aborted True`), was read once per sim-minute, and ended within the 5 s after `ready`.
- **Natural stale waits also occur, from a second cause** [verified count, cause open]. With the switch on, waits that
  were not aborted (`aborted False`) also went stale, in the Sim's own task (`Sim`, an interaction waiting for its outfit):
  4 on Baseline-BP in the last repro run (14 and 40 stale waits in two earlier runs), 20 in 12 min on the LR in-load A/B.
  About a third of them became ready later. The switch delays those by under a sim-minute. Why their swap is late is not
  known (ledger I-414-5).
- **Cost** [not measured]. The LR load of 2026-10-31 had no costly stale wait: `Wait` was in at most 3 of about 884
  managed samples in every arm, so the four per-load timing arms could not show a cut. The #115 case (the makeover
  alarm, I-414-2) is on the day-406 save; the cost check (I-414-3) waits for that save.

## 5. The second cause: waits that were never aborted (2026-11-01, I-414-5, I-414-6)

**What sets `mReady`** [verified IL]. Only the helper's own `Ready`, the `SwapReadyCallback` that `Start` hands to
`Sim.SwitchToOutfitDelayed(category, index, callback)`. That call stores the Sim's pending outfit (`mPendingOutfitInfo`)
and passes the callback to the native `CASUtils.SetOutfitInGameObjectDelayed`. `Abort` is the only script code that
clears `mReady`, and it also sets `mAborted`. So a stale wait with `aborted False` means the callback never ran.

**Other delayed swaps of the same Sim** [verified IL]. These start a delayed swap with their own callback and do not
abort the Sim's active helper first:

- `SimBuilderTask.FinishModelLoad` (age-up and other model rebuilds) and the sculpting station's copy of it;
- `BuffTransformation...GenerateAndApplyTransformOutfit` (from `BuffTransformation.OnAddition`);
- `Sim.SwitchToOutfitWithHiding` (from `GoToCommunityLot.Run` and `Sim.DoReset`);
- `OccultPlantSim.OnAddition`/`OnRemoval`, `OccultMermaid.AddOutfits`, `OccultMermaid.RemoveMermaidWithVortex.Run`.

A few paths end or cancel a Sim's pending swap without the helper: `Sim.DoOutfitDelayedSwap` from
`SimBuilderTask.SwapModel`, `BuffTransformation...ApplyTransformedOutfitSwap` and `Sim.OutfitSwapWithHidingReady`; and
`Sim.AbortOutfitDelayedSwap` when no helper is active. `Sim.SwitchToOutfitWithoutSpin(category, outfit, index)` also
sets the new outfit at once and resets the pending outfit to none, without touching the active helper, and so does
`Sim.DoReset` [verified IL].

**Hypothesis** [inferred, I-414-6]. If the game keeps one pending swap and one callback per Sim, a second delayed swap
drops the helper's callback, and the helper's wait never ends, with `aborted False`. Whether the native call keeps one
callback or all of them is not read yet.

**Diagnostics** (ts3fixes, switch on only). Each stale wait is classed once, when it is marked
(`OutfitWaitCore.StaleCause`, self-tested):

| class | test | counter |
|---|---|---|
| aborted | `mAborted` (the #115 cause, §1) | none, as before |
| own | the Sim's pending outfit is still the helper's, and the helper is the Sim's active one | 2003 `outfitwaitpoll_cause_own` |
| replaced | anything else with an outfit pending (another delayed swap, or another or no active helper) | 2004 `outfitwaitpoll_cause_replaced` |
| cleared | no outfit pending: the Sim's swap was completed or cancelled without the helper | 2005 `outfitwaitpoll_cause_cleared` |

The stale-wait line also gives the pending and the helper's outfit, the active helper (this, other, none), the Sim's
current interaction, whether it has a transformation buff, and the five calling frames of `Wait`.

**Checks.**

- Repro `outfit-wait-replace-check` (Baseline-BP):
  - Control: an untouched delayed swap calls back within seconds.
  - Replace: a second `SwitchToOutfitDelayed` on the same Sim, without an abort, while the helper waits. If only the
    second callback runs, the hypothesis is verified. If both run, it is refuted.
  - Neither swap is completed: `stop` aborts the helper, which cancels the pending swap.
- Job `lr-outfit-wait-causes-ab` (day-406 save, in-load `--ab outfitwaitpoll`): the cause counts of natural stale waits,
  and the cost per arm.
- Queue `tools/harness/queues/outfit-wait-cause.json`.

**Results, run 1** (2026-11-01, Baseline-BP, job `bp-outfit-wait-replace` PASS in 11.1 min, ultra speed, switch on for
the whole load):

- Control: not a valid test in run 1. A step's `waitS` runs after the step, so the state was read in the same second
  as the start (ready False, the wait task still in `Wait`), and `stop` aborted it 10 s later. The repro now waits
  30 s after the start.
- Replace: the second swap (to Everyday) started, and the helper was not aborted. 80 s later the helper's wait had
  returned by itself (aborted False, no change False), and the second swap's callback had not run. So the second swap did
  not drop the helper's callback: **I-414-6 is refuted** for this case **[verified, 1 run]**. A second delayed swap
  does not explain the stale waits.
- Natural stale waits in the same load: 7. Of these, 5 were `own`, 0 `replaced` and 2 `cleared`; 5 of the 7 became ready
  later, and there were 116 minute polls.
  - `own` (5): the pending outfit was still the helper's, and the helper was the active one. The waits came from outfit
    changes before routing (`SimScriptAdaptor.CheckForClothingChangesBeforeRouting`, `RoutingComponent.DoRoute`) and
    from `WorkOutClone`, in `VisitCommunityLotLabLecture`, `GoHome` and `WorkOutClone`. These look like swaps that are
    slow, not lost **[inferred: 5 waits ended, and these are the likely 5]**.
  - `cleared` (2): no outfit was pending while the helper, not aborted and still active, waited for Outerwear. Both
    Sims were in `VisitCommunityLotLabLecture`, waiting from `CheckForClothingChangesBeforeRouting`. Something reset
    their pending outfit without the helper: `SwitchToOutfitWithoutSpin`, `DoReset` or `DoOutfitDelayedSwap` (see above).
    Which one is not known yet. If the 5 waits that ended are the `own` ones, these 2 never end **[inferred]**. They are
    the candidate for the second cause.
- Job `lr-outfit-wait-causes-ab` (the day-406 save): the game crashed 8 minutes in, inside Apex Radiance's call into
  the game (TS3W `0x8AB2B0` reading freed memory, called from `ApexRadiance.asi` +0x2C0434). It is the first crash at
  this site in 11 crash reports on this machine, and probably unrelated to this switch **[inferred]**. A rerun is
  queued.

**Diagnostics added after run 1** (switch on only). Two more hooks log each call that resets a Sim's pending outfit
while its active helper is started, not ready and not aborted, with the Sim's interaction and the five calling frames:

- `Sim.SwitchToOutfitWithoutSpin(category, outfit, index)`, an instant switch: counter 2008 `outfitwaitpoll_clear_instant`;
- `Sim.DoOutfitDelayedSwap` from another path (the helper's own `ChangeOutfit` runs it only after `Ready`, so that is
  not counted): counter 2009 `outfitwaitpoll_clear_delayed`.

`DoReset` is not hooked. The repro has a third part, "clear": a helper waits, and task 2 switches the Sim at once to a
third category. If the wait still returns, an instant switch does not lose the callback. If it goes stale with cause
`cleared`, it does, and the instant switch is the second cause.

**Results, run 2** (2026-11-01, Baseline-BP, `bp-outfit-wait-replace` PASS in 11.3 min, the repro as changed above):

- Control: the untouched swap's wait returned within 30 s and its Sim never went stale **[verified, 1 run]**. On the
  day-406 save (`lr-outfit-wait-causes-ab` rerun, PASS, 15.3 min): 71 stale waits, 68 `own`, 0 `replaced`, 1
  `cleared`, 28 ended; 3 instant switches while a helper waited (2008), 0 delayed swaps by another path (2009). Script
  time with the switch on was 1.025 times that with it off (three 2-min slices per arm, within noise).
- Replace (second swap to Formalwear): as in run 1, the helper's wait returned by itself and the **second** swap's
  callback had not run 70 s later **[verified, 2 runs]**. So with a swap pending, a second delayed swap of the same Sim
  gets no callback, or a very late one (I-414-8). This matters for the callers in the list above that start a second
  swap and then wait on its callback: `SimBuilderTask.FinishModelLoad` waits forever in that case (docs/503 §2).
- Clear (instant switch to Everyday while the helper waits): the pending outfit was reset to none, and the helper was
  ready within 10 s. So an instant switch does not lose the helper's callback: **I-414-7 is refuted**. The new
  hook logged that switch with its callers, as designed.

Open: what keeps 43 of the day-406 save's stale waits from ending (slow swaps or lost callbacks), what the natural
`cleared` waits are if not an instant switch, and whether the second swap's callback ever comes.

**Why the second callback is lost** **[verified, IL of SimIFace]**: `CASUtils.SetOutfitInGameObjectDelayed` (and
`SetModelInGameObjectDelayed`) first registers the caller's swap-ready callback in a static table with one entry per
object, then starts the native swap. If an entry for that object is already there, the new callback is not stored.
When the native swap is ready, `CASUtils` runs and removes the entry it has, which is the first caller's.
`CompleteDelayedModelSwap` and `AbortDelayedModelSwap` remove the entry without running it. So a second delayed swap of
a Sim while the first callback is registered gets no callback at all, and its caller waits forever if it waits on it.
This also covers a stale entry: if nothing completed, aborted or called back an earlier swap, the Sim's next helper
loses its callback, and that would be a stale `own` wait (pending is the helper's own outfit) **[inferred]**.

A drop logger (switch on only, EA's call unchanged) hooks `CASUtils.RegisterCallback`: counter 2010
`outfitwaitpoll_cb_dropped` counts dropped callbacks and logs the first 8 with both callbacks (a helper's state when the
callback is a helper's) and the callers. 2011 `outfitwaitpoll_cause_dropped` counts the stale waits whose helper's
callback was dropped. Jobs `bp-outfit-wait-cbdrop` (the replace part must count a drop) and `lr-outfit-wait-cbdrop-ab`
(how many day-406 stale waits are drops).

A fix cannot just run every callback: `SwitchOutfitHelper.ChangeOutfit` completes the swap whenever its helper is
ready, and `Sim.DoOutfitDelayedSwap` with nothing pending sets the visible outfit info to none **[verified, IL]**, so
two helpers called back for one swap would complete it twice.

**Drop logger results** (2026-11-01):
- `bp-outfit-wait-cbdrop` PASS (11.3 min): the replace part's second swap was counted as a dropped callback (2010 = 1).
  The kept callback was the first helper's `Ready`, with that helper started, not ready, not aborted. **I-414-8 is
  verified in game.** No other callback was dropped in the run, and none of the 7 natural `own` stale waits had a
  dropped callback.
- `lr-outfit-wait-cbdrop-ab` PASS (15.4 min, day-406 save, the switch on in half the slices): 18 stale waits, 15 `own`,
  0 dropped callbacks, 0 stale waits with a dropped callback. **I-414-9 is refuted**: the natural `own` waits keep their
  callback, and their swap is just not reported ready (the native side never calls back, or very late; 8 of 18 ended).
- The natural `own` waits come from route-time clothing changes (`CheckForClothingChangesBeforeRouting`,
  `RoutingComponent.DoRoute`), going to school, and working out.

So a fix for the dropped callback is not needed for the waits seen so far. It still matters for code that starts a
second delayed swap while one is pending (the replace case; the age-up model swap, docs/503 §6).

**Where the natural waits are (2026-11-01, `lr-outfit-wait-view-log`, day-406 save, PASS) [verified]:** in 11 minutes the
switch-on log counted 19 stale waits (17 `own`, 0 replaced, 0 cleared, 0 with a dropped callback) and 11 that ended. None
of them had its Sim on screen when the wait went stale or when it ended, and none was on the active lot. Some were far from
the camera and some were not. So every natural stale wait seen so far belongs to a Sim the player cannot see, and the
`outfitwaitpoll` poll ends them with nothing visible changing. Why an unseen Sim's swap does not become ready is still
open [inferred: the engine may not build outfits for Sims it does not draw].
