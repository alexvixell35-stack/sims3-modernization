# Queue unseen-tp-goal-1115 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-tp-goal-1115-2026-11-15.jsonl`.

## 2026-11-15T08:02 UTC, build a96b4916, jobs bp-unseen-tp-goal

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-unseen-tp-goal | 08:02 (16:02) | 13.9 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet) | 0 | 2177.4/1895.4/1596.8 -> 2272.2/1903.4/1466.2 | - | timing: invalid (noise medium); build: asi 7abdd048b563 ts3native.asi.master-lanes-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning a3888c256bca ts3fixes.package.master-lanes-1115/ts3fixes.package.master-lanes-1115b (expected game-copy/ts3fixes.package.master-lanes-1115b); noise: medium (other apps 13.6% of game CPUs); others on game CPUs 13.6%; clock avg/min 4209/4095 MHz; Correctness (10 |
