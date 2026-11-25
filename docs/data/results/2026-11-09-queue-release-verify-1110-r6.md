# Queue release-verify-1110-r6 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/release-verify-1110-r6-2026-11-09.jsonl`.

## 2026-11-09T15:07 UTC, build 575cfac7, jobs bp-dfguard-release-r6

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dfguard-release-r6 | 15:07 (23:07) | 0.1 | **FAIL** | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked<br>FAIL: events not fired: log /^ts3fixes: dancefloor list: call #\d+ / 0 lines (want >= 3); log /^ts3native 0\.36\.238 / 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: UNKNOWN: no ts3native.log copy, log rules not checked; FAIL: events not fired: log /^ts3fixes: dancefloor list: ca; noise unknown); build: asi 3c80e76729dc ts3native.asi.rc1110c ts3native 0.36.238; ts3fixes tuning 30b5c07430db not in DB (expected ts3fixes.package.rc1110b); [r6 = rerun with setup maxS 180 in repro dance-together-floor (repros.json is harness data; t |

## 2026-11-09T15:07 UTC, build 575cfac7, jobs bp-dfguard-release-r6

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dfguard-release-r6 | 15:07 (23:07) | 0.1 | **FAIL** | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked<br>FAIL: events not fired: log /^ts3fixes: dancefloor list: call #\d+ / 0 lines (want >= 3); log /^ts3native 0\.36\.238 / 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: UNKNOWN: no ts3native.log copy, log rules not checked; FAIL: events not fired: log /^ts3fixes: dancefloor list: ca; noise unknown); build: asi 3c80e76729dc ts3native.asi.rc1110c ts3native 0.36.238; ts3fixes tuning 30b5c07430db not in DB (expected ts3fixes.package.rc1110b); [r6 = rerun with setup maxS 180 in repro dance-together-floor (repros.json is harness data; t |

## 2026-11-09T15:09 UTC, build 369060e1, jobs bp-dfguard-release-r6

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dfguard-release-r6 | 15:09 (23:09) | 16.5 | **FAIL** | FAIL: profile 15 min, seed 12345 (rng-compare.ts)<br>repro dance-together-floor step dance_together_floor [cycle 6 (phase A, pool members 1 with 2 on every floor): Sim setup on every floor (setup_sims via op place, AGENTS.md Repro Sim setup; realistic, only the waiting is skipped): queues cancelled, onto the floor with Teleport Me Here unless there, motives maxed, moodlets cleared, autonomy off (N | 1 | 2178.4/1863/1576.8 -> 2226.1/2022.1/1393.6 | - | timing: invalid (job FAIL: FAIL: profile 15 min, seed 12345 (rng-compare.ts); FAIL: repro step dance_together_floor failed: dance_together_floor fi; noise medium); build: asi 3c80e76729dc ts3native.asi.rc1110c ts3native 0.36.238 (log ok); ts3fixes tuning 1f88237bac75 ts3fixes.package.rc1110b (expected game-copy/ts3fixes.package.rc1110b); noise: medium (other apps 32.2% of game CPUs); others on gam |
