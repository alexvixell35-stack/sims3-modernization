# Handoff: replan-burst logging build (docs/261 §6 L1)

Branch `feature/replan-burst-log` (from `feature/jit-bundle-1113`, so the JIT-on arm carries the logging). Local only: not merged, not pushed. Built, host self-tests pass, **NOT RUN in game**. Logging only: no behaviour change, household untouched.

Design and background: docs/261 (branch `docs/npc-replan-bursts-1114`). Bursts of ~7 `Route_Replan`/s x ~40 ms for 2-11 s from `FollowRouteAction.PerformRoute` callbacks, seen only in the JIT-on SV runs of jit-bundle-ab-1113 (n = 2 per arm). Every `arRefusedReplan` there is reason 2 (`[RouteAsync] Kinds=0` -> NoStart in `GetOptionHook`), so the other checks never ran.

## Builds

| file | sha256 | what |
|---|---|---|
| `game-copy/ts3native.asi.replanlog-1114` | `f3d79d0f643a5c7f3a25e53dfde5ddf1c439827605c61773210cf403b00f7fe6` | ts3native 0.36.256-replanlog1 = jitbundle-1113 (0.36.256) + `[RouteAsync] ReplanLog` + GetOption2 q 10-13; map `ts3native.map.replanlog-1114` |
| `game-copy/ts3fixes.package.replanlog-1114` | `611b73c3dd6f380acf03e42bb096585b6e804e3691c539aae9d282c62a79b6da` | release rc1111e source (454cfb69) and tuning (same tuningDigest 0373884c) + replanlog only; source on local branch `feature/replan-burst-log-rc1111e` 350770e7 (package-only, never merge) |

Both are in `docs/data/builds.json` on this branch. The package keeps rc1111e's behaviour, so the runs compare with jit-bundle-ab-1113. ts3fixes packages are not byte-reproducible: the tuning digest equals rc1111e, and the replanlog lines show this package is the one installed.

## What it does

- **ts3native.** `[RouteAsync] ReplanLog=0|1` (default 0, read only when `[RouteAsync] Enabled=1`). It is read back in `route async: ReplanLog=1 (docs/261, ts3fixes replanlog lines)`. The `GetOption2` "would" channel (`optHi 0x88000000`, `ArWould`) gets these questions:
  - q 10: ReplanLog on.
  - q 11: Kinds has Replan.
  - q 12: the Replan IC is hooked (`arReady` bit 0).
  - q 13: LOS refusal.
  - The existing q 1 / 5 / 6 / 7 cover asyncMode, NoYield, Active and M1.
  - All are plain reads: no counter changes, no lock taken.
- **ts3native, `userdiag.cpp`.** Lines starting `replanlog` use the hitch-cause cap (6000). Without that they would fall under the shared 400-line cap.
- **ts3fixes, `ReplanLog.cs` and `ReplanLogCore.cs`.** `RlBegin` runs from `RouteSrcBegin` and `RlEnd` from `RflPlan`, kind 1 only. `ReplanHook` IL is unchanged, so no saved-stack refusal. Asks q 10 once per session; when off, the cost is one int compare per Replan.
- **Shadow check ("would").** Before the call, `RlBegin` evaluates the own kind-1 START's checks in `GetOptionHook` / `AsyncStartBody` order and acts on none of them:
  - `notOwn`: C# would not send an own START. That happens when `asyncreplan` is off, the current task is not the Sim's, or the context cannot yield.
  - `KindsOff`, `asyncOff`, `notHooked`, `LOS`, `NoYield`, `M1`, `Active`.
  - `start` = no check failed.
  - `Running` / `QFull` are not evaluated: both need a job slot.
- **Burst.** A burst is 4 or more Replans within 1 s. It ends at the first Replan more than 1 s after the last hot one.

## Log format (`ts3native.log`)

```
ts3fixes: replanlog: <ms> ms sync|async <RoutePlanResultType>, burst #<n>|single, sim <Full Name> 0x<SimDescriptionId> <Species>, scope household|selected|seenNpc|unseen|unknown why <UfWhy>, would <chain>|start, simN1s <this Sim's Replans in the last 1 s>, ia <Interaction>><TargetType>, caller <Type.Method<Type.Method<Type.Method>, dist <m to goal> route <m>, at <TickCount ms>, goalLot <hex>, lot <hex> <lot name>
ts3fixes: replanlog burst start #<n>: <calls> calls / <ms> ms in the last 1 s, at <TickCount> ms
ts3fixes: replanlog burst end #<n>: <s> s, calls <n>, ms <ms> (<ms/s> ms/s), scopes household c/ms selected c/ms seenNpc c/ms unseen c/ms unknown c/ms, Sims <n> top: <Sim> calls/ms ms; ..., interactions top: <ia> calls/ms ms; ...
```

- **When a call line is written.** For a call inside a burst, or any call of 10 ms or more.
- **Caps.** 60 call lines a real minute and 2000 a session; 400 burst lines a session. Dropped lines are counted.
- **Line length.** Lines are cut at 470 characters, so the lot name comes last.
- **Open burst at session end.** A burst still open at session end has no end line. Its calls are in the call lines.
- **Counters** (ts3fixes `int[]`, docs/registry.md):

| slot(s) | meaning |
|---|---|
| 969 / 970 | Replans seen / their ms |
| 971-975 | calls per scope: household, selected, seenNpc, unseen, unknown |
| 976-980 | ms per scope, same order |
| 981 | bursts |
| 982 | call lines |
| 983 | lines dropped |
| 984 | calls whose checks all passed except Kinds |

Reader (grep recipe, Git Bash, per job log `probe-out/queue/replan-burst-1114-<date>-<job>-<hhmm>-ts3native.log`):

```
L=probe-out/queue/replan-burst-1114-*-sv-rl-abba-on-1-*-ts3native.log
grep -h "^ts3fixes: replanlog burst" $L                                   # bursts with scope split and top Sims
grep -h "^ts3fixes: replanlog: .*burst #" $L | sed 's/.*, sim \([^,]*\),.*/\1/' | sort | uniq -c | sort -rn | head   # Sims in bursts
grep -h "^ts3fixes: replanlog: .*burst #" $L | sed 's/.*, caller \([^,]*\),.*/\1/' | sort | uniq -c | sort -rn | head # callers
grep -h "^ts3fixes: replanlog: " $L | sed 's/.*, would \([^ ]*\), simN1s.*/\1/' | sort | uniq -c | sort -rn           # would-be refusal chains
grep -h "^ts3fixes: replanlog: " $L | sed 's/.*, scope \([a-zA-Z]*\) .*/\1/' | sort | uniq -c                         # scope of logged calls
```

Count all bursts per arm with `grep -c "^ts3fixes: replanlog burst start" <log>` over the off and on jobs.

## Queue

`tools/harness/queues/replan-burst-1114.json` (this branch, dry-run clean):
- SV only, 10 min jobs, seed 12345, Mode=1, no samplers.
- Smoke first: SV, ON set.
- Then ABBA x2: off-1, on-1, on-2, off-2, off-3, on-3, on-4, off-4.
- ON and OFF = the jit-bundle-ab-1113 SV ini sets, with `RouteAsync.ReplanLog=1` in every arm. Same asi and package in every arm.

Pass: no crash, script errors 0, `^route async: ReplanLog=1 \(docs/261` read back, and at least one `^ts3fixes: replanlog: \d+ ms (sync|async) ` line. ON arms also keep the jit-bundle-ab-1113 JIT read-back rules and native requirements; OFF arms keep "no `jit: compiled` line".

- **Running time.** About 130 min plus reruns.
- **Timing rule.** It is a timing-type queue (machine noise reruns), so do not build during it.
- **Where to run it.** Run it from this worktree, or from master after merge. The "repo ini" base is the running checkout's `native/ts3native/ts3native.ini`.

```
node tools/harness/queue.ts tools/harness/queues/replan-burst-1114.json
```

Then:
- `node tools/harness/hitch-ab.ts replan-burst-1114` and `node tools/harness/arm-ms.ts replan-burst-1114`
- the grep recipe above

## Gates run

- `native/ts3native/build.cmd` with `selftest ok`, including new asserts for q 10-12 and unknown q 14. No counter moves.
- `node tools/selftest/replanlog/run.ts`: scope mapping, burst start / seed / end, top list, key cap.
- `node tools/selftest-all.ts`: 231 PASS, 0 FAIL.
- `node tools/check-docs-code.ts`: ok.
- Queue `--dry-run`: clean.

## Not done / open

- Not run in game. The first smoke must show the read-back line and replanlog lines.
- The shadow arm of docs/261 §2 (`Kinds=1 KindsMode=0`) is not in this queue. The "would" chain covers the same checks without the A10 kind-1 hook-order risk (docs/261 §2, last note).
