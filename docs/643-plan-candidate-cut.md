# 643: planner candidate phase: split timing, deadline cut, water goal check

2026-11-19, static (disassembly of the Steam 1.67 exe with capstone, Ghidra decompiles of `0x65E0D0`, `0x65BB90`, `0x6595B0`,
`0x65BC20`, `0x654CC0`, `0x65DF90`, `0x650D80`, `0x6443A0`, `0x671930`, `0x620560`; none committed), built, **NOT RUN**.
Code: `native/ts3native/plancand.cpp` (+ `plancand-rule.h`, `plancand-test.h`), included after `plandeadline.cpp`.
Switches (ts3native.ini, all default 0): `[RouteAsync] CandidateCut`, `[RouteAsync] WaterGoalCheck`,
`[RouteJobPhases] CandidateSplit` (+ `CandidateSplitLogMs`, default 100). Export `ts3native_plancand`, slots 4050-4059
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
answer (0), are never cut: counted in slot 4054. `HhExempt` is deliberately not used: `[Experimental] HouseholdToo=1` lifts
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
`=2` shadow: the search runs; a would-reject that finds a path counts in slot 4057 and logs `WATER GOAL CHECK MISMATCH` (must
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
