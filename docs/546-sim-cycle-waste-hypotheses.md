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
