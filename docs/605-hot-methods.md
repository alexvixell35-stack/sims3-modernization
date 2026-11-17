# 605. Hot managed methods without a hook: verdicts, `detailprioexact`, ShortcutOn cache

Written 2026-11-16 (UTC). Static only (decompiled EA code, Mono 2 `mscorlib` via ilspycmd), no game run. Input:
`docs/data/compile-candidates.csv` (docs/207: sampled sim-thread self time, ms/s, BP + IP + SV summed). A local test package
(`ts3fixes-on-kHitchCauseHooks`). Tags: [verified] decompiled code or our source, [measured] the CSV, [inferred] reasoning.

**Caveat on the input [measured].** Each row is 2-6 samples per world (one sample is about 0.43 ms/s), so every figure
below is ±40% or worse. The gains are estimates [inferred], to be replaced by the Check timing lines.

## 1. Verdicts per method

| Method | ms/s | Hook today | Verdict |
|---|---|---|---|
| `Lot.CalcOrig` = EA body of `Lot.CalculateDetailPriority` | 3.8 | yes: `DetailPriorityHook` (docs/74 §6.1, `detailprio` off, not exact within a tick) | **built `detailprioexact` (§3)**. Every pass of every lot builds `ActiveHousehold.AllActors`: `Members.GetActorsOfType` makes a pruned list (AddRange of humans and pets) and a second list of created Sims [verified]. `get_AllActors` is also a region root of 12-21 ms/s per world [measured], partly from here |
| `Sim.RecalculateAllowedRoomsIfNecessary` + `Sim.AllowedOrig` (EA body of `IsAllowedInRoom(ulong,int)`) | 3.1 + 2.9 | yes: `AutRoomHook` (always swapped; `autroommemo` off, docs/97 §7) | not built. Already a versioned cache (docs/74 §7 said the same): `GetLot`, `mAllowedRooms.TryGetValue`, `GetObjectCacheVersion<IPortalConnectionObject>` (a `Dictionary<Type,…>` lookup on `typeof(T)`), then `mAllowedRooms[lotId]` again and the room lookup [verified]. The only repeat is the second `mAllowedRooms` lookup; `mAllowedRooms` is private, so a hook would need reflection per call, which costs more than it saves. Autonomy callers are covered by `autroommemo` |
| `Sim.WaitForExitReason` | 4.9 | yes: `WpSimHook` (`wakepoll` off, docs/211) | not built. The body is one flag test and `Sleep(1)` per tick; the cost is the wake itself. Nothing repeats with the same inputs inside a poll; fewer wakes = `wakepoll` (unseen only) |
| `Lot.UpdateReactions` | 4.8 | yes: `WpLotHook` (`wakeprobe`), pulses throttled by `broadcast` (on) | not built. Per tick on every simulating lot: `list.ToArray()` plus 4-5 property reads per broadcaster [verified]. A reused buffer would save one small array per lot per tick, but a broadcaster callback can yield (docs/207 yield path) and another lot's task then runs, so a shared buffer is unsafe and a per-lot one needs `mSavedData` (private). Gain under ~1 ms/s [inferred] |
| `ReactionBroadcaster.Simulate` | 2.2 | pulse throttled (`broadcast`, on) | not built. Between pulses it only adds `deltaTime` and calls `DrawDebug`; skipping ticks would move `CheckEndCondition` / `Dispose` timing (not exact) |
| `IdleManager.DistressIdle` | 4.2 | no | not built. Per idle tick per Sim: three `Count` tests, three `IsTimerExpired` clock reads, then six `IdleAnimationTrack.Clear` calls, each a Mono `List.Clear` = `Array.Clear` over the whole capacity [verified, mscorlib]. The timed tracks are always empty on entry (only `ScheduleCurrentDistressIdles`, called inside, fills them) [verified]. Skipping an empty Clear is exact except `List._version`, but the track arrays are private: a `DistressIdle` hook needs reflection, and a hook on `IdleAnimationTrack.Clear` saves about one icall per call. ~0.5-1 ms/s [inferred]; not worth a swap |
| `SimWalkStyleRules.GetAdjustedWalkStyleForRoute` | 2.8 | no (cane part: `caneearly`, off) | not built. Per route, not per tick. Repeats: `r.GetPathData(num2)` read twice per sub-path (once discarded), `ObjectFromId<Stairs>` and `<PoolPortalObject>` on the same id [verified]. Exact but < 0.2 ms/s [inferred]; the private helpers it calls cannot be reached from a hook |
| `Lot.UpdateNumZombiesOnLot` | 2.2 | no | not built. Per tick on simulating lots (EP7): `ZombieDanceTimeOut.Instance()` x3, `HasStereoOnTheLot` and `UpdateTimeOutOnTheLots` dictionary lookups; the lot's Sim count only every 3 sim-minutes [verified]. The interval field is private; docs/129 / docs/144 already rank it as a precompute candidate |
| `Ts3Fixes.Fixes.ShortcutOn` (ours) | 2.7 | ours | **rewritten (§2)** |

## 2. ShortcutOn: answers cached per switch-table generation (ours, always on)

**What it did [verified, our source + mscorlib].** Every call: `sSwitches.TryGetValue(name)` (Mono 2's chained
`Dictionary`: string hash + `GetPrev` walk; `Dictionary.GetPrev` is the hottest frame of docs/207 §3), and for the ~99% of
names with no entry, the compiler's string `switch` (a second full hash of the name, then a compare chain) to reach an
`Instantiator.kX` field. 705 distinct names, ~360 call sites.

**Now** (`Fixes.cs`, `SwitchCacheCore.cs`): a `SwitchCache` table keyed by the name's *reference* (call-site literals are
interned: one object per name). A hit is five character reads, a probe and one reference compare. States: `Off` / `On` (no
table entry: the tuning default) or `Live` (the name has an entry: read from the dictionary on every call, one hash instead
of two). A table is valid for one `ts3fixes.switchgen` generation and one key count.

Why it stays exact:
- Writers bump `ts3fixes.switchgen` (`Util.SwitchesChanged` in the agent: `patches`, `event_cost`, `reflect_set`; ts3fixes
  `SwitchesChanged`: `NameCacheCheck`). Without a bump (an older agent build): an added key changes `Count` (new table);
  an overwrite only touches names that have an entry, which are `Live`; only `NameCacheCheck` removes keys, and it bumps.
- Tuning defaults are cached only after `ApplyTuned` ran with `Instantiator.TuningSet` (the loader has written the fields);
  before that every call takes the slow path. Every world load finished starts a new table.
- `IniUnseen()` is memoized by itself. All 613 other case bodies are plain `Instantiator.kX` fields, `true` or `false` [verified].
- Null names still throw from `TryGetValue`; empty names are never stored.
- Threads: an entry is one immutable object published by one reference store; no locks (the game's Mono has no MonoThread
  on workers). A lost entry is looked up again, never paired with another key's answer.
- Residual [inferred]: a third-party runtime write to a ts3fixes `k*` field (NRaas Tuner, if it lists our tuning) takes
  effect at the next world load or switch change instead of at once.

`ModOn` in the agent finds `ShortcutOn` by name: still one method of that name. Tools that read `ShortcutOn("x")`
literals (check-live-map, compat-scan, proven-fixes) are unchanged: no call site was rewritten. A Roslyn rewrite to integer
ids was considered and dropped for that reason; the reference-keyed table gives the same per-call cost with no source change.

**Proof.** Offline: `node tools/selftest/switchcache/run.ts` (reference hits, same-text misses, half-full cap, live entries,
all 705 real names stored with longest probe 9) PASS. In game: `reflect_call Ts3Fixes.Fixes.SwitchCacheCheck` (repro
`switchcache-check`): every name asked so far plus the table's keys, cached vs slow (twice), an override set with a bump,
overwritten without one (must read live), bumped, removed; timing line slow vs cached ns per call; `verdict: PASS`.
A/B: `reflect_set Ts3Fixes.Fixes.sSwCacheOff true` (the set bumps the generation; the next call goes uncached).

**Expected gain [inferred]:** most of the 2.7 ms/s self time, plus ShortcutOn's share of `Dictionary.GetPrev` and the string
compare frames: ~2.5-3.5 ms/s.

## 3. Built: `detailprioexact` (2026-11-16; proven in game, §4)

Switch `detailprioexact` (tuning `kDetailPriorityExact`), default off; `DetailPrioExact.cs` + the existing `DetailPriorityHook`
(`ScriptHotSpots.cs`); counters 2793-2796; repro `detailprioexact-check`.

**What EA does [verified, decompiled].** `Lot.Simulate` calls `UpdateDetailPriority` → `CalculateDetailPriority` on every
pass (each tick on simulating lots, every 30 ticks on the rest). No active household → None; its home lot → Home; else
`foreach (Sim s in Household.ActiveHousehold.AllActors) if (s.LotCurrent == this && s.RabbitHoleCurrent == null) return
PlayerSim;`. `AllActors` = `GetActorsOfType(all ages | all animals)`: a new list with `AddRange(mSimDescriptions)` and
`AddRange(mPetSimDescriptions)`, then a second new list of each description's `CreatedSim` where `HasSameCASAGSFlags`
holds and `CreatedSim` is set.

**Ours.** The same tests over `SimDescriptions` then `PetSimDescriptions` (the lists EA copies), in EA's order, first hit
wins, no allocation. All getters involved are side-effect free and nothing yields, so the interleaving cannot matter.
`HasSameCASAGSFlags` still runs for every description (as EA's list build does), and the scan runs to the end: a null
member list or a null description (where EA throws before testing) returns "EA's body", which throws as before. The
same answer for every Sim, household included. With `detailprio` (the per-tick lot set) on, that path keeps priority and
`detailprioexact` is not used. Loop and order fix in the hook: `ShortcutOn` before `Paused()`.

**Check.** `DetailPrioExactCheck run`: every lot answered three ways in one tick: EA's body over the same public members
(reference), our scan, and the hooked private method through a bound delegate (as `Lot.Simulate` calls it); a difference
counts 2795. Controls: home lot = Home, each member's lot outside a rabbit hole = PlayerSim, one lot with no member = None,
on both paths. `members found` includes the EpLive assert (`DetailPriorityHook` swap in). `result`: counters and a `timing:`
line (EA body vs exact scan, ns per lot pass). Off → BROKEN, on → FIXED.

Counters: [2793] exact answers, [2794] EA's body (null list or description, error), [2795] check mismatches (must stay 0),
[2796] errors.

**Expected gain [inferred]:** CalcOrig's 3.8 ms/s self time drops to the scan (about half), and the `GetActorsOfType` /
`GetPrunedSimDescriptionList` / `AddRange` / list-alloc frames under it go: ~3-6 ms/s summed over the three worlds.

## 4. Next leads

- `Household.get_AllActors` as a region root: 12 / 21 / 12 ms/s (BP / IP / SV) [measured]. The other callers (autonomy,
  situations, UI) are the larger share; a census of them is the next static pass.
- `Lot.GetObjectCacheVersion<T>` / `RuntimeTypeHandle.get_Value` (7.4 ms/s, docs/207 §3): a per-`T` static cache of
  `typeof(T)` is not possible from a hook; native side only.

## 4. In-game results (2026-11-16, Bridgeport)

**`detailprioexact`** (repro `detailprioexact-check`, switch off then on in one load):

| | Off | On |
|---|---|---|
| verdict | BROKEN (every lot answered by EA's body) | FIXED |
| lots compared | 83 | 83 |
| differ from EA | 0 | 0 |
| hook answered from the exact scan | 0 | 83 (119 exact answers in the run) |
| check mismatches (2795) / errors (2796) | 0 / 0 | 0 / 0 |
| cost per lot pass | EA body 8202 ns, exact scan 3363 ns | EA body 7498 ns, exact scan 3267 ns |

- [verified] The verdicts, the controls and the counters above.
- [measured, one load] About 4-5 us less per lot pass. The total depends on how many lots simulate each tick [inferred].

**ShortcutOn answer cache** (always on; repro `switchcache-check`):
- [verified] `SwitchCacheCheck`: PASS. It checked 698 names: 1,396 cached answers compared with the slow path, 0 mismatches. The override flip (set with a generation bump, overwritten without one, bumped, removed) was ok.
- [measured] 516 ns per call on the slow path, 279 ns per call cached. The profiler had measured ShortcutOn at 2.7 ms/s of sim-thread self time.
