# 622. Script CPU hitch fixes: docs/617 leads, docs/620 moodlet bugs, docs/618 MdHook allocation

2026-11-17, static (ilspycmd IL and decompile of `game-copy/assemblies`, our source), r75, built, NOT RUN.
Package: `~/dev/ts3-runs/pkg/ts3fixes.r75.package`. Code: `mods/ts3fixes/src/Hitch622.cs` plus the hook sites named below.
Counter block 3490-3519 (`fixes-3490`); 3490-3513 used, 3514-3519 free. Every switch is default off.
Tags: [verified] read in IL/decompile or our source, [inferred] reasoning.

## 0. Skipped, and why

| Lead | Verdict |
|---|---|
| docs/617 §4 Stereo `RemoveInteractionByType` `is` pre-test (EA rank 3) | **Skipped.** `Stereo+DanceTogetherA+Definition` has a subclass: `FairyHouse+DanceTogetherA+Definition` in Sims3GameplayObjects [verified, `ilspycmd -il` over all 10 assemblies in `game-copy/assemblies`, one `extends` hit]. EA's loop compares the exact type, so an `is` test alone would also match FairyHouse entries. An `is` filter in front of the exact compare would stay exact but only saves the GetType calls on entries of other types, and the rule for this item was "only if it has no subclass". |
| docs/617 §3 second item (read `AllActors` only when an entry is third-party) | **Not done.** The per-actor `ProcessLifeEvent(IActor, LifeEventData, Event)` is private and `LifeEventData` is a private class, so our copy of the loop would need `MethodInfo.Invoke` (an `object[]` per call), which costs more than the list it saves. The id check (§5) is the exact part. |
| docs/620 F3 `Buff.OnReAddition` signed compare | **Skipped: EA intent not provable.** 12 of the 20 overrides call `base.OnReAddition`, so a change in the base changes them too (PhoneJunkie raises the value and then calls base; Fear clamps to a negative cap). EA's "stronger" rule with `Math.Abs` exists only in `BuffSourcesList.AddOther/AddPaused`, not in re-addition, and a replacement of a tiered buff (`existingBuff.mBuff.BuffGuid != guid`) may mean "milder wins" on purpose. Low impact (docs/620). |
| docs/620 O1 `UpdateBuffs` per-update allocations | **Skipped: no gain shown, not simple.** The game's mscorlib has no `System.Reflection.Emit` (ilspycmd type list), so a non-allocating copy of `UpdateBuffs` needs three `FieldInfo.GetValue` per call (`mValues`, `mAutosolvableBuffs`, `mActor`) to save two small objects, and replacing EA's body inside `MdHook` would step around the stacked `buffupdate` hook's out-of-sync catch. After §6 the per-entry `TimeoutSources` lists go away with the empty entries, which was the growing part. |
| docs/620 F2 stale paused sources across travel | **Skipped**: not cheap (an hourly object-id walk of every Sim's source lists) and the counter block has no room left for it after §1-§8; the stale-source mechanism is [inferred]. |
| docs/617 §5, §9-§12 | Out of this list (traffic, hourly passes, teen curfew, venue alarm). |

## 1. `autmetarows` (docs/617 rank 1, ours): AddObj rows kept per meta object [verified]

**Cause.** `TryRequest` (AutShadow.cs) calls `AddObj` for every public meta object, every known lot, the current, home and
career lots. Per ad, `AddObj` called `InteractionDefinition.GetType()` (Mono icall; the s88 profile's top self frame),
probed `sDefIndex`, and for allow-listed pairs with a scoring function called `SfAllowed` (`GetInvocationList`,
`Assembly.GetName` twice, the allow-list search, `L3Ok`). Per object it called `ImplVerdict(m.GetType())`.

**Why a cache is exact.** Those answers depend on the object's runtime type, the definition object (its type never
changes), the scoring delegate (immutable) and the guard state, which only `GuardSweep` rewrites (`sDefIndex`,
`sDefGuardHit`, `sImplGuardHit` through `Census`/`MarkOwner`, and `sImplVerdict.Clear()`) [verified: the only writers].
`GuardSweep` now bumps a generation that drops every row. Per object we keep, per ad index, the definition seen there and
its verdict, and the delegate seen there with its `SfAllowed` answer; a different definition or delegate at that index is
recomputed, so build/buy and load edits are seen. `L3Ok` inside `SfAllowed` is then cached until the next sweep, the same
contract `sImplVerdict` already has. The table is keyed by object identity (`RuntimeHelpers.GetHashCode`), cleared at
4096 objects and at every sweep (each load). `[122]` still counts every `-3` pair.

**Switch / counters.** `autmetarows` (`kAutMetaRows`): [3490] pair rows from the cache, [3491] rows computed, [3492] check
mismatches. The switch is read once per request (`sMetaRowsOn`).

**Check.** `MetaRowsCheck run`: every public meta object and lot, each ad verdict by the uncached code and by the rows,
two passes (fill, hits); prints the per-request cost with the switch off (GetType calls, probes, `SfAllowed` calls).
FIXED = on, no mismatch, and the second pass all hits.

## 2. `buffremovemerge` (docs/617 rank 5, docs/620 O3): one lookup on the no-op remove [verified]

**Cause.** EA [verified, ilspycmd `BuffManager`]: `RemoveElement(BuffNames)` calls `RemoveElement(ulong, true)`, which
(1) with clear and a ref-count entry removes the entry, (2) with a pending delayed timer for the buff removes the timer
and returns, (3) calls `GenericManager.RemoveElement` (`TryGetValue`, remove, `OnRemoved`). For an absent buff with no
timer and (no clear, or no entries) nothing changes. Our path on that no-op was `BuffRemoveHook` (`ShortcutOn`,
`HasElement`, two `FieldInfo.GetValue`), `BBrdHook` (`HasElement`), EA's `ContainsKey`, the delayed-list enumerator and
`TryGetValue`. `Motive.UpdateMotiveBuffs` makes this call for every remove trigger on every motive tick.

**Fix (sim thread).** `BuffRemoveHook` passes straight through; `BBrdHook` (the funnel every overload reaches) does one
`HasElement` and two integer reads and returns on the no-op. The two private field references (`mBuffCountList`,
`mDelayedBuffs`) are memoized for the last `BuffManager` [verified: the only `stfld` are in the two constructors]. An
absent buff that is not a no-op (a timer pending, or clear with any entry) runs EA's body once, without `BrPending`
(`buffremovedelayed` acts only on live buffs). A live buff takes the unchanged `buffremovedelayed` path, its pending test
read from the memoized list. No `DynamicMethod` getters: the game's mscorlib has no `Reflection.Emit`.

**Switch / counters.** `buffremovemerge`: [3493] no-op removes after one lookup, [3494] removes to EA's body, [3495]
errors (old path taken). With it on, `buffremovenoop`'s [1018]/[1019] stop counting (its test is folded in).

**Check.** `BuffRemoveMergeCheck run`: every Sim x every buff in the static table, remove with clear; the merged rule's
no-op vs EA's own state (absent, no entry, no pending timer). A no-op where EA would act is a mismatch.

## 3. `swslots` (docs/617 rank 6, ours): switch reads on per-tick hooks [verified]

**Cause.** `ShortcutOn` costs `SwCurrent()` plus `SwitchCache.Get(name)` (string hash and probe) per call.

**Fix.** A hot site takes a slot once (`static readonly int kSwX = SwSlot("name")`) and asks `SwOn(kSwX)`: while the
`SwitchCache` built for the current generation and key count is the one the slots were filled for, the answer is an
array load; otherwise every slot is refilled through `ShortcutOn` (and the `swslots` switch itself is read there). Off:
`SwOn` is `ShortcutOn`. Converted: `HcLotHook` door/detail (`alarmtime`), `DetailPriorityHook` (`detailprio`,
`detailprioexact`), `BuffRemoveHook` (`buffremovenoop`), `EcHook.ProcessEvent` (`eventcost`), `MotivesUpdateHook`
(`buffbufcheck`, `motivebuf`, `motivebufcheck`), `LiveMeta` (the seven `aut*` switches; `autseengame` is read once into
a local instead of up to four times), and every new switch in this doc. `tools/static/check-live-map.ts` now maps
`SwOn(kSwX)` to its name, so `CheckLiveMap.cs` keeps the same hook rows.

**Answers.** Same as `ShortcutOn` under docs/605 §2's rule (every writer bumps `ts3fixes.switchgen`; the current agent
does). One difference: a table entry overwritten with no bump by an older agent build is read live by `ShortcutOn` and
seen by the slots only at the next bump.

**Switch / counters.** `swslots`: [3496] refills, [3497] check mismatches, [3498] check runs.

**Check.** `SwSlotsCheck run`: every slot vs the uncached answer (twice), a probe switch flipped through the table with
bumps, and the ns per ask of both paths.

## 4. `stuckwatchscan` (docs/617 §8, ours): one Sim scan for both watchdog passes [verified]

**Cause.** Every 10 sim-minutes `StuckWatchdog.Simulate` ran `Check` and `CheckIdle` back to back, each with its own
`Queries.GetObjects<Sim>()` and a new `keep` dictionary copied into `mSeen` / `mStill`; `CheckIdle` also made a new queue.

**Fix.** One scan per round, passed to both passes. Nothing yields between them, and `Check` only resets or fades (the
fade's destroy runs from an alarm later), so the second query would return the same Sims; the shared scan also skips
destroyed Sims. The `keep` dictionaries and the queue are reused: the filled one is swapped in instead of copied (same
contents). Spreading the walk over ticks (docs/617's last step) is not done: it moves verdicts in time.

**Switch / counters.** `stuckwatchscan`: [3499] idle passes on the shared scan, [3500] idle passes on their own scan
(switch off), [3501] check mismatches.

**Check.** `StuckWatchScanCheck run`: the shared scan (destroyed skipped) vs a fresh query, same Sims; prints the per-round
cost both ways.

## 5. `lifeeventidfirst` (docs/617 rank 2, EA): the id check before `AllActors` [verified]

**Cause.** `LifeEventManager.ProcessLifeEvent(Event)` (private, reached from `SendLifeEvent` for every `EventTracker`
event) builds `Household.ActiveHousehold.AllActors` before `sData.TryGetValue(e.Id)`, so the list is built for events with
no life event. `AllActors` = `Members.GetActorsOfType` = two new lists, `SimDescription.CreatedSim` (field read) and
`HasSameCASAGSFlags` (age/species flag math): allocation only, no side effect [verified].

**Fix.** Hook `ProcessLifeEvent(Event)`: an id absent from `sData` returns first; everything else (null event, no table,
id present) runs EA's body unchanged, so its own enabled and household tests and its throw on a null table stay. `sData`
is stored only in `Startup` and `Shutdown` [verified: the only stores]; hooks on both mark our id set stale (and keep it
unused while `Startup` runs). The set is a `Dictionary<int,bool>` of the keys (`EventTypeId` is an int enum), so no key
is boxed per event. Without all three hooks the switch stays inert (logged).

**Switch / counters.** `lifeeventidfirst`: [3502] events returned before the list, [3503] events to EA's body, [3504]
errors. Swap audit rows added (Startup may yield; stub remap).

**Check.** `LifeEventIdFirstCheck run`: every `EventTypeId` value, our skip answer vs EA's table lookup, the cached set vs
the live table, and an `AllActors` purity control (two reads, same Sims, members unchanged).

## 6. `buffsrcempty` (docs/620 F1b, EA bug): emptied ref-count entries dropped [verified]

**Cause.** `BuffSourcesList.TimeoutSources` drops timed-out sources but leaves the `mBuffCountList` entry; `UpdateBuffs`
then times the buff out with `RemoveElement(guid, false)`, which keeps the entry. On every later update any live copy of
that buff gets the empty list's `MaxTimeout` (`float.MinValue`) and is removed: an id-less add (Jukebox, MP3 player,
StinkJuice) after an id add (Stereo, smelly objects) lasts one update.

**EA intent.** EA deletes the entry itself when the last source goes by id (`RemoveElement(guid, id, inv)` and
`UnpauseBuff`: `Count == 0` -> `mBuffCountList.Remove`). Readers treat an empty entry as a missing one
(`BuffActiveCount` 0, `GetBuffSourceList` empty; `AddElementFromList`'s only caller, `BuffWarmed.OnRemoval`, tests
`BuffActiveCount > 0` first) [verified].

**Paths that empty an entry** [verified, ilspycmd: the only callers of `TimeoutSources` and `BuffSourcesList.Remove`]:
(1) `UpdateBuffs`' `TimeoutSources(minutes)`; (2) `RemoveElement(guid, id, inv)`'s `TimeoutSources(0f)` after a source is
removed (it re-adds over the emptied entry); (3) load: `mBuffCountList` and `BuffSourcesList` are persisted, so saves carry
entries emptied in earlier sessions. `RemoveElement` by id and `UnpauseBuff` delete at `Count == 0`, and travel
(`StoreBuffsForTravel`) leaves the list as it is. Only `UpdateBuffs` acts on an empty entry (the `MaxTimeout` kill).

**Fix.** In `MdHook` (already on `UpdateBuffs`): entries with `BuffActiveCount == 0` are removed before EA's body (paths 2
and 3; the first time the switch is seen on in a world, or after a household change, every Sim in `LotManager.Actors` is
swept once) and after it (path 1). r78 (first build) swept only after EA's body: on-arm census 22 empty entries left on
Sims not updated since the flip (saved leftovers), verdict UNEXPECTED. One `FieldInfo` read and a count test per sweep;
the key walk runs only when the Sim has entries. All households.

**Switch / counters.** `buffsrcempty`: [3505] entries dropped, [3506] empty entries seen by the Check, [3507] errors.

**Check.** `BuffSrcEmptyCheck run`: empty entries on every Sim now (off: leftovers; on: 0 after one sim-minute).

## 7. `buffpausedtimeout` (docs/620 F1a, EA bug): paused-only re-add with `float.MinValue` [verified]

**Cause.** `RemoveElement(BuffNames, ulong, bool)` with sources left calls `TimeoutSources(0f)`; with only paused sources
(`CountOther == 0`) `MaxTimeout` is `float.MinValue` (the scan starts there and reads only the other list), and EA re-adds
with `AddElementPaused(g, origin, MaxTimeout)`. The paused moodlet shows -3.4e38 time left and, when its last source
unpauses it, is removed on the next update instead of running its tuned time.

**EA intent.** `UnpauseBuff`'s paused-only re-add uses `AddElementPaused(g, origin)`, i.e. timeout `float.MaxValue`,
the buff's tuned default [verified].

**Fix.** Hook `AddElementPaused(BuffNames, Origin, float)`: exactly `float.MinValue` becomes `float.MaxValue`. The only
other caller of that overload passes StirCrazy's hours x 60 [verified, grep of the decompile].

**Switch / counters.** `buffpausedtimeout`: [3508] re-adds given the tuned timeout, [3509] MinValue re-adds with the switch
off, [3510] errors. Swap audit row added.

**Check.** `BuffPausedTimeoutCheck run`: the rule with controls (off keeps MinValue; 120, -1 and MaxValue untouched) and
the hook's swap.

## 8. `mooddriftnoalloc` (docs/618 §2.1, ours): MdHook without allocation [verified]

**Cause.** `MdHook` runs after every `UpdateBuffs` (each Sim, at most once per sim-minute) and allocated `new int[n]`,
`bm.List` (`mValues.Values`, a `ValueCollection`) and its enumerator boxed through `ICollection<T>`.

**Fix (sim thread).** A static scratch `int[]` of the target's length, cleared; a `foreach` over `mValues` read as
`Dictionary<ulong, BuffInstance>` (struct enumerator, same entries, same version check). Integer sums do not depend on
order, so the diff and the correction are the same. Errors stay in `mooddrift`'s [2253].

**Switch / counters.** `mooddriftnoalloc`: [3511] updates summed without allocation, [3512] check mismatches, [3513]
reserved.

**Check.** `MoodDriftNoAllocCheck run`: both walks on every Sim, sums compared.

## 9. Test pairs (smallfix phase)

All Checks follow `run` / `result` and print `verdict: BROKEN` off, `verdict: FIXED` on (B582Line); repros
`<switch>-check` in `tools/harness/repros.json`.

```
autmetarows MetaRowsCheck
buffremovemerge BuffRemoveMergeCheck
swslots SwSlotsCheck
stuckwatchscan StuckWatchScanCheck
lifeeventidfirst LifeEventIdFirstCheck
buffsrcempty BuffSrcEmptyCheck
buffpausedtimeout BuffPausedTimeoutCheck
mooddriftnoalloc MoodDriftNoAllocCheck
```

Notes: `MetaRowsCheck` needs AutShadow's guard sweep done (a world with the shadow ready); `BuffSrcEmptyCheck` on needs a
sim-minute after the flip; `stuckwatchscan`'s counters grow every 10 sim-minutes. For timing, compare the s88 sampler
stacks of `TryRequest`/`AddObj`, `BuffManager.RemoveOrig`, `ShortcutOn` and `SendLifeEvent` with all eight on vs off.
