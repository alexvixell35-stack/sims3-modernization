# 643. Native no-path memo for NPC water and boat plans (`[RouteAsync] NoPathMemo`)

2026-11-19. Source: Isla Paradiso logs (docs/642 §6), ts3native code, Steam 1.67 disassembly. Built (`nopathmemo.cpp`), default
off, NOT RUN. Counters: `ts3native_nopathmemo` export, slots 4000-4003 (docs/registry.md).

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
  - Slots 4000 recorded, 4001 served, 4002 ms the answers stand for, 4003 invalidated.
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
