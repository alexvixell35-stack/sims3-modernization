# 870: Make Simbot missing in Bridgeport (`simbotworld`)

2026-11-22, static (EA code read with a decompiler and EA's interaction tuning read, local only), built + offline
selftest, default off, NOT RUN in game. Switch `simbotworld` (tuning `kSimbotWorld`), `mods/ts3fixes/src/SimbotWorld.cs`
+ `SimbotWorldCore.cs`.

## 1. The bug

Ambitions' "Make Mystery Invention" / "Make Simbot" option on the inventing workbench never appears in Bridgeport,
Late Night's Downtown world.

- [verified, tuning read] The interaction tuning (ITUN, `GameplayData.package`) for
  `InventionWorkbench+MakeFrankensim+Definition` on `Sims3.Gameplay.Objects.HobbiesSkills.Inventing.InventionWorkbench`
  (CodeVersion EP2) has `<World RestrictionType="Allow" Types="Base" Names="" />`. The workbench's other three tuned
  interactions (Invent, MakeInvention, ScrapInvention) have `RestrictionType="None"`.
- [verified, decompiled] `InteractionTuning.LoadAvailability` reads that line into the tuning's `Availability`:
  `WorldRestrictionType` Allow, `WorldRestrictionWorldTypes` a list holding `Base`, `WorldRestrictionWorldNames` an
  empty list (an empty `Names` parses to an empty list, not null).
- [verified, decompiled] `Autonomy.CheckAvailability` returns `Tuning_World` for an Allow tuning when neither the
  current world type (`GameUtils.GetCurrentWorldType`) nor the current world name (`GameUtils.GetCurrentWorld`) is in
  those lists (a Disallow tuning is refused when either is listed). So Make Simbot is refused in any world whose type is
  not `Base`: Downtown (Bridgeport), and also Future, Vacation and University worlds.
- [verified, decompiled] `InteractionObjectPair.AddTuning` takes the tuning from `AutonomyTuning.GetTuning` (walking the
  target's base types) and keeps that same object (also in its static `sTuningCache`); `AutonomyTuning` keys its table
  by the hashed interaction and object full names. A change to that object's lists reaches every workbench.
- [inferred] Bridgeport's `GetCurrentWorldType()` is `Downtown` (Late Night's world type; `WorldName.NewDowntownWorld`
  is Bridgeport). The check prints the type, so the first run settles it.
- [community-reported] ModTheSims "Inventor's Workbench fix for Bridgeport" (mod 437402) overrides exactly this ITUN;
  forum reports say the Simbot opportunity works after moving to another world.

## 2. EA's other world-restricted tunings

[verified, tuning read] World lines across EA's ITUNs: 3965 `None`, 11 `Allow [Base,Downtown,Future]`,
9 `Allow [Base,Downtown]`, 4 `Allow [Base]`. EA updated 20 Allow tunings for Late Night's Downtown type (11 of them later
for Future too) and left these four at Base only:

| Interaction | Object | Changed by simbotworld |
|-------------|--------|------------------------|
| `InventionWorkbench+MakeFrankensim+Definition` | InventionWorkbench | yes |
| `Computer+TravelToVacationWorld` | Computer | no |
| `Computer+TravelToVacationWorld` | HoloComputer | no |
| `CarOwnable+GoShopping` | (car) | no |

The other three are NOT changed. Leads only, unverified: vacation travel from a computer in Bridgeport and a car's
GoShopping may be refused there on purpose (or may be the same miss). Each needs its own reading before any fix.

## 3. The fix

No method swap: a data fix on the loaded tuning.

- At every world load (the ts3fixes `OnWorldLoadFinished` callback in `Instantiator`, next to `FortuneScamWorldLoad` and
  `ConsignmentSkillCurveWorldLoad`, which repair tuning data the same way) and in the check's `run`, `SimbotWorldSync`
  looks up the MakeFrankensim tuning with `AutonomyTuning.GetTuning`. `Fixes.Apply` cannot read tunings (docs/124
  §17.8: it sees C# defaults); by world load the ts3fixes tuning is set and EA's interaction tunings are loaded ([inferred], §5), so the
  sync runs there, not in `Apply` or `ApplyTuned` (which is for method swaps).
- Switch on and the tuning still in EA's shipped shape (Allow, types exactly `{Base}`, names empty: `SbwCore.EaShape`):
  `Downtown` and `Future` are added to its world types, EA's own pattern in its 11 later tunings. What was added and
  which tuning object it went into are kept.
- Switch off: exactly what was added is removed from that object (a type that was there before stays).
- Any other shape (another mod already changed it, e.g. the ModTheSims override): left alone and counted.
- Shortcut: one record. A tuning re-read by a store hot install after the widening (`AutonomyTuning
  .LoadNewlyAddedInteractionTuningRecrods` replaces the object) stays EA's until the switch goes off and on.
- Household: an EA bug fix, so household Sims are included. No Sim, object or save data changes; only the tuning in
  memory. Turning the switch off restores EA's list in the same session.

## 4. Counters, check, repro

Counters 2366-2369 (reserved by hand; the reserve tool finds no free block):

| Slot | Meaning |
|------|---------|
| 2366 | tunings widened |
| 2367 | tuning not in EA's shape, left alone |
| 2368 | widening undone (switch off) |
| 2369 | errors |

Check `Ts3Fixes.Fixes.SimbotWorldCheck`:

- `run` re-syncs the tuning to the switch, then prints the world name and type, the MakeFrankensim restriction, what
  we added, and whether EA's world test (the `CheckAvailability` world branch above, on the live `Availability`) refuses
  it here. In a Downtown or Future world: off `verdict: BROKEN` (refused), on `verdict: FIXED` (allowed). Control: the
  MakeInvention tuning (no world restriction) must be allowed, else `UNEXPECTED`. Any other world: `verdict: N/A`.
- `result` prints the counters.

Repro `simbot-world` (`tools/harness/repros.json`, save `Baseline-BP-Spring`): off run / result, on run / result, off
run again (2368). Offline selftest `tools/selftest/simbotworld` (shape test, widen / undo, EA's world test before and
after, controls).

## 5. Not verified

- Not run in game. The check reads EA's world test on the tuning, not the pie menu itself; MakeFrankensim's own
  `Definition.Test` and the Simbot opportunity were not read.
- Whether an Allow-tuned interaction is also filtered before `CheckAvailability` (e.g. at menu build) was not read.
- [inferred] EA's interaction tunings are loaded before the first world load finishes (the caller of
  `AutonomyTuning.LoadAllInteractions` was not read); the check's `run` re-syncs in any case.

## In game, first run (2026-11-22, pass-15 contributor build, repro `simbot-world`, Bridgeport)

- [verified, in game] `GameUtils.GetCurrentWorld()` is `NewDowntownWorld` and `GetCurrentWorldType()` is `Downtown` in Bridgeport.
- The live MakeFrankensim record was not EA's shape: `Disallow`, empty lists. The fix left it alone (2367 counted it).
  The test install has a mod package that overrides this ITUN with `<World RestrictionType="Disallow" Types="Vacation" />`,
  the same change as the MTS Bridgeport workbench fix. So the option already worked there, and the live test proved nothing.
- The check therefore also runs a simulated test when another mod changed the record. It sets EA's shipped shape on the live
  record, runs EA's world test, widens it as the fix does when on, and puts the record's own values back in a `finally`. All
  of this happens inside one call with no yield, so no other code sees the simulated shape. Off: `verdict: BROKEN`. On:
  `verdict: FIXED`. The verdict on the live record stays as before when the record is EA's own.

## Import 2026-11-23 (maintainer)

PR #653 by Dalexlol, counters 2366-2369 kept. `simbotworld` is **ON by default** at import: repro `simbot-world` (pass 15, Bridgeport) in game, off `verdict: BROKEN` / on `verdict: FIXED`, control allowed, errors 0 (see the first-run section above). Household: an EA bug fix on a tuning, no Sim, object or save data changes. Repro `simbot-world` added to repros.json.
