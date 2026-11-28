# 584: Unseen visit trips, Sims stuck upstairs, the 692 ms PlanFromPoint, last search in the census

2026-11-15, from run `phase-20261115-2043` (s55, Bridgeport). Static, built, **not run**. All new switches default off.

## 1. Unseen NPCs crossing town on foot (`visitcar`, `visitwalkfinish`)

**Root [verified, decompiled].** `VisitCommunityLot.Run` routes to the lot's front door first (`Lot.RouteToFrontDoor`, a
plain `CreateRoute()`, car allowed). With no front door (parks, plazas) or when that route fails, it creates the lot
route and, for an autonomous visit, draws `RandomUtil.RandomChance(Lot.ChanceOfGoingToCommunityLotByCar)`
(`Lot.kChanceOfGoingToCommunityLotByCar = 50`); on false it turns `EnablePlanningAsCar` off. Half of those visits walk
however far the lot is. In run 2043 unseen walkers covered ~1 m per sim-minute for hours (one ~800 m in 13 sim-hours).
The same flip is in `GoToCommunityLot`, `VisitLotWithDate` and `RabbitHole.RouteNearEntranceAndEnterRabbitHole`; only
the visit is changed.

**EA's distance.** `Car.GetWorldDrivingMinimumDistance()` (`kDistanceToDestinationSoSimWillDrive` 30 m, with Egypt,
China, France and downtown variants, all 30), which `Car` also hands to the planner as
`Route.SetMinimumDistanceForCarTravel`. `visitcar` uses it as the threshold.

**`visitcar`.** Hook on `Lot.get_ChanceOfGoingToCommunityLotByCar` (pass-through) plus `VisitCommunityLotHook.RunHook`:
an autonomous visit by an NPC that `TownieUnseen` calls unseen at Run start (never household, selected or unknown
scope; standing, not riding), with the lot farther than EA's driving distance, reads 100 once in that Run's task. That
is one of the two outcomes EA's own flip gives, so the result is a vanilla outcome; the planner still decides whether a
car is used. Counters 2722 judged, 2723 kept driving, 2724 left to EA (household, selected, seen at start).

**`visitwalkfinish` (backstop for walks already underway).** Every 10 sim-minutes (alarm started by the first visit
Run with the switch on, NeverPersisted), an NPC whose visit Run started 60+ sim-minutes ago and is still routing to the
lot, unseen, with the lot out of every viewer's reach, gets EA's route interrupt
(`SimRoutingComponent.RequestRouteInterruptAction`, soft stop, as `SimScriptAdaptor`'s toddler walk-fall). The callback
runs in the Sim's task, checks again, places the Sim on a verified safe spot of the lot (`SafeSpot`, as the visit
shortcut) and ends the route (`ActionResult.Terminate`). Run then plans again from the lot (front-door branch) or
returns false; in that case the hook runs Run's own arrival step (`MakeSimDoSomethingAfterArrivingAtVenue` when the
visit looks for actions on arrival, the grouping situation) and returns true. Refused at the callback: the walk goes on
(the interrupt re-queues a clone of the follow action, EA's resume). Exemptions are the visit shortcut's
(`ShortcutToLot`):
- EP10 resort lots: Run sends resort visitors through the resort tower / check-in path; diving lots need the boat or
  mermaid water route (`EnablePlanningAsBoat`). A placement would skip that.
- Households with pets, and any Sim in a grouping situation: `GetFollowers` may bring pets, household members or a
  horse (`NpcShouldRideHorse`); moving the leader would leave them across town.
- Moving houseboats, apartment shells, the active household's lot, world lots.

Counters 2725 interrupts asked, 2726 walks finished, 2727 refused at the callback, 2728 errors. Open [inferred]: what
`DoRoute` reports for a terminated route (a route-fail count on an unseen Sim at most); check in the first run.

## 2. Tom Wordy upstairs, Lenny Shutter in Founders Keep room 28

`hiddenroomnpc` (HiddenRoom.cs `HrPass`) has **no floor filter**: it judges any NPC standing in a room that
`Lot.IsRoomHidden` marks, on any level, with EA's allowed-room flood (`HrReachesOutside`). It did not act on Tom because
he was never in a hidden room: every census row of run 2043 has him in room 40 (or 28 in the one `routecause` line,
level 1 of his own Crib) with `hidden_room false` and `trapped false` (the same flood: an allowed way out exists). His
failing plan was `DisposeOutside` to an outdoor trash can (`NoPathToDestination`, holding a TrashPile), after which he
stood idle; the stuck watchdog's idle check (EA's `IsPointInLotSafelyRoutable`, lot edges) did not reset him, so the
planner could reach the lot edge from his spot [inferred from no reset 46 line]. That is the idle question (§4), not a
closed room; widening the hidden-room rescue would not have moved him.

The floor-0 limits are in the harness side only: `HiddenRoomCheck`'s scan lists floor-0 rooms and its repro lot is the
first lot with a floor-0 marker; its trap picks a floor-0 marker first (HiddenRoomChecks.cs). Lenny's upstream FAIL (`bp-unseen-tp-goal-rerun`)
read `UfTpCleanup`, which only moves NPCs our own teleport placed there, 1 s after the trap; `hiddenroomnpc` needs two
10-minute passes (or a failed plan). No rescue change was made; the check strings stay as they are.

## 3. The 692 ms PlanFromPoint on the sim thread (tick 20979)

`ufplan: kind 2 692 ms why -1` with no Sim name, `ufplansplit ... no wait, result Succeeded, dist 58`. `why -1` with no
name means `UfPlanBody` returned before scoping: our own checks, EA's `IsPointInLotSafelyRoutable`, the stuck check, a
pet or a non-Sim follower. A succeeded PlanFromPoint over ~58 m fits `IsPointInLotSafelyRoutable` (a PlanFromPoint
from the spot to the lot's 4 corners and 4 edge midpoints) [inferred]; its callers are `AttemptToFindSafeLocation`
(every reset), `FindRoutablePointInsideNearFrontDoor`, `CreateTeleportInstanceToPosition` and our `SpotVerdict`.

Why sync: `AsyncKindStart(2)` sends an own START only for the route's own Sim task, and ts3native refuses own kind-2
STARTs with `[RouteAsync] Kinds=0` unless they carry the watchdog tag (`WatchdogKind2`, A1 only); foreign STARTs need
`ForeignKinds` bit 2, and release has `ForeignKinds=4` (ReplanFromPosition). Both are ts3native config, already
hooked (`hooked mask 6`); turning on `Kinds=2` (or `ForeignKinds=6`) is an A/B, not code. Plans from a context that
cannot yield (a reset run outside the Sim's task, alarms) stay sync whatever the config.

Callers [verified, decompiled; 26 call sites]: `Sim.AttemptToFindSafeLocation` (every reset and `AttemptToPutInSafeLocation`),
`Sim.FindRoutablePointInsideNearFrontDoor` (send-home, NRaas instantiation), `Sim.CreateTeleportInstanceToPosition`,
`Service.CreateSim` / `GetPositionNearLotCorners`, `ServiceSituation.IsGoodSpawnPoint`, `PerformerBase.GetPositionForShow`,
Pregnancy, Boat, Stroller, teleporters and others, plus ours through `SpotVerdict` (SafeSpot for the trip shortcuts, the
watchdog). The plan is always kind 2 (`Route.PlanFromPoint` of a throwaway adult route whose follower is the Sim). Which
caller produced tick 20979 is not in that log; the `src` label below names it in the next run. Scale: 411 runs in
ts3-runs hold 19,274 slow unscoped kind-2 plans (median 239 ms, max 3,291 ms, ~4,960 s in all).

**`routableasync` (default off, for an A/B with `[RouteAsync] Kinds=2`).** `AsyncKindStart(2)` sends a START only from
inside `IsPointInLotSafelyRoutable` (`sInRoutableCheck`, now kept per task so a check sleeping on its job never tags
another task's plans) for an unseen NPC (`UfWhy` 0: never household, selected, unknown scope or pets) and outside the
watchdog's own A1 path; every other PlanFromPoint is refused locally and stays sync, as with `Kinds=0`. The job takes the
pre-plan callback (doors, tombs, closed venues) as A1 does. Why it is safe for those callers: the job runs the game's
own PlanFromPoint IC on the worker (the same IC and handshake as the watchdog's A1 jobs, `WatchdogKind2`, which have a
sync shadow compare arm, `watchdogasyncshadow`); the route is a throwaway (no follower moves, only its result is read); the Sim's own task sleeps until READY
only where `CheckYieldingContext` allows a yield, else the check stays sync; with `KdOwnPlanFromPointMs=0` no job is cut,
so the verdict is the planner's own, one or more frames later. A cut (non-zero deadline) would answer "not routable"
for an unseen NPC only, and SpotVerdict would cache that cell for a sim-day, so the A/B keeps it at 0.
Counters 2736 let through, 2737 refused here, 2738 let through but not started.

**Decision 2026-11-19 (Rodney): ON in master** for next week's set (not rc-1119b), with `Kinds=2`, `H2Ic=1` (own STARTs need it) and `KdOwnPlanFromPointMs=0` in ts3native.ini. Evidence: Dalexlol's 13 soak jobs on the arm (12 PASS, 0 stuck, 1 FAIL not his) and his 17-job soak 2026-11-19 (8,770 STARTs let through, 18,059 refused here, 5,540 not started, no problem traced to it). No A/B yet: `tools/harness/queues/routableasync-ab-1119.json`; a regression puts all three ini values back.

A/B lines (in-load ABBA, `--ab asyncreplan` toggles arMode: shadow = sync, 1 = async):
```
[RouteAsync]
Kinds=2
KindsMode=1
KdOwnPlanFromPointMs=0
```
EARLYFIX `"routableasync":true` and `"ufplansplit":true` (both arms). Read: ufplan / planwait lines with `src routable`,
hitchwatch, 2736-2738; correctness: `watchdogasyncshadow` style mismatches are not wired for this path, so compare the
unseen-NPC reset placements (moved lines) between arms. `ForeignKinds=6` (adds PlanFromPoint from other yielding tasks)
is a second step: none of the callers above plans from another Sim's task except service spawns, which run in
alarm / situation code that mostly cannot yield [inferred].

Change (log only): `ufplan` / `ufplansplit` lines of unscoped plans now carry `src <kind> <Sim> [in <task Sim>'s task]`
(`routable`, `spot`, `watchdog`, `stuckcheck`, `leg`, `pet` or the follower's type), and since 2f6ce3e4 the refused
START's sync plan is timed (`sync N ms (START refused)` instead of `no wait`). The next `ufplansplit` run names the caller.

## 4. Last search in the census (`autlastsearch`, log only)

`AutPushHook` (AutLive.cs, on `Autonomy.ConsiderPerformingAutonomousAction`, installed in kAutLive packages: default
on) records each Sim's last search that reached the push decision: `pushed <definition>`, `kept <head> over <pick>`, or
`none` (nothing scored). ts3agent's census adds `last_search` and `last_search_min` per Sim (null when the switch is off).
An idle Sim with `none` a few minutes ago is an empty search; with a stale or missing entry, autonomy is not running for it.

## 5. Per-update list copies (GC leads from docs/583 §7.2), ranked by call rate; `motivebuf`

Call rates from the code paths [verified, decompiled] and run 2043's census (123 Sims in the world on average, 6 in the
household); rates are upper bounds where unseenthrottle stretches unseen Sims' updates.

| Rank | Site | Called | Calls / sim-hour (Bridgeport, 123 Sims) | Allocations per call |
|---:|---|---|---:|---|
| 1 | `Motives.UpdateMotives` | `Autonomy.Update`, every Sim, once >= 1 sim-minute has passed (the active Sim every update) | ~7,400+ | List + array (+ ValueCollection on Mono 2.x) |
| 1 | `BuffManager.UpdateBuffs` | same call, right after | ~7,400+ | List + array (copy of `mDelayedBuffs`, almost always empty); a second List only when a buff times out |
| 3 | `ThoughtBalloonManager.Update` | same call, only while `NeedsToUpdate` (a balloon showing: seen Sims) | small | ArrayList |
| 4 | `DreamsAndPromisesManager.Update` | household Sims only (6), when set to update | small | Dictionary + up to 5 Lists |
| 5 | `RoleManagerTask.Simulate`, `TrafficManager.Simulate`, `SeasonsManager.Update` | once per their task's tick, world-wide | per tick, not per Sim | list of lists / lot list + table / float[2] |

`motivebuf` (default off, MotiveBuf.cs) takes rank 1a: a swap of `UpdateMotives` that snapshots into a pooled list (taken
per call, so a Sim sleeping mid-loop keeps its own) instead of a new one, with EA's timer, sleep, destroyed-Sim return and
`MotiveChanged` unchanged. Proven-exact, so it covers household Sims too (CLAUDE.md: exact speedups stay); identical
results are the point of `motivebufcheck` (pure check: every snapshot compared with EA's copy by count and reference,
mismatches counted in 2731 and must be 0). Allocations saved per sim-hour = 2 x the rise of counter 2729 (at least).

Rank 1b (`UpdateBuffs`): ts3native IL patch `ilp_buffbuf` (default off, needs `[IlPatch] Enabled=1`; spec
`mods/ilpatch/specs/buffbuf-delayed-copy.json`, EA 1.67 body only; other builds fail the hash and run EA's body). Inserted
at the top: `if (!mDelayedBuffs.GetEnumerator().MoveNext()) goto 0x89` (the code after the first loop). With an empty
list, EA copies it into a new List and walks nothing, so skipping is exact; with buffs pending, EA's body runs unchanged.
`ListSorted` has no `Count` field reference in the module (an IL patch can only use existing tokens), so the test
enumerates: that box stays, the copy's List (and its array on this Mono [inferred]) goes. `buffbufcheck` (ts3fixes,
off) runs right before each `UpdateBuffs` from `Autonomy.Update` (after the motive hook): calls (2733), empty lists = the
skips (2734), and `ListSorted.Count` vs enumeration (2735, must be 0: EA's other code trusts Count). Tooling:
`tools/ilpatch-convert.ts --dump` / `--fill` (hand specs), and MemberRefs whose return type ends in `>` now key right.
The rest are per-tick or rare; measure the GC share first (§6).

## 6. GC measure for the next A/B

`tools/harness/gc-rate.ts <off log> <on log> [--sim-hours H,H]` reads the [MemDiag] samples: marks (Boehm collections,
`GC_gc_no`) per hour and live-set growth per hour (gcHeap - gcFree at the `ev=gc-after` samples, least squares; every
sample as an upper bound when there are fewer than two), per arm and on - off. Run 2043 for reference: 1.5 h, 164 marks
(108/h), live set 215 -> 298 MB (+49 MB/h). The gc fields are still flagged unvalidated in the log.

## 7. s56 recovery drop: F1's lot test closes every lot with a door (`tpdoorrights`)

s56 (phase-20261115-2218) against 2043: route retry after a cut 92.2% -> 69.1%. 161 of 235 sampled failed retries are
GoHome plans that fail in 1-2 ms with result 10 (FailedBecauseNoDestinationSpecified, 0 plans) right after an unseen cut
(2043: 5); GoHome cuts barely moved (275 vs 281), census "unseenfb route goal" placements fell 99 -> 11. The new part of
s56 is upstream's F1 goal check (`UnseenTeleportGoal.cs`, docs/267, shipped inside `unseenfbteleport`, no switch of its own).

Cause (verified in EA's code, inferred at run time until s57's counters): F1 asks `UfLotOpen`, which closes the lot when any
`CommonDoor` has `OneWayRoomDestinationId != 0`. EA's "not one-way" value is 0x8FFF (36863): the field initializer and
`SetLockTypeAndOwner(lock, owner)` both set it, and `CommonDoor.IsAllowedThrough` tests `!= 36863`. So every lot with a door
counts as closed, every indoor goal is refused (counter 1041, "lot closed"), and only outdoor-ground goals still teleport.
The refused cut then falls through to EA's own fallbacks (front door slots, mailbox, PlanToLotEx), whose first plan the
native retry line records as the failed retry. The run's door dump (doors.json) shows nearly all doors "Anybody", so the
lock test itself is not what closes them. The same `!= 0` test is in `UfLotOpen`'s other caller `UfPreLot` (docs/202 §3.5 D)
and in `UfLotLocked` (`UrCore.DoorRefuses`), both older (since 2026-10-27); `tpdoorrights` fixes them too (`UfDoorOneWay`,
`UfPreCore.OneWay`): `UfPreLot` (the pre-plan teleport, docs/202 §3.5 D) then accepts lots with only unlocked ordinary doors,
which it refused all along; `UfLotLocked` gets the right value, but `DoorRefuses` ignores one-way since §3.9.7b (whose "one-way
doors refused every beach / park goal" was this sentinel), so it does not change. No other `== 0` / `!= 0` one-way test exists
in the tree or in upstream/main (DoorMirror, snap.cpp and p4door.h only copy the value; HiddenRoom and DoorMirrorCore use 36863).

`tpdoorrights` (default off; off = upstream's rule unchanged): F1 judges the lot with the teleporting Sim's own door rights
(`UfPreCore.TpDoor`, pure, self-check in tools/selftest/unseenfb): special lots stay closed as before; a door with no lock and
no real one-way room is free; "nobody" and a real one-way room close; any other lock passes when EA's
`IsAllowedThrough(sim)` does; on the Sim's home lot a lock owned by its household (owner or owner list) passes too, so a
resident going home is not refused by a family member's locked room. The room flood (`HrReachesOutside`) still uses the
Sim's own rights, so it never lands in a room it cannot leave. Only unseen NPCs reach it (UfTeleport's UfWhy and TownieUnseen).
Counters 2739 lots opened that upstream closes, 2740 of those only by household access, 2741 lots closed by a door the
Sim cannot pass. Once a sim-hour, when they moved, ts3native.log gets `ts3fixes: unseen tp goal: ...` with 1040-1045, the
teleport count (307) and 2739-2741, so s57 can confirm the reason with the switch off and on.

## 8. Apartment and shell lots: F1 still refuses GoHome in Bridgeport (`tpaptgoal`)

The merge regression (3024b2d6 -> b22f48e3, "retry after a cut" 95-96% -> ~70%, same engine and switches) is all GoHome:
the cuts did not grow (good 150 / 176 GoHome cuts, bad 133 / 136), but cuts followed by EA's own replan on the same route
(`GoHome.Run`'s second `PlanRouteIntoLot`, result 10 in ~1 ms, 0 plans) went from 5 / 4 to 98 / 108. Distinct
"unseenfb route goal" placements in the census fell from 76 / 74 to 4 / 7, and with `unseenfbteleport` off (0612) to 0
with the same replan pattern (102 failed of 147 cuts): the cut teleport is what recovered GoHome, and F1 refuses it.
§7's `tpdoorrights` fixes the sentinel but recovers only part: s57 (0104) logged lot closed 61 with "closed by a door 1",
so almost every refusal comes before the door loop: `UfTpLotOpen` (and `UfLotOpen`) close `IsApartmentLot` and
`BuildableShells` lots outright, and Bridgeport's NPCs largely live in apartments [inferred: the log does not name the lot
type; an off-lot, non-ground goal also counts as "lot closed"].

That close is deliberate (NPCs trapped in Late Night hidden apartment rooms, the hiddenroom* switches). `tpaptgoal` (default
off) opens only the unseen NPC's own home lot (`Household.LotHome`, what GoHome targets) and only by the goal room:
`UfPreCore.TpAptLot` / `TpAptOpen` (pure, `tools/selftest/unseenfb`, with hidden-room, no-way-out, raised, unknown-flood and
visitor controls), then `TpGoal` as before (tomb or hidden room refused first) with a room flood (`UfTpAptReach`) that asks
EA's `IsAllowedThrough(from, to, sim)` per portal plus `TpDoor` per door (real one-way rooms and "nobody" locks close, no
household access: other units' doors locked to other households block). Other apartment lots, houseboats, resorts and
diving lots stay closed; household, selected and unknown-scope Sims never get the rule. Counters 2753 allowed, 1952
refused as tomb or hidden, 1953 refused otherwise (counted in UfTeleport only), also on the hourly `unseen tp goal:` line.
Also fixed: `UfLotOpen`'s one-sim-hour lot cache kept a verdict across a runtime `tpdoorrights` toggle (the one-way reading
changed under it). Check: `UnseenTpCheck apt <hex id|any>` (repro `unseen-tp-apt-goal`). A/B: tpdoorrights on, tpaptgoal
off vs on, same save; expect GoHome same-route replans after a cut to fall back toward 3024b2d6's handful.

## 9. Usage-ranked speedups (docs/589 §3): `stcsocials`, `ilp_idleparams`

The usage sweep (docs/589 §3, `docs/data/usage-rank-1115.csv`) lists about 17 exact rewrites. Ranked by how often the code
runs, not by type score: social autonomy (Sim targets: 16.5M autonomy test calls across our runs, the largest row) builds a
`SocialInteractionCandidateCollection` per actor/target pair, which calls `Conversation.GetSocialsForSTC` once per playable
commodity; idle choice tests every idle candidate through `IdleManager.TestIdleInfo`. motivebuf (§5) and ilp_buffbuf
already cover the motive and buff updates. The fridge / stove / counter checks (`Food.LotHasUsable*`) run from Fridge_Have
(31 autonomy calls in all runs) and the cooking process, so they are left for later despite `Cabinetry.IsCounterFree`
doing a routing-slot test per counter; `HasAnyElement(params)` in Motive allocates only for the Social motive (a few
thousand small arrays per sim-hour); CarpoolManager.LoopUpdate's rider delegate is not proven free of side effects.

`stcsocials` (ts3fixes, `SocialStc.cs`, default off, every Sim: proven exact): the autonomous branch of GetSocialsForSTC
without its two redundant copies. `ActionAvailabilityData.Get` returns a new list each call, so the earlier STCs' lists
are added to it instead of to a copy of it; when the relationship has no STC or its STC is on this commodity, EA keeps
every candidate in a third list and looks each one up in ActionData without reading the result (a dictionary lookup), so
the list itself is returned. Same items, same order, same exceptions (r.STC null on the commodity test throws as in EA);
the non-autonomous branch (pie menu, trait-menu items) runs EA's method. `stcsocialscheck` runs EA's method as well and
compares name, topic and trait-menu flag item by item; EA's result is returned. Counters 2742 calls, 2743 lists or lookups
saved, 2744 checks, 2745 mismatches (must stay 0), 2746 fallbacks.

`ilp_idleparams` (ts3native [IlPatch], spec `idleparams-lazy-array`, default off): TestIdleInfo builds
`new object[1] { mSim }` for every candidate that passes the early tests and reads it only when `ProceduralTest` is set.
Inserted at 0x13C (branches to it enter the insert): `ldarg.1; ldfld ProceduralTest; brfalse 0x16F`, EA's own target for
"no procedural test". Exact: nothing else reads the array, and newarr / stelem have no side effect. One object[] less per
tested idle candidate. Needs a ts3native build (fork CI) and [IlPatch] Enabled=1.

Not measured yet: allocations saved per sim-hour come from the counters (2743) and gc-rate.ts in an A/B. REA N1
(`0xC692F0`, lot-bounds polygon copies) is not timed: it needs a native timing detour (prologue bytes from TS3W.exe, not
in game-copy) and an in-game run, so it waits for a run slot after s57.

## 10. Call census of the speedup candidates (`callcensus`)

None of the docs/589 §3 and docs/590 §3 candidates appear in `jit-hot.csv` or `jit-call-edges.csv`, so their real call
rates are unknown. `callcensus` (ts3fixes `CallCensus.cs`, default off, log only) swaps 22 of them for pass-through hooks
(the 23rd, GetSocialsForSTC, is counted in the stcsocials hook): same arguments, same result, same exceptions; off, a hook
reads a flag cached for 1024 calls and calls EA's method. On, each call is counted and, where the method does real work,
timed with one running EA StopWatch (microseconds). Once a sim-hour ts3native.log gets `ts3fixes: callcensus: <method>
calls N in H sim-h, per sim-hour R, us/call U, ms total T` for each method that ran. The hooks may run on the autonomy
worker thread (counts are plain increments: approximate there); the log alarm starts at world load on the sim thread.

Hooked: Food.LotHasUsableCounter / Stove / Microwave / Grill, Sink.GetBrushTeethScore, BuffManager.HasAnyElement and
TraitManager.HasAnyElement (every params caller), SocialComponent.SocializeWithMultiplier and FindSimOfRelationshipType,
Autonomy.CalculateScoreForSocial, Relationship.GetMiniRelationships, Conversation.GetSocialsForSTC and IsSuitable,
ActionData.Test (the per-social procedural-test CreateDelegate), InteractionQueue.InsertInteraction,
ViewObjects.CreateRouteToObject, TV.CountOfHumansWatchingTvOrPlayingVideoGames, RabbitHole.GetRabbitHolesOfType,
ImprovedProtestSituation.FindProtest, Terrain.ModifyRouteForSpecies, SingingInfo.AllKnownCompositions,
BarAdvanced.CurrentSnackBowl, PetGuardSitutation.GuardObject.IsBeingGuarded.

Not worth it (dropped): MetaAutonomyManager.CalculateTotalDesiredNumSims (hourly alarm), GetCandidateMetaObjectsForHotSpot
AndDeadZones (once a day), Callbox.HasVisitableSimsOnLot (callbox test only), WildHorses.FindGoodLot (wild-horse pool),
MoodManager 342/348 (GetMotiveSingleColor: the household mood skewer colour), Motive 1958/1974 (horses), VelvetRopes
AddInteractions (ropes' menu / interaction list). Not hookable as a plain swap (private nested classes or single
Definitions; read-only estimate rare): CarpoolManager's LoopUpdate (a waiting carpool), Windows.RouteToSimOutside,
ChainableGardeningInteraction.CanGardenPlant, Terrain GetClosestPuddle (rain), SummonFairies.Test, Children.Sleep.Test,
PlayVideoGameSystemOnTVOnline.Test. Already built: Motives (motivebuf), UpdateBuffs (ilp_buffbuf), TestIdleInfo
(ilp_idleparams, not census-hooked because its body is IL-patched), GetSocialsForSTC (stcsocials).

By code alone nothing left on the list runs per frame or per Sim per tick: Motive 1107 allocates only for the Social motive
(once per Sim per motive update), and the per-autonomy ones (ActionData.Test's CreateDelegate, IsSuitable's repeated
ActionData.Get, CalculateScoreForSocial) wait for the census; ActionData.Test's delegate cannot be cached by an IL patch
(no field token to hold it) and needs a body swap of a large method, so it is built only if the census ranks it high.

## 11. Usage sweep 3 (docs/591 §3): `ilp_autolights`

`LightGameObject.AutoLightsOff(sim, lot, roomId)` runs from `Sim.RoomChanged` for every Sim that changes room on a lot.
After its Sim scan it fetches `lot.GetObjects<IUserControlledLight>()` whenever no awake Sim is in the room, but a light is
switched only when `flag` (everyone on the lot asleep) or `flag3` (a sleeper in that room) holds. The walking Sim is
awake and on the lot, so `flag` is almost always false, and with no sleeper in the room the loop's test is false for every
light: the array and the walk do nothing. IL patch `ilp_autolights` (spec `autolights-early-out`, builds ea-1.67 and
simler90-242) inserts at 0x97 `ldloc.2; ldloc.s 4; or; brfalse 0xFA` (the method's ret). Exact: no side effect is skipped
(GetObjects is a query; the loop's `flag || (flag3 && ...)` short-circuits before any call).

Not built: GenericPrivacySituation.ClearRoom.Init `continue` -> `break` is not proven exact (the first pass adds joiners
to Participants, and OtherSimCanJoinSituation / OtherSimCanIgnorePrivacy may answer differently on the second pass);
RepairableComponent.UpdateBreakage, CatJungleGym, LeafPile: per object use or per wait of one object, rare; Daycare
GetExperience changes results (bug fix, not a speedup).

## 12. Usage sweep 4 (docs/592 §3): `ilp_locnocheck` (diagnostic only on the S3PP stack)

`Localization.FillInTokens` ends both of its normal returns with `LogLocalizationResult(entryKey, text,
StringTable.HasLocalizedString(entryKey))`. LogLocalizationResult's body is empty in the shipped game [verified,
decompiled]. HasLocalizedString(string) calls ScriptCore's `LocalizedStringService_GetLocalizedStringByString` icall.
IL patch `ilp_locnocheck` (spec `locfill-no-has-lookup`, ea-1.67 and simler90-242) removes the argument pushes and both
calls at 0xA5 and 0x1327; the third LogLocalizationResult (bad token, success false) is left.

Native side (Ghidra, TS3W 1.67, decompiled 2026-11-16) [verified]:
- the icall `0x7437E0` converts the MonoString through the service interface (vtable +0x58), hashes it to the 64-bit key
  (`0x7CFE40`) and calls `0x59C850` -> `LocStrSvc_GetString_A` `0x59C5B0`;
- `0x59C5B0` walks the string-table list at `service+0x48` (reads only), copies the hit into a temporary wide buffer (the
  allocator tag "String/LocalizedStringService", freed before return; ja-jp gets a character replacement in that buffer)
  and returns a new MonoString (vtable +0x50). No cache, no miss log, no write to the service or the tables.
So on the vanilla game the lookup only allocates (a native temporary and one managed string): removing it is exact.

With the Sims 3 Performance Patch it is not proven exact. S3PP's `LocalizedStringCache` (on in this setup:
`LocalizedStringCache=1`, cap 16384) detours `0x59C5B0` and keeps a 64-bit-key cache that fills on a miss and clears at 75 %
(docs/17 §8). When FillInTokens' string came from the same key moments before (`LocalizeString(entryKey)`), the removed
lookup is a cache hit and nothing changes. When it came from a gender or species variant (`FindString`,
`GetLocalizeSpeciesString`), the removed lookup is the only one for `entryKey` and inserts an entry: without it the cache
fills more slowly and clears later. Results then differ only through S3PP's own staleness (an entry cached before a string
table loads serves the old answer until a clear), so a shifted clear can change which answer a later lookup gets
[inferred]; otherwise only the timing of later lookups changes. Verdict: exact without S3PP's cache; diagnostic only (A/B
measurement, not for release) while `LocalizedStringCache=1`.

Not built: InteractionInstance.RemoveMotiveDelta (one small List per interaction end that has motive deltas; low rate),
RandomUtil.GetRandomKeyValuePairFromPairedListDictionary (3 callers, rare), AlarmManager.Read (load only).
