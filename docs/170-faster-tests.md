# 170. Faster tests

Rodney 2026-10-22: cut test time. Other sections of this doc come from other agents; this file was created with §3 only.

## 1. Several repros per load: `run.profile.repros`

Built, self-checked, dry-run only; **no game run yet** (ledger §272).

A profile job may list `repros: ["id", {id, minutes?, fixesRequire?, hitchTasks?, nativeRequire?, frameLead?}, ...]`. `queue.ts` loads the save once and runs one `profile-world.ts` window per repro, back to back. Each window is a separate run for every reader:

- tag `<job tag>-<repro id>`. The queue log is `probe-out/queue/<tag>.log`, the file `runContext` reads. The window has its own profiler files, `stop.json`, `calls.jsonl` and `stuck.json`, and the repro's own `loop` (arms, stop rule). `hitch-ab.ts --scenario <job tag>` and the other prefix readers list each repro as its own run.
- Pass rules per repro. Item fields override the job's profile fields, and profile-world checks `fixesRequire`, `hitchTasks` and `frameLead`. `nativeRequire` is checked over that window's own `/native` delta.
- `<tag>-HHMM-ts3native.log` keeps the start-up part of the log (hook and ini lines) and only that window's lines (`logCopy`). So `--causes` and `logRequire` never see an earlier window's sync plans.
- The row gets `row.repros[]`: id, tag, load, `sinceLoad`, exit, stop line, stuck count, verdict, log slice and the carry-over line after it. The md verdict has one line per repro.
- Between two windows the carry-over check of §2 runs, with the job's `reuseSession` limits (default `REUSE_DEFAULTS`). When a check fails, the game reloads (close, restart, the same asi and ini, load). A failed repro reloads and goes on at night. In the daytime it stops the job and the queue.
- Not combinable with `profile.repro`, `ab`, `untilCompares` or `travel`.

## 2. Session reuse across jobs: `reuseSession`

`reuseSession: true | {maxJobs, simDelta, membersDelta, lotObjectsDelta, vasMB, minLargestFreeMB, simHours, stuck, onFail}` can be set on a job or on the queue. It is off by default. A job takes over the running game of the job before it when four things hold:

- reuse is on for both jobs;
- both are profile runs (not travel, not `menu`);
- both have the same game start: asi path and sha, merged ini, fixes, save, content, gameArgs, defaultIni, s3ppIni, s3ssToml, dxvkConf and pin (`keyOf`, a stable key);
- the carry-over check after the earlier job passed.

Only runtime switches differ between such jobs: `fixesOn` / `fixesOff`, `--ab`, and `native_set` steps.

**Carry-over check** (`session-reuse.ts`; it runs after every window that hands the game on):

1. Every ts3fixes switch (the `patches` booleans) and every ts3native runtime switch (`native-switches.ts`: `/native` modes plus the `Ts3nCounters` offsets) goes back to its value right after the load. The check then reads them back. A value other than 0/1 cannot be written through the probe; that counts as a failure. This covers `--ab`, which leaves its switches on at the end, and `native_set` loop steps, which nothing undid before.
2. A world snapshot is compared with the snapshot taken right after the load. It holds the sim clock, the Sims in the world (`sims_activity`), the active household and its members, the active household's home lot and its object count (`list_objects`), and commit and largest free block (`/memory`).
3. The next window gets a fresh start when any of these holds:
   - `maxJobs` windows ran since the load (default 4);
   - the Sim count moved by more than 8;
   - the household members changed;
   - the active household or lot changed;
   - the lot object count moved by more than 5;
   - commit grew by more than 300 MB, or the largest free block fell below 150 MB;
   - more than 48 sim hours passed;
   - a window left a stuck Sim (`<tag>-stuck.json`);
   - a repro cleanup failed;
   - a switch did not go back;
   - the snapshot was incomplete.
   All limits are configurable.
4. Counter baselines are not part of the snapshot. Each job, and each repro, reads `/native` and the patches counters at its own start, so no delta spans two windows.

**Failure:** a crash or failed window ends the session. With `onFail: auto` and `TS3_NIGHT=1` (set by `night-run.ts`), the queue goes on with a fresh start. Daytime (no `TS3_NIGHT`) stops the queue. `restart` and `stop` force either rule. The existing crash rules (`crashStop`) still apply.

**Rows:** each row records `row.session`, which is one of:
- `fresh load`;
- `reused session (#n since load)`, plus the drift line after the window (`drift ok (Sims 43 (+3), members 4, lot objects 200 (+0), commit +120 MB, ...)` or `drift: <reasons>`) and `kept for <next>` / `fresh start for <next>`. The md note shows it.

**First minute:** profile-world gets `--since-load n` and writes `<tag>-session.json`. When n > 0, `worst-hitches.ts baseWins` (via `afterLoad`, used by `runContext`, so also by hitch-ab and freeze-report, and by `frame-lead.ts`) excludes only a 15 s profiler start edge, not the first minute. A repro's first step whose note starts with `lead-in` (the 75 s wait in the scenario repros) is cut to 15 s. Without the file, behaviour is unchanged.

**Frames:** `frame-times.js` stays loaded until the game closes, and a second session refuses the hooked prologue (I-170-12). So one session per load writes `probe-out/profile/<queue>-<date>-<job>-load<n>-<time>-frames.csv` (`--frames-shared`). Every window copies its own epoch span into `<tag>-<secs>-frames.csv` (`sliceFrames`), so readers see one file per run as before.

**Time saved [inferred, from 229 earlier 10 min profile rows]:**

| Item | Per window |
|---|---|
| Job overhead now (restart, launch, load, close), median 11.8 min per job | 1.6-3.4 min |
| Reused window cost (carry-over check, ~55 agent or probe calls, plus the same profile-world setup) | well under 1 min (I-170-15) |
| Restart and load avoided | ~2-3 min |
| Real measured data gained: the excluded first minute becomes a 15 s edge | +45 s |
| Scenario repros: 75 s lead-in cut to 15 s | ~1 min |

Net: roughly 20-30% of each reused job's wall time. Over 26 grouped release jobs that is ~1 h.

**Not covered or open:**
- the stutter profiler attaching twice in one load (I-170-11);
- whether reused windows compare with fresh ones (I-170-14: an ABBA fresh/reused check before reuse goes into measurement A/Bs);
- `ts3native-shadow.jsonl` is copied whole per job in a shared load (shortcut);
- `worst-hitches.ts classify` still labels hitches in seconds < 60 `first minute after load` (a cause label only, not an exclusion);
- the release-check generator (§4) still emits one load per job.

**Checks [verified]:**
- `node tools/harness/session-reuse.ts`: options, keys, blocks, repro items, lead-in cut, frames slice, log copy, and a fake game with switches flipped by a job. Restore and read-back pass, every drift limit trips on its own, a mode value cannot be restored, and a deaf patches answer fails.
- `queue.ts --self-check`: reuseSession at queue and job level, repros validation.
- `worst-hitches.ts --self-check`: reused start edge and `afterLoad`.
- `hitch-ab`, `freeze-report` and `frame-lead` self-checks still pass.
- `queue.ts tools/harness/queues/faster-tests-example.json --dry-run`: 3 repros in one load, then 2 jobs reusing it as windows #3 and #4.
- The existing queues (`scenario-hitches`, `overwatch-loop2`) dry-run unchanged, all fresh.

## 3. Adaptive waits per firing

Built and self-checked; **no game run yet**. Loop repros used fixed waits after each firing (`waitS` 20-45 s), sized for the slowest arm. A firing now ends when its effect is done.

**Step format** (`tools/harness/repro-loop.ts`, any step, plain or loop body):

- `waitUntil`: one condition, a list (all must hold), or `{on: ..., off: ...}` picked by the arm in the step note (`fire off #3`). Conditions:
  - `{counter: name | [names], rises?: n, of?: name, slack?: n}`: ts3fixes counters (agent `patches`, summed for a list) rose by at least `n` (default 1) since just before the step. With `of`, the rise must also reach `rise(of) - slack` while `rise(of) > 0`: a sliced pass has caught up with what was queued.
  - `{quiet: ms, iterMs?: 100}`: no sim iteration of `iterMs` or more for `ms`. The runner sends back-to-back agent `ping`s (50 ms apart). The mailbox answers once per sim tick on the sim thread (`Mailbox.Poll`), so a ping's round trip spans the iteration in progress [verified by reading `Mailbox.cs`; live check I-170-31]. No new native counter is needed. Quiet counts from the step's return, so `minS` must cover any alarm delay.
  - `{taskDone: name}`: the stutter profiler's task records through `io.taskDone`. The API is there, but profile-world does not wire it yet and no repro uses it.
- `maxS` (cap, required) and `minS` (floor). The step ends at the first poll where every condition holds and `minS` has passed (`signal`), or at `maxS` (`cap`).
- `between: "normal" | "fast"`: the step's wait runs at ultra, then this speed is set again. This is only for setup between firings whose effect does not depend on wall time, never for a firing. No current repro uses it. The route repros already run at ultra throughout. The hitch repros fire at normal speed, and their between-firing steps (`patches` 1 s, `strand_many` 3 s) are fixed agent-side waits, not sim time, so ultra would save nothing.

**Wrong-arm risk:** the next firing starts only after the signal or the cap, never earlier. Each adaptive step logs `firing #n done after X s (signal|cap) [note]`. A capped step's effect may run into the next firing: the loop's stop line adds `N steps capped`, and `hitch-ab.ts --scenario` lists every capped step per run so its window can be checked. `maxS` is the old `waitS` everywhere, so a capped firing takes no longer than before. `waitS` stays in each step as the fixed wait for runners that ignore `waitUntil`.

**Step-level API:** `runStep(step, io, read?)` reads counters, runs the step, waits, and returns `{r, secs, why, rises}`. `runLoop` uses it, so `bp-overwatch-ab-loop2` (loop form) gets adaptive waits now. **Hand-written repros (all the others below) need one line in `profile-world.ts`** (owned by another agent's open change): replace `await reproStep(s); if (s.waitS) await sleep(s.waitS * 1000);` with `await runStep(s, { ag, step: reproStep, sleep, now: Date.now, halted: () => stopped || Date.now() >= at(secs) });`. Until that lands they keep their fixed `waitS`. Their windows stay at `--minutes` until they move to `loop` form with a stop rule (docs/168 §6); only then does the shorter firing shorten the job.

**Signals per repro** (counters verified by reading `mods/ts3fixes/src`; times [inferred] from 1 sim-minute ≈ 1 s at normal speed (I-170-32), docs/147 tick counts and the docs/169 loop notes):

| repro | firing step | signal off / on | old s per firing | new s per firing (est.) |
|---|---|---|---|---|
| `hitch-stuckcheck-loop`, `-check` | `StuckCheckNow(0, 3)` | quiet 3 s / `stuckcheckasync_run + _dropped` caught up with `_queued`, + quiet 3 s; minS 4, maxS 20 | 24 (arm 1 + strand 3 + 20) | ~9-11 |
| `hitch-easpread-lots-*` | `EaSpreadNow("lots")` | quiet 3 s / `easpread_lots_passes` or `_stopped` +1; minS 4, maxS 23 | 24 | ~5-6 |
| `hitch-easpread-pets-*` | `EaSpreadNow("pets")` | quiet 3 s on both arms (with full pools ours adds no counter); minS 4, maxS 23 | 24 | ~5-6 |
| `hitch-easpread-decay-*` | `EaSpreadNow("decay")` | quiet 3 s / `easpread_decay_passes` or `_finished_early` +1; minS 4, maxS 23 | 24 | ~5-6 |
| `hitch-errortrap-*` | `ErrorTrapSpreadNow` | `etspread_passes + etspread_theirs` +1 on both arms; minS 2, maxS 23 | 24 | ~3-7 |
| `bp-overwatch-ab-loop`, `-loop2` | `OverwatchSpreadNow("all", 1)` | `owspread_theirs` +1 and quiet 2 s / `owspread_types_run` caught up with `_scheduled` minus 1 (the inventory type counts in `_inv_passes`, or not at all with its setting off); minS 3, maxS 45 | 46 | off ~4-5, on ~23-25 (22 types 1 sim-minute apart) |
| `route-stuckcheck-reset-loop` | `stuck_check_fire` | stuckcheckasync caught up + quiet 4 s (both arms; the job turns `stuckcheckasync` on); minS 4, maxS 28 | 31 | ~12-15 |
| `route-queuefull-replan-loop` | `build_mode off` "exit on/off #n" only | quiet 3 s; minS 3, maxS 10. The home leg stays fixed: its Sims walk through doors, and their replans must not reach the next arm | ~40 per loop | ~33-35 |
| `route-nopath-crowd-loop` | `crowd_unreachable` | quiet 4 s; minS 6, maxS 18 (home leg fixed) | 32 | ~20-24 |
| `route-sv-seacheck-freeze` | `stuck_check_fire` | stuckcheckasync caught up + quiet 4 s; minS 4, maxS 30 (walk to the sea lot stays 40 s fixed) | ~72 | ~50-55 |

The 20-firing hitch loops drop from ~480 s of firings to ~100-220 s. In loop form with a stop rule, a 10-per-arm job would end at ~3-5 min after the lead-in instead of 10 min.

**Known limits:** quiet cannot tell "done" from "between slices" for a pass that is sliced into ticks under `iterMs`, so sliced on-arm passes use a counter as well. In Overwatch, if `CleanupInventory` holds the last slot and its setting is on, the on-arm firing ends one type (~1 s) early. Exceptions in the stuck-check drain count twice (`run` and `dropped`), so a firing can end that many checks early. `quiet` pings add ~20 agent calls per second on the sim thread while a firing waits (I-170-34).

**Checks [verified]:** `node tools/harness/repro-loop.ts` uses a fake game with a clock, counters as functions of time, and slow iterations that hold a ping until they end. It covers the counter signal, the minS floor, the cap and its log line, `of` with and without `slack`, `of` with nothing queued, counter lists, quiet with slow iterations above and below `iterMs`, combined conditions, on/off by note arm, a missing arm, `between` speed calls, `taskDone` with and without `io.taskDone`, loop capped counts in the stop line, validation, and all 178 adaptive steps in `repros.json`. Also run: the old loop fixtures unchanged, `hitch-ab.ts --self-check` (`cappedSteps`), `queue.ts --self-check`, and `queue.ts overwatch-loop2.json --dry-run`. Not checked in game: ledger §273.
Sections 1-3 come from another agent (created here with §4 only).

## 4. Release check in hours: `tools/release-check.ts`

**Built 2026-10-22, static only: self-check, queue generated, `queue.ts --dry-run` ok on a scratch copy with the derived repros installed; never run in game.** Ledger §274.

Rodney 2026-10-22: a release should be checked in hours, not a night. The v0.1.4 `release-validate.json` ran 46 fixed jobs (~8 h) whatever changed. `release-check.ts` builds the queue from what changed since the previous release instead.

```
node tools/release-check.ts <prev tag|commit> [--features id,id] [--asi game-copy/ts3native.asi.rel020] [--min-counters 98] [--out file] [--write] [--install-repros]
node tools/release-check.ts --self-check
```

- **Ship set:** `--features` = the previous release's on features plus these ids. Without it, the features the repo `ts3native.ini` and ts3fixes tuning have on now (the state after `release-features.ts --write`). With `--features` set and different from the repo defaults, jobs carry ini and `fixesOn` overrides. The final run comes after `release-features.ts --write` and has none.
- **Previous release:** a tag or commit. Tags missing locally resolve through `feature-repros.json` `releases` (`v0.1.4` = `d6ff7a91`, the commit the v0.1.4 asi was built from; no git tags exist in the private repo).
- **Changed:** `git diff <prev> HEAD`. A feature counts as changed when one of its `paths` changed, or when a changed line of the shared files (`ts3native.cpp`, `ts3native.ini`, `Fixes.cs`, `Instantiator.cs`) matches its `match` regex. Selftest, `-test` and `-fake` files never count. A feature new in the ship set always counts as changed.

### Parts

| Part | What | Jobs | Pass |
|---|---|---|---|
| a smoke | BP/IP/SV, 6 min play each, release ini + measurement logging of `tool-check.json` (HitchAttr, RouteJobPhases, fallback log), `cameraPan`, seed 12345, no samplers | 3, always | 0 crashes or watchdog lines, version, `asyncStarted > 0`, the docs/RELEASE.md tripwires 0, route fails within 10% of v0.1.4 `*-rel-play` |
| b save matrix | `bp-rel-matrix` (shipped mode) + `bp-rel-travel-france` from release-validate.json | 2, always; SV/IP matrices and the other 5 trips come back in part e via `@save` when a save-path feature changed | as release-validate (every case PASS) |
| c fix on | per shipped feature that is new or changed: its forced repro with the fix **on only**. Loop repros (hand-written ABBA or `loop` format) become a fixed-count loop: arms `[on]`, `until {events: 6}`. Switches of features not shipped are forced to their off value; the target and other shipped switches go on. Plain forced repros run as they are for their minutes. | one per feature that has a repro | no crash, the stop line, counters rose (`fixesRequire` with `{n}` = on firings, `nativeRequire` per feature). Hitch lines that do occur should name the fix's task (`StuckCheckDrain`, `ErrorTrapSpreadNow`); read them with `hitch-ab.ts --scenario`. This is not a FAIL rule: a working fix leaves no hitch line. |
| d bundle A/B | per loop repro used in part c: all new ship-set switches flipped together (`$on` in one `native_set` + one `patches` arm step), ABBA, `until {perArm: 10}` (docs/168 §1 `MIN_EVENTS`, fixed before the run) | one per source loop repro | no crash, no tripwire, stop line; read per arm with `hitch-ab.ts --scenario`; on not worse than off beyond the docs/136 null |
| e rare | the release-validate.json jobs of shipped features whose code changed (`rare` per feature, groups `@route`, `@crowd`, `@water`, `@lifecycle`, `@unseen`, `@save`), sorted by save | by change | as release-validate |
| f soak | BP 45 min natural play, no repro | 1, always | no crash, 0 watchdog lines, stuck Sims within docs/TESTING.md; read real hitches (`hitch-ab.ts`, docs/161), `freeze-report.ts`, VAS growth from the row's commit/private/largest-free before -> after |

Derived repros are named `rc-on-<feature>` and `rc-ab-<source repro>`. `profile-world.ts` reads repros only from `repros.json`, so `--write` puts them in `tools/harness/release-check-repros.json` and `--install-repros` splices them as one block at the end of `repros.json`. The splice is text-only (the file is hand-formatted; nothing else is rewritten), and a rerun replaces the block. Install on the release branch when nobody else edits `repros.json`. `queue.ts` refuses a job whose repro is missing, so a queue run before the install fails at start and runs nothing.

### Feature map: `tools/harness/feature-repros.json`

Per feature: `on` (ini keys / tunables; ids that are in `release-features.ts` take them from there), `native` / `fixes` (runtime switch names, used for the loop arms and `fixesOn`), `values` (caps such as `synccapus`, never flipped by `$on`), `paths`, `match`, `repro` (+ `minutes`, `noEnv`, `onEvents`, `bundlePerArm`), `nativeRequire` / `fixesRequire`, `task`, `rare`, or `needsRepro` with the reason. 41 features: the v0.1.4 base (GC, WaterJps, Decompress, SaveWrite, LotRemoveGuard, OptsInit, RouteAsync, RouteGate, Discourage, the ts3fixes fixes on since v0.1.3/4), every release-features.ts candidate, the bundle fold-ins (earlyout, tailunlock, replandedupe, h2, legreplanskip, bouncerskip, idlememo) and stuckchecknear/failrepeat. The mapping is [inferred] from the docs per feature and the release-validate job notes. A wrong path or regex only moves a job between run and skipped; the tool lists both.

**Needs repro** (as built in §4; **§5 gives all but bouncerskip and lotremoveguard a forced loop**; no forced repro; covered by smoke, soak and bundle only): gc, decompress, savewrite (the save matrix is its repro), lotremoveguard, optsinit (every load), routegate (every route repro passes the gates), tailunlock, h2 (counted in crowd route runs, v015-allon-verify), unseenenv/unseenmotives/unseenbroadcast, legreplanskip, bouncerskip (needs a velvet-rope venue), idlememo, ts3fixes-v014. **Plain repro, no fixed-count loop** (part c runs for fixed minutes, part d cannot use it): waterjps, discourage, deferfootprint (`pet-guard-cancel`), destroypark (`rt-s2b-sweep`), slotenabledro (`steer-crowd`), avoidro (`avoid-crowd`), unseenthrottle (`unseen-crowd-far`), farthestsorted/indoorcap (`candidate-loops`), replandefer/replanspread (`replan-buildbuy-crowd`).

### Estimate for v0.1.5 (`node tools/release-check.ts v0.1.4 --features slotenabledro,unseenthrottle,stuckcheckasync,errortrapspread`, the suggested set of docs/RELEASE.md)

Changed since v0.1.4 in shipped code: `savewrite.cpp` (Apex stand-down), `routegate.cpp` (docs/165 F1 lock fast path on every gate release, Gate0RO/TailUnlock hooks), `discourage.cpp`, `rtsites.cpp`, ts3fixes `RouteFixes.cs`/`ScriptHotSpots.cs`, RouteAsync lines in `ts3native.cpp`.

| part | jobs | est. min | source |
|---|---|---|---|
| a smoke | 3 | 23 | measured overhead |
| b save matrix | 2 | 19 | matrix [inferred]: 3.3 min for 9 cases + 2 reload loads (4.7 min each), each measured apart; travel measured |
| c fix on | 6 | 43 | [inferred] from step waits + measured overhead |
| d bundle A/B | 3 | 40 | [inferred] from step waits |
| e rare | 26 | 230 | measured (v0.1.4 release-validate rows) |
| f soak | 1 | 47 | measured overhead |
| total | 41 | 402 (6.7 h) | profile overhead 1.7 min/job, median of the v0.1.4 release-validate rows |

With all 14 bundle candidates (`v015-bundle-ab.json` list): 48 jobs, ~8.0 h (c 11 jobs, d 5 jobs). Parts a-d + f alone take **~2.9 h**. Part e is large for v0.1.5 because the shipped routing gate code changed under every route path (`@route`) and the save write path changed (`@save`). A release whose shipped code is unchanged runs a + b + f (~1.5 h), plus c + d for its new features. Measured times come from `docs/data/results/*-queue-*.md` (PASS rows) and `*-save-matrix.md` runs with 9 or more cases. Loop jobs are estimated from their step waits; they end at the stop rule, and the cap is 1.5x that.

Order: a, b, c, d, e (by save), f. Split with `--only` if the day is short: a-d + f first, then e. Part d is a timing read: clean night only, as every A/B (clean-night rule). a-c, e and f are correctness and may run in the day.

### Coverage against the 46-job release-validate.json (for the v0.1.5 set above)

| Not run | Why acceptable / what replaces it |
|---|---|
| `*-rel-play` (12 min x 3) | smoke a (6 min x 3) + soak f (45 min BP): same pass rules, longer total play on BP, shorter on IP/SV |
| `bp-rel-stuck-unseen` (15 min) | soak f has the stuck Sims rule over 45 min; unseenthrottle has its own part c repro |
| `bp-rel-feat-*` (12) | features shipped on and new/changed get part c with their forced repro (fix on, counters) instead; features shipped off are not in the release (smoke and soak run their off path) |
| `sv-rel-rare-route-bits`, `bp-rel-rare-big-crowd` come back via `@crowd`; `sv-rel-rare-winter-pond`, `sv-rel-rare-death-walk`, `sv-rel-rare-horse-ride`, `fr-rel-rare-world-walk` | second world of a rare route mode that runs in its first world; a feature with a world-specific risk adds `@route-all-worlds` |
| SV/IP matrices and 5 trips (only when no save-path feature changed; for v0.1.5 they run) | save code unchanged: its matrices passed in the previous release; the BP matrix and the france trip run every release |

**Gaps that remain:** (1) save size after long play: the soak does not save (TODO behind session reuse: save at the soak's end in the same load); the matrix saves give size after short play only. (2) IP/SV get 6 min of play instead of 12 + rare jobs. (3) Interactions of a new feature with an unchanged feature's rare route mode are covered only by the bundle repros and the soak, not by that mode's rare job. (4) Features with no forced repro (list above) are checked only through smoke, soak and the bundle. Their counters are not required to rise in this queue; `v015-allon-check.ts` over the soak's dumps can check them.

### Multi-repro per load and session reuse

Built since (§1-2, `profile.repros` and `reuseSession`), not yet used here. The generator emits plain one-load jobs, so the queue runs today. A TODO in `release-check.ts` marks where grouping by save goes: part e is already sorted by save. Each grouped job saves ~1.7 min of load and profiler start, ~45 min over the 26 rare jobs. The soak's final save goes in the same load.

### Checks [verified]

`node tools/release-check.ts --self-check`: on-key matching (missing key = off), change attribution (paths, shared-line regex, test files ignored), derivation of hand-written ABBA and `loop`-format repros in both modes (target on, other shipped switches on, unshipped switches off, unknown switch warned, missing bundle switches added as an arm step, `checkLoop` valid), a whole build on fixtures, the measured estimate, the coverage rows, the text splice of `repros.json` (CRLF kept, rerun replaces, empty removes), and every map id, repro id and rare id against the real files. Also in CI (`build.yml` self-checks). `queue.ts tools/harness/queues/release-check.json --dry-run` on a scratch copy with the 6 derived repros installed: 41 jobs, 6 stop rules, exit 0. The only remark is the missing release asi `game-copy/ts3native.asi.rel020`, which is not built yet. Not checked: any game run (I-170-1..3, ledger §274).

## 5. Forced loops instead of the old rare jobs

**Built 2026-10-22, static only: self-check, `repro-loop.ts` self-check, `queue.ts --dry-run` of `repro-check-release.json` and of the regenerated `release-check.json` (scratch install of the 13 `rc-*` repros, restored after); never run in game.** Ledger §275.

Rodney 2026-10-22: release checks in ~2-2.5 h, not 6.7 h. §4 brought back 3.8 h of old rare jobs (part e) because `routegate.cpp` and `savewrite.cpp` changed since v0.1.4, and 14 features had no forced repro or only a plain one. Each of them except bouncerskip now has a forced loop: the triggering state is set directly, the loop fires many times per load and re-creates the state each firing, saves are real, waits are adaptive (§3) and the run stops at a fixed event count (docs/168 §6).

### New repros (`tools/harness/repros.json`, all `loop` format)

| repro | save | features | per firing | arms |
|---|---|---|---|---|
| `route-gate-crowd-loop` | BP-Spring | routegate, tailunlock, h2, optsinit | 30+ Go Here plans in one tick (household + 30 spawned), 5 objects placed and destroyed, 6 route-mode plans + `@route.Replan` (ghost through walls, car only, leading a horse, stroller, flier, direct/no avoidance), 10 s camera pan, build/buy enter/exit (gates 15/14, replan of every routing Sim; quiet 3 s), everyone home | `native_set {tailunlock, h2}` ABBA; RouteGate and OptsInit have no runtime switch (on in both) |
| `save-write-loop` | BP-Spring | savewrite, decompress | CAS open/hair/close, buy open/close (resource reads), `save_game {name: "RC-SaveLoop"}` with quiet 3 s at `iterMs` 500 (the sim thread is in the native save until it returns), min 8 s, cap 90 s | on only (no runtime switch) |
| `alloc-churn-loop` | BP-Spring | gc | spawn 5 Sims, place 20 EA objects, remove, CAS open/close, remove Sims (alloc-dupfree in loop form), each step quiet 3 s | on only |
| `unseen-crowd-loop` | BP-Spring | unseenthrottle, unseenenv, unseenmotives, unseenbroadcast | 20 spawned Sims walk to the farthest lot (household and camera stay home), then back into view (snap-back); out leg ends on the on arm after 10 summed `unseen_*_skipped` (min 20 s), off arm quiet 2 s or 45 s | `native_set {unseenthrottle}` + `patches {unseenenv, unseenmotives, unseenbroadcast}` ABBA |
| `ip-route-legs-loop` | IP-Spring | legreplanskip, idlememo | household + 20 Sims across the water to the farthest lot (boat exits set `ReplanWhenFinished`, docs/151 §2; Sims idle after a successful plan feed D5) and back; out leg ends on the on arm when the two skip counters rose (min 30 s) | `patches` ABBA |
| `winter-pond-loop` | BP-Spring (`noEnv`) | waterjps | pond plan + Replan, 6 Sims cross the frozen pond (quiet 4 s), back | `native_set {waterjps}` ABBA |
| `pet-guard-loop` | BP-Spring | discourage, deferfootprint | guard start, cancel (footprint remove + destroy in one call), `kill_sim {pick: routing}` (dead-Sim sweep), spawn 1 | `native_set {deferfootprint}` ABBA |
| `object-churn-loop` | BP-Spring | destroypark | place 10 EA objects, destroy them under running jobs, build/buy enter/exit | `native_set {destroypark}` ABBA |
| `diner-crowd-loop` | BP-Spring | slotenabledro, avoidro | 6 townies visit Hogan's diner once; per firing the household + 10 Sims arrive at the diner (quiet 4 s) and go home | `native_set {slotenabledro, avoidro}` ABBA |
| `candidate-loops-loop` | BP-Spring | farthestsorted, indoorcap | `CandidateLoopProbe` (sync, 2 s) | `patches` ABBA, 10 per arm |
| `replan-buildbuy-loop` | BP-Spring | replandefer, replanspread | wave onto the home lot + build/buy enter/exit, far wave + enter/exit | `patches` ABBA |

`ts3fixes-v014` now maps to `route-nopath-crowd-loop` (30+ failing plans per firing: route failure backoff, watchdog route-fail path). Features with no runtime switch get an `on`-only loop; release-check derives their part c loop the same way and adds the bundle switches as an arm step in part d.

**Built 2026-11-15 (not run):** bouncerskip, repro `bp-bouncerskip-loop` (agent `velvet_bouncer`: night clock, the velvet rope found by object, the bouncer put at the rope in WatchDoor; ABBA, counted by `routesrc_bouncer_at_slot`), queue `l10-repros-1115.json`. Was: The bouncer loop runs only on a velvet-rope venue lot open at night; the agent finds lots by name only and no BP club name is in the repo. A lookup of lots holding an `IVelvetRopes`, then `test_env {hour: 22}` and `sim_visit_lot`, would make one. Its `rare` list is empty, so no old job comes back for it. lotremoveguard (unchanged since v0.1.4) keeps `@lifecycle`: the agent has no forced lot-removal path.

**Load half of the save matrix:** not in `save-write-loop`. A `load_save {reload: true}` inside a profile window is untested (the profiler and the mailbox across a world reload); every job load reads through [Decompress], and part b runs the matrix reload case.

### Mapping (`tools/harness/feature-repros.json`)

New per-feature fields: `replaces` (old release-validate jobs or groups that the feature's forced loop stands in for; listed in the coverage table as `replaced by forced loop <repro> (<feature>): <replacesWhy>`, not run) and `rareWhy` (why a kept `rare` job stays). `rare` now holds only the jobs no forced path reaches:

| kept old job | feature | why |
|---|---|---|
| `bp-rel-rare-winter-pond`, `ip-rel-rare-ip-water` | routeasync | the async plan job's water-grid paths (frozen pond in winter, IP sea, houseboat portal) need a winter state or the IP save that the BP crowd loops do not reach; both are already forced single runs (~15 min together) |
| `ip-rel-rare-ip-water` | waterjps | IP-only water (sea, mermaid, houseboat portal) |
| `@lifecycle` | lotremoveguard | no forced lot-removal path |

Replaced (reasons per row in the tool output): `@route` (routeasync, routegate, fallbackasync, earlyout: the crowd loops plan 30+ Sims under async jobs every firing and sweep the old route modes at plan level; the gates do not depend on the route mode), `@crowd` (the crowd loops repeat the big-crowd waves many times per load), `@lifecycle` (pet-guard-loop forces footprint add/remove, destroy apply and the dead-Sim sweep; object-churn-loop the destroy park), `@unseen` (the old jobs move household Sims, which the throttle never skips: household within 80 m snaps back, docs/143 §6.5, and `SimUnseen` excludes the active household, so they only check pass-through), `@save` (savewrite and decompress are world-agnostic: the save loop saves 6 times under one name, part b's BP matrix checks the saved file and the france trip the travel-save arguments). [inferred: each replacement reason; I-170-55.]

### Generator changes (`tools/release-check.ts`)

- Part c: **one job per source repro** (`rc-on-<repro>` when features share it, else `rc-on-<feature>`), with every sharing feature's `fixesRequire` / `nativeRequire` joined; `{n}` now also works in `nativeRequire` (savewrite `saveCount>={n}`). Default firings per part c loop 6 -> **4** (`ON_EVENTS`): correctness, not power.
- Part d: bundle A/B only on the loops of **new** features. A changed-but-shipped feature's loop runs fix-on in part c; an A/B there would flip only the new switches on a repro not aimed at them.
- Estimates: adaptive steps count at the middle of `minS..maxS` (`estSecs`, [inferred], I-170-56); the cap still uses `waitS` (= `maxS`). The output adds the total without part d and the optional session-reuse / multi-repro saving (`reuseSaving`: every part c-f job after the first of its save skips the ~1.7 min load + profiler start).
- The output lists the kept old rare jobs with `rareWhy`; the coverage table shows each replaced job with its loop and reason.

### Estimate for v0.1.5 (same command as §4)

| part | jobs | est. min (§4) | est. min now |
|---|---|---|---|
| a smoke | 3 | 23 | 23 |
| b save matrix | 2 | 19 | 19 |
| c fix on | 9 (was 6) | 43 | 52 |
| d bundle A/B | 4 (was 3) | 40 | 69 |
| e rare | 2 (was 26) | 230 | 15 |
| f soak | 1 | 47 | 47 |
| total | 21 (was 41) | 402 (6.7 h) | **224 (3.7 h)** |

Without part d (a clean-night timing read, run apart): **2.6 h**. With session reuse / multi-repro (optional, not on master): -24 min, so **3.3 h** with d and **2.2 h** without. Part d grew because slotenabledro and unseenthrottle now have loops (`diner-crowd-loop` ~25 min, `unseen-crowd-loop` ~23 min at 10 per arm, the docs/168 §1 minimum). With all 14 bundle candidates: 26 jobs, 5.4 h (2.7 h without d, 2.2 h without d with reuse); §4 had ~8.0 h.

### Correctness jobs: `tools/harness/queues/repro-check-release.json`

11 jobs, 5-6 min each, `asi.0331`, the repro-check measurement ini plus every switch the loops flip installed (`AvoidRO.Enabled`, `Discourage.DeferFootprint`, `Discourage.DestroyPark`, ...). Pass = no crash and the repro fired (the job's `nativeRequire` / `fixesRequire`: e.g. `asyncStarted>=20,rgEntries>=1,tuWins>=1,h2Released>=1` for the gate loop, `saveCount>=3` and no decoder failures for the save loop). A loop ending at the window with its rule not met is fine there. Run it (daytime OK) before a release-check run relies on these repros; a job whose counters stay 0 means the forced state is not reached (report, do not tune the loop to pass).

### Checks [verified]

`node tools/release-check.ts --self-check` (new: shared-repro grouping and joined requirements, `{n}` in `nativeRequire`, part d only for new features, the `replaces` row text, `estSecs`, `reuseSaving`; every `replaces` id exists in release-validate.json; every mapped loop derives in both modes and passes `checkLoop`; a feature that replaces old jobs must have a loop repro); `node tools/harness/repro-loop.ts` (198 adaptive steps valid, the new ones included); `queue.ts tools/harness/queues/repro-check-release.json --dry-run` (11 jobs, 11 stop rules, exit 0; asi.0331 is missing in this worktree only); `queue.ts tools/harness/queues/release-check.json --dry-run` after a scratch `--install-repros` (21 jobs, 13 stop rules, exit 0; repros.json restored). Not checked: any game run (I-170-50..56).

## 6. Instances per firing

**Built 2026-10-23, static only: `repro-loop.ts` self-check (161 adaptive steps valid), `release-check.ts --self-check`, `queue.ts --dry-run` of `repro-check-release.json`, `repro-check-route.json`, `route-hitch-loops.json`, `script-hitch-loops.json`, `repro-check.json` (all exit 0); never run in game.** Ledger §279.

Rodney 2026-10-23: shorten the repros by firing more instances per firing (more Sims, objects, stranded Sims per firing, within what a busy real world has) and stop on the instance count (docs/168 §6: a step's `count: {counter}` adds the counter rise, `count: {result}` a numeric answer field; without one each firing is 1 event). The rule is still checked only at ABBA cycle ends, so the floor is one cycle = 2 firings per arm.

| repro | per firing now (was) | counted by | until now (was) | firings old -> new | est. min old -> new |
|---|---|---|---|---|---|
| `route-gate-crowd-loop` | 30 spawned + household Go Here plans (same) | firings (crowd_go answers a list) | perArm 2 (4) | 8 -> 4 | 10.7 -> 5.8 |
| `save-write-loop` | 1 save (same; a save is one instance) | firings | events 4 (6); job needs `saveCount >= 3` | 6 -> 4 | 9.5 -> 6.4 |
| `alloc-churn-loop` | 8 Sims, 30 objects (5, 20) | `place_objects` `tracked` | events 90 objects (6 firings) | 6 -> 3 | 9.6 -> 4.8 |
| `unseen-crowd-loop` | 30 walkers (20) | firings (per-walker counters rise on the on arm only) | perArm 2 (4) | 8 -> 4 | 9.3 -> 5.3 |
| `ip-route-legs-loop` | 30 spawned + household (20) | firings (skip counters on arm only) | perArm 2 (4) | 8 -> 4 | 14.2 -> 7.8 |
| `winter-pond-loop` | 12 Sims on the ice (6) | firings | perArm 2 (4) | 8 -> 4 | 10.3 -> 5.4 |
| `pet-guard-loop` | 1 guard/cancel under running jobs, dead sweep each firing (none) | firings (one pet: `pet_guard` has no count) | perArm 2 (4) | 8 -> 4 | 7.1 -> 5.2 |
| `object-churn-loop` | 20 objects destroyed under 10 routing Sims (10 under 5) | `place_objects` `tracked` | perArm 30 objects (4 firings) | 8 -> 4 | 6.0 -> 3.8 |
| `diner-crowd-loop` (SV save) | 15 spawned + household (10) | firings | perArm 2 (4) | 8 -> 4 | 10.5 -> 6.3 |
| `replan-buildbuy-loop` | 30 spawned + household, 2 exits (20) | firings (replan counters on arm only) | perArm 2 (4) | 8 -> 4 | 8.2 -> 4.8 |
| `route-stuckcheck-reset-loop` | 20 stranded Sims (same: more on one lot is not a real world) | `stuck_check_fire` `armed` | perArm 40 (hand-written 8 firings) | 8 -> 4 | 3.2 -> 1.9 |
| `route-nopath-crowd-loop` | 30 spawned + household no-path plans | `crowd_unreachable` `sent` | perArm 50 (hand-written 7) | 7 -> 4 | 3.8 -> 2.5 |
| `route-queuefull-replan-loop` | 30+ Go Here starts + replan-all | firings | perArm 2 (hand-written 6) | 6 -> 4 | 4.5 -> 3.3 |
| `hitch-stuckcheck-loop` | 12 stranded townies (8) | `strand_many` `stranded` | perArm 120 = 10 firings per arm (hand-written 20) | 20 -> 20 | 6.6 -> 6.6 |
| `bp-overwatch-ab-loop2`, `candidate-loops-loop`, `hitch-easpread-*`, `hitch-errortrap-loop` | one world-wide pass / one probe per firing | unchanged | unchanged | same | same |

Estimates: the body at fixed `waitS` and adaptive steps at the middle of `minS..maxS` (as `release-check.ts estSecs`), plus the plain steps; [inferred], ledger I-170-57. Sum of the changed rows: ~118 -> ~70 min.

**Loop form for the hand-written route loops:** `route-stuckcheck-reset-loop`, `route-nopath-crowd-loop`, `route-queuefull-replan-loop` and `hitch-stuckcheck-loop` were written out firing by firing; they are now `loop` repros (firing #1 and #2 give the body, every 0/1 or true/false that differs becomes `"$on"`; trailing counter reads moved to `cleanup` or dropped: the harness reads the counters itself). `route-sv-seacheck-freeze` stays hand-written: its on values (`synccapus 1000000`) are not switches `"$on"` can express.

**pet-guard: why 6 firings gave `discDeadSweep` 0** [verified in `native/ts3native/discourage.cpp`]: a dead-id sweep runs only for a deferred Remove record (an async route job running at the call) whose object is gone when D drains it (`DiscApplyOne`). With `[Discourage] DestroyApply=1` (default) the destroy gate `DiscOnDestroy` applies the slot's Remove at `ObjectService_DestroyById` while the slot still exists (`discDestroyApplied`), so the sweep never runs; ledger I-84-7-12 already says the sweep counters need the gate off. `kill_sim` never reaches it: a dead Sim is not destroyed. Now each firing sends the 10 spawned Sims on Go Here plans 2 s before the cancel and sets `native_set {discdestroy: 0}` around the cancel (both arms), so each firing makes one sweep; a plain one-off guard/cancel with the gate on runs first for the gate. `bp-rcr-pet-guard` requires `discDeadSweep >= 2` (was `>= 1`); `discDestroyApplied` is read, not required (the one-off depends on a job running at that moment).

**Caveats:**
- Instances are not independent hitch events where one firing's instances share a tick: the off arm of the stuck checks runs every check in one alarm tick, a build/buy exit replans everyone in one tick. So `hitch-stuckcheck-loop` (a clean-night timing A/B whose expect is one hitch per firing) keeps 10 firings per arm. The route repros' pass rules count instances (failed plans, stuck checks), which the new rule fits; read their hitch A/B per firing (`hitch-ab.ts --scenario`) and rerun with a larger `until` if it says not enough events.
- `release-check.ts derive` copies each loop step as is, `count` included, so its part c `{events: 4}` and part d `{perArm: 10}` now count objects / stuck checks / plans for the counted repros (`alloc-churn`, `object-churn`, route loops), i.e. one firing or one cycle; its estimate and note text still say firings (owner of `release-check.ts` to decide).
- `diner-crowd-loop` moved to `Baseline-SV-Spring` on master (Hogan's diner is SV); `route-stuckcheck-reset-loop` and `route-nopath-crowd-loop` still use its x,z on `Baseline-BP-Spring` (as before this change; not checked here).

**Agent command gaps (ts3agent, not edited here):** `crowd_go` and `crowd_sea_lot` answer `sent` as a list of names: a numeric `count` would let the crowd loops count plans; `spawn_sims` answers `sims` as a list (no count); `remove_objects` answers `removed` as a list (counting destroys, not places, would be exact); `pet_guard` drives only the household's first pet: a `pets: n` (<= 6, the household cap; `make_sim {species: dog}` exists) would give 2-3 guards per firing; native counters (`asyncStarted`, `discDeadSweep`, `dpParked`, `saveCount`) cannot be a `count: {counter}` (that reads ts3fixes counters from `patches` only).
