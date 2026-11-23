# 643. Native no-path memo for NPC water and boat plans (`[RouteAsync] NoPathMemo`)

2026-11-19. Source: Isla Paradiso logs (docs/642 §6), ts3native code, Steam 1.67 disassembly. Built (`nopathmemo.cpp`), default
off. Ran in the 2026-11-20 storms on the combo branch without acting (docs/680 §2: served 0); §8 stale-answer check
2026-11-23. Counters: `ts3native_nopathmemo` export (docs/registry.md). §7: fuzzy match and household scope
(`npmfuzzy`, `npmhousehold`, slots 4240-4243).

Note for this repo: "docs/642" below is the boat no-path memo doc, which is docs/851 here. NoPathMemo itself does not need
any other combo-only file; floodmemo.cpp / smoothmemo.cpp, which read its player-command marks on the combo branch, are not
part of this change.

## 1. Problem [verified, log + code]

- NPC water and boat route plans fail with FailedBecauseNoPathToDestination after ~300-650 ms. The time is inside
  `PathPlanner_PlanHierarchical 0x65E0D0` before any world A* runs. The plan has PlanDeadlineMs' 300 ms session, but none of
  its exits is reached: `route deadline: sync plan ... past its deadline, entry Route_PlanLocked (no IC); world A* capped 0,
  cell skipped 0, water skipped 0`.
- EA plans the same route again every ~10 ticks: the houseboat leg (own sync kind 2, PlanFromPoint, `ArWdPass` -> game IC ->
  Route_PlanLocked -> `CallPlan` -> `CallPlanBudget` -> `PdPlanBegin`), boat-rider jobs (worker, kinds 0 / 3) and watchdog
  plans (docs/642 §1, §4, §6).
- Correction to docs/642 §6 and the old a10.cpp comment: `ArWdPass` plans skip `SyncIc`, but their Route_PlanLocked still
  opens PlanDeadlineMs' own 300 ms session through `PdPlanBegin`. They are not deadline-free; the deadline just has no exit to
  take.

The managed memos (`boattaximemo`, `houseboatlegmemo`) each cover one caller. This one sits below all of them.

## 2. Design

Site: `CallPlan` (earlyout.cpp), the single Route_PlanLocked path of the sim thread (`RunPlan`) and the route worker (sync
`PlanJob` and async jobs). `CallPlan` calls `NpmPlan` (nopathmemo.cpp) when the key is on. `NpmPlan` wraps `CallPlanEo` (the
plan inside the [RouteEarlyOut] request scope), so an answered plan does not count as an early-out request.

**Who** (household rule): only routers of known NPC scope, 2 unseen or 3 seen NPC.
- On the sim thread (or the worker serving the sim's sync plan): the UfWhy answer ts3fixes sent for the plan's route
  (`g_kdScopeH` / `g_kdScope`, as `KdSyncCeilUs(0)` reads it).
- On the worker inside a job: that job's scope (`uf`, `kdScope`).
- The route the scope belongs to must have this plan's follower (route `+0x40/+0x44` == the guid arguments). Otherwise the
  scope belongs to another plan and the router counts as unknown.

Household, selected and unknown scope never record and are never answered: `HhExempt`. `[Experimental] HouseholdToo=1` lifts
the exemption, because the answer is a cached real failure.

**Which plans:**
- Mode 1: only requests with water or boat planning. That is RouteOption hi `0x10000000` EnableWaterPlanning or
  `0x20000000` EnablePondPlanning, or RouteOption2 lo `0x2` EnablePlanningAsBoat or `0x8` BeginAsBoat (`opts+0x28..+0x34`,
  docs/11 Q2, docs/99 §11.1) [verified].
- Mode 2: every request of such a router. The houseboat gangway leg needs it: it has water and boat planning off (docs/642 §6,
  `HlmLegRoute`). The kind-2 legs measured in §1 are the gangway leg [inferred from s122/s123, docs/642 §6], so Mode 1 alone
  leaves them out.

**Key** (`NpmReqOf`, since the first in-game run, §6): routing world, follower ObjectGuid, agent class, all four RouteOption
words and the route's destination set. The route is `outList - 0xE0` (Route_PlanCommon's call `0x760DCA lea edi,[esi+0xE0]`
[verified disassembly]); its destination set at `+0x144` is hashed as ReplanDedupe does (list `+0x18`, first 0x40 bytes of each
destination, words `+0x20..+0x40`). That is the destination EA asked for, the same on every retry; the `goals` argument holds
the goal points Route_PlanCommon's candidate step generates for each try and is not used. The start is not in the key: the
entry keeps the recorded first start point and a request matches only when its start is within 1 m of it (`NPM_FAR`
otherwise: the plan runs and the entry stays). Only plans from Route_PlanCommon's call site are looked at (`notPlanCommon`
counts the rest), which also means nothing is recorded on a build whose PlanCommon addresses are unmapped (EA 1.69 table).

**Record** after the plan, when all of these hold:
- Route_PlanLocked returned 0 and the first path's status is 7/8 (FailedBecauseNoPathToDestination, docs/93 §2.1).
- No path in the out list has a Sim blocker. A blocker record (`RoutingPathImp +0xD0..+0xD4`, 40 bytes) has its kind at
  `+0x00` and the object id at `+0x08/+0x0C`. Kind `0x14` is RouteBlockerType Sim, and the follower's own id is skipped, as
  `0x752620` does through path slots `+0x160` / `+0x164` (`0x81FCE0` / `0x81D990`) [verified disassembly]. RouteBlockerType
  Vehicle is never produced natively (docs/93 §2.2).
- No exit cut the plan: no PlanDeadlineMs / UnseenFallback skip or cap, no SyncCapMs hit, no worker job deadline cut, no
  budget cap. A session that ran past its deadline with no exit taken is the game's own answer, which is the measured case,
  so it is recorded.
- The world was quiet before the plan (`RtQuietWorld`: no pending lot updates) and [RouteSnap]'s static version did not move
  during the plan.
- The plan took at least `NoPathMemoMinMs` (default 50). Cheaper failures are counted as `cheap`: answering them saves little,
  and in the first run they filled the table (217 evictions).

The entry stores the plan's sim tick, both world versions and the plan's ms.

**Answer:** the same key within `NoPathMemoMinutes` sim-minutes, measured on SimClock ticks, is answered when all of these
hold:
- The out list is empty.
- The world is quiet.
- The static version equals the recorded one: lot routing, quad tree and LotGraph builds, water grid builds and marks, lot
  updates, routing IC epochs (`PrVersions`).
- A request that reads dynamic footprints (BlockedByPeople `4` / BlockedByDynamicFootprints `0x1000`) also needs the
  footprint version unchanged (FailMemo's rule, replandedupe.cpp).

The answer is `return 0` before the lock, and the caller sees the result type the recorded plan had:
FailedBecauseNoPathToDestination (16), with no blocker. With no blocker, `GetRouteFailPath` plans nothing either.

How the 16 is produced [verified disassembly, Steam 1.67]:
- `Route_PlanCommon 0x7606E0` calls Route_PlanLocked at `0x760DE1` (returns to `0x760DE6`).
- Route_PlanLocked returns 0 and the out list is empty. `0x760F2D cmp [edi],edi; 0x760F2F je 0x76156D` takes the empty-list
  branch. That branch copies PlanCommon's local result `[esp+0x78]` (0x48 bytes, `rep movs` at `0x761581`) to the caller.
  The local result's default type is Failed (2).
- With a path in the list, PlanCommon instead calls `0x752620` on that same `[esp+0x78]`. For status 7/8 it writes type 16
  and the blocker fields.
- NoPathMemo retargets the rel32 of that one `je` to `NpmPcStub`. The stub runs `NpmPcFix([esp+0x78])`: when this thread's
  Route_PlanLocked was answered by the memo (`t_npmServed`, read once), it writes 16 into the result type. The stub then goes
  on to `0x76156D`.
- So the caller gets type 16 with zero blocker fields, the same as a vanilla no-path failure without a blocker.

Guards:
- `PlanLockedHook` stores its return address (`t_npmRet`) and clears `t_npmServed` on every call.
- A memo hit is answered only when the stub is in (`ready` bit 3) and the plan came from PlanCommon's call site. Those are the
  four route ICs: Plan, Replan, PlanFromPoint, ReplanFromPosition.
- Other callers of Route_PlanLocked (the road plans `0xC7C6E3` / `0xC7C9D5`) always search.
- If the site bytes differ, the memo only records and never answers (`SKIP route nopath memo: bytes at ...`).
- Selftest `TestNoPathMemoStub` runs the stub on a fake frame. It checks that type 16 comes only after a served answer and only
  once, and that the registers, esp and the jump target are kept.

Other rules:
- An answer never extends its entry.
- A plan of the same request that succeeds (or fails another way) drops the entry.
- Another routing world clears the table.
- A version change drops the entry on its next look-up (`invalidated`).
- A sim clock that went back (another save) expires it.

Sim clock: `MonoScriptHost_Simulate` stores SimClock.sSimTickCnt through `[this + 0xC24]` (`0xD81D43`). `this` is the script
host interface `[0x11D4BBC]` (host + 8, vtable `0x108B690`, slot 8 = Simulate `0xD81840`) [verified disassembly]. 37.5 ticks
per sim-minute (docs/04a).

**Memory and threads:**
- `nopathmemo-core.h`: 32 sets x 8 ways (256 entries, ~10 KB), LRU eviction inside a set, O(8) per call.
- One SRWLOCK in `nopathmemo.cpp`, taken exclusive for look-up, record and drop. It is never held across a plan.

## 3. What differs from vanilla

Only for an NPC router, the second and later identical plans inside the TTL return FailedBecauseNoPathToDestination (16) at
once, instead of after the full search. That keeps the stranding visible to `houseboatlegmemo` and `houseboatgangwayrescue`,
which act on a NoPath result. Three things differ from vanilla:
- There is no blocker. The recorded plan may have named an Object, Sea or Terrain blocker, but never a Sim one.
- The route holds no path, so `GetNumPaths` is 0 instead of 1 (docs/93 §3 P3) and PlanCommon's EditPath tail does not run on
  a failed path.
- The route-fail replan toward a blocker is skipped, which boattaximemo already accepts (docs/642 §3). If the world could have opened a path, the plan runs again: a static rebuild, a lot update, a water grid
change or the TTL.

Not covered: a plan whose start moves by more than 1 m each try, and a retry toward a rebuilt destination set (a new key).
The first failure is still paid.

## 4. Settings, counters, log

- `ts3native.ini [RouteAsync]`:
  - `NoPathMemo=0`: 0 off, 1 water / boat, 2 every request of an NPC router. The key also turns on the [RouteSnap] counters.
  - `NoPathMemoMinutes=120`: 1-1440.
  - `NoPathMemoMinMs=50`: 0-60000, the record threshold.
- Needs the Route_PlanLocked detour (the key installs it) and the routing lock learned by [RouteGate] (`g_rgLock`). Without
  them, plans are counted as `refused` and run as vanilla.
- `ts3native_nopathmemo` export:
  - `mode` at +0x00 is the runtime switch.
  - Export fields recorded, served, servedMs (ms the answers stand for), invalidated (docs/registry.md).
  - Then expired, evicted, dropped, exempt, not recorded by reason, refused, and from +0x40 minMs, cheap, misses, tooFar,
    notPlanCommon, exemptSlow (docs/registry.md).
- Log lines, 10 a minute each:
  - `route nopath memo: recorded route %08X kind K scope S, N ms`
  - `route nopath memo: served route %08X kind K scope S, stands for N ms`
  - `route nopath memo: miss route %08X kind K, N ms: nearest entry of this follower D m away, destination same|differs,
    agent same|differs, options xor A B C D, recorded M ms` (or `no entry of this follower`): a recordable failure that found
    no entry of its key or one too far. It names the key part that changed between EA's retries.
  - `route nopath memo: exempt route %08X kind K scope S, N ms no path`: an exempt plan >= `NoPathMemoMinMs` that failed with
    no path, to show which slow failures the scope rule keeps out.
  - `kind` is the job's IC kind on the worker, the [ProbeP10] IC on the sim thread, or -1 when not known.
  - Plus one counter line a minute: `route nopath memo: mode ... recorded ... served ...`.
- Unit test: `TestNoPathMemo` (`nopathmemo-test.h`, in selftest) covers key, start tolerance, TTL, invalidation, eviction, the nearest-entry lookup and clearing. It only
  needs the pure table, so it also builds as a plain C++ program.

## 5. Test

Isla Paradiso, the docs/642 §6 save, one session, `NoPathMemo` off then on (runtime switch `mode`), with Mode 2 for the
houseboat leg:
- Count the `route deadline: sync plan` lines with `failed` for the stranded NPCs.
- Count the `ufplansplit kind 2` lines >= 300 ms.
- Count the hitchwatch lines >= 300 ms.

Expected:
- On: one `recorded` line per (NPC, start, goal), then `served` lines at the EA repeat rate. Counter 4002 grows by ~300-650 ms
  per answer.
- No line has a household Sim's route. Check that `exempt` grows while the household plays.
- Clear regression rules: AGENTS.md.

## 6. First in-game run (2026-11-19, Isla Paradiso, Mode 2, ~20 min) [verified, log]

Counter line at the end: `recorded 514 served 0 (0 ms) invalidated 24 expired 17 evicted 217 dropped 0 live 256, exempt 1524,
not recorded: cut 14 sim blocker 57 world moved 34 other 3`. Why no repeat matched:
- Key drift [inferred from code; the `miss` lines of the next run will show it]: the old key (`RouteKey`) hashed the exact start
  points and the per-try goal points. The follower moves between EA's retries and the candidate step's goal points differ per
  try, so a retry was a new key. Fixed by the key above.
- The table was full of cheap failures: of the logged `recorded` lines (capped at 10 a minute) only 4 took >= 50 ms, most 0-7 ms; with 256 entries, 217 live
  entries were evicted. Fixed by `NoPathMemoMinMs`.
- Invalidations (24) and expiries (17) are too few to explain 0 answers.
- The slow repeats seen in `ufplansplit` (~300 ms sync plans 60 ticks apart, e.g. Cynthia Patel on Sparkling Sands) were
  not recorded. `route 00000000` in their `route deadline: sync plan` / `route phases` lines does not mean "no scope": those
  lines print `t_pd.handle`, which `PdPlanBegin` sets only for a seen-NPC ceiling session [verified code]. The same plan's
  `route candidate split` line says `scope unseen`, read by `PdScopeAt(0)` from `g_kdScopeH` at `PdPlanBegin` (not an
  UnseenFallback session: the entry is `Route_PlanLocked (no IC)`, so `t_ufSess` is off) [verified code]. So these plans had
  scope 2 at CallPlan; a refused START never reaches `KdScopeTake` (every refusal returns before it; only a failed
  `JobQueue` refuses after it) [verified code]. Why they were not recorded is not in the log: the counters only give totals
  (sim blocker 57, world moved 34). The capped `not recorded` line now names the reason per slow plan.
- One real gap fixed: `g_kdScopeH` is a single slot. A plan inside another plan's callback (EA's PostPlanRouteCallback ->
  CheckAndUpdateForHouseboats plans the houseboat legs on their own routes) overwrites it with its own op and clears it with its
  op 12, so a later Route_PlanLocked of the outer route found another route's scope or none (unknown, exempt). NpmScope now
  checks that the slot's route is this plan's route object (`outList - 0xE0`, with the follower), and otherwise asks a table of
  open plans by handle (`g_npmScopes`, fed by the same UserDiag ops 10 / 11 / 13 and cleared by op 12; 16 entries, 120 s age
  limit). No answer is still unknown = household. Selftest `TestNoPathMemoScope`.
- The kind-0 job plans of 240-530 ms seen were cut by KindDeadlines (`why 0 cut`) and stay unrecorded by design.

Proof on the next run: the counter line's `slow NPC plans N (by route handle M)` (plans of scope 2 / 3 that ran >=
`NoPathMemoMinMs`), `scope by route handle`, the ` (scope by route handle)` tag on `recorded` / `served` lines, and the capped
`route nopath memo: not recorded route %08X kind K scope S, N ms: <reason>` lines (cut by an exit, a Sim blocker, world not
quiet or version moved, path status N; -1 = empty out list).

## 7. Fuzzy match and household scope (`NoPathMemoFuzzy`, `NoPathMemoHousehold`; ts3fixes `npmfuzzy`, `npmhousehold`)

2026-11-19, built, default off, NOT RUN. Code: `nopathmemo-core.h` (fuzzy table), `nopathmemo.cpp`, `userdiag.cpp` (ops 38 /
39), ts3fixes `NoPathMemoSwitches.cs`.

### 7.1 Evidence [verified, log]

Appaloosa Plains run phase-20261119-2045 (`ts3native-full.log` lines 12985-13029):
- Two hitches of 1056 ms and 1014 ms (hitchwatch, ticks 461641 and 461901, 260 ticks and 1.9 s apart).
- Before the first: 10 `recorded` / `miss` pairs in one tick, all scope 3 (seen NPC), kind -1 (sync plan on the sim thread),
  95-101 ms each, every one `nearest entry of this follower 0.00 m away, destination differs, agent same, options xor 0 0 0 0`.
  Each names another route handle: a new Route object per try.
- The second burst is not in the log: the `miss` / `recorded` lines are capped at 10 a real minute and the 1.9 s gap is inside
  the same minute. Same pattern [inferred].
- Which follower: the memo lines did not print it (they do now, `fuzzy served ... follower`). The brief names
  `2B9900416E8D4F90`: Skedaddle Darnell, a townie horse (routecause `npc`, routefail `seen npc`), not a household pet
  [verified, log lines 2027-2044, 14332]. Its FollowerMeetUpAtPoint plans in the same log fail with
  FailedBecauseDestinationPortalLocked toward goals 0.6-0.9 m apart (`goal 758.5,52.2,637.5` / `759.2,52.2,636.9`).

### 7.2 What drives the retries [verified, decompiled Sims3GameplaySystems]

`FollowerMeetUpAtPoint` (a `GoHereWithHoldingInteraction`, made by `GoHereWithSituation.ReserveFollowers` with the leader's
priority + 2) runs the loop in `GoHereWithHoldingInteraction.Run`:
- Per meeting point, up to `GoHereWithSituation.kNumberOfTimesToRoute` (3) tries. Each try is `Actor.CreateRoute()`, one or
  two `RadialRangeDestination`s around the meeting point (`AddRouteDest`: radius `kMinFollowerRadius` 1 m to
  `kMaxFollowerRadius` 3.5 m, x1.5 with a boat; a horse or a large group gets the full 360° ring), `SetValidRooms(lot, [room])`
  from the meeting point, `DoRouteFail = false`, then `route.Plan()` (sync).
- After a failed try that ends more than 3.5 m from the point, the meeting point moves `1.5 x 0.3 m` (0.45 m) in a random
  direction and the next try starts.
- When all tries fail, `routeCompleteDelegate(false)` and `RouteFailed`; the situation hands out a new meeting point around
  the leader and the loop runs again.

So every try has new destination records (a new Route, a new centre) and the exact key of §2 can never repeat: `destination
differs` on every line. The exact key also hashes the destination set's valid-rooms vector pointers (`+0x20/+0x24`), which are
per Route object; that is fine for the same route's retries (§2), but means even an identical request on a new Route is a new
key.

### 7.3 Rule

Fuzzy entries are recorded next to every exact record (same gate: NPC scope or §7.4, no cut, no Sim blocker, quiet world, at
least `NoPathMemoMinMs`), in a separate 64-entry LRU table, when `fuzzy` is on and the plan had goal points. A later plan is
answered with EA's FailedBecauseNoPathToDestination (16, the §2 stub) when an entry matches all of:
- the same follower and the same request shape (`fkey`): routing world, follower, agent class, the four RouteOption words, the
  destination count and each destination's type (vtable), the destination set's flag `+0x30`, its valid rooms (the room ids,
  not the vector pointers) and their lot `+0x38/+0x3C` (docs/92 route fields [verified]). That is the same room and lot when
  the script set them;
- a start within 0.5 m (`NPM_FZ_START_M`);
- a goal centroid (mean of the `goals` records, the points Route_PlanCommon generated for this try) within R = 1.0 m in xz
  (`NPM_FZ_GOAL_M`) and 0.5 m in height (`NPM_FZ_DY_M`: same level), and a goal spread (largest xz distance of a goal point
  from the centroid) within 1.0 m of the recorded one (the same ring size);
- inside `NoPathMemoFuzzyMinutes` (default 10 = 375 sim ticks) of the recorded plan, the same static world version (and
  footprint version for a request that reads footprints), the world quiet, the out list empty, the stub in: the §2 rules.

Why these numbers:
- R = 1 m: one EA step moves the centre 0.45 m; two steps up to 0.9 m; the logged goals of consecutive tries are 0.6-0.9 m
  apart. 1 m is also the ring's own minimum radius, so the new ring overlaps the failed one almost entirely. Only real plans
  record: an answer never extends the reach, so a chain of answers cannot drift more than 1 m from a plan EA really ran.
- TTL 10 sim-minutes: covers the second burst (260 ticks after the first), short against the exact memo's 120.
- Start 0.5 m: the bursts started 0.00 m apart; a moved follower searches again.

Never after a success: any plan of that follower that does not fail with no path (success, another failure, a Sim blocker,
an empty out list), on any path through `CallPlan` (also exempt, refused, not-PlanCommon and non-water plans), drops all its
fuzzy entries (`NpmFzNote`). A cut plan is not a success and keeps them. World changes drop entries as for the exact memo: the
[RouteSnap] static version (lot routing, quad tree / LotGraph builds, water grid, lot updates, routing IC epochs: a build-mode edit
rebuilds lot routing; whether a door lock change moves it is not checked [open]), another routing world (clears the table), the sim clock going back. Switching `fuzzy` or the
memo off clears the table (successes would go unseen).

### 7.4 Household, selected and unknown scope

`household` (NoPathMemoHousehold / `npmhousehold`) lets scope 0 / 1 plans record and be answered, exact and fuzzy: the answer
is the same type EA gave for the same request a moment before (approved by the user 2026-11-19). Except player commands:
ts3fixes marks a route with UserDiag op 39 (right after the scope op, only with `npmhousehold` on) when its Sim is the
selected or a selectable Sim and its current interaction is UserDirected, not autonomous and not a `FollowerMeetUpAtPoint`
(a follower's situation interaction, which carries the leader's priority). NpmPlan checks the mark against this plan's route
object and follower (`NpmIsCmd`, 8 records, cleared by the route's op 12 or after 120 s); a marked plan always searches
(`cmdExempt`). `[Experimental] HouseholdToo=1` still lifts the old exemption as before, but never a marked command.

### 7.5 Wiring

- ts3native.ini `[RouteAsync]`: `NoPathMemoFuzzy=0`, `NoPathMemoHousehold=0` (start values of the runtime switches at
  `ts3native_nopathmemo +0x64 / +0x68`), `NoPathMemoFuzzyMinutes=10` (1-60). All need `NoPathMemo` 1 or 2.
- ts3fixes `npmfuzzy`, `npmhousehold` (`Instantiator.kNpmFuzzy` / `kNpmHousehold`, default False, tuning XML in
  build-fixes.ts, ts3agent `patches` names): UfPlanBody sends UserDiag op 38 (`lo` bit 0 fuzzy, bit 1 household) when the
  switch state changed since the last send; the answer (1 = memo installed) counts [4240] / [4241]. ts3fixes then owns the
  runtime switches (the ini values are only the start). Op 39 marks count [4242]; errors [4243]. Check:
  `reflect_call Ts3Fixes.Fixes.NpmCheck run` (flag encoding and the command rule with controls).
- Counters (registry.md): export `+0x64..+0x9B` (fuzzy, household, fzTtlMin, fzRecorded, fzServed, fzServedMs, fzFar,
  fzDropped, fzStale, fzEvicted, hhLifted, hhServed, cmdExempt, optMsgs).
- Log: `route nopath memo: fuzzy served route %08X kind K scope S follower G, goal D m and start D m from a recorded no-path
  plan, stands for N ms` (in the `served` cap), `recorded ... (+fuzzy)`, `exempt ... (player command)`, a second minute line
  `route nopath memo: fuzzy F household H: fuzzy recorded ...`, and `ts3fixes switches npmfuzzy F npmhousehold H` on a change.

### 7.6 What differs from vanilla

As §3, for the fuzzy answers also: a retry toward a goal within 1 m of a goal EA just failed to reach gets the failure without
its own search. If that nearby goal was in fact reachable (a wall or door between two rings 1 m apart, same room and lot), the
follower takes the failure path of `GoHereWithHoldingInteraction.Run` one try early: the meeting point moves on or the
situation gives a new one (EA's own path after a failed try). With `household` on this can happen to a household pet or Sim's
automatic route, never to a marked player command.

### 7.7 Build and test

- Built 2026-11-19 on Linux: clang-cl + xwin (`ts3native.asi`, Steam table; `/DTS3A_TABLE /EHsc /Zs` clean), ts3fixes and
  ts3agent packages with Roslyn. A clang-built asi must not run in the game (x86 SEH limit); CI's MSVC build is the game build.
- `TestNoPathMemoFuzzy` (`nopathmemo-test.h`): key without the destination records, goal radius (0.45 m and 0.85 m hit, 1.2 m
  far), level, spread, start (0.4 m hit, 0.6 m far), follower and shape, nearest recorded goal, refresh in place, TTL per
  record, static / footprint versions, clock back, follower drop, the 64-entry bound. `TestNoPathMemoScope` also checks the
  op 39 marks. Passed under Wine, as a plain program and in the selftest build run in isolation (the full clang-built selftest
  stops at an older test at a direct memory fault, the known SEH limit, before these).
- In game (to do), the same save, `NoPathMemo=2`, `npmfuzzy` off then on: count hitchwatch >= 500 ms, the `miss ... destination
  differs` lines and `fuzzy served` lines; `fzServed` should grow by ~9 per burst and `fzDropped` by one per follower success.
  `npmhousehold` as a second arm: `hhServed` grows, `cmdExempt` grows with Go Here clicks, no player command in `served` lines.

## 8. Stale answer after furniture moves? Checked, not a bug (2026-11-23) [verified, log + in-game]

Question: session ws-m1 (phase-20261123-0645, `NoPathMemo=1`, fuzzy, household, `HouseholdToo=1`) had a fridge set into a
bedroom doorway, moved away and sold; later Go Here orders from the back yard into the room failed with `blocker Wall`.
Was that a memo answer that outlived the fridge?

### 8.1 That session [verified, log]

- Every `route nopath memo:` minute line of the session reads `recorded 0 served 0` and `fuzzy recorded 0 served 0 ...
  household scope let in N served 0` (last pair: log lines 5996-5997). The memo held no entry and answered nothing.
- The failed orders are searches that ran: `gohere plan: cmd 22 attempt 1, Plan async FailedBecauseNoPathToDestination,
  failed blocker Wall ..., candidates 108/108/0, ... plan 6 host 18 other 2 ms, ... nodes 0 in 1 plans, door asks 4 live 2
  locked 0` (lines 3731-3733; cmd 26 at 4081-4084 the same). A memo answer has no blocker, no plan time and no door asks.
- They are player commands, which NpmPlan never answers (§7.4, `player commands searched`).
- Why those searches failed is not known from the log [open]; it is not this memo.

### 8.2 What a furniture change does to the versions [verified, in-game, paused game, npm-m1]

`PrVersions` deltas per action (leaf counters `snLeaf` at `ts3native_counters +0x1A30`, IC sum at `+0x1F08`), game paused,
3 s of pause alone = no change:

| action | leaves that moved |
| --- | --- |
| Live `SetPosition` of a fridge (move_object) | W2 1, W6 1, FPCORE 1, FPREMOVE 1, LOTREMOVEFP 18, GRIDREMOVE 18 |
| Live `SetPosition` next to a door (put_in_front) | W1 4, W2 1, W3 2, W6 17, FPCORE 2, FPREMOVE 1, LOTREMOVEFP 3, GRIDREMOVE 3 |
| Live sell (`SellBase` + `Destroy`) | W2 1, W6 19, FPREMOVE 1, LOTREMOVEFP 17, GRIDREMOVE 17 |
| create + place | W3 1, W6 5, FPCORE 1, LOTREMOVEFP 2, GRIDADD 1, GRIDREMOVE 2, HIGHCOST 1, IC 2 |
| enter Buy mode | IC 1 |
| sell in Buy mode | W2 1, W6 17, FPREMOVE 1, LOTREMOVEFP 2, GRIDREMOVE 2 |
| leave Buy mode | W4 1, APPLYLOT 1, IC 2 |

GRIDREMOVE (`0x62D9F0`), W3, W4, GRIDADD, APPLYLOT and the ICs are in the static sum (`PrIsFootprint` lists only W1, W2,
FPCORE, PRIVATE, FPREMOVE, LOTREMOVEFP). So every move, sale and placement, with or without Buy mode, moves `sver`, and an
entry of any follower is dropped at its next look-up (`NPM_INVALID`, fuzzy `stale`). The `dyn` rule (footprint version only
for BlockedByPeople / BlockedByDynamicFootprints requests) never decides this case.

Side finding: a walking Sim's footprint update takes the same path. 3 s of an unpaused Sunset Valley afternoon moved
FPCORE 241, LOTREMOVEFP 257, GRIDREMOVE 252, W3 19, IC 26. The static version therefore moves several times a second in a
live town, and entries rarely live long (npm-m1: fuzzy `recorded 45 ... stale 39 ... live 0`). That limits what the memo
can save; it cannot make an answer stale.

### 8.3 Forced check [verified, in-game, npm-m1 = phase-20261123-0827]

Sunset Valley new game, one room with one door (build_layout), Bea inside, Bo at the mailbox in the back yard, a fridge
0.5 m inside the door. Ini `NoPathMemoMinMs=0` (else the 6-30 ms lot plans count as `cheap`), npmfuzzy + npmhousehold on.

- A blocked doorway does not reach the memo: `sim_route_to` answers FailedBecauseDestinationPortalLocked (18) with no
  Route_PlanLocked call (no memo counter moves); status 18 is never recorded anyway (§2).
- Mode 1 does not look at a household Sim's land plan at all (no water / boat option): no counter moved.
- Memo off, mode 1, mode 2: blocked = fails; fridge moved away in Live mode = Succeeded at once, from the yard and from
  inside; fridge sold in Buy mode = Succeeded at once. Go Here orders after the sale: `gohere end: cmd 11 route ok`
  (Bo, yard into the room) and `cmd 10 route ok` (Bea, room to the door side). `served 0`, `fuzzy served 0` at the end.
- A sealed room (door sold) gave real no-path records for Bo (`recorded route ... kind 0 scope 1, 7 ms (+fuzzy)`), but no
  repeat was answered: each `sim_route_to` is a new Route (`destination differs`, §7.2), and the fuzzy entries of a
  follower go when EA's find-the-obstacle replan succeeds (`dropped`).

Repro entry: `nopathmemo-stale-force` (repros.json; the by-hand sequence, not yet run as an entry).
