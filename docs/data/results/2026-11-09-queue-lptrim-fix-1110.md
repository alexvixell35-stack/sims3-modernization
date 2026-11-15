# Queue lptrim-fix-1110 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/lptrim-fix-1110-2026-11-09.jsonl`.

## 2026-11-09T17:18 UTC, build bd4f18af, jobs bp-lptrim-on-memtravel-x3, bp-lptrim-off-memtravel-x3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-lptrim-on-memtravel-x3 | 17:18 (01:18) | 10.7 | PASS | travel: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2185.9/1872.2/1581.3 -> 2346.1/2052.5/1357.3 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 3c80e76729dc ts3native.asi.rc1110c ts3native 0.36.238 (log ok); ts3fixes tuning 1f88237bac75 ts3fixes.package.rc1110b (expected game-copy/ts3fixes.package.rc1110b); noise: medium (other apps 39.6% of game CPUs); others on game CPUs 39.6%; clock avg/min 4174/4078 MHz; [est 30 min] [LotPool] Trim=1 arm (SettleSec 1 |
| bp-lptrim-off-memtravel-x3 | 17:28 (01:28) | 10.1 | PASS | travel: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2177.2/1885.2/1564.6 -> 2328.9/2210.6/1219.1 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi 3c80e76729dc ts3native.asi.rc1110c ts3native 0.36.238 (log ok); ts3fixes tuning 1f88237bac75 ts3fixes.package.rc1110b (expected game-copy/ts3fixes.package.rc1110b); noise: medium (other apps 32.9% of game CPUs); others on game CPUs 32.9%; clock avg/min 4192/4176 MHz; [est 25 min] [LotPool] Trim=0 comparison arm o |
