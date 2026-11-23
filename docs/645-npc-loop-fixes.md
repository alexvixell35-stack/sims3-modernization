# 645. NPC loop fixes, batch A (`picnicexit`, `poolfailmemo`, `petstartleskip`, `petlonghome`)

2026-11-19. Source: OFF / sweep runs `phase-2026111[5-9]-*` (`ts3native-full.log`, `ts3native.log`, `health.jsonl`),
decompiled Sims3GameplaySystems / Sims3GameplayObjects. All four switches built, default off, NOT RUN. Managed only
(ts3fixes). Package tag `npcloopsa`.

Counter slots 4600-4609 (built as 4070-4079 on the combo branch; moved at the upstream PR because main uses 4070-4089,
docs/registry.md). `picnicexit` and `poolfailmemo` come in the NPC loops PR; `petstartleskip` and `petlonghome` come in
the pets PR, which gives them their own slots (the rows 4604-4607 below are this page's numbering only). Ten slots for four fixes, so the exempt and error
counts are shared and the log lines say which switch they came from:

| slot | switch | meaning |
|---|---|---|
| 4600 | picnicexit | finished picnic situations exited |
| 4601 | picnicexit | finished picnics skipped (a Sim holds a role, or the situation's cleanup interaction is still queued) |
| 4602 | poolfailmemo | NPC GetInPool plans that failed with no path / no destination, recorded |
| 4603 | poolfailmemo | autonomous GetInPool tests refused |
| 4604 | petstartleskip | NPC pets recorded after a startle flee route failed with PortalLocked |
| 4605 | petstartleskip | startles skipped |
| 4606 | petlonghome | unseen NPC pets / horses placed on the lot of a long GoHome / GoToLot walk |
| 4607 | petlonghome | refused after the long-trip gate: pet seen, lot seen, no safe spot, or a lot kind the shortcuts skip |
| 4608 | all four | exempt: a household, selected or unknown-scope Sim met (HouseholdExempt or IsPlayerSim), nothing changed |
| 4609 | all four | errors (caught; EA's code ran unchanged) |

Agent `patches` read-back: `picnicexit_exited`, `picnicexit_skipped`, `poolfailmemo_recorded`, `poolfailmemo_refused`, `petstartleskip_recorded`, `petstartleskip_skipped`, `petlonghome_placed`, `petlonghome_refused`, `npcloops_exempt`, `npcloops_errors`.
Every log line is capped at 10 a minute per switch; the counters are the measure.

## 1. Finished picnics never leave the situation list: `picnicexit` (`PicnicExit.cs`)

**Root cause [verified, decompiled]:** `PicnicBasketSituation.EndPicnic` (EA's alarm at the end of the picnic, or the basket
cleanup interaction calling `PicnicBasket.EndPicnic`) runs `PicnicSituation.EndPicnic` (alarm and broadcaster gone) and
`SetState(new CleanUpPicnic(this))`. The old `BeAtPicnic` state's CleanUp removes every Sim's role and the BeAtPicnic
motive. `CleanUpPicnic.Init` then pushes the basket cleanup on a household Sim (`ForceCleanUp`, with an `OnFailure` callback
that starts EA's `AsyncCleanup`) or starts `AsyncCleanup` directly, and sets `Basket.Situation = null`. It calls `Exit()` only
when the basket is already gone. Nothing else ever exits the parent: `AsyncCleanup` does not, and `PicnicBasket.Dispose`
only disposes through `mSituation`, which is null by then. The root situation stays in `Situation.sAllSituations` (its
constructor added it; only `Dispose` removes it) and is saved with the world. OFF runs [log analysis of the sweep]: Isla
Paradiso 19 -> 54 picnic situations a session, Sunset Valley 9 -> 41.

**Fix (default off):** `PicnicBasketSituation.EndPicnic` is wrapped (`PicnicExitHook`). After EA's body, every listed
`PicnicBasketSituation` in the `CleanUpPicnic` state is checked (`PicnicExitCore.Decide`):
- already exited (`SituationExited`): left alone, never a second `Exit` (EA's Exit marks the whole chain first);
- still its basket's situation: not finished;
- any Sim with the situation in `Autonomy.SituationComponent.Situations` (one pass over `LotManager.Actors`): skipped;
- a situation interaction of the cleanup state still in a Sim's queue (`mInteractions`, the ForceCleanUp push): skipped,
  because `Exit` runs `ClearSituationSpecificInteractions`, which clears that interaction's callbacks, so EA's
  `OnFailure -> AsyncCleanup` fallback could no longer run. It is tried again at the next picnic's end;
- else EA's own `Situation.Exit()`: `CleanUp` + `Dispose` from the CleanUpPicnic leaf up to the root (the same call
  `CleanUpPicnic.Init` makes when the basket is gone). `AsyncCleanup`, if it runs, only uses the child's cached basket.

Finished picnics loaded from an older save are swept the same way at the next EndPicnic. No Sim is touched, so no household
exemption is needed. Counters 4600 / 4601 / 4609.

**Log:** `picnicexit: exited N finished picnic(s), M skipped (sims present|cleanup pending), K picnic situations left listed,
T situations in all`.

**Proof:** agent leak census (`LeakCensusHandlers` situations block) now also reports `picnic_finished`: listed
PicnicBasketSituations in the CleanUpPicnic state. OFF arm: `picnic_finished` grows with every picnic that ends (and
`by_type` PicnicBasketSituation with it); ON arm: it stays at 0-1 (a picnic whose cleanup is still queued), 4600 counts the
exits, no new exceptions. Self-check: `tools/selftest/picnicexit` (no double exit, Sims or queued cleanup skip, only one input
combination exits).

## 2. NPCs retry a pool they cannot enter: `poolfailmemo` (`PoolFailMemo.cs`)

**Root cause [verified, decompiled + logs]:** `GetInPool.Run` plans `PlanToSlot(pool, PoolPortalComponent Inside slots)`.
On a pool with no Inside routing slots the plan fails at once with `FailedBecauseNoDestinationSpecified` (routecauselog:
`routecause: <NPC> npc, GetInPool, Plan FailedBecauseNoDestinationSpecified, ..., pool inside slots 0`; 126 of the 128
GetInPool routecause lines in the sweep, the other two NoPath on pools with slots). `GetInPool.Definition.Test` knows nothing
of the failure, so autonomy picks the same pool for the same NPC again on its next pass (OFF runs [log analysis of the
sweep]: Bridgeport 816 `routefail job: ... GetInPool target Pool` lines in 25 runs, Isla Paradiso 135).

**Fix (default off):** `RouteFailLog.RflPlan` (every plan of a Sim follower) calls `PfmPlan` for failed plans: when the
Sim's current interaction is GetInPool and the result is NoPath (16) or NoDestinationSpecified (10), the pair (Sim object
id, pool object id) is remembered for `kPoolFailMemoHours` (1) sim-hours. `GetInPool.Definition.Test(ref parameters, ref
tooltip)` is wrapped (`PoolFailMemoHook`; the skinny-dip definition inherits it): after EA's test passes, an autonomous pick
(`parameters.Autonomous`) of a live pair returns `GenericFail`, the answer EA gives a Fatigued Sim. User-directed picks
always get EA's answer. A Sim blocker, a locked door or a success is never recorded; a refusal does not extend the entry; a
clock that went back (another save) drops it. Household, selected and unknown-scope Sims (`HouseholdExempt` or
`IsPlayerSim`) are never recorded, and a recorded Sim that turned exempt is not refused (4608). Rule: `PoolFailMemoCore`.

**Log:** `poolfailmemo: recorded <Sim>, pool 0x<id>, FailedBecauseNoDestinationSpecified, 1 sim-h` and
`poolfailmemo: refused <Sim>, pool 0x<id> (autonomous)`.

**Proof:** OFF arm on a lot with a slot-less pool (the Isla Paradiso spa lot of the sweep, `pool inside slots 0`): repeated
`routefail job: ... GetInPool target Pool` lines for the same follower. ON arm: one `recorded` line per NPC and pool, then
`refused` lines instead of new GetInPool route fails for an hour; 4602 / 4603 grow, the GetInPool routefail count drops,
no new exceptions, household Sims' GetInPool unchanged. Self-check: `tools/selftest/poolfailmemo` (recorded results,
autonomous-only refusal, exemption, life window, bounded table).

## 3. Startled pets flee into pet-locked doors again and again: `petstartleskip` (`PetStartleSkip.cs`)

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
and unknown-scope pets (`HouseholdExempt` or `IsPlayerSim`) are never recorded and never skipped (4608). A skip does not
extend the window. Rule: `PetStartleSkipCore`.

**Log:** `petstartleskip: recorded <pet> (<species>), flee route PortalLocked, startles skipped 30 sim-min` and
`petstartleskip: skipped <pet>, <StartleType>`.

**Proof:** OFF arm on a lot with a pet door locked for NPC pets and a startle source (TV / stereo turned on near an NPC pet):
repeated `routefail job: ... PetStartle ... result 18` for the same follower. ON arm: one `recorded` line, then `skipped` lines
and no new PetStartle PortalLocked fails for that pet for 30 sim-minutes; 4604 / 4605 grow, household pets unchanged, no new
exceptions. Self-check: `tools/selftest/petstartleskip` (PortalLocked only, pets only, forced and exempt startles run, life
window, bounded table).

## 4. NPC pets walk home for a whole day: `petlonghome` (`PetLongHome.cs`)

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
Sim of unknown scope is never touched, 4608), not held (`UfHeld`: carried, ridden, riding, carrying; or any parent, e.g. a
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
after a sim-hour. ON arm: one `placed` line, the pet on its home lot within a minute, 4606 +1; household pets and pets in view
keep walking; no new exceptions or stuck Sims. Self-check: `tools/selftest/petlonghome` (gate order, distance and trip-age
rules, unseen checks, trip table).

**Unicorns (2026-11-19):** unicorns and strays are never placed. A wild horse on a long unseen GoToLot is placed at that lot (same unseen and distance rules); otherwise only a pet with a real household heading to its own home lot (`lot == LotHome`) is. GoToLot to any other lot keeps EA's walk.
