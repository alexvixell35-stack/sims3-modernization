# Queue golive-unseen-u4b 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/golive-unseen-u4b-2026-11-01.jsonl`.

## 2026-11-01T12:52 UTC, build 22fe4043, jobs ip-gu-dry-defcmp-u4-rhoff2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-gu-dry-defcmp-u4-rhoff2 | 14:09 (22:09) | 7.2 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: early gate: no rabbit-hole pair skipped by the compare: autunseenrh not in the package (unseen4?) (fixes:autunseen_rh_game=0) [want >0 from 300 s]; child stopped<br>ts3fixes activity: autunseen_dry_top10 0->1241, autunseen_cmp_obj_skipped 0->4133, autshadow_skips 0->77249, autshadow_gm4_methods 0->683, autshadow_a0 0->1712, autshadow_a4 0- | 1 | 2175.1/1873.9/1580.9 -> 2173.4/1891.7/1547.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (322 of 600 s); crash or abort (no "done; game alive"); noise medium); build: asi c0a4235076c2 not in DB ts3native 0.36.148 (log ok); ts3fixes tuning 891a5ee6b476 unseen4 (expected game-copy/ts3fixes.package.unseen4); noise: medium (other apps 11.3% of game CPUs); others on game CPUs 11.3%; clock avg/min 4233/4195 MHz; RERUN of ip-gu-dry-de |

Stopped: ip-gu-dry-defcmp-u4-rhoff2 failed (stopQueueOnFail): early gate: no rabbit-hole pair skipped by the compare: autunseenrh not in the package (unseen4?) (fixes:autunseen_rh_game=0) [want >0 from 300 s].

## 2026-11-01T17:14 UTC, build 3816a66b, jobs ip-gu-dry-defcmp-u4-rhoff2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-gu-dry-defcmp-u4-rhoff2 | 17:14 (01:14) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: autunseen_dry_top10 0->2603, autunseen_cmp_obj_skipped 0->9116, autshadow_skips 0->157347, autshadow_gm4_methods 0->683, autshadow_a0 0->3367, autshadow_a4 0->245, autshadow_gm4_us 0->123923, autunseen_cmp_late_mis 0->403, autunseen_cmp_wpgf_w0 0->10335, g8_null_picks 0->3918, autunseen_cmp_pairs 0->139047, g8_simhours_x100 0- | 0 | 2167.5/1857/1596.9 -> 2183.8/1884.2/1539.3 | - | timing: invalid (noise medium); build: asi c0a4235076c2 not in DB ts3native 0.36.148 (log ok); ts3fixes tuning 891a5ee6b476 unseen4 (expected game-copy/ts3fixes.package.unseen4); noise: medium (other apps 6.5% of game CPUs); others on game CPUs 6.5%; clock avg/min 4250/4219 MHz; RERUN of ip-gu-dry-defcmp-u4-rhoff with profile.fixesOff autunseenrh actually passed (queue.ts ignored the job-level key |
