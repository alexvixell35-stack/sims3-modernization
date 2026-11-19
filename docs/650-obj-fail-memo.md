# 650. `objfailmemo`: NPC (Sim, object) route-failure memo for autonomy; social partners that never link

2026-11-19. Source: OFF / sweep runs `phase-2026111*` (`ts3native*.log`, `routefail job:` and `routefail:` lines,
`syncwait` / `socialreceivefail` lines), decompiled Sims3GameplaySystems. §1 built, default off, NOT RUN. Managed only
(ts3fixes), package tag `objfail`. §2 is an investigation; §3 `socialunlinkend` was built from it (default off, NOT RUN, tag `socialunlink`).

## 1. `objfailmemo` (slots 4120-4129, `kCounterSlots` 4130)

### Problem

NPC autonomy keeps scoring an object the NPC cannot route to. Nothing remembers the failure per Sim: EA's only memory is
`GameObject.UpdateBlockTime` (one `mLastBlocked` tick per object, a 1 sim-minute block and then a score penalty that fades
over 30 sim-minutes) [verified, decompiled], and it is per object, so it would steer household Sims as well. The NPC
re-picks the same object on its next autonomy pass. `poolfailmemo` (docs/645 §2) fixed this for GetInPool only.

Evidence, `routefail job:` lines (`<interaction> target <type>, ... result N`), all phase-2026111* logs [measured]:

| interaction > target | result | lines | handled by |
|---|---|---|---|
| ServingContainerGroup_GrabAServing > Plate (Isla Paradiso) | 16 NoPath | 1459 (+18 NoDestination, 5 PortalLocked) | **objfailmemo** |
| DisposeOutside > TrashPile, EmptyTrash > TrashcanIndoorWithLid | 16 | 1983 / 1025 | trashoutsidefallback (docs/646 §2); objfailmemo skips both interactions |
| GetMailEx > MailboxWall, AutonomouslyShopInRabbitHole > Bookstore, restaurants | 16 | 935 / 1286 / 1613 | objfailmemo |
| GetInPool > Pool (Bridgeport) | 10 | 1402 | poolfailmemo (left unchanged; objfailmemo skips GetInPool) |
| MopPuddle > Terrain | 10 / 18 / 16 | 668 / 531 / 73 | mopunreachable (Terrain is never recorded by objfailmemo) |
| GoHome / VisitLot / GoToLot > Lot | 16 / 10 / 18 | tens of thousands | trip shortcuts; Lot is never recorded |
| SocialInteractionA/B > Sim | 16 | 711 / 1119 | §2; Sim targets are never recorded |

Starlight Shores DollHouse Play (238 fails) is done by household children: exempt by rule, untouched.

### Fix

- **Record** (`OfmPlan`, called from `RouteFailLog.RflPlan` for each failed plan of a Sim follower, after the
  `routefailprobe` filter so spot probes do not count): result NoPath (16), NoDestinationSpecified (10) or
  DestinationPortalLocked (18), and the Sim's current interaction targets a plain object (not a Sim, Terrain or Lot; not
  GetInPool, DisposeOutside or EmptyTrash). The pair (Sim, object) counts one failure per interaction instance: EA replans one attempt several times,
  and those plans count once [inferred from the log pattern: several plans per route id].
- **Block**: `kObjFailMemoFails` (default 2) failed attempts within `kObjFailMemoWindowHours` (2) sim-hours from the
  first counted one block the pair for `kObjFailMemoHours` (1) sim-hours. A failure during a block does not extend it;
  after it the pair starts from zero. A clock that went back (another save) drops the entry. Table capped at 512 pairs.
- **Refuse**: the hook point is the autonomy candidate step, `Autonomy.CalculateScoreAndAddToCandidates(iop)` [verified,
  decompiled: every scoring path (objects, meta objects, situations, local autonomy, social) adds a candidate through it].
  That method is already patched by `AutRoomMemo.cs` (`ArmPairHook`, always installed), so objfailmemo rides on that hook
  instead of a second patch: before EA scores the pair, a blocked pair is left out when `Autonomy.CurrentSearchType` is
  `Autonomy`. Pie menus (`PieMenuChoices`), posture transitions, buff solves, social searches and pushed interactions do
  not run this search, so user-directed and pushed actions are never refused. With nothing blocked the hook costs one int
  read. This is not `autonomyinuseblock`'s mechanism (EA's per-object `UpdateBlockTime`), which would reach household
  Sims too; poolfailmemo's per-interaction Test hook is not reused either (one hook for every object is the point).
- Household, selected and unknown-scope Sims (`HouseholdExempt || IsPlayerSim`) are never recorded and never refused.
- Switch off: the memo is dropped at the next failed plan or blocked lookup.

Log, capped at 10 a minute: `objfailmemo: <Sim> <interaction> on <object type> failed N times (<result>), skipped for H sim-h`.

| slot | meaning |
|---|---|
| 4120 | NPC failures counted (one per attempt) |
| 4121 | pairs blocked (K reached) |
| 4122 | autonomous candidates refused |
| 4123 | exempt (household, selected, unknown scope) |
| 4124 | errors (caught; EA's code ran) |
| 4125 | failures left to other switches (Sim / Terrain / Lot target, GetInPool, DisposeOutside, EmptyTrash) |
| 4126-4129 | spare |

Agent `patches` read-back: `objfailmemo_counted`, `_blocked`, `_refused`, `_exempt`, `_errors`, `_left_to_others`.
Self-check: `node tools/selftest/objfailmemo/run.ts` (rule in `ObjFailMemoCore.cs`). Not yet in `CheckLiveMap` (the switch
has no Apply section of its own; its live path is `ArmPairHook`, listed under `autroommemo`).

Test plan: Isla Paradiso OFF/ON pair, `objfailmemo` on; expect `routefail job:` GrabAServing > Plate lines to drop from
~1459 towards K per (NPC, plate) per block, 4121 > 0, 4122 > 0, no new stuck NPCs (hungry NPCs pick other food).

## 2. Social partners that never link (investigation, no code)

Logs [measured]: `syncwait ... failed` A-side `in SocialInteractionA, partner in SocialInteractionB (linked running)`
with `partner step receive<fail`, and B-side `in SocialInteractionB, partner in none (no link)` with `partner sync
Completed/None on none` (3317 of 3360 B-side lines). `socialreceivefail: ... ended at once (partner's receive failed)`:
846 lines (switch on).

EA code [verified, decompiled]:
- `SocialInteraction.PushSocialB` queues B and sets `A.LinkedInteractionInstance = B` (the setter links both ways).
- B-side: `SocialInteractionB.Run` → `ReceiveSocialInteraction` sets its Sim Started/Receiver, then waits in
  `Sim.WaitForSynchronizationLevelWithSim(A, Routed, kSocialSyncGiveupTime 30)`. `IsAtSynchronizationLevelWith` needs
  `A.SynchronizationTarget == B` and an Initiator role.
- A ends early with `return false` in several places after B was pushed (`SocialInteractionA.Run`: VerifyPositionAndAlignment
  fails, `Target.CurrentInteraction is not SocialInteractionB`, the skittish-pet startle; BeginSocialInteraction's route
  failures) without `CopyExitReasonToLinkedInteraction`. A's dequeue (`InteractionQueue.CleanupInteraction`) unlinks
  both and calls `CancelInteraction(B, false)`; `ClearSynchronizationData` sets A to Completed/None/target null. For a
  running B, `CancelNthInteraction` adds an exit reason only when the head is locked or in transition; otherwise it
  marks B Cancelled and removes it from the list, and B's script keeps waiting [inferred: no exit reason reaches the
  wait loop]. The wait can no longer succeed (A's target is null); it ends only by the giveup, an unrelated exit reason
  or a failed `CanSocializeWithSyncCheck` (measured waits: 1570 at 0 min, 816 < 1, 709 1-5, 265 5-29 sim-min).
- A-side: B's `ReceiveSocialInteraction` fails (A not its target's current interaction, jig route fails; SocialInteractionB
  has 1119 NoPath routefails), B adds RouteFailed and sits in `WaitForMasterInteractionToFinish` at Started; A waits
  for Routed. This is exactly `socialreceivefail` (docs/546 §9); the 846 "ended at once" lines are that fix firing.

Coverage: `socialreceivefail` covers the A-side case. `socialfinish` covers only the six masters that skip the Finished
signal in `WaitForSyncComplete`. Neither covers the B-side "no link" wait.

Proposed fix (not built), `socialunlinkend`: in the wait hook `SocialReceiveFail.cs` already owns, when the waiting Sim
is the Receiver, its current interaction is a `SocialInteractionB` whose `LinkedInteractionInstance` is null or not
running on the partner, and the partner's `SynchronizationTarget` is not this Sim, end the wait at once through
`OnWaitForSynchronizationLevelWithSimFailed` + false (the giveup's own result, sooner). EA bug fix with an unchanged
outcome, so every Sim, like socialreceivefail; counters from a new reservation. Root-cause alternative (bigger): copy
A's exit reason to B on A's early-return paths. Built as §3 (scope narrowed to non-exempt NPC receivers and a null link).

## 3. `socialunlinkend` (slots 4140-4149, `kCounterSlots` 4150)

Built 2026-11-19, default off (`kSocialUnlinkEnd` false), NOT RUN. Managed only (ts3fixes), package tag `socialunlink`.
The B-side case of §2.

### Problem

Logs [measured, B-side `syncwait` fail lines `in SocialInteractionB, partner in none (no link)`, phase-2026111*, 3409
lines]: 1464 Routed waits (142 at 0 min, 348 < 1, 709 1-5, 265 5-29 sim-min) and 1945 Committed waits (1477 at 0 min,
468 < 1). Most Committed ones follow a failed Routed wait at 0.0 min (SynchronizationFailed already set).

EA code [verified, decompiled; settles the §2 inference]:
- `InteractionQueue.CleanupInteraction(A)` sets `A.LinkedInteractionInstance = null` (clearing B's side too) and calls
  `CancelInteraction(B, false)` (reason CanceledByScript). `CancelNthInteraction` adds that reason to the actor only when
  B is running **and** the head is locked or a transition is set; otherwise `RemoveInteraction` takes B off the list and
  runs `CleanupInteraction(B)` → `B.Cleanup()` (slave interaction, social jig destroyed, listeners removed) while B's
  script is still inside `ReceiveSocialInteraction`. No exit reason reaches the wait.
- `IsAtSynchronizationLevelWith(A, Routed/Committed)` needs `A.SynchronizationTarget` = the waiting Sim and an Initiator
  role; A's `ClearSynchronizationData` left Completed/None/null, so the wait cannot succeed. It ends by the 30 sim-min
  giveup, an exit reason or a failed `CanSocializeWithSyncCheck` (hence the spread of measured times).

### Fix

In the wait hook `SocialReceiveFail.cs` owns (`Sim.WaitForSynchronizationLevelWithSim`, 7 args; the 3-arg overload that
`ReceiveSocialInteraction` calls forwards to it), right after the socialreceivefail check: `SueCannotSync`
(`SocialUnlinkEnd.cs`, rule `SocialUnlinkEndCore.Decide`). The wait fails at once when all hold:
- desired level Routed or Committed (Started / Completed waits are left alone);
- the waiting Sim is the Receiver, its `SynchronizationTarget` is the wait's target, and its running interaction
  (`InteractionQueue.RunningInteraction`, since a removed B is off the list; else the head) is a `SocialInteractionB`
  whose `Target` is that partner;
- that B's `LinkedInteractionInstance` is null;
- the partner's `SynchronizationTarget` is not the waiting Sim, and neither its current nor its transition interaction
  targets it.

The failure is the vanilla one: `SynchronizationSleeping` set as on entry, `OnWaitForSynchronizationLevelWithSimFailed`
(adds SynchronizationFailed to the receiver only, since the partner's target is not it), return false.

B-side cleanup after that is EA's normal failure path, the same as after the giveup [verified, decompiled]:
`ReceiveSocialInteraction` goes on to its Committed wait, which stops at the first check on SynchronizationFailed (part of
`ExitReason.Default`) → CanceledByExitReason, false. `SocialInteractionB.Run` adds RouteFailed; `CopyExitReasonToLinkedInteraction`,
`WaitForMasterInteractionToFinish` (no sync target, no PostLoop) and `WaitForSyncComplete` are no-ops without a link.
Posture: `EnterSocializingPosture` runs only on success, so none was entered. Animation state machine:
`ReceiveSocialInteraction` and the setup helpers it calls create none. Social jig: destroyed by `SocialInteraction.Cleanup`
when B was removed (or at B's normal end).

Scope: non-exempt NPC receivers only; `HouseholdExempt || IsPlayerSim` receivers (household, selected, unknown scope) are
counted and left to EA. The partner is never touched.

Possible difference [inferred, not seen]: within the 30 minutes the old partner could start a new social aimed at this Sim
and take the Initiator role with it as target; the vanilla wait could then succeed on the stale B. The fix ends the stale
wait first and leaves the new social to its own B.

Log, capped at 10 a minute: `socialunlinkend: <Sim> -> <partner>, <level>, partner in <interaction> (no link): wait ended at once`.

| slot | meaning |
|---|---|
| 4140 | receiver waits ended at once |
| 4141 | exempt receivers left to EA (household, selected, unknown scope) |
| 4142 | errors (caught; EA's wait ran) |
| 4143-4149 | spare |

Agent `patches` switch `socialunlinkend`; read-back `socialunlinkend_ended`, `_exempt`, `_errors`. Tuning
`kSocialUnlinkEnd` (TUNING_XML False). Self-check: `node tools/selftest/socialunlinkend/run.ts`.

Test plan: Isla Paradiso and Twinbrook OFF/ON pair with `synctime` on; expect B-side `(no link)` Routed fail lines with
minutes > 0 to drop to ~0 for NPC receivers, 4140 > 0, household receivers unchanged, no new stuck Sims or exceptions.
