# 618: GC pressure leads: per-tick allocators and the [GC] tuning verdict

2026-11-17, static, NOT RUN (no game launch, no build, no commit). Inputs: run `~/dev/ts3-runs/phase-20261117-1133`
(`ts3native.log`, 58 `stall watch: sim thread` samples), `docs/data/jit-hot.csv`, `docs/data/jit-call-edges.csv`, the
`callcensus` lines of that run, and the hot lists of docs/604 (`game-copy/analysis/hotlogic/hotlist*.tsv`).
Already covered elsewhere and left out here: docs/04a §6 (GetObjects arrays, `Lot.UpdateReactions` ToArray,
`GetInRadiusSims`, `RoutePlanResult.ToString`, meta-autonomy list copies, `AlarmOneShot`, `EveryHumanSimDescription`),
docs/617 (CPU leads, `AllActors` in `ProcessLifeEvent`, `ThoughtBalloonManager`), docs/258 (`InternalEquals` lock cost),
docs/13 / docs/199 (GC knobs).

## 1. The evidence first: the 13 GC samples are the test harness, not gameplay [measured]

Stall samples mapped with `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (script `map.py`, §5). The 21 samples with a GC
frame fall into three groups:

| Group | Samples (ms into freeze) | Log lines | What ran |
|---|---|---|---|
| `GC_gcollect` <- managed `GC.Collect` | 194-737 (6), 191, 201-635 (5), 159 = **13** | 1317-1336, 11088-11099 | both inside a `gc-before` ... `gc-after` mark pair: ts3agent `mem_gc` (`MemHandlers.cs:33`, 2 x `GC.Collect()`), and the log line `ui tasks 0 -> 1 by ... Handlers.MemGc` right before the freeze |
| `GC_malloc*` / `GC_allocobj` in a long freeze | 155-590 (4), 301-409 (2) | 1651-1659, 11275-11277 | right after the `p-base` and `p-idle5` marks (snapshot + census work of the harness) |
| same, at world start | 278, 291 | 421, 470 | first tick after load (`mono thread: GC table at first tick`) |

So in this run no GC freeze came from ordinary play. The other four runs of the day (0923, 0949, 1045, 1143) have one
stall sample each and none with a GC frame. docs/617 §12 already says `mem_gc` (1,718 ms) and `leak_census` (801 ms)
are diagnostics. `tools/harness/hw-align.ts` tags hitch-watch hitches in a `gc-before`/`gc-after` bracket as `mem_gc`.
The stall-watch count does not use that tag. **Fix for the evidence:** apply the same bracket filter to `stall watch`
samples, and add the `p-*` marks, before counting GC share.

The heap was never short. Every mark has `gcHeap=353-356 MB`, `gcFree=102-136 MB` and `gcN` 20 -> 40 over the run, so
`GC_collect_or_expand` had room every time.

## 2. Per-tick allocators, ranked

Per-tick counts use this run's rate, 124,669 ticks over 47.6 sim-hours, which is about 2,620 ticks per sim-hour (game
speed varies, so per tick is the stable unit). "Boxes" are 12-24 byte Boehm objects.

### 2.1 Ours (ts3fixes) first: no hook is a large allocator [tool-ranked, read]

ClrHeapAllocationAnalyzer 3.0.0 on `mods/ts3fixes/src`: 7,209 warnings. HAA0201/0202 string concatenation makes up 82% of
them. With log/diag lines filtered out, the code was intersected with the 710 `Patch(...)` targets (`patches.tsv`). The 43
hooks on hot EA methods were checked: `HasAnyElement`, `ActionData.Test`, `Conversation.IsSuitable`, `Autonomy.Update`,
`UpdateBuffs`, `UpdateMotives`, `UpdateMotiveWithDecay`, `UpdateMood`, `UpdateTimer`, `UpdateDetailPriority`,
`UpdateReactions`, `MapTagController.Simulate`, `Queries.GetObjects`, the name/relationship/env-score caches and others.
Explicit `new` is counted too (the analyzer does not flag it). Results:

| Hook | Per call | Rate | Verdict |
|---|---|---|---|
| `MdHook.UpdateHook` (MoodDrift.cs:43-48, on `BuffManager.UpdateBuffs`) | `new int[n]` + `bm.List` (= `mValues.Values`: a `ValueCollection` and a boxed enumerator through `ICollection<T>`) = **3 objects** | each Sim, at most once per sim-minute | the only per-Sim hook that allocates on every call. Fix: a static scratch `int[]` (sim thread only) and `foreach` over `mValues` as `Dictionary<ulong,BuffInstance>` (struct enumerator); exact |
| `AutonomyUpdateHook.UpdateHook` (UnseenThrottle.cs:252) | `(float)FieldInfo.GetValue` = 1 box | unseen Sims, each `Autonomy.Update` | small; read `mLastTime` with a typed accessor if one exists |
| `IstHook.InitHook` (SeasonsHunt594.cs:88-91) | 3 reflection `SetValue`/`GetValue` boxes | each `InteractionInstance.Init` | net saving: it drops EA's `new StackTrace(4, true)` (O(depth^2) frames). Keep |
| `SpatialCacheHook` (SpatialCache.cs:54) | `Array.Clone()` on a hit | each `Queries.GetObjects` | same allocation as vanilla (native also returns a new array). Neutral |
| `ActionWatch` (PlayTrace.cs:76) | enum `ToString` only on a change | per frame | fixed already (comment at :58) |

The loop tasks with the most HAA hits (`OutfitWaitCheck` 128, `AgeUpSwapCheck` 76, `BookHandCheck`, `SaveCleanupSpreadRun`)
are agent checks or nightly passes, not per-tick.

### 2.2 Top 5 overall

| # | Site | Allocations | Rate (estimate) | Evidence |
|---|---|---|---|---|
| 1 | **Enum keys through `DefaultComparer`** (any `Dictionary<TEnum,..>` lookup, `List<TEnum>.Contains/IndexOf/Remove`) | Mono's `DefaultComparer.Equals` IL: `box x` (null test), `box y`, then `constrained. !T callvirt Object::Equals`. An enum does not override `Equals` itself, so the receiver is boxed too: **3 boxes per key compare**. `GetHashCode` is `constrained` (1 box) + `Enum.get_value` (1 box) = 2 [verified: `ilspycmd -il` on game mscorlib, `EqualityComparer`1/DefaultComparer`] | `Enum.Equals` 585,292 calls in the profiled window, all to `ValueType.DefaultEquals` (`jit-call-edges.csv`); docs/258 counts ~420k `InternalEquals`/s (that run included a Verify sweep). Even 10% of that in play is ~40k compares/s = **~120k boxes/s** | callers are not named: the compiled-caller edges hold only 346 + 258 + 69 calls, so they are interpreted EA methods |
| 2 | **`AlarmManager.mTimers` `Dictionary<AlarmHandle, List<Timer>>`** (AlarmManager.cs:234) | `AlarmHandle` is a struct without `IEquatable<AlarmHandle>`, so `DefaultComparer` is used: `box x` + `box y`, and `AlarmHandle.Equals(object)` calls `GetType()` on `this` (a third box). **3 boxes per key compare** | 22 `mTimers` operations in 13 methods: `InternalAddAlarm`/`InternalRemoveAlarm` (each fire of a repeating alarm), `AlarmWillYield`, `GetTimeLeft`, `UpdateAlarmTime`, `RemoveAlarm`, `SetNewAlarmTime`. That is ~3-4 lookups per alarm fire = **~10 boxes per fire**; there is one manager per lot plus the global one [inferred rate: tens of fires per tick in a full world] | IL scan (`il-agg.tsv`: `UpdateAlarmTime` enumkey `AlarmHandle`), source read |
| 3 | **`BuffManager.HasAnyElement(params BuffNames[])`** callers | 1 `BuffNames[]` (16 + 8n B) per call, built by the caller before our `CcBuffs.AnyHook` runs | callcensus in phase-1133: up to 66,035 calls per sim-hour = **~25 arrays per tick**; docs/604 counted 4.8 M (and docs/608 13.7 M) over the night runs | `HAA0101`; `Motive.UpdateMotiveBuffs` alone has 3 call sites (horse-gated) |
| 4 | **`Household.Members.GetActorsOfType`** (via `AllActors` 169 uses, `Sims` 492, `Pets` 108) | `new List<Sim>()` + its array, which grows on `Add`: 2-3 objects per call | `jit-hot.csv` rank 38: **1.27 ms/s** sampled self time, all EA callers; at ~5 us a call this is ~250 calls/s [inferred] | IL `newobj` + `compile-candidates` row 28 (docs/207 lists it only as a compile candidate) |
| 5 | **CommodityKind/SkillNames enum dictionaries on the motive/skill path**: `BuffInstanceCommodityDecayModifier.GetCommodityMultiplier` (`Dictionary<CommodityKind,float>`, called in `Motive.UpdateMotiveWithDecay` :1604 for each decay-modifier buff), `InteractionInstance.Begin/EndCommodityUpdate` (5 lookups), `Begin/EndSkillUpdate` and `AlterSkillUpdateMultiplier` (`SkillNames`, up to 9) | these are the named instances of #1: 3 boxes per compare, 2 per hash | per motive per Sim per motive update (`UpdateMotiveWithDecay` 2.07 ms/s, `UpdateMotiveBuffs` 2.03 ms/s in `jit-hot.csv`) | IL scan `enumkey` (`il-agg.tsv`) |

Other enum-key sites from the same scan, all lower rate: `BuffManager.AddBuff` (`mAutosolvableBuffs`, CommodityKind),
`WeatherManager.UpdateCloudiness`/`UpdateWeatherQueue` (FutureWeather), `RoleManagerTask.Simulate` (RoleType, 4),
`SimWalkStyleRules.GetLocomotionStateForWalkStyle` (WalkStyle; docs/04 fixed only the comparer, not this lookup),
`MetaAutonomyTuning.GetIntensity` (DaysOfTheWeek), and `Lot.CommonVisitTest` (8 closures + 13 params arrays, 0.37 ms/s).

### 2.3 Fix sketches

1. **Enum comparers (covers #1 and #5).** For each enum key type on the hot path (CommodityKind, SkillNames,
   BuffNames, FutureWeather, RoleType, DaysOfTheWeek, WalkStyle), set `EqualityComparer<TEnum>._default` (the field is
   named `_default` in the game mscorlib's `.cctor` IL) by reflection, early in ts3fixes start-up, to a sealed per-enum
   subclass with `Equals(a, b) => a == b` and `GetHashCode(a) => (int)a`. This gives the same equality and no boxes.
   Limits: dictionaries capture the comparer when they are built, so statics built before us keep the boxing comparer.
   **Check first that EA persistence does not write a Dictionary's comparer into the save** (per-Sim dictionaries such
   as `SkillManager.mSkillModifiers` are persisted) [not verified]. Step 0: name the interpreted callers of `Enum.Equals`
   (`0xDA7FB0`) with the s88 managed sampler of docs/617, then do only the top types.
2. **AlarmHandle (#2).** Option A, exact, no save impact: patch `AlarmHandle.Equals(object)` to `obj is AlarmHandle &&
   this == (AlarmHandle)obj`, which drops the `GetType()` box (1 of 3). First check that `Patch` can redirect a value-type
   instance method. Option B (all 3 boxes): give `mTimers` an `IEqualityComparer<AlarmHandle>` after load. `AlarmManager`
   lives in `Lot.mSavedData`, so this has the same save-persistence check as fix 1.
3. **HasAnyElement (#3).** The array is built at the call site, so only an IL rewrite of the top call sites removes it
   (a cached `static readonly BuffNames[]`, or a chain of `HasElement` calls). Rank the call sites with the callcensus
   caller split first.
4. **GetActorsOfType (#4).** Add a per-household, per-tick read-only cache only for callers proven to only enumerate the
   list. A Remove on a shared list would change behaviour, so this is per caller, not a blanket hook.
5. **MdHook (ours).** Use the scratch array and the struct enumerator described in §2.1. Exact. This is the cheapest
   win in our code.

## 3. [GC] keys: is there a built-but-off tuning that cuts the GC step time?

`native/ts3native/ts3native.ini` `[GC]`:

| Key | Value | State | Measured |
|---|---|---|---|
| `InitialHeapMB` | 352 | on | docs/13 / docs/66: forced GCs 128 -> 1 in 6 min (Bridgeport 2026-10-12). docs/199 §7: 160 MB = +42% forced ms (5,430 vs 3,815), same GC-led hitch count |
| `GrowthStepMB` | 32 | on | log: `GC growth step ... 0x800 -> 0x2000` x3 |
| `BlackListCapKB` | 1024 | on | docs/232 |
| `BudgetCeiling` | 0 (1500 unchanged) | **built, off, never measured** | docs/15 / patch-points: "opt-in, not measured" |
| `SqrtHeapLimit` (+ `SqrtC`, `SqrtCeilingMB`, `SqrtMinExtraMB`, `SqrtLogSec`) | 0 | **built, off** | docs/199 §7: live L = 80-93 MB, so the limit stayed at the 352 MB floor in every line; mode 1 never grew the heap (gcMs per cycle 587 vs 580) |

**Verdict: no.** None of the off keys would cut the GC step time measured in phase-1133:

- 13 of the 21 GC samples are `GC_gcollect`, a full collection from managed `GC.Collect`. It runs with no stop function,
  so the per-frame budget (`BudgetCeiling`, stop function `0xD717B0`, docs/13 Q3) never applies to it. Its cost is
  mark (live ~85 MB) + clear-marks/sweep (scales with the 356 MB heap). A larger heap makes it slower, not faster.
- `SqrtHeapLimit=1` is measured to be a no-op at this live size (the limit is pinned at the floor), and the heap was not
  short anyway (102-136 MB free at every mark).
- The remaining 8 samples are load and harness snapshot allocations. No knob is needed for those; leave them out of the
  count.

**What a short test would show** (one 15-min Bridgeport natural-play job per arm, same save and seed, ts3agent `mem_gc`,
`leak_census` and the `p-*` snapshots OFF):
1. Baseline arm: expect **0** `stall watch` samples with `GC_gcollect`. Any `GC_collect_or_expand`/`GC_allocobj`
   samples left are real allocation pressure; read them with the bracket filter from §1.
2. Only if (1) shows natural GC freezes: `BudgetCeiling=2500` vs `0`. Read `gcForced` / `gcForcedMs` per minute and
   iterations/s (docs/13 Q5 #3). Expected: fewer forced finishes, but about +1 ms per sim iteration, because the per-frame
   GC never idles.
3. `SqrtHeapLimit=2` (observe) in the same job gives `gcsize: alloc=<MB/s>`, the first measured managed allocation rate.
   That number is what the fixes in §2.3 must lower, so it is the before/after metric for them.

## 4. Tool runs

- **ClrHeapAllocationAnalyzer 3.0.0** (already in `~/.nuget/packages`; added with `/analyzer:`, no sudo) through the
  SDK Roslyn `csc.dll` against `game-copy/assemblies`. Warnings: ts3fixes 7,209; Sims3GameplaySystems 11,389 (21
  declaration errors in the ILSpy output, analysis still ran); Sims3GameplayObjects 6,051 (three files with
  `protected virtual` on explicit interface members were fixed in scratch copies; without that fix the analyzers do not
  run); ScriptCore 37; SimIFace 91.
- **hot-logic-walker `--all`** on `mods/ts3fixes/src` for method line ranges (5,756 methods); EA ranges from
  `game-copy/analysis/hotlogic/all.tsv.ranges`.
- **IL scan** (`ilspycmd -il` on Sims3GameplaySystems/Objects, 242 MB of text): per-method `box`/`newobj`/`newarr`, enum-keyed
  collection calls and interface `GetEnumerator`, joined to the docs/604 hot lists and `jit-hot.csv` ms/s.
- **mscorlib IL** of `EqualityComparer`1/DefaultComparer` and `Enum.Equals/GetHashCode` for the box counts.
- Outputs (git-ignored): `game-copy/analysis/gcalloc/` (`haa-*.txt`, `il-agg.tsv`, `fix-agg.tsv`, `patches.tsv`, scripts
  `map.py`, `agg.py`, `ilscan.py`).

## 5. Not done

- No allocation rate is measured anywhere yet (no `gcsize:` line exists in any run). The rates in §2.2 are call counts
  times objects per call.
- The callers of `Enum.Equals` are not named (interpreted frames). That needs one sampler run.
- The save-persistence of a replaced Dictionary comparer is not checked; it blocks fix 1 and fix 2 option B.
