# Queue stove-wall-repeat-v0210e 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-wall-repeat-v0210e-2026-11-07.jsonl`.

## 2026-11-07T13:03 UTC, build 63c494cb, jobs stove-wall-repeat-v0210e

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat-v0210e | 13:03 (21:03) | 20.5 | PASS | PASS: profile stopped at 1 events after 17.0 min (off 0, on 1; 1 firings, 1 cycles; rule 1 events), cap 150 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->21, owspread_types_run 0->420, stuckcheckasync_queued 0->1927, stuckcheckasync_run 0->1792, etspread_passes 0->1, stuckcheck_noplan 0->1737, stuckcheck_notstuck 0->104, stuckcheck_rearmed 0->83, stuckcheck_reset 0->9<br | 0 | 2129.2/1842.8/1612.9 -> 2438.1/2242.2/1146.8 | - | timing: invalid (noise medium; A/B 0 off / 1 on iterations, < 4 per arm (repro firings)); build: asi 32fe95a0a467 v0210e ts3native 0.36.168e (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/asi-v029/ts3fixes.package); noise: medium (other apps 17.3% of game CPUs); ot |

## 2026-11-07T13:27 UTC, build d1e3bbc7, jobs stove-wall-repeat-v0210e

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat-v0210e | 13:27 (21:27) | 32.4 | STOPPED at cycle 50 (auto-stop, mix orders not run, Rodney) | no permanent failure, no crash. 47 of 50 cycles reached BakingDish_CookInOven (not: 14 transient, 22 no use, 31 transient); ate and cleaned up >= 14 (steps cut at 300 chars); transients 2 (0/0/0, both busy), no use 1, walk-aways 0 seen, permanent fails 0. fpLost 0, fpLoad 0, fpSaved 558; removal first mode 1 parked 607 capped 0 max ticks 19. routefail 289 lines, 287 household (NoPath 32, PortalLocked 35, NoDestinationSpecified 104, PlannerAborted 2, 114 unclassified). 4 first-chance C0000005 at startup (spfileq x2, setupapi, frida-agent), none ours | - | game killed by ts3-track at the cycle 50 state line (21:59:59) | - | no end line (the stop killed the run); numbers read from the log `probe-out/queue/stove-wall-repeat-v0210e-2026-11-07-2127-ts3native.log` (copied from Bin, git-ignored). Pair: asi v0210e 32fe95a0a467 (0.36.168e, RemovalFirst=1), ini ts3native.ini.v0210e, ts3fixes v0.2.9 package; agent with the cycle 26 fix (permanent only with the Sim free on two rechecks 30 s apart). |
