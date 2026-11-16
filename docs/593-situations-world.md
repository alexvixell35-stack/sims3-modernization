# 593: Usage sweep 5: situations, services and world systems

> Posted upstream with the switches listed under "In-game results" at the end. Sections on other leads or switches are kept as research notes; those switches are not part of this change.


## Batch 13 (built 2026-11-16, all default off; proven in game, results at the end)

EA bug fixes as EA intended, household included. ts3fixes `mods/ts3fixes/src/Batch593Logic.cs`, checks `Batch593Checks.cs` (pure decision + CkLive live path), repros `<switch>-check`. Slots 3196-3240 (3 each). All household-visible except firefightercount, repairmanskill and roundupreturn (service NPCs).

| § | lead | switch | slots | fix |
|---|---|---|---|---|
| 1 | V2 | `nullsituation` | 3196-3198 | a service whose situation could not be made no longer throws in AddAssignment and stops every service for the session |
| 2 | L1 | `lightningweights` | 3199-3201 | lightning can strike Sims and objects again after a strike that found none |
| 3 | G1 | `plantdormantflip` | 3202-3204 | a fruit tree with frost and leaf cover disagreeing stays dormant instead of flipping every evaluation |
| 4 | G2 | `plantdormanttime` | 3205-3207 | a plant coming out of dormancy does not lose a whole dormant span of water at once |
| 5 | H2, H5 | `harvestextras` | 3208-3210 | Gatherer and Science extra harvestables reach the harvester's inventory, and mushroom collecting credits the harvester |
| 6 | H3 | `shroomslots` | 3211-3213 | mushrooms fill their slots in order and get their harvest timeout instead of skipping slots and throwing |
| 7 | H4 | `harvestregion` | 3214-3216 | a poorly grown plant's harvest gets no quality bonus (EA's bottom growth region) |
| 8 | B2b | `wellstealcooldown` | 3217-3219 | coins cannot be stolen from a wishing well right after a wish (EA's cooldown) |
| 9 | O1 | `oppfailcharge` | 3220-3222 | a failed opportunity's deleted-object penalty is charged once, not twice |
| 10 | T1 | `tempforecast` | 3223-3225 | the temperature forecast range follows the real day curve instead of its mirror |
| 11 | F1 | `mountfishcost` | 3226-3228 | mounting a fish charges that fish's cost, not the last fish tested |
| 12 | F2 | ~~`fishscoremounted`~~ withdrawn after s58 (false positive: `Contains("Mounted")` is case-sensitive, so "Unmounted" never matches; Arg0 is never null) | none (withdrawn) | was: an Unmounted fish photo subject no longer scores mounted fish, and Any matches once |
| 13 | V8 | `firefightercount` | 3229-3231 | no more firefighters come than the pool's maximum |
| 14 | V9 | `repairmanskill` | 3232-3234 | a repairman's handiness is the rolled level, not the roll added on every visit |
| 15 | V3 | `roundupreturn` | 3235-3237 | a curfew round-up with no active Sim leaves instead of throwing in Init |
| 16 | V1 | `butlerpayalarm` | posted with docs/582 (`ButlerPayAlarm.cs`, 2926-2928) | a butler whose job ended stops charging the household every week |
| 17 | H1 | `petharvestvfx` | 3238-3240 | a pet eating from a harvest plant stops its eating effect |

`butlerpayalarm` runs inside the `serviceshowalarm` swap of ServiceSituation.CleanUp (one swap per method); its Check also asserts that swap.

Not built, with reason:

- G3 (shared plant save key): false positive. PropertyStreamWriter XORs every key with a per-type hash (Int32 `0x415642B`, Float `0x4EDCD7A9`), so mFertilizerDaysLeft and mWaterLevel under 101511562 are separate entries (same as docs/590 B7).
- B2a: inside BlackjackTable's long Run loop: IL patch candidate.
- V4, V5, V7, V10, V11, V12, V15: inferred. V6: no household effect, the child state has no CleanUp of its own. V13: EA does not define when eating "has started". V14: Init restores autonomy on purpose; the CleanUp intent is unclear.
- Minor list and §4 speedup candidates: not built.

## In-game results (Bridgeport combined soaks, each switch off then on in one load)

Switches of this doc that are posted in this change, with the runs where the check read off BROKEN and on FIXED (`smallfix.txt` of each run).

| Switch | Off | On | Runs off BROKEN / on FIXED | Runs (phase-…) |
|---|---|---|---|---|
| `firefightercount` | BROKEN | FIXED | 1 | 20261116-0234 |
| `harvestextras` | BROKEN | FIXED | 1 | 20261116-0234 |
| `harvestregion` | BROKEN | FIXED | 1 | 20261116-0234 |
| `lightningweights` | BROKEN | FIXED | 1 | 20261116-0234 |
| `mountfishcost` | BROKEN | FIXED | 1 | 20261116-0234 |
| `nullsituation` | BROKEN | FIXED | 1 | 20261116-0234 |
| `oppfailcharge` | BROKEN | FIXED | 1 | 20261116-0234 |
| `petharvestvfx` | BROKEN | FIXED | 1 | 20261116-0234 |
| `plantdormantflip` | BROKEN | FIXED | 1 | 20261116-0234 |
| `plantdormanttime` | BROKEN | FIXED | 1 | 20261116-0234 |
| `repairmanskill` | BROKEN | FIXED | 1 | 20261116-0234 |
| `roundupreturn` | BROKEN | FIXED | 1 | 20261116-0234 |
| `shroomslots` | BROKEN | FIXED | 1 | 20261116-0234 |
| `tempforecast` | BROKEN | FIXED | 1 | 20261116-0505 |
| `wellstealcooldown` | BROKEN | FIXED | 1 | 20261116-0234 |

- [verified] The verdicts, controls ok and members found in the runs listed.
- [inferred] How often each case happens in normal play: the checks call the same decision functions as the hooks.
