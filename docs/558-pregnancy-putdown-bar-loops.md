# 558. Bridgeport gameplay loops: hospital birth retry, carried child never put down, bartender PortalLocked

2026-11-14, static (decompiled 1.67 scripts, read locally) + the 2026-11-14 run logs (`phase-20261114-*`), built, NOT RUN.
Switches `birthroute` and `putdownplace` (both off), diagnostics `BarStaffCheck` (no switch). Counters 2438-2447, 2469-2476.
Repros `birth-hospital-retry`, `putdown-child-loop`, `bar-staff-portal-locked`.

## 1. Pregnant NPC loops to and from the hospital (`birthroute`)

**Seen [measured].** `phase-20261114-0212` (Appaloosa Plains): 10 `ufloop:` lines for Patricia Bedlington, each = 4 restarts
of `Pregnancy+HaveBabyHospital` on the same hospital after a failed plan inside 20 sim-minutes (40+ restarts in ~20 s wall).
Every failed plan of hers there is our cut: `cut (UnseenFallback, mask 2)` at the 150 ms unseen deadline or
`cut (KindDeadlines, mask 2)` at 250 ms, with host time 200-650 ms; `last guard action cut (kind 0)`. `phase-20261114-2151`
(Bridgeport): the same for Stella Striker (`HaveBabyHospital target Hospital`, cut at 203 ms, 4 restarts).

**EA birth flow [verified, decompiled `Pregnancy.cs`, `RabbitHole.cs`, `InteractionQueue.cs`, `Sim.cs`, `PetPregnancy.cs`].**
Hour 70 (`HourlyCallback`): water breaks, `BabyIsComing` buff, contraction broadcaster, 5-min contraction alarm,
`TakeToHospital` on the mom. Hour 72: `HaveTheBaby` disposes the broadcaster, removes the hourly and contraction alarms,
sets `BabyShouldBeBorn` on a queued `HaveBabyHospital` if there is one (an autonomous early hospital visit), else queues
`HaveBabyHospital` (a hospital closer than the home lot) or `HaveBabyHome`. `HaveBabyHospital` is a rabbit-hole
interaction: `RouteNearEntranceAndEnterRabbitHole` (a carried toddler joins as follower), `InRabbitHole` waits for the dad's
`GoToHospital`, then `CreateNewborns`, carry or basket, `AfterExitingRabbitHole` pushes `GoHome` + `GoInsideLot` on
`LotHome`. `HaveBabyHome` routes to the home lot and places the mom there when the route fails, then always births.
`Continue()` (Sim instantiation) re-adds the labour state at hour >= 70 and a re-have alarm at hour 72.

| # | EA flaw | Evidence | Fix (switch `birthroute`) | Who |
|---|---|---|---|---|
| F1 | `HaveBabyHospital.Cleanup` / `HaveBabyHome.Cleanup` re-call `HaveTheBaby` 1 sim-minute after every failed trip, unbounded; a failed plan to the hospital is not retried inside `RouteNearEntranceAndEnterRabbitHole` (only failed outside-routes are), so one bad plan = one more lap | [verified code]; the logged laps [measured] | re-haves wait 1, 15, then 60 sim-min after the last attempt [2469]; from the 3rd failed attempt the other kind: home birth after hospital failures (EA's own no-hospital branch = the safety net), the hospital after home failures [2440]; an unseen NPC mom whose hospital trip failed is placed by the hospital when that spot is unseen too (EA's `HaveBabyHome` places its mom the same way), so EA's rabbit-hole route only walks the last metres [2473] | NPC |
| F2 | lost birth: `HaveTheBaby` removes the alarms first and ignores `InteractionQueue.Add`'s result; `InsertInteraction` refuses an interaction whose `Test` fails with no Cleanup (e.g. `HaveBabyHospital` while `HaveBabyHome` is queued, either without `BabyIsComing`) -> labour at hour 72 for good. A birth interaction dropped from the queue without running (load, reset) is lost the same way | [verified code]; the queue-drop case [inferred] | a missing `BabyIsComing` is restored first (as EA's `Continue()`) [2472]; when nothing is queued after EA's code the re-have alarm is re-armed in 10 min [2470]; a watchdog re-have 3 h later (EA's own delegate, AlwaysPersisted like EA's alarms) re-queues a birth that vanished, and is a no-op otherwise [2474] | all |
| F3 | duplicate / stale calls: re-have alarms are AlwaysPersisted and `Continue()` adds another, EA dedupes only a queued `HaveBabyHospital` (a queued `HaveBabyHome` gets a second birth interaction); an alarm firing after the birth runs `HaveTheBaby` on a finished pregnancy | [verified code] | a call for a pregnancy that is no longer the mom's, or while a birth interaction is queued or running, does nothing [2471] | all |
| F4 | homeless mom (townie): `mMom.LotHome.GetDistanceToObject` throws after the alarms were removed -> never gives birth; after a hospital birth `GoHome` / `GoInsideLot` target the null lot | [verified code]; the after-birth throw [inferred] | the closest hospital without the home comparison [2441]; after the birth only EA's basket put-down, no `GoHome` on a null lot [2475] | NPC (homeless moms are never household) |
| F5 | `HaveBabyHospital.Cleanup` reads `Actor.SimDescription.Pregnancy.HaveTheBaby` when the pregnancy was cleared meanwhile (`ClearPregnancyData`: Dispose, travel merge) | [verified code] | the birth flag is cleared so EA adds no alarm [2442] | all |
| F6 | ours: `unseenfbplan` / `KindDeadlines` cut unseen NPC birth routes | [measured] | plans of an unseen NPC whose current, transition or head interaction runs at Pregnancy priority get scope `kBirth`: native's seen-NPC ceiling, no cut, no teleport, no placed-Sim repair [2438] | NPC |

**Household births.** The "all" rows touch household moms. Each changes only a case where EA loses the birth (F2),
queues a second birth interaction or runs a finished pregnancy (F3) or throws (F5); a household birth that EA completes
runs EA's code unchanged (the watchdog then finds no current pregnancy and returns). Waits, placement, the other-kind
safety net and the uncut scope are NPC-only (`IsNpcSim`; unknown scope = household).

**Why the hospital route failed.** In the 2026-11-14 logs every failed hospital plan of both moms was our cut [measured];
with F6 the route runs EA's rabbit-hole routing uncut. Other EA failure sources stay possible (plan failure without retry,
a locked or hidden-room door, a carried child's follower route) and are bounded by F1.

**Variants.** Pets (`PetPregnancy.HaveTheBaby` override -> `HaveLitter`, which places the mom at home on a failed route)
and male alien births (`MaleHaveBabyHome`, via `BuffMalePregnancy`) do not go through `Pregnancy.HaveTheBaby`: unchanged.
`HaveLitter` keeps EA's 1-minute re-have only when the home lot has no inside point; a stray (no home lot) would make
`HaveLitter.Definition.Test` throw [inferred, candidate, not fixed]. `HaveBabyHome.Cleanup` has the F5 read too
(not hooked: queued `HaveBabyHome` + cleared pregnancy is the rare Dispose / travel-merge case) [candidate].
Save/load mid-labour: alarms persist (AlwaysPersisted); the interaction queue is not counted on [inferred]; F2's watchdog
covers a lost queue.

- Check `BirthRouteCheck run`: pure decisions (cut scope, NPC wait and kind after failed attempts with household and
  2-failure controls, stale / duplicate / normal call action); off BROKEN, on FIXED. `who` lists pregnant Sims (hour,
  labour buff, home, birth interaction, attempts, failed hospital trips).

**Left as candidates [inferred, not fixed].** `InRabbitHole` waits while the dad's current interaction is any
`GoToHospital` (also one for another mom). `UnseenFallbackLoop`'s loop breaker cannot help a birth loop; F1 ends it.

## 2. Carried child never put down: Stella Striker, 47 failed PutDownChild (`putdownplace`)

**Seen [measured].** `phase-20261114-1737` (17) and `-1835` (30): `routefail job ... PutDownChild target Sim ... result 16`
(`FailedBecauseNoPathToDestination`), 0 nodes, 0-13 ms, `uncut (exempt)` (not our cut), follower `48EE0034E1D3CDC0` =
Stella Striker (the `ufloop` line of `-2151` names that id), day 7 ~19:30, in groups of six (one per attempt). The
`-2022` report (`HiddenRoomCheck sim`) shows her `CarryingChildPosture` with toddler Sebastian Striker, on Butterfly
Esplanade, room 0 (outside), `GoHome`; she carries him from day 1 in every run (`Sebastian -> Stella ... partner in none
(linked in transition)` lines), and in `-2151` her `HaveBabyHospital` loops too (§1). Where she stood at day 7 is not in
the logs (the ts3fixes `routefail:` detail lines were capped by then), so why every jig route fails is open [open].

**EA code [verified, `ChildUtils.cs`, `PutDownChild.cs`, `BeingCarriedPosture.cs`, `CarryingChildPosture.cs`].**
- `ChildUtils+PutDownClass.PutDownChild` tries up to seven spots (near the parent, the lot's front door, mailbox, four lot
  corners): `FindGoodLocationNearby` for the jig, then a route to jig slot A (route-failure animation on the last). Every
  route failed = return false; the child stays carried. No in-place or put-anywhere step after the search.
- New attempts come without bound: the toddler's own actions (`BeingCarriedPosture+PushPutDownOnParent` queues
  `PutDownChild` on the parent and waits up to 30 sim-min), and every interaction of the parent that needs standing
  (`CarryingChildPosture.GetStandingTransition` falls back to `PutDownChild`). A pregnant mom in this state also fails every
  `HaveBabyHospital` (its transition is this put-down), which feeds §1.
- EA's stuck check does not see it: it skips a Sim whose position changed and every Sim on the active household's lot
  (docs/552 §3) [verified]; she keeps planning other routes (her `ufplan:` lines, 160-430 ms) [measured].

**Fix (`PutDownPlace.cs`, NPC parent and NPC child only).** After EA's search failed on routes only (no cancel or other exit
reason), the 2nd such failure of the same parent within 2 sim-hours puts the child down where the parent stands: EA's own
in-place branch of the same method (jig at the parent's position, turn to face, no route), then EA's success tail line for
line (put-down animation, child to the jig's SimB slot, both standing, footprints). A child whose new spot fails EA's
placement test (`World.TestObjectPlacement`) gets EA's `AttemptToPutInSafeLocation` [2445]. Social put-downs (a social
pushed on the child), a stroller, a fire on the lot, household / selected / unknown-scope Sims keep EA's code [2446].
Counters [2443] failed searches (also off), [2444] in place, [2447] errors. Check `PutDownPlaceCheck run` (pure bound),
`who` (Sims carrying a child, failures counted).

## 3. Bartender route PortalLocked: no fix (cause not provable from the logs)

**Seen [measured].** Only `phase-20261114-1835`: an unseen bartender (follower `14BC00F559C351B0`) running `Tend` on a
`BarProfessionalLounge`, day 4 12:40-23:08: twice `FailedBecauseNoDestinationSpecified`, then 8x
`FailedBecauseDestinationPortalLocked`, all `nodes 0 in 0 plans` (rejected before any search), all `uncut`. The Bartender
role re-pushes `Tend` while the Sim is off the bar's lot (`Bartending.Bartender.SimulateRole` ->
`PushRoleStartingInteraction`), so the bar stays unstaffed for the shift. In other runs bartender `Tend` plans were cut by
`unseenfbplan` once and the retry succeeded (`-1923`, `-2059`, `-2234`).

**Not our code [verified].** The plans were uncut; the route backoff records only `NoPathToDestination`, not 18;
`doormirror` (native door table) is off by default and in these runs; `hiddenroomnpc` / `hiddenroomspawn` only move Sims and
read doors; no ts3fixes hook changes `IsAllowedThrough`, `SetLockType` or portal costs.

**EA rules [verified, `CommonDoor.IsAllowedThrough`, `VelvetRopes.CanSimEnter`].** A hidden room closes its doors for every
Sim first; then only the Firefighter, GrimReaper, Police, SocialWorkerChildProtection, Burglar and Repoman services ignore
locks; a Bartender role has no exemption from Nobody / SelectedHousehold / SelectedActor / OwnerList locks or one-way
doors. Velvet ropes do let active Bartender roles through (`kAllowedRoles`), so a VIP rope is not the cause.

**Why no fix.** No log line names the lot or the door, and three causes fit (a door locked in the world file, a bar in a
hidden room, a one-way or gender door). A blanket "staff ignore locks" would change routing for every role Sim on every
lot without evidence. Instead `BarStaffCheck scan` (read-only, `BarStaffCheck.cs`) lists every bar, its bartender, the room
of its bartender spot, whether that room reaches outside for that Sim (the game's own `IsAllowedThrough` per portal, the
`HrReachesOutside` flood of docs/552) and each closed portal with its lock. Run it once in Bridgeport (repro
`bar-staff-portal-locked`); a `reach closed` row names the case for a targeted fix.

## 4. Next

One Bridgeport run with the new package: `birth-hospital-retry` and `putdown-child-loop` (off then on, pure checks), their
`who` lists, `bar-staff-portal-locked`. In the natural half, compare `ufloop: ... HaveBabyHospital` lines and counters
2438-2447 off vs on; with `putdownplace` on, `PutDownChild ... result 16` groups should stop after the 2nd attempt.
