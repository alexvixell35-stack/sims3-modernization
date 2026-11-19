# 650. `objfailmemo`: NPC (Sim, object) route-failure memo for autonomy; social partners that never link

2026-11-19. Source: OFF / sweep runs `phase-2026111*` (`ts3native*.log`, `routefail job:` and `routefail:` lines,
`syncwait` / `socialreceivefail` lines), decompiled Sims3GameplaySystems. §1 built, default off, NOT RUN. Managed only
(ts3fixes), package tag `objfail`. §2 is an investigation only (no code).

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
A's exit reason to B on A's early-return paths.
