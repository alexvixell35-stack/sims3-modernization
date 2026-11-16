# 854. `fakemetaseen`, `fakemetabackoff`, `placeholderunseen`: no full Sim builds for fake-autonomy Sims and NPC children on unseen lots (2026-11-16, contributor, default off)

Code: `mods/ts3fixes/src/FakeMetaSeen.cs`, `PlaceholderUnseen.cs`; tunables `kFakeMetaSeen`, `kFakeMetaBackoff`, `kPlaceholderUnseen` (False); counters 3720-3731 (docs/registry.md). The long design comments in both files are the detail; this page is the summary. Tags: [verified] = decompiled scripts read locally (not committed) or logs; [inferred] = reasoning.

## 1. `fakemetaseen` / `fakemetabackoff`

EA's `FakeMetaAutonomy` service spawns pool Sims onto random community lots (with a rabbit hole that offers a meta interaction, or a park) between its start and end hour, at most `kMaxNumAssignedSims` at once: `FindAssignment` picks a lot, `FindAssignmentOnLot` takes a pool Sim or makes one and instantiates it hidden at the lot's NPC start point (a full Sim build), then `FakeMetaAutonomySituation` runs a trip. Every route failure exits at once and the Sim is destroyed; the next Simulate spawns the next one [verified]. Local Bridgeport game: 101 of 185 such Sims dead within 30 sim minutes, 27 plans on one big park failed.

- `fakemetaseen`: after EA's own hour / max-assigned gate and lot list, only lots that are seen (`UfLotSeen`: camera lot, active household's lot, lot circle reaching into the view or the 80 m household viewer rule) or that hold an active-household or selected Sim stay; EA's random pick and EA's `FindAssignmentOnLot` follow. No seen lot: false, as EA returns with no lot. Off, not live, travelling, world going away or a failed reflection lookup: EA's own body. Only service pool Sims are involved; a lot with an active-household or selected Sim always gets EA's behaviour. Cost: a lot the household walks onto has no new fake-autonomy Sim until the service's next Simulate (one service per 30 ticks).
- `fakemetabackoff` (alone or with the first): a Sim that lived under 25 sim minutes (a successful visit lives at least 30) holds its lot out of the list for 3 sim hours; never a lot with an active-household or selected Sim; static, keyed by ids, cleared per world load, nothing persisted.

## 2. `placeholderunseen`

An NPC household's child becomes a placeholder when the caregiver monitor finds no caregiver on the home lot; `CheckForCaregiver` rematerializes it (a full Sim build) as soon as any teen-or-older household Sim stands there, and the monitor restarts at every adult route with a child on the lot, so each adult round trip rebuilds every child (local census: 76 builds of 4 children on Isla Paradiso, 30 Twinbrook, 23 Bridgeport per game). With the switch, `CheckForCaregiver` keeps the placeholder while the home lot is unseen (not `UfLotSeen`, circle plus 40 m out of view) and no active-household or selected Sim is on it; EA's loop runs when the lot is seen or near, so the child is back within one 5-minute alarm. Moving, the social worker and the household becoming active do not go through `CheckForCaregiver` and are untouched. While kept, the caregivers' child care, the child's motives, skills and socials do not run, as in EA's own placeholder state [inferred]. The active household (or none) is never kept.

## 3. Evidence

- `fakemetaseen` [measured, one local 3-game run, bundled with 7 other changes]: `Service.CreateNpcSim` filler builds Bridgeport 190 -> 0, Twinbrook 238 -> 0; all instantiations BP 446 -> 306, TB 395 -> 188, IP 462 -> 243; counters: 249-254 asks per game, all skipped (no seen lot), 0 allowed. Frame numbers fell a lot but cannot be credited to this switch.
- `fakemetabackoff`, `placeholderunseen`: never turned on.

Deciding tests (not run): `fakemetaseen`: Twinbrook in-load A/B alone, 2 games both orders (comp + simsvc ms/min, frames over 50 ms, filler instantiations). `fakemetabackoff`, `placeholderunseen`: one smoke game each (counters fire, no errors), then an A/B.
