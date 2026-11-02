# Queue autshadow-content-guard 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/autshadow-content-guard-2026-11-01.jsonl`.

## 2026-11-01T12:22 UTC, build 21ae326, jobs bp-cg-user

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-cg-user | 12:22 (20:22) | 4.2 | **CRASH** | cycle.ts --save exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: crash: no crash report written since 2026-11-01T12:22:52.159Z; noise high; heavy tool samplers; heavy tool RouteSnap.audits); build: asi ebfcb61f71c8 not in DB ts3native 0.36.152; ts3fixes tuning 54aacd9ba2b4 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 9.0%; clock avg/min 3304/2307 MHz; Arm a, the player' |

Stopped: bp-cg-user failed (stopQueueOnFail): cycle.ts --save exit 1.

## 2026-11-01T12:27 UTC, build 21ae326, jobs bp-cg-notui

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-cg-notui | 12:27 (20:27) | 8.4 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm6_mismatch 0->82, autshadow_gm6_cha_mismatch 0->624, autshadow_gm4_methods 0->683, autshadow_gm4_us 0->314879, autshadow_content_unknown 0->2, autshadow_gm6_owners_skipped 0->17, autshadow_gm6_us 0->122895, autshadow_requests 0->700, autshadow_content_modules 0->7, autshadow_gm6_methods 0->683, autshadow_gm5_types 0 | 0 | 2234.1/2134.4/1348.5 -> 2545.1/2480.2/946.6 | - | timing: invalid (noise high; heavy tool samplers; heavy tool RouteSnap.audits); build: asi ebfcb61f71c8 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 54aacd9ba2b4 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock), GPU 3D others 32.2%); others on game CPUs 2.5%; clock avg/min 3186/2782 MHz; Arm b, as arm a with Tiny UI Fix moved out (its bundle carrie |

## 2026-11-01T12:35 UTC, build 21ae326, jobs bp-cg-ref

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-cg-ref | 12:35 (20:35) | 8.3 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm4_methods 0->683, autshadow_gm4_us 0->81585, autshadow_gm6_us 0->23228, autshadow_requests 0->642, autshadow_content_modules 0->7, autshadow_gm6_methods 0->683, autshadow_gm5_types 0->11, owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->115, stuckcheckasync_run 0->115, etspread_passes 0->1, s | 0 | 2268.8/2153.1/1335 -> 2629.6/2518.1/939.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 119% from 153%); noise medium; heavy tool samplers; heavy tool RouteSnap.audits); build: asi ebfcb61f71c8 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 54aacd9ba2b4 not in DB (expected build/ts3fixes.package); noise: medium (other apps 3.1% of game CPUs, GPU 3D others 26.7%); others on game CPUs 3.1%; clock avg/min 3123/2 |

## 2026-11-01T12:44 UTC, build 21ae326, jobs bp-cg-user

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-cg-user | 12:44 (20:44) | 8.4 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm6_mismatch 0->96, autshadow_gm6_cha_mismatch 0->862, autshadow_gm4_methods 0->683, autshadow_gm4_us 0->396739, autshadow_content_unknown 0->4, autshadow_gm6_owners_skipped 0->17, autshadow_gm6_us 0->147607, autshadow_requests 0->478, autshadow_content_modules 0->7, autshadow_gm6_methods 0->683, autshadow_gm5_types 0 | 0 | 2275.3/2108.9/1382.6 -> 2579.2/2469.1/947.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 11.9% >= 10%); noise medium; heavy tool samplers; heavy tool RouteSnap.audits); build: asi ebfcb61f71c8 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 54aacd9ba2b4 not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.5% of game CPUs, GPU 3D others 29.0%); others on game CPUs 7.5%; clock avg/min 2906/2314 MH |

## 2026-11-01T12:52 UTC, build 21ae326, jobs bp-cg-user

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-cg-user | 12:52 (20:52) | 8.5 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm6_mismatch 0->96, autshadow_gm6_cha_mismatch 0->862, autshadow_gm4_methods 0->683, autshadow_gm4_us 0->358071, autshadow_content_unknown 0->4, autshadow_gm6_owners_skipped 0->17, autshadow_gm6_us 0->129966, autshadow_requests 0->522, autshadow_content_modules 0->7, autshadow_gm6_methods 0->683, autshadow_gm5_types 0 | 0 | 2289.8/2148/1343.1 -> 2637.6/2546.4/854.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 77% from 141%); noise high; heavy tool samplers; heavy tool RouteSnap.audits); build: asi ebfcb61f71c8 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 54aacd9ba2b4 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 4.8%; clock avg/min 3014/1624 MHz; Arm a, the play |
