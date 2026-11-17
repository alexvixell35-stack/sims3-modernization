# 637: route plans wait a sim tick per door ask (TaskServe)

2026-11-17, static plus log analysis, NOT RUN in game. Switch `[RouteAsync] TaskServe` (ts3native.ini, default 0),
`native/ts3native/taskserve.cpp`, selftest `TestTaskServe` (`taskserve-test.h`). No ts3fixes change, no counter slot
(native export `ts3native_taskserve`, docs/registry.md).

## 1. Question

Sessions phase-2026111[67]*: `ufplansplit: kind 0` lines show ~50k slow ordinary Sim plans (>= 150 ms), 264 ms on average.
Short routes (dist 6) take 300+ ms, and `route phases: foreign job` lines read `host 307-333` with every search phase at 0-6 ms.
Is that the planner's own search, or waiting?

## 2. How the numbers are measured

- `wait` / `polls` (ts3fixes `RouteBackoff.cs` AsyncWait): the task loops `GetOption2(kAsyncReady)` then `Simulator.Sleep(0)`.
  `polls` counts the sleeps, `wait` is their wall time. `Sleep(0)` resumes the task at the next sim tick (docs/04a).
- `host` (ts3native `Proxy`, `RJP_HOST` frame in routephases.cpp, `t_jobHostUs` in the routefail line): the time the worker
  sits in `Proxy`, from posting a managed call to the sim thread until that thread has run it (`g_invReady` -> `g_invDone`).
  `[RouteJobPhases] HostSplit` breaks it into queue (post -> pick-up, per serve site), serve and return (hostcalls.cpp).
- The sim thread picks a posted call up only at its serve sites: the job's own task at READY (one call, then the task
  sleeps a tick; ReadyDrain off), a routing-lock pump (`LockAcquireHook`), or a blocking job wait (`WaitAsyncDone` /
  `WaitJobDone`). There is no other point. [verified source]

## 3. Measured cause: tick-granular service of the planner's managed calls, not search compute

| Evidence | Value |
|---|---|
| routefail job lines, all sessions (43,020 jobs) | 3,361 s job time: plan (search) 113 s = 3%, **host 3,087 s = 92%**, other 143 s |
| same, jobs >= 150 ms (10,738) | 3,015 s: plan 61 s, **host 2,855 s (95%)** |
| phase-20261117-1604 HostSplit | calls 152,301 (~7.6 per job); queue 288 s of 394 s total job time; **serve 2.0 s** (13 us per call), return 1.1 s |
| which calls | door memo x asks 243k (hits 84k + misses 159k) vs host calls 152k: the calls are the portal-lock door asks (`LotManager.PortalLockedCallback`, docs/159) |
| queue per serve site (1604) | READY 15,285 calls / 190 s (12.4 ms each), lock pumps 85,599 / 77 s, asyncDone 51,417 / 20 s |
| slow kind 0 plans by poll count (all sessions) | 1 poll 5,546 (median 204 ms), 2: 3,490, 3: 5,415, 4: 9,626, 5: 12,210, **6+: 20,851 (5,548 s)**; median wall per poll 36-57 ms |
| ms per poll, every session | 36-41 ms (one sim tick) |

So the ~300 ms is wall time in which the worker waits for the sim thread to run each door ask. A job with k asks needs
about k READY polls, one per tick, unless a lock pump happens to take some. Search compute for a dist-6 route is a few ms
(`plan 0-8 ms`, `nodes` in the hundreds). Not a sleep or poll period of ours, not a deadline (cut columns 0), not a lock
(lock phase 0), not Wine/NTsync scheduling (return latency 7 us per call). The "~300 ms" cluster is partly the log
filter (`foreign job` lines only for jobs >= 300 ms) and ~7-8 asks x ~37 ms.

The 1-poll plans (median 204 ms) are one long tick (a frame the sim thread spent elsewhere); they are 10% of the slow total
and not touched here.

ReadyDrain (off, 2026-11-15/16 sessions 2157 / 2233) was the earlier attempt: it spins up to 2 ms on READY. It cut
polls per async plan from 1.63-1.69 to 1.37-1.39 and the async wait from ~62 to ~51 ms, but only 9% of drains ended the job
in the same poll (idle stops 53%, budget ends 37%): a job computes between asks, and a spin on the sim thread pays for it.

## 4. Questions 2 and 3

- A finished job cannot wake the waiting task inside the same tick: the task already ran this tick (TaskObjects round
  robin) and `Sleep(0)` / `Simulator.Wake` resume it next tick at the earliest. One tick per plan stays (the floor EA's
  own async-free path does not have, since it blocks the frame instead). What can go is the tick per door ask.
- No cache or shortcut is needed for dist-6 routes: their search is 3% of the job. (Door asks already have the exact
  DoorMemo / DoorMemoX memos; their misses are the asks counted above.)

## 5. Fix: TaskServe

`ScriptHost_RunTask` `0xD7F000` (thiscall(host, entry*, bool, bool), `ret 0Ch`) runs one script task until it yields. Its
four call sites `0xD7FA74`, `0xD7FB53` (ThreadedObjects), `0xD7FE01` (TaskObjects), `0xD800FC` are retargeted (byte-checked
`E8` rel32, all or none) to `TsRunTaskHook`: run the task as before, then, if the worker has posted a call (`g_invPosted`,
set by `Proxy`, cleared by every `ServeInvoke`) and this is the job owner thread, not inside a served call, with a running
async job, serve it with `ServeInvoke(HC_TASK)`. A sim tick runs hundreds of tasks, so a door ask now waits for the end of
the current task instead of the next tick.

- Exactness: the same call, on the same thread, in the same order, through the same `ServeInvoke` (EA's task-id guard
  `host+0xBD0` cleared and restored around it) as at READY or a lock pump; only the moment differs, and it is a moment the
  current design already serves at (the lock pump serves from inside arbitrary EA native code). The job's answers are the
  game's live answers. Household and selected Sims are not singled out because nothing is cut, cached or faked.
- Cost: one flag read per task run while nothing is posted; a served call is the call itself (~13 us). No spin, no wait.
- The waiting task still collects at its next poll, so a plan keeps a one-tick floor.

Expected: slow plans with 4-6+ polls drop toward 1-2 polls; the sim thread's blocking waits behind running jobs
(`pump 36 s`, asyncDone 51k serves in 1604) shrink with the jobs.

## 6. Test

- Native selftest `TestTaskServe` (forcing): off / nested / not owner / no job leave the posted call alone; on serves it
  after the fake task run on the owner thread with the task id cleared around it and restored; nothing posted = nothing
  counted; another site's serve clears the flag. Passed under clang-cl + Wine (in isolation; the full clang-built selftest
  stops in an older test at a direct memory fault, same on HEAD: the clang SEH limit).
- In game (to do): A/B `TaskServe=0/1`, same save, 20 min. Read the `planwait:` lines (async wait per plan, polls per
  plan), `ufplansplit: kind 0` count and sum, `route host calls:` queue per site, `route task serve:` (served, queue ms),
  `route async: ... pump` and the hitch counts. Pass: async wait per plan and the slow-plan sum down, hitches not up,
  `route task serve` skipped count small.

## 7. Files

`native/ts3native/taskserve.cpp`, `taskserve-test.h`, `ts3native.cpp` (`g_invPosted` / `g_invPostAt`, `HC_TASK`, export
block, minute line, include and install), `hostcalls.cpp` (`HcPick` has no row for `HC_TASK`), `ts3native.ini`
(`TaskServe=0`), `ts3addr-ea169.h` (5 unresolved rows: no EA 1.69 addresses, the patch logs SKIP there), docs/68 row.

## In-game A/B (2026-11-17, Bridgeport, same save, 10 min routine each, all proven fixes on)

| | TaskServe=0 (s78) | TaskServe=1 (s77) |
|---|---|---|
| plans in the last planwait minute | 1857 | 1551 |
| mean wall per plan | 55 ms | 36 ms |
| plans over 150 ms | 122 (6.6%) | 14 (0.9%) |
| plans needing 4+ polls | 143 | 2 |
| slow kind-0 plans (ufplansplit) in the session | 955 | 90 |
| game hitches | 0 | 0 |

`route task serve:` 62,465 task runs found a call posted, 62,392 served there, 0 refused. Verdict: works as designed; each route waits about one tick instead of one tick per door ask.
