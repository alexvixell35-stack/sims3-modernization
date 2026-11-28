# Queue s3pp-compat-1124-ts3exe-m1124d2-ea 2026-11-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/s3pp-compat-1124-ts3exe-m1124d2-ea-2026-11-24.jsonl`.

Machine: ea-laptop

## 2026-11-24T14:34 UTC, build 95d0135f, jobs bp-s3ppgc-smoke-ts3exe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| bp-s3ppgc-smoke-ts3exe | 14:34 (22:34) | 1.3 | **KILLED** | PASS: repro events fired<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise medium; correctness-only tool ProbeSnap.CallSites); build: asi ef19c1b510a1 ts3native-ea169.asi.eam1124d2 ts3native 0.36.305-eam1124d2; ts3fixes tuning b7018ac0bf65 ts3fixes.package.m1124b/ts3fixes.package.eam1124b/ts3fixes.package.m1124c/ts3fixes.package.m1124d/ts3fixes.package.eam1124d2 (expected game-copy/eam1124d2/ts3fixes.package) [cut: full row in the jsonl] | ea-laptop |

Stopped: bp-s3ppgc-smoke-ts3exe failed (stopQueueOnFail): cycle.ts --save exit 1; TS3.exe process gone for ~60 s (fail-fast guard).
