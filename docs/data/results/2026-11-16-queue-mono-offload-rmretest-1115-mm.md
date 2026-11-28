# Queue mono-offload-rmretest-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-offload-rmretest-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T03:08 UTC, build e82388bb, jobs bp-rmr-snapback

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rmr-snapback | 03:08 (11:08) | 12.4 | **FAIL** | FAIL: profile 16 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step unseen_snapback failed: unseen_snapback check failed: wanted 6, passed 0, failed 36, not tested 0, passed with Rate debt 0 (needs 1), state done (fewer passes than asked)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0:  | 1 | 1709.5/1633.1/1810.3 -> 1819.7/1720.4/1794.8 | rmPasses +19739, rmPassesRm +13037, rmK_1 +18488, rmK_2 +96, rmK_3 +36, rmK_4 +74, rmK_5 +63, rmK_6 +48, rmK_7 +934, rmWalks +14782, rmCallsAll +32646, rmLateRm +28, rmActSum +9298337, rmActMax +526, rmRmSum +30383, rmRmMax +7, rmSampled +1233, rmW1Us +1422489, rmTsN +580803, rmTsUs +48694, rmTailN_0 +414, rmTailN_1 +819, rmTailUs_0 +11181, rmTailUs_1 +187192, rmSWalks +910, rmWalkUs +96668, rmN_0 | timing: invalid (job VOID: runInvalid: profiler cut short (643 of 960 s); crash or abort (no "done; game alive"); noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.pac |

Stopped: bp-rmr-snapback failed (stopQueueOnFail): FAIL: profile 16 min, seed 12345 (rng-compare.ts).
