# Queue compat-overlap-1122 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-overlap-1122-2026-11-23.jsonl`.

## 2026-11-23T07:14 UTC, build cd3070f8a, jobs ip-compat-overlap-smoke, ip-compat-overlap-play, ip-compat-overlap-control

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-compat-overlap-smoke | 07:14 (15:14) | 5.8 | **FAIL** | FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.RomanticInteractions/^ts3fixes: script assemblies \(\d+\): .*Savanita\.RomanticInteractions.*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.Romanti; noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a; ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b) [cut: full row in the jsonl] |

Stopped: ip-compat-overlap-smoke failed (stopQueueOnFail): cycle.ts --save exit 1.
