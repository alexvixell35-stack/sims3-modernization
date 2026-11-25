# Queue ts3fixes-hotspots 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ts3fixes-hotspots-2026-11-16.jsonl`.

## 2026-11-16T19:24 UTC, build 76142a409, jobs bp-hoursmemo-correct, ip-hoursmemo-correct

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hoursmemo-correct | 19:24 (03:24) | 13.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: hoursmemo_hits 0->63459, hoursmemo_misses 0->82735, hoursmemo_barhits 0->30560, hoursmemo_checked 0->63459, owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->250, stuckcheckasync_run 0->250, etspread_passes 0->1, stuckcheck_noplan 0->229, stuckcheck_notstuck 0->20, stuckcheck_rearmed 0->9 [cut: full row in the jsonl] | 0 | 1750.8/1626.6/1808.4 -> 1970/1850.4/1568.1 | - | timing: invalid (noise medium); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning 3719648bbb6a ts3fixes.package.hoursmemo-svprobe-1116 (expected game-copy/ts3fixes.package.hoursmemo-svprobe-1116); noise: medium (other apps 23.1% of game CPUs); others on game CPUs 23.1%; clock avg/min 4139/4084 MHz; docs/276 §1.3 hoursmemo correctness, Baseline-BP-Spring [cut: full row in the jsonl]<br>sim ms/s 199.6; p99 80 ms, p99.9 135, max 362, >=300 1, >=1000 0 (5.5 min) |

Paused after bp-hoursmemo-correct (AI usage 5h - | 7d 98% (reset 14:00 PHT) | read 3 min ago); resume with `--from ip-hoursmemo-correct`.
