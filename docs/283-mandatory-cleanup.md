# 283. Mandatory disk cleanup (worktrees, build intermediates, harness saves)

2026-11-17. Branch `bugfix/mandatory-cleanup-1117`. Tool: `tools/harness/cleanup.ts`. Skill: `.claude/skills/ts3-cleanup/SKILL.md`.

## 1. Why

On 2026-11-17 drive C: reached 0 bytes free and broke builds and game runs. The space went to about 120 Apex/GI/DXVK worktrees under `game-copy/rewind-bridge-1115` (2-3 GB each), agent worktrees under `.claude/worktrees`, build intermediates, `ApexRunner-*` and other harness saves, crash dumps (1.5-2.3 GB each) and frame folders. Nothing removed finished work's worktrees or old artifacts automatically. Cleanup is now part of the code paths that create and finish work, and of every game run.

A one-off sweep the same day also removed a fresh agent worktree (no commits yet, so its HEAD was "merged"). Because of that, the worktree rule below protects worktrees that have a recent checkout or an agent reservation [verified: it happened to this branch's worktree].

## 2. What `cleanup.ts` removes

Every mode is a dry run unless `--apply`. A dry run prints the plan with sizes. Each line also goes to `probe-out/cleanup/<date>.log` in the main checkout.

| Mode | Scope | Removed only when |
|---|---|---|
| `worktrees` | this repo's `.claude/worktrees/*`, and every worktree of the separate repos under `game-copy` (`game-copy/*/.git`, `game-copy/*/*/.git`: Apex, GI, DXVK, remix; each repo's own git) | the branch is merged into a target (this repo: `master`; other repos: `feature/realtime-gi`, `main`, `master` and their `origin/`; `--target` overrides), or its PR is merged or closed (`gh pr list --head`, `GH_TOKEN=$PERSONAL_GITHUB_TOKEN`, 60 s) **and** `git status` is clean **and** no commit is outside the remotes and targets **and** neither the path nor the folder name appears in a running process's command line, a running chain script (`probe-out/queue/*.sh`), a queue json those name, or `job-running` **and** the worktree is not git-locked **and** it has no agent reservation (`docs/data/registry-reservations.json`) younger than 24 h **and** no commit or checkout in the last 2 h |
| `builds` | every worktree and main checkout of the repos above, unless a process names it or an intermediate was written < 30 min ago | git-**ignored** intermediates only: `obj` dirs next to a `.csproj`/`.vcxproj`/`.sln`, `*.tlog` dirs, meson `*.p` dirs next to `build.ninja`, files `.obj .o .iobj .ipdb .pch .ilk .idb`. Outputs (`.asi .dll .exe .pdb .package`) and tracked files stay. Never follows a link, never enters a nested repo, `game-copy` or `probe-out` |
| `saves` | `Saves/` harness temp saves (`ApexRunner-*`, `Matrix-*`, `QuitCrash-*`, `QuitStop-*`, `Repro-<tag>-HHMM*`, `.sims3` and `.sims3.backup`); `probe-out/{queue,profile}/*-frames`, `probe-out/rewind-*`; dumps: `.dmp` under `probe-out/dumps` (any depth), `probe-out/*.dmp`, `%LOCALAPPDATA%\CrashDumps\{TS3W,NvRemixBridge}*` | saves and artifacts: older than `--days` (default 3) and not referenced (as above). Dumps: deleted once `<name>-stacks.txt` holds cdb stacks (`dump-stacks.ts` extracts them first); no stacks after `--days`: deleted; a `keep` file in the folder or the name in docs/ keeps it. Each delete logs its path relative to its dump dir and its size; dry run unless `--apply`; never through a junction/symlink (game-copy is one in worktrees) and never outside those dump dirs. The newest save of each family (the name without its trailing `-N`) always stays. Baseline, story and other hand-made saves never match the pattern |

`--branch <b>` (used by `agent-merge.cjs finish`) names one worktree. It skips the 24 h and 2 h guards, because the merger has just finished that work. Any other blocker gives exit 1 with the reason.

Worktree removal uses `tools/worktree-clean.ts` `removeWorktree`. It unlinks a `game-copy` junction first (the link only, checked as a reparse point, with its target checked afterwards), refuses a worktree with real files in `game-copy` or other links inside, then runs `git worktree remove --force` and `git worktree prune`. It never recurses through a junction.

## 3. Where it is mandatory (code)

| Path | What runs | Failure |
|---|---|---|
| `tools/harness/queue.ts` start (not `--dry-run`, not the smoke child) | `cleanup.ts gate` | exit 5, `STOP: not enough free disk space` before any job |
| `tools/harness/queue.ts` end | `cleanup.ts saves --apply` | logged (`end sweep:`), the exit code does not change |
| `tools/harness/apex-run.ts` start, before Bin is touched | `cleanup.ts gate` | exit 2 `refused: not enough free disk space` |
| `tools/harness/night-run.ts --close`, before apps are closed | `cleanup.ts gate` | exit 2 |
| `node tools/agent-merge.cjs finish <branch>` after the merge commit | branch must be in HEAD with no merge in progress, then `cleanup.ts worktrees --apply --branch <b> --target HEAD` | exit 1 with the reason (not merged, dirty, unpushed, locked, used by a chain, REFUSED junction) |
| `agent-merge.cjs` conflict resolver | prints the `finish` command for the branch in `MERGE_MSG` | reminder only |
| `tools/selftest-all.ts` | `cleanup.ts --self-check` (found automatically) | FAIL |

**Gate thresholds:** free space below **30 GB** (`--auto-gb`, env `TS3_CLEANUP_AUTO_GB`) runs `cleanup.ts all --apply`. If free space is still below **15 GB** (`--min-free-gb`, env `TS3_CLEANUP_MIN_GB`), the gate refuses (exit 3) and names the next step. The drive checked is the one holding the main checkout (`statfs`).

Agents (AGENTS.md "Rules"): run `node tools/harness/cleanup.ts gate` before every `git worktree add` (this repo and the Apex/GI/DXVK repos). After a branch is merged, or its PR is opened and pushed, run `node tools/harness/cleanup.ts worktrees --apply --branch <b>`. Chain scripts start with `node tools/harness/cleanup.ts gate || exit 1`. The Agent tool's own worktree isolation cannot be hooked, so that part stays a rule.

## 4. Self-check

`node tools/harness/cleanup.ts --self-check` (about 13 s) builds a temp repo and checks:

- regexes against real names and lines, with match and non-match cases: temp save names, GitHub origin URLs, queue json mentions in chain lines, worktree-name references, intermediate extensions
- a dry run changes nothing
- a fresh worktree is kept by the 2 h guard
- a merged clean worktree with a `game-copy` junction is removed and the junction target's files survive
- a merged worktree of a separate repo under `game-copy` is removed with that repo's git
- worktrees that are unmerged, dirty, named by a running chain's queue, reserved or git-locked are kept, each with its reason
- a worktree is removed once the chain ends
- "PR merged" with only local commits is kept
- `--branch` on a blocker gives exit 1; `--branch` ignores the reservation guard; a branch with no worktree gives exit 0
- builds: ignored `.obj/.pch/.p/obj` are removed, while `.asi`/`.dll` outputs, a tracked `mesh.obj` and the files behind a junction stay; a fresh intermediate skips the worktree
- saves: the older member of a family is deleted and the newest kept; a Baseline save is kept; a save named by a running queue is kept; an old dump is deleted and a new one kept; dumps: stacks present = deleted, keep marker / lock-only text / young without stacks = kept, a dump behind a junction or outside the dump dirs is skipped, a dry run deletes nothing and logs `would delete dumps/<rel> <size>`

`node tools/agent-merge.cjs --self-check` adds two checks: `finish` refuses a branch that is not merged into HEAD, and the `MERGE_MSG` branch regex matches real lines and rejects non-matching ones. `tools/worktree-clean.ts --self-check` is unchanged and still passes after `removeWorktree` was extracted.

## 5. Known limits

- A PR that was squash-merged and whose remote branch is gone keeps its worktree, because its commits are on no remote. Remove it by path with `node tools/worktree-clean.ts <path> --yes` after a manual check.
- Reference matching works on text. Any mention of a worktree's folder name in a running chain counts, comments included. This errs toward keeping.
- `builds` in the main checkout is skipped while any process command line names the main checkout path, which is usually the case during a chain.

## 6. Leftovers: %TEMP%, raw probe-out data, game-copy copies, dropped lanes (2026-11-23)

Branch `feature/cleanup-auto-1123`, tooling only. On 2026-11-23 `%TEMP%` held 165 GB in 38,712 entries [verified: counted], `probe-out/profile` + `probe-out/queue` ~22 GB and `game-copy` 65 GB (cc-synth 25.9 GB, rewind-bridge-1115 16.1 GB, compat-mods 11.0 GB) [verified: measured]. Nothing removed them.

| Mode | Scope | Removed only when |
|---|---|---|
| `temp` | top-level entries of `%TEMP%` (`TS3_TEMP` overrides) whose name starts with a prefix in `TEMP_CLEAN`: only `frida-` (Frida's own helper dirs) and `ts3-publish-` (`tools/publish-public.ts`), Rodney 2026-11-23. Every other prefix our scripts create stays. The creator list in the log comes from a scan of `tools/` source (`mkdtemp(join(tmpdir(), 'x-'))`, `join(tmpdir(), \`x-${pid}\`)`, python `mkdtemp(prefix='x-')`, ps1 `$env:TEMP "x-..."`) and widening the set is a one-line change. The rest of the name must be `[\w.-]+` | newest file inside older than `--temp-days` (2), no junction/symlink inside, not named by a running process / chain / queue. A locked or AV-held entry (EPERM/EBUSY/EACCES, Kaspersky on `.tmp`) is skipped and counted, never fatal |
| `probe` | raw per-job data in `probe-out/{profile,queue}`: `-frames.csv -hitches.jsonl -seconds.csv -freezes.jsonl -simstack.jsonl -activity.jsonl -moved.jsonl -shadow.jsonl -autshadow.jsonl -machine.jsonl -samplers.jsonl -probe.log`. Results (`<queue>-<date>.jsonl`), logs, json, reports, gifs and chain scripts never match | older than `--probe-days` (14), not of a queue a running chain / queue / `job-running` names, and neither the file name nor its job stem (name without the raw suffix) is a token anywhere in `docs/` (any depth: results, crashes, ledger), `PLAN.md`, `README.md` |
| `gamecopy` (by name only; not in `leftovers` / `all`: Rodney 2026-11-23 keeps cc-synth and compat-mods) | `game-copy/cc-synth/<set>` (generated by `cc-gen*.ts`, rebuilt with the same `--seed`); `game-copy/compat-mods/<mod>` (MTS downloads) | cc-synth: unused > `--gc-days` (14) and not named by a running chain/queue or by a queue json / chain script written in the last `--gc-days`. compat-mods: every `docs/data/compat-matrix.csv` row for the folder has a recorded verdict (`no overlap`, or `overlap` with a status) and no running or recent queue names it |
| `leftovers` | `temp` + `probe` | also part of `all` (so the < 30 GB gate runs it) |
| `lane --branch <b>` | `game-copy/<name>` copies of the lane's builds (`docs/data/builds.json` names ending `.<lane>`, `.<lane>b`, `.<lane>-x`; lane = branch name without `feature/` and an issue number) | not a link, not named by a running or recent queue. Hashes stay in builds.json |
| `dropped --branch <b>` | `lane` + the branch's worktree (any repo) even unmerged + the lane's raw probe-out outputs at any age | worktree: clean, not locked, not referenced, and the branch ref exists (it is never deleted, so every commit stays reachable). Docs-linked raw files stay |

Automatic hooks:

| Path | What runs |
|---|---|
| `tools/harness/queue.ts` end (via `sweepSaves`, queue.ts itself unchanged) | after `saves --apply` and `worktrees --apply`: `leftovers --apply` in its own child (60 min timeout); the end-sweep line gets the freed totals |
| `node tools/agent-merge.cjs finish <branch>` | after the worktree is removed: `lane --apply --branch <b>` (a non-zero exit is reported, finish still succeeds) |
| design judged not good | by hand: `node tools/harness/cleanup.ts dropped --branch <b>` (dry run), then `--apply` |

Chains run queue.ts per queue, so every chain also gets the end sweep.

### 6.1 First dry run (2026-11-23, nothing applied)

| Category | Would free | Count | Kept |
|---|---|---|---|
| temp `frida-*` | 83.1 GB | 2,247 | 565 newer than 2 d |
| temp `ts3-publish-*` (`tools/publish-public.ts`) | 18.5 GB | 3,169 | 238 newer |
| temp `ts3-diag-selfcheck-*` (`tools/diag/ts3-hang-diag.ps1`) | 0.4 GB | 3 | |
| temp `jit-replay-*` (`tools/jit-replay.ts`) | 0.2 GB | 205 | 3 newer |
| temp `mts-key-*` / `mts-root-*` (`tools/mts-helper/cli/chrome.ts`), `install-lot-*` (`tools/install-lot.ts`), 15 more prefixes | < 0.1 GB | ~2,600 | `ts3-cleanup-check-*` / `wt-clean-check-*` with a junction inside kept |
| probe-out raw files > 14 d | 14.1 GB | 17,160 | 421 linked from docs, 7,675 younger |
| game-copy cc-synth (11 sets incl. `.il` cache) | 5.2 GB | 11 | `cc-2k`, `cc-2k-v2`, `many-mods-cc`, `pkgs-2000`: named by a queue < 14 d |
| game-copy compat-mods (verdict recorded) | ~2.1 GB | 87 | 234 without a recorded verdict |
| total | ~124 GB | | |

Rodney's decision on this list (2026-11-23): clean `frida-*`, `ts3-publish-*` and the probe-out raw files; keep the other temp prefixes, cc-synth and compat-mods. The code now does exactly that (`TEMP_CLEAN`, `gamecopy` left out of `leftovers` / `all`). The first real `leftovers --apply` ran the same day (§6.3).

Not ours, never touched: `%TEMP%\claude` (17 GB), `remoteIpMoProxy_*` (PowerShell compatibility sessions, ~4 GB in 20k entries), `gi-triage-build`, `pv-*`, `flow-ui-*`, `4edm*` and other names no tool of ours creates [verified: no source match]. `hotspots-st-*` (57 entries, ~0 GB) has no creator on master any more and stays.

`game-copy/rewind-bridge-1115` (16 GB) is Apex/GI worktrees: `worktrees` keeps 36 of them as "not merged and no merged/closed PR" and many more as dirty. Those are parked or dropped designs: they go only with `dropped --branch <b>` once someone names the branch as dropped.

### 6.3 First apply (2026-11-23, approved by Rodney, nothing running: track.ts status no job/game/chain/queue)

`node tools/harness/cleanup.ts leftovers --apply` freed 115.6 GB [verified: log `probe-out/cleanup/2026-11-23.log`]: temp `frida-*` 2,247 entries 83.1 GB (565 newer than 2 d kept), `ts3-publish-*` 3,173 entries 18.5 GB (267 newer kept), probe-out raw 17,162 files 14.1 GB (421 linked from docs, 7,673 younger kept). 0 entries locked (no EPERM). Free space 94.2 GB before, 217.4 GB after.

### 6.2 Known limits

- Temp entries with a junction inside (killed self-check fixtures) stay; remove them with `node tools/worktree-clean.ts <path> --yes`.
- The probe rule judges a raw file by name tokens. A doc that cites only a queue name (not the job file or stem) does not keep that run's raw data past 14 days; worst-hitches / route-stutter re-analysis of such runs needs a rerun.
- `gamecopy` by name: deleted compat-mods folders must be downloaded again for a `compat-scan.ts` rescan or a `mod-set.ts` set that uses them.
- `lane` matches build names by the branch's lane tag; a build named with a different tag is not found.
