# 632. Unseen loop skip: one switch at EA's shared loop for NPCs nobody can see

2026-11-17, static, package r87-loopskip, NOT RUN. Switches `unseenloopskip` and `unseenloopskip_offscreen` (default off), tunings `kUnseenLoopSkipCapMin` (30), `kUnseenLoopSkipStep` (off), `kUnseenLoopSkipStepMin` (10), `mods/ts3fixes/src/LoopSkip632.cs`, counters 3700-3708 (3709-3739 spare), repro `unseenloopskip-check`. The general form of docs/628 (bar only).

## 1. Targets (measured)

`node tools/harness/wait-report.ts <dir> --top 1000` over every phase-2026111* run with health.jsonl, "stand still" + "queue behind" rows summed (sim-min = time held x count):

| # | interaction | sim-min | count | EA class | loop | here |
|---|---|---|---|---|---|---|
| 1 | Watch Door | 187476 | 1190 | VelvetRopes.WatchDoor | DoLoop(LoopDel, 5 min) | EA: bouncer role, re-pushed |
| 2 | Work Until End of Shift | 56690 | 215 | rabbit hole work | no DoLoop | EA |
| 3 | Perform For Tips | 44670 | 104 | PoolTable.PerformForTips / PerformanceTips proxy | DoLoop(Loop) | (b) PoolTable only |
| 4 | Go Home | 26730 | 173 | GoHome | route | EA (other shortcuts) |
| 5 | Dance Together | 24007 | 145 | Stereo.DanceTogetherA | DoLoop(DanceTogetherDelegate) | (b), partner in scope too |
| 6 | Watch | 22346 | 152 | TV.WatchTV (+ many *.Watch) | DoLoop(WatchTVDelegate) | (b) TV.WatchTV |
| 7 | Performance | 21624 | 106 | ShowStage / PerformanceTips careers | DoLoop(PerformLoop) | EA: job |
| 8 | Sleep Until Fully Rested | 16764 | 55 | BedSleep | DoLoop(LoopHandler) | EA: motive outcome |
| 9 | Dance Wildly Together | 15931 | 81 | Stereo.DanceTogetherA (club) | DoLoop(ClubDanceTogetherDelegate) | (b) |
| 10 | Meet Up | 13575 | 80 | situation | no loop | EA |
| 11 | Work out + Strength Training | 12655 | 44 | AthleticGameObject.WorkOut | DoLoop(WorkOutLoop) | (b) + (a) |
| 12 | Read | 10919 | 72 | ReadBook | DoLoop(LoopDel) | (b) + (a) |
| 13 | Blues / Jam | 9181 | 33 | instrument play | no DoLoop | EA |
| 14 | Eat Out | 5009 | 30 | rabbit hole | no DoLoop | EA |
| 15 | Play Alone | 2032 | 6 | Trampoline / instrument / pool | mixed | EA |

Script cost: `callcensus` lines rank checks (BuffManager.HasAnyElement, ActionData.Test, InsertInteraction), none of them a loop body; `tools/static/hot-rank/rank.py` ranks no DoLoop callback in its top rows. The per-pass cost is the shape below (one script pass per sim-minute per looping Sim), and the larger cost is holding the object: rows 3-12 are the queue-behind side.

## 2. Choke point (verified, ilspycmd decompile)

`Interaction<TActor,TTarget>::DoLoop(ExitReason, InsideLoopFunction, StateMachineClient, float notifySimMinutes)`: every pass `WaitForExitReason(Sim.kWaitForExitReasonDefaultTime = 1 sim-min)`, `EventTracker.SendEvent(kUsedObect)`, `loopMe` once when `mLifeTime >= n * notify` (`mDeltaTime` = since the previous pass), `UpdateLoop` (tone, stage). Then EA's own off-screen fast path: if `!Actor.Autonomy.IsSufficientlyOnScreenForHighLODSimulation` and the interaction is autonomous (`Autonomous || priority <= Autonomous`), `mLifeTime > Autonomy.MaxTimeForLowLODInteractionDuringTheDay` (420, 4:00-22:00) or `...Night` (120) adds `ExitReason.Finished` and returns true; the interaction's own post-loop steps run. The two static getters have no other reader. `DoTimedLoop` polls the same 1-minute wait with a fixed duration. Overrides of DoLoop: Bathtub, CuddleRelaxing, CuddleSeated (not listed). 1164 DoLoop sites, callback use of `mDeltaTime` / `mLifeTime` tabulated by script over the decompile (not committed).

Low LOD: [verified, SimUpdate::Simulate] a low-LOD Sim skips `Autonomy.Update` (UpdateMotives / UpdateSkills), so ending or stepping its loop loses no time-rated commodity change that EA would have applied.

## 3. Design

Scope, both options: actor not household / selectable / selected (`IsPlayerSim`), low LOD, autonomous (EA's test), the Sim on its own task; a Sim target must be in scope too. Tier 1 `unseenloopskip`: `UfWhy` 0. Tier 2 `unseenloopskip_offscreen` (needs tier 1): `UfWhy` on screen / near viewers and `ZoomHidden` (docs/631: not `World.IsObjectOnScreen`, or with `zoomtier` on, too small). Re-checked at every getter ask and every step; nobody is moved. Active lot, held and seen Sims keep EA.

- (b) early end, hook on the two getters: an in-scope allow-listed loop gets `min(EA, kUnseenLoopSkipCapMin)`. The loop ends through EA's own branch with EA's end steps. It changes the town schedule (loops of 130-430 min end at 30), only where EA already ends them for low-LOD Sims. Allow list (exact types): TV.WatchTV, ReadBook, AthleticGameObject.WorkOut, Stereo.DanceSolo, Stereo.DanceTogetherA, HotTubBase.Relax, PoolTable.PerformForTips. Post-loop checked for each: EndCommodityUpdates + StandardExit / skill stop / use-list removal (WorkOut also finishes a TrainSim partner).
- (a) fewer passes, hook on `Sim.WaitForExitReason`, patched only with `kUnseenLoopSkipStep` (ApplyTuned, chains over WakePark's hook): DoLoop's own 1-minute wait becomes `kUnseenLoopSkipStepMin` for delta-aware classes. Exit reasons still end it on the same tick. Delta-aware (verified): ReadBook.LoopDel (Book.BaseLoopDel takes deltaTime; the kReadBook event is once, by lifetime), AthleticGameObject.WorkOutLoop (TimePassedEvent with mDeltaTime; animation switch by lifetime counter, catches up). Not delta-aware, stay 1 minute: Computer.PlayComputerGames (event per call), PoolTable.PerformForTips (random roll per call), TV.WatchTV / Stereo dances (yields, per-call state). Residual: kUsedObect is sent once per step instead of once per minute.

Not listed, stay EA: role and job loops (VelvetRopes.WatchDoor, ShowStage, PerformanceTips careers), sleep (BedSleep), subclasses (Violin / FairyHouse dances, ProSports / PoliceStation workouts), rabbit holes, situations, instruments.

## 4. Check

`Ts3Fixes.Fixes.UnseenLoopSkipCheck run|result`: pure scope / cap / step decisions (off: EA's 420/120 end, BROKEN; on: cap, FIXED) with controls (household, high LOD, user-directed, on screen and close, tier 2 without its switch, active lot, held, unlisted class, never longer than EA, step only for DoLoop's own wait of a delta-aware class); live: getters still answer EA's values off scope, allow list resolved, step hook state, census of listed loops by tier. Live run: 3700 grows with the switch on; the wait-report rows 3, 5, 6, 9, 11, 12 should shrink; watch for more autonomy runs (more loops ending means more choices).

## 5. Counters

3700-3701 (b) loops capped tier 1 / 2 (once per instance), 3702-3703 (a) stepped waits tier 1 / 2, 3704 listed but out of scope, 3705 class not listed, 3706 off own task, 3707 errors, 3708 Check mismatches (must stay 0). 3704 split by reason: 3709 household / selected, 3710 high LOD (our re-check), 3711 not autonomous, 3712 partner out of scope, 3713-3722 UfWhy code 1-10 (3715 household, 3716 active lot, 3717 on screen, 3718 near viewers, 3719 held, 3720 social seen). The Check also lists the 8 unlisted classes that ask EA's cap most.

## 6. r88 (phase-20261117-1411, Bridgeport): UNEXPECTED off and on

- Live getters read 360 day / 180 night: EA's tuning XML sets `kMaxTimeForLowLODInteractionDuringTheDay/Night`, so the C# 420/120 initialisers are not the live EA value. The Check now reads both fields by reflection and tests the pure decisions and the getters against them (that caused controls ok=False and the 3708 mismatches).
- Never acted: 3700 = 0, 3704 = 906 (frozen early), 3705 = 3384. The only listed loops in the run's census are household Sims (hw-2.json: `HH Kirby: ReadBook`; WatchTV / PlayComputerGames also household), off camera and low LOD, so EA asks the cap and our household rule keeps EA (inferred: 3709 confirms it next run). No unseen NPC ran a listed loop: in this soak, `unseen 0, off-screen 0` in every census.
- 3384 unlisted asks: no class breakdown in r88; the next run's Check prints the top 8.


## 7. SwingSet.Swing added (2026-11-17, s88 census)

- s88 (phase-20261117-1442 smallfix.txt) unlisted asks, on arm: VelvetRopes+WatchDoor 1453, Piano+PianistPerform 375, SwingSet+Swing 327, then rabbit hole visits. WatchDoor and PianistPerform are job loops and stay EA.
- [verified, ilspycmd SwingSet+Swing.Run] `DoLoop(ExitReason.Default, LoopCallback, ...)`; EA's low-LOD branch adds Finished and returns true; the post-loop steps then run as for any end: UnregisterGroupTalk, stop swinging unless BePushed_SwingSet is next (AnimateSim Neutral, UpdateFootprint), EndCommodityUpdates, TryMovingToAnotherObject. The callback (PlayGroundObject.LoopCallback) reads mLifeTime only. Added as `kLsEnd` (8 classes), no step.
- The Check now prints the top 15 unlisted classes.
