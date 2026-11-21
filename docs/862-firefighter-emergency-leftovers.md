# 862: Firefighter emergency player report: four symptoms mapped, `fireleftover`

2026-11-21 (UTC), static (EA code read in a local decompile, not committed). `fireleftover` built, default off, NOT RUN.
Follows docs/570 (firefighter logic, `fireactivejob`, `firetrapcount`) and docs/681 (`firegreet`); not redone here.

## 1. The report

A player's video of one emergency, with the household firefighter (verbatim):

1. "first, the sim waits a really long time before taking action and leaving home"
2. "at 2:00, he is just standing even though there's more fire to be put out"
3. "after 5:20 im trying to find the last person to be rescued, but there's nobody left"
4. "after vlad has been convinced to run, my sim just sits down to watch tv. i have to wait the timer to run out to fail
   and get rid of the emergency. that one took 12h"

The video is not available. Logs: the emergency's folder has a `ts3native.ini` from 11-13 and NRaas ScriptErrors from
11-15 (no `ts3native.log`); a later log (11-21, ts3native 0.36.298-m1121b) shows `kFireTrapCount` and `kFireActiveJob` on but
has no emergency in it. `firetrapcount` first reached players in the 11-18 public sync (on by default), so the 11-15 game
ran without it [verified: git history of `FireTrapCount.cs`].

## 2. Symptoms against EA's code

| # | Cause | Verdict |
|---|---|---|
| 1 | [verified] When an emergency starts, `FireFightingJob.MakeMapTagForRandomJobVisible` rings the alarms on the firefighter's lot (`ActiveFireFighter.StartAlarms`), and `UpdateMapTag` sets normal speed and starts `StartRespondToEmergencyAlarm`: the Sim is pushed to respond only after `kMinutesTillSimGoToEmergency` = 60 sim-minutes (tunable), and only while on a lot with a fire alarm (`RespondToEmergencyCallback`). Until then EA waits for the player to click Respond to Emergency (alarm or map tag). Once going, `GoToOccupationJobLocation.Run` sets the whole job up (`FireFightingJob.SetupTasks`: route plans for fire spots, trap spots, every window) on the responding Sim before `RespondToEmegency.Run` starts, and the driver then waits for household firefighter passengers with no timeout. | by design (60-minute auto push). The passenger wait is a lead (§4) |
| 2 | [verified] `ExtinguishBase.Run` keeps going only through fires of its own kind: `Fire.Extinguish` collects fire tiles, burning windows need `Windows.ExtinguishWindow`. Each fire's route goal must be in the fire's own room (`Fire.PlanRouteToExtinguish`); fires behind doors `LockDoorsInRoom` locked around trapped Sims (break the door down first), or behind entrance doors held locked until the window fires are out (`UnlockEntrances`), are skipped. `Fire.StartExtinguishAlarm` and `EmergencyFireObject.TestGoingOut` are empty, so such a fire burns until reached, and the chain stops at the first route that fails while being walked. A player Sim then idles. [inferred] which of these the video shows. | by design / not confirmed; no fix (§4) |
| 3 | [verified] One rescue task is added per trapped Sim and is done only by the job owner's own Convince to Run / carry out / dig out (`SimCompletedTask`; `Occupation.OnSimCompletedOccupationTask` ignores events from any other Sim, so a rescue by another firefighter frees the victim and leaves the task); a trapped Sim who leaves the lot or is deleted keeps its task (`FirefighterEmergencySituation.OnSimLotChanged` / `OnParticipantDeleted` only drop the victim), and apartment trap spots could be in hidden units. | covered by `firetrapcount` (docs/570 §5), on since 11-18; the player's 11-15 game predates it. Other ways out of the trapped state: `fireleftover` (§3) |
| 4 | [verified] The job ends only when its pending task list is empty (`Job.OnTaskComplete` -> `OnCompletion`). With a rescue task nobody can do, nothing completes it; `Job.OnJobTimeout` re-arms `kTemporaryTimeoutIncreaseForJob` for as long as the job's firefighter is on the lot, and when the last firefighter leaves `OnSimLotChanged` restarts the timer at `kJobTimeOutTime` = 5 h. Hence hours of waiting. The task left was another victim's: one no longer there (symptom 3), or one freed by another firefighter [inferred from the report]. | the stuck task: `firetrapcount` + `fireleftover`; the timeout extension itself is by design |

EA's only leftover check is for fire tiles: `FireManager.UnregisterFire` (last fire out) -> `FireFightingJob.StartFireCompleteAlarm`
-> 15 sim-minutes later `OnForceSendCompleteFireTaskAlarmCallback` completes the fire tasks if no fire is left [verified].
The service fire department stays away from emergency lots (`Firefighter` request tests `EmergencyFireSituation`) [verified].

## 3. Fix: `fireleftover` (ts3fixes `FireLeftover.cs`, `FireLeftoverCore.cs`, default off)

Once a sim-minute (the NeverPersisted alarm `firegreet` already makes at world load; each fix reads its own switch), while
`ActiveFireFighter.sActiveJob` is set up (its `FirefighterEmergencySituation` exists and `PendingTasks` is built):

- Rescue tasks: pending `TrappedSims` above the number of the job's own trapped Sims (`mTrappedSims`) still on the job lot in
  `TrappedInRoom` or `TryingToBeCarriedOut` are dropped the way `SimDied` drops one (`Tasks.Remove`, then EA's
  `OnTaskComplete`, which completes the job when nothing else is left). The same for `TrappedRubble` against
  `mRubbleTrappedSims` in `TrappedUnderRubble`. Passers-by who failed to run are not counted (they can still take a task
  in EA's code, which only lowers the count).
- Window fires: a pending `WindowFires` task with no spreading window fire left (`FireManager.HasMajorWindowFires`, EA's own
  test at the end of an extinguish run) is completed with EA's call (`OnTaskComplete(WindowFires, lot)`, which also unlocks
  the entrances). EA completes it only when one of the firefighter's own extinguish runs sees the last spreading window go
  out; rain, sprinklers, a unicorn or a reset in the middle of the run leave it pending [verified callers].
- Each decision needs the same state for 15 sim-minutes in a row, the wait of EA's own fire-task fallback. EA has a short
  gap of its own: a dig destroys the rubble pile during its Exit animation (`FireRubblePile.ClearFireRubble`, which frees the
  victim through `OnRubbleDisposed`) and only then calls `SimCompletedTask` [verified]. Acting inside that gap would let EA's
  late completion take a second rubble victim's task; 15 minutes is far longer than the animation. Carry-out and Convince to
  Run set the state and complete the task in one step, and a Sim being carried counts as trapped [verified].
- A late EA completion of a task the fix already took is a no-op: `Job.OnTaskComplete` acts only when the task is still
  pending, and `UnlockEntrances` only unlocks doors that are still locked [verified].

What it covers beyond `firetrapcount`'s two events: it works from state, not events, so it also catches a victim rescued
by another firefighter than the job owner (the owner's task stays; see symptom 3) [verified code; how often: inferred], a trapped Sim
moved off the lot before the situation subscribed (the trap teleport happens in `SetupTask`, before the situation exists)
[inferred], a rubble pile gone without the dig (`OnRubbleDisposed` marks the Sim free and completes no task) [verified code;
how a pile goes without the dig: inferred], and the window-fire task. Nothing else changes. Household Sims: only the job's
outcome for the household firefighter (EA bug fix, CLAUDE.md); NPC victims are not touched.

Counters: [2178] rescue tasks dropped, [2179] window-fire tasks completed, [2180] errors (slots taken by hand: free in source
and registry). Offline selftest `tools/selftest/fireleftover`. Repro `fire-leftover-stuck` (`FireLeftoverCheck run`: off
`verdict: BROKEN`, on `verdict: FIXED`; prints the live emergency's pending tasks, trapped Sims still on the lot and window
fires).

## 4. Leads, not fixed

| Where | Finding | Why not fixed |
|---|---|---|
| `RespondToEmegency.Run` | the driver waits, with no timeout, while any household firefighter passenger still has the fire-engine GetInCar queued; a passenger asleep or in a long interaction holds the engine (symptom 1 in a household with two or more firefighters) [verified code, inferred effect] | needs a rewrite of EA's Run (driver uncancellable); no evidence the player's household has two firefighters |
| `ExtinguishBase.RemoveFire` | a route that plans but fails while walked ends the whole extinguish chain (only planning failures move on to the next fire) [verified code] | no log of such a failure; may be the "standing" of symptom 2 |
| `Fire.StartExtinguishAlarm` | empty, while windows put themselves out after `kMinTillExtinguish` when unreachable; emergency fire tiles never go out by themselves (`EmergencyFireObject.TestGoingOut` empty) [verified] | a fallback would also put out fires behind doors the player is meant to break down |
| `Occupation.OnSimCompletedOccupationTask` | an event from a Sim with no occupation (e.g. mopping) clears `mTaskEventListener` but keeps the listener; a later `RegisterForTaskEvents` adds a second one, so each task completion counts twice (rescue counts drop by two, task pay twice) [verified code] | the opposite of this report (jobs end sooner); separate fix if seen |
