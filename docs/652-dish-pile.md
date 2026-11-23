# 652 Dirty dishes piling up on one Isla Paradiso lot (dishpilecap, dish_census)

2026-11-19. Static analysis of the IP night runs (phase-20261118-0045 .. 20261119-1345, `unseenservice` on) and the
decompiled EA code. Built, default off, NOT RUN. Package tag `dishpile`. Counter slots 4130-4139, `kCounterSlots` 4140.

## 1. Symptom [measured]

Leak census `broadcasters` at the last snapshot (leak-6):

| run | Plate+Bowl | max on one lot | max minus dishes |
|---|---|---|---|
| 20261117-2345 (s101) | 0 | 27 | 27 |
| 20261118-0045 | 165 | 189 | 24 |
| 20261118-0225 | 116 | 137 | 21 |
| 20261118-0404 | 179 | 203 | 24 |
| 20261118-2356 | 133 | 162 | 29 |
| 20261119-0857 | 125 | 154 | 29 |
| 20261119-1039 | 6 | 29 | 23 |
| 20261119-1345 | 114 | 136 | 22 |

Each dirty serving has two lot broadcasters (`ServingContainer.CreateDirtyReactionBroadcaster`: dirty + dirty-pet), set when
the last food unit is eaten or the food spoils. The pile is real `Plate` / `Bowl` classes, not `PlatePaper` (the class a
serving gets on a community lot through `CookingProcess.CreateSingleServingOfFood`). In 0404 it grows 0 -> 34 by the party
snapshot, 79 at routine, 179 after the idle phase (Thu 9:48 PM -> Wed 2:22 PM, ~6.7 sim-days).

## 2. Which lot [inferred, strong]

- **Every dish is on the lot that is already the biggest without dishes.** "max on one lot minus dishes" stays 21-29 in
  every run, the same as the biggest lot of the runs without a pile (27, 29). The pile adds to one lot only.
- **That lot is the active household's home (Colonial Casa, lot 0x8F300533282EBE0, the Collins).** The other dirty
  broadcasters in each census are `BathtubValue`, `ToiletCheap`, `SinkPedestalModerate` and `SinkCounterKitchen` (12-16 in
  every run). In the `routefail:` lines those exact types are the Collins' fixtures on that lot (ToiletCheap 326,
  SinkPedestalModerate 302, BathtubValue 102, SinkCounterKitchen 60 lines on 0x8F30...; elsewhere 7-11).
- **EA's nightly cleanup removes dirty dishes on every other lot** [verified, decompiled]: `LotManager.CleanUpLotsAndInventories`
  (3 AM, "Global venue cleanup alarm"; ours runs the same steps spread over ticks with `easpreadlots`, `EaSpread.cs`)
  calls `Lot.MagicallyCleanUp` on each lot with `(!CanSimTreatAsHome(ActiveActor) && !IsControllable) || IsBaseCampLotType`.
  `Lot.DoesObjectNeedCleaning` picks each `IThrowAwayable` whose `ShouldBeThrownAway` holds (`PreparedFood`: empty or spoiled)
  unless it is `InUse`, held by a Sim or `IsBlocked`, and `ThrowAwayImmediately` destroys it; dirty fixtures are
  `ForceClean`ed. A lot it cleans cannot hold ~90 dishes 11 sim-hours after 3 AM, and dirty toilets and baths would not
  last from night to night. Only the active Sim's home, a venue the household owns, or a lot with an active-household job
  (`Occupation.DoesLotHaveAnyActiveJobs`, the whole lot skipped) keeps them.

Not confirmed in game: `dish_census` (§5) and the leak census `max_lot` / `active_home` fields name the lot directly.

## 3. Why nobody cleans them [measured + verified]

On the home lot only the household cleans (no maid hired). The Collins are not played for ~7 sim-days in the night
runs, and their cleanup routes fail:

| interaction, target | result | lines (all IP runs) |
|---|---|---|
| ServingContainer_CleanUp, Plate / Bowl / PlateServing | FailedBecauseDestinationPortalLocked | 56 + 6 + 5 |
| WashDishes, SinkPedestalModerate | NoDestinationSpecified / NoPath / PortalLocked | 72 / 23 / 8 |
| WashDishes, SinkCounterKitchen | NoDestinationSpecified | 37 |
| PutAwayLeftovers, PlateServing | NoDestinationSpecified | 11 |

The CleanUp goals (931.4,58,1202.5 and 931.4,58,1208.5) are the upstairs bathroom pedestal sinks: `ServingContainer.
GetBestObjectToWashDishes` takes the closest routable sink in the room, then the closest on the lot, and a bathroom whose
door is privacy-locked by a bathing or toilet Sim answers PortalLocked. That is vanilla behaviour for the household.

## 4. What it is not [verified]

- `unseenservice` (UnseenService88.cs): its bar food comes from `BarAdvanced.MakeFood.CreateFood` and gets EA's
  `StartAutoDestroyAlarm` (45 sim-min, then `OnAutoDestroy` fades it out when unused); group food is skipped. No pile.
- The 385 `ServingContainerGroup_GrabAServing` route fails on "Plate": NPCs at the resort buffets (La Costa Verde,
  Sparkling Sands, Hobart's Hideaway). In 0404 the 76 lines are 46 `routefail job` lines, almost all ts3native
  `cut (UnseenFallback, mask 2)` deadline cuts, and 30 `routefail retry` lines, 29 of them `succeeded`. `RunBodyForGrabAServing` returns before
  `CreateSingleServingOfFood` and `AddToUseList` when its route fails, so a cut leaves no serving, use or reservation. Resort
  buffet servings are `PlatePaper` with `EatResortFoodDefinition` (destroyed after the meal), not the pile's classes.
- `cleanflag` (fire pit and picnic basket flags), `eatpostonce` (posts only), `buffetemptypick` (table pick only): no dish
  is created, moved or kept.

## 5. Changes

- **`dish_census`** (ts3agent, `DishCensusHandlers.cs`, read only): per lot, servings dirty / spoiled / fresh / held / used
  or routed / on a buffet / in an inventory / EA auto-destroy pending / what `DoesObjectNeedCleaning` would take; what EA's
  3 AM cleanup does with the lot (cleaned, or skipped as home / venue / active-job lot); sinks, dishwashers, Sims and
  household Sims on it; types. A history of (sim time, world dirty, biggest lot) is kept between calls.
- **Leak census** `broadcasters`: new `max_lot` (name, id, "(active home)") and `active_home` (broadcasters on the active
  household's home), so the next night run places the pile without another tool.
- **`dishpilecap`** (ts3fixes, `DishPileCap.cs` + pure `DishPileCapCore.cs`, `kDishPileCap` off, `kDishPileCapPerLot` 10,
  `kDishPileCapBudget` 20): a safety net for the lots EA's cleanup leaves between nights or skips for an active job. It does
  not touch the Colonial Casa pile, by the household rule. Hourly: dirty servings per lot; on a lot that is not the active
  home or an owned venue, has no household / selected / unknown-scope Sim (`IsPlayerSim`) and is not in view (`UfLotSeen`),
  the dirty dishes above the cap that are unused, unheld, not routed to, not on a buffet / picnic blanket / bake sale
  table, without EA's auto-destroy alarm and outside `FarFromViewers` get EA's nightly step early: `Lot.DoesObjectNeedCleaning`
  then `ThrowAwayImmediately`. Fresh food is never touched. Switch off: counts only, and once a sim-day logs the biggest
  pile (the active home named as such).
  Counters 4130 removed, 4131 lots capped, 4132 over-cap lots left alone (household tie or view), 4133 dishes skipped
  (used, held, EA says no), 4134 near a viewer, 4135 errors, 4136 passes, 4137 dirty on the biggest lot, 4138 dirty in the
  world, 4139 lots over the cap (last three per pass, any switch state). Log `dishpilecap: <lot> 0x<id> (<kind>, sinks N)
  D dirty (...), removed R (cap C)` / `dishpilecap: off, biggest pile ...`, 10 a minute, 300 a session.
- Self-check `node tools/selftest/dishpilecap/run.ts` (lot gate, dish verdicts, quota, tunable bounds).

Not built: the "remove a dish after a Sim's second failed CleanUp route" fallback. The pile's failed routes are the
household's on its own home, which no fix may touch; on other lots EA's nightly cleanup already removes what a failed
route left, and `dishpilecap` covers the time between.

### How it composes with objfailmemo (feat/objfailmemo, docs/650)

`objfailmemo` keeps a (Sim, object) no-path memo so an NPC does not pick the same unreachable object again; it covers
serving picks (`GrabAServing`, `CleanUp`) generically. `dishpilecap` adds nothing to autonomy: it only removes dirty
dishes, so a memo entry for a removed dish simply goes stale. No dish-specific memo was added.

## 6. Open

- Run one IP night with `dish_census` per snapshot to confirm the lot (§2) and read the Colonial Casa kitchen sink
  `NoDestinationSpecified` (37 lines: something in front of the sink, or its slots taken) and the PortalLocked bathroom
  picks. Both are household-side; a fix would have to be an EA bug fix that leaves controlled Sims vanilla.
- The night-run harness leaves an 8-Sim household unplayed for ~7 sim-days. In the leak census the home lot's dishes are a
  harness effect (a player cleans), so `max_on_one_lot` should be read together with `active_home`.
