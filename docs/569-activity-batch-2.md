# 569: Activity batch 2 (gym training, Bistro meals, consignment, inventing, venues)

**In-game status:** `trainboost`, `eatoutsidecount`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-15, static (decompiled EA scripts). This is a deeper pass on the areas docs/568 §4 only surface-scanned:
AthleticGameObject WorkOut / TrainSim / QueueUpTrainSim, InventionWorkbench Invent / MakeInvention scrap handling,
ConsignmentRegister / BotShopRegister, Restaurant / EatHere / RestaurantInteraction / EatOutside / EatOutsideWith /
GetDrinks, CelebrityManager discounts, VelvetRopes (VIP access, bribes, temp lists). Built, proven in game (status above). Both switches are off by
default; EA bug fixes only, using EA's own methods.

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `trainboost` | 2566-2567 | `Ts3Fixes.Fixes.TrainBoostCheck` | `train-boost` |
| 2 | `eatoutsidecount` | 2568-2569 | `Ts3Fixes.Fixes.EatOutsideCountCheck` | `eat-outside-count` |

The checks evaluate the fix decisions with controls (off prints BROKEN, on FIXED); neither changes the world. Slots
2564-2565 were reserved for a consignment fix and released (§3).

## 1. Interrupted Train Sim leaves the trainee boosted for good (`trainboost`, TrainBoost.cs)

[verified, decompiled] `AthleticGameObject.TrainSim.Run` raises two values on the trainee and lowers them only after
DoLoop returns, with no try/finally and nothing in Cleanup:
- `SkillManager.AddToSkillGainModifier(Athletic, kAthleticSkillMultiplierOfTrainedSim - 1)`
- `SimDescription.UpdateShapeDeltaMultiplier(kBodyShapeMultiplierOfTrainedSim - 1)`

A trainer reset mid-session unwinds Run past the lowering. Both values are saved with the trainee:
`mSkillModifiers` and `mShapeDeltaMultiplier` (written in SimDescription persistence). The trainee then gains
Athletic and body shape twice as fast for good, +1 per further interrupted session. Player cancels and normal ends
return from DoLoop and are fine.

Fix: Run runs inside try/finally. The trainee's two values are read first. Only when the body unwinds, and only for a
value now exactly EA's added amount above the start, EA's `SubtractFromSkillGainModifier` /
`UpdateShapeDeltaMultiplier(-amount)` undo it. Not covered: the store BalletBarre `TrainSimBB` has its own Run.

## 2. Bistro "Eat Outside" follower counted twice (`eatoutsidecount`, EatOutsideCount.cs)

[verified, decompiled] In `EatOutside.Run` the leader waits while `mNumFollowersLeft > 0`. It then sends Finished to
every follower's `EatOutsideWith` and seats each Sim that holds food (`SitAndEatAtTable` pushes the sit/eat
continuations and charges the meal).

A follower calls `NotifyFollowerDone` (count - 1) as it leaves the bistro with food, then idles in DoLoop. If it is
then cancelled or displaced, its Cleanup (`!mbSuccess`) calls `NotifyFollowerCanceled`, which removes it and
decrements again. With two or more followers this releases the leader while another follower is still inside. That
follower gets Finished on its way out, has no food in hand when the group is seated, and is never seated or charged.
It wanders with its plate.

Fix: `NotifyFollowerDone` notes the (meal, Sim). `NotifyFollowerCanceled` for a noted Sim runs EA's body (removes it)
and puts the second decrement back. The noted list is pruned above 256 entries.

## 3. Read, no bug / left

- Consignment, `OnPreReturnHome` (ConsignmentRegister and BotShopRegister): on travel return, consigned items go back
  to the Sim / family inventory without `EnableInteractions()`. Every other return path (cancel, expiry, purchase)
  calls it. Whether the disabled flag (`InteractionsDisabledComponent`) survives the travel inventory transfer was
  not traced [inferred], so this is not fixed. It also dereferences `simDescription.Household` unchecked.
  ConsignmentRegister leaves `sConsignedObjects` null after a return, and `OnConsignedItem` / `CancelConsignedItems`
  use it without a null check. BotShopRegister.Startup re-creates it; ConsignmentRegister does not. The home world's
  PersistableStatic load probably restores it [inferred].
- WorkOut: the stages alarm self-removes, and Pumped/jig/outfit cleanup is in Cleanup. `TrainSim.Run` dereferences
  `Trainee` when no trainee is present (the loop's lot check), which ends in a script error and reset. This is rare,
  and EA's Test requires a trainee.
- Inventing: scrap is reserved and unreserved in Cleanup, and consumed only after the route. No leak found.
- `EatOutside.SitAndEatAtTables` returns false when the last Sim exactly fills the last free table, which skips the
  "ate at restaurant" events. `GetTablesToEatAt` never decrements numSims, so this needs every free table filled.
  This is a minor leftover.
- Restaurant followers: a vampire follower gets `Meal` instead of `Sated`. This is cosmetic.
- CelebrityManager freebies and discounts and VelvetRopes access checks were read: temp lists clear nightly, and
  bribes are already covered by docs/561 §7. No bug found.
- Library / bookstore reading: covered by docs/251 (bookhand) and BookCarry. Nothing new found.
