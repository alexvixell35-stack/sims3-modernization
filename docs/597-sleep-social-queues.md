# 597. Sleeping Sims as social targets, shared beds, need-object lines

2026-11-16, static hunt (decompiled C# + shipped tuning), r53, built, NOT RUN. Both switches are off by default and act on household Sims too: they are EA bug fixes, with no skip or throttle. Our harness Sims barely look after their needs, so this was a read of the code, not of logs.

Existing work checked first: docs/565 §6 `spoonreset` (pair kept after a partner reset), docs/589 U10 `bathcleanup`, docs/577 (privacy situations), docs/595 (toilet / sink route fails are contention), docs/529 (bed-partner claim, simler90 guards it). Nothing here repeats them.

## Built

### §1 `spoonslavechecks` (fixes 1954-1957, `SpoonSlaveChecksCheck`): household-visible, every night for couples

[verified, decompiled] `BedSleep.LoopHandler` runs each sleeper's own checks every tick: the light-sleeper wake after `TraitTuning.LightSleeperMinutesAbleToSleep` (120), early `TiredFromMoving` removal, and the one-time `NoMonsters` cut. The `if (mSpooningSlave) return;` test comes before them.

Two romantic partners in one bed are paired by whichever ticks first. That Sim is the master and the other the slave, and the roles last the whole night: after each spoon `ClearSpooningContext` is followed by a new pairing on the master's next tick. So the slave skips all three checks all night. The master still runs them, even mid-spoon, because they come before its `if (mSpooning)` block. `BedDreamPod` and `FairyHouse` run the same three checks for every sleeper. That asymmetry is why we read the early return as meant for the spoon logic only (the intent is [inferred]; the code is [verified]).

Effect: in a shared bed, a light sleeper who is the slave sleeps on past the limit, and a slave's TiredFromMoving waits for its own timeout.

Fix: on a tick where EA's body returns at the slave test (the Sim is the slave, and EA does not take the evil-gnome branch, which comes first when not spooning), the same three checks run right after EA's body, in EA's order. That is the same point in the tick where EA runs them for everyone else; nothing between the slave test and the checks touches what they read. It uses the spoonreset hook (`SrHook`, one swap of LoopHandler) and reads the flags before EA's body runs. With the switch off, slot 1956 counts the skipped slave ticks.

### §2 `queueproximity` (fixes 1958-1960, `QueueProximityCheck`): household-visible, rare

[verified, decompiled] In `SimQueue.FindNewPlaceInLine`, the `OrderByProximity` branch compares the newcomer's squared distance (`GetDistanceToObjectSquared`) with each queued Sim's plain distance (`GetDistanceToObject`). `SortLineByDistance`, which runs every check period afterwards, uses squared distances on both sides.

Effect: a Sim more than 1 m away is placed behind Sims nearer than its squared distance. On a full line (place >= max) it is refused with `ObjectInUse`, although EA's own sort would have put it ahead and marked the farthest Sim `Evicted`. This affects elevator lines (`PortalComponent`, `StayAtCurrentLocation`, so the place only matters for that full-line refusal), skating-rink and greeting-card booth entrances.

Fix: squared distances on both sides. Only the `OrderByProximity` branch changes; `CutToHeadOfLine` and `CutAheadOfLowerPrioritySims` are tested first and left to EA.

## Shipped tuning (GameplayData.package, read-only `tools/static/tuning-xml-dump.py`)

[verified] The Toilet tuning XML (the class with `kToiletTuning`, `kMaxLengthUseToilet`) ships `kTimeToWaitInLine = 0`, the same as the code default. Bathtub `kTimeToWaitForBath = 15`, Shower `kTimeToWaitToEvict = 15`, Sink 15, toilet line length 3, `SimQueue.kCheckPeriodInMinutes = 1`.

With 0, a Sim queued for a busy toilet routes to the door, tries one substitute, waits one check period (1 sim-minute) and times out (`ObjectInUse` + `TimedOut`); autonomy then picks again. EA tuned it that way on purpose, so it is not changed.

## Read, not built

- **Substitution route fail** [verified code, intent inferred]: when `TryReplacementInteraction` pushes another toilet, `WaitForTurn` breaks and still calls `PlayRouteFail`, a blocking route-fail animation plus balloon, before the Sim walks to the free toilet. This affects toilets, urinals and the all-in-one bathroom. It may be deliberate feedback ("busy"), so it is not built. If wanted: skip the balloon when the substitution was pushed (no yield between the two calls).
- **Megaphone wake** (`Sim.WakeWithMegaphone.WakeTargetUp`): its first test always uses `Target`, so with a partner in bed the target is cancelled and gets `RudeAwakening` three times. The re-add only refreshes the buff and the cancels hit the same head. No visible effect.
- **Pet `WakeUpSim`**: `mBed` is taken from the target's posture with no null test (the target may already be up when Run starts), and `Cleanup` does not clear the bed part's `ContainedSim` if the pet is reset between jumping on and jumping off. Both are rare (needs a reset or a stale queued wake); log-only.
- **BedSleep slave wake wait** (docs/589 lead): the slave's wait loop re-reads `Actor.ExitReason` each pass, so a reason from the first pass is lost if the loop runs twice. The master's next tick usually ends it in one pass. A fix needs a 150-line Run copy; not built.
- **Social tests on sleepers**: `SocialInteractionA` and `BeginSocialInteraction` refuse a sleeping target (`Social_TargetSleeping`; toddlers only for player-directed actions), and a target that falls asleep while the actor routes cancels the route (the `kSimFellAsleep` listener, removed in Cleanup, once per interaction). No autonomy loop and no stuck waiter found.
- **Lines on reset, death or leaving the lot**: `InteractionQueue.OnReset` runs `Cleanup`, which runs `RemoveFromLine`, and `RemoveFromUseList` also leaves the line. The object's `DoReset` clears its line. Eviction marks the last record, which is never the newcomer. No stale head found.
- **Naps** (`Nap`, `BedNap`): `IsSleeping` is cleared in both Run and Cleanup. No wake-order issue.

## Pairs

r53 package; the off/on pairs are the repros `spoonslavechecks-check` and `queueproximity-check`.
