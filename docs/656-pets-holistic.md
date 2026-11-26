# 656. NPC pets and animals: system map, measured cost, shared roots, fix set

2026-11-19 (UTC). Static (decompiled script code of the 1.67 game assemblies, read only, never in git) plus the OFF / sweep logs of
the `phase-2026111[0-9]-*` run folders (health.jsonl, ts3native(-full).log). Branch `feat/pets-holistic`. Built, all new
switches default off. In-game runs s134 / s136: §9 (petlonghome and petlongvisit act; no proven off/on verdict). Slots 4170-4180 and
4196-4198 (4181-4195 belong to unseenboatwide), plus 4620-4630 for the switches of §8. Package tag `pets`.

Switches: `petarrivepoint` (§4.1), `strayleave` (§4.2), `petlongvisit` and the petlonghome no-spot memo (§4.3). Agent repro
`pet_repro` (§6). Earlier pet switches stay as they were: `petstartleskip`, `petlonghome` (§8.2), `wildlotskip`
(§8.3), `wildhorselot`, `petpoolghost`, `petpoolorphan` and the batch pet fixes.

## 1. System map [verified, decompiled unless marked]

**Who the animals are.** Cats, dogs and horses of households (owned pets; `SimDescription.IsPet`), and pet-pool animals in
`PetPoolManager` pools (`PetPoolType`: StrayCat, StrayDog, AdoptCat/Dog/Horse, WildHorse, Unicorn, Deer, Raccoon), each
made by its `*Creator` class and living in the pet household. Pool animals have no `LotHome`; their "home" is
`GoToVirtualHome` (walk off and de-instantiate).

**Strays** (`StrayPets`): an hourly global alarm (`StrayPetHelper`) picks a lot (the active household's lot after
`kActiveHouseholdMaxTimeBetweenVisists` hours or by chance, else a random lot) and, while fewer than `kMaxStraysInWorld` (6)
strays are instantiated, instantiates one off screen (`SendStrayToLot`) and pushes `StrayGoToLot` -> `GoToLot`. On
arrival (`OnGoToLotSuccess`) the stray gets the `StrayPet` buff for 30-90 sim-minutes; `BuffStrayPet.OnTimeout` calls
`Sim.MakeSimGoHome` (-> `GoToVirtualHome`). That timeout is the **only** exit of a stray's visit: meta-autonomy's own
go-home in `Autonomy` runs only when the search picked nothing, and a pet always has Sit On Ground.

**Wild horses** (`WildHorsePool`, `WildHorses`): a herd led by one horse; `HerdBehaviorCallback` (repeating alarm) picks the
next lot with `WildHorses.FindGoodLot` (weights, no reachability test) and pushes `GoToLot` on the herd. **NPC animals**
(deer, raccoon; `NPCAnimal` role): `SimulateRole` picks `GetRandomLotToVisit` (weighted by `ScoreLot`, no reachability test)
while on the street. **Unicorns** (`UnicornPool`): EA's own spawn and summon behaviour; this doc leaves them alone.

**Owned NPC pets**: autonomy (pet commodities, `PetSittingOnGroundPosture` idles), `GoHome`, `VisitCommunityLot` (on their
own or as followers: `FollowerMeetUpAtPoint`, `PetFollowSimAround`), `PetStartle` (`PetStartleBehavior.StartlePet`,
startled by TVs, stereos, showers ...), socials between pets (`SocialInteractionA/B`, `PetInitateSocial`), horse riding and
leading (`Terrain.RideHere`, `HorseManager.NpcShouldRideHorse` adds the horse as a GoHome follower), `ReactToFire`, `Jump`.

**Portal rules per species** (the heart of it):

| Portal | Refuses | Source |
|---|---|---|
| Doors and gates (`CommonDoor.IsAllowedThrough`) | horses at every door that is not a barn door, and at gates that are not `GateDouble` / `IElectronicGate` with `CanLargeAnimalUse`; strays at every door of a lot that has not greeted them (`IsStray && !IsGreetedOnLot`); pets at a door locked "Pets"; plus the lock owner rules | CommonDoor.cs |
| Stairs (`Stairs.IsAllowedThrough`) | puppies, kittens, horses, wild animals | Stairs.cs |
| Elevators (`ElevatorDoors`) | horses, toddlers, wild animals | ElevatorDoors.cs (also docs/580 §11, `PetHomeCensus`) |
| Box stalls, horse jumps, fire poles | their own tests | `Sim.OnPortalPathPlan` |

`Sim.OnPortalPathPlan` adds only **Door** portals to `SimRoutingComponent.LockedDoorsDuringPlan`. So a PortalLocked plan that
failed on stairs, an elevator or a stall shows `locked doors 0` in our `routecause:` lines; that is why most pet lines
read "PortalLocked, locked doors 0" (§2).

**Lot arrival** (`Lot.PlanRouteIntoLot`, used by `GoHome.Run` and by `RouteToFrontDoorOrMailbox` when the Sim wants to be
inside): first a plan to the lot's cached `mBestRouteIntoLotLocation`, validated once per lot for whoever asks first. On a
lot without apartment slots that point is a routing slot of the front door **inside** the house; if the validating plan
fails, EA sets the point Invalid for every Sim after it (`BestRouteIntoLotLocationValidated` stays true until build mode).
Then `PlanToMailbox` (horses with the wider 0-5 m `kMailboxHorseRouteMin/Max`, preferring far from origin) and
`PlanToLotEx` (entry points in room 0, then `PlanToLotCore`). `GoToLot.Run` for a residential lot uses
`RouteToFrontDoorOrMailbox(wantToBeOutside: true)` (outside of the front door), else `PlanToLotEx`.

**Placement on reset** (`Sim.AttemptToFindSafeLocation`, `FindRoutablePointInsideNearFrontDoor`, Overwatch
`InstantiateAtHome`): the home lot's front door, checked for routability from the spot (the Sim's own route, so species
aware), with the mailbox as an unchecked last resort.

**Our own NPC machinery and animals** [verified, source]: `UnseenFallback.UfPreSim` refuses pets (no unseen plan cuts;
pet plans show as `ufplan ... src pet`), `GoHomeHook.Unseen` and `ShortcutToLot` refuse any Sim whose household has pets
(§8.2), so animals get only `petlonghome` (GoHome to the home lot, wild-horse GoToLot), `petstartleskip`,
`wildlotskip`, `wildhorselot`. The watchdog's "stationary a sim-day" reset (RouteFixes.cs counter 48) spares horses on the
world lot only.

## 2. Measured cost, OFF (household runs only; no-household runs skipped)

Scripts (scratch, not in git): `measure.py` (ufplan / routefail / routecause by species), `health.py` and `still2.py`
(health.jsonl), `rc.py` (routecause), `track.py` (one pet's trajectory).

**Slow plans (ufplan >= 150 ms) whose follower is an animal** [measured]:

| World | pet plans | pet ms | all ms | share | >= 300 ms |
|---|---|---|---|---|---|
| Bridgeport | 923 | 224 340 | 51 303 132 | 0.4 % | 158 |
| Isla Paradiso | 698 | 192 544 | 70 253 608 | 0.3 % | 209 |
| Starlight Shores | 176 | 44 954 | 249 242 | 18 % | 29 |
| Moonlight Falls | 79 | 18 518 | 66 192 | 28 % | 16 |
| Twinbrook (s130 OFF arm) | 19 | 4 145 | 15 681 | 26 % | 3 |
| Twinbrook (s130 ON arm) | 27 | 6 590 | 11 043 | 60 % | 5 |

In the big-town saves pets are a small slice; in the quieter worlds they are a quarter of the slow plans, led by wild
horses (Starlight 35.5 s, Moonlight 11.8 s, "Wild Horse" alone). The s130 ON arm shows petlonghome's own cost: about 1.3 s
of `src spot:petlonghome@<lot>` SafeSpot plans (180-500 ms each), one per herd member, 15 of them refused "no safe spot"
(§4.3).

**Failed route jobs (native `routefail job`) of animals**, top by ms: Bridgeport dog GoHome NoPath 6.6 s (7), horse
GoHome PortalLocked 3.6 s (70), dog SocialInteractionB 5.6 s (162), horse FollowerMeetUpAtPoint 2.0 s (64), dog / horse
PetStartle 2.6 s (24); Appaloosa Plains horse ReactToFire PortalLocked 1.9 s (29), horse Jump NoPath 2.3 s (8); Isla
Paradiso cat / dog GoHome NoPath (260 jobs, 1.1 s).

**Why they fail** (`routecause:` lines of animals, all runs, 194 lines): PortalLocked in 185. GoHome 74, GoToLot 62
(Meeko / Meeka Raccoon, wild horses), VisitCommunityLot 22, FollowerMeetUpAtPoint 11 (horses), PetFollowSimAround 8,
PetStartle 6, RouteAway 7. `locked doors 0` in 180 of them: the refusing portal is not a locked door but a species rule
(§1: a horse at a house door, stairs for horses, kittens, puppies and wild animals).

**Symptoms** [measured, health.jsonl, 2026-11-10..19 household runs]:

- Standing still > 12 h with no interaction: **71 %** of (run, NPC pet) pairs vs 23.5 % of NPC humans. Almost all with no
  route failures in the window: they run 5-6 short interactions per sample (Sit On Ground, Stand), in one place (CC Cat
  83 h, Dink Cat 76 h, Mothball Lionheart 81 h). Owned pets at their own home: EA's NPC pet autonomy, no route cost.
  Strays without the StrayPet buff: never leave (§3.2).
- Route-locked snapshots: Twinbrook Wild Horse 80, Meeko Raccoon 49, Meeka 17, deer 22; Appaloosa wild horse 26. By
  interaction: horses with no head interaction on lot terrain 191 (between interactions, failing again and again).
- Long walks > 6 h: 119 (Go Home 64, Go to Lot 19, Visit Buena Vista Resort 18, Visit other lots 15): Minnie Cat "Visit
  Eugi's" 2203 sim-min, Tigger Cat Go Home 2110, Meeko Raccoon Go to Lot 1860, Snowshoe Cat Visit Buena Vista 1793,
  Majora Cat Go Home 1777, Cookie Horse Visit Bridgeport Sports Zone 1106.
- Cookie Horse (Bridgeport 1115-2218): home lot Cliff Cottage up a cliff (y 86.6). GoHome PortalLocked; at 5:30 Overwatch
  `InstantiateAtHome` puts it on its home lot; from there every route out fails ("routefail summary: Cookie Horse townie
  seen 140"), the top route-failer of the session; our watchdog resets it each sim-day to the same lot.

## 3. Shared roots

**Root A: animal destinations and placements are chosen with human reachability [verified, decompiled].** The portal rules
are species-specific (§1 table), but every picker of a goal or a spot for an animal ignores them: the lot arrival point
(an inside slot of the front door, cached per lot for everyone), `FindGoodLot` / `GetRandomLotToVisit` (no reachability),
the startle flee, `PlanToLotCore`, `InstantiateAtHome` / resets (the home lot, whatever its stairs), and even which
households get a horse (a cliff lot or apartment). The planner then answers PortalLocked after a full-length plan,
uncut (animals are outside our unseen cuts), and the animal retries. Each symptom of the brief is a case of it:
horses `route_locked` (stairs and doors), startled pets fleeing through pet-locked doors, wild animals picking lots
reachable only by stairs, GoHome's first plan aiming inside a house a horse or a stray cannot enter, and Cookie Horse put
back on a home it can never leave.

- §3.1 (fixed by `petarrivepoint`): `PlanRouteIntoLot`'s first plan for an animal the front door refuses is a guaranteed
  PortalLocked plan, and when that animal is the first to ask on a lot, EA throws away the point for every human after it.
- §3.4 (open): a horse whose home lot is reachable only through stairs, an elevator or a door; EA's reset places it there.

**Root B: an animal's trip has no exit except its timer or its route [verified].** Humans have our shortcuts, cuts and
deadlines; animals only EA's timers. §3.2: a stray leaves only by its buff's timeout, so a stray that never got the buff
or lost it (resets clear buffs; NRaas GoHere notes that EA's StrayGoToLot failure callback can fire at once, `StrayGoToLotEx`
[community]) stays for days and blocks new strays (`kMaxStraysInWorld`). §3.3: pets' own `VisitCommunityLot` walks have no
shortcut at all (petlonghome covers GoHome / GoToLot only).

## 4. Fix set (all default off; household, selected and unknown-scope animals exempt via `HouseholdExempt` / `IsPlayerSim`)

### 4.1 `petarrivepoint` (`PetArrivePoint.cs`, rule `PetArrivePointCore`, slots 4170-4173)

`Lot.PlanRouteIntoLot(Route, bool, Sim)` hooked. For a non-human, non-exempt actor on a lot without apartment slots whose
front door EA's `CommonDoor.IsAllowedThrough(actor)` refuses, and with something to skip (a cached point, or a lot not yet
validated), only EA's own tail runs: `SetValidRooms(lot, null)`, `PlanToMailbox` (private, by reflection) with EA's
distances (horse ones for a horse), then `PlanToLotEx` when `planToLotAsFallback`. The lot's cache is never touched: an
unvalidated lot stays for the next Sim to validate. No teleport, the same EA plans minus one that cannot succeed through
that door. [inferred] the inside point could still be reached through another door the animal may use; for an NPC animal
the mailbox / lot goal is then taken instead, which is where EA ends after a failed first plan anyway.
Counters: 4170 inside points skipped, 4171 of those on an unvalidated lot, 4172 exempt, 4173 errors. Log
`petarrivepoint: <animal> (<species>) -> <lot>: front door closed to it, inside point skipped|not validated by it, EA's mailbox / lot plan <result>` (10 a minute).

### 4.2 `strayleave` (`StrayLeave.cs`, rule `StrayLeaveCore`, slots 4174-4177; `kStrayLeaveHours` 3)

`StrayPets.StrayPetHelper` (EA's hourly alarm) hooked; before EA's body each instantiated stray is looked at: no StrayPet
buff, not leaving or on a trip (GoHome, GoToVirtualHome, GoToLot, StrayGoToLot, ReactToStrayGnome current or queued), not
in a social, not held, for `kStrayLeaveHours` sim-hours -> EA's `Sim.MakeSimGoHome(sim, false)`, exactly what the buff's
timeout does. A walk, no teleport. A failed walk home waits a whole window again. Counters: 4174 sent home, 4175 strays
seen without the buff, 4176 exempt, 4177 errors. Log `strayleave: <stray> (<species>) on <lot>: no StrayPet buff for H sim-h, EA's MakeSimGoHome pushed`.

### 4.3 `petlonghome` additions: `petlongvisit` and the no-spot memo (`PetLongHome.cs`, slots 4178-4180)

- `petlongvisit` (`kPetLongVisit`): `VisitCommunityLotHook` calls `PlhBefore(..., "visit")` when the visit has no
  followers. An owned NPC pet (not a stray, wild horse, unicorn or pet-pool animal) going to a community lot gets
  petlonghome's rule: long trip (> `kPetLongHomeMeters` or > `kPetLongHomeHours`), unseen pet and lot, SafeSpot, PlaceSim;
  resorts, diving lots, houseboats, apartment shells and the active household's lot keep the walk. 4179 placed, 4180
  refused; exempt 4624 and errors 4625 shared with petstartleskip (§8).
- No-spot memo (`kPetLongHomeNoSpotMinutes`, default 0 = search every time as before): a (lot, horse / other) whose
  SafeSpot found nothing is refused without a new search for that many sim-minutes. One search per herd member at a lot
  with no spot is what cost the s130 ON arm 1.3 s. 4178 searches saved.

### 4.5 Herd spot share (`PetLongHome.cs`, slots 4196-4198; only with `kPetLongHomeNoSpotMinutes` > 0)

s136 (Appaloosa Plains, phase-20261119-2045, household Fox, memo 60): pet slow plans did not drop (OFF 56 / 12.4 s, ON
63 / 13.9 s). **What the "pet Wild Horse" plans are** [verified, decompiled `WildHorsePool.HerdBehaviorCallback` + log]:
the herd alarm (every `kHerdBehaviorFrequency` 120 sim-min) picks a lot with `WildHorses.FindGoodLot` and pushes EA's
`GoToLot` on **every** herd member (each plans its own 450-880 m walk, 150-400 ms; three in one tick at line 817), plus
`GoToLot` to the leader's lot for members elsewhere and `RouteToWildHorse` (30-60 m) near the leader. Who covers them:
`wildlotskip` / `wildhorselot` only change which lot is picked (unreachable ones), `petarrivepoint` only
`Lot.PlanRouteIntoLot` (GoHome; the herd's GoToLot does not use it), `petlonghome` places them (via gotolot) when its spot
search finds a spot. In the ON arm 23 of the 28 Wild Horse walk plans followed a petlonghome refusal "no safe spot", 16 of
them "(remembered)", while other members of the same herd were placed on the same lot (Cinnamon Crest Falls, Water
Treatment, Wolfson's). Cause [verified, log order + SafeSpot source]: the members' searches overlap (spotasync lets one
search wait on its job while the next member starts its own on the same lot), so each member pays a 150-400 ms check
("spot:petlonghome" 9 / 2.0 s), and a member whose search ended without a spot (the unseen gate failing during the wait
also returns false) noted "no spot" while the others succeeded; the later members then walked.

Fix (bug fix, same placement rule): a spot SafeSpot just verified for (lot, species class) is handed to the next pet of
that class while the memo life lasts, after cheap checks only (still on that lot, unseen; 4196); a pet whose search would
start while another task's search of the same key runs waits for it in its own task (up to 40 sim-ticks, then re-checks
its unseen gates; 4197); a failed search whose unseen gate is gone afterwards is not noted as "no spot" (4198); a found
spot clears and beats a parallel "no spot". Species classes are now horse, big dog, little dog, cat. Herd members share
one spot (they stand together; EA's RouteToWildHorse spreads them). Self-test cases in `tools/selftest/petlonghome`.

### 4.4 Not changed, and why

- Owned NPC pets idling at home for days (§2): EA's pet autonomy; no route cost, nothing broken a player sees.
- Root A §3.4, horses whose home is unreachable (Cookie Horse): needs a placement policy (where to put a horse whose
  home lot has no horse path) and a check of the lot's stairs / door graph; EA's reset and Overwatch put it back. Lead
  for the next round: on an NPC horse's GoHome PortalLocked at its own lot, remember the lot and let `petlonghome` /
  resets use the mailbox spot after a species routability check. `PetHomeCensus` (docs/580 §11) lists such horses.
- Startle flee (`petstartleskip`), wild lot picks (`wildlotskip`, `wildhorselot`): already address the same root at the
  pick; kept as they are. Their repros are still missing (`fix-repro-check.ts` lists petstartleskip).
- Pet socials (dog SocialInteractionB 162 failed jobs in Bridgeport) and horse FollowerMeetUpAtPoint / ReactToFire
  PortalLocked: same root (goal behind stairs or a door), not fixed here; the `objfailmemo` / `poolfailmemo` pattern is the
  natural place.
- `routecause:` should name stairs / elevator refusals too (today only doors are listed): a log-only follow-up.

## 5. Unicorns

No new switch touches a unicorn: `petarrivepoint` only changes a plan an animal cannot complete (unicorns have no home
lot with a front door; a unicorn that had one would get EA's own mailbox plan), `strayleave` reads only the stray pools,
`petlongvisit` refuses unicorns.

## 6. Repro (`pet_repro`, `PetReproHandlers.cs`; entries in `tools/harness/repros.json`)

`pet_repro {mode, start: true, seconds} | {result: true} | {stop: true}`; result: pet, lot, current interaction, reached /
left and when, counter deltas 4170-4180 and 4622. Same steps in both arms, only the `patches` switch differs (`$on`):

| Repro id | Mode | OFF | ON |
|---|---|---|---|
| `pet-arrive-point` | arrive: `spawn_horse`, an NPC horse whose home front door refuses it, set at a lot 60-250 m away, EA's GoHome (routecauselog on) | `routecause: <horse> npc, GoHome, Plan FailedBecauseDestinationPortalLocked` | 4170 >= 1, `petarrivepoint:` line, no such routecause line |
| `pet-stray-leave` | stray: EA's SendStrayToLot to the active lot, StrayGoToLot cancelled (no buff), `kStrayLeaveHours` 1 by `reflect_set`, ultra speed 420 s | left false | 4175 +1, 4174 +1, `strayleave:` line, left true |
| `pet-long-visit` | visit: an owned NPC pet at a lot >= 250 m from the active Sim, EA's VisitCommunityLot to a far community lot | walks | 4179 +1, `petlonghome: placed ... via visit` |
| `pet-long-home` | home: the same, EA's GoHome to its far home lot (petlonghome, §8.2) | walks | 4622 +1, `petlonghome: placed ... via gohome` |

Run as an off/on pair: `node tools/harness/queue.ts` with a job per arm (`fixesOff` / `fixesOn`, or the repro's `$on`
resolved per arm), BP save; a pick that finds nothing is "not triggered" (N/A), not FAIL.

### 6.1 First run, s133 (Bridgeport, phase-20261119-1902): the repro stressed nothing; reworked

- arrive: no pick in either arm (no NPC horse at a home whose front door refuses it, with a lot 60-250 m away, even after
  `spawn_horse`). In the ON arm `petarrivepoint` never fired, also not on Cyan Horse's own GoHome PortalLocked to Still Water
  (4170-4172 all 0): EA's `Decide` inputs there are unknown (most likely the lot was already validated with no point, the
  case EA itself skips, or `FindFrontDoor` is a gate). The step now builds its case (§6 header comment): a horse with a
  refusing front door, else a cat or dog behind EA's Pets lock on its own front door, the lot's point marked unvalidated,
  40-145 m from home; `before` / `after` report the hook's inputs (validated, has_point, front door type, door_allows, lock).
- home: `reached_s` 0 in both arms because the timer started before Teleport Me Here ran (the OFF tick saw Cyan Horse still at
  home; the ON arm's 0 s was a real petlonghome placement, 503 m). `reached` latches, so the end lot (Marina Field) differed.
  Now: push and timer only once the pet stands at the start spot (30 s setup limit, else error), plus `on_target_now`,
  `push_dist_m` and `jumped_s` (first move > 30 m between samples = a placement).
- visit: Cupid Horse 295 m, 26 s (horses gallop), ON refused "lot seen". Now cats and dogs first, 400-1500 m, both lots >= 300 m
  from the camera target and the active Sim. Counters stay global (4180 +2 came from other pets); `jumped_s` is per pet.
- Pennie Belle placed 4 times at Still Water via gohome (744, 728, 735, 605 m): day 3 20.5 h, day 5 10.4 h, day 5 21.8 h, day 6
  23.7 h, socials at home in between. A daily trip out and back, not a loop; no cooldown added.
- `refused Riley Muse -> Terpsichore Studios: houseboat or apartment`: a pet (the hook returns earlier for non-pets); the line
  now carries species, distance and via like the other refusals.

## 7. Tests

Offline: `TS3_CSC=~/.local/bin/ts3-csc node tools/selftest/petarrivepoint/run.ts`, `.../strayleave/run.ts`,
`.../petlonghome/run.ts` (with the no-spot memo cases) pass. Build: `node tools/setup/build-fixes.ts --tag pets`
(refs-resolve-check 0 unresolved, check-live-map ok), `node tools/harness/build-agent.ts`; `fix-repro-check.ts` covers
the three new switches (routecauselog's exemption dropped: `pet-arrive-point` turns it on).

References: NRaas GoHere `StrayGoToLotEx` / `RouteStrayToLot` (stray visit callbacks) read for behaviour only; nothing
ported.

## 8. Moved here: `petstartleskip`, `petlonghome`, `wildlotskip` (were docs/645 §3-§4 and docs/646 §3 on the combo branch)

On the contributor's combo branch these three were written up in docs/645 (NPC loop fixes, batch A) and docs/646 (trash
and wild lots) next to non-pet fixes, at slots 4074-4079 and 4085-4089. Upstream already uses those slots, so here they
are 4620-4630 and their text lives in this doc:

| slot | switch | meaning |
|---|---|---|
| 4620 | petstartleskip | NPC pets recorded after a startle flee route failed with PortalLocked |
| 4621 | petstartleskip | startles skipped |
| 4622 | petlonghome | unseen NPC pets / horses placed on the lot of a long GoHome / GoToLot walk |
| 4623 | petlonghome | refused after the long-trip gate: pet seen, lot seen, no safe spot, or a lot kind the shortcuts skip |
| 4624 | petstartleskip, petlonghome, petlongvisit | exempt: a household, selected or unknown-scope Sim met, nothing changed |
| 4625 | petstartleskip, petlonghome, petlongvisit | errors (caught; EA's code ran unchanged) |
| 4626-4630 | wildlotskip | pairs remembered / picks drawn again / every lot remembered / long walks cancelled / exempt or errors |

Agent `patches` read-back: `petstartleskip_recorded`, `petstartleskip_skipped`, `petlonghome_placed`, `petlonghome_refused`,
`pets_exempt`, `pets_errors`, `wildlotskip_*`.

### 8.1 Startled pets flee into pet-locked doors again and again: `petstartleskip` (`PetStartleSkip.cs`)

**Root cause [verified, decompiled + logs]:** every pet startle goes through
`PetStartleBehavior.StartlePet(Sim, StartleType, Origin, GameObject, bool ignoreCooldown, PetStartleReactionType,
InteractionPriority)` (CheckForStartle from TVs, stereos, showers, dishwashers, toilets, washers; litter boxes, horse jumps,
water troughs). It pushes `Sim.PetStartle`, whose run-away route (CatDogCoward, HorsePanic, deer) can lead through a door
locked for pets: the plan fails with `FailedBecauseDestinationPortalLocked` (`routecause: <pet> npc, PetStartle, Plan
FailedBecauseDestinationPortalLocked, ...`). After EA's per-pet cooldown (`GetStartleCooldownTime`) the next noise pushes the
same startle and the same failing flee (OFF runs [log analysis of the sweep]: Bridgeport 321 in 40 runs, Isla Paradiso 251,
Twinbrook 174; `routefail job: ... PetStartle` results in the sweep: 1445 x 10, 350 x 16, 90 x 18, 15 x 1).
`ReactToDisturbance.NoiseBroadcastCallback` (:127) only stops a second startle while one is queued.

**Fix (default off):** `RouteFailLog.RflPlan` calls `PssPlan` for failed plans: when the Sim is a pet, its current
interaction is `Sim.PetStartle` and the result is PortalLocked (18), the pet is remembered for `kPetStartleSkipMinutes` (30)
sim-minutes. `StartlePet` (the 7-argument funnel, `PetStartleSkipHook`) then returns at once for that pet, as EA itself does
inside its cooldown. Forced startles always run: `ignoreCooldown`, Electrocution, GrimReaperAppear, Fire, Meteor. Only the
PortalLocked case is recorded (the brief's evidence); NoPath / NoDestination startle fails are left to EA. Household, selected
and unknown-scope pets (`HouseholdExempt` or `IsPlayerSim`) are never recorded and never skipped (4624, shared with petlonghome). A skip does not
extend the window. Rule: `PetStartleSkipCore`.

**Log:** `petstartleskip: recorded <pet> (<species>), flee route PortalLocked, startles skipped 30 sim-min` and
`petstartleskip: skipped <pet>, <StartleType>`.

**Proof:** OFF arm on a lot with a pet door locked for NPC pets and a startle source (TV / stereo turned on near an NPC pet):
repeated `routefail job: ... PetStartle ... result 18` for the same follower. ON arm: one `recorded` line, then `skipped` lines
and no new PetStartle PortalLocked fails for that pet for 30 sim-minutes; 4620 / 4621 grow, household pets unchanged, no new
exceptions. Self-check: `tools/selftest/petstartleskip` (PortalLocked only, pets only, forced and exempt startles run, life
window, bounded table).

### 8.2 NPC pets walk home for a whole day: `petlonghome` (`PetLongHome.cs`)

**What the runs show [verified, health.jsonl + logs]:** NPC pets and horses walk home on foot at pet speed (about 1 m a
sim-minute) for 10-35 sim-hours: Tigger Cat 2110 min (Bridgeport), Majora Cat 1777 min (Sunset Valley), Lucky Coyle 816 min
(Isla Paradiso, a real pet, not a harness spawn). health.jsonl samples of NPC pets whose interaction had run over 300
sim-minutes in the sweep: "Go Home" 113 cat, 52 dog, 13 horse; "Go to Lot" 33 (wild horses). Only the 5:30 Overwatch reset
(`owreset`) ends those walks.

**Why our shortcuts never helped [verified, code]:** `GoHomeHook.UnseenBody` and `ShortcutToLot` refuse any Sim whose
household has pets (`sim.Household.Pets.Count > 0`, meant to keep a human's horse with it), which is every owned pet itself.

**Fix (default off):** at the start of `GoHome.Run` and `GoToLot.Run` (`GoHomeHook.RunHook`, `GoToLotHook.RunHook`, before the
human shortcuts) `PlhBefore` runs for pets only (`SimDescription.IsPet`: cats, dogs, horses). Gates, in order
(`PetLongHomeCore.Pre`): not exempt (`HouseholdExempt` or `IsPlayerSim`: a pet of the active household, the selected Sim or a
Sim of unknown scope is never touched, 4624), not held (`UfHeld`: carried, ridden, riding, carrying; or any parent, e.g. a
leash jig), no followers, not already on the lot, and a long trip: straight distance > `kPetLongHomeMeters` (150 m) or this
trip (same pet, same lot) first seen more than `kPetLongHomeHours` (2) sim-hours ago. Then the lot kind (the active
household's lot, houseboats, apartment shells, resorts and diving lots keep EA's walk) and the unseen checks the trip
shortcuts use (`PetLongHomeCore.Post`): `TownieUnseen` for the pet, `FarFromViewers` for the lot and its entry plus
`UfGoalUnseen(entry)`, `SafeSpot(sim, lot, lot.EntryPoint(), ...)` on the target lot (its home lot; for an owned pet the owner
household's lot, which is never the active one here) and `UfGoalUnseen` for the spot. Then `PlaceSim`, the same placement the
human trip shortcuts use, and EA's Run walks the last metres as usual.

The trip-age rule only sees a new Run: one uninterrupted Run is judged at its start, where a walk that would last over two
hours at pet speed is already more than ~120-150 m. Rule: `PetLongHomeCore`.

**Log:** `petlonghome: placed <pet> (<species>) at <lot> via gohome|gotolot, <m> m away, trip <min> sim-min` and
`petlonghome: refused <pet> (<species>) -> <lot>: pet seen|lot seen|no safe spot, ...` (or the lot kind). Placements also go to
the shortcut log and `MovedSims`.

**Proof:** OFF arm: an unseen NPC pet sent home from a lot > 150 m away (agent: push GoHome on a non-active household's pet
from a far community lot, camera elsewhere) is still walking (`health.jsonl` "Go Home", interaction_min growing, routing true)
after a sim-hour. ON arm: one `placed` line, the pet on its home lot within a minute, 4622 +1; household pets and pets in view
keep walking; no new exceptions or stuck Sims. Self-check: `tools/selftest/petlonghome` (gate order, distance and trip-age
rules, unseen checks, trip table).

**Unicorns (2026-11-19):** unicorns and strays are never placed. A wild horse on a long unseen GoToLot is placed at that lot (same unseen and distance rules); otherwise only a pet with a real household heading to its own home lot (`lot == LotHome`) is. GoToLot to any other lot keeps EA's walk.

### 8.3 `wildlotskip` (default off; `kWildLotSkipHours` 6, `kWildLotSkipWalkHours` 8)

Code path [verified, decompiled `WildHorsePool`, `WildHorses`, `NPCAnimal`, `Raccoon`]:

- `WildHorsePool.HerdBehaviorCallback` (repeating alarm) asks `WildHorses.FindGoodLot(lead)` when it is time to change lots,
  and again whenever the lead horse stands on the street with no GoToLot queued (`WildHorsePool.cs:135-166`). FindGoodLot
  weights every non-port lot, with no reachability test. A GoToLot that fails at once leaves the queue empty, so the next
  callback picks again, often the same lot.
- `NPCAnimal.GetRandomLotToVisit` (raccoon, deer) weights every lot by `ScoreLot` (Raccoon: x2 with outdoor food).
  `SimulateRole` picks again while the animal is on the street and not in a GoToLot. Nothing limits how long a GoToLot runs.

Fix:

1. Note: a failed GoToLot / GoToCommunityLot plan (`DestinationPortalLocked` or `NoPathToDestination`) of a wild horse
   (`WildHorses.IsWildHorse`) or an NPCAnimal role remembers (animal, lot) for `kWildLotSkipHours` sim-hours; for a wild horse
   also (herd, lot), since the herd follows the lead.
2. Pick: when EA's pick is a remembered lot, the pick is drawn again from the same weights with the remembered lots left out.
   This gives EA's own distribution over the other lots (P(x) = w_x / sum of kept weights; the self-check tests the
   proportions). When every weighted lot is remembered, EA's pick stays. FindGoodLot has no swap of its own: wildhorselot's
   `BEp4WhHook` already patches it, so its body now builds the lists with `WhLists(horse, on)` (on = wildhorselot's
   corrected weights, off = EA's loop exactly) and calls `WlsHorsePick` with the same flag. The two switches compose: the
   redraw uses whichever weights produced the pick. `NPCAnimal.GetRandomLotToVisit` is hooked (EA's pick first, then
   `ScoreLot` via reflection for the redraw only).
3. Long-walk guard: `NPCAnimal.SimulateRole` and `HerdBehaviorCallback` first look at each animal. A GoToLot that has run
   `kWildLotSkipWalkHours` since we first saw it is cancelled with `InteractionQueue.CancelInteraction(ia, false)` (a script
   cancel, not RouteFailed, so NPCAnimal's failure teleport does not run) and its lot is remembered. EA's next look then picks
   another lot.

Household rule: wild horses and NPC animals belong to the pet household. `Fixes.HouseholdExempt` and `IsPlayerSim` still
keep EA for an adopted, selected or unknown-scope animal. A horse with no Sim yet (`SpawnWildHorse`) keeps EA's pick.

Counters: [4626] (animal, lot) pairs remembered, [4627] picks drawn again, [4628] every weighted lot remembered (EA's pick
kept), [4629] long walks cancelled, [4630] exempt or errors.

Log (at most 10 per real minute, 300 per session):
`wildlotskip: <Sim> (wild horse|Raccoon|Deer): GoToLot 0x<id> (<name>) FailedBecauseDestinationPortalLocked, lot skipped for 6 h`,
`wildlotskip: <Sim> (...): 0x<id> (<name>) skipped, drew 0x<id> (<name>)`,
`wildlotskip: <Sim> (...): every lot skipped, EA's pick 0x<id> (<name>) kept`,
`wildlotskip: <Sim> (...): GoToLot 0x<id> (<name>) running <h> sim-hours, cancelled, lot skipped`.

**Unicorns (2026-11-19):** a unicorn is never treated as wild (`IsUnicorn` returns false from WlsWild), and the long-walk guard leaves a wild-horse herd alone while a unicorn is on, or heading to, its target lot (EA's Summon Wild Horses pushes GoToLot on the whole herd).

## 9. In-game runs (2026-11-19, Appaloosa Plains, `pet_repro`, one load per run, off arm then on arm)

| run | phase | mode | off | on |
|---|---|---|---|---|
| s134 | phase-20261119-1941 | home (`petlonghome`) | Ralston Schmidt walked 419 m, reached after 46 s | placed at once (`reached_s` 0, `jumped_s` 0) |
| s134 | phase-20261119-1941 | visit (`petlongvisit`) | walked 590 m, reached after 18 s | placed at once, 4179 +1 |
| s134 | phase-20261119-1941 | arrive (`petarrivepoint`) | no case (the picked horse's lot had no point) | 4170 grew (global count, not tied to the picked horse) |
| s136 | phase-20261119-2045 (no-spot memo 60) | home | walked, 46 s | placed at once |
| s136 | phase-20261119-2045 | visit | walked, 49 s | placed at once |
| s136 | phase-20261119-2045 | stray (`strayleave`) | stray de-instantiated before the check (not triggered) | the same; 4175 +1, 4174 0 |

Errors (4173, 4177, 4625) were 0 in every arm. This is evidence that `petlonghome` and `petlongvisit` act in the game; it
is not a proven OFF BROKEN / ON FIXED verdict: one run per arm, and the herd spot share of §4.5 was built after s136 and
has not run. `petarrivepoint` acted (4170) but not on the repro's own case; `strayleave`, `petstartleskip` and
`wildlotskip` were never exercised (no line in either run).
