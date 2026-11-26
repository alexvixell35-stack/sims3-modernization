# 644: planner candidate phase: split timing, deadline cut, water goal check

2026-11-19, static (disassembly of the Steam 1.67 exe with capstone, Ghidra decompiles of `0x65E0D0`, `0x65BB90`, `0x6595B0`,
`0x65BC20`, `0x654CC0`, `0x65DF90`, `0x650D80`, `0x6443A0`, `0x671930`, `0x620560`; none committed), built, **NOT RUN**.
Code: `native/ts3native/plancand.cpp` (+ `plancand-rule.h`, `plancand-test.h`), included after `plandeadline.cpp`.
Switches (ts3native.ini, all default 0): `[RouteAsync] CandidateCut`, `[RouteAsync] WaterGoalCheck`,
`[RouteJobPhases] CandidateSplit` (+ `CandidateSplitLogMs`, default 100). Export `ts3native_plancand` (offsets +0x00-+0x24, docs/registry.md)
(docs/registry.md). Build check: clang-cl `/Zs` of `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp` only; the
pure rules ran as a host test. Not game-safe until CI's MSVC build.

## 1. Problem

[measured, earlier logs] An NPC water/boat plan that ends `FailedBecauseNoPathToDestination` spends ~300-650 ms inside
`PathPlanner_PlanHierarchical 0x65E0D0` before any world A\*; the plan deadline (docs/164 §7) has only three cut points (cell
A\* entry, WaterPath_Plan entry, world A\* cost calls), so the session passes its deadline with nothing to fire on ("world A\*
capped 0, cell skipped 0, water skipped 0"). A rider job log shows `candidate 327` ms.

## 2. Where the candidate time can go (what CandidateSplit measures)

Call graph of the candidate phase [verified, E8 scan of `.text`]:

| Helper | Address | Called from | Notes |
|---|---|---|---|
| closest waypoints `closestWps` | `0x65BB90` (thiscall, 11 args, `ret 0x2C`) | `0x65E0D0`: `0x65F731`, `0x65FA0E`, `0x65FB55`, `0x65FC97`; inside `0x65BC20`: `0x65C401`, `0x65D4CC`; inside `0x65DF90`: `0x65DFE5`, `0x65E045` | loops `0x6595B0` over three tiers (`0x65BBB0..0x65BBFD`) until one finds a waypoint |
| same land component `sameLand` | `0x65DF90` (4 args, `ret 0x10`) | `0x65F3DA` (goal-lot loop, land plans); scripts `0x787282` | two closest-waypoint searches per call |
| same water body `sameWater` | `0x650D80` (4 args, `ret 0x10`) | `0x65F35D` (same loop, water flag `param_10` bit 28); `0x7872D2` | two grid lookups: cheap [inferred from code size] |
| LotEdgePortals_Choose `choose` | `0x65BC20` (8 args, `ret 0x20`) | `0x65EF72` (start lot), `0x65F41B` (goal-lot loop, every candidate) | calls closestWps twice |
| LotEdgePortals_Gather `gather` | `0x654CC0` (8 args, `ret 0x20`) | `0x65EC43`, `0x65EED9` | builds the candidate list `this+0x160`; physics range queries `0x83B3A0` |
| shore helper `shore` | `0x6443A0` (8 args, `ret 0x20`, a forwarder to `0x644080`) | `0x65E4A1` | direct start-goal test |
| `LotSearch_Entry` | `0x640E00` (6 args, `ret 0x18`) | `0x65EB8E`, `0x65ECDE`, `0x65EDE5`; `0xC7630A` | already a `[RouteJobPhases]` "candidate" site |

Which of these dominates is **not measured** [inferred candidates: closestWps (it runs inside choose and sameLand too, three
tiers each), gather]. `CandidateSplit=1` gives one line per `0x65E0D0` call >= `CandidateSplitLogMs`:

`route candidate split: PlanHierarchical N ms code C, sync|worker job, route H, scope S: closestWps ms/calls, sameLand ..., sameWater ..., choose ..., gather ..., shore ..., lotSearch ... (self ms/calls); rest R ms (own code + world A*, cell A*, water, lot legs); cut K`

Self times sum to N (each helper's nested helpers are subtracted); "rest" is everything not hooked here, so run it with
`[RouteJobPhases] SyncPlans=1` (world / cell / water / lotleg per plan) in the same job. Totals every 60 s: `route candidate
split totals: ...`. Code C = the planner's return value (0 found; 5 is the failure every path below ends in [verified]).

## 3. CandidateCut: the deadline exit in the candidate phase

Past the plan's deadline, a helper called from a listed planner site answers "not found" at once. The deadline is the open
`PdPlanBegin` / IC session's (`t_pd.dl`, sim thread, `g_pdTid`) or a worker job's `t_fbDeadline` on the `g_pdWkTid` slot (same
mode gate as `PdWorkerSkip`), with `KdCamHold` as at the other exits.

**Household rule** [code]: cut only when the router's scope is 2 (unseen) or 3 (seen NPC). The scope is read once at the
session's open (`PdSess.scope`, `PdScopeAt` in plandeadline.cpp: `t_ufSess` = 2, else `g_kdScope` for that route as
`KdSyncCeilUs` reads it) or is the worker job's `t_hcScope` (`j->uf ? 2 : j->kdScope`). Household or seen (1), and unknown or no
answer (0), are never cut: counted at export +0x10 (cutExempt). `HhExempt` is deliberately not used: `[Experimental] HouseholdToo=1` lifts
realistic-only exemptions, never cuts (AGENTS.md).

Cut sites (return addresses, so the helpers' other callers are untouched), each not-found path [verified, disassembly]:

| Helper | Call sites (return) | Cut answer | What the planner does then |
|---|---|---|---|
| `0x65BC20` | `0x65EF72` (`0x65EF77`) | AL 0, `ret 0x20`, nothing written: the function's own early exit `0x65CAC0` (taken at `0x65BC3D` etc.) writes nothing either | `je 0x65F3C2`: releases its ref, `0x65EB97`: frees, returns 5 |
| `0x65BC20` | `0x65F41B` (`0x65F420`) | same | `je 0x65F697`: closest-waypoint fallback `0x65F731` (cut too) -> `0x65F776` / `0x65F781` -> `0x65F910`: releases, frees, `0x65EBD6`: returns 5, as when every candidate failed |
| `0x65DF90` | `0x65F3DA` (`0x65F3DF`) | AL 0, `ret 0x10`, nothing written (as its own early exit `0x65DF9C` -> `0x65DFFF`) | flag `0x200` stays clear, then `0x65BC20` at `0x65F41B` (cut) |
| `0x65BB90` | `0x65F731` (`0x65F736`), `0x65FA0E` (`0x65FA13`), `0x65FB55` (`0x65FB5A`), `0x65FC97` (`0x65FC9C`) | AL 0 and the out vector (arg 8) end = begin, `ret 0x2C`, as `0x6595B0`'s own first write (`param_10[1] = *param_10`) leaves it | each site zeroes the vector before the call and frees it after; `0x65F736` also tests end != begin (empty -> not found); `0x65FA13` -> free -> returns 5; `0x65FB5A` / `0x65FC9C` skip the world A\* for that goal |

No partially built state: every cut answer is the function's own "nothing found" exit, taken before it allocates or writes;
the vectors are the callers' own (empty, freed by them). Retries: `PathPlan_WithRetries 0x661B50` calls `0x65E0D0` again on
some codes; past the deadline each retry is cut at its first site again [inferred]. The final script result
`FailedBecauseNoPathToDestination` for code 5 is [inferred] (same code as the measured no-path plans; mapping not traced).

A cut marks the plan as cut where the other exits' cuts are read: the session's `capped` (the "route deadline:" line counts it
as "world A\* capped"), a worker job's `t_fbCapHit` (so KindDeadlines' backoff, routefail and UnseenFallback recovery see a
cut job). Log, first cut per plan, 20 a minute: `route candidate cut: <helper> after N ms, scope S; sync plan|worker job,
route H, call 0x...`. Needs a deadline feature on (PlanDeadlineMs, KindDeadlines, UnseenFallback, FbJobDeadline).

## 4. WaterGoalCheck

`WaterPath_Plan 0x671930` [verified]: out vector end = begin; start and goal cell <= `[this+0xC]` and in the same water body
(`[this+0x1C]` 2 bytes per cell) else exit `0x671B98`; boat tier from `**(route+0x8C) & 0xCF00` (`0x624150` / `0x55DA60` are pure
compares: `0x4100/4200/4300` -> 2/3/4, `0xC00-0xF00, 0x4500, 0x4600` -> 1, else 0); tier walked down while the start cell is
land in that tier's grid (`0x671A3E..0x671A6D`, tier 0 not tested); `0x66DBE0` only builds the cost object (copies, no
snapping); then the JPS `0x620560` on grid `[this+0x7B0] + tier*16`, width `[this+0x7A0]`, height `[this+0x7A4]`. It never
tests the goal cell.

`0x620560` [verified decompile; jump semantics from our `waterjps.cpp`, which `waterjps-test` checks against the game's
jump]: start == goal -> found at once; flag `local_94` = start cell is land, passed to every jump; with the flag clear a jump
returns only cells it stepped onto as water (`WjStep` fails on land), and only popped jump points are compared with the goal.
So with the start cell water and the goal cell land the search cannot succeed: it floods the reachable water and returns 0.
With the start cell land (flag set) jumps take one-step moves over land, so a land goal can be reached: no reject there.
`WaterGoalCheck=1` answers that one case at once (out vector end = begin, AL 0: the same result). Exact, so every router.
`=2` shadow: the search runs; a would-reject that finds a path counts at export +0x1C (wgcMismatch) and logs `WATER GOAL CHECK MISMATCH` (must
stay 0 before mode 1 is trusted). Hook: own detour at `0x671930` when its prologue is the game's, else after the plan deadline
stub (`g_pdOrig[1]`, so its skip exit runs first); `[RouteJobPhases]` chains after either (`RjpOwner`).

Not fixed: the search's decrease-key path (`0x620560`, the `local_6c` branch) takes a copy of the open list (`0x90B690`, read as a vector copy [inferred]) and scans it
linearly for the node, per improved neighbour: O(open) each, O(n^2) over a flood [verified decompile]. With the goal check the
largest floods (unreachable goals) are skipped; a heap index would be its own change.

## 5. Hooks and prologues

Detours (5-byte jmp, whole-instruction prologues, Steam 1.67): `0x65E0D0` `55 8B EC 83 E4 F0`, `0x65BB90` `51 53 8B 5C 24 2C`,
`0x65DF90` `83 EC 10 53 56`, `0x650D80` `56 8B F1 8B 4E 14`, `0x65BC20` `55 8B EC 83 E4 F0`, `0x654CC0` `55 8B EC 83 E4 F0`,
`0x6443A0` `8B 44 24 18 D9 44 24 14`, `0x640E00` `55 8B EC 83 E4 F8`. Branch-into scan of the stolen bytes (rel8 / rel32 jmp,
jcc, call over `.text`): one byte-pattern hit (`0x654C9B`), mid-instruction, not a branch [verified]. Hooks are C `__fastcall`
functions (edx unused: same stack and callee cleanup as thiscall), EAX passed back whole. `[RouteJobPhases]` takes the place
after ours at `0x65E0D0` / `0x640E00` (`RjpOwner` -> `PccOwnerAt`). CandidateCut alone installs only the three cut helpers.
EA 1.69: the new addresses are `0` rows in `ts3addr-ea169.h`; the section logs SKIP there (`ts3a_noaddr`).

## 6. Proof lines in game

- install: `OK   route candidate split + cut: 8 of 8 helper sites hooked; CandidateCut=1 ...`, `OK   water goal check: ...`
- per plan: `route candidate split: PlanHierarchical ...` (which helper), `route candidate cut: choose after 412 ms, scope unseen; ...`
- then the session's `route deadline: sync plan ... failed ... world A* capped 1` (or the job's capped / long-job line) with
  the plan's total close to the deadline instead of +300-650 ms;
- every 60 s: `route candidate: cut mode 1 plans P calls C exempt E; water goal check mode M rejects R mismatch 0; ...`.
- household check: exempt > 0 with no cut line naming scope `household-or-seen` or `unknown`.

## 7. Risks

- The cut changes a plan's result (failure instead of a late path, as every deadline exit); unseen / seen-NPC routers only.
- Unmeasured split: if `gather` or `lotSearch` (before the loop) dominate, the cut does not shorten that part.
- Return-address gating relies on the Steam 1.67 call sites; another hook that calls these helpers from C would not be cut
  (only timed).
- Scope for a non-IC session (handle 0) is the pending `g_kdScope` answer, as `KdSyncCeilUs` uses it.

## 8. Run 1 and the cell A* loop cut

2026-11-19, IP, ~20 min, CandidateCut=1, CandidateSplit=1, SyncPlans=1, NoPathMemo=2 [measured]:

- 3 cuts (`route candidate: cut mode 1 plans 3 calls 3 exempt 0`), WaterGoalCheck=2: 1 reject, mismatch 0.
- Split totals: closestWps 258 ms / 4558, choose 83, gather 476, shore 1604 / 159892, lotSearch 10801 / 4534; "rest" 147,049
  ms over 13,589 calls. 278 lines >= 100 ms: 211 unseen worker jobs with code 0 (100-355 ms, every helper ~0), 13 code 5.
- The ten SyncPlans lines of the same run: 270-306 ms each, `cell=253..288`, `cand=16..17`, world / lotleg / water 0. The
  phases there are exclusive, so the sync plans' time is cell A* `0x61E500`.
- UnseenFallback: overrun past the cut p99 80 ms sync / 114 ms job, all cell A* (`cell 80/114`, world and water -1).

Where cell A* sits [verified, E8 call graph of `.text`]: it is not called from `0x65E0D0` directly. LotPlan `0x6410B0` (from
`0x65E3C2`, `0x65F11C`, `0x65F582`, `0x660794`, `0x660ABE`, `0x660D83`, `0x661A9B`, and through `0x656A70` from `0x6611A1` /
`0x661308`) -> `0x63F6E0` -> `0x632630` -> `0x61E500` (call `0x632C1A`); LotSearch `0x640E00` (from `0x65EB8E`, `0x65ECDE`,
`0x65EDE5`) -> `0x633FA0` -> `0x632E40` -> `0x61E500` (call `0x633594`). So "rest" was cell A* under LotPlan plus world A* and
own code, and lotSearch's self time had its cell A* inside. [inferred: the worker jobs' rest is the same cell A* as the sync
plans'; the new split proves it.] The planner's own loops (`0x65F2E0`..`0x65F84B` candidate loop, the goal loops) call
helpers that each have a deadline exit already (choose / sameLand / closestWps here, world A* cost stubs, cell A* and water
entry stubs), so a back-edge exit there would save only the loop's own bookkeeping; what runs on past the deadline is one cell
A* search already started (bounded only by its node cap 0xFA00, or 0x40002 with route flag 0x100000).

### 8.1 Split, part 2 (CandidateSplit=1)

- Two more timed sites: `cell` (cell A* `0x61E500`: after the plan deadline stub through `g_pdOrig[0]` when it is on, else own
  detour) and `lotPlan` (LotPlan `0x6410B0`, own detour, 8 bytes). Self times as before, so lotSearch and lotPlan no longer
  include the cell A* under them; "rest" is now own code + world A* + water + allocator.
- Lot legs by call site: LotPlan's inclusive time (cell A* under it included) per return address in `0x65E0D0` (the seven
  sites above, named by call address; `other` = through `0x656A70` or outside the planner).
- Per line: `route candidate split: PlanHierarchical N ms code C, ...: closestWps a/b, ..., cell a/b, lotPlan a/b (self
  ms/calls); lot legs by call 65F11C a/b, ... (incl. ms/calls); rest R ms ...`.
- Every 60 s, after the totals line, the 8 slowest calls (>= CandidateSplitLogMs) since the last one, slowest first, not
  rate-limited: `route candidate top i/n: PlanHierarchical ...` (same fields).

### 8.2 Cut, part 2: the cell A* loop (CandidateCut=1)

Loop of `0x61E500` [verified disassembly, Steam 1.67]: head `0x61E640`, pop the best open node into esi (ref counted), the
goal test, best-so-far ebx update, then the node-cap test `0x61E76E: mov ecx,[esp+18h]; cmp [edi+24h],ecx; jae 0x61E7D9`,
expansion (vtable `+0x1C`, `+0x10`, `0x72C920`, `0x66BFD0`, `+0x2C`), back-edge `0x61E7D3 jge 0x61E640` while the open count
`[[edi+18h]+18h] >= 0`. Exits: `0x61E7D9` (ebx = 0 -> `0x61E59C`, else the path to ebx, AL 1: the goal and node-cap result) and
`0x61E59C`: `[edi+1Ch] = -1`, frees the two local vectors (`0x61BAB0` on `[esp+30h]` + free `[esp+40h]`, `0x61BCE0` on
`[esp+20h]` + free), releases ebx and esi (each null tested), AL 0, `ret 0x18`. The same exit the loop takes when it ends with
no best node.

The patch: the 9 bytes at `0x61E76E` become `jmp stub; nop x4` (no branch targets `0x61E76F`-`0x61E776` [verified scan]). The
stub runs the replaced test unchanged (cap -> `0x61E7D9`), then calls `PccCellLoopCut`: every 32nd expansion on that thread the
same rule as the other cut sites (`PccCutNow`: the session's or worker job's deadline, `KdCamHold`, scope 2 / 3 only, household,
selected and unknown never); a cut jumps to `0x61E59C`, else on to `0x61E777`. eax / ecx / edx and the flags are written before
any read at all three targets, so the C call may clobber them; esp is untouched (jumped, not called).

What the plan sees: cell A* AL 0 with `[this+0x24]` = nodes expanded so far and `[this+0x1C]` = -1, the same as the entry
stub's skip exit (AL 0, count 0) that the deadline already uses on every later search (docs/164 §7 D3): LotPlan_Inner takes its
code-5 path, LotSearch its not-found, PlanHierarchical ends 5 [verified for the entry exit; the count differs only, and no
caller of `0x61E500` was seen reading `[this+0x24]` after AL 0 (inferred)]. The open list is left as the goal exit leaves it;
the next search starts with `0x61E360` (read as its reset [inferred]). The plan is marked cut as at the other sites (worker `t_fbCapHit` / `t_jobCapAt`,
sync `t_pd.capped`), first cut per plan logged `route candidate cut: cell after N ms ... call 0x0061E76E`.

Cost with CandidateCut=1: one call and one TLS increment per expansion, a clock read every 32nd (a plan with no open session
returns after the thread compare). The 60 s line adds `cell A* loop cut on|off`.

Not done, and why: a deadline exit at a back-edge of `0x65E0D0` itself. Its loops' calls are cut already, and run 1 shows no
time in its own code that the split could see; if the new top lines show a large `rest` with no lot leg or cell time, that is
the next place (the candidate loop back-edge `0x65F84B`, exit through `0x65FE58`).

EA 1.69: no new table rows; the new addresses are offsets inside `0x61E500` / `0x65E0D0` (`PCC_IN`), and the EA port skips
the whole file (`ts3a_noaddr`).
