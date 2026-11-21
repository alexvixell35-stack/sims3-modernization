# 620. Moodlets review: BuffManager, Buff, BuffInstance, MoodManager and their per-tick callers

2026-11-17, static, NOT RUN. Scope: `Sims3.Gameplay.ActorSystems` `BuffManager`, `Buff`, `BuffInstance`, `Buff*` subclasses,
`MoodManager`, and the callers that add and remove buffs every tick (`Autonomy.Update`, `Motive.UpdateMotiveBuffs`,
broadcaster callbacks). Tools: ilspycmd (decompile and `-il` of `BuffManager`), `game-copy/nraas-src` (ErrorTrap
`DerefBuffInstance*`), grep over the decompile, the profile `probe-out/profile/s88-1145-managed.jsonl` (566 samples).
Claim marks: **[verified]** read in decompile or IL; **[inferred]** our reading of how often or in which flow it happens.
Nothing was run and nothing was built. Line numbers are lines of `game-copy/decompiled/Sims3GameplaySystems/Sims3.Gameplay.ActorSystems/BuffManager.cs`
(`BM`) and `Buff.cs`.

## 0. Already covered, skipped

`buffupdate` (docs/510, out-of-sync walk), `buffremovedelayed` (Batch589cLogic), `buffremovenoop` + `permamemo`
(ExactMemos, docs/95 §5.5, docs/245 §5), `mooddrift` + `moodpending` (docs/545), `moodtankreset` (Batch589bLogic;
I re-found the same `mPreviousMotiveToTank` bug independently), `buffstate` (BuffSharedState, six shared-field buffs),
Stereo/EnjoyingMusic remove+add per 20-minute pulse (docs/74 §3, S3PP caches it), Delusional timeout making the random
traits permanent (intended: `OnTimeout` clears the stored traits on purpose). docs/589 line 89 lists "TimeoutSources can
return float.MinValue" as a one-line candidate, never built; F1 below is the full analysis of it.

## 1. Logic bugs

### F1. A ref-counted buff's timeout comes from `BuffSourcesList.MaxTimeout`, which is `float.MinValue` when no timed source is left (three effects)

Evidence [verified, decompile and IL]. `BuffSourcesList.TimeoutSources` (BM 295-331, IL `BuffSourcesList::TimeoutSources`):
`num = float.MinValue`; only `mSourcesListOther` entries with `Timeleft != -1` set it; paused sources are never looked at;
`mMaxTimeleft = num`. So with an empty or paused-only list `MaxTimeout == float.MinValue`. Three callers use it:

- **F1a. Paused-only re-add gets a timeout of -3.4e38** (`RemoveElement(BuffNames,ulong,bool)` BM 764-790). Two paused sources
  (A, B) of one buff, then `RemoveElement(buff, A, inv)`: `Count > 0`, `TimeoutSources(0f)`, `CountOther == 0`, so
  `AddElementPaused(guid, origin, MaxTimeout)` runs with `MaxTimeout == float.MinValue`. `AddBuff` copies it
  (`timeoutLength != float.MaxValue` -> `newBuff.mTimeoutCount = timeoutLength`, BM 1071+). The instance is paused, so it
  sits there; `UITimeoutCount` (BuffInstance) returns it raw for the moodlet tooltip. When the last source goes through
  `UnpauseBuff` (BM 1527-1552: `Count == 0` -> `TogglePause(false)`), the buff is unpaused with that timeout and the next
  `UpdateBuffs` removes it (`< 0f`), instead of letting it run its normal duration. Callers that use paused sources with
  an id and `inventory: true`: `Inventory.UpdateBuffCounters` (trash piles, flame fruit, disgusting food in a Sim's bag,
  Inventory.cs 1528-1576, which calls `RemoveElement(buff, id, inventory: true)` when an item leaves).
  Effect in game: Disgusted/CozyFire from carried items drops one tick after the last item goes, not after its tuned
  duration; with 2+ items the moodlet tooltip shows a garbage time left. Frequency [inferred]: every Sim carrying two
  bad items.
- **F1b. Emptied entries are never deleted, so a later non-id add of the same buff is killed next update.** `UpdateBuffs`
  (BM 1563-1629) removes a timed-out buff with `RemoveElement(guid, clearRefCounts: false)`, which leaves its
  `mBuffCountList` entry (only `RemoveElement(guid)` / the id overload with `Count == 0` delete it). Next, for any buff
  with an entry the loop does `item2.mTimeoutCount = mBuffCountList[guid].MaxTimeout` (BM 1590-1593) and, with an empty
  list, that is `float.MinValue`, so `< 0f` and it is removed in the same pass. Same buff, different caller: EnjoyingMusic
  is added with an id by Stereo, MusicBox, DJ booth, Karaoke (1435), musical instruments, FairyHouse, BeachTowel, phone
  hold; and without an id by Jukebox (Jukebox.cs 1981), PortableMp3Player (873), Violin (342), KaraokeMachine (483, 602,
  792, 986). Disgusted: id from vomit/food/sink/smelly broadcasters, no id from StinkJuicePotion (140) and VomitPile
  cleaning (131). A Sim who once heard a stereo (entry created, source timed out, entry stays) gets every later Jukebox
  or MP3 EnjoyingMusic cut to one update tick. Effect: the moodlet flashes and the mood bonus the player paid for is
  gone. [verified] mechanism; [inferred] frequency, no live check yet.
- **F1c. `mMaxMood` and `mOrigin` are not recomputed when sources time out** (`TimeoutSources` removes from
  `mSourcesListOther` but never calls `FindMaxMood`, BM 295-331). The Stereo/CozyFire path re-adds with
  `mBuffCountList[guid].Mood` / `SourceOrigin` after one source is removed (BM 770-788): the buff comes back with the
  mood of a source that has already expired. Small effect (mood points and origin text), [verified].

Fix sketch (keeps EA's intent: a ref-count entry should die with its last source, and a source list should report what is
still alive):
1. Hook `BuffManager.UpdateBuffs` (we already own `MdHook`): after EA's method, for each `mBuffCountList` entry with
   `Count == 0` remove it (`mBuffCountList` is a private field, read by `FieldInfo`, as `ExactMemos` does).
   Run it only when `mBuffCountList.Count > 0`, so Sims without entries pay one integer test.
2. Replace `float.MinValue` by `-1f`-safe handling: in `AddElementPaused(buff, origin, timeout)` when `timeout <=
   float.MinValue / 2` call the two-argument overload (default timeout) instead.
3. In `TimeoutSources` after the removal loop call `FindMaxMood` (private; reflection once, cached delegate) when
   anything was removed.
   Switch name suggestion `buffsrclist`, counters in the next free block.

### F2. A stale paused source keeps a buff paused for good [inferred, mechanism verified]

`UnpauseBuff(buff, id, inv)` (BM 1527-1552): after removing the source, if `Count > 0` the buff is removed and added again
paused (`CountPaused > 0`). `TimeoutSources` only walks `mSourcesListOther`, so a paused source never expires. A source
whose exit callback never ran (Sim reset or travel while in a broadcaster, object destroyed, docs/589 "Sink: a reset leaks
the Disgusted broadcaster", docs/615 food broadcaster) stays in the list. `StoreBuffsForTravel` (BM 1732-1743) calls
`RemoveAllElements` but leaves `mBuffCountList` and `mDelayedBuffs` as they are. Effect: every later Disgusted from any
object ends paused: the moodlet never times out until something removes it with the id-less `RemoveElement(guid)`.
Fix sketch: in the F1 hook, once per sim hour drop non-inventory sources whose object id no longer resolves
(`GameObject.GetObject(new ObjectGuid(id)) == null`) and re-run EA's `RemoveElement(guid, id, false)` for them, so the
list, the live buff and the mood effect stay consistent; also clear `mBuffCountList` where `StoreBuffsForTravel` runs.

### F3. `Buff.OnReAddition` compares signed values, so a weaker negative moodlet replaces a stronger one (Buff.cs, `OnReAddition`) [verified, low impact]

`if (newBuff.mEffectValue > existingBuff.mEffectValue)` replaces the live buff. For a positive buff that picks the
stronger; for a negative buff (-15 live, -5 new) it replaces the worse with the milder, and a -15 added over a -5 is
dropped (only the timeout may extend). Most negative buffs are added with their tuned value, so the compare is equal and
nothing happens; callers that pass a value: `Embarrassed` (`kEmbarrassedBuffAffect`, career code), `OnFire`, `CryingBaby`,
`HeartBroken` timeout overloads. Practical effect small. Fix sketch if wanted: compare `Math.Abs` when both values are
negative (a prefix on `Buff.OnReAddition` for the non-overriding classes, 20 subclasses override it and keep EA's
rule).

### F4. `kGotBuff` is sent twice on a replacement and once per refresh (BM 1138-1147) [verified]

In `AddBuff` the first `EventTracker.SendEvent(kGotBuff)` happens as soon as an element exists. If `OnReAddition` returns
false (stronger value) the code continues and sends the same event again at BM 1172. Listeners of `kGotBuff` (skill
and "got buff" counters, achievement-style trackers) see two events for one buff, and one event for each no-op refresh
(F-pulse adds). Effect: counters count high; no visible bug found for the common ones. Not worth a fix alone; note when
the next listener review runs.

### F5. `AddBuff(BuffInstance)` (travel re-add, BM 1006-1069) [verified, low frequency]

`bi.mBuff.Version` is dereferenced before `bi == null` is tested. `mValues.Add(bi.BuffGuid, bi)` throws
`ArgumentException` when the Sim already holds that buff; `LoadBuffsFromTravel` has no try/catch, so the remaining travel
buffs are lost and `TravelBuffs` is never set to null (it is retried on the next load and throws again). The path skips
`ShouldAdd`, `ShouldBlock`, the availability flags and the dead-Sim check that the normal `AddBuff` makes, so a buff that
is now blocked (Sim became a mummy: `BuffMummysCurse.ShouldAdd`) comes back after travel. Fix sketch: prefix
`LoadBuffsFromTravel`: skip entries already in `mValues`, wrap each add in try/catch, always null `TravelBuffs`.

### F6. `UpdateBuffs` removes timed-out buffs by guid after calling their `OnTimeout` (BM 1609-1627) [inferred, low]

The removal list holds instances, but removal is `RemoveElement(guid)`. An `OnTimeout` earlier in the list that adds or
replaces another expiring buff (`BuffImpendingEpisode.OnTimeout` adds Delusional) can make the guid refer to a fresh
instance, which then gets removed with its full timeout. Only chained timeouts are exposed (ImpendingEpisode ->
Delusional is a different guid, so safe today). No fix proposed.

### F7. HotHeaded anger multiplier ordering (BM 1071+, `BuffInstance.EffectValueForSim`) [inferred, intent unclear]

`EffectValueForSim` multiplies by `HotHeadedTraitAngryMoodMultiplier` only when `mAxisEffected == Angry`. `AddBuff` calls it
(`newBuff.mEffectValue = newBuff.EffectValueForSim(Actor)`) before it redirects a negative buff of a HotHeaded Sim to the
Angry axis, so redirected buffs never get the multiplier. May be intended. Left alone.

## 2. Unoptimized code that runs often

### O1. `UpdateBuffs` allocates on every call (once per Sim per `Autonomy.Update`) [verified, IL]

IL of `BuffManager::UpdateBuffs`: `IL_0006 newobj List<BuffTimer>::.ctor(IEnumerable)` (a copy of `mDelayedBuffs`, always,
even when empty); `IL_00c8 call GenericManager::get_List()` then `IL_00cd callvirt IEnumerable<BuffInstance>::GetEnumerator`
(the buff walk goes through `ICollection<>`, so the `Dictionary.ValueCollection.Enumerator` is boxed); and per
`mBuffCountList` entry `BuffSourcesList::TimeoutSources` does `newobj List<BuffSourceInfo>` (IL_0008) every call, empty
or not. Entries stay forever (F1b), so a Sim's cost grows with the number of id-based buffs it ever had. `HasDebuffOfSolveCommodity`
(called from `OnRemoved`) and `GetBuffOfSolveCommodity` box the same way.
Profile (s88-1145): `UpdateOrig` is 1 of 566 samples, allocation shows up as GC time, not here.
Fix sketch: only the F1 hook is needed first (entries deleted -> no per-entry lists). Then in `MdHook`: if
`mDelayedBuffs.Count == 0 && mBuffCountList.Count == 0`, run an equivalent walk over the private `mValues` dictionary
by struct enumerator; otherwise EA's method. Must keep: `OnTimeOutUpdated` per live non-paused buff, removal after the
walk, `AttemptToPrioritize`. This overlaps `buffupdate`'s try/catch, so it belongs in the same hook.

### O2. `HasAnyElement(params BuffNames[])`: array allocated at the call site (114 call sites) [verified, IL]

`BuffManager::HasLuckyBuff` IL: `ldc.i4.2; newarr BuffNames; ... stelem.i8 x2; call HasAnyElement`. Every call allocates a
16-24 byte array before the method runs, so a hook on `HasAnyElement` (our `CcBuffs.AnyHook`, CallCensus) cannot remove it.
doc 604: 4.8 M calls in the 1116 runs. The lookups themselves are ~25 ns, the array is gen0 garbage.
Fix sketch (low priority): pick the top callers from CallCensus by caller, rewrite those as chained `HasElement` calls in
the existing offload code, as `AutOffload` already does; do not hook `HasAnyElement`.

### O3. The no-op remove path does four dictionary or list touches plus two reflection reads [verified, profile]

Profile: 8 of 566 samples sit under `BuffManager.RemoveOrig` (4 `GenericManager.RemoveElement`, 3 `Dictionary.ContainsKey`,
1 `ListSorted.GetEnumerator`), reached from `Motive.UpdateMotiveBuffs`. One no-op `RemoveElement(BuffNames)` is:
`BuffRemoveHook` (`ShortcutOn`, `HasElement`, two `FieldInfo.GetValue` for `mBuffCountList` and `mDelayedBuffs`),
`BBrdHook.RemoveHook` (`HasElement` again), EA's `mBuffCountList.ContainsKey`, the `foreach` over `mDelayedBuffs`, and
`base.RemoveElement` (`TryGetValue`). `UpdateMotiveBuffs` (Motive.cs 1909-1950) calls it for every `mRemoveBuff` of every
matching trigger on every motive tick, and `IsPermaMoodlet` + `RemoveElement` for every non-matching trigger.
Fix sketch: fold `buffremovenoop` and `buffremovedelayed` into one hook on `RemoveElement(ulong, bool)` (the funnel all
overloads reach): `if (!mValues.ContainsKey(g) && (!clear || mBuffCountList.Count == 0) && mDelayedBuffs.Count == 0)
return;` with the three private fields read through cached `DynamicMethod` getters instead of `FieldInfo.GetValue`
(docs/245 §5 flagged the reflection cost). One lookup and two integer reads on the no-op path.

### O4. Not a finding

`UpdateMotiveBuffs` "re-adds every pulse": it guards with `HasElement(item.mAddBuff)` before `AddElement`, so only the remove
side repeats (O3). Boundary values that match two overlapping triggers could flip a buff each pulse; I did not check
the motive tuning for overlaps [not verified].

## 3. Leak check on per-buff alarms and listeners

Scan: every `Buff*.cs` with `AddAlarm*` / `AddListener` / `AlarmType.AlwaysPersisted` against a matching remove, and the
`BuffInstance.Dispose` calls (EA's `BuffManager.OnRemoved` never calls `Dispose`; each `OnRemoval` must). Of 70 buff
classes that override `Dispose` all but `BuffImpendingEpisode`, `BuffDelusional` (both empty bodies) call it from
`OnRemoval`; `BuffMeal`, `BuffGoodNectar`, `BuffDivineMeal`, `BuffScared`, `BuffInstanceGotFleas` only chain to `base`.
`BuffMummysCurse`, `BuffEmbracingTheFuture`, `BuffDrowning` remove their handles in `OnRemoval` [verified]. NRaas ErrorTrap
carries 22 `DerefBuffInstance*` cleanups for instances destroyed without `OnRemoval`; 33 more alarm-holding buffs have
none, but their alarms are Sim-owned (`Sim.AddAlarm*`, `DeleteOnReset`) so they die with the Sim. No new per-buff leak
found beyond `buffstate`'s six. One open point [inferred]: `BuffDrowning` for a pregnant Sim sets `mTimeoutCount = -1`
(never times out) and relies on `GetOutOfPool` or `SwimmingInPool`/`PoolPortalComponent` to remove it; if the Sim leaves
the pool by a reset the buff stays until another pool exit, whose `RemoveElement` clears it.

## 4. Ranking

1. F1b (verified mechanism): non-id add after an expired id add is cut to one tick (EnjoyingMusic, Disgusted).
2. F1a (verified, frequency inferred): paused-only re-add with a -3.4e38 timeout.
3. F2 (inferred): stuck paused Disgusted from a stale source.
4. O1 (verified IL): three allocations per Sim per update, growing with F1b leftovers; do after F1.
5. O3 (verified, profile 1.4%): merge the two remove hooks, cached getters.
6. F5, F1c, F3, F4, O2, F6, F7 (small).

Suggested test: probe with `ArtifactData`-free harness runs `buff_churn` / `buff_drift` (docs/545): add EnjoyingMusic with an
id, let it time out, add it without an id, count updates until it disappears; two paused inventory sources, remove one,
read `TimeoutCount` of the live instance.
