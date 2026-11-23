# Queue gc-idle-gate-time-1117-rerun2-1118-smoke 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-idle-gate-time-1117-rerun2-1118-smoke-2026-11-18.jsonl`.

## 2026-11-18T23:33 UTC, build 9c0fc2bae, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 23:33 (07:33) | 1.5 | **VOID** | VOID: known cause apex-0010-backbuffer (Apex Radiance 4.0.1 bridge client, patch 0010): run void, relaunched once as smoke-kc; fixed by our bridge patch 0016, https://github.com/loinyx/Sims3-ApexRadiance/pull/89 (docs/270 §29)<br>first attempt (known cause): error: cycle.ts --save exit 1; probe server exited (0) for ~60 s (fail-fast guard) [cut: full row in the jsonl] | - | ? -> ? | - | timing: invalid (job VOID: VOID: known cause apex-0010-backbuffer (Apex Radiance 4.0.1 bridge client, patch 0010): run void, relaunched once as smo; noise medium; A/B iterations unknown); build: asi d67f02888197 ts3native.asi.gchold2-1117 ts3native 0.36.272-gchold2; ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b) [cut: full row in the jsonl] |

## 2026-11-18T23:44 UTC, build be38e484d, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 23:44 (07:44) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->289, stuckcheckasync_run 0->289, etspread_passes 0->1, stuckcheck_noplan 0->235, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->26, stuckcheck_reset 0->8<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1758.1/1636.7/1771.4 -> 2017/1930.9/1571.7 | - | timing: invalid (A/B 1 off / 2 on iterations, < 4 per arm); build: asi d67f02888197 ts3native.asi.gchold2-1117 ts3native 0.36.272-gchold2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b); noise: low; others on game CPUs 0.3% (apps; incl. kernel 17.2%); clock avg/min 4153/4105 MHz; [est 15 min] measurement smoke: BP 10 min [cut: full row in the jsonl]<br>sim ms/s off 231.8 / on 278.1 (on - off +46.4, +20.0%); off: p99 94 ms, p99.9 247, max 7989, >=300 4, >=1000 4 (1.8 min); on: p99 85 ms, p99.9 227, max 8373, >=300 9, >=1000 9 (3.4 min) |
