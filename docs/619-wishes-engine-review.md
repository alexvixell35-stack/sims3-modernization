# 619. Wishes engine review (DreamsAndPromises), static pass 2026-11-17

Scope: `Sims3.Gameplay.DreamsAndPromises` (Sims3GameplaySystems, 32 files), `EventTracker` drain, `Sim.NullDnPManager` and friends, `DreamsAndPromisesModel` (UI glue), the reflection-bound `DreamsAndPromisesDelegateFunctions` (Sims3GameplayObjects, 24k lines).
Method: hot-logic-walker over all 2,710 methods of those files (0 hits), `ea-loop-logic` and `ea-gameplay-logic` semgrep rules (0 hits), then hand reading of the manager, ActiveDreamNode, ActiveNodeBase, ActiveTimerNode, NodeSubject, EventTracker.ProcessEvent and the Sim DnP lifecycle. IL via `ilspycmd -il -t Sims3.Gameplay.DreamsAndPromises.DreamsAndPromisesManager` where it matters. Lines are the decompiled `game-copy/decompiled/Sims3GameplaySystems/Sims3.Gameplay.DreamsAndPromises/<file>.cs` lines (M = DreamsAndPromisesManager.cs).
Marks: **verified** = read in decompiled code or IL, effect follows from the code; **inferred** = a trigger or effect that was not seen live. Nothing here was run in game.
No NRaas mod patches these (ErrorTrap only dereferences `mPotentialRemovalNodes`, `mActor`, `mActiveNodes`, `mEventListener`).

Already covered, skipped: `dnpdisposelistener` (ctor listens on the wrong id), `dreamrootorphan`, `blogorphan`, `dnpupdateguard`, `dreamcleanup` (pet / object / woken-node index), `dnppetkeys`/`dnpobjkeys`, `dnpsubjectcache` (invalid SimDescriptions in the canonical NodeSubject cache), `dreamnodedeadsim`, `petdreamtimer`, `treewishingwell`, docs/545, docs/266, docs/73 (event-dispatch cost).

## Ranked findings

| # | Finding | Kind | Mark | Size |
|---|---|---|---|---|
| 1 | Timer-node alarms are never removed when the node or manager is dropped | leak, save growth, spurious updates | verified | medium |
| 2 | A wish whose feedback function throws once loses its listener for good | wish never fulfills | verified (EventTracker) | medium |
| 3 | "Dream failed" feedback for a promised wish never happens | logic | verified | small, certain |
| 4 | Promised wishes of a child taken by the social worker stay in their promise slot | slot leak | verified path | small |
| 5 | A SetToUpdate made during Update is dropped | delayed fulfilment | verified | small |
| 6 | Subject death drops the index entry of nodes it chose to keep | stale wish for deleted Sim | verified | small |
| 7 | `PartialUpdate` can modify `mCompletedNodes` while enumerating it | exception, replays | inferred trigger | low |
| 8 | NodeSubject `==` misses PhotoSubject and mixes OccultType keys | wrong comparison | verified code, inferred effect | low |
| 9 | Cached MiniSimDescription in a canonical NodeSubject is never refreshed | wish stuck invisible | inferred | low |
| 10 | Unselected-but-active "shadow" nodes after `SelectDreamsToDisplay` | stale list, per-event cost | inferred | low |
| 11 | `PromiseDream` returns true when no slot took the node | logic | verified | tiny |
| P1-P5 | Per-event / per-update waste | perf | verified code | all small |

## (a) Logic bugs

### 1. Timer-node alarms leak (ActiveTimerNode.cs 123, 167-181, 216, 227-241; M 3119-3141; Sim.cs 60112-60120, 60261-60267)
- Evidence: `ActiveTimerNode` creates `mOwner.AddAlarmRepeating(..., AlarmType.AlwaysPersisted)` (ctor line 123, `SetupFromExport` line 216). `RemoveListenerFromTracker()` (line 167) is an empty override. The alarm is removed only by `KillNode` (175) and by `TimerCallback` when `IsComplete` (236). Every other way a timer node leaves play does not touch it: `RemoveActiveNode` (M 1548) only calls `RemoveListenerFromTracker`; `OnLoadFixup` drops nodes of refreshed trees with `RemoveAt` + `RemoveListenerFromTracker` (M 3131-3141); `Sim.NullDnPManager` (Sim.cs 60112) calls `ClearActiveNodeListeners`, which calls the same empty method on every active node.
- Effect: each `ResetDnP`, each Sim that stops being selectable (household switch) and each load after a tree-hash change (`sSavedTreeHashes` null for old saves, so every tree) leaves the old repeating alarm alive and persisted in the save. Each stale alarm keeps its dead node and manager reachable, fires `TimerCallback` every `TimeBetweenSatisfies`, and calls `SetToUpdate(fullUpdate: true)` on the Sim's current manager, so a household Sim gets extra full Updates (N stale timers = N per interval) for the rest of the save. Not seen live.
- Mark: verified (code path); effect size inferred (how many timer trees exist in 1.67 data not counted).
- Fix sketch (memory/alarm only, keeps EA's intent): hook `ActiveTimerNode.RemoveListenerFromTracker` to also `mOwner.RemoveAlarm(mAlarmHandle); mAlarmHandle = kInvalidHandle` when the alarm is valid and the node is not the one being completed. `RemoveListenerFromTracker` is called only when a node leaves, so this is safe; `Update` never calls it for a timer that is still running (`flag2 = false` path at M 918). One caveat: `RemoveActiveNode` of a completed non-repeating timer is already alarm-free, so the guard is a no-op there.

### 2. Listener dropped on any exception in a wish feedback function (EventTracker.cs 91-102; ActiveDreamNode.cs 1781-1798)
- Evidence: `ProcessEvent` wraps `eventListener.ProcessEvent(e)` in `try { ... } catch (Exception) { RemoveListener(value2, eventListener); }`. A wish's listener is `ActiveDreamNode.RunFeedbackFunction`, which calls one of 2,233 reflection-bound functions (`DreamsAndPromisesDelegateFunctions`, `sFeedbackWrapper`, line 146). Any NullReference on a destroyed subject, null Sim or missing component inside one of them removes the listener; `mEventListener` still points at it, `IsComplete` stays false, and nothing re-adds it until `Reset(true)` (repeat wake) or load.
- Effect: that wish can never complete again, and it still occupies its slot and counts as active. Silent. docs/61 and docs/73 note the removal rule in general; no switch targets wishes.
- Mark: verified (dispatch code); whether any shipped function throws in play is not measured. Ranked 2 because the cost of being wrong is a lost wish and the fix is tiny.
- Fix sketch: patch `ActiveDreamNode.RunFeedbackFunction` with try/catch that counts and returns `ListenerAction.Keep` on exception (same shape as `dnpupdateguard`). EA's intent (listener stays until the wish completes) is preserved; a function that throws every time just throws every time at the same cost it had before it was removed, so count and log once per node.

### 3. Failure notice, sound and slot test are dead code in `ObjectDestroyedDelegate` (M 611-688, lines 663-678)
- Evidence: the loop does `RemoveActiveNode(item, false)` first. `RemoveActiveNode` (M 1548-1580) calls `CancelDream` and `BreakPromise(node)`, and `BreakPromise` (M 1696) calls `RemoveDisplayedPromiseNode`, which empties the slot. The next statement, `if (RemoveDisplayedPromiseNode(item))`, is therefore always false. Everything inside never runs: the `DreamFailureTNS` notification, the `sting_dream_fail` sound (flag), and the Midlife-crisis refresh. (`BreakPromise` itself already does `FireBreakPromise` and the crisis update, so the UI and crisis state are right; only the notice and sting are lost.) Also: in the `kEventObjectDisposed` branch `dnPSubjectDestroyedEvent` is null, so if this block did run for an object it would throw `NullReferenceException` at `.SimDescription` (line 668). Fixing the order alone would turn a silent loss into an exception.
- Effect: when the subject of a promised wish dies or is deleted, the player gets no "wish failed" message or sound, although EA intended both.
- Mark: verified.
- Fix sketch: capture `bool wasPromised = GetPromiseIndex(item) >= 0` before `RemoveActiveNode`, use it in place of the dead test, and null-check `dnPSubjectDestroyedEvent` before the TNS. Cosmetic; low priority, bundle with finding 6.

### 4. Child taken by social worker: promised wishes stay promised (M 567-609)
- Evidence: for matching nodes of four trees whose subject is the child it calls `KillNode()` then `CancelDream(node)`. `KillNode` sets `mTimeActive = SatisfyTime` but `Update` never times out a promised node (`!IsPromised` in ActiveDreamNode.cs 801), `CancelDream` does not touch `mPromiseNodes`, and the node is not removed (compare `ObjectDestroyedDelegate`, which uses `RemoveActiveNode`).
- Effect: a promised wish about that child sits in the promise bar until the player cancels it by hand; the child is no longer reachable so it cannot complete. Occupies one of 4 slots.
- Mark: verified path; whether the four tree ids (13278830765380689789, 556925676188524974, 418218285870389475, 1598595854461485554) carry promisable wishes was not checked.
- Fix sketch: after `KillNode` call `RemoveActiveNode(node)` (as the subject-destroyed path does) when `node.IsPromised`. Leave non-promised nodes as EA has them.

### 5. SetToUpdate during Update is lost (M 3394-3408; EventTracker.cs 160-165; M 826-952)
- Evidence: the drain runs `Update()` then `sNeedToUpdateList.Remove(sNeedToUpdateList[0])`. `Update` sets `mFullUpdate = false` at its top. Anything that runs inside it and calls `SetToUpdate` (OnCompletion sends `kPromisedDreamCompleted`; listeners then run `RunFeedbackFunction`, which calls `SetToUpdate(false)` and `AddCompletedNode`; `RemoveActiveNode` raises UI events; `TimerCallback`) finds the manager already in the list (`Contains` is true), adds nothing, and the drain then removes it. The re-set `mFullUpdate` / the new `mCompletedNodes` entry wait for the next unrelated SetToUpdate on that manager.
- Effect: a wish completed by an event raised from inside the update (for example one completed by another wish's fulfilment) is fulfilled, scored and rewarded late, at the next event that happens to touch that Sim's manager. Usually seconds to minutes; unbounded for a Sim with no further wish events.
- Mark: verified (control flow); frequency inferred.
- Fix sketch: in the existing `WlMgr.UpdateHook` (WishListenerFixes.cs), after EA's `Update` returns, if `mFullUpdate || mCompletedNodes.Count > 0` call `SetToUpdate` again after the drain has removed the manager (or push to the end of `sNeedToUpdateList` and let the drain's `Remove(list[0])` take only the first occurrence: `Remove` removes the first match, which is index 0, so adding a second copy at the end works). No new work is invented; EA's own update runs, only not lost.

### 6. Death keeps nodes but drops their index entry (M 652-681)
- Evidence: for a dead Sim (`flag2`) nodes where `IsValidAfterSubjectDeath()` are skipped with `continue` (line 657), but line 680 `mPotentialRemovalNodes.Remove(obj)` removes the whole list for that id afterwards.
- Effect: those wishes (about a dead Sim, valid after death) stay active but unfiled; when the SimDescription is later destroyed (`kDnPSubjectDestroyed` with `WasSimDescriptionDestroyed`) the lookup finds nothing, so they never get the subject-gone clean-up; they linger until their own `Test()` throws them away. Memory plus one stale wish.
- Mark: verified.
- Fix sketch: build a `kept` list while looping; after the loop write `kept` back if non-empty, else `Remove(obj)`. Do it in the same hook as finding 3.

### 7. `PartialUpdate` enumerates a list its own events can append to (M 954-1010; ActiveDreamNode.cs 1781-1798; ActiveDreamNode.cs 812-875)
- Evidence: the third loop is `foreach (mCompletedNodes)` and calls `OnCompletion()`, which sends `kPromisedDreamCompleted`. A wish listener on that event runs `RunFeedbackFunction` -> `AddCompletedNode` -> `mCompletedNodes.Add` during the enumeration (`InvalidOperationException`). `mCompletedNodes.Clear()` (line 1009) is then skipped, so after `dnpupdateguard` swallows the exception the already-processed nodes are replayed on the next partial update: a repeatable node is added to `mSleepingNodes` twice.
- Mark: inferred. I could not find a listener on `kPromisedDreamCompleted` in code (Telemetry only); data-bound wishes use event ids from XML that I did not search.
- Fix sketch: copy `mCompletedNodes` to a local list and clear it before the loops; process the copy. Also covers the full-Update leftover below.
- Related, verified: the full `Update` only calls `RemoveCompletedNode` inside the `PromiseParent` test (M 859-866), so a completed node whose promise parent was broken or completed stays in `mCompletedNodes` and is processed again by the next `PartialUpdate` (same duplicate sleeping add for repeatables).

### 8. NodeSubject equality (ActiveNodeBase.cs 342-387, 399-436, 617-628)
- Evidence: `operator ==` has no case for `PhotoSubject` (falls out of the switch and returns false unless the same reference) and `GetHashCode` falls to `base.GetHashCode()` for it. The key group in `==` also contains `OccultType`, which compares `mKeyValue`, but `OccultType` subjects are created through the `ulong` overload (DreamNodeInstance.cs 584-588) and hashed, exported and printed by `mEnumValue`: `mKeyValue` is null for all of them, so two different occult types compare equal.
- Effect: `IsNodeAlreadyActive` / `AddToReferenceList` use `==`, so photo-subject wishes are never seen as already active when the subject object differs (duplicate "photograph X" wishes) and the canonical cache `sNodeSubjects` never dedupes them (one entry per Create, kept until world shutdown). Occult subjects compare equal across types in the same node reference (limited by NodeReference including the instance id, so low impact).
- Mark: verified code; game effect inferred.
- Fix sketch: patch `NodeSubject.op_Equality` and `GetHashCode` to add `PhotoSubject` to the key group and move `OccultType` to the enum group. Changes only which duplicates are suppressed.

### 9. Stale MiniSimDescription cache (ActiveNodeBase.cs 154-169, 306-340)
- Evidence: the getter caches the first lookup, `SimDescription.Find(id)` else `MiniSimDescription.Find(id)`, and returns it forever (only `Reset()` clears it). `dnpsubjectcache` clears a cache only when the SimDescription is invalid and "leaves MiniSimDescriptions alone".
- Effect: a subject that was an out-of-town Mini when the NodeSubject was first used, then moves in or is spawned as a full SimDescription, keeps resolving to the Mini: `RunCheckFunction` sees `simDescription2 == null` and in a travel world sets `flag` (non-visible, ActiveDreamNode.cs ~1740), and `mSim` stays null. Wishes about that Sim never show / never complete until reload.
- Mark: inferred (not reproduced).
- Fix sketch: extend `DnpSubjectCachePass`: if the cached object is a `MiniSimDescription` and `SimDescription.Find(id) != null`, null the cache.

### 10. Active but never-displayed "shadow" nodes (M 1050-1085, 1403-1480)
- Evidence: `ScoreChildren` activates and files every `kBecomeActive` child with weight > 0 or children before `SelectDreamsToDisplay` chooses. In the selection loop a candidate that fails the display test (`NodeIsAlreadyDisplayed`, not visible, tutorial) is removed from `value` with `value.Remove(scoredNode)`, and the cleanup loop below only visits what is left in `value`, so that leaf stays active, listener on, with no display and no cleanup.
- Effect: until its own `SatisfyTime` ends, it listens on every matching event, can complete unseen, and blocks re-creation through `IsNodeAlreadyActive`.
- Mark: inferred (needs the count of such nodes; a census of `mActiveNodes` vs `mDreamNodes + promised` would measure it).
- Fix sketch: after the selection loop, for each candidate dropped from `value` with `Children.Count <= 0`, call `RemoveActiveNode`, as the existing cleanup does for unselected ones.

### 11. `PromiseDream` returns true without a slot (M 1654-1694)
`NumPromisedDreams() < 5` is the only test; `AddDisplayedPromiseNode` can still return false (regular slots 1-4 full while slot 0 is empty, no lifetime wish yet or after `RemoveExistingLifetimeWish`). The method then returns true with the node not promised, so callers (`DreamsAndPromisesModel.DreamPromised`) believe it succeeded. Verified, tiny. Fix: return the result of `AddDisplayedPromiseNode`.

### Smaller verified notes
- `OnLoadFixup` removals (M 3128-3141) skip `mPotentialRemovalNodes`, so persisted index lists keep nodes it just dropped. Memory only; save-side, so left alone (no save work).
- Two constructors' listeners both on `kDnPSubjectDestroyed` run `ObjectDestroyedDelegate` twice per event (covered by `dnpdisposelistener` for the one it moves).
- `NullDnPManager` leaves the manager in the static `sNeedToUpdateList`; it runs one last `Update` on a manager with no listeners. Harmless.

## (b) Performance

Context: docs/73 measured wish work at about 0.3% of managed samples (`RunFeedbackFunction` 30% and the drain 20% of the 1.1% spent in event dispatch). None of the following is a hot spot; they are listed for completeness, ranked by effort.

| # | Where | Cost | Fix |
|---|---|---|---|
| P1 | `AddToReferenceList` (M 483-499) | dead loop: the `foreach` breaks on a subject match but IL_0057 `leave.s` falls into the unconditional `Add` (IL_0067 `callvirt List.Add`); O(n) compares per add, duplicates always added | drop the loop (EA's behaviour is "always add") or make it `return` on match |
| P2 | `NodeReference` is a class, new per `Add/Remove/IsNodeAlreadyActive` (M 186-218, 483-524) | allocation per call, per scored child | cache or make a struct |
| P3 | `ObjectDestroyedDelegate` registered per manager on `kEventObjectDisposed` (after `dnpdisposelistener`) | every object disposal runs N manager callbacks: casts + one dictionary lookup each; N = household Sims. Estimated 10^4 disposals x 8 Sims on a lot unload, a few ms | one static listener that does the lookup per manager only when any manager has a non-empty `mPotentialRemovalNodes` |
| P4 | `SelectDreamsToDisplay` (M 1059) | `value.ToArray()` per pick | reuse one array |
| P5 | Sim selection: manager static `OnSelectedSimChanged` (M 551) does a full Update + forced Refresh and `DreamsAndPromisesModel.OnSelectedSimChanged` Refreshes again | two UI refreshes per switch | skip one |

No O(n^2) over all wishes or all Sims, no per-tick scans, and no first-item returns were found in this namespace; the walker agrees (0 hits).

## Suggested order
1. Finding 1 (timer alarms): memory and persisted alarms, one hook.
2. Finding 2 (exception guard in `RunFeedbackFunction`) plus a counter, to measure whether any function throws.
3. Findings 5, 7 and the full-Update leftover: all three live in `WlMgr.UpdateHook` / one snapshot of `mCompletedNodes`.
4. Findings 3, 4, 6, 11: small, behaviour-visible, each a few lines.
5. 8-10 after a census confirms the trigger.

Needs measuring before building: count of timer nodes per Sim (finding 1), a `catch` counter for wish listeners (2), shadow-node count (10).
