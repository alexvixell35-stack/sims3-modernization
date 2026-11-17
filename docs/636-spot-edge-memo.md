# 636. Spot edge memo: fewer goals for the lot routability check

Status: static, built into r96-combo (memo only, running in s79) and r97-combo (+ seed, memo across sim-days), NOT RUN. Switch `spotedgememo` (default off).

## 1. Finding

- [verified, logs] The slowest spot checks in the 2026-11-1x runs are townies idling at Bridgeport Eternal Rest
  Cemetery: 300-578 ms each, all Succeeded. Seven of the top nine slow `spot:visit` checks are on that lot. Across
  `phase-2026111*/ts3native.log` there are 135 `ufplansplit` lines on the cemetery (150-1000 ms, median ~206 ms;
  120 Succeeded, 15 FailedBecauseNoPathToDestination).
- [verified, ilspycmd] The check is EA's `Sim.IsPointInLotSafelyRoutable(lot, pos)`. After cheap early answers (null lot or
  corners, zero or out-of-world spot, spot on another lot, water terrain, world lot = true), it plans one route from
  `pos` with `CreateRouteAsAdult()`, `EnableWaterPlanning = IsHuman`, and 8 `RadialRangeDestination`s of radius 1 m
  (2 m on a houseboat lot): corner i then the midpoint of edge i..i+1, for i = 0..3. Only `PlanResult.mType ==
  Succeeded` counts. `RoutePlanResult` carries `mDestination` and `mDistanceTotal`, so the reached goal can be read back.
- Hypothesis [inferred, not measured]: the cemetery's corners and some edges are fenced or walled off. The multi-goal
  search is pulled toward near but unreachable goals and floods the enclosed lot before it finds the gate edge.
  `SpotEdgeMemoCheck probe` measures it: EA's path length vs the straight distance to the reached goal, and each goal
  planned alone (ms, result). A failed single goal floods the lot, so the probe hitches on purpose.

## 2. Fix `spotedgememo` (default off, exact)

`SpotEdgeMemo.cs`, called from `RoutableCacheHook` (FewerPlans.cs) wherever it would call EA's original, so it covers
our SpotVerdict (and the spotasync job), routablecache misses and EA's own callers.

- Memo: per lot and route profile (`SpotVerdicts.Edge`, keyed like the verdict cells with cell 0), a bit mask of the
  goal indices that a successful full plan reached (`mDestination` matched to the nearest goal within radius + 0.25 m,
  captured in `RouteBackoffHook.PlanFromPointHook` for the armed task only).
- Next check on that lot: the same route (same options, same radius) to the remembered goals only.
  - Success: a path from the spot to one of EA's own goals exists, so EA's 8-goal plan also succeeds: answer true.
  - Failure: EA's own full call answers, so every false is EA's own verdict; the mask is cleared and relearned.
- Exactness [inferred]: a multi-goal plan succeeds when any goal is reachable. The one theoretical gap is the planner's
  abort budget (`FailedBecausePlannerAborted`): an 8-goal plan could abort where a 1-goal plan succeeds; not seen.
  So household Sims use it too (proven-exact speedups stay); no HouseholdToo gate is needed (no worker path added).
- Invalidation (r96; r97 below): no build/buy hook is needed; a stale entry can only cost a fallback plan, never a
  wrong answer, since a memo success is a live plan.
- spotcut / spotbusydefer: when a memo plan's job was cut or deferred, the check returns false without the fallback
  plan (SpotVerdict discards that verdict as "no verdict" anyway).
- Cost: a spot EA would reject now pays the memo plan plus EA's full plan (both fail). The verdict caches limit repeats.

- Seed (r97): with no memo for the profile yet, the check first plans one goal, the edge midpoint nearest the lot's street
  side ([verified, ilspycmd] `Lot.LotEntryPnts[0]`: EA's road-nearest point between an edge midpoint and the lot center,
  sorted by `World.FindPlaceOnRoad` distance; else `Lot.EntryPoint()`, front door or mailbox slot; no seed when EA fell back
  to the lot position). Success = true and the goal is remembered (exact, as a memo hit); failure = EA's full call. A lot
  stops seeding after 2 misses.
- Memo storage (r97): its own per-lot dictionary (`SemLot`, at most 512 lots, then cleared), kept across sim-days. Dropped
  on world load start or quit, lot added or removed, the clock going back (another save), or when the lot's position or
  corners changed (moved or replaced). Up to 2 goals per profile (`SemAdd`); a miss keeps the mask and adds the new goal.

Counters 3806-3813: memo hits, misses (no memo), fallbacks (memo plan failed), ms saved estimate (the lot's last
full-call ms minus the memo plan's ms, per hit), goals learned, check mismatches (memo or seed true where EA said false;
must stay 0), seed hits, seed misses.

## 3. Test

Repro `spotedgememo-check` (tools/harness/repros.json): `SpotEdgeMemoCheck run` off then on. It picks a Sim on an
eligible lot spot (a lot named Cemetery first), runs EA's call with the memo bypassed, then the memo route to the goal
EA reached, then the seed goal (a seed true where EA said false counts as a mismatch). FIXED needs the memo to plan fewer than 8 goals with the same verdict; pure controls: off or no memo = EA's
call, memo failure = EA's call, the street-side edge pick on a test square. Then `probe` (hypothesis data) and `result`. Pending in tools/harness/sets/proven-fixes.txt.

## 4. s79 result and r99: cheapest goal alone first

- s79 (phase-20261117-1751, r96/r97 path, smallfix.txt) [verified, game]: `SpotEdgeMemoCheck` off BROKEN / on FIXED,
  mismatches 0, seed hits 72 vs 2 misses. Live line, Brigit Hemlock at the cemetery: EA's 8-goal plan 129 ms, reached
  goal 7, path 77.6 m vs 31.5 m straight; goal 7 alone 116 ms; the seed goal 3 (street side) alone 13 ms. So the goal EA
  reaches is not the cheap one, and remembering it saves little.
- r99: each learned goal (from EA's full call or a successful seed) keeps a cost, the EWMA (alpha 0.3) of its single-goal
  plan ms; a failed try also counts, so a goal this spot cannot reach sinks in the order. A check plans the learned goals
  one at a time, cheapest known cost first (unknown last), at most 2 single-goal tries, then EA's full call. Still exact:
  any success is true, and false only comes from EA's full call. Learning from the full call keeps the 2-goal cap; over
  it, the most expensive goal is evicted (and its cost forgotten) instead of restarting the memo.
- Counter 3814: hits on the first single-goal try. The Check's live line prints the memo's per-goal costs and the try
  order; pure controls cover the eviction (the s79 costs keep goal 3), the order and the EWMA.
