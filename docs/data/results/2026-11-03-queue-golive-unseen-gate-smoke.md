# Queue golive-unseen-gate-smoke 2026-11-03

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/golive-unseen-gate-smoke-2026-11-03.jsonl`.

## 2026-11-03T02:05 UTC, build 54e5f01e, jobs bp-gug-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-03T03:13 UTC, build 6999f3ff, jobs bp-gug-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-gug-smoke | 03:13 (11:13) | 7.4 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_cost_us 0->431593, autunseen_guard_windows 0->16, autunseen_posted 0->508, autshadow_skips 0->21604, autshadow_gm4_methods 0->683, autunseen_tagged 0->41, autshadow_a0 0->467, autshadow_a4 0->44, autunseen_nopick 0->38, autshadow_gm4_us 0->146416, autunseen_test_pass 0->41, autunseen_saved_us 0->1345590, g8_null_picks | 0 | 2115.9/1871.1/1616.8 -> 2240.7/1966/1499.9 | - | noisy: does not count, rerun as bp-gug-smoke-rerun; timing: invalid (job VOID: runInvalid: noisy machine (others 24.2% of game CPUs); noise high); build: asi 1d1b6f224712 rel025c-asi ts3native 0.36.166 (log ok); ts3fixes tuning 1086f7220ea8 not in DB (expected game-copy/ts3fixes.package.rel025c-gu78); noise: high (other apps 26.2% of game CPUs); others on game CPUs 26.2%; clock avg/min 4092/3696 M |
| bp-gug-smoke-rerun | 03:20 (11:20) | 7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_cost_us 0->443609, autunseen_guard_windows 0->19, autunseen_posted 0->608, autshadow_skips 0->26054, autshadow_gm4_methods 0->683, autunseen_tagged 0->37, autshadow_a0 0->577, autshadow_a4 0->21, autunseen_nopick 0->46, autshadow_gm4_us 0->132727, autunseen_test_pass 0->37, autunseen_saved_us 0->1434497, g8_null_picks | 0 | 2125/1879/1608.9 -> 2269.4/2086.5/1357.6 | - | rerun of bp-gug-smoke: counts instead of it; timing: invalid (noise medium); build: asi 1d1b6f224712 rel025c-asi ts3native 0.36.166 (log ok); ts3fixes tuning 1086f7220ea8 not in DB (expected game-copy/ts3fixes.package.rel025c-gu78); noise: medium (other apps 17.7% of game CPUs); others on game CPUs 17.7%; clock avg/min 4119/4052 MHz; rerun of bp-gug-smoke (noisy machine): Smoke (5 min BP, --ab aut |
