# Queue jit-coremod-correct-1120-rerun1 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/jit-coremod-correct-1120-rerun1-2026-11-20.jsonl`.

## 2026-11-20T15:51 UTC, build 58f85dfe3, jobs ip-coremod-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-coremod-verify | 15:51 (23:51) | 17.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 03:32 +703 min, no 03:00); ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1794.4/1723.5/1836.3 -> 1844.5/1773.1/1816.7 | - | timing: invalid (noise medium); build: asi 96623cf7f560 not in DB ts3native 0.36.298-coremod1 (log ok); ts3fixes tuning 96f496b7d197 ts3fixes.package.m1120n/ts3fixes.package.m1120t/ts3fixes.package.m1120u (expected game-copy/ts3fixes.package.m1120t); noise: medium (other apps 25.9% of game CPUs, other CPUs 73.7% busy); others on game CPUs 25.9%; clock avg/min 3790/3102 MHz [cut: full row in the jsonl]<br>sim ms/s 40.9; p99 919 ms, p99.9 1904, max 3724, >=300 196, >=1000 10 (5.3 min) |
