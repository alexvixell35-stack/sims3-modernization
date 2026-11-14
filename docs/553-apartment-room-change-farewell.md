# 553. Apartment visitors say goodbye in the hallway (Late Night)

2026-11-14, static audit of the 1.67 script code (Sims3GameplaySystems, decompiled locally), check and fix built, NOT RUN.
Player report (a project dev, related to docs/552): "when people who are not part of your household go to a part that's
not assigned to you, they announce it with an interaction 'looks like it's time to leave, see you'. It also happens when
they are not leaving the lot but going between rooms where one has a public room marker and the other does not." Later:
"It ruins parties." Switch `aptleave` (default off, `mods/ts3fixes/src/AptLeave.cs`, counters 2391-2394), harness check
`ApartmentLeaveCheck` (`AptLeaveChecks.cs`), repro `apartment-leave-farewell`.

## 1. Where the farewell comes from

- The message is not a social. It is the `kSimTalking` notification shown by `VisitSituation.OnSimLeavingLot(sim)`:
  `Gameplay/Core/Lot/GoHome:NPCLeaving1/2` for a plain visitor, or the party's `LeavingMessage` (HouseParty: by party
  outcome) for a party guest [verified, `VisitSituation.cs` ~1967].
- `OnSimLeavingLot` is EA's "this guest is leaving" step. Its callers: `GoHome.Run` (first line, before the walk),
  `GoOutside.Run` (once outside), the rabbit-hole commutes (work, school, passport event), the holo-projection go-home,
  and **`Sim.RoomChanged`** [verified, grep of all callers].
- `Sim.RoomChanged(roomId, previousRoomId, previousLot)`, on a lot with `HasVirtualResidentialSlots` (a residential lot
  with at least one `PublicRoomMarker`, i.e. an apartment building), same lot (no lot change): if the previous room is not
  public and the Sim now stands in a public room (`IsInPublicResidentialRoom`), and it does not work on that lot, EA calls
  `OnSimLeavingLot(this)` [verified, `Sim.cs` ~54116]. Public = `Lot.IsRoomPublic`: a room listed by a PublicRoomMarker,
  or outside. Not public = every unmarked room (the household's apartment) and every hidden room.
- So the trigger is exactly the transition the player describes: unmarked (apartment) room -> marked public room
  (hallway, lobby, elevator landing), on the same lot. The other direction (public -> apartment) does not call it.

## 2. What the call does

`OnSimLeavingLot(sim)`, in order [verified]:
1. `HostedSituation.GetHostedSituationIAmGuestAtCurrently(sim)?.RemoveGuest(sim, bLeftEarly: true)`, before any other
   test (also for household Sims who are guests at someone else's party). For a party: the Sim leaves `Guests`, joins
   `SimsWhoLeftParty` and `SimsWhoLeftEarly`; `OnSimGoingHome` applies the liking change with the host (mood plus
   `kPenaltyForEarlyDeparture` = -50) and, for HouseParty, the party balloon and buff cleanup; `HouseParty.RemoveGuest`
   adds that mood (with the -50) to `mPartyMood` and counts it in `mNumberOfGuests`, which the party outcome averages.
   The Sim is no longer a party guest for `Party.GetPartyIAmGuestAt` / party-animal cheers / the guest leave timer.
2. Returns for a selectable Sim, or unless the Sim is greeted on the active household's home lot (or the party it left
   has a selectable host on this lot).
3. Finds the Sim's VisitSituation; returns if the farewell was already shown (`mbSimAnnouncedLeaving`). Sets the flag,
   then shows the notification (party message, or NPCLeaving unless asked to leave / daycare).

It does **not** end the visit, send the Sim home, ungreet it or cancel what it is doing [verified]: the VisitSituation
stays (EA's own visit code lets a guest stand in a public room up to 30 sim-minutes before `MakeSimGoHome`), the party
never re-adds it, and when it really leaves later (GoHome) no farewell shows because the flag is already set.

Spam: once per visit per Sim (the flag); a new visit can show it again. Each guest who walks into the hallway once counts
as an early leaver, so a party where guests use the corridor (to cross between two parts of the apartment, to a lobby
object, or routed through it by a social) loses its guests one by one: worse score (-50 per guest), host liking down,
and `HouseParty.Pulse` goes to `AllGuestsHaveLeft` (party over) once no listed guest is on the lot and every leaver
still there is more than 2 sim-hours past its own scheduled leave time, although they are all still there [inferred from
the Pulse count, not measured]. "It ruins parties" [verified mechanics, not measured].

Household Sims: the farewell part never runs for them (step 2), but step 1 removes a household Sim from an NPC's party in
another apartment of the same building when it walks into the hallway. aptleave leaves household Sims on EA's code.

## 3. Bug or design

EA bug [inferred intent, verified mechanics]. The call is EA's "visitor leaves the apartment" announcement for a building
where the hallway is the street. Every other caller runs it when the Sim really leaves, and the room-change caller already
exempts a Sim who works on the lot, i.e. EA meant "leaving the apartment", not "standing in the hallway". Nothing undoes
it when the Sim stays, and EA's own visit logic treats a guest in a public room as still visiting. Unmarked rooms are the
household's apartment by design (they are not "unassigned"); builders cannot avoid the trigger except by making the whole
building one public room, which breaks the apartment.

## 4. Fix: aptleave (default off)

Hooks `Sim.RoomChanged` (marks "room change of Sim X from room A to room B", per Sim because EA's body can yield; EA's
body unchanged) and `VisitSituation.OnSimLeavingLot` (acts only on the room-change call, re-checking EA's own condition:
apartment lot, A not public, B public, the Sim stands in B). For NPCs (`IsNpcSim`) the call is skipped while the Sim is
not leaving:
- it has a current interaction whose target lot is this lot, and it is not GoHome / GoOutside;
- no GoHome / GoOutside is queued, its visit has no leave reason (asked to leave etc.);
- it does not live on this lot (residents of other units keep EA's call).
Otherwise (no interaction, one that targets another lot, leaving interactions) EA's call runs. GoHome / GoOutside /
commute calls are never touched, so a real departure gets EA's farewell and party bookkeeping, now at the right time.
Household and unknown-scope Sims always run EA's code. Counters: 2391 skipped, 2392 run with the switch on, 2393 room-change
calls seen (any switch state), 2394 errors. The last 32 decisions are kept for the check.

## 5. Check and repro

`ApartmentLeaveCheck` (reflect_call): `list` (rooms with a door tagged P/H/U, U|P doors, parties, NPCs with farewell flag
and guest state, decision lines, counters); `visit` / `party [make]` (test setup: one adult NPC placed in the apartment
room of a U|P door, greeted, visit flag cleared, optionally made a party guest; EA's Go Here to the public room behind the
door queued); `status` (verdict BROKEN / FIXED / WAIT); `home` (EA's MakeSimGoHome: the farewell must show in both arms);
`unparty`. Repro `apartment-leave-farewell` (Baseline-BP, Trawler Lodge F5900033BA9E8C10): off visit + party ->
BROKEN (party=left-early), on visit + party -> FIXED, on home -> farewell shown. Not run yet.

Real party (user request): `throw [n] [now]` makes the selected Sim throw a HouseParty at home the phone's way (guests =
the n best-related Sims of EA's phone invite list `Phone.Call.GetAllValidCalleesForParty`, invitations delivered,
`kThrewParty` event; start +1 sim-hour, `now` = +2 sim-min) and starts a per-guest tracker fed by the two hooks (arrived,
apartment->public / public->apartment room changes, room-change farewells run / skipped). `nudge` walks arrived guests
from the apartment to the public side of the nearest U|P door. `partyreport` prints per guest: arrived, room, crossings,
farewells, party state (guest / left / left-early, "still here, no longer partying"), mood; and EA's running score
(`mPartyMood / mNumberOfGuests`, read by reflection) with `GetPartyOutcome()`. `partyend` sends the remaining guests home
the EA way (`MakeGuestGoHome`, not early), prints the final score and outcome, exits the party and stops the tracker
(cleanup). Repro `apartment-leave-party` runs it off then on. Test saves only (the household really throws a party).

## 6. Open

- In-game A/B (repro above). If `visit` reports N/A (no U|P door the NPC may pass on Trawler Lodge), read `list` for the
  door layout.
- A guest who stands in the hallway with no interaction at all still gets EA's call (kept vanilla on purpose).

## 7. Other code read in this path (standing rule: fix or list)

Read: `VisitSituation.OnSimLeavingLot`, `Create`, the visit's go-outside reminder, `HostedSituation.RemoveGuest` /
`GetHostedSituationIAmGuestAtCurrently` / `GetHostedSituationIAmLeaving`, `HouseParty.RemoveGuest` / `Pulse` /
`GetPartyOutcome`, `Sim.RoomChanged` (greet and visit parts), `Lot.IsRoomPublic` / `FindApartmentDoor`, and the caregiver
public-room checks in `InteractionQueue` and `CaregiverRoutingMonitor`.
- Fix candidate, not built: household guests at an NPC's party in another apartment of the same building. Step 1 of §2
  (`RemoveGuest(left early)`) runs before the selectable return, so a household Sim who steps into the hallway leaves that
  party early (liking with the NPC host goes down). This is the same bug family as aptleave. It stays out because aptleave's "not
  leaving" test is a heuristic and the household rule needs an exact fix. Candidate: run the same test for household guests only when
  the current interaction is player-queued and targets this lot [inferred safe, not proven].
- No other bug found: the caregiver checks (`targetLot.IsRoomPublic(sim.RoomId) && !targetLot.IsRoomPublic(targetRoom)`
  = "in a public room, heading into the apartment") match their intent; `VisitSituation.Create` refusing visits in public
  rooms and the 30 sim-minute go-outside reminder are consistent with the apartment model. `mbSimAnnouncedLeaving` is
  set for animals too (no notification), which is harmless.
