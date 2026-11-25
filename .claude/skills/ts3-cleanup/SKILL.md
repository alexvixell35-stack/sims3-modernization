---
name: ts3-cleanup
description: Free disk space safely in sims3-modernization - finished agent and Apex/GI/DXVK worktrees (junction-safe), build intermediates, old harness saves, dumps and frames - and the free-space gate queue.ts / apex-run.ts / night-run.ts run. Use for "disk full", "free space", "clean worktrees", "clean up after the feature", before `git worktree add`, after a merge.
---
# Mandatory cleanup (docs/283)

Dry run first. It prints what would go and the sizes:
```
node tools/harness/cleanup.ts                     # all: worktrees + builds + saves
node tools/harness/cleanup.ts worktrees|builds|saves [--days 3]
node tools/harness/cleanup.ts leftovers           # temp + probe (docs/283 §6)
node tools/harness/cleanup.ts temp [--temp-days 2]        # %TEMP% frida-* and ts3-publish-* only
node tools/harness/cleanup.ts probe [--probe-days 14]     # raw probe-out/{profile,queue} data (frames, hitches, shadow...) not linked from docs
node tools/harness/cleanup.ts gamecopy [--gc-days 14]     # by name only (Rodney keeps cc-synth / compat-mods): cc-synth unused 14 d, compat-mods with a verdict
node tools/harness/cleanup.ts lane --branch <b>           # the lane's game-copy build copies (agent-merge finish runs it)
node tools/harness/cleanup.ts dropped --branch <b>        # parked/dropped design: worktree even unmerged (branch ref kept) + lane builds + raw outputs
```
Apply:
```
node tools/harness/cleanup.ts all --apply [--min-free-gb 50]      # --min-free-gb: only when free < N GB
node tools/harness/cleanup.ts gate                                # < 30 GB: all --apply; still < 15 GB: exit 3 (REFUSED)
```
Rules for agents:
- Run `node tools/harness/cleanup.ts gate` before every `git worktree add` (this repo, Apex, GI, DXVK).
- After a merge commit: `node tools/agent-merge.cjs finish <branch>`. It runs `cleanup.ts worktrees --apply --branch <b> --target HEAD`, and a non-zero exit names the blocker.
- After a PR is opened and pushed (Apex etc.): `node tools/harness/cleanup.ts worktrees --apply --branch <b>`. It removes the worktree only once the PR is merged or closed, or the branch is in a target.
- A chain script starts with `node tools/harness/cleanup.ts gate || exit 1`. queue.ts, apex-run.ts and night-run.ts already gate by themselves. queue.ts also sweeps old saves, dumps, frames, merged worktrees and `leftovers` at its end.
- A design judged not good (parked or dropped): `node tools/harness/cleanup.ts dropped --branch <b>` (dry run first, then `--apply`).

A worktree is never removed while it is dirty, has commits that are on no remote, is git-locked, is named by a running chain, queue or process, has an agent reservation younger than 24 h, or had a checkout in the last 2 h (`--branch` skips the last two). `game-copy` junctions are unlinked first and never followed. Log: `probe-out/cleanup/<date>.log`.
