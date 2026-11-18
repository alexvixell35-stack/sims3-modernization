# 590. Usage sweep 2 (ranks 101-200 and top-20 callees)

Date: 2026-11-16. Static read, nothing built. Follows docs/589. Ranking from `docs/data/usage-rank-1115.csv`.

Every item was checked against ts3fixes, proven-fixes and simler90 UPDATE242 (ilspycmd diff of both DLLs). Unless noted, simler90 matches EA and no ts3fixes switch touches the site. "V" = verified in code, "I" = inferred. "HH" = household can see it.

## 1. Outings, venues, callees

| # | Site | Bug | Fix direction | V/I | HH |
|---|---|---|---|---|---|
| B1 | RabbitHole MetaRabbitHoleInteraction.Run (NPC wait) | `mNPCFinishedWaitingAlarm` is never removed; the alarm adds Finished to a later interaction. Route result overwritten by `flag = DoLoop` | Remove alarm in Cleanup; keep route result | V | yes |
| B2 | ImprovedProtestSituation StartImprovedProtest.Run / CancelNPCProtest | Only the root is cleaned; child OnLotChangedEvent handler leaks | Call Exit() | V | yes |
| B3 | Terrain GoJogging.JogLoop | GoJogging is `Interaction<Sim,Sim>`, but passes its Sim Target to GoHere (TerrainInteraction): InvalidCast each fatigue check | Use Terrain.Singleton | V | yes |
| B4 | Terrain SurveyBeach.SurveyLot / RescueCheck | Lifeguard who quits: NRE after exit reason | Return after setting exit reason | V | yes |
| B5 | OccultFairy FrolicLoop | `flag2 =` instead of `|=` | `|=` | V | yes |
| B6 | DislikesChildrenTraitShoo.Run | Mutates shared TurnToFaceAndAskToLeave.Singleton | Clone definition | V | yes |
| B7 | VelvetRopes Export/Import | Bribe amount written and read under the celeb-level key 177446789 (bribe key is 177446815); one value overwrites the other | Use 177446815 with fallback read | V | yes |
| B8 | Terrain MakeIgloo | Look-ats disabled, never re-enabled | Re-enable in Cleanup | V | yes |
| B9 | Protest CalculateOutcomeFromScore | Victory whenever organizer is null | Treat as fail/none | V | yes |
| B10 | GoHereWithSituation OnParticipantDeleted | Deleted Sim stays in mSimsToPutInBoat/HoverTrain | Remove from both | V | yes |
| B11 | BarAdvanced OrderRound/Pizza/BirthdayCake; Bartending OpenTab | Charged with no refund on failure; tab never closed; sOpenBarTabs not saved | Refund on failure; close tabs | V | yes |
| B12 | VelvetRopes ShouldBouncerNoticeSneaker; AskToGetIn / BribeBouncer | Squared distance vs plain tunable; null CurrentRole | Square the tunable; null guard | V | yes |
| B13 | MagicianSocialTricks.Run; FearOfHumansFaint | DisableSocialsOnSim / SetShutDown never undone | Undo in Cleanup | V | yes |
| B14 | MetaAutonomyManager.UpdateLotsToPrune | Caller passes `null`; method assigns its parameter, so the list never reaches the caller and nothing is pruned | Return the list / ref param | V | no (NPC meta) |
| B15 | WerewolfHunt FoundMetal/FoundGem, FoundInsect | NRE; `=` instead of `+=` | Guard; `+=` | V | yes |
| B16 | VisitRabbitHoleBase.LoopDel | Woohoo partner NRE; TryForBaby rolls twice | Guard; one roll | V | yes |
| B17 | Lower | PlayTag AskToJoinTest, protest Megaphone chance, ShoppingRabbitHole CreateItemDictionary, Stadium sound, puddle tile, Grocery lotto funds, CarNpcWithNpcDriver, Children.Sleep, AskForAutograph cooldown | see agent notes | V/I | mixed |

## 2. Objects and home

Dropped as already fixed: RidingSkill jump-wins key (simler90), Windows spread-fire alarm (ilpatch s12b), Jetpack FreeFlying buff (`jetpackflyingbuff`), Jetpack FlyHere StopFX (`jetpackflyherecleanup`).

| # | Site | Bug | Fix direction | V/I | HH |
|---|---|---|---|---|---|
| A1 | DoggySwimAround.Run | `pool.ActorsUsingMe` is the live list; `Remove(Actor)` takes the dog out of the pool's use list | Pick from a filtered copy | V | yes |
| A2 | Homework.CopyHomework.Cleanup | Copy broadcaster and CopyingSim undone only on normal path; stale reactions after cancel | Destroy/null in Cleanup | V | yes |
| A3 | MagicWand CastSpell / BeTargetOfSpell | Target never gets Finished; wait loops can hang | Finished to linked instance in Cleanup; break waits | V | yes |
| A4 | Jetpack.FlyAround.Run | Crash re-rolled even with mOverrideFail (show-off social) | Skip roll when override | V | yes |
| A5 | Plant.WaterPlant.DoWater (and the second site) | Null state machine path has no return: NRE | `return false` | V | yes |
| A6 | FoodTray_Prepare.BringTrayToGrill | Unguarded `WasInterrupted` after failed push; tray kept on early return | Guard; put tray down | V/I | yes |
| A7 | SingingInfo.ImportCompositions | Null compositions added; null array | Skip nulls; guard | V | yes |
| A8 | Windows.LookInWindow.Run | Shooer is `list[0]` (baby or visitor possible) | Pick an eligible household Sim | V/I | yes |
| A9 | CuttingBoard_Prepare chop VFX | Effects not nulled, not stopped on interrupt; Counter null at two sites | Stop/null in Cleanup | I | yes |
| A10 | SleepAndNapOnObject.SleepLoop | Dream balloon timer not reset on one branch | Reset on both | V | yes |
| A11 | Plant.TendGarden progress | Sum leaves out mEmotionalChargeTimeSpent | Add it | V | yes (UI) |
| A12 | Lower | WildHorses.FindGoodLot index 0, gnome check; DanceOnCounter reaction; BeKingOrQueen outfit; RidingSkill HeroicPose sync; MinorPet/AlienUtils null order | | V/I | mixed |

Read with no new bug: WorldPondHelper, ScubaSocializeWith, Window variants, FirepitTI, HarvestPlantFrostable, BedRelax, Bookshelf_ReadSomething, GetBook, RecycleNewspaper, ScratchObject, JumpOnObject, Pot_CookOnStove, BowlLarge_Prepare, CookingInteraction, Glass, SimTemperature, Snowman, SnowAngel, FoosballTable, DigitalFishTank, CatToyWallDancer, ArcadeMachineBase. Only skimmed: SwingSet, JungleGym, UberBoxPedestal, StuffedAnimal, CatCondo.

## 3. Proven-exact speedups (sent to the perf agent)

VelvetRopes Enum.GetValues cache; MetaAutonomyManager CalculateTotalDesiredNumSims GetTuning hoist; GetCandidateMetaObjectsForHotSpotAndDeadZones HashSet; RabbitHole.GetRabbitHolesOfType filter while copying; FindProtest early break; PetGuard IsBeingGuarded any-loop; GetClosestPuddle; ModifyRouteForSpecies single ToArray; SummonFairies.Test early exit; BarAdvanced.CurrentSnackBowl single call; Children.Sleep.Test static arrays; SingingInfo.AllKnownCompositions count/contains without building; Callbox.HasVisitableSimsOnLot early exit; Windows.RouteToSimOutside Length gate; CanGardenPlant HashSet mirror; WildHorses.FindGoodLot no copy.

## Batch 12 (built 2026-11-16, all default off, NOT RUN)

EA bug fixes as EA intended, household included (user rule "fix all"), from docs/590, docs/591 and docs/592. Each switch is a ts3fixes Mono swap with a pure decision, a `<Name>Check` (the hook's own handles, a live-path assert, controls) and a `<switch>-check` repro. Slots 3064-3195 (`fixes-3064`, 3 each: fixed / seen with the switch off / errors). Code: `mods/ts3fixes/src/Batch590Logic.cs`, `Batch591Logic.cs`, `Batch592Logic.cs`, checks `Batch12Checks.cs`. Every switch is household-visible.

| § | lead | switch | slots | fix |
|---|---|---|---|---|
| 1 | 590 B2 | `protestexit` | 1730-1732 | a cancelled NPC protest ends with Exit(), so the child protest's lot-change handler is removed |
| 2 | 590 B3 | `jogterrain` | 1733-1735 | a fatigued jogger's walk back is created on the terrain instead of throwing every minute |
| 3 | 590 B4 | `lifeguardquit` | 1736-1738 | a lifeguard who quit mid-survey stops the loop instead of throwing in RescueCheck |
| 4 | 590 B5 | `frolicexit` | 1739-1741 | any critical motive ends a fairy frolic, not only the last one in the list |
| 5 | 590 B6 | `shoodef` | 1742-1744 | the Dislikes Children shoo uses its own definition instead of writing into the shared shoo Singleton |
| 6 | 590 B8 | `igloolookats` | 1745-1747 | an igloo builder's look-ats are turned back on after the build |
| 7 | 590 B10 | `gohereboatlist` | 1748-1750 | a deleted Sim leaves the go-here boat and hover-train lists |
| 8 | 590 B12 | `bouncerrole` | 1751-1753 | the bouncer notices sneakers within the tuned radius (squared) and ropes without a bouncer role do not throw |
| 9 | 590 B13 | `tricksocials` | 1754-1756 | a magician trick that ends early turns the watcher's socials back on |
| 10 | 590 B13 | `fearfaintundo` | 1757-1759 | a Fear of Humans faint that ends early restarts the robot and removes its jig |
| 11 | 590 B14 | `lotprune` | 1760-1762 | the player's lot-popularity map drops deleted, residential and no-longer-visited lots |
| 12 | 590 B15 | `werewolffind` | 1763-1765 | a werewolf hunt with no metal or gem in range finds nothing instead of throwing, and an insect adds to the hunt value |
| 13 | 590 A2 | `copyhomeworkfx` | 1766-1768 | a homework copy cut short removes its cheating broadcaster |
| 14 | 590 A3 | `spelltargetend` | 1769-1771 | a spell's target stops waiting when the cast ends |
| 15 | 590 A6 | `foodtraygrill` | 1772-1774 | a food tray whose grill step fails is put down and a failed grill push no longer throws |
| 16 | 590 A7, 591 C8 | `compositionnull` | 1775-1777 | singing and band composition imports skip missing keys and unknown compositions |
| 17 | 590 A10 | `petdreamtimer` | 1778-1780 | a sleeping pet's dream balloon timer resets after a dream of its human too |
| 18 | 591 C1 | `awkwardscience` | 1781-1783 | the Socially Awkward science gain bonus applies to Science only |
| 19 | 591 C2 | `chessdefeated` | 1784-1786 | the highest-ranking defeated chess opponent is the strongest one, stored with its rank |
| 20 | 591 C4 | `privacyothers` | 1787-1789 | a Sim who left a private room's lot gets a new room check when it comes back |
| 21 | 591 C5 | `gigmaster` | 1790-1792 | Gig Master completes at the number of gigs its text names |
| 22 | 591 C6, 592 S4 | `intmultiple` | 1793-1795 | RandomUtil.GetIntMultiple can return its top multiple (sales up to 80%, idles up to 30 min) |
| 23 | 591 C7 | `doorprivacynull` | 1796-1798 | a privacy situation with no sync target no longer strips the privacy of later doors |
| 24 | 591 R1 | `leafburnfail` | 1799-1801 | a leaf pile whose fire never started is usable again |
| 25 | 591 R2 | `catchpreyodds` | 1802-1804 | snakes and turtles are a sure catch only with Slither Stalker, like lizards |
| 26 | 591 R3 | `picnicants` | 1805-1807 | a packed picnic basket loses its ant alarm and the ant effect is disposed |
| 27 | 591 R5 | `loyaldoglay` | 1808-1810 | a Loyal dog's lie-down near its human targets the dog so its test passes |
| 28 | 591 R6 | `visitlisteners` | 1811-1813 | a removed visit host's listeners are not re-added after a load |
| 29 | 591 R7 | `sciencefxstop` | 1814-1816 | a science outcome's old effect set is stopped before a new one starts |
| 30 | 591 R9 | `daycareliking` | 1817-1819 | daycare experience uses the child's real liking for the owner |
| 31 | 591 R10 | `kegbreakage` | 1820-1822 | the juice kegger breakage multiplier applies only at a juice kegger party |
| 32 | 591 R11 | `romancecooldown` | 1823-1825 | the bad-date cooldown in a group of three or more lasts hours, not minutes |
| 33 | 591 R12 | `lightcustom` | 1826-1828 | a room or house custom light level copies the chosen level |
| 34 | 591 R13 | `eatpreycancel` | 1829-1831 | a cancelled or failed prey eat keeps the prey and re-enables its interactions |
| 35 | 591 R16 | `meltalarm` | 1832-1834 | an ice sculpture with no known artist melts without a throw and a restart replaces its melt alarm |
| 36 | 591 R21 | `newspaperdef` | 1835-1837 | reading the newspaper autonomously no longer renames the newspaper menu root |
| 37 | 592 S2 | `leaveconvnull` | 1838-1840 | leaving a conversation the Sim never joined does not count as a social |
| 38 | 592 S3 | `vacationalarms` | 1841-1843 | alarms on vacation arrival are not shifted twice |
| 39 | 592 S5 | `cleanupvfx` | 1844-1846 | an interaction's named effects are disposed at its cleanup in a live game (also in the djcleanup copy) |
| 40 | 592 S6 | `lookatcopy` | 1847-1849 | asking for a room's look-at objects without Sims no longer removes the Sims from the registry |
| 41 | 592 S8 | `lottotooltip` | 1850-1852 | the lotto 'not available' tooltip shows the real start hour |
| 42 | 592 S9 | `traiticondislike` | 1853-1855 | pie-menu entries that lower a trait motive show the dislike icon |
| 43 | 592 S11 | `roleunprotect` | 1856-1858 | a townie removed from an inactive role loses the role's story-progression protection |
| 44 | 592 S13 | `putdownplaced` | 1859-1861 | putting an object on a surface fires the placed event, not removed |

Also: our `djcleanup` copy of InteractionInstance.Cleanup (DjBoothCleanup.cs) repeated EA's inverted effect-list test; it now follows `cleanupvfx` (`CvDispose`).

Not built, with reason:

- 590 A1, A12: already in the EP branch (`dogswimaroundlist`, `wildhorselot`).
- 590 A5 (Plant.GetStateMachine never returns null: dead branch), 590 B7 (PropertyStreamWriter keys are typed: no collision), 591 C3 (shipped tuning has 6 ranks), 591 R15 (the first plan never reads r): false positives.
- 590 B1, B16, A4, A11, 591 R4, R20, 592 S1: inside generic or very large methods: IL patch candidates (R4 is the highest value: NRE for every non-date member leaving a player-led group).
- 590 B11, 591 R17: several sites, own pass. 590 A8 (pick rule unspecified), A9, 592 S10, S12 (inferred), 591 R18 (inferred native trigger; the pre-check doubles a per-door call), 592 S7 (removing ended clones breaks EA's own foreach; needs a design pass).
- 591 R8, R14, R19: V/I effects unconfirmed. 590 B17, 591 R22, R23: one-line leads without notes.

### Check audit, batches 3-12 (2026-11-16)

As in docs/587 §14.1: a batch Check used to prove only its pure decision with constants, so a hook whose swap was missing or whose reflection handle failed would still print FIXED. Now:

- `tools/static/check-live-map.ts` (run by build-fixes.ts) generates `mods/ts3fixes/src/CheckLiveMap.cs`: for each switch, the hook classes its Apply patches, how many unconditional swaps each has, and every reflection handle the hook uses with how it uses it (primitive cast, static read, Invoke argument count). `CkLive(switch)` runs EpLive (EpLiveCheck.cs, shared with the EP checks) on them. B582Counters / B582Line, B4Line (docs/574) and the two hand-built checks (vehiclefxhelper, residentspawnwalk) add it to the members verdict, so a dead live path reads UNEXPECTED. EP and store checks keep their hand-built EpLive calls.
- The tool's gate lists batch Checks whose pure decision no hook calls (a parallel copy). Hooks fixed to call their decision: agingalarmcancel (`AcLeaves`), artsellnoartist (`AsCompletes`), moodidleflavor (`MiFlavor`; also a Fulfilled-sentinel bug that dropped rows following a Fulfilled row), spcollectnull (`SpCollectSkips`), spcreatemovein (`SpSpecAdds`), splotdensity (`SpDensityCounts`), sprelcount (new `SpOwnOnly`, shared with the Check). Counter-only checks (whole-method copies, IL patches: bathsituationfx, computergametype, distressgate, fightbroadcastend, goodbyepure, moodtankreset, socialnoeffect, trainsimgone, tvdisclistener, tvsyncalarm, tvwatchskill) can prove only their swaps and handles; their effect needs the game run.
- Proven switches that had the gap (no live-path assert; artsellnoartist also a parallel decision): the 44 in Batch574/582* checks: artsellnoartist gotoworkstale movingflag promoteshift royaltyorphan sandscoins splitpartner tombmusic wildherdmother holidaylights lifeguardsurvey maildogchaser moonriselistener prankditchempty serviceassignexit serviceshowalarm vehiclefxhelper doorlockowner elevatorkitten elevatorresetriders elevatorridertimeout elevatorsavedlog moondiallistener snublistener starlightreset digsiteemptypick homeworkskillbonus horseranchchallenge oppchainworld photosubjectstars rolenullguard teleportcommunitylot residentspawnslot shelldoorsummon talkoccupationchance boardingschoolmoney disposebase freshclothingresume twinbellyshape boatstaleswimmer nocommercialcar oppcategoryprotect partylotlistener scavengernolot.
- Self-checks on Linux: `tools/selftest/csc-shim.ts` runs the offline C# self-checks with TS3_CSC (Roslyn plus .NET reference assemblies, run through `dotnet`). Still Windows-only: rewind-census, night-run, machine-load (ping.exe / PowerShell).
