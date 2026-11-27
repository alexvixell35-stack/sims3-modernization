# 546. Sim-cycle waste: work the game repeats without checking it can pay off

**Status: hypothesis list, nothing measured yet unless a row says so (2026-11-13).** Each row is a kind of work EA's
scripts may do every cycle (autonomy pass, event dispatch, alarm tick, sim update) even when it cannot change anything.
The plan for each is the same as for the leak fixes (docs/545): count it first with the test agent's census
(read only, no behaviour change), and only then build a fix behind a switch and run it off / on.
Claim marks as in CLAUDE.md: **[verified]** read in the code, **[measured]** seen in a run, **[inferred]** our reading.

## 1. The list

| # | What may be wasted | Why it could be expensive | How to measure (census, read only) | Status |
|---|---|---|---|---|
| 1 | Exceptions thrown and caught quietly | EA wraps many tests and callbacks in try/catch; in the game's Mono 1.2 interpreter (docs/04 F6) a thrown exception costs far more than a normal call [inferred]. One test that throws for every Sim on every autonomy pass would cost a lot unseen | Exceptions per second by throw site (method of the throwing frame) and by catching method | todo |
| 2 | Interactions that can never pass | Autonomy scores the interactions of the objects near each Sim; an interaction that fails its test for everyone (broken object, missing pack, object off the lot) still costs a test call each pass [inferred] | Per object type and interaction: tests run, passed, failed, time in the test; flag rows with many tests and 0 passes | **measured, §3**; first fix attempt failed |
| 3 | Objects autonomy should not consider | Objects with no lot, at an invalid position, destroyed but still listed, or left by deleted Sims can stay in the candidate lists [inferred] | Candidate objects per autonomy pass that are invalid by those rules, by type | todo |
| 4 | Listeners that filter inside the handler | Many listeners take every event of a kind and check "is this about me?" inside, so the cost is listeners x events. The census shows ~4,900 wish listeners (ActiveDreamNode) and growing kSocialInteraction listeners [measured, docs/545 runs] | Handler calls per event id, and how many returned without acting (no state change) | todo |
| 5 | Alarms that return at once | Repeating alarms whose owner is gone or whose condition no longer holds still fire and return [inferred] | Alarm callbacks per minute by callback, and their time; owners gone at fire time | todo |
| 6 | Reaction broadcasters | Fire, smells, dirty objects and others check every Sim in range each tick; a broadcaster left on a deleted or moved object keeps checking [inferred] | Live broadcasters by type, their owner's state, checks per second | todo |
| 7 | Situations that never end | Entries in Situation.sAllSituations whose participants are gone keep their alarms and listeners [inferred] | Situations by type and age, with gone participants | todo |
| 8 | Full world scans every tick | Script calls such as GetObjects<T>() or loops over LotManager.Actors in per-tick code walk every object [inferred] | Calls per second of the world-scan APIs by caller, and objects visited | todo |
| 9 | Relationships of Sims who never met | Relationship entries grow roughly with Sims squared; story progression updates them for townies [inferred]. Entries went 366 -> 670 in one soak [measured, docs/545] | Entries by kind (met / never met, both alive), updates per minute | todo |
| 10 | Retries of an unreachable target | A Sim that cannot reach an object retries the route and the interaction [measured in part: "townie retries" in every phase run] | Retries by Sim scope, object type and interaction, and route time spent on them | partly covered |

Order: 1 and 2 first (cheap census, most likely to show a large single cost), then 4 and 8, then the rest.
Row 1 needs a native hook on the interpreter's throw path (`0xE59449` / search `0xE540AB`, docs/130 §1.5), so it waits
for an MSVC build of ts3native (a clang-cl build must not run in the game, its SEH guards do not catch faults).

## 2. Related finding: a save takes ~2 min while the display is off (test machine)

[measured, Steam Deck under Wine, Isla Paradiso test world, ts3native 0.36.240 and 0.36.247] The game's own world
save step (ts3native save log phase "write: world save (lots, script state)") took **122-158 s** in every logged save
of the unattended test runs since 2026-11-11 (13 run logs, about 123 s in the latest ones, nearly constant), and
**6-8.5 s** with the display on: 22 saves in phase-20261113-1944 (whole save 6.5-9 s), the screen woken with
`kscreen-doctor --dpms on` and idle blanking held off with `kde-inhibit --power --screenSaver`. The unattended runs had
the laptop panel blanked by the desktop's idle timer (DPMS off after 5 min). Ruled out on the slow saves: ts3fixes (the
same 123 s without it, phase-20261113-1923), GC (no collection during a 122 s save), route jobs and ts3native gates
(none open) [measured].

So the slow saves were a display-off effect of the test setup, not a game or release slowdown. The nearly constant
~2 minutes looks like a timeout inside the game's world save while nothing is presented [inferred; which wait is not
known]. Whether a player hits it (save, then the monitor sleeps or the game is minimised) is not tested; it could be
one reading of the v0.2.12 known issue "save stays at Saving..." [inferred]. The door-edit repro itself did not run:
the test lot has no free 4 x 4 area for the agent's building.

For harness runs: save timings and anything that waits on rendering need the display on (see above); the leak and
listener counts of docs/545 do not depend on it.

## 3. Row 2 measured: autonomy test census (2026-11-13)

Tool: ts3agent `aut_census` (`mods/ts3agent/src/plugin/Handlers/AutCensusHandlers.cs`). It swaps
`InteractionObjectPair.TestAndCalculateScore(Autonomy, AutonomySearchType, InteractionFlags)`, the call every
autonomous candidate goes through (the definition's Test, then the score) [verified, decompiled], and counts per
"definition type @ target type": calls, calls that returned <= 0, wall time, the household share (actor selectable,
in the active household or of unknown scope), and for every 32nd zero the `InteractionTestResult` of a second Test
call. It is installed only on request at the main menu (`{"install":true}`, harness `ACENSUS=1`), because the swap must
precede the first compile. The clock is `DateTime.UtcNow` (the game's Mono has no Stopwatch): row times are sums over
minutes, fine for ranking, not for one call.

[measured, Isla Paradiso test world, release v0.2.12, display on, 4 runs of 11-20 min] About 1,500 autonomous tests per
second; **98% score 0**; ~2.6-3.3% of wall time is spent inside them. Townies account for ~60% of that time. The two
biggest rows by far are the lot visits every lot carries (`Lot.AddInteractions`), tested against every public lot
(`Autonomy.PublicMetaObjects`) by each meta autonomy search:

| Row (695 s run, phase-20261113-2106) | Tests | Zero | Time | Household time |
|---|---|---|---|---|
| `VisitCommunityLot.Definition @ Lot` | 133,136 | 99.7% | 6.1 s | 0.26 s |
| `VisitLot.Definition @ Lot` | 133,136 | 99.9% | 3.7 s | 0.48 s |
| next: `GoForJoyride @ SofaDesigner` | 751 | 100% | 1.2 s | 1.2 s |

Why they score 0 (sampled, phase-20261113-2050): VisitCommunityLot 56% pass the whole test and then score 0 in the
meta ad scoring (`MetaAdScore` path of `Autonomy.CalculateScoreForObjectInteraction`), 29% `Tuning_Age`, 13% the
definition's own test (`SharedVisitTests`); VisitLot 69% its own test (`IsGreetedOnLot` / `CommonVisitTest`), 29%
`Tuning_Age`. Household Sims fail these early and cheaply; townies run the generic wrapper (`CheckAvailability`,
`UpdateCacheIfNeeded`, `CommonTests`, `SpecialCaseTests`) before they fail.

**First fix attempt, dropped: a townie zero cache.** Remembering a townie's zero per (Sim, lot, definition, search
type) for 30 sim-minutes (household and selected Sims exempt) hit **94 times against 69,691 stored zeros** in a
6.5-min arm (phase-20261113-2145): a townie almost never re-tests the same lot within 30 sim-minutes, so the cost is
breadth (townies x lots), not repetition. Autonomy test time in that arm rose to 27.7 s against 12.0 s in the same
arm of an off/off run (phase-20261113-2121), household rows included, which the cache never touches; our reading is
GC pressure from the 40k-entry dictionary being refilled and cleared [inferred]. The code was removed.

Next for this row: an exact early-out instead of a cache. Every earlier check also returns 0, so a definition-level
rejection that is cheap and certain (e.g. `VisitCommunityLot` on a residential lot the Sim cannot treat as home) can
be answered before the generic wrapper without changing any score; and find what makes the meta ad score 0 for the
56% that pass, which may let the search skip those lots for a whole pass.

## 4. Hitch: imagination costumes built inside an autonomy test (fix `joyridecostume`)

[measured, `aut_census` longest single call, release v0.2.12] `GoForJoyride.Definition @ SofaDesigner`: one call of
**618 ms** (phase-20261113-2212) and **623 ms** (phase-20261113-2229), household child, in the first minutes after
load; the row's total was ~1.2 s in every run whatever its call count, i.e. about two such calls per game.

Cause [verified, decompiled]: `GoForJoyride.Definition.Test` and `RouteAndGetToCostumeForJoyride.Definition.Test` call
`ImaginationHelper.GetSimOutfitForActor`, which builds both imagination costumes (`PopulateImaginationCostumes`, two
`OutfitUtils.CreateOutfitForSim`) when the child has no `ChildImagination` outfit yet. The costumes are saved, so it
happens once per child per save (every child, household or not, and every child who ages up or is born later).

Fix (`kJoyrideCostume`, off by default, `JoyrideCostume.cs`): both tests use the costume only to ask "is the child
wearing it now", which cannot be true without one. The Joyride test returns false when there is no costume (vanilla
returns false on every path then, unless the build fails: EP4 data missing [inferred, not seen]); the costume-route
test runs with `GetSimOutfitForActor` answering null instead of building (exact). The builds that remain are where the
costume is used: `RouteAndGetToCostumeForJoyride.Run` and `BeKingOrQueenForADay.Run` [measured: the 2 builds logged in
phase-20261113-2319 were in those two interactions].

Result, switch on from load (phase-20261113-2254, 22 min, idle / social / CAS / outfit stress): the Joyride rows are
no longer among the 25 longest single tests (all under ~2 ms); 45 tests answered without a build; 0 errors, 0 script
errors. Still to do: the build at the real activity is still a ~0.6 s freeze for each child who plays it the first
time; moving it to a loading screen for household children is a design choice, not done.

## 5. Leak: fire-pit test chairs, one more per used fire pit per save + load (existing fix `firepitchair`)

[measured, `object_types {"match":"FirePit"}`, same 3 fire pits] Out-of-world `FirePit+FirePitChair` objects:
release defaults 4 -> 5 -> 8 over two save + reload cycles (phase-20261113-2319); with `firepitchair` on (issue #90,
`kFirePitChair`, off by default, destroys each pit's test chair before a save) 5 -> **1** after one cycle
(phase-20261113-2339). The second cycle of that run crashed, from address space, not the fix: both runs fell to a
1 MB largest free block before the crash point (the off run survived by luck), and the crash was a null + 0xC write
after repeated "low address space" warnings [measured]. Cause [verified]: `FirePit.mTestChair` is
`[Persistable(false)]` and never destroyed, while the chair object itself is saved. Also a hitch: creating the test
chair inside the "create chair and sit" test took up to 255 ms (`aut_census`), once per fire pit per load.
Suggestion for the maintainer: `firepitchair` on by default.

## 6. Tools added for the hitch and interaction work

- `aut_census` longest single call per row, with who and when (`{"top":25,"sort":"max"}`), and harness dumps
  `autmax-*.json` (`ACENSUS=1`).
- `object_types {"match":...}`: objects by type, in world / out of world.
- `hitch_watch`: a sim-thread tick stamp; every gap >= `min_ms` is kept with what the household Sims and the Sims on
  the active lot were doing on the tick before and the tick after (interaction, transition, queue, routing, posture).
- `household_routine`: one round of the everyday actions (meal, outfit change, shower, social, TV, computer, read,
  toilet, nap, music) pushed on the household Sims with an empty queue, rotating, matched on pie-menu labels.

## 7. Alarms: slow callbacks, silent kills, audit (2026-11-14)

Measured with the `alarmtime` test switch (docs/247 §9, test package with `kHitchCauseHooks`), Isla Paradiso, 26 min of
routine/outfit/social (phase-20261114-0032, hitch_watch on). Slowest world/lot passes and the alarm that took the time:

| Alarm callback | Worst | Notes |
|---|---|---|
| `StrayPets.StrayPetHelper` (hourly) | 285 ms | `SendStrayToLot` → `InstantiateOffScreen`: a stray pet's Sim object is created inside the alarm. A townie re-create in the same run took ~12 ms; why a pet costs ~20× is open [inferred: pet rig/outfit load] |
| `ObjectSpawner.TriggerSpawn` (sea life) | 242 ms | `TryPlaceSpawnedObjectInWorld` tries up to `kMaxSpawnAttempts`+1 = 11 `PlaceAtGoodLocation` searches; with `deleteObjOnFailure` false it places the object at the last start position when all fail [verified, decompiled]. Test hook `spawnplace:` now logs tries/successes per slow placement |
| `PetAdoption.RefreshCallBack` (midnight) | 111 ms | rebuilds the adoption pool |
| ts3fixes hourly sweep (`StartErrorTrapSims` alarm, ours) | 32 ms (avg 24) | 17 cleanup passes in one callback; `hourlypass:` now times each (test switch `alarmtime`) |

**Silent kills [verified, decompiled AlarmManager].** When a repeating alarm's callback throws, `Simulate` (and, for
yielding alarms, `AlarmOneShot.HandleException`) calls `InternalRemoveAlarm(timer, true)`: the alarm is gone for the
session, with no log or retry. Those are the only two callers that pass `true`, so new log-only switch `alarmkilllog`
(on; counters 2308-2309) hooks that call and names each killed alarm (method, target, owner, sim time) in the fix_applied
log. Our own hourly sweep was exposed the same way (one throwing pass stopped all 17): each pass now runs in its own try
and a throwing pass is logged once and skipped.

**Leaks and bad starts.** leak_census alarms (phase-20261114-0003): 2,939 alarms, 0 with a destroyed owner, 0 with a gone
callback target. New agent command `alarm_audit` (dumped as alarms-k.json per snap) also checks table/queue mismatch
(registered but never queued, queued but not registered), overdue, duplicate registration (same method + target + owner),
repeating with interval <= 0 and due > 1 sim year out.

**Harness fixes found on the way.** `social_round` counted socials as started only on `StandardEntryCalled`, which
`SocialInteractionA.Run` never sets: completed socials were reported as "dropped" (82 of 96). It now also reads
`SocialInteractionA.mSocialStarted`. The agent's `patches` command passed only switch names it listed, so a new fix's
switch silently stayed off; unlisted bool names now pass through and are echoed as `unlisted`. The household routine
skipped every non-human; pets now get their own rotation (eat, play, sleep, wander) and census rows carry `[Dog]`,
`[Cat]`, `[Horse]`.

## 8. Household pet route failures, Appaloosa Plains (2026-11-14; data for the pet-routing work, no fix here)

phase-20261114-0117, Lionheart household (1 human, 4 pets), 26 min of household routine. ts3native `routefail:` lines for
household Sims: **296 failed plans, 8.1 s of planning in total**, single failed searches up to 712 ms (each one a frame
freeze; household Sims, so no deadline cuts them). By interaction and result:

| Interaction | Result | Total | Count | Max |
|---|---|---|---|---|
| DrinkFromPond (pet) | NoPathToDestination | 1,782 ms | 9 | 684 ms |
| LookOutWindow (pet) | NoPathToDestination | 1,245 ms | 8 | 317 ms |
| SocialInteractionA | DestinationPortalLocked | 1,082 ms | 33 | 331 ms |
| CatShredNewspaper | NoPathToDestination | 712 ms | 1 | 712 ms |
| LayDownHere (pet) | DestinationPortalLocked | 620 ms | 2 | 324 ms |
| RunAround (pet) | NoPath / NoDestinationSpecified | 275 ms + | 34 + 26 | 65 ms |
| UseLitterBox (kitten) | NoPathToDestination, same 1.6 m route each time | small | 23 | 8 ms |

EA code [verified, decompiled]:
- `DrinkFromPondAutonomously` (Sim.cs) aims at `World.FindPondRepresentativePositions(lot)[0]`, always the first point; its
  test checks lot, terrain type and frozen ponds, never reachability. `Terrain.DrinkFromPond.Definition.Test` returns true.
  An unreachable first point fails the same way at every thirsty moment (`RouteToDrinkLocation`: shoreline search, then
  `CreateRouteToNaturalWaterBody` + `Plan`).
- `LitterBox.UseLitterBox.Definition.Test` returns true; `Run` fails at `RouteToSlotListAndCheckInUse`.
- `CommonDoor.tLock.Pets` gives pets `PortalRestriction.DoorRestriction`; the pet `DestinationPortalLocked` failures point at
  targets behind such doors. Agent command `doors` (new) lists each door's lock and the rooms it joins.
- The existing `[RouteAsync] FailMemo` (docs/164 §7) reuses a failure only within 500 ms; these repeats are minutes apart.

## 9. Interaction locks: Sims waiting for a sync partner (2026-11-14)

Test switch `synctime` (kHitchCauseHooks builds; `SyncWaitLog.cs`) logs every `Sim.WaitForSynchronizationLevelWithSim`
of >= 1 sim minute or that failed: actor, partner, level, result, minutes, social checks run during the wait, and (from
build 2026-11-14 02:50) what the partner was doing. EA limits [verified, decompiled]: `StartSync` pre-sync loop up to
`kNumMinToWaitOnPreSync` 15 min, then sync start up to `kNumMinToWaitOnSyncStart` 30; `WaitForSyncComplete` up to
`kNumMinToWaitOnSync` (5 in the shipped tuning); `WaitForMasterInteractionToFinish` has no limit; horse mounting passes 1000.

phase-20261114-0212 (Appaloosa Plains, 3 social phases + 1 routine, 169 waits logged): the per-tick
`CanSocializeWithSyncCheck` is cheap (18 ms over a 45 min wait at most), so the cost is Sims standing locked, not CPU.
- ShowVideos: 15 of 15 waits for SyncLevel.Completed timed out at 5.1 min (fix `socialfinish` below).
- Socials timing out after 30 min (Routed) or 45 min (Started): 5; the partner never arrived. Partner state logged next.
- PutDownPet: the pet waits 8-19 min for the Sim holding it; horse mounting waits up to 61 min (limit 1000).

**Fix `socialfinish` (off by default; counters 2310-2311).** A normal social ends with `Actor.AddExitReason(Finished);
CopyExitReasonToLinkedInteraction(); WaitForSyncComplete()`. ShowVideos, InkBlotTest, Interview, TriviaChallenge,
MindMeld and PresentObjectToSim call `WaitForSyncComplete` without the first two (`Interaction.DoLoop` does not copy exit
reasons), so their `SocialInteractionB` partner keeps looping in `DoLoop(ExitReason.Default)`, never reaches Completed,
and both Sims stand until the timeout, then both get SynchronizationFailed. The hook gives the partner `Finished` in
`WaitForSyncComplete` when this Sim is the sync initiator and its linked SocialInteractionB is still running with no exit
reason: the wait cannot succeed otherwise, and a social that signalled already is untouched. Difference from vanilla:
those six socials end cleanly for both Sims instead of after a 5 min stand and a sync failure (EA bug fix; every Sim).

**Open: the `synctime` hook goes dark mid-game [measured, cause unknown].** In every run with both `SyncWaitLog`
(on `Sim.WaitForSynchronizationLevelWithSim`, 7 args) and `SocialFinish` (on `InteractionInstance.WaitForSyncComplete(float)`)
installed, the wait hook stopped being reached after ~1,000-2,000 calls (phase-20261114-0242, -0312, -0341), while socials
kept starting (social_round: 6-11 started per round afterwards), the `synctime` switch still read true, and other hooks
(`alarmtime`, `socialfinish` counters) kept working. phase-20261114-0341 polled every 30 s: hook heartbeat at 2,000 waits
(03:50:46), then no further call; snapshot GCs before and after did not matter; `[MonoJit]` was off. The run without
SocialFinish (phase-20261114-0212) logged to the end. Until explained, any hook that must see every call of a hot method
needs a heartbeat. `socialfinish` therefore times its own waits (under `synctime`) for its off/on run.

**socialfinish off/on [measured].** Off (phase-20261114-0401, first half): 9 of 9 ShowVideos stood 5.3 min, wait failed.
On from load (phase-20261114-0430): the partner was found looping in SocialInteractionB at SyncLevel.Committed each time;
every logged wait ended ok after 0.5 min; counter 2310 = 16 signalled, 2311 = 0 errors, no script errors. Only the first
4 of the 16 were logged: test lines from this hook and the wait hook stop reaching ts3native.log after ~1,000 waits while
the `synctime` switch still reads true (polled) and `lotpass` lines from the same `HcLine` keep arriving; open.

**Test lines going dark: cause found [verified, source].** Not the hooks: `ts3native` (userdiag.cpp) logs at most 400
generic ts3fixes lines per session, without a notice, and only a fixed prefix list (`alarmtime:`, `lotpass:` ...) has its own
6,000-line cap. `syncwait`, `socialfinish:`, `hourlypass:` and `spawnplace:` lines shared the 400, so they stopped after the
first ~1,000 waits while the hooks kept running (live check, phase-20261114-0935: 5,814 waits, 384 lines sent, 138 in the
log). This also explains the missing fix-on lines of runs 0242/0312/0401. Native fix (needs an MSVC build): those prefixes
join the larger cap, and the shared cap logs one "capped" line. Test counts that do not depend on the log: fix counters
2314-2316 (`synctime`).

**socialreceivefail [measured, step log].** The 30-minute starter timeouts with a running partner: the partner's
`ReceiveSocialInteraction` had already returned false (32.0 min before, phase-20261114-0929). It then parks in
`WaitForMasterInteractionToFinish` at SyncLevel.Started, which the starter's wait for Routed can never accept; the copied
RouteFailed exit reason reaches the starter only if its social was already running (otherwise EA cancels it, which did not
stop it here). Fix `socialreceivefail` (off by default, slots 2312-2313) ends that wait through the vanilla failure call.
On from load (phase-20261114-0935): counter 2312 = 16 after two social phases, 0 errors.

**Long waits that are travel, not locks [verified, decompiled; durations measured 2026-11-13/14].** Successful waits of
5+ sim min, grouped: horse dismount (rider waits for the horse's Routed, giveup 1000; n=51, mean 29.0, max 109.5 min):
`BeingDismounted.Run` puts the dismount spot near the rider's next interaction's target, so the horse rides there first
and the wait is the ride. `PutDownPet` (pet waits for Started, giveup 45; n=38, mean 7.8, max 18.9): the Sim walks to the
drop-off point carrying the pet. Receiver waiting for the starter's Routed (n=320, mean 4.8, max 26.9): the starter's
approach walk. None of these is a fix target; the stand-offs are the failed waits (socialfinish, socialreceivefail).

**socialreceivefail off/on [measured, fix counters].** Off (phase-20261114-1011, 2 social phases): 6 waits ran to their
giveup, 5 of them initiators on a parked receiver. On from load (phase-20261114-1113, 5 social phases): 37 waits ended at
once (2312), 2 ran to their giveup, 0 of the parked case and 0 declined for another reason (2316-2319), 0 errors (2313).
One earlier run (phase-20261114-1101) hung after 5 min with the whole process silent (native route logging stopped too);
the identical rerun ran its full 45 min, and no EA loop calls a sync wait without sleeping at a level the fix acts on.

## 10. Repeated route failures, ranked by time (2026-11-14, 19 Appaloosa Plains runs) [measured, routefail lines]

Failed plans only (the log has no line for a plan that succeeds), summed per Sim, interaction and failure:

| Total | Count | Who | Interaction | Failure | Note |
|---:|---:|---|---|---|---|
| 96.2 s | 1,231 | household dog/cat (adult) | ReactToFire | DestinationPortalLocked | fix `petfireplan` |
| 47.8 s + 47.5 s | 411 + 540 | second household pet (adult, child) | ReactToFire | DestinationPortalLocked | fix `petfireplan` |
| 18.0 s + 9.3 s + 6.6 s | 207 | the same pets | ReactToFire | NoDestinationSpecified | fix `petfireplan` |
| 15.0 s | 113 | household elder | TakeBath | NoPathToDestination, blocker LitterPile | not fixed: see below |
| 10.6 s | 56 | household pet | PetFollowSimAround | DestinationPortalLocked | pet routing (other agent) |

**petfireplan [verified in the decompiled code; measured].** `FireSource.ReactToFire.Run` plans a route to the nearest
fire for every actor (one `PlanToPointRadialRange`, and a second one ignoring the fire when the first fails), but only
`DoHumanReactions` takes the route; the pet reactions read only the object found. A fire on the household lot in
phase-20261114-1113 burned 18 sim hours (day 1 17:43 to day 2 11:34) and its broadcaster re-pushed ReactToFire on the
two pets ~40 times a sim hour, so 1,358 plans failed in that run alone. The fix answers for an animal with the same
nearest-object choice and no plan (exact; humans unchanged). Slots 2320-2321.

**LitterPile blocks the tub [measured, not fixed].** A pet's litter pile sits on the only approach to a bathtub; the
elder's autonomy keeps choosing TakeBath and each try fails its plan (up to 68 tries per run, 28-349 ms each). Any change
would alter a household Sim's choices, so it stays vanilla; the player sees it as "Sim won't bathe" and cleaning the pile
ends it.

**Log gap closed (2026-11-14, logging only).** This ranking used household `routefail:` lines; NPC failures exist only as
ts3native `routefail job:` / `routefail retry:` lines, which did not name the interaction or its target. They now do, right
after the route (`routefail job: route 25ABCDEF, ReactToFire target FireSource, job seq ...`), and every ts3fixes routefail
variant shares the same `<interaction> target <type>` text, so NPC failures rank by cause the same way. Format and transport:
docs/226 "Logging".

### 10.1 `petfireplan` proven: the thrown-away pet plans also set off Overwatch resets (2026-11-25, Deck, storms s550a-s553a) [PROVEN]

Seen first in storm s550a (Appaloosa Plains, new game, seed 152, all routing fixes on, `petfireplan` not in the list): a
house fire on the active Fox lot gave 2078 routefail lines in 5 minutes (other worlds 17-58) and 12 hitches >= 150 ms
(8 >= 500 ms: "slow plan: pet" 635 and 633 ms, "other (routefail)" 602 ms). 832 lines were the two horses'
`ReactToFire` plans (§10, the plans only the human reaction uses), 1605 were "spot probe" lines.

What the probes are [verified, caller stacks in the log]: NRaas Overwatch's Stuck Check V2 (`StuckCheck.OnPostPlan`, on
`Route.PostPlanCallback`) counts every failed plan of a Sim that has not moved; past `mMinimumRouteFail` fails over
`mRouteFailTestMinutesV2` sim-minutes it resets the Sim (`ResetTask`, "Unroutable"). A horse that stands outside and
reacts to an indoor fire fails both `ReactToFire` plans every time the fire's broadcaster re-pushes the reaction
(`FailedBecauseDestinationPortalLocked`: a horse cannot go through the house doors), so Overwatch resets it again and
again. Each reset runs `Instantiation.AttemptToPutInSafeLocation` -> `Sim.AttemptToFindSafeLocation` ->
`FindPositionNearFrontDoor`: a `FindGoodLocation` over indoor spots near the front door, each spot one
`IsPointInLotSafelyRoutable` plan with the horse's door rights, all failing (about 200 failed plans per reset), and then
the horse is put at the lot position. So it is not a replan-every-tick loop in EA's reaction; it is EA's wasted plan
multiplied by a third-party stuck check. (The "spot probe" tag of `routefailprobe` was written for NPC placement on the
selected Sim; here the probe Sim is the reset Sim itself. The probe line now names its caller frames, `from ...`.)

Forced repro: `PetFireCheck startin` (EventChecks.cs) lights a fire inside the house next to the front door's inside
routing slot; storm `s552-run.sh` (5 min, seed 152, same switch list, Overwatch as installed), one arm without and one
with `petfireplan`:

| Appaloosa, indoor fire, 5 min | off (s552a) | on (s552b) |
|---|---|---|
| routefail lines | 11,498 | 12 |
| pet `ReactToFire` plan fails | 2,805 | 0 (519 plans skipped, [2320]; errors [2321] 0) |
| front-door "spot probe" fails | 9,666 | 0 |
| hitches >= 150 ms | 49 (23.9 s; 40 >= 500 ms) | 3 (1.1 s; none from routing) |
| Overwatch resets of the pets (s553a trace, off) | 33 front-door searches from `ResetTask` | - |

Both arms "Bin pair restored", no crash. The pets still react on the on arm (same nearest-fire choice). No new switch:
`petfireplan` (PetFirePlan.cs, default on since import 2026-11-27) is the fix; it should be in every all-on storm list.
`routefaillog` also writes one `frontdoor search: <Sim> in <interaction>, from <frames>` line per
`FindRoutablePointInsideNearFrontDoor` call of an active-household Sim (200 per session at most).

## 11. Events lost to one failed route (2026-11-14, reported in play)

**awardkeep [verified in the decompiled code; harness check].** A career great success (the firefighter's hero award and
the other City Hall ceremonies) books a ceremony: `CityHall.CreateCelebration` makes the award jig and an NPC mayor,
and the honoree's `AttendAwardCeremony` pushes a `GiveAward` pair (mayor master, honoree receiver). The master's
`GiveAward.Cleanup` calls `GreatSuccessCelebrationCleanup` whether or not the award was given. When the honoree's route to
the jig fails (the route tantrum reported in play), the mayor's route fails, or the pair never syncs, the mayor, the jig
and the crowd vanish and the award (`kSimAcceptedReward`, HeroOfTheCity) is lost. EA's only fallback,
`MissedCelebrationCallback` (the ceremony nobody attended), turns the award into a "Collect Service Award" pickup at City
Hall. The fix runs that same callback when the master cleans up a ceremony that is still open; the success path clears
`CanAttendAwardCeremony` before Cleanup, so a given award is untouched. Slots 2322-2323; check `AwardKeepCheck`
(setup, then expect lost / expect kept).

## 12. More events lost to one failed route: wedding, graduations, boarding school (2026-11-14 audit, static)

Follow-up of §11: the same pattern (a route failure ends the whole event, or ends it without the reward) checked in the
decompiled code. Four real bugs, one switch each, all off by default, built, NOT RUN. Every fix gives EA's own fallback
outcome; none moves a Sim or skips a route, so they apply to every Sim. A player cancel keeps EA's outcome in all four.

| Switch | File | Slots | EA bug [verified in the decompiled code] | Fix | Check |
|---|---|---|---|---|---|
| `weddingarchkeep` | `WeddingArchKeep.cs` | 2345-2346 | `WeddingCeremony.RouteToWeddingArch.OnRouteFailed` calls `Parent.Exit()` when either partner's `RouteToWeddingArchSlot` fails, so one partner's route failure ends the wedding (no ring exchange, couple stays engaged; a resort's NPC wedding never happens). The same callback serves a player cancel. EA's wedding without an arch (`CallOut` > `GatherRound` > `ExchangeRings`) is the fallback. | A Run hook marks a route failure (Run false, state still RouteToWeddingArch, Sim not in the arch's use list, no cancel exit reason); for that failure only, OnRouteFailed switches to `CallOut` and cancels the partner's arch walk; the replaced state's later callbacks are ignored. Cancels run EA's code. | `WeddingArchKeepCheck`, repro `wedding-arch-keep-check` |
| `hsgradkeep` | `GraduationKeep.cs` | 2347-2348 | `School.GraduateInCityHall.Cleanup` queues `GraduateInPlace` only for autonomous instances. EA's force-graduation alarm (14:30) retries every Sim at state None, but once. A user-directed instance (the player's "Graduate" at City Hall, or the household graduates the leading graduate queues with isAutonomous false) that fails after 14:30 leaves the Sim graduating forever: no diploma, no GraduationType. | After EA's Cleanup: not autonomous, not user-cancelled, still graduating, past 14:30, no graduation queued: queue `GraduateInPlace` as EA's autonomous branch does (same priority, MustRun, not cancellable) but with the failed instance's autonomy (user-directed). Before 14:30 EA's alarm retries. | `HsGradKeepCheck`, repro `hs-grad-keep-check` (first run: the queue refused the fallback, see below) |
| `unigradkeep` | `GraduationKeep.cs` | 2349-2350 | `Annex.UniversityGraduationCeremony.Cleanup` re-adds the ceremony when `mReaddOnClean` is set, but nothing sets it (dead code). A failed route or a late arrival loses the diploma, IGraduatedFromUniversity and the bonus trait. EA's only fallback runs on returning home (`ReturnHomeBehaviorInHomeWorld`: active household, `LastStudiedDegree` only), so NPC students, and a student who studies another degree first, lose it. | After EA's Cleanup: degree complete, rewards not granted, not user-cancelled: queue `CollegeGraduation.GraduateInPlace` for the ceremony's degree (EA's no-rabbit-hole path). The return-home fallback then finds the rewards granted. | none (needs a University world); exemption in `fix-repro-check.ts` |
| `bsrefund` | `BoardingRefund.cs` | 2351-2353 | `BoardingSchool.SchedulePickUp` takes the tuition at once. `BoardingSchoolPickUpSituation.CreateCarAndRoute.OnFailure` (car's drive failed, GetInCar refused, driver's wait ended) withdraws the student with no refund; EA refunds only when the student aged out. With no NPC car at all (`Init`), the situation exits and the student stays enrolled at home with no school and no pickup. | Refund `BoardingSchoolData.Cost` when a pickup ends with the student still enrolled in that school; on the no-car exit also withdraw the student as OnFailure does. The aged-out branch (school type already None) is not refunded twice. No notice (no localized string for it). | none (enrolment dialog, NPC car); exemption in `fix-repro-check.ts` |

Checked, not the pattern or not fixed:
- Prom (`PromSituation.GoToProm`): a Sim whose route fails only misses prom; the event goes on and the player can send the
  Sim again until 22:00. Not fixed.
- High-school graduation, player cancel: EA queues nothing for a user-directed cancel (intended; the player can click
  City Hall again while still graduating). Kept.
- Boarding school pickup when the student is not instantiated (`AlarmCallBackPickUp` does nothing): the student stays
  enrolled at home like the no-car case. Rare (household Sims are instantiated); logged, not fixed.
- Service situations that end on a route failure (`PizzaDeliverySituation`, `NewspaperDeliverySituation`,
  `MailCarrierSituation`, `DancerServiceSituation`, `SocialWorkerAdoptionSituation`, `PassportPickupSituation`, ...): each
  ends only its own visit and charges on arrival or not at all [inferred from a quick read; not traced to the payment];
  candidates for a later pass, not fixed.

Test: `node tools/harness/queue.ts` with the repros `wedding-arch-keep-check` (needs a second teen-or-older human Sim on
the active lot; the check places a wedding arch itself when the lot has none, from the buy catalogue, and deletes it at
the end) and `hs-grad-keep-check` (a teen of the active household, N/A without one). Both checks force the failure path
synchronously and clean up after themselves (no wedding, no graduation left).

First in-game run 2026-11-14 (Appaloosa Plains): `hs-grad-keep-check` "expect lost" PASS, "expect kept" FAIL with nothing
queued: the queue refused the fix's `GraduateInPlace` (`InteractionQueue.InsertInteraction` runs `entry.Test()`; with the
earlier autonomous copy the counter rose with nothing queued, so it failed there too). Cause not found statically
[open]: `School.GraduateInPlace` has `[DoesntRequireTuning]` and no ITUN in GameplayData (so no age / world / availability
test) and its definition `Test` returns true; what is left is `InteractionDefinitionUtilities.CommonTests` /
`SpecialCaseTests` / `SpecialCaseTooltipTests` on the actor. That old check used the active Sim (an adult) with a bare
table entry. Changes: `hsgradkeep` counts [2347] only when the queue took the instance and otherwise logs
`gradkeep: the queue refused ... (<InteractionTestResult>)` once per Sim; the check now uses a teen of the active
household with a table entry as EA's `GraduateSim` and the 14:30 alarm leave it, and prints the queue's Test result when
nothing was queued. Whether real play is affected follows from that result: a refusal for a real teen means the fix
needs EA's exact autonomous form or a different queue entry point. Note [inferred from GameplayData]:
`CollegeGraduation.GraduateInPlace`'s tuning allows only the University world, so EA's home-world fallback
(`ReturnHomeBehaviorInHomeWorld`) may be refused the same way; `unigradkeep` queues it in the University world.

## 13. Crowd on the honoree's goal: City Hall only (2026-11-14, `eventcrowdring` measured; audit static)

`eventcrowdring` (`EventRouteRetry.cs`, slot 2327, off by default) gathers the City Hall ceremony's NPC crowd on a ring
clear of the award jig (min radius = jig reach + 2.5 m, depth 6 m, 135° cone, 1 m spacing) instead of EA's 0-6 m around
the entrance slot that also holds the jig. Measured in game: ceremony GIVEN 6/6 with the ring (22 ring routes), LOST 6/6
without (`EventCeremonyCheck`, "start n block" forces the crowd onto slot B).

Why the ring has to supply the clearance itself (docs/548 §6, verified in the disassembly): the native radial goal
validator `0x759B20` rejects only points inside a **standing** Sim's footprint; it sees no planned goal, no reservation
and no walking Sim, and the honoree's slot goal never goes through it. The 2.5 m gap equals EA's maximum arrival push
(`kPushSimsAwayDistanceMax`), which is what separates two crowd Sims that picked the same spot.

Same pattern looked for in the other jig events with a gathering crowd (docs/548 §6 table): SimFest award (audience on
assigned `ShowFloor` watch slots or 0-3 m around a show floor; the jig is on the stage), weddings with and without an
arch (every guest position is at least `sMinDistanceBetweenAudienceAndCouple` = 4 m from the couple or the arch), high
school and university graduations (rabbit-hole visits, no jig, no outdoor crowd). **None has the crowd on the honoree's
goal, so no further switch was added**; the ring helper stays private to City Hall until a second user exists.

## 14. No bug: a sync wait does not spin on a partner that is gone (2026-11-14; `syncpartnergone` built and removed)

Every sync wait loop and whether it notices a partner that is gone [verified, decompiled InteractionInstance / Sim /
InteractionQueue / SocialInteraction*]:

| Wait | Giveup | Notices the partner gone? |
|---|---|---|
| `StartSync` pre-sync loop | `kNumMinToWaitOnPreSync` 15 | yes: returns false once the linked interaction is neither running nor queued on the partner |
| `WaitForSynchronizationLevelWithSim` (Started / Routed / Committed) | 30 (`kNumMinToWaitOnSyncStart`, `kSocialSyncGiveupTime`); 5-100 in object interactions; 1000 horse mounting | yes, through the waiter's own interaction (exit reason `Default`): any removal of the partner's linked interaction from its queue runs `InteractionQueue.CleanupInteraction`, which **resets the waiter** (`SetObjectToReset`, when the partner was stopped immediately and the waiter's interaction is running) or cancels it. A reset partner goes through that path: `Sim.DoReset` → `InteractionQueue.OnReset` → `ImmediatelyStopInteraction` on every queued interaction, the running social included. A destroyed or out-of-world partner also fails the per-tick `CanSocializeWithSyncCheck` (social waits) |
| `WaitForSyncComplete` (Completed) | `kNumMinToWaitOnSync` (15 in code, 5 in the shipped tuning) | a reset partner is at Completed, so the wait succeeds; the lock here is the masters that never signal (`socialfinish`, §9) |
| `WaitForMasterInteractionToFinish` | none | yes: ends when the master is no longer running |

**Measured** (phasesoak "syncgone", Appaloosa Plains, `SyncPartnerGoneCheck "start reset"`): partner reset at Routed
with the waiter at Started (the waiter routing to the jig): the waiter's social ended 0.3 sim min later, no
SynchronizationFailed, switch off and on alike (counters 2370-2373 +0). That is the CleanupInteraction path above.

What is left is theoretical: a partner whose linked interaction runs only nested (on the running stack but not in the
queue list) or as the transition interaction (OnReset runs only `Cleanup` on those, no linked handling), and waits that
skip both the exit-reason and the social test (`StartSync` with `ignoreExitReasons` and no social test: Grim Reaper
death sequence only). Neither has been seen. The earlier claim here ("a reset partner spins the wait to 30 min") was
wrong: it missed CleanupInteraction.

`syncpartnergone` (ended such a wait through EA's own failure call) was built and removed the same day, since EA already
ends these waits; slots 2370-2373 retired. Do not rebuild it. The 30-45 min social timeouts of §9 are the parked-receiver
case (`socialreceivefail`) and the partner's approach walk, not a gone partner.

## 15. Household route failures and route cost, Bridgeport (2026-11-14, phase-20261114-1635) [measured; causes from decompiled code]

Run: Bridgeport, one 6-Sim household at home, 5 phases x 8 min (socials, routine, camera, party, idle), ts3fixes with
`kHitchCauseHooks` and `synctime`, autshadow/autlive/autunseen on by ini. The routefail lines use the older format (the
Sim's running interaction, no target). 275 household `routefail:` lines, 6.4 s of failed plans: 1.96 s sync (sim thread:
132 `Replan`, 88 `PlanFromPoint`), 4.45 s async (worker; 55 `Plan`).

| # | Pattern | Count | Sims | Cause | Sim-thread ms |
|---:|---|---:|---|---|---:|
| 1 | Replan bursts at a bathroom door, `DestinationPortalLocked` (BrushTeeth 27 in 2 routes, Matty Watch 14 + no interaction 14 in one route each, SocialInteractionA 13 in one route, Kirby TakeShower 1) | 69 | Apollo, Matty, Indie, Kirby | someone else is in the bathroom: EA privacy; Sim stands at the door and replans once per wait cycle | 783 |
| 2 | Replan bursts at a used slot, `NoDestinationSpecified` (Fridge_EatLeftovers 12 in 2 routes, Clean 5 + 4 + 2, WashDishes 4 + 2 + 1, BrushTeeth 2) | 37 | Katelyn, William, Apollo, Kirby, Indie, Matty | another Sim stands on the target's slots [inferred]; same wait loop | 178 |
| 3 | Scare, `NoDestinationSpecified` / `NoPath` (15 replans in one route, 6 async plans, 9 one-off `PlanFromPoint`s) | 30 | Kirby, Katelyn | the 1-2.5 m, 70° cone behind the target (target's room only) has no free point in a small bathroom; EA then falls back to `RouteToDynamicObjectRadius`, whose loop replans | 249 (+435 worker) |
| 4 | Far goals 120-330 m away, `DestinationPortalLocked`, `PlanFromPoint` sync, 10 goals, minutes apart, while sleeping, reading, mopping | 24 | Indie Girl only | caller not identified (not ts3fixes or ts3agent: neither calls a FromPoint plan); the goal room on another lot is locked | 356 |
| 5 | MopPuddle / TakeBath, async, goal in the tub room (room 15) | 9 | Indie, William | privacy as in row 1 | worker 1,144 |
| 6 | Contemplate, async, `NoDestinationSpecified`, goal = own position | 7 | Kirby | `Terrain.Contemplate.Run` plans 0.3-1 m around the point but limits it to a random nearby object's room, and tries objects until one room fits | worker 277 |

**Row 1-2 mechanism [verified, decompiled].** `PrivacySituation` (toilet, shower, bath) adds `OnPortalPathPlan` on the
lot and locks the room's portals for every other Sim whose interaction is autonomous (or below `Privacy` priority); the
run's door dump shows all 13 home doors on lock `Anybody`, so the lock comes from this callback, not from a door setting
[inferred, the room numbers match: goals in rooms 10 and 15, the Sims at the room-10 door 499.5,1550.5]. A Sim already on
its way meets the change as an obstacle: `ObstacleEncounteredRouteInterruptAction` runs a StandAndWait
(`kDefaultStandAndWaitDuration` 50) and calls `mInterruptedPath.Replan()` on every cycle while the plan fails (check
frequency 1), so one wait gives 13-14 `Replan` lines (each burst here ran its full length and failed [inferred from 13-14 lines per route]). This is EA's
"wait at the door" behaviour; a skipped replan could miss the moment the bathroom frees, so nothing to change for
household Sims.

**Does autonomy re-pick the blocked target? [verified, decompiled]** Yes (Apollo BrushTeeth again ~3 min after the first
wait, same sink), but EA has no memory for that: `SimRoutingComponent.DoRoute` only adds `ExitReason.RouteFailed`; autonomy reads
failed routes only as the debug counter `NumRouteFailsPerHour`; nothing records the target. So it is vanilla, not a
forgotten memory, and not ours: autlive/autunseen replace the search with an exact one (docs/124, docs/222), and no
ts3fixes code makes a FromPoint plan. `PortalLocked` is not excluded from any memory because none exists. Not a fix candidate under the household
rule. (Pets differ: §8.)

**Row 4** is the only open one: 24 cheap plans in 43 min. The newer routefail format names the target type; the next
Bridgeport run says which interaction plans it.

**Row 6** is wasteful (each object whose room does not fit costs a plan) but a fix would change the random object draws
and, near a doorway, which room is accepted, so it is not exact for household Sims. NPC-only use is not worth 277 worker ms.

**Route cost per phase** (minute counter lines; household failed sync plans from routefail lines):

| Phase | Sim waits on route jobs: runplan (n / ms) | rtwait + disc + drop ms | household failed sync plans ms | long jobs (n / worker s) |
|---|---|---:|---:|---|
| socials | 686 / 7,633 | 1,793 | 126 | 135 / 36.0 |
| routine | 795 / 8,913 | 2,713 | 132 | 134 / 34.4 |
| camera | 764 / 10,045 | 2,765 | 611 | 148 / 38.0 |
| party | 781 / 10,069 | 3,360 | 212 | 147 / 36.4 |
| idle | 755 / 9,508 | 3,710 | 784 | 131 / 32.1 |

Phase edges come from phase.txt mapped onto the log's ms clock (anchored at the last line, +-20 s). Longest single
sim-thread wait of the whole run: 187 ms. Failed household plans are 1-8% of the sim thread's route-wait time; the rest
are plans that succeed.

**The 734 `route async: long job` lines are not sim-thread stalls [verified, counters].** All are worker jobs (729 own
START, 33 foreign, 0 capped; 344 at 200-250 ms, 385 at 250-500 ms, 4 at 500-1000 ms, one 1,199 ms own household job
before the phases). The sim thread waits for a job only at the counted sites above. Of the 493 jobs >= 200 ms with a
split, 128 of 133 s are `host` (the worker waiting for the sim thread to run the job's managed calls) and 18 s are A*.
So these jobs are slow because of the sim-thread round trips (portal and plan callbacks such as `OnPortalPathPlan`),
not path search [inferred: `host` does not split call time from queue wait]. 479 unseen NPC jobs were cut by the
UnseenFallback deadline (104 s worker) and retried, as designed.

**Sync waits.** 170 `syncwait` lines, 24 household: one failure (Apollo BeScared, partner Katelyn's Scare route failed,
row 3), no household timeout. The 8 `socialfinish: ShowVideos would signal, wait failed after 5.3/5 min` are NPC
ShowVideos waits, the known §9 case; `socialfinish` was off in this run. Other NPC timeouts: Lala -> Tiara Routed
30 min (the partner's receive failed: `socialreceivefail`), clubby -> Bubbles Started 45 min (partner's social queued
behind VisitCommunityLot).

**Result: no EA bug to fix for household Sims in this run**; no switch added. Analysis script: ad hoc (routefail lines
grouped by Sim, interaction, result and route id).

## 16. Route failures across the 2026-11-14 runs, ranked (2026-11-14) [measured; causes from decompiled code]

Input: every `phase-20261114-*` run with a ts3native.log (33 runs: Appaloosa Plains, Bridgeport, one Isla Paradiso).
Household `routefail:` lines carry Sim, goal, position, lot and room; NPC failures exist only as native `routefail job:` /
`routefail retry:` lines, which name the interaction and target type only in the newest format (Bridgeport
`phase-20261114-1737`, `-1835`, `-1911`) and carry no Sim name or position. Key: interaction, target type, result,
blocker, household vs NPC. Already explained and left out: pet fire plans (§10), bathroom privacy and used-slot replans,
autonomy re-picking a failed target, Scare, Contemplate (§15), hidden apartment rooms (docs/552), combo rabbit holes
(docs/551), award crowds (docs/548). Analysis script: ad hoc (routefail lines grouped by that key; per-Sim and per-goal
repeats; distance and level gap between goal and Sim).

| # | Pattern | Count | Who | Cause | Verdict |
|---:|---|---:|---|---|---|
| 1 | NPC plans cut by our UnseenFallback deadline and retried (GoHome 218, VisitCommunityLot 185, VisitLot 29, GetMail 29, trash 66, ...) `NoPath` cut | ~650 in 3 runs, ~120 s worker | unseen NPCs | ts3native cut at 220 ms (designed); retries: 734 succeeded, 81 failed | ours, as designed; no change |
| 2 | Foreign plans with `interaction unknown`, seen NPCs: `NoDestinationSpecified` 539 + `NoPath` 103 + `PortalLocked` 29, up to 49 per Sim | 671, 16 s worker | ~60 NPCs near the household | plans made while ts3fixes marks an own check (`sStuckIn` = EA's `LotManager.RunStuckCheckHelper`, `sInRoutableCheck` = `Sim.IsPointInLotSafelyRoutable`), which send no interaction id [verified, `UnseenFallback.cs` `ownCheck`]: EA's stuck check probing NPCs it finds not safely routable [inferred: the alternating 10/16 results match `AttemptToFindSafeLocation`'s tries] | EA stuck check working; which NPCs and where is not logged (next: give own-check plans a pseudo interaction id, e.g. `StuckCheck` / `SafelyRoutable`) |
| 3 | Household `PlanFromPoint` sync `NoPath`, goal 1-3 m away on a door tile (x.5 coordinates), one plan each, any interaction | 146 in 3 runs, 550 ms | all 6 household Sims | caller not identified (a FromPoint plan to the next door; the running interaction is unrelated) [inferred] | cheap, no player effect; not pursued |
| 4 | NPC `GoToVirtualHomeInternal target Lot` `NoDestinationSpecified`, 0 ms, 1-2 per Sim | 134 | ~125 apartment residents | `Sim.GoToVirtualHomeInternal.Run`: route to the NPC door's slots, then the nearest elevator; on any failure the Sim fades out where it stands [verified] | vanilla outcome (fade either way); the door route failing is docs/552 territory (hidden units) |
| 5 | Household far `PlanFromPoint` sync `DestinationPortalLocked`, goals 120-1000 m away on other lots, interaction unrelated (Indie Girl's 24 in `-1635`) | 67 in 5 runs, 839 ms | the selected Sim (Indie Girl, Apollo, Suzy, Matty, William, Katelyn) | **identified:** `Sim.IsPointInLotSafelyRoutable(lot, pos)` called on `PlumbBob.SelectedActor` by `Service.CreateSim` and the virtual-resident instantiation (NPC apartment door slots) and on `Sim.ActiveActor` by `Service.GetPositionNearLotCorners`: a plan with the selected Sim's route options from a spot on the NPC's apartment lot to that lot's 4 corners and 4 edge midpoints (the logged goal = the first destination, corner 0) [verified code; goal = corner inferred from the call shape]. It fails `PortalLocked` when every corner/edge point lies behind a locked or hidden-room portal for that Sim, so the NPC-door spot is refused and EA falls back (elevator spot, corners, mailbox, `lot.Position`) | EA design (a household Sim's portal rules judge an NPC's spawn spot); no household effect. Handed to docs/552: these refusals feed the fallbacks that can land inside hidden rooms |
| 6 | Household `AutonomousSplashInPuddle` `NoDestinationSpecified`, async, 3 plans per try | 32 in 6 runs, 1.7 s worker | Matty, Apollo (Bridgeport), the Collins family (Appaloosa Plains) | **EA bug:** rain puddles on a roof. 32/32 goals are exactly 3.0 m above the Sim, often straight over it, while the Sim stands in an indoor room; `IsPuddleValid` checks only "outdoors" and "no object" (no floor test, unlike `PuddleManager`, which needs `HasSolidFloor`); the definition's Test asks only the native "any valid rain puddle", so autonomy offers the splash again after each failure [verified code; roof inferred from positions] | fixed for NPCs: `splashroof` (below); household stays vanilla |
| 7 | NPC `PutDownChild target Sim` `NoPath`, 6 plans per try (Nearby, Mailbox, 4 lot corners), 5 tries | 47 (one NPC, same moment in `-1737` and `-1835`, same save) | one NPC carrying a child | `ChildUtils.PutDownClass.PutDownChild` [verified]; even the nearby jig fails, so the NPC cannot route at all where it stands [inferred: trapped, possibly a docs/552 hidden room] | not fixed; needs the NPC's position |
| 8 | NPC bar: `Tend target BarProfessionalLounge` `PortalLocked` (2 bartenders, 8 + 4) and patrons `OrderDrink` / `OrderFood` / `OrderConversation` `NoPath` then `NoDestinationSpecified` (~40, one pair each) | ~55 | Bridgeport lounge and pub staff, patrons | bar or its customer slots behind a portal the NPC may not pass [inferred; no position in the log] | open; drinks may stop at that venue; needs NPC positions |

Smaller: NPC `VisitLot target Lot` `NoDestinationSpecified` 144 (0-1 ms each, cheap); `GoHome` `PortalLocked` 20 (7 NPCs);
harness `gohere` commands end `route false` mostly by `HigherPriorityNext` (1,842 of 2,600: autonomy took over, not a
route failure).

**splashroof [built, not run (needs rain)].** `SplashRoof.cs`, tuning `kSplashRoof` (off), NPC actors only (`IsNpcSim`): while an
NPC's `AutonomousSplashInPuddle.Run` runs (its task id is held), the hooked `Terrain.SplashInPuddle.IsPuddleValid(Vector3,
bool, bool)` also refuses a puddle above the ground floor whose tile has no solid floor (either of EA's lookups,
`GetLotLocation` / `GetLotLocationAtFloor`), so the three tries go to reachable puddles and a lot with only roof puddles
costs no plan. Household Sims, the selected Sim and unknown scope run EA's Run unchanged; other `IsPuddleValid` callers
(pie-menu splash, pet puddles, worker threads) pass through. Counters 2384 roof puddles dropped, 2385 NPC runs affected,
2386 errors. Check `SplashRoofCheck` (`SplashRoofCheck.cs`), independent of where the active Sim stands: `list` counts the
active lot's rain puddles and roof ones; `run` takes an existing roof puddle (else sets a rain-puddle floor feature on a
roof tile above an indoor room), places one human NPC directly in the room below, runs EA's puddle choice for it step by
step (same filter, distance sort and three plans as the Run, inside the hook's `SrBegin`/`SrEnd`), queues EA's interaction
and prints `verdict: BROKEN|FIXED|NOT A BUG|N/A`; `clear`, `result`. Repro `splash-roof-puddle` (Bridgeport): off `run`
must read BROKEN (roof puddle tried, `FailedBecauseNoDestinationSpecified`), on `run` FIXED (2384/2385 rise, a reachable
puddle or none picked). First try (phase run ~19:54, old check) exercised nothing: it needed the Sim under a roof and
placed the NPC off the lot; the lot had 96 rain puddles.

**Log gap.** Rows 2, 7 and 8 cannot be placed without the NPC's name, position and lot; the native job lines carry only
the follower id. Next logging step (not built): an NPC variant of the `routefail:` line for repeats only (same Sim and
interaction failing 3+ times), within the shared 400-line cap.

## 17. Script errors, loops, leaks and hitches across the 2026-11-14 runs (2026-11-14) [measured; causes from decompiled code]

Input: every `phase-20261114-*` run (49 dirs: Bridgeport, Appaloosa Plains, Isla Paradiso, Starlight Shores), their
`ts3native.log`, `leak-*.json` snapshots, phase / report / autotest files, the eight `auto-*/cycle-1.log`, and the
NRaas ErrorTrap `ScriptError_*.xml` files of the day. Left out as already explained: route failures (§10, §15, §16),
combo buildings, hidden rooms, apartment farewell, parties (docs/547-554), harness hitches (docs/247 §14), garbage
collection, pet routing, the Starlight Shores 20:35 crash (triaged elsewhere; no new evidence here).

| # | Pattern | Count | Who / where | Cause | Verdict |
|---:|---|---:|---|---|---|
| 1 | Leak: `ReactionBroadcaster`s of shuffleboards | 0 -> 94 (`-1737`), 0 -> 148 (`-1835`, 79 on one lot), every Bridgeport run with a bar | Bridgeport bars | **EA bug [verified, decompiled]:** `Shuffleboard+Play.TriggerReactions` overwrites `mBroadcaster` / `mPetBroadcaster` without `Dispose`; `Run` calls it after the throw and again at round end, `Cleanup` disposes only the last pair. Broadcasters have `TimeDuration -1`, so each round end leaves a pair that pulses the lot every second for the session, and later players get one stale `BroadcastCallback` per leaked pair (reaction toward a gone thrower; at game over `Quit` + `CanceledByScript`) | fixed: `shuffleboardbcast` |
| 2 | Leak: `CookingProcess.ObjectClickedOnDestroyed` listeners (`kObjectStateChanged`) | 0 -> 132 (`-1635`), 0 -> 122 (`-1737`), 0 -> 50 (`-1113`, Appaloosa Plains); grows in every world, linear in play time | fridges / stoves of every cooking household | **EA bug [verified, decompiled]:** `AddObjectListeners` hooks the clicked appliance for the cook and again in the private copy constructor of every serving; only `CompletedMakingARecipe` (the original process), an appliance repair / buy-mode move (`ObjectStateChanged`) or delete removes them. An eaten / washed / thrown-away serving leaves its process on the appliance's listener list and `ObjectDisposed` event [owner state inferred; `CookClickCheck list` prints it] | fixed: `cookclicklistener` |
| 2b | same code: `ObjectDisposed` handler stranded on the appliance | not counted by the census | stoves, grills, microwaves, food synthesizers | **EA bug [verified]:** the Prepare steps (`Pot`, `FryingPan`, `BakingDish`, `FoodTray`, `Microwave`) set `ObjectClickedOn = null` directly, the state-change handler nulls it and returns `Remove` without unhooking, `FutureFoodSynthesizer` re-points it without re-adding: `ClearObjectListeners` can then no longer unhook `ObjectDisposed` from the object it was added to | fixed in `cookclicklistener` (counter 2413) |
| 3 | NPC birth loop: `HaveBabyHospital` restarted after failed plans | 40 `ufloop:` restarts in one run (`-0212`, the only run with `unseenfbloopdet` on); the same Sim plans all run long in every Appaloosa Plains run | Patricia Bedlington (NPC, Appaloosa Plains, combo hospital) | her plans run 400-930 ms and are cut by our deadlines (11 UnseenFallback + 2 KindDeadlines cuts, 13/13 retries succeed), yet the interaction restarts; EA's `HaveBabyHospital.Cleanup` re-adds `Pregnancy.HaveTheBaby` one sim-minute later whenever `BabyShouldBeBorn` and no newborn exist, and `HaveTheBaby` picks the hospital again whenever it is closer than home: no home-birth fallback after a failed trip [verified, decompiled]. Why the trip fails after a good retry is open (combo `ComboHospitalScienceLab` entry, or our cut) | candidate, not fixed: needs one Appaloosa Plains run with `unseenfbloopdet` + the interaction's exit reason logged |
| 4 | ErrorTrap correction at load: `Destroyed Object Found: DoorSingleColonialContemporary` x4 | 9 of 9 Bridgeport loads since 19:11 (0 before, same world file) | Bridgeport new game, ~10 s after live mode | not EA script; starts with the first `ts3fixes.hr*` test package (only switch difference vs `-1835`: `kHitchCauseHooks` off); no ts3fixes code destroys doors [read] | benign (docs/552 §11, measured with `etderefdiag`): ObjectId-0 dead graphs (4 doors, 3 combo buildings, 5 MovieSets) closed among themselves, same with hooks on or off; "since 19:11" = the ScriptError folder keeps only ~11 files |
| 5 | Script exceptions (`ts3fixes: ... Exception`, ErrorTrap stack traces) | 0 in game; 2 agent-side (`SocialTest.AskToChangePortTest` NRE from a harness `reflect_call`; combo check `TargetException`, known) | - | - | nothing new |
| 6 | Sync waits that time out | ShowVideos 101 (§9 `socialfinish`), social `Committed/Routed failed` (§9 `socialreceivefail`), DanceTogether 9 | NPCs | known §9 | nothing new |
| 7 | Overwatch nightly resets with `result False` (`home False`) | ~20 Sims in runs with `owresettime` | NPCs, many pets | NRaas Overwatch's 03:00 reset, not EA | not ours to fix; log only |
| 8 | Game hitches >= 300 ms named as game by `hitchwatch:` | 2 (462, 569 ms, no agent call, no cause named) | Appaloosa Plains | `alarmtime` / `owresettime` were not on in those runs | nothing to act on (docs/247 §14 next run names them) |

Checked and not leaks: `School.RegularWorkDay*` alarms (plateau at enrolment, ~85), `ObjectSpawner.DeleteOldestUntakenObject`
(steady 60-86), `SingerEventListener` (flat 36), Cake / PizzaBox broadcasters (bounded by the objects).

**shuffleboardbcast [built, proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15].** `ShuffleboardBroadcast.cs`, tuning `kShuffleboardBcast` (off). Hooks
`Shuffleboard+Play.TriggerReactions` and `Play.Cleanup`: a pair about to be replaced is kept and disposed right after that
Play instance's own Cleanup, where EA ends the current pair. While the interaction runs both pairs pulse as in EA's code,
so every reaction during the throw is unchanged for every Sim (household included); only the pulses after Cleanup (the
leak) go. Counters 2409 pairs replaced (counts with the switch off), 2410 disposed, 2411 errors. Check `ShuffleboardCheck`
(`ObjectLeakChecks.cs`): `run` makes a Play instance on an idle board with one NPC, runs EA's TriggerReactions twice and
EA's Cleanup, counts the board's broadcasters: off BROKEN (+2 left), on FIXED (+0, 2410 +2); `clear` disposes the off
arm's pair. Repro `shuffleboard-broadcast-leak` (any world with an idle shuffleboard; Bridgeport). Natural check: census
`broadcasters.by_object_type.Shuffleboard` stays near 2 per board in use.

**cookclicklistener [built; proven in game 2026-11-15 after rework (pass moved to the hourly sweep, in-use appliances skipped): off BROKEN / on FIXED; all-on Bridgeport soak 0739: household-lot Replan-sync PortalLocked 0 (first version: 57/32/38 vs off 4/0/4, bisected 2026-11-15). Was proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED. Bisect: Trawler Lodge Replan-sync PortalLocked 57/32/38 with it on, 4/0/4 off, 0 with all-on minus it (phase-20261115-0702); bursts of ~10 replans by one household Sim toward the toilet/shower/keyboard while the room is privacy-locked. No code path from the cleanup to routing found [verified: EventTracker defers removals from a list being dispatched]. Changed 2026-11-15, untested: the check runs in the hourly pass instead of inside every AddObjectListeners call, and skips appliances in use].** `CookClickListener.cs`, tuning `kCookClickListener` (off). Hooks
`CookingProcess.AddObjectListeners` (notes the process and the object it hooked; checks 8 noted entries per call, no
alarm) and `ClearObjectListeners` (drops the note; unhooks `ObjectDisposed` from the original object when `ObjectClickedOn`
was nulled or re-pointed). A process whose Owner has been destroyed for 10 sim-minutes gets EA's own
`ClearObjectListeners`. A dead process's handlers write only its own fields, so the fix is exact for every Sim; servings in
fridges and inventories are live and keep theirs. Counters 2412 removed, 2413 handlers unhooked, 2414 errors. Check
`CookClickCheck`: `run` builds one process with EA's constructor (a fridge as clicked object, a new single serving as Owner),
destroys the serving, runs the fix's check without the wait: off BROKEN (listener still on the fridge), on FIXED (2412 +1);
`list` splits live listeners by owner state. Repro `cook-click-listener-leak` (any world). Natural check: census
`events.by_delegate.CookingProcess.ObjectClickedOnDestroyed` flat with the switch on.

Loopholes seen in the same code, not fixed: `HaveTheBaby` reads `mMom.LotHome` without a null check (a homeless NPC mother
throws); `HaveBabyHospital.InRabbitHole` spins `Sleep(0)` until each newborn's placeholder interaction starts, with no
bound; `Shuffleboard.Play.BroadcastCallback` on a stale broadcaster can `Quit` a Sim from a later game at game over
(gone with `shuffleboardbcast`).
