# Queue f1-callpath-m1110 2026-11-10

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/f1-callpath-m1110-2026-11-10.jsonl`.

## 2026-11-10T01:08 UTC, build c2bb5ed, jobs ip-f1-mintcall-ab, ip-f1-mintcall-ab-onfirst

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-f1-mintcall-ab | 01:08 (09:08) | 12.5 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: fixed_work failed twice in a row: RPCException: Error: previous request still pending (agent package without the command?) | 1 | 2276.7/1987.9/1528 -> 2336.3/2111.8/1344.7 | - | timing: invalid (job VOID: runInvalid: profiler cut short (329 of 600 s); crash or abort (no "done; game alive"); A/B iterations unknown); build: asi bf3e9d8c9fc8 not in DB ts3native 0.36.239 (log ok); ts3fixes tuning 7c3921b1573a not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 1.0%; clock avg/min 4732/4626 MHz; F1 in-load A/B, IP 10 min, seed 12345, off first: --ab mi |
| ip-f1-mintcall-ab-onfirst | 01:21 (09:21) | 16.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->381, stuckcheckasync_run 0->380, etspread_passes 0->1, stuckcheck_noplan 0->304, stuckcheck_notstuck 0->13, stuckcheck_rearmed 0->50, stuckcheck_reset 0->14 | 0 | 2273.2/2029.4/1494.2 -> 2332.8/2088.9/1355.4 | - | timing: valid-half; build: asi bf3e9d8c9fc8 not in DB ts3native 0.36.239 (log ok); ts3fixes tuning 7c3921b1573a not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 0.5%; clock avg/min 4774/4673 MHz; F1 in-load A/B, IP 10 min, on first. Pool both orders. |
