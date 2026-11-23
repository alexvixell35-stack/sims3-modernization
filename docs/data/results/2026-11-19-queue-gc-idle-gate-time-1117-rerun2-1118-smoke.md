# Queue gc-idle-gate-time-1117-rerun2-1118-smoke 2026-11-19

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-idle-gate-time-1117-rerun2-1118-smoke-2026-11-19.jsonl`.

## 2026-11-19T00:19 UTC, build be38e484d, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 00:19 (08:19) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->272, stuckcheckasync_run 0->272, etspread_passes 0->1, stuckcheck_noplan 0->233, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->19, stuckcheck_reset 0->3<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1750.1/1629.3/1811.5 -> 1951.6/1870.3/1635.9 | - | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi d67f02888197 ts3native.asi.gchold2-1117 ts3native 0.36.272-gchold2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b); noise: medium (other CPUs 56.1% busy); others on game CPUs 0.2% (apps; incl. kernel 17.5%); clock avg/min 4110/3997 MHz [cut: full row in the jsonl]<br>sim ms/s off 237.8 / on 259.4 (on - off +21.6, +9.1%); off: p99 86 ms, p99.9 137, max 6073, >=300 3, >=1000 2 (1.1 min); on: p99 102 ms, p99.9 282, max 7183, >=300 6, >=1000 5 (2.3 min) |

## 2026-11-19T00:50 UTC, build be38e484d, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 00:50 (08:50) | 12.3 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->281, stuckcheckasync_run 0->278, etspread_passes 0->1, stuckcheck_noplan 0->225, stuckcheck_notstuck 0->31, stuckcheck_rearmed 0->28, stuckcheck_reset 0->6<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1752.9/1629/1809.2 -> 1973.3/1879.7/1586 | - | timing: invalid (job VOID: runInvalid: profiler cut short (475 of 600 s); noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi d67f02888197 ts3native.asi.gchold2-1117 ts3native 0.36.272-gchold2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b); noise: medium (other CPUs 54.4% busy) [cut: full row in the jsonl]<br>sim ms/s off 255.3 / on 263.2 (on - off +7.9, +3.1%); off: p99 88 ms, p99.9 159, max 8182, >=300 3, >=1000 3 (1.8 min); on: p99 83 ms, p99.9 185, max 8225, >=300 8, >=1000 8 (3.4 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- gc-idle-gate-time-1117-rerun2-1118-smoke-smoke-r2: sim ms/s off 255.3 / on 263.2, on - off +7.9 (+3.1%); >=300 ms per 10 min off 17.1 / on 23.9, >=1000 off 17.1 / on 23.9 (1 off, 1 on run)

## Bridge note (added 11-19 12:45 PHT)
chain-night-1118 steps 1-3 (this queue among them) ran on Apex 4.0.6 asi a1c121f3 + bridge pair 1ace01d9/faf22f07 (v4.0.6 + our 0015/0016). From step 4 (z1-m1-timing-1116-master-1117b, 12:43 PHT) the chain runs on the fork test install ours/test-integration-1119 a69a1bbc: asi a89fc5b4, pair da20b397/f2a58455 (probe-out/apex-latest-switch-1119.md). Do not compare timing across that point without a re-baseline.
