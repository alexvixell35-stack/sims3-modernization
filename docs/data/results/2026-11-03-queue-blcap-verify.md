# Queue blcap-verify 2026-11-03

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/blcap-verify-2026-11-03.jsonl`.

## 2026-11-03T08:51 UTC, build 78b3cd52, jobs bp-blcap-reload-win, bp-blcap-reload-off, bp-blcap-travel, bp-blcap-play, ip-blcap-smoke-1, ip-blcap-smoke-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-blcap-reload-win | 08:51 (16:51) | 0 | **FAIL** | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked<br>FAIL: events not fired: log /ts3native 0.36.168/ 0 lines (want >= 1); log /^OK   GC black-list spacing cap: 0x00E4A856/ 0 lines (want >= 1); log /^gc blcap: restored 8 MB clamp at world load #/ 0 lines (want >= 7); log /^gc blcap: cap lenient again at world quit/ 0 lines (want >= 6) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: UNKNOWN: no ts3native.log copy, log rules not checked; FAIL: events not fired: log /ts3native 0.36.168/ 0 lines (w; noise unknown; correctness-only tool ProbeSnap.CallSites); Start-up smoke + memory stress, window arm. Baseline-BP, cap 1024, [MemDiag] marks. save-reload.ts: 2 min play (CAS + Buy visit), save as SaveReload-BP, then 6 menu-route reloads (save, main m |

Stopped: bp-blcap-reload-win failed (stopQueueOnFail): cycle.ts --restart exit 1.

## 2026-11-03T08:51 UTC, build 78b3cd52, jobs bp-blcap-reload-win, bp-blcap-reload-off, bp-blcap-travel, bp-blcap-play, ip-blcap-smoke-1, ip-blcap-smoke-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-blcap-reload-win | 08:51 (16:51) | 5.8 | **FAIL** | FAIL: Error: save normal: native result 0<br>FAIL: C:\Users\<user>\IdeaProjects\sims-3-modernization\.claude\worktrees\agent-a788a176bffe5762b\probe-out\save-reload\BP-play-2026-11-03T0853Z.json<br>FAIL: events not fired: log /^gc blcap: restored 8 MB clamp at world load #/ 1 lines (want >= 7); log /^gc blcap: cap lenient again at world quit/ 0 lines (want >= 6)<br>PASS: no forbidden log lines | 1 | 2191.6/1847/1564.7 -> 2202.3/1889.4/1516.4 | - | timing: invalid (job FAIL: FAIL: Error: save normal: native result 0; FAIL: C:\Users\<user>\IdeaProjects\sims-3-modernization\.claude\worktrees\agen; noise medium; correctness-only tool ProbeSnap.CallSites); build: asi a7a77b3ddd7f blcap-asi ts3native 0.36.168 (log ok); ts3fixes tuning b779b0d14a03 rel025c-fixes/blcap-fixes (expected build/ts3fixes.package); noise: medium (other apps 9.5% of game C |

Stopped: bp-blcap-reload-win failed (stopQueueOnFail): FAIL: Error: save normal: native result 0.

## 2026-11-03T08:59 UTC, build 9292952a, jobs bp-blcap-reload-win, bp-blcap-reload-off, bp-blcap-travel, bp-blcap-play, ip-blcap-smoke-1, ip-blcap-smoke-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-blcap-reload-win | 08:59 (16:59) | 16.5 | PASS | PASS: C:\Users\<user>\IdeaProjects\sims-3-modernization\.claude\worktrees\agent-a788a176bffe5762b\probe-out\save-reload\BP-play-2026-11-03T0900Z.json<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2199.3/1849/1560.8 -> 2667.1/2402/993.1 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi a7a77b3ddd7f blcap-asi ts3native 0.36.168 (log ok); ts3fixes tuning b779b0d14a03 rel025c-fixes/blcap-fixes (expected build/ts3fixes.package); noise: medium (other apps 10.5% of game CPUs); others on game CPUs 10.5%; clock avg/min 4238/4187 MHz; Start-up smoke + memory stress, window arm. Baseline-BP, cap 1024, [M |

Paused after bp-blcap-reload-win (AI usage 5h - | 7d 95% (reset 14:00 PHT) | read 4 min ago); resume with `--from bp-blcap-reload-off`.
