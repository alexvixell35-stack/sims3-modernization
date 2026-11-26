# 279: Daily experimental pre-releases (2026-11-17)

Rodney 2026-11-17: "Create a daily experimental CI of our latest master with the verified fixes on. That way we can release sooner for testers."

## What it is

A GitHub **pre-release** (never `latest`) built from master once a day, tag `v<next patch>-exp.<YYYYMMDD>` (PHT date; next patch after the newest stable release, e.g. `v0.2.13-exp.20261117` while v0.2.12 is stable). Assets as a stable release: `ts3native-<tag>.zip` (`ts3native.asi` + experimental `ts3native.ini`) and `ts3fixes.package`. Steam 1.67 only (the EA 1.69 zip is built on the EA laptop). The stable release, `latest` and `docs/data/builds.json` are never touched. Only the newest 7 experimental pre-releases stay (older ones and their tags are deleted).

## Which switches are on

`docs/data/verified-switches.json`, data, not hand-picked per release:

- `verified`: the switch's correctness gate PASSED in game (result page in `evidence`); its `on` values go into the ini / package tuning.
- `blocked`: household mismatch, crash or open FAIL; its `off` values are forced, even when master has it on (DoorMemoX on 2026-11-17).
- `unverified`: built only, NOT RUN, timing only or parked; master's default stays.

Agents update an entry in the same commit as the result page when a correctness job passes or fails. `[Experimental] HouseholdToo` can never be set by the list (the script refuses anything but 0). Every ini key and tunable must already exist (a typo fails the build). `knownIssues` goes into the release notes.

## Where and when it runs

GitHub Actions cannot build `ts3fixes.package` (EA assemblies are never in git or CI), so it runs on the maintainer's Windows machine: scheduled task "TS3 Experimental Release" (`tools/release/register-experimental-task.ps1`), daily 12:30 PHT (after the 12:00 tools update) and then hourly for 10 h. A run:

1. while `track.ts status` shows a job, chain, queue, hold or the game, it builds anyway (Rodney 2026-11-23) unless a timing job runs: a live `probe-out/queue/job-running` marker in any checkout (`git worktree list`) whose job is a profile job with its noise check on (queue.ts `isTimingJob`; `-rerun` / `-kc` / `-smoke` ids map to the base job), or a live marker whose job is in no queue file of that checkout. Then it skips and the next hourly repeat tries again (a build would trip the job's noise stop);
2. skips when the newest experimental release was built from the same master commit and switch list (marker comment in its notes), or today's tag exists;
3. builds in its own detached worktree `probe-out/experimental/wt` (never the main checkout's `build/` or `Bin/`): gates `node tools/selftest-all.ts`, `native/ts3native/build.cmd` (asi + `selftest.exe`), then `tools/setup/build-fixes.ts` against `game-copy/assemblies`;
4. applies the switch list, vets the files (`tools/mts-zip.ts`), scans asi / ini / package / notes for personal names, home paths and emails (publish-public scrub list);
5. public sync (`tools/publish-public.ts sync --yes`, all its checks), then `gh release create --prerelease --latest=false` on the public merge commit, checks `latest` is still the stable tag, prunes to 7.

Any failure stops the run before publishing (log `probe-out/experimental/experimental.log`). The `-exp.` tags skip the CI release job in `.github/workflows/build.yml` (the script publishes them; CI would fail on the missing CHANGELOG section).

Manual: `node tools/release/experimental.ts --dry-run --ignore-busy` builds and checks without publishing; `--force` releases an unchanged master again (not twice a day: the tag is per date).

## Natural evidence (2026-11-18)

`node tools/release/natural-hits.ts [--since YYYY-MM-DD] [--min 3]` (self-check `--self-check`) reads every finished queue row since `--since` (default 7 days back; `probe-out/queue/<queue>-<date>.jsonl` plus each row's /native before/after file) and writes `docs/data/natural-hits.json` and `docs/data/natural-hits.md`. Per switch: runs with it on (natural = no repro steering the job; "measured" = the row recorded its counters), natural hits and hit runs, repro hits, hits in off runs (bug seen with the fix off: `pkAbbaSeen`, `mooddrift_corrected`, `moodpending_seen`), its own error counters, and crashes / script errors in its hit runs. **Verified naturally** = >= `--min` natural hits, 0 own error counts, no crash and no script error in any hit run (strict: an unrelated EA script error in a hit run blocks it). `verifiedSwitches` in the JSON maps each `verified-switches.json` entry to its members' natural status (`verified` / `some-hits` / `hits-with-errors` / `none`); a sub-key ini switch (e.g. `[GoalValidRO] UnseenDefer`) has no census row of its own and reads `none`. Small-fix rule: an entry whose natural status is `verified` counts as its one repro.

Mapping: native switches = ts3native.ini sections; counters = the camelCase fields of registry native reservations whose text starts with `[Section]`; on = the row's `featuresOn` (Enabled >= 1 in the Bin ini) or Mode >= 1 (job ini, else repo ini: inferred). ts3fixes switches = the agent names in `Fixes.cs` (`case "name": return Instantiator.kX;`); counters = `patches` keys `<name>_*` (or the name without underscores, e.g. `unseen_park_*`); on = `row.fixCensus.on`, else profile fixesOn / fixesOff, else the shipped tuning default (inferred). Overrides for the release plan §1b conditionals: `[PkgLock]` hit = `pkCloseDefer` + `pkCloseForce`, error = `pkAbbaSeen`; `bouncerskip_skipped`, `takeariskguard_skips`, `menumaven_done`, `mooddrift_corrected`, `moodpending_skipped`, `weddingcleanup_guarded` (errors `*_errors`).

Gaps closed on the same branch: `queue.ts` writes `row.fixCensus` (`patchCensus`: every switch the agent's `patches` reply says is on and every numeric `patches` key that rose) into every row from the merge on, so the census covers all ts3fixes switches; before it, rows only kept the reported prefixes (the ACTIVITY set + fixesReport / fixesOn), so most managed switches read "0 measured". ts3agent `PatchHandlers.cs` names the unnamed counters `mooddrift_corrected/points/errors` (2251-2253), `moodpending_seen/skipped/errors` (2278-2280), `weddingcleanup_guarded/errors` (1058-1059); needs a ts3agent build (waits for an idle machine).

First read (since 2026-11-11, 431 rows): `bouncerskip` verified naturally (37 natural hit runs, 0 errors), `unseenpark` too; `[PkgLock]` never on (Mode=0 everywhere until this overlay), `takeariskguard` / `menumaven` / `mooddrift` / `moodpending` / `weddingcleanup` 0 measured runs (counters never recorded). Broad native features (`[UnseenTable]`, `[RouteAsync]`, `[MonoSigGuard]`, `[MonoJit]`) have hundreds of hit runs but each has 5-13 runs with a script error, so the strict rule leaves them unverified.

## Dogfood (Rodney 2026-11-18 "Yes, from tonight"; built, starts with the first queue after the merge)

Every correctness job (`queue.ts` `!isTimingJob`: no profile, or `noiseCheck: false`) runs the v0.3.0 release-candidate ON set as its base: `tools/harness/dogfood.json` (written by `node tools/release/dogfood.ts`, self-check `--self-check`) holds the ini keys where that set differs from the repo ini, and `queue.ts` `rowIni()` merges them under the job's own ini (the job's keys win, so an off/on pair or an `ab` job keeps its own values and both arms get the same base), over the job's `iniBase` too (queues pin `game-copy/ts3native.ini.master-*`). Not for timing jobs (A/B arms compare one switch on master defaults), a release / rc / old-version base ini (`ini.rc*`, `ini.rel*`, `asi-v0xx/`: already a shipped set), release tests (`releaseDefaults`, `asi.rel*`), worldimage, runs without ts3native, or a job with `"dogfood": false`. Every row records `base` (`repo` / `dogfood <source> <sha8>[ over <iniBase>]` / `iniBase <path>` / `release` / `none`), and `natural-hits.ts` counts runs per base (`bases`, `rowsByBase`).

Until the rc files exist: base = master + release plan §1 ON (N1-N11, table `RC_ON` in dogfood.ts) + the off values of every `blocked` entry in `verified-switches.json`, minus the excluded keys (§2 decide-later: EqualsFast, GC IdleGate, MonoJit YRows, Startup DxDiagCache; HouseholdToo). On 2026-11-18 master already had all of §1, so the overlay is one key: `RouteSnap.DoorMemoX=-1` (doormemox blocked) [verified: dogfood.ts run]; PkgLock Mode=1 comes from `lightIni()` for every job. Tunables cannot be overlaid per job: the master package already carries T1-T5 and T7a ON and the save-data tunables (`savedata-fixes`) OFF (`tunablesNotApplied` lists any gap; none). Thursday: `node tools/release/dogfood.ts --rc-ini game-copy/ts3native.ini.rc-1119` (overlay = every rc key that differs from the repo ini, same exclusions), and correctness jobs that should use the rc package set `fixesPackage`.

Read-backs: DoorMemoX has no `Enabled` key, so `featuresOn`, version read-backs and every queue's `regexCases` are unchanged [verified: queue.ts self-check, dry runs of rc1119-smallfix, hhtoo-local-rows-correct-1118 (gets `RouteSnap.DoorMemoX=-1`), gc-idle-gate-time-1117 (timing: unchanged)]. Risk: old correctness queues that exercised DoorMemoX implicitly (householdtoo-correct-1116*, crashn-rerun, crasho-rerun) would now run with it off; a rerun of them sets `"RouteSnap.DoorMemoX": 3` or `"dogfood": false`.
