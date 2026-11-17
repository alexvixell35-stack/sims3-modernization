# 277. SV household local-search rate: what existing data says (docs/276 §2.2 lead)

2026-11-16. Static read plus existing data only, no game run. Follow-up of docs/276 §2.2: on Sunset Valley the household
local phase cost ~70 ms/s (BP 7.76, IP 4.52), because the household ran 5.2 local searches/s against BP's 0.66/s at about
the same cost per search. The question is whether a retry loop drives this, and whether it is EA code, another mod or our fix.

A passive counter probe is being built on `feature/hoursmemo-svprobe-1116`. This doc does not duplicate it. §5 lists what
that probe has to count to settle the question.

Tags: [verified] = read in code or the data file; [measured] = counted from existing run data; [inferred] = reasoning, not yet checked.

## 0. Verdict

- **Cause: not proven.** Existing data cannot attribute local searches to a Sim. Neither the L0 counters nor our logs carry
  the actor of a search. No managed stack carries it either.
- **The 70 ms/s figure is from another machine and another save.** All three L0 jobs (BP, IP, SV) ran on Dalexlol's machine
  and his `Baseline-SV-Spring` (PR #341, imported in 79048c83a). His clocks were 4.7-4.8 GHz; ours run at 4.2 GHz. [verified]
  BP and SV used the same build, ini, switches and mod set, so whatever drives the gap is in the SV save state, not in a
  switch or mod that is on in one world only. [verified: same queue job shape and pair]
- **Our SV save shows the same direction, smaller.** Over the last 25 profile runs per world (managed samples), the share of
  samples inside `ScoreInteractionsForLocalAutonomy` reached from `AutonomyManager -> TryFindBestAction` is:
  - SV 4.87%
  - BP 1.15%
  - IP 1.56%

  That is 4.2x BP and 3.1x IP. The whole B9 share (all callers) is SV 5.58%, BP 2.35%, IP 2.48%, and SV is higher in every
  one of its 25 files (2.0-9.5%). [measured]
- **Leading hypothesis:** EA's idle re-search path for *selectable* Sims has no backoff when a search picks nothing (§2.1).
  A household Sim with nothing it can do runs a full local search every Sim-loop pass. [verified code; that it drives the SV
  rate is inferred]
- **A smaller, SV-specific EA bug is confirmed (§3).** Justine and Marty Keaton keep trying to mop one burnt floor tile they
  cannot reach (`MopBurnt`, `FailedBecauseNoDestinationSpecified`). The goal is always the same point, 3.0 m above the floor
  they stand on. This is the same shape as the roof-puddle bug in docs/546 row 6. It accounts for at most ~0.3 searches/s
  on our save [inferred from 2-17 failures/min], so it is not the main 5.2/s.
- **Our code: no driver found.**
  - The stuck check (`FewerPlans.cs` `StuckCheckNear`) returns early on the active household lot, in EA's own order [verified].
  - `routefaillog` only logs [verified].
  - The L0 probe itself only reads [verified, `AutLocalProbe.cs`].
  - Our rule is "ours until proven", so this stays open until the probe names the Sim and the interaction.

## 1. Data used

| Source | What it gives | Limit |
|---|---|---|
| `docs/data/results/2026-11-10-queue-tier2-local-l0-m1110.md` (04:26 block) | `aut_l0_*` for BP and SV | contributor machine and save; no per-Sim split |
| `probe-out/profile/*-{sv,bp,ip}-*managed.jsonl` (25 newest per world, 22.5k / 17.6k / 20.8k samples) | where script time goes, callers of B9 | no actor identity |
| `probe-out/profile/*-activity.jsonl` (60 newest per world, 30 s polls) | household interaction names, idle (`null`) share | far too coarse to count searches |
| `probe-out/queue/*-sv-*-ts3native.log` (11 SV runs, 11-13..11-16) | `ts3fixes: routefail:` lines per Sim, interaction, goal | capped at 30 lines/min and 400 per session; route failures only |
| `probe-out/idle-watch/sv-v027.json*` (11-05, `Baseline-SV`, not Spring) | 15 min of household interactions, no keep_alive | a different save |
| decompiled script DLLs (local only, `game-copy/decompiled`, not committed) | EA autonomy scheduling, `MopFloor` / `Terrain.MopBurnt` | - |

L0 counter anomaly [verified]:
- `aut_l0_pairn_hh` reads 316 (SV) and 3 (BP), while `aut_l0_n_hh` reads 3,163 and 397.
- The source increments both slots on the same line (`AutLocalProbe.cs` `L0Close`), so they should be equal.
- `pairn_seen` equals `n_seen` on both worlds.
- So either slot 909 or the report's name mapping is off. The "727 pairs per search" in docs/276 divides by `n`, so it may
  still be right, but treat the pair figures as unconfirmed until the probe reads them again.

## 2. EA mechanism (decompiled, Sims3GameplaySystems)

### 2.1 When a Sim searches again [verified]

`Sim.DoInteraction`, idle branch (empty queue):
- **`CanRunAutonomyImmediately()`.**
  - Only for Sims that run local autonomy (B8).
  - If the last interaction was autonomous and succeeded: requeue at once.
  - If it failed: requeue at once only for non-selectable Sims. Selectable (household) Sims fall through to the delay test.
- **Delay test.** Requeue when `Autonomy.TimeSinceInteractionQueueBecameEmpty >= kAutonomyDelayNormal` (5 sim-minutes; mounted, socializing and user-cancel use their own delay tunables),
- **The empty time is set in only two places:**
  - in `InteractionQueue.DeQueue`, when the queue goes from non-empty to empty;
  - in `Sim.Simulate` start-up.

  A search that pushes nothing does not reset it.
- **`Autonomy.RunAutonomy(CommodityKind, bool)`.** When nothing is chosen, a non-selectable NPC is sent home
  (`MakeSimGoHome`). A selectable Sim gets nothing.
- **Result:** once a household Sim has had an empty queue for 5 sim-minutes, every search that pushes nothing (no candidate,
  or `ConsiderPerformingAutonomousAction` declines the pick) is followed by another. The Sim loop sleeps 2 ticks
  (`kSimLoopSleepTicksWhenNotInQueue`), then `AutonomyManager.Simulate` runs it. The manager runs one autonomy per pass and
  alternates the high-LOD and low-LOD queues.
- The search is a full B7 -> B9 local search of the lot map, which explains why the cost per search is unchanged (13.4 vs
  11.8 ms) while the rate rises 8x.
- docs/245 l.88 and docs/04a l.49 describe the same idle path for NPCs.
- Nothing in EA's code limits this for household Sims. [verified]

Same EA idle path, a second trigger [inferred]: an autonomous interaction that "succeeds" without doing anything (for example
a `Run` that returns true after pushing a continuation that is then refused) also requeues at once through
`CanRunAutonomyImmediately`.

### 2.2 Why SV and not BP/IP [inferred, open]

A loop needs a household Sim that is idle (high LOD, on its lot) and keeps finding nothing it can push.

Things that could be SV-specific on the Keaton save:
- **Thin or broken lot.**
  - docs/05 l.60: keep_alive repaired 4 broken plumbing objects in the Keaton house (bathtub, toilet, 2 sinks).
  - In idle-watch `sv-v027` (`Baseline-SV`): "Think About Repossessed Object", Hygiene -100 at both start and end for both
    adults, Justine on "Mop Up Puddle" for most of 15 min.
  - A hygiene need at -100 with the bath or shower broken or repossessed leaves autonomy with no valid pick for the top
    motive.
- **Burnt floor tile above the ground floor (§3).**
- **A pet in the household (Razta).** Pets are selectable Sims in the household, so the same rule applies to them.
  - `RunAround` routefails (`NoDestinationSpecified`) are frequent on SV: 198 + 22 in 11 runs.
  - The same happens on BP (Roxy: `DoggySwimAround` 6,499, `RunAround` 578 in 99 runs) and IP (Bella 517 + 303), so a pet
    alone does not explain SV.

Counter-evidence: the idle (`null`) share per household Sim at 30 s polls is about the same in all three worlds [measured]:
- SV: Justine 28%, Marty 20%, Razta 11%
- BP: Lala 15%, Tiara 14%, Roxy 19%
- IP: Cara 27%, Edward 21%, Bella 19%

So "idle more often" alone does not separate SV. What would separate it is "idle *and* finding nothing" against "idle
between successful picks". Only the probe can tell these apart.

Household size is not the cause on our saves:
- SV and BP each poll 4 household members (2-3 adults, 1 baby/toddler, 1 dog).
- The L0 "household" class also counts unknown scope (`UnseenFallbackCore.HouseholdScope`: gone, not live, -1), but a
  mid-play window should have few of those. [inferred]

## 3. Burnt tile that cannot be reached (EA bug, SV Keaton lot) [verified data + code]

Every `MopBurnt` routefail on our SV-Spring save (11 runs, 2-176 lines per run, 0.1-17.5 per minute) shows the same values:
- Sims: Justine Keaton `0x713C` and Marty Keaton `0x71AF`, both household and seen.
- Lot: `0x6C11001B8B52BB30`.
- Result: `Plan async FailedBecauseNoDestinationSpecified`, job result 10, 0 ms.
- Goal: **`1064.7,52.8,1243.2` every time**. The Sims stand at y 49.1-49.8, so the tile is 3.0 m above their floor.

EA code [verified]:
- `Sim.MopBurntTiles` (`MopFloor`, solo Sim interaction) offers itself while `World.GetBurntTiles(lot)` returns any tile that
  `PuddleManager.CanMopPuddle` allows.
- `Run` takes the first valid tile in list order and pushes `Terrain.MopBurnt` as a continuation.
- `MopBurnt` routes to the tile and fails because no destination around it is reachable.
- The tile stays burnt, so the next autonomy pass offers the same tile again. No memory of the failure, no floor test.
- The same shape as roof puddles (docs/546 row 6: `IsPuddleValid` has no solid-floor test, goals exactly 3.0 m up).

Cost [inferred]:
- One full local search per try, plus a 0 ms async plan.
- After the failure, EA's 5 sim-minute delay applies, because `MopBurnt` itself failed (`mLastInteractionSucceeded` false)
  and the Sims are selectable.
- That bounds it to about one try per Sim per 5 sim-minutes. That is ≤ ~0.3 searches/s on our save, against an SV excess of
  ~4.5/s in the L0 data.
- Real, but a side cost. It also wastes the Keatons' time: they keep "cleaning" a tile they never reach.

## 4. Fix designs

### 4.1 `burnttilefloor` (EA bug fix, small, design only)

What:
- Hook `MopFloorDefintion<MopBurntTiles>.Test` and `MopBurntTiles.Run`, or a shared filter on the tiles they iterate.
- Skip a burnt tile when its tile has no solid floor, or is on a level the actor cannot reach from its own level.
- Same lookups as `splashroof` (docs/546: `GetLotLocation` / `GetLotLocationAtFloor` + `HasSolidFloor`).
- When every burnt tile is skipped, `Test` returns false, so autonomy no longer offers the interaction.

Household rule:
- This changes what a household Sim does.
- Allowed only as an EA bug fix behind its own switch, default **off** until one repro passes (memory: fix-ea-bugs-too,
  small-fix-one-repro).
- The visible outcome equals vanilla's real outcome: the tile is never mopped either way. Only the impossible tries are gone.

No save changes:
- The tile stays burnt in the save (memory: no-mod-data-in-saves).
- An optional load-time "un-burn floorless tiles" step would be a save change. It needs the full save matrix, so it is
  not proposed now.

Repro: SV-Spring Keaton lot. Count `MopBurnt` routefails per 10 min with the switch off and on. Expect off > 0, on 0. Also
check that a reachable burnt tile is still mopped (set one with `World.SetBurnt` in the agent).

Effort:
- ~30-45 min agent build (switch, two hooks, counters via `registry.ts reserve fixes`, selftest);
- 1 repro job (~10 min).

Gain: small (≤ ~4 ms/s on this save [inferred]). Ships as a rare-case fix (memory: rare-case-fixes-bundle).

### 4.2 Idle no-pick loop (only if the probe confirms §2.1)

- Do **not** add a backoff for household Sims by default. A household Sim would then react later than in vanilla, which
  breaks the household rule.
- Exact option: memo "nothing found". If a household Sim's search returns no pick, and nothing that feeds the search has
  changed since (lot object add/remove counter, the Sim's motive buckets, buffs, the sim-hour bucket), the next search
  returns the same "nothing".
  - Skipping it gives the same result as vanilla only if the memo key really covers every input of the search.
  - That is the `kHoursMemo` exactness problem that docs/276 already ranks first. Prove exactness there first.
- Opt-in option: `[Experimental] HouseholdToo=1` lets a household Sim's no-pick search take the worker path (docs/271).
  It is a realistic-only site, never the default.
- If the probe shows a specific EA dead end instead (for example a top motive with no valid object, such as broken
  plumbing at Hygiene -100), fix that dead end as an EA bug behind a switch, like §4.1.

## 5. What the probe (`feature/hoursmemo-svprobe-1116`) should count

Per household search (the L0 hook point: B7 entry to B13 entry, household class only, which already exists in
`AutLocalProbe.cs`):
1. **Actor.**
   - Per-Sim search counts with SimDescriptionId and name, top 6 by count. Pets are separate rows.
   - Selectable flag and scope (household / unknown / not live) as `UfWhy` gave them. This also checks how much of
     "household" is unknown scope.
2. **Outcome of the search.**
   - Picked nothing (`AssignProbabilitiesAndChooseInteraction` null).
   - Picked but not pushed (`ConsiderPerformingAutonomousAction` false).
   - Pushed. For pushed: the definition name (top 10 per Sim).
3. **Why the Sim was enqueued** (read at the `AutonomyManager.Add` site, docs/243 §217: only two callers):
   - `CanRunAutonomyImmediately` true, split by last interaction succeeded or failed;
   - or the delay path, with `TimeSinceInteractionQueueBecameEmpty` bucketed as 5-10, 10-60 or > 60 sim-min.

   A large "delay path, > 60 sim-min, picked nothing" bucket proves §2.1.
4. **Last interaction before the search:** name and `mLastInteractionSucceeded`, top 10 pairs per Sim. This catches a
   succeed-without-acting loop (§2.1, second trigger) and the `MopBurnt` failure (§3).
5. **Gap since the same Sim's previous search,** in ms and in sim-minutes (histogram: < 0.5 s, 0.5-2 s, 2-10 s, > 10 s).
   Back-to-back searches with gap < 0.5 s are the loop signature.
6. **Motive snapshot when the pick is nothing:** lowest motive name and value. Shows whether a broken-object dead end
   (Hygiene -100) is behind it.
7. **Lot and level:** the Sim's lot id, and whether it is the active household lot.
8. **Re-read `aut_l0_pairn_*` against `aut_l0_n_*`** (§1 anomaly), and the pairs per search.

World and save:
- Run on **our** `Baseline-SV-Spring` (and BP for comparison), release ini + fixes as in L0.
- Ask Dalexlol for the same probe on his save. His save is the one with 5.2/s, and the 8x gap may be partly his save state.

Decision rule:
- If one Sim with the "nothing picked, delay > 60 min" signature makes up ≥ 50% of household searches: §2.1 is proven.
  Design §4.2 (the exact memo first).
- If the top pushed definition is the same one, over and over, with failures: an EA dead-end bug. Fix it like §4.1.

## 6. Not done here

- REA/Ghidra on TS3W.exe: not needed. The scheduling is managed (`AutonomyManager.Simulate`, `Sim.DoInteraction`), and the
  local-phase caller is B7/B9 in `Sims3GameplaySystems`. Skipped also because a night chain is running (no heavy CPU).
- `tools/managed-callgraph.ts`: not run. The decompiled read answered the caller question.
- Community/NRaas search:
  - docs/24 lists no Sunset Valley household-autonomy-loop issue. The only puddle-related entry is an MTS puddle tuning mod
    (row 45). [verified grep]
  - No web search was made for "unreachable burnt tile" reports.
  - NRaas Overwatch has stuck-Sim and reset tools but no backoff for idle searches [community-reported, not checked here].
- The test install contains NRaas ErrorTrap and LynMods (seen in managed stacks). Neither shows up inside the SV B9 caller
  split (§0), but a mod that leaves a household Sim with nothing to pick would only show up in the probe's §5 item 2/4.

## 7. Built 2026-11-16 (branch `feature/hoursmemo-svprobe-1116`, NOT RUN)

### 7.1 The `aut_l0_pairn_hh` "anomaly" (§1) is a report cut, not a counter bug [verified]

- The results md writer (`tools/harness/queue.ts`, the row's verdict cell) cut every cell at 400 characters.
- In the BP and SV rows the `ts3fixes activity:` list ends exactly at that limit, on `aut_l0_pairn_hh 0->3` and `0->316`. The full values were 397 and 3,163, the same as `aut_l0_n_hh`.
- The IP row, where the list goes on past `aut_l0_pairn_hh 0->281` (= `n_hh` 281), shows the slot is right.
- Fix: `mdCell` cuts at the last item boundary (", ", "; ", "<br>") and appends "[cut: full row in the jsonl]" (self-check cases in `queue.ts`). The pair figures of docs/276 (727 per search on SV) stand.

### 7.2 Probe as built (`hhsearchprobe`, test-only switch)

Hook points (all read-only; household Sims are only counted):
- `AutonomyManager.Add` (`AutQueueHook`): why the Sim was enqueued. It reads the fields `Sim.DoInteraction`'s idle branch just read: `IsRunningLocalAutonomy` as `CanRunAutonomyImmediately` left it, `mLastInteractionWasAutonomous`, `mLastInteractionSucceeded`, `TimeSinceInteractionQueueBecameEmpty`. `ShouldRunLocalAutonomy` is not called, because its getter writes `IsRunningLocalAutonomy`.
- `Autonomy.ConsiderPerformingAutonomousAction` (`AutPushHook`): every `RunAutonomy` search with `addToQueue` reaches it, with a pick or with null [verified, decompiled].
- `L0Close` (with `autt2l0` on): the pair count of the same household search.

§5 items, counters fixes-1073..1084 and 1115..1134, per-Sim lines `hhsearch_sims` (top 6 by count):

| §5 item | Where |
|---|---|
| 1 actor | per-Sim line: name, SimDescriptionId, pet, selectable, scope; counters `hhsearch_scope_*`, `hhsearch_pets` |
| 2 outcome | `hhsearch_nopick`, `_busyuser` + `_busyauto` (picked, not queued, by the queue head), `_push`; top 10 pushed picks per Sim |
| 3 why enqueued | `hhsearch_why_immok`, `_immfail`, `_lt5`, `_5to10`, `_10to60`, `_over60`, `_none`; `hhsearch_nopick_over60` |
| 4 last interaction | top 10 "previous autonomous pick or user/other, ok/fail" per Sim. EA keeps no name of the last interaction, so the previous autonomous pick stands in; a continuation such as `MopBurnt` shows as its parent `MopBurntTiles` [inferred] |
| 5 gap | `hhsearch_gap_lt500ms`, `_lt2s`, `_lt10s`, `_ge10s`; `hhsearch_shortgap` (< 2 sim-min) |
| 6 motive at no pick | lowest of Hunger, Energy, Bladder, Hygiene, Social, Fun, bucketed to 25, top 3 per Sim |
| 7 lot and level | per-Sim last lot id and level; `hhsearch_activelot` |
| 8 pairs | `hhsearch_pairs` / `hhsearch_pairn` beside `aut_l0_pairs_hh` / `aut_l0_n_hh` |

- Queues: `tools/harness/queues/hhsearch-sv-1116.json` (SV-Spring + BP-Spring, 12 min each) and `hhsearch-dalexlol-1116.json` (the same two jobs for his machine and save).
- Offline: `tools/selftest/autlocalprobe` (record, EA idle-branch order, scope, gaps).

### 7.3 `burnttilefloor` as built (§4.1)

- One hook: `Sim+MopBurntTiles+Definition.GetMoppableLocations(lot)`. Both `MopFloorDefintion.Test` and `MopFloor.Run` read the tile list through it [verified, decompiled].
- Tiles above the ground floor with no solid floor (`level > 0 && !World.HasSolidFloor`, splashroof's floor rule) are left out; EA's order is kept otherwise.
- Applies to every Sim: the visible outcome equals vanilla's (the tile is never mopped either way). The save is not written.
- Not covered: a tile that has a floor but cannot be reached (another level with no stairs). If the Keaton tile is one, the repro's on arm still fails and the rule needs a reachability test [inferred risk; `BurntTileCheck list` prints level and floor per tile].
- Not checked: that a reachable burnt tile is still mopped (no agent `SetBurnt` step yet).
- Counters fixes-1135..1138 (`burnttile_dropped`, `_lists`, `_empty`, `_err`).
- Repros `burnt-tile-floor-off` / `-on` force MopBurntTiles 3 times, 60 s apart, on the Keaton lot.
- Queue `tools/harness/queues/burnttilefloor-sv-1116.json`: the off arm must show the routefail line for goal 1064.7,52.8,1243.2, the on arm must not.

## 8. `burnttilefloor` design 2: EA's planner decides (2026-11-17, branch `bugfix/burnttile-reach-1117`, NOT RUN)

### 8.1 Why design 1 missed [verified data, cause inferred]

- `burnttilefloor-sv-1116` off arm (chain-1116d): the list step logged `burnt tiles 80, no floor 0`; the 12 tiles shown are all
  L2 with a solid floor. The same job has 20 MopBurnt routefails, all at goal 1064.7,52.8,1243.2. Design 1's no-floor rule
  (§7.3) matched no tile, so the on arm could never pass (ledger I-493-5 refuted).
- Every MopBurnt plan in our logs (432 lines over all queue dirs) has that one goal and `FailedBecauseNoDestinationSpecified`,
  431 async and 1 sync (`rebaseline-1116c` sv-apex-on-3): not our async planner [verified, counted].
- EA flow [verified, decompiled]: `MopFloor.Run` pushes `Terrain.MopBurnt` for the first tile of `GetBurntTiles` (list order is
  fixed, so always the same tile); `Mopping.Run` takes the nearest burnt tile in that tile's room and level;
  `PuddleInteraction.RouteToPuddle` plans a `RadialRangeDestination` around it (min radius 1 m, max auto) with
  `SetValidRooms(lot, {tile room})` and `ConstrainGoalsToSingleLevel`, and retries once with `CheckForFootprintsNearGoals` off.
  Both plans end with no destination left after the room/level limits.
- So the failing target is an L2 tile with a floor (the Sims stand at y 49.1-49.8 = L1 on a foundation lot, the goal is
  y 52.8 = L2) whose room gives the planner no usable goal: most likely an upstairs room with no stairs link to the Sims'
  level, or no free spot inside it [inferred, ledger I-497-1]. Not off-lot (the lot id resolves) and not floorless [verified].
  The rerun2 list steps print room and world position of the first 12 tiles to name it.

### 8.2 Design as built

- Learn, don't guess: in `RouteFailLog.RflPlan` (runs after every Plan/Replan/PlanFromPoint/ReplanFromPoint), a failed plan
  with result `FailedBecauseNoDestinationSpecified` whose Sim's current interaction is an **autonomous** `Terrain.MopBurnt`
  and whose route has `CheckForFootprintsNearGoals` off (EA's last try) marks its goal tile (`GetLotLocation` +
  `GetLotLocationAtFloor`, x/z/level) unreachable on that lot.
- `GetMoppableLocations` hook (same as design 1) leaves learned tiles out, EA's order kept. Test answers false when only
  learned tiles are left; Run goes to the next tile.
- Forgotten: after 24 sim-hours, when build/buy adds or removes an object on that lot (`RflObjectEvent`, e.g. stairs), and at
  world quit. Max 256 tiles per lot (the rest keep EA's behaviour). Nothing is written to the save.
- Household rule: `Sim+MopBurntTiles` is only ever autonomous in EA code (solo ad; imaginary friend push with
  `isAutonomous: true`) [verified]. A player click on a burnt tile runs `Terrain.MopBurnt` directly: never learned from (not
  autonomous) and never filtered (other path). No Sim or no lot id = unknown scope: nothing learned. The outcome equals
  vanilla's (EA's own planner already said no destination). Cost: one failed try per tile per 24 sim-hours instead of one per
  ~5 sim-minutes per Sim.
- Design 1's no-floor rule is removed (it matched nothing; a floorless tile also fails EA's planner and is learned the same way).
- Counters: fixes-1135..1138 as before, plus fixes-1139 `burnttile_learned` (tiles learned) and fixes-1140 `burnttile_repeat`
  (a NoDestination fail on an already learned tile: 0 = the memo held).
- Harness: `BurntTileCheck list` prints levels, floor, learned unreachable and room + world position of the first 12;
  `push` now queues MopBurntTiles autonomous (as EA does) and reports why a Sim was not queued (design 1's pushes queued
  0 Sims in that job; cause not known, the counts will say).
- Repros `burnt-tile-floor-off/-on` list before and after the tries. Queue
  `tools/harness/queues/burnttilefloor-sv-1116-rerun2-1117.json` (supersedes rerun1): off arm = goal line >= 1, nothing
  dropped or learned; on arm = goal lines <= 4 (one try: 2 plans x up to 2 Sims), learned >= 1, dropped >= 1, repeat 0, err 0.
- Not checked: that a reachable burnt tile is still mopped (no agent `SetBurnt` step; unchanged from §7.3).

### 8.3 rerun2 result and the memo length (2026-11-17)

- `burnttilefloor-sv-1116-rerun2-1117` (chain-1117b): off arm PASS. On arm FAIL on the gate only: 6 routefail lines for goal
  1064.7,52.8,1243.2 (want <= 4), while `burnttile_learned` 5, `burnttile_dropped` 555 in 311 lists, `burnttile_repeat` 0,
  `burnttile_err` 0 [measured].
- Learning works [verified]: the fallback plan's `CheckForFootprintsNearGoals` reads back off after the async plan (ledger
  I-497-2), and no failure hit a tile while it was learned (repeat 0). The tile Run picks next was reachable for none of the
  6 tiles seen (all L2 room 0: 1064.7, 1063.8, 1065.1, 1064.2, 1066.4, 1065.5), each failing once with 2 plans from one Sim.
- Cause of the 6 lines [verified, counted]: the memo's 24 sim-hours. The harness runs at ultra speed: `SimClock.CurrentTicks`
  in the `ufplan` lines grows 444 ticks/s = 11.8 sim-minutes per real second, so 24 sim-hours is ~122 s real. Every learned
  tile failed again 122-155 s after it was learned (1064.7 at +0, +178, +306 s; 1063.8 at +27, +180, +335 s; the others the same).
- Fix: memo 7 sim-days (`kBtMemoHours` 168; ~14 min real at ultra, ~2.8 h at speed 1). Still one try per tile per 7 sim-days
  instead of one per ~5 sim-minutes per Sim. Build/buy object changes on the lot still forget at once; a wall or floor change
  alone (not an object) waits for the expiry; a player click on the tile is never filtered. Gates unchanged: with the memo
  holding, each tile gave exactly 2 lines from one Sim. Queue `burnttilefloor-sv-1116-rerun3-1117.json` (fixes
  `burnttile-reach-1117b`, same agent).
- Seen in the list step: all 80 burnt tiles are L2, room 0 at their world position, with a solid floor (ledger I-497-1: an
  outdoor L2 floor, e.g. a deck or roof floor, with no route up; still inferred).
