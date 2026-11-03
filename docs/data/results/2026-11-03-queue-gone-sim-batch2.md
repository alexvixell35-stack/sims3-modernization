# Queue gone-sim-batch2 2026-11-03

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gone-sim-batch2-2026-11-03.jsonl`.

## 2026-11-03T05:32 UTC, build 94b7eac, jobs bp-bed-partner-check, bp-leavejob-dispose-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bed-partner-check | 05:32 (13:32) | 0.1 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown); Correctness (6 min). Pass: the repro's steps pass (off: NullReferenceException, claim unfinished; on: no exception, own half and Sim.Bed set, other half free), bedpartner_claims >= 1. |
| bp-leavejob-dispose-check | 05:32 (13:32) | 0.1 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown); Correctness (7 min). Pass: the repro's steps pass (off: the deleted Sim stays in Workers; on: in no Workers, Coworkers or Boss), leavejobdispose_leaves >= 1. |

## 2026-11-03T05:57 UTC, build 94b7eac, jobs bp-bed-partner-check, bp-leavejob-dispose-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bed-partner-check | 05:57 (13:57) | 3.2 | **FAIL** | FAIL: profile 6 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: bedpartner check: no household with a couple at home, neither instantiated, and a double bed<br>note: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle (forced repro: not the job subject) | 1 | 2254.4/2105.8/1388.6 -> 2389.5/2160.4/1298.7 | - | timing: invalid (job VOID: runInvalid: profiler cut short (2 of 360 s); noisy machine (machine load: no samples)); build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning dd53454ba3ef not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.4%; clock avg/min 3580/3155 MHz; Correctness (6 min). Pass: the repro's steps pass (off: NullReferenceException, c |
| bp-leavejob-dispose-check | 06:00 (14:00) | 10.2 | PASS | PASS: profile 7 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->198, stuckcheckasync_run 0->197, etspread_passes 0->1, stuckcheck_noplan 0->181, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->6 | 0 | 2241.8/2085.9/1414.5 -> 2610.8/2473/973.1 | - | timing: valid-single; build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning dd53454ba3ef not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.2%; clock avg/min 3517/3205 MHz; Correctness (7 min). Pass: the repro's steps pass (off: the deleted Sim stays in Workers; on: in no Workers, Coworkers or Boss), leavejobdispose_leaves >= 1. |

## 2026-11-03T06:14 UTC, build 94b7eac, jobs bp-bed-partner-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-bed-partner-check | 06:14 (14:14) | 3.2 | **FAIL** | FAIL: profile 6 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: bedpartner expect broken: wrong outcome; threw nothing, own half True, other half free True, Sim.Bed set True; Jeffrey Cook (partner Lady Cook, Off White House - 3br, 3ba), partner put back (switch False) | 1 | 2312.1/2111.4/1373.4 -> 2369.1/2165.2/1299.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (1 of 360 s); noisy machine (machine load: no samples)); build: asi 6b167c9d00a8 not in DB ts3native 0.36.166 (log ok); ts3fixes tuning dd53454ba3ef not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.2%; clock avg/min 3492/3296 MHz; Correctness (6 min). Pass: the repro's steps pass (off: NullReferenceException, c |
