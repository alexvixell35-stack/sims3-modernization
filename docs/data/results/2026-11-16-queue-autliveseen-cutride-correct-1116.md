# Queue autliveseen-cutride-correct-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/autliveseen-cutride-correct-1116-2026-11-16.jsonl`.

## 2026-11-16T19:58 UTC, build 67bc6466e, jobs bp-ride-smoke, bp-ride-dry-crowd, ip-ride-dry-crowd, bp-ride-act-crowd, ip-ride-act-crowd

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ride-smoke | 19:58 (03:58) | 5.5 | **FAIL** | FAIL: profile 4 min, seed 12345 (rng-compare.ts)<br>FAIL: early gate: no seen NPC rode an open batch: switch, package, or no unseen request in the same frame (fixes:autliveseenride_rode=0) [want >0 from 200 s]; child stopped<br>FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (asi without the counte | 1 | 1753.6/1632/1813.4 -> undefined/undefined/undefined | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEn; noise medium); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning 826270d81d86 ts3fixes.autliveseen-cutride-1116 (expected game-copy/ts3fixes.package.autliveseen-cutride-1116); noise: medium (other apps 26.6% of game<br>sim ms/s 201.2; p99 86 ms, p99.9 149, max 174, >=300 0, >=1000 0 (1.4 min) |

Stopped: bp-ride-smoke failed (stopQueueOnFail): early gate: no seen NPC rode an open batch: switch, package, or no unseen request in the same frame (fixes:autliveseenride_rode=0) [want >0 from 200 s].
