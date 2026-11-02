# Queue ilpatch-smoke-local 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ilpatch-smoke-local-2026-11-02.jsonl`.

## 2026-11-02T08:48 UTC, build 7520d73, jobs bp-ilpatch-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ilpatch-smoke | 08:48 (16:48) | 3.9 | **CRASH** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step method_touch failed: InvalidOperationError: script has been destroyed<br>FAIL: events not fired: log /^il patch: b89-fixup-no-inventory applied to Sims3\.Gameplay\.CAS\.SimDescription::Fixup\(\) \(ea-1\.67, 1445 -> / 0 lines (want >= 1); log /^il patch: b80-remote-chat-no-everyday-outfit applied to Sims3\.Gameplay\.Actors\.Sim::D | 1 | 2292.4/2155/1350.8 -> undefined/undefined/undefined | - | timing: invalid (job VOID: runInvalid: profiler cut short (29 of 300 s); noisy machine (machine load: no samples); crash or abort (no "done; game a; noise high); build: asi b4ad7951affa not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 0d19ef086257 not in DB (expected build/ts3fixes.package); noise: high (GPU 3D others 62.7%); others on game CPUs 2.5%; clock avg/min 4441/4322 MHz; Load (b89 a |

Stopped: bp-ilpatch-smoke failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts).

## 2026-11-02T09:15 UTC, build ab26a49, jobs bp-ilpatch-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ilpatch-smoke | 09:15 (17:15) | 8.4 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->147, stuckcheckasync_run 0->147, etspread_passes 0->1, stuckcheck_noplan 0->130, stuckcheck_notstuck 0->14, stuckcheck_rearmed 0->6<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2308.3/2138.3/1383.9 -> 2665.8/2576.6/888.6 | - | timing: invalid (noise high); build: asi b4ad7951affa not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 0d19ef086257 not in DB (expected build/ts3fixes.package); noise: high (GPU 3D others 41.6%); others on game CPUs 3.1%; clock avg/min 3615/3070 MHz; Load (b89 applies at the world fixup) + method_touch of the other two methods (repro ilpatch-touch-bp: one call each on the sim thread with an  |
