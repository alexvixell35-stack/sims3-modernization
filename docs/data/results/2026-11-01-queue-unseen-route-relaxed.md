# Queue unseen-route-relaxed 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-route-relaxed-2026-11-01.jsonl`.

## 2026-11-01T01:38 UTC, build 4c920500, jobs sv-ur-fenced-repair

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ur-fenced-repair | 01:38 (09:38) | 2 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step fenced_place failed: no spot on Stoney Falls unreachable for every placed Sim: 1692.1,1596.5: Zebulon Scroggins: lot entry point not routable; 1690.4,1604.2: Zebulon Scroggins: lot entry point not routable; 1661.4,1617.8: Zebulon Scroggins: lot entry point not routable; 1679.5,1646.4: Zebulon Scroggins: lot entry point<br>ts3fixe | 1 | 2061/1789.3/1612.9 -> 2101.4/1820.3/1595.3 | asyncStarted +200, asyncRefused +11, asyncCollected +200, asyncUs +1883711, asyncMaxUs +90755, asyncPumpUs +925290, asyncPumpWaits +196, asyncQueueMax +2 | timing: invalid (job VOID: runInvalid: profiler cut short (20 of 480 s); noisy machine (machine load: no samples); crash or abort (no "done; game a; noise medium); build: asi 27edd9af1a45 unseenroute2-asi ts3native 0.36.150 (log ok); ts3fixes tuning 0a13b5ab834b unseenroute7 (expected game-copy/ts3fixes.package.unseenroute7); noise: medium (other apps 14.3% of game CPUs); others on game CPUs 14.3% |

Stopped: sv-ur-fenced-repair failed (stopQueueOnFail): FAIL: profile 8 min, seed 12345 (rng-compare.ts).
