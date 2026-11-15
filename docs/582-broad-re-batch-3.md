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
| 1 | `vehiclefxhelper` | 1006-1008 | any vehicle (cosmetic, leak) | verified |
| 2 | `prankditchempty` | 1009-1011 | prankster (household or NPC) | verified |
| 3 | `serviceassignexit` | 1012-1015 | service bookkeeping; **household-visible**: the preferred service Sim can return | verified, + load sweep |
| 4 | `serviceshowalarm` | 1016-1018 | NPC service Sims | verified |
| 5 | ~~`ridingjumpwins`~~ | (released) | removed: simler90 core fixes the same line | [simler90 fixes it] |
| 6 | `maildogchaser` | 1022-1024 | **household-visible**: the dog no longer chases at every delivery | verified |
| 7 | `lifeguardsurvey` | 1025-1027 | household lifeguard's leftover alarm | verified, + load sweep |
| 8 | `moonriselistener` | 1028-1030 | mostly NPC fairies and werewolves (leak) | leak verified, gameplay effect inferred |
| 9 | `holidaylights` | 1031-1034 | world (NPC homes), alarm leak | verified, + load sweep |

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

The bug is real (EA's `RidingSkill.ImportContent` reads key 2999955368 into `mCrossCountryCompetitionsWon`), but simler90's Gameplay Core Mod UPDATE242, which our installer ships (docs/07 §2.1), already reads it into `mJumpCompetitionsWon`. The switch, its hook and slots 1019-1021 were removed so we do not stack a second swap on simler90's replacement.

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
- **Orphan after load:** the statics are `[PersistableStatic(false)]` but the alarm is saved. After a load mid-trickle, the saved alarm is an orphan that EA can never remove.

**The fix.**
- Before EA's call, a held trickle alarm is removed.
- After it, a 0 pulse with lots waiting becomes 1, and the alarm is made NeverPersisted. A load forgets the lists anyway, as before.
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
| B4 §1 | `doorlockowner` | 1035-1039 | **household-visible**: a door locked to a gone Sim opens, or is re-pointed to a resident | verified, + load pass |
| B4 §2 | `elevatorkitten` | 1040-1042 | kittens and puppies (any household): the route fails at planning, not at the doors | verified |
| B4 §3 | `elevatorresetriders` | 1043-1045 | **household-visible**: riders get EA's failsafe reset at once instead of after 30 frozen minutes | verified |
| B4 §3b | `elevatorridertimeout` | 1059-1061 | **household-visible**: each rider's failsafe counts from its own arrival | verified |
| B4 §4 | `elevatorsavedlog` | 1046-1049 | log only | REA E4 unconfirmed; this logs it |
| B4 §5 | `starlightreset` | 1050-1052 | **household-visible**: the selected Sim is no longer reset | verified |
| B4 §6 | `moondiallistener` | 1053-1055 | deleted moon dials (leak) | verified |
| B4 §7 | `snublistener` | 1056-1058 | destroyed Sims (leak) | verified |

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
- **B4 §1**: lock a door to one Sim, then move that Sim out. Off, the door stays locked. On, it opens. On a saved game, counter 1037 is nonzero after load.
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
| B5 §1 | 1 | `rolenullguard` | 1062-1064 | barista or waiter with no role: the menu or test answers "no" instead of throwing | verified; simler90 partial |
| B5 §2 | 2 | `homeworkskillbonus` | 1065-1067 | **household-visible**: the homework-skill bonus applies below level 10 | verified |
| B5 §3 | 3 | `horseranchchallenge` | 1068-1070 | **household-visible**: "visit a horse ranch" is hidden for households with no horse | verified |
| B5 §4 | 4 | `digsiteemptypick` | 1071-1073 | **household-visible**: an empty dig no longer uses up the site | verified |
| B5 §5 | 6 | `oppchainworld` | 1074-1076 | **household-visible**: chain history is cleared from the world change | verified (intent: the repeated `opportunity4` test) |
| B5 §6 | 9 | `teleportcommunitylot` | 1077-1079 | **household-visible** (menu): no "Teleport to community lot" when no lot is valid | verified |
| B5 §7 | 11 | `photosubjectstars` | 1080-1082 | UI: the photo journal stars use float division | verified |
| B5 §8 | Apollo lead | `residentspawnslot` | 1083-1086 | NPC spawn placement: an apartment resident's door slot is judged with the resident's door rights, not the selected Sim's | verified (code); live count pending |
| B5 §9 | 7 | `talkoccupationchance` | 1087-1089 | **household-visible** (menu): the Unemployment entry's chance is 1/(count+1) as a float | verified |
| B5 §10 | 8 | `shelldoorsummon` | 1090-1092 | Showtime: the summoned proprietor's exit is queued only after its teleport | verified |

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
- Live doors for CD30 ("Founders Peace - Studio", household Shutter): 6 Anybody and 3 Nobody. The three refusing doors are NPC doors to hidden room 7 (rooms 10/7). No owner is gone, so `doorlockowner` does not apply. The resident is refused there too: `Door.CanGoThrough` is false for NPC doors, and `CommonDoor.IsAllowedThrough` refuses hidden rooms before it reads the lock. So `residentspawnslot` keeps EA's failure here ([1085]). EA already skips the hidden-side slots, so the open question is whether corridor room 10 (level 1) reaches outside. The tool to answer it is `HiddenRoomCheck "reach F590003595C3CD30 1 all"`, which prints out/closed per probe (selected, resident, NPC, or a named Sim) and each portal with IsAllowedThrough per probe. `lot` scans now list level-1 rooms too. If room 10 is closed, EA is right: the resident spawns at the ground elevator spot, and the failed plans are only a cost. If room 10 is out, the planner disagrees with the room walk, and a slot-test fix is due. Pending.
- `SimDescription.Instantiate` is not logged, so the spawn count is not known (each spawn tests every NPC-door slot).
- `Service.cs:1123` (`GetPositionNearLotCorners`, `Sim.ActiveActor`) tests outdoor corner spots for visitors. A visitor's rights are about the same as anyone's there, so it is left alone. In a resident's spawn (its last fallback) the same re-judging applies.
- simler90 ships `Service` unchanged at these lines; it does not touch `SimDescription.Instantiate`.

**The fix (switch on).**
- While either EA method runs, the resident is recorded for that sim task: a wrapper hook on `Instantiate`, and one line in HiddenRoomSpawn's `Service.CreateSim` hook. Both methods yield, so the record is per task. A check by another task (the selected Sim's own) is never judged for the resident.
- `Sim.IsPointInLotSafelyRoutable` is already hooked by FewerPlans (`routablecache`), so one line there hands the selected Sim's verdict to `RsAdjust`. With the switch on, in a resident's context, the verdict cache is skipped, because a cached "false" has no door list to judge.
- A failed verdict is re-judged from EA's `SimRoutingComponent.LockedDoorsDuringPlan`, which EA clears at each plan start. The spot counts as routable only if the plan found at least one locked door and the resident passes **every** one of them by `CommonDoor`'s lock rules: its own lock, its household's lock, or its owner list. Pets locks are passed by non-pets.
- These stay blocking: hidden-room doors, one-way doors, Nobody and VIPRoom locks, horses at non-barn doors, and the four door types with their own `IsAllowedThrough` (SecretDoor, NonAnimatedDoor, gender doors, Turnstile). A failure with no locked door (no path at all) stays a failure.
- The selected Sim is never routed, moved or changed; only where the NPC appears does. Switch off: EA's verdict, counted as "would accept" in [1084].
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
- **B5 §8**: with Apollo's household selected, the count of `FailedBecauseDestinationPortalLocked` plans at the CD30 corners (off), then [1083] and residents fading in at their doors (on). First run `doors {"lot":"0xF590003595C3CD30"}`.
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
| B6 §1 | P1 | `disposebase` | 1093-1095 | **household-visible**: five object types run `GameObject.Dispose` | verified (IL, all five) |
| B6 §2 | P2 | `boardingschoolmoney` | 1096-1098 | **household-visible** (funds): the boarding-school phone-call amounts come from the tuned ranges | verified |
| B6 §3 | P3 | `freshclothingresume` | 1099-1102 | **household-visible** (moodlet): Fresh Clothing resumes after a swim or shower | verified code; effect inferred |
| B6 §4 | P5 | `twinbellyshape` | 1103-1105 | **household-visible** (cosmetic): the belly shrinks per baby with twins and triplets | verified |
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
- **B6 §1**: sell a junk pile or a register with a Sim's interaction queued on it; the queue is purged ([1093]).
- **B6 §2**: [1096] after the world loads; check a need-money call's amount against the tuned range.
- **B6 §3**: Fresh Clothing time before a swim and after changing back.

