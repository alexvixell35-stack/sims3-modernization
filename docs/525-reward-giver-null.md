# 525. An opportunity reward object sent by a named Sim throws (`rewardgiver`)

Found while reading `ObjectOwnerComponent` holders for the dead-reference sweep (docs/205, issue #166). EA's
`GameObject.SetRewardGiver(sim)` cannot work: it adds the owner component through a path that dereferences every
argument, and it passes a null one. This doc describes the bug, the ts3fixes hook that replaces the method (switch
`rewardgiver`, off by default; counter 2050, registry `fixes-2050`; ledger §922) and the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `GameObject.SetRewardGiver(Sim sim)` is one line: `AddComponent<ObjectOwnerComponent>(new object[2] { sim, null })`
  **[verified]**. The two arguments are the component constructor's `owner` and `thief`
  (`ObjectOwnerComponent(GameObject o, GameObject owner, Sim thief)`): the reward is owned by the Sim who sent it and
  was not stolen.
- `AddComponentInternal` builds the constructor's parameter list with `list.ConvertAll(o => o.GetType())` over
  `[this, sim, null]` **[verified]**: `null.GetType()` throws `NullReferenceException` before any constructor is
  looked up. The method cannot add the component for any `sim`; a null `sim` would throw too.
- The only caller is `RewardsManager.GiveRewards`, case `RewardType.ObjectInMail` **[verified]**: the reward data
  names an object and a Sim; the object is created out of world, `SetRewardGiver(sim2)` runs when that Sim exists, then
  the object goes into the actor's home mailbox. The exception leaves `GiveRewards` through `Opportunity`'s
  completion (`RewardsManager.GiveRewards(arrayList, Actor, ...)` after `Cleanup(bSuccess: true)`), so the object is
  never mailed (and never destroyed: it stays out of world), the rewards after it in the list are not given, the
  completion text is not shown and the follow-up opportunity (`mCompletionTriggerOpportunity`) is not set
  **[verified in the code]**.
- No shipped opportunity uses it **[verified 2026-11-02]**: a scan of every resource of the installed `GameplayData.package`
  (5,727 XML entries; the 3,192 `OpportunitiesCompletion` rows with a `CompletionWinReward1` are all there, including the
  expansions') and of every expansion and stuff pack build package finds the string `ObjectInMail` nowhere. So the
  broken path is reached only by custom opportunities that use this reward type; the fix matters to those, not to EA's
  own opportunities.
- The sibling `SetStealActors(thief, victim)` passes both arguments non-null and works **[verified]**; the burglar
  path is unaffected.

## 2. The fix

`mods/ts3fixes/src/RewardGiver.cs`, hook `RewardGiverHook.GiverHook` swapped over `GameObject.SetRewardGiver`
(`EaFixes.cs` patch list). With the switch on and `sim` non-null it does what `AddComponentInternal` would have done
for this component: if the object already has an owner component, nothing (EA's path would only validate it);
otherwise `new ObjectOwnerComponent(this, sim, null)`, `OnCreation()` (the base no-op), appended to `mObjComponents`
(created when null). The version number stays 0: the component has no `[Version]` attribute, so EA's lookup would set
0 too. Off, or with a null `sim`, the original runs. Counter [2050] `rewardgiver_added`.

Not done: the leaked out-of-world object of a failed call before the fix is not cleaned up (it has no owner component
and no mailbox; a future sweep phase could find out-of-world objects with no holder).

## 3. Test

Repro `reward-giver-check`, queue `tools/harness/queues/reward-giver.json`, save Baseline-BP, the player's core mod on.
The agent check `Ts3Fixes.Fixes.RewardGiverCheck` makes a `SocialJigTwoPerson` out of world, calls
`SetRewardGiver(Sim.ActiveActor)` once inside a try and destroys the object. Off arm (`expect broken`): the call must
throw `NullReferenceException` and the object must have no owner component. On arm (`expect fixed`): no exception, an
owner component whose `mOwner` is that Sim; `rewardgiver_added` reaches 1. Early gate: the counter at 3 min.

Result (2026-11-02 03:33, `bp-reward-giver-check`, PASS in 9.1 min, al18, the player's core): off arm, `SetRewardGiver(Sim.ActiveActor)` on a `SocialJigTwoPerson` made out of world threw `NullReferenceException` and the jig had no owner component; on arm, the same call threw nothing and the jig had an `ObjectOwnerComponent` owned by that Sim; `rewardgiver_added` 1 (the job's gate). No script error in either arm.

## 4. What is left

- A real opportunity completion with an `ObjectInMail` reward through the fixed path (the check calls the method
  directly). Needs an opportunity with that reward type and its named Sim in the world; the harness has no step for it.
- The leaked out-of-world objects of earlier failures (above).
