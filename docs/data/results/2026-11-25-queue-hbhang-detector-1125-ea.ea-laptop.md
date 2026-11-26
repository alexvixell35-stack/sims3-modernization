# Queue hbhang-detector-1125-ea 2026-11-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hbhang-detector-1125-ea-2026-11-25.jsonl`.

Machine: ea-laptop

## 2026-11-25T04:13 UTC, build cadfddef, jobs ip-hbhang-detector-run1, ip-hbhang-detector-run2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ip-hbhang-detector-run1 | 04:13 (12:13) | 2.6 | **FAIL** | PASS: repro events fired | - | ? -> ? | - | rerun of ip-ubrel-hbstart-repro-rc1119: noisy again, neither counts; timing: invalid (job FAIL: exit code ?; noise high; correctness-only tool ProbeSnap.CallSites); build: asi b5accc90000f ts3native-ea169.asi.eam1124d2hbdet ts3native 0.36.305-eam1124d2hbdet [cut: full row in the jsonl]<br>arm ms/s not read: Error: ENOENT: no such file or directory, scandir '<repo>\probe-out\profile' | ea-laptop |
| ip-hbhang-detector-run2 | 04:16 (12:16) | 14 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenboatstart_shortcuts 0->1, unseenboat_rj_route 0->238, unseenboat_max_hold_min 0->106, unseenboat_rj_goal 0->20, unseenboat_released_seen 0->23, unseenboatgenboat_ignored 0->14, unseenboat_candidates 0->343, unseenboatstart_candidates 0->99, unseenboat_seen 1->4, unseenboat_rj_owned 0->2, unseenboat_houseboat 0->1 [cut: full row in the jsonl] | 0 | 2019.4/1683.7/1808.8 -> 2026.5/1712/1794.1 | asyncStarted +4558, asyncRefused +218, asyncCollected +4546, asyncDropped +12, asyncUs +111643128, asyncMaxUs +447232, asyncPumpUs +11374159, asyncPumpWaits +2897, asyncRefusedM1Pending +45, asyncBackoffs +7, asyncQueueMax +5 | rerun of ip-ubrel-hbstart-repro-rc1119: counts instead of it; timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi b5accc90000f ts3native-ea169.asi.eam1124d2hbdet ts3native 0.36.305-eam1124d2hbdet (log ok) [cut: full row in the jsonl] | ea-laptop |
