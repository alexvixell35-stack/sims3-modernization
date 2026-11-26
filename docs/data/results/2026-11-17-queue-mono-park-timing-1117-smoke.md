# Queue mono-park-timing-1117-smoke 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-park-timing-1117-smoke-2026-11-17.jsonl`.

## 2026-11-17T23:18 UTC, build af05e7a58, jobs bp-upark-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-upark-smoke | 23:18 (07:18) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseen_park_arrivals 0->174, unseen_park_exempt 0->1823, gohome_no_spot 0->12, unseen_park_seen 0->160, unseen_park_burst_max 0->3, gohome_runs 0->579, unseen_park_held 0->181816, unseen_park_snap 0->457, unseen_park_stagger_defer 0->814, gohome_shortcuts 0->164, unseen_park_unseen 0->2890, owchain_alarms 0->4 [cut: full row in the jsonl] | 0 | 1746.3/1626.2/1752 -> 3005.1/2774.5/464.1 | - | timing: invalid (noise medium); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c/ts3fixes.package.envlist-bcpos-1116 (expected game-copy/ts3fixes.package.master-1116c); noise: medium (other apps 26.4% of game CPUs); others on game CPUs 26.4%; clock avg/min 4081/3819 MHz [cut: full row in the jsonl]<br>sim ms/s 201.1; p99 91 ms, p99.9 150, max 270, >=300 0, >=1000 0 (6.0 min) |
