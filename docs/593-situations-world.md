

## Batch 13 (built 2026-11-16, all default off, NOT RUN)

EA bug fixes as EA intended, household included. ts3fixes `mods/ts3fixes/src/Batch593Logic.cs`, checks `Batch13Checks.cs` (pure decision + CkLive live path), repros `<switch>-check`. Slots 3196-3240 (`fixes-3196`, 3 each). All household-visible except firefightercount, repairmanskill and roundupreturn (service NPCs).

| § | lead | switch | slots | fix |
|---|---|---|---|---|
| 1 | V2 | `nullsituation` | 1862-1864 | a service whose situation could not be made no longer throws in AddAssignment and stops every service for the session |
| 2 | L1 | `lightningweights` | 1865-1867 | lightning can strike Sims and objects again after a strike that found none |
| 3 | G1 | `plantdormantflip` | 1868-1870 | a fruit tree with frost and leaf cover disagreeing stays dormant instead of flipping every evaluation |
| 4 | G2 | `plantdormanttime` | 1871-1873 | a plant coming out of dormancy does not lose a whole dormant span of water at once |
| 5 | H2, H5 | `harvestextras` | 1874-1876 | Gatherer and Science extra harvestables reach the harvester's inventory, and mushroom collecting credits the harvester |
| 6 | H3 | `shroomslots` | 1877-1879 | mushrooms fill their slots in order and get their harvest timeout instead of skipping slots and throwing |
| 7 | H4 | `harvestregion` | 1880-1882 | a poorly grown plant's harvest gets no quality bonus (EA's bottom growth region) |
| 8 | B2b | `wellstealcooldown` | 1883-1885 | coins cannot be stolen from a wishing well right after a wish (EA's cooldown) |
| 9 | O1 | `oppfailcharge` | 1886-1888 | a failed opportunity's deleted-object penalty is charged once, not twice |
| 10 | T1 | `tempforecast` | 1889-1891 | the temperature forecast range follows the real day curve instead of its mirror |
| 11 | F1 | `mountfishcost` | 1892-1894 | mounting a fish charges that fish's cost, not the last fish tested |
| 12 | F2 | ~~`fishscoremounted`~~ withdrawn after s58 (false positive: `Contains("Mounted")` is case-sensitive, so "Unmounted" never matches; Arg0 is never null) | 1895-1897 unused | was: an Unmounted fish photo subject no longer scores mounted fish, and Any matches once |
| 13 | V8 | `firefightercount` | 1898-1900 | no more firefighters come than the pool's maximum |
| 14 | V9 | `repairmanskill` | 1901-1903 | a repairman's handiness is the rolled level, not the roll added on every visit |
| 15 | V3 | `roundupreturn` | 1904-1906 | a curfew round-up with no active Sim leaves instead of throwing in Init |
| 16 | V1 | `butlerpayalarm` | 1907-1909 | a butler whose job ended stops charging the household every week |
| 17 | H1 | `petharvestvfx` | 1910-1912 | a pet eating from a harvest plant stops its eating effect |

`butlerpayalarm` runs inside the `serviceshowalarm` swap of ServiceSituation.CleanUp (one swap per method); its Check also asserts that swap.

Not built, with reason:

- G3 (shared plant save key): false positive. PropertyStreamWriter XORs every key with a per-type hash (Int32 `0x415642B`, Float `0x4EDCD7A9`), so mFertilizerDaysLeft and mWaterLevel under 101511562 are separate entries (same as docs/590 B7).
- B2a: inside BlackjackTable's long Run loop: IL patch candidate.
- V4, V5, V7, V10, V11, V12, V15: inferred. V6: no household effect, the child state has no CleanUp of its own. V13: EA does not define when eating "has started". V14: Init restores autonomy on purpose; the CleanUp intent is unclear.
- Minor list and §4 speedup candidates: not built.

## plantroofshelter (2026-11-25, Dalexlol PR #740, ON since import 2026-11-27)
- Player question: does the game get dormancy wrong for plants indoors or under a roof? `Plant.UpdateSeasonalState` shelters a plant only in a greenhouse or when it is not outside and a roof or floor is above. A plant on a covered porch, carport, roofed patio or balcony reads outside and goes dormant at the first frost, although EA's own rain watering already treats it as covered. [verified in game by his probe; the decompile reading is ours]
- Fix: `plantroofshelter` (Batch593Logic.cs, runs in the `plantdormantflip` hook before EA's body): a roof or floor above shelters. Counters 4000-4002. Plants only, no Sim is touched. A plant brought under a roof wakes at its next daily check, as EA has it.
- Evidence (his ABBA on Sunset Valley, forced frost for one re-check): off, roofed outside plants dormant; on, awake; open and room plants unchanged; errors 0; natural 45-minute runs pass. His repros `plant-shelter-probe` and `plant-shelter-fix` are in his harness tree, not in this repo.
