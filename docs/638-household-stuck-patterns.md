# 638. Household stuck patterns: Scare stand-off, 45-min Started timeouts, seated group chat (2026-11-17)

Data: phase-20261117-1703 (30 min of normal play at speed 3, Bridgeport, all proven fixes on, household "Drama on TV":
Katelyn, Kirby, Matty, William, Apollo, Indie). Three visible household stalls. Causes from the decompiled game scripts
(`game-copy/decompiled`, ilspycmd) and the log lines.

| # | Pattern | Cause | Verdict | Fix |
|---|---|---|---|---|
| 1 | Katelyn's Scare: 15 routefails, Kirby frozen in BeScared 26.9 min | scarer's stand-and-wait behind its own target | EA wait loop (docs/546 §15 row 3, docs/595 §3) | none |
| 2 | 45.1-min "Started" timeouts with the partner's B queued | receiver busy with an equal or higher priority activity; StartSync 15 + 30 min | intended by EA (docs/577 §4) | none for household |
| 3 | Seated chat starters time out at 15.2 min, receiver's GroupChatB "routing" | GroupChatB queued at Autonomous behind the receiver's activity | **EA bug** (skips the CreateSocialB rule) | `groupchatpriority` (off) |

## §1 Scare: the goal is not in the target's footprint

[verified, `Sim.BaseScare.Run`] The scarer pushes BeScared (`AddNext`), then plans `PlanToPointRadialRange(Target,
Target.Position, 1, 2.5, -forward, 70°)` with `MakeDynamicObjectAdjustments`, falling back to
`RouteToDynamicObjectRadius(Target, 1, 2.5)` (360°). The goal is a ring 1 to 2.5 m from the target. The routefail line's
"goal" is `Route.GetApproximateOriginalDestination()`, which for a radial destination is the ring's **centre**, so it
is the target's spot when the plan was made (here 492.6,1555.4; Kirby had since moved 0.5 m to 492.9,1555.0). That's a
logging artefact, not a goal inside the footprint.

What actually happened (log lines 11856-11975, day 2 hour 20.9-21.33):
- the first `PlanFromPoint` failed `NoPathToDestination`, blocker Kirby (the target), with Katelyn 1.2 m away;
- Katelyn then stood at 493.2,1554.8, **0.36 m** from Kirby, and route 259B898F logged 14 `Replan sync
  FailedBecauseNoDestinationSpecified` lines over ~25 sim-min.

[verified, `ObstacleEncounteredRouteInterruptAction`] That is EA's obstacle stand-and-wait. `PerformAction` runs a
`StandAndWaitController` for `kDefaultStandAndWaitDuration` = 50 sim-min. Each cycle, `StandAndWaitCycleHandler` calls
`mInterruptedPath.Replan()` while the plan fails and returns `true` (keep waiting). That early return also comes before
its `CancelExternal` / `MidRoutePushRequested` checks. Meanwhile Kirby's `BeScared` stands in
`WaitForSynchronizationLevelWithSim(Routed, 40)` for the scarer, so neither Sim moves until one gives up. Kirby's wait
ended at 26.9 min (CanceledByScript, SynchronizationFailed). It was not a priority wait: BeScared was already running, so
`scarepriority` does not apply.

Verdict: EA's wait loop behaving as designed; docs/546 §15 and docs/595 §3 reached the same verdict. Ending the wait
earlier would change a household Sim's timing with no EA intent to restore. The 169 other run logs that have
`Replan sync FailedBecauseNoDestinationSpecified` show the same loop at toilets, sinks and showers. Nothing built.

## §2 45-minute Started timeouts: the receiver keeps its activity

Cases: Matty→Indie PillowFight (Indie was receiver in William's social), Kirby→Katelyn SocialInteractionA (Katelyn was
receiver in Apollo's social), Katelyn→Apollo DanceTogetherA (Apollo in DanceSolo, B "linked queued").

[verified]
- `DanceTogetherA` starts through `BeginSocialInteraction`, which pushes the B with `CreateSocialB`. That method raises
  an Autonomous priority to `NonCriticalNPCBehavior` and appends the B with `InteractionQueue.Add`.
- `RemoveLowerPriorityInteractions` cancels a non-MustRun head only for a **strictly** higher priority (or
  `ShouldReplace`).
- `Stereo.DanceSolo.Run` loops in `DoLoop(ExitReason.Default)`, so a `HigherPriorityNext` would end it.

DanceSolo ran for 45 min, so it was not below the raised B: a player-ordered (UserDirected) dance. A Sim that is the
receiver of another social is at the same NonCriticalNPCBehavior and keeps that one. The 45.1 min is `StartSync`
(`kNumMinToWaitOnPreSync` 15) plus `WaitForSynchronizationLevelWithSim(Started, 30)`.

Verdict: intended. One conversation at a time, and player orders beat autonomous socials. The NPC-only early end is
`npcbusypartner` (docs/577 §4). The parked-receiver case (`socialreceivefail`, docs/546 §9) doesn't match: here the B
never started. No household change.

## §3 Seated group chat: GroupChatB never gets a turn (fix `groupchatpriority`)

Log: five household `SeatedSocialInteractionA` starters (Indie, Kirby, William, Apollo, Katelyn → Matty, and William →
Katelyn) timed out at `Committed 15.2/15 min`. In every Matty case, the `gohere start: ... interaction GroupChatB` line
(the receiver's first route) comes **3-30 lines before** the timeout. One example is line 12698 against the timeout at
12701, and Indie's wait at line 10246 still had Matty in `Fridge_Have`. So the receiver routed only once its own
activity ended, seconds before the starter gave up. It wasn't a route loop: Matty's routefails with interaction "none"
are unrelated single `PlanFromPoint` failures.

[verified]
- `SeatedSocialInteractionA : SocialInteractionA`, `MountedSocialInteractionA` and `Sim.GroupChat` push the receiver in
  their own `PushGroupChatB` as `GroupChatB.Singleton.CreateInstance(..., GetPriority(), ...)` plus `InteractionQueue.Add`.
  They bypass `CreateSocialB`, so an autonomous starter's receiver stays Autonomous.
- The receiver waits behind any autonomous head.
- The starter waits in `WaitForSynchronizationLevelWithSim(Target, Committed, 15f)`.
- `GroupChatB.Run` then routes (`RouteToGroupTalk`, 2-5 m) and calls `StartSync`, by which time the starter has gone.

This is the same EA inconsistency as BaseScare (docs/580 §14, `scarepriority`).

**Fix `groupchatpriority`** (`GroupChatPriority.cs`, tuning `kGroupChatPriority`, default off, counters 3841 raised /
3842 seen with the switch off / 3843 errors / 3844 spare):
- After EA's `PushGroupChatB` (all three hooked), if the linked `GroupChatB` is still queued, isn't the head, and is
  Autonomous, the fix calls `SetPriority(NonCriticalNPCBehavior)`.
- EA's priority-changed callback runs `RemoveLowerPriorityInteractions`, which cancels only an autonomous, non-MustRun
  head.
- The result is what `CreateSocialB` gives every other social receiver, so the household is allowed under the EA bug fix
  rule.
- A player-ordered chat (UserDirected) and a receiver busy with a player order keep EA's behaviour.

It shares the pure decisions `SpRaise` / `SpCancelsHead` with `scarepriority`.

Check `GroupChatPriorityCheck` (repro `groupchatpriority-check`):
- off: `verdict: BROKEN`; on: `verdict: FIXED`;
- controls: player-ordered head kept, MustRun head kept, already raised / player-directed / no longer queued / head
  receivers unchanged, unraised receiver keeps EA's wait.

Package `ts3fixes.r100-combo.package`. NOT RUN in game. The in-game check is an off/on run with seated chats: counter
3841 > 0 and no `SeatedSocialInteractionA ... Committed, timeout, 15.2` line with the switch on.
