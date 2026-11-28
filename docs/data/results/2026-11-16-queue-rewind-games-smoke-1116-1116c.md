# Queue rewind-games-smoke-1116-1116c 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rewind-games-smoke-1116-1116c-2026-11-16.jsonl`.

## 2026-11-16T15:28 UTC, build 68447d379, jobs bp-rewind-games-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rewind-games-smoke | 15:28 (23:28) | 14 | PASS | PASS: game 1 off: profile 2 min, first game after the load<br>PASS: game 2 on: profile 2 min, after a reload (rewind refused)<br>PASS: game 3 on: profile 2 min, after a reload (rewind refused)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1750.3/1634.4/1812.9 -> 1850.2/1749.6/1799.7 | - | timing: invalid (noise medium); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: medium (other apps 41.2% of game CPUs); others on game CPUs 41.2%; clock avg/min 4076/3891 MHz; [est 15 min] [natural play, no cheats |
