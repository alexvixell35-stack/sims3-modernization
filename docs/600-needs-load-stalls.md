# 600. Needs-stress stalls: 1 s route plans and "other" hitches (2026-11-16)

> **Cause found (2026-11-16):** the slow runs had the screen off (DPMS). With the display off the game runs ~20 ticks/s, and the sim loop polls about once a second. Runs that switch the screen on (s64, s66) run ~285 ticks/s. The battery drop at 15:02 coincided because the screen turns off sooner on battery. The 1 s plan quanta are not a route-system wait.

Run `phase-20261116-1503` ("needs": every Bridgeport Sim at -40 on all needs, household on full autonomy, 6 x 8 min).
Engine asi drain-7ab14d12 (ts3native 0.36.264-mono1), same ini as s58 (`phase-20261116-0234`, 0 game hitches).
`hitch-attr.ts`: 46 game hitches, 44 s, 45 "other" at 880-1017 ms. Static read of the logs and source; nothing built or run.

## 1. What the log shows

| | needs (1503) | s58 (0234) |
|---|---|---|
| plans (ufplansplit planwait, whole run) | 3,995 | 55,763 |
| async wait per READY poll (wait ms / polls) | **330 ms** (2,285,802 / 6,919) | 39 ms |
| ufplansplit lines at 900-1099 ms | 1,845 of 2,145 | few |
| route jobs started | 3,426 (~1.1 a second) | 11,194 in the first ~10 min |
| route host calls queue max | 1,939 ms | 632 ms (minute 10) |
| door memo x mode 3 | **armed 0**, asks before arming 68,460, hits 0 | armed 1, hits 58,310 |
| sim waits for jobs (runplan / rtwait / disc, whole run) | 2.4 / 0.4 / 2.8 s, max 70 ms | |
| route lock pump | 3.4 s in 1,040 waits | |
| SimClock tick of every ufplan line, mod 5 | always 0 | spread 0-4 |

- [verified, log] Long jobs are host-call time: `plan 993 host 990`, `plan 1018 host 1016`, `route phases: foreign job 968 ms ...
  host 965`. The worker sits in `Proxy` waiting for the sim thread to run its managed call (event 59 door ask).
- [verified, log] The 1 s is a clock, not a job length. Job ends, plan completions and hitch ends fall on the same phase of a
  ~1 Hz beat that drifts a few ms per second (15,030-15,049 s: one completion burst a second at +160..+230 ms; hitch ends at
  15,025,152 and 15,057,193 ms on the same beat; `long job` end 10-30 ms before the `ufplan` lines it frees). The
  SimClock tick of those lines is 15 mod 20 almost every time (20 ticks = 1 real second here).
- [verified, log] Most of that beat is not a sim hitch: hitch_watch counted ~30 iterations a second and only 94 gaps >= 100 ms
  over the run. Script tasks still run; the tasks that sleep in ts3fixes `AsyncWait` (`Simulator.Sleep(0)` per poll) get a
  turn only every ~330 ms on average, and the first poll after START usually lasts the whole ~1 s (`wait 962 (1 polls)`).
- [verified, log] The sim thread is not stalled inside our route waits: sim job waits and the lock pump add up to ~9 s over the
  run, max 70 ms; no `route async: sim waited` / `lock ... busy` lines after load; StallWatch silent after load.

## 2. Which wait gives the quantum

No 1000 ms wait or deadline exists on this path in our code [verified, source]:

- Worker: `Proxy` (`native/ts3native/ts3native.cpp` ~3934) posts the call and waits with no timeout:
  `while (WaitForMultipleObjects(2, h, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) ServePlan();` (h = g_invDone, g_jobReady).
- Sim serve sites (the only places the call is run): READY poll `ServeInvoke(HC_READY)` (~5452, one call per poll of a task whose
  own job is queued or running), `LockAcquireAsync` pump (~5620, 1 ms waits, only while the sim wants a lock), `WaitAsyncDone` /
  `WaitJobDone` (~4710 / ~4733, 2000 ms is only the watchdog log period), RunPlan (~4525). `ReadyDrain` is off (no line).
- Other 1 s or near constants are elsewhere: `KdUncutCeilMs` 30 s (watchdog jobs only: 2 jobs of 1.7-1.9 s here),
  `SyncCapMs=1000` (sync plans, 7-15 a minute), `KdA10ForeignMs=1000`, `[AutShadow] TierClassMs=1000` (per Sim, not global),
  `stallwatch.cpp` `Sleep(1000)` (reads a stamp, suspends only after 5 s), `pkglock.cpp` `Sleep(1000)` (Mode 0).

So the job's length is "until a sim-thread serve site runs", and under this load that happens once per beat. The quantum is the
cadence at which the waiting script tasks get a turn [verified as the mechanism]. What sets that cadence to 20 ticks is
[inferred]: SimClock advances 5 ticks per update in this run (always 0 mod 5) against 1 in s58, i.e. EA's sim falls behind
real time and catches up in 5-tick steps; task wake-ups are keyed to those ticks (docs/119 §FakeSleep, docs/04a), and the
Sims' tasks that hold a route wait line up on a 20-tick beat. Not found in our code; EA's scheduler is the remaining candidate.

## 3. Why it becomes a hitch

The ~1 s "other" hitches end on the same beat, 8-16 ms after a job end and right after the plan completions it frees
(e.g. job end 15,025,136, three `ufplan` lines 15,025,143-149, hitch end 15,025,152). Our counters say the sim was not in a
route job wait or the lock pump during them, so the pass is long for another reason [inferred: the beat's pass runs the
backlog of Sim tasks that were waiting on route results plus EA's catch-up work at once]. Nothing in our waits explains the
44 s; what we add is that every routing Sim is parked for the beat instead of a frame, so Sims act in bursts.

## 4. Ours or EA's

- Ours: the route worker cannot finish a job without the sim thread, and the sim serves only when a waiting task polls. With
  task turns at ~330 ms, every proxied call costs a beat fraction; one job at a time means ~1 job a second for the whole
  world (3,426 jobs in ~50 min). Also ours: `DoorMemoX=3` acts only after ts3fixes `doorgen` arms it; `doorgen` is default off and
  was on only as a test switch in s58, so in this run every door ask went to the sim (68,460 unarmed asks, 0 hits).
- EA's: the load itself (every Sim's autonomy at -40) and the tick catch-up that sets the 1 s beat [inferred].
- Not involved: RouteJobPhases / HostSplit are observe-only stamps; UnseenFallback caps cut ~93 jobs per 10 min at
  `capAt ~991` (the cut lands on the beat because the job was in a host wait past its 150 ms deadline, docs/202 §3.1.1:
  no cut exit runs during a Proxy wait); KdCutCamRecheck held 1 job.

## 5. Fix (proposed, not built)

1. Serve pending host calls once per Simulate iteration, not only at task polls: a `SimLoopAdd` callback (simloop.cpp; the
   host-call split already hooks the same spot with `HcLimiterOut`) that runs `ServeInvoke(HC_LOOP)` while `g_invReady` is set
   and the sim thread is not inside a managed call (`g_inInvoke == 0`). `ServeInvoke` already clears the task id pair as EA's
   own guard does, and the lock pump serves from arbitrary native points, so a loop-end serve is no riskier [inferred; check
   on the selftest pair first]. Expected: queue per call <= one iteration (~33 ms), jobs back to ~200 ms, throughput no longer
   tied to task turns. New HostSplit site counts it.
2. Make `DoorMemoX=3` useful in release: arm it from ts3fixes when `doorgen` is on by default, or ship `DoorMemoX=1` until it is.
3. Optional: cut exits during a Proxy wait (docs/202 §3.1.1) so a capped unseen job ends at 150 ms instead of on the beat.

## 6. Smallest A/B

Same needs test (same save, same 6 x 8 min, ufplansplit on), one change per arm:

- **A/B 1, attribution (ini only):** `[RouteAsync] Enabled=0`. If the 880-1017 ms "other" hitches and the 20-tick beat stay,
  the stall is EA's load; if they go, it is our async path. Read: hitch-attr count, hitchwatch phase, `planwait` sync times.
- **A/B 2, door memo (switch only):** ts3fixes `doorgen=on` (as in s58). Expect `door memo x ... armed 1`, fewer host calls per
  job, shorter jobs; if the beat stays, it confirms the serve cadence, not the call count, is the bound.
- After fix 1 is built: needs test with the new loop serve off/on (one key), read `route host calls` queue max, per-poll wait
  (target ~40 ms like s58) and hitch-attr.
