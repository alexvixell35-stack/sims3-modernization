# Queue events-batch-1 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/events-batch-1-2026-11-01.jsonl`.

## 2026-11-01T12:11 UTC, build d1d4525, jobs bp-events-batch-1-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-events-batch-1-check | 12:11 (20:11) | 0.1 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown); Correctness (5 min, Bridgeport test save): repros photo-session-check, egg-spot-check, graduation-table-check. Pass: every step ok (each arm's check throws on a wrong outcome) and the on-arm counters went up. |

Stopped: bp-events-batch-1-check failed (stopQueueOnFail): cycle.ts --restart exit 1.

## 2026-11-01T12:12 UTC, build d1d4525, jobs bp-events-batch-1-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-events-batch-1-check | 12:12 (20:12) | 9.8 | **FAIL** | PASS: photo-session-check: profile 5 min, first window after load | - | 2264.5/2142.2/1342.8 -> ? | - | timing: invalid (job FAIL: exit code ?; photo-session-check: job VOID: runInvalid: noisy machine (other CPU 18.7% >= 10%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105; ts3fixes tuning daeecb3b4962 not in DB (expected build/ts3fixes.package); noise: high (other apps 15.9% of game CPUs, NOT comparable (load or clock), GPU 3D others 51.0%); others on game CPUs 15.9%;  |

Stopped: bp-events-batch-1-check failed (stopQueueOnFail): reload: cycle.ts --save exit 1; early gate: the photosession on arm ended no session: the hook is not installed or the repro did not run (fixes:photosession_ended=0) [want >=2 from 180 s].
