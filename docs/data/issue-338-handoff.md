# Issue #338 handoff: `[RouteAsync] KdUncutCeilMs` (30 s uncut ceiling)

2026-11-14. Branch `bugfix/338-uncut-ceiling` (off master 5c1f6b1b). Code, ini, docs and selftest are done. **Not built, not run.** Design and as-built notes are in docs/264 §6. Counters v198 (`native-v198`, 0x62F4-0x6303).

## 1. Build (between timing jobs, never during one)

1. In the branch worktree, run `native\ts3native\build.cmd`. It builds `out/ts3native.asi` and runs `selftest.exe`. Expected selftest line: `KdUncutCeilMs (docs/264): counters v198; ... ok`. Everything else must stay green, especially `TestPlanDeadline`.
2. Run `node tools/harness/native-switches.ts --self-check`. It passed on the branch before the build (118 offsets).
3. Park the build as `game-copy/ts3native.asi.uncut338` and the branch ini as `game-copy/ts3native.ini.uncut338`.
4. Version bump: the merge agent sets the next free `0.36.x` at merge (master is 0.36.253; 0.36.254-256 are in use on other branches).

## 2. Verify

### Smoke

One short load on any world.

- `ts3native.log` must have the start line `[RouteAsync] KdUncutCeilMs=30000: a route plan with no other deadline is cancelled after 30.0 s and treated as a failed route (route plan cancelled after 30.0 s (KdUncutCeilMs))`.
- `/native` must show `kdUncutMode` 1 and `kdUncutCeilUs` 30000000.
- `native_set kduncutceil 0`, then 1, must read back.

### Forced repro (preferred)

This is a test-lowered ceiling plus an uncut job:

1. `native_set kduncutceilus 300000`. This sets a 0.3 s ceiling. The 5-300 s clamp applies only to the ini value (`UcIniUs`). `UcCapUs` uses the counter as it is, and `native_set` writes values raw (`native-switches.ts`). Selftest `TestUncutCeil` asserts that `UcCapUs(0)` returns 300000 after that write. `kdUncutMode` must be 1, which the default ini value 30000 gives; the code and this repro agree.
2. Run any job that lands in an uncut branch. The cheapest is the watchdog idle check (tag `0x400`, docs/264 §1 (a)): it fires on its own for idle NPCs in SV/IP natural play. In `jit-bundle-verify-1113` SV it ran 39 jobs ≥ 200 ms in 10 min.
3. Queue job: `sv-kd-uncut-ceiling`. SV, 10 min natural, `noiseCheck false`, `ini RouteAsync.KdUncutCeilMs=30000`, one setup step `native_set kduncutceilus 300000`. Set `nativeRequire` to `kdUncutHit>=1,watchdogs<=0` and `logRequire` to `uncut ceiling hit: 1`.

Pass when all of these hold:
- `kdUncutHit` ≥ 1;
- every `uncut ceiling hit` line has `us` ≤ ceiling + tail (expect < 3 s);
- no `busy for N s` line ≥ 2 s;
- no crash;
- `kd-arm-cuts.ts --expect-on-zero` stays green.

`kdUncutHit` 0 after 10 min means N/A. Then use the docs/264 §3 forcing repro (`kd-uncut-ceiling`, needs agent target `sealed` and value `kdownplanus`; not built).

### Logging checks (docs/264 §4, in the same build)

Check these in the same SV job:

- **`route async: sim waited N ms on lock … last job seen route …: <who> START, IC kind, scope, tag, uncut: <why>`.** Every sim-thread wait of 1 s or more on `+0x918` gets one line (20 a minute). Before this change, nothing printed below 2 s. Expect ≥ 1 line in the off arm of the docs/264 §3 repro.
- **The `lock … busy for N s` line now ends in `; job: …`** with the same job fields.
- **Long-job lines show `, uncut: backoff|sc-last|watchdog|kind-0|kd-off (KdUncutCeilMs|no ceiling)`** for jobs that had no deadline of their own. The watchdog check reads `uncut: watchdog`.
- **`route async: post-cut tail N ms, route …`** appears for every job that ran ≥ 300 ms past its cut (20 a minute). It splits the tail into plans started after the cut (and how many were capped at once), host calls after the cut, and the rest. This decides docs/264 §1 (d).
- **Harness:** an agent call that fails twice now prints `sim frozen on the route lock, docs/264: <last wait line>` instead of "agent package without the command?" whenever such a line is in `ts3native.log`. `tools/harness/lock-wait-line.ts --self-check` passes.

### Static safety review (if neither repro gives a hit)

Check:
- `UcCapUs` only replaces a 0 deadline;
- the cut uses the same exits as KindDeadlines (`BudgetStep`, `PdWorkerSkip`), so the IC and `LockReleaseHook` path are unchanged;
- `t_ucJob` is set and cleared once per job;
- `kdCut*` / `fbjcCapped` stay unchanged for ceiling hits.

Per the rare-crash-fix rule (MEMORY "Bug diag lifecycle"), an unreproduced crash guard ships after the static safety review only.

### Release arm

Use the normal bundle job. Expect `kdUncutHit` 0 in natural play at 30 s. Any hit is a real ≥ 30 s plan: read its line and file it under issue #338.

## 3. Files touched

- `native/ts3native/ts3native.cpp`: counters v198 and placeholder, `t_ucJob`, worker cap, `g_pdWkTid` arm, long-job label, `UcJobEnd` call.
- `native/ts3native/plandeadline.cpp`: `UcIniUs`, `UcCapUs`, `UcJobEnd`, ini read and start line, `KdJobEnd` counters.
- `native/ts3native/plandeadline-test.h` and `selftest.cpp`: `TestUncutCeil`.
- `native/ts3native/ts3native.ini`: `KdUncutCeilMs=30000` with the player wording.
- §4 logging (`ts3native.cpp`): `UC_*` reasons (`t_kdWhy` set in `KdJobBegin`, `t_ucWhy`), `g_runJob` / `g_runWhy` and `RunJobText`, the job fields in the busy line, the ≥ 1 s wait line in `LockAcquireAsync`, `PostCutTailNote` with `t_pc*` counted in `CallPlanBudget0` and `Proxy`.
- `tools/harness/lock-wait-line.ts` (new, with self-check) and `profile-world.ts` (the FAIL text).
- `docs/PLAYERS.md`, `docs/registry.md`, `docs/264-…`, `docs/index.md`, `tools/harness/native-switches.ts`, `tools/probe/agent.js`, `PLAN.md`.

## Status 2026-11-14 (build window)

- Built: `native\ts3native\build.cmd` green, `selftest ok` incl. `TestUncutCeil` (KdUncutCeilMs line) and TestPlanDeadline. `native-switches.ts --self-check` ok (118 offsets), `lock-wait-line.ts --self-check` ok, `selftest-all` 226 PASS / 0 FAIL.
- Build fixes needed (mechanical): the counters block grew to 6337 int32 (0x6304 bytes), so the `sizeof(Ts3nCounters)` asserts (6323 * 4 / 0x62CC) in ts3native.cpp, selftest.cpp and 49 `*-test.h` were moved to 6337 * 4 / 0x6304; the `UC_*` job-reason enum clashed with unseencost.cpp (`UC_N`) and is now `UCW_*`; a `\n` in the TestUncutCeil printf was a raw newline.
- Parked: `game-copy/ts3native.asi.uncutceil-1114` (sha256 ad3e8ad4273fd8e874b3514073efd862f8056577dfbf3a179e59d3616fd911c3, 0.36.253 in the build; merge agent bumps), `game-copy/ts3native.ini.uncutceil-1114`; builds.json entry added.
- Queue ready, not started: `tools/harness/queues/kd-uncut-ceiling-1114.json` (`sv-kduncut-smoke` 5 min, `sv-kd-uncut-ceiling` 10 min with repro `sv-kd-uncut-ceiling` in repros.json: native_set kduncutceil 1 + kduncutceilus 300000). fixesPackage pinned to rc1111e; swap if a newer release package is current.
