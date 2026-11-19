# 666. Route stutter profile: what we can measure, what the no-plan hitches are, and the s140 profiling run

2026-11-19. Sources: the 30 phase runs of 2026-11-19 (`<runs>/phase-20261119-*/ts3native-full.log`, the docs/662 set plus
s137), ts3native / ts3fixes source at combo e1d6f52a, the CI build of run 44626443641 (strings only), and a Ghidra function export
of TS3W 1.67 (`tools/ghidra/ExportFunctions.java` -> `game-copy/analysis/functions.csv`, 38,952 functions, local only).
Branch `feat/route-profile`. Measurement only: no fix and no native change here.

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

- stall watch: log a sample when the 8 KB copy has no Simulate frame instead of dropping it, or copy more stack;
- `SwNote`: a lower line threshold (e.g. 100 ms) for sim waits for a job, and a per-hitch `+0x918` wait line (HitchAttr
  frame block in the log, not only to Frida);
- resolve the `caller` address of the sync plan line to a method name.
- ts3fixes, not native: a `kHitchCauseHooks` package for alarmtime / lotpass. At combo e1d6f52a `build-fixes.ts` stops on
  CS0305 (non-generic `Action`) in `OwElevIdle.cs:126` and `UnseenService88.cs:268`.
