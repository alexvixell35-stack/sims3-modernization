# 563: Life-events logic batch (weddings, ghosts, story move-in)

2026-11-15, static (decompiled EA scripts + semgrep `tools/static/ea-gameplay-logic.semgrep.yml` /
`ea-script-logic.semgrep.yml` over 83 aging, birthday, death, ghost, moving, wedding and pet files, then reads of the
Run / Init / CleanUp paths). Built, NOT RUN. Every switch is off by default and is an exact EA bug fix.

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `weddingcancel` | 2505-2507 | `Ts3Fixes.Fixes.WeddingCancelCheck` | `wedding-cancel` |
| 2 | `ghostspawnargs` | 2508-2509 | `Ts3Fixes.Fixes.GhostSpawnArgsCheck` | `ghost-spawn-args` |
| 3 | `moveinpartner` | 2510-2512 | `Ts3Fixes.Fixes.MoveInPartnerCheck` | `movein-partner-cap` |
| 4 | `reaperflow` | 2513-2515 | `Ts3Fixes.Fixes.ReaperFlowCheck` | `reaper-flow` |
| 5 | `moveinpartner` (teens) | 2516 | `Ts3Fixes.Fixes.MoveInPartnerCheck` | `movein-partner-cap` |

## 1. Wedding ceremony stuck after the rings-impatience cancel (`weddingcancel`, WeddingCancel.cs)

Verified in the decompiled `WeddingCeremony`. Every wedding outside a wedding party (the Get Married social, the arch,
resort weddings) runs this root situation. `ExchangeRings` arms a 1-hour alarm; when the rings are not exchanged (the
forced Exchange Rings failed or was cancelled), `ImpatienceAtNoRingsExchanged` sets `WeddingCanceled(parent, null)`.

- (a) `WeddingCanceled.Init` returns at once when the jilter is null, before arming its end alarm
  (`sWaitTimeAfterWeddingCanceled` -> `EndOfCeremony` -> `Exit`). Nothing else ends the ceremony unless a wedding party
  ends, or a ring Sim dies or is deleted. The couple keep the ceremony role, and the stale ceremony is what
  `GetSituationOfType<WeddingCeremony>` returns first. On the next arch wedding, `WeddingArch.RouteToWeddingArchSlot` sees
  the stale ceremony's child is not `RouteToWeddingArch`, fails, and the new ceremony exits: **the couple can no longer
  marry under an arch.**
  Fix: after EA's Init, with no jilter and no alarm, the same end alarm is armed (EA's tunable, EA's callback, stored in
  `mAlarmHandle` so `CleanUp` removes it). Counter 2505.
- (b) With a jilter, Init means to give each guest who likes the jilted Sim an Insulting STC toward the jilter. Instead it
  calls `Relationship.Get(jilter, guest, true)`, drops the result, and sets the STC on the jilter/jilted relationship again.
  Fix: after EA's Init, the STC is set on the jilter/guest relationship for the same guests (valid audience member,
  positive LTR with the jilted Sim), with EA's commodity and value (Insulting, 150). Counter 2506; errors 2507.

## 2. Urnstone.GhostSpawn drops its lot and isAngry (`ghostspawnargs`, GhostSpawnArgs.cs)

Verified in the decompiled `Urnstone`. `GhostSpawn(creatingForBurialGround, position, lot, isAngry)` (the `IUrnstone`
overload) forwards to the 5-argument `GhostSpawn` with `LotCurrent` and `isAngry: false`, so it drops both of its own
arguments.

Callers that pass them:
- `GhostHunter.GhostHunterJob.CreateAngryGhost`: `isAngry: true` and the job lot; its urn is created out of the world.
- `SpellBook` and `OccultGenie`: the caster's lot.

For Ghost Hunter angry-ghost jobs this means:
- the spawn is refused once `kGhostMaxGhostsInWorld` non-playable ghosts exist, so the job's ghost is never made;
- the ghost gets the plain react-to-ghost broadcaster instead of the job's panic one (`OnPanicStart`);
- it greets the urn's lot instead of the job lot;
- it gets a return-to-grave alarm, which then skips it because it has `BeAngryGhost`.

Fix: the overload passes its own lot (the urn's `LotCurrent` when null) and `isAngry`, with the same duration EA picks.
Counters: 2508 angry spawns, 2509 lot-only forwards.

## 3. Story move-in with partner over the household cap (`moveinpartner`, MoveInPartner.cs)

Verified in the decompiled `StoryProgression.MoveInWithPartner`. NPC households only: the action skips the active and
service households.

`NotLivingWithPartner` accepts a couple when either direction works. But the constructor picks the mover by "can leave"
alone, without the room test. `IsValid` passes on the other branch, so `Execute` adds the Sim to a household with no room
(`CanAddSimDescriptionToHousehold`: 8 Sims, 6 pets, 10 in all).

Fix: before EA's Execute, when the partner's household has no room, the partner moves instead if that direction is valid.
Otherwise the move is skipped. Counters: 2510 swapped, 2511 skipped, 2512 errors.

## 4. Grim Reaper stuck after a failed soda / chess with no grave (`reaperflow`, ReaperFlow.cs)

Verified in the decompiled `GrimReaperSituation`.

- (a) `UseVendingMachine.Init` forces the reaper's soda at a deadly vending machine with `callbackOnFailure: null`. It is
  the only state with no failure callback. When the soda fails (machine unroutable or in use, push refused), the situation
  stays in that state for good: the reaper stands on the lot, never reaps, socializes or leaves, and the machine keeps
  `KilledASim`. Fix: the same push, with EA's own completion step (`PostVendingMachine`: clear `KilledASim`, then
  `CheckIfEveryoneIsReaped`) as the failure callback too. Both callbacks are EA methods, with no closure, because the
  callbacks are saved with the interaction. Counter 2513 (pushes given the failure path).
- (b) `ChessChallenge.Definition.Test` checks only `ChessOpponent == null` and a free chess table, not `LastGraveCreated`.
  After a match, `ClearChessData` sets it to null (as `ClearHarassData` does after a pet save), while the reaper may stay
  to socialize. The pie menu's `GetInteractionName` then dereferences the null grave (script error), and a challenge that
  runs ends in `PostChessMatch`, which spawns the null grave's ghost. Fix: the challenge is not offered when there is no
  grave or the grave has no dead Sim. Counter 2514; errors 2515.

## 5. Story move-in takes teens out of their family (`moveinpartner`, teen part)

Confirmed. "Partner" is the going-steady LTR. EA's socializing data (copy in NRaas source, `OriginalSocializing.xml`)
sets `SetsPartner` 1 for Partner, Fiancee and Spouse, and the Prom "seal the deal" calls `SetPartner`. Neither
`NotLivingWithPartner` nor `StoryAction.SelectRandomSimDescription` filters by age, and `CanRemoveSimFromHousehold` passes
for a teen whose parents stay home. So the action moves an NPC teen into their steady's family, or takes the steady in.
The player's own Ask to Move In needs a young adult. Fix: the move is skipped when either Sim is a teen or younger.
Counter 2516.

## Checked, not fixed (no bug or not worth a switch)

- AgeUp: `mCheerBroadcaster` is disposed in Cleanup. BirthdayParty: `kSimAgeTransition` carries the CreatedSim, so the
  party outcome works. The NPC age-up situation's child `Exit()` exits the whole chain (Situation.Exit walks to the root).
- WeddingCeremony `PostLoad` listener re-add: non-persisted listeners, re-registered once (semgrep listener-overwrite
  false positive).
- Grim Reaper `PostPetSave`: `Get(createIfNone: false)` is safe, because the pet savior needs a BFF relationship.
- Stray pets: `sLastActiveHouseholdVisit` is written by `StrayGoToLot`.

## Leftovers (recorded, not fixed)

- `AgingState.AgeTransitionCompleted` aging down an uninstantiated Sim would dereference `createdSim.IsPet`. Not
  reachable: its only age-down caller is the `AgeDown` interaction, whose Actor is instantiated. No fix.
- `AgeUp` for a ghost toddler becoming a child with no grave: `FindGhostsGrave(Actor).GhostSetup` throws mid-Run, after
  the age is already set. Not cheap: the null is inside EA's Run, and a correct fix needs a grave to re-apply the ghost
  state from. Needs a playable ghost whose urn was deleted; CAS and death ghosts get one. No fix.
- Pets: adoption / stray / aging paths read clean for this pass; pet routing is another agent's.
