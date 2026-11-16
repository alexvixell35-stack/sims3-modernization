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

1. skips while `track.ts status` shows a job, chain, queue, hold or the game (builds must not load the machine during game runs); the next hourly repeat tries again;
2. skips when the newest experimental release was built from the same master commit and switch list (marker comment in its notes), or today's tag exists;
3. builds in its own detached worktree `probe-out/experimental/wt` (never the main checkout's `build/` or `Bin/`): gates `node tools/selftest-all.ts`, `native/ts3native/build.cmd` (asi + `selftest.exe`), then `tools/setup/build-fixes.ts` against `game-copy/assemblies`;
4. applies the switch list, vets the files (`tools/mts-zip.ts`), scans asi / ini / package / notes for personal names, home paths and emails (publish-public scrub list);
5. public sync (`tools/publish-public.ts sync --yes`, all its checks), then `gh release create --prerelease --latest=false` on the public merge commit, checks `latest` is still the stable tag, prunes to 7.

Any failure stops the run before publishing (log `probe-out/experimental/experimental.log`). The `-exp.` tags skip the CI release job in `.github/workflows/build.yml` (the script publishes them; CI would fail on the missing CHANGELOG section).

Manual: `node tools/release/experimental.ts --dry-run --ignore-busy` builds and checks without publishing; `--force` releases an unchanged master again (not twice a day: the tag is per date).
