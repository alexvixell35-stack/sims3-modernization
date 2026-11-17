# 577: autonomy re-picking taken objects, what makes Sims wait, pets/occult stuck states, privacy locks

§1 `autonomyinuseblock` and §4 `npcbusypartner` are built and proven in game (results at the end). `socialreplanlog` (§2, log only) is not in the PR that adds them.

Date: 2026-11-15. Static work (decompiled scripts, deterministic scans) plus the wait census of
`phase-20261115-1146` (all-on) vs `phase-20261115-0523` (all-off), Bridgeport, read with
`tools/harness/wait-report.ts` (cleanup branch). Built, not run in game.

| § | Switch | Slots | Check | Repro |
|---|---|---|---|---|
| 1 | `autonomyinuseblock` (off) | 2693-2695 | `Ts3Fixes.Fixes.AutonomyInUseBlockCheck` | `autonomyinuseblock-check` |
| 4 | `npcbusypartner` (off) | 2696-2698 | `Ts3Fixes.Fixes.NpcBusyPartnerCheck` | `npcbusypartner-check` |
| 2 | `socialreplanlog` (off, log only) | 2699-2700 | none (fix-repro exemption) | its `socialreplan:` lines in a soak |

Read-only diagnostic: `Ts3Fixes.Fixes.PrivacyCensus("list")` (§3).

## §1 Autonomy re-picks an object another Sim holds (`autonomyinuseblock`, AutonomyInUseBlock.cs)

[verified, decompiled] Autonomy's only failure memory is `GameObject.UpdateBlockTime`. It gives a 1 sim-minute hard
block in autonomous tests (`kBlockingTimeToTestOut`), then a score penalty that fades over 30 sim-minutes
(`kBlockingTimeToLowerScore`). `InteractionQueue.ProcessOneInteraction` calls it after a failed Run only when
`ShouldUpdateBlockTime` sees an `ExitReason.CausesBlocking` reason (`0x10687`: TimedOut, FailedToStart, RouteFailed,
ObjectInUse, CanceledByScript, FailedToRemoveFromInventory, NullValueFound).

EA's routing helpers `RouteTo*AndCheckInUse` add `ObjectInUse` when the object is taken on arrival
(`SimRoutingComponent` 2969-3067), so those failures are blocked.

`runfalse_scan.py` checked every `Run()` override for a `return false` guarded by a use test. It found 502 such exits; 441
of them sit in a Run that never adds an exit reason. Leaving out the `CheckInUse` helpers, 96 exits test
`Target.InUse` / `UseCount` / `ActorsUsingMe` themselves. Examples:
- `TrashcanOutside`, `Counter`, `VRGoggles`, `InsectJig` x3, `ScienceSkill` x2;
- `HitchingPost` (horses), `PetHouse`;
- `Kelp` / `MermadicKelp` (mermaids), `OccultFrankenstein` x3 (plumbots/simbots);
- `PetStone`, `HackySack`.

The interaction then ends with no exit reason, so the object is not marked. An NPC whose last autonomous action failed
re-runs autonomy at once (`Sim.CanRunAutonomyImmediately`: `!mLastInteractionSucceeded` returns `!IsSelectable`). It
scores the same object at full value and routes to it again: this is the failure spam.

**Fix:** a prefix on `InteractionQueue.DeQueue(bool)` with `succeeded == false`. It acts only on the main run path
(`mIsHeadInteractionActive`, set just before Run). The posture and test failure paths through
`OnValidInteractionFailed` stay with `autonomyfailblock` / EA. It acts when all of these hold:
- the head interaction is autonomous and not cancelled;
- its Sim has no exit reason;
- the target is a valid non-Sim object;
- a Sim other than the actor and its linked partner holds the object (carries it or is on its use list).

Then it calls EA's `UpdateBlockTime`, the same mark EA sets through `ObjectInUse`. Player-directed actions are untouched
(EA clears the block for them).

Household: this is an EA bug fix applied to every Sim, as `autonomyfailblock` is. The block is per object, which matches
EA's own `CheckInUse` behaviour.

Cost: a few field reads per failed DeQueue. The one reflected field read happens only after every cheap filter passes.

**Census for free:** counter 2694 counts the same case with the switch off. `result` lists the last 8 cases
(interaction @ object type). The next soak gives the real frequency per source with no extra logging.

Counters: 2693 blocked, 2694 seen with switch off, 2695 errors.

## §2 Wait census 2026-11-15: root causes ("what is still making Sims wait")

Per-run numbers come from `wait-report.ts` (1146 all-on | 0523 all-off).

**(a) Bar drink customers wait up to 21.6 sim-min (630 min x54 | 410 min x34). This is EA tuning, not a bug and not ours.**
[verified, decompiled] `BarAdvanced.BartenderInteraction.Run` runs these steps in order:
1. route to the customer and take the order (Committed, ~2.4 min);
2. `RouteToBar`;
3. `MakeDrink.RunMakeBehavior`: `DoTimedLoop(Bartending.GetTimeToMakeDrink)`, a Gaussian between 10 and 17 sim-min at
   skill 0 and between 5 and 12 at skill max (15 without the skill);
4. `RouteToCustomer`;
5. set Completed.

The customer's `OrderDrink` waits for Completed through all of it. One bartender serves orders one at a time
(`Target.mInteractions`). Per order the wait is the same with fixes on and off (11.7 vs 12.1 sim-min). The all-on run
simply had more orders (54 vs 34). No re-routing loop and no sync stall was seen.

**(b) and (c) Social sync timeouts while the partner is linked and running, and the household replan loop with the
partner as blocker. Not a ts3fixes regression.**

Measured, `phase-20261115-1146`:
- William: 14 `routefail: ... SocialInteractionB target Sim, Replan sync FailedBecauseNoPathToDestination blocker ... Sim`.
- Apollo: 13 of the same.
- Each route has a single route id, replanned about every 2 sim-min.
- The blocker is the same Sim object in both, the initiator HH Suzy. She stands 0.1-0.2 m from the receiver, and the
  receiver's goal is 0.4-0.5 m away.
- Suzy's `SocialInteractionA` waits at Routed. William's case ends in the 30-min `kSocialSyncGiveupTime` timeout
  (`partner step receive>`: the receiver is still inside `ReceiveSocialInteraction`'s `DoRoute`). Apollo's ends ok
  after 21 min.

Not all-on only: across 55 Bridgeport runs the same blocker pattern appears in runs whose only switch is `synctime`
(logging only): `1737` 56, `0039` 90, `1232` 26, `1034` 13. Runs without it are runs where the harness did not push a
social in that spot. A switch bisect cannot explain it: no switch is in every positive run and absent from every
negative run.

Cause [inferred, from the decompiled code]: the receiver's route is built without the initiator on its ignore list. This
is the group-position branch of `ReceiveSocialInteraction` (`route3` / `PlanToMatrix` to a `SocialPosition`), and the
`RouteSimsToSocial` turn-to-face route also qualifies. The initiator has already reached its spot, which covers the
receiver's start. Every replan fails (`NoPathToDestination`, start inside a Sim footprint) until the initiator gives up.

No fix yet, because the branch is not proven and a routing change for household Sims must be exact.

**`socialreplanlog`** (log only, off; SocialReplanLog.cs) collects the proof. It hooks nothing new per plan:
- `RouteFailLog.RflPlan`, which already runs after every plan, calls it for failed plans.
- The receiver-step hooks of the synctime log (`SimsNeedToRoute`, `SetupTwoPersonSocial`, `SetupGroupSocial`,
  `PlaceSocial`) moved into a guarded `PatchSwSteps()` in SyncWaitLog.cs. They are now installed in every package and
  feed the branch trail. Each costs one switch test while both logs are off.

For each failed Replan / ReplanFromPoint of a Sim whose running interaction is a `SocialInteraction`, whose role is
Receiver and whose blocker object is its own `SynchronizationTarget`, the first 3 replans of a receive each log a
`socialreplan:` line with:
- the result;
- the branch trail (`needroute<yes`, `twoperson<ok`, `place<ok`, `group<ok`, `jig`);
- the route's ignore list (`partner` / `self` / ids);
- both positions and the gap;
- the partner's route destination or "standing", with its sync level and role.

At the end of the receive a `socialreplan done:` line gives the count and ok / fail. The log is capped at 300 lines per
session. Counters: 2699 partner-blocked receiver replans, 2700 lines dropped by the cap.

**(d) DanceTogether / snowball waits for a busy partner (45 min).** Fixed for NPC pairs in §4.

Other rows already have owners:
- `ShowVideos` timeouts: `socialfinish`.
- `BeScared` / `Scare` failures: docs/546 §9.
- `MakeConversation` server waits: the customer's order flow, EA design (same per-wait time on and off).

## §3 Locked doors and privacy

[verified, decompiled] `PrivacySituation`s persist with the lot (`Situation` is `[Persistable]`; `PostLoad`
re-registers the room callbacks). The owning interaction (shower, toilet, bath, woohoo) ends one in its `Cleanup`.
`GenericPrivacySituation.OnParticipantDeleted` ends it when its Sim is deleted. `PostLoad` reads `SyncTarget.RoomId` with
no null check. If it throws after `Lot.AddPrivacySituation` and `PortalPathPlan +=`, `OnPortalPathPlan` then throws on
every portal plan of that lot.

Whether situations without an owner survive a load in practice is not known. `PrivacyCensus("list")` (read-only) prints
each live privacy situation with:
- its type, lot and room;
- its Sim's state (null / destroyed / off the lot / on the lot);
- the Sim's head interaction and `SituationExited`.

A row with no Sim on the lot, or an empty queue, is a room locked with nobody left to end it. A fix waits for the first
non-zero census.

## §4 NPC starter waits 45 min for a partner that never starts (`npcbusypartner`, NpcBusyPartner.cs)

[verified, decompiled] `InteractionInstance.StartSync` first waits up to `kNumMinToWaitOnPreSync` (15 sim-min) for the
partner to start the linked interaction it was given. When that time runs out, the loop just ends: there is no failure
branch. The starter then goes into `WaitForSynchronizationLevelWithSim(Started)` for `kNumMinToWaitOnSyncStart` (30
sim-min) more, although the partner has not begun. Census (`1146`):
- `DanceTogetherA` timeouts x2 (90 min);
- `SocialInteractionA` "Started timeout 45.1" with partners in `SnowWaterFightThrow`, `VisitCommunityLot` and
  `FollowerMeetUpAtPoint`, all "linked queued";
- all of them NPC pairs.

**Fix:** a call at the top of the shared wait hook (`SocialReceiveFail.cs`; the method is hooked once). It ends the wait
at once through EA's own failure call `OnWaitForSynchronizationLevelWithSimFailed`, the same end the 30-minute timeout
reaches, when all of these hold:
- the wait is for Started;
- both Sims are NPCs (`IsNpcSim`; a Sim of unknown scope counts as household);
- the waiter's linked interaction is still only queued on the partner: in its queue, but not its head and not its
  current or transition interaction.

Inside StartSync that state means the pre-sync already ran out. Household Sims, and NPCs waiting on a household Sim, are
unchanged.

Counters: 2696 ended, 2697 seen with switch off, 2698 errors.

## §5 Pets, horses and occults: stuck-state pass (RE, scans)

Scans:
- `waitloop_scan.py`: 235 unbounded sleep loops in total, 16 in pet/horse/occult code.
- `stateflag.py`: flags set in Run with no reset in Cleanup, 93 in total.
- `runfalse_scan.py` (§1).

| Lead | Finding | Verdict |
|---|---|---|
| Pet bowls, beds, houses, litter, hygienator, cat toys, terrariums | Use `RouteTo*AndCheckInUse` (ObjectInUse set: EA blocks) | no bug |
| `HitchingPost` tie-up, `PetHouse` protect | Direct `InUse` / `ActorsUsingMe` test then `return false` | covered by §1 |
| `PetBed` snuggle receiver wait (`while linked IsSleeping`) | The receiver's own `FinishLinkedInteraction` cancels the master, which clears sleeping | no bug |
| `BoxStall.Clean` `mIsBeingCleaned` | Set and reset in try/finally | no bug |
| Plumbot `BotChargingStation.Recharge.Cleanup` sets `Decay = mDecayToRestore` (0 when Run never started) | `Motive.UpdateMotive` recomputes `Decay` on every update | cosmetic, no fix |
| Werewolf `PracticeFight` jig | Destroyed on every early return and in Cleanup | no bug |
| `FairyHouse` waits on pause / CAS | UI-mode waits for the selectable Sim only | no bug |
| Mermaid `Kelp` / `MermadicKelp`, `OccultFrankenstein` repairs | In-use `return false` | covered by §1 |
| Vampire drink, ghost resurrection, genie lamp | Nothing stuck found in this pass (genie: `geniebanish` exists) | parked: need census rows |

## Loop pass

- §1 hook: one O(use list) walk, only after the filters pass.
- §4: O(queue) once per Started wait.
- No new per-tick work, retries or loops.

## Tests

- **Pure checks:** `autonomyinuseblock-check` and `npcbusypartner-check`: off BROKEN, on FIXED, controls ok, members
  found.
- **Soak:** with the switch off, counters 2694 and 2697 give the frequency, and `AutonomyInUseBlockCheck result` names
  the sources. With it on:
  - 2693 rises;
  - wait census: no NPC "Started timeout 45" rows with "linked queued";
  - household rows unchanged.
- `PrivacyCensus list` after a load and each in-game hour.

**(b)/(c) proof run:** turn `socialreplanlog` on in a household social soak. Each receiver replan whose blocker is its
`SynchronizationTarget` logs:
- which `ReceiveSocialInteraction` branch built the route (jig / two-person / `PlaceSocial` / group position);
- the receiver's route ignore list;
- both Sims' positions;
- the initiator's route destination.

## In-game result (Bridgeport, two combined soaks, each switch off then on: phase-20261115-1321 and phase-20261116-0234)

| Switch | Off | On |
|---|---|---|
| `autonomyinuseblock` | BROKEN | FIXED |
| `npcbusypartner` | BROKEN | FIXED |

- [verified] The verdicts, controls ok and members found, in both runs (the runs' `smallfix.txt`).
- [inferred] How often each case happens in normal play: the checks run the same decision functions as the hooks.
