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
