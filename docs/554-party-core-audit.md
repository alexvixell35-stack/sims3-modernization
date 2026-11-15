# 554. House party core: audit, two fixes, candidates

**In-game status:** `partymotive`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15. `partyoutcome`: not provable on a simler90 core-mod install.

2026-11-14, static audit of the 1.67 script code (Sims3GameplaySystems, decompiled locally), fixes built (in-game status above).
Request: "see if you can find some loopholes or logic bugs in parties". The scope was cut to the common flow: a regular
house party (`HouseParty`, `HostedSituation`, `Party`) on a house lot and on an apartment lot (invite, arrive, greet,
room-change bookkeeping, score, end and cleanup). Other party types, lot types and expansion packs: candidates only (§4).
Switches `partyoutcome` and `partymotive` (default off, `mods/ts3fixes/src/PartyFixes.cs`, counters 2400-2404; 2398-2399
unused). Check `PartyFixCheck` (`PartyFixChecks.cs`). Repro `party-outcome-motive`. The apartment hallway bug
(guests dropped as early leavers) is docs/553 (`aptleave`).

## 1. Fixed: party outcome (partyoutcome)

- **No lame parties outside University worlds** [verified]. In `HouseParty.GetPartyOutcome`, with University Life (EP9)
  installed, a score below the EP9 lame threshold enters a branch that only returns Bad inside a University world.
  Elsewhere it leaves Neutral, and the else-if chain skips the base lame test. So with University Life installed, a home-world
  party is never lame: no Threw a Lame Party buff, and the outcome message does not match. Fix: a Neutral result whose score
  (`mTotalPartyScore`, written by the same call) is below the base lame threshold becomes Bad. Every other path to Neutral
  already failed that test, so this is exactly what the base test gives.
- **Per-guest tiers in the wrong order** [verified code; live only if the tuned tiers lie above Great, which `PartyFixCheck
  tuning` reports]. `GetPartyOutcomeForSim` tests Good before Amazing, Killer and Raging. `GetPartyOutcome` tests the
  highest tier first. Only a guest's leaving message uses this method (one caller, `LeavingMessage`). Fix: the same tiers in
  GetPartyOutcome's order.
- Household: the host's outcome changes (EA bug fix). Counters 2400 lame restored, 2401 guest tier changed, 2402 errors.

## 2. Fixed: party motive left on early leavers (partymotive)

`HouseParty.Happening` creates an "enjoy party" motive (Adult/Teen/ChildEnjoyParty) on every guest. Its CleanUp removes
the motive only for Sims still in `Guests`. `RemoveGuest` moves a leaving guest to `SimsWhoLeftParty` and never removes it.
A created motive stays until `RemoveMotive` is called, so every guest who leaves before the end keeps the party motive for good
[verified]. Fix: after `HouseParty.RemoveGuest`, while the party is in Happening, the guest's motive is removed the way
Happening does it (its own `partyMotives` entry, then `Motives.RemoveMotive`). This includes household Sims who are guests
at an NPC party. Counters 2403 removed, 2404 errors.

## 3. Checked in the core flow, found correct

`OnSimLeavingLot` cannot count a guest twice (it acts only on Sims still in Guests). The celebrity array index (level-1)
stays in range. The average mood is guarded against zero guests. `PostLoad` strips null Sims. Every alarm on the party's own
path is removed in some CleanUp (invitations, preparations, pulse). HostedSituation.CleanUp removes the role from every list.
`SimsWhoLeftEarly` is always a subset of `SimsWhoLeftParty`. The phone guest list removes duplicates. Guests not in the
world are created off screen and walk in, or come in through their own apartment's NPC door, and never land in a hidden
room (docs/552 placement fallbacks are separate). `HouseParty.CleanUp` always frees a rented lot.

## 4. Candidates (not fixed; evidence from the audit, by reading)

Core flow, low impact or needing a mid-method change:
- C1 `HouseParty.Pulse`: after `SetState(AllGuestsHaveLeft)` (which exits the party) it falls through, and with the hosts off
  the lot it also runs `AllHostsHaveLeft` on the ended party. CleanUp runs twice; the party-end message itself is guarded
  (`mbPartyFinishedCalled`). `AllHostsHaveLeft` sends the remaining guests home without `RemoveGuest`, so they are never
  scored.
- C2 `Pulse` turns a household-member guest into an other-host by calling `RemoveGuest` first. That scores the member and
  changes its liking with its own household's host.
- C3 Invitation delivery (`TimeToDeliverInvitations`) and `BirthdayParty.FindBirthdaySims` loop over `Guests`, which is
  empty until the invite step, so the invitation call to a playable guest never fires.
- C4 `TimeToInviteGuests` adds the result of `Instantiate` without a null check. A failed instantiate puts null into Guests,
  and the next Pulse throws and ends the party.
- C5 After a load mid-party, the host-died listener (`SetUpHostDiedListener`) is not re-added: HouseParty and PoolParty
  have no PostLoad, and SlumberParty re-adds its age-up listener only for old saves [inferred from Birthday/Wedding, which
  do re-add].
Other party types and lot types (out of the reduced scope):
- C6 Birthday and Wedding parties never remove their EventTracker listeners. A guest of an old wedding who marries later
  gets `RemoveGuest` on the dead party (a liking change with its host). A fix was built and then dropped when the scope was cut.
- C7 University: the per-guest party bonus adds guests + 10 instead of guests x 10, and it runs after the outcome is set.
- C8 CampaignFundraiser `OnSimGoingHome` uses Dictionary.Add per Sim, so a second leave throws inside Pulse, which ends
  the party with no outcome. SlumberParty reads a guest's visit without a null check.
- Not audited: houseboats and resorts, dorms and greek houses, festival lots, Late Night venue parties, and the room
  bookkeeping on stairs and elevators. `PushComeToParty` only tells residential lots from community lots.

## 5. Checks

- `PartyFixCheck tuning`: the tuned thresholds, plus verdicts for whether the bug is live with them (BUG LIVE / N/A).
- `PartyFixCheck outcome`: the outcome now for every running party, and per guest the answered tier vs the fixed tier order
  (`DIFFERENT (EA order)` shows off only).
- `PartyFixCheck motives`: guests who left a running party and still carry the motive (BROKEN off / FIXED on).
- Repro `party-outcome-motive` throws a real party with `ApartmentLeaveCheck throw` and ends it with `partyend`.

## 6. First in-game run (2026-11-14, Bridgeport)

`PartyFixCheck tuning`: lame 10, lameEP9 64, great 65, amazing 90, killer 120, raging 150, EP9 installed, not a University
world. So both partyoutcome bugs are live in this install: any party scoring below 64 is Neutral, never Lame, and no
leaving guest ever gets an Amazing/Killer/Raging message. `outcome` / `motives` read N/A because `throw` refused (selected
Sim away). They now have a deterministic `force` / `forceresult` mode (forced score, thresholds set around a mood for one call,
one NPC leaves a Happening party early through EA's RemoveGuest), and repro `party-outcome-motive` uses it.

Second in-game run (phase-20261114-2224): partymotive off BROKEN / on FIXED (2403 +1). The two partyoutcome force lines gave
the fixed answers (kBadParty, kAmazingParty) in **both** arms with 2400/2401 at +0, so EA's body never returned the wrong
tier. Cause [verified, IL of the loaded assembly]: this install runs simler90's GameplayCoreMod UPDATE242, which replaces
`Sims3GameplaySystems`; its `GetPartyOutcome` puts the University-world test inside the EP9 lame condition (scores below
lameEP9 outside a University world fall through to the base lame test) and its `GetPartyOutcomeForSim` tests Raging, Killer,
Amazing before Good. So both partyoutcome bugs are EA-only and already fixed by that core mod; the fix stays idle beside it
(no counter moves, same answers). A score between lame and lameEP9 (e.g. 30) does not separate the arms: EA, the core mod and
the fix all give Neutral there (the fix restores EA's base lame threshold, not lameEP9). The force verdict now prints EA's and
the fixed answer, the counter delta, and **NOT TRIGGERED** when the fixed answer comes without a counter move (both jobs N/A
on this install per AGENTS.md; BROKEN/FIXED needs a profile without the core mod).
