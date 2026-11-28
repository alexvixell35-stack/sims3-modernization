# Queue mono-jitsmall-b6-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-jitsmall-b6-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T00:39 UTC, build 6a216cc5, jobs bp-jitsmall-b6, ip-jitsmall-b6, sv-jitsmall-b6

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-jitsmall-b6 | 00:39 (08:39) | 11.8 | **FAIL** | FAIL: events not fired: @jitOn missing; @jitDirect missing; @jitRegionKB missing; @jitMethCap missing; @jitMethods missing; @bcOn missing; @bcFills missing; @clOn missing; @tdOn missing; @mcSwitch missing; @mfReady missing; @mfRuns missing; @mfInner missing; @mfWhole missing; @clKillWhy missing; @rtdKillWhy missing; jitMismatch missing; jitFaults missing; jitTableFull missing; mcMismatch missing;  | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: @jitOn missing; @jitDirect missing; @jitRegionKB missing; @jitMethCap missing; @jitMethods missi; noise medium; heavy tool samplers); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1; ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshad |

Stopped: bp-jitsmall-b6 failed (stopQueueOnFail): cycle.ts --save exit 1.
