# 551. Combined rabbit hole bugs (combo buildings)

2026-11-14, static audit of the 1.67 script code (Sims3GameplayObjects, Sims3GameplaySystems; the EP code lives in the same
two DLLs), fixes built. Switches `comboprotest`, `combovenue`, `combosituation` (all default off,
`mods/ts3fixes/src/ComboRabbitHole.cs`, counters 2337-2343), harness checks `ComboRabbitHoleCheck` / `ComboFixCheck`
(`ComboChecks.cs`), repro `combo-rabbithole-check`. Follows the City Hall ceremony finding (`ceremonyspot`, docs/546 §11).

> **Correction 2026-11-14 (first in-game check, Appaloosa Plains):** a piece's `LotCurrent` is the **world lot**
> (`FFFFFFFFFFFFFFFF`), not null [verified in game: all 13 pieces]. The native lot callback runs for an out-of-world
> object too and `GameObject.InternalOnSetLotCurrent` maps a null lot to the world lot. So every candidate that rested on
> a null lot changed: the alarm bug (#4, #5) does not exist (the world lot's alarm manager *is* `AlarmManager.Global`), the
> venue calls (#6, #7, #9) do not throw, the field trip (#11) does not exit. `comboalarm` and the lot lends of `combovenue`
> / `combosituation` were removed (counters 2335, 2336, 2339, 2341 retired). Bugs that rest on the piece's OutOfWorld
> position stay (#2, #3, #8, #10).

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

Rows 16-20 and 24 were written for a null piece lot. With the world lot (correction above) the piece adds or compares the
world lot instead of null (#16: the graduate is placed for the world lot, #17/#18: a world-lot candidate / picker entry,
#19: `IsJobValidForCreation(world lot)`); none is fixed, re-trace before any fix [inferred].

## 3. Fixes (ComboRabbitHole.cs)

Shared helpers: `RhPiece` (a rabbit hole whose proxy is another object), `RhSlotOrEntrance` (EA's slot, or the first enter
slot 2 m out when the slot reads as the origin; `EventRouteRetry.cs`'s `EvrAnchor` calls it), `RhMoveBroadcaster` (origin
and lot of a broadcaster moved from the world lot to the building and its lot).

| switch | hooks | counters |
|---|---|---|
| `comboprotest` | `CityHall.CreateProtest`; protest branch of the existing `EventCrowdRingHook` (`CityHall.RouteToGatheringArea`) | 2337 broadcasters moved, 2338 protest routes |
| `combovenue` | `Theatre.StartShowSoundBroadcaster` (TheatreMusic broadcaster moved to the building) | 2337 |
| `combosituation` | `AutographSessionSituation.Create` (the building instead of the piece), `FieldTripSituation.Create` (ComboStadiumTheatre theatre link, #12) | 2340 autograph, 2342 theatre links |
| all | | 2343 errors; 2335, 2336, 2339, 2341 retired; 2344 reserved |

Removed 2026-11-14 after the first in-game check: `comboalarm` (hooks on `GameObject.RemoveAlarm` / `GetTimeLeft`; the bug
does not exist, see §1), the `combovenue` lot lend on the six Theatre / Stadium player-show calls and the `combosituation`
lot lend on `FieldTripSituation.Create` (both lent only while the piece had no lot, which never happens; their bugs are
cosmetic or absent, #6, #7, #11).

Scope: every path tests `RhPiece` (or `IsProtestActive` with a missing slot) first, so stand-alone rabbit holes run EA's
code unchanged. No Sim is placed or teleported; the protest route change only moves the goal, so household Sims are
covered by it the same way as NPCs (they walk). Save safety: a moved broadcaster lives in the building lot's reaction list
like any other; nothing new is written to the save.

## 4. Test

`reflect_call Ts3Fixes.Fixes.ComboRabbitHoleCheck "combo"` (facts) and the `combo-rabbithole-check` repro
(`ComboFixCheck "run"` with the switches off, then on) on a world with combos (Appaloosa Plains has
ComboCityhallPoliceMilitary, no Theatre piece). Lines: `lot` (fact: the piece's lot and its alarm manager), `protest`
(broadcaster origin, plus the anchor and where the planned gathering route for the active Sim ends: position, distance from
the building, lot, outdoors; FIXED only when the origin is the building and the spot is outdoors on the building's lot),
`venue` (TheatreMusic broadcaster origin; N/A without a Theatre piece), `situation` (session rabbit hole and lot). A world
without a combo piece reads N/A everywhere (record as not triggered).

First run 2026-11-14 (old check): facts as in §1 (13 pieces, all OutOfWorld on the world lot, combos `routingSlot0-3=oooo`;
ICityHall order: the piece first); alarm "FIXED" with the switch off (the bug is not real), protest BROKEN off / FIXED on
(origin), venue threw `TargetException` in both arms (the check invoked a swapped method through `MethodInfo.Invoke`;
the venue line now uses a delegate and only the remaining broadcaster hook), situation read FIXED off (the old verdict
tested only for a lot; now it tests where the session is centred).
