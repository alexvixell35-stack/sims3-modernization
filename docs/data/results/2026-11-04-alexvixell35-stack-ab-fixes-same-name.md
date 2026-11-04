# --ab and --fixes-on with the same name (2026-11-04)

Machine: Steam, contributor alexvixell35-stack. Queue combo-local (local), save "1111 kit5".

## What happened

- Job `combo-jobyield`: `run.profile.ab = "jobyield"`, and `fixesOn` included `jobyield`.
- After the world load, profile-world.ts stopped with `Error: --fixes-off / --fixes-on / --ab name jobyield twice`. The job was a FAIL in 3.3 min, without any profile.

## Cause

- `--ab jobyield` flips `[RouteAsync] JobYield`, the Ts3nCounters field at +0x45CC (`counterSwitches`). It does not touch the ts3fixes `patches` switch `jobyield`.
- The ts3fixes switch `jobyield` (step-aside, RouteBackoff.cs) acts only while ts3native answers JOBBUSY, so only while the counter switch is on.
- So `--ab jobyield --fixes-on jobyield` is the A/B of both pieces together. The guard treated every `--ab` name as a `patches` switch and refused it.
- queue.ts did not run the check, so the error came only after the game launch and world load.

## Fix

- `abFixesClash(ab, fixesOn, fixesOff)` in native-switches.ts: a name in both fixes lists, or an `--ab` name that `--ab` flips as a `patches` switch (not native, not a counter switch) and that is also in a fixes list.
- profile-world.ts uses it in place of its own rule.
- queue.ts runs it on every profile job when it reads the queue, before the game starts.
- The native-switches self-check covers the counter case (jobyield), the native case (routeasync), a `patches` clash (idlememo) and a name in both fixes lists.
- docs/TESTING.md: one sentence on the "Whole-run switches" line.

## Checks

- `node tools/harness/native-switches.ts --self-check`: ok.
- `node tools/harness/queue.ts --self-check`: ok.
- `queue.ts --dry-run` on a copy of the failed job: accepted. On the same job with `ab` and `fixesOn` both `idlememo`: `job 1 (clash-b): run.profile ab / fixesOn / fixesOff name the switch idlememo twice`.
