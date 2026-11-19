# 665. Algo sweep: data-ranked managed hot spots, nearest / in-range queries, O(n²) loops

Written 2026-11-19 (UTC). Static analysis plus the logs of existing runs; no new game run. Exact (result-identical) perf
fixes only, no gameplay change. Branch `feat/algo-sweep` (from the combo at 91e7f9b6).
Tags: [measured] from a log or sampler, [verified] from decompiled code or our source, [inferred] reasoning.

## 0. Scope, inputs, and what was already done

- **Managed code only.** Native routing (nopathmemo, route-speed caches, sync plan to async) and the route checks
  stuckcheck / watchdog / spot belong to other agents and were not touched.
- **Inputs.**
  - `callcensus` lines in `~/dev/ts3-runs/phase-20261119-*/ts3native-full.log`: 29 runs, about 700 sim-h per method.
    The last cumulative line of each run was taken, then summed.
  - The s88 managed sampler (`probe-out/profile/s88-1145-managed.jsonl`): 900 C# stacks over 180 s, so 1 sample is
    about 0.11% of sim-thread time.
  - `hitchwatch` and the stall stacks in the same phase logs.
- **Already covered, so not redone** (grep of docs/ and the source):
  - docs/617 leads, built in docs/622: `autmetarows`, `buffremovemerge`, `swslots`, `stuckwatchscan`,
    `lifeeventidfirst`.
  - docs/625: non-boxing enum comparers.
  - docs/598: world `GetObjects<T>` scans in hot-named EA methods. None of them runs per tick.
  - `spatialcache`: a per-tick memo of lot object queries, `SpatialCache.cs`.
  - `broadcast` / `unseenbroadcast` / `autoffbcast`: broadcaster throttles and the offload; docs/208 §8.3 builds the
    range candidates from `LotManager.Actors`.
  - `unseenenv` / `autoffenv`: the environment score.
  - `spbucketindex`, `splotlookup`, `spclonedict` (docs/588): story progression bugs, not perf.
  - `hoursmemo`, `detailprioexact`, `hovertraintest`.
- **Tools.**
  - semgrep with a new rule file, `tools/static/perf-scan.semgrep.yml`. Its four rules: a sqrt distance compared inside a
    loop; `List.Contains` / `IndexOf` inside a loop; a world or lot query inside a loop; a nearest-object scan over a
    world query.
  - It ran over `game-copy/decompiled/Sims3GameplaySystems` and `Sims3GameplayObjects` (`--no-git-ignore`: game-copy is
    gitignored), giving 824 hits, and over `mods/ts3fixes/src` plus `mods/ts3agent/src`, giving 168 hits.
  - Every hit's enclosing method was then joined with the sampler frames and the callcensus names. Only methods that are
    both flagged and measured hot were read by hand.

## 1. Ranking

### 1.1 callcensus (hooked candidates, 29 runs) [measured]

| Method | Calls | ms total | mean us/call (max run) | Verdict |
|---|---|---|---|---|
| `ViewObjects.CreateRouteToObject` | 809 | 9,013 | 52,081 | route plan (native): other agents |
| `Terrain.ModifyRouteForSpecies` | 33,651 | 7,007 | 1,854 | `Replan()` and blocker checks (native route): other agents |
| `InteractionQueue.InsertInteraction` | 58,394 | 6,024 | 135 | cost is `entry.Test()`, no loop issue |
| `ActionData.Test` | 272,595 | 2,990 | 16 | per-social tests, no loop issue |
| `Relationship.GetMiniRelationships` | 54,109 | 1,077 | 33 | **O(n²) `List.Contains` dedup: §3.1 `minirelset`** |
| `Conversation.GetSocialsForSTC` | 3,652 | 964 | 426 | `ActionAvailabilityData.Get` per STC, no loop issue |
| `Conversation.IsSuitable` | 212,306 | 704 | 3.8 | cheap per call |
| `RabbitHole.GetRabbitHolesOfType` | 796 | 615 | 1,807 | world scan, but ~1 call per sim-h |
| `Autonomy.CalculateScoreForSocial` | 28,222 | 451 | 21 | |
| `SocialComponent.SocializeWithMultiplier` | 10,050 | 273 | 35 | |
| `BuffManager.HasAnyElement` | 4,173,335 | untimed | - | docs/625: skipped |

### 1.2 s88 sampler: spatial and lookup work [measured]

| Inclusive site | Samples | Where the time goes | Verdict |
|---|---|---|---|
| `ReactionBroadcaster.PulseSims` (via `Lot.UpdateReactions`) | 34 (3.8%) | `GetInRadiusSims` 16, of which the native `Query_GetObjectsInRange` is 10; `CallOnEnterCallback` 15, of which `Stereo` `RemoveInteractionByType` > `GetType` is 11 | §4.1 design, not built; the Stereo part was skipped in docs/622 §0 |
| `SimUpdate.CalculateSimEnvironmentScore` | 19 (2.1%) | `Lot.GetObjectsInRoom` 8: uncached lot scans 4, `RoomConnectionObject.ConnectsToRoom` 3 | §4.3 |
| `World.FindPlaceOnRoadOffScreen` (traffic) | 13 | native road search | docs/617 §5, camera-dependent |
| `Lot.GetObjects` through `spatialcache` | 13 | `GetObjectsInRoom`, parked vehicles, auto lights | already memoized per tick |
| `Posture.FindTransitionInteraction` | 11 | posture autonomy (`FindBestActionForCommodityOnLot`) | not a loop issue |
| `SimDescription.Find` | 4 | walks every household, then a world `GetObjects<Urnstone>` on a miss | §4.2, rejected |
| `MinorPet.PetsOrig` (spawn cap) | 3 | world query | below the cut |

### 1.3 Our code

- Our own self time in the sampler is small, and its top items are already fixed: `AddObj` 8 (`autmetarows`),
  `ShortcutOn` 5 (`swslots`), and `SrcNowUs` 5, which is the `eventcost` diagnostic that was on in s88.
- Most of the 168 semgrep hits in our code are in `*Check` functions, world-load passes or agent handlers.
- The runtime hits left are in the hourly passes, fixed in §3.2.
- The `hitchwatch` spikes in the phase logs are the harness's own `leak_census` and `mem_gc` (817-993 ms). Keep those
  diagnostics off in timing runs.

## 2. Prior art and techniques (credits)

### 2.1 Other Sims 3 performance work

Read for what they changed. None publishes per-method managed timings. Sources were checked locally where noted; the
rest are [community-reported].

- **NRaas** (https://github.com/Chain-Reaction/NRaas, local clone).
  - It has no spatial index.
  - Its performance work is cleanup: Overwatch alarm passes (`StuckCheck`, `ResetCheck`, the `Cleanup*` passes).
  - It has memos with explicit invalidation: `ScoringMethods/ScoringLookup.cs` (`IScoringCache.UnloadCaches`), and
    StoryProgression's `SimData.InvalidateCache`.
  - The dirty-flag style of §3 follows that pattern.
- **simler90 Gameplay Core Mod** (https://modthesims.info/d/659969). A bug-fix mod; for example, it stops loops of
  stray instantiation and route failure. No timings.
- **Catalogue Lag and Memory Leak Fix** (Nexus 242). Builds fewer hidden catalogue windows. No numbers.
- **Sims 3 Performance Patch** (Nexus 235).
  - Native work: an incrementally kept script type-lookup index, cached type checks and text lookups.
  - Load time is reported as going from about 18 min to about 4 min
    (https://simscommunity.info/2026/07/16/sims-3-performance-patch-mod-review/).
  - Its incremental index is the same idea as §4.1's grid, on the native side.
- **Sims3SettingsSetter** (https://github.com/sims3fiend/Sims3SettingsSetter). Native: mimalloc, a SIMD RefPack
  decoder, a precise frame limiter.
- **Smooth Patch** (https://modthesims.info/d/658759). A tick limiter.
- **OhRudi "need less space"** (https://modthesims.info/d/683307). A tuning change that cuts routing failures.
- **TSS ToddlerRoutingFix**. Only the local archive name is known.

### 2.2 Techniques that fit a Mono 2 script runtime with no per-tick allocation

- **Uniform grid / spatial hashing vs a quadtree.** A grid is O(1) per cell and best when objects are similar in size
  and evenly spread, which fits Sims on a lot. A quadtree suits uneven density. Sources: C. Ericson, *Real-Time
  Collision Detection*, ch. 7 (https://realtimecollisiondetection.net/books/rtcd/); Teschner et al. 2003, "Optimized
  Spatial Hashing for Collision Detection of Deformable Objects". For nearest neighbour, grow rings of cells and stop when
  a ring's minimum distance exceeds the best found (Hjaltason & Samet 1999, https://dl.acm.org/doi/10.1145/320248.320255).
  The engine's own range query is a native quadtree (docs/208 I-402-5), so a managed grid can only stand in front of it
  as a conservative filter (§4.1).
- **Dirty flag / incremental caches.** R. Nystrom, *Game Programming Patterns*, "Dirty Flag"
  (https://gameprogrammingpatterns.com/dirty-flag.html). Invalidate on writes; recompute on read. This is EA's own
  `Lot.ObjectCache<T>` with `RefreshObjectCachesIfNecessary`.
- **Squared distances.** Compare d² with r². EA already does this where it counts (`LotManager.LotsWithinRadialRange`).
  The 10 sqrt-in-loop hits are rare interactions, or `GetDistanceToObject`, which is a native call and costs far more
  than the sqrt.
- **Object / struct pools.** Nystrom, "Object Pool" (https://gameprogrammingpatterns.com/object-pool.html). On Mono 2's
  non-generational Boehm GC every short-lived list costs; that is the docs/618 work.
- **Time-slicing.** Spread a pass over ticks with resumable state. Sources: Nystrom, "Update Method"; Millington & Funge,
  *AI for Games*, scheduling. It is used by `stuckwatchscan` / `CheckIdle`.
- The book and paper links in this subsection are from the research agent's recollection and were not opened in this
  session [community-reported].

### 2.3 Pathfinding: context for the native route-speed work

TS3's router is closed native code, which we can hook only at call boundaries (`Route.Plan*`).

| Idea | Source | Fits TS3? |
|---|---|---|
| Path caching / whole-answer memo | Amit Patel's A* notes (http://theory.stanford.edu/~amitp/GameProgramming/MovingObstacles.html) | **Yes**, at the boundary: key on start cell, goal set, agent parameters and world version, and invalidate on obstacle or lot change. This is what nopathmemo, boattaximemo and spotedgememo already do. |
| Replan triggers from incremental search (D* Lite, Koenig & Likhachev 2002; LPA*, Koenig et al. 2004) | https://idm-lab.org/bib/abstracts/papers/aaai02b.pdf | **The trigger idea only**: replan only when an edge on the stored path changed. Incremental repair itself needs the search state inside the router. |
| HPA* (Botea et al. 2004) | https://webdocs.cs.ualberta.ca/~mmueller/ps/hpastar.pdf | **No**: needs the router's graph to build clusters. |
| Jump point search (Harabor & Grastien 2011) | https://ojs.aaai.org/index.php/AAAI/article/view/7994 | **No**: it lives inside the search loop, and TS3 plans on a navmesh with footprints, not a uniform grid. |
| Flow fields (Emerson, *Game AI Pro* ch. 23) | http://www.gameaipro.com/ | **Mostly no**: one field per goal for many agents needs the cost grid. A managed version could only pick goals for crowds we choose. |

## 3. Built

### 3.1 `minirelset` (EA, default off, slots 4233-4235) [verified]

**Site.** `Relationship.GetMiniRelationships(SimDescription)` (`Relationship.cs:1427`). For each key of the Sim's
relationship dictionary, EA runs `if (!list.Contains(dictionary2[key])) list.Add(dictionary2[key])`. That is a second
dictionary lookup plus a linear search of the list built so far: O(n²) in the Sim's relationship count, which is
hundreds for an old townie. semgrep `perf-list-search-in-loop` flags it, and it is in the callcensus top 5 (§1.1).

**Change.** The fix rides the existing callcensus swap of this method (`CcStatic.Mini`), so it adds no new hook. With
the switch on, our path does the following:
- It walks the same dictionary once by `KeyValuePair`. That is the same enumeration, so the same order as `Keys`.
- It dedups through `RefSet665`, a reference-identity hash set with a null flag (`AlgoSweepCore.cs`). Below 8 entries
  it keeps EA's linear `Contains` and allocates no set.
- It then runs EA's `MiniSimDescription` loop word for word.

**Why it is the same list.**
- `Relationship` overrides neither `Equals` nor `GetHashCode`, and no EA assembly subclasses it [verified, decompiled].
  So `List<IMiniRelationship>.Contains` is a reference compare. That holds whether Mono calls `x.Equals(item)` or
  `item.Equals(x)`.
- Our path checks at runtime that `Equals` and `GetHashCode` are still `Object`'s.
- A value of any other runtime type (a mod subclass) sends the whole call to EA's body. Our reads have no side effects,
  so EA's call then runs as if ours never had.
- A null Sim or a null table goes to EA's body, which throws as before.
- On the autonomy worker thread, EA's body runs (docs/128 G-M3).

**Check.** `MiniRelSetCheck run` (repro `minirelset-check`):
- For up to 400 Sims, it compares EA's body with our path: the same count and the same object at every index. It times
  both.
- It then makes one call through the hooked method, which with the switch on must be answered by our path.
- Off gives BROKEN (the O(n²) path is in use). On gives FIXED with 0 differences.

**Counters.** [4233] calls answered by our path; [4234] calls sent to EA's body while on; [4235] errors.

**Selftest.** `tools/selftest/algosweep`: 500 random rounds, with repeated references, nulls, and different objects with
equal ids. Our dedup gives the same members in the same order as EA's `Contains` loop, and the same membership answers.

**Expected gain** [inferred].
- Over the 29 runs the method took about 1.1 s in total, a mean of 1.5 ms per sim-h. One run was hotter: 3,228 calls in
  one sim-h, at a 12.5 us mean.
- The n² term is what grows with save age, so the gain is largest in long-played worlds.
- To measure: the callcensus `us/call` with the switch off, then on, in the same run.

### 3.2 Our hourly passes: O(n²) membership (no switch: same behaviour) [verified]

- **`DreamNodeDeadSimPass` (`DeadSimHolders.cs`).**
  - The old code ran `drop.Contains(n)` (a list) for every `EventTracker` listener.
  - It now uses a `RefSet665` kept next to `drop`. `drop` still keeps the order in which nodes are removed.
  - `ActiveNodeBase` has no `Equals` override; the overrides in that file belong to the nested `NodeSubject` and
    `DataListEntry` classes [verified]. So `Contains` was a reference compare.
- **`DeadSimCountPass` (`DeadSimCount.cs`).**
  - The old code ran `actors.Contains(s)` (a list of all actors) for every old, unhollowed, destroyed Sim tracked; al
    logs show up to 1,303 tracked Sims.
  - It now uses a reference set over the same `actors` snapshot, built lazily on the first old entry. `Sim` has no
    `Equals` override [verified: `ScriptObject` and `GameObject` define none].

### 3.3 Tooling

`tools/static/perf-scan.semgrep.yml` holds the four rules of §0, for reruns after a patch or EP change.

## 4. Designed, not built (reasons)

### 4.1 Broadcaster range precheck: a uniform grid in front of `Query_GetObjectsInRange` [inferred]

**Why it cannot simply replace the query.** `GetInRadiusSims` calls the native quadtree query each pulse. Its result
order feeds the enter-callback order, and those callbacks draw random numbers, so a managed index cannot replace the
query exactly (docs/208 §8.3: the actor-list order is "equal in distribution", not equal).

**What would stay exact.**
- Keep a per-tick uniform grid of `LotManager.Actors` positions (cell about 8 m). Rebuild it lazily on the first pulse of
  a tick, and also when the actor count or the list identity changes.
- When no actor lies within radius + `kBroadcasterRadiusBuffer` + 3 m of the broadcaster, return EA's empty dictionary
  without the query. An empty answer has no order.

**Why it is not built.**
1. The rebuild reads every actor's position through interop, about 100 native calls a tick. That pays only if several
   pulses run per tick, which is unmeasured.
2. Exactness needs every in-world Sim to be in `LotManager.Actors`. A Sim is added in `LotManager.AddObjectToLot`
   [verified]; the window before that callback is [inferred].
3. On full-simulation lots most pulses have Sims nearby, so the hit rate is unknown.

**Next step.** A counter-only probe: pulses per tick, and pulses whose native answer was empty. Build it only if the
empty share is high.

### 4.2 `SimDescription.Find` index: rejected

An id → description index needs invalidation on every household membership write: `Household.Add` / `Remove`, the
household list, `IsPreviousTravelerHousehold`, `GameStates.TravelerIds`, direct `mSimDescriptionId` sets, and urnstones
created or emptied. The hit path cannot be validated without the scan. That is not airtight, so it is not exact.

### 4.3 Environment score: second aromainator lot scan

In the indoor branch, `GetObjectsInRoom<IAromainator>(room).Count` re-runs the lot query that
`GetObjects<IAromainator>()` ran on the line before. Counting from that array would be exact. But `GetObjectsInRoom<T>`
is generic, so it cannot be swapped, and the fix needs a full copy of `CalculateSimEnvironmentScore`, a 300-line method
that our `autoffenv` / `unseenenv` hooks already stack on. The gain is at most 4 samples (0.4%). Not worth the risk.

### 4.4 Below the cut

- `RabbitHole.GetRabbitHolesOfType`: about 1 call per sim-h.
- `LotManager.AddObjectToLot`: a `sActorList.Contains` check per lot change.
- `CalculateSimEnvironmentScore`'s outdoor `if (isResidentialLot)`, which does not change inside its loop.
- The `Seesaw` / `SpaceSimulator` / `GhostHunter` sqrt compares: per interaction only.

## 5. Status

Built: ts3fixes `--tag algo` and ts3agent, the `algosweep` selftest, and `refs-resolve-check` (0 unresolved). NOT RUN in
game. Repro `minirelset-check` is in `tools/harness/repros.json`.
