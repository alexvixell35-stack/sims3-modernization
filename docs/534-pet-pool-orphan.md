# 534. A pet the adoption pools drop stays alive with no household (`petpoolorphan`)

Issue #166 build 52. The adoption and horse pools drop pets when they refresh; a dropped pet without family links is
never disposed and goes into the save, where the next load throws it out. This doc describes the bug, the ts3fixes
hooks (switch `petpoolorphan`, off by default; counters 2062-2064, registry `fixes-2062`; ledger §938) and the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `PetPool.RandomRemovePet` (one random pet) and `PetPool.ClearAllPets` (all of them) call `RemovePet`, which takes the
  id out of the pool and the description out of `Household.PetHousehold` with `RemoveTemporary` **[verified]**. Their
  callers are the pools' refresh (`PetPoolManager.RefreshPool`, from the pet adoption refresh alarm, for the AdoptCat,
  AdoptDog, AdoptHorse and BuySellHorse pools), the equestrian center and `PetAdoption.PutPetBackToPool` **[verified in
  the core build notes]**.
- EA 1.67 disposes none of the dropped pets **[verified: the game's own `PetPool`]**. simler90's UPDATE242 core disposes
  the ones whose `Genealogy` has relations (with a MiniSim), and leaves the rest **[verified: the IL the core patch was
  written against]**.
- A dropped pet without family links stays a valid description with no household. The relationship table, the
  `AgingManager` and its aging alarms keep it, so it goes into the save. At the next load no household fixes it up
  (`SimDescription.PostLoadFixUp`), so it stays invalid: EA drops its relationships, and ErrorTrap's load scan logs
  "SimDescription Removed" for it **[verified live]**. The player's run 44: 42 "SimDescription Removed" at one load,
  37 of them adoption pool pets and horses dropped in the session before (25 held by the relationship table, 17 by
  aging alarms).

## 2. The fix

`mods/ts3fixes/src/PetPoolOrphan.cs`, hooks swapped over `PetPool.RandomRemovePet` and `PetPool.ClearAllPets`
(`EaFixes.cs` patch list). With the switch on, the hook notes the pool's pets, runs the original, and disposes each pet
that left the pool and is still valid, has no household, no family links and no Sim in the world:
`Dispose(clearInstantiatedBit: true, keepPartnerGenealogy: false, addMiniSim: false)`. With no family links a MiniSim
would keep nothing, and EA's own path for these pets (the drop at the next load) keeps none either. Pets with family
links are left as the core leaves them (EA keeps them, simler90 disposes them with a MiniSim). Counters [2062]
`petpoolorphan_disposed`, [2063] `petpoolorphan_inworld` (a Sim of the pet is in the world: left alone), [2064]
`petpoolorphan_threw` (Dispose threw: logged, the pet left as EA leaves it).

Shortcut: a pet with a Sim in the world is only counted. The core build put it on a list and disposed it once the Sim
was gone; that waits until [2063] shows it happens in play.

## 3. Test

Repro `pet-pool-orphan-check`, queue `tools/harness/queues/pet-pool-orphan.json`, save Baseline-BP, **simler90's
UPDATE242 core without the player's patches** (the player's core carries build 52). The agent check
`Ts3Fixes.Fixes.PetPoolOrphanCheck` makes a new adoption cat with EA's own creator (`PetPoolManager.CreatePetByType`,
private, by reflection; a new cat has no family links, which the check confirms), puts it alone in a new AdoptCat pool
with `AddPet` and calls `RandomRemovePet`. Off arm (`expect broken`): the cat is removed and stays valid with no
household; the check disposes it. On arm (`expect fixed`): removed and disposed; `petpoolorphan_disposed` reaches 1.
Early gate: that counter at 3 min.

Result (2026-11-05, pure UPDATE242 core, `docs/data/results/2026-11-05-alexvixell35-stack-pet-pool-orphan.md`): PASS.
Off arm: removed, still valid, no household. On arm: removed and disposed; `petpoolorphan_disposed` 1.

## 4. What is left

- A real pool refresh (the check builds its own pool) and a save and load after it: the "SimDescription Removed" count
  at the next load should drop by the number of pets disposed.
- Pets with a Sim in the world (counter 2063), as above.
