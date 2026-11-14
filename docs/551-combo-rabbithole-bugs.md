# 551. Combined rabbit hole bugs (combo buildings)

2026-11-14, static audit of the 1.67 script code (Sims3GameplayObjects, Sims3GameplaySystems; the EP code lives in the same
two DLLs), fixes built, NOT RUN. Switches `comboalarm`, `comboprotest`, `combovenue`, `combosituation` (all default off,
`mods/ts3fixes/src/ComboRabbitHole.cs`, counters 2335-2344), harness checks `ComboRabbitHoleCheck` / `ComboFixCheck`
(`ComboChecks.cs`), repro `combo-rabbithole-check`. Follows the City Hall ceremony finding (`ceremonyspot`, docs/546 §11).

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
| A piece's `LotCurrent` is null (`mLotCurrent` is `[Persistable(false)]` and only the native lot-change callback `InternalOnLotChanged` sets it) | [inferred]; EA code elsewhere uses `RabbitHoleProxy.LotCurrent` on purpose (`RabbitHoleFieldTrip.Test`, `RemoveRabbitHolesThatCantHaveJobs`); `ComboRabbitHoleCheck` reads it |
| A combo model has no `RoutingSlot_0` (reads as the world origin) | [verified in game for ComboCityhallPoliceMilitary, 2026-11-14]; other combos: `ComboRabbitHoleCheck` prints `routingSlot0-3` |
| `GameObject.AddAlarm` with no lot uses `AlarmManager.Global`; `RemoveAlarm` / `GetTimeLeft` / `UpdateAlarmTime` with no lot do nothing / return 0 | [verified] |
| `ReactionBroadcaster.Init` registers a lot-less origin on the world lot and finds Sims around `origin.Position`; `Lot.UpdateReactions` drops a broadcaster whose origin has no `LotCurrent` | [verified] |
| Every lot's alarm manager runs (`Lot.Simulate`) | [verified] |

## 2. Candidates

Verdicts: **verified** = traced in the decompiled code with the facts above; **likely** = the path exists but depends on a
detail not traced to the end (order of a query, a timing); **not a bug** = EA already handles the combo there.

| # | game class / method | what goes wrong in a combo | combos | verdict | fix |
|---|---|---|---|---|---|
| 1 | `CityHall.CreateCelebration` / `RouteToGatheringArea` (`RoutingSlot_0` on the proxy) | award jig and crowd goal at the world origin, every ceremony lost | ComboCityhallPoliceMilitary | verified in game | `ceremonyspot` (done earlier, now through the shared `RhSlotOrEntrance`) |
| 2 | `CityHall.CreateProtest` | its three broadcasters (gather, react, pet react) use `this` (the piece), unlike `CreateCelebration`, which uses `RabbitHoleProxy`: registered on the world lot at OutOfWorld and dropped at the next update; nobody joins | ComboCityhallPoliceMilitary | verified | `comboprotest`: broadcasters moved to the building (origin and lot), the `Protest` interaction still targets the piece |
| 3 | `CityHall.RouteToGatheringArea` for a protest | same origin goal as #1, for NPC and household protesters | ComboCityhallPoliceMilitary | verified | `comboprotest`: EA's goal shape at the entrance, every Sim (goal only, the Sim walks) |
| 4 | `CityHall.GetProtestTimeRemaining` (`GetTimeLeft`) | reads 0, so `Protest.ConfigureInteraction` gives each protester the 1 min floor | ComboCityhallPoliceMilitary | verified | `comboalarm` |
| 5 | `GameObject.RemoveAlarm` on a piece | no-op: `Stadium.ResetScore` adds a repeating `GameScoreUpdate` alarm every game day that `StopScore` cannot remove (one more running score alarm per game day for the session); `StartPlayerConcert` cannot cancel the NPC show's close-doors / end alarms, which then still fire during a player concert (EndShow stops its sound and show state) | all; the stadium case in ComboSchoolStadium(EP7), ComboStadiumTheatre, ComboStadiumAnnex | verified | `comboalarm`: a piece also removes / reads its handle on `AlarmManager.Global` and on the building's lot manager (a handle carries its manager's lot id, so no other alarm can match) |
| 6 | `Theatre.StartPlayerConcert` / `EndPlayerConcert` / `StartPlayerConvention` / `EndPlayerConvention` | `TurnOnAllSearchLights(LotCurrent)`, `LotCurrent.AlarmManager`, `TurnOffAllSearchLights` (in `EndShow`) dereference the null lot: the NullReferenceException ends Perform Concert / Hold Psychic or Game Convention inside the building (no pay, no `kPerformedConcert`, no concert count) | ComboStadiumTheatre, ComboCriminalTheater | verified | `combovenue`: the call runs with the building's lot lent to the piece |
| 7 | `Stadium.StartPlayerConcert` / `EndPlayerConcert` | same through `StartSearchLights` / `StopSearchLights` / `UpdateGraphic` | ComboSchoolStadium(EP7), ComboStadiumTheatre, ComboStadiumAnnex | verified | `combovenue` |
| 8 | `Theatre.StartShowSoundBroadcaster` | TheatreMusic broadcaster on the piece: nobody near the building gets the buff | ComboStadiumTheatre, ComboCriminalTheater | verified | `combovenue`: moved to the building |
| 9 | Theatre / Stadium daily alarms (`StartSearchLights`, `UpdateSandwichBoards`, `ResetSandwichBoards`, `PrepareForConcert`, `StopScore`, `EndShow`, `ResetSandwichBoardFromPlayerConcert`) | NullReferenceException on the null lot; EA drops a repeating alarm after its callback throws (alarmkilllog), so lights and boards just stop; `StopScore` skips its board-reset alarm | theatre / stadium combos | verified, cosmetic | not fixed: lights and sandwich boards only; lending the lot inside a repeating callback would re-add alarms the piece cannot track |
| 10 | `AutographSessionSituation` (Sim.AutographSession on a piece) | situation lot = piece lot = null; `RouteEveryoneToLot.Init` Exit()s at once and its broadcaster dies: the session is lost | every combo (the interaction is on every rabbit hole) | verified | `combosituation`: the session is held at the building (the combo is a RabbitHole; GoToRabbitHoleEntrance and the cancel interaction work on it) |
| 11 | `FieldTripSituation` (School field trip) | destination picked from `Queries.GetObjects<IFieldTrippable>()`, which holds the pieces (Theatre, ScienceLab, PoliceStation, Bistro) as well as ComboStadiumTheatre / ComboHospitalScienceLab; for a piece the situation lot is null and `RouteAdultToSchool.Init` Exit()s: the trip the player accepted is lost | ComboStadiumTheatre, ComboCriminalTheater, ComboHospitalScienceLab, ComboCityhallPoliceMilitary, ComboDaySpaBistro | verified | `combosituation`: built with the building's lot (the rest of the situation already uses `RabbitHoleProxy.LotCurrent`) |
| 12 | `ComboStadiumTheatre.OnFieldtripUpdate` and the reward getters | `mTheatre` is set only in `OnFieldtripStart`, which only the chaperone calls; a student whose loop ticks first throws (ComboHospitalScienceLab sets its link in `OnStartupRabbitHole`) | ComboStadiumTheatre | likely | `combosituation`: link set when the trip is created |
| 13 | `ComboCityhallPoliceMilitary.CreateCelebration` / `RewardAvailable` / `AddRewardPickup` / `ShowInProgress` stubs | callers (`RewardsManager`, `MapTagManager`) go through `GetRabbitHolesOfType(CityHall)`, which returns the CityHall piece | ComboCityhallPoliceMilitary | not a bug [inferred: the combo's Guid is not CityHall; `ComboRabbitHoleCheck` prints it] | - |
| 14 | `CityHall.Celebrate` (`Target.LotCurrent` wait) | Target is the combo (the celebration broadcaster sits on the proxy; `GetCityHall` maps the combo to its piece) | ComboCityhallPoliceMilitary | not a bug | - |
| 15 | `CityHall.WatchFireworkShow` (`Target.Position`) | piece Position is OutOfWorld | future world only (`ScheduleFireworkShow` tests `IsFutureWorld`) | not a bug in practice [inferred: Oasis Landing's City Hall is not a combo] | - |
| 16 | `School` graduation (`Queries.GetObjects<ICityHall>()[0]`) | when the piece comes first, a graduate with no Sim is instantiated on `null` lot = a random lot (`GetPositionInRandomLot`), then walks; the ceremony itself works | ComboCityhallPoliceMilitary | likely, minor | not fixed (no lost event); `ComboRabbitHoleCheck` prints the query order |
| 17 | `ImprovedProtestSituation` daily random protest (`objects[0].LotCurrent`) | a piece first adds a null lot to the candidates; picking it creates the protest on no lot (exceptions swallowed): that day's protest is lost | ComboCityhallPoliceMilitary (EP9) | likely | not fixed (needs the query order; low impact) |
| 18 | `ImprovedProtestHelperObject.FindMapTagPickerInfo` | adds a picker entry for the piece's null lot next to the combo's real one | ComboCityhallPoliceMilitary (EP9) | likely | not fixed (UI behaviour of a null-lot entry not traced) |
| 19 | `Occupation.TryCreateJobCreationSpec`, RabbitHole destination | uses `item?.LotCurrent` (null for a piece) while the RabbitHoleLot branch and `RemoveRabbitHolesThatCantHaveJobs` use the proxy's lot; `IsJobValidForCreation(null)` is false and the loop stops: such jobs are never offered at a combo piece | hospital / science lab / hideout pieces | verified | not fixed on purpose: those jobs would then hit #21's OutOfWorld uses |
| 20 | `Occupation.ValidateJobConsistencyWithLotType` (Edit Town) | `job.RabbitHole.LotCurrent != lot` is always true for a piece: a RabbitHoleLot job at a combo is cancelled when its lot is edited in Edit Town | all | verified, minor | not fixed |
| 21 | `FirefighterEmergencySituation` (turn to face), `RabbitholeFirefightingJob` and `GhostHunter` (VFX at `RabbitHole.Position`) | OutOfWorld for a piece: no fire / ghost cloud effect, the firefighter turns toward nowhere | hospital / science lab / hideout pieces (RabbitHoleLot jobs) | likely, cosmetic | not fixed |
| 22 | `ScienceLab` restore-ghost `Cleanup` | ghost placed near `Target.Position` (OutOfWorld) when the interaction ends after the ghost was made but before `AfterExitingRabbitHole` | ComboHospitalScienceLab | likely, rare | not fixed (rare path; a placement fix for a household ghost would be a follow-up) |
| 23 | `LotManager` teen hangout multiplier (`rabbitHole.LotCurrent == TeenHangout`) | never true for a piece | all | verified, minor (autonomy weight) | not fixed |
| 24 | `MapTagManager` (`list4.Remove(rabbitHole.LotCurrent)`) | null for a piece, so a lot tag is not replaced by the piece's tag | pieces with `kReplaceLotMaptag` | likely, cosmetic | not fixed |
| 25 | `ComboRabbitHole.Dispose` | destroys the pieces but never calls `base.Dispose` (the combo's own door jigs, alarms, reactions) | all, Edit Town delete only | verified, minor | not fixed |
| 26 | `Teleporter.PreTeleport`, `GetClosestRabbitHoleOfType`, `RabbitHoleFieldTrip` carpool / Test, `PromSituation`, `HaveBabyHospital`, `Hideout` / `MilitaryBase` door code, `CareerEventManager` | use `RabbitHoleProxy` or swap in the combo | - | not a bug | - |
| 27 | `Stadium` constructor sets `RabbitHoleProxy = this` | `OnCreation` sets the combo afterwards; on load the persisted field wins | combo stadiums | not a bug [inferred; `ComboRabbitHoleCheck` prints the proxy] | - |

## 3. Fixes (ComboRabbitHole.cs)

Shared helpers: `RhPiece` (a rabbit hole whose proxy is another object), `RhSlotOrEntrance` (EA's slot, or the first enter
slot 2 m out when the slot reads as the origin; `EventRouteRetry.cs`'s `EvrAnchor` now calls it), `RhLendLot` / `RhTakeLot`
(the building's lot set on the piece for one EA call, back to null in a finally), `RhMoveBroadcaster` (origin and lot of a
broadcaster moved to the building), `RhRemovePieceAlarm` / `RhPieceTimeLeft`.

| switch | hooks | counters |
|---|---|---|
| `comboalarm` | `GameObject.RemoveAlarm`, `GameObject.GetTimeLeft` (hot paths: a non-RabbitHole or non-piece runs EA's body after one type test) | 2335 removals, 2336 time reads |
| `comboprotest` | `CityHall.CreateProtest`; protest branch of the existing `EventCrowdRingHook` (`CityHall.RouteToGatheringArea`) | 2337 broadcasters moved, 2338 protest routes |
| `combovenue` | `Theatre.StartPlayerConcert` / `EndPlayerConcert` / `StartPlayerConvention` / `EndPlayerConvention` / `StartShowSoundBroadcaster`, `Stadium.StartPlayerConcert` / `EndPlayerConcert` | 2339 lent calls, 2337 broadcasters moved |
| `combosituation` | `AutographSessionSituation.Create`, `FieldTripSituation.Create` | 2340 autograph, 2341 field trips, 2342 theatre links |
| all | | 2343 errors, 2344 reserved |

Scope: every path tests `RhPiece` (or `IsProtestActive` with a missing slot) first, so stand-alone rabbit holes run EA's
code unchanged. No Sim is placed or teleported; the protest route change only moves the goal, so household Sims are
covered by it the same way as NPCs (they walk). Alarms a piece adds during a lend (EndShow's facade update, the board reset
alarm, anything added across a yield inside the call) land on the building's lot manager, which runs every lot's alarms;
`comboalarm` searches there too. Save safety: `mLotCurrent` is not persisted and is null again after each call; a moved
broadcaster lives in the building lot's reaction list like any other; nothing new is written to the save.

Rejected: giving pieces their building's lot for good. `GameObject.AddAlarm` / `RemoveAlarm` / `Dispose` pick the alarm
manager by `mLotCurrent`, so alarms the piece made before would become unremovable and its dispose would clean the wrong
manager.

## 4. Test

`reflect_call Ts3Fixes.Fixes.ComboRabbitHoleCheck "combo"` (facts) and the `combo-rabbithole-check` repro
(`ComboFixCheck "run"` with the switches off, then on) on a world with combos (Appaloosa Plains has
ComboCityhallPoliceMilitary). Expected facts: every piece out of the world with no lot; combo `routingSlot0-3` starting
with `o`. A world without a combo piece reads N/A everywhere (record as not triggered).
