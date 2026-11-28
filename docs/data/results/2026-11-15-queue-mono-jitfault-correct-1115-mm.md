# Queue mono-jitfault-correct-1115-mm 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-jitfault-correct-1115-mm-2026-11-15.jsonl`.

## 2026-11-15T13:49 UTC, build 78389c7f, jobs bp-jitfault-forced, bp-jitfault-on, ip-jitfault-on, sv-jitfault-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-jitfault-forced | 13:49 (21:49) | 7.3 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: early gate: the FaultTest method never ran compiled: hook not armed or the method is not reached from interpreted code (frTestCalls=0) [want >0 from 300 s]; child stopped<br>FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (asi without the c | 1 | 1799.3/1621.8/1784.4 -> undefined/undefined/undefined | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEn; noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3f |

Stopped: bp-jitfault-forced failed (stopQueueOnFail): early gate: the FaultTest method never ran compiled: hook not armed or the method is not reached from interpreted code (frTestCalls=0) [want >0 from 300 s].

## 2026-11-15T22:45 UTC, build 53cce0ec, jobs bp-jitfault-forced, bp-jitfault-on, ip-jitfault-on, sv-jitfault-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-jitfault-forced | 22:45 (06:45) | 11.8 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: idlememo_skipped 0->10, stuckcheckasync_run 0->284, stuckcheckasync_queued 0->284, bouncerskip_skipped 0->6812, stuckcheckasync_maxdepth 0->14, idlememo_capped 0->216, stuckcheckasync_maxwait_simsec 0->1488, owchain_alarms 0->4, owspread_types_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->236, stuckcheck_notstuck 0->25 | 0 | 1717.6/1631.5/1813.2 -> 1964.3/1886.5/1587.6 | - | timing: invalid (job FAIL: FAIL: events not fired: frRecovered=0 (want >=1); frTestCalls=0 (want >=1000); noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.mas |

Stopped: bp-jitfault-forced failed (stopQueueOnFail): FAIL: events not fired: frRecovered=0 (want >=1); frTestCalls=0 (want >=1000).
