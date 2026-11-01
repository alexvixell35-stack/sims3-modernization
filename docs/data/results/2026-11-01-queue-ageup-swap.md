# Queue ageup-swap 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ageup-swap-2026-11-01.jsonl`.

## 2026-11-01T03:59 UTC, build 319c080, jobs bp-ageup-swap

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ageup-swap | 03:59 (11:59) | 17.1 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->7, owspread_types_run 0->140, stuckcheckasync_queued 0->399, stuckcheckasync_run 0->399, etspread_passes 0->1, stuckcheck_noplan 0->383, stuckcheck_notstuck 0->18, stuckcheck_rearmed 0->10 | 0 | 2345.5/2154.2/1358.6 -> 2649.5/2549.8/836.4 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning e964c8edf2b1 not in DB (expected build/ts3fixes.package); noise: medium (GPU 3D others 20.9%); others on game CPUs 0.5%; clock avg/min 4311/3943 MHz; Correctness (14 min): repro ageup-swap-check. Pass: off special part hangs (Done False) and finishes after unstick; on special f |

## 2026-11-01T04:51 UTC, build cfc1690, jobs bp-ageup-swap

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ageup-swap | 04:51 (12:51) | 17.2 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->7, owspread_types_run 0->140, stuckcheckasync_queued 0->412, stuckcheckasync_run 0->411, etspread_passes 0->1, stuckcheck_noplan 0->381, stuckcheck_notstuck 0->22, stuckcheck_rearmed 0->14, stuckcheck_reset 0->1 | 0 | 2330.4/2130.3/1366.3 -> 2662.5/2532.4/872.8 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning e964c8edf2b1 not in DB (expected build/ts3fixes.package); noise: medium (GPU 3D others 20.4%); others on game CPUs 0.6%; clock avg/min 4247/3787 MHz; Correctness (14 min): repro ageup-swap-check. Pass: off special part hangs (Done False) and finishes after unstick; on special f |

## 2026-11-01T05:43 UTC, build 0345361, jobs bp-ageup-swap

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ageup-swap | 05:43 (13:43) | 17.2 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->8, owspread_types_run 0->160, stuckcheckasync_queued 0->447, stuckcheckasync_run 0->446, etspread_passes 0->1, stuckcheck_noplan 0->421, stuckcheck_notstuck 0->30, stuckcheck_rearmed 0->13 | 0 | 2381.1/2194.1/1304.7 -> 2617.4/2526.3/871.2 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning e964c8edf2b1 not in DB (expected build/ts3fixes.package); noise: medium (GPU 3D others 21.5%); others on game CPUs 0.6%; clock avg/min 4297/4033 MHz; Correctness (14 min): repro ageup-swap-check. Pass: off special part hangs (Done False) and finishes after unstick; on special f |

## 2026-11-01T10:15 UTC, build c91b8ae, jobs bp-ageup-swap

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ageup-swap | 10:15 (18:15) | 17.3 | PASS | PASS: profile 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->7, owspread_types_run 0->140, stuckcheckasync_queued 0->369, stuckcheckasync_run 0->368, etspread_passes 0->1, stuckcheck_noplan 0->341, stuckcheck_notstuck 0->24, stuckcheck_rearmed 0->13, stuckcheck_reset 0->1 | 0 | 2253.4/2126.5/1354 -> 2704.4/2607.7/767.1 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 78% from 179%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 731e641def8d not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 1.5%; clock avg/min 3947/1870 MHz; Correctness (14 min): repro ageup-swap-check. Pass: of |
