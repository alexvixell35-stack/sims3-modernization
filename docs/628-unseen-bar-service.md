# 628. Unseen bar service: instant orders for NPCs nobody can see

2026-11-17, static, package r81-svc, NOT RUN. Switches `unseenservice` and `unseenservice_offscreen` (both default off), `mods/ts3fixes/src/UnseenService88.cs`, counters 3600-3611 (3612-3619 spare), repro `unseenservice-check`.

## 1. Evidence

`node tools/harness/wait-report.ts` on phase-20261117-1237 and phase-20261117-1155 (syncwait lines in ts3native.log):

| row | sim-min | count |
|---|---|---|
| MakeConversation server waits for customer (Committed, limit float.MaxValue) | 567 | 34 |
| OrderDrink customer waits (Completed, 78-120 min limit) | 346 | 30 |
| OrderFood customer waits | 99 | 16 |
| OrderConversation customer waits (Routed) | 63 | 36 |

All six interaction names belong to `BarAdvanced` (Late Night bars: BarProfessional, PoolBar). `Bar.MakeDrink` (home bar), `BaristaBar.MakeDrink`, `FoodTruckBase.OrderFood` and `IndustrialOven.OrderFood` are other objects with their own paths and do not show in the census.

## 2. Root cause (verified, ilspycmd)

- `BarAdvanced::EnqueueInteractionAndWait`: customers queue in `mInteractions`; only the head gets a bartender instance (`CreateBartenderInteractionInstance`, linked, added to the bartender's queue). Everyone behind waits for the whole service.
- `BarInteraction::Run` (customer): waits Routed, plays the order (`RunOrderBehavior`), sets Committed, waits Completed while the bartender works, then `RunBehavior`.
- `BartenderInteraction::Run` (server): routes to the customer, waits Committed with `float.MaxValue`, routes to the bar, `RunMakeBehavior` (MakeDrink: `DoTimedLoop(GetTimeToMakeDrink)`, 15 sim-min at skill 0), routes back, `PutDownCarriedObject` into the customer's `BarDestination` slot.
- `OrderConversation::RunOrderBehavior` is `DoTimedLoop(5..10)` inside commodity updates; the bartender's float.MaxValue wait covers it plus both exit animations (docs/578 §1 barorderlog).

Nothing here is wrong for a Sim the player watches. For two NPCs off camera it is time spent in animations and waits that only hold the queue.

## 3. Fix

At the queue head (our `EnqueueInteractionAndWait` hook is EA's loop plus one branch), an in-scope pair is served at once with EA's own end steps, then `Run` returns the order's `RunBehavior` answer (`BarInteraction.Run` hook):

- conversation: customer `BeginCommodityUpdates` / `EndCommodityUpdates(true)` (RunOrderBehavior without the loop), the bartender's MakeConversation instance the same (its RunMakeBehavior is `return true`), then `OrderConversation.RunBehavior` (random social buff, LTR +3/-3).
- one drink: customer `RunOrderBehavior` (EA hands the description to MakeDrink), then MakeDrink's non-animation steps: `SkillManager.AddElement(Bartending)`, the autonomous unique-drink pick, `UseIngredients` + `CreateDrink` (as `CreateDrinksAndSetActors`), `Glass.OnPoured` (the anim event 600 handler; EA's own no-animation precedent `BarAdvanced::CreateRoundsOfDrinks` calls it directly), then `PutDownCarriedObject`'s steps (`ShouldMakeFlamingDrink` -> `SetOnFire`, `ParentToSlot` to the destination), then `OrderDrink.RunBehavior`: payment / tab, `Bartending.OnServedSim` + tip (the server's skill and job credit), `kSimOrderedDrinkFromBar`, `PickUpWithoutRouting` + `PushDrinkAsContinuation` (the drink in hand).
- one plate: customer `RunOrderBehavior`, `MakeFood.CreateFood` + `StartAutoDestroyAlarm`, `ParentToSlot`, then `OrderFood.RunBehavior` (pay, `kOrderedFood`, Eat continuation).

Commodity updates over the skipped loops: both Sims must be low LOD (`Autonomy.IsSufficientlyOnScreenForHighLODSimulation` false). [verified, SimUpdate::Simulate] a low-LOD Sim skips `Autonomy.Update`, the only caller of `UpdateMotives` and `UpdateSkills`, so the time-rated part of these commodity changes is zero in EA's own path too; the begin/end parts run through EA's methods on the same instances. Neither Sim moves: the customer has already routed to the bar (EA), the bartender stays in IdleTend.

Preconditions (else EA's hand-off, counter 3607): server in IdleTend with nothing queued behind, both right hands empty, destination slot empty, one glass or one serving, `Bartending.CanMakeDrink` (MakeDrink.RunResponseBehavior's test).

## 4. Scope

- Tier 1 `unseenservice`: customer and server both `UfWhy` 0 and both low LOD.
- Tier 2 `unseenservice_offscreen` (needs tier 1 on): no player or selected Sim; customer, server, bar and destination object not `World.IsObjectOnScreen` (re-checked just before the drink or plate is made), both low LOD; the 80 m FarFromViewers rule is not required. Resort workers are always high LOD in EA and stay on EA's path.
- Both: community lot only, never a venue the active household owns (`RealEstateManager.FindProperty`), never a household or selected Sim (`IsPlayerSim`, `IsSelectable`, `PlumbBob.SelectedActor`).

## 5. Skipped and why

- Rounds, trays, group drinks (`OrderRound`, `mNumberOfDrinks > 1`): the customer's end is `WaitWithOrderedDrink` on a tray the bartender carries; EA's no-animation tray path (`CreateRoundsOfDrinks`) places trays by a surface search, not into the customer's slot, so the end state would differ.
- Group food: the result is a grab-a-serving push per grouping participant, who are other Sims with their own scope.
- `OrderDrinkOnResidentalLot`, `OrderFreeDrink`, `OrderPizza`, `OrderBirthdayCake`, `OrderNectar`: other payment and role paths (residential lots are household territory).
- `PracticeAsCustomer` / `Practice`: the outcome is the practice skill loop itself, which needs time.

## 6. Check

`Ts3Fixes.Fixes.UnseenServiceCheck run|result`: pure scope decision (off: EA's linked wait, BROKEN; on: served at once, FIXED) with controls (player, residential, owned venue, high LOD, seen, tier 2 needs its own switch, preconditions), plus a live probe: on a world bar with a bartender each order type maps to EA's server type through `CreateBartenderInteractionInstance` (nothing runs; mismatches count in 3611), and a census of Sims ordering and serving now. In a live run, 3600-3605 grow with the switch on while NPCs order at bars off camera, and the wait-report bar rows should shrink.

`Ts3Fixes.Fixes.UnseenServiceOffscreenCheck run|result` (2026-11-17, repro `unseenservice_offscreen-check`): s89 (phase-20261117-1435) toggled only `unseenservice_offscreen` and ran UnseenServiceCheck, which reports the main tier, so both arms read FIXED. This Check tests the tier-2 decision itself (an off-screen order near viewers, main switch taken as on): sub-key off = EA's wait, BROKEN; on = tier 2, FIXED. Controls: unseen stays tier 1, on screen / player / main switch off keep EA.

## 7. Counters

3600-3602 tier 1 conversation / drink / food served, 3603-3605 tier 2 the same, 3606 out of scope, 3607 precondition kept EA's hand-off, 3608 tier 2 refused at the on-screen re-check, 3609 RunBehavior false after a served order (payment refused), 3610 errors (falls back to EA's hand-off), 3611 Check live mismatches (must stay 0).
