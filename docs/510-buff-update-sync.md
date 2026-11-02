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
- **Without ErrorTrap, the Sim's update task ends** [inferred, not tested]: motives, buffs, mood and autonomy stop for
  that Sim until something builds a new update task for it.
- **With NRaas ErrorTrap, the Sim is replaced** [verified, runs 1 and 2]: after the throw the picked Sim object was
  destroyed and the Sim came back as a new object, whose motives move. So in a setup with ErrorTrap the cost is a reset
  Sim (its interactions dropped) rather than a frozen one [inferred: the reset is ErrorTrap's; its report does not say].
- **Seen live** (run 1, 2026-11-01, Baseline-BP, original core mod) [verified, ErrorTrap report]: the off arm's throw came
  out of `BuffManager.UpdateBuffs` as `InvalidOperationException` ("out of sync", from the dictionary enumerator), through
  `Autonomy.Update`, `SimUpdate.Simulate` and the script proxy. With NRaas ErrorTrap installed, the Sim object was then
  replaced: the check found the old object's buff manager gone. Run 2 showed the same.

## 3. The fix (`buffupdate`, `mods/ts3fixes/src/BuffUpdate.cs`)

A hook on `BuffManager.UpdateBuffs(float)`. EA's method runs unchanged inside a try/catch. An exception that
`BuffUpdateCore.OutOfSync` recognises (an `InvalidOperationException` whose message says "out of sync" or "Collection was
modified") is counted [2033], also with the switch off, and rethrown when the switch is off. With the switch on it is
caught [2034] and the first five are logged with the Sim's name. Then EA's method runs once more with no time passed,
so the walk reaches every buff in the same tick: each buff's `OnTimeOutUpdated`, and the removal of buffs that timed
out. Every other exception passes, also from the second walk.

- **`AttemptToPrioritize` runs again** for the buffs with a solve commodity that the first walk reached. EA's
  `AutoSolveRecord` attempts once only (`mbAttempted`) and only with auto-solve enabled, so the repeat does nothing for a
  record already attempted [verified, decompile].
- **The second walk is safe to repeat** [verified, IL]. With no time passed, no timer moves. The overrides of
  `OnTimeOutUpdated` act on sim-clock time or on their own state (last update time, tick targets, a check for an
  interaction already queued), so a second call in one tick does nothing new. Male pregnancy pauses its buff when it
  acts, and the walk skips paused buffs. The suntan's event is marked sent before the first walk breaks.
- What it still loses: the buffs the first walk did not reach miss this tick's countdown, so they last one tick longer.
- **Not ported**: the player's core build walks a copy of the table, so the walk never breaks. A hook cannot add that
  without rewriting EA's method. The limit: if the second walk also throws out of sync (a buff that changes the table
  on every call), the rest of that tick's walk is skipped.

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

**Run 1** (2026-11-01, Baseline-BP, `bp-buff-update-check` FAIL in the check): the off arm reproduced the bug (§2,
"Seen live"). The check then threw `NullReferenceException` on the replaced Sim's missing buff manager. The check now
keeps the Sim description and reports whether the Sim object is the same or replaced.

**Run 2** (2026-11-01, Baseline-BP, `bp-buff-update-check` PASS in 8.8 min, original core mod) **[verified, log]**: on
Tiara Angelista. Off: the listener ran once, seen +1, caught +0, and ErrorTrap logged the "out of sync" exception. The
Sim object was then replaced (the picked one destroyed), and the new one's motives moved. On: the listener ran once,
seen +1, caught +1, the Sim object the same, Inspired present; 20 s later its motives had moved. Inspired was gone by
then; the check does not say why (not part of the claim).

**Run 3** (2026-11-01, Baseline-BP, `bp-buff-update-check` PASS in 8.4 min, original core mod, with the second walk)
**[verified, log]**: off as in run 2 (seen +1, caught +0, the Sim object replaced). On: seen +1, caught +1, walked again +1
(the second walk finished), the same Sim object, Inspired present, motives moved 20 s later.
