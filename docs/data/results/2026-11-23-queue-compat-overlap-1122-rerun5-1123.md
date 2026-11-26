# Queue compat-overlap-1122-rerun5-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-overlap-1122-rerun5-1123-2026-11-23.jsonl`.

## 2026-11-23T14:43 UTC, build cd648f975, jobs ip-overlap-bisect-e-simsmx, ip-overlap-bisect-e-icarus

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-overlap-bisect-e-simsmx | 14:43 (22:43) | 3.2 | PASS | PASS: profile 1 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->75, stuckcheckasync_run 0->75, etspread_passes 1->1, stuckcheck_noplan 0->51, stuckcheck_rearmed 0->16, stuckcheck_reset 0->8<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1772.2/1649.6/1812.5 -> 1765.8/1668.3/1809 | - | timing: invalid (noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b); noise: medium (other apps 13.1% of game CPUs); others on game CPUs 13.1%; clock avg/min 4115/4073 MHz [cut: full row in the jsonl] |
| ip-overlap-bisect-e-icarus | 14:46 (22:46) | 5.6 | **FAIL** | FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): / 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): / 0 lines (want >= 1); noise high); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a; ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b); noise: high (other apps 16.0% of game CPUs) [cut: full row in the jsonl] |
