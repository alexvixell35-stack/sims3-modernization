# Queue release-verify-rc1119-rest-m1124d2-ea 2026-11-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/release-verify-rc1119-rest-m1124d2-ea-2026-11-24.jsonl`.

Machine: ea-laptop

## 2026-11-24T16:43 UTC, build 5516fb5a, jobs ip-natural-combined-rc1119, bookcarry-crowd-check-rc1119, bp-outfitwait-dead-rc1119

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ip-natural-combined-rc1119 | 16:43 (00:43) | 31.7 | **FAIL** | PASS (soft tries): unseenboatstart 10 of 15 tries; recent: Craig Foster => Plaza Park (gotocommunity from Bella Vista Beach) / Yelena Ojeda => route goal (unseenboat, 479 m, held 65 sim-min) / Rajan Patel -> Spirit of the Sea - 2br [cut: full row in the jsonl] | 0 | 1966.7/1656.5/1852.9 -> 2044.4/1725.4/1755.5 | asyncStarted +12102, asyncRefused +501, asyncCollected +12100, asyncDropped +2, asyncUs +259128732, asyncMaxUs +588717, asyncPumpUs +26340227, asyncPumpWaits +7671, asyncRefusedM1Pending +59, asyncBackoffs +31, asyncQueueMax +7 | timing: invalid (job FAIL: FAIL: events not fired: arWdPass=0 (want >=1); noise medium; correctness-only tool ProbeSnap.CallSites); build: asi ef19c1b510a1 ts3native-ea169.asi.eam1124d2 ts3native 0.36.305-eam1124d2 (log ok) [cut: full row in the jsonl] | ea-laptop |
| bookcarry-crowd-check-rc1119 | 17:15 (01:15) | 14 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->225, stuckcheckasync_run 0->225, stuckcheck_noplan 0->177, stuckcheck_notstuck 0->20, stuckcheck_rearmed 0->21, stuckcheck_reset 0->7; ts3fixes errortrapspread: stood down (errortrapspread: ErrorTrap not loaded, off)<br>PASS: repro events fired [cut: full row in the jsonl] | 0 | 2066.9/1725.7/1778 -> 2125.5/1824.1/1655.8 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi ef19c1b510a1 ts3native-ea169.asi.eam1124d2 ts3native 0.36.305-eam1124d2 (log ok); ts3fixes tuning b7018ac0bf65 ts3fixes.package.m1124b/ts3fixes.package.eam1124b/ts3fixes.package.m1124c/ts3fixes.package.m1124d/ts3fixes.package.eam1124d2 (expected game-copy/eam1124d2/ts3fixes.package) [cut: full row in the jsonl] | ea-laptop |
