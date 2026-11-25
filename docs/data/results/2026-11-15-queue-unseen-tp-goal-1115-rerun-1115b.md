# Queue unseen-tp-goal-1115-rerun-1115b 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-tp-goal-1115-rerun-1115b-2026-11-15.jsonl`.

## 2026-11-15T11:22 UTC, build d6a8dbd6, jobs bp-unseen-tp-goal-rerun

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-unseen-tp-goal-rerun | 11:22 (19:22) | 2.5 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>repro unseen-tp-goal-closed step reflect_call [the active lot: hidden room marker, one spot per floor-0 room, entry point; PASS]: {"id":38,"ok":true,"result":"goal: lot Air Spray - 2br, 2ba (6C1100337657B720), probe Lenny Shutter\n  ParkingSpace r19: refused (lot closed or special)\n  entry point outside: teleport ok\nN/A (no cl<br>repro unseen- | 1 | 2170.4/1894/1540.8 -> 2214.9/1925.2/1505.4 | - | timing: invalid (job VOID: runInvalid: profiler cut short (3 of 600 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise high); build: asi b75b706ac7c9 ts3native.asi.master-merge-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package.master-dalexlol-405d-1115/ts3fixes.package.master-merge-1 |

Stopped: bp-unseen-tp-goal-rerun failed (stopQueueOnFail): FAIL: profile 10 min, seed 12345 (rng-compare.ts).
