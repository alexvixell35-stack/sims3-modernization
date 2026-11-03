# Queue ufc-sites-verify 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ufc-sites-verify-2026-10-30.jsonl`.

## 2026-10-30T08:04 UTC, build 69c2d490, jobs sv-ufc-buildbuy

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-ufc-buildbuy | 08:04 (16:04) | 14.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbtrip_runs 0->238, unseenfbpre_rj_task 0->56, unseenfbpre_rj_vehicle 0->914, unseenfb_seen 11->684, unseenfbsites_ids 0->18530, unseenfbpre_rj_caller 0->2, unseenfbpre_rj_townie 0->17, unseenfb_stuck_deferred 0->2389, unseenfbpre_rj_short 0->1961, unseenfbpre_rj_lot 0->4, unseenfb_marked 0->3168, unseenfbsites_lists 0-> | 0 | 2151.9/1802.4/1564.9 -> 2232/1869.4/1462.2 | - | timing: invalid (noise medium); build: asi d58954012dd4 not in DB ts3native 0.36.117 (log ok); ts3fixes tuning 94c60769ef8f not in DB (expected build/ts3fixes.package); noise: medium (other apps 4.5% of game CPUs); others on game CPUs 4.5%; clock avg/min 4247/4188 MHz; Not outside live mode (docs/202 §6): bb-cc-short (Build/Buy placement cycles on the home lot, short form of bb-cc-cycle) with Site |
