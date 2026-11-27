---
name: ts3-night-run
description: Start or resume unattended game test queues (night runs, chains of queues) with clean-machine checks. Use for "start tonight's run", "run these queues", "resume the night run".
---
# Night / chained runs

A night chain is set up with `tools/harness/night-plan.ts` (since 2026-11-24): the rules below are code there, not memory.
Hand-written chain scripts are no longer the way: they missed the watcher, a junction, a timeoutS, an expectFail and a regex
on 2026-11-24 (probe-out/queue/day-reruns-1124.md).

```
node tools/harness/night-plan.ts --check plan.json     # preflight only: refuses with one list
node tools/harness/night-plan.ts --start plan.json     # preflight + probe-out/queue/chain-<night>.sh + detached start (nohup + disown)
node tools/harness/night-plan.ts --watch <night>       # the session's wake call: run_in_background, restart after each wake
```

Plan file: `{ "night": "night-1125", "cutoff": "07:45", "rgbOffUntil": "09:00", "runs": [{ "worktree": "agent-x", "queue": "q-name", "est": 30 }, { "branch": "bugfix/y", "queue": "..." }] }`
(`worktree` "." = main checkout; est from the plan, else the jobs' `[est a-b min]` notes; times are machine-local PHT).

## Checklist (what `--check` enforces; fix every REFUSED line, never route around one)
- worktree exists, with a `game-copy` junction (created when missing)
- every pinned file (asi, fixesPackage, agent) is a builds.json entry of its worktree and passes `builds.ts check`
- agent package per queue: queue/job key `agentPackage`, else the builds.json ts3agent entry built on the branch, else the newest in
  its history; a missing file refuses, no guessing
- `queue.ts --dry-run` clean (no `** MISSING / REFUSED **` line; modSet `set.json` needs a `files` list)
- slow repro steps (place_objects, remove_objects, wear_parts, restore_outfit, travel_*, load_*, fresh_world) carry `timeoutS`
  (default reply timeout 8 s)
- every logRequire regex has regexCases from real log lines, and they pass (markers split across lines; arm-line counts)
- a job whose note says it FAILs by design has `expectFail: true`
- disk gate (`cleanup.ts gate`, docs/283) passes; total est fits before the cutoff
- the generated script has no rm / del / Remove-Item and no elevation, and every child reads `/dev/null`

## What the generated chain does
- takes the chain lock `probe-out/queue/night-chain.lock` first (one chain at a time, since 2026-11-27: nogame / wait-job-end
  alone miss the gap between another chain's queues); a second chain waits for it, a dead owner's lock is taken over, the
  EXIT trap releases it
- waits for TS3W.exe and the running job to end; main checkout on master; Bin snapshot + `trap restore EXIT` (Bin state per queue:
  queue key `binState` (a `turbo-compat.ts` state, unknown refused = skipped; `binStage` files copied + sha256 checked first),
  else rewind auto -> apex-rewind when the worktree has the rewind tool (its own, or the main checkout's built from the same
  sources, copied in), else apex. `apex` = our fork build (install-copy output for FORK.sha: asi, bridge pair, ts3wrap.exe;
  rewindMode off); apex-rewind = apex + rewindMode; apex-oop-atlas = apex + atlas-oop.dll; stock Apex only as `apex-stock`.
  A missing or stale fork install refuses (`--check` problem, queue SKIPPED), never stock. queue.ts logs the Bin asi sha per
  job (row `apexAsi`, "fork <sha8>" or "NOT the fork build"). Bin back to apex after a non-apex queue; bridge only)
- per queue: dry-run (refused = skipped), agent copied into the worktree's `build/ts3agent.package` and put back after,
  `timeout` at 2x est (>= 30 min), skip on timeout / error / refusal, no queue starts past the cutoff
- one rerun of the FAIL / VOID / CRASH jobs only (`--only`), never the whole queue, never expectFail jobs
- after each queue `night-plan.ts --triage` writes `probe-out/queue/<night>-triage.md` (class + suggested fix: build file
  missing / step timeout / regex miss / early log miss / game never started / crash / harness error / refused / queue timeout) and,
  on a failure, a line in `<night>-wake.log`; a queue that fails in under 25% of its est is flagged **FAST FAIL: likely setup**
- `rgbOffUntil`: AlienFX lights off every 2 min until that time (`%LOCALAPPDATA%/AlienFX Tools/alienfx-cli.exe`, when present)

## Session rules
- The orchestrating session MUST start `node tools/harness/night-plan.ts --watch <night>` with run_in_background right after
  `--start`, and again after every wake (exit 0 = new FAIL / STOP / DONE line; exit 2 = timeout, re-arm). On a wake: read the
  triage block, hand the failed job to a ts3-fail-triage sub-agent at once, decide; never wait for the morning.
- Main session decides only; log reads, triage and fixes go to sub-agents (AGENTS.md, "Main agent orchestrates").
- No game runs, no `queue.ts`, no harness merges while a chain runs (`node tools/harness/track.ts status`); tools/harness changes
  merge only between runs. No agents during timing queues (CPU noise).
- No hand-written rm / del / Remove-Item, nothing elevated or interactive. Kaspersky stays on: it drops `.tmp` files and gives EPERM
  on deletes; suspect it first, never reconfigure it.
- Machine quiet for timing queues only (Rodney says when he uses it; external load >= 10% or a clock drop = rerun); Spotify is
  stopped only on night runs (`TS3_NIGHT=1`), other apps close only with `night-run.ts --close` on Rodney's go (needs lock or idle).
  Correctness queues may run in the daytime when they unblock work.
- A timing queue over ~1 h starts with a smoke job (queue.ts runs it; per-arm split, heavy tools off, switch read-back).
- Fail fast: the 30 s guards stop dead games; early logRequire (below) stops a job whose repro line is already missing.
- A crash gets crash triage before any further rerun (crash cause before fix); cancelled jobs are no data; read results as each
  queue ends. Times in PHT.

## queue.ts keys for night runs (2026-11-24)
- `expectFail: true` (job): a FAIL row is a PASS as expected (no rerun); a PASS row is a FAIL (the repro does not separate the
  arm); crash / kill / N/A / VOID stay what they are.
- `agentPackage` (queue or job): build/ts3agent.package is set from this file before the game starts (sha read back) and put
  back at the queue end.
- early logRequire: on a profile job with a repro, a step with `"expectLog": true | ["<logRequire regex>", ...]` checks those
  lines right after the step, and the repro's last step checks every rule (a 0-line rule fails); a miss stops the job with
  `FAIL early` instead of profiling on. Rules only the profile window writes get `checkAt: { "<regex>": "end" }` (or `checkAt: "end"`
  for the whole job).

## Older entry points
`node tools/harness/night-run.ts --queue-list a.json,b.json --close [--stop-on-fail] [--resume]` (clean machine: closes the
everyday apps, runs a queue list in the main checkout) stays for one-checkout runs; night-plan.ts is for multi-worktree nights.
Check progress with the ts3-status skill. Watching a single log: `node tools/harness/watch.ts <log> --match "<verdict re>"
--until "<end re>" --timeout 1800` in a Monitor, never `tail -F` (`track.ts clean` kills orphan `tail` watchers).
Rewind (docs/270 §30): queues with `"rewind": "auto"` start same-start jobs from a rewind; the chain sets Bin state per queue.
