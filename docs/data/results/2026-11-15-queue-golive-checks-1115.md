# Queue golive-checks-1115 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/golive-checks-1115-2026-11-15.jsonl`.

## 2026-11-15T02:54 UTC, build 76d5cdf3, jobs bp-golive-checks

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-golive-checks | 02:54 (10:54) | 30.2 | **CRASH** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: exit 1 with no verdict line (job timeout); last output: kill dump powershell 2320 MB in 10 s: C:\Users\<user>\IdeaProjects\sims-3-modernization\probe-out\dumps\ts3w-9144-20261115T032438.dmp; lock waits: 0 of 153 threads; no cycle<br>ts3fixes activity: autunseen_offlist 0->131061, autshadow_skips 0->147552, g8_hh_simhours_x100 0->52704, g8_l | 1 | 2148.8/1895.9/1513.4 -> 2314.8/2160.4/1311.3 | asyncStarted +41661, asyncRefused +743, asyncCollected +41617, asyncDropped +6, asyncUs +648120946, asyncMaxUs +636778, asyncPumpUs +49280610, asyncPumpWaits +58117, asyncRefusedM1Pending +377, asyncSkipped +33, asyncBackoffs +647, asyncQueueFull +11, asyncQueueMax +32 | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 10.7% >= 10%); crash or abort (no "done; game alive"); noise high); build: asi 7abdd048b563 ts3native.asi.master-lanes-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning a3888c256bca ts3fixes.package.master-lanes-1115/ts3fixes.package.master-lanes-1115b (expected game-copy/ts3fixes.package.master-lanes-1115); noise: high (other  |

Stopped: bp-golive-checks failed (stopQueueOnFail): FAIL: profile 10 min, seed 12345 (rng-compare.ts).
