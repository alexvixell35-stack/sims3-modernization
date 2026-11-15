# Queue save-reload-memory-master 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/save-reload-memory-master-2026-11-07.jsonl`.

## 2026-11-07T14:43 UTC, build 661bb96f, jobs bp-save-reload

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-save-reload | 14:43 (22:43) | 49.1 | **FAIL** | PASS: C:\Users\<user>\IdeaProjects\sims-3-modernization\probe-out\save-reload\BP-play-2026-11-07T1445Z.json<br>FAIL: events not fired: log /^save: end #\d+ result 1 / 0 lines (want >= 3)<br>PASS: no forbidden log lines | 0 | 2154.6/1842.2/1606.3 -> 2514.1/2352.6/1095.6 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^save: end #\d+ result 1 / 0 lines (want >= 3); noise medium); build: asi e6b48f5aa4e9 master0189 ts3native 0.36.189 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189 (expected build/ts3fixes.package); noise: medium (other apps 18.7% of game CPUs); others on game CPUs 18.7 |

## 2026-11-07T15:33 UTC, build fd1a7aa7, jobs bp-save-reload-control

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
