# 661. Lot-type gameplay audit: venue scoring, roles, bar venue hours

2026-11-19. Static analysis of the decompiled game scripts (`game-copy/decompiled`, not in git) plus IL (`ilspycmd -il`)
for every claim marked [verified IL], and a `health.jsonl` census of the 65 runs whose `autotest.txt` reads
`in live mode ("active_household":"<name>")` (1286 live snapshots: Bridgeport 888, Isla Paradiso 315, other worlds 83).
No game run. Built: two default-off fixes and one diagnostic (§3, §4), slots 4220-4249, `kCounterSlots` 4250.

Related: docs/659 (NPCs idle at home, EA low-LOD; not repeated here), docs/633 (`bouncerplace`), docs/590 B12
(`bouncerrole`), docs/555 (`viproomrefresh`), docs/569 (consignment, venues), docs/614 (`barputdownwait`), docs/634
(unseen pianist/show loops), docs/646 (`trashoutsidefallback`), docs/647 (`shuffleboardnpccap`), docs/648
(`paparazzicooldown`). Gameplay-balance proposals and meta-autonomy tuning were not pursued (owner decision: bug fixes only).

## 1. How each lot type gets, keeps and loses NPCs [decompiled]

Every community lot is an `IMetaObject` in `Autonomy.PublicMetaObjects`. An NPC goes there only through meta-autonomy
(docs/659 §1: off-screen NPCs not on the active lot get no local autonomy). The pick is
`Lot.HowMuchItWantsSimToCome` (Lot.cs:2796): residential lots use the `VisitPlayer` / `VisitNeighbor` / `VisitOther`
tunings; community lots switch on `mMetaAutonomyType` to one `MetaAutonomyVenueType` and score
`Autonomy.ScoreMetaObject(random-of-the-day, NumSimsGoingToOrAtThisLot, desired, GetIntensity)` (Lot.cs:3020-3060).
Desired = the venue's `DesireCurve` for the hour (`MetaAutonomyTuning.DesiredNumSims`: hot spot / dead zone / active
household around / popular / not around, downtown and full-moon variants), capped by CPU level (4 / 9 / 13 / 20).
Open hours exist only as `HourOpen` / `HourClose` columns; the curve carries the time-of-day shape.

| Lot type | How NPCs come | Staff / roles | How they leave | What keeps them busy |
|---|---|---|---|---|
| Residential | `VisitOther` / `VisitNeighbor` / `VisitPlayer` desire x intensity, `kMinTimeSinceLastVisit*` gate, gig audience override | none | `VisitSituation` end, go-home meta pick | home objects (low LOD off the active lot, docs/659) |
| Apartments / towers (BP) | residential path; `BuildableShells` lots | none | as residential | as residential |
| Bars, lounges, nightclubs (incl. vampire lounge, werewolf bar) | venue type curve; `Bartending.BarData` hours | bartender (`BarAdvanced` IdleTend role giver, `IRoleGiverExtended` times = open - delta .. close + delta), bouncer (`Bouncer` role, velvet ropes), pianist / DJ (`IRoleGiverExtended`) | `BartendingVenueHelper`: staggered exit from close - 2 h (30% per 15 min), after-close "remind guests to leave" every 5 min until open | bar orders, dance floor, piano, VIP rooms |
| Restaurants, diners, bistros | venue curve; `Bistro` tuning | chef / waiter role givers | visit end, meal done | `Eat Out` (rabbit-hole style) |
| Gyms, pools, parks, beaches, fishing spots | venue curves (`Gym`, `Pool`, `BigPark` / `SmallPark`, `BeachVenue`, `FishingVenue`), rain/hail penalty list | lifeguard job (`kNumberOfSimsDesiredOnLotWithLifeguardJob` when the household is there) | visit end | treadmills, chess, playground, fishing |
| Libraries | `Library` curve | none | visit end | bookshelves, View |
| Festival grounds (Seasons) | `FestivalLot` -> Spring / Summer / Fall / Winter festival tuning of the season | stand merchants (`RoleConcessionsStandMerchant`) | visit end | festival objects |
| Resorts, dive lots, ports (IP) | `Resort` (capped by `ResortManager` occupancy when controllable), `Diving`, `Port` | `IResortStaffedObject` roles kept off the community-lot test (RoleManagerTask) | visit end, boat home | dive, buffet |
| Hangouts, cafés | `Hangout`, `CoffeeShop` | barista (`BaristaBarTender`) | visit end | espresso bar |
| Consignment, rabbit holes | `ConsignmentStore`; rabbit holes score through `RabbitHole` meta (RabbitHole.cs:5886) | proprietor (`Proprietor` role) | rabbit-hole exit | rabbit-hole interaction |
| Graveyards | `Graveyard` / `Mausoleum` | none | visit end | View, mausoleum |
| Horse ranches / equestrian | `HorseRanch` venue; `EquestrianCenter` rabbit hole (show hours) | none | visit end | horses, competitions |
| Town halls | rabbit-hole only (`CityHall`), jobs | none | work shift end | `Go to City Hall`, work |

Roles: `RoleManagerTask.Simulate` (RoleManagerTask.cs:51) loops forever: fill role-giver objects on community lots whose
role time is valid (`FillRoleGiverObjects`), fill numbered roles (`UpdateAndGetRolesThatNeedPeople`, RoleManager.cs),
create new townie households for what could not be filled (`AddMoreInWorldSims`), then simulate active roles for
120 sim-min in 30-min steps. Roles are dropped when the Sim is no longer good, the giver left the world, or a valid-time
role has no Sim and is not starting (RoleManager.cs `UpdateAndGetRolesThatNeedPeople`).

## 2. Measured health per lot type [measured]

NPC humans per snapshot, idle = no interaction and `still_min` >= 30. "On the selected lot" = the lot of the selected
Sim in that snapshot. Lot types are named from lot names (only Twinbrook has a typed lot list, and no run here is
Twinbrook), so the split is [inferred] per name.

| Lot type | Lots | NPCs / snapshot | On the selected lot: rows, idle | Elsewhere: rows, idle | Median still (min) |
|---|---|---|---|---|---|
| residential | 136 | 30.7 | 205, 3% | 39 261, 80% | 186 |
| nightclub / lounge / bar | 11 | 16.9 | 0 | 21 704, 38% | 150 |
| apartment / tower (BP) | 22 | 11.6 | 0 | 14 879, 77% | 161 |
| street (no lot) | 3 | 7.7 | 182, 4% | 9 665, 9% | 0 |
| restaurant / diner / bistro | 7 | 4.6 | 0 | 5 872, 40% | 42 |
| park / plaza | 19 | 4.2 | 0 | 5 432, 59% | 128 |
| dive / port (IP) | 17 | 2.2 | 0 | 2 879, 74% | 170 |
| beach | 10 | 2.0 | 0 | 2 585, 64% | 81 |
| rabbit hole / work lot | 10 | 1.8 | 115, 6% | 2 247, 39% | 54 |
| gym | 4 | 1.4 | 0 | 1 848, 54% | 160 |
| theatre / stadium | 7 | 1.2 | 132, 1% | 1 415, 32% | 51 |
| library | 6 | 1.2 | 0 | 1 488, 47% | 70 |
| gallery / museum | 5 | 1.1 | 0 | 1 351, 44% | 157 |
| graveyard | 6 | 0.9 | 0 | 1 157, 35% | 154 |
| town hall / civic | 3 | 0.5 | 70, 3% | 608, 19% | 52 |
| pool | 4 | 0.4 | 0 | 479, 65% | 107 |
| resort | 1 | 0.3 | 0 | 429, 17% | 0 |
| fishing spot | 4 | 0.1 | 0 | 96, 48% | 86 |

Findings:
- **The runs never had the selected Sim on a venue.** Every venue row is off the active lot, so the venue idle shares
  are EA's low-LOD layer (docs/659: 0.2-1.3% idle on the active lot, 36-39% elsewhere), not venue behaviour. A run that
  sends the selected Sim to one venue of each type is needed before any on-lot venue verdict.
- **Bars and clubs staff and close correctly.** Bridgeport clubs: two bouncers (`Watch Door`) at Aquarius, Banzai,
  Sports Zone, Brightmore, Prosper Room (one at Plasma 501) only inside each club's open hours (Sports Zone from 10:00,
  Banzai / Prosper Room from 13:00, Aquarius / Brightmore from 16:00); patrons 2-3.5 a snapshot in the evening, falling
  to 0.0-0.4 two hours after close. Bartenders (`BarAdvanced+IdleTend`, the empty interaction name in health rows,
  2819 rows) staff every lot with a bar, including The True Modern Art Gallery and Bridgeport Eternal Rest Cemetery
  where they are often the only NPC.
- **Always empty (< 0.1 NPC a snapshot, max 1-2):** Isla Paradiso: Paradise Pub (3 rows in 315 snapshots), Island Jewel
  Park, Petite Park, Beso de Sol Garden (max 1, mostly `Perform For Tips`), Pearl's Deep (dive lot, 4 snapshots, 100%
  idle, still 667 min), most ports (Port Pleasant, Promising Port, Port Playa, ...), Greenbeard's / Fortunate Fishing;
  Bridgeport: Bogaard Overlook, Civic Plaza (max 1, a busker), Floodgate Apartments. University and Moonlight Falls
  venues (Loel's Lounge, Varg's Tavern, The Toadstool) under 0.5 a snapshot.
- **Always full:** Beckett Theatre (max 25, above the 20 CPU cap: show audience via `Attend Show` is the gig / show
  override, not the curve), Bridgeport "Community Lot" (present in 682 / 888 snapshots, max 18), Steve's Business
  Complex (621 / 888, max 18), Buena Vista Resort (max 21 when present).
- Paradise Pub's emptiness and the parks' max-1 pattern fit a desire curve of 0 or an intensity of 0 for those venue
  types in Isla Paradiso; §3.2 is one code path that yields that [inferred: the Venues XML is not in game-copy].

## 3. Confirmed bugs (built, default off, NOT RUN)

### 3.1 `barcloseorphan`: after a bachelor party, bars of that type kick guests out during open hours [verified IL]

`Bartending.BartendingVenueHelper` (one per bar venue type) arms day alarms for open, close and the staggered exit
(Bartending.cs:918-944). `OnClose` (Bartending.cs:1074-1100):
1. for each lot of its type, if `IsClosingTimeExtended` (a started `BachelorParty` there) it re-arms itself with
   `mExtendedClosingTimeAlarm = AddAlarm(kPartyEndCheckInterval (30) min, OnClose)`;
2. at the end, always: `mRemindVenueGuestsToLeave = AddAlarmRepeating(0, ..., RemindVenueGuestsToLeave, kExitVenueAlarmRepeatTime (5) min)`.

IL `OnClose` IL_00d1-IL_00fa: `stfld mRemindVenueGuestsToLeave` with no `RemoveAlarm` of the old handle. Each 30-min
re-run while the party lasts (up to `kTimeOfPartyEnd` 06:00) orphans the previous repeating alarm. `OnOpen` and
`RemindVenueGuestsToLeave` (when nobody needed a push) remove only the handle in the field. The orphans run every 5
minutes until the world unloads (`NeverPersisted`) and call `MakeSimLeaveVenueIfNecessary` for every actor of every lot
of that bar type, at any hour: every guest inside who is not in a bar role gets `VisitSituation.MakeSimExitToYard`, 50%
of NPCs straight home. Symptom: the venue type (dive bars, lounges incl. the vampire lounge, clubs, werewolf bar)
empties all next day.

Fix (`BarCloseOrphan.cs`, rule `LotGameplayCore.StrictlyOpen`): EA's own legit pushes happen only from
`HourClose - kStaggeredExitTimeDelta` to the next `OnOpen`. While the venue is strictly open (after `HourOpen`, before
the staggered exit; wrap past midnight handled, bad hours never skip), `MakeSimLeaveVenueIfNecessary` answers false
(EA's own "no push" answer) for a non-exempt NPC. Household, selected and unknown-scope Sims run EA's body unchanged
(they still get the orphan's push, as in vanilla). Counters 4230 skipped, 4231 exempt let through, 4232 errors; log
`barcloseorphan: ...` capped 10 a minute. Repro `barcloseorphan-check` (forced: calls EA's private
`RemindVenueGuestsToLeave` once on an open bar's helper, what an orphan does; off BROKEN, on FIXED).

### 3.2 `venueintensity`: venue intensity read from the other world's column [verified IL]

`MetaAutonomyTuning.GetIntensity` (MetaAutonomyTuning.cs:225) decompiles to
`float num = !GameUtils.IsDowntownWorld() ? Intensity_Downtown : Intensity;`. IL: `IL_0072 call IsDowntownWorld`,
`brfalse.s IL_0082` -> `ldfld Intensity_Downtown`; else `ldfld Intensity`. The loader (MetaAutonomyManager.cs:378-385)
fills `Intensity` from the Venues XML column `Intensity` and `Intensity_Downtown` from `Intensity_Downtown`: every world
except Bridgeport weights each venue with its downtown column, Bridgeport with the plain one. The same intensity
multiplies the residential visit score (Lot.cs:2852), every community-lot score (Lot.cs:3052), rabbit holes
(RabbitHole.cs:5886), ponds (WorldPondHelper.cs:201), gardens and go-home (GoHome.cs:142). A venue whose
`Intensity_Downtown` cell is empty reads 0 outside Bridgeport: it scores 0 for every NPC (only the gig / location
opportunity 100 overrides). Impact depends on the Venues XML, which is not in game-copy [open: first run logs it].

Fix (`VenueIntensity.cs`, rule `LotGameplayCore.ShouldSwap`): for a non-exempt NPC, when the right column for this
world is set (> 0) and differs from what EA reads, the two fields are swapped around EA's own `GetIntensity` body and
restored in a `finally` (the body does not yield), so traits, encouraged days, rich / celebrity / pet multipliers and
the active-Sim 2000 stay EA's. Exempt Sims get EA's value. The first call per venue type logs both columns
(`venueintensity: <type> Intensity a, Intensity_Downtown b[: swapped for NPCs]`), so the first run with the switch on
names every venue the bug touches. Counters 4236 types logged, 4237 swapped, 4238 exempt, 4239 errors. Repro
`venueintensity-check` (hook vs EA's body with the columns as loaded and swapped; N/A when no column pair differs).

## 4. Diagnostic: `visitwalkfinish` reason counters (docs/659 §2 follow-up)

docs/659 left ~25 unseen street "Visit" walkers past 4 h that `visitwalkfinish` never interrupted. `VwfPass`
(UnseenVisitTrip.cs) now counts, for each tracked walker past the 60 sim-minute mark and each 10-minute pass, the
first reason it is not interrupted: 4220 due, 4221 not routing, 4222 resort / dive lot, 4223 household has pets, 4224
grouping situation, 4225 other lot refusal (world lot, already there, moving houseboat, apartment shell, active
household's lot), 4226 not unseen, 4227 near a viewer, 4228 asked but still walking (the interrupt did not take), 4229
trips dropped as gone. Same decisions as before (only the order of the free checks changed). Walkers never tracked
(Run started before the switch, or the 1024-entry table cleared) do not show here; a high 4228 or a gap between
4220 and the street census points there. Agent read-back: `vwf_*`.

## 5. Leads checked and not built

| Lead | Verdict |
|---|---|
| Hour checks past midnight: `SimClock.IsTimeBetweenTimes`, role times, bar hours | correct wrap (SimClock.cs:272); hand-rolled ranges found by grep (`Stadium`, `NewspaperDelivery`, `MailCarrier`, `FakeMetaAutonomy`, show venues) are same-day windows [verified] |
| `AddAlarmDay(HourOpen - kBartenderStartTimeDelta)` / `(HourClose - kStaggeredExitTimeDelta)` | `AddAlarmDay` returns `kInvalidHandle` for a negative hour (AlarmManager.cs:399): a bar closing before 02:00 (or opening before the delta) never starts its staggered exit / early bartender. Latent: the measured clubs close at 02:00 or later and show EA's decline; depends on BarData, not built |
| `RoleManagerTask.Simulate` `mSimsToAdd.Add(item2.RoleType, num)` after role givers filled the same dictionary | a duplicate RoleType would throw `ArgumentException` and end the task (only `ResetException` is caught); needs a role type that is both an object role and a numbered role in RoleData; none seen; ErrorTrap's `CheckRoleManagerTask` covers the throw [inferred, not built] |
| `UpdateAndGetRolesThatNeedPeople` / `VerifyRoleGivingObjectRoles` loops that `continue` without an index step | terminate: `RemoveSimFromRole` always calls `RoleManager.RemoveRole` (Role.cs:486-503) unless a role's `EndRole` throws, which ends the task (ErrorTrap) [verified] |
| `GetIntensity` `sim.Household.Pets` with a null household on a pet venue | NRE only for a household-less Sim on a venue with `mAllowedPetSpecies`; not seen in logs, not built |
| Bartenders staffing lots with nobody else (gallery, cemetery) | EA's per-object role giver: by design (balance, not pursued) |
| Venue idle shares 38-80% | EA low-LOD off the active lot (docs/659), not venue code |

## 6. Switches and slots

| Switch | Slots | Default | Status |
|---|---|---|---|
| `barcloseorphan` | 4230-4232 | off | built 2026-11-19, NOT RUN, repro `barcloseorphan-check` |
| `venueintensity` | 4236-4239 | off | built 2026-11-19, NOT RUN, repro `venueintensity-check` |
| (diagnostic, under `visitwalkfinish`) | 4220-4229 | with `visitwalkfinish` | built 2026-11-19, NOT RUN |

4233-4235 and 4240-4249 spare. Selftest: `node tools/selftest/lotgameplay/run.ts` (window wrap, bad hours, swap rule,
reason codes).
