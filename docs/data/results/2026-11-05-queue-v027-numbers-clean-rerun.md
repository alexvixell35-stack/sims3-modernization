# Queue v027-numbers-clean-rerun 2026-11-05

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v027-numbers-clean-rerun-2026-11-05.jsonl`.

## 2026-11-05T22:03 UTC, build 51eb1159, jobs bp-baseline-clean-r1, bp-v027-clean-r1, bp-v027-clean-r2, bp-baseline-clean-r2, ip-v027-clean-r2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-baseline-clean-r1 | 22:03 (06:03) | 12.1 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines and route hook state, counter gate and /native counters<br>WARN: 3 safe-default click(s): Nice! -> #3 cancel  | 0 | 1841.7/1593.9/1836.9 -> 1980.1/1859.2/1565.2 | - | timing: invalid (noise medium); noise: medium (other apps 11.2% of game CPUs); others on game CPUs 11.2%; clock avg/min 4236/4147 MHz; rerun: Clean numbers, BP Baseline run 1 |
| bp-v027-clean-r1 | 22:16 (06:16) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->260, stuckcheckasync_run 0->260, etspread_passes 0->1, stuckcheck_noplan 0->213, stuckcheck_notstuck 0->34, stuckcheck_rearmed 0->29, stuckcheck_reset 0->5 | 0 | 2140.1/1843/1611.4 -> 2252.5/2072.2/1364.8 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 12.5% of game CPUs); others on game CPUs 12.5%; clock avg/min 4220/4149 MHz; rerun: Clean numbers, BP v0.2.7 run 1 |
