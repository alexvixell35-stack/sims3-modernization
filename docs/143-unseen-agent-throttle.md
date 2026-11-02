# 143. Unseen agents: per-tick locomotion work, existing LOD, probe and throttle sketch

Written 2026-10-21. Rodney's idea: spread routing and locomotion work over several ticks for Sims (and traffic cars) the player cannot see. Static study on `game-copy/bin/TS3W.exe` (Steam 1.67: capstone 5 over the exe, a direct-call walk, string and vtable reads) and the decompiled script library; an offline-built probe (`[UnseenProbe]`, ts3native counters v79 since the 0.36.13 merge, v78 on its first branch, selftest only); a queue that is written but **not run**; a throttle design sketch, since 0.36.13 its phase 1 built offline (§6.5), since 0.36.14 the phase-1 freeze fix (§6.6), and since 0.36.15 phase 2 (§8). No game run. Tags as in AGENTS.md; ledger rows in docs/51 §206, §207, §211, §212.

## Summary

- **The per-agent work [verified].** `MonoScriptHost_Simulate` calls `Locomotion_Update 0x83CED0` once per tick (docs/86); it walks the agent list and calls `Locomotion_UpdateAgent 0x957210` for each enabled agent (call site `0x83CF52`). Per agent: EA's own on-screen test, the behaviour update `0x839F70` (avoidance `0x847390` -> `RouteFollow_A` -> `+0x918` key `0x846D21`, docs/137), then for a walking Sim (state 1) or paddleboat (state 4) the steering obstacle list `0x85FFE0` -> `Locomotion_ObstacleCheck 0x85FD10` -> `+0x918` key `0x85FE10` (the S2 site, docs/84). Footprint updates (`0x833560`, key `0x833593`) are **not** in this pass: they run from the animation passes (docs/89). Cars are agents too (state 2, `CarBody` + `CarBrain`); they get neither avoidance nor the steering obstacle list.
- **EA already has an off-screen LOD for walking Sims [verified selection, behaviour inferred].** Every agent update recomputes an on-screen byte `agent+0xC8` (frustum test of 3x the model bounds, false beyond 100x that radius, `0x954240` -> `0x980E00` -> `0x980D50` -> `0x68D130`). When it flips, the agent's behaviour set is rebuilt: an off-screen walking Sim gets **`Locomotion/OffScreenSimBrain` + `Locomotion/SimpleBody`** instead of `SimBrain` + `AnimatedSimBody`. Both brains keep the avoidance behaviour, and the steering obstacle list does not look at the byte. So the two `+0x918` takers (steering and avoidance, 11-21 and 10-17 ms/s of keyed waits on BP, docs/136) run every tick for every walking Sim, seen or not. That is the gap a new throttle can use without stacking on EA's.
- **Other throttles that exist** (§3): script autonomy LOD (60 ticks), `SimUpdate` 2 high-LOD Sims per tick, lot LOD and imposters, Evaluate skipped for idle actors, every 10th pass a world-bounds sweep, a per-agent timer at `+0xE0`, and ts3fixes' unseen-townie shortcuts plus a 1-in-4 broadcaster pulse. None of them throttles the locomotion `+0x918` takers.
- **Probe built (`[UnseenProbe]`, off by default, counters v79, `unseen.cpp`):** classifies every agent update visible / unseen with the ts3fixes rule (on screen, or within `max(80 m, 2 x camera height over target)` of the camera or its target; no household test natively) and splits per class: agent update time (Sim / other), first `+0x918` acquires and keyed waits (steering, avoidance, footprint, other), `UpdateSimFootprint` calls, route plans (car-only routes apart), class flips, and how often EA's own byte says off screen. Selftest ok. Report: `tools/harness/unseen-report.ts`.
- **Queue `tools/harness/queues/unseen-split.json`** (not run): BP play 10 min, BP `rare-big-crowd` 8 min, IP play 10 min, release ini + `UnseenProbe.Enabled=1`. Correctness/data job, daytime OK.
- **Throttle point [design]:** the steering obstacle list call at `0x9573B7` is a clean place: the result vector is zeroed before the call and an empty result means "no notice this tick", so skipping the call for an unseen walking Sim on N-1 of N ticks is a call-site patch with no game state written. Avoidance needs a wrapper on its behaviour slot (phase 2, its return use not yet read). Expected gain is bounded by how much of the two keys' waits the probe puts in the unseen class, and by how much of a removed wait just moves to the next acquirer in the same pass (S2 moved ~75% of its steering gain to avoidance, docs/137 §1): **~1-21 ms/s** over the plausible range (§6.3), real number only after the queue.
- **Phase 1 built (0.36.13, §6.5, offline, not run):** `[UnseenThrottle]` (default off, `--ab unseenthrottle`) skips the obstacle-list call for unseen walking Sims on 3 ticks of 4, staggered by owner id, with every §4 snap-back rule; household positions come from ts3fixes (`UnseenViewers.cs`) over a new GetOption2 channel. Selftest `TestUnseenThrottle` ok. Queue `tools/harness/queues/unseen-throttle-ab.json` (not run).
- **Measured 2026-10-21 (`unseen-first`, BP 10 min, 0.36.13 probe):** unseen agents are 94.4% of agent updates; per second unseen Sim updates 56.6 ms, other agents 11.4, `+0x918` waits steering 12.6 / avoidance 5.1 / footprint 7.1, footprint bodies 10.4, other plans 10.1, car-only plans 15.3; visible ~10 ms/s in all. The same queue's phase-1 A/B jobs **froze the game ~10 s after the switch** (both): the path test released a path reference it never took (§6.6, fixed in 0.36.14).
- **Phase 2 built (0.36.15, §8, offline, not run), each part off by default with its own switch:** `Avoid=1` (`--ab unseenavoid`): ObstacleAvoidance every 4th pass for unseen Sims beyond 3 m of the goal; `Footprint=1` (`--ab unseenfootprint`): an unseen Sim's footprint refreshed at most every 500 ms, equal calls folded; `Rate=1` (`--ab unseenrate`): the whole agent update every 4th pass with the summed step (behaviour B's own 1/30..0.5 s clamp); ts3fixes `unseencars`: an unseen traffic car bound for an unseen spot stays parked 3 rounds in 4 (the game's own "no free spot" branch, nothing moved). Expected (BP, not additive, §8.7): rate ~15-35 ms/s, footprint ~4-12, cars ~3-10, avoidance ~2-6. Counters v80, ts3fixes 197-200, selftest `TestUnseenThrottle2` ok, queue `tools/harness/queues/unseen-throttle-p2-ab.json` (19 jobs, not run).

## 1. Method

Disassembly of `0x83CED0`, `0x957210`, `0x954240`, `0x980D50`-`0x981070`, `0x956850`, `0x953810`, `0x953860`, the brain builders `0x953310`-`0x953530` and the behaviour ctors, with a direct-call walk 4 levels deep from `0x957210` (203 functions, virtual calls not followed) looking for camera, frustum and LOD reads. Strings and vtables read from the exe. Script side: `CarNpcWithNoDriver.LiteDriver`, `CarRoutingComponent`, `RoutingComponent.DoRoute`, `Route.RouteOption` in the decompiled library; ts3fixes `Fixes.cs` (`FarFromViewers`, `TownieUnseen`). Earlier docs for the keyed waits (docs/84, 86, 89, 136, 137) are taken as verified there.

## 2. Per-tick locomotion work on the sim thread

### 2.1 The pass [verified]

| Step | Address | What |
|---|---|---|
| caller | `0xD81C5C` in `MonoScriptHost_Simulate` | one `thiscall` per tick, ecx = locomotion system `[0x11D9140]`, one float dt (docs/86, `locowin.cpp`) |
| list walk | `0x83CF27`..`0x83CF5A` | intrusive list at system `+0x40` (node at agent `+0x40`); for each agent whose `[agent+0x10]` is non-zero: `call 0x957210` at `0x83CF52` (agent, dt) |
| every 10th pass | `0x83CF6D`..`0x83D0B3` | counter at system `+0x104` mod 10: a second walk tests each agent's position against the world manager bounds (`WM+0xB4..+0xC4`) and handles agents whose `+8` part reports 2 or 3 (its vtable `+0x18`) and that are outside [verified branch; purpose inferred: agents leaving the map] |

There is no distance, LOD or "every N ticks" test in the per-agent walk itself: every enabled agent is updated every tick.

### 2.2 `Locomotion_UpdateAgent 0x957210` (`thiscall(agent; dt)`, `ret 4`) [verified]

1. **On-screen byte.** Owner object `[agent+4]`; position from the transform component (`0x935770` with bit global `0x11E9158`, then `0x959000`), adjusted for a parent when component `0x11ECF08` says so; `0x954240` then tests a sphere of 3 x the model bounds radius (`[0x1074010]` = 3.0) against the camera frustum(s) (`0x980E00`, which calls `0x980D50` for the camera(s) it keeps at `0x11E94C8` [list meaning inferred]; `0x980D50` returns false when the radius is under 0.01 or the distance is over 100 radii, `[0x117E058]` = 100, else `0x68D130`). The result is compared with `agent+0xC8`; on a change it is stored, a listener is told, and `0x956850` rebuilds the brain and body (§2.3).
2. **Behaviour update** `0x839F70(agent+8, dt)`: runs the brain's behaviours and the body. The avoidance behaviour (vtable `0x1026E04`, slot `+0xC` = `ObstacleAvoidance 0x847390`) queries `RouteFollow_A 0x846B70` only near the goal, taking `+0x918` (key `0x846D21`, docs/137 §2).
3. **Agent timer.** When `agent+0xE0` > 0 and `agent+0xC0` is 0, `agent+0xE4` accumulates dt and calls the agent's vtable `+0x68` each time it passes `+0xE0` [verified; what `+0x68` does was not read].
4. **Steering obstacle list**, only when `[agent+0x10C]` is set and the state `agent+0x94` is 1 (walking Sim) or 4 (paddleboat): a local id vector is zeroed (`0x957388..0x957396`), `Locomotion_ObstacleList 0x85FFE0` is called at `0x9573B7` (cdecl, 5 arguments, caller pops), which calls `Locomotion_ObstacleCheck 0x85FD10` -> `0x82EDB0` (`+0x918`, key `0x85FE10`, the S2 site). A non-empty result goes to the agent's vtable `+0x60` (obstacle notice, docs/82); a debug draw follows when the system's `+0x100` byte is set; the vector is freed.

### 2.3 Brain and body by state and on-screen byte [verified]

`0x956850` picks a body hash with `0x953810(state, onScreen)` and a brain hash with `0x953860(state, onScreen)`, then builds by hash (string = the class's memory tag, read at the push before each allocation):

| State `+0x94` | On screen `+0xC8` | Body | Brain | Brain's behaviours |
|---|---|---|---|---|
| 1 (Sim) | 1 | `AnimatedSimBody` (ctor `0x84EFF0`) | `SimBrain` (`0x953410`) | avoidance (`0x846820`) + behaviour A (vtable `0x1026EE8`) |
| 1 (Sim) | 0 | `SimpleBody` (`0x858510`, the default) | `OffScreenSimBrain` (`0x9534A0`) | avoidance + behaviour B (vtable `0x1026E98`) |
| 2 | any | `CarBody` (`0x8565D0`) | `CarBrain` (`0x953390`) | behaviour A only |
| 3 | any | tag `0x1044724` (`0x8557A0`) | `CarBrain` | behaviour A only |
| 4 | any | `PaddleBoatBody` (`0x856AC0`) | `PaddleboatBrain` (`0x953530`) | avoidance only |
| 5 | any | tag `0x10446F4` (`0x857220`) | `BasicBrain` (`0x953310`) | behaviour B only |

The hashes are 32-bit FNV-1 of the lower-case names (checked for `paddleboatbrain` 0xBF7F3A69 and `paddleboatbody` 0x7BBC9F2F). What behaviours A and B do, and how much cheaper `SimpleBody` is than `AnimatedSimBody`, was not read [inferred: B is the plain path follower EA uses for anything it does not animate; A adds the animated / vehicle follow].

### 2.4 Where the keyed waits sit

| Key | Taker | In the locomotion pass? | Seen / unseen today |
|---|---|---|---|
| `0x85FE10` steering | `0x85FD10` from the obstacle list (§2.2 step 4) | yes, per walking Sim per tick | both: the byte is not read there |
| `0x846D21` avoidance | `ObstacleAvoidance` in both Sim brains; also two route ICs | yes for the behaviour, no for the ICs | both |
| `0x833593` footprint | `UpdateSimFootprint 0x833560` from `Locomotion_Notify` in the animation passes, event `0x21`, ICs | no (docs/89 §1) | both |
| `0x62EF7A` gate 0 and others | transform wrapper inside the queries (docs/86) | partly | both |

docs/136 (BP, noisy night): steering 11-21 ms/s, avoidance 10-17 ms/s; with S2 on, steering's waits moved to avoidance (21.5 -> 11.3 vs 9.9 -> 17.4).

### 2.5 Driving cars and `LiteDriver` [verified script, native inferred]

- `CarNpcWithNoDriver.LiteDriver` is a script task per driverless traffic car: once it lists the commercial lots; then, until told to drive off, picks a random lot's street parking spot, and if it is more than 10 m away builds a car route (`CarRoutingComponent.CreateRoute`: `EnablePlanningAsCar`, `DisablePlanningAsPedestrian` `0x20`, `BeginAsCar`, `EndAsCar`, `CreateSubPaths`), plans it with `PlanToPoint` (-> `Route.Plan` -> `IC_Route_Plan`), follows it with `DoRoute`, then sleeps `TrafficManager.kCarWaitingTimeRange` sim-minutes. The plans are the `Foreign` 50-64 ms sync plans of docs/141.
- While following, the car is a steering agent in state 2 (`CarBody` + `CarBrain`): no avoidance query, no steering obstacle list (step 4 needs state 1 or 4). Its per-tick cost is the body/behaviour update only.
- An earlier shortcut that skipped ambient car routes was **dropped** (docs/04 F-table: more stuck Sims in Bridgeport, ~1% gain). So cars are a measurement item here, not a throttle target until the probe shows their share.

## 3. Existing LOD and visibility throttles

| Throttle | Where | What it saves | Overlap with a new unseen throttle |
|---|---|---|---|
| Off-screen brain/body swap | native, `agent+0xC8`, §2.3 [verified] | animated locomotion for walking Sims outside the (3x enlarged) frustum or far away | do not touch the body or brain choice; throttle only what both brains still run (avoidance, obstacle list) |
| Every-10th-pass bounds sweep | `0x83CF6D` [verified] | runs a sweep 1 pass in 10 | none |
| Agent timer `+0xE0` / vtable `+0x68` | `0x9572A5` [verified, role unknown] | periodic per-agent work | none known |
| Autonomy LOD | script `IsSufficientlyOnScreenForHighLODSimulation`, 60-tick recompute, `mbHighLOD` (docs/59 B8, docs/126 H10) | local autonomy only for high-LOD Sims | none: autonomy, not locomotion |
| `SimUpdate` limit | script, 2 high-LOD Sims per tick (docs/04a) | motives and buffs | none |
| Lot LOD and imposters | native lot scoring, `Max Active Lots` (docs/28 §5, docs/65) | lot activation and drawing | none |
| Animation Evaluate | skipped for idle actors (docs/22 §5.1a) | pose evaluation | none |
| ts3fixes shortcuts | go home, rabbit holes, lot visits, route-fail backoff for unseen townies (`TownieUnseen`); broadcaster pulse 1 call in 4 for unseen Sims (`Fixes.cs`) | whole walks and script reactions | same classifier wanted, so a throttled Sim and a shortcut agree on "unseen" |

## 4. What a throttle would change, and where it must snap back

| Option | Mechanism | Behavioural effect [inferred unless marked] |
|---|---|---|
| (a) skip whole agent updates, run every Nth tick with N x dt | patch `0x83CF52` | bigger steps: corner cutting and goal overshoot if the body/behaviours do not clamp; door and portal events, route events to script (`+0x60`, `+0x64` notices) up to N ticks late; footprints of moving Sims refreshed less often (other plans with `BlockedByPeople` see older positions); EA's on-screen byte also updated less often, so the brain swap reacts later. Too broad. |
| (b) steering obstacle list every Nth tick for unseen walking Sims | skip the call at `0x9573B7` | a skipped tick reports no obstacles (vector stays empty [verified]); a blocking object along the path is noticed up to N-1 ticks later, so the replan/stop it triggers is later. No step-size change, no state written. **Clean point.** |
| (c) avoidance every Nth tick | wrapper on behaviour slot `0x1026E10` | the near-goal "goal obstructed" notice (`+0x64`) comes later; whether a skipped behaviour update leaves the agent's steering unchanged or zero depends on how `0x839F70` uses the slot's result (not read) |

**Snap back (run the full work this tick, every tick, while any holds):**

1. The agent is visible by the probe's rule (on screen or within reach), or EA's byte `+0xC8` is 1.
2. The Sim is in the active household or selectable (ts3fixes knows; native does not) or within 80 m of one (the ts3fixes `FarFromViewers` part).
3. The route is near its goal (the avoidance threshold of `0x8469F0`), or crossing a portal / door / stairs segment, or the agent is linked to another agent (the `0x83A280` lookup at `0x957333` that makes the obstacle list run on the linked agent's behalf, e.g. riding or carrying [inferred]).
4. The first tick after any of the above clears (no skip on the tick a Sim leaves view), so the class flip itself never hides a notice.

## 5. Probe as built (`[UnseenProbe]`, `unseen.cpp`, counters v79)

**Sites [verified bytes]:** the rel32 of `call 0x957210` at `0x83CF52` (PatchCalls, refused unless it is that call); `UpdateSimFootprint 0x833560` entry detour (8 bytes `83 EC 10 80 7C 24 1C 00`; `Footprint=0` skips it; with design U (`[Discourage] DeferFootprint=1`, the same entry, installed first) the probe takes no site there: design U's way to the game's body (`g_usfOrig`) is pointed at the probe's hook, so it counts the bodies that run, deferred records at their drain; docs/68); `IcPlanHook` (ts3native's own detour of `IC_Route_Plan`, needs `[RouteAsync]`) calls `UnPlan`; `WaitNote` and `LockAcquireHook` (ts3native) call `UnWait` / `UnAcq`.

**Classifier.** Per locomotion pass (the list head's update, or 250 ms without one) it reads `Camera_GetPosition 0x73D410` and `Camera_GetTarget 0x73D480` once and sets reach = `max(80, 2 |camY - targetY|)`, the ts3fixes formula. Per agent: owner position (`0x935770` + `0x959000`), a 1 m sphere 1 m above it tested with the game's `0x68D130` on the frustum of `0x96B850` (as `IC_World_IsObjectOnScreenImpl 0x78AAB0`, but without model bounds), then the reach test against camera and target. No camera = visible. **Gap:** no active-household test natively; a household Sim walking far off screen counts as unseen (rare: household Sims are usually near the camera or in rabbit holes). Kind: Sim when the owner has the Sim component (bit global `0x11E90FC`), else other (cars, boats) [inferred mapping].

**Counters** (v79, 69 int32 at `+0x32B4`, after v78 early-out since the 0.36.13 merge; registry row): `unReady` (bits: agent site, footprint, plans, keyed lock), `unPasses`, `unCamFail`, `unSkipped`, `unFlips`, `unAgents[class][kind]`, `unAgentUs`, `unAgentMaxUs[class]`, `unAcq[class 0-2][steering, avoidance, other]` (first `+0x918` acquires on the sim), `unWaitN` / `unWaitUs[class 0-2][steering, avoidance, footprint, other]` (keyed waits for the route job), `unFp` / `unFpUs[class 0-2]`, `unPlan` / `unPlanUs[other route, car-only route][follower class 0-2]` (follower = route `+0x40/+0x44`, written by `0x74B1B0` [verified]; car-only = option bit `0x20` at route `+0x158+0x28`), `unReachX10`, `unEaOff[class]` (EA's byte 0 after the update). Class 2 = outside an agent update (route ICs, plans, other sim code) or not classified.

**Overhead.** Per agent per tick: 2 QPC reads, 4-5 game calls (component lookups, position, frustum), one SEH frame. All agent time counters include it; read shares and ratios, not absolute ms/s, and never compare a probe run's it/s with a run without it.

**Checks.** `build.cmd`: `TestUnseen` (rule: reach, camera vs target, on screen, zoom; a visible Sim and an unseen car through the thunk with their acquires and waits; pass detection by list head; a flip; an unowned agent not split; no camera = visible; footprint by object with its own wait; plan kind and follower class) -> `selftest ok`. `node tools/harness/unseen-report.ts --self-check` ok. `node tools/hook-sites.ts --check`: the new addresses are in docs/68.

**Queue** `tools/harness/queues/unseen-split.json`: `bp-unseen-play` (10 min), `bp-unseen-big-crowd` (`rare-big-crowd`, 8 min), `ip-unseen-play` (10 min); asi `game-copy/ts3native.asi.0313` (0.36.13 merge build), `minCounters` 79; ini = release defaults + `UnseenProbe.Enabled=1`. Pass: no crash, 0 watchdog lines, `unReady` 15, report ok. After: `node tools/harness/unseen-report.ts probe-out/queue/unseen-split-<date>-<job>-native.json`.

**What the queue answers:** (1) the unseen share of agent updates and of their time, Sim vs car; (2) how much of the steering and avoidance keys' waits happen inside unseen agents' updates (the only waits a throttle can remove); (3) how often EA's byte already says off screen for our "unseen" (expected ~100%) and for our "visible" (Sims in reach but off screen); (4) car-only plans per minute and their sim ms/s, and how many come from unseen cars; (5) class flips per second (how often snap-back would fire).

## 6. `[UnseenThrottle]` sketch (design only)

### 6.1 Rule

`[UnseenThrottle] Enabled=0, Every=4, Avoid=0`; runtime switch for `--ab unseenthrottle`. For an agent in state 1 whose class is unseen and none of the §4 snap-back conditions holds, run the steering obstacle list only when `(pass + agent hash) % Every == 0`, so unseen Sims spread over the ticks instead of all skipping together. Household and selectable Sims come from ts3fixes: it already reads the active household once per tick (`RefreshViewers`); it would hand their object ids to ts3native through the existing internal-call route (docs/122's registration) or a small shared array, and the classifier reads them. Phase 2 (`Avoid=1`): the same rule for the avoidance behaviour's slot, after reading how `0x839F70` uses the slot result.

### 6.2 Where

Phase 1: rewrite the rel32 at `0x9573B7` (`call 0x85FFE0`, cdecl, the caller pops 0x14) to a thunk that returns at once on a skipped tick; the zeroed vector then means "no notice" [verified]. No game state is written on a skipped tick. The probe's classifier and counters are reused (`unseen.cpp`); counters for skipped calls and snap-backs by reason.

### 6.3 Expected effect

Let W = keyed waits of the two takers (BP: steering 11-21 + avoidance 10-17 = ~21-38 ms/s, docs/136), u = the probe's unseen share of those waits (`unWaitUs[1][0..1]` / all classes), N = `Every`, and m = the part of a removed wait that is not just taken by the next acquirer in the same pass (S2 kept only ~0.26 of its steering gain net, docs/137 §1, so m = 0.25-1).

Saved ≈ W · u · (N-1)/N · m. With N = 4:

| u (from the probe) | m = 0.25 | m = 1 |
|---|---|---|
| 0.25 | 1.0-1.8 ms/s | 3.9-7.1 ms/s |
| 0.5 | 2.0-3.6 ms/s | 7.9-14.3 ms/s |
| 0.75 | 3.0-5.3 ms/s | 11.8-21.4 ms/s |

Phase 1 alone touches only the steering key, so W there is 11-21 ms/s and the table scales by ~0.55; phase 2 brings avoidance in and raises m (both takers skipped on the same ticks, so the wait is not handed from one to the other). Sim-thread CPU outside the waits: at most `unAgentUs[1][0]` x (N-1)/N x the query share of an update (not measured). Cars: nothing in phase 1 or 2 (no steering list, no avoidance); their plans are a separate topic (docs/141, the dropped shortcut).

### 6.4 Gates and effort

G-U0 the queue above (data). G-U1 build phase 1 (~1-2 h, selftest with a fake agent and the real call-site bytes). G-U2 one BP `--ab unseenthrottle` 10 min plus the stuck-Sim check (docs/TESTING.md) and a door-heavy repro (`rare-big-crowd`), 2 game runs; pass = steering key lower on than off beyond the docs/136 per-key null (-1.5..+9.4 ms/s), all keyed waits not higher, no new stuck Sims, it/s not down more than 2%. Phase 2 after reading the avoidance slot use (~1-2 h static, ~1-2 h build), same gates. Clean-night timing only after G-U2 passes.

### 6.5 Phase 1 as built (ts3native 0.36.13, counters v79 `ut*`, offline, not run)

**Site [verified bytes].** `PatchCalls` rewrites the rel32 at `0x9573B7` (refused unless it is `E8` to `0x85FFE0`) to `UtListThunk`, a cdecl function with the same five arguments (`part`, `path`, `&vec`, 0, 0). A skipped call returns at once: the caller's vector stays as zeroed at `0x957388..0x957396`, the `begin == end` test at `0x9573C7` skips the obstacle notice, the debug draw and the free see an empty vector, as the game's own "no obstacles" path. `[UnseenThrottle] Enabled=1` also installs the probe's `0x83CF52` site (the agent and its class come from there through thread-locals); `[UnseenProbe] Enabled=0` then leaves the probe's footprint, plan and lock splits off.

**Rule, in order** (`UtWhy`; a reason that holds is kept per agent, and the next call of that agent runs too):

| # | Runs because | Source |
|---|---|---|
| 0 | `utMode` 0 (`--ab unseenthrottle` off half) | runtime switch |
| 1 | no agent context (update not classified, fault) | probe thunk |
| 2 | visible by the probe's rule | probe class |
| 3 | EA's on-screen byte `agent+0xC8` is 1 | [verified] byte |
| 4 | state `agent+0x94` is not 1 (paddleboats run every tick) | [verified] |
| 5 | linked agent: `part != agent+8` (the `0x83A280` lookup at `0x957333` put another agent in `ebx`) | [verified] |
| 6 | within 80 m of an active-household Sim | ts3fixes table |
| 7 | no fresh household table (older than 2 s) while `NeedViewers=1` | channel |
| 8 | first call after any held reason, or an agent not seen before | per-agent slot |
| 9 | stagger turn: `(unPasses + owner id) % Every == 0` | owner `+8` |
| 10 | remaining route distance `0x8469F0(path, cur)` <= `NearM` (15 m = `[0xF9A62C]`, the avoidance near-goal default) | [inferred] |
| 11 | current sub-path rest (`path` vtable `+0x12C` minus `+0xA4(cur)`) or progress <= `PortalM` (3 m) | [inferred] |
| 12 | no path, or a fault in the path tests | SEH |

`cur` = the agent part's vtable `+0x10` (`edx:eax`), the same value `ObstacleAvoidance 0x847390` passes to `0x8469F0` every tick. Rows 10-12 run only on would-skip ticks (cost in `utPathUs`). Sub-paths ending at portals (doors, stairs) is an inference from how the route is split (docs/92); the A/B's door-heavy job reads `utWhy_11` and the stuck check guards it. Ledger §207.

**Household positions.** ts3fixes `UnseenViewers.cs` (started at every world load) pushes the active household's `Sim.Position`s every 5 script ticks over `Route.GetOption2`, RouteOption2 top byte `0x82` (bits 63 + 57; the door channel is `0x84`, A10 `0x0C`): code 1 BEGIN, 2/3/4 x/y/z float bits (z adds a viewer), 5 END with the count; at most 32. ts3native (`UtViewerMsg`, from `GetOptionHook`, so it needs `[RouteAsync]`) answers BEGIN with 0 when the throttle is not installed, and ts3fixes stops for that load. A table older than 2 s counts as missing: with `NeedViewers=1` (default) nothing is skipped then (old ts3fixes, paused game, no `[RouteAsync]`).

**Counters** (v79, after the probe's fields): `utMode` (+0x33C8, switch), `utReady` (bit 0 site, 1 a table arrived), `utEvery`, `utCalls`, `utRun`, `utSkipped`, `utWhy[13]` (the table above), `utPathUs`, `utViewerPushes`, `utViewers`, `utViewerBad`. `unseen-report.ts` prints a throttle line when `utReady` bit 0 is set.

**Selftest** `TestUnseenThrottle`: viewer channel (refused while not installed, count mismatch and out-of-order refused, good table live), no fresh table = run, stagger (two agents with ids 1 and 2 run at passes 3, 7 and 2, 6 of 1-8: one call in four each, at different passes), every snap-back row above with its "first tick after" rerun, NaN distance runs, `NeedViewers=0`, mode off; a skipped call leaves the vector exactly as zeroed, a run call reaches the original with the same arguments.

**Not built:** phase 2 (built in 0.36.15, §8); per-agent `NearM` (the game's own threshold is `max(1.5, 2 x radius)` with a per-agent record, 15 m without: 15 m everywhere is the conservative side).

**Queue** `tools/harness/queues/unseen-throttle-ab.json` (not run; ts3fixes from the same commit): `bp-ut-play` / `-onfirst` (10 min, `--ab unseenthrottle`), `bp-ut-crowd` / `-onfirst` (`rare-big-crowd`, 8 min), `bp-ut-stuck-off` / `bp-ut-stuck-on` (15 min, throttle off / on for the whole run, stuck Sims compared). Pass as §6.4 G-U2.

### 6.6 Crash: 0.36.13 hang in the first seconds of play (fixed in 0.36.14)

**What happened.** `bp-ut-play` and `bp-ut-play-onfirst` (`unseen-first`, 2026-10-21, BP seed 12345, `--ab unseenthrottle`) both froze 9 s after the profile unpaused the game (heartbeat last seen 01:51:34 and 01:54:37 UTC; "2.8 min" counts from the job start, load included). Both runs started with the throttle on: `utMode` is 1 from install and the `--ab` warm-up slice is on in both orders (`unseenthrottle on` logged by both), so the arm order played no part. Not a crash: no new first-chance exception after the 5 startup ones (`[Crash] Log` keeps 16), no EA xcpt file; the process kept one core busy (`ts3w` 6.4-6.7% of 16 threads in the machine log) with flat memory until the harness killed it (`detached: application-requested`). Counters read while hung: `unPasses` 3 / 4 (0 before: the game was paused until the profile), `utCalls` 48 / 49 = `utRun` + `utSkipped` (no call in flight), path tests reached `0x8469F0` 9 / 7 times (`utWhy_10` + `utWhy_11` + `utSkipped`). The jobs without `[UnseenThrottle]` (`bp-unseen-play`, `bp-unseen-big-crowd`, `ip-unseen-play`) passed on the same build.

**Cause [verified disassembly].** `0x8469F0` takes the path as a by-value intrusive reference (count at `+4`) and releases it before returning (`0x846A7E` when it steps to the next sub-path, `0x846AB2` at the end): the caller adds the reference, as `ObstacleAvoidance` does at `0x847532` (`lock xadd [path+4], 1` right before the call). `UtPathWhy` passed the raw pointer, so every path test dropped one reference the agent's route still owned. After a few ticks the path's count reached 0 and its deleting destructor ran (vtable `+0`, `push 1`) while the agent kept the pointer; the next uses read and wrote freed memory, which surfaced as a spin on the sim thread instead of an access violation. Nothing else in the throttle touches a refcount (vtable `+0x12C`, `+0xA4` and the part's `+0x10` are plain thiscalls, `0x9573B7` passes the same raw `path` the game passes there). Ledger §211.

**Fix (ts3native 0.36.14).** `UtPathWhy` adds the reference (`InterlockedIncrement(path + 4)`) before calling `0x8469F0`. A fault inside the call leaks that reference instead of risking a double release. Selftest: the fake `0x8469F0` releases the reference as the game does, every call checks that the path's count is unchanged, and the fault case checks the leak. Without the fix the selftest fails at the first path test (negative control run). Next: rerun `unseen-throttle-ab.json` on 0.36.14.

## 7. Open items

- What `OffScreenSimBrain`'s behaviour B and `SimpleBody` do per tick (I-143-3); B's dt clamp is read (§8.4), its path integration is not.
- The avoidance slot's result use: closed, nothing reads one (§8.2, I-212-2).
- The agent timer `+0xE0` / vtable `+0x68` (I-143-4): the rate part runs every pass for periods under 1 s (§8.4).
- Household positions from ts3fixes: built (§6.5); selectable Sims outside the active household (none in normal play) are not covered.
- Cars' own agent updates (11.4 ms/s unseen "other"): not throttled; `CarBrain`'s behaviour A dt handling not read (§8.7).

## 8. Phase 2 (ts3native 0.36.15, counters v80; ts3fixes `unseencars`; offline, not run)

Rodney, 2026-10-21: unseen Sims are clearly the big cut; build the solution. All parts reuse phase 1's classifier (probe class, EA's byte, household positions from ts3fixes, `UtViewerWhy`), stagger (`(pass + owner id) % Every == 0`, so every part of one Sim runs on the same pass and a removed `+0x918` wait is not handed from one taker to the next) and "first call after a hold" rule. Each part is installed by its own ini key under `[UnseenThrottle]` (`Enabled=1` plus `List`, `Avoid`, `Footprint`, `Rate`), has its own runtime switch, and its mode starts at 1 when installed.

### 8.1 Phase-1 fix

The 0.36.13 freeze and its fix (the path reference `0x8469F0` releases) are §6.6, ts3native 0.36.14, ledger §211; phase 2 builds on that build. Per the crash rule it counts as fixed after two clean runs of the `bp-ut2-list` pair (or master\'s phase-1 A/B on `.0314`).

### 8.2 Avoidance (`Avoid=1`, `utAvMode` +0x3424, `--ab unseenavoid`)

**Site.** `.rdata` slot `0x1026E10` (behaviour vtable `0x1026E04` + 0xC) holds `ObstacleAvoidance 0x847390`, thiscall(behaviour; float dt), `ret 4` [verified]; refused unless it holds that pointer. A pointer swap, no code bytes; `[AvoidRO]` and S2 sit below it.

**What a skipped tick leaves [verified effects, harmlessness inferred, I-212-2/3].** Nothing reads a result. The routine writes the agent part's avoidance record (`0x85D760` twice, `0x85D980` with the agent radius), then, when the route rest is under `max(15 m, 2 x radius)`, gathers the goal footprints and calls `RouteFollow_A` (the `+0x918` key `0x846D21`) and on a hit posts the agent's `+0x64` "goal obstructed" notice. Skipped: the record stays as last written and the notice comes up to Every-1 ticks (~0.1 s) later.

**Rule** (`UtAvWhy`): runs for mode 0, no agent context, a behaviour that is not the updating agent's own (`[behaviour+0xC] != agent+8`), visible, EA's byte, state not 1 (paddleboats), near a viewer / no fresh household table, the first call after any of these, the stagger turn, and **within `AvoidNearM` (3 m) of the goal**. Unlike phase 1 the 3-15 m band is throttled: that is where the waits are (the query runs only inside 15 m).

### 8.3 Footprint updates (`Footprint=1`, `utFpMode` +0x3428, `--ab unseenfootprint`)

**Site.** `UpdateSimFootprint 0x833560`. One stub per address: below design U's stub when `[Discourage] DeferFootprint=1` (U's drains reach us under `+0x918` and pass through), else our own detour; the probe's footprint split then sits below the throttle, so it still counts the bodies that run.

**Rule** (`UtFpHook`). The body is remove-all plus optional re-insert from the object's current components (docs/89 §10.2, the same fact design U's collapse uses), so of equal calls only the last one's effect stays (I-212-7). For an unseen Sim (probe class, not near a viewer, not parked in the lock-key map), an **enable** call within `FpMs` (500 ms) of the object's last applied update becomes one pending update per object; later equal calls fold into it (force or-ed). It is applied when due, at the next footprint call or locomotion pass start **with no route job running** (or 2 x FpMs late regardless), straight into the layer below, with the object found again by id (gone, reused or another world = dropped, `utFpGone`). Any other call for that object (disable, other arguments, visible, parked, nested under `+0x918`, another thread) drops the pending one and runs at once: the later call decides the state. At most 64 pending; full = run.

**Effect.** Other routes see an unseen Sim's footprint at its last applied place, at most ~0.5 s (~1 m of walking) old; nothing changes near the camera or the household.

### 8.4 Whole-update rate (`Rate=1`, `utRateMode` +0x342C, `--ab unseenrate`)

**Site.** The probe's `0x83CF52` thunk: a skipped pass returns without calling `Locomotion_UpdateAgent`.

**Stepping [clamp verified, I-212-4].** Every unseen Sim has `OffScreenSimBrain` (EA's byte 0 in 100% of unseen updates, §5), whose path behaviour B (`0x847830`) clamps its dt to [1/30 s `[0x10434C4]`, 0.5 s `[0xF9A5AC]`] at entry; install refuses if those constants differ. A skipped pass adds `max(dt, 1/30)` to the agent's debt (what B would have used); the run passes `max(dt, 1/30) + debt` (exactly `dt` when nothing was skipped). A pass whose next skip could take the sum over 0.5 s runs (`UTW_CAP`); if a dt spike still would, the debt runs as its own update first (two updates that pass, each inside the clamp). Selftest: over random dt sequences no update gets more than 0.5 s and passed + owed = sum of `max(dt, 1/30)` (no time lost or added).

**Snap-back** (`UtRateWhy`; each makes this pass run with its debt): mode 0, visible (our test runs every pass, so a Sim that comes into view runs **that same pass** with what it owes; selftest), not a Sim (cars, boats), EA's byte, state not 1, a group link (`[part+0xC] > 0` and byte `[part+0x20]`, I-212-5), an agent timer with a period under 1 s (`+0xE0`, `+0xC0` 0), near a viewer / no fresh table, no path, a **new path** (the debt goes into its first step), the first pass after a hold, the stagger turn, within `RateNearM` (3 m) of the goal or `PortalM` (3 m) of a sub-path end or start. Hash slot taken by another owing agent = this one runs unthrottled; a debt older than one pass (agent left the list) is dropped (`utRateDropped`).

**What it skips.** On 3 passes of 4: EA's on-screen test, both behaviours (avoidance and B), the body, the timer, the steering obstacle list. Arrival and door events come up to 3 ticks late at most only outside the 3 m bands; inside them the Sim runs every pass.

### 8.5 Traffic cars (ts3fixes `unseencars`, tuning `kUnseenCarThrottle` false, `kUnseenCarEvery` 4)

`CarNpcWithNoDriver.LiteDriver` loops: random commercial lot, `StreetParking.GetParkingSpotForCar`; a spot over 10 m away gets a car-only plan and a drive; then the spot is freed and the task sleeps `kCarWaitingTimeRange` sim-minutes. With no free spot the game itself skips the drive. `ParkingSpotHook` (on `GetParkingSpotForCar`, only when the current script task is `LiteDriver` and the car a `CarNpcWithNoDriver`) asks the game first; when both the car and the spot are out of view and out of the household's reach, it hands the spot back and answers "none" on 3 rounds of 4 per car (`EveryN`, staggered by car id). The car stays parked for one more wait; no route, no teleport (the docs/04 traffic shortcut that stranded Sims placed cars instead of driving them). TrafficManager's spawn query and the NPC, food-truck, ice-cream and fire-engine routes are untouched. Counters 197 asks, 198 unseen car + spot, 199 turned down, 200 seen; agent `unseen_car_*`.

### 8.6 Switches, counters, checks, queue

| Part | ini (`[UnseenThrottle]`) | Switch | Counters (v80) |
|---|---|---|---|
| phase 1 obstacle list | `List=1` (default 1 with `Enabled=1`) | `unseenthrottle` (`utMode` +0x33C8) | `ut*` (v79) |
| avoidance | `Avoid=1`, `AvoidNearM=3` | `unseenavoid` (+0x3424) | `utAvCalls`, `utAvSkipped`, `utAvWhy[18]` |
| footprint | `Footprint=1`, `FpMs=500` | `unseenfootprint` (+0x3428) | `utFpCalls`, `utFpSkipped`, `utFpCoalesced`, `utFpFlushed`, `utFpGone`, `utFpWhy[12]`, `utFpFlushUs`, `utFpPendMax` |
| rate | `Rate=1`, `RateNearM=3` | `unseenrate` (+0x342C) | `utRateCalls`, `utRateSkipped`, `utRateWhy[18]`, `utRateCatchUp`, `utRateDebtMaxUs`, `utRateDropped` |
| cars | ts3fixes tuning | `unseencars` (patches) | ts3fixes 197-200 |

`ut2Ready` (+0x3430): bit 0 avoidance slot, 1 footprint layer, 2 below design U, 3 rate. `utWhy` rows 13-17 (rate and avoidance only): not a Sim, short timer, new path, step cap, slot taken. `unseen-report.ts` prints one line per installed part.

Checks: `build.cmd` (ts3native 0.36.15) `selftest ok` with `TestUnseenThrottle2` (rate stagger, time conservation under and over the 1/30 clamp, 0.5 s cap and spike split, visible switch same pass, every snap-back row, dropped debt; avoidance turn, 3 m, visible, linked, no agent; footprint hold-back, fold, flush when due, disable / other argument / visible / nested drop it, gone, other world) and the phase-1 test with a releasing `0x8469F0` fake; `node tools/selftest/unseen/run.ts` PASS; `node tools/harness/unseen-report.ts --self-check` ok; `build-fixes.ts`, `build-agent.ts` build; `hook-sites.ts --check`: no new gap (docs/68 row).

**Queue** `tools/harness/queues/unseen-throttle-p2-ab.json` (19 jobs, not run; asi `game-copy/ts3native.asi.0315` = this branch's `out/ts3native.asi`, ts3fixes and agent from the same commit): `bp-ut2-{list,avoid,footprint,rate,cars}` and `-onfirst` (BP 10 min, one part installed, `--ab` that part; the cars pair with repro `unseen-traffic`: new agent command `spawn_traffic` places 20 driverless cars out of view as TrafficManager does), `bp-ut2-crowd` / `-onfirst` (`rare-big-crowd`, 8 min, all parts, `--ab` all five), `ip-ut2-all` / `-onfirst` (Isla Paradiso 10 min, all parts), `bp-ut2-stuck-off` / `-on` (15 min with repro `unseen-crowd-far`: 20 spawned Sims walk unseen to the farthest lot and back; nothing vs everything, `fixesOn: unseencars`). Run the `list` pair first: it is the 0.36.13 freeze's fix check. Timing only on a clean night.

### 8.7 Expected effect (BP, from the measured unseen costs; not additive)

| Part | Measured unseen cost | Expected cut | Why the range |
|---|---|---|---|
| rate | Sim updates 56.6 ms/s (incl. steering 12.6 + avoidance 5.1 waits inside them) | ~15-35 ms/s | skipped share after snap-backs ~0.5-0.7 of unseen Sim updates; probe overhead in the base; a removed wait partly moves to the next taker (m = 0.25-1, §6.3) |
| footprint | bodies 10.4 + key `0x833593` waits 7.1 | ~4-12 ms/s | the share of an unseen Sim's calls that come within 500 ms of its last one (not measured: `utFpSkipped / utFpCalls` answers it) |
| cars | car-only plans 15.3 | ~3-10 ms/s | LiteDriver's share of car-only plans and the unseen-to-unseen share (I-212-8) |
| avoidance | avoidance waits 5.1 + its CPU inside the updates | ~2-6 ms/s | waits only inside 15 m of a goal; 3-15 m band throttled |
| phase 1 list | steering waits 12.6 | ~3-9 ms/s | §6.3 with u ~ 0.85 |

Rate contains most of phase 1 and avoidance (a skipped update runs neither), so with every part on the expected total is **~25-45 ms/s** of the ~127 ms/s unseen share, plus the separate cars and footprint terms where they do not overlap. Not covered: unseen cars' own agent updates (11.4 ms/s; `CarBrain`'s step handling not read) and the other plans (10.1 ms/s; the existing trip shortcuts and docs/141 work).

### 8.8 Risks

- **Rate, behavioural:** bigger steps for unseen Sims outside the 3 m bands; behaviour B's path integration with a 0.13-0.17 s step is inferred to follow the polyline like EA's own hitch steps (I-212-4). Late door events outside `PortalM`, late goal notices outside `RateNearM`. The stuck pair and the crowd job guard it. `agent+0xE0` timers with periods >= 1 s fire up to one step late.
- **Rate, the group-link test** is an inference (I-212-5); a missed link makes a carried or following agent step every 4th pass while its partner steps every pass (both unseen).
- **Footprint:** unseen Sims' footprints up to 0.5 s stale for other route plans (`BlockedByPeople`); a destroyed object with a pending enable leaves its footprint at the last applied place (as an object destroyed without a disable does today).
- **Cars:** fewer ambient cars drive between unseen spots; a drive from an unseen spot to an unseen spot can still cross the camera's view (those drives now happen 4 times less often).
- **Avoidance:** a goal obstruction inside 3-15 m noticed up to 3 ticks later (I-212-3).
- **Phase 1** stays unproven in game until the `list` pair passes twice (I-211-1).

### 8.9 In-game snap-back check (`unseen_snapback`, counters `utSnap` / `utSnapMaxMs`)

Rodney asked for proof that a Sim returns to full-rate routing the moment it is seen.

- **Counters (v80, +0x352C / +0x353C):** per part (0 list, 1 avoidance, 2 footprint, 3 rate) the snap-backs caused by becoming visible (a call that runs because the agent is visible after that part's previous call for it was skipped) and the largest owed time at one, in ms (rate: the owed step; list / avoidance: time since that part last ran; footprint: time since the last applied update). `unseen-report.ts` prints them.
- **Watch (viewer channel, codes 6-11):** the agent arms one object id; ts3native records that Sim's first update after its class flips unseen to visible: flipped, ran that same pass, owed time, whether avoidance and footprint were called and ran after the flip, and any skip after it.
- **Agent command `unseen_snapback {count}`** (5-12, a game task): picks walking Sims unseen by the throttle's rule (with a 20 m margin), and for each arms the watch, moves the in-game camera onto it (CameraController only, never the game window), samples its position every tick until the flip, reads the record, then moves the camera back and waits until the next pick is out of view. `{result: true}` returns per Sim: flip tick, `ran_same_pass`, `owed_ms`, `avoid_called` / `avoid_ran`, `footprint_called` / `footprint_ran`, `skips_after`, positions before and after, `jump_m` over the flip tick, its normal per-tick step, `jump_bound_m` = step + 4 m/s x owed + 0.25 m, `pass`. 2026-11-02 (a Stray Dog failed: 0.623 m jump over a 0.25 m bound, because the median step was taken after the flip, ~0 for a throttled Sim): the normal step is now the median over 8-tick windows of 32 ticks before the camera moves (whole Rate cycles, `step_median_m`; the old one stays as `step_median_post_m`), the bound adds [UnseenAnim]'s owed pose time at the Sim's first visible pose call (`anim_owed_ms`, viewer channel code 24, ts3native 0.36.154): step + 4 m/s x (`owed_ms` + `anim_owed_ms`) + 0.25 m; rows carry `species` (pets stay in the pick); the result has `summary` ("wanted 6, passed 6, failed 1, ..."), also the require-step error text.
- **Repro `unseen-snapback`** (10 spawned Sims sent to the farthest lot so unseen walkers exist, then the check for 6 Sims) and queue job **`bp-ut2-snapback`** (first after the `list` pair; all parts on, 8 min). Pass: at least 5 Sims, each `pass` true (flipped, ran the same pass, no skip after, jump within the bound), `utSnap_3` > 0 with `utSnapMaxMs_3` <= 500, no stuck Sims.
- Selftest: the visible switch counts one rate snap-back with its owed ms and the watch reports flipped / ran / owed; an avoidance skip followed by a visible call and a footprint pending dropped by a visible call each count one.

**First live run (2026-10-21 `bp-ut2-snapback`, 0.36.15):** the queue said PASS but only 2 of 6 Sims passed the check: 3 flipped, ran that same pass and stayed within the jump bound, yet `skips_after` was set; 1 (Polly Maloney) never flipped. Cause [verified in the code, the camera part inferred]: every skip path needs the Sim classed **unseen** at that moment (rate skips only with class 1; the obstacle list and avoidance only with `t_unCls` 2; footprint holds only with `UnObjClass` 1), so a part cannot skip a Sim while it is classed visible; the old watch had **one** skip counter that also counted skips after the Sim was classed unseen again, and the old check **stopped moving the camera at the flip**, so in the 10 ticks after it the Sim could drop out of view (owed_ms 0 in all six: Rate held no time at the flip; the later skips were new unseen stretches). Polly's position jumped ~190 m in the window (a trip shortcut or arrival), so her agent never had an unseen->visible update to flip on. Fix: the watch splits skips while visible (code 10, the real failure) from skips after being classed unseen again (code 16) and counts unseen updates after the flip (code 17); the check keeps the camera on the Sim every tick through the whole window, retries a no-flip Sim once with the camera closer, records the camera target distance at the flip and its maximum, gives each Sim a verdict (pass / fail / not tested) and keeps picking Sims until `count` passed (at most 3 x count tried). `unseen_snapback {result, require}` throws unless done, 0 failed and >= count passed, so the repro step and the queue job now **fail** when the check fails (job `bp-ut2-snapback`, 12 min). Selftest: a Sim that flips and then goes unseen counts only unseen-again skips.

### 8.10 Vehicles, boats, riders and swimming (Rodney: can a throttled Sim end up in the ocean off a vehicle?)

**Who follows the route during a ride [verified script].** `GetInVehicleRouteAction` puts the Sim into the vehicle (`PutInDriver` / `PutInPassenger`) and inserts a `VehicleFollowSubPathRouteAction` whose routing object is the **vehicle** (cars, bicycles, the hover train leg); `GetInBoatRouteAction` sets the route's follower to the **boat** (`mOriginalRoute.Follower = boat.Proxy`) and `BoatFollowSubPathRouteAction` hands it back to the Sim at the end. So during a ride the moving steering agent is the vehicle's, and the Sim is parented to it.

**Agent state by steering type [verified disassembly 0x954E60..0x954F86]:** the agent's state `+0x94` comes from the owner's steering-type string: `Sim`, `GameSim`, `AnimatedBoat` -> 1; `Car` -> 2; `Bicycle` -> 3; `Paddleboat` -> 4; `Rail` -> 5 (0x953D70 restores it from a save). An **AnimatedBoat** (speedboats, sailboats, water scooters, the water taxi, houseboats are boat objects [inferred: which IP objects use the AnimatedBoat type was not read from their resources]) therefore has **state 1 like a walking Sim**, but its owner has no Sim component (kind 1).

**World position of a rider [verified 0x958EE0]:** the transform component's `+0x30` is its parent; the position read composes the parent's transform, so the classifier sees a rider where it really is, and `+0x30 != 0` marks a parented object (in a vehicle, on a horse, carried).

**Before 0.36.15's vehicle holds:** rate already ran every non-Sim agent every pass (`UTW_KIND`), but phase 1 (obstacle list) and avoidance tested only state 1, so an unseen **AnimatedBoat** could have its obstacle list and avoidance skipped on 3 passes of 4 (no state written; obstacles noticed later; the passengers stay parented, so no one could be dropped). Now, with no new v80 field (the reason arrays are full; another branch's v81 sits right after v80):

| Part | Vehicle (owner not a Sim) | Rider (owner parented) | Counted in |
|---|---|---|---|
| rate | runs every pass | runs every pass | `utRateWhy_13` (not a Sim) / `utRateWhy_5` (linked) |
| avoidance | runs every pass | runs every pass | `utAvWhy_13` / `utAvWhy_5` |
| obstacle list (phase 1) | runs every pass | runs every pass | `utWhy_4` (state row) / `utWhy_5` (linked row) |
| footprint | runs (`UTF_KIND`) | runs, new `UTF_RIDER` | `utFpWhy_4` / `utFpWhy_11` (the array's spare row) |

**Verdict per case:**
- Car, bicycle, paddleboat, rail / hover train, AnimatedBoat (water taxi, speedboat, sailboat, houseboat): the vehicle's agent is never throttled by any part [verified: state / kind tests, selftest]; the Sim inside is parented and moves with it [verified].
- Sim inside a vehicle or on a horse (parented): never throttled [verified: transform `+0x30`, selftest].
- **Ridden horse (Rodney: full rate):** the horse is a Sim (kind 0, state 1, not parented), so natively it looks like a walker; its rider is parented and moves with it [verified]. ts3fixes `UnseenViewers.cs` now pushes, with the household positions every 5 ticks, the object ids of every horse in `BeingRiddenPosture` and its `Rider`, and both sides of a running `Sim.Mount` / `Sim.Dismount` / `Sim.BeingDismounted` (viewer channel codes 12 BEGIN, 13 id low, 14 id high, 15 END with the count; at most 32; older than 2 s = empty). ts3native holds those like a parented rider in every part (rows: rate / avoidance / list 5 linked, footprint 11 rider). Live check: repro `unseen-horses` (agent `unseen_horse_ride` makes horses and queues the Mounts), job `bp-ut2-horses`.
- **Swimming** (ocean / pond): the swimmer is a walking Sim (state 1) and can be throttled; the swim legs are sub-paths, so getting into and out of the water are sub-path ends (`PortalM`, every pass) [inferred from the route actions `StartRoutingOnPondRouteAction`, `GoFromPondOrOceanWalkToStandingRouteAction`]; mid-water steps are summed like on land. No vehicle to fall off.

**Live check:** repro `unseen-boats` (Isla Paradiso: 10 spawned Sims cross water to the farthest lot, swim 10 m into the sea, return; camera on the active lot) with the new read-only agent command `unseen_water_check` (per spawned Sim: water depth under it, parent, routing, interaction, `stranded` = deeper than 0.5 m, no vehicle, not routing, not swimming or boating). Queue jobs `ip-ut2-boats` right after `bp-ut2-snapback`, then `bp-ut2-horses` (repro `unseen-horses`, Pets); pass: `stranded` 0 at both checks, no stuck Sims, the hold counters above counted. Ledger §212 rows I-212-10..13.

### 8.11 Crash: `ip-ut2-boats` at `remove_sims` (0.36.15 `.0315`, 2026-10-21 07:02:27 UTC)

**What happened.** IP, all parts on, repro `unseen-boats`, 10.1 min in: EA xcpt type BREAKPOINT at `0x58D996`, call stack `0x58D996` <- `0x847503` (the return of ObstacleAvoidance's `call 0x85D980` at `0x8474FE`) <- ts3native (`UtAvHook`) <- the body update `0x839AE1` <- locomotion pass. Free virtual memory 1677 MB. The crash second is the repro's `remove_sims` step (07:02:27 in both the repro log and the xcpt name); the check one step earlier had `in_vehicle` 1 (a spawned Sim sitting in a vehicle).

**Fault [verified].** `0x58D990..0x58D996` (`push ecx; mov byte [esp+3], 0xA5; int3`) is EA's **pure-call handler**: it is registered at `0x58DC2A` through `_set_purecall_handler` (IAT `0xF954A4`), next to `_set_invalid_parameter_handler`, `_set_abort_behavior` and `signal(SIGABRT)`. So a virtual call reached a pure virtual: an object whose destructor already reset its vtable. The first code address on the stack after the handler is `0x85F1AA`, the return of `call [vtable+0x18]` at `0x85F1A8` in `0x85D980`, on the object `0x839CA0(part, 0)` returns: the agent part's **first group-linked agent**, called only when `[part+0xC] > 0` (`0x85F183`); the result is compared with 2 (state Car) [verified disassembly; the frame link inferred from the stack scan, no dump].

**Cause [inferred, strong].** A spawned Sim in a vehicle was disposed in place by our agent command `remove_sims` (`SimDescription.Dispose`), so the vehicle/rider group link of the agent being updated still pointed at the destroyed agent; ObstacleAvoidance's `0x85D980` then called into it. Nothing in the throttle writes or clears group links; the agent was in our `UtAvHook` only as a pass-through (its avoidance ran: a skip returns before `0x847390`). The same dispose would reach the same call in an unmodified game whenever that avoidance runs [inferred]. Not excluded without a dump: that a throttled update of the linked partner made it miss an unlink EA does lazily in its own update.

**Fixes (0.36.15 rebuilt, same layout).** (1) Harness: `remove_sims` no longer disposes a Sim that is parented (vehicle, boat, horse) or routing: it resets it (`SetObjectToReset`, the game's own way out of a vehicle) and disposes it from a task 30 ticks later (`deferred` in the answer). (2) Native, defence in depth: avoidance and the obstacle list now hold an agent with a group link (`[part+0xC] > 0` and byte `[part+0x20]`, the test `0x85D980` makes before the call that faulted), as the rate part always did, so a linked agent's update is never skipped by any part. Selftest: a group-linked agent's avoidance runs (`utAvWhy_5`). Ledger I-212-16. Proof: `ip-ut2-boats` twice clean on the rebuilt `.0315` (crash rule).

### 8.12 Stuck pair (`bp-ut2-stuck-off` 20 vs `-on` 37, 2026-10-21): noise of the stuck check, not the throttle

Same save, seed and repro (`unseen-crowd-far`), 15 min each; `-on` ran the rebuilt `.0315`, `-off` the earlier one.

- **Same pace, clean machine:** mean 195.8 vs 198.4 it/s (quarters 46/130/299/308 vs 38/140/308/308), machine load ok on both (other CPU 2.4% / 1.9%).
- **What the lists are:** `-stuck.json` holds every Sim that ever matched the rule (same spot and interaction for 6 checks = 3 min, or routing without moving for 3), not Sims stuck at the end. On: 20 "Work Until End of Shift" inside a work building, 15 venue activities (performances, dancing, reading, watching, swings, an instrument), 1 visit, 1 routing. Off: 3 work shifts, 17 venue activities. 27 of the 37 (on) and 7 of the 20 (off) later moved on to another interaction in the activity log (shift ended, went home); the rest at the end are performances, dances and reading, the same kinds in both.
- **Why more on the on side:** which day- or night-shift workers sit inside a building during some 3-minute window depends on where each run's Sims were when the shift ran (the runs diverge in who works when: e.g. Diana Jones worked 3 checks then went home in `-off`, 9 checks then went home in `-on`). Inside a work building or at a venue activity a Sim has no route and no walking update, so none of the four throttle parts touches it.
- **The one routing entry** (`-on` only): Rafael Striker, "Visit Banzai Lounge", routing for 3 checks; at the next check he was ordering a drink at the lounge (arrived).
- **Fix (harness):** `tools/harness/stuck-rule.ts` counts work shifts and long venue activities as standing still by design when the Sim is not routing (as sleep or frozen already were). Re-read with the new rule: off 1 (Romeo Rake, library visit), on 2 (the same Romeo Rake plus Rafael Striker, who arrived), within the docs/TESTING.md rule (at most one more). Ledger I-212-17.

### 8.13 Making the live checks exercise their case (2026-10-21)

- **Rate holds time only at small steps [measured].** In both snap-back runs Rate skipped 0.03-0.1% of the updates it looked at and `owed_ms` was 0 for every Sim: the step cap ran on 153,480 of 777,768 updates, i.e. a pass carried more than 0.25 s of game time (fast game speed), so one more skip could pass behaviour B's 0.5 s clamp. At speed 1 a pass is ~0.04 s and Rate skips 3 of 4. So **Rate saves time only at normal speed**; at faster speeds it runs nearly every pass (no gain, no risk).
- **Snap-back check:** the repro now runs at normal speed; ts3native's watch reports the watched agent's current Rate debt (code 18, watch state only, no counter change); the check arms each candidate for 12 ticks and skips Sims without debt (at most 4 x count skips), and the require step also needs at least one pass with `owed_ms` > 0 (`passed_with_rate_debt`).
- **Boats:** `crowd_go {to: island}` (new) goes to the community lot whose straight line from the active Sim crosses the most sea (>= 100 m), so the leg needs a boat, a water taxi or a swim; the new agent command `unseen_water_watch` samples every 15 ticks and its require step fails the job as "not exercised" unless >= 3 unseen Sims were at once in a vehicle or in water (and none stranded).
- **Horses:** the same watch's require step needs a rider mounted on its horse (parent = horse) while unseen at least once.

### 8.14 Route time rises with every part on: diagnostics (ts3native 0.36.18, counters v83, offline, not run)

**Measured (clean A/B, 0.36.15 `.0315`, both run orders).** With every part on, locomotion drops only ~2.5 ms/s but sim-thread route time rises (IP +15-21 ms/s); route count unchanged (~7/s), route fails slightly lower. By hitch entry: `Route_Replan` > 100 ms ~40 on vs ~16 off; `Route_ReplanFromPosition` ~128 on vs ~108 off, ~54 vs ~46 ms each; `Route_Plan` and `PlanFromPoint` unchanged. Footprint alone and Rate alone: no route change. Per-part BP pairs (list, avoid, cars) still running.

**Finding: the sim's `+0x918` wait is conserved per part [measured, 2026-10-22 per-part BP pairs].** `wait-ab.ts`, mean of both run orders, summed keyed sim waits on/off in ms/s: list 77.7 / 77.2 (steering S2 key -2.6, but gate 12 +1.9, destroy +1.5, `0x8334CC` +1.7), avoidance 77.1 / 73.1, rate 78.6 / 76.9, footprint 77.5 / 70.9 (`UpdateSimFootprint` key up, 7.8 vs 6.8), crowd (all parts) 56.7 / 54.8. Worker job time (`asyncUs`) ~115 ms per wall second, flat across slices (`bp-ut2-list-offfirst`: 133 / 114 / 115 / 116). Locomotion CPU saved per part <= 2.6 ms/s. The cars pair is invalid (the `unseen-traffic` repro jumps it/s from ~50 to ~300 after slice 1 in both orders).

So throttling removes acquirers, but the next acquirer in the same tick waits out the worker's hold: the sim's wait is bound by the route job's `+0x918` hold time, not by how many sim-thread takers there are (the §6.3 factor m is ~0, not 0.25-1). Static side: `Route_PlanLocked 0x82C6E0` takes `+0x918` at `0x82C733` [verified bytes, ts3native.cpp M1Pending note]; that a job holds it for its whole search and that the sim acquires it several times per tick (steering, avoidance, footprint, slot and gate ICs, docs/81, docs/89, docs/90) are [inferred] from those docs, not re-read here.

**Levers that do cut the wait** (shorter or lent hold, not fewer takers): P5b ReleaseLock and P5c FreeLock (the job lends or drops `+0x918` during its search; `p5b.cpp`, `p5c.cpp`), design H2 (no queued job starts during the locomotion pass, docs/86 §5, `[LocoWindow]`), and S2 / the read-only clones (`[SteerRO]`, `[AvoidRO]`, `[SlotEnabledRO]`: the taker reads under QL shared instead of `+0x918`). The unseen throttle's gain is its CPU (<= 2.6 ms/s per part) plus whatever the levers above free.

**Secondary: the replan rise.** Avoidance raises `Route_Replan` hitches in both orders; the counters below test why. Hypotheses (all inferred):

| # | Hypothesis | Mechanism |
|---|---|---|
| H1 | notice delay | a skipped obstacle list (`0x9573B7`) posts no `+0x60` obstacle notice that tick, a skipped avoidance no `+0x64` goal-obstructed notice (docs/82); the Sim walks on up to Every-1 ticks into a worse spot and the script replans from there, which costs more |
| H2 | cars | `unseencars` keeps unseen traffic cars parked longer: different street and parking state for the Sims' replans |
| H3 | idle pacing | Rate's summed steps (0.13-0.17 s for an unseen Sim) move a Sim further per update; it reaches a blocked spot or a stale route section between checks and replans |
| H4 | stale footprint | footprint hold-back leaves an unseen Sim's footprint up to 0.5 s old, so *other* Sims' plans route through or around the wrong place and replan later |

The measured "footprint alone" and "rate alone" A/Bs showed no route change, which already weighs against H3 and H4 alone (not against their interaction with the list and avoidance parts).

**Counters (v83, `+0x3568..+0x3603`, on with `[UnseenThrottle] Enabled=1`, observe only, no switch).**
- Per object (the agent's owner `+4`, or the footprint object), a 512-slot table remembers when each part last skipped it (`at[part]`, `GetTickCount`), the pass of its last skip, and whether its last obstacle-list run returned a non-empty vector (`begin != end`, the test at `0x9573C7` before the `+0x60` call [verified disassembly]).
- `utDefer[3]` (+0x356C): list / avoidance / rate skips while that object's last list was non-empty, i.e. a `+0x60` notice the skipped tick would likely have posted again (estimate: the list can turn empty in between). `utListNotices` (+0x3578): list runs inside unseen updates that posted the notice (the base for `utDefer`).
- Detours on `IC_Route_Replan 0x765490` and `IC_Route_ReplanFromPosition 0x765550` (the prologues `[ProbeP10]` / A10 use; skipped when either owns them, `utDgReady` bits 0/1): each sim-thread call is timed whole (what stutter-profile's `byEntry` times) and split by the route's follower (route `+0x40/+0x44`, found by id as `UnPlanClass`): class 0 = some part skipped it within the last 8 passes, 1 = other follower, 2 = none found. `utRpN[2][3]` (+0x357C), `utRpUs[2][3]` (+0x3594), `utRpLong[2][3]` (+0x35AC, calls >= 100 ms).
- For class 0: `utRpPart[2][4]` (+0x35C4) the part that skipped the follower last (0 list, 1 avoidance, 2 rate, 3 footprint) and `utRpPartMs[2][4]` (+0x35E4) the summed ms from that skip to the replan (mean = age). The cars part lives in ts3fixes (slots 197-200) and has no per-Sim attribution here.
- The follower object found by id and the steering agent's owner are the same object [inferred]; class 0 staying 0 in on slices while `utSkipped` > 0 would mean that identity is wrong, not that H1 is.

**How each counter confirms or refutes each hypothesis** (per slice from `<tag>-ab-native.jsonl`; off slices have no skips, so class 0 empties 8 passes into each off slice):

| Reading (on slices) | H1 notice delay | H2 cars | H3 idle pacing | H4 stale footprint |
|---|---|---|---|---|
| the extra `utRpUs` / `utRpLong` sit in class 0 (follower throttled lately), mean us per call class 0 > class 1 | supports | refutes as main cause | supports | refutes (H4 hits other Sims' replans: class 1) |
| the extra sits in class 1 | refutes | supports | refutes | supports |
| class 0 `utRpPart` mostly list / avoidance, mean age short (< ~0.5 s, a few passes) | confirms | - | weakens | - |
| class 0 `utRpPart` mostly rate | weakens (rate also skips the list: read with `utDefer_2`) | - | supports | - |
| `utDefer` a large share of the skips (vs `utListNotices`) | needed for H1 (notices really deferred) | - | - | - |
| `utDefer` near 0 | refutes (nothing to defer) | - | - | - |
| class 0 share of calls ~ the throttled share of walking Sims and class 0 cost = class 1 | refutes (replans not caused or worsened by skips) | possible | refutes | possible |

H2 vs H4 then come from the per-part pairs: the cars pair (route time with `unseencars` alone) and the footprint pair (already measured: no change).

**Checks:** `TestUnseenDiag` (split by class, last part wins with its age, the 8-pass window, deferred notices count list / avoidance / rate but not footprint, other-thread calls pass through uncounted) and the phase-1 test's notice count (`utListNotices`, `utDefer_0`); `unseen-report.ts` prints the lines (`--self-check`). Queue `tools/harness/queues/unseen-diag-ab.json` (not run): `ip-ut2-diag` (IP) and `bp-ut2-diag-crowd` (`rare-big-crowd`), 6 min, 1.2-min slices, every part `--ab` (warm-up + off,on,on,off; off,on,off,on is not supported by profile-world.ts), asi `game-copy/ts3native.asi.0316diag`. No fix is built until the cause is shown. Ledger §215.

### 8.15 Why the throttle gains ~0: results (2026-10-22, clean night, ts3native 0.36.15 / 0.36.18)

All numbers are measured, as pair means of an off-first and an on-first job (on minus off), so the slow middle slices of every run cancel. Single-order jobs (off, on, on, off) are not usable here: the middle slices are slow with nothing switching (BP crowd single-order deltas -33% and +39% for the same switch).

| Test (queue) | it/s | locomotion ms/s | summed `+0x918` wait ms/s | Replan + ReplanFromPosition ms/s |
|---|---|---|---|---|
| List only, BP (`clean-night-unseen-p2` list pair) | +2.5 it/s (~+5%) | -2.6 | +0.5 | route -8.8 |
| List + Footprint + Rate vs all off, IP run 1 / run 2 / BP crowd (`unseen-noavoid-ab` + twins) | -1.3% / +0.8% / +2.9% | -2.1 / -11.0 / +5.8 | +7.1 / -9.0 / +12.9 | +18.3 / +4.5 / -0.8 |
| Avoid alone, other parts on in both halves, IP / BP crowd (`unseen-avoid-iso` + twins) | -10.8% / -10.6% | +10.7 / +14.1 | +10.3 / +13.5 | -17.9 / +7.7 |

Findings:
1. **The wait the design expected to remove is conserved.** Per part, the sim thread's summed keyed `+0x918` wait stays the same (list 77.7 vs 77.2 ms/s: steering -2.6, other keys up by the same amount; §8.14). The route worker's job time (`asyncUs` ~115 ms/s) bounds it; a skipped acquirer hands the wait to the next acquirer in the same tick (m ~ 0 in §6.3). The §8.7 estimate of ~25-45 ms/s assumed these waits were removable.
2. **The CPU left to save is small.** EA already runs off-screen Sims on `OffScreenSimBrain` + `SimpleBody`; the parts save at most a few ms/s of locomotion, within the per-run noise.
3. **The avoidance skip costs more than it saves** (both orders negative in IP): more locomotion work and more lock waits. Mechanism (inferred): late avoidance lets unseen Sims run into each other, so their steering and replans work harder.
4. The release default is already the safe set: `release-features unseenthrottle` turns on `Enabled=1` with `List=1`, `Avoid=0`, `Footprint=0`, `Rate=0`.

What would cut the wait is a shorter or lent `+0x918` hold (P5b `ReleaseLock`, P5c `FreeLock`, design H2 docs/86, S2), not fewer acquirers. The `unseen-traffic` cars pair is not usable (it/s jumps from ~50 to ~300 after the first slice in both orders).

### 8.16 Water entry cause check: throttle glitch or the Sim's own intent (2026-10-22, offline, not run)

Rodney asked for a stranding check that tells a throttle glitch from a Sim's own intent, not only "is the Sim over water".

**Agent command `unseen_water_cause`** (ts3agent `WaterCauseHandlers.cs`, read only). A game task samples every spawn_sims Sim every 5 ticks. A **water entry** is a Sim that stood on something at the last sample (land, beach, dock, houseboat deck, or parented to a vehicle) and is now in water: not parented, `y <= sea + 0.3 m`, more than 0.5 m of water under it; only entries while the Sim is out of view count (100 m from the camera and its target, not on screen, as `unseen_water_watch`). Per entry it records:
- support before: `vehicle:<type> #id` (parent), `houseboat:<lot> #boat` (`Lot.IsHouseboatLot`), `dock:<lot>` (above deep water, not a houseboat), `beach:` / `land:`;
- interaction / posture before and after (type names);
- the last water intent and its age: a current or queued interaction whose type name has Swim, Dive, Snorkel, Scuba, Jump, Fish, Boat, Mermaid, Ocean, Pond, Water or Taxi, or a route action of that kind in the routing component's action list (`GetCurrentRouteAction` / `GetRouteActionAfter` / `GetPreviousRouteAction`: `StartRoutingOnPondRouteAction`, `GoFromPondOrOceanWalkToStandingRouteAction`, `GetInBoatRouteAction`, ... [verified script: public accessors on `RoutingComponent`]). The route's sub-path list itself is not read (only native; the route actions are the script-side copy of the water sub-paths [inferred, §8.10]);
- distance to the support's edge (0.25 m steps to 3 m, 8 directions): houseboat = off its lot (`World.GetLotLocation`); dock = no terrain (bridges included) or level-0 height above the sea. shortcut: that `GetLevelHeight` sees IP dock tiles is not verified; if every dock row reads 0.25 m it does not, and the dock edge needs another test;
- support movement over the last 3 s (houseboat object or vehicle position; land and docks do not move);
- ts3native's per-Sim throttle record over the viewer channel (new codes, no counter change, v83 layout kept): 19 / 20 object id halves (finds the Sim's `g_utDg` slot by the id at owner `+8/+0xC`, as the snap-back watch), 21 newest skip of any part `<= lo` ms ago (the agent bisects it: `skip_age_ms`), 22 that skip's part (`skip_part`: list, avoid, rate, footprint), 23 any part switched on (the `--ab` side). Selftest `TestUnseenDiag` step 7.

**Classes:** `intended` = a water intent within 20 s before the entry or at it; `support moved` = not intended, the support moved > 0.5 m in the 3 s before, the Sim was within 1 m of its edge at the last sample, and a throttle part skipped the Sim within 500 ms; `unexplained` = the rest. `{result: true}` gives per side (off / on / unknown) the three counts and the exposure (unseen Sim-samples), and up to 10 sample rows per class.

**A/B rule** (`{result, require, ab: true}`): ts3native answering, at least `min_entries` (3) entries, no entry with an unknown side, `support_moved` on == 0, and `unexplained` on <= `unexplained` off x (exposure on / exposure off) + 1. The exposure ratio removes the warm-up slice (always on) and uneven activity per side. Without `--ab` (all parts on, `ab: false`): `support_moved` == 0 and `unexplained` <= `max_unexplained` (0).

**Houseboat move feasibility [verified script, game not run]:** the helm's "Cruise Here on Autopilot" interaction (`Helm`, string `CruiseHereOnAutoPilot`) only calls `Actor.LotCurrent.GetHouseboat().RequestAutopilotToLocation(hit point)`, a public method that queues an autopilot route with **no driver** (`RouteToRadialRange(null, point)`: `LeavePort`, `WaitForSafeToRoute`, a radial-range plan with `DisablePlanningOnLand`, `DoRoute`); `RequestAutopilotReturnToPort` docks it again (`ReturnToPort(null)`). The `HasAutoPilotUpgrade` test sits only in `AutopilotAllowed` (the UI), not on that path. So the game moves a houseboat with Sims aboard and script can do it. Sims on the deck stand on the houseboat **lot**, which the game parents to the boat (`World.ParentLotToHouseboat`); script converts between world and deck positions with `World.GetWorldToHouseboatDisplayPosition` / `GetHouseboatDisplayToWorldPosition`, so deck Sims' positions may be in the lot's own frame and not move in world space while the boat sails [inferred from the API names and `GoBelowDeck`'s use]. Then the throttle cannot strand a deck Sim by boat motion at all; the repro checks the outcome either way (lot membership, not world motion).

**Repros and queue** (`tools/harness/queues/unseen-water-cause.json`, day correctness, noisy machine OK, asi `game-copy/ts3native.asi.0322` = the 0.36.22 merge build that includes it):
- `ip-water-cause-ab` (repro `unseen-water-cause`, 10 min, `--ab unseenthrottle,unseenavoid,unseenfootprint,unseenrate` together, 2-min slices): 12 spawned Sims at normal speed (Rate skips only there, §8.13) cross the sea, swim 10 m out, go home, swim again; the A/B rule above.
- `ip-houseboat-move` (repro `unseen-houseboat-move`, 9 min, all parts on): agent `houseboat_move {setup: "deck"}` picks a houseboat with a lot (a household's first), places 6 spawned Sims with `Houseboat.PlaceSimAtEntrance` and keeps them walking between the entrance and the lot centre, moves the camera to the farthest community lot; `{move}` autopilots 80 m out, `{return}` back to port; `{check, require}` = every Sim on the houseboat lot and not in water, boat moved >= 20 m; then `unseen_water_cause {ab: false}`.
- `ip-dock-edge` (repro `unseen-dock-edge`, 10 min): `{setup: "dock"}` needs a houseboat docked at a port gangway; 6 Sims walk 2-8 m along the gangway's forward line while the boat leaves, returns and leaves again; pass = no dock Sim in water, boat moved, cause check clean.
If the save has no houseboat, both houseboat jobs fail at setup with "no houseboat": not a throttle result; pick a save with a houseboat household. Ledger §224.

### 8.17 Houseboat move and dock edge results: the check, not the throttle (2026-10-22, measured on a noisy machine; fixed check offline, not run)

Runs (asi `game-copy/ts3native.asi.0322`, save Baseline-IP-Spring, houseboat "Sea Spirit - 1br, 2ba", household The Romantic, boat moved 75-77 m out in every run). Logs `probe-out/queue/unseen-water-cause-2026-10-22-ip-*.log` and `unseen-houseboat-control-2026-10-22-*.log`, rows in `probe-out/profile/<tag>-repro.jsonl`:

| job | throttle | final check: Sims "off the deck or in water" | mid check at sea (rows) |
|---|---|---|---|
| ip-houseboat-move (05:44 UTC) | on (all parts) | 4 | 5 of 5 on the deck, none in water |
| ip-houseboat-move-off (06:43) | off | 2 | 4 of 6 on the deck; Marquis Clements on Port Pescado (y -0.32 m vs sea, `GoHere`), Elvis Libby on no lot (+0.53 m, `GoHere`); none in water |
| ip-houseboat-move-on2 (07:01) | on | 3 | 2 of 6 on the deck; Gabriel Libby on Holiday House (+28.2 m, `GoHere`), Clayton Herring on The Spanish Queen (+23.6 m, `GoHere`), Latoya Hickey on Casita Azulita (+1.7 m, `GoHere`), Aja Grogan on no lot (+0.2 m, `GoHome`); none in water |
| ip-dock-edge (05:53) | on | 5 ("off the dock or in water", water only) | none (no mid check) |
| ip-dock-edge-off (06:51) | off | 5 | none |

The final `require` checks threw with the count only, so no per-Sim rows exist for them, and the repro stopped there: `unseen_water_cause {result}` never ran in any of the five, so the cause classes are empty.

**Cause [verified from the mid-check rows, dock part inferred].**
- Not one of the 17 mid-check rows was in water (`in_water` false for all). Every Sim off the deck was dry, on another lot or on none, and doing `GoHere` (the repro's own walk task) or `GoHome` (the spawned Sim's own household). Two sat 24-28 m above the sea on hill lots, which only a route gets them to.
- The walk task sent `GoHere` to two **world** points taken at setup: the entrance and the houseboat lot's centre, both at the berth. Once the boat sails, neither point is on the deck, so our own Go Here routes the Sim off the houseboat towards the old berth (the off run's Sim on Port Pescado at -0.32 m is next to it). That is the repro, not the game, and it happens with the throttle off as well (final: off 2, on 4 and 3; mid check off the deck: off 2, on 0 and 4).
- Dock: the pacing points ran 2-8 m along the gangway's forward line, which points at the boat's berth [inferred: 5 of 6 counted in water on both sides, no rows]. With the boat away those points are open water, so Go Here sends the Sims swimming. The old dock check counted any Sim in water, intent or not.
- The old check also counted Sims that were never on the deck at move start (Ginny Corley's object was gone by the first mid check) and Sims that left on their own interaction.

**Does the game strand deck Sims on a move?** Not shown: no Sim in any row was in water, and every off-deck Sim carried a route interaction. The game's own move with Sims aboard keeps them on the lot when nothing sends them elsewhere (run 1 mid check: 5 of 5 on the deck while the boat was 75 m out, throttle on).

**Verdict for the throttle: cleared of these five failures** (same counts with it off, cause = the repro's walk targets and check definition). The stranding question itself (Rodney: can a throttled deck or dock walker end up in the water?) stays open until the fixed check runs: queue `tools/harness/queues/unseen-water-cause.json` jobs `ip-houseboat-move` and `ip-dock-edge`, on and off.

**Fixed check (ts3agent `houseboat_move`, built, not run):**
- Walk targets: deck = the entrance and the centre kept as offsets from the houseboat lot's **live** centre, and a target is used only while `World.GetLotLocation` puts it on the houseboat lot (shortcut: the offset ignores the boat's turn, the lot test drops a point that falls off); dock = the first and last placed Sims' own spots (where `FindGoodLocationNearby` put them on the dock), never a point that reads as water.
- The first `{move}` records which Sims stand on the support and are dry (`on_support_at_move`; deck = on the houseboat lot). Only those are watched.
- The walk task (every ~8 s) records, per watched Sim, the first water intent (`WcIntent`: swim/boat/... interaction or route action) and whether the Sim was in water at a sample.
- `{check, require}` fails only for a watched Sim that is in water now or was at a sample, with no water intent since the move. Sims that walked off dry are counted in `left_deck_dry`, not failed. It also needs boat moved >= 20 m and >= 1 watched Sim ("not exercised" otherwise). Rows now carry x/z, `wet_since_move` and `water_intent`, and the require error names each failing Sim with lot and height. Ledger §249.
