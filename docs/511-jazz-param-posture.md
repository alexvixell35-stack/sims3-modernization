# 511. A jazz parameter for a Sim with no posture (`jazzparam`, issue #166 build 21)

An ErrorTrap report (issue #166, build 21) showed `NullReferenceException` in `Sim.UpdateJazzParameter` for a Sim in
Sunset Valley during a scripted run: the Sim had no posture at that moment. The EnjoyingMusic buff was being added to it
as a listener of `MusicalInstrument.StartBroadcast`, inside another Sim's skill level-up. This doc describes the
ts3fixes switch `jazzparam`. Off by default. Counters 2035-2036 (registry `fixes-2035`), ledger §909.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. The bug

- **One half is checked, the other is not** [verified, IL]. `UpdateJazzParameter(name, value)` sets the parameter on the
  current interaction's state machine when there is an interaction and it has one. Then it reads
  `Posture.CurrentStateMachine` without checking `Posture`, which is the Sim's `mPosture` field.
- **Callers** [verified, IL]: `BuffEnjoyingMusic.OnAddition`, `BuffEnjoyingMusic.OnRemoval`, `Sim.PlayFacialIdle`. From the
  buff, the exception leaves `BuffManager.AddElement` or `RemoveElement`, so it ends whatever added or removed the buff:
  in the report, the instrument's broadcast inside another Sim's level-up.
- **When a Sim has no posture** [inferred]: briefly while it is being set up or reset. Prior fixes: none found in NRaas'
  assemblies we have or online.

## 2. The fix (`jazzparam`, `mods/ts3fixes/src/JazzParam.cs`)

A hook on `Sim.UpdateJazzParameter(string, bool)`. EA's method runs unchanged inside a try/catch. `JazzParamCore.Decide`
picks the outcome. A `NullReferenceException` while the Sim has no posture is counted [2035], also with the switch off,
and rethrown when the switch is off. With the switch on it is caught [2036]: the interaction half already ran, and there
is no posture to set the parameter on. That is what the player's core build does with a check. Every other exception, and
any exception while the Sim has a posture, passes.

## 3. Checks

- **Self-test** (`node tools/selftest/jazzparam/run.ts`): every exception kind, posture and switch combination.
- **Repro** `jazz-param-check` (queue `jazz-param.json`, Baseline-BP, `Fixes.JazzParamCheck`). It needs EA's method, so it
  runs with the original core mod (a core with the player's Build 21 checks the posture). Each expect step takes a Sim's
  posture away for one call of `UpdateJazzParameter` and puts it back in a finally. `expect thrown` (off): EA's
  `NullReferenceException`, seen +1. `expect control` (on, posture in place): nothing counted. `expect caught` (on): no
  exception, caught +1. Each throws on a wrong outcome.
- **What the repro does not build**: a Sim that really has no posture while a music buff is added. It checks the hook's
  handling of EA's exception, not when the game reaches that state.

## 4. Results

**Run 1** (2026-11-01, Baseline-BP, `bp-jazz-param-check` PASS in 8.9 min, original core mod) **[verified, log]**:
on Tiara Angelista, `expect thrown` (off): EA's `NullReferenceException`, seen +1, caught +0. `expect control` (on,
posture in place): no exception, nothing counted. `expect caught` (on): no exception, seen +1, caught +1. The posture
was back after each step.
