# Queue commodmap-fast-1121-correct-rerun3 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/commodmap-fast-1121-correct-rerun3-2026-11-21.jsonl`.

## 2026-11-21T03:13 UTC, build 14aed570a, jobs ip-mm-commodmap-shadow

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-mm-commodmap-shadow | 03:13 (11:13) | 6.4 | **FAIL** | FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= 1)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= ; noise high); build: asi 66f4d0305688 ts3native.asi.mkit2-1121 ts3native 0.36.298-mkit2; ts3fixes tuning e290a6386bec ts3fixes.package.cmf-1121 (expected game-copy/ts3fixes.package.cmf-1121); noise: high (other apps 55.9% of game CPUs) [cut: full row in the jsonl] |

Stopped: ip-mm-commodmap-shadow failed (stopQueueOnFail): cycle.ts --save exit 1.
