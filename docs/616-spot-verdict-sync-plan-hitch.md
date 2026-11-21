# 616: the Bridgeport 12:46 PM hitch: SafeSpot's sync routability plan (`spotasync`, s86)

2026-11-17, static (logs, ilspycmd, source), r74, built, NOT RUN. Follows docs/584 §3.

## 1. The hitch

`node tools/harness/hitch-attr.ts <run> --lines 12` on the three phase runs: each has exactly one game hitch, all in
Bridgeport, Sun 12:46-12:47 PM, tick ~28760 [verified, logs]:

| run | hitch | plan 10 ticks before | Sim | dist |
| --- | --- | --- | --- | --- |
| phase-20261117-0857 | 314 ms | `ufplan: kind 2 271 ms why -1 src spot`, `sync 270 ms (START refused)` | Michael Sleep | 704 |
| phase-20261117-0127 | 360 ms | 281 ms, `sync 280 ms (START refused)` | Martin Shaker | 224 |
| phase-20261117-0923 | 370 ms | 281 ms, `sync 277 ms (START refused)` | Enyo Strong | 332 |

All Succeeded. Not one other `src spot` plan in any of the three logs.

## 2. Which caller [verified for the shape, inferred for the exact site]

- `Sim.IsPointInLotSafelyRoutable` [verified, ilspycmd] is one `PlanFromPoint(pos)` of a `CreateRouteAsAdult` route with
  8 `RadialRangeDestination`s (the lot's 4 corners and 4 edge midpoints). `ufplansplit`'s `dist` is the follower Sim's
  position to the route's first destination: 224-704 m means the Sim was nowhere near the lot it checked.
- `src spot` with no `in X's task` means the plan ran in the Sim's own task (UfSrc).
- That rules out `RouteFixes.cs:364` (the watchdog: `src watchdog`, its own task), `UnseenFallback.cs:776` (UrRepairSim:
  `sim.Position` on the Sim's own lot, sweep task) and `:622` (UfVerdict: `mayCheck` false, never plans), and the cached
  reuse branch `Fixes.cs:2298` (`mayCheck` false). It is `Fixes.cs` SafeSpot's fresh check (old line 2315): a trip shortcut
  of a far, unseen NPC in its own task. SafeSpot's callers in the Sim's task: `gohome`, `rabbithole`, the lot trips
  (`visit`, `visitlot`, `gotolot`, `gotocommunity`, `unseenfbtrip`) and `visitwalkfinish` (route interrupt, on in these runs).
- New tag (log only): the `ufplan` / `ufplansplit` src now reads `spot:<caller>@<lot>` (for example
  `spot:visitwalkfinish@Bridgeport Acres`), `spot:other` for the non-SafeSpot full checks. The next run names the site.

Why the same game time: the three runs start from the same rewind snapshot, so the same autonomous trips start at the same
ticks; a different Sim wins each time because autonomy's picks differ run to run [inferred]. The likely clock is
`visitwalkfinish`'s 10-sim-minute pass (an NPC still walking 60 sim-minutes after its visit Run started) or a visit / go-home
trip about an hour after the load; the tag settles it.

## 3. Why the cache missed

SpotVerdicts is in memory only: per lot, per 0.5 m cell and route profile, one sim-day, empty after every load [verified,
source]. SafeSpot's fresh path asks `FindGoodLocation` from the entrance and checks that new spot's cell; its `Good` list is
empty until the lot's first check of the session. So the first shortcut to each lot in a session always pays one full
check, and these runs were ~45 sim-minutes into the session. Not a bug: a cold cache. A persisted cache would change save
data and could go stale (lots change); not done.

## 4. Fix: `spotasync` (default off, 3485-3489)

The plan was sync because `[RouteAsync] Kinds=0` refuses own kind-2 STARTs. ts3native already starts one kind of own
PlanFromPoint START with Kinds=0: the watchdog's A1 check, tagged `kProbe | 0x10400`, accepted with `WatchdogKind2=1`
(on in the installed ini) [verified, ts3native.cpp `wd2`]. A tagged job is never cut by KindDeadlines: plandeadline.cpp
returns deadline 0 for a job with `sc & 0x400` (`uncut: watchdog`, only the 30 s KdUncutCeilMs ceiling) [verified, source;
the 0857 log shows `long job 252 ms, own START, IC kind 2, ... watchdog check, uncut: watchdog`]. So the job's verdict is
the planner's own, the same IC and handshake as A1, whose sync shadow arm already exists.

With `spotasync` on, SafeSpot's full check (SpotVerdict, cache miss inside the tick's budget) takes that path when
(`H4Core.SpotAsync`): the check runs in the Sim's own task, `MayYield`, ts3native would start it (`WdA1Would`, q 9), the Sim
is an unseen NPC (`UfWhy` 0, never household / selected / unknown scope), outside the watchdog's own check. The Sim's task
sleeps until READY; the verdict is cached as today. After a wait, SafeSpot places the Sim only if the caller's gate still
holds (`H4Core.SpotPlaceAfter`: alive, `TownieUnseen`, `FarFromViewers(near)`, as SafeSpotYield); else SafeSpot answers
false and the game's own trip runs. The ceremony caller (EventRouteRetry) stays sync (`asyncOk` false: it has no unseen gate).

Bug fixed with it (ours): `sInOwnCheck` was one slot, so two sleeping checks cleared each other's cover; it is now a
per-task set. Only reachable once checks sleep (A1 or spotasync); no change for sync checks.

Counters: 3485 full checks answered by a job (sync plans avoided), 3486 their wall ms (~the sync cost moved off the sim
thread [inferred]), 3487 eligible but planned sync (START refused), 3488 placements dropped after the wait, 3489 shadow
mismatches (with `watchdogasyncshadow` on, each async verdict is re-planned sync and compared; expected 0).

Proof: `SpotAsyncCheck run` (pure decision + controls, ts3native q 9 now, one live sync verdict; `clear` empties the cache
for the repro), h4core selftest cases, repros `spotasync-check` and `spotasync-trip-loop` (hitching repro: cold cache,
8 townies to the farthest lot; UNVERIFIED).

## 5. `routableasync` A/B instead? No

Installed ini: `Kinds=0`, `ForeignKinds=4`, `KdOwnPlanFromPointMs=250`, `WatchdogKind2=1`. `routableasync` needs `Kinds=2`
(an ini change: relaunch, no rewind ABBA), and with `KdOwnPlanFromPointMs=250` a ~280 ms check is cut and answers "not
routable", which SpotVerdict caches for a sim-day: a different verdict. Its A/B needs `KdOwnPlanFromPointMs=0` as well
(docs/584 §3). `spotasync` needs no ini change and is a runtime switch, so it is the cheaper A/B.

Phase run for `spotasync` (no ini change): pair `spotasync SpotAsyncCheck`; EARLYFIX adds `"ufplansplit":true` and, for
correctness, `"watchdogasyncshadow":true`. Read `ufplan ... src spot:` lines, `planwait` async vs sync, 3485-3489.

For the `routableasync` A/B only:
```
[RouteAsync]
Kinds=2
KindsMode=1
KdOwnPlanFromPointMs=0
```
with EARLYFIX `"routableasync":true` `"ufplansplit":true`.
