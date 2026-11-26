# Queue mono-offload-rmretest-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-offload-rmretest-1116-2026-11-16.jsonl`.

## 2026-11-16T08:21 UTC, build 958eff6a, jobs bp-rmr-snapback

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rmr-snapback | 08:21 (16:21) | 12.7 | **FAIL** | FAIL: profile 16 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step unseen_snapback failed: unseen_snapback check failed: wanted 6, passed 6, failed 1, not tested 2, passed with Rate debt 1 (needs 1), state done<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had  | 1 | 1712.8/1633.6/1718 -> 1791/1697.1/1698.5 | rmPasses +19598, rmPassesRm +12293, rmK_1 +18409, rmK_2 +123, rmK_3 +18, rmK_4 +44, rmK_5 +50, rmK_6 +34, rmK_7 +920, rmWalks +14725, rmCallsAll +22392, rmLateRm +35, rmActSum +9115735, rmActMax +516, rmRmSum +18922, rmRmMax +5, rmSampled +1224, rmW1Us +1584579, rmTsN +569276, rmTsUs +53895, rmTailN_0 +455, rmTailN_1 +769, rmTailUs_0 +10521, rmTailUs_1 +187505, rmSWalks +897, rmWalkUs +104948, rmN | timing: invalid (job VOID: runInvalid: profiler cut short (644 of 960 s); noisy machine (other CPU 14.3% >= 10%); crash or abort (no "done; game al; noise high); build: asi 728891275879 ts3native.asi.master-1116 ts3native 0.36.265-m1116 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mo |

Stopped: bp-rmr-snapback failed (stopQueueOnFail): FAIL: profile 16 min, seed 12345 (rng-compare.ts) | FAIL: repro step unseen_snapback failed: unseen_snapback check failed: wanted 6, passed 6, failed 1, not tested 2, passed with Rate debt 1 (needs 1), state done.
