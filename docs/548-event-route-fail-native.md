# 548. Award-ceremony jig route failures: what the native planner does (draft)

Written 2026-11-14. **Static only, no game run.** Question: a Sim routing to the City Hall award jig (`SocialJigTwoPerson`, placed with `FindGoodLocationNearby(tempPlacement: true)` near the rabbit hole's `RoutingSlot_0`, with a celebrating crowd around it) fails its route ("route tantrum") and the event is lost; logs show `FailedBecauseNoPathToDestination` / `DestinationPortalLocked` / `NoDestinationSpecified` with a blocker.

Tools: capstone 5 over `game-copy/rea/TS3W.repack.exe` (repack of 1.67; every address below read in its bytes), the local decompile in `game-copy/decompiled` (read only, nothing copied), docs/93, docs/101, docs/140, docs/177. REA was not used: no Java or Ghidra on this machine, so it could not run. OpenBarrio covers none of these routing functions (only `0x75AFA0`, which calls `0x759B20`). Tags: **[verified]** = read in bytes or decompiled C#; **[inferred]** = reasoning, with the check that would confirm it.

## 1. Script side (decompile)

- `CityHall.CreateCelebration` creates the jig `kPreCelebrationTime` before the ceremony, at `RoutingSlot_0`'s position, hides its model, runs `FindGoodLocationNearby(tempPlacement: true)` and moves the jig there if that succeeds. The jig lives until `GreatSuccessCelebrationCleanup`, which runs on `GiveAward.Cleanup` (master) or on `MissedCelebrationCallback` after `kMaxCelebrationTime` [verified].
- `SocialJig.OnStartup` calls `RoutingFootprintLocking.AddLockKey(jig, MustHaveKey, jig id)`: the jig footprint is locked to every route that does not carry the jig's key [verified C#].
- `GiveAward.Run` uses `RouteToJigA(Actor)` / `RouteToJigB(Actor)` and passes no Sim to ignore, so the built-in retry (add that Sim to the ignore list, then `Replan`) never runs. Both `PlanRouteToJigA` and `RouteToJigB` turn `UseAutoSlotFootprintLocking` off, call `SetLockKeys(jig)` and `AddObjectToIgnoreForRoute(jig)`, then `PlanToSlot(jig, SimA/SimB slot)`. The slot list holds **one slot per actor** [verified].
- Default Sim route options (`SimRoutingComponent.SetRouteOptions`): `PushSimsAtDestination`, `ReplanToFindObstacleWhenPathPlanFails`, `CreateSubPaths`, `CheckForFootprintsNearGoals` and `PenalizeGoalsOnDifferentLevels` are on; `BlockedByPeople` (0x4) and `BlockedByDynamicFootprints` (0x1000) are off [verified].
- `DoRoute` loop (`SimRoutingComponent`, around the `CheckForFootprintsNearGoals` block): after a route, if `IsPointObstructedBySim(end)` names a Sim other than the interaction target and `PushSims(end, 0.5)` fails, it sets **`BlockedByPeople = true`, runs `Replan()`**, then restores the option. If that replan fails, or the end point is still obstructed, the Sim runs an emergency get-away route, the route counts as **failed**, and `PlayRouteFailureIfAppropriate(blockingSim)` plays the balloon [verified C#]. A push request in the middle of a route (`mbPushRequested`) also replans with `r.Replan()` [verified]. Together these explain the repeated `Replan sync` rows against the same goal [inferred].

## 2. Native: how a slot goal is accepted or rejected

`PlanToSlot` produces a slot-array destination. Its generator, `RouteDest_SlotArray_Generate 0x7596E0` (`ret 0x20`), runs once per slot [verified]:

1. It computes the slot transform, optionally applies the slot position helper `0x76F0A0` (byte `dest+0x54`), and snaps the height.
2. `0xB01390` true means the slot goes straight to the candidate list with no check (`0x75998B`).
3. Otherwise it calls `RoutingWorld_RouteToObjectCheck 0x831530` with these arguments: the lot at the target's position (`0x74BE50` -> `World_LotLookup 0xC68990`), the target (the jig), the point, the route options, the age/gender/species flags, **a7 = route has `BlockedByPeople | BlockedByDynamicFootprints`** (`0x81C880(0x1004)` at `0x7596FD`), and an ignore id (`dest+0x40/+0x44`) [verified].
4. Result code 0 means candidate. Any other code means the slot goes to the non-candidate list `0x74C650` with flag 1, and the blocker id is kept when one was returned (`0x7599CF`).
5. With no candidates and a blocker id, the generator writes the blocker id and **the blocker object's own position** into the result (`0x759A69..0x759A7F`) and returns false. `0x751FF0` then sets `mType` 16 and blocker type 3 (Object) (docs/101 §4.1). **So `mBlockerLocation` is, by construction, the blocking object's position.** A log that measures "goal to blocker" from `mBlockerLocation` reads 0.0 m every time. Check which field the sibling's "goal at the jig position (0.0 m)" figure uses before treating that distance as a finding [verified code; the log field is not checked].

Inside `0x830340` (the route part of `0x831530`, `ret 0x28`) [verified]:

- **Step 2, obstacle query at the slot (`0x82EDB0`):** the jump table at `0x8308C0/0x8308C8` gives code 4 for footprint types 2, 4, 0xC, 0xD, 0x11, 0x15 and 0x17 (wall, roof and similar types; docs/93 §2.2). Sims (0x14) and objects (1, 9) do not trigger it. The check skips only the **target's** own id. It does not read the route's ignore list or lock keys.
- **Step 5, early accept:** when the slot point lies inside the target's own footprint (`+0xB8` point test at `0x830683`), the code is 0. Jig slots probably sit inside the jig's footprint, in which case only step 2 can reject them [inferred; confirm by logging `0x830689` hits for the jig].
- **Step 7, `BlockerCheck2d 0x82E340`:** it skips ids on the route's ignore list. `0x81C970` checks the options list at `+0x38`, which is the list `AddObjectToIgnoreForRoute` (`IC 0x758F40`, options at `route+0x158` -> `0x81C8D0`) appends to. With option 4 set it also skips the Sim's own list `+0x2C0` [verified]. Codes:
  - **Sim (0x14): code 1 only when a7 is set or the route has `BlockedByPeople`** (`0x82E3F6..0x82E412`, `0x82EA5E`), with no blocker id.
  - Portal-locked footprint: code 3 with id.
  - Type 9 object: code 6 with id.
  - Other types: code 7.

Footprint cost class `0x807090` (used by the search and the quadtree filters): an id on the route's ignore list gives class 0 (passable). Otherwise a locked footprint gives class 6 (blocked) when the lock test `0x946E90` fails for the route (no key), and the footprint's default class `+0x34` in every other case [verified bytes; "6 = blocked" inferred from the docs/140 code list].

Status mapping (docs/93 §2.1, docs/101 §4.2) [verified]:

| Status | When |
|---|---|
| 10 (`NoDestinationSpecified`) | no candidates, or the goal step rejected every goal and the first rejected record's `+0x30 != 1` |
| 18 (`DestinationPortalLocked`) | the same, with `+0x30 == 1` (locked door on the way to the goal room) |
| 16 (`NoPathToDestination`) | search failure; slot generator failure with a blocker; or P1, where every slot was a non-candidate, the plan went to those slots, and the route holds a real path yet reports 16 |
| 17 (`FailedDestinationBlockedBySim`) | never produced |

`0x752620` names the first blocker in the obstacle replan's list that is not the follower. It does **not** filter the route's ignore list [verified `0x752620` per docs/93 §2.2; whether the replan collects ignored footprints is inferred].

## 3. Answers

**(a) Does the jig block its own slots?** Not for a route built by `RouteToJigA/B`: the jig is on the ignore list (class 0 in the search, skipped in `BlockerCheck2d`), the route carries its lock key, and step 2 skips the target id [verified]. It **does** block every other route that lacks its key and does not ignore it. The search sees a `MustHaveKey`-locked footprint, and a slot check against another target names it as a type-9 object blocker. The jig sits at, or next to, City Hall's `RoutingSlot_0`, the rabbit hole's own routing slot, for the whole pre-ceremony window. Rabbit-hole entry routes, the honoree's other routes to City Hall, and `GetRouteFailPathNearObject` replans therefore get "blocker SocialJigTwoPerson" [inferred from the code path; confirm by logging the route's destination object and lock keys on each jig-blocked failure]. `tempPlacement` affects only the `FindGoodLocationNearby` search. The jig was already created in the world by `SocialJigTwoPerson.Create`, and the logs naming it as a blocker show its footprint is in the routing world [inferred].

**(b) Managed API that excludes the jig:** `Route.AddObjectToIgnoreForRoute(jig.ObjectId)` (ignore list, class 0) and `Route.SetLockKeys(jig.ObjectId.mValue)` (passes the lock). `RouteToJigA/B` already call both. Other routes need both, or the lock removed with `RoutingFootprintLocking.RemoveLockKey`.

**(c) A Sim standing on the goal:** it is not a hard block by default. Slot checks count Sims only with `BlockedByPeople` or `BlockedByDynamicFootprints` [verified], and the search treats Sims as dynamic, handled by pushing [inferred]. It becomes a hard block in EA's own `CheckForFootprintsNearGoals` fallback (`BlockedByPeople = true` + `Replan`) when the push fails. With one slot per actor, that replan has no other goal, so it fails with a Sim blocker beside the goal, which is the 620/727 pattern [verified C#; native outcome inferred]. Knobs: `Route.SetOption(RouteOption.CheckForFootprintsNearGoals, false)` drops that fallback; `BlockedByPeople` stays off by default; `AddObjectToIgnoreForRoute(sim)` per crowd Sim; the `simObjIdToIgnore` argument of `RouteToJigA/B`; `PushSims` with `waitForPushes` before planning. There is no native goal relaxation for a single-slot destination.

## 4. Native bugs or oddities

1. **P1 reports a real path as 16** (docs/93 §3) [verified code]. When every slot is a non-candidate, the planner still plans to the slots and gets a path, then reports failure.
2. **The blocker is not filtered by the ignore list** in `0x752620`. A failed route to the jig can name the jig, or an ignored Sim, as the blocker even though that object was not what blocked it [inferred].
3. Step 2 of `0x830340` does not honour the ignore list or lock keys (it skips only the target), so a wall or roof at the slot is final [verified]. This is not the crowd case.
4. No loop or stale state was found in these functions. The repeated same-goal rows are script replans (§1) [inferred].

## 5. Fix options, ranked (cheapest first)

1. **Script, ts3fixes:** for jig routes (`RouteToJigA/B` callers, or `GiveAward`), set `CheckForFootprintsNearGoals = false` on the jig route, or exempt routes whose destination is a jig from the fallback. Then call `PushSims` on the slot area with `waitForPushes` before `DoRoute`. Household routes change only on the failure branch (an EA bug fix); keep it behind a switch.
2. **Script:** create or relocate the award jig at `GreatSuccessCelebrationStart`, or at `GiveAward` start, instead of hours earlier. Add it to the ignore list (plus `SetLockKeys`) of City Hall entry routes, or remove its lock key until the ceremony starts. This removes the "blocker SocialJigTwoPerson" failures of non-jig routes.
3. **Script:** pass the partner, and crowd Sims standing on the slot, as ignore ids (`RouteToJigA(actor, simToIgnore)`), so EA's retry runs.
4. **Native (last):** filter the blocker in `0x752620` against the options `+0x38` list (log quality only), or treat P1 as success. Both change native result codes for every route, so neither is recommended.

Probes before the fix: log the failing route's destination type and object, its lock keys and ignore list, `mNumCandidatePointsGenerated` / `Accepted` / `Rejected`, `BlockedByPeople` at plan time, and whether the plan was the `CheckForFootprintsNearGoals` replan.
