# 582: broad RE batch 3 (Generations, Pets, Island Paradise, Supernatural, Seasons, Late Night, services)

2026-11-15. Static only. The EA code was read in `game-copy/decompiled`, which stays local. Built, not yet run in game (in-game off/on test pending).

**How the leads were found.**
- Four read-only lead passes, one per area group:
  - Generations and University.
  - Pets and Island Paradise.
  - Supernatural, Seasons and Into the Future.
  - Base-game services, skills and Late Night.
- One `log-issues.ts` pass over 160 logs and 11 ErrorTrap reports. Its uncovered growth rows led to §1. Two other growth rows were ruled out:
  - School alarms show no duplicates in `alarms-8.json` (the growth is population).
  - `IncreasedEffectiveness` is per-Sim learning.
- Every fix below was re-checked against EA's code, and overlap was checked with `docs/data/swap-stack-audit.csv` and the 56x/57x docs. No EA method is hooked twice.

**Checks.** Each fix has a pure check, `Ts3Fixes.Fixes.<Name>Check("run"|"result")`, kept in `Batch582Checks.cs`.
- A check prints `controls ok=` and `members found=`.
- It gives `verdict: BROKEN` with the switch off, `verdict: FIXED` with it on, and `UNEXPECTED` otherwise.
- The repros are in `tools/harness/repros.json`, under the ids `<switch>-check`.

**Rules.**
- Every switch is off by default and sits on the `proven-fixes.txt` pending line.
- Every fix acts where the bad state is created. A load sweep exists only where the bad state is saved, and the section says so.
- No fix moves a Sim.
- Household-visible changes are flagged below, and each restores EA's own intent.

| § | Switch | Slots | Scope | Status |
|---|---|---|---|---|
| 1 | `vehiclefxhelper` | 1118-1120 | any vehicle (cosmetic, leak) | verified |
| 2 | `prankditchempty` | 1121-1123 | prankster (household or NPC) | verified |
| 3 | `serviceassignexit` | 1124-1127 | service bookkeeping; **household-visible**: the preferred service Sim can return | verified, + load sweep |
| 4 | `serviceshowalarm` | 1128-1130 | NPC service Sims | verified |
| 5 | ~~`ridingjumpwins`~~ | (released) | removed: simler90 core fixes the same line | [simler90 fixes it] |
| 6 | `maildogchaser` | 1134-1136 | **household-visible**: the dog no longer chases at every delivery | verified |
| 7 | `lifeguardsurvey` | 1137-1139 | household lifeguard's leftover alarm | verified, + load sweep |
| 8 | `moonriselistener` | 1140-1142 | mostly NPC fairies and werewolves (leak) | leak verified, gameplay effect inferred |
| 9 | `holidaylights` | 1143-1146 | world (NPC homes), alarm leak | verified, + load sweep |

## §1 vehiclefxhelper: orphaned tire and hover effect helpers

**The bug [verified].**
- `Vehicle.OnRouteStarted` runs `CreateTireEffectHelper` and `CreateHoverEffectHelper`. The script adaptors call it for every followed route.
- Both methods assign a new helper over `mTireEffectHelper` / `mHoverEffectHelper` without destroying the one held.
- Only `PostScriptAdaptorRoute`, `DoReset` and `Dispose` destroy the held helper.
- So a second route start before PostRoute orphans the old helper. Its kSnowLevelChanged and kRoadWetnessChanged listeners have no owner, so they are never removed, and they keep the vehicle reachable after it is disposed. Its effects never stop.

**Measured.** `leak.events.by_delegate.TireEffectHelper.UpdateEffects` grows from 0 to 10 per run, in 34 runs.

**The fix.** EA's own `Destroy*EffectHelper` runs first when a helper is held.

## §2 prankditchempty: doorbell ditch throws when nobody is home

**The bug [verified].**
- `Door.PrankDoorbellDitch.Run` builds the list of household Sims with `IsAtHome` after its route and its ring, then calls `RandomUtil.GetRandomObjectFromList`.
- That call throws on an empty list. The resident test ran before the trip, so if everyone has left meanwhile, the prankster is reset.
- The sibling pranks (FlamingBag, ThrowingEggs) test the count first.

**The fix.**
- Only that exception is caught (message "The list is empty.", lot household with nobody at home). Run then ends as EA's null-pick branch does: `AnimateSim("Exit")`, return true.
- Anything else is rethrown.

## §3 serviceassignexit: "Ask to Hang Out" leaves a dead, saved service assignment

**The bug [verified].**
- `ServiceSituation.SetToSocializingState` sets `IsSocializing` and ends the service.
- `Service.OnAssignmentFinished` skips entries whose situation `IsSocializing`. So the Sim stays in `mSituationsAssignedToSims`, `mCreatedSims` and `mSimsAssignedToLots`, pointing at an exited situation. Nothing else removes it, and `Service` is saved.
- Results:
  - `IsSimAssignedTask` stays true, so that Sim is never sent again. A household's preferred maid never returns.
  - New pool NPCs are made instead.
  - `NumAssignedSims` stays inflated.

**The fix.**
- After EA's method, the entries naming the exiting situation are removed. No DestroyMyself push is made: EA's skip stays.
- Reassigning a Sim while they hang out is impossible, because `FindSimForAssignment` requires `CreatedSim == null`.
- Load sweep (saved state): entries whose situation has exited are removed. Live situations are untouched.

## §4 serviceshowalarm: route retries stack make-visible alarms

**The bug [verified].**
- `RouteToLot.OnRouteFailed` retries (up to 3 times) by calling `Init` again, which calls `OnServiceStarting` again.
- `OnServiceStarting` re-adds the one-shot "Make Service Sim Visible" alarm over `mAlarmShowSim` without removing the pending one.
- So the Sim can be shown up to 4 times. Each time it runs `DecrementAutonomyDisabled` (which eats disable counts other systems hold), `EnableSocialsOnSim` and `FadeIn`.

**The fix.**
- While the held alarm is still pending, EA's re-arm is skipped, so the Sim is shown once, at the first alarm's time.
- A fired or missing alarm runs EA's code.
- `BurglarSituation`'s override is untouched.

## §5 ridingjumpwins: removed (simler90 fixes it)

The bug is real (EA's `RidingSkill.ImportContent` reads key 2999955368 into `mCrossCountryCompetitionsWon`), but simler90's Gameplay Core Mod UPDATE242, which our installer ships (docs/07 §2.1), already reads it into `mJumpCompetitionsWon`. The switch, its hook and slots 1131-1133 were removed so we do not stack a second swap on simler90's replacement.

## §6 maildogchaser: one dog chases the mail carrier forever

**The bug [verified].**
- `Mailbox.PutMail.OnEnterDogChaseBroadcaster` rolls once per delivery and stores the dog in the single mail service's `DogChaser`.
- Nothing ever clears it; the getter only hides a destroyed dog.
- So after the first chase, every delivery on any lot runs DogBark ("Growl At" forced on that dog) and DogChase.

**The fix.**
- When `MailCarrierSituation.DogChase.Finished` runs, the service's chaser is cleared if it is still this chase's dog.
- A dog set meanwhile by another lot's delivery is kept.

## §7 lifeguardsurvey: a finished beach survey keeps a throwing, saved alarm

**The bug [verified].**
- `Lifeguard.BeachSurvey` adds the repeating, AlwaysPersisted alarm "Lifeguard - Check Beach Events". Only `BeachSurvey.Cleanup` removes it.
- When the last task is done, `Job.OnCompletion` calls `ShutDown`, which also drops the timeout. So the alarm repeats for good.
- Each fire:
  - finds `mPendingTasks` empty;
  - re-adds tasks to `mTasks`;
  - restarts the music and the camera callback that ShutDown removed;
  - throws "The list is empty".
- That the shifts often finish every task early is inferred from the tuning.

**The fix.**
- After EA's `OnCompletion` of a BeachSurvey, EA's own `RemoveAlarm` runs.
- Load sweep (saved state): `CheckLifeguardEvents` alarms whose job is no longer its occupation's `CurrentJob` are removed.

## §8 moonriselistener: fairy and werewolf moonrise listeners pile up

**The bug [leak verified; gameplay effect inferred].**
- `OccultFairy.SetupSimHandlers` and `OccultWerewolf.SetupSimHandlers` add an ownerless kMoonRise listener into `mEventLunarCycleEvent` without removing the held one.
- They run at every instantiation. Only `OnRemoval` (losing the occult) removes the listener.
- Every load of a fairy or werewolf adds one more listener. Between loads, the werewolf handler adds alarms to the destroyed Sim.

**The fix.** The held listener is removed (EA's `RemoveListener`) before EA adds the next one.

## §9 holidaylights: no lights in small worlds and an eternal trickle alarm

**The bug [verified].**
- **Zero pulse:** `ApplyLightsToWorld` sets `sNumLightsToApplyPerPulse = (int)(picked/12 + 0.5)`, which is 0 for 1-5 picked lots. No light is ever placed, and the hourly AlwaysPersisted alarm never ends.
- **Overwritten handle:** a re-run overwrites `sTurnOnLightsAlarm` without removing the alarm it held.
- **Orphan after load:** an alarm overwritten by (b) is saved too, and nothing can remove it after the load. (Corrected 2026-11-15: `[PersistableStatic(false)]` means "not main-menu-only", so the statics ARE saved with the alarm and a saved trickle resumes. The first build's claim that they are not saved was wrong.)

**The fix.**
- Before EA's call, a held trickle alarm is removed, but only when EA rebuilds the lists (`sLotsWithLights` null).
- After it, a 0 pulse with lots waiting becomes 1. (The first build also made the alarm NeverPersisted. That left a saved handle with no alarm after a load and stranded the trickle, so it is removed.)
- Load sweep (saved state): trickle alarms other than the held handle are removed.

## Leads not built

These are reported for the user. Each would change what household Sims see beyond restoring a plain intent, or needs a larger rewrite.

**Generations and University**
- `Door.PrankDoorbellDitch` can pick a baby or toddler as prankee. A ChildOrAbove filter would change who reacts.
- `Door.PrankReaction.Run` adds Finished or CanceledByScript to the prankster without checking that they are still pranking. A late prankee therefore ends the prankster's current, unrelated interaction. The fix needs `PrankReactionThrowingEggs`' use of `Prankster` kept.
- `Sim.Streak.StreakLoop`:
  - It tests `kTimeToDance` (30) where it means `kTimeToStreak` (120).
  - `ClearExitReasons()` after a dance drops a player's cancel.
- `Door.PrankFlamingBag`: a reset between ignite and wander leaves a persisted burning bag with no self-destroy alarm.
- `BoardingSchool`:
  - Pickup paths (null CreatedSim, no car, participant deleted) leave the child enrolled but in the world, with no school.
  - The `RemoveCommon` "continue enrollment" branch keeps a public-school career.
- `DaycareChildManagerProblemChild.VictoryCallback`: unguarded null, and the weekly bonus is lost with the owner Sim.

**Services and Late Night**
- `CelebrityManager.TriggerDisgrace` uses `relationship.SimDescriptionB` as the other Sim. When the celebrity is B, the jealousy targets the celebrity and the partner is not skipped. `GetOtherSimDescription` is the fix.
- The babysitter's `mCheckForChildAlarm` is not removed when she leaves.
- The maid's `mTimeToFinishHandle` is not removed on dismiss or fire, so the alarm can clean the whole lot for free.
- `PizzaDeliverySituation.RouteToLot.OnDrove`: no `return` after `Exit()` on a null pizza box. Rare.
- `GenerateOffspring` loops over every dying Sim, so a couple both dying may generate offspring twice. Inferred.

**Pets and Island Paradise**
- `DogHuntingSkill` / `CatHuntingSkill.RemoveMapTagsAlarmCallback`: NRE on a null MapTagManager after a household switch.
- `UnderwaterCave`:
  - A declined warp leaves the Sim in the destination cave's list, so that cave never waves its tentacle again.
  - A failed state-machine acquire stops the waving for good.
- `PetAdoption`: the neighbour-litter removal alarm restarts at the full 5 days on each load.
- `ResortManager.DoUpdate` writes `mClosed` directly and skips the setter's clean-up.
- `EquestrianCenter`: the won flag is reset before the early return, and the in-competition flag is shared by all entrants.
- `Lifeguard.PanicAttack.DrownDelegate`: unchecked `createdSim`.

**Supernatural, Seasons and Into the Future**
- `FacePaintingBooth.GetPainted`: the wear-off alarm is owned by the Sim and dies on unload, so the paint is permanent.
- Full-moon zombies become permanent after a save/load before sunrise, because `sFullMoonZombies` is not saved.
- `BeReanimatedSolo`: opacity is left at 0 on cancel.
- `AlchemyStation.MixPotion` succeeds with a missing ingredient. Inferred.
- `AlchemyCandle` never burns down: `mLoopingAlarm` is never set.
- `HolidayHouseLights`: after a load in winter `sLotsWithLights` is null, so `TurnOffLightsInWorld` cannot turn the saved lights off. Not verified that lot light state persists.
- `PlumbBob.SelectedActor` is unguarded in `TriggerLunarEvents` and in the tutorial callbacks.

## External leads folded in (research agent, 2026-11-15)

The full list of 30 is in `docs/data/external-bug-leads-1115.md` (untracked). Leads already fixed by the installed simler90 core are left out. NRaas has no licence, so any fix is written from a behaviour spec only.

**Where they meet this batch's RE:**
- Ext 9/10 (lunar alarms and state saved mid full moon) match the permanent full-moon zombie lead above (`sFullMoonZombies` is not saved). Build both together next.
- Ext 21 (boarding school) matches the pickup-path lead above.
- Ext 12 (one throwing Sim stops the aging loop for the town) and ext 5 (`RoleManager` roles whose Sim is gone; docs/184 has no fix) are the next base-game candidates.

**Not started, ranked for batch 4:**
- 1: career tied to a deleted rabbit hole.
- 2: elevator `mAssignedSims`.
- 3: `CommonDoor` lock owner null.
- 6: completed opportunity still active.
- 13: consignment never returned.
- 15: harvest plant seed lost.
- 27: laundromat states.
- 28: hostless NPC party lot-change handlers.
- 29: outfit trimming.

## Tests (in game, off then on)

These are the `<switch>-check` repros in `tools/harness/repros.json`: `vehiclefxhelper`, `prankditchempty`, `serviceassignexit`, `serviceshowalarm`, `maildogchaser`, `lifeguardsurvey`, `moonriselistener`, `holidaylights`.

Live confirmation, where a soak can show it:
- §1: the `TireEffectHelper.UpdateEffects` listener growth goes to 0 with the switch on.
- §8: the `dream_listeners` / `global_by_event` kMoonRise count stays flat across fairy and werewolf loads.
- §9: no `TrickleOnHouseLights` alarm survives a load (alarm census).

## simler90 core check (all batch 3 and batch 4 leads)

The user's install (and our installer, docs/07 §2.1) runs simler90's Gameplay Core Mod UPDATE242. It ships full replacement copies of `Sims3GameplaySystems` and `Sims3GameplayObjects` plus `simler90Extras`. So the methods our hooks swap are simler90's copies whenever it is installed.

**Method.**
- `tools/extract-assemblies.ts` pulled the three assemblies from the package into `game-copy/simler90/asm` (ignored).
- The per-method IL diff against EA (`docs/data/mod-method-diff.csv`, docs/218, 467 changed methods) was checked for every hooked or affected method.
- ilspy decompiles of the touched types (`game-copy/simler90/dec`, ignored) confirmed each hit. No simler90 code is copied.

| Lead | simler90 | What we do |
|---|---|---|
| vehiclefxhelper, prankditchempty, serviceassignexit, serviceshowalarm, lifeguardsurvey, moonriselistener, holidaylights | not touched | kept |
| maildogchaser | not touched (the `HasBeenDestroyed` test in `MailCarrier.DogChaser` is EA's own) | kept |
| ridingjumpwins | **fixes it** (`RidingSkill.ImportContent` reads into `mJumpCompetitionsWon`) | removed |
| door locks (B4 §1) | **partial**: `CommonDoor.IsAllowedThrough` null-guards `mLockOwner`, `mOwnerList` and the resort manager. That removes the SelectedHousehold NRE, but not the stale owner. | built; we do not hook `IsAllowedThrough` |
| elevators E1-E4, StarLight, MoonDial | not touched | built |
| SnubManager | **partial, different bug**: `OnSimAgeTransition` / `OnSnubTimeout` bodies changed (`SnubAgeUpFix`, `SnubTimeOutFix`); the constructor's listener registration is EA's | built (hooks `Sim.Destroy`, not those methods) |
| Household.Remove, Lot.MoveIn, Sim.Destroy, GameObject.AddToUseList | not changed by simler90 | hooked; `Household.Add` / `HandleLastSimsDeath` (simler90-changed) are not hooked |

Every batch 3 and batch 4 hook targets a method that simler90 leaves byte-identical to EA's, so no swap stacks on a simler90 replacement (swap-stack audit rows: `docs/data/swap-stack-audit.csv`).

## Live results 2026-11-15 (phase-20261115-2043, smallfix.txt)

**Proven in game: 22 of the switches in this doc**, plus `sandwichalarm` (docs/580 §16). Each switch was run off, then on. It printed off BROKEN and on FIXED, with controls ok and members found. They moved into the proven set of `tools/harness/sets/proven-fixes.txt`, and their registry rows are marked.
- **Batch 3:** `vehiclefxhelper`, `prankditchempty`, `serviceassignexit`, `serviceshowalarm`, `maildogchaser`, `lifeguardsurvey`, `moonriselistener`, `holidaylights`.
- **Batch 4:** `doorlockowner`, `elevatorkitten`, `elevatorresetriders`, `elevatorsavedlog`, `starlightreset`, `moondiallistener`, `snublistener`, `elevatorridertimeout`.
- **Batch 5:** `rolenullguard`, `horseranchchallenge`, `digsiteemptypick`, `oppchainworld`, `teleportcommunitylot`, `photosubjectstars`.

**Two did not pass. Both are fixed and need another run:**
- **`homeworkskillbonus`**: the result was UNEXPECTED both ways, with "members found=False (missing: Homework.IsOld)". The lookup asked for a property, but `IsOld` is a public field. Because of that, Apply returned early and the hook was never installed: "rates raised" stayed 0. It now uses `GetField`.
- **`dreamcleanup`** (docs/580 §18): `run` printed nothing, while `result` worked. Two changes:
  - The check now reports any exception as "threw ...; verdict: ERROR", so it can no longer print an empty line.
  - `DcFindKey` now reads the index through `IDictionaryEnumerator.Key/Value` instead of `foreach (DictionaryEntry ...)`. The game's Mono may hand out boxed KeyValuePairs for a generic Dictionary; this is the likely cause, still inferred.

## Batch 4 (REA tranche 2b, docs/584 §5): door locks, elevators, StarLight, listener leaks

2026-11-15. Static only. Code: `Batch582bLogic.cs`, checks: `Batch582bChecks.cs`. The rules are the same as above.

| § | Switch | Slots | Scope | Status |
|---|---|---|---|---|
| B4 §1 | `doorlockowner` | 1147-1151 | **household-visible**: a door locked to a gone Sim opens, or is re-pointed to a resident | verified, + load pass |
| B4 §2 | `elevatorkitten` | 1152-1154 | kittens and puppies (any household): the route fails at planning, not at the doors | verified |
| B4 §3 | `elevatorresetriders` | 1155-1157 | **household-visible**: riders get EA's failsafe reset at once instead of after 30 frozen minutes | verified |
| B4 §3b | `elevatorridertimeout` | 1171-1173 | **household-visible**: each rider's failsafe counts from its own arrival | verified |
| B4 §4 | `elevatorsavedlog` | 1158-1161 | log only | REA E4 unconfirmed; this logs it |
| B4 §5 | `starlightreset` | 1162-1164 | **household-visible**: the selected Sim is no longer reset | verified |
| B4 §6 | `moondiallistener` | 1165-1167 | deleted moon dials (leak) | verified |
| B4 §7 | `snublistener` | 1168-1170 | destroyed Sims (leak) | verified |

### B4 §1 doorlockowner: doors stay locked to a dead or moved-out Sim

**The bug [verified].**
- `CommonDoor` keeps `mLockOwner` for SelectedActor and SelectedHousehold locks after that Sim dies or moves out.
  - A SelectedActor lock then admits nobody.
  - A SelectedHousehold lock admits only the gone Sim's new household. With a dead owner, the household is null, so EA's test throws (simler90 null-guards that throw).
- OwnerList cleanup is `HouseholdSimChanged` on the lot household's `HouseholdSimsChanged`. It has three gaps:
  - Only `SetLockTypeOwnerList` subscribes it, so it is gone after any load.
  - `Household.Remove` fires the event only for instantiated Sims.
  - A household moving into the lot inherits every lock.
- Sims behind such a door are trapped. NPCs never re-lock. This matches the user's report of Sims stuck in apartment rooms.

**The fix (at the source).** After `Household.Remove(SimDescription, bool)` (death, move-out, deletion) and after `Lot.MoveIn(Household, bool)` (not on load), the lot's doors are checked against the lot's current household:
- **SelectedActor**: an owner who is not a resident → Anybody.
- **SelectedHousehold**: re-pointed to a resident, so the lock stays "household only". If nobody lives there → Anybody.
- **OwnerList**: non-residents are dropped (EA's own rule). The lock becomes Anybody only when that removal empties the list.
- Nobody, Pets, VIPRoom and a player-chosen empty owner list are never touched.
- Changes go through EA's `SetLockTypeAndOwner`, keeping the one-way room, so the routing restriction and lock visuals refresh.

**Load pass (saved games only).**
- EA's own `SetHouseholdCallback` is called again for OwnerList doors on occupied lots. It does `-=` then `+=`, so it is never doubled.
- The same check then runs once for owners who are already gone.

**Our choice.** Falling back to Anybody (open) rather than Nobody is ours: EA has no rule for a gone owner.

### B4 §2 elevatorkitten: the planner lets kittens and puppies into elevators the doors refuse

**The bug [verified].**
- `ElevatorDoors.IsAllowedThrough` (the planner's check in `Sim.OnPortalPathPlan`) refuses toddlers, horses and wild animals.
- `ElevatorDoorsPortalComponent.OnApproachingPortalObject` (the ride) also refuses kittens and puppies.
- So a kitten or puppy plans through the elevator, walks there, fails, and replans.

**EA's intent.** The planner is the odd one out. EA's rule everywhere else is "toddlers, puppies and kittens never change floors on their own":
- `Stairs.IsAllowedThrough` refuses puppies and kittens.
- `Stairs` `OnPortalStart` refuses toddlers, puppies and kittens.
- The elevator ride (`OnApproachingPortalObject`) refuses toddlers, kittens and puppies.
- Only `ElevatorDoors.IsAllowedThrough` forgets kittens and puppies.

So admitting them at the doors would invent a ride EA never allowed, and its animations and slots are untested for kittens and puppies.

A kitten or puppy upstairs is already stuck in vanilla: every elevator attempt fails at the doors. The fix does not trap it any further; it ends the walk-fail-replan loop.

**The fix.** The planner returns DoorRestriction for kittens and puppies.

### B4 §3 elevatorresetriders: an elevator reset freezes waiting riders for 30 minutes

**The bug [verified].**
- `ElevatorPortalComponent.OnReset` clears `mAssignedSims` while riders sit in `OnPortalStart`'s wait loop.
- `IsElevatorReadyForLaunch` is then false (the elevator is empty), so the loop ends only at the 30 sim-minute failsafe, which calls `SetObjectToReset` on the rider.

**The fix.** Riders that were waiting with a live task when the elevator reset get that same reset at once. The end state is EA's.

### B4 §3b elevatorridertimeout: one failsafe clock for all riders (REA E2)

**The bug [verified].** `OnPortalStart` keeps one `mFailsafeTime`, and each new rider overwrites it before its wait loop. So every waiting rider's 30 sim-minute failsafe counts from the newest arrival. On a busy, stuck elevator, nobody is released until 30 minutes after the last rider came, and then everyone is reset together.

**The fix.** The coordinator approved replacing the method. With the switch on, `OnPortalStart` runs our own implementation of EA's ride:
- It takes the same steps, in the same order, with the same EA calls: the button animation, the full-elevator timeout, the launch by the last sleeper, the use list, the leave effects and the hand-over to the next floor's slot.
- The only difference: the failsafe test uses the rider's own start time.
- `mFailsafeTime` is still written; nothing else reads it.

With the switch off, EA's method runs unchanged.

**Notes.**
- Household-visible: a stuck household rider is released at its own 30 minutes.
- A save made while a rider waits inside the replacement resumes only in the same build. Other builds reset that rider at load, as for any yielding hook (docs/242).

### B4 §4 elevatorsavedlog: saved rider entries (log only)

`ActiveUserEntry` is persisted. After a load, an entry that is not sleeping blocks every later launch until the failsafe; that is REA E4, not yet confirmed.

At world load the pass logs each elevator's saved entries:
- the count;
- the entries not sleeping;
- the entries whose rider is missing or has no live task.

It changes nothing. A fix follows only if a live run shows the state.

### B4 §5 starlightreset: resetting a lit StarLight resets the selected Sim

**The bug [verified].**
- `StarLight.DoReset` (lit) clears the use list, adds `PlumbBob.SelectedActor`, then calls `GameObject.DoReset`.
- `GameObject.DoReset` resets every Sim in the use list, then clears the list.
- So a lot reset, an object reset or moving the light resets whichever household Sim is selected, wherever that Sim is.

**The fix.** While `StarLight.DoReset` runs, `GameObject.AddToUseList` skips the selected Sim for that light. Nothing else changes, because `GameObject.DoReset` empties the list right after anyway.

`SwitchLightInternalStarLight`'s own add is left as EA has it. It keeps the light "in use" while lit, and its effect on autonomy is unknown.

**Limit.** A reset of the light that yields could let a second light's reset clear the flag early. In that case the vanilla reset happens; it is never a wrong skip.

### B4 §6 moondiallistener: deleted moon dials keep their moon listener

**The bug [verified].**
- `MoonDial.OnStartup` registers `mEventLunarCycleEvent` (kMoonChangeEvent), and `Dispose` never removes it.
- A deleted dial keeps creating a lunar VisualEffect for a gone object at every moon change. The effect is never disposed.

**The fix.** `Dispose` removes the held listener first.

### B4 §7 snublistener: SnubManager age-up listeners outlive their Sim

**The bug [verified].**
- `new SnubManager(sim)` registers a kSimAgeTransition listener and drops the handle.
- `Sim.Destroy` only stops the broadcaster, and `EventTracker.RemoveSimReferences` cannot match the listener (it has no ScriptObject).
- So every instantiation of a snubbing NPC teen leaves one listener behind, holding the dead Sim.

**The fix.** At `Sim.Destroy`, the kSimAgeTransition listeners whose delegate belongs to that Sim's manager are removed.

**Not built: the load gap.** `mSnubManager` is saved, but its listener is not registered again after a load. So EA's "end snubs at age-up" never runs for a manager loaded from a save. That fix would be household-visible; proposal only.

### Batch 4 tests (in game, off then on)

Repros `<switch>-check`: `doorlockowner`, `elevatorkitten`, `elevatorresetriders`, `elevatorridertimeout`, `elevatorsavedlog`, `starlightreset`, `moondiallistener`, `snublistener`.

Live confirmation:
- **B4 §1**: lock a door to one Sim, then move that Sim out. Off, the door stays locked. On, it opens. On a saved game, counter 2836 is nonzero after load.
- **B4 §4**: log lines `elevatorsavedlog:` on a Late Night apartment save.
- **B4 §6 and §7**: kMoonChangeEvent and kSimAgeTransition listener counts stay flat in the leak census.

## Batch 5 (Roslyn analyzer leads, docs/583 §2, user-approved)

2026-11-15. Static only. Code: `Batch582cLogic.cs`, checks: `Batch582cChecks.cs`. The rules are the same as above.

**simler90 first.** simler90 already fixes the barista `ConfigureInteraction` and `Run` null tests. It leaves:
- the barista `AddInteractions`;
- all of the Into the Future waiter.

It changes none of the other methods below (`docs/data/mod-method-diff.csv` and an ilspy diff).

| § | Lead | Switch | Slots | Scope | Status |
|---|---|---|---|---|---|
| B5 §1 | 1 | `rolenullguard` | 1174-1176 | barista or waiter with no role: the menu or test answers "no" instead of throwing | verified; simler90 partial |
| B5 §2 | 2 | `homeworkskillbonus` | 1177-1179 | **household-visible**: the homework-skill bonus applies below level 10 | verified |
| B5 §3 | 3 | `horseranchchallenge` | 1180-1182 | **household-visible**: "visit a horse ranch" is hidden for households with no horse | verified |
| B5 §4 | 4 | `digsiteemptypick` | 1183-1185 | **household-visible**: an empty dig no longer uses up the site | verified |
| B5 §5 | 6 | `oppchainworld` | 1186-1188 | **household-visible**: adventure chain history is cleared from the world change (category test fixed 2026-11-15) | verified (intent: the repeated `opportunity4` test) |
| B5 §6 | 9 | `teleportcommunitylot` | 1189-1191 | **household-visible** (menu): no "Teleport to community lot" when no lot is valid | verified |
| B5 §7 | 11 | `photosubjectstars` | 1192-1194 | UI: the photo journal stars use float division | verified |
| B5 §8 | Apollo lead | `residentspawnslot` | 1195-1198 | NPC spawn placement: an apartment resident's door slot is judged with the resident's door rights, not the selected Sim's | verified (code); live count pending |
| B5 §9 | 7 | `talkoccupationchance` | 1199-1201 | **household-visible** (menu): the Unemployment entry's chance is 1/(count+1) as a float | verified |
| B5 §10 | 8 | `shelldoorsummon` | 1202-1204 | Showtime: the summoned proprietor's exit is queued only after its teleport | verified |

**How each fix works.**
- **B5 §1.** The no-role test is `role == null && role.RoleGivingObject == null`, which dereferences the null role. Our hooks run before EA's code on four methods: `BaristaBar.BuyDrinkFromBar/BuySnackFromBar.Definition.AddInteractions`, `FutureFoodSynthesizer+OrderFoodOnWaiter+Definition.AddInteractions` and `.Test`. A target with a null role gets EA's own "no": nothing added, or Test false. A role with no giver already gets EA's "no" without throwing, so that case is left alone. `FutureFoodSynthesizer.TestIfSocialValid` has the same test, but it sits behind `HasActiveRole`, so a null role cannot reach it; not hooked.
- **B5 §2.** `SkillLevel / MaxSkillLevel` is int division, so it is 0 below the maximum.
  - After EA's rate, the missing `k * level / max` is added. The skill used is EA's pick: the better of the student's and the helper's.
  - The added part is scaled by EA's end multipliers: copying (x2, or x1 when old) and MultiTasker.
  - At level 10, EA's result is already right and nothing is added.
- **B5 §3.** The horse count test `>= 0` is always true. An "active" answer for a household with no horse becomes EA's otherwise unreachable `kBecomeActiveButNonVisible`.
- **B5 §4.** The pick test `Count >= 0` is always true. With the switch on, the 4-line method is replaced. The calls are the same and in the same order: the pick, then `GiveTreasuresToSim` with the collection behavior. The site count drops only when the pick holds something.
- **B5 §5.** `(target != Undefined || target != Undefined)` should test the source. With `source != target`, the corrected clause is always true. So the only missed case is: source world set, target world Undefined, and EA's other clause false.
  - In that case, EA's own world-change branch runs: walk up to the source or target, then `ClearChainHistory` from its parent.
  - Everything else runs EA's code.
- **B5 §6.** `GetValidCommunityLots(a).Count >= 0` is always true. EA's true becomes false when the collection is empty.
- **B5 §7.** `SubjectDefinition.Difficulty` returns the same weighted mean, computed with float division. Its only reader is the journal's star row.

**Not built (proposals; each needs a large EA method replaced or has no clear intent).**
- **Lead 5, `UniqueObject` split/merge `&`.** After a failed remove, both paths `Destroy()` the object; EA's `&` also adds the doomed object to the new inventory first. The fix means replacing the 60-line handler, and what should happen to the object is unclear (keep it in the old inventory?).
- Leads 7 and 8 were built later by method replacement (B5 §9, §10).
- **Lead 10, `FlyingDisc` skilled-throw chance.** Animation only, inside the play loop.
- **Lead 12, Medical drug notice pronouns.** Cosmetic, in the middle of large methods.
- **Lead 13, `BinCommon.MergeHouseholdFromExportBin(item, lot)` calls itself.** EA never calls it, and the intended `showConfirmDialog` default is unknown.

### B5 §8 residentspawnslot: apartment residents tested with the selected Sim's door rights

**The bug [verified, decompiled and routecause logs].** `SimDescription.Instantiate(Lot, ResourceKey)` (a Sim with a `VirtualLotHome`: apartment residents) and `Service.CreateSim` (a service NPC living in an apartment) pick the resident's NPC-door slot. Each candidate slot, and the elevator fallback, is tested with `PlumbBob.SelectedActor.IsPointInLotSafelyRoutable`: a full plan from the slot to the lot corners, with the **selected** Sim's door rights.
- A door the resident may pass but the selected Sim may not fails every test (`FailedBecauseDestinationPortalLocked`, about 2 ms each).
- The resident is then placed at an elevator or a lot corner instead of its own door.
- phase-20261115-1831: 119 such failed plans attributed to Apollo Bloom (household), all with goal 815.1,29.4,1658.2 (a corner of lot 0xF590003595C3CD30) and 10 locked doors at rooms 10/7 on that lot. None was Apollo's own route.
- `doors.json` in phase 1831 and 1935 covers only the household's own lot (Trawler Lodge, 14 doors, all Anybody), so the CD30 lock types are not on record. The agent `doors` command now reports, per door, the lock owner, whether that owner still lives there, the owner-list size and gone ids, hidden sides and NPC doors. Run `doors {"lot":"0xF590003595C3CD30"}` to tell which case applies. A lock to a gone owner is `doorlockowner`'s (B4 §1); a lock to the living residents is this one's.
- Live doors for CD30 ("Founders Peace - Studio", household Shutter): 6 Anybody and 3 Nobody. The three refusing doors are NPC doors to hidden room 7 (rooms 10/7). No owner is gone, so `doorlockowner` does not apply. The resident is refused there too: `Door.CanGoThrough` is false for NPC doors, and `CommonDoor.IsAllowedThrough` refuses hidden rooms before it reads the lock. So `residentspawnslot` keeps EA's failure here ([2884]). EA already skips the hidden-side slots, so the open question is whether corridor room 10 (level 1) reaches outside. The tool to answer it is `HiddenRoomCheck "reach F590003595C3CD30 1 all"`, which prints out/closed per probe (selected, resident, NPC, or a named Sim) and each portal with IsAllowedThrough per probe. `lot` scans now list level-1 rooms too. If room 10 is closed, EA is right: the resident spawns at the ground elevator spot, and the failed plans are only a cost. If room 10 is out, the planner disagrees with the room walk, and a slot-test fix is due. Pending.
- `SimDescription.Instantiate` is not logged, so the spawn count is not known (each spawn tests every NPC-door slot).
- `Service.cs:1123` (`GetPositionNearLotCorners`, `Sim.ActiveActor`) tests outdoor corner spots for visitors. A visitor's rights are about the same as anyone's there, so it is left alone. In a resident's spawn (its last fallback) the same re-judging applies.
- simler90 ships `Service` unchanged at these lines; it does not touch `SimDescription.Instantiate`.

**The fix (switch on).**
- While either EA method runs, the resident is recorded for that sim task: a wrapper hook on `Instantiate`, and one line in HiddenRoomSpawn's `Service.CreateSim` hook. Both methods yield, so the record is per task. A check by another task (the selected Sim's own) is never judged for the resident.
- `Sim.IsPointInLotSafelyRoutable` is already hooked by FewerPlans (`routablecache`), so one line there hands the selected Sim's verdict to `RsAdjust`. With the switch on, in a resident's context, the verdict cache is skipped, because a cached "false" has no door list to judge.
- A failed verdict is re-judged from EA's `SimRoutingComponent.LockedDoorsDuringPlan`, which EA clears at each plan start. The spot counts as routable only if the plan found at least one locked door and the resident passes **every** one of them by `CommonDoor`'s lock rules: its own lock, its household's lock, or its owner list. Pets locks are passed by non-pets.
- These stay blocking: hidden-room doors, one-way doors, Nobody and VIPRoom locks, horses at non-barn doors, and the four door types with their own `IsAllowedThrough` (SecretDoor, NonAnimatedDoor, gender doors, Turnstile). A failure with no locked door (no path at all) stays a failure.
- The selected Sim is never routed, moved or changed; only where the NPC appears does. Switch off: EA's verdict, counted as "would accept" in [2883].
- Limit: the lock list shows why the planner was refused, not that a path exists with the doors open. A refused door that is the only obstacle is the logged case.

### B5 §9 talkoccupationchance: the Unemployment entry never appears by chance (lead 7)

**The bug [verified].** `TalkAboutOccupation.Definition.AddInteractions` (celebrity "Try to Impress... Talk About Occupation") computes `float chance = 1 / (count + 1);` as integer division. The chance is 0 whenever any occupation is left over, and 1 when none is.

**The fix (switch on).** The method is replaced with our copy of EA's steps; only the chance is computed as `1f / (count + 1)`. The copy keeps:
- EA's order of steps;
- the `RandomChance01` draw, which EA makes even at chance 0;
- the sort on EA's own `ImpressMerticTuple` list (built by reflection);
- the loop limits.

If the copy throws, its entries are removed and EA's original runs. Household-visible: the player's pie menu may now offer Unemployment.

### B5 §10 shelldoorsummon: the proprietor's exit is queued after a refused teleport (lead 8)

**The bug [verified].** `BigLotShellDoor+SummonProprietor.Run` queues the proprietor's `TeleportMeHere`, then runs `if (flag & queue.Add(exit))`. The `&` does not short-circuit, so the exit interaction is queued even when the teleport was refused. The proprietor then walks out of a door it was never brought to. `Run` returns false, the exit stays queued, and no return-home alarm is set.

**The fix (switch on).** `Run` is replaced with our copy of EA's steps. The exit is offered to the queue only after the teleport was queued (`&&`). The summoning Sim only sees the interaction's result; the proprietor is an NPC.

### Batch 5 tests (in game, off then on)

Repros `<switch>-check`: `rolenullguard`, `homeworkskillbonus`, `horseranchchallenge`, `digsiteemptypick`, `oppchainworld`, `teleportcommunitylot`, `photosubjectstars`, `residentspawnslot`, `talkoccupationchance`, `shelldoorsummon`.

Live confirmation:
- **B5 §2**: a level-5 Homework Sim's homework rate before and after.
- **B5 §7**: the journal shows 1-5 stars.
- **B5 §8**: with Apollo's household selected, the count of `FailedBecauseDestinationPortalLocked` plans at the CD30 corners (off), then [2882] and residents fading in at their doors (on). First run `doors {"lot":"0xF590003595C3CD30"}`.
- **B5 §9**: the Talk About Occupation menu offers Unemployment about 1 time in count+1.

## Batch 6 (Roslyn pass 2, docs/583 §6, user-approved)

2026-11-15. Static only. Code: `Batch582eLogic.cs`, checks: `Batch582eChecks.cs`. The rules are the same as above.

**simler90 first.** simler90's copies of `Sims3GameplayObjects` and `Sims3GameplaySystems` were decompiled with ilspy for each site. All of them are as EA wrote them:
- the five `Dispose` bodies (same IL calls);
- the `BoardingSchoolData` parse;
- `LaundryManager` (the map is written, never read);
- `InternalHaveBabyHome.Run` (`1 - BabyIndex / TotalCount`).

| § | Lead | Switch | Slots | Scope | Status |
|---|---|---|---|---|---|
| B6 §1 | P1 | `disposebase` | 1205-1207 | **household-visible**: five object types run `GameObject.Dispose` | verified (IL, all five) |
| B6 §2 | P2 | `boardingschoolmoney` | 1208-1210 | **household-visible** (funds): the boarding-school phone-call amounts come from the tuned ranges | verified |
| B6 §3 | P3 | `freshclothingresume` | 1211-1214 | **household-visible** (moodlet): Fresh Clothing resumes after a swim or shower | verified code; effect inferred |
| B6 §4 | P5 | `twinbellyshape` | 1215-1217 | **household-visible** (cosmetic): the belly shrinks per baby with twins and triplets | verified |
| - | P4 | not built | - | mermaid `AskedToRehydrate` | see below |
| - | P6 | skipped | - | zombie property recursion | no caller in EA or NRaas |

### B6 §1 disposebase

**The bug [verified, IL].** The `Dispose` overrides of JunkPile, ShoppingRegister, PetBedReward, BubbleColumn and FogMarker never call the base. PetBed and Column do not override `Dispose`, so for all five the skipped base is `GameObject.Dispose`. Skipped with it:
- purging Sims' queued interactions on the object;
- removing its alarms and reactions;
- removing it from the lot's commodity map and the room look-at list;
- disposing its components;
- `kEventObjectDisposed`.

**The fix (switch on).** After the override's own cleanup, `GameObject.Dispose` runs, as in every other EA object. Each hook class derives from `GameObject`, so `base.Dispose()` is a non-virtual call to `GameObject.Dispose` from inside the swapped body. There is one class per target because each needs its own stub.

### B6 §2 boardingschoolmoney

**The bug [verified].** `BoardingSchoolData(XmlDbRow)` crosses the two ranges:
- `NeedMoneyRange` fills RewardMin and NeedMoneyMin;
- `RewardMoneyRange` then overwrites NeedMoneyMin and fills RewardMax;
- NeedMoneyMax is never set, so it stays 0.

As a result, the need-money call asks for `GetInt(RewardMoneyRange[0], 0)`, an inverted range that gives 1 up to that minimum. The reward call starts from `NeedMoneyRange[0]`.

**The fix (switch on).** EA parses at startup, before ts3fixes is up. So at world load, the fix re-reads EA's own `BoardingSchool` table and sets each parsed school's four fields from its own range. EA's fallbacks are 100 and 500 in both branches; those become each pair's min and max. Turning the switch on takes effect at the next world load.

### B6 §3 freshclothingresume

**The bug [verified code, effect inferred].** The remaining time is saved in three places, and nothing reads it:
- `OnClothesChanged` saves the Fresh Clothing buff's remaining time in `mFreshClothingTimeouts` when a Sim changes into swimwear or naked, and removes the buff.
- `BuffFreshClothing.OnRemoval` records 0 when the buff ends.
- A dryer cycle clears the map.

Because nothing reads the map, `AddLaundryBuff` always adds the buff at full length.

**The fix (switch on), at the reader.** In `AddLaundryBuff`, for a Sim that has a saved entry and no Fresh Clothing buff on:
- with a saved time, the add resumes it, and the entry is used up;
- with a saved 0, nothing is added, because the buff already ran out on this laundry.

With no entry, or with the buff still on the Sim, EA's add runs.

### B6 §4 twinbellyshape

**The bug [verified].** `Pregnancy+InternalHaveBabyHome.Run` calls `SetPregnancy(1 - BabyIndex / TotalCount)` with integer division. The belly stays full until the last baby.

**The fix (switch on).** The `Run` hook records the baby index and count for its own sim task, because `Run` yields in `AnimateSim`. When `SimDescription.SetPregnancy(float)` is called from that task with EA's value, it gets `1 - i/n` instead. Any other call is untouched.

### Not built

- **P4, mermaid `AskedToRehydrate`.** The field is never set because EA has no "ask to rehydrate" interaction. The frozen-Sim twin, `AskToThawOut`, exists in SimTemperature; nothing like it exists for mermaids. A fix would mean adding a new social, with no text or animation known to ship, so it is a feature rather than a fix.
- **P6, the zombie `ChanceToAutonomouslyPerformBraaaiiinsSocial` property.** It calls itself, but nothing in EA's code or in the NRaas sources calls it, so it is skipped.

### Batch 6 tests (in game, off then on)

Repros `<switch>-check`: `disposebase`, `boardingschoolmoney`, `freshclothingresume`, `twinbellyshape`.

Live confirmation:
- **B6 §1**: sell a junk pile or a register with a Sim's interaction queued on it; the queue is purged ([2892]).
- **B6 §2**: [2895] after the world loads; check a need-money call's amount against the tuned range.
- **B6 §3**: Fresh Clothing time before a swim and after changing back.


## Batch 7 (REA tranche 3, docs/584 §6, plus external lead NRaas #42)

2026-11-15. Static only. Code: `Batch582fLogic.cs`, checks: `Batch582fChecks.cs`, slots 2905-2919. All switches are off by default.

**simler90 first.** simler90 changes none of the methods below (`docs/data/mod-method-diff.csv`). It does change `BoatMultipleOccupancy.OnRouteFinished`, but not `OnRouteStarted`.

| § | Lead | Switch | Slots | Scope | Status |
|---|---|---|---|---|---|
| B7 §1 | T1 | `boatstaleswimmer` | 1218-1220 | **household-visible**: a boat waits for its swimmers again | verified |
| B7 §2 | T2 | `oppcategoryprotect` | 1221-1223 | NPC culling only | verified; trigger inferred |
| - | T3 | already fixed | - | `viproomrefresh` (docs/555 §3) runs EA's loop over a copy | not repeated |
| B7 §3 | T4a | `nocommercialcar` | 1224-1226 | ambient traffic | verified |
| B7 §4 | T4b | `scavengernolot` | 1227-1229 | **household-visible**: the accept completes in a world with no community lots | verified |
| B7 §5 | NRaas #42 | `partylotlistener` | 1230-1232 | **household-visible**: the selected Sim's lot changes reach every other listener again | verified; NRaas fixes the saved state at load |

**How each fix works.**
- **B7 §1.** `OnRouteStarted` loops over the read-only key view of `mPassengersInTheWater`. When a passenger's Sim is gone, it removes that key and carries on, so the next step throws. As a result, base `OnRouteStarted` and the re-board wait are skipped.
  - The fix removes those stale keys first, then runs EA's body unchanged.
  - With the switch off, [2906] counts the routes that would throw. That happens only when the stale key is not the last one.
- **B7 §2.** `AcceptOpportunity(toAdd, …)` adds FullFromOpportunities protection before its "category taken / no shared data" early return. Nothing removes it on that path, so the NPC is never culled.
  - On that path the fix skips the whole method. The protection was its only effect there.
- **B7 §3.** `CarNpcWithNoDriver+LiteDriver.Simulate` picks a random "other commercial lot" with no emptiness test. With none to pick, the throw becomes a script error and the car is destroyed.
  - With no such lot, the fix runs our copy of EA's loop without the drive step. The car waits EA's random interval until it should drive off, then `DriveOffscreenAndRelease`. Reset and destroy work as in EA.
- **B7 §4.** `ScavengerOpportunity.OnAdded` spawns items on a random community lot. In a world with none, the pick throws partway through the accept.
  - In that case only `Opportunity.OnAdded` runs, via a non-virtual base call. EA could spawn nothing there anyway.
- **B7 §5.** `NpcParty` children `WaitForSelectableGuestToArrive` and `Happening` unsubscribe from `Sim.OnLotChangedEvent` only in `Dispose`. `Situation.Exit` and `SetState` run `CleanUp`, then `Dispose`.
  - `Happening.CleanUp` dereferences `Parent.Host`. For a party whose host is gone it throws, so `Dispose` never runs and the listener stays. `PostLoad` adds it again after every load.
  - From then on, each lot change of a selectable Sim throws in that listener, and the throw stops the event's other subscribers (jukebox, DJ booth).
  - The fix: a listener whose party has no parent or no host unsubscribes itself and returns. Only selectable Sims reach EA's throw, so only they are judged.

**External leads checked, not built.**
- **B2 "one bad Sim stops all aging".** simler90 changes `AgingManager.DayPassedCallback` and four other aging methods, so this is left to the installed core mod.
- **B20 "whim-tracker exception on every lot switch".** No whim/dream lot-change subscriber exists in EA's DreamsAndPromises code. The lot-change throw found is NRaas #42, built as B7 §5 above.

**New tool.** `tools/static/reflect-members-check.ts` checks every `typeof(T).GetField/GetProperty/GetMethod("n")` in ts3fixes against the decompiled EA tree. It flags a member asked for as the wrong kind, the homeworkskillbonus `IsOld` mistake.
- It is local-only and needs `game-copy`.
- Current tree: 476 lookups checked, 0 wrong.
- The live failure would have been caught: EA declares `Homework.IsOld` as a field.

### Batch 7 tests (in game, off then on)

Repros `<switch>-check`: `boatstaleswimmer`, `oppcategoryprotect`, `nocommercialcar`, `scavengernolot`, `partylotlistener`. Live confirmation:
- **B7 §5:** [2918] with the switch off in a save that has had NPC parties.
- **B7 §1:** [2906] after boat trips with swimmers.

**Proven 2026-11-15 (s56, phase-20261115-2218 smallfix, off BROKEN / on FIXED):** `boardingschoolmoney`, `boatstaleswimmer`, `disposebase`, `freshclothingresume`, `homeworkskillbonus`, `nocommercialcar`, `oppcategoryprotect`, `partylotlistener`, `residentspawnslot`, `scavengernolot`, `shelldoorsummon`, `talkoccupationchance`, `twinbellyshape` (moved into `tools/harness/sets/proven-fixes.txt`). `dreamcleanup` was ERROR both ways; see B8 notes.

## Batch 8 (REA tranche 4, docs/584 §7; coverage sweep, docs/586; CD30 spawn walk)

2026-11-15. Static only. Code: `Batch582gLogic.cs`, checks `Batch582gChecks.cs`; §8 in `ResidentSpawnSlot.cs`. Slots 3545-3650. All switches off by default. simler90 UPDATE242 changes none of these methods (its `Secrets.cs` has the same key bug), and none was hooked before (`docs/data/swap-stack-audit.csv`).

| § | Lead | Switch | Slots | Scope | Status |
|---|---|---|---|---|---|
| B8 §1 | REA R4 | `trashpileimport` | 1233-1236 | **household-visible**: an imported pile ages and smells once | verified |
| B8 §2 | coverage #1 | `catgymwatch` | 1237-1239 | **household-visible**: passers-by now watch a cat on the jungle gym | verified |
| B8 §3 | coverage #3 | `badsecretkey` | 1240-1242 | **household-visible**: the bad relationship secret text shows | verified |
| B8 §4 | coverage 2 #1 | `traitlistenerremove` | 1243-1245 | **household-visible**: a lost trait stops reacting | verified |
| B8 §5 | coverage 2 #2 | `weddinglistener` | 1246-1248 | stale state only | verified |
| B8 §6 | runtime R1 | `portasknohome` | 1249-1251 | **household-visible**: a homeless Sim's social menu builds | verified, runtime x2 |
| B8 §7 | runtime R2 / #13 | `lecturebroadcaster` | 1252-1254 | **household-visible**: one lecture pulse, not one per lecture given | verified, tuning read |
| B8 §8 | CD30 reach | `residentspawnwalk` | 1255-1257 | NPC spawn placement only | verified (code + live reach) |

**How each fix works.**
- **B8 §1.** `TrashPile.CreateAlarmAndStartAgeEffects` runs from `OnCreation` and again from `ImportContent`.
  - The second run adds an age alarm without removing the first; that one later re-fires Fresh→Old over the live broadcaster and stink.
  - A pile saved as Ancient goes straight to `AlarmCallbackOldToAncient`, which needs the broadcaster only Fresh→Old makes: the import throws.
  - Fix: release the earlier alarm, broadcaster and stink first; build an Ancient pile through EA's Old step, then EA's Old→Ancient step (a delegate call, not `MethodInfo.Invoke`).
- **B8 §2.** `CatJungleGym.OnEnterWatchPetBroadcaster` pushes `WatchCatJungleGym` without `JungleGymRef`; `Run` returns false when it is null. Fix at `Run`: fill it with the gym the cat is using (`Target.CurrentInteraction.Target`, what `InLoop` checks).
- **B8 §3.** `Secrets.GetBestBadSecretTextFor` builds the relationship key without the `:` its own guard uses. Fix after EA's method: when the result is exactly the wrong key's text, it becomes the right key's text. EA's pick and its `sRelationshipSecrets` bookkeeping stay.
- **B8 §4.** `TraitManager.RemoveElement(ulong)` reads `TraitListener` from a fresh dictionary clone (always null). Fix before EA's method: the Sim's own trait (`GetElement`) has its listener removed, as `Trait.Fixup` does.
- **B8 §5.** `WeddingParty.CleanUp` never removes the world-wide `kMarriage` listener. Fix after EA's `CleanUp`: the party's still-registered listeners are removed.
- **B8 §6.** `SocialTest.AskToChangePortTest` calls `IsHouseboatLot()` on a null home lot. Fix: false where EA would throw.
- **B8 §7.** `Education.BeIntrigueing` makes an unstored broadcaster; the shipped `kGreatLectureBroadcastParams` has `TimeDuration -1` (`tools/static/game-data-check.ts tuning kGreatLectureBroadcastParams`). Fix: one broadcaster per lecturer; a new lecture disposes the previous one, then builds EA's. The kept one has EA's endless duration (user decision 2026-11-15: keep it, de-dupe only).
- **B8 §8 (root of the CD30 resident spawn).** EA's `SimDescription.Instantiate(Lot, ResourceKey)` tests the resident's NPC-door slot with `PlumbBob.SelectedActor.IsPointInLotSafelyRoutable`: a plan as the selected Sim from the slot to the lot corners.
  - From room 10 (level 1) that plan rides the elevator. `ElevatorDoors.IsAllowedThrough` asks `CanTravelInElevator` of the plan's Sim: the selected Sim is not on its home lot, not greeted and not an NPC, so only ground-level elevators are open to it (`DoorRestriction`).
  - That is no door lock, so `LockedDoorsDuringPlan` stays empty and `residentspawnslot` never sees it. The resident (an NPC of the lot) may ride any elevator in a public residential room. Live reach on CD30: r10 out for the resident and an NPC, closed for William.
  - Fix: a failed check during a resident's placement is re-judged by the room walk (`HiddenRoom.cs HrWalkOut`, now shared with HiddenRoomCheck) with the resident's rules: a portal the selected Sim passes is open; an elevator opens as EA's NPC branch opens it; a door opens when the resident passes its lock (`RsDoorPasses`); anything else is closed. EA's public-residential test on the slot still follows. Never for a resident of the active household.
  - The fix cannot move the test to the resident itself: the resident has no Sim (no route proxy) until this method makes one.

**Already covered, not rebuilt.** REA R2 and R5(b): `fieldtripadult` (docs/565 §2 (a), (c)). REA R1: `prankditchempty` (B §2).

**Checked, not built.**
- **R3 (face-paint booth loops).** Not reachable with the shipped data: every listed paint and failure pattern has a CAS part for child, teen, adult and elder, both sexes (`tools/static/game-data-check.ts casp ...`; tuning from GameplayData.package). Path (b) sits mid-`Run` after a yield, so a guard means replacing `Run`: not cheap.
- **R5(a).** `TryToGiveReward` reads `Reward` before its null check: field trips are made only for `IFieldTrippable` rabbit holes.
- Coverage #2 (midlife export), #4 (Inner Beauty listener), #5 (nemesis, design call), #6, #7, coverage 2 #3-#5 and runtime R3 (needs a stack): left for a later batch.

**Our harness bugs fixed.**
- **dreamcleanup ERROR (s56).** The game's Mono throws `KeyNotFoundException` from a generic `Dictionary`'s `IDictionary` indexer on a missing key (.NET returns null). The missing key was the not-yet-filed woken node's key (9) in the check, and any unfiled key in the live hooks (counted as errors). `DcIndexList` now tests `Contains` first.
- **combo venue TargetException (2026-11-14 runs).** Already fixed in c45b9d65 (a delegate for the swapped `StartShowSoundBroadcaster`); `EndShowSoundBroadcaster` now goes the same way.

**New tool.** `tools/static/game-data-check.ts` (local-only, read-only): `tuning <regex>` greps XML tuning in the installed packages; `casp <names>` checks CAS parts per age and sex.

### Batch 8 tests (in game, off then on)

Repros `<switch>-check` for all eight switches, and `dreamcleanup-check` again. Live confirmation: `residentspawnwalk` [3649] with the switch off on CD30 (Apollo Bloom), then the resident at his own door with it on.

## Batch 8b (coverage sweep, docs/639 §4-§5)

2026-11-15. Static only. Code: `Batch582hLogic.cs`, checks `Batch582hChecks.cs`, slots 3651-3692 (below the EP batch range 1400-1499). All switches off by default. simler90 UPDATE242 changes none of these methods; none was hooked before.

| § | Lead | Switch | Slots | Scope | Status |
|---|---|---|---|---|---|
| B8b §1 | coverage #2 | `midlifewishsave` | 1258-1260 | **household-visible**: the crisis ending after a trip | verified omission; travel effect inferred |
| B8b §2 | coverage #4 | `innerbeautyonce` | 1261-1263 | **household-visible**: one notice per mean social | verified |
| B8b §3 | coverage 2 #3 | `betrayednullsim` | 1264-1266 | **household-visible**: notice and break-up instead of a throw | verified null path; reach inferred low |
| B8b §4 | coverage 2 #4 | `topictestnull` | 1267-1269 | **household-visible**: topic lists build | verified code; reach inferred |
| B8b §5 | coverage 2 #5 | `opptrackerrelisten` | 1270-1272 | **household-visible**: completion listener once | verified double add; effect inferred |

**r43 load hang (2026-11-16, fixed in r43b):** `betrayednullsim`'s static `sBtLoc` looked up `RomanceVisibilityState.LocalizeReputationKey` by name alone. Both EA 1.67 and simler90 UPDATE242 declare it twice (`(bool, string, params object[])` and `(bool[], string, params object[])`), so `GetMethod` threw AmbiguousMatchException inside the static constructor of the partial class `Fixes`: every ts3fixes fix was dead from startup (no ts3fixes log line at all, not even the main-menu `play: ui tasks`), and Instantiator's world-load handler then called `Fixes.ClearNameCache()` outside a try, so the exception left EA's world-load-finished event and the load never finished (sim loop alive, no stall). Fix: the lookup names the `(bool[], string, object[])` overload; the three world-load calls are wrapped like the others; `tools/static/refs-resolve-check.ts` (run by build-fixes) fails the build on unresolved refs and on name-only `GetMethod` lookups that are ambiguous in the game's assemblies. It also found the same pattern in ts3agent's LoadWorld fallback (`PlayFlowModel.ActivateSimScreenMaskedFunc`, `(object)` and `(ulong)`), fixed.

**How each fix works** (details in the code comments):
- **§1.** `MidlifeCrisisManager` export/import skips `mWishesFulfilled` and `mLastUpdatedDate`. Our hooks write them after EA's fields under our own keys and read them back; a stream without them leaves EA's values (vanilla readers ignore unknown keys).
- **§2.** Each Inner Beauty instance adds an unfiltered listener bound to a handler that acts on the event's actor. Each `Event` (new per send) is now handled once.
- **§3.** `ShowTNSAndBreakUp` reads `createdSim.IsFemale` when only the victim is selectable. The notice takes the sex from the SimDescription; EA's break-up runs.
- **§4.** `ActiveTopic.PassesTestFunction` calls test functions with `MethodInfo.Invoke`, which refuses swapped methods in the game's Mono, so nothing is swapped: at world load the five topics' `TestFunction` is re-pointed to wrappers that call EA's method (switch off: unchanged) or answer where EA would throw (switch on).
- **§5.** `OpportunityTrackerModel.TriggerInteraction` re-adds a completion listener that is already registered (same key; `AddListener` does not dedupe). On exactly EA's path the old registration is removed first.

**Not built: `BuffImminentNemesis.OnSocialization` (coverage #5), needs the user's call.** It gates the instant nemesis on `TargetSim.CanHaveRomanceWith(other)`, the same test as `BuffImminentRomance`.
- Reading A (copy-paste bug): a nemesis needs no romance eligibility; the spell should fire on any human socialized with, including relatives and teen/adult pairs. Fix: drop the test (keep `IsHuman`).
- Reading B (intended): EA kept the romance-eligible pool on purpose, as the spell's mirror of Imminent Romance. Then nothing changes.
- The code alone cannot tell them apart; the buff's description text would.

**Skipped:** coverage #6 (Imaginary Friend copy ctor; inferred, rare), #7 (MoodLamp; no callers), runtime R3 (no stack yet).

## Review of batches 3-8b (2026-11-15)

The user's rule: every EA method we replaced or read for batches 3-8b, and its direct callees, was read again for loops that can't exit, uncapped retries, per-tick O(n²), first-item returns, collections changed while iterated, wrong order and other defects. Fixes went into the same switch. Static only; no new slots.

**Our-code defects fixed:**
- `oppchainworld` (B5 §5): the hook tested the category the wrong way round. It changed ordinary chains and never reached the adventure branch with EA's typo. It now acts on adventure chains only. EA's walk there can also reach null (NRE, or recursion on null); with the switch on that case clears nothing. **Household-visible.**
- `digsiteemptypick` (B5 §4): a site whose treasure info does not resolve always picks nothing, and the dig loop ends only at count 0, so the dig could go on forever. After 3 empty digs in a row on one site, the count drops as in EA. **Household-visible.**
- `holidaylights` (§9): the held trickle alarm was removed even when EA then returned early (`sLotsWithLights` set), which stranded the remaining lots. It is now removed only when EA rebuilds the lists. Batch 9: the statics are saved (`PersistableStatic(false)` = not main-menu-only), so the NeverPersisted step, which left a dangling saved handle, is removed. NPC lots.
- `doorlockowner` (B4 §1): for an instantiated Sim, EA's own `HouseholdSimChanged` empties the owner list during `Household.Remove`, so the after-check saw nothing gone. Lists holding the Sim are recorded first; a list this removal emptied opens. **Household-visible.**
- `weddinglistener`, `serviceassignexit`, `lifeguardsurvey`: our post-step now runs in `finally`, so a throw in EA's method (null host, KeyNotFound) no longer skips it.
- `twinbellyshape` (B6 §4): EA's `SetPregnancy` does not redraw an already-pregnant Sim, so the corrected value never showed. `RefreshBodyShape` now follows. **Household-visible.**
- Static Sim / Event references (lecturebroadcaster, innerbeautyonce, the dig-run map) are cleared at world load.

**EA defects folded in:**
- `innerbeautyonce` (B8b §2): EA shows "MeanSocialTNS" outside its buff test, so every selectable Sim gets it. With the switch on, only actors with Inner Beauty get it. **Household-visible.**
- `residentspawnslot` (B5 §8): `Instantiate(Lot, ResourceKey)` tests the slot with the caller's lot, not the resident's VirtualLotHome, so invited residents never use their own door. With the switch on the method receives VirtualLotHome. NPC-only.
- `serviceshowalarm` (§4): `ServiceSituation.CleanUp` drops a pending show alarm, leaving a surviving (apartment) worker hidden, non-autonomous and unsociable. With the switch on, EA's `MakeServiceSimVisible` runs first. NPC-only.

**Queued, not built here** (new switches, a full method copy, or a design call):
- `TraitManager.RemoveElement` null trait (needs a full copy including the base call; reach inferred).
- `FutureFoodSynthesizer` null RoleGivingObject (low reach).
- Homework copy multiplier (design call).
- LaundryManager per-lot mood flag.
- HolidayHouseLights `PreviousSeason` order and unsaved statics.
- Theatre post-show one-shot handle.
- `Service.CreateSim` non-public slot fallback.
- WeddingParty / NpcParty CleanUp null guards.
- `AttemptToPutInSafeLocation` fallback leaving the Sim hidden.
- Smaller items (review list B14).

## Batch 9 (review queue, Imminent Nemesis)

2026-11-15. Static only. Code: `Batch582iLogic.cs`, checks `Batch582iChecks.cs`, slots 3693-3834. All default off. simler90 UPDATE242 changes none of these methods.

| § | Switch | Slots | Scope | Status |
|---|---|---|---|---|
| B9 §1 | `traitremovenull` | 1273-1275 | **household-visible** | verified (Trait.Fixup -> RemoveElement for a missing-pack or removed-CC trait) |
| B9 §2 | `homeworkcopyold` | 1276-1278 | **household-visible**: copying old homework is slower | verified; user: "as EA intended" |
| B9 §3 | `laundrymoodeach` | 1279-1281 | **household-visible** | verified |
| B9 §4 | `partycleanupnull` | 1282-1284 | **household-visible** (player parties) | verified derefs; root cause of partylotlistener / weddinglistener |
| B9 §5 | `safelocunhide` | 1285-1287 | **household-visible** (reset placement) | verified code; reach inferred |
| B9 §6 | `midlifeuiending` | 1288-1290 | **household-visible**: the crisis ends at the day pass | verified |
| B9 §7 | `nemesisnoromance` | 1291-1293 | **household-visible** | user decision: option A |

**How each fix works** (details in the code comments):
- **§1.** `RemoveElement` reads `traitFromDictionary.Guid` with no null test for an instantiated Sim. EA's steps run null-safe; the base step goes through reflection. It shares traitlistenerremove's hook.
- **§2.** The copy multiplier is read from the copier's own homework, which is never old. The rate is scaled to the copied homework's multiplier.
- **§3.** The mood multiplier flag is per lot, so only the first Sim gets it, and a re-add can multiply twice. Each buff is raised idempotently to base x multiplier. It shares freshclothingresume's hook.
- **§4.** `Happening.CleanUp` and `WeddingParty.CleanUp` throw on a gone host or guest before `base.CleanUp`. Where EA would throw, its steps run null-safe, then EA's base, through hook classes that derive from EA's base types.
- **§5.** The reset fallback in `AttemptToPutInSafeLocation` does not unhide. It now runs EA's found-spot unhide steps. It shares hiddenroomspawn's hook.
- **§6.** The moodlet tooltip getter `GetMinutesLeft` called `SuccessfulEnding`. It now returns EA's value with no side effect.
- **§7.** `CanHaveRomanceWith` is dropped. Both Sims must be human and child or older (the forced Fight! social's ages).

**Folded into existing switches (logic changed, so off/on retest):**
- `rolenullguard`: `FutureFoodSynthesizer.TestIfSocialValid` had the same typo, and throws on an active role with no giver.
- `sandwichalarm`: the theatre one-shot does nothing while a player show runs.
- `talkoccupationchance`: the best-choice cap was off by one (6 shown where the tuning says at most 5); the seed is called only with occupations left.
- `elevatorridertimeout`: the failsafe return clears the entry's sleeping flag and task.
- `holidaylights` correction: `[PersistableStatic(false)]` means "not main-menu-only", so the statics are saved. The NeverPersisted step left a dangling saved handle and is removed.

**Not built:**
- `Service.CreateSim` slot fallback: EA keeps the last routable door slot (the resident's own side) on purpose as a fallback; a bad elevator spot is already moved by `hiddenroomspawn`.
- `HolidayHouseLights.SeasonChanged` null order: the listener only receives SeasonTransitionEvent, so it is unreachable. The "statics not saved" item was a false premise.
- InnerBeauty broadcaster re-create / RemoveEffects null: inferred; needs a load test.
- Werewolf / fairy alarm handles: the alarms belong to the old Sim object and go with it.
- Elevator StateMachineClient release: EA's pattern everywhere.

**Lecture (lecturebroadcaster), not changed:** EA starts the broadcaster in the social's `peau` (after-update) callback, once the speech animation has ended, so the effect is meant to outlast the speech. User decision 2026-11-15: keep EA's lingering (never-ending) effect, de-dupe only.
