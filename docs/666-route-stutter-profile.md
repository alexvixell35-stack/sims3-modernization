# 666. Route stutter profile: what we can measure, what the no-plan hitches are, and the s140 profiling run

2026-11-19. Sources: the 30 phase runs of 2026-11-19 (`<runs>/phase-20261119-*/ts3native-full.log`, the docs/662 set plus
s137), ts3native / ts3fixes source at combo e1d6f52a, the CI build of run 44626443641 (strings only), and a Ghidra function export
of TS3W 1.67 (`tools/ghidra/ExportFunctions.java` -> `game-copy/analysis/functions.csv`, 38,952 functions, local only).
Branch `feat/route-profile`. Measurement only: no fix and no native change here. **§5 (branch `feat/prof-gaps`) closes the
native gaps of §4: deep stall watch stacks, the job-wait threshold, +0x918 wait lines, the managed caller on the sync plan
line and a per-hitch `hitch:` line with a cause class.**

**Upstream carve-out (2026-11-20):** §5 and the report tools come over. `[RouteJobPhases] CandidateSplit` (docs/644,
plancand.cpp) and `[RouteAsync] NoPathMemo` named in §1-§3 are combo-only, not in this tree; route-stutter-report.ts reads
their lines when a log has them and skips them otherwise. §5 ran in game from s142 on (every docs/668 storm arm used its keys).

## 1. Profiling facilities that already exist

| facility | what it records | how to turn it on |
|---|---|---|
| ts3fixes `hitchwatch` | one line per tick gap >= `min_ms`: ms, tick, sim time, GCs during it, the agent command running (`-` = none), `at` = GetTickCount | agent `hitch_watch {"start":true,"min_ms":100}` (`HWATCH=1` in phasesoakr.sh); the harness reads `top` into `hw-N.json` |
| `[StallWatch]` (stallwatch.cpp) | sim-thread eip + up to 24 return addresses (module+rva) when no Simulate iteration ended for StallMs | `Enabled=1`, `StallMs=150 EveryMs=50 Lines=N` for hitches (default 5 s) |
| `[HitchAttr] Holders` | sim / main waits >= `HoldersMinMs` (20) on any game lock **except +0x918**, with holder thread and both call sites (`lock holders:` lines) | `Holders=1` (needs the Lock_Acquire hook of `[RouteAsync]` or `[RouteGate]`) |
| `[HitchAttr] Enabled` | per-frame sim waits for the route job by class (+0x918, other lock, job wait, plan wait) in export `ts3native_hitchattr` | `Enabled=1`; read only by `tools/frida/stutter-profile.js` (Windows, Frida); `tools/harness/hitch-attr-report.ts` |
| sim waits for a job (`SwNote`) | `route jobs: sim waited N ms for a job at runplan/rtwait/disc...`, per site counters every 60 s | always on; a line only for waits **>= 300 ms** (20 a minute) |
| `[RouteAsync] LogFallbackStack` | after each `route async: sync plan` line, `script stack of route H (innermost first)`: up to N interpreter frames by name, the managed caller | `LogFallbackStack=1..16`; `tools/harness/nostart-report.ts` ranks it |
| `[RouteJobPhases] CandidateSplit` | per PlanHierarchical call >= `CandidateSplitLogMs`: self ms / calls of closestWps, sameLand, sameWater, choose, gather, shore, lotSearch, cell A*, lotPlan, lot legs, rest; CI build adds world, water, post, insert (docs/663 §2); 60 s totals and top 8 | `CandidateSplit=1` (any thread, needs nothing else) |
| `[RouteJobPhases] SyncPlans` | `route phases: sync plan N ms ...; ms plan= cell= post= insert= world= lotleg= portal= cand= lotgraph= pending= water= alloc= lock= other=` per sim-thread plan >= `[RouteAsync] LogFallbackMs` | `SyncPlans=1`, `SyncLinesPerMin` |
| `[RouteJobPhases] Enabled / HostSplit / GoalSplit / GoalStepSplit` | the same phase timers inside worker jobs; host-call queue split; goal step sites | each `=1`; `tools/harness/route-phases-report.ts` |
| ts3fixes `ufplan` / `ufplansplit` | per slow unseen-fallback plan: kind, ms, why (scope), src, and `sync N ms (START refused)` or `pre / wait (polls, longest)` | switch `ufplansplit` (log only) |
| ts3fixes `callcensus` | calls per sim-hour of the docs/589-590 candidates (`callcensus:` lines) | switch `callcensus` |
| ts3fixes `routecauselog` | `routefail` / `gohere` lines (failed routes and Go Here commands, interaction and target) | switch `routecauselog` / tuning |
| ts3fixes `alarmtime` / `lotpass` / `boatreplanlog` / `owresettime` / `synctime` (HitchCause.cs) | which alarm or lot update made a long `Lot.Simulate` pass, etc. | runtime switches, but the hooks exist only in a package built with `--on kHitchCauseHooks` |
| ts3fixes `routesrc` | route plans by source bucket (docs/151 §2) | tuning `kRouteSources` |
| s88 managed sampler | C# stacks of the sim thread (docs/617: 900 stacks over 180 s, `probe-out/profile/s88-*-managed.*`) | Frida `stutter-profile.js` (Windows only) |
| `[MonoJit] Time` | per-method compiled vs interpreted time (`jittime:` rows) | `Time=1` with `Mode=2` (docs/handoff/jit-paired-timing.md) |
| `[AnimPhase]` | four timers in rig pose 0x940430 | `Enabled=1` |
| `[LoadProf]` | load-time profile | not used (save/load is Rodney's) |

The "JIT name resolver": there is no separate one. `LogFallbackStack` reads the interpreter's frame records (docs/130), which
name methods whether or not `[MonoJit]` compiled them (docs/537 frame census: 0 unknown). The `caller XXXXXXXX` field of the
sync plan line is a code address and is not resolved.

## 2. The no-plan hitches of 2026-11-19 [measured, logs only]

`tools/harness/route-stutter-report.ts` over the 30 logs: 409 game hitches >= 200 ms (198.5 s; 427 harness hitches skipped).
A hitch's cause is the largest logged wait covering >= 50% of it. 147 hitches (86.5 s) are sync plans (docs/662 counted 163
with its own window rule). The other 262 (112 s):

| category | hitches | s | basis |
|---|---|---|---|
| no logged wait, nothing enriched in the window | 127 | 57.5 | unknown |
| a worker route job >= 250 ms ended in the window, no logged sim wait | 80 | 30.0 | inferred (enrichment, see below) |
| sim waited >= 300 ms for a worker job (`disc` 24, `rtwait` 8, `runplan` 2, own `disc` 3) | 37 | 15.4 | measured |
| stall-watch sampled, no logged wait (`Lock_Acquire` 2 / 2.0 s, GC mark, `SimRender_TripleBufferSync`, thumbnails) | 11 | 5.0 | sampled |
| GC (`gc +1` in the hitchwatch line) | 6 | 2.6 | measured |
| route lock wait (`route async: sim waited N ms on lock`) | 1 | 1.3 | measured |

Context check against a baseline (same window rule at +20 s, where there is no hitch; scratch script, 409 vs 402 windows):
a `route async: long job` line is in 38% of hitch windows and 2.7% of baseline windows (14x). Everything else is
co-location: P5c releases 63% vs 71%, lock-holder waits 17% vs 33%, 60 s stats blocks 11% vs 13%, callcensus dumps 10% vs 16%,
Go Here / routefail lines 42-75% vs 50-75%, async-plan lines 91% vs 86%. So the one lead in the logs is the worker route job:
the sim thread likely waits for it below the 300 ms log threshold or on the +0x918 routing lock, which Holders skips.
Harness agent commands, saves and GC explain almost none of the agent `-` hitches. No category is "script tick work",
"object creation", "LOD / streaming" or "audio" by the logs, because nothing in them measures those.

**What the logs cannot tell apart:**
- a sim wait on +0x918 or a job wait < 300 ms vs sim CPU work in the same frame (only the Frida-read `ts3native_hitchattr`
  frame block has it);
- script tick work (alarms, lot passes, autonomy) vs native frame work vs the sim waiting for the main thread
  (`SimRender_TripleBufferSync`, frame limiter): hitchwatch measures the tick gap only;
- **why stall watch is blind:** 7 runs had `StallMs=150`, yet 0 of 105 sync-plan hitches and 0 of 145 other wait or unknown
  hitches got a sample [measured]. `SsCapture` copies at most 2048 dwords (8 KB) of the stack and `SsCheck` drops a sample
  silently when no return address inside `MonoScriptHost_Simulate` is in that copy (`if (got && !inSim) return;`) [verified,
  stallwatch.cpp]. A plan or script stall has a deep interpreter + planner stack, so Simulate is past 8 KB [inferred]. Only
  shallow waits get logged;
- the managed caller of sync plans: `LogFallbackStack=0` in all 30 runs; ufplansplit gives only kind / why / src;
- the phase split of most plans: CandidateSplit was on only in the later runs (lines >= 100 ms, 20 a minute), and the old
  build has world A*, water, post and insert inside "rest".

## 3. s140: one profiling run

`<runs>/s140-run.sh` (not in git: personal paths). It waits for s139, backs up the Bin pair (`bak-s140/`, sha256 list,
`restore-pending` marker; trap restores and verifies on exit; `./s140-run.sh restore` after a kill), installs the CI asi
`native/ci-44626443641/ts3native.asi` on the installed ini and sets only measurement keys:
`[RouteJobPhases] CandidateSplit=1 CandidateSplitLogMs=100 SyncPlans=1 SyncLinesPerMin=60`, `[RouteAsync] LogFallbackStack=16`,
`[StallWatch] Enabled=1 StallMs=150 EveryMs=50 Lines=8`, `[HitchAttr] Holders=1`. ts3fixes: newest test package r139-algo with
the proven set + `ufplansplit`; `routecauselog`, `callcensus` and `synctime` stay off. Two sessions (Isla Paradiso, then
Bridgeport), each `phasesoakr.sh 15 "idle:15 idle:15"` with `NOSNAP=1 HWATCH=1`: the per-phase mem_gc / leak_census /
alarm_audit / aut_census snaps become a mark + hitch_watch read, and one full snap runs after "measurement end".

**The sampler:** stall watch is blind for deep stacks (§2), so s140 samples from Linux: `perf record -k boottime -e cpu-clock:u
-F 499 -g -t <Simulator tids>,<main tid>` from the first idle phase to "measurement end", after a 3 s smoke that must see
Simulator samples. EA's sim thread is named `Simulator` under Wine (`/proc/<pid>/task/*/comm`); Wine's GetTickCount follows
CLOCK_BOOTTIME on this machine [measured: log start 10:39:04 = boottime 209,607 s, first hitch 79 s later], so perf time
matches the logs' `at` ms. `cpu-clock` samples only a running thread: few sim samples in a hitch = the sim thread waited.

**The report** (`tools/harness/route-stutter-report.ts`, `--self-check`): per hitch (`--hitches`) the cause, the managed caller
(LogFallbackStack stack through `nostart-report.ts caller()`, else ufplan kind / why / src, else job / lock key), the
phase breakdown (CandidateSplit per route, else SyncPlans), the stall-watch leaves and the perf share on CPU with the top sim
function; then causes, callers, callers x phases by stall ms, phase totals, perf self / inclusive functions of the sim thread,
main-thread functions in hitches where the sim was off CPU, and context flags. TS3W frames are named from `functions.csv` +
`tools/ghidra/symbols/*.csv`. A run dir's `sim.perf.data` is read automatically.

With the installed asi instead of the CI one (`S140_ASI`, or the artifact missing), the run still works; lost are the world /
water / post / insert sites (they stay inside "rest"; `LOST:` in `s140-check.txt`).

## 4. Needs a native change (CI build), not done here

- stall watch: log a sample when the 8 KB copy has no Simulate frame instead of dropping it, or copy more stack
  (**done, §5:** `StackKB`, `KeepNoSim`, `MainThread`, `Names`, `Managed`);
- `SwNote`: a lower line threshold (e.g. 100 ms) for sim waits for a job, and a per-hitch `+0x918` wait line (HitchAttr
  frame block in the log, not only to Frida) (**done, §5:** `JobWaitLogMs`, `RouteLockMs`, `HitchMs`);
- resolve the `caller` address of the sync plan line to a method name (**done, §5:** `LogFallbackCaller`).
- ts3fixes, not native: a `kHitchCauseHooks` package for alarmtime / lotpass. At combo e1d6f52a `build-fixes.ts` stops on
  CS0305 (non-generic `Action`) in `OwElevIdle.cs:126` and `UnseenService88.cs:268`.

## 5. The native gaps closed (branch `feat/prof-gaps`, built; run in game from s142, docs/668)

2026-11-19, on combo 87921903. Measurement only: every key defaults to the old behaviour, no counters layout change (new
export `ts3native_hitchsum`), no hook on a route result. With the keys off each of the six call sites tests one global after
a wait or a plan that already took its own timers. Files: `stallwatch.cpp`, `hitchsum.cpp` (new), `stallnames.h` (generated),
small call-site lines in `ts3native.cpp`, `syncphases.cpp`, `hitchattr.cpp`, `memexp.cpp`.

| gap (§2, §4) | key (default) | what it logs |
|---|---|---|
| stall watch copies 8 KB | `[StallWatch] StackKB` (8, max 1024) | that many KB of stack per sample |
| a sample with no Simulate frame is dropped | `[StallWatch] KeepNoSim` (0) | the sample, tagged `, no Simulate frame` (loads and travel then log too) |
| only the sim thread is sampled | `[StallWatch] MainThread` (0) | `stall watch: main thread T while the sim thread stalls N ms: ...` after every logged sim sample (a second suspension, not the same instant) |
| addresses only | `[StallWatch] Names` (0) | `TS3W+0x25E0F0[PathPlanner_PlanHierarchical+0x20]`: 2,337 function rows of `stallnames.h` (`tools/native-names.ts`: our names from `tools/ghidra/symbols/ts3w-1.67.symbols.csv`, function ends from the local Ghidra export; image offsets, no exe bytes). The EA 1.69 build maps a frame through the nearest `ts3addr-ea169.h` pair inside the same function and marks the name `~` [inferred per frame]. `[MonoJit]` code: `jit+0x1234[Ns.Cls.Meth+0x1A]` |
| no script frames in a sample | `[StallWatch] Managed` (0, max 16) | `stall watch: sim thread T script stack (innermost first): A < B`: the interpreter frame records of the suspended thread, read through its TEB with `ReadProcessMemory` (no fault handler while it is suspended) |
| 24 frames | `[StallWatch] Frames` (24, 8-96) | more return addresses |
| job waits logged from 300 ms | `[HitchAttr] JobWaitLogMs` (300), `JobWaitLinesPerMin` (20) | the same `route jobs: sim waited N ms for a job at SITE` line from N ms; the per-frame sum is in the `hitch:` line |
| +0x918 waits not logged | `[HitchAttr] RouteLockMs` (0 = off) | `route lock wait:` (below), sim and main thread, pump and plain acquires |
| `caller` of a sync plan is an address | `[RouteAsync] LogFallbackCaller` (0, max 8) | `, managed caller A < B [compiled M +0x1A]` at the end of the `route async: sync plan` line (and of `route phases: sync plan` lines that came through no IC) |
| no per-hitch split | `[HitchAttr] HitchMs` (0 = off), `HitchLinesPerMin` (60) | `hitch:` per sim frame >= N ms, `hitch totals:` every 60 s |

`MainThread`, `Names`, `Managed` or `Frames != 24` switch stall watch to the deep line: unique return addresses in stack order,
`*n` = times on the stack (interpreter recursion collapses), `^` = the frame-pointer chain holds it, the innermost 3/4 and the
outermost 1/4 past `Frames` with `...(+skipped)` between, the rest on `more frames` lines:

```
stall watch: sim thread 9: no Simulate iteration finished for 180 ms (1/8), no Simulate frame: deep 256 KB, 812 hits, 97 unique, no Simulate hit: eip TS3W+0xE16F5[Lock_Acquire+0x5], stack scan (48): TS3W+0x25E0F0[PathPlanner_PlanHierarchical+0x20]^*2 ...
stall watch: sim thread 9 more frames (from #31): ...
stall watch: sim thread 9 script stack (innermost first): ScriptCore.Route.Route_Plan < ...
stall watch: main thread 7 while the sim thread stalls 180 ms: deep 256 KB, 40 hits, 31 unique, no Simulate hit: eip ntdll+0xD410, stack scan (31): ...
route lock wait: sim waited 312 ms on +0x918 (key 0085FE10, job pump), held by route worker 4321; job route 1A2B3C4D seq 77 foreign IC kind 0 ended in the wait after 410 ms (>= 250 ms: yes), at 9000 ms
hitch: frame 1000 ms (#3), cause sync-plan/world: sync 700 (n=1 world=600 water=0 post=0 insert=0 cell=0 lot=40 lock=10 other=50), jobwait 100 (n=2 max=80 at disc route 00001A2B insync=0), lock918 0 (n=0 max=0 key 00000000 holder - 0 job250=0 insync=0), otherlock 0 (n=0 insync=0), gc 0 (+1), limiter 16, cpu 800, unknown 184, at 5000 ms
hitch totals: frames 3, hitches 2 (2000 ms) by cause lock918=1/1000 sim-cpu=1/1000; +0x918 waits >= 1 ms n=5 ms=426 max=312 (main thread n=1 ms=25): behind the route worker n=4 ms=386, its job ended in the wait n=4 ms=386, that job >= 250 ms n=1 ms=312, other holder n=1 ms=40; hitches >= 50% on +0x918 behind a job >= 250 ms: 1 of 2; stall watch lines without a Simulate frame 3, main samples 4, at 6000 ms
```

**The `hitch:` line.** A frame = one Simulate iteration (loop-back to loop-back, `simloop.cpp`), all ms:
- `sync`: sim-thread sync plans; the phases need `[RouteJobPhases] SyncPlans=1` (lot = lot leg + portal A* + lot graph; other =
  plan, candidate, pending, allocator, rest). Without it only the IC plans `[RouteAsync]` times, all as `other`;
- `jobwait`: every sim wait for a route job (`SwNote`: runplan, rtwait, disc, collect, drop), the longest with site and route;
- `lock918`: sim waits on the routing lock (job pump and plain acquires), the longest with key and holder; `job250` = the ms
  spent behind a worker job that ended inside the wait after running >= 250 ms;
- `otherlock`: other game locks; complete with `[HitchAttr] Holders=1`, else only while a route job runs;
- `gc`: the per-frame `GC_try_to_collect` step (call-site timer at `0xD819AA`, or memexp's when `[GC] SqrtHeapLimit` owns the
  site, then forced collections too), `+N` = collections finished (`GC_gc_no`). A forced collection inside an allocation
  is not timed without SqrtHeapLimit: it shows as `sim-cpu` with `+N > 0`;
- `limiter`: sim pump return to loop-back (the frame limiter's sleep); `cpu`: the sim thread's kernel + user time
  (`GetThreadTimes`, ~16 ms steps); `-1` = not measured;
- `insync` = the part of a wait inside a sync plan (taken out once); `unknown` = the frame minus all of it.

Cause class: the largest of sync work (sync minus its lock phase and job waits), jobwait, lock918, otherlock, gc, limiter
when it is >= 50% of the frame (`sync-plan/<largest phase>`, `job-wait`, `lock918`, `other-lock`, `gc`, `limiter`); else by
the CPU time no measured part explains: `sim-cpu` (>= 50% of the frame was sim-thread CPU work nothing logs: script or
native work), `off-cpu` (>= 50% was a wait nothing logs: main thread, render sync, an unhooked lock), `mixed`, or `unknown`
when the CPU time could not be read. So the 127 "nothing in the window" hitches get `sim-cpu` or `off-cpu` at least, and
the stall watch lines of the same window name the frames (script stack for `sim-cpu`, main thread for `off-cpu`).
Managed script time itself has no cheap hook (`[MonoJit] Time` is per method): `sim-cpu` is its upper bound.

**The +0x918 hypothesis (80 hitches, 30 s, §2) is decided by a run with these keys:** `hitch totals:` prints
`hitches >= 50% on +0x918 behind a job >= 250 ms: K of H` (export `ts3native_hitchsum.hitchLock250`), the +0x918 wait ms by
holder, and each such hitch has `cause lock918 ... job250=N` plus its `route lock wait:` line. The route worker writes its
job-end record before it gives the lock back, so the waiter it wakes always finds it. K near 80 of ~260 = proven; K near 0
with `job-wait`, `sim-cpu` or `off-cpu` instead = refuted, and the class says where to look.

**Hitch-run keys** (all log only): `[HitchAttr] HitchMs=100 RouteLockMs=20 JobWaitLogMs=20 JobWaitLinesPerMin=120 Holders=1`,
`[StallWatch] Enabled=1 StallMs=150 EveryMs=50 Lines=8 StackKB=256 KeepNoSim=1 MainThread=1 Names=1 Managed=8 Frames=48`,
`[RouteAsync] LogFallbackCaller=4`, `[RouteJobPhases] SyncPlans=1 SyncLinesPerMin=60 CandidateSplit=1`.

**Cost when on:** two QPC reads and one guarded owner read per sim / main +0x918 acquire outside the job pump (the acquire
stays the same single call; the pump path was timed already); per sim frame one QPC, `GetThreadTimes`, `GetTickCount`; two
QPC around the per-frame GC step; one more call layer on the sim pump slot (`SimPumpAdd`, as HostSplit). A deep stall
sample suspends the thread for the stack copy (256 KB memcpy) and, with `Managed`, ~40 `ReadProcessMemory` calls; the scan
(up to 65,536 words, region cache) runs after the resume on the watchdog thread.

**Limits:** stall watch still samples only after `StallMs` (>= 150) without a finished iteration; waits nested in another
wait's pump count twice (as `[HitchAttr]`); past 256 unique return addresses the outermost are only counted; the name of a
frame in an unnamed function is module+rva (the report names it from the local Ghidra export); the +0x918 holder is read
once at the wait's start (0 = free then: the worker's lent window, or a race).

**Tests:** selftest `hitchsum-test.h` (`TestStallWatchDeep`, `TestHitchSum`): name table order and lookup, scan (unique,
counts, stack pointers skipped, frame-pointer marks, Simulate hit), text (names, gap, `more frames`), script frames through
a fake TEB, KeepNoSim, a real deep capture of a sleeping thread; frame sums off / on / sim thread only, sync phase map, all
ten cause classes, the lock-in-sync case, +0x918 waits by holder and job, both lines byte for byte, totals, JobWaitLogMs,
LogFallbackCaller. Those two tests plus `TestStallWatch` and `TestHitchAttr` pass in a clang-cl build under Wine, run alone
(the full clang selftest stops at the clang SEH limit, as before); clang-cl `/Zs /W4`: `ts3native.cpp` clean for Steam and
`/DTS3A_TABLE`, `selftest.cpp` clean for Steam (its 6 `/DTS3A_TABLE` errors are the old ones in `monojit-test.h` /
`g_testExpAt`; CI does not build the selftest under the table). MSVC CI builds the real asi and runs the whole selftest.
`route-stutter-report.ts --self-check` parses every new line (and reads a log with `hitch:` lines but no hitchwatch lines
with the native frames as its hitches); its output on the 2026-11-19 logs is unchanged.

## 6. Short storm arms (docs/668)

The profiling runs from s141 on use `route_storm` phases (`storm:4` / `storm:5`) instead of 15 min idle phases: about 4 min
per arm. `tools/harness/storm-table.py --report <this report>` splits the hitches, slow plans and plan rate by phase.
