# 543. Shared beds: the spooning slave's checks and wake reason, a pet left in the bed

2026-11-16, static hunt (decompiled C# + shipped tuning), built, proven in game (s66, off BROKEN / on FIXED). All three switches are off by default and act on household Sims too: they are EA bug fixes, with no skip or throttle. The section numbers follow the hunt's doc: §2-§3 (`queueproximity`, `subroutefail`, the need-object lines) are in docs/597.

Existing work checked first: docs/565 §6 `spoonreset` (pair kept after a partner reset), docs/529 (bed-partner claim, simler90 guards it). Nothing here repeats them.

## Built

### §1 `spoonslavechecks` (fixes 2163-2166, `SpoonSlaveChecksCheck`): household-visible, every night for couples

[verified, decompiled] `BedSleep.LoopHandler` runs each sleeper's own checks every tick: the light-sleeper wake after `TraitTuning.LightSleeperMinutesAbleToSleep` (120), early `TiredFromMoving` removal, and the one-time `NoMonsters` cut. The `if (mSpooningSlave) return;` test comes before them.

Two romantic partners in one bed are paired by whichever ticks first. That Sim is the master and the other the slave, and the roles last the whole night: after each spoon `ClearSpooningContext` is followed by a new pairing on the master's next tick. So the slave skips all three checks all night. The master still runs them, even mid-spoon, because they come before its `if (mSpooning)` block. `BedDreamPod` and `FairyHouse` run the same three checks for every sleeper. That asymmetry is why we read the early return as meant for the spoon logic only (the intent is [inferred]; the code is [verified]).

Effect: in a shared bed, a light sleeper who is the slave sleeps on past the limit, and a slave's TiredFromMoving waits for its own timeout.

Fix: on a tick where EA's body returns at the slave test (the Sim is the slave, and EA does not take the evil-gnome branch, which comes first when not spooning), the same three checks run right after EA's body, in EA's order. That is the same point in the tick where EA runs them for everyone else; nothing between the slave test and the checks touches what they read. It uses the spoonreset hook (`SrHook`, one swap of LoopHandler) and reads the flags before EA's body runs. With the switch off, slot 2165 counts the skipped slave ticks.

### §4 `petwakebed` (fixes 2167-2169, `PetWakeBedCheck`): household-visible, rare

[verified, decompiled] Pet `Sim.WakeUpSim` jumps on the free side of a double bed with `partData.SetContainedSim(Actor)`, and Run clears it only after the 5-minute wait and Jump Down. `Cleanup` removes the pet from the bed's use list and destroys the jig, but leaves the part's `ContainedSim`. A pet reset in between (`InteractionQueue.OnReset` runs Cleanup) leaves the pet "in" that side: `GetSimInOtherPart` returns the pet, the side looks taken, and only a later route fail on that part clears it (SimRoutingComponent's stale-part heal). Fix: after EA's Cleanup, any part of `mBed` that still holds the pet is cleared, unless the pet is on the bed.

### §5 `slavewakereason` (fixes 2170-2173, `SlaveWakeReasonCheck`): household-visible, couples

[verified, decompiled] `BedSleep.Run`, the spooning slave after its sleep loop: `do { exitReason = Actor.ExitReason; Actor.ClearExitReasons(); synced = IsAtSynchronizationLevelWith(master, Completed); if (!synced) { Sleep(5); synced = master gone; } } while (!synced); Actor.AddExitReason(exitReason);`. The read is inside the loop, so when the wait takes a second pass (the master's `FinishSpooning` plays the ToSleep animation, many 5-tick passes) the reason that ended the sleep is lost. Effect: `HigherPriorityNext` (the player queued an action) is gone, so a tired slave (BadNightSleep/Tired/Sleepy/Exhausted) plays the Slow Wake instead of the Fast Wake, and the queue's post-Run reason checks see nothing.

Fix, without a 150-line Run copy: the first pass's `IsAtSynchronizationLevelWith` call (after EA's first read and clear) is hooked. For the slave in this wait only (its current BedSleep is a spooning slave whose master has `mSpooningCancel`, not sleeping, level Completed), the hook runs EA's wait itself in EA's order (sync test, `Sleep(5)`, master gone test, clear), keeps the reasons that arrive during it, adds them back and answers true; EA then adds the first pass's reason. A one-pass wait gives EA's result. EA's loop has no ceiling; ours stops after 6000 passes (30000 ticks, crash guard only). The hook yields, so it has a stub remap (swap audit row).

## Read, not built

- **Megaphone wake** (`Sim.WakeWithMegaphone.WakeTargetUp`) [verified, decompiled; checked again 2026-11-16, skipped as harmless]: the first test always uses `Target`, so with a partner in bed the target gets `CancelNthInteraction(0)` and `RudeAwakening` three times and the partner once. Every sleeper reaches that code inside its running sleep interaction (all `SetIsSleeping(true)` callers are in Run), so the head is locked and `CancelNthInteraction` only ORs `CanceledByScript` into the exit reasons again; it never removes the head or reaches the next queued action. The re-added buff only refreshes. Each sleeper already ends up woken once; nothing to fix.
- **Pet `WakeUpSim` null bed** [verified, skipped]: `InteractionQueue.RunInteraction` calls `RunTest` (Test: target sleeping, container `IBed`) with no yield before Run, so `mBed` cannot be null at Run start. The Cleanup half is built (§4).
- **Social tests on sleepers**: `SocialInteractionA` and `BeginSocialInteraction` refuse a sleeping target (`Social_TargetSleeping`; toddlers only for player-directed actions), and a target that falls asleep while the actor routes cancels the route (the `kSimFellAsleep` listener, removed in Cleanup, once per interaction). No autonomy loop and no stuck waiter found.
- **Naps** (`Nap`, `BedNap`): `IsSleeping` is cleared in both Run and Cleanup. No wake-order issue.

## In-game result (s66, Bridgeport, 2026-11-16, run phase-20261116-2018, one load, switch off then on)

| Switch | Off | On |
|---|---|---|
| `spoonslavechecks` | BROKEN | FIXED |
| `petwakebed` | BROKEN | FIXED |
| `slavewakereason` | BROKEN | FIXED |

- [verified] The verdicts, controls ok and members found (checks `SpoonSlaveChecksCheck`, `PetWakeBedCheck`, `SlaveWakeReasonCheck`, repros `<switch>-check`).
- [inferred] A live case: the run had no spooning pair or pet wake while the checks ran (counters 0). The checks call the same decisions as the hooks, and the live-path assert confirms the swaps and handles.
