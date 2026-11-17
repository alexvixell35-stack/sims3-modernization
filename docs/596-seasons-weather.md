# 596. Seasons weather: outfits, rain, snow (2026-11-16)

Static + decompiled read, then proven in game (s64, results at the end). Two switches built (default off): §1-2. The rest are leads or ruled
out (§3-5). Earlier Seasons work is not repeated here: docs/594 (initstacktrace, weatherstonelistener, weathermanagerthrow,
rainsheltercheap, pondclearscan, soakedstat), docs/571 (snowfightjoinwait, hauntedumbrella), docs/578 / s58 (puddlereach,
splashroof, mopunreachable, petpuddletile), docs/593 (lightning), docs/224 (outfitwaitpoll).

Log data (16 Bridgeport runs, phase-2026111[56]-*, Seasons on): no outfit, weather or snow stack in `hitch-attr.ts` output;
outfit lines are only the `OutfitWaitSavedPass` hourly pass and service-uniform one-shots. Weather/snow lines: SnowWaterFight
route fails (snowfightjoinwait), and 8 identical `MakeSnowman` `Replan sync FailedBecauseNoDestinationSpecified` fails for
one household Sim on one route (phase-20261115-2043): a route-fail lead for docs/595, not touched here.

## Built

Code: `mods/ts3fixes/src/SeasonsWeather596.cs`; checks print `verdict:` BROKEN (off) / FIXED (on), `controls ok`,
`members found`; live-path asserts registered with `LiveAdd` (`CheckVerdict.cs`); repros `<switch>-check`. Both act on household Sims (EA bug
fixes; user-directed picks keep EA's answer).

### §1 `outerwearautoloop` (fixes 2149-2153, `OuterwearAutoLoopCheck`) — bug, autonomy loop, household-visible
- [verified, decompiled] The Tests of `Sim.ChangeIntoOuterwear` (cold <= 32 F, outside), `Sim.ChangeOutOfOuterwear` and
  the autonomous branch of `ChangeToddlerClothes` (toddler outside, <= 55 F) do not test the guards that make Run a no-op.
  Run goes through `Sim.GetOutfitForClothingChange` (TemperatureTooCold / RemovingOuterwear), which answers "no change"
  when the occult or a buff disallows clothes changes, the Sim wears the skinny-dipping towel, is the Grim Reaper, or the
  player changed its clothes less than `kClothingChangeHoursPlayerChoiceIsSticky` (2) sim-hours ago (both reasons fall in
  that test's default branch). `ChangeToddlerClothes.StartBuildingOutfit` then returns `Return.True` (success, nothing
  done), the adult Runs return true; the motive is unchanged, so autonomy picks the same interaction again.
- Trigger: dress a Sim or toddler by hand (the toddler's own "Change Clothes" marks the change user-directed), then cold
  weather outside: for 2 sim-hours the parent / Sim keeps re-running the change. [inferred: the adult outerwear
  interactions advertise autonomously (EP8 ITUN not extracted here); the toddler one does, its Test has an autonomous branch]
- Fix: after EA's Test passed (EA's own RNG use in the trait test kept), an autonomous Test answers false when Run's guards
  certainly refuse. `IDisallowClothingChange` (depends on the interaction running at Run time) is not used, so "no" only
  where Run is a no-op. User-directed tests unchanged. Check `run`: rule + controls, our sticky test equals EA's private
  `TestClothingChangeReasonAgainstPlayerDirectedChange` on every live Sim, and each refusal is also EA's
  `GetOutfitForClothingChange(TemperatureTooCold)` answer (pure for that reason).
- How often: per autonomy pass that scores these interactions (cold weather, Sim outside, EA's Test passed).

### §2 `outerwearrestore` (fixes 2154-2156, `OuterwearRestoreCheck`) — bug, wrong outfit kept, household-visible
- [verified, decompiled] Every path into outerwear (GoingOutside, GoingToOutsideRoom, GoingOffLot, GoingToWork,
  GoingToSchool) stores the outfit to go back to in `Sim.OutfitCategoryToUseWhenSpinOutOfOuterwear`; RemovingOuterwear
  reads and clears it. TemperatureTooCold (`ChangeIntoOuterwear`) does not store it, so taking the coat off later gives
  Everyday, or a stale category left by an earlier trip whose outerwear came off another way (swim, bed, bath).
- Fix: when `ChangeIntoOuterwear.Run` really moved the Sim from another category into outerwear, that category is stored,
  as GoingOutside does. Toddlers are left alone (their field is never cleared by EA's carried path, so storing would
  leave it stale). Field is saved with the Sim (EA's own field and meaning); default off.
- How often: once per ChangeIntoOuterwear.

## §3 Outfit path read, no fix (ruled out or EA design)
- Door callback (`SimScriptAdaptor`, per room / door crossing): GoingOutside / GoingToOutsideRoom answer "no change" when
  already in outerwear (category == current), so no re-change; RoutingComponent removes outerwear only after a successful
  route that started outside and ended inside. Flip-flop at a door is EA's design (out: on, in: off), not a loop.
- No global trigger: nothing changes every Sim on a lot when weather or temperature changes; only door crossings,
  routes and the two autonomous interactions.
- `CanSwitchToOuterwear` tests the temperature second (after `SeasonsManager.Enabled`), so summer heat already skips the
  outerwear work. Age / species: `sOutfitAgeRulesOuterwear` is human toddler..elder and `CanSwitchToOuterwear` needs
  `IsHuman`, ghosts, ImmuneToCold and active roles are excluded: correct.
- Lead, not built: `SwitchToOutfitWithSpin(reason)` calls `GetCategoryAndIndexToUse` twice (once itself, once in the
  overload it calls). For a Sim with a transformation buff (`BuffManager.TransformBuffInst`) each call runs
  `GenerateTransformOutfit` (a `SimBuilder.CacheOutfit` build), so the build runs twice per change. Not exact to drop: the
  trait test between the two calls can change the category (Insane / Lunacy random), and the first call also creates a
  missing outerwear outfit. Rare (transformed Sims only).
- Lead: NPCs without outerwear get it built synchronously (`CreateRandomOuterwear`, a `SimBuilder` build) at their first
  cold door crossing; pre-building would change RNG order, not exact. No such hitch in the 16 runs.
- Lead: `LeavingRoom` reads `Household.Sims.Count` with no null test (homeless NPC in revealing sleepwear indoors in daytime
  would throw in the route callback). Very rare; not built.
- Toddler carried in/out (`DoCarriedToddlerSpinIfNeeded`): removing uses the child's stored category, which the carried
  path never sets, so the toddler goes to Everyday (None -> default case). Harmless; left.

## §4 The cache idea (user, 2026-11-16): not built, measured instead
Idea: cache each Sim's weather-outfit decision until season, weather / temperature band or in/out changes. Not exact:
`GetOutfitForClothingChange` also reads buffs, occult state, the current interaction, roles, the clock (player-sticky,
nighttime, party and work hours), the household size and the stored categories, and GoingOutside writes the stored
category; the trait test after it draws random numbers (Insane, Lunacy), so skipping a decision would shift the RNG.
Season-awareness already exists: `CanSwitchToOuterwear` returns on the temperature before any other work. The check's
`result` (`OuterwearAutoLoopCheck`) prints the cost of one GoingOutside decision on the active Sim (`timing:` field) to
confirm it is too small to matter (one decision per door crossing per Sim).

## §5 Rain and snow, read, no new fix
- `SeasonsManager.ConsiderCancellingSimInteractions` (rain start only): shelter natives before the cheap priority test,
  once per rain event: too rare to matter.
- Snow / ponds / leaves / umbrella / snowball fight / puddles: covered by the earlier builds listed above; DeepSnow,
  Snowman, SnowAngel listeners and VFX are removed in Dispose / Cleanup (docs/594 scanner queue). Lead for docs/595:
  the MakeSnowman Replan with no destination (above).

## In-game results (s64, Bridgeport, 2026-11-16, run phase-20261116-1426, switch off then on)

| Switch | Off | On | Measured |
|---|---|---|---|
| `outerwearautoloop` | BROKEN | FIXED | sticky answer same as EA 89/89; our refusals are refusals EA makes too (0/0); GoingOutside decision 2 us, our test 0-1 us |
| `outerwearrestore` | BROKEN | FIXED | controls (already in outerwear / no change / off) store nothing |

- [verified] The verdicts, controls and timings from the check lines in the run's `smallfix.txt`.
- [inferred] A live loop or a live restore in that run: the temperature was 67-68 F, so no outerwear change happened
  (counters 0).
