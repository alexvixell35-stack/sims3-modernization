# Queue v025-speed-ab 2026-11-04

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v025-speed-ab-2026-11-04.jsonl`.

## 2026-11-04T01:06 UTC, build 620bc796, jobs bp-v024-a1, bp-rel025d-b1, bp-rel025d-b2, bp-v024-a2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v024-a1 | 01:06 (09:06) | 14.1 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->316, stuckcheckasync_run 0->315, etspread_passes 0->1, stuckcheck_noplan 0->278, stuckcheck_notstuck 0->16, stuckcheck_rearmed 0->28, stuckcheck_reset 0->1 | 0 | 2141.8/1842.3/1548.9 -> 2264.2/2091.4/1259.6 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1/ts3fixes.0.2.2/ts3fixes.0.2.3 (expected game-copy/asi-v024/ts3fixes.package); noise: medium (other apps 10.1% of game CPUs); others on game CPUs 10.1%; clock avg/min 4239/4192 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, s |
| bp-rel025d-b1 | 01:20 (09:20) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->286, stuckcheckasync_run 0->286, etspread_passes 0->1, stuckcheck_noplan 0->230, stuckcheck_notstuck 0->24, stuckcheck_rearmed 0->28, stuckcheck_reset 0->7 | 0 | 2129.5/1824.7/1633.6 -> 2246.1/2052.3/1378.6 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes (expected game-copy/ts3fixes.package.rel025d); noise: medium (other apps 10.1% of game CPUs); others on game CPUs 10.1%; clock avg/min 4241/4201 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, seed 12345, light tools only, NO ini overrides (Foregroun |
| bp-rel025d-b2 | 01:34 (09:34) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->282, stuckcheckasync_run 0->281, etspread_passes 0->1, stuckcheck_noplan 0->225, stuckcheck_notstuck 0->25, stuckcheck_rearmed 0->26, stuckcheck_reset 0->9 | 0 | 2148.8/1830.5/1628.9 -> 2328.9/2120.4/1327.9 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes (expected game-copy/ts3fixes.package.rel025d); noise: medium (other apps 9.4% of game CPUs); others on game CPUs 9.4%; clock avg/min 4223/4150 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, seed 12345, light tools only, NO ini overrides (ForegroundR |
| bp-v024-a2 | 01:47 (09:47) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->294, stuckcheckasync_run 0->294, etspread_passes 0->1, stuckcheck_noplan 0->233, stuckcheck_notstuck 0->36, stuckcheck_rearmed 0->33, stuckcheck_reset 0->7 | 0 | 2149.1/1848.9/1532.9 -> 2269.5/2084.9/1307.3 | - | timing: invalid (noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 95963744a24e ts3fixes.0.2.0/ts3fixes.0.2.1/ts3fixes.0.2.2/ts3fixes.0.2.3 (expected game-copy/asi-v024/ts3fixes.package); noise: medium (other apps 9.7% of game CPUs); others on game CPUs 9.7%; clock avg/min 4234/4081 MHz; v0.2.4 vs v0.2.5 speed ABBA, BP 12 min natural play, see |

### Verdict (release-numbers.ts / hitch-ab.ts 'all' arm; per-phase ms/s = seconds.csv means after the first 60 s)

| run | it/s | p99 ms | worst ms | >=300 ms /10 min | script | route | taskObjects | animPose | locomotion |
|---|---|---|---|---|---|---|---|---|---|
| bp-v024-a1 | 46.2 | 87 | 1062 | 2.5 | 12.2 | 33.8 | 73.3 | 59.3 | 77.4 |
| bp-rel025d-b1 | 38.6 | 99 | 503 | 1.4 | 11.5 | 41.2 | 138.4 | 51.6 | 79.8 |
| bp-rel025d-b2 | 40.6 | 94 | 292 | 0.0 | 11.0 | 36.4 | 137.9 | 55.9 | 73.1 |
| bp-v024-a2 | 45.0 | 90 | 542 | 1.3 | 12.2 | 34.7 | 70.7 | 59.8 | 78.5 |
| pooled v024 | 45.6 | 88 | 1062 | 1.9 | | | | | |
| pooled rel025d | 39.6 | 97 | 503 | 0.7 | | | | | |

rel025d -13% it/s pooled, both rel025d runs below both v024 runs (all four runs noise medium, other apps ~10% of game CPUs, same for both arms): clearly slower. Where the time goes: TaskObjects (MonoScriptHost TaskObjects seam 0xD7FC60, the managed task scheduler) +66 ms/s (72 -> 138); route +4.6; animPose -5.8 (UnseenAnim gain); script and locomotion flat. Per the verdict rule root motion goes off in the release ini (rel025d-ini2); confirm A/B in v025-speed-ab-rmoff.
