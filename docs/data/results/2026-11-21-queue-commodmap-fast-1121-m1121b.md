# Queue commodmap-fast-1121-m1121b 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/commodmap-fast-1121-m1121b-2026-11-21.jsonl`.

## 2026-11-21T08:19 UTC, build c36f9f608, jobs bp-commodmap-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-commodmap-check | 08:19 (16:19) | 14.1 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1741.1/1675/1813.2 -> 1848.2/1775.5/1794.8 | - | timing: invalid (noise medium); build: asi 830639419e49 ts3native.asi.m1121b ts3native 0.36.298-m1121b (log ok); ts3fixes tuning 33ae3f04e973 ts3fixes.package.m1121b (expected game-copy/ts3fixes.package.m1121b); noise: medium (other apps 31.0% of game CPUs); others on game CPUs 31.0%; clock avg/min 3811/3565 MHz [cut: full row in the jsonl]<br>sim ms/s 56.9; p99 <50 ms, p99.9 <50, max 81, >=300 0, >=1000 0 (4.8 min) |
