# 551. Combined rabbit hole bugs (combo buildings)

**In-game status:** `comboopp`, `combojob`, `comboprotestlot`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-14, static audit of the 1.67 script code (Sims3GameplayObjects, Sims3GameplaySystems; the EP code lives in the same
two DLLs), fixes built. Switches `comboprotest`, `combovenue`, `combosituation`, `combojob`, `comboprotestlot`, `comboopp`
(all default off, `mods/ts3fixes/src/ComboRabbitHole.cs`, counters 2337-2343, 2374-2378 and 2405-2408), harness checks `ComboRabbitHoleCheck` / `ComboFixCheck`
(`ComboChecks.cs`), repro `combo-rabbithole-check`. Follows the City Hall ceremony finding (`ceremonyspot`, docs/546 §11).

> **Correction 2026-11-14 (first in-game check, Appaloosa Plains):** a piece's `LotCurrent` is the **world lot**
> (`FFFFFFFFFFFFFFFF`), not null [verified in game: all 13 pieces]. The native lot callback runs for an out-of-world
> object too and `GameObject.InternalOnSetLotCurrent` maps a null lot to the world lot. So every candidate that rested on
> a null lot changed: the alarm bug (#4, #5) does not exist (the world lot's alarm manager *is* `AlarmManager.Global`), the
> venue calls (#6, #7, #9) do not throw, the field trip (#11) does not exit. `comboalarm` and the lot lends of `combovenue`
> / `combosituation` were removed (counters 2335, 2336, 2339, 2341 retired). Bugs that rest on the piece's OutOfWorld
> position stay (#2, #3, #8, #10).

> **Re-trace 2026-11-14 (leftovers #16-#25 with the world lot):** #19 is real and the biggest one (jobs never offered,
> fixed: `combojob`, which also covers the OutOfWorld uses of #21 that such a job then meets); #17 and #18 are real with
> the measured query order (piece first) and now on the world lot (fixed: `comboprotestlot`); #16 stays (the graduate is
> placed for the world lot's entry point, the ceremony works); #20, #22, #23, #24, #25 and the cosmetic #6, #7, #9 stay
> not fixed (rare, Edit Town only, autonomy weight or cosmetic). None vanished the way `comboalarm` did.

## 1. How a combo building works

A combo building is a `ComboRabbitHole` subclass (13 classes: `ComboCityhallPoliceMilitary`, `ComboSchoolStadium` (+EP7),
`ComboHospitalScienceLab`, `ComboStadiumTheatre`, `ComboStadiumAnnex`, `ComboCriminalTheater`, `ComboDinerGrocery` (+EP10),
`ComboBookstoreDaySpa`, `ComboBusinessBookstore`, `ComboBusinessRestaurant`, `ComboDaySpaBistro`). Its `OnCreation` makes
each piece (CityHall, PoliceStation, Theatre, Stadium, ...) with `CreateObjectOutOfWorld` plus `ComboInitParams`; the piece's
`RabbitHoleProxy` is the combo, the combo's is itself [verified]. The pie menu of the combo lists the pieces' interactions,
so an interaction's `Target` is the piece, while routing and the door code use `RabbitHoleProxy` [verified].

Facts the bugs below rest on:

| fact | status |
|---|---|
| A piece is never added to the world: `Position` is `Vector3.OutOfWorld` | [verified indirectly: EA's `Teleporter.PreTeleport` tests exactly `Position == OutOfWorld` on a store to swap in its combo]; `ComboRabbitHoleCheck` reads it |
| A piece's `LotCurrent` is the world lot (the native callback `InternalOnSetLotCurrent(null)` stores the world lot) | [verified in game 2026-11-14, `ComboRabbitHoleCheck`: `lot=FFFFFFFFFFFFFFFF` on all 13 pieces; the earlier "null" was an inference and wrong]; EA code elsewhere uses `RabbitHoleProxy.LotCurrent` on purpose (`RabbitHoleFieldTrip.Test`, `RemoveRabbitHolesThatCantHaveJobs`) |
| A combo model has no `RoutingSlot_0` (reads as the world origin) | [verified in game for ComboCityhallPoliceMilitary, 2026-11-14]; other combos: `ComboRabbitHoleCheck` prints `routingSlot0-3` |
| The world lot's `AlarmManager` is `AlarmManager.Global` (the `AlarmManager.LotId` setter makes the manager of lot `ulong.MaxValue` the global one), so `AddAlarm` / `RemoveAlarm` / `GetTimeLeft` on a piece all use Global and work | [verified in code; `ComboFixCheck` lot line: time read 60 min, alarm removed with no fix] |
| `ReactionBroadcaster.Init` finds Sims around `origin.Position`: a broadcaster on a piece (OutOfWorld) reaches nobody | [verified in code] |
| Every lot's alarm manager runs (`Lot.Simulate`) | [verified] |

## 2. Candidates

Verdicts: **verified** = traced in the decompiled code with the facts above; **likely** = the path exists but depends on a
detail not traced to the end (order of a query, a timing); **not a bug** = EA already handles the combo there.

| # | game class / method | what goes wrong in a combo | combos | verdict | fix |
|---|---|---|---|---|---|
| 1 | `CityHall.CreateCelebration` / `RouteToGatheringArea` (`RoutingSlot_0` on the proxy) | award jig and crowd goal at the world origin, every ceremony lost | ComboCityhallPoliceMilitary | verified in game | `ceremonyspot` (done earlier, now through the shared `RhSlotOrEntrance`) |
| 2 | `CityHall.CreateProtest` | its three broadcasters (gather, react, pet react) use `this` (the piece), unlike `CreateCelebration`, which uses `RabbitHoleProxy`: registered on the world lot at OutOfWorld, so it reaches nobody; nobody joins | ComboCityhallPoliceMilitary | verified | `comboprotest`: broadcasters moved to the building (origin and lot), the `Protest` interaction still targets the piece |
| 3 | `CityHall.RouteToGatheringArea` for a protest | same origin goal as #1, for NPC and household protesters | ComboCityhallPoliceMilitary | verified | `comboprotest`: EA's goal shape at the entrance, every Sim (goal only, the Sim walks) |
| 4 | `CityHall.GetProtestTimeRemaining` (`GetTimeLeft`) | reads the real time (the piece's alarms are on Global through the world lot) | ComboCityhallPoliceMilitary | not a bug (was "verified" on the null-lot inference; `comboalarm` removed) | - |
| 5 | `GameObject.RemoveAlarm` on a piece (claimed no-op) | works: removes from Global (world lot manager); `Stadium.ResetScore` / `StopScore` and the player concert's cancels work | all | not a bug (in-game check 2026-11-14: alarm removed with no fix) | `comboalarm` removed |
| 6 | `Theatre.StartPlayerConcert` / `EndPlayerConcert` / `StartPlayerConvention` / `EndPlayerConvention` | no exception: `LotCurrent` is the world lot, so `TurnOnAllSearchLights(LotCurrent)` / `TheatreSandwichBoard.UpdateGraphic(LotCurrent)` act on the world lot's lights and boards, not the building's: the building's search lights and boards do not react to a player show; the show itself (pay, event, count) works | ComboStadiumTheatre, ComboCriminalTheater | verified in code, cosmetic | not fixed (the lot lend was removed: it never ran, the piece has a lot) |
| 7 | `Stadium.StartPlayerConcert` / `EndPlayerConcert` | same as #6 through `StartSearchLights` / `StopSearchLights` / `UpdateGraphic` | ComboSchoolStadium(EP7), ComboStadiumTheatre, ComboStadiumAnnex | verified in code, cosmetic | not fixed |
| 8 | `Theatre.StartShowSoundBroadcaster` | TheatreMusic broadcaster on the piece: nobody near the building gets the buff | ComboStadiumTheatre, ComboCriminalTheater | verified | `combovenue`: moved to the building |
| 9 | Theatre / Stadium daily alarms (`StartSearchLights`, `UpdateSandwichBoards`, `ResetSandwichBoards`, `PrepareForConcert`, `StopScore`, `EndShow`, `ResetSandwichBoardFromPlayerConcert`) | act on the world lot's lights and boards (no exception): the building's lights and boards stay as they are | theatre / stadium combos | verified in code, cosmetic | not fixed |
| 10 | `AutographSessionSituation` (Sim.AutographSession on a piece) | the session is centred on the piece: its broadcaster sits at OutOfWorld (on the world lot), so the autographer, sent to the building's entrance, never enters it; the session never starts (no fans, no pay) until the player cancels it | every combo (the interaction is on every rabbit hole) | verified in code (was described as an at-once Exit on a null lot) | `combosituation`: the session is held at the building (the combo is a RabbitHole; GoToRabbitHoleEntrance and the cancel interaction work on it) |
| 11 | `FieldTripSituation` (School field trip) to a piece | situation lot = the world lot (not null), so `RouteAdultToSchool.Init` goes on; the rest of the situation uses `RabbitHoleProxy.LotCurrent` | ComboStadiumTheatre, ComboCriminalTheater, ComboHospitalScienceLab, ComboCityhallPoliceMilitary, ComboDaySpaBistro | not a bug (was "verified" on the null-lot inference; the lot lend was removed) | - |
| 12 | `ComboStadiumTheatre.OnFieldtripUpdate` and the reward getters | `mTheatre` is set only in `OnFieldtripStart`, which only the chaperone calls; a student whose loop ticks first throws (ComboHospitalScienceLab sets its link in `OnStartupRabbitHole`) | ComboStadiumTheatre | likely | `combosituation`: link set when the trip is created |
| 13 | `ComboCityhallPoliceMilitary.CreateCelebration` / `RewardAvailable` / `AddRewardPickup` / `ShowInProgress` stubs | callers (`RewardsManager`, `MapTagManager`) go through `GetRabbitHolesOfType(CityHall)`, which returns the CityHall piece | ComboCityhallPoliceMilitary | not a bug [inferred: the combo's Guid is not CityHall; `ComboRabbitHoleCheck` prints it] | - |
| 14 | `CityHall.Celebrate` (`Target.LotCurrent` wait) | Target is the combo (the celebration broadcaster sits on the proxy; `GetCityHall` maps the combo to its piece) | ComboCityhallPoliceMilitary | not a bug | - |
| 15 | `CityHall.WatchFireworkShow` (`Target.Position`) | piece Position is OutOfWorld | future world only (`ScheduleFireworkShow` tests `IsFutureWorld`) | not a bug in practice [inferred: Oasis Landing's City Hall is not a combo] | - |
| 16 | `School` graduation (`Queries.GetObjects<ICityHall>()[0]`) | the piece comes first [verified in game, Appaloosa Plains]: a graduate with no Sim is instantiated for the world lot (`GetPositionInRandomLot(world lot)`: a road spot near the world lot's `EntryPoint`, not near the City Hall), then walks; `GraduateInCityHall` targets the piece and routes to the building; the ceremony itself works | ComboCityhallPoliceMilitary | verified in code, minor (an NPC graduate may arrive late) | not fixed (no lost event) |
| 17 | `ImprovedProtestSituation.ScheduleTodaysProtest` (`objects[0].LotCurrent`) | the piece comes first, so the daily NPC protest's City Hall candidate is the world lot: the City Hall never hosts the daily protest, and the days that pick the candidate hold it on the world lot (not at any building; TNS names the world lot) | ComboCityhallPoliceMilitary (EP9) | verified (code + measured query order) | `comboprotestlot`: `ImprovedProtestSituation.Create` moves a world-lot protest to the combo City Hall's lot (only a City Hall piece puts the world lot there) |
| 18 | `ImprovedProtestHelperObject.FindMapTagPickerInfo` (megaphone / podium organize-protest picker) | loops every `ICityHall`: the piece adds a world-lot entry next to the combo's real one (the combo is an `ICityHall` too); picking it plans the protest on the world lot | ComboCityhallPoliceMilitary (EP9) | verified in code (how the map shows a world-lot entry not traced) | `comboprotestlot`: world-lot entries dropped |
| 19 | `Occupation.InitializeBaseJobCreationSpec`, RabbitHole destination | tests `IsJobValidForCreation(item?.LotCurrent)` = the world lot for a piece (false), while the RabbitHoleLot branch and `RemoveRabbitHolesThatCantHaveJobs` use the proxy's lot: a piece is skipped. A job type whose rabbit hole exists only inside combos is never offered: Firefighter `EpicHospitalRescue` / `EpicScienceRescue` / `EpicCriminalRescue`, Medical `SpecificMedicalRabbitholeJob1-3` (ScienceLab, CityHall, Hideout), Film `PickUpFoodForShoot` (Restaurant) and `ResearchCompetingFilm` (Theatre) [job data: EA's jobs XML]. "Any" jobs still go to stand-alone rabbit holes or the combo itself | Hospital / ScienceLab / CityHall / Hideout / Restaurant / Theatre pieces (Appaloosa Plains: Hospital, ScienceLab, CityHall, both restaurants) | verified | `combojob`: when EA found none and no stand-alone of the type is valid, EA's selection over the pieces with the building's lot |
| 20 | `Occupation.ValidateJobConsistencyWithLotType` (`EditTownModel.ChangeLotType` only) | `job.RabbitHole.LotCurrent != lot` is always true for a piece: a RabbitHole / RabbitHoleLot job at a combo is cancelled when that lot's type is changed in Edit Town | all | verified, minor (lot type change while holding such a job) | not fixed |
| 21 | `FirefighterEmergencySituation` (victims turn to `RabbitHole.Position`), `RabbitholeFirefightingJob.SetupRabbitholeRescue` and `GhostHunterJob.CreateRabbitHoleInvestigation` (effect at `RabbitHole.Position`), `ActiveCareerMapTag.Target` (the piece) | for a job at a piece (only through #19's fix, or Film's RabbitHoleLot audition): no rescue / ghost-cloud effect at the building, map tag on an out-of-world object [inferred: no map position], panicking victims turn toward nowhere | jobs at pieces | verified in code, cosmetic | `combojob`: effects and the map tag moved to the building; the victims' turn stays (cosmetic) |
| 22 | `ScienceLab` restore-ghost `Cleanup` | ghost placed near `Target.Position` (OutOfWorld) when the interaction ends after the ghost was made but before `AfterExitingRabbitHole` | ComboHospitalScienceLab | likely, rare | not fixed (rare path; a placement fix for a household ghost would be a follow-up) |
| 23 | `LotManager.ScoreTeenHangoutForRabbitHole` (`rabbitHole.LotCurrent == TeenHangout`) | the world lot is never the hangout: teens' x10 autonomy weight for rabbit holes at the hangout misses a combo's pieces | all | verified, minor (autonomy weight) | not fixed |
| 24 | `MapTagManager` (`list4.Remove(rabbitHole.LotCurrent)`) | removes the world lot (not in `AllLots`), so a combo lot's tag is not replaced by the piece's tag (the piece's own tag sits on an out-of-world object) | pieces with `kReplaceLotMaptag` | likely, cosmetic | not fixed |
| 25 | `ComboRabbitHole.Dispose` | destroys the pieces but never calls `base.Dispose` (the combo's own door jigs, alarms, reactions) | all, Edit Town delete only | verified, minor | not fixed |
| 26 | `Teleporter.PreTeleport`, `GetClosestRabbitHoleOfType`, `RabbitHoleFieldTrip` carpool / Test, `PromSituation`, `HaveBabyHospital`, `Hideout` / `MilitaryBase` door code, `CareerEventManager` | use `RabbitHoleProxy` or swap in the combo | - | not a bug | - |
| 28 | Opportunity steps whose listener EA aims at the building (`OpportunityManager.GetTargetForEvent` returns `TargetObject`, the building) for an event the piece's own interaction sends with the piece (`kAteAtRestaurant`: `CulinaryCareer_Report`) | the step never completes | restaurant combos | verified in code (§5) | `comboopp` |
| 29 | `kInteractionSuccess` steps (`FindValidTargets`): Investigator case steps "eat at a restaurant / bistro" (`EP2_Case_CulinaryBackstab3`, `EP2_Case_SuperFruits2`), `EP3_Celebrity_RestaurantPromotion1`, `EP9_Academics_PhysEd_10` (Stadium `AttendGame`) | the building is not of the interaction's target type, so EA lists some other (random) object or none; the piece's event is never in the list: the step cannot be done at the building the map shows | restaurant / bistro / stadium combos | verified in code (§5) | `comboopp` |
| 30 | `SchoolRabbitHole` audition / recital and `ScienceLab` imaginary-friend potion tests (`activeOpportunity.TargetObject != target`) | the target is the piece, the opportunity's target the building: the interaction never shows; EP4 auditions, recitals and both potion steps cannot be done | ComboSchoolStadium, ComboHospitalScienceLab | verified in code (§5) | `comboopp` |
| 31 | `VisitRabbitHoleSynchronizedWithBase` (`AttendRecital`) follower wait (`follower.RabbitHoleCurrent != Target`) | `RabbitHoleCurrent` is the building, `Target` the piece: followers always count as not arrived; the leader waits the full hour, then drops and cancels them (no recital credit for the family) | ComboSchoolStadium | verified in code | not fixed (private method of a generic class; fix candidate) |
| 32 | `RabbitHole.GetCaught.ShouldPushOnSim` (`CareerLoc.Owner == target`) | an NPC source Sim whose workplace is a piece of the target building is not seen as "at its workplace": EA pushes `GetCaught` on it during its work day (it walks into the same building; the `RequireSourceInRH` step still completes) | combos, rare | verified in code, minor | not fixed (candidate) |
| 33 | `OpportunityManager.CheckTargetLotRequirement`, `WorldHasRabbitHoleType` (`lot.GetObjects<RabbitHole>()` with a Guid match) | never matches a combo lot (the pieces are on the world lot); used only by `Lot.VerifyLocationOpportunity` after build / buy on a lot holding a location opportunity, which then cancels it | combos, rare | verified in code, minor | not fixed (candidate) |
| 34 | `kVisitedRabbitHole` steps at a combo (turn-in): Investigator "go to the police station" steps, `EP7_FortuneTellerCareer_HelpInvestigation_Police`, `CriminalCareer_BigHeist` ("Steal The Golden Llama!", City Hall), `RequireSourceInRH` steps | `SetupHelper` aims the opportunity at `RabbitHoleProxy`; the turn-in interaction is added to the building, sends the building, the listener waits for the building, the source Sim's `RabbitHoleCurrent` is the building: consistent | ComboCityhallPoliceMilitary and all | not a bug in the script code [static trace; not run] | - |
| 35 | Investigator stakeouts / snooping at a rabbit hole target | the target list holds the building's lot (`TargetObject.LotCurrent`), the lot of the building: consistent | all | not a bug | - |
| 27 | `Stadium` constructor sets `RabbitHoleProxy = this` | `OnCreation` sets the combo afterwards; on load the persisted field wins | combo stadiums | not a bug [inferred; `ComboRabbitHoleCheck` prints the proxy] | - |

Rows 16-20 and 24 were first written for a null piece lot; they were re-traced with the world lot on 2026-11-14 (box at
the top) and the table now states the world-lot behaviour.

## 3. Fixes (ComboRabbitHole.cs)

Shared helpers: `RhPiece` (a rabbit hole whose proxy is another object), `RhSlotOrEntrance` (EA's slot, or the first enter
slot 2 m out when the slot reads as the origin; `EventRouteRetry.cs`'s `EvrAnchor` calls it), `RhMoveBroadcaster` (origin
and lot of a broadcaster moved from the world lot to the building and its lot), `FirstPiece<T>` (the first piece of a type),
`RhCityHallLot` (the lot of the combo holding a City Hall piece), `RhJobAtPiece` / `RhMoveJobEffect` (`combojob`).

| switch | hooks | counters |
|---|---|---|
| `comboprotest` | `CityHall.CreateProtest`; protest branch of the existing `EventCrowdRingHook` (`CityHall.RouteToGatheringArea`) | 2337 broadcasters moved, 2338 protest routes |
| `combovenue` | `Theatre.StartShowSoundBroadcaster` (TheatreMusic broadcaster moved to the building) | 2337 |
| `combosituation` | `AutographSessionSituation.Create` (the building instead of the piece), `FieldTripSituation.Create` (ComboStadiumTheatre theatre link, #12) | 2340 autograph, 2342 theatre links |
| `combojob` | `Occupation.InitializeBaseJobCreationSpec` (after EA: when EA found no rabbit hole, the job type's destination is RabbitHole and no stand-alone rabbit hole of the type is valid, EA's own steps over the pieces in random order with the building's lot: first valid lot, EA's household-job stop, start time; the job keeps the piece as its rabbit hole, as EA's RabbitHoleLot jobs do), `ActiveCareerMapTag.get_Target` (a piece reads as its building), `RabbitholeFirefightingJob.SetupRabbitholeRescue` / `GhostHunter.GhostHunterJob.CreateRabbitHoleInvestigation` (effect moved to the building) | 2374 jobs at a piece, 2375 map tag reads, 2376 effects moved |
| `comboprotestlot` | `ImprovedProtestSituation.Create` (a world-lot protest moves to the combo City Hall's lot), `ImprovedProtestHelperObject.FindMapTagPickerInfo` (world-lot entries dropped); both only when a City Hall piece exists | 2377 protests moved, 2378 picker entries dropped |
| `comboopp` | `OpportunityManager.GetTargetForEvent` (#28: the listener waits for the building's piece of the opportunity's rabbit hole type, except `kVisitedRabbitHole`, `kVisitedRabbitHoleWithPet`, `kDeliveredToSim`, `kWentToWork`), `OpportunityManager.FindValidTargets` (#29: the building is listed for a piece of the interaction's target type, so EA's own tag / effect code marks the building), `OpportunityInteractionCompleteListener.ProcessEvent` (#29: a piece's event is matched through its listed building), School `AttendAudition` / `AttendRecital` and ScienceLab `PurchaseImaginaryFriendPotion` / `DeliverImaginaryFriendRgm` `Definition.Test` (#30: run with the piece as the opportunity's target) | 2405 listener targets, 2406 success targets, 2407 events matched, 2408 tests passed |
| all | | 2343 errors; 2335, 2336, 2339, 2341 retired; 2344 reserved |

Removed 2026-11-14 after the first in-game check: `comboalarm` (hooks on `GameObject.RemoveAlarm` / `GetTimeLeft`; the bug
does not exist, see §1), the `combovenue` lot lend on the six Theatre / Stadium player-show calls and the `combosituation`
lot lend on `FieldTripSituation.Create` (both lent only while the piece had no lot, which never happens; their bugs are
cosmetic or absent, #6, #7, #11).

Scope: every path tests `RhPiece` (or `IsProtestActive` with a missing slot, or the world lot with a City Hall piece)
first, so stand-alone rabbit holes run EA's code unchanged. `combojob` offers the active household's careers jobs EA meant
to offer (player Sims are not moved; they walk or drive to the building like to any job), and leaves EA's pick alone
whenever EA found a rabbit hole. No Sim is placed or teleported; the protest route change only moves the goal, so household Sims are
covered by it the same way as NPCs (they walk). Save safety: a moved broadcaster lives in the building lot's reaction list
like any other; nothing new is written to the save.

## 4. Test

`reflect_call Ts3Fixes.Fixes.ComboRabbitHoleCheck "combo"` (facts) and the `combo-rabbithole-check` repro
(`ComboFixCheck "run"` with the switches off, then on) on a world with combos (Appaloosa Plains has
ComboCityhallPoliceMilitary, no Theatre piece). Lines: `lot` (fact: the piece's lot and its alarm manager), `protest`
(broadcaster origin, plus the anchor and where the planned gathering route for the active Sim ends: position, distance from
the building, lot, outdoors; FIXED only when the origin is the building and the spot is outdoors on the building's lot),
`venue` (TheatreMusic broadcaster origin; N/A without a Theatre piece), `situation` (session rabbit hole and lot), `job`
(EA's selection, on the Firefighter's static occupation with validation off, for the first epic rescue whose rabbit hole
exists only as combo pieces: BROKEN = no job, FIXED = a job at the piece on the building's lot; N/A without Ambitions or
without such a type), `protestlot` (the daily protest's City Hall candidate lot, the picker's world-lot / City Hall entries,
and the lot of a protest `Create` makes on that candidate for an NPC organizer, exited at once with EA's last-protest day
restored: BROKEN = world lot, FIXED = the City Hall's lot and no world-lot entry; N/A without EP9 or a combo City Hall).
With the switches off neither new line can read FIXED: EA's code never returns a piece job (the world lot fails
`IsJobValidForCreation`) and `Create` keeps the lot it is given. A world without a combo piece reads N/A everywhere
(record as not triggered).

Worlds: Appaloosa Plains [verified in game] has ComboCityhallPoliceMilitary, ComboHospitalScienceLab, ComboSchoolStadium,
ComboBusinessRestaurant, ComboDinerGrocery, ComboBookstoreDaySpa and a stand-alone Theatre: every line but `venue` can
run there (`job` uses EpicHospitalRescue, `protestlot` the combo City Hall). `venue` needs ComboStadiumTheatre or
ComboCriminalTheater (the hideout rescue also needs ComboCriminalTheater); which shipped or store worlds have them is not
in the script code: `ComboRabbitHoleCheck "combo"` on a world lists its combos.

First run 2026-11-14 (old check): facts as in §1 (13 pieces, all OutOfWorld on the world lot, combos `routingSlot0-3=oooo`;
ICityHall order: the piece first); alarm "FIXED" with the switch off (the bug is not real), protest BROKEN off / FIXED on
(origin), venue threw `TargetException` in both arms (the check invoked a swapped method through `MethodInfo.Invoke`;
the venue line now uses a delegate and only the remaining broadcaster hook), situation read FIXED off (the old verdict
tested only for a lot; now it tests where the session is centred).

## 5. Opportunities and the Investigator at a combo building (2026-11-14, static, `comboopp`)

Player report: Investigator (and other) opportunities "bugging because of the police station combo", e.g. "help
investigation" (`EP7_FortuneTellerCareer_HelpInvestigation_Police`) and "Golden Llama" (`CriminalCareer_BigHeist`, turn-in
"Steal The Golden Llama!" at City Hall; the name was found in the game's English string table, not in the script code).

How EA aims an opportunity at a rabbit hole [verified in code]: `SetupHelper` (Target `RabbitHole`) picks a rabbit hole
of the type and returns its `RabbitHoleProxy`, so a combo's piece becomes the BUILDING. The "Workplace" target returns
`CareerLoc.Owner` (the piece). The turn-in interaction is added to the target, so it is on the building, and it sends
`kVisitedRabbitHole` with the building; the listener for that event (and `RequireSourceInRH` with the source Sim's
`RabbitHoleCurrent`, which is the building) waits for the building. So both reported opportunities, every Investigator
"go to the police station" step and every other turn-in step trace as consistent (#34): no combo bug was found for them
in the script code. Not run in game: what "bugging" looks like for them is still open (an in-game look at one of them
at Appaloosa Plains' ComboCityhallPoliceMilitary would settle it; time window and day checks are EA's normal ones).

Shared root cause of the real failures (#28-#31): the opportunity's target is the building, but the piece's OWN
interactions carry the piece, as `Target` and as the event's target. Every place that compares the two never matches:
listeners from `GetTargetForEvent` (#28), the interaction-success target list (#29, two Investigator case steps), the
School / ScienceLab tests (#30) and the recital follower wait (#31). `comboopp` treats a piece and its building as the
same place at #28-#30 and changes nothing for opportunities at stand-alone rabbit holes (no piece is involved).

Worlds: the Police Station is only a combo piece in Appaloosa Plains [verified in game: ComboCityhallPoliceMilitary].
Starlight Shores was measured with combos too (coordinator note); which of its rabbit holes are combos is listed by
`ComboRabbitHoleCheck "combo"`. Other worlds: not in the script code.

Check: the `opp` line of `ComboFixCheck "run"` (first combo piece; a throwaway opportunity aimed at its building, never
registered): BROKEN off (the `kAteAtRestaurant` listener waits for the building; a listener holding the building ignores
the piece's interaction-success event), FIXED on (waits for the piece; event matched). The School / ScienceLab tests need
an active special opportunity and are read through counter 2408 in play.
