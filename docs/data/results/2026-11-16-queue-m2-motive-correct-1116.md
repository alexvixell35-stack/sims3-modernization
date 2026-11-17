# Queue m2-motive-correct-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/m2-motive-correct-1116-2026-11-16.jsonl`.

## 2026-11-16T20:07 UTC, build 68447d379, jobs bp-m2-motive-correct, ip-m2-motive-correct

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-m2-motive-correct | 20:07 (04:07) | 16.2 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 11:53 +806 min, no 03:00); ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay \(/ 0 lines (want >= 1); log /^ | 0 | 1905.1/1784.7/1750.1 -> 2022.3/1770.7/1649.6 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay \(/ 0 lines (want >= 1); log /^jitcount yrow; noise medium); build: asi 6ff008f1de0a not in DB ts3native 0.36.268-motive1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: medium (other <br>sim ms/s 71.2; p99 566 ms, p99.9 5708, max 7048, >=300 103, >=1000 4 (4.7 min) |

Stopped: bp-m2-motive-correct failed (stopQueueOnFail): FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay \(/ 0 lines (want >= 1); log /^jitcount yrow .*Motive\.UpdateMotiveWithDecay: entries [1-9]/ 0 lines (want >= 1).
