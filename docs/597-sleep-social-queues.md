# 597. Sleeping Sims as social targets, shared beds, need-object lines

2026-11-16, static hunt (decompiled C# + shipped tuning), r53 + r55, built, NOT RUN. All five switches are off by default and act on household Sims too: they are EA bug fixes, with no skip or throttle. Our harness Sims barely look after their needs, so this was a read of the code, not of logs.

Existing work checked first: docs/565 §6 `spoonreset` (pair kept after a partner reset), docs/589 U10 `bathcleanup`, docs/577 (privacy situations), docs/595 (toilet / sink route fails are contention), docs/529 (bed-partner claim, simler90 guards it). Nothing here repeats them.

## Built

### §1 `spoonslavechecks` (fixes 1954-1957, `SpoonSlaveChecksCheck`): household-visible, every night for couples

[verified, decompiled] `BedSleep.LoopHandler` runs each sleeper's own checks every tick: the light-sleeper wake after `TraitTuning.LightSleeperMinutesAbleToSleep` (120), early `TiredFromMoving` removal, and the one-time `NoMonsters` cut. The `if (mSpooningSlave) return;` test comes before them.

Two romantic partners in one bed are paired by whichever ticks first. That Sim is the master and the other the slave, and the roles last the whole night: after each spoon `ClearSpooningContext` is followed by a new pairing on the master's next tick. So the slave skips all three checks all night. The master still runs them, even mid-spoon, because they come before its `if (mSpooning)` block. `BedDreamPod` and `FairyHouse` run the same three checks for every sleeper. That asymmetry is why we read the early return as meant for the spoon logic only (the intent is [inferred]; the code is [verified]).

Effect: in a shared bed, a light sleeper who is the slave sleeps on past the limit, and a slave's TiredFromMoving waits for its own timeout.

Fix: on a tick where EA's body returns at the slave test (the Sim is the slave, and EA does not take the evil-gnome branch, which comes first when not spooning), the same three checks run right after EA's body, in EA's order. That is the same point in the tick where EA runs them for everyone else; nothing between the slave test and the checks touches what they read. It uses the spoonreset hook (`SrHook`, one swap of LoopHandler) and reads the flags before EA's body runs. With the switch off, slot 2165 counts the skipped slave ticks.

### §2 `queueproximity` (fixes 1958-1960, `QueueProximityCheck`): household-visible, rare

[verified, decompiled] In `SimQueue.FindNewPlaceInLine`, the `OrderByProximity` branch compares the newcomer's squared distance (`GetDistanceToObjectSquared`) with each queued Sim's plain distance (`GetDistanceToObject`). `SortLineByDistance`, which runs every check period afterwards, uses squared distances on both sides.

Effect: a Sim more than 1 m away is placed behind Sims nearer than its squared distance. On a full line (place >= max) it is refused with `ObjectInUse`, although EA's own sort would have put it ahead and marked the farthest Sim `Evicted`. This affects elevator lines (`PortalComponent`, `StayAtCurrentLocation`, so the place only matters for that full-line refusal), skating-rink and greeting-card booth entrances.

Fix: squared distances on both sides. Only the `OrderByProximity` branch changes; `CutToHeadOfLine` and `CutAheadOfLowerPrioritySims` are tested first and left to EA.

### §3 `subroutefail` (fixes 1961-1963, `SubRouteFailCheck`): household-visible, need-object lines

[verified, decompiled] `SimQueue.WaitForTurn`: on the first pass, when `TryReplacementInteraction` finds a free object for the same need (toilet, urinal, all-in-one bathroom, shower, any need object with `TryAnotherObjectIfLineExists`), it pushes it as a continuation (`WasInUse`, `FailedToStart`) and the loop breaks with the Sim not at the head. The timeout tail then runs and calls `PlayRouteFail`: a route-fail balloon showing the busy object's user plus `Sim.RouteFailure`, a blocking animation, before the Sim walks to the substitute it just found. The intent is [inferred]: EA hands over to the substitute silently (WasInUse, continuation), and the balloon tells the player the Sim failed when it did not.

Fix: the hook on `TryReplacementInteraction` stamps (Sim, tick) after a push; the hook on `PlayRouteFail` skips the route fail for that Sim on that tick (no yield lies between the two; the autonomy search inside TryReplacementInteraction can yield, so the stamp is taken after it returns). Queue removal and exit reasons stay EA's; every other route fail is unchanged, and `DontPlayRouteFail` callers stay silent as before.

### §4 `petwakebed` (fixes 1964-1966, `PetWakeBedCheck`): household-visible, rare

[verified, decompiled] Pet `Sim.WakeUpSim` jumps on the free side of a double bed with `partData.SetContainedSim(Actor)`, and Run clears it only after the 5-minute wait and Jump Down. `Cleanup` removes the pet from the bed's use list and destroys the jig, but leaves the part's `ContainedSim`. A pet reset in between (`InteractionQueue.OnReset` runs Cleanup) leaves the pet "in" that side: `GetSimInOtherPart` returns the pet, the side looks taken, and only a later route fail on that part clears it (SimRoutingComponent's stale-part heal). Fix: after EA's Cleanup, any part of `mBed` that still holds the pet is cleared, unless the pet is on the bed.

### §5 `slavewakereason` (fixes 1967-1970, `SlaveWakeReasonCheck`): household-visible, couples

[verified, decompiled] `BedSleep.Run`, the spooning slave after its sleep loop: `do { exitReason = Actor.ExitReason; Actor.ClearExitReasons(); synced = IsAtSynchronizationLevelWith(master, Completed); if (!synced) { Sleep(5); synced = master gone; } } while (!synced); Actor.AddExitReason(exitReason);`. The read is inside the loop, so when the wait takes a second pass (the master's `FinishSpooning` plays the ToSleep animation, many 5-tick passes) the reason that ended the sleep is lost. Effect: `HigherPriorityNext` (the player queued an action) is gone, so a tired slave (BadNightSleep/Tired/Sleepy/Exhausted) plays the Slow Wake instead of the Fast Wake, and the queue's post-Run reason checks see nothing.

Fix, without a 150-line Run copy: the first pass's `IsAtSynchronizationLevelWith` call (after EA's first read and clear) is hooked. For the slave in this wait only (its current BedSleep is a spooning slave whose master has `mSpooningCancel`, not sleeping, level Completed), the hook runs EA's wait itself in EA's order (sync test, `Sleep(5)`, master gone test, clear), keeps the reasons that arrive during it, adds them back and answers true; EA then adds the first pass's reason. A one-pass wait gives EA's result. EA's loop has no ceiling; ours stops after 6000 passes (30000 ticks, crash guard only). The hook yields, so it has a stub remap (swap audit row).

## Shipped tuning (GameplayData.package, read-only `tools/static/tuning-xml-dump.py`)

[verified] The Toilet tuning XML (the class with `kToiletTuning`, `kMaxLengthUseToilet`) ships `kTimeToWaitInLine = 0`, the same as the code default. Bathtub `kTimeToWaitForBath = 15`, Shower `kTimeToWaitToEvict = 15`, Sink 15, toilet line length 3, `SimQueue.kCheckPeriodInMinutes = 1`.

With 0, a Sim queued for a busy toilet routes to the door, tries one substitute, waits one check period (1 sim-minute) and times out (`ObjectInUse` + `TimedOut`); autonomy then picks again. EA tuned it that way on purpose, so it is not changed.

## Read, not built

- **Megaphone wake** (`Sim.WakeWithMegaphone.WakeTargetUp`) [verified, decompiled; checked again 2026-11-16, skipped as harmless]: the first test always uses `Target`, so with a partner in bed the target gets `CancelNthInteraction(0)` and `RudeAwakening` three times and the partner once. Every sleeper reaches that code inside its running sleep interaction (all `SetIsSleeping(true)` callers are in Run), so the head is locked and `CancelNthInteraction` only ORs `CanceledByScript` into the exit reasons again; it never removes the head or reaches the next queued action. The re-added buff only refreshes. Each sleeper already ends up woken once; nothing to fix.
- **Pet `WakeUpSim` null bed** [verified, skipped]: `InteractionQueue.RunInteraction` calls `RunTest` (Test: target sleeping, container `IBed`) with no yield before Run, so `mBed` cannot be null at Run start. The Cleanup half is built (§4).
- **Social tests on sleepers**: `SocialInteractionA` and `BeginSocialInteraction` refuse a sleeping target (`Social_TargetSleeping`; toddlers only for player-directed actions), and a target that falls asleep while the actor routes cancels the route (the `kSimFellAsleep` listener, removed in Cleanup, once per interaction). No autonomy loop and no stuck waiter found.
- **Lines on reset, death or leaving the lot**: `InteractionQueue.OnReset` runs `Cleanup`, which runs `RemoveFromLine`, and `RemoveFromUseList` also leaves the line. The object's `DoReset` clears its line. Eviction marks the last record, which is never the newcomer. No stale head found.
- **Naps** (`Nap`, `BedNap`): `IsSleeping` is cleared in both Run and Cleanup. No wake-order issue.

## Pairs

r53 package; the off/on pairs are the repros `spoonslavechecks-check` and `queueproximity-check`. r55 package adds `subroutefail-check`, `petwakebed-check` and `slavewakereason-check`.
