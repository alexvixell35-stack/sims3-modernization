# 597. Need-object and elevator lines (`queueproximity`, `subroutefail`)

2026-11-16, static hunt (decompiled C# + shipped tuning), built, proven in game (s66, off BROKEN / on FIXED). Both switches are off by default and act on household Sims too: they are EA bug fixes, with no skip or throttle. §1 and §4-§5 of this doc number (sleep fixes) are not part of this change.

### §2 `queueproximity` (fixes 2760-2762, `QueueProximityCheck`): household-visible, rare

[verified, decompiled] In `SimQueue.FindNewPlaceInLine`, the `OrderByProximity` branch compares the newcomer's squared distance (`GetDistanceToObjectSquared`) with each queued Sim's plain distance (`GetDistanceToObject`). `SortLineByDistance`, which runs every check period afterwards, uses squared distances on both sides.

Effect: a Sim more than 1 m away is placed behind Sims nearer than its squared distance. On a full line (place >= max) it is refused with `ObjectInUse`, although EA's own sort would have put it ahead and marked the farthest Sim `Evicted`. This affects elevator lines (`PortalComponent`, `StayAtCurrentLocation`, so the place only matters for that full-line refusal), skating-rink and greeting-card booth entrances.

Fix: squared distances on both sides. Only the `OrderByProximity` branch changes; `CutToHeadOfLine` and `CutAheadOfLowerPrioritySims` are tested first and left to EA.

### §3 `subroutefail` (fixes 2763-2765, `SubRouteFailCheck`): household-visible, need-object lines

[verified, decompiled] `SimQueue.WaitForTurn`: on the first pass, when `TryReplacementInteraction` finds a free object for the same need (toilet, urinal, all-in-one bathroom, shower, any need object with `TryAnotherObjectIfLineExists`), it pushes it as a continuation (`WasInUse`, `FailedToStart`) and the loop breaks with the Sim not at the head. The timeout tail then runs and calls `PlayRouteFail`: a route-fail balloon showing the busy object's user plus `Sim.RouteFailure`, a blocking animation, before the Sim walks to the substitute it just found. The intent is [inferred]: EA hands over to the substitute silently (WasInUse, continuation), and the balloon tells the player the Sim failed when it did not.

Fix: the hook on `TryReplacementInteraction` stamps (Sim, tick) after a push; the hook on `PlayRouteFail` skips the route fail for that Sim on that tick (no yield lies between the two; the autonomy search inside TryReplacementInteraction can yield, so the stamp is taken after it returns). Queue removal and exit reasons stay EA's; every other route fail is unchanged, and `DontPlayRouteFail` callers stay silent as before.

### Result (s66, Bridgeport, in game)

The harness ran each Check with the switch off, then on (repros `queueproximity-check`, `subroutefail-check`):

| switch | off | on | controls |
|---|---|---|---|
| `queueproximity` | BROKEN: the 2 m newcomer goes last, behind the 3 m Sim (EA's mixed units) | FIXED: placed between | empty, one-Sim and all-nearer lines unchanged |
| `subroutefail` | BROKEN: route fail played after the substitute was pushed | FIXED: skipped | no push, other Sim, later tick and `DontPlayRouteFail` left to EA |

The live path (hook classes swapped in) is part of each verdict. The live counters stayed 0 in that session: the harness Sims did not hit a full proximity line or a substitute push on their own.
