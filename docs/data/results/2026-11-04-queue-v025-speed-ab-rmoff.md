# Queue v025-speed-ab-rmoff 2026-11-04

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v025-speed-ab-rmoff-2026-11-04.jsonl`.

## 2026-11-04T02:04 UTC, build 0adc3107, jobs bp-v024c-a1, bp-rmoff-b1, bp-rmoff-b2, bp-v024c-a2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v024c-a1 | 02:04 (10:04) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->300, stuckcheckasync_run 0->300, etspread_passes 0->1, stuckcheck_noplan 0->241, stuckcheck_notstuck 0->28, stuckcheck_rearmed 0->28, stuckcheck_reset 0->11 | 0 | 2147.4/1843/1564.9 -> 2293.5/2100.7/1299.4 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1/ts3fixes.0.2.2/ts3fixes.0.2.3 (expected game-copy/asi-v024/ts3fixes.package); noise: medium (other apps 9.3% of game CPUs); others on game CPUs 9.3%; clock avg/min 4240/4194 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, see |
| bp-rmoff-b1 | 02:18 (10:18) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->272, stuckcheckasync_run 0->272, etspread_passes 0->1, stuckcheck_noplan 0->223, stuckcheck_notstuck 0->19, stuckcheck_rearmed 0->33, stuckcheck_reset 0->4 | 0 | 2142.9/1827.9/1632.2 -> 2264.4/2089.5/1357.4 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes (expected game-copy/ts3fixes.package.rel025d); noise: medium (other apps 9.4% of game CPUs); others on game CPUs 9.4%; clock avg/min 4233/4205 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, seed 12345, light tools only, NO ini overrides (ForegroundR |
| bp-rmoff-b2 | 02:32 (10:32) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->286, stuckcheckasync_run 0->286, etspread_passes 0->1, stuckcheck_noplan 0->228, stuckcheck_notstuck 0->24, stuckcheck_rearmed 0->31, stuckcheck_reset 0->8 | 0 | 2148.9/1828.8/1638.2 -> 2293.6/2107/1340.6 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes (expected game-copy/ts3fixes.package.rel025d); noise: medium (other apps 10.6% of game CPUs); others on game CPUs 10.6%; clock avg/min 4235/4202 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, seed 12345, light tools only, NO ini overrides (Foregroun |
| bp-v024c-a2 | 02:45 (10:45) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->300, stuckcheckasync_run 0->299, etspread_passes 0->1, stuckcheck_noplan 0->257, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->21, stuckcheck_reset 0->4 | 0 | 2151.9/1842.3/1548.9 -> 2240.6/2078.7/1299.4 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1/ts3fixes.0.2.2/ts3fixes.0.2.3 (expected game-copy/asi-v024/ts3fixes.package); noise: medium (other apps 10.2% of game CPUs); others on game CPUs 10.2%; clock avg/min 4237/4175 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, s |

### Verdict (release-numbers.ts / hitch-ab.ts 'all' arm; per-phase ms/s = seconds.csv means after the first 60 s)

| run | it/s | p99 ms | worst ms | >=300 ms /10 min | script | route | taskObjects | animPose | locomotion |
|---|---|---|---|---|---|---|---|---|---|
| bp-v024c-a1 | 43.7 | 88 | 387 | 1.3 | 12.1 | 37.3 | 73.4 | 59.1 | 80.2 |
| bp-rmoff-b1 | 39.4 | 95 | 827 | 4.1 | 10.8 | 39.1 | 137.3 | 55.8 | 78.8 |
| bp-rmoff-b2 | 37.7 | 98 | 280 | 0.0 | 11.5 | 36.8 | 144.0 | 54.4 | 79.9 |
| bp-v024c-a2 | 44.5 | 87 | 906 | 3.6 | 11.7 | 38.9 | 69.9 | 58.4 | 85.1 |
| pooled v024 | 44.1 | 88 | 906 | 2.5 | | | | | |
| pooled rel025d RM off | 38.6 | 96 | 827 | 2.0 | | | | | |

rmoff_b1/b2: rmReady 0, rmMode 0 (root motion really off). Still -12.5% it/s, both rmoff runs below both v024 runs: root motion is not the cause. The gap is TaskObjects (MonoScriptHost TaskObjects seam 0xD7FC60) +69 ms/s (72 -> 141), the same as with root motion on. Stopped here per Rodney's rule (no other feature disabled); release re-measure (step 3) not run.
