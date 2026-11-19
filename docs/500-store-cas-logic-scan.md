# 500: Store objects, CAS and CAST logic scan, plus horse routing (2026-11-19)

Static bug hunt over `Sims3StoreObjects.dll`, Create a Sim (`UI.dll` `Sims3.UI.CAS*`, `Sims3.Gameplay.CAS`, `OutfitUtils`) and Create a Style, plus a horse routing lead from game logs. It does not repeat docs/555, 557, 561, 579 or 604. Five fixes were built. All are off by default. The four in §2 have not run in game; `horselotentry` (§6) and `lotentrypet` get their first game test next.

## 1. Method

A Roslyn walker over the decompiled sources (as docs/604) looked for four patterns: an `as` cast that is dereferenced and never null-tested, the same test twice in one `&&`/`||` chain or if/else-if chain, a local whose value is never read, and a static collection that grows and is never cleared. Every candidate was then read in the decompiled code, and in IL where noted.

## 2. Verified bugs, fixed (`EaBugs500.cs`; pure decisions in `EaBugs500Core.cs`)

| switch | slots | bug [verified] | fix |
|---|---|---|---|
| `playpenflags` | 4017-4019 | `PlaypenClassic` keeps four busy marks: `mToddlerBeingPickedUp1/2` (TakeOut), `mPlayingWithOutsideSim` + `mOutsideSim` (PlayWithOutsideSim) and `mSyncing` (PlaySocial). Each is cleared only at the end of the normal path. There is no Cleanup and no finally; only the pen's `DoReset` clears them. Waits on these marks have no exit test: `BeingPickedUp` (the toddler cancels everything it queues), the `PlayingWithOutsideSim` and PlaySocial follower Sleep loops, and the adult's waits in TakeOut and PlayWithOutsideSim. | When one of the three Runs ends by an exception or reset, the mark that run set is cleared. A normal end runs EA's code. Reachability (a reset mid-run) is [inferred]. |
| `voodoobindwait` | 4020-4022 | `VoodooDoll+Bind.RunFromInventory` runs `while (sim.IsSleeping) { Sleep(0); Actor.LoopIdle(); }` with no cancel or destroyed test. The picker offers sleeping Sims. | Our hook runs the same wait first. It returns false on a `Canceled` exit reason or a destroyed sleeper, then EA's body runs. |
| `saunawoohoopreg` | 4023-4025 | `SaunaClassic+CuddleSeatedWooHooSauna+Definition.Test` tests `a.SimDescription.IsVisuallyPregnant` twice. IL confirms that it loads the closure's `a` both times and never the `target` argument. The sibling `StartSaunaSeatedCuddleA` tests both, and `CanGetRomantic` has no pregnancy test. So sauna WooHoo is offered on a visibly pregnant target. | A visibly pregnant target fails the test. |
| `lotentrypet` | 4026-4029 | `Lot.PlanRouteIntoLot` sets `BestRouteIntoLotLocationValidated` before it plans. A failed door plan resets `mBestRouteIntoLotLocation` to Invalid but leaves the flag set, and the flag is saved. A horse (no stairs, no water planning) or a pet the door refuses fails first, so every later Sim, household humans included, routes to the mailbox. EA clears the flag only on build/buy (`LotManager`) and lot import. | When a non-human's call validated the cache and left no spot, the flag is cleared again, so the next Sim decides. A human's result is vanilla. At world load, a validated cache with no spot is cleared once (slot 4029). This matches EA's own build/buy reset: the next plan redoes the door search. |

All four are exact EA bug fixes, so household Sims are included.

## 3. Not fixed

14 leads were left as harmless, not reachable or unknown:

- Store: a redundant SlotMachine LuckyCoin compare, `chairOut != null` twice in Blackjack, Poker and Roulette, and posture `as` casts in BabySwing and MagicCauldron [unknown reachability].
- CAS: dead `BaselineOperation` and `ClearStackToCurrentOperation`, an undo stack kept until the next CAS session, the `RequestSetDualBlendAmount` null-key match, and dead `OutfitUtils.TestOutfit` locals.
- Out of scope in SGS (same test twice): `Relationship.CalculateAttractionScore` (mummy test twice), `Opportunity.ClearChainHistory`, `CharacterImportOnGameLoad` (IslaParadiso twice) and `Collecting.Collected`.

No new CAS or CAST bug with a player effect was found. docs/545 and docs/557 already cover that area.

## 4. Cost

`lotentrypet`: until a human has planned into a lot, each later horse or pet plan redoes the failing door plan. This is bounded by planning, not by a loop. `horselotentry` (§6) removes most of these doomed horse plans.

## 5. Fire spread on outside tiles off the ground (lead, not fixed)

A Sim's ReactToFire failed 18 plans to a fire on an apartment lot's outside tile on level 2. `Fire.TestSpread` and `Fire.TestObjectForFire` place a fire on any solid `LotFloor`/`LotTerrain` tile next to a fire with no wall between, and on every tile a burning object covers; a burning door skips the wall test [verified, decompiled]. No test asks whether a Sim can reach the tile, so there is no exact EA bug to fix and nothing was changed. A logging probe for how such a fire starts stays local until it finds one.

## 6. Horse routing (`horselotentry`, `EaBugs500b.cs`)

Census of the horse route failures in three Appaloosa Plains runs (route failure trace lines). Horses were picked by horse-only interactions and plans without water options: three townie horses. They had 157 failed plans (3.4 s async) and 254 successful ones [measured]:

| plans | ms | interaction, result, plan kind, goal |
|---|---|---|
| 33 | 880 | GoHome, `NoDestinationSpecified` (10), Plan, The Painted Lady room 11 level 1 (inside the front door; 18 / 14 / 1 per run) |
| 10 | 237 | VisitCommunityLot, 10, Plan, gym room 0 level 0 |
| 7 | 213 | VisitCommunityLot, `DestinationPortalLocked` (18), Plan, library room 0 level 1 |
| 7 | 126 | SocialInteractionB, `NoPathToDestination` (16), Plan |
| 6 | 151 | BeingDismounted, 16, Plan |
| 6 | 78 | VisitLot, 18, Plan, residential room 0 level 1 (porches) |

No horse plan to a goal off the ground (level != 0) succeeded: 0 of 57. On the ground 226 of 312 succeeded [measured].

**Why the top row must fail [verified, decompiled].** `GoHome.Run` calls `Lot.PlanRouteIntoLot` with the horse as the actor. When the lot's entry cache holds a door spot (a human found it), the method plans a 1-3 m ring around that spot in the spot's room. When the cache is not validated yet, it plans a 2 m ring around the front door's first inside routing slot. Only after that plan fails does it plan to the mailbox (horse distances), then `PlanToLotEx`. The Painted Lady stands on a foundation, so the door spot is on level 1. The horse starts on level 0. A level change needs a portal that spans two levels. `Stairs.IsAllowedThrough` refuses puppies, kittens, horses and wild animals. `CommonDoor.IsAllowedThrough` refuses horses except at barn doors and at double or electronic gates with `CanLargeAnimalUse`. `ElevatorDoors` refuses horses. The planner asks script (`Sim.OnPortalPathPlan`) about doors, elevator doors, poles, stalls and jumps, so those answers are exact. Stairs, ladders and platforms are decided by the native species mask, and EA's managed answer is assumed to match it [inferred; the census agrees].

**Fix (`EaBugs500b.cs`, in the `lotentrypet` hook).** The door plan is skipped, and EA's fallback runs at once with the same calls (mailbox ring, then `PlanToLotEx`), when all of these hold:
- the actor is a horse, not a player Sim (`IsPlayerSim`), and plans its own route (`Route.Follower`);
- the horse stands on level 0, and the door spot is on another level;
- the lot has a mailbox, so the mailbox plan overwrites everything the failed plan left on the route;
- every portal object of the lot whose openings span two levels answers `DoorRestriction` for the horse in both directions. A portal with no openings, or with fewer than two rooms, blocks the skip.

When the cache was not yet validated, the skip leaves it validated with no spot, as EA's failure does. `lotentrypet`'s tail then sees the same state. Apartment lots (`HasVirtualResidentialSlots`) are left to EA.

Player horses are left out because the stairs part is inferred. With the switch off, the predicate still runs, and slot 4032 counts each prediction that EA's own plan proved wrong: the door spot was kept, or the route ended off the ground on the lot. It must stay 0.

- **Slots:** 4030 skipped, 4031 seen with the switch off, 4032 mispredicted, 4033 errors, 4034 / 4035 ms in `PlanRouteIntoLot` for these calls in the off / on arm. Saved per call = 4034/4031 - 4035/4030.
- **Expected:** the 33 GoHome door plans above (~27 ms of async plan each, ~0.9 s worker time over three runs), so only a small gain. The planning work moves off the worker, and the horse leaves about one plan sooner.
- **Left out:** the VisitLot / VisitCommunityLot porch plans (18, level 1). They come from other plan setups (outside door spots, not `PlanRouteIntoLot`'s inside spot), about 25 plans in three runs. The BeingDismounted and social failures are on the ground and not provably doomed. Pets were not censused: adult cats and dogs use stairs, so the same rule does not apply to them.

## 7. Tests

- Offline: `node tools/selftest/eabugs500/run.ts` passes (every decision against its truth table).
- Harness checks: `<switch>-check` in `tools/harness/repros.json` (pure decisions with controls, plus the members and swaps the hooks use).
- Proposed game repros, each with the switch off and then on:
  - **playpenflags:** reset the adult mid-TakeOut. The toddler then stays in BeingPickedUp, and a second TakeOut hangs.
  - **voodoobindwait:** Bind a sleeping Sim, then cancel. With the switch off, the binder stands until the Sim wakes.
  - **saunawoohoopreg:** a visibly pregnant Sim in a sauna should get no WooHoo option from a partner.
  - **lotentrypet:** in Appaloosa Plains, clear the flag on a home lot. A horse runs GoHome first, then a human runs GoHome. Off: the human goes to the mailbox. On: the human goes to the door.
  - **horselotentry:** Appaloosa Plains, a non-household horse runs GoHome to a foundation house. Off: slot 4031 rises and 4032 stays 0. On: slot 4030 rises and the horse leaves without the failed door plan.
