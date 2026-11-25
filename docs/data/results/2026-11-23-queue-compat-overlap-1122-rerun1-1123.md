# Queue compat-overlap-1122-rerun1-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-overlap-1122-rerun1-1123-2026-11-23.jsonl`.

## 2026-11-23T13:25 UTC, build cd648f975, jobs ip-compat-overlap-smoke, ip-compat-overlap-play, ip-compat-overlap-control

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-compat-overlap-smoke | 13:25 (21:25) | 5.6 | **FAIL** | FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.RomanticInteractions/^ts3fixes: script assemblies \(\d+\): .*Savanita\.RomanticInteractions.*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.Romanti; noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a; ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b) [cut: full row in the jsonl] |

Stopped: ip-compat-overlap-smoke failed (stopQueueOnFail): cycle.ts --save exit 1.

## 2026-11-23T18:46 UTC, build cd648f975, jobs ip-compat-overlap-smoke, ip-compat-overlap-play, ip-compat-overlap-control

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-compat-overlap-smoke | 18:46 (02:46) | 3.4 | **FAIL** | PASS: profile 1 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->73, stuckcheckasync_run 0->73, etspread_passes 1->1, stuckcheck_noplan 0->56, stuckcheck_rearmed 0->13, stuckcheck_reset 0->4 [cut: full row in the jsonl] | 0 | 1763.2/1645.7/1801.5 -> 1796.3/1698.2/1776.1 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.Romanti; noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b) [cut: full row in the jsonl] |

Stopped: ip-compat-overlap-smoke failed (stopQueueOnFail): FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.RomanticInteractions|^ts3fixes: script assemblies \(\d+\): .*Savanita\.RomanticInteractions.*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= 1).

## 2026-11-23T18:51 UTC, build cd648f975, jobs ip-compat-overlap-smoke, ip-compat-overlap-play, ip-compat-overlap-control

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-compat-overlap-smoke | 18:51 (02:51) | 3.3 | **FAIL** | PASS: profile 1 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->71, stuckcheckasync_run 0->71, etspread_passes 1->1, stuckcheck_noplan 0->51, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->14, stuckcheck_reset 0->5 [cut: full row in the jsonl] | 0 | 1778.9/1660.9/1803.1 -> 1815.1/1713/1792.2 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.Romanti; noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b) [cut: full row in the jsonl] |

Stopped: ip-compat-overlap-smoke failed (stopQueueOnFail): FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): .*TwoBTech\.Mods\.MoarInteractions.*Savanita\.RomanticInteractions|^ts3fixes: script assemblies \(\d+\): .*Savanita\.RomanticInteractions.*TwoBTech\.Mods\.MoarInteractions/ 0 lines (want >= 1).
