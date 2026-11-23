# Queue compat-rules-proof-1122 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-rules-proof-1122-2026-11-23.jsonl`.

## 2026-11-23T00:12 UTC, build 62e57c616, jobs ip-rules-r-resolve-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-rules-r-resolve-on | 00:13 (08:13) | 12.8 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->280, stuckcheckasync_run 0->280, etspread_passes 1->1, stuckcheck_noplan 0->277, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->3, stuckcheck_reset 0->1 [cut: full row in the jsonl] | 0 | 1736.2/1641.7/1762.4 -> 1820.1/1719.7/1710.4 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^ts3fixes: compat precedence: cleanupvfx on Sims3\.Store\.Objects\.WoodFireOven\+WOBake\.Cl; noise medium); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 9800d38a5331 ts3fixes.package.compat-resolve-1122f (expected game-copy/ts3fixes.package.compat-resolve-1122f) [cut: full row in the jsonl]<br>sim ms/s 159.2; p99 89 ms, p99.9 222, max 539, >=300 8, >=1000 0 (7.1 min) |

Stopped: ip-rules-r-resolve-on failed (stopQueueOnFail): FAIL: events not fired: log /^ts3fixes: compat precedence: cleanupvfx on Sims3\.Store\.Objects\.WoodFireOven\+WOBake\.Cleanup \(StoreObjectsWoodFireOven \w+\): ok$/ 0 lines (want >= 1); log /^ts3fixes: compat precedence: cleanupvfx on Sims3\.Store\.Objects\.TeppanyakiGrill\+TGCook\.Cleanup \(StoreObjectsTeppanyakiGrill \w+\): ok$/ 0 lines (want >= 1); log /^ts3fixes: compat precedence: cleanupvfx .
