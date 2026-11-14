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
