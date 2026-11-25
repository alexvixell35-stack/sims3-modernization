# Queue compat-overlap-1122-rerun3-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-overlap-1122-rerun3-1123-2026-11-23.jsonl`.

## 2026-11-23T14:24 UTC, build cd648f975, jobs ip-overlap-bisect-c-arsilcherry, ip-overlap-bisect-c-junmainsimsicarus

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-overlap-bisect-c-arsilcherry | 14:24 (22:24) | 3.2 | PASS | PASS: profile 1 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->78, stuckcheckasync_run 0->78, etspread_passes 1->1, stuckcheck_noplan 0->52, stuckcheck_rearmed 0->18, stuckcheck_reset 0->8<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1780.4/1661.1/1810.9 -> 1780.4/1680.1/1802.9 | - | timing: invalid (noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b); noise: medium (other apps 13.4% of game CPUs); others on game CPUs 13.4%; clock avg/min 4076/4061 MHz [cut: full row in the jsonl] |
| ip-overlap-bisect-c-junmainsimsicarus | 14:27 (22:27) | 5.6 | **FAIL** | FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): / 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: script assemblies \(\d+\): / 0 lines (want >= 1); noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a; ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b/ts3fixes.package.rel031 (expected game-copy/ts3fixes.package.hf031b); noise: medium (other apps 16.7% of game CPUs) [cut: full row in the jsonl] |
