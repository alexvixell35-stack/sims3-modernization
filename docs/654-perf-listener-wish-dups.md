# 654 Performer missed-job listener leak (perfjobmissedlistener) and wish-node growth on household Sims (report)

2026-11-19. Static analysis of the decompiled EA code and the leak census of the phase runs (phase-2026111*). §1 is
built, default off, NOT RUN. Package tag `strays`. Counter slots 4160-4169 (4150-4159 and 4170-4179 belong to other
branches), `kCounterSlots` 4170. §2 is a report only: nothing is built.

## 1. `perfjobmissedlistener`: PerformanceCareer.OnJobMissed is never removed

### Symptom [measured]

Leak census `events.target_holds_gone_by_delegate`, last snapshot of each run: 287 runs, 26 with leaked
`OnJobMissed` listeners (the key shows as `SteadyGigHook.OnJobMissed` when `steadygigmiss` swapped the method). Per run:
1 (8 runs), 2 (6), 3 (4), 4 (1), 5 (2), 6 (3), 7 (1), 8 (1). "Target holds gone" means the delegate's target (the
`PerformanceCareer`) holds a `SimDescription` that is no longer valid: a performer was disposed and its career, and the
description through `OwnerDescription`, stay reachable from `EventTracker` for the rest of the session.

### Cause [verified, decompiled]

- `PerformanceCareer.SetAttributesForNewJob` calls `EventTracker.AddListener(kOccupationJobMissed, OnJobMissed)` with no
  owner key and drops the returned listener. No code removes it.
- Every way out of the career runs `PerformanceCareer.LeaveJobNow`: quitting, being fired or retiring
  (`Occupation.LeaveJob` -> `ActiveCareer.LeaveJob` -> `LeaveJobNow`), a new occupation (`CareerManager.AssignJob` moves
  the old one to `QuitCareers`), and description disposal (`SimDescription.Dispose` -> `CareerManager.LeaveAllJobs` ->
  `LeaveJob(false, kDebug)`). `LeaveJobNow` removes the gig alarms and the steady-gig notifier and calls
  `RemoveCareerItems`, but leaves the listener.
- Rejoining a quit career reuses the same instance (`CareerManager.TryGetNewCareer` takes it from `QuitCareers`) and
  runs `SetAttributesForNewJob` again: a second listener on the same career, so each missed job counts twice.
- `mSteadyGigs` is not cleared on leave, so a left career's listener can still charge a miss to an old steady gig and,
  after two, run `CancelSteadyGig` (a TNS about a gig of a career the Sim no longer has; on a disposed owner it reads
  `OwnerSim` of a gone description).

### Fix

`PerformanceCareer.RemoveCareerItems` is hooked (`PerfJobMissedListener.cs`). It is `LeaveJobNow`'s own step and runs
after `base.LeaveJobNow`'s `AssignOccupation(null)`. After EA's body, one sweep over the `kOccupationJobMissed` listeners
removes, with EA's `EventTracker.RemoveListener` (deferred while the list is being dispatched), every
`PerformanceCareer.OnJobMissed` listener whose career is not its owner's current occupation or whose owner description
is invalid (`PerfJobMissedCore`). The same entry is dropped from `steadygigmiss`'s world-load registry. The sweep also
catches a listener left by an earlier `LeaveJobNow` that threw before this step.

The other two callers of `RemoveCareerItems` (a declined hire dialog in `CareerManager`, `CareerSelectionModel` before a
quit career is rejoined) remove nothing current: the listener of a career that is its owner's occupation is always kept.

Why it is safe for all Sims (household included): the fix removes only listeners of careers their Sim no longer holds.
Such a listener does nothing a player can want: it charges misses to a career that was left, or throws on a gone owner.
A current career's listener, gigs, misses and TNS are untouched, so a household performer plays exactly as in vanilla.
The one visible difference is the EA bug itself: after quitting and rejoining, a missed gig counts once (vanilla twice),
and a quit career no longer posts a "steady gig cancelled" TNS.

Counters: [4160] stale listeners removed, [4161] stale listeners seen with the switch off (counted on each sweep),
[4162] errors, [4163] sweeps; 4164-4169 spare. Log `perfjobmissedlistener: removed N stale
PerformanceCareer.OnJobMissed listener(s): owner gone G, career left L` (at most 50 lines a session). Selftest
`node tools/selftest/perfjobmissedlistener/run.ts`. Agent `patches` reports `perfjobmissedlistener_*`. Judge it in game
with the leak census: `target_holds_gone_by_delegate` `*.OnJobMissed` should be 0 with the switch on.

## 2. Wish and dream listeners on household Sims (report only)

### Symptom [measured]

phase-20261119-1243 (Isla Paradiso, Collins), `events.dream_listeners`: Christopher Collins 1205 listeners (leak-0) ->
1306 -> 1381 -> 1685 -> 2085 -> 2082 -> 3247 (leak-6) over ~425 sim-hours; his manager's active nodes go 1205 -> 2067.
The other Collins stay at ~1000-1250. The growth is in `hidden never_times_out` and `hidden past_display` nodes. By
trigger, the biggest risers are `kSocialInteraction`, `kRelationshipLTRChanged`, `kCaughtCheatingWithSim`,
`kCaughtCheatingOnPartner`, `kBoughtObject`, `kSkillLevelUp`. At leak-6, 1180 of his listeners belong to nodes that his
manager no longer holds (`state: orphan (household)`; nodes `DreamNodeInstance` 1170, `SocialNodeInstance` 10).

### Which listener, and who adds it

1. **The listener** [verified]: each active `ActiveDreamNode` holds one `EventTracker` listener for its trigger, owner
   keyed (docs/580 §17). Nodes that never time out (roots, `SatisfyTime` 0) keep theirs until they complete. Most of the
   growth is EA's design: hidden subject wishes, one node per (primitive, subject), made for each Sim the household Sim
   socializes with (docs/580 §17). That part is not a leak.
2. **A duplicate-registration path** [verified in code; share of the growth inferred]: in
   `DreamsAndPromisesManager.ScoreChildren` the "already active" test (`IsNodeAlreadyActive`, primitive + tree + instance
   + subject) runs before `Test()`. Several check functions set the node's subject inside `Test()`
   (`BeMarriedCheckFunction` sets the partner, `BeOnDateWithSimCheckFunction`, `BeInFlirtyContextCheckFunction`,
   `HaveNegativeRelationshipWithSimCheckFunction`, `FatherReadPregnancyBookCheckFunction`, `HaveNoTraitChipCheckFunction`,
   `HasAJetpackCheckFunction`). Before `Test()` the subject is still `None`, so the test misses an existing node for that
   subject. The `kBecomeActiveButNonVisible` branch tests again after `Test()`; the `kBecomeActive` branch does not and calls
   `ActivateListener` + `AddActiveNode`. Each time the parent completes (repeatable parents on `kSocialInteraction`, for
   example) another node with the same primitive, tree, instance and subject is added, each with its own listener. Both
   copies fire on the trigger, complete and score their children.
3. **Orphan listeners** [measured, cause not found]: the 1180 leak-6 listeners whose node is not in any of the manager's
   collections. `RemoveActiveNode` always removes the listener, so these nodes left the manager another way. The census
   cannot name the primitive. A next census step should list orphan nodes by primitive id and parent before any fix.

### Proposed fix (not built)

At the `kBecomeActive` branch of `ScoreChildren`, test `IsNodeAlreadyActive` again after `Test()`, as the
`kBecomeActiveButNonVisible` branch already does. When it is true, drop the new node (it has no listener yet:
`CreateNode(..., activateListener: false)`) instead of adding it.

It does not keep behaviour identical. The duplicate is not a pure memory copy: the scored node also goes into the
display candidate list, and both copies complete on the trigger and score their children. Skipping it changes how often
child wishes are rolled and which wish can be offered. On household Sims that is visible state (the wish panel), so under
the household rule it would need Rodney's approval. A leak-only variant keeps the candidate in the display list and drops
only a hidden duplicate (not displayed, offered or promised). That still removes the second completion and its child
scoring, so it is not exact either. Recommendation: first add the census step from item 3 and count true duplicates by
(primitive, tree, instance, subject). The current `dup` counter keys only on primitive + subject, so it also counts the
same primitive in different trees.
