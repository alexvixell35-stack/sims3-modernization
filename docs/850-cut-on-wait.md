# 850. `[RouteAsync] CutOnWait`: end a townie's route job when the sim thread waits for it (2026-11-14)

Contributor switch, next version, off by default (`CutOnWait=0`). Code: `native/ts3native/plandeadline.cpp` (`CowJobBegin`,
`CowAsk`, `CowFindSkip`), hook points in `ts3native.cpp` (`ServeAsync`, `WaitAsyncDone`, the +0x918 lock pump, `BudgetStep`),
selftest block 11b in `selftest.cpp` and step 5 in `jobyield-test.h`. Counters v922 `cow*` (docs/registry.md). Builds on
`[RouteAsync] KindDeadlines` / `FbJobCapMs` (docs/197, docs/178) and the scope answers of docs/249.

Tags: [verified] = disassembly or a run's log; [measured] = numbers from in-load A/B runs; [inferred] = reasoning, not probed.

## 0. Short answer

- **Problem:** on Isla Paradiso most long sim-thread freezes behind the route worker are waits for *another* Sim's job that the
  sim thread wants nothing from: the deferred-footprint drain (60% of the wait time >= 150 ms), the event-9 insert flush (34%),
  a destroy (5%). 83% of that time is behind seen NPCs' foreign Plan jobs, which run 1-2 s and fail anyway in 93-100% of the
  cases once they pass 300 ms [measured, 6 IP games with JobYield on, log analysis].
- **Fix:** with `CutOnWait=1`, a sim-thread wait at one of those sites asks the running job to end. The job's deadline moves to
  its start + 200 ms (route known unseen) or + 500 ms (seen NPC), never later than the deadline it had. The job then ends as
  an ordinary deadline cut, and also skips the rest of its candidate loop (`ClosestWaypoints_Find 0x65BB90` answers "none").
- **Never asked:** household, selected, unknown-scope jobs (unknown counts as household), stuck-check rounds, probes, jobs
  without a deadline.
- **Result (Isla Paradiso, 16 games, in-load A/B):** all-freeze time per 10 min 378 -> 303 s and 403 -> 322 s, freezes >= 1 s
  106 -> 20 and 117 -> 9.4 per 10 min. Costs: +37% route plans, +47-52% failed plans [measured, §4]. Bridgeport: no change.

## 1. Behaviour

Keys (`[RouteAsync]`, read once at load):

| Key | Default | Meaning |
|---|---|---|
| `CutOnWait` | 0 | start value of `cowMode` (+0x5C58, `--ab cutonwait`); 1 also installs the `0x65BB90` detour |
| `CutOnWaitUnseenMs` | 200 | earliest end of an asked job whose route is known unseen (scope 2), ms from its start (50-10000) |
| `CutOnWaitNpcMs` | 500 | the same for a seen NPC (scope 3, ts3fixes op 13; 100-10000); 0 = seen NPCs are never asked |
| `CutOnWaitLock` | 1 | a +0x918 wait in the lock pump while a townie's job runs asks too |

1. **Worker, job start** (`CowJobBegin`, after `KdJobBegin`, the job deadline and the worker slot): the job may be asked only
   when `cowMode` is 1, its scope is 2 (unseen) or 3 (seen NPC, not a ts3fixes unseen mark), it has a deadline
   (`t_fbDeadline`), it runs in the deadline worker slot (`g_pdWkTid`), and it is not a probe or a stuck-check round. The
   worker publishes the job's seq (`g_cowOk`) and its start; it marks every job as looked at (`g_cowDecided`).
2. **Sim thread, wait** (`CowAsk`): `WaitAsyncDone` at `disc` (DiscDrain), `rtwait` (event-9 insert flush and destroy waits),
   `runplan` (a plan behind a *foreign* job only: an own job may be the result it collects next) and, with `CutOnWaitLock`, the
   +0x918 pump. One ask per job (`g_cowReq` = its seq). Collect and drop waits never ask. Until the worker has looked at the
   running job, `WaitAsyncDone` polls every 5 ms instead of 2 s.
3. **Worker, every deadline check** (`PdCowApply`, in `BudgetStep`, `PdWorkerSkip` and `CowFindSkip`): for an asked job the
   deadline becomes min(deadline, start + minimum run). A job a few ms old is therefore not cut at once; it runs to its
   minimum, then takes the same exits as any deadline cut (world A* budget, cell A*, water). A job that `KdCutCamRecheck`
   holds in view (`t_kdCam` 2) runs on to its ceiling.
4. **Candidate loop exit** (`CowFindSkip`, entry detour on `ClosestWaypoints_Find 0x65BB90`, prologue
   `51 53 8B 5C 24 2C`): past the moved deadline of an asked job the call returns AL = 0 (`ret 0x2C`), what EA returns after 3
   failed tries (`0x65BBFF`..`0x65BC04`). All 8 callers test AL or read an output vector they emptied before the call
   [verified disassembly, Steam 1.67: `0x65C401`, `0x65D4CC`, `0x65DFE5`, `0x65E045`, `0x65FA0E`, `0x65FB55`, `0x65FC97`;
   `0x65F731`]. Jobs no wait asked and other threads run the game's function unchanged. The detour is installed only with
   `CutOnWait=1` in the ini; `--ab cutonwait` on an ini with 0 asks jobs but has no candidate exit.
5. **Job end** (`CowJobEnd`, before the +0x918 release): an asked job that ended capped counts in `cowCut` (and `cowNpcCut`
   for seen NPCs); the sim's wait then logs one `cut on wait:` line (route, scope, site, how long the job had run, how long the
   wait lasted after the ask, cut or not; 20 a minute).

Thread safety [inferred from the code]: only the worker writes its deadlines; the sim thread writes one LONG keyed by the job's
seq, so an ask that lands after the job ended reaches no later job (selftest: "stale ask").

Counters v922 (version field stays 912; the free words at +0x5C54, +0x5C54 stays free): `cowMode` +0x5C58, `cowAsks` +0x5C5C,
`cowCut` +0x5C60, `cowLateMaxUs` +0x5C64 (longest ask-to-wait-end), `cowNpcCut` +0x5C68.

## 2. Why v1 (2026-11-12) failed and v2 waits

The first build cut the asked job at once. In its on arm the long waits were all behind unseen foreign jobs capped at 1-90 ms
that then ran 1-4 s with 0 world A* nodes (route phases: candidate 1190 of 1344 ms) [measured, local build, IP]. A cut that
early fails every candidate's world A*, so the job walks its whole candidate list through `ClosestWaypoints_Find`, which has no
deadline exit (up to 3 widening tries of `0x6595B0`, the routability test `0x659390` on every gathered waypoint, the
sea-level ray and the bisection rays `0x658870`; docs/99 §4.5). Fewer waits (22 vs 110 log lines) but 1.2 s mean instead
of 0.34 s, so more >= 1 s freezes. v2 lets the job run to 200 / 500 ms and gives the asked job the candidate exit.

## 3. Household rule

Scope comes from `KdJobBegin` (`j->kdScope`, ts3fixes' answer; `j->uf` = known unseen): 0 unknown and 1 household or seen are
never asked (selftest 11b loops every site for both), only 2 and 3 are. Unknown counts as household (AGENTS.md). With
`CutOnWaitNpcMs=0` seen NPCs are never asked either.

## 4. Evidence [measured]

Setup: one machine, a copy of the game folder on the RTX Remix bridge (the project's baseline stack since 2026-11-13), Isla
Paradiso. In-load A/B: `profile-world.ts --ab cutonwait`, 2-min slices, both start orders. Release `ts3fixes.package` and the
release ini values in both arms, `[RouteAsync] JobYield` on in both arms, `CutOnWait=1` in the ini (so both arms have the
detour; the off arm asks nothing, so it never answers). Rates per 10 min of game time, off -> on.

| | rs1 (8 games, seeds 2024 / 12345) | rs2 (8 games, seeds 4242 / 9001) |
|---|---|---|
| all freeze time | 378 -> 303 s; 7 of 8 games better (paired p ~0.02) | 403 -> 322 s; 7 of 8 better, 1 tie (paired t 3.0) |
| background job waits | 157 -> 92 | 235 -> 152 |
| route lock waits | 76 -> 53 | 98 -> 73 |
| freezes >= 1 s | 106 -> 20 | 117 -> 9.4 |
| freezes >= 300 ms | 264 -> 297 | 280 -> 330 |

Freezes >= 300 ms go up because long waits become 200 / 500 ms ones.

Costs (both runs): route plans +37%, failed plans +47-52% (fail rate 11.0% -> 12.3%), `ReplanFromPosition` +38-47%. The cut
jobs fail, and their followers plan again.

Order effect: the gain is about 4x larger in games that start with the switch on than in games that start with it off.

Bridgeport: no change (2 games); few waits to cut there.

Not shown yet: a second machine, frame times, stuck-Sim counts beyond the runs' own checks. The switch stays off until a second
machine's A/B.

## 5. Not in this change

- `[RouteAsync] ClosestWpsCut` as its own switch (the same `0x65BB90` exit for every unseen job past its deadline): no gain in
  local runs; here the exit answers only jobs a wait asked.
- A repeat-failure backoff for seen NPCs: CutOnWait makes more seen-NPC jobs end cut, so `KdNpcBackoff`-style backoff is the
  natural next A/B [inferred].
- Deferring the event-9 insert or gating the other drain sites: larger simulated saving, but footprints go stale.
