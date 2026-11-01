# 510. The buff update walks a table that its own buffs change (`buffupdate`, issue #166 build 20)

An ErrorTrap report (issue #166, build 20) showed `InvalidOperationException` "out of sync" from a dictionary enumerator
inside `BuffManager.UpdateBuffs`, for a Sim at a community lot in Isla Paradiso during a scripted run. This doc explains
how a buff update can change the table it walks, what that costs the Sim, and describes the ts3fixes switch `buffupdate`.
Off by default. Counters 2033-2034 (registry `fixes-2033`), ledger §908.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. How the walk breaks

- **The walk is over the live table** [verified, IL]. `UpdateBuffs(simMinutesPassed)` has three loops. The first, over
  delayed buffs, walks a copy of the list. The third walks `GenericManager.List`, the Sim's live buff table, and for
  every buff with a running timeout calls `BuffInstance.OnTimeOutUpdated()`, and `AutoSolveRecord.AttemptToPrioritize`
  for buffs that are solved by a commodity. Buffs that timed out are removed only after the walk.
- **A buff can change the table from inside the walk** [verified, IL]. Several `OnTimeOutUpdated` overrides do more than
  bookkeeping. `BuffSuntan.BuffInstanceSuntan` sends `kBronzed` once the tan is `kDaysForBronzed` days old;
  `EventTracker.ProcessEvent` runs every listener of that event at once, so a listener that adds or removes a buff on
  that Sim changes the table during the walk.
  [inferred] Other overrides (male pregnancy, gasping for air) queue interactions; a social or reaction that a mod or EA
  runs at once from there could change the table too.
- **Then the next step throws** [verified, self-test on .NET; game message from the report]: the enumerator sees the
  table changed and throws `InvalidOperationException`.
- **Which buff did it in the report** [inferred]: not known. The report's lot and world fit the suntan path, but a mod
  listener or another override fits too.

## 2. What it costs the Sim

- **Nothing catches it** [verified, IL]. `Autonomy.Update` calls `UpdateMotives`, `UpdateBuffs`, `UpdateMood` with no
  handler. `SimUpdate.Simulate` catches only `ResetException`. ScriptCore's proxy catches the rest, asks
  `ScriptErrorWindow.DisplayScriptError` (which returns false in the release game), calls the task's `OnReset` (empty
  for a `ScriptTask`) and rethrows.
- **So the Sim's update task ends** [inferred, checked by the repro's off arm]: motives, buffs, mood and autonomy stop for
  that Sim until something builds a new update task for it.

## 3. The fix (`buffupdate`, `mods/ts3fixes/src/BuffUpdate.cs`)

A hook on `BuffManager.UpdateBuffs(float)`. EA's method runs unchanged inside a try/catch. An exception that
`BuffUpdateCore.OutOfSync` recognises (an `InvalidOperationException` whose message says "out of sync" or "Collection was
modified") is counted [2033], also with the switch off, and rethrown when the switch is off. With the switch on it is
caught [2034] and the first five are logged with the Sim's name. Every other exception passes.

- What a catch skips: the rest of that tick's walk, and the removal of buffs that timed out in it. Those buffs keep
  their timeouts and are handled at the next tick, when the buff that changed the table has already done its one-time
  work (the suntan's event is marked sent before the walk breaks).
- **Not ported**: the player's core build walks a copy of the table, so the walk never breaks. A hook cannot add that
  without rewriting EA's method. The limit: a buff that changes the table on every tick would stall the buffs after it
  in the walk, with the switch on; with it off, the Sim's update ends at the first one.

## 4. Checks

- **Self-test** (`node tools/selftest/buffupdate/run.ts`): the game's and .NET's messages, a real dictionary changed
  during its walk, and the exceptions that must pass.
- **Repro** `buff-update-check` (queue `buff-update.json`, Baseline-BP, `Fixes.BuffUpdateCheck`). It needs EA's
  `UpdateBuffs`, so it runs with the original core mod (a core with the player's Build 20 never throws). Each arm uses its
  own Sim of the active household. `arm` adds a listener on `kBronzed` that adds Inspired to that Sim once, and gives the
  Sim the Suntanned buff acquired `kDaysForBronzed` + 1 days ago with its event not sent, so the Sim's next buff update
  sends the event inside the walk. `expect thrown` (off): the listener ran, one exception seen, none caught. `state`
  (off): whether that Sim's motives still move 20 s later. `expect caught` (on): caught, Inspired present.
  `expect alive` (on): the Sim's motives move afterwards. Each expect throws on a wrong outcome.

## 5. Results

Not run yet.
