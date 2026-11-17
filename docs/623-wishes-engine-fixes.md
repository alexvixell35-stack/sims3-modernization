# 623. Wishes engine fixes (docs/619 findings 1-6), built 2026-11-17

Source: `docs/619-wishes-engine-review.md` (static review). Code: `mods/ts3fixes/src/Wishes86.cs`, plus the shared `WlMgr.UpdateHook` in `WishListenerFixes.cs`. Counter slots 3520-3540 (`fixes-3520`; 3541-3549 of the handed block are free). All five switches are default off and are EA bug fixes. Household Sims are included, because each fix restores what EA's own code meant to do: nothing is skipped, cut, faked or throttled.
Marks: **verified** means read in IL (`ilspycmd -il`) or decompiled code, and the effect follows from the code. **Inferred** means it was not seen live. **Status: NOT RUN.** The package (r76-wishes) is built but not tested in game.

Hooks: `W86TimerHook` (ActiveTimerNode), `W86FeedbackHook` (ActiveDreamNode), `W86MgrHook` (DreamsAndPromisesManager). Each hook class holds only hook/stub pairs on one game class, so `swapdelegates` maps it. This matters for `ActiveTimerNode.TimerCallback`, which a saved alarm names.

Every Check builds its own nodes, and three of them build their own test manager (EA's private parameterless ctor, so no listeners). A test manager is never the active manager, so no UI event fires and no sting plays, and the failure notice is counted instead of shown. Each check reports BROKEN with its switch off and FIXED with it on, plus controls. Repros: `<switch>-check` in `tools/harness/repros.json`.

## §1 timeralarmleak (3520-3524), docs/619 finding 1
- **Bug [verified]:** the `ActiveTimerNode` ctor and `SetupFromExport` add an `AlwaysPersisted` repeating alarm (IL `ldftn TimerCallback` -> `GameObject::AddAlarmRepeating`). Its `RemoveListenerFromTracker` is empty. That method is what every leave path calls: `RemoveActiveNode`, the duplicate throw-away in `ScoreChildren`, the refreshed-tree removal in `OnLoadFixup`, and `ClearActiveNodeListeners` from `Sim.NullDnPManager` (ResetDnP, Sim made unselectable, travel). On all these paths the alarm stays. It keeps the dead node reachable and calls `SetToUpdate(full)` on the Sim's current manager every `TimeBetweenSatisfies` hours, until `NumberOfSatisfies` is reached.
- **Fix, part 1:** a timer node's "listener" is its alarm. `RemoveListenerFromTracker` now removes it the way `KillNode` does (`mOwner.RemoveAlarm`, handle set to invalid).
- **Fix, part 2 (alarms already out there):** EA's `TimerCallback` already ends a node that has lost its owner or its node instance (`WasKilled`, `mCount = int.MaxValue`, `RemoveAlarm`).
  - **Loaded-save orphan [verified]:** a save keeps the orphan's alarm, but `mNodeInstance` is `[Persistable(false)]` and only nodes still held by a manager get `OnLoadFixup`. So EA's own guard removes a loaded orphan at its first fire.
  - **Same-session orphan:** here the node instance is still set, so EA's guard misses it. When the owner's current manager does not hold the node (a missing manager counts as not holding it), the callback now takes EA's kill path. A node that is in its manager runs EA's callback unchanged.
- **Check:** `TimerAlarmLeakCheck` uses its own `TimerNodeInstance` (3 satisfies, 24 h apart) and timer nodes for the selected Sim. It tests the leave path, an orphan fire, and, as the control, an orphan with no node instance, which EA's guard ends either way. It removes every alarm it made. In the off arm, the orphan fire asks the Sim's manager for one full update, as EA does.

## §2 wishfeedbackkeep (3525-3528), finding 2
- **Bug [verified, EventTracker::ProcessEvent IL_0069..IL_0081]:** `catch Exception -> RemoveListener`. A wish listener is `ActiveDreamNode.RunFeedbackFunction`, which runs a reflection-bound feedback function. If that function throws once, the listener is gone for good: the wish stays in its slot and can never complete until `Reset(true)` or a load.
- **Fix:** if the call throws and the node is a live wish, the listener answers Keep, which is EA's "not completed" answer. A live wish has an owner, its description, `CreatedSim`, a wish manager, its node instance and its listener. These are every reference `RunFeedbackFunction` makes before it calls the function, and they are read after the throw, so the hot path costs nothing. A broken node still throws, and EventTracker drops it as before.
- **Not capped:** a function that throws on every event now throws on every event, at the cost it had before it was dropped. The 3525 counter measures whether any shipped function throws.
- **Check:** `WishFeedbackKeepCheck` swaps EA's wrapper for one that throws, for one call on a listener-less node. The control is a node with no listener, which still throws.

## §3 wishsubjectgone (3529-3533), findings 3 and 6
- **Bug [verified, ObjectDestroyedDelegate]:**
  - `RemoveActiveNode(item, false)` (IL_0115) runs `BreakPromise`, which empties the promise slot. So the next test, `RemoveDisplayedPromiseNode(item)` (IL_012b), is always false, and EA's `DreamFailureTNS` notice (IL_0171) and `sting_dream_fail` (IL_01e4) never happen. `BreakPromise` already fired the UI break and the midlife-crisis update.
  - For a dead subject, the wishes that are valid after death are skipped, but `Dictionary::Remove(obj)` (IL_01ce) drops the whole index entry. When the SimDescription is destroyed later, the clean-up for those kept wishes finds nothing.
- **Fix (EA's body runs unchanged):**
  - Before it runs, the subject key is derived as EA derives it. The fix notes which removed nodes were promised and which nodes are kept.
  - After it runs, each removed promised wish gets EA's notice when the event names the SimDescription (the `kEventObjectDisposed` branch has none, and EA's statement would throw there). The sting plays once if this is the active manager.
  - Kept nodes that are still active are filed again under the same key. There is no second `FireBreakPromise` or crisis update.
- **Check:** `WishSubjectGoneCheck` uses a test manager holding a promised wish and a valid-after-death wish, filed under a fresh dead SimDescription, and sends `kDnPSubjectDestroyed`.

## §4 wishupdatelost (3534-3537), finding 5
- **Bug [verified, EventTracker::ProcessEvent IL_01ef..IL_020d]:** the drain runs `Update()` on `sNeedToUpdateList[0]`, then calls `Remove(list[0])`. `SetToUpdate` adds a manager only when it is not already listed. So a request made while that manager's own Update runs (a wish completed by an event that Update raised) is lost until some later, unrelated event.
- **Fix:** the `SetToUpdate` hook notes a request for the manager whose Update is running while that manager is listed. After Update, if the drain is about to remove it (it is draining, and the manager is first in the list), the manager is added once more at the end. `Remove(list[0])` then takes the first copy, and the requested update runs in the same drain.
- **Loop guard:** at most 8 such re-adds in a row per manager (counted in 3536).
- **Check:** `WishUpdateLostCheck` lists an empty test manager and drains it with a `kEventNone` SendEvent. The manager asks for one more update inside its Update: off gives 1 update, on gives 2.

## §5 socialworkerpromise (3538-3540), finding 4
- **Bug [verified, ChildTakenBySocialWorkerCallback IL_0083/IL_0097]:** EA calls `KillNode` and `CancelDream` but never removes the node. `KillNode` only sets `mTimeActive = SatisfyTime`, and `ActiveDreamNode.Update` times a node out only when `!IsPromised`. So a promised wish about the child stays in its slot for good. EA's own subject-gone path removes such a node with `RemoveActiveNode`.
- **Fix (EA's body runs unchanged):** afterwards, each node EA just killed that is still promised is removed with `RemoveActiveNode`, which breaks the promise. These are the nodes matching EA's filter: not one-off, Sim subject equal to the child, one of the four trees, `WasKilled`. Unpromised nodes keep EA's timeout path.
- **Check:** `SocialWorkerPromiseCheck` uses a test manager with a promised and an unpromised wish from one of the four trees, both about the selected Sim. It reports "none of the four trees loaded" when those trees are missing.
- **Not checked:** whether the four trees hold promisable wishes in the shipped data. If they hold none, the fix is a no-op.

## Not built
The rest of docs/619 (7-11, P1-P5) was not in scope for this build. 7 has an inferred trigger, and 8-10 need a census first.

## Next
Run r76-wishes with the five `-check` repros (off, then on), then a clean run.
