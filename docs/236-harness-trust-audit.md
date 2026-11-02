# 236. Harness trust audit (2026-11-02)

Why: on the EA laptop, runs 7h and 7j2 crashed after the ts3agent autopilot accepted EA's long-distance move dialog, and nothing in the harness noticed. Rodney: "We may need to fix our test harness and not trust it fully." This doc lists every harness behaviour found that can change game state without a request, and every check that can pass silently or measure the wrong thing. Each one is ranked by how likely it was to give a false PASS, a false FAIL or a wrong crash attribution. It also lists the fixes and what is still open.

Scope: `tools/harness/*.ts`, `tools/probe/ts3probe.py`, queue.ts, watchdog.ts, the in-game agent `mods/ts3agent` (HarnessHandlers.cs, AutopilotHandlers.cs) and the soak/sampler scripts. The audit was read-only first. Line numbers are as of main 8ca431e.

## Verdict

- Most likely to have misled us: (1) unknown dialogs answered with "accept", (2) xcpt pickup with no run binding, (3) PASS from absent counters and logs, in particular "verify ok" for WaterJps and LotSinCos.
- Fixed: the six top-ranked items (commits below). Round 2 (same day) fixed restore read-back, the wait progress checks, the verdict line cap and the `-test` rule, and made accept-expecting callers pass `{accept: true}`. Still open: low-rank verdict gaps (19, 21, 22) and per-feature "it ran" rules (17, listed below).
- Past EA runs whose verdict or cause may be wrong: 7h, 7i and 7j2 (cause), the verify claims of 7f, 7f2 and 7g, the 7d soak samples, and the crash file 7e2 printed. See "Past verdicts" below.

## Ranked findings

Kinds: **FP** = false PASS, **FF** = false FAIL, **FC** = wrong crash attribution, **SC** = unrequested state change.

| # | Finding | Kind | Where | Status |
|---|---|---|---|---|
| 1 | Unknown dialogs got the highest-scoring button (`Score()`: ok/accept/yes = 2). A rule whose words matched no button fell through to the same default. The long-distance move offer was accepted, the household left the world, and the game crashed later at 0x00AD04EC (7h, 7j2). | SC, FC | ts3agent HarnessHandlers.cs `AutoDialog` | fixed 552809d (ea169-port) |
| 2 | Autopilot clicks every 250 ms in the world and kept its clicks only in `sApCleared` (in memory, returned on the next `autopilot` call), so a crash lost them. | FC | AutopilotHandlers.cs | fixed 04a5609 (other lane: `ap click:` lines in ts3native.log) |
| 3 | No check for a mode exit or household change the harness did not request. watchdog.ts even "recovered" from one by calling `enter_live_mode`, which hid it. | SC, FP | queue.ts, watchdog.ts | fixed f5d6647 |
| 4 | xcpt pickup: `crash-ledger.ts triage` with no window (watchdog.ts, game-alive.ts) printed every xcpt in Documents, newest last. 7e2 printed an older run's report. queue.ts used `--since` only, so an EA `-test` relaunch's crash in the same window could be blamed on the job. | FC | crash-ledger.ts, watchdog.ts:86, game-alive.ts:30, queue.ts:1768 | fixed d98f756 |
| 5 | featureIdle skipped a feature whose /native counter was absent, or all features when /native did not answer. An asi without the counter counted as "active". | FP | queue.ts:735 | fixed e1cdbc4 |
| 6 | WaterJps and LotSinCos Verify runs: the only gate was `waterJpsMismatches<=0`. 0 calls with 0 mismatches read as "verify ok". LotSinCos had no gate at all, only prose in native-batch3.json. On 1.69, WaterJps is not even installed (`SKIP water jps: code ... differs`). | FP | feature-repros.json:16, queue.ts ACTIVITY | fixed e1cdbc4 (ACTIVITY rows: Verify=1 needs calls) |
| 7 | fixActivity skipped a ts3fixes fix judged on whose `patches` counters were absent, or everything when the agent did not answer at the end. | FP | queue.ts:769 | fixed e1cdbc4 |
| 8 | asyncIdle returned ok when /native was null or had no `asyncStarted`. | FP | queue.ts:708 | fixed e1cdbc4 |
| 9 | logForbid / logRequire read a missing ts3native.log copy as empty, giving "PASS: no forbidden log lines". | FP | queue.ts:2345 | fixed e1cdbc4 |
| 10 | "mono ledger INCOMPLETE" was a text line only and did not fail the row. | FP | queue.ts:2300 | fixed e1cdbc4 |
| 11 | repro.ts native watch read an absent counter as 0, which passes `equals:0`, `atMost` and `equalsName`. The stuck watch passed when `sims_activity` never answered (stuck set empty). | FP | repro.ts:148, :157-170 | fixed e1cdbc4 |
| 12 | Process name hard-coded as TS3W in focus-game.ts, tests-be.ts and night-run.ts. On EA the game is `Sims3Launcher.exe`, so these looked at no process at all (7d: a soak loop watched the wrong name). | FP, FF | focus-game.ts:20, tests-be.ts:115/166/238, night-run.ts:38 | fixed 6bf25fd |
| 13 | The EA ini turned several `[Section] Enabled=1` features on in one run without listing them. | FC | queue.ts (no log) | fixed ba03987 (row.featuresOn + log line) |
| 14 | v015-allon-check.ts read absent tripwire counters as 0. | FP | v015-allon-check.ts:23 | fixed e1cdbc4 |
| 15 | `verdicts()` keeps only the last 8 PASS/FAIL lines, so an early FAIL can be pushed out. `childLost` treats any PASS/FAIL-like line as "the child wrote its own result" even on a non-zero exit. | FP | queue.ts:837, :869 | fixed 04d8407, b26c291, 70cc9f2 |
| 16 | Restores are not read back: Default.ini, Sims3Performance.ini, S3SS.toml, dxvk.conf and ts3native.ini are written back with no hash compare. `restoreContent` moves files back with no size check and skips files missing from the stash without counting them. | FP (next run on the wrong config) | queue.ts:1940-1943, :535, :2435; cycle.ts:57/80 | fixed cbbd07e |
| 17 | Features with no "it ran" rule: about 26 of 48 rows in feature-repros.json have no `nativeRequire`, and gc/avoidro have only `<=0` rules. | FP | feature-repros.json | listed (round 2 coverage table); rules not added |
| 18 | Waits with only a long timeout, no progress check: cycle.ts `until` ('world state' 60 s, 24 retries), save-matrix.ts case timeout 900 s, repro.ts `until` 300 s, ts3probe.py 300 s attach loop, children run without jobGuard (install-fixes undo, cache clear: 10 min). | FF (slow), wasted time | cycle.ts:34/114/148, save-matrix.ts:287, repro.ts:123, ts3probe.py:175, queue.ts:1945/1974/1981 | cycle/save-matrix/repro fixed 679433b; ts3probe.py start-up dialog loop and unguarded queue.ts children: round 3 (below) |
| 19 | `scriptErrorsMax` counts only files with mtime >= job start in USER. A ScriptError written elsewhere, or a failed readdir, gives "PASS: 0 new script errors". | FP | queue.ts:2353 | open (low) |
| 20 | `killEaRelaunch` treats every `-test` GAME_EXE process as EA's crash relaunch, but every xcpt on the EA laptop on 2026-11-02 has `Commandline: ... Sims3Launcher.exe -test`, including the harness's own launches. It runs only after the game is gone, so it is safe today, but the "-test = relaunch" premise is unproven. | FC (latent) | game-exe.ts:31 | fixed 280909a |
| 21 | run-verdict.ts `subPass` passes a repro window on exit 0 with no FAIL line. | FP (low) | run-verdict.ts:84 | open |
| 22 | verify-verdicts.ts on /native below v20 only prints an error, and `live()` with no `before` credits load-time counts to play. | FP (low) | verify-verdicts.ts:189 | open |

## What each fix does

- **d98f756 crash pickup** (main). `crash-ledger.ts triage --pids` reads the PID and process start from the minidump beside each xcpt*.txt (`MINIDUMP_MISC_INFO`, stream 15) and leaves out reports of other processes. It lists them in one note line, printed first because callers take the last block. A report with no dump is kept and marked "PID not checked". `gameRunning()` in game-exe.ts records every GAME_EXE PID it sees. queue.ts clears that set at job start; watchdog.ts and game-alive.ts pass `--since <own start>` and `--pids`. `crash-ledger.ts --self-check` is new (fixture dumps).
- **6bf25fd process name** (main). The running game is found by `GAME_EXE` / `GAME_NAME` only. game-exe.ts `--self-check` scans tools/harness for hard-coded `Get-Process TS3W`, `IMAGENAME eq TS3W`, `frida.attach('TS3W`, `-n TS3W`, `/IM TS3W`.
- **552809d unknown dialogs** (ea169-port). The order is now:
  1. `kDialogRules`.
  2. A naming dialog (empty text field) is filled and accepted.
  3. A dialog with one enabled button gets that button.
  4. `{accept: true}` keeps the old scoring.
  5. Anything else gets the first cancel / not now / later / no / decline / close / don't button.
  6. With none of these, nothing is clicked: McpException "no safe button, nothing clicked: '<title>' buttons: ...".

  The clicked text names the path, e.g. `#12 cancel (safe default)`. The autopilot logs a blocked dialog once, as `ap click: (not clicked) -> no safe button ...`. This also matters on Steam: it reaches main with the 11-06 merge.
- **f5d6647 unrequested state change** (main). ts3probe.py records the last state-changing ts3agent command (`STATE_CMD`: travel_*, load_save, play_*, cas_*, main_menu, switch_household, ...; not auto_dialog), and `/status` returns `state_cmd`, `state_cmd_ago_s`. game-alive.ts `modeExit()` returns "unrequested mode exit: ..." for LiveModeState -> None / out of the world, or a changed active household, when no state command was sent in the last 180 s. queue.ts checks it every guard tick and stops the child with FAIL plus the last 10 `ap click:` lines. It also fails at once on the autopilot's "no safe button" line. watchdog.ts runs the same check before its recovery steps.
- **e1cdbc4 missing data** (main). Findings 5-11 and 14: an absent counter, log or answer gives `UNKNOWN: ...` (run void / FAIL), never PASS. New ACTIVITY rows: `[WaterJps] Verify` and `[LotSinCos] Verify` must show calls.
- **ba03987 features on** (main). Each job logs `ts3native.ini features on: ...` and keeps `row.featuresOn`.

Self-tests: `node tools/selftest-all.ts --ci` gives 135 PASS, 1 FAIL, 48 SKIP. The one FAIL is `tools/worktree-clean.ts` on the EA laptop: its self-check compares the 8.3 temp path `RPANTO~1` with the long path. That is not touched by these commits. The ts3agent change has no unit test: it compiles with build-agent.ts against both the 1.69 and the Steam assemblies, and its harness side (the `no safe button` line, modeExit) is covered by the game-alive.ts self-check. `python -I tools/probe/ts3probe.py self-check` passes.

## Past verdicts (docs/232, 7d-7j2) that may be wrong

| Run | docs/232 verdict | Doubt | Why |
|---|---|---|---|
| 7d | PASS | soak part | Finding 12: the soak loop watched the wrong process name, so its samples are not evidence that the game stayed alive. The log-line criteria still hold. |
| 7e2 | PASS | crash print only | Finding 4: watchdog printed an older xcpt. The PASS itself does not depend on it. |
| 7f, 7f2, 7g | 7f FAIL (start), 7f2 no crash, 7g PASS | any "verify ok" for WaterJps / LotSinCos / FootprintIndex | Finding 6: no check that the Verify paths ran. WaterJps is SKIPped on 1.69 (nothing written). Treat these verify claims as UNKNOWN until a run shows calls > 0. |
| 7h | FAIL, "game left the world on its own", "harness sent nothing" | **cause wrong** | Finding 1: the autopilot is harness input and accepted the long-distance move dialog. The `LiveMode -> None, family Keaton -> none at +97 s` line is that move (triage 2026-11-02), not a spontaneous exit. |
| 7i | PASS, "7h was a rare race" | conclusion wrong | The dialog did not come up in 7i. 7h was the move dialog, not a race in 7f6f7f8. |
| 7j | FAIL at start (0x00E824B2) | ok | The xcpt (17.40.30, pid 32252, process started 17:40:13) belongs to that launch. |
| 7j2 | FAIL in world (0x00AD04EC) | **cause** | Same crash address and timing as 7h (about 90 s after the load). It is the long-distance move path, so not evidence against AutShadow/MonoThread. Rerun after 552809d. |

## Round 2 (2026-11-02, main)

| Commit | Finding | What changed | Self-test |
|---|---|---|---|
| 04d8407 | 15 | `verdicts()` keeps every FAIL line plus the last 8 others. `row.pass` also needs no FAIL line in `row.verdict`, whatever the exit code. `childLost` accepts only a line in the children's FAIL format (`FAIL: why`, `<id>: FAIL`) as the child's result; PASS or summary lines on a non-zero exit count as a lost child. | queue.ts --self-check |
| b26c291 | 15 | `<id>: UNKNOWN: ...` (repro.ts, exit 1) is kept and counts as a failed result, so childLost does not call that child lost. | queue.ts --self-check |
| 70cc9f2 | 15 | Profile rows and repro-session items keep profile-world's FAIL lines on exit 0 too (`FAIL: [vres] ...`, `FAIL: stutter profiler still running 90 s past -t; killed`). | covered by the verdicts() cases |
| cbbd07e | 16 | `readBack()`: Default.ini, Sims3Performance.ini, S3SS.toml and dxvk.conf are compared byte for byte after the write-back. A mismatch sets the job error and stops the queue. The end-of-queue Bin asi + ini pair is compared with bin-before; a mismatch logs `RESTORE FAILED` and the queue exits 1. `restoreContent` checks size after each move, and a listing check flags manifest files found in neither Mods nor the stash (the stash is kept for a human). cycle.ts compares the asi install and the package copies after the copy and throws on a mismatch. Saves were already read back by the save guard (`hashSaves` before and after). | queue.ts --self-check (readBack, lost stash file); the cycle.ts part has no self-check because cycle.ts needs the live game |
| 679433b | 18 | The cycle.ts and save-matrix.ts `until()` helpers default to `stallGuard` (progress sampled every 30 s, two misses fail with the reason). The repro.ts `until` steps call it too. Waits with their own guard keep it. cycle.ts "game closed" opts out with `null`. | game-alive.ts --self-check scans the three files |
| c8a4dac | open 3 | Callers that open a dialog themselves pass `{accept: true}`: the save-matrix trip waits and the PlayFlow venue prompt, the travel-trip.ts trip waits, the cycle.ts load-blocking modal and the Edit Town to live switch, the new-save.ts flow waits, and all 18 story-template `auto_dialog` steps. Pre-save clears, liveHours, age-save, save-reload, watchdog and the cycle.ts menu wait keep the safe button. | story-lint.ts --self-check: every auto_dialog step must set `args.accept` |
| 280909a | 20 | `killEaRelaunch` / `relaunchPids` are gone. `logUntrackedGame` (EA only, 150 s after a crash in queue.ts) logs any GAME_EXE PID that `gameRunning()` never saw in this job, with its command line. It never kills the process or calls it a relaunch. The old function was imported on main but never called. | game-exe.ts --self-check |
| ed6d6ab | - | Cherry-pick of 6921715 (worktree-clean long path). | worktree-clean.ts --self-check |

`node tools/selftest-all.ts --ci`: 136 PASS, 0 FAIL, 48 SKIP.

### Jobs whose verdict changes

- **15 (04d8407, b26c291, 70cc9f2).** Rows that used to PASS now FAIL in two cases. First, a profile job (timing A/B, repro loop, night chain) where profile-world printed a fail-fast FAIL line ([vres] snapshots, a stutter profiler or Frida script killed past `-t`) and still exited 0. Second, a repro, matrix or saveReload child that printed `FAIL`/`UNKNOWN` and exited 0. A child that exits non-zero with only PASS or summary lines now gets `row.error` ("exited ... without a result"), and the game is closed at once, which ends a reused session. That row was FAIL before as well.
- **16 (cbbd07e).** Clean restores do not change. A mismatch now stops the queue: jobs with `defaultIni`, `s3ppIni`, `s3ssToml` or `dxvkConf`, content `no-cc`/`no-store`, and `ccSet` jobs. The queue then exits 1. When a stash file is lost, the manifest is kept, so the next queue refuses to start until `--restore-content`.
- **18 (679433b).** Affected: every job's `cycle.ts --save` load (the "world state" wait), the save-matrix queues (release-check matrix and travel cases, including the trip "left" wait, which had no guard before), and repro `until` steps. A frozen game or a silent probe now FAILs within about 60-90 s, with a hang capture and a dump, instead of after 60 s, 900 s or 300 s. A false FAIL is possible only if the stallGuard mark (process, probe, heartbeat, world flag, commit, ts3native.log) stops moving during a valid phase. The same guard already runs on the trip world load.
- **open 3 (c8a4dac).** Steam does not change before the 11-06 merge, because main's ts3agent ignores the argument. After the merge those flows keep accepting. A dialog that age-save, save-reload, a pre-save clear, liveHours, watchdog or the menu wait used to accept will then get cancel, or a visible "no safe button" FAIL.
- **20 (280909a).** No change on Steam. On EA, a crashed job now spends 150 s logging untracked game PIDs before the restore. Nothing is killed; the next job's `closeGame` still kills by image name.

### Feature "it ran" coverage (finding 17, list only)

These are the 26 rows of feature-repros.json with no `nativeRequire`, plus the rows whose only rule is `<= 0`. "Rule" means an existing check that fails when the feature did nothing: a `fixesRequire` with `>=`, or a queue.ts ACTIVITY row. Counter fields are in native/ts3native/ts3native.cpp. ts3fixes counters come from the agent's `patches` command (mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs). Found by grep on 2026-11-02; not yet confirmed on a run.

| Feature | Proof that it ran | Counters? | Rule today |
|---|---|---|---|
| lotremoveguard | `lrPendFlush` (ts3native.cpp:1120); no OK init line when on | yes | none |
| optsinit | `OK   OptsInit: search options hub references ...` (optsinit.cpp:45); `oiClone` counts only with Enabled=2 | **no** at Enabled=1 | none |
| m1retry | `fbRetryParked` / `fbRetryQueued` / `fbRetryGaveUp`; init `route async: ... M1Retry=%ld ...` (ts3native.cpp:4465) | yes | none |
| fbjobcap | `fbjcCapped`, `fbjcLong`, `fbjcJobHist`; same init line | yes | none |
| deferfootprint | `usfDeferred` | yes | ACTIVITY |
| gc | init `settings: InitialHeapMB=... GrowthStepMB=... BudgetCeiling=...` (ts3native.cpp:260); `gcForeignAllocs` belongs to [GcThreadCheck] | **no** (init line only) | `<= 0` only |
| avoidro | `avTakenHit` / `avTakenMiss` / `avShadowRuns`; init `OK   avoid RO: ...` (avoidro.cpp:468) | yes | `<= 0` only |
| waterjps Verify | `waterJpsCallsK` counts mode-1 calls; Verify has no call counter of its own, only `waterJpsMismatches`; init `OK   water jps: ... verify %ld` (waterjps.cpp:192). Not installed on 1.69. | **no** Verify counter | ACTIVITY (calls), e1cdbc4 |
| lotsincos Verify | `lscCallsK` / `lscHitsK`; Verify has only `lscMismatches` and `lotsincos MISMATCH` lines; init `OK   lot sin/cos cache: ... Verify=%d` (lotsincos.cpp:115) | **no** Verify counter | ACTIVITY (calls), e1cdbc4 |
| failrepeat | `failrepeat_recorded` / `failrepeat_answered` | yes | none |
| safespotcap | `safespotcap_calls` / `safespotcap_limited` | yes | none |
| replandefer | `replandefer_deferred` / `replandefer_kept_sync` | yes | none |
| replanspread | `replanspread_held` / `replanspread_replayed` | yes | `replanspread_exceptions <= 0` only |
| overwatchspread | `owspread_types_run` / `owspread_alarms` | yes | ACTIVITY (stand-down of overwatchchain) |
| legreplanskip | `legreplanskip_skipped` | yes | none |
| bouncerskip | `bouncerskip_skipped` | yes | none |
| stuckcheckasync, stuckchecknear, farthestsorted, indoorcap, overwatchchain, easpreadlots, easpreadpets, easpreaddecay, errortrapspread, unseenenv, unseenmotives, unseenbroadcast, idlememo, ts3fixes-v014 | `stuckcheckasync_run`, `stuckchecknear_checks`, `farthest_calls`, `indoorcap_calls`, `owchain_alarms`, `easpread_lots_passes`, `easpread_pets_moved`, `easpread_decay_passes`, `etspread_passes`, `unseen_*_skipped`, `idlememo_skipped`, `backoff_plans` | yes | `fixesRequire >=` (covered) |

Features that print no counters at all: gc (init line only), optsinit at Enabled=1 (OK line only), and the Verify modes of WaterJps and LotSinCos (no Verify call counter, so 0 mismatches cannot tell "verified" from "never ran"). Native rows with a counter but no rule: lotremoveguard, m1retry, fbjobcap, avoidro. ts3fixes rows with a counter but no rule: failrepeat, safespotcap, replandefer, replanspread, legreplanskip, bouncerskip.

## Round 3 (2026-11-02)

| Commit | Item | What changed | Self-test |
|---|---|---|---|
| 03e2bfe (ea169-port) | autopilot race | New ts3agent command `dialog_expect {seconds = 30, title?}` (0 clears, max 900). While it is active the autopilot leaves a matching dialog (any dialog without `title`) alone and logs it once: `ap click: <title> -> (left to the harness, dialog_expect)`. It ends by timeout, so a missed answer blocks the autopilot for at most `seconds`. `auto_dialog {accept:true}` still answers, with the kDialogRules first (move offers get "not now"). Built with build-agent.ts for the ea169 and Steam assemblies. | no ts3agent unit test exists; compile only |
| main | callers | save-matrix.ts (`dialogs(true)`, the "left" and "arrived" trip waits), travel-trip.ts (`dialog(true)`, boarding wait), cycle.ts (load wait, Edit Town to live switch), new-save.ts (`clearDialogs(true)`) send `dialog_expect {seconds: 60}` before each accept poll and at the start of the wait. story-fuzzer.ts sends it before a step whose next step is `auto_dialog {accept:true}` (the step's `waitS` + 30 s). Repro steps and the other `auto_dialog` callers use the safe button and need nothing. On an agent without the command (Steam before the 11-06 merge) the call answers ok:false and is ignored. | game-alive.ts --self-check (each of those files mentions dialog_expect) |
| main | 18 ts3probe | The 300 s start-up dialog answerer (`answer_startup_dialog`, the only 300 s loop in ts3probe.py) checks every 30 s that the game process (same pid) still exists; two misses in a row end it with a `startup-dialog` event naming the reason. | ts3probe.py self-check (`next_misses`) |
| main | 18 queue children | See the list below. | queue.ts --self-check |

Queue children without jobGuard. jobGuard watches the probe server and the game process, so a child that runs while the game is closed has nothing to watch. Those children are exempt from it and get a hard cap instead (`noGameDeadline`: 15 min, not the job deadline of 60 min):

| Child | Where | Why exempt |
|---|---|---|
| `install-fixes.ts <undo> --only ...`, `install-fixes cache-clear` (restore paths) | restoreStart, the ccSet and content restores | game closed; 10 min cap kept |
| `install-fixes cache-clear` (ccSet, no-cc / no-store content) | job setup after `closeGame()` | game closed; capped at 15 min now |
| `install-fixes install|uninstall --only ...` | job setup | game closed; capped at 15 min now |
| `cycle.ts --restart` (job start, reloadGame, reload-check) | job setup, reloadGame, saveReload | closes the game and clears caches, never starts one; capped at 15 min now |

Every child that waits on a running game (`cycle.ts --save`, save-matrix.ts, profile-world.ts, repro.ts, watchdog.ts) runs between startProbe and endGame and has jobGuard.

`node tools/selftest-all.ts --ci`: see the report of the round 3 run below.

## Open

1. Finding 17: add `>=` rules from the table above. Counters exist for all but gc, optsinit at Enabled=1 and the two Verify modes; those need a counter in ts3native first.
2. (Done in round 3: `dialog_expect`.) Travel flows on ea169-port: the in-game autopilot (`dialogs: true`) uses the safe default and may cancel a travel confirm before the harness's `dialogs(true)` gets to it. This needs a ts3agent change (an accept window, or the autopilot off during trips). That change is not on main, and round 2 did not edit ts3agent. Check on the next travel or save-matrix run after 552809d.
3. Steam: 552809d (safe default plus the `accept` argument) reaches main with the 11-06 merge. Until then, Steam runs keep the old accept default and c8a4dac changes nothing.
4. Finding 18 remainder: done in round 3. Not covered: the `answer_startup_dialog` loop only checks that the game is there, not that the dialog was seen.
5. scriptErrorsMax source (19), run-verdict subPass (21), verify-verdicts below v20 (22).
6. A relaunched game that gameRunning() saw before the triage still counts as "this run". Recording a first-seen time per PID would close that.

## Vanilla harness baseline (2026-11-02, plan only, nothing run)

Rodney: "bisect harness first". Before any further mod run, run the harness on vanilla EA 1.69.47 with our mod removed and confirm every harness feature works there. A feature that already misled us once (finding 1) must show it works without ts3native and ts3fixes in the process. Static plan: no game was started, and Bin, Mods and Saves were not touched while writing it. Status labels: [verified] seen in a result row or log, [inferred] from reading code, [untested] no EA run found.

### Vanilla state

| Part | State | Why |
|---|---|---|
| ts3native.asi + ts3native.ini (the pair) | out of Bin | the whole pair, not a SKIP-all ini: no `host` line, no hooks, no `ts3native.log` |
| ts3native.log, autshadow.jsonl (outputs of the pair) | out of Bin | stale logs would read as this run's (game-alive.ts, watchdog.ts and the mode guard read `ts3native.log` size and lines) |
| Mods\Packages\ts3fixes.package | out of Mods | our player mod |
| Mods\Packages\ts3agent.package | stays, ea169 head build | the harness needs it |
| Mods\Packages\ts3-nointro.package | stays | harness-only (skips intro movies, 996 B, no gameplay code); say so in the row. Out only if the first start shows the agent does not connect with it in |
| DXVK d3d9.dll + dxvk.conf, Options.ini, LoadOption.ini | stay | player environment; the harness sets `selectedflags` itself |
| wininet.dll (Ultimate ASI Loader 10.0.2, docs/230) | stays | see risk 1 |
| simler90 EAapp core mod, NRaas and other community mods, ErrorTrap disabled | stay | player environment |

Environment risks (say in every result row):
1. UAL with no asi [inferred, check before run 1]: it proxies wininet and loads only `*.asi` from Bin, `scripts\`, `plugins\`. After the move `Get-ChildItem Bin -Recurse -Filter *.asi` must list nothing (the `ts3native.asi.pre-*` backups end in `.pre-*`, so they are not loaded) and no `scripts`/`plugins` folder may exist. It is still a DLL in the process, so a crash with a `wininet.dll` frame in its stack must be triaged before it is called vanilla EA. Fallback: a run with `wininet.dll` moved out as well (add it to the install list).
2. The simler90 core mod replaces ScriptCore/gameplay assemblies. It is a mod, not EA code: "vanilla" here means EA 1.69 plus the player mods, and a result in the travel, move or save paths could come from the core mod. Run H-move with it in. If H-move crashes, rerun it with the core mod (and the mods that need it) moved out (H-move-b, 12 min, conditional) to split EA from the core mod.
3. Other community mods (NRaas Overwatch, sloped terrain, house boat fix, ld_SmoothPatch, MonoPatcher, Performance Fixes, Store packages) stay and can change script-error counts; `scriptErrorsMax` only excludes the known EA errors in known-script-errors.json.
4. ts3agent log lines go through `DiagLine` -> `MemSend` (RareHandlers.cs:52), a `GetOption2` call that only ts3native answers. Without the asi, `ap click:` lines, mem marks and the "no safe button" line are never written, so the unrequested-mode-exit guard (queue.ts:1778), game-alive `clickLines`/`noSafeButton` and watchdog.ts:115 are partly blind [inferred; confirm in run 1 by an empty or missing `ts3native.log`]. Substitute: the `autopilot` command returns and clears `cleared` (`title -> clicked`, AutopilotHandlers.cs:73); poll it every 30 s into `probe-out/<tag>.clicks.log` (V0.3).
5. queue.ts and cycle.ts assume the asi: queue.ts copies the job's asi, writes `Bin/ts3native.ini` and fails the build check when the log's first line is missing (queue.ts:1961, 2007, 2150, 2322); cycle.ts `--restart` runs build-agent.ts and build-fixes.ts and copies `ts3agent.package`, `ts3-nointro.package` and `ts3fixes.package` from the MAIN tree's `build/` into Mods (cycle.ts:62-81); main's ts3agent has no `dialog_expect` before the 11-06 merge. Run as is, either script undoes the vanilla state. This is why V0 comes first.

### Inventory (file, entry point, EA today)

EA = works with `TS3_LAUNCH=ea`, `TS3_EXE=Sims3Launcher.exe`, `TS3_BIN` = EA Bin.

| # | Flow / feature | File and entry point | EA today | Vanilla note |
|---|---|---|---|---|
| 1 | Launch, EA chain | tools/probe/ts3probe.py `serve --launch` -> `Probe.launch` (EALauncher.exe + `TS3_EA_URI`; TS3.exe swapped over Sims3Launcher.exe, docs/230); tools/harness/game-exe.ts (`LAUNCH`, `gameRunning`, `seenPids`) | EA [verified smoke run 4, 7-series] | no game args on EA (`--world` and `gameArgs` such as `-disablestoryprogression` are ignored with a WARN) |
| 2 | selectedflags | ts3probe.py `ensure_selected_flags` (LoadOption.ini from HKCU `LastRunIProducts & LastRunEProducts`) | EA [verified 9890067] | check the `Installed` line lists packs, not BaseGame |
| 3 | Mod-version popup guard | ts3probe.py `is_mod_version_dialog`, `answer_startup_dialog` (docs/231; 300 s loop, 30 s progress check) | EA [verified] | the core mod keeps the popup; it must still be answered |
| 4 | Start-up dialog answerer ("Mod Scripts Found") | cycle.ts `--save` menu loop (`auto_dialog`, quiet 5 s); queue.ts menu path | EA [verified smoke run 4] | |
| 5 | ts3agent connect | cycle.ts `until('agent answering')`, `stallGuard` (game-alive.ts); agent `ping` | EA [verified, 22 s] | needs the ea169 package (below) |
| 6 | load_save | HarnessHandlers.cs `LoadSave` (appends `.sims3`); cycle.ts `--save X.sims3`; queue.ts `save:` | EA [verified] | |
| 7 | Autopilot, dialog rules, safe default | AutopilotHandlers.cs (`kDialogRules`, `Score`), HarnessHandlers.cs `Autopilot`, `AutoDialog`; ea169-port 04a5609, 552809d | EA [verified 7j3: move dialogs declined] | click log lines lost without asi (risk 4) |
| 8 | `dialog_expect` | HarnessHandlers.cs `DialogExpect` (03e2bfe, 54d2331); callers cycle.ts, save-matrix.ts, travel-trip.ts, new-save.ts, story-fuzzer.ts, game-alive.ts | ea169 head only [untested on a run]; the Steam agent lacks it until 11-06 | H-move is its first live test |
| 9 | Routing drive (7e2) | manual: `ts3probe.py serve --launch`, `cycle.ts --save`, `autopilot {enabled, speed, dialogs}`, `watchdog.ts --every 1`, `probe-out/ea-7e2-sampler.ps1` (process CPU/WS/threads every 30 s); queue forms `run.routelock`, `run.soak` | EA [verified 7e2 manual]; routelock [untested] on EA | routelock reads the probe's native route lock: needs asi; the vanilla drive is soak + sampler |
| 10 | Soak, samplers, memory guard | queue.ts `run.soak` (autopilot ultra + watchdog.ts), watchdog.ts `--min-free-mb 300`, game-alive.ts `sampleGame`, probe `/memory`, machine-load.ts | EA [verified smoke 5 min] | `/native`, `[Verify]` and counter samplers: need asi, dropped |
| 11 | Harness save | ts3agent `save_game` (ControlHandlers.cs:42); save-matrix.ts:395; profile-world.ts `reproSave`; repro-save.ts | EA [untested] | save a NEW name only, never Baseline-* (Save guard hashes in queue.ts) |
| 12 | quit_game | HarnessHandlers.cs:49,630 (`TransitionToGameStateQuitNoCheck`); queue.ts `closeGame` | EA [untested; smoke closes by kill] | check the process exits within 60 s without a kill |
| 13 | Cycle: restart, load modal, Edit Town | tools/harness/cycle.ts (`--restart`, `--save`; Edit Town to live switch with `dialog_expect {60}`) | `--save` EA [verified, docs/232]; `--restart` EA-capable but installs packages (risk 5) | needs the V0 vanilla switch |
| 14 | Travel-trip | tools/harness/travel-trip.ts (`--to france|china|egypt|university|future`, `dialog(true)` boarding wait) | EA [untested]; EP destinations need those packs in selectedflags | travel confirm races the autopilot: `dialog_expect` |
| 15 | save-matrix | tools/harness/save-matrix.ts (cases control, identical, overwrite, back-to-back, unpaused, reload, travel) | EA [untested] | `identical` and `reload` compare save bytes, no asi needed; the save-fix arms are ts3native: dropped |
| 16 | new-save | tools/harness/new-save.ts (`--world`, `--save`, `--household`) | EA [untested]; world list may differ from Steam | writes a new save name |
| 17 | Repro steps | tools/harness/repro.ts `run <id>`, repros.json (326 entries), repro-loop.ts | EA [untested] | pick an entry with no counters: `store-cas-browse` (no native/fixes text in the entry) |
| 18 | Story templates | tools/harness/story-fuzzer.ts, stories/templates/*.json | excluded: stopped by memory rule `story-tests-stopped` (2026-10-23) | listed only |
| 19 | queue.ts verdicts, jobGuard, childLost | queue.ts `silentFail`, `jobGuard` (:1823), `childLost` (:883), run-verdict.ts | EA [verified in smoke rows] | asi/build/ini/fixes checks block vanilla (risk 5) |
| 20 | Watchdog | tools/harness/watchdog.ts (clock, heartbeat, dialogs, address space, stack), stuck-rule.ts | EA [verified 7e2, smoke 5 min] | `ts3native.log` size is only one of its progress marks |
| 21 | xcpt binding | game-exe.ts `seenPids`, crash-ledger.ts `triage --since --pids`, game-exe.ts `killEaRelaunch` path in queue.ts | EA [verified d98f756; EA `-test` relaunch seen 13:07] | change set must read "no ts3native, no ts3fixes" |
| 22 | Restore read-back | queue.ts `readBack`, `stashContent`/`restoreContent` (:533, :540), `restoreStart` (:2025), docs/236 #16 | EA [verified by self-check] | the vanilla stash reuses this code (below) |
| 23 | Unrequested-mode-exit check | queue.ts `unrequestedModeGuard` (:1778), game-alive.ts `modeSnap`/`modeExit`, ts3probe.py `STATE_CMD`; watchdog.ts:115 | EA [verified in code; 7j3 quiet] | `auto_dialog` is not in `STATE_CMD`, so an accepted move must be flagged |
| 24 | Features-on logging | queue.ts `iniFeaturesOn` (:635, `row.featuresOn`), `rowIni` | EA [verified] | must read "none" plus a `vanilla: true` field |
| 25 | Machine tag | game-exe.ts `MACHINE`/`machineTag`, queue.ts `machineCols` | EA, `TS3_MACHINE=ea-laptop` [verified] | rows go to `...queue-<name>.ea-laptop.md`; queue name `ea-vanilla` |
| 26 | pin / noise | pin.ts (`MACHINES['ea-laptop']`), machine-load.ts | EA [self-check verified, ran in smoke] | correctness only, no timing claims |

### Runs

Order: riskiest first. All with `TS3_MACHINE=ea-laptop`, queue `ea-vanilla`, save `Baseline-SV-Spring` (the 7h/7j2 save) unless stated, correctness only, no timing.

Common pass: vanilla state read back (asi list empty, no `ts3native.*` in Bin, no ts3fixes in Mods; row says `vanilla: true`); `Installed` lists the packs; "Mod Scripts Found" and the version popup answered; agent answers; world loaded; no xcpt for this run's PIDs; `scriptErrorsMax` 0 (known EA errors excluded); clicks log written.
Common fail-fast: game process gone (game-alive, 60 s); agent heartbeat frozen 60 s; two missed 30 s progress checks in any wait; unrequested mode exit; autopilot "no safe button" in the clicks log; xcpt with this run's PID; free address space under 300 MB; agent not answering 300 s after launch; any ts3native file reappearing in Bin.

| Run | Flows (inventory #) | Pass criteria | Own fail-fast | Est. (min) |
|---|---|---|---|---|
| H-move | 1-8, 21-23 | Load, then `dialog_expect {seconds: 300, title: "long-distance move"}` (confirm the real title text from the clicks log first; 7j3 names "new towns available", "long-distance move", "preparing to move"), then `auto_dialog {accept:true}`. Record: (a) crash or no crash in 10 min after the accept; (b) the accept is the only harness action; (c) the mode guard reports the exit as unrequested (`auto_dialog` is not in STATE_CMD), so the row is FAIL with that text, which is the expected result; (d) xcpt address if any (7h/7j2: 0x00AD04EC, about 90 s after the load). | `dialog_expect` expires without the dialog (log "move dialog never seen", accept nothing else); another dialog type appears | 12 |
| H-travel | 1-8, 11, 14, 15 (travel case), 22-23 | `save-matrix.ts --travel --to university`, then `travel-trip.ts --to france --trips 1`: out, wait for arrival, save away, back, save home; every travel confirm answered by the harness through `dialog_expect`, not by the autopilot; the saves load. | confirm cancelled by the autopilot (clicks log `cancel`/`not now` on a travel title); trip wait with no progress 2 x 30 s; save onto an existing Baseline-* name | 30 |
| H-cycle | 1-6, 12, 13 | `cycle.ts --save`, play 2 min, Edit Town and back to live (load modal answered via `dialog_expect {60}`), `cycle.ts --restart` in vanilla mode, `--save` again; Mods still the vanilla set after the restart (read-back). | package reinstall after `--restart` (ts3fixes or asi present); Edit Town switch stuck 60 s | 15 |
| H-newsave | 1-5, 11, 16 | `new-save.ts --world "Sunset Valley.world" --save Vanilla-SV-fresh`: the save file exists, loads, the household has the members; nothing overwritten. | no household found; save modal unanswered 60 s | 12 |
| H-matrix | 1-8, 11, 15 (non-travel) | `save-matrix.ts --cases control,identical,overwrite,...` (names as in its `--cases` help): each case saves under a new name, `identical` byte-equal, `reload` loads. | `save_game` rejected under a modal; `reload` hang | 15 |
| H-repro | 1-8, 17 | `repro.ts run store-cas-browse`: every step answers `ok:true`, Buy/CAS visits return to live mode, no dialog left over. | step `ok:false`; state mismatch after a step | 10 |
| H-fault | 19-23 | Deliberate faults in one load: (1) `taskkill` the game mid-soak: FAIL "game died", `childLost` text, xcpt or "no xcpt" reported, never PASS; (2) a harness state command (`enter_live_mode`) must NOT raise the exit check; (3) a directly accepted dialog must; (4) vanilla read-back deliberately broken (rename one file) must FAIL the start check. | any PASS on 1, 3 or 4 | 8 |
| H1 basic | 1-6, 9-12, 20, 24-25 | Load, 10 min in world with the routing drive (autopilot soak + 30 s sampler in the `ea-7e2-sampler.ps1` form), watchdog quiet, harness `save_game` to `Vanilla-SV-H1`, `quit_game` and the process exits without a kill; clock advances; working set stable (7e2: 2.09-2.14 GB). | watchdog stuck; clock frozen; working set up more than 200 MB in 5 min | 15 |

Total: 12 + 30 + 15 + 12 + 15 + 10 + 8 + 15 = 117 min of game time, about 2.5 h with 5 min reading between runs. Add V0 (about 45 min agent time, no game) and about 10 min for the install self-check and restore. Conditional: H-move-b, 12 min. H-move goes first (known crash, first live test of `dialog_expect`). If it crashes the other runs still proceed: they do not accept the move dialog (the autopilot declines it by default).

### Install and restore (vanilla)

Prerequisite V0 (code, before any game run; shared with the Steam machine, so Rodney reviews it):
1. `TS3_VANILLA=1` (queue job `"asi": "none"`): queue.ts skips the asi copy, ini write, build check, fixes check and `[CrashForensics]` line; the row gets `vanilla: true` and `featuresOn: none`. cycle.ts `--restart` skips build-fixes.ts and the asi/fixes copies.
2. cycle.ts and queue.ts take the agent package from `TS3_AGENT_PACKAGE` (the ea169-port build) instead of running build-agent.ts on the main tree.
3. Clicks poller: `autopilot` status every 30 s into `probe-out/<tag>.clicks.log`; the mode guard and `noSafeButton` read it when `ts3native.log` is missing.
4. `tools/harness/vanilla.ts install|restore|status|--self-check`: the stash logic below, no game.

Install (game closed; refuses if a stash manifest or `probe-out/queue/job-running` exists):

| File | From | To (backup, outside Mods) |
|---|---|---|
| ts3native.asi, ts3native.ini, ts3native.log, autshadow.jsonl | `C:\Program Files\EA Games\The Sims 3\Game\Bin\` | `%USERPROFILE%\Documents\Electronic Arts\The Sims 3 vanilla-stash\Bin\` |
| ts3fixes.package | `Documents\Electronic Arts\The Sims 3\Mods\Packages\` | stash `Mods\Packages\` |
| ts3agent.package (old copy) | Mods\Packages | stash `Mods\Packages\ts3agent.package.old`, replaced by the ea169 head build |

Moves are renames on drive C: (as `stashContent`, queue.ts:533): manifest first (path, size, sha256), move, read back every file (size and sha256 at the new place, nothing left at the old, `readBack`), then delete `scriptCache.package` (Mods changed). The stash folder is a sibling of the Documents Sims 3 folder, like `The Sims 3 content-stash`; queue.ts refuses to start while either manifest exists. `.pre-*` backups, `wininet.dll`, `d3d9.dll`, `dxvk.conf` and the Documents `ts3native\` state folder stay (inert without the asi); list `Saves\` for sidecar files and leave them.

Read-back at the start of every run (harness side): Bin has no `*.asi` and no `ts3native.ini/log`; Mods\Packages has no `ts3fixes*.package`; sha256 of `ts3agent.package` equals the manifest's; `wininet.dll` and `d3d9.dll` sha256 equal the install manifest's. The row writes `vanilla: true` and these hashes; a miss FAILs before the game starts. H-fault #4 proves it.

Restore (`vanilla.ts restore`, also called from the `restoreStart` path after a failed run): move every manifest entry back, compare sha256 with the manifest, put the old ts3agent.package back, delete `scriptCache.package`, delete the manifest only after all reads pass; "file in neither place" = lost = FAIL with the stash kept (same rule as queue.ts:552). Before the install record `sha256sum` of the asi, ini and ts3fixes.package; after the restore `status` must show the same values.

ts3agent package: build from ea169-port head (97ef3fa; last agent code 03e2bfe + 54d2331, which contain 04a5609 and 552809d): `node tools/harness/build-agent.ts` in the ea169-port worktree with `TS3_BUILD_DIR=build/ea169` and the EA 1.69 reference assemblies in `TS3_REF_DIR` (as for the existing `build/ea169`, [inferred]). The existing `build/ea169/ts3agent.package` there is 457,026 B (sha256 prefix `2e91f7c95587`); the package now in Mods is 454,426 B, equal in size to the main tree's older `build/ea169` build, so it may not be the head build. Install path: `Documents\Electronic Arts\The Sims 3\Mods\Packages\ts3agent.package`. Check after the first start: `agent('dialog_expect', {seconds: 0})` answers `ok:true`.

### Gaps (cannot be tested on EA, or not vanilla)

| Flow | Reason |
|---|---|
| Launch with game args (`-disablestoryprogression`, `--world`, `-w`) | `TS3_LAUNCH=ea` starts EALauncher.exe with only the URI and no arguments (ts3probe.py WARN). Story progression is on in every EA run. |
| queue.ts asi/build check, `[Verify]`, `/native`, native_set read-back, counter ACTIVITY rules, `routelock`, `worldimage`, call-site DB | need ts3native; absent by definition in vanilla. |
| Save-fix arms of save-matrix | ts3native save hooks; absent. |
| Timing, hitch, A/B and profile jobs, noise verdicts | the laptop gives no timing (memory rule `ea-laptop`); not part of this plan. |
| Story templates | stopped by memory rule `story-tests-stopped` (2026-10-23). |
| `focus-game.ts` (looks for TS3W), `ai-usage.ts` (no usage file) | harmless on EA, not exercised. |
| Click log and mem marks through `ts3native.log` | the DiagLine sink is ts3native; replaced by the `autopilot` `cleared` poll (V0.3) [inferred; confirm in run 1]. |
| Pure-EA claim | the simler90 core mod and other mods stay: results are "EA 1.69 + player mods"; H-move-b splits the core mod. |

### Vanilla baseline results (2026-11-02, EA laptop, TS3_VANILLA=1)

Setup: V0 commits 4c4e3b0 and 5d59e2c (`tools/harness/vanilla.ts`, `cycle.ts --restart` read-back only, `queue.ts` refuses under TS3_VANILLA=1 instead of reinstalling the asi; queue.ts itself has no vanilla mode, the runs were driven by hand as in 7e2). ts3agent package = `build/ea169/ts3agent.package` (457,026 B, sha256 2e91f7c95587): built 18:37:24, no `mods/ts3agent` file newer and none changed between 03e2bfe and head 97ef3fa, so it is the head build (not rebuilt). State read back before every run (no `*.asi`, no `scripts`/`plugins`, no ts3native.* in Bin, no ts3fixes, agent/wininet/d3d9 sha against the manifest). Saves mirrored to `The Sims 3 vanilla-stash saves\Saves` (robocopy /MIR, 18 GB) before and restored after every run; listing and robocopy /L equal after each. Monitor `probe-out/ea-H-mon.ts`: 2 s sampler, 2600 MB guard, 30 s `autopilot` poll into `<tag>.clicks.log`. Game windowed, never focused, `focusmute=1` (Options.ini audio levels left as the player has them).

| Run | Verdict | Reason | Logs (`probe-out/`) |
|---|---|---|---|
| H1 basic | FAIL (fail-fast), rest of the features PASS | At about 5 min of soak (autopilot ultra) a career offer ("Justine's ... Patrol Officer (job level 4) ... Should Justine leave her current position") had no safe button: clock paused, `blocked: no safe button` in 3 polls, watchdog.ts diagnosed it and exited. Continued with an explicit `auto_dialog {accept:true}` (harness action, logged); clock advanced again. WS 1.36 GB at load, 1.88-1.92 GB during the run (no growth over 200 MB/5 min). `save_game` `Vanilla-SV-H1` wrote the save; `quit_game` ended the process in about 7 s without a kill; no xcpt; no ts3native file. The clicks substitute works: `autopilot` returns `cleared` (also the "Mod Scripts Found" and the move dialog text) and `blocked`. | `ea-H1-1102.*` |
| H-move | UNKNOWN | The move dialog never reached `dialog_expect` while it was armed. Run 1 (300 s): the autopilot answered it `no (rule)` between 19:47:35 and 19:48:05, the armed window ended about 19:48:01. Run 2 (600 s, re-armed twice, normal then ultra, 28 min): it never came; other dialogs (date, opportunities) were cleared by the autopilot because the title filter did not match, which shows the filter works; a career offer paused the game again. Runs 3a-3c: launcher stuck (below). No accept was sent, so no crash answer. | `ea-Hmove1-1102.*`, `ea-Hmove2-1102.*` |
| H-move (3 relaunches) | not run | Three launches in a row stopped at 103 MB / 17 threads / 2-4 CPU s with the "The Sims 3" launcher window, before the agent loaded (cycle.ts failed fast after 300 s once, then killed by hand). EA app shows `authenticated: false` for every launch today, including the good ones, so that is not the difference. Cause unknown. The probe attached and the game froze at 283 MB. | `ea-Hmove3a-1102.*`, `ea-Hmove3b-1102.*`, `ea-Hmove3c-1102.*` |
| H-travel, H-cycle, H-newsave, H-matrix, H-repro, H-fault, H-move-b | not run | stopped at the launch hang; H-move-b only on a crash | none |

Move dialog title, verbatim from the clicks log: `There are new towns available for your Sims to live in! You can always perform a long-distance move at a later time by using your Sim's phone or computer. Would you like to move your Sim family to a different town right now?` (autopilot rule answer `#3 no no`).

Findings:
1. The agent mailbox takes one call at a time: a second client (the 30 s poll, watchdog.ts) during cycle.ts's start gets `RPCException: previous request still pending`. Harmless here, but a poller must start after the load.
2. `blocked` stays set (also `'Saving... ' buttons:` right after a save), so "no safe button" in a poll is not always a live block; the clock check decides.
3. The move dialog is not on a reproducible timer: 45 s after load (H1, ultra), about 5 min (H-move 1), never in 28 min (H-move 2). H-move needs a trigger that does not depend on luck, or a long armed window (`dialog_expect` caps at 900 s per call and is re-armed).
4. Soak at `ultra` meets the career offer dialog within about 5 min on this save; it has no safe answer by design, so a soak longer than that needs a rule or accepts a stop.
5. The scratch-save cleanup in run 7k was an ad hoc `rm -f` on a `.sims3` directory, not code in a harness script; `vanilla.ts rm-save` (and the Saves mirror) now do it recursively, refuse `Baseline-*`, and are self-checked.
6. Not harness failures found: no harness crash, no wrong verdict, no failed restore, and the only unrequested action was the autopilot's documented safe default. The launch hang is open: not shown to be the harness.

Restore: Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14 and scriptCache db69b1b6a862 back by sha (`ea-H0-1102.sha-before/after.txt` equal); Mods/Bin listings equal except `Sims3Launcher_d3d9.log` (DXVK rewrites it each start). The 18 GB Saves mirror folder is still there; delete it when no more vanilla runs are planned.

## Round 4: harness fixes after the vanilla baseline (2026-11-02, EA laptop, code and logs only, no game run)

| Commit | Branch | Item | What changed | Check |
|---|---|---|---|---|
| 8f2a239 | ea169-port | dialog rules | `kDialogRules`: career job offer gets `keep job` (fragments `leave her current position`, `leave his current position`, `keep job`); `go on a date` and `to the prom` join the invite rule; NPC move-in request and pet adoption (`move in with`, `wants to move in`, `move into your`, `join your household`, `adopt`) get cancel/decline/no; school dialogs (`field trip`, `boarding school`, `homesick`, `visit a friend`) get cancel/decline/no. `kSafeWords` gains ignore, reject, refuse. | builds ea169 + Steam |
| 413f985 | ea169-port | dialog_expect race | `dialog_expect {seconds, title?, accept:true}`: the agent itself clicks the accepting button of the first matching dialog in the window, logs `dialog_expect accept: <title> -> <clicked>` (diag line and `cleared`) and ends the window. A window that ends with no match logs `dialog_expect expired (no match)` plus the title. | builds ea169 + Steam, CI green |
| 4c15cdf | ea169-port | dialog_expect rules | An accepting window still applies `kDialogRules` (Edit Town town-save prompt stays "just continue"); `rules:false` turns them off for that window only (H-move: accept the move offer that the move rule declines). | builds ea169 + Steam |
| ab072ff | main | 1 vanilla queue | See "Vanilla queue" below. | queue.ts, vanilla-steps.ts --self-check |
| 708ad8f | main | 3 callers | save-matrix.ts, travel-trip.ts, new-save.ts, story-fuzzer.ts send `dialog_expect {accept:true}`; cycle.ts arms the Edit Town switch by title (`save your town before continuing`). The old auto_dialog accept polls stay as the fallback for older agents (they ignore `accept`). | game-alive.ts --self-check asserts accept:true at every caller |
| 6653071 | main | 4 audio | ts3probe sets every audio level to 0 and every mute to 1 in Options.ini before each launch (Steam and EA, vanilla or not), byte-exact with CRLF kept, and reads them back; a failed read-back refuses the launch. The line also logs `resolution` and `lastdevice`. | ts3probe self-check |
| d733119 | main | 5 launch hang | `launchGuard` in the "agent answering" wait: committed memory under 400 MB and flat (within 2 MB) over two 30 s samples while the agent never answered gives `hung at launcher stage: <exe> up at N MB committed with no growth for ~60 s, agent never answered`; the probe is detached and the game, EALauncher.exe and TSLHelper.exe are killed (never EADesktop.exe). queue.ts `closeGame` kills the same leftovers. ts3probe logs the EA processes before every EA launch. | game-alive.ts, game-exe.ts --self-check |

`node tools/selftest-all.ts --ci`: main 138 PASS, 0 FAIL, 48 SKIP; ea169-port 136 PASS, 0 FAIL, 48 SKIP. ts3agent builds: ea169 and Steam, 458,586 B each. ea169-port is pushed. A rebase of ea169-port onto origin/main was tried and aborted: origin/main holds a squash of the EA harness work (#198, #207), and the 76 commits conflict from the first one on.

### Vanilla queue

`TS3_AGENT_PACKAGE=<ea169-port build>/ts3agent.package node tools/harness/queue.ts tools/harness/queues/vanilla-matrix.json --no-smoke` (EA laptop: also `TS3_MACHINE=ea-laptop TS3_LAUNCH=ea TS3_EXE=Sims3Launcher.exe TS3_BIN=<EA Bin>`). The queue file sets `"vanilla": true`; `TS3_VANILLA=1` does the same for any queue.

- vanilla.ts install at the start and restore at the end; a stash that was installed before the run is only read back and left in place.
- Every game start reads the vanilla state back and fails before the launch on a miss; the job guard fails a running job whose Bin or Mods leave that state.
- Skipped checks are written as `N/A (vanilla): ...` in every row (asi and ts3fixes build checks, ts3native.log version gate, ini features, hook lines, counters, ts3fixes activity), never as PASS.
- Refused before anything moves: asi other than `"none"`, ini, fixes, counter minimums, native log rules and the other ts3native-only job keys.
- Click log: the autopilot poll after the load writes `probe-out/queue/<queue>-<date>-<job>.clicks.log` (`TS3_CLICKS_LOG` for child scripts). An accepted `dialog_expect` excuses mode exits for 900 s (a move takes long).
- New run kind `run.script: ["<harness script>.ts", args...]`; `vanilla-steps.ts` holds `accept`, `state-cmd` and `kill-game`.

Jobs: vh-move, vh1-soak, vh-cycle-a/b, vh-travel-matrix (University), vh-travel-trip (France), vh-newsave (menu, Vanilla-SV-fresh), vh-matrix (control, identical, overwrite, back-to-back, unpaused), vh-repro (store-cas-browse), hf-statecmd (PASS expected), hf-kill (FAIL expected, last). Not covered by a job: the Edit Town round trip, `quit_game` (the queue counts a game that is gone as a crash), H-fault #3 (vh-move is the accepted dialog), H-fault #4 (queue.ts and vanilla.ts self-checks).

### Dialogs a soak can raise

From the H-run click logs and the decompiled scripts (call sites in Occupation, Career, NPCAskToMoveIn, SocialCallback StrayPets, BoardingSchool, School, AlienUtils, PlayDateManager, AttractionDateCall, PromSituation). AcceptCancelDialog has `accept` and `cancel` buttons, so the safe default already cancels it; the rules make the decline explicit where a fragment is known. Texts marked [inferred] come from the code, not from a seen dialog.

| Dialog | Answer | Status |
|---|---|---|
| Career job offer ("Should Justine leave her current position, Squad Leader (job level 4)?", change job / keep job) | keep job (rule) | was "no safe button" in H1 [verified text] |
| Opportunities (career joins, skill offers) | cancel (safe default) | [verified] |
| Date ask, prom, phone invites | decline/cancel (invite rule) | [inferred texts for prom] |
| NPC move-in request | cancel (rule) | [inferred text] |
| Stray pet adoption | no (rule) | [inferred text] |
| Field trip, visit a friend, play date | cancel (rule or safe default) | [inferred texts] |
| Boarding school homesick | no (rule) | [inferred text] |
| Long-distance move offer | no (move rule) | [verified] |
| Alien pregnancy (accept/reject labels) | reject (safe word) | [inferred label] |
| Career branch choice, career event choice | none: every answer changes the household | still fails fast with the "no safe button" line |

### Audio (item 4)

No harness code ever set audio. The 11:53 change in docs/226 was a one-time manual edit (backups `Options.ini.bak-2026-11-02`, `.bak-audio-`, `.bak-gfx-2026-11-02` in `Documents\Electronic Arts\The Sims 3`). At 19:51:46, during H-move run 2, the game rewrote Options.ini with every option at its default: audio levels 255 and mutes 0, `resolution = 1024 768 60`, `advancedrendering = 1`, high detail, and `lastdevice` changed from `0;8086;7d45;8826` (the Intel GPU) to `0;10de;1080;65535`, the GeForce GTX 580 that `dxvk.conf` (customVendorId/customDeviceId, written 12:02) reports [verified: file diff]. A changed device resets the options [inferred]. Why the reset came only at 19:51 is not explained. Fixed for all runs by setting audio before every launch (6653071); the Steam machine gets the same.

### Launch hang (item 5, static)

| Launch | Gap since the previous game exit | Previous exit | Options.ini in effect | Result |
|---|---|---|---|---|
| H-move 1 | 61 s | H1 clean `quit_game` | pre-reset (1600x900, low) | ok |
| H-move 2 | 29 s | kill | pre-reset; reset to defaults while running | ok |
| H-move 3a | 15 s | kill | defaults (window place moved to 549,378 from 350,117) | hung |
| H-move 3b | 20 s | kill | defaults | hung |
| H-move 3c | 36 s | kill | defaults | hung |

- selectedflags was `001fefff` ("already") in every launch, so it is not the difference [verified: probe logs].
- The gap does not separate good from bad: H-move 2 started 29 s after a kill, 3c hung 36 s after one [verified].
- No process listing existed for any launch, so leftovers cannot be compared; ts3probe now logs them (d733119).
- The EA app reported `authenticated: false` in all runs, the good ones too.
- The DXVK log of the last hung start (`Sims3Launcher_d3d9.log`, 865 B) ends after `Found device: Intel(R) Graphics` and `Process set as DPI aware`, before any device or swap chain line; a good start (18:54) left 13,685 B. `DeviceConfig.log` was created empty at 20:27:31. Hung processes stayed flat at about 80 MB private, 103 MB working set [verified: files, sampler].

Proposed cause [inferred, not shown]: the three hung launches are the first three after the game reset Options.ini for the spoofed DXVK device. With the defaults (1024x768 at 60 Hz, advanced rendering, high detail) the game stalls in D3D9 device or swap chain creation through DXVK on the Intel GPU, before scripts and the agent load. A short relaunch after a kill is at most a weaker second factor. Nothing points at a licence or entitlement step.

### Next vanilla rerun

1. Without the game: copy the graphics keys of `Options.ini.bak-gfx-2026-11-02` (resolution 1600 900 0, low detail, `advancedrendering = 0`, ...) into the current Options.ini and keep `lastdevice = 0;10de;1080;65535`; restoring the backup whole would bring back the Intel device and trigger the reset again. Audio is now set by the probe.
2. First launch: if the agent answers, the cause above holds. If it hangs, the new guard ends it in about 90 s with `hung at launcher stage` and kills the leftovers; read the EA process line, then try `dxvk.conf` without the custom device id (one change at a time).
3. Then the vanilla matrix queue with the ea169-port 4c15cdf build as `TS3_AGENT_PACKAGE`, vh-move first. Per job read: the clicks log (`dialog_expect accept`, `expired (no match)`, any blocked dialog), the `N/A (vanilla)` line, and the audio and EA process lines in the probe log.

## Round 4 results (2026-11-02, EA laptop, vanilla matrix rerun, stopped at the launch)

Options.ini fix (game closed): every key of `Options.ini.bak-gfx-2026-11-02` copied into Options.ini except `lastdevice`, which stays `0;10de;1080;65535` (21 keys changed: resolution 1600 900 0, fullscreen 0, low detail incl. drawdistance 1, advancedrendering 0, audio levels 0 and mutes 1). Diff against the backup afterwards: only the `lastdevice` line. Pre-fix copy: `Options.ini.bak-reset-2026-11-02`. The game did not reset the file in any of the launches below. ts3agent = `build/ea169/ts3agent.package` of 4c15cdf, 458,586 B, sha256 ec1e97c67347 (clean tree). vanilla.ts install and read-back OK.

| Job | Verdict | Reason |
|---|---|---|
| attempt 0 (vh-cycle-a, launch test) | harness environment error, not a game result | `python` on the PATH is a pyenv shim that `spawn('python')` cannot run (exit 9009): the probe server never started; fail-fast ended it after 60 s. Fixed for the session by putting `pyenv\versions\3.13.5-win32` first on PATH. |
| launch 1 (vh-cycle-a, house Options.ini, faked GTX 580 device) | HANG | `hung at launcher stage: Sims3Launcher.exe up at 283 MB committed with no growth for ~60 s, agent never answered`; leftovers killed by the guard. `Sims3Launcher_d3d9.log` ends at `Found device: Intel(R) Graphics (Intel Corporation 101.8826.0)` and `Process set as DPI aware`. Options.ini was not the cause. |
| launch 2 (same, `d3d9.custom*` lines commented out in dxvk.conf, `lastdevice` set to the Intel id for that try) | HANG | same message, same DXVK log end. The faked device is not the cause. |
| vh-move, vh1-soak, vh-cycle-a/b, vh-travel-matrix, vh-travel-trip, vh-newsave, vh-matrix, vh-repro, hf-statecmd, hf-kill | not run | stopped at the launch (instruction: no variant works). |

vh-move answer: none (not reached). Unknown dialogs: none seen. Harness failures: none from the code under test; the launch hang reproduces with the new fail-fast guard working as designed (about 90 s, leftovers killed, EA Desktop left alone). The probe logged `EA processes before launch: EABackgroundService.exe, EADesktop.exe` (no leftover launcher).

Open: the hang is at DXVK Vulkan device or swap chain creation on the Intel iGPU, before the agent loads, in a state that worked at 18:54. Not tried: reboot or EA app restart, removing DXVK for one launch, a launch from the EA app by hand, Intel driver state (101.8826.0).
Root cause of the launcher hang (found later 2026-11-02): a fast user switch at 19:50:58. Another account took the console, our Windows session became Disconnected, and D3D device creation blocks without a display; the Open item above (Intel iGPU, DXVK) was a wrong lead. Locked sessions on the console launched fine. Fix: launch preflight in ts3probe.py (WTS API, `session-check`, queue.ts throws so the job is FAIL), main 56a4f10.

Restore: dxvk.conf sha ad935e6920de, Options.ini byte-equal to the house copy (`lastdevice` `0;10de;1080;65535`), Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14, scriptCache db69b1b6a862, Saves listing equal after `vanilla.ts saves-restore`, Mods listing equal, Bin listing equal except dir times and `Sims3Launcher_d3d9.log`. The 18 GB "vanilla-stash saves" folder is untouched.

## Round 5 results (2026-11-02, EA laptop, vanilla matrix, stopped after vh-move)

Session preflight (56a4f10): `console session state: session 2 Active, console held by yes` before the run and before every job. ts3agent = ea169-port 4c15cdf `build/ea169/ts3agent.package`, 458,586 B, sha256 prefix ec1e97c6734f (the brief said ec1e97c67347; size and clean tree at 4c15cdf match). Real python first on PATH. Options.ini checked, not changed (1600 900 0, fullscreen 0, audio mutes 1, `lastdevice` `0;10de;1080;65535`). Launch: no hang this time (the Round 4 hang was the disconnected session).

| Job | Verdict | Reason |
|---|---|---|
| launch (start of vh-move) | PASS | world loaded after 43 s, "Mod Scripts Found" answered by rule, agent answered, 37 households |
| vh-move | PASS (13.4 min) | offer armed 13:32:55, dialog_expect accept-on-match at 13:33:25 (`-> #2 yes`), game up 10 min after, no crash, no xcpt, 0 new script errors; the follow-up dialog "Moving to a New Town" was cancelled by the safe default, so the household did not move |
| vh1-soak | stopped, not a result | queue killed at its start (see unknown dialogs) |
| vh-cycle-a/b, vh-travel-matrix, vh-travel-trip, vh-newsave, vh-matrix, vh-repro, hf-statecmd, hf-kill | not run | stopped after vh-move |

vh-move answer: vanilla 1.69 does not crash when the offer is accepted (10 min up), but the accept only opens the move flow; the next dialog "Moving to a New Town" got the safe-default cancel, so no move happened. Crash 0x00AD04EC (7h/7j2) is not reproduced because the move itself was never carried out.

Unknown dialogs (no rule, safe-default cancel `#3 cancel`; the clicks log keeps the chosen button only, not the button list): "Moving to a New Town" (13:33:25), "Committee Hearing" (13:36:55), "The Dish Showdown" (13:37:25), "The Complete Circuit" (13:38:25). Per the fail-fast rule the matrix was stopped here. Fix to make: log the full button list for safe-default clicks, add rules for these four titles (and decide whether the move flow should be completed to test the move).

Harness notes: `ai-usage.ts` failed (ignored, no usage file). Options.ini differs from `Options.ini.bak-gfx-2026-11-02` only by `enabletelemetry` (0 now, 1 in the backup) and `lastdevice`; changed by the game or EA app during the run, left as is. Results file `2026-11-02-queue-vanilla-matrix.ea-laptop.md` committed like the ea-smoke one.

Restore verified: Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14, scriptCache db69b1b6a862, Saves mirrored back, no game process left. The 18 GB stash folder untouched.

## Round 6 results (2026-11-02, EA laptop, vanilla matrix, stopped at vh-travel-matrix)

ts3agent = ea169-port, final build b58ab59 (`build/ea169/ts3agent.package` sha256 prefix 995699294ac4; the runs used 495d100+history `ed69f42a0229` for vh-move, 995699294ac4 for the vh1-soak rerun). Harness = main 50fffc0 plus the dump fix below. Session check before every job: `session 2 Active, console held by yes`. Real python first on PATH, window never focused, Saves backed up before and mirrored back after every job (vanilla.ts saves-backup/saves-restore; the 18 GB stash left alone).

Fixes in this round (ts3agent 7120ef2, 495d100, 6528ff4, b58ab59; main d5a55ea, 50fffc0, dump fix): safe-default clicks log the full button list; decline rules for the three event dialogs and "Moving to a New Town"; `dialog_expect {title, accept_all:true, all_seconds, seconds}` (title trigger starts a window that accepts every dialog, rules and safe default off); click history that `autopilot` polls cannot drain (`dialog_expect {status:true}`); safe-default clicks are a WARN line in the job verdict; vh-move waits 6 min for the offer.

| Job | Verdict | Reason |
|---|---|---|
| vh-move run 1 | void (harness bug, fixed 6528ff4/50fffc0) | the household moved, but the `autopilot` poll that drained the click lines timed out while the game loaded the new world, so the script saw no click and printed UNKNOWN not triggered |
| vh-move run 2 | PASS (12.8 min) | offer at 14:10:28, accept_all clicked 4 dialogs in the same second (`#2 yes`, "Moving to a New Town" `#2 accept`, "preparing to move" `#2 yes`, save prompt `#3 don't save`), game left the world 26 s later (`out of the world`, later `PlayFlowState` in IslaParadiso.world), game up 10 min after, no crash, no xcpt |
| vh1-soak | PASS (12.3 min, WARN 4) | 10 min ultra soak, 0 script errors; safe-default clicks below |
| vh-cycle-a | PASS (4.4 min) | 2 min soak after the fresh start |
| vh-cycle-b | PASS (4.2 min) | second fresh start, vanilla read-back passed |
| vh-travel-matrix | FAIL (harness) | save-matrix.ts exits 2 at once: `no ts3native.log at ...Bin\ts3native.log`. save-matrix needs the ts3native log, which vanilla mode removes. Real harness failure: matrix stopped (fail-fast) |
| vh-travel-trip, vh-newsave, vh-matrix, vh-repro, hf-statecmd, hf-kill | not run | stopped after vh-travel-matrix |

vh-move answer: the household moved and vanilla 1.69 did not crash in the 10 min after it. The move flow is all dialogs (offer, "Moving to a New Town", "preparing to move", save prompt); after them the game sits in the new world's play flow (`PlayFlowState`, active household none), which needs a household or lot pick in the UI; that was not clicked. Crash 0x00AD04EC (7h/7j2) did not reproduce on vanilla, so it needs the ts3native/ts3fixes of 7h/7j2 to appear.

Safe-default WARN dialogs (vh1-soak rerun, buttons as logged): "Learn Martial Arts" `#3 cancel` (buttons #2 accept, #3 cancel, #18 see location); "Add It Up" `#3 cancel` (#2 accept, #18 see location, #3 cancel); "The coach wants Marty to start drinking a new energy drink..." `#3 decline` (#3 decline, #2 drink it!); "Super Stir-Fry" `#3 cancel` (#3 cancel, #2 accept, #18 see location). First soak run: "Add It Up" and "Hot Dogs, Please" (no button list yet, older agent). These are opportunity offers; declining is safe, no rule needed.

Kill dumps: four ~2 GB full dumps (`probe-out/dumps/ts3w-<pid>-...`, PIDs 15424, 24816, 33964 and 42604) were written at the end of every soak job, not by a hang or crash. Cause: the soak runs `watchdog.ts` with the soak length as its deadline, and queue.ts `run()` treats that deadline as "job deadline" and calls `dumpHungGame` before killing the child. The game was healthy (PASS rows). Harness misfire, fixed: soak and routelock pass `plannedEnd` and skip the dump. Disk: C: 310 GB free, the dump planner also refuses below 10 GB free.

Restore verified: Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14, scriptCache db69b1b6a862, Options.ini byte-equal to the start of the round (50cb641832f8), Saves identical to the backup, no game process left.

## Round 7 results (2026-11-02, EA laptop, vanilla matrix, stopped at vh-repro)

Same set-up as Round 6: ts3agent ea169-port b58ab59 (`build/ea169/ts3agent.package` sha256 prefix 995699294ac4), real python first on PATH, `TS3_LAUNCH=ea TS3_EXE=Sims3Launcher.exe TS3_MACHINE=ea-laptop`, vanilla stash installed once and read back before every job (`queue.ts --only <job>`), session check before every job (`session 2 Active, console held by our session`; the preflight wording is fixed, was "held by yes"), window never focused, Saves backed up before and mirrored back after every job. No safe-default click and no unknown dialog in any job (the move offer was declined by its rule, "Check Out Your Smart Phone!" had one button).

Harness fixes (main 6739491, 1854755, 3837896, a53c09b): save-matrix.ts and the session line (6739491), store-cas-browse waits (1854755), empty-button window (3837896), stale `blocked` text (a53c09b, not run against the game).

| Job | Verdict | Reason |
|---|---|---|
| vh-travel-matrix | PASS (7.8 min) | travel case to University: travel save, save away, trip home, save home all done; per-save compress, flush, footprint, fallback checks and the fix arms are listed as `skip ... N/A: vanilla`. The trip-out and trip-back travel save were not seen through `save_in_progress` (polled about 1 s), so those two checks are N/A too |
| vh-travel-trip | PASS (7.5 min) | one round trip to France: out 90 s, back 43 s + 69 s, commit 2004 -> 2082 MB |
| vh-newsave | PASS (3 min) | menu to Sunset Valley, Andrews household (2 Sims), saved as Vanilla-SV-fresh in 42 s (scratch save removed by the Saves mirror) |
| vh-matrix | PASS (5.5 min) | control, identical, overwrite, back-to-back, unpaused: 5 of 5; result check is the save folder plus `save_in_progress`, no result code in vanilla |
| vh-repro | FAIL x3 (harness), not a result | see below |
| hf-statecmd, hf-kill | not run | stopped after vh-repro; the two-cycle limit was used up |

vh-repro failures, all harness, none a game result:
1. 2.7 min: `cas_open` is a queued game task and CAS needed 59 s to open on this laptop; the fixed `waitS 30` then hit `cas_category` with "CAS is not open". Fix: `until` on `cas_state` (cas_ready, buy_catalog, back in LiveModeState) for the four open/close steps (1854755).
2. 4.1 min: the CAS entry shows "Entering Create a Household..." (no buttons) and the vanilla mode guard stopped the job after two polls. Fix: a window with an empty button list is a block only after 60 s (2 x GUARD_MS); a dialog with buttons but no safe one still stops at the second poll (3837896).
3. 6.8 min: all CAS and Buy steps passed (CAS open 59 s, 8 CAS categories 0.3-2.8 s each, Buy open and 6 categories 0.1-1.3 s), then the guard stopped on `'' buttons:` about two minutes after a blank Buy-mode transition window had closed. The agent keeps `blocked` until its next successful click, so the same stale text was seen twice. Fix: the guard asks `pending_dialogs` and drops the text when no modal is open (a53c09b). Not run against the game: that was the third fix-and-rerun and the limit is two. The CAS and Buy timings above are from a run that the guard ended, so they are indicative only (machine load other CPU 58%, NOT comparable).

Vanilla job count: 11 jobs. PASS so far: vh-move, vh1-soak, vh-cycle-a, vh-cycle-b (Round 6), vh-travel-matrix, vh-travel-trip, vh-newsave, vh-matrix (Round 7) = 8. vh-repro (fix 3 untested), hf-statecmd and hf-kill are still open; hf-kill FAILs by design. Next round: `queue.ts ... --only vh-repro`, then hf-statecmd (it also changes modes, so it needs fix 3), then hf-kill.

Restore verified: Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14, scriptCache db69b1b6a862, Options.ini byte-equal to the start (50cb641832f8), Saves identical to the backup (robocopy /MIR list run: 0 differences), no game process left. The 18 GB stash and the four 2 GB plannedEnd dumps in probe-out/dumps were left alone.

## Round 8 results (2026-11-02, EA laptop, vanilla matrix, last three jobs)

Same set-up as Round 7 (ts3agent ea169-port b58ab59, sha256 prefix 995699294ac4; real python first on PATH; `TS3_LAUNCH=ea TS3_EXE=Sims3Launcher.exe TS3_MACHINE=ea-laptop`; session check `console held by our session` before every job; window never focused; Saves backed up before and mirrored back after every job). Harness environment slip, not counted: the first launch of vh-repro ran with the pyenv shim first on PATH (probe server exit 9009, as in Round 4), nothing ran; rerun with `pyenv\versions\3.13.5-win32` first on PATH.

| Job | Verdict | Reason |
|---|---|---|
| vh-repro | PASS (7.2 min) | store-cas-browse: CAS open 58 s, 8 CAS categories, Buy open and 6 categories, back to LiveModeState; the stale-`blocked` fix (a53c09b) held, no guard stop. No safe-default click |
| hf-statecmd | PASS (4.9 min, after 2 harness fixes) | buy_open (53 s), buy_close (15 s), enter_live_mode all ran and the unrequested-mode-exit guard stayed quiet. Clicks: the move offer and one date offer answered by rule, no safe-default (WARN) click |
| hf-kill | CRASH, i.e. FAIL by design (7 min) | game killed 60 s into the job (`taskkill /F /IM Sims3Launcher.exe`); the queue ended the child through the probe-server fail-fast guard ("probe server exited (0) for ~60 s"), row CRASH, exit 1, not PASS. "no crash report of this run written since ... with pid 26840" is the xcpt triage line (a kill writes no xcpt). Vanilla state restored (7 files, sha read back), no game process left |

hf-statecmd failures, both harness, none a game result:
1. 20 s fixed wait after `buy_open` (a queued task, Buy took 53 s here): `buy_close` answered "Buy mode is not open". Fix c198539: poll `cas_state` (buy_catalog / LiveModeState) up to 180 s.
2. The c198539 poll read `buy_catalog` and `state` from the top level of the agent reply but they are under `result`, so it never saw Buy open and failed after 180 s. Fix c85cc13: read `result.*`; also enables autopilot with dialogs (a modal move offer blocks `buy_open`) and logs the `buy_open` reply and, on failure, the last `cas_state` and `pending_dialogs`. One extra diagnostic rerun was needed to see the reply layout (the third run of the job, no fix in between).

Final vanilla matrix, 11 jobs:

| # | Job | Verdict |
|---|---|---|
| 1 | vh-move | PASS (Round 6) |
| 2 | vh1-soak | PASS, 4 safe-default WARN (Round 6) |
| 3 | vh-cycle-a | PASS (Round 6) |
| 4 | vh-cycle-b | PASS (Round 6) |
| 5 | vh-travel-matrix | PASS (Round 7) |
| 6 | vh-travel-trip | PASS (Round 7) |
| 7 | vh-newsave | PASS (Round 7) |
| 8 | vh-matrix | PASS (Round 7) |
| 9 | vh-repro | PASS (Round 8) |
| 10 | hf-statecmd | PASS (Round 8) |
| 11 | hf-kill | FAIL by design, reported as CRASH with the right reason (Round 8) |

10 of 10 PASS jobs pass and the one designed failure fails correctly: the harness now passes all vanilla flows. Each PASS was in a separate `--only` run, not one uninterrupted queue run, so the full-queue run (install once, restore once) is not demonstrated end to end.

Restore verified: Bin pair c035abe5a3a6 / 8aa37522782a, ts3fixes ac2c3f8799af, original ts3agent 1921c00c4c14, scriptCache db69b1b6a862, Options.ini 50cb641832f8 (byte-equal to the start), Saves identical to the backup (robocopy /MIR /L: 2602 files skipped, 0 to copy or delete), no game process left. The 18 GB stash and probe-out/dumps were left alone.
