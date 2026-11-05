# 195. Gate-12 "F-F" wait, key `0x62FB74`: what it is and what would remove it

Static analysis (capstone on the Steam 1.67 copy, raw `E8` caller scan) and existing run data only; no build, no game run. 2026-10-25. Ledger §352.

**Short answer.** `0x62FB74` is not a `+0x918` acquire. It is the return address of `LotTable_GetIfPresent 0x62FB60`'s call to `LotTable_Contains 0x628A90`, whose entry is route gate site 12 (`routegate.cpp` `kRgSites`): the sim thread waits there until the running async job leaves `A_RUNNING` (P6W class 1, `RgEnter` / `RtWaitJob`). "F-F" is the label `wait-ab.ts` gives the key; the docs/84 F-F family is wider (`SurfaceAtPoint`, PlaceGroupSocial), but almost all of the key is one caller: the script idle check `IC_RouteManager_IsOkToIdleAtCurrentLocation 0x7556B0`. The wait is gate policy, not a data dependency (docs/178 §8, I-178-8-3). `[IdleCheckRO]` (0.36.48, off) already answers the lookup exactly, but the same IC then runs its own obstacle query `0x82EDB0` (key `0x755990`), which takes `+0x918` and waits for the same job: in `night-1024-gate12` all 118 skips moved there (net <= 59 ms). Removing the wait needs both halves: `[IdleCheckRO]` plus a read-only answer at the IC's obstacle query (site C below). Expect it to move again (destroy, gate 0); in IP natural play this key leads only 2 of ~39 iterations >= 300 ms, so it is a stress-loop and absorber problem more than a natural-freeze cause.

## 1. The function and its callers

**`LotTable_GetIfPresent 0x62FB60`** (thiscall, lot planner P, args = lot key lo / hi, `ret 8`) [verified disassembly]: calls `LotTable_Contains 0x628A90` at `0x62FB6F` (return `0x62FB74` = the key); a hit then calls `LotTable_GetOrInsert 0x62EAB0` on `P+0x48` at `0x62FB86` (gate site 0, return `0x62FB8B`). Reads only; the gate exists because `GetOrInsert` writes the table's window slot and inserts on a miss, and a job may rehash the table (W5) or build a lot (W4) (docs/80, docs/84 §4.3). Under QL shared the `Contains` + hit read is safe (docs/84 §4.3; S2's `SrEntry(contains)`).

**Direct callers of `0x62FB60`** (raw `E8` scan, 30 sites) [verified static]. Only the ones that can run on the sim thread while a job runs matter (the gate passes the job's own calls):

| caller (return = P1b second frame) | path up | thread | share of the key |
|---|---|---|---|
| `IC_RouteManager_IsOkToIdleAtCurrentLocation 0x7556B0` (`0x7558DC`) | script `Sim.RunIdleLogic` -> IC | sim | 781 of 801 waits, 6.98 of 7.17 s in the four P1b runs of 2026-10-18 [measured]; `night-1024-gate12`: 15 of 152 waits came while `[IdleCheckRO]` was on, 137 while off [measured, `ik` column; consistent with this caller leading, arm lengths not compared] |
| `RoutingWorld_SurfaceAtPoint 0x834B80` directly (`0x834CF6`) and via `RoutingWorld_EnsureTerrainFootprints 0x8341D0` (`0x834201`) | `World_SurfaceTypeAtPoint 0xAB9580` <- `FootstepAudio_Surface 0x8693E0`, `IC_World_GetInformationAboutRoomImpl 0x7933A0` (via `0xB01B50`), placement `0xB463F0` / `0xB470C0`; also `UpdateTerrainFootprints` | sim | 3 waits in P1b; writes lazy terrain footprints (W1) |
| `0x823800` (`0x8238C5`) | `0x8273B0` / `0x827430` <- `IC_RouteManager_PlaceGroupSocialImpl 0x764910` (docs/84) | sim | 0 seen; writers unknown (docs/189 §8.2) |
| `IC_RouteManager_SetPortalLockStatus 0x74B490` (`0x74B4B5`) | script door lock | sim | 0 seen; a writer (F-D) |
| footprint level rebuild `0x806770`, `0x80B060`, `0x80C200`, `DoorRouting_AddDoorPortals 0x80CB40` (`0x806B1C`, `0x80B163`, `0x80C29A`, `0x80D798`) | F-A rebuild (`0x80E5B0`) | sim | 0 seen in P1b (docs/178 §8) |
| `ClosestWaypoints_GatherChunk 0x654790` (`0x654BE5`), `PathPlanner_PlanHierarchical`, `LotEdgePortals_*`, `Portal_LockState 0x64F2C0`, `LotQuadTree_*`, `RoutingWorld_ApplyLotUpdate`, `RoutingSystem_OnExitBuildBuy`, `RoutingDebug_*` | the planner (job), sim sync plans, lot rebuilds, build/buy exit | job or sync plan | 17 waits / 0.19 s (`0x654BE5`, a sim sync plan) |

**What the idle-check IC does after the lookup** [verified disassembly, `0x7558C0`-`0x755A60`]: a hit runs the leaf `0x625140` (no calls; reads the lot entry's rooms and portals; 1 = "OK to idle", IC returns `0x40`). A miss or a 0 goes to `0x75592B`: `0x6508F0` (builds a query shape on the stack), then `RoutingWorld_ObstacleQuery 0x82EDB0` at `0x75598B` (mode 0, the IC's option word, a fresh empty footprint vector with tag `0x1004514`), which takes `+0x918` (key `0x755990`). An empty answer goes straight to the release of the empty vector and the IC's "nothing here" return; a non-empty one enters a hit loop (`0x7559A0`-`0x755A55`): per footprint virtual reads (type, owner id, a flag call, a class call), `0x788EA0`, `FindById 0x939560` and an object flag test against `[0x11E90FC]`; return codes 4 and others, vector released by `0x652470`. No routing write in the IC outside the two lock-taking calls [verified for the callees to 4 hops, docs/178 §9.2].

**`[IdleCheckRO]` live result** (`night-1024-gate12` `ip-idlecheckro-shadow`, 0.36.49) [measured]: 3,961 shadow compares, 0 mismatch; 118 taken, **pass 1, onward 117**: the leaf almost never says "OK to idle", so nearly every idle check goes on to `0x82EDB0`. That is why every skip moved to `0x755990`.

## 2. How much there is

| run | 0x62FB74 ms/s | >= 300 ms iterations led by it | max |
|---|---|---|---|
| IP natural-30, 2026-10-22 (v0.1.4-era defaults) | not keyed in that build | 0 of 47 (75 iterations have it as the longest wait) | wait 180 ms, iteration 298 ms |
| IP v0.1.5 bundle A/B, `overnight-1023` offfirst / onfirst (natural, in-load ABBA) | **bundle on 21.3 / 22.0**, bundle off 3.2 / 4.5 | 2 of 27 / 0 of 12 (whole run) | wait 260 ms, iteration 362 ms |
| IP locktaker verify `rerun-0333` (stress) | - | 3 of 21 | wait 338 ms, iteration 957 ms |
| IP P6 ladder 2026-10-25 (stress loop, `wait-ab.ts` on / off) | 19.5-30.8 on all rungs; kinds rung on 55.8 | not counted here | rung 1: 1,122 waits, 27.3 s, avg 24 ms, max 338 ms; kinds: 1,323 waits, 56.1 s, avg 42 ms, **max 2.93 s** |

[measured: `wait-ab.ts`, `*-hitches.jsonl` `rtMaxKey`, `ntN/ntUs/ntMaxUs_3` of the ladder's `-ab-native.jsonl`.] The ladder ran with `[IdleCheckRO]` and P1b off, so the caller split there is inferred from the earlier runs.

Two readings [inferred]:
- **It is an absorber.** With the v0.1.5 bundle off the key is 3-4 ms/s; with it on (SteerRO, AvoidRO, SlotEnabledRO, Gate0RO, RouteToObjRO, PlacementRO, DeferFootprint, ...) it is 21-22 ms/s, while steering `0x85FE10` drops 24-27 -> 0-2. The waits the bundle removes land on the idle check later in the same tick: docs/152 §1 conservation (I-152-0).
- **Its size follows the job span.** A gate wait lasts until the job leaves `A_RUNNING`, so each one costs the rest of the job: ~24 ms average in the loop, ~42 ms on the kinds rung where refused foreign jobs run clone + game plan (docs/189 §17.4). That is why it doubled there, and why its max reached 2.9 s.

## 3. Coverage today

| switch | covers `0x62FB74`? |
|---|---|
| `[IdleCheckRO]` (0.36.48, docs/178 §8) | the lookup + leaf at `0x7558D7` / `0x755916`, exactly; **off in release defaults** (`Enabled=0`); alone it only moves the wait to `0x755990` |
| SlotEnabledRO (SH), PlacementRO, RouteToObjRO, AvoidRO, Gate0RO, SteerRO (S2), LotFrontDoorRO, ObstructedRO, GoalValidRO (T6), DeferFootprint / 2 | no: different call sites; they feed it (absorber) |
| docs/152 bundle B0 | no (lists `0x62FB74` as residual writers, which docs/178 §8 refuted) |
| P5b / P6 HoldKeyed | no: `0x62FB60` is "kept out" (docs/155, docs/189 §8.2: F-D / F-F share the key and are not told apart) |

## 4. Design: finish the idle check (`[IdleCheckRO]` site C)

**Cheapest correct design:** keep A and B as built and add one call-site retarget **C at `0x75598B`** (the `E8` to `0x82EDB0`), run only right after A/B on the same IC call (`g_ikA` style state, owner thread, job running, `AvMayRun`):
1. **C1, empty answers (T6 pattern):** reuse `gvro.cpp`'s `GvRun` (S2's `SrQuery`, mode 0, visitor `0x662DB0`, record option word = the IC's option) under QL shared with S2's protocol. The call site matches T6's shape [verified]: mode 0 pushed, fresh empty vector, answer in `al`. Lot found + no footprints = return `al` with the vector untouched; the IC then takes its own empty path, exact as for T6.
2. **C2, non-empty answers (ObstructedRO pattern, optional second step):** clone the hit loop's reads under QL shared on the clone's footprint list without the refcount +1/-1 (net 0), using the game's own virtual calls, `0x788EA0` and `0x939560`, and return the IC's code from the hook. Do C2 only if the C1 run shows non-empty answers dominate. In a crowd, other Sims' footprints near an idling Sim are likely, so C1 alone may cover little [inferred].
3. Any doubt (QL busy, tripwire, sequence or generation moved, job end, lot not in table, deferred flush, buffer limit, mode != 0, non-empty vector without C2) calls the game's `0x82EDB0`: today's wait.
Shadow: with no job, the game's call, then the clone, compared (`al`, footprint count, and with C2 the IC's return code).

**Expected effect [inferred].** In the IC: A+B+C1 leave it lock-free for empty answers; A+B+C1+C2 for all non-fallback answers. Gate wait `0x62FB74` + `0x755990` -> ~0 for covered calls. **Moved-waits caveat (docs/152 §1, I-152-0, docs/189 §17):** the sim still meets the job later in the tick. The next takers in the bundle-on arm are destroy `0x2000003` (25-36 ms/s), gate 0 `0x62EF7A` (9-16), `0x83156D` (5-6), `0x82F4AB` (~5) and the unmapped `0x729E2C` / `0x04B0021` (2-3). Expect most of the 21-22 ms/s to reappear there, so summed sim wait changes little. A real gain needs either the whole taker bundle of the tick or shorter job spans; the idle check is one more piece of that bundle, not a fix alone. On IP natural play this key leads 0-2 iterations >= 300 ms per 10 min, so the ceiling for visible freezes is small; the stress-loop numbers (19-56 ms/s, max 2.9 s) overstate it.

**Alternatives rejected.** SH-style admission (run the game's IC under a QL hold): `0x82EDB0` takes `+0x918` and adds footprint refcounts, so no. Defer: the script needs the answer now. Reduce the job's hold: that is the P6 line, stopped by docs/189 §17.7.

**Effort (Claude time):** C1 ~1-2 h (hook, `GvRun` reuse, counters in the free v119 tail or a new version reserved via `tools/registry.ts`, selftest cases like `TestGoalValidRO`); C2 ~3-5 h more (loop clone + selftest with fake footprints). Game runs: 1 shadow / smoke job (~15 min), then 1 verify job.

**Proof (validate-and-fix, no new A/B, smoke before timing).** One job on `ip-idlecheck-crowd-loop` (exists, `night-1024-gate12.json` shape) with `[IdleCheckRO] Enabled=1 Shadow=1` + C, `[MovedWaits] Enabled=1`, `[ProbeP1b] Enabled=1`:
- pass: no crash, `ikShadowMismatch` 0 and the C shadow mismatch 0 over >= 20 compares each; C1 empty share and C2 taken counts > 0.
- read in place: `moved waits idlecheck` net (avoided - moved) and the new moved key (expected destroy / gate 0, not `0x755990`); P1b second frame of the remaining `0x62FB74` waits (confirms `0x7558DC` still leads on a current build, I-178-8-1).
- then only if net > 0: put it in the next IP natural bundle smoke and read the >= 300 ms count by `rtMaxKey` with `hitch-ab.ts` (natural A/B only where hitches remain).

## 5. Open

- Caller split on current builds and in the ladder (P1b was off): I-195-1.
- C1 empty share in a crowd: I-195-3; if small, only C2 helps.
- Whether the moved wait lands on destroy / gate 0 (I-195-4); if so, this belongs with DestroyPark and G1 (docs/152) in one bundle, not alone.

## 6. Footsteps: `[FootstepRO]` (2026-11-03/04, issue #156, `fsro.cpp`, counters v901)

**Caller [measured].** With the eleven read-only pieces on (old save, P1b), the `0x62FB74` wait from `0x834201` was 14.2 ms/s (0.9 with them off): `RoutingWorld_SurfaceAtPoint 0x834B80` -> `EnsureTerrainFootprints 0x8341D0` -> `LotTable_GetIfPresent 0x62FB60`. A Frida drill (840 s) put 7,166 of 7,232 `SurfaceAtPoint` calls inside the pose pass `0x571B90`, all from footstep sounds: `FootstepAudio_Surface 0x8693E0` asks `World_SurfaceTypeAtPoint 0xAB9580` at `0x869713` [verified disassembly]. Only the sound reads the answer (`0x869130` maps it to the sound name).

**Design.** Call-site retarget at `0x869713`. On the sim thread the stub keeps the game's last answer per (0.5 m cell, level, flags) in a 4096-slot table. While a job is `A_RUNNING`, a fresh entry (`MaxAgeMs`, default 60 s) answers without the game's call. On a miss: `MissNear=1` takes a fresh entry of one of the 8 neighbouring cells, then `MissLast=1` the last surface the game gave, else the game is called (today's wait). With no job every call goes to the game, and the counters check what each answer would have been (`fsChk*`, `fsNearChk*`, `fsLastChk*`). A wrong answer plays another surface's step sound once; nothing else reads it.

**A/B [measured, `combo-local-2026-11-04-combo-fs-near`, save "1111 kit5", main b1d6b3c + this build, in-load `--ab footstepro`, 2-min slices, 10 min, `MissNear=1 MissLast=1`, the eleven pieces on in both arms].**

| | on | off |
|---|---|---|
| `0x62FB74` gate 12 (F-F) wait, ms/s | 1.0 | 13.1 |
| all keyed sim waits, ms/s | 36.4 | 42.6 |
| arm sum (`arm-ms.ts`: script + route + animPose + locomotion), ms/s | 78.9 | 101.5 (on - off -22.5, -22%; animPose -16.7) |

Job calls 973: 590 cell hits, 210 neighbours, 173 last surface, 0 to the game. Checks with no job (how often each answer differs from the game's): cell 46 of 2,749 (1.7%), neighbour 203 of 2,923 (6.9%), last surface 1,263 of 4,368 (28.9%). So in that run about 50 of the 973 job steps played another surface's sound, most from `MissLast`. One run; the same round's FailMemo job, whose switch did nothing (`fmRep` 0), differed by -9.2 ms/s (-11%) between its arms, so single-run arm sums carry about +-10% noise; the gate-12 key drop is the direct effect.

**`MissLast=0` run (2026-11-04, stack-local `stack-fs-nolast`, `--ab footstepro`, `MissNear=1`, Tail and DestroyPark on both arms).** Gate 12 F-F `0x62FB74` 16.0 to 2.6 ms/s; all keyed sim waits 45.4 to 34.8 ms/s; arm sum 106.2 to 73.5 ms/s (-31%, one run). Job calls 1,308: 831 cell hits, 342 neighbours, 135 to the game (10%). Checks with no job: cell 36 of 3,206 (1.1%), neighbour 197 of 3,809 (5.2%), last surface 1,480 of 4,900 (30.2%, measured, not used). So `MissLast=1` saves about 1.6 ms/s of F-F wait and costs a wrong surface sound on about 5% of job steps; `MissLast=0` gives about 2% wrong.

**Recommendation.** `MissLast=0` (the safe side when the gain is this small). Defaults stay `Enabled=0` until the PR review.

## 7. Site C1 built: `[IdleCheckRO] SiteC` (2026-11-04, counters v903, `idlero.cpp`)

**Call site [verified disassembly 2026-11-04].** `0x75598B` `call 0x82EDB0`, thiscall world = `[esp+0x34]`. Arguments: pt = the point `0x6508F0` builds (xyz +0, level +0x24, the layout GvRun reads); r = `[ebp+0x14]`; vec = a fresh empty FootprintRefVec (begin = end = cap = 0, tag `"Routing/Host"`); mode 0 (`0x755960`); opt = `[ebp+0x18]`. After the call the IC reads only the vector: begin == end jumps to `0x755A5B` (frees the empty range, returns 0); `al` is not read. So C1 is §4 step 1 as designed: the shape of `[GoalValidRO]`'s call site.

**Build.** `IcStub` at `0x75598B` runs `GvRun` (gvro.cpp) under QL shared while a job runs. An empty answer (no footprint, or no lot) returns without the game's call. Footprints found (`icFb[6]`) or any doubt (`icFb[0..5]`, GoalValidRO's reasons) call the game's `0x82EDB0`, which is today's wait. Shadow with no job: the game's answer, then `GvRun`, compared (`icShadowMismatch`); `icShadowEmpty` is the share Mode 1 would take (I-195-3). Ini `[IdleCheckRO] SiteC=0` (needs `Enabled=1`), `SiteCMode`, `SiteCShadow`; runtime switches `--ab idlecheckc` (+0x5B88) and `--ab idlecheckcshadow` (+0x5B8C). Selftest `TestIdleCheckC` (on GoalValidRO's fakes). C2 (non-empty answers) is not built.

**Runs (2026-11-04, stack-local, one save, 10 min each, in-load A/B).**

- Shadow (`SiteCShadow=1`, mode 0): 1,747 compares, 0 mismatches. Only 8 were empty (0.46%): the IC almost always finds footprints near the point.
- `--ab idlecheckc` (Tail on both arms): `icCalls` 2,080, `icTaken` 3 (all no-lot answers). Arm sum -6%, inside the noise: the shadow job, which cannot help, also read -6%.
- Tail and site C together vs neither: `0x83156D` 11.5 to 0.1 ms/s; all keyed waits 32.2 to 34.6 ms/s. The wait moves on.

**Verdict.** C1 does not pay and stays off. Only C2 (non-empty answers under QL shared) can make site C pay; it needs the footprint refs the IC's loop reads (§4 step 2).

**Harness bug found by these runs.** With SiteC installed, the IC's wait was keyed by `IcStub`'s own return address in ts3native.asi, not by `0x755990`. That address changes every run and differs per call instruction. The 32 P0 key slots fill first-come, so the off arm's key got no slot: its wait went to the untimed `blockOther` (262 vs 143 per min), and the off arm's keyed total read low by about 11 ms/s. Fix: `t_stubKey` (`StubKey` in `GvStub` and `IcStub`) keys the outermost ObstacleQuery wait by the game site (`0x755990`, `0x75A64C`), as AvSite does with `0x846D21`; both keys are seeded. The asi-range key in older runs (for example `0x6696A870`, 3 to 5 ms/s) was `GvStub`'s wait: the dx-local round (fixed keys) shows `0x75A64C` at 5-9 ms/s and no asi-range key.

## 8. Site C2: the IC's own loop on the clone (`[IdleCheckRO] SiteCLoop`, 2026-11-04, counters v905)

**Why.** After the footstep cache, the route-to-object tail and DestroyParkDx, idle check site C (`0x755990`) is the biggest sim wait left (about 19 ms/s on, docs/88 §11). C1 answers only empty results (0.46%). The other 99.5% find footprints near the point.

**Why not hand refs back.** The vector holds footprint interface pointers (object + 4). `FootprintRefVec_ReleaseRange` (`0xAAC090`) does a plain `-1` on `[el+4]` and deletes at zero. The sim thread cannot add refs while a job may change the same counts on the worker [verified disassembly 2026-11-04].

**What the loop does** (`0x7559A0..0x755A4C`) [verified disassembly 2026-11-04]. Per element, in vector order; the first footprint that decides ends the IC:

- Interface slot `+0xC` is the kind. All 11 targets are `sub ecx, 4` then a constant getter: `0x6262F0` 1, `0x74BB10` 2, `0x624EC0` 3, `0x624E10` 4, `0x624E60` 5, `0x6250A0` 6, `0x620E00` 7, `0x624E50` 8, `0x624E00` 9, `0x620E50` 10, `0x624EB0` 11.
- Kind 5:
  - With no ids in the frame (`[f+0x1C]` 0), the answer is 0x20.
  - Otherwise the id is read (slot `+4` = `0x620E60` -> `0x806FE0`: object `+0x14`/`+0x18`) and looked up in the frame's id list (`0x788EA0` is a plain find). Found means the next element.
  - Not found: `ObjectService_FindById` (`0x939560`, no lock, no AddRef). No object gives 0x20; an object's `+0x2C & [0x11E90FC]` gives 4, else 0x20.
- Other kinds:
  - Slot `+0x1C` is `0x620D20`: the primary class slot `+8` with `(0, 0)`, true for classes 2..12. The class functions are the six the S2 clone already calls under QL shared. The member footprint's slot `+0x1C` (`0x624F70`) is false.
  - When true, slot `+8` is the type byte `+0x35` (`0x624ED0`) or 10 (`0x620E50`). Type 1 gives 0x10, types 12 and 23 give 8; any other type means the next element.
- No footprint decides: the answer is 0 (`0x755A5B`).

**Frame** (the IC's esp at `0x755990`, after the call's `ret 0x14`): `+0x18` ids, `+0x1C` count, `+0x34` ids end (`0x755949`), `+0x38` the vector, `+0x4C` the object service (`0x7556CF`). `IcStub` takes it from its return slot and checks three things: the return address `0x755990`, `vec == f + 0x38`, and `end == ids + count * 8`.

**Build (C2a).**

- While a job runs, `GvRun` runs the loop on the clone's footprints inside its QL hold (a visitor), with only reads.
- Answer 0 (no footprint decides): the vector stays empty, and the IC returns 0 at `0x755A5B`, as after the game's call. The answer does not depend on list order.
- Any other answer, or a doubt (an unlisted slot target or class function, a frame that fails the checks, a truncated clone list): the game's call.
- Shadow (`SiteCShadow=1`, no job): the loop on the game's vector and on the clone, compared. `icLoopShadowZero` is the share C2a would take; `icLoopShadowMismatch` (one list answered 0, the other did not) must stay 0. `icLoopShadowCodeDiff` (both non-zero, different) can come from list order.
- Ini `SiteCLoop=0` (needs `SiteC=1`, `SiteCMode=1`); runtime switch `--ab idlecheckc2` (+0x5BD8). Selftest: the loop cases, the frame check, the job path and the shadow compare, in `TestIdleCheckC`.

**C2b: `[IdleCheckRO] SiteCLoopJump` (counters v906 `icJumpMode`, `icJumped`; `--ab idlecheckc2b`, off by default, needs `SiteCLoop=1`).** After C2 the calls a footprint decides (about 20%, mostly 0x10) still call the game. With C2b, IcStub writes the IC's exit for that answer into its own return slot instead of `0x755990`: 4 `0x755A12`, 8 `0x755A82`, 0x10 `0x755A97`, 0x20 `0x755A6D`. Each exit is `lea ecx, [esp+0x38]; call 0x652470` (the vector's release: `ReleaseRange` on its range, then free when allocated; empty here), `mov eax, answer`, the pops and `ret`. No exit reads a register the loop set [verified disassembly]. The return slot is the one IcFrameOf checked (`0x755990`); the stack after IcStub's `ret 0x14` is the one the IC's own path has. Install checks each exit's first 9 bytes (lea + call to `0x652470`) and the `mov eax, answer` after it, or leaves C2b off. The answer is the one the shadow compares (code diffs 0 of 223 non-zero answers in round 2). A doubt still calls the game.
Measured (2026-11-04, two A/B runs with C2 on in both arms, plus a 35-min soak): site C `0x755990` 3.9 and 18.5 ms/s off,
0.0 on, and the keyed sim waits fall 19% and 54%. In the soak, 2,050 jumps and 0 doubts
(docs/data/results/2026-11-04-alexvixell35-stack-idle-check-c2.md).

**Round 1 (2026-11-04, c2-local, final setup on both arms).**

- Shadow: 598 loop compares, 0 mismatches, 0 code diffs; 426 (71%) answered 0. But 1,132 evaluations were doubts.
- A/B `--ab idlecheckc2` twice: of 1,021 and 1,112 runs, 222 and 245 taken (22%), 669 and 715 doubts. Site C `0x755990` 13.8 vs 17.5 and 21.7 vs 16.5 ms/s: noise at that share.
- Doubt log (doubt reason codes added, the first 24 logged with their slot targets): all 24 were the kind getter `0x624E00` (interface `0xFEBA74`, kind 9), which the first table missed. Added.

**Round 2 (kind 9 added, c2e-local).**

- Shadow: 1,737 loop compares, 0 mismatches, 0 code diffs, 0 doubts; 1,514 (87%) answered 0.
- A/B `c2e-ab`: 1,268 runs, 1,028 taken (81%), 240 decided by a footprint (229 of them 0x10), 0 doubts. Site C `0x755990` 4.7 vs 13.7 ms/s; all keyed sim waits 23.9 vs 30.7 (-22%). Arm sum +7% (inside the noise). Stuck Sims 5. 10 ms of clone and loop time over 1,028 taken calls.
- A/B `c2e-ab-b` died after about 8 min: crash W (docs/67 §27), the main thread's Sim thumbnail render reading a released pose at `0x54E79A` (an EA bug, also vanilla). The stack holds only TS3W addresses; C2 answers only what the game would. The first crash W in our runs. The next runs turn on `[EaCrashFixes] ThumbPose` (docs/67 §27.5) on both arms.
- A/B with ThumbPose on both arms, twice: taken 82% and 80%; site C 3.8 vs 15.5 and 6.0 vs 18.8 ms/s; all keyed sim waits -26% and -38%; arm sums -17% and -15%; no crash. Results: `docs/data/results/2026-11-04-alexvixell35-stack-idle-check-c2.md`.
