# Queue dating-profile 2026-11-03

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/dating-profile-2026-11-03.jsonl`.

## 2026-11-03T05:29 UTC, build 94b7eac, jobs bp-dating-profile-check, bp-dating-profile-noaut

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dating-profile-check | 05:29 (13:29) | 3.2 | **FAIL** | FAIL: profile 6 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: datingprofile expect broken: wrong outcome; met False, records added 0, SimHasProfile False/False (switch False)<br>FAIL: self check: [RouteAsync] on but 0 async route jobs started (invokeHook 0): async off, run void<br>note: self check: [Discourage] DeferFootprint on but usfDeferre | 1 | 2264.1/2147.6/1327.1 -> 2398.6/2197.3/1235.3 | - | timing: invalid (job VOID: FAIL: self check: [RouteAsync] on but 0 async route jobs started (invokeHook 0): async off, run void; runInvalid: profil); build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning d2405d5eb4ba not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.1%; clock avg/min 3344/3024 MHz; Correctness (6 min), AutShadow on (Mode 0). Pa |

Stopped: bp-dating-profile-check failed (stopQueueOnFail): FAIL: profile 6 min, seed 12345 (rng-compare.ts).

## 2026-11-03T05:37 UTC, build 94b7eac, jobs bp-dating-profile-check, bp-dating-profile-noaut

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dating-profile-check | 05:37 (13:37) | 10.2 | PASS | PASS: profile 6 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autshadow_gm6_cha_mismatch 0->7, autshadow_gm4_methods 0->683, autshadow_gm4_us 0->249483, autshadow_content_unknown 0->2, autshadow_gm6_owners_skipped 0->12, autshadow_gm6_us 0->111170, autshadow_requests 0->877, autshadow_content_modules 0->7, autshadow_gm6_methods 0->683, autshadow_gm5_types 0->11, autshadow_gm6_cha_methods  | 0 | 2252.1/2088.1/1415.4 -> 2671.5/2514.2/872 | - | timing: valid-single; build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning d2405d5eb4ba not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.1%; clock avg/min 3454/3111 MHz; Correctness (6 min), AutShadow on (Mode 0). Pass: the repro's steps pass (off: records added 2, both read as a profile; on: records added 0, no profile for any empty record,  |
| bp-dating-profile-noaut | 05:47 (13:47) | 9.2 | PASS | PASS: profile 6 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->137, stuckcheckasync_run 0->137, etspread_passes 0->1, stuckcheck_noplan 0->133, stuckcheck_notstuck 0->5, stuckcheck_rearmed 0->3 | 0 | 2285.8/2122.9/1364.2 -> 2589/2494.3/942.9 | - | timing: valid-single; build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning d2405d5eb4ba not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.2%; clock avg/min 3473/3216 MHz; Correctness (6 min), release defaults (AutShadow off, so DidSimsMeetOnline has no hook). Pass: the repro's steps pass (on: the records made read as no profile), datingprofile |
