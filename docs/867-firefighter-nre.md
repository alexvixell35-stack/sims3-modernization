# 867: Firefighter emergency NREs, `firenre`

2026-11-22 (UTC), static (EA code read in a local decompile and its IL, not committed). Fix built, default off, NOT RUN.
Follows docs/570 §2 rows 4 and 6 (leads); both are real.

## 1. Question

Two leads from docs/570 §2: (4) `FirefighterEmergencySituation.FirefighterAddedToLot` calls `RestartTimer` on a null
`CurrentJob` for a visibly pregnant firefighter; (6) `FireFightingJob.OnCompletion` calls `StartGratefulSimAlarm` on a Sim
that left the career. Are they real, what does the player see, and what does each NRE skip?

## 2. Row 4: pregnant firefighter at the emergency setup

Findings [verified, decompiled + IL unless marked]:

- `FirefighterAddedToLot(Sim)` ends with `Occupation?.CurrentJob.RestartTimer(...)`: the IL tests only the occupation for
  null, then calls `get_CurrentJob` and `Job::RestartTimer` on its result. `ActiveFireFighter.CurrentJob` returns null when
  its owner `IsVisuallyPregnant`, so the call throws a NullReferenceException.
- The situation's constructor calls it for every Sim on the job lot that is `IsActiveFirefighter && IsSelectable`;
  `Sim.IsActiveFirefighter` has no pregnancy test. `OnSimLotChanged` (firefighters arriving later) excludes visibly pregnant
  Sims, the constructor does not. Job lots exclude the owner's own home lot, so the pregnant firefighter has to be on
  another lot when the emergency is set up: rare.
- The constructor runs inside `FireFightingJob.SetupTasks` after `base.SetupTasks` has lit the fires and trapped the
  victims, and that runs from the responding Sim's go-to-job interaction, which has no catch. What the player sees: a
  script error, and the responding firefighter is reset (its queue and the drive are dropped) [verified path: ScriptProxy
  error handling resets the Sim].
- Afterwards: the job is marked set up (so a second response goes straight to the lot), `mSituation` stays null, and the
  half-built situation stays registered in `Situation.sAllSituations` with the victims' roles; `CleanUpJob` never exits it.
  The emergency runs without its situation: arriving firefighters get no rescue interactions (only the pregnant Sim got
  them), no emergency motive, no timer extension. [inferred] The trapped-Sim tasks then cannot be done and the job times
  out. If it does complete, `OnCompletion` skips pay, XP and the notice (it needs `mSituation`) [verified].

Fix: `FnSitHook.AddHook` (swap of `FirefighterAddedToLot`, stub `AddOrig`). Before EA's body it reads the Sim's occupation
and `CurrentJob` (both plain getters). Not the null case: EA's body as is. Null case, switch on: EA's body runs inside a
catch for NullReferenceException; after the throw it runs the two lines EA would have run next (fire spread, and window fire
spread for LargeFires; both internal, called by reflection). So only `RestartTimer` is skipped: the pregnant Sim has no
current job whose timer could be extended. [inferred] An NRE earlier in EA's body would be caught the same way; none is
known.

## 3. Row 6: thank-you call for a Sim out of the career

Findings [verified, decompiled unless marked]:

- `OnCompletion` pays every Firefighter that is an `ActiveFireFighter` (its loop skips the others), then picks the
  owner's Sim if it is in the situation's `Firefighters`, else a random member, shows the completion notice for it
  (`Occupation.ShowOccupationTNS`) and, on `RandomChance(kChanceToReceiveThanksCall)` (60), calls
  `(sim.Occupation as ActiveFireFighter).StartGratefulSimAlarm(...)` with no null test.
- A Sim stays in `Firefighters` until it leaves the lot; `OnParticipantDeleted` does not remove it. So a member who quit
  the career (or changed career) on the lot can be the pick. The owner is picked whenever listed, and tasks complete
  only through the owner, so the pick is a random member only when the owner is not listed at completion (e.g. it carried
  the last victim off the lot). [inferred] rare.
- The NRE skips: the grade sting, `ShutDown` (map tag, timeout alarm, removing the job from the occupation),
  `CleanUpJob` (situation exit, fires, door unlocks, minute listener, station alarms), `NumJobsCompleted++` (earthquake
  chain) and the job-completed event. Pay, XP and the completion notice came before it. `EventTracker.ProcessEvent`
  catches the exception and removes the listener, which is the owner's task listener: the owner's later active-career
  tasks never complete until the next load [verified path]. [inferred] The leftover timeout alarm later shows a failure
  notice.

Fix: two swaps, EA's body untouched.

- `FnJobHook.CompleteHook` (swap of `OnCompletion`, stub `CompleteOrig`): when a Sim EA can pick has no
  `ActiveFireFighter` occupation it arms the observer, runs EA's body, and restores the tuned
  `kChanceToReceiveThanksCall` when it returns (finally). Otherwise EA's body only.
- `FnTnsHook.TnsHook` (swap of the static `Occupation.ShowOccupationTNS(string, Sim, bool, bool)`, stub `TnsOrig`): while
  armed, a notice for one of the job's Firefighters sets the roll for that Sim: 0 for a Sim with no fire career (switch on),
  else the tuned value. EA names its pick last, right before the roll, so `RandomChance(0)` (one draw, always false)
  skips the null call and EA runs the rest.
- Not a copy of `OnCompletion`: it calls private and protected members, and a reflection call into a method another mod
  swapped throws TargetException (SwapInvokeDiag.cs [verified in game]); `CleanUpJob` is swapped by `firedoorkeep`.
- `shortcut:` the roll stays 0 until `OnCompletion` returns, also across a yield in `ShutDown` / `CleanUpJob`; only another
  `FireFightingJob.OnCompletion` in that window would read it (one emergency at a time [inferred]).

## 4. Counters (contributor range, reserved by hand)

`node tools/registry.ts reserve` finds no free 4-slot block past 4089; 2354-2357 are free in the source and registry here
and in the open contributor branches (checked 2026-11-22).

| Slot | Meaning |
|---|---|
| 2354 | `RestartTimer` skipped for a firefighter with no current job (row 4, switch on) |
| 2355 | thank-you rolls set to 0 for a pick with no fire career (row 6, switch on; EA throws on 60% of them) |
| 2356 | either case seen with the switch off (row 4: EA throws; row 6: the pick EA rolls for) |
| 2357 | errors (detection or the reflection calls; EA's path runs) |

Household Sims: an EA crash fix (AGENTS.md); it acts on household firefighters only by skipping the null call.

## 5. Check and repro

- `Ts3Fixes.Fixes.FireNreCheck("run")`: `FnCore` (FireNreCore.cs) for the bug layouts (pregnant firefighter, pick out of
  the career) and controls (firefighter with a current job, Sim with no occupation, pick in the career, owner listed and in
  the career, nobody listed, every listed Sim in the career). Off: `verdict: BROKEN`; on: `verdict: FIXED`. Read-only.
  `"result"`: counters 2354-2357, members missing, live path.
- Repro `fire-nre` (tools/harness/repros.json): off run / result, on run / result. Pure check, any world.
- Offline: `node tools/selftest/firenre/run.ts` (in selftest-all).
- End to end (not run): testingcheatsenabled, a visibly pregnant household firefighter visiting a lot, debug Spawn Job
  on that lot, respond with another household firefighter; off: script error + reset, `result` shows 2356; on: 2354 and
  a normal emergency.

## 6. Not done / next

- No game run; the in-game effects in §2 and §3 marked [inferred] and the reachability of row 6 are not observed.
- simler90's core edits `StartGratefulSimAlarm` (docs/data/mod-method-diff.csv), not `OnCompletion`; untouched here.
- The orphaned situation of an already-broken emergency in an old save is not cleaned up.

## Import 2026-11-23 (maintainer)

PR #645 by Dalexlol, counters 2354-2357 kept (free on master, reserved in `registry-reservations.json`). `firenre` is **ON by default** at import: his harness repro `fire-nre` (pass 13, Isla Paradiso) is off BROKEN / on FIXED with controls and errors 0, plus a 27-minute smoke and a 5-save soak, and the selftest passes. Household rule: an EA crash fix, the same for every Sim; it only skips the null call that would throw (the rest of EA's body runs), so household and selected Sims are not changed in any other way. [inferred] The roll of the thank-you call stays 0 until `OnCompletion` returns (his note, §5). Repro `fire-nre` added to repros.json.
