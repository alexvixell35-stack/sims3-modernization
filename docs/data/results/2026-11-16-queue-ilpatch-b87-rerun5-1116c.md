# Queue ilpatch-b87-rerun5-1116c 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ilpatch-b87-rerun5-1116c-2026-11-16.jsonl`.

## 2026-11-16T20:04 UTC, build 48272eda5, jobs bp-ilp-b87-off, bp-ilp-b87-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ilp-b87-off | 20:04 (04:04) | 1.7 | **FAIL** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step ilp_scavenger_check failed: wrong outcome: expected throw, got ok {"kept_held":0,"removed":0,"tried":1,"kept_household_lot":0,"kept_inventory":0,"left":"gem 1, metal 0, insect 2","opportunity":"EP4_ScavengerHunt_1","found":3,"needs":["EP4_ScavengerHunt_1: Metal Any x0 (min 3, max 3, forced 6); Gem Any x0 (min 3, max 3, forced<br> | 1 | 1712.8/1625.3/1809.9 -> 1760/1666.5/1809.9 | - | timing: invalid (job VOID: runInvalid: profiler cut short (5 of 180 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise medium); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: m |

Paused after bp-ilp-b87-off (AI usage 5h - | 7d 98% (reset 14:00 PHT) | read 1 min ago); resume with `--from bp-ilp-b87-on`.
