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
