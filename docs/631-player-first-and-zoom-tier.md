# 631: player-first route queue and the zoom tier

2026-11-17, static + logs, package r83-resp, NOT RUN. Two default-off switches, slots 3660-3689 (`Responsive631.cs`).

## 1. What delays a click (measured)

Source: `route async: long job` lines of the 219 `phase-2026111*/ts3native.log` runs (only jobs >= 200 ms are logged; "at" is
the job's end, so start = at - job ms).

| Scope (ts3fixes scope op) | Long own-START jobs | Mean ms | Max ms |
|---|---|---|---|
| unseen | 37323 | 330 | 46804 |
| seen NPC | 6321 | 345 | 15210 |
| household-or-seen (household side) | 8380 | 314 | 21088 |

- The async route worker runs one job at a time, oldest START first (`ServeAsync`, ts3native.cpp). 171 household long jobs
  started within 3 ms of a townie long job's end (151 behind unseen, 20 behind seen NPC): they had queued behind it.
  Mean townie job ahead of them 353 ms, max 1965 ms. Lower bound: jobs under 200 ms are not logged.
- `refused` STARTs (all slots busy, the route plans sync on the sim thread): 147-164 a run in the 1320/1327 runs.
- Lock waits (`lock holders: sim waited ... held by main`) are 20-137 ms and not queue order; out of scope.
- EA's own queues: a Sim's `InteractionQueue` already orders by priority (UserDirected before autonomous), so a click is not
  queued behind townie interactions there. Our `replanspread` replay (ReplanDefer.cs, after build/buy exit) was FIFO, 4
  routing Sims a tick: a household Sim could wait ~N/4 ticks behind townies.

## 2. `playerfirst` (3660-3669)

A route the player ordered: household / selectable / selected Sim (`IsPlayerSim`), or the Sim's current interaction is
non-autonomous `UserDirected`. Our own route checks (stuck check, routable probes) are never marked.

- ts3fixes, UnseenFallback plan hook, right after the scope op: `DiagSend(37, handle)` (UserDiag op 37). ts3native keeps it
  for that route's next START (`PfTake`, like `KdScopeTake`), op 12 clears it.
- ts3native `[RouteAsync] PlayerFirst` (default 0): `ServeAsync` picks a marked queued job before older unmarked ones,
  oldest first within each group (`PfBefore`). Each job is the same IC on the same START snapshot (world lock,
  `RtQuietWorld` at claim): only order changes. No starvation: a household has <= 8 Sims, one job per route. Log line
  `route async: PlayerFirst: job ... ran ahead of N older queued job(s)` at jumps 1, 2, 4, ... Self-test in selftest.cpp.
- replanspread: a player Sim's held replan is inserted after the player entries already at the head (`SpreadQueue.Add(x,
  first)`). Same call per entry. Tidy: `DrainTick` removes the taken entries in one `RemoveRange` (was `RemoveAt(0)` each).
- Counters: [3660] marks honoured by ts3native (op 37 answered 1), [3661] not honoured (PlayerFirst=0 or older dll),
  [3662] replays put first, [3663] player routes with the switch off, [3664] errors.
- Check `PlayerFirstCheck` (repro `playerfirst-check`): off BROKEN (order abpq), on FIXED (pqab), controls: townie with an
  autonomous interaction and our own checks unmarked. Needs ts3native with PlayerFirst=1 for 3660 to grow.

## 3. `zoomtier` (3670-3689)

[verified, ilspycmd `Sims3.SimIFace.CameraController`] `GetPosition()` (eye, world metres), `GetZoom()`,
`IsMapViewModeEnabled()`; no FOV or back-buffer size in script. Threshold `kZoomTierMeters = 250`: a 1.8 m Sim at distance d
is H * 1.8 / (2 d tan(fov/2)) px tall; with an estimated ~45 degree vertical FOV (inferred, not readable) that is ~7 px on an
800-line screen and ~10 px on 1200 lines at 250 m. Map view always counts as too small.

- `ZoomHidden(GameObject o, Sim sim)` / `ZoomHidden(Sim)` / `ZoomHidden(ObjectGuid, Sim)` (Responsive631.cs): true when
  `!UcOnScreen`, or (switch on) on screen but camera >= 250 m or map view. A player or selected Sim is never hidden by zoom.
- Wired: rabbit hole middle tier (`rhentryrelaxnear`, RhEntry627.cs) decision and its at-action re-check. The unseen tier
  keeps the real on-screen test. `unseenservice_offscreen` (branch feat/unseen-service-1117, UnseenService88.cs, not edited
  here) can call `ZoomHidden(id, sim)` in place of its `!UcOnScreen(id)` once both branches meet.
- Counters: [3670] tier-2 decisions lifted by zoom, [3671] would lift with the switch off, [3672] errors.
- Check `ZoomTierCheck` (repro `zoomtier-check`): off BROKEN, on FIXED; controls: off-screen kept, close / household /
  selected (also in map view) seen. `run` prints the live camera (map view, zoom, eye) to calibrate the 250 m.

## 4. Next

Run r83-resp with ts3native from the fork CI build, `[RouteAsync] PlayerFirst=1`: compare household job start-after-townie
counts (section 1 method) off vs on; read zoomtier's live camera line at max zoom-out to confirm 250 m is reachable.

## 5. Household route-fail incidents in the s220-s247 storms: not the job order; two limits and a per-jump line (2026-11-21, `fix/playerfirst-jam-1121`, NOT RUN, no MSVC build)

Question: 8 of 12 two-minute Isla Paradiso storms with `[RouteAsync] PlayerFirst=1` had a household incident (a Sim
replanning one short route 7-15 times at a doorway or stair top; s229 / s241: four Sims on the third floor with
`NoPathToDestination` to every lower goal), 0 of the 20 storms s202-s247 without it. Is the worker's order the cause?

### 5.1 What the switch changes [verified, source]

`g_pfMode` is read in three places: `PfBefore` (the pick in `ServeAsync`), the jump line, and the answer to op 37 (a
counter in ts3fixes). `j->pf` is also read by SkipRetry's scope test (off in these runs). Nothing else: no job gets a
different call, snapshot or deadline. With P4 / P5 off (these runs) a job has no cut or image taken at START; it reads the
world under `+0x918` when it runs. So the only effect is that a marked job runs before older unmarked ones, and the jump
line counts each time that happened: **4-16 times per storm** of 1,700-1,900 jobs (`jumps` 4-16, `jobs passed` 13-84).

### 5.2 What the logs show [verified, log lines of the runs named]

- **The loop is there without the switch.** The same count over every storm phase since s141: 15 of 71 phases without
  PlayerFirst before s202 have a household route with >= 5 fails (s142, s143, s148, s151, s158, s160, s172-s175, s179,
  s184, s185, s187, s192), and 7 of 8 idle phases (s188, s219, s224, s233, s234, s236). Same lot, same spots, same
  results: `Replan` x13-14 `DestinationPortalLocked` at 927.5,58,1203.3 to 930.4,58,1201.5 in s151 (sync replans) and
  s160, which is the s220 loop; 928.0,58,1204.8 to a goal at y 61 in s174 = s240; the stair top 927.3,61,1203.2 in s148.
  docs/545 and docs/638 §1 read this as EA's obstacle stand-and-wait (one `Replan` per wait cycle while the goal or portal
  is taken) [verified there, decompiled].
- **s238: the loop began before the first reorder.** Its first failed `Replan` is job seq 61; the line `jumps 1` comes
  after the loop's third fail, and the jumping job is a `Replan` of that loop. Up to that line the worker's order was
  START order, as with the switch off.
- **Household plans are not served sooner in these storms.** `actdelay` `plan-job` of household lines: median 56 ms with
  the switch (12 runs, 90 lines), 46 ms without (7 runs, 44 lines); 2 lines >= 300 ms in each group. Household plans per
  run are equal (mean 168 and 167, fails taken out).
- **The stranding (s229, s241) follows the full clone set, not the switch.** Among the PlayerFirst runs: 2 of 3 with
  ObstructAtRO + Blocker2dRO + LandCheckRO serving + SteerRO RetryWait + JobYield, 0 of 9 without that set. No storm has
  that set without PlayerFirst, so the logs cannot split the two. `upstairs-landing-jam` arm d (the full set, placed, no
  storm) passed in three loads (docs/670 §26.1).
- Caches (hypothesis: a stale negative answer): `route nopath memo` `recorded 0 served 0` in s229 and s247; `door share`
  `household 0`; `connected rooms RO` answered 26 calls in s229 (served, not shadow-checked in that run). Not read: ReplanDedupe (serves a failed replan again while
  the world version is unchanged, never type 18; on in every run, no counter line in these logs).

So: 8/12 against 0/20 is not explained by the order (inferred from the above: 4-16 reorders a run, one loop before the
first of them, the same loop at a 21% base rate before s202). Of the two same-key groups, s240 + s231 + s245-s247
(2 of 5) against s242-s244 (0 of 3) is no difference (Fisher p = 0.36). What changed the base rate at s202 is open.

### 5.3 What was wrong in the order anyway [verified, source; host test]

"No starvation: at most 8 more jobs" (§2) does not hold. A route queues its next job as soon as the last one is
collected, and a blocked Sim's wait cycle is a marked `Replan` every cycle, so an unmarked job queued before it waits as
long as the cycle does (EA: up to 50 sim-minutes). The jobs are 0-1 ms each, so the cost in these storms is small; the
bound is still wrong, and the cycle is not a click.

### 5.4 Built (`playerfirst-core.h`, `ServeAsync`, `AsyncStart`)

`PlayerFirst=1` now has two limits; `PlayerFirst=2` is the order as before (comparison arm).

- **Bound:** each queued unmarked job counts the marked jobs run ahead of it (`pfPassed`); at `kPfMaxPassed` = 8 it is
  served before the next marked job, oldest first.
- **Repeats:** a `Replan` / `ReplanFromPosition` (kinds 1, 3) of a route whose last marked job failed keeps START order
  (`pfDem`; a 32-slot table of route handles, noted by the worker at the job's end, forgotten at the route's next
  success). A `Plan` / `PlanFromPoint` is always served first: a click's plan and a moving Sim's replans keep the gain.
- **Lines:** every one of the first 64 jumps is logged (then 1 in 2^n) with the job's seq and kind and the oldest seq
  passed, so an incident can be read against the reorders before it; summary `route async: PlayerFirst: mode M, jumps J,
  jobs passed P, unmarked jobs run by the bound (8 passes) A, repeat replans in START order R` with the footprint lines.
- No counter fields (layout unchanged). Household results: the same calls; the limits only move jobs back to START order.

### 5.5 Checks (no game)

- `playerfirst-host-test.cpp` (clang++ `-m32`, freestanding, exit 0): the gain (a click's plan ahead of queued townie
  jobs, both groups in order, seq wrap); a blocked Sim's wait cycle with one townie job queued first: mode 2 runs 1000
  household jobs and never the townie job, mode 1 runs it second; eight household Sims with successful plans: mode 1 runs
  the townie job after 8 passes, mode 2 never; the repeat table (kinds, modes, success forgets, a full table forgets).
- `selftest.cpp` (MSVC, **CI only**): `PfFirst`, `PfRepeat` cases next to the `PfBefore` ones.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: no error, warning count unchanged.

### 5.6 Next runs

Same seed and keys as s245 (`JobYield=0`) and s240 (`JobYield=1`), 4 each with `PlayerFirst=1`, and the full s229 set
x3. Because the loop has a base rate without the switch, add in the same session: `PlayerFirst=0` x4 (s242 keys) and the
full set with `PlayerFirst=0` x3 (the missing arm for the stranding); `PlayerFirst=2` x4 if the others differ. Read each
incident against the jump lines: a loop whose route is not in a jump line and has no jump in the jobs before its first
fail is not the order's. Pass for the limits: the summary line shows `repeat replans` > 0 in a run with a loop and no
unmarked job passed more than 8 times; incidents with `PlayerFirst=1` not above the `PlayerFirst=0` arm.
