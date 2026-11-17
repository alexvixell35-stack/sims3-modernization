# Queue hhsearch-sv-1116 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hhsearch-sv-1116-2026-11-16.jsonl`.

## 2026-11-16T19:39 UTC, build 76142a409, jobs sv-hhsearch, bp-hhsearch

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-hhsearch | 19:39 (03:39) | 4.3 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>FAIL: self check: ts3native hook failed, run void (FAIL door memo x: a memo answer differed from the live ask in mode 1: DoorMemoX off for this session) for ~60 s (fail-fast guard); child stopped<br>ts3fixes activity: hhsearch_pets 0->1, aut_l0_pairn_seen 0->16, hhsearch_why_5to10 0->26, aut_l0_n_seen 0->16, hhsearch_gap_lt10s 0->25 [cut: full row in the jsonl] | 1 | 1699.5/1592.4/1830.5 -> 1767.4/1673.1/1794.8 | - | timing: invalid (job VOID: FAIL: self check: ts3native hook failed, run void (FAIL door memo x: a memo answer differed from the live ask in mode 1:; noise medium); build: asi 17a2fcf3b5a3 not in DB ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning 3719648bbb6a ts3fixes.package.hoursmemo-svprobe-1116 (expected game-copy/ts3fixes.package.hoursmemo-svprobe-1116) [cut: full row in the jsonl]<br>sim ms/s 116.1; p99 50 ms, p99.9 106, max 202, >=300 0, >=1000 0 (1.2 min) |

Paused after sv-hhsearch (AI usage 5h - | 7d 98% (reset 14:00 PHT) | read 4 min ago); resume with `--from bp-hhsearch`.
