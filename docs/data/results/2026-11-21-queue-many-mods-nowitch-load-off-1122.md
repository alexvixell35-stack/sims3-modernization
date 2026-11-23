# Queue many-mods-nowitch-load-off-1122 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/many-mods-nowitch-load-off-1122-2026-11-21.jsonl`.

## 2026-11-21T16:32 UTC, build fdde90d3b, jobs ip-mms-load-off

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-mms-load-off | 16:32 (00:32) | 11.7 | **FAIL** | N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines and route hook state, counter gate and /native counters | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise medium; heavy tool samplers); noise: medium (other apps 16.2% of game CPUs); others on game CPUs 16.2%; clock avg/min 4126/4069 MHz; hang capture: 5 sim stacks, top frame TS3W.exe!FUN_00e4c3e0+0x8a (3/5 same); 129 other thread stacks; main thread under TS3W.exe!FUN_005887a0+0x29; thread frames 89 x TS3W.exe!FUN_004dae40+0xbe, 10 x - [cut: full row in the jsonl] |

Stopped: ip-mms-load-off failed (stopQueueOnFail): cycle.ts --save exit 1.
