# Queue v025-slow-diag 2026-11-04

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v025-slow-diag-2026-11-04.jsonl`.

## 2026-11-04T03:03 UTC, build da7167f7, jobs bp-diag-rel025d, bp-diag-v024

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-diag-rel025d | 03:03 (11:03) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->290, stuckcheckasync_run 0->289, etspread_passes 0->1, stuckcheck_noplan 0->231, stuckcheck_notstuck 0->28, stuckcheck_rearmed 0->30, stuckcheck_reset 0->6 | 0 | 2151.5/1834.4/1627.8 -> 2220.1/2071.4/1332.8 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes (expected game-copy/ts3fixes.package.rel025d); noise: medium (other apps 10.3% of game CPUs); others on game CPUs 10.3%; clock avg/min 4245/4184 MHz; Diagnosis (not timing): managed + native stack samplers on, else same as v025-speed-ab; d |
| bp-diag-v024 | 03:17 (11:17) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->294, stuckcheckasync_run 0->294, etspread_passes 0->1, stuckcheck_noplan 0->248, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->26, stuckcheck_reset 0->6 | 0 | 2146.2/1844.7/1564.9 -> 2224.5/2061.1/1323.3 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1/ts3fixes.0.2.2/ts3fixes.0.2.3 (expected game-copy/asi-v024/ts3fixes.package); noise: medium (other apps 10.7% of game CPUs); others on game CPUs 10.7%; clock avg/min 4234/4184 MHz; Diagnosis (not timing): managed + |

### Diagnosis (managed samples, 885 per run, share x 1000 = ms/s of wall)

| | v024 | rel025d |
|---|---|---|
| it/s (samplers on, not timing) | 42.3 | 39.4 |
| TaskObjects ms/s | 72.3 | 141.1 |
| Ts3Fixes ViewerPushLoop / PushViewers (inclusive) | 0 | 60 |
| of it PushHeld -> Queries.GetObjects<Sim> | 0 | 59 |
| Query_GetObjects self (all callers) | 14 | 62 |

Cause: ts3fixes UnseenViewers.cs ViewerPushLoop (a LoopTask, runs in TaskObjects). Before v0.2.5 ts3native refused BEGIN without [UnseenThrottle], so the loop ended at once. Since v0.2.5 [UnseenAnim] / [UnseenTable] accept codes 1-5 (unseen.cpp UtViewerMsg, g_aaOn / g_utbOn), so the loop runs every 5 ticks; its PushHeld scans every Sim with Queries.GetObjects<Sim>() and only then sends BEGIN 12, which ts3native refuses (codes > 5 need g_utOn = [UnseenThrottle], off in the release). The scan is pure waste: ~60 ms/s. Everything else differs by < 20 ms/s (DoRoute vs DoRouteOrig/RflRouteHook is the same route work under a renamed hook). Fix: 3943a9b6 (BEGIN 12 first); confirm A/B v025-slow-ab.

### Second slowdown: GC / machine check from existing runs (2026-11-04, no game runs)

```
run it/s | gcHeapMB b->a | gcCount/min | gcMark ms/s marks/s aborted/s | mhz avg/min | pin | other og% | ts3w%
bp-v024-a1 | 46.5 | 356->360 | 4.9 | 6.0 | 3.47 | 3.38 | 4244/4194 | 22/22 | 2.3 9.7 | 11.8
bp-rel025d-b1 | 39.0 | 354->360 | 3.6 | 4.8 | 2.70 | 2.63 | 4237/4203 | 22/22 | 2.5 10.0 | 11.7
bp-rel025d-b2 | 40.7 | 356->360 | 4.1 | 5.2 | 2.99 | 2.92 | 4232/4197 | 22/22 | 2.3 9.3 | 11.6
bp-v024-a2 | 45.5 | 356->360 | 4.5 | 5.7 | 3.31 | 3.24 | 4220/4105 | 22/22 | 2.4 9.5 | 11.8
bp-v024c-a1 | 43.1 | 356->360 | 4.3 | 5.4 | 3.12 | 3.05 | 4229/4156 | 22/22 | 2.3 9.1 | 11.7
bp-rmoff-b1 | 39.7 | 354->360 | 3.9 | 5.0 | 2.88 | 2.82 | 4236/4160 | 22/22 | 2.4 9.3 | 11.5
bp-rmoff-b2 | 37.9 | 354->360 | 3.5 | 4.7 | 2.51 | 2.45 | 4237/4181 | 22/22 | 2.7 10.8 | 11.6
bp-v024c-a2 | 43.8 | 356->360 | 4.3 | 5.5 | 3.15 | 3.07 | 4236/4200 | 22/22 | 2.3 10.0 | 11.7
bp-diag-rel025d | 39.4 | 356->360 | 3.6 | 4.9 | 2.65 | 2.59 | 4238/4187 | 22/22 | 2.4 10.2 | 11.4
bp-diag-v024 | 42.3 | 356->360 | 4.0 | 5.2 | 2.88 | 2.81 | 4241/4200 | 22/22 | 2.7 10.4 | 11.7
bp-v024d-a1 | 54.0 | 354->360 | 7.4 | 7.6 | 5.98 | 5.84 | 4206/4161 | 22/22 | 4.9 14.1 | 11.6
bp-ph-b1 | 39.2 | 354->360 | 3.2 | 4.6 | 2.40 | 2.34 | 4169/4141 | 22/22 | 7.0 14.7 | 11.7
```

GC is not it: rel025d runs fewer collections and less mark time (both scale with iterations); heap 354-356 -> 360 MB in every run. Clock (4.17-4.24 GHz avg), pinning (22/22) and other load (2.3-2.7% other, 9-11% on game CPUs) match between arms before 12:00 PHT. TS3W CPU is 11.4-11.8% in every run while rel025d does ~15% fewer iterations: the same CPU buys less game work, and no new busy helper thread shows up as extra process CPU (machine.jsonl has no per-thread data, so a helper on the sim thread's hyperthread sibling is not ruled out directly). bp-v024d-a1 (54 it/s) ran with more GC (7.4 collections/min) and 14% other on game CPUs, so it is an outlier, not a clean baseline. Next A/B: --ab mintcall in one load (v025-mintcall-ab.json), then the rel025d asi + rel024b ts3fixes swap if CallPath is clean.
