# Queue gc-idle-gate-time-1117-rerun3-1119-smoke 2026-11-19

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-idle-gate-time-1117-rerun3-1119-smoke-2026-11-19.jsonl`.

## 2026-11-19T21:34 UTC, build e7a8faba5, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 21:34 (05:34) | 13.5 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->222, stuckcheckasync_run 0->220, etspread_passes 0->1, stuckcheck_noplan 0->189, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->18, stuckcheck_reset 0->7<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1797.3/1684.2/1757.2 -> 2006.5/1766.7/1507.5 | - | timing: invalid (noise medium); build: asi d67f02888197 ts3native.asi.gchold2-1117 ts3native 0.36.272-gchold2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b); noise: medium (other CPUs 90.2% busy); others on game CPUs 0.5% (apps; incl. kernel 30.6%); clock avg/min 3938/3783 MHz; [est 15 min] measurement smoke: BP 10 min [cut: full row in the jsonl]<br>sim ms/s off 218.4 / on 206.8 (on - off -11.6, -5.3%); off: p99 111 ms, p99.9 206, max 448, >=300 3, >=1000 0 (2.5 min); on: p99 88 ms, p99.9 157, max 199, >=300 0, >=1000 0 (1.9 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- gc-idle-gate-time-1117-rerun3-1119-smoke-smoke: sim ms/s off 218.4 / on 206.8, on - off -11.6 (-5.3%); >=300 ms per 10 min off 11.8 / on 0.0, >=1000 off 0.0 / on 0.0 (1 off, 1 on run)
