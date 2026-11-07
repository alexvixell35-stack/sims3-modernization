# 226. Show Off Badges: "cannot go there" (player report, triage)

Status 2026-11-02: logging build and repro built, not run in game. No fix yet. Root cause found 2026-11-03 (partner plans to the unplaced jig while the initiator sleeps in AsyncWait): fix `asyncjigsync`, docs/240.

## Report

A child of the active household cannot do the scouting social "Show Off Badges" to any adult, inside or outside; the Sim shows the "cannot go there" balloon. Works after removing our mods (unclear whether the player removed `ts3fixes.package`, `ts3native.asi` or both). Player versions: ts3native 0.36.105, ts3fixes 0.1.1 (release v0.2.4).

## What the game does [verified, decompiled code]

- `Sim+ShowOffNewSkills` (pie menu "Show Off ..."): `Test` needs the target older than toddler, the actor not singed, and `DoShowOffMove.HasSkillLevelForMove`: Badges needs Scouting level >= 1. `Run` creates `Sim+DoShowOffMove` with the child as actor and the adult as target, master side.
- `DoShowOffMove.Run` (master): pushes the linked instance on the adult, syncs both, then creates a `SocialJigTwoPerson`, plans the child's route to the adult's position (`PlanToPoint`) and calls `PlaceJigAlongRoute(child, adult, route)`. `SocialJigTwoPerson.PlaceJigAlongRoute` ignores the route: it places the jig with `FindGoodLocationNearby` next to the adult (`PlaceJig`) and adds it to the world. Then `RouteToJigA` plans to the jig's slot A and `DoRoute` runs it. `SimRoutingComponent.DoRoute` with a failed plan runs `DoRouteFailureBehavior` (the balloon) and returns false.
- So the balloon means the plan to the jig slot failed (or the follow failed). The first plan only costs time; its result is never used.

## Suspects [inferred, unconfirmed]

1. `[RouteAsync]` own job for the `PlanToPoint` plan (the child's own task, so ts3fixes sends START) and for the `RouteToJigA` plan.
2. KindDeadlines (`KindDeadlines=1`, `KdOwnPlanMs=250` in the v0.2.4 ini): an own job past 250 ms is cut and ends as a failed plan. A plan to an occupied spot (the adult's position) or to a fresh jig slot may search long. A cut job of >= 200 ms already gets a native `route async: long job N ms, own START, ..., capped (KindDeadlines)` line.
3. `[Discourage]` / `DeferFootprint`: the jig is added to the world right before the slot plan; if its footprint update is deferred while a job runs, the slot plan may see the world without the jig's footprint.
4. `[OptsInit]`: zeroes two search option fields in every plan; no runtime switch.

## Logging (ts3fixes `RouteFailLog.cs`, switch `routefaillog`, on; counters 687-689)

Lines go to `ts3native.log` through the [UserDiag] channel; 30 a minute, 400 a session at most.

- `routefail: <Sim> 0x<desc id> <age> active, <interaction type>, <Plan|Replan|PlanFromPoint|ReplanFromPoint> <async|sync> <result> blocker <type>, goal x,y,z, at x,y,z, lot 0x.. room N, N ms`: every failed plan of an active-household Sim. `DoRoute false plan <result> blocker <type> [exit <reason>]` for a failed DoRoute (the balloon).
- Since docs/242 the DoRoute and jig placement hooks are gone (logging only, they refused saved Sims): `routefail:` has plan lines only, `showoff:` has plan lines and `route end ok|false` for the repro's two Sims.
- `showoff: ...` the same fields for every plan, jig placement (`jig placed|NOT placed at x,y,z (d m from <adult> <age> room N), route-to-B plan <result>`) and DoRoute of a Sim inside `DoShowOffMove`, also on success, so one attempt reads as: Plan (PlanToPoint) -> jig -> Plan (RouteToJigA) -> DoRoute.
- `async` = the plan waited on a ts3native job (AsyncWait ran); `sync` = no START or START refused. An async failed plan of about 250 ms or more next to a native `capped (KindDeadlines)` line = suspect 2.

## Player test build (showoff1)

v0.2.4 + `RouteFailLog.cs` only (branch `test/showoff-badges-v024`, ts3fixes version `0.1.1-test-showoff1`); the v0.2.4 ini with `[RouteAsync] LogFallbackMs=50` and `LogFallbackStack=8` (sync plans >= 50 ms with their script callers, 20 lines a minute); the player keeps their 0.36.105 asi. No native change: the existing lines cover cuts (long job lines), sync plans (sync plan lines) and the per-minute route job, kind-deadline and footprint-defer counters.

## Repro `showoff-badges-route`, queue `showoff-badges.json`

Baseline-BP, asi 0.36.105 (`game-copy/ts3native.0.36.105.rel020`), ts3fixes from master. Test shortcuts (labelled): a child made with `make_sim` if needed, Scouting set to 1, both Sims placed (outside at the mailbox, inside next to a ground-floor object in a room), then EA's `ShowOffNewSkills` (Badges) is queued as the pie menu does. Arms in one load: shipped, then `routeasync`, `kinddeadlines`, `discourage`, `deferfootprint` off one at a time (`native_set`), outside and inside each; `record` notes each arm, `verdict` throws if any arm failed and lists all. Second job: the same with `[OptsInit] Enabled=0`.

`node tools/harness/queue.ts tools/harness/queues/showoff-badges.json --no-smoke`
