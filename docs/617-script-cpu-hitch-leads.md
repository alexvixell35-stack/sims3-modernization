# 617. Script CPU hitch leads: per-tick and per-Sim hot code not fixed yet

Written 2026-11-17 (UTC). Static analysis plus one live profile. No game run for this doc, and no code was changed.
Tags: [measured] from a sampler or log, [verified] from decompiled or our source, [inferred] reasoning.

## 0. Evidence and method

- **Freezes.** Run `phase-20261117-1133` has 58 "stall watch: sim thread" lines (150-725 ms). With
  `tools/ghidra/symbols/ts3w-1.67.symbols.csv`, 48 of the 58 are in `ves_exec_method_with_context` (script CPU) and 13 are
  Boehm GC steps. The native stacks do not name C# methods, so the ranking below uses the sources that follow.
- **Live managed sampler (ranked first).** Run s88 (Bridgeport, all proven fixes on) has 900 C# stacks over 180 s
  (`probe-out/profile/s88-1145-managed.{txt,jsonl}`). 1 sample is about 0.11% of sim-thread time, and 37.1% of the
  samples have no script running. Caller and callee counts below come from a small script over the jsonl.
- **Per-pass timings.** The `alarmtime`, `lotpass` and `hourlypass` lines in `phase-20261117-1133/ts3native.log`.
- **Tool ranking (static).** The managed IL call graph (`game-copy/analysis/callgraph/index.json`) was walked
  breadth-first from the per-tick roots: `SimUpdate.Simulate`, `Sim.Simulate`, `Motives.UpdateMotives` and
  `BuffManager.UpdateBuffs` (per Sim); `Autonomy.Update`, `RunAutonomy` and `FindBestAction` (per autonomy pass);
  `Lot.Simulate` and `ReactionBroadcaster.Simulate` (per lot); `AlarmManager.Simulate`, every `Task.Simulate()`, and
  the 362 callbacks passed to `AddAlarmRepeating`. Weights fall off by 0.6 per call level and by 0.3 on virtual or
  multi-target interface edges. Each method's own cost score counts these calls in its IL: world, lot or household scans
  (x10), reflection and `Enum` reflection (x8), LINQ (x3), list copies (x2), new collections and string building (x1).
  Added to that: the hot-logic-walker `--all` hits (NESTEDSCAN / REPEATCALL / INFLOOP, x15), the ClrHeapAllocationAnalyzer
  HAA count per method (Roslyn, `game-copy/analysis/roslyn/bug-rules-all3.tsv`, x0.2), and 3 x the sampled ms/s from
  `docs/data/compile-candidates.csv`. The script is kept in the session scratchpad; it can go to `tools/static/` if it is
  wanted again.
- **CodeQL** 2.27.4, with `codeql/csharp-queries` 1.11.0 already in `~/.codeql/packages`. The database was built with
  `--build-mode=none` on the 120 EA files of the 250 top-ranked methods plus `mods/ts3fixes/src`. Queries run:
  `Performance/{StringConcatenationInLoop, StringBuilderInLoop, UseTryGetValue}`, `Linq/BadMultipleIteration` and
  `Likely Bugs/NestedLoopsSameVariable`. Result: 161 hits. The hits in our code (string concatenation in loops) are all in
  log and check builders, none on a per-tick path. The EA hits are `ContainsKey` followed by the indexer
  (`BuffManager.cs:766,853`, among others): two lookups instead of one, which is small next to the leads below.
- **hot-logic-walker on our source** (`--all` over `mods/ts3fixes/src`): 15 NESTEDSCAN hits. All are in world-load or
  check code except `StuckWatchdog`'s passes (§7).
- **Skipped as already covered** (checked with grep): docs/247, 196, 96, `tools/harness/sets/proven-fixes.txt`, and the
  verdicts in docs/74, 603, 608 and 144. Covered there: `Household.get_AllActors` via `detailprioexact`,
  `GetInRadiusSims` / `PulseSims` via `broadcast` and `unseenbroadcast`, `UpdateReactions`, `DistressIdle`,
  `IsAllowedInRoom`, `TryGetHoursOfOperationExtended` via `hoursmemo`, and `GetTuning`. Hooked already:
  `CalculateSimEnvironmentScore` and `UpdateMotiveWithDecay`.

## 1. Ranked list

| # | Method | Samples (s88) | Ranked by | Exact? |
|---|---|---|---|---|
| 1 | `Ts3Fixes.Fixes.LiveMeta` > `TryRequest` > `AddObj` (ours, autonomy meta scoring) | 34 incl. (3.8%); TryRequest 16, AddObj 10 (7 in `GetType`) | sampler | yes |
| 2 | `LifeEventManager.SendLifeEvent` / `ProcessLifeEvent` (EA, every `EventTracker` event) | 34 incl. (3.8%) | sampler | partly |
| 3 | `Stereo.AddEnjoyingMusicCallback` > `RemoveInteractionByType` > `GetType` (EA broadcaster pulse) | 15 incl., 11 in `GetType` | sampler | yes |
| 4 | `World.FindPlaceOnRoadOffScreen` from `CarNpcManager.DriveAwayInNpcCar` and `UpdateTrafficTask.Simulate` (EA) | 13 self (native IC), plus 10 in `CreateObjectOutOfWorld` | sampler | no (rate only) |
| 5 | `BuffManager.RemoveElement` through our two hooks `BuffRemoveHook` > `BBrdHook` | 11 incl. through `BBrdHook`; 5 of the 19 `Dictionary.GetPrev` leaves | sampler | yes |
| 6 | `Ts3Fixes.Fixes.ShortcutOn` on per-tick hook paths (ours) | 9 incl. | sampler | yes |
| 7 | `StuckWatchdog.Check` / `CheckIdle` (ours, every 10 sim-minutes) | 8 incl. | sampler + walker (NESTEDSCAN) | yes |
| 8 | `Fixes.HourlyPasses`: `OutfitWaitSavedPass` and `DreamNodeDeadSimPass` (ours) | 47 fires of 26 ms (max 32) in phase-1133 | `alarmtime` / `hourlypass` log | yes |
| 9 | `SimUpdate.Simulate` teen branch: `Sim.IsSupervised` computed when unused (EA, per teen per update) | 0 (Bridgeport) | call graph (perSim, depth 1, 2 scans) | yes |
| 10 | `VenueFlowUtility` / `BartendingVenueHelper.RemindVenue[Npc]GuestsToLeave` (EA, repeating alarm) | 0 | call graph + walker (NESTEDSCAN: per-lot Sim scan inside `LotManager.AllLots`) | yes |

Leads 1, 5, 6, 7 and 8 are our own code: about 7% of s88 sim-thread samples [measured], plus a 26 ms spike every
sim-hour. EA's share inside them (for example `MetaOrig` under lead 1) is not counted as ours.

## 2. LiveMeta / TryRequest / AddObj (ours) [measured, verified]

**Hot because.** On every sampled autonomy meta search, `TryRequest` (`AutShadow.cs:907`) rebuilds the full request:
it calls `AddObj` for every public meta object, every known lot, the current lot, the home lot and the career location.
For every meta ad of each object, `AddObj` (`AutShadow.cs:1027`) calls `m.GetType()` (passed to `ImplVerdict`),
`iop.InteractionDefinition.GetType()` plus a `sDefIndex` dictionary lookup, a `q.PairIndex[iop]` insert, and
`SfAllowed`. It also loops over `LotManager.Actors` for the reachability rows. Mono 2's `GetType` is an icall that goes
through `RuntimeTypeHandle`, and it is the top self frame of the profile (3.0%). These inputs barely change between
searches: the meta objects and their `MetaAds` lists change only on build, buy and load. Of the 34 samples, 15 are EA's
own `MetaOrig`, and that part would run anyway.

**Fix sketch.** Cache per `IMetaObject` the rows `AddObj` writes: `ImplVerdict`, then per ad `Def`, `Acb` and the
`SfAllowed` verdict. Key the cache on the `MetaAds` ArrayList reference plus its `Count`, and rebuild it on a mismatch,
on `sDefGuardHit` changes and on `GuardSweep`. `AddObj` then copies the arrays into `q`. The rows posted to the worker
are the same, so EA's pick is unchanged. Cheaper first step: a one-entry `lastType`/`lastIndex` memo in front of
`sDefIndex`.

## 3. LifeEventManager.SendLifeEvent / ProcessLifeEvent (EA) [measured, verified]

**Hot because.** `EcHook.ProcessEvent` (50 samples) sends every gameplay event through `ProcessEventOrig` into
`SendLifeEvent`. The callers seen: `Autonomy.UpdateOrig` 9, `Sim.InternalOnSetLotCurrent` 6, rabbit-hole enter 3,
`SendSimEnvironmentUpdateEvents` 3. The static `ProcessLifeEvent(Event)` builds `Household.ActiveHousehold.AllActors`,
a new pruned list, before it checks `sData.TryGetValue(e.Id)`, so the list is built even for event ids that have no life
event. Inside the per-Sim `ProcessLifeEvent`, `ShouldAddLifeEvent` > `RunFeedbackFunction` > `RunCheckFunction` has 14
samples. `GetActiveNode` has 5: for a non-selectable actor, or with lightweight mode on, it builds a
`LifeEventActiveNode` (and a one-off dream instance) that it never stores, on every event. Dictionary probes have 5.

**Fix sketch.**
- Exact: hoist `sData.TryGetValue(e.Id)` above the `AllActors` read. The getter allocates and has no side effect.
- Exact: for the third-party loop, read `AllActors` only when some `LifeEventData` in the list `IsThirdPartyEvent`.
- Needs a hand check: a throwaway-node memo for non-selectable actors keyed by (actor, `data.Id`, equivalent subject).
  It must keep the `RandomChance01` call order and must not reuse check results that depend on the event. Measure
  first; the two exact items may be enough.

## 4. Stereo.AddEnjoyingMusicCallback (EA) [measured, verified]

**Hot because.** On each broadcaster re-pulse (every `PulseRepeatTime` sim-minutes, per Sim in range), the callback
removes and re-adds the `EnjoyingMusic` buff and the `DanceTogetherA` interaction. `GameObject.RemoveInteractionByType`
walks the Sim's `mInteractions` and calls `InteractionDefinition.GetType()` on each entry until it finds a match. docs/74
§3 measured 0.11-0.51% with S3PP's `MonoTypeEHashProbe` and `MonoIsinstCache` on, and 1.92% with them off. s88 shows
11 `GetType` leaves out of 15 (1.7%), the "caches off" figure.

**Fix sketch.**
1. First check whether S3PP's type caches are loaded in s88. If they are not, that is a setup gap, not a code fix.
2. If our own fix is wanted (docs/74: "our own type cache, not planned"), hook the
   `RemoveInteractionByType(InteractionDefinition)` overload. Run the same loop, but compare
   `ReferenceEquals(entryDef.GetType(), t)` only after a cheap `entryDef is DanceTogetherA.Definition` test when
   `t == typeof(DanceTogetherA.Definition)`. This is exact if `DanceTogetherA.Definition` has no subclass; check that
   with ilspycmd first. List order and the remove/add semantics stay as they are, which keeps docs/74's reason not to
   dedupe.

## 5. World.FindPlaceOnRoadOffScreen (EA traffic) [measured]

**Hot because.** It is a native road search (docs/04: the same search as `IC_World_FindPlaceOnRoadOffScreen`), with 13
self samples. 7 come from `CarNpcManager.FindOffScreenPlaceOnRoad` < `DriveAwayInNpcCar` < our
`TrafficReleaseHook.DriveOffscreenAndRelease`, and 6 from `UpdateTrafficTask.Simulate`, which also spends 5 in
`CreateObjectOutOfWorld`. docs/129 lists traffic as about 19 ms/s, an offload candidate, not fixed.

**Fix sketch.** The result is camera-dependent, so it cannot be memoized exactly. Behaviour-keeping options:
(a) at most one `DriveAwayInNpcCar` search per tick, deferring the rest by one tick through our existing
`TrafficReleaseHook` (the car is released a tick later and ends in the same state);
(b) the docs/129 offload route. Check first whether `TrafficReleaseHook` adds release calls that vanilla would not make.

## 6. BuffManager.RemoveElement through BuffRemoveHook > BBrdHook (ours) [measured, verified]

**Hot because.** `Motive.UpdateMotiveBuffs` (per motive, per Sim update) calls `RemoveElement` for moodlets that are
usually absent. Our chain is `BuffRemoveHook.RemoveElement` > EA `RemoveOrig` > `BBrdHook.RemoveHook`
(`Batch589cLogic.cs:51`). `BBrdHook.RemoveHook` does `HasElement` and `BrPending` before EA's body, EA's body does its
own lookup, and then `HasElement` runs again when the buff was live. That is 3-4 `GenericManager` dictionary probes per
call where vanilla does 1. 6 of the 11 samples come from `UpdateMotiveBuffs`.

**Fix sketch.** Merge the two hooks into one entry and do a single `TryGetValue` first. When the buff is absent, call
`RemoveOrig` once (EA's absent-guid path) and return, with no `BrPending`. Do the post-check only when it was live and
pending. Output is identical.

## 7. ShortcutOn on per-tick hooks (ours) [measured, verified]

**Hot because.** Even after docs/605 §2, every call does `SwCurrent()` (two field compares) plus `SwitchCache.Get(name)`,
a string hash and probe. In s88 its 9 samples come from `HcLotHook.UpdateDoorPrivacy` / `UpdateDetailPriority`
(per lot per tick), `DetailPriorityHook`, `BuffRemoveHook`, `EcHook.ProcessEvent` and `MotivesUpdateHook`. `LiveMeta`
alone calls it up to 8 times per search.

**Fix sketch.** Give each hot switch a static slot, for example
`static readonly int kSwDoorPrivacy = SwSlot("...")`, resolved once into a `bool[]` that is refilled only when the
`SwCurrent()` generation changes. Hot hooks then read `SwOn(kSwDoorPrivacy)`, an array load. Answers stay the same
(same table, same generation rule). In `LiveMeta`, read the `autunseen` / `autseengame` / `autliveseen*` switches once
into locals.

## 8. StuckWatchdog.Check / CheckIdle (ours) [measured, verified]

**Hot because.** Every 10 sim-minutes (`kWatchMinutes`, `RouteFixes.cs:117`), `Simulate` runs both passes back to back.
Each pass calls `Queries.GetObjects<Sim>()` (a world query: 2 of the 8 `Query_GetObjects` leaves), allocates a new
`keep` dictionary, and does per-Sim `TryGetValue` and vector math. `CheckIdle` caps full routability checks at 16 per
pass and yields after each one, but the stillness bookkeeping for every Sim runs in one tick.

**Fix sketch.** Take one Sim snapshot (`LotManager.Actors`, which is what EA's own Sim loops use) for both passes. Reuse
the two `keep` dictionaries: clear them and swap them with `mSeen` / `mStill`. Spread the walk over ticks in chunks of
about 16 Sims with `Sleep(0)`. The verdicts are the same, at most a few ticks later within the 10-minute window.

## 9. HourlyPasses (ours) [measured, verified]

**Hot because.** All 20 sweep passes run in one alarm callback. `alarmtime` logged 47 fires of about 26 ms (max 32 ms),
the top `lotpass` item 44 times. The two passes above 3 ms:
- `OutfitWaitSavedPass` (12 ms, `OutfitWaitSaved.cs:30`): enumerates the whole `ScriptCore.Simulator.mObjHash` (every
  script object in the world) by reflection every sim-hour, with an `IScriptProxy` cast and `IsInstanceOfType` per entry.
  The tasks it looks for (a saved `MagicalMakeover.ChangeOutfitCB` wait) can only exist from a load; new ones go
  through the outfit-wait hook.
- `DreamNodeDeadSimPass` (7 ms, `DeadSimHolders.cs:188`): walks every `EventTracker` listener every hour, with
  `drop.Contains` (a linear search, O(n²) when many nodes drop). A node only gets a gone owner when a Sim is destroyed.

**Fix sketch.**
- `OutfitWaitSavedPass`: scan `mObjHash` once after the load and keep the ids of `AlarmOneShot` tasks whose callback is
  a `MagicalMakeover`. Each hour, re-check only that list, and drop the pass when the list is empty. The same tasks end
  (exact).
- `DreamNodeDeadSimPass`: run only when a Sim-destroyed counter changed since the last pass, and use a `HashSet` for
  `drop` (exact).
- `HourlyPasses`: run one pass per tick (a small `Task`, or 20 one-shot alarms a tick apart) instead of all 20 in one
  callback. The order and the hourly period stay; the spike goes.

## 10. SimUpdate.Simulate teen curfew: IsSupervised computed and unused (EA) [verified]

**Hot because.** On every high-LOD update of a teen, `bool flag3 = mSim.IsSupervised()` (`SimUpdate.cs:171`) runs
before anything checks whether it is needed. Off the home lot, `IsSupervised` scans `LotCurrent.GetSims()`, or the
household's Sims on the world lot, plus career and occult lookups. `flag3` is read only at `SimUpdate.cs:197`
(when `OutAfterCurfew` is present) and at line 202 (when `flag2`, curfew in effect). Outside curfew hours, with no
buff, the result is thrown away.

**Fix sketch.** Compute `IsSupervised` lazily, only on those two branches. `IsSupervised` has no side effects, so this
is exact. It needs a hook on `SimUpdate.Simulate` (already hooked for other items), so fold it into the existing hook
rather than adding a new swap. Small gain; it scales with teens away from home in the evening.

## 11. RemindVenue[Npc]GuestsToLeave (EA venue-close alarm) [verified]

**Hot because.** The walker flags NESTEDSCAN at `VenueFlowUtility.cs:268,290` and `Bartending.cs:961,985`: for every
lot in `LotManager.AllLots`, `allLot.GetAllActors()` or `GetSims()` (a native query per lot), even on lots that are not
venues or not closing. It runs from a repeating alarm. It did not show in s88; it would spike at closing times.

**Fix sketch.** Filter `AllLots` by the commercial venue check EA's body applies to each lot before the per-lot scan.
Alternatively, loop over `LotManager.Actors` once and bucket the Sims by `LotCurrent`. Same Sims, same order per lot if
the bucket keeps `GetSims` order; check that order against the IC.

## 12. Below the cut (tool-ranked, not sampled in s88)

- `Opportunity.CollectiblesForOpp`: REPEATCALL of `Queries.GetObjects<IMetal/IGem/IRock/IInsectJig>` inside a loop.
- `SimDescription.Find` (4 samples): walks every household's `AllSimDescriptions`.
- `CaregiverRoutingMonitor.AlarmCallbackInternal`: 3 scans plus list copies.
- `ThoughtBalloonManager.GetBalloonData`: reflection plus 9 HAA allocations, per autonomy pass.
- `InteractionObjectPair.TuningOrig` under our `AutTuningHook`: 5 `GetType` samples.
- Our diagnostic agents: `hitchwatch` in phase-1133 logged `mem_gc` 1,718 ms and `leak_census` 801 ms on the sim
  thread. These are diagnostics, not gameplay; keep them off in any timing run.
