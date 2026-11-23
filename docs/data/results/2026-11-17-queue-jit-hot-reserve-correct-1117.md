# Queue jit-hot-reserve-correct-1117 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/jit-hot-reserve-correct-1117-2026-11-17.jsonl`.

## 2026-11-17T13:18 UTC, build 6deedf20b, jobs bp-hotres-correct, ip-hotres-correct

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hotres-correct | 13:18 (21:18) | 17.3 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->5, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay: [0-9]+ positions/ 0 lines (want >= 1) [cut: full row in the jsonl] | 0 | 1866.5/1779.2/1771.8 -> 2005.4/1786.7/1665.7 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay: [0-9]+ positions/ 0 lines (want >= 1); log ; noise medium); build: asi 353d22013256 ts3native.asi.hotres2-1117 ts3native 0.36.273-hotres2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b) [cut: full row in the jsonl]<br>sim ms/s 82.5; p99 546 ms, p99.9 988, max 5296, >=300 75, >=1000 1 (4.9 min) |

Stopped: bp-hotres-correct failed (stopQueueOnFail): FAIL: events not fired: log /^jit: compiled .*Motive\.UpdateMotiveWithDecay: [0-9]+ positions/ 0 lines (want >= 1); log /^jit: HotReserve released \(.*Motive\.UpdateMotiveWithDecay compiled\)/ 0 lines (want >= 1); log /^jitcount yrow .*Motive\.UpdateMotiveWithDecay: entries [1-9]/ 0 lines (want >= 1).
