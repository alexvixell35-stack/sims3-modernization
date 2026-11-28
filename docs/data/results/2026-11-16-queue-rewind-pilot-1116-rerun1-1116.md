# Queue rewind-pilot-1116-rerun1-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rewind-pilot-1116-rerun1-1116-2026-11-16.jsonl`.

## 2026-11-16T20:24 UTC, build cd50ab966, jobs bp-rewind-pilot-a, bp-rewind-pilot-b, bp-rewind-pilot-c

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rewind-pilot-a | 20:24 (04:24) | 6.9 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->132, stuckcheckasync_run 0->132, etspread_passes 0->1, stuckcheck_noplan 0->120, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->6<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1741.5/1629.4/1815.3 -> 1924.4/1835.9/1761.5 | - | timing: invalid (noise medium; heavy tool samplers); build: asi 2b633b6935a7 ts3native.asi.rwud1116 ts3native 0.36.268-rwud1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); start: fresh (92 s); snapshot taken (5860 ms); noise: medium (other apps 23.0% of game CPUs); others on game CPUs 23.0%; <br>sim ms/s 202.3; p99 84 ms, p99.9 147, max 148, >=300 0, >=1000 0 (0.4 min) |

Paused after bp-rewind-pilot-a (AI usage 5h - | 7d 98% (reset 14:00 PHT) | read 7 min ago); resume with `--from bp-rewind-pilot-b`.

rewind: 0 of 2 planned jobs started from a rewind, 0 refused, 0 failed (fresh load instead); ~0 min saved vs fresh starts (92 s each: mean of this run's 1 fresh starts)
