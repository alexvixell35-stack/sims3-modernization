# Queue autliveseen-cutride-correct-1116-rerun1-1117 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/autliveseen-cutride-correct-1116-rerun1-1117-2026-11-16.jsonl`.

## 2026-11-16T23:27 UTC, build b9d68e0ee, jobs bp-ride-smoke, bp-ride-dry-crowd, ip-ride-dry-crowd, bp-ride-act-crowd, ip-ride-act-crowd

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ride-smoke | 23:27 (07:27) | 7.6 | **FAIL** | FAIL: profile 4 min, seed 12345 (rng-compare.ts)<br>FAIL: early gate: no seen NPC rode an open batch: switch, package, or no unseen request in the same frame (fixes:autliveseenride_rode=0) [want >0 from 200 s]; child stopped<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>not | 1 | 1782.6/1664.3/1699.4 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise high); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning 826270d81d86 ts3fixes.autliveseen-cutride-1116 (expected game-copy/ts3fixes.package.autliveseen-cutride-1116); noise: high (other apps 85.4% of game CPU<br>sim ms/s 163.0; p99 90 ms, p99.9 127, max 171, >=300 0, >=1000 0 (1.4 min) |

Stopped: bp-ride-smoke failed (stopQueueOnFail): early gate: no seen NPC rode an open batch: switch, package, or no unseen request in the same frame (fixes:autliveseenride_rode=0) [want >0 from 200 s].
