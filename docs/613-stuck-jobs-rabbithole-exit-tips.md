# 613: stuck NPC interactions: rabbit hole exit pacing, NPC Perform For Tips (s83)

2026-11-17, static (ilspycmd decompiles of game-copy/assemblies, census of 6 Bridgeport sessions), r71, built, NOT RUN.

Input: `tools/harness/wait-report.ts` ranking over phase-20261117-0008, -20261116-2349, -2318, -2233, -2218, -2212
(`health.jsonl` per-Sim census). Extra census passes for this doc: per-Sim motive freeze between snapshots (energy and
hunger unchanged = no motive update), "still since load" Sims, Sims per lot per interaction, posture.

## 1. Two EA rules that explain most leads

- **Low-LOD Sims get no motive updates** [verified, `SimUpdate.Simulate`: `Autonomy.Update` -> `Motives.UpdateMotives`
  runs only on the `IsRunningHighLODSimulation` branch]. Census: NPCs in long interactions show frozen motives
  (e.g. Watch Door 338/338 frozen, Performance 85/85). So anything that ends on a motive or buff ("until fatigued",
  Fatigued) never ends for an unseen NPC.
- **EA's own failsafe for that is DoLoop's low-LOD cap** [verified, `Interaction<,>.DoLoop(..., float)`]: a low-LOD Sim
  leaves any loop after `kMaxTimeForLowLODInteractionDuringTheDay` 420 sim-min (04-22 h) or `...Night` 120, but only when
  the interaction is autonomous or at priority Autonomous or below.

## 2. rhexitunseen (off, 3453-3456, `StuckJobs83.cs`, repro `rhexitunseen-check`)

[verified, `RabbitHole.AnimateExitRabbitHole(Sim, Slot, Sim, SimRabbitHoleState, bool)`] a Sim leaving a rabbit hole,
unless `skipWait` or `CanSimSkipExitWait(sim)`, sleeps in `Sleep(30)` steps until `kNpcExitFreq` (15 sim-min, tuning
comment "Exit frequency in minutes for NPC sims leaving this rabbit hole") has passed since the hole's `TimeLastSimLeft`,
then stamps it. One NPC leaves per 15 sim-min per rabbit hole, whoever wakes first (no order). Base
`CanSimSkipExitWait` = `sim.IsSelectable`; `SchoolRabbitHole` overrides it so pupils skip (EA's fix for a crowd leaving
at once); `ResortTower` has a SkipWait exit.

Census: 5-10 NPCs at a time inside Steve's Business Complex (`InRabbitHolePosture`) with Go Home, Eat Out or Visit as
current interaction, 50-314 sim-min still, all low LOD; that is the Go Home x8 (65-170 min) and Eat Out 147/271 min leads.

The pacing is a staged walk-out for the camera [inferred: tuning comment, selectable and school skips]. Fix as EA did
for school: `CanSimSkipExitWait` also answers true for a non-player Sim that is not high LOD
(`Autonomy.IsSufficientlyOnScreenForHighLODSimulation` false: not on the active lot, not with the player, not on screen).
EA's `GetExitSlot` still reserves exit slots. Household rule: `IsPlayerSim` Sims keep EA's answer (skip, not realistic
only, so no HouseholdToo lift). Hooked method read in full: no loop; the caller's two waits stay EA's (the slot wait has
its own 15 sim-min deadline).

Counters: 3453 waits skipped, 3454 unseen NPC waits with the switch off, 3455 household or seen Sims kept (on), 3456 errors.

## 3. tipsnpcend (off, 3457-3460, `StuckJobs83.cs`, repro `tipsnpcend-check`)

[verified, `NPCShowManager.CreateNPCTipsPerformer`] the daily NPC tips performer gets `PerformerPerformForTips` at
`CriticalNPCBehavior`, `isAutonomous: false`, so §1's cap never applies. [verified, `PerformerPerformForTips.LoopHandler`]
its only end is the Fatigued buff, which a low-LOD Sim never gets; after each trick it clears every exit reason except
CancelExternal, so a HigherPriorityNext from a queued GoHome is wiped too. Census: Perform For Tips 316-589 sim-min,
queue 2 (queued interaction never runs), motives frozen 10/11.

Fix as EA intended: in `LoopHandler`, a non-player Sim that is not high LOD and whose loop lifetime is past EA's cap for
the time of day (DoLoop's own expression) gets `AddExitReason(Finished)` and skips the trick, as DoLoop's cap does; `Run`
then takes EA's normal ending (stop performing, bounded tips wait, TNS). Hooked method read in full: no loop.

Counters: 3457 loops ended at the cap, 3458 past the cap with the switch off, 3459 past the cap kept (household or seen,
switch on), 3460 errors.

## 4. Lead table

| Lead | Verdict | Why |
|---|---|---|
| Watch Door x82 stand still, x14 queue behind (Sports Zone, Prosper Room, Banzai, Aquarius, Brightmore, Plasma 501) | intended job | Bouncer role [verified `Bouncer.SimulateRole`, `VelvetRopes.PushWatchDoor`]: two ropes per venue, one bouncer each at fixed spots (2 per lot in every snapshot); WatchDoor is autonomous so §1's cap ends it and SimulateRole cancels the queue and re-pushes it; queued autonomous items are dropped by design. Route cost covered by `bouncerskip` (docs/151) |
| Performance x9 stand still, x6 queue behind | intended job | `Piano.PianistPerform`, pianist role at RequiredNPCBehavior; EA freezes the pianist's motives on purpose (`Pianist.FreezeMotivesWhilePlaying`) |
| Perform For Tips x3 stand still + queue behind (316-589 min) | **EA bug, built `tipsnpcend`** | §3 |
| Go Home x8 stand still 65-170 min (Steve's), queue behind x3 | **EA pacing, built `rhexitunseen`** | §2; InRabbitHolePosture at 513.4,1704.1 |
| Eat Out stand still 147 / 271 min | **same as Go Home** | `MetaRabbitHoleInteraction.Run` venue wait (Gaussian Min..MaxLength) + exit pacing §2 |
| Work out until fatigued, 150-350 min | intended | §1: fatigue never rises for a low-LOD NPC; EA's 420/120 cap ends it (WorkOut is autonomous) |
| Work out until fatigued 921 min (Girl Clubber) and Take Out Trash 1122 min (same Sim, other run) | save/load state, skipped | Same Sim stuck from the first snapshot in 2 runs (still = minutes since load, motives frozen), ended only by our stationary watchdog; `EmptyTrash` (Lot neighbour activity, NonCriticalNPCBehavior) has no loop. Logged in `docs/data/stuck-log.md`; save/load is Rodney's area |
| Dance Together / Dance Wildly Together 163-323 min | intended | Partner present in every snapshot (A and B side 1 m apart); within EA's 420 sim-min day cap (Stereo `DanceTogetherA` is autonomous) |
| Watch (instruments, TV) queue behind, Hang Out 318, Read 318 | intended | Low-LOD NPC jobs under EA's cap; "since load" Sims |
| MakeConversation server waits x28, partner in OrderConversation | covered | docs/578 (bar make time, linked running, not a bug) / `npcbusypartner` docs/577 |
| Sleep Until Fully Rested / Work Until End of Shift queue behind | intended | household Sims (untouched) / work shift |

Earlier docs: none of docs/575-612 covers §2 or §3 (no `kNpcExitFreq`, `CanSimSkipExitWait`, `PerformForTips` or
low-LOD cap hits in docs or mods). `outfitwaitend` (docs/601) and `stairwaitend` (docs/612) are the same shape (an NPC wait
with no end for an unseen Sim) but different methods. NRaas (ErrorTrap `DerefPerformanceCareerPerformerPerformForTips`,
`DerefInRabbitHolePosture`) only cleans references; no reset rule for these.

## 5. Status

Built r71 (`~/dev/ts3-runs/pkg/ts3fixes.r71.package`), pairs in s83-pairs. Not added to proven-fixes. Next: the two
check repros off then on; a natural-play arm at Bridgeport evening with both on (Go Home InRabbitHolePosture census).
