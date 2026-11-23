# Queue unseen-rate-ab-1118-smoke 2026-11-19

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-rate-ab-1118-smoke-2026-11-19.jsonl`.

## 2026-11-19T06:33 UTC, build e7a8faba5, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-19T06:41 UTC, build e7a8faba5, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 06:41 (14:41) | 13.3 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->220, stuckcheckasync_run 0->220, etspread_passes 0->1, stuckcheck_noplan 0->174, stuckcheck_notstuck 0->19, stuckcheck_rearmed 0->22, stuckcheck_reset 0->5<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1763.2/1682.4/1366.1 -> 2071.7/1770.7/1273 | - | timing: invalid (noise medium); build: asi 57214b153844 ts3native.asi.unseen-measure-1118 ts3native 0.36.277-unseenmeas1 (log ok); ts3fixes tuning f961d3c2c642 ts3fixes.package.master-1117c (expected game-copy/ts3fixes.package.master-1117c); noise: medium (other CPUs 90.3% busy); others on game CPUs 0.2% (apps; incl. kernel 22.1%); clock avg/min 3566/3320 MHz [cut: full row in the jsonl]<br>sim ms/s off 241.2 / on 229.3 (on - off -12.0, -5.0%); off: p99 114 ms, p99.9 193, max 291, >=300 0, >=1000 0 (3.5 min); on: p99 117 ms, p99.9 180, max 359, >=300 1, >=1000 0 (3.3 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- unseen-rate-ab-1118-smoke-smoke-r2: sim ms/s off 241.2 / on 229.3, on - off -12.0 (-5.0%); >=300 ms per 10 min off 0.0 / on 3.0, >=1000 off 0.0 / on 0.0 (1 off, 1 on run)
