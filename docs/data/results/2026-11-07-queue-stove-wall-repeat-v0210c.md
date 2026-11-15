# Queue stove-wall-repeat-v0210c 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-wall-repeat-v0210c-2026-11-07.jsonl`.

## 2026-11-07T12:20 UTC, build 9d2f744f, jobs stove-wall-repeat-v0210c

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat-v0210c | 12:20 (20:20) | 6.3 | PASS | PASS: profile stopped at 1 events after 2.9 min (off 0, on 1; 1 firings, 1 cycles; rule 1 events), cap 150 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->323, stuckcheckasync_run 0->286, etspread_passes 0->1, stuckcheck_noplan 0->271, stuckcheck_notstuck 0->2, stuckcheck_rearmed 0->10, stuckcheck_reset 0->3<br>PASS: r | 0 | 2128.6/1840.4/1596.9 -> 2287.9/1988/1416.6 | - | timing: invalid (noise medium; A/B 0 off / 1 on iterations, < 4 per arm (repro firings)); build: asi 43a719dabe02 v0210c ts3native 0.36.168d (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/asi-v029/ts3fixes.package); noise: medium (other apps 18.3% of game CPUs); ot |

## 2026-11-07T12:32 UTC, build ef4a8948, jobs stove-wall-repeat-v0210c

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat-v0210c | 12:32 (20:32) | 9.9 | PASS | PASS: profile stopped at 1 events after 6.6 min (off 0, on 1; 1 firings, 1 cycles; rule 1 events), cap 150 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->8, owspread_types_run 0->160, stuckcheckasync_queued 0->703, stuckcheckasync_run 0->692, etspread_passes 0->1, stuckcheck_noplan 0->670, stuckcheck_notstuck 0->13, stuckcheck_rearmed 0->20, stuckcheck_reset 0->3<br>PASS: | 0 | 2129.1/1830.4/1608.9 -> 2428.7/2238.3/1185.1 | - | timing: invalid (noise medium; A/B 0 off / 1 on iterations, < 4 per arm); build: asi 43a719dabe02 v0210c ts3native 0.36.168d (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/asi-v029/ts3fixes.package); noise: medium (other apps 21.1% of game CPUs); others on game CPU |
