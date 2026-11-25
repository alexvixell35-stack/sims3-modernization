# Queue bookhand-verify-1112 2026-11-12

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/bookhand-verify-1112-2026-11-12.jsonl`.

## 2026-11-12T00:49 UTC, build 42e1889b, jobs bp-bookhand-verify-1112

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bookhand-verify-1112 | 00:49 (08:49) | 6.7 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call: reflect_call until [{"path":"ready","equals":true}] not met in 180 s: {"reading":false,"stops":5,"$type":"Ts3Fixes.Fixes+BhState","seen":0,"stuck":false,"$str":"Ts3Fixes.Fixes+BhState","fixedN":0,"ready":false,"carry":true}<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0 | 1 | 2175.4/1887.5/1565.4 -> 2281.5/2090.3/1362.5 | - | timing: invalid (job VOID: runInvalid: profiler cut short (282 of 480 s); noisy machine (others 27.3% of game CPUs); crash or abort (no "done; game; noise high); build: asi f05a9fd6a71c ts3native.asi.rc1111d ts3native 0.36.240 (log ok); ts3fixes tuning 3bee114eaf9b ts3fixes.package.rc1111c (expected game-copy/ts3fixes.package.rc1111c); noise: high (other apps 26.2% of game CPUs); others on game CP |
