# 194. Routing out of process: the P5/P6 clone and its A\* jobs in a helper process

**Parked 2026-10-25: P6 stopped, see docs/189 §17.** All P5/P6 switches ship off; P6 no longer gates v0.2.0.

Written 2026-10-25. **Static design study only:** no game run, no native build, no harness edit. Decision being designed (Rodney, 2026-10-25): after the P5/P6 clone is slimmed for its own gains (docs/66 §7 S1-S3, separate agent), move the clone and the clone-run A\* jobs **out of the 32-bit game process** into a helper, to give back the largest-free-block cost of routesnap. EA's own routing data stays in the game: the sim thread reads it every tick.

Inputs: docs/66 §7 (clone memory cost), docs/116 (go/no-go), docs/94 §12 (P4 redirect), §14/§16/§21 (P3 images, P3-B replay clone, provider), §17 (non-routing live reads N1-N12), §19 + §22 (P5 door answers, mod guard, answer move), docs/182 (replay records off the lock), docs/119 + PLAN M1.1 (monohost maps TS3W.exe as a library), docs/123 (world image restore at the same addresses), docs/159 (host-call wait), docs/105 (portal answers), docs/157 (P5 failure modes), docs/189 (P6 whole-problem plan), docs/102 / docs/131 (indirect calls), docs/187 + `docs/data/apex-hooks.csv` (Apex sites).

Tags (AGENTS.md): **[verified]** = read in our code, the disassembly-backed docs or a measurement, with the source named; **[measured]** = run data, source named; **[inferred]** = reasoning still to confirm, claim id I-194-n; **[design]** = a choice made here. Effort per the CLAUDE.md rule: wall-clock hours for Claude with agents; game runs and clean nights listed apart.

## 0. Summary

- **Verdict: feasible, worth it only after slimming and only if the dynamic live reads close (gate G0).** Architecture: a **32-bit helper `routehost` that maps TS3W.exe at `0x400000` as a library** (the monohost loader, M1.1 GO [verified, PLAN "Mono M1.1 go/no-go"]), holds the replay clone, applies the P5 record stream from a shared-memory ring and runs the game's own `Route_PlanLocked 0x82C6E0` on the clone. The game keeps EA's routing world, the recorder, the cut, the IC body and goal step, and gets back a serialized answer that it rebuilds into `route+0xE0` with the game's allocator. A 64-bit helper with our own planner port is rejected (§2.2: 1,322-function closure, bit-equal answers, ~300+ h).
- **MB regained** [inferred, I-194-1]: the helper takes the clone heap, the private pools (and their 96 MB reserve), the cut images and the rebuild peaks; the game keeps a ~6-10 MB shared view (ring with payloads, job/result slots) plus code. Against today's unslimmed routesnap: **~100-105 of the 112 MB at load, ~110-195 of the 204 MB (BP) after 10-16 min** (the low end if the unexplained in-play high-water of docs/66 §7 is not ours). Against a slimmed in-process clone (S1+S2+S3, ~95-125 MB back): **~+70-100 MB more**. Nothing measured yet; phase 3 measures it with `/memory` as docs/66 §7 did.
- **Door answers [decided, Rodney 2026-10-25]:** native in the helper (docs/105 evaluator = docs/159 H4, door state as DOOR records in the ring from the built door channel, per-plan Sim profile and S-tier answers in the job slot, H2 memo); IPC to the sim only for eight fallback cases F1-F8 (§3.3). Expected native share ~95-99% of asks on EA-only installs, much lower with NRaas GoHere or on installs failing the method-integrity guard [inferred, measured first in phase 0].
- **Latency** [inferred, I-194-2]: per job +30-150 us (two cross-process hand-offs, args out, answer back, answer rebuild) against an IP job span of ~10-16 ms (docs/189 §3.6 [measured]): +0.3-1.5%. Native door answers remove most of the 9.3 / 18.2 asks per job and their 0.40-0.52 ms mean wait (docs/159 [measured]); a fallback ask costs that wait +2-40 us. **Where it can lose a lot: the dynamic non-routing reads** (physics loose quadtree N8, objects by id N7, terrain LOD shapes N9, docs/94 §17.2): today read live in-process for free; out of process each is a remote query or a page fetch. Unknown count per plan: gate G0.
- **Failure handling:** P5 pattern unchanged at its core: every refusal or fault answers the request with the game's own plan (`PlanLockedBody`). Adds helper crash (process handle signalled), hang (heartbeat + per-job deadline), out-of-sync (ring sequence gaps, EPOCH), version handshake, and a restart that rebuilds the clone lot by lot (needs S3) instead of one world-wide copy under a sim stall.
- **Effort** [design]: **~60-95 h** in six phases (§7; the native door evaluator adds ~14-20 h), plus **~11-17 game runs** (door share and door shadow 3-6, 2 capture, 2 shadow, 4-6 take/fault/memory, 1 soak) and **2 clean nights**. Phase 1 is offline (monohost-style host on recorded streams).
- **Top 3 risks** (§8): (1) dynamic live reads from the helper cost more IPC than the job saves (N7/N8/N9); (2) native hooks inside the closure that the helper does not have (S3SS, S3PP, Apex, our own gate stubs) make helper answers differ from the game's; (3) AV heuristics for a helper that maps an EXE image, shares sections with the game and is spawned by an injected DLL.

## 1. What runs where today (in-process P5/P6) and what moves

| Part | Today (in TS3W.exe) | Out of process | Tag |
|---|---|---|---|
| EA routing world (lot table, quadtrees, R, G, W, footprints, portals) | live, sim + jobs | **stays** (sim reads it every tick) | [design] |
| Recorder: P1 writer leaves, `g_spWrites`, replay ring records INS / REM / NOQ / STALE / SYNC / LOTS / EPOCH | sim (`snap.cpp`, `replay.cpp`), ring 65,536 x 24 B | **stays**; ring moves into a shared section, payloads move into the ring | [verified source names, docs/182 §2.1, docs/66 §7] |
| Replay consumer (applies records to the clone) | "snap replay" thread, `RecsUnlocked` off-lock since 0.36.34 | **moves** (helper thread) | [verified docs/182] |
| Replay clone `g_rpClone` (lots, Qs, trees, map M, footprint copies) | own `HeapCreate` heap, `rpKB` 20-33 MB, peak 42-113 | **moves** | [measured docs/66 §7] |
| Private pools (tree, leaf, map nodes) | 96 MB contiguous reserve, 20-31 MB committed | **moves** | [measured docs/66 §7] |
| P3 cut images / lot copies (`spKB`) and rebases | sim copies under `+0x918`, 7-25 MB | **copy stays on the sim** (needs the live lock), **storage moves**: the copy is written into the ring as a payload | [design] |
| Provider (`PcAcquire`, W' private world planner, copy's G trees) | worker | **moves** | [verified docs/94 §21.13] |
| Job START, IC body, goal step (`Route_PlanCommon`), `IcCollect` | sim + in-game worker | **stays** (reads live lot planner state under `+0x918`, docs/87 §10) | [verified docs/94 §12.4.1] |
| Clone run `Route_PlanLocked` on the clone | in-game worker, lock lent (P5b/P5c) | **moves**; the in-game worker waits on the helper instead | [design] |
| Answer move into `route+0xE0` | node relink (~0.1 ms, docs/189 §3.2) | **becomes answer rebuild** from a serialized answer | [design] |
| Door asks (event 59) | `0x64F4D3` stub -> `0x791C00` -> `HostCallHook` -> sim | **native in the helper** (docs/105 evaluator, DOOR records, per-plan prologue); in-game proxy -> sim only for fallback cases F1-F8 | [decided, §3.3] |
| Verify jobs (`VerifyEvery`), shadow compare (`P4Classify`) | in-game | compare stays in-game (game plan is in-game) | [design] |

The split boundary stays **`Route_PlanLocked`**, docs/93's and P4's compare boundary: everything above it reads live state and stays; the clone run below it moves.

## 2. Architecture

### 2.1 Chosen: 32-bit `routehost` with TS3W.exe as a library [design]

- **Loader.** Reuse the monohost loader: map a clean copy of TS3W.exe at `0x400000` (`MEM_IMAGE`), snap imports itself (24 DLLs / 664 functions in monohost), redirect EA's allocator entry points to the helper's own heap [verified: monohost does this for eglib + `GeneralAllocator`, PLAN M1.1]. The exe has no relocations, so `0x400000` is mandatory [verified, `tools/emu/emu.py` header]. Mono is **not** started: routing never needs it (no managed exit inside the closure except event 59 at `0x64F4D3` and the two script-host slots of docs/102 §1).
- **Why same base is the key property.** The clone is a game-layout copy: footprint copies, lot graphs and Q objects carry vtable pointers into TS3W `.rdata` and reach constants in `.data` (mask table `0x11CF180`, docs/182 §2.1). With the exe at its own base in the helper every such pointer is valid unchanged [inferred, I-194-3: no clone object holds a pointer into ts3native.asi; checked for vtables only]. The P3-B clone code (`replay.cpp`, `p4clone.cpp`, `pools.cpp`) can then run in the helper with a different I/O layer, not rewritten.
- **Option G's redirect becomes free.** T1-T4 (A\* stamp `0x11580C4`, clip scratch `0x1158580`, world root `0x11D9128`, docs/94 §12.2) exist because the clone run shares globals with live plans. In the helper every global is private by construction; T4 becomes one write of the clone world into the helper's own `0x11D9128` [design]. The warm-up guards #11-#27 (docs/94 §12.3.2) are copied once from the game's `.data` at world load (§3.4).
- **Our own stubs.** The helper loads a helper build of the ts3native parts the clone run needs (pools, provider, replay consumer, the `0x64F4D3` door stub, D4 lazy-build tripwire, fault guard), not the gates, QL, steering, probes or the recorder. The §12.4.3 pass-through rule becomes "not installed" [design].
- **Any other exit traps.** An indirect call into code that is not in the helper (a mod DLL, `0xD75FA0` other than the door path, `World_FireEvent`) faults on an unmapped address or a guard page; the existing `P4Guarded` handler turns it into a refusal (docs/157 rows 1, 12) [design]. docs/102 §1: only `ScriptHost_InvokeDelegate 0xD75FA0` and `0xD7D2F0` reach managed code among the resolved targets; 31 unresolved non-lifetime search sites remain [verified docs/102].

### 2.2 Alternatives weighed

| Option | For | Against | Verdict |
|---|---|---|---|
| A. 32-bit helper, TS3W.exe as a library (§2.1) | game's own bytes, so G4 compare classes and P4/P5 tests carry over; vtables valid; monohost loader exists and works [verified M1.1]; helper address space is not the constraint (~150-300 MB) | must mirror every live read the clone run makes (§4); AV profile (§6.4); mods' native patches absent (§5) | **chosen** |
| B. 64-bit helper with our own planner port | unlimited address space, no exe mapping, x64 speed | the closure is 1,322 functions, 927 indirect sites, 332 unresolved [verified docs/102 §1]; docs/98/99 specify the in-room A\* and world graph only; answers must be bit-equal for the G4 classes (docs/94 §13.2); ~300+ h [inferred]; every EA quirk (tie rules docs/94 §13.1, I-94-322 unset-stack read) re-found by hand | **rejected**; reconsider only if A fails G0 |
| C. 64-bit helper running the 32-bit code under emulation (`tools/emu` Unicorn) | exact bytes | ~50-100x slower per instruction [inferred]; a 10 ms plan becomes 0.5-1 s | rejected |
| D. Per-job world image (docs/123 E: dump private memory, restore at the same addresses) | zero mirroring logic | ~1.8-2.1 GB per image, seconds of game suspension [verified docs/123 §Summary] | rejected for jobs; **kept for the offline proof** (phase 1 fixtures) |
| E. Stay in-process, slim only (docs/66 §7 S1-S3) | ~95-125 MB back for ~5-10 h [inferred there] | the remaining ~80-110 MB stays | **do first** (decided); this note is the step after |

### 2.3 Processes, threads and shared memory [design]

| Object | Owner | Size in the game's address space |
|---|---|---|
| Section `ring` (records + payloads, single producer = sim / recorder threads under the ring spin lock, single consumer = helper) | game creates, helper maps | 4 MB (8 MB if phase 0 shows lot-copy bursts > 2 MB) |
| Section `jobs` (16 job slots: args in, answer out, door ask mailbox, state words) | game | 1 MB (64 KB per slot; answers larger than a slot fall back to the game plan, counted) |
| Section `ctl` (versions, heartbeat, applied sequence, per-lot sequence table, switch words, `SnLive`-style window flag for D2) | game | 64 KB |
| Section `stage` (load-time copies of static world data, §3.4) | game, mapped only during the copy, then unmapped | 0 after load (transient 16 MB window) |
| Helper threads: consumer, job runner(s), watchdog | helper | 0 |

Signalling: spin-then-wait on shared words, with one named auto-reset event per direction as the sleep path (spin ~20-50 us first). Cross-process Dekker on shared memory works with interlocked operations like in-process (same physical pages) [inferred, I-194-4; standard x86 TSO + `lock` prefixes].

## 3. Sync: the P5 record stream over the ring

### 3.1 Records [verified kinds, docs/182 §2; design for the transport]

| Record | Today | In the ring |
|---|---|---|
| INS (footprint insert, ~90% of consumer time, docs/182 §1) | 24-B record + footprint copy made on the sim (1-5 us, docs/94 §16) in the replay heap | 24-B header + the footprint copy inline (position-independent: polygon arrays as offsets, live addresses kept only as table keys) |
| REM, NOQ, STALE, SYNC | 24-B records | unchanged |
| LOTS (lot shells / untaken lots) | batch, `LotsUnlocked` | header + lot copy payload (worst 0.6 / 1.2 MB BP / IP, `snLotCopyMaxKB` [measured docs/66 §7]) |
| EPOCH (new clone after a world change) | the sim builds a new clone and posts it | header only; the helper builds the new clone from the stage copy + LOTS stream (§6.2) |
| R bit 7 (blocked node), high-cost cells, grid add/remove | P1 records, R image copies (P6EarlyCopy "copy when not own-only", docs/189 §1 table row 4) | small records; R itself is in the helper from the stage copy |
| new: CUT (job START cut sequence) | `g_spWrites` count in the job | header with the sequence number and the per-lot sequence vector the job needs (docs/189 phase 2 staleness) |

Rate: ~418-564 records/s mean (docs/94 §10/§16 [measured P1/P1b]); with payloads ~50-300 KB/s plus lot copies [inferred]. A 4 MB ring holds >10 s of steady traffic. Overflow = the producer marks `g_rpNeedEpoch` as today; the helper rebuilds (§6.2).

### 3.2 Job protocol [design]

1. **START (sim, unchanged):** `P5OnStart` arms; the cut becomes a CUT record (sequence `s`).
2. **In-game worker** runs the IC body and `Route_PlanCommon` (goal step, live) as today. At the `Route_PlanLocked` detour (`P4LivePlan`) it serializes `args` (starts, goals, agent id, options object copy, `guidLo/Hi`; the §12.3.1 capture list [verified docs/94]) into the job slot, publishes `J_SUBMIT`, lends `+0x918` (P5c window as today) and waits on the slot.
3. **Helper job runner:** waits until its applied sequence >= `s` (deadline: `RingLagMs`, default 20 ms; miss = refusal `lag`), checks per-lot staleness, `PcAcquire` on its clone, runs `Route_PlanLocked` on the clone world into a private list, then serializes the answer: per path the §12.3.1 `live` field list (status `+0x94`, `PathType +0xB0`, object id `+0xB8`, `PortalStartPos`, nodes with xyz/`+0x20`/`+0x24`/flags `+0x2C`, length `+0x8C`, blocker records `+0xD0..+0xD8`), `out1`/`out2`, return value. This is exactly what `P4CaptureAnswerRaw` already reads for the §13 compare [verified docs/94 §22.1].
4. **In-game worker** retakes `+0x918` (as today's final retake), runs the post-run stale check (`P5cStale` on the shared sequence), then **rebuilds** the paths with the game's own constructors (path ctor `0x8204B0`, node emit, list append) on the EA heap and links them into `route+0xE0`. Rest of the IC tail as today.
5. Any refusal or fault on either side: the worker runs the game's plan (`PlanLockedBody`) under the retaken lock, exactly as P5's fallbacks (docs/157).

**Why a rebuild, not a pointer move:** the route later frees its paths through the game's allocator; nodes allocated in the helper cannot be freed in the game. I-94-66 [verified docs/94 §17.5]: a clone-run path holds only scalars, its own node and blocker vectors, sibling links and its refcount, so a value rebuild is complete for every field `Route_PlanLocked` writes. Open: the "undecoded 64-byte" subpath record and the rest of the 0x190-byte object written by the ctor and the PlanCommon tail [inferred, I-194-5]; gate = byte-equal answer JSON in verify jobs (phase 3).

### 3.3 Door answers (event 59): native in the helper, IPC to the sim only as fallback [decided, Rodney 2026-10-25]

**Decision.** Door / portal-lock answers move into the helper with the clone. The helper's `0x64F4D3` stub answers each `World_FirePortalLockedQuery 0x791C00` ask natively with the docs/105 evaluator (docs/159 H4) from a door mirror, a lot mirror and a per-plan Sim profile, behind an in-helper H2 memo. A proxied event 59 call to the sim thread is used **only** for the portals the evaluator returns "ask" for (list below). This replaces docs/94 §19.1's "(b) first, (a) only if door waits exceed half of job wall time" rule for the out-of-process design.

**Why** [measured inputs, docs/159 §Summary]: host-call wait BP 18.8 of 73.5 ms/s held (25.6%), IP 11.1 of 64 (17.4%); 9.3 / 18.2 asks per job; mean 0.52 / 0.40 ms per ask, ~19-23 us of it service and **~62-81% BP / ~48-73% IP queue delay** until the sim reaches a serve point. Out of process every proxied ask would add two cross-process hand-offs on top. The brief's "door asks ~48% of the wait behind P6 jobs" matches the low end of docs/159's IP queue-delay share; no docs/189 table gives 48% as a share of the sim's wait [not found, flag, Q1]. Native answers remove the queue delay, the hops and the 121-151 sim dispatches/s (docs/115 §5; sim gain ~0.5-2 ms/s [inferred, docs/94 §19.1]).

**Parts and where they run** [design on docs/105 §4, existing builds named]:

| Part | Runs on | Source | State |
|---|---|---|---|
| Door mirror (per script-relevant portal object: kind, lock type, lock owner + household, owner list, one-way room, barn/gate flags, runtime type) | built by ts3fixes on the sim, sent over the **docs/94 §14 door channel** (`DoorChannel`/`DoorMirror.SyncRoute`, native `spDoor` table in `snap.cpp`) | docs/105 §4.1, §10.5 | **built 2026-10-18, offline only** [verified docs/105 §10.5] |
| Door state sync to the helper | ts3native forwards every door-channel message (lot begin/end, door record, invalidation) as a **DOOR record in the ring**, in order with the routing records, so the helper's mirror is at the job's CUT sequence | §3.1 | new [design] |
| Lot mirror (hidden rooms, apartment flag, lot type, handler-list identity, live privacy situations) | ts3fixes on the sim (lot load/unload, `PrivacySituation.RegisterCallbacks`/`CleanUp` patches) -> DOOR/LOT records | docs/105 §4.2 | not built |
| Plan prologue + Sim profile (tier P, ~30 field reads) and S-tier answers for the named lots (privacy locked-room sets, turnstiles, VIPRoom doors, apartment `CanTravelInElevator`) | ts3fixes body patch of `ScriptCore.Route::Plan`, `Replan`, `PlanFromPoint`, `ReplanFromPosition`, on the sim before the IC; result copied into the **job slot** with the args (§3.2) | docs/105 §4.3, §4.4 | not built |
| Evaluator (decisions 1-11 in `Sim.OnPortalPathPlan` order, then the privacy set) | helper, at `0x64F4D3`, inside the clone run | docs/105 §3, §4.5 | not built |
| `LockedDoorsDuringPlan` | helper returns the (door, locked) pairs with the answer; the in-game worker adds them at collect on the sim (docs/93 E3) | docs/105 §4.5 | new transport [design] |
| H2 memo | helper, per job, keyed by the 9 argument dwords; cleared per job (evaluator answers depend on the Sim and clock, so no cross-job memo for evaluated answers; a cross-job memo only for proxied answers, docs/189 `DoorMemoX` rules) | docs/159 §4.1 | [design] |
| Line of sight (decision 12) | stays on the sim (goal building is above `Route_PlanLocked`) | docs/105 §6 | unchanged |

**Fallback: when the helper asks the sim** (proxy over the job slot's ask mailbox; the in-game worker calls `0x791C00` through the existing `HostCallHook`/`ForCaller` path; +2-40 us per ask on top of today's 0.40-0.52 ms [inferred, I-194-2]):

| # | Case | Detected by | Scope of the fallback | Expected share of asks |
|---|---|---|---|---|
| F1 | MonoPatcher present, or the event 59 delegate is not EA's `LotManager.PortalLockedCallback` (docs/104 L2, P1) | ts3native at load / world load (L2 signatures, docs/107 §2.2); ts3fixes reads the delegate | session: every ask proxied | 0 on vanilla; 100% on such installs [inferred] |
| F2 | Method integrity fails: an `[TypePatch]` or replaced assembly changes one of the ~550 S-closure methods (docs/104 L3 with the docs/94 §19.2 amendments: declaring class, `invcache` and `hoursmemo` whitelisted) | ts3fixes at world load (IL hash of the S closure) | session | depends on mods; see note on L1 below |
| F3 | Lot handler list is not exactly {`Sim.OnPortalPathPlan`, `PrivacySituation.OnPortalPathPlan` x n} (GoHere's lot handler, other mods' handlers; docs/104 P2) | prologue compares the handler-list reference per named lot (docs/105 §4.2, I-105-6); DOOR/LOT record carries the identity for other lots | that lot's portals | ~0 on vanilla; **up to 100% with NRaas GoHere** if its handler is on every lot it manages [inferred, I-194-13; docs/184 GoHere] |
| F4 | Portal object of a non-EA runtime type (scripted CC / mod doors; docs/104 O1 census) | the door mirror stores the runtime type and MVID; evaluator sees a non-EA type | that portal | small: share of scripted door CC among doors on touched lots [not measured] |
| F5 | Sim-side types not EA (Sim, routing component, posture from a mod; docs/104 O3-O5) | prologue | that plan | ~0 on vanilla [inferred] |
| F6 | Lot not named by the prologue (not start, destination or start-point lot) whose mirror lists a live privacy situation or an S-tier object (turnstile, VIP door, box stall, horse jump on a lot the prologue did not evaluate) | evaluator: lot mirror bits | that portal | low: plans touch mostly named lots; privacy situations are short and room-bound [inferred, docs/105 §4.4] |
| F7 | Door mirror invalid for the lot at the job's CUT (bin / library lot import, save-load reflection restore without an event, I-105-14; lot invalidated and not rebuilt because the prologue did not name it) | DOOR record "invalidated" without a later "lot end" before CUT | that lot's portals | rare: lot imports and loads, then the next prologue naming the lot rebuilds it [inferred, docs/105 §10.3] |
| F8 | Evaluator gap: a decision the evaluator cannot reproduce (a new EA rule found by the shadow compare; `IsOpenVenue` at a clock edge inside the job) | shadow mode `dnMismatch` (see gate) per decision id; clock edge = venue hour within the job window | that decision kind (evaluator returns "ask" for it until fixed) | 0 by gate |

**L1 (EA image GUIDs) is not a trip in this design** [design]: it would turn the evaluator off for the whole session on installs that ship a replaced `ScriptCore.dll` (NRaas ErrorTrap) or replaced SGS/SGO (simler90, on Rodney's set, docs/128) even when the S closure is unchanged. F2's method hash covers what L1 protected; L1 stays a log line (M2-3). Whether simler90's and ErrorTrap's replaced assemblies keep the S-closure IL unchanged is open [inferred, I-194-14; docs/128 says simler90's autonomy closures are unchanged, not its door closure].

**Expected native share** [inferred, I-194-15, to be measured]: vanilla or EA-only installs ~95-99% of asks answered natively (F6/F7 residue); installs with GoHere: whatever share of asks lands on lots without GoHere's handler (could be ~0%); installs failing F1/F2: 0%. Measure before building the evaluator: phase 0 adds a shadow count on today's in-process path at `0x64F4D3` of what the evaluator **would** do per ask (`dnNative`, `dnAsk[F1..F8]`), from the door channel and a handler-list readout, on Rodney's set and a vanilla set.

**Gate (shadow first)** [design]: the evaluator runs in shadow next to the proxied answers (the docs/105 I-105-12 shadow patch) until `dnMismatch` = 0 over >= 20,000 asks per world on BP and IP, including a GoHere save (memory: NRaas GoHere save to-do) and a privacy-heavy lot; any mismatch puts that decision kind on F8 "ask".

**Cost** [docs/94 §19.1 figures]: evaluator 2-3 h, prologue + Sim profile + channel codes 1-1.5 h, S-tier answers ~1 h, shadow patch 1 h, guard (F1-F5) 5-8 h, mod probes ~2 h: **~12-17 h + 2-4 live jobs**, plus ~2-3 h for DOOR records in the ring and the `LockedDoorsDuringPlan` transport.

### 3.4 What the clone run reads that the record stream does not carry

From docs/94 §17.2 (N1-N12), §12.3.2 (statics), docs/91-115 inputs (docs/116 §1.1), docs/102 / docs/131:

| Class | Reads | Today | Out of process [design] | Risk |
|---|---|---|---|---|
| Static after load | N1 world bounds, sea level; N10 blend layer 0; R (`world+0x54`, only bit 7 moves, docs/116 §1.1 [verified]), G water grid, high-cost maps, `blockedNodeMap`; statics #11-#27, mask table | live, unguarded | **stage copy at world load, restored at the same addresses** in the helper (docs/123 technique: helper reserves those VAs before its own allocations) | address collision with the helper's own heap: refuse the epoch, counted [inferred, I-194-6] |
| Static while the sim runs | N5 floor heights, N6 room partitions (0 writes in 10 min BP/IP [verified docs/108 §7 U8]) | live | stage copy; build/buy exit and `RebuildLotRouting 0x835110` already rebase / EPOCH | low |
| Rare writers | N2 lot index, N3 lot tree, N4 Lot objects, N11 lock-key sets | live, writers drained (D2) | copy at load + LOTS records on lot add/remove/houseboat; N11 already copied with footprints (§12.8 item 1) | medium: every field the closure reads from a `Lot` must be in the copy (list from §17.2 N4) |
| **Per-tick dynamic** | **N7 `ObjectService_FindById 0x939560`** (15 sites: footprint owners, stairs, portal owners), component lookup `0x958560`; **N8 physics loose quadtree** (`Routing_PhysicsRangeQuery 0x83B3A0`, `Routing_PhysicsRayVertical 0x6583E0`); **N9 terrain height** (`Terrain_GetHeight 0xC6A540` -> cell shapes, ~70 sites, LOD swaps ~0.15/s) | live, `g_qtLock` (D3) / no lock | **open, gate G0** (§4) | **high** |
| Door answers N12 | event 59 | proxied to the sim (A10 `Proxy`) | native in the helper from DOOR records + prologue; proxy for F1-F8 (§3.3) | medium (evaluator equivalence, I-105-12) |
| Live pointers held by the clone | portal references (H20, docs/94 §21.8: the clone holds refs on **live** portals, dropped by `RpGraveClone` under `+0x918`, docs/182 §2.1); footprint copies' "owner" words (never followed by the replay [verified replay.cpp header via docs/182]) | in-process | portals must become value copies or ids in the clone; owner words stay opaque keys | medium: portal refs are followed in the search [inferred, I-194-7] |
| Unresolved indirect sites | 31 non-lifetime search sites (docs/102 §1), 29.1% unresolved in the glue v2 (docs/131) | live | in the helper an unknown target either exists at the same address (exe code) or traps -> refusal | low for correctness (trap = fallback), unknown for take rate |

## 4. The dynamic live reads (N7, N8, N9): options and gate G0

Three ways to serve them, per read family [design]:

1. **Remote query by value:** the in-game worker (idle while it waits) runs the game's function under `g_qtLock` and returns values. Works only if the caller uses values, not returned pointers it then dereferences. For `0x83B3A0` (range query from the hierarchical planner `0x654CC0`) the result is a shape list the caller walks [inferred, I-194-8]; for `0x6583E0` (vertical ray in the obstacle path `0x6593F0`) the result feeds blocker records built by value (docs/94 §17.5 [verified]) [inferred usable].
2. **Page mirror:** the helper reserves the game's heap ranges at the same VAs and fills a page on first touch (vectored handler + a read of that page from the game, or a push by the in-game worker); pages of dynamic families are dropped at each job start. Generic, but every read is torn unless the game side holds `g_qtLock` for the duration (a cross-process lock) and every first touch costs ~10-50 us [inferred, I-194-9]. Cross-process memory reads also raise the AV profile (§6.4).
3. **Mirror by records:** physics shape add/remove/move as ring records (the #1 writer stubs of D3 already see every one [verified docs/94 §17.4 D3]); terrain cell shape swaps (`vfSwaps`) as records. Rate of physics moves = every moving object per tick [not measured]; may exceed the ring's budget.

**G0 (phase 0, offline + existing counters):** for each family, calls per clone run and distinct live pages touched per clone run. Tool: the K6/K8 harness in `steerro-test.cpp` already runs the planner on the clone with live data on no-access pages [verified docs/94 §13.3]: extend it to count (not fail) touches per family. Pass rule: remote queries <= ~20 per job (<= ~0.4 ms at 20 us) and no family that needs option 2. Fail: stop at phase 0 and stay with slimming (option E).

## 5. Mod guard out of process

- **Managed mods:** door answers are native (§3.3), so the managed door chain is guarded by F1-F5 there (delegate identity, S-closure method integrity, per-lot handler list, door and Sim runtime types); a failing check proxies that session, lot, portal or plan to the game's own chain, so modded behaviour is kept, only slower. docs/128/132 mod scans are about Mono offload, not routing [verified scope].
- **Native hooks inside the clone closure:** in-process, a foreign hook on a closure site turns `Mode=2` off (M2-1, first 5 bytes of up to 160 sites compared at every armed START [verified docs/94 §22.1]). In the helper the closure runs **clean EA bytes**, so a game whose closure is patched by a mod (or by our own `[RouteGate]`, `rtsites`, D1 stubs) can get answers the game would not give. Rule [design]: at helper start and every world load the game sends the first 16 bytes of every closure function the helper may execute (the M2-1 list extended to the whole `Route_PlanLocked` closure, ~1,322 functions, ~21 KB); any difference that is not one of our own documented sites (docs/68, `tools/hook-sites.ts`) -> out-of-process off for the session (fallback: in-process P5 if still built, else plain A10).
- **Our own in-game stubs that change answers:** D1 exceptions (`rtsites` #1/#5 take locks only, no answer change), `[HeightCache]` `0xC2A570` (a cache: must give the same height [inferred, I-194-10]). None replaces an answer [inferred from docs/94 §12.4.3].
- **Apex Radiance:** of 386 Apex sites, a grep for 12 closure addresses (`0x82C6E0`, `0x83B3A0`, `0x6583E0`, `0xC6A540`, `0xC2A570`, `0x939560`, `0x939420`, `0x958560`, `0x791C00`, `0x4E69D0`, `0x4E51B0`, `0x4E3F70`) hits only `0x4E3F70` (developer allocator categories, off by default) [verified grep of `docs/data/apex-hooks.csv`]. Apex owns lookups (ObjectLookupIndex) and the allocator (FastMemory) (docs/187); a full closure intersection is phase 0 work (`hook-sites.ts --check` against the closure list).

## 6. Address space, latency, failures, launch

### 6.1 Address-space budget

| Item | In game today (routesnap on) | In game, out of process | Moves to helper | Tag |
|---|---|---|---|---|
| Replay clone heap | 20-22 at load, 24-33 in play, peak 42-113 | 0 | yes | [measured docs/66 §7] |
| Pools | 96 MB reserve (20-31 committed) | 0 | yes | [measured] |
| Cut images | 7-13, peak 13-25 (cap 64) | 0 (payloads in the ring) | yes | [measured] / [design] |
| Ring | 1.5 | 4-8 (with payloads) | | [design] |
| Job slots + ctl | ~0 | ~1.1 | | [design] |
| Stage window | 0 | 0 after load (16 transient) | | [design] |
| Heap slack, rebuild high-water | ~13 at load + most of the 65-90 in-play excess | 0-? (the part caused by in-game rebases stays) | most | [inferred docs/66 §7] |
| Code (ts3native IPC side, helper build excluded) | | +50-100 KB | | [inferred] |
| **Largest free block, BP after 10-16 min** | **1,097 MB (-204 vs release)** | **~1,190-1,290 MB (-10..-110)** | | [inferred, I-194-1] |

Per-world numbers scale with footprint count, not lot count (~330-375 B per clone map entry, docs/66 §7 [computed there]); out of process the scaling cost stays in the helper. The helper itself: clone + pools + stage copies (R, G, statics; size unknown, phase 0 reads it) + TS3W image 14.4 MB + CRT: ~150-300 MB in its own 4 GB [inferred]; RAM is not the limit on 32 GB.

### 6.2 Latency budget

| Step | In-process today | Out of process | Tag |
|---|---|---|---|
| Submit (args copy + wake helper) | 0 | 1-3 us copy + 1-5 us spin / 10-20 us event | [inferred] |
| Wait for ring catch-up | 0 (consumer and clone share memory; staleness global) | 0 in steady state; up to a lot-copy burst (~1 ms per MB applied [inferred]) | [inferred] |
| Clone run | same CPU work | same; no EA heap critical-section contention with the sim (own allocator) | [inferred] |
| Door asks | 9.3 / 18.2 per job x 0.52 / 0.40 ms (BP 18.8 / IP 11.1 ms/s of hold) | native: ~1-5 us each in the helper [inferred]; fallback asks (F1-F8): today's wait + 2-40 us | [measured] / [inferred] |
| Prologue on the sim at START | 0 | ~30 field reads + S-tier calls on named lots, about one event 59 dispatch (~20 us) per plan, against 4.3 / 8.8 dispatches per plan removed | [inferred, docs/105 §4.3] |
| Dynamic live reads | free (in-process reads) | **G0 unknown**: 0 to several ms per job | open |
| Answer back + rebuild | node move ~0.1 ms | serialize 10-50 us + rebuild 20-100 us | [inferred, I-194-11] |
| **Per job total added** | | **+30-150 us without N7-N9 queries** on a ~10-16 ms span | [inferred] |

Sim-side effect by docs/189 §3.6's model (sim wait = r x span x jobs/s, r 0.48, 9.7 jobs/s IP [measured inputs]): +0.15-0.7 ms/s at +30-150 us per job. CPU: the helper uses a core while jobs run; spinning on both sides costs ~10-15% of one core at IP job rates [inferred]. The machine has 16 threads (CLAUDE.md).

### 6.3 Failure handling [design, on top of docs/157]

| Failure | Detection | Recovery |
|---|---|---|
| Helper crash | process handle signalled; `ctl` heartbeat stops | pending jobs: game plan; out-of-process off; restart once per session after 10 s, clone rebuilt as in the "Restart in play" row |
| Helper hang | per-job deadline (`JobDeadlineMs`, default 3x the p99 job time, min 50 ms) and heartbeat age > 2 s | job: game plan under the retaken lock (docs/157 row 11 pattern); hang: `TerminateProcess`, then as crash |
| Out of sync | ring sequence gap, consumer fault (`rpFaults`), verify-job M2/M3 (`m2VerBad`) | EPOCH rebuild; 3 bad verifies per session -> off (P5 rule) |
| Ring overflow | producer finds no room | `g_rpNeedEpoch`; jobs refused until the EPOCH is applied |
| Answer too large for a slot | helper side | refusal `big`, game plan |
| Version mismatch | handshake: protocol version, ts3native build id, helper build id, TS3W.exe PE timestamp + size (the helper maps the game's own exe path, so a match is expected) | out-of-process off for the session, log line |
| Restart in play | | **needs S3 (per-lot on-demand clone)**: the helper starts empty, each lot comes in as a rebase LOTS payload on first use (~1 ms sim copy per lot under `+0x918` [inferred from docs/94 §16]); without S3 a restart is one world-wide copy (hitch of ~0.5-1.5 s [inferred]) |
| World close / travel / save | existing drains (`rtClosing`, `rtBusy`, docs/157 rows 4-5, 15) | unchanged; helper told EPOCH/close via `ctl` |
| Game exit or crash | helper waits on the game's process handle | helper exits; Job Object with kill-on-close as a backstop |

### 6.4 Second process: anti-cheat, AV, Steam

- **Anti-cheat:** The Sims 3 is single-player with no anti-cheat [inferred, I-194-12; community-known, no source checked].
- **AV heuristics** [inferred]: patterns that raise scores: an unsigned process spawned by an injected ASI, creating a child suspended and writing into it (process hollowing shape: avoid; the helper reserves its own ranges in its first instructions, fixed base, no `WriteProcessMemory`), mapping an EXE image as data, `ReadProcessMemory` of the game (avoid: the game pushes data through sections, §2.3). Plan: no cross-process writes or reads at all in the default design; the page-mirror option (§4 option 2) only behind a switch. Code signing of the helper is a release question for Rodney.
- **Steam launch:** unchanged. ts3native (already loaded in TS3W.exe) spawns `Game\Bin\routehost.exe` on the first world load with routesnap on, in a Job Object; the helper maps the TS3W.exe the game was started from. Steam overlay and Steam DRM are not involved in a child that never calls Steam [inferred].
- **Crash reports:** `[CrashReport]` (docs/135) gains a helper section (helper alive, last job, last record sequence); a helper crash is its own signature.

## 7. Phased plan with gates

| Phase | Work | Gate | Effort | Game runs |
|---|---|---|---|---|
| 0. Measure (offline) | K6/K8 harness extended to count N7/N8/N9 calls and live pages per clone run; size of stage data (R, G, statics, lot fields); closure byte list + Apex/S3SS/S3PP intersection (`hook-sites.ts`); cross-process spin/event microbenchmark on this machine; door shadow counters `dnNative` / `dnAsk[F1..F8]` on today's in-process `0x64F4D3` path (door channel + handler-list readout, no answer change) | **G0**: dynamic reads served by remote query <= ~20 per job or by records within ring budget; IPC hop p50 <= 5 us spinning, <= 30 us by event; no foreign hook in the closure on Rodney's set; native door share measured on Rodney's set and a vanilla set | ~6-9 h | 1-2 (door share BP + IP, day jobs) |
| 0b. Door evaluator in-process, shadow | lot mirror, prologue + Sim profile, S-tier answers, evaluator, guard F1-F5, I-105-12 shadow patch; still in-process, answers compared with the proxied ones | **G0b**: `dnMismatch` = 0 over >= 20,000 asks per world (BP, IP, a GoHere save, a privacy-heavy lot) | ~12-17 h | 2-4 |
| 1. Offline proof (monohost-style) | `native/routehost` from the monohost loader; helper build of pools / replay / provider; capture build: ring + CUT + job args + game answers + door answers to files (behind a switch); routehost replays a capture and answers every job; compare with the game's answers using the docs/94 §13 classes | **G1**: >= 5,000 jobs per world BP + IP, M2 = 0, M3 within the in-process P4 baseline, 0 helper faults | ~14-22 h | 2 capture runs (BP, IP) |
| 2. Live shadow | sections, ring transport, job protocol, handshake, watchdog; game plans as A10, helper answers compared only | **G2**: compare classes equal to the phase 1 offline result; ring lag p99 < 20 ms; sim cost delta <= +1 ms/s | ~10-16 h | 2 (BP, IP correctness) |
| 3. Take answers | answer rebuild, evaluator moved into the helper (DOOR records in the ring, prologue in the job slot, `LockedDoorsDuringPlan` pairs returned), F1-F8 proxy + H2 memo, fallbacks, fault injection (kill / hang / overflow / version), in-game clone and pools switched off | **G3**: verify jobs byte-equal answer JSON; all docs/157 rows + the new §6.3 rows pass in `TestP5Fault`-style tests; largest free after 10-16 min >= release - 30 MB on BP; hitch counts (`hitch-ab.ts`) not worse than in-process P6 | ~12-20 h | 4-6 (IP correctness, fault day job, BP + IP memory, IP hitch) + 1 clean night |
| 4. Release | spawn path, installer, crash-report section, docs/PLAYERS.md line, soak | soak 3 h clean, memory flat | ~4-6 h | 1 soak + 1 clean night |
| **Total** | | | **~60-95 h** (+0-15 h if N7-N9 need records) | **~11-17 runs, 2 clean nights** |

Order with the rest: after v0.2.0 and after docs/189 phases 1-3 (A_HOLD, per-lot staleness, door asks without retakes), because those change the protocol this note serializes. Slimming S2 and S3 first (§9).

## 8. Risks

| # | Risk | Effect | Mitigation |
|---|---|---|---|
| 1 | Dynamic live reads (N7 objects, N8 physics, N9 terrain LOD) need many round trips or a cross-process lock | job span up, take rate down; gain lost | G0 before any build; fallback = stay in-process slimmed |
| 2 | Native hooks inside the closure (mods, Apex, our own stubs) absent in the helper | answers differ from the game's (M2) or the feature turns off on modded installs | byte list at world load (§5); off on mismatch; verify jobs |
| 3 | AV / SmartScreen flag the helper | players lose the feature or the game | no cross-process reads/writes by default, Job Object child, signing decision, VirusTotal check before release (Rodney) |
| 4 | Answer rebuild misses a field the clone run set (I-194-5) | wrong route object, crash later | byte-equal answer JSON in verify jobs; rebuild only the §12.3.1 fields, refuse on unknown path types |
| 5 | Same-address stage restore collides with the helper's own allocations (I-194-6) | epoch refused | helper allocates top-down and reserves the stage ranges before its heap grows |
| 6 | Live portal references in the clone (H20, I-194-7) | dangling in the helper | value copies of portals (also a slimming item, §9) |
| 7 | Restart without S3 = one world-wide copy | 0.5-1.5 s hitch | require S3 for in-play restarts; else restart only at the next world load |
| 8 | Native door evaluator differs from the game's managed chain (I-105-12), or its native share is low on real mod sets (GoHere, replaced assemblies) | visible door bugs (Sims through locked doors) or little gain from the move | shadow gate G0b (`dnMismatch` = 0), per-decision F8 "ask", share measured in phase 0 before the build |
| 9 | Two processes to support | player reports harder to read | `[UserDiag]` and `[CrashReport]` sections; one switch to turn it off |

## 9. Slimming choices that make the move easier (feedback to docs/66 §7)

- **S2 (one clone build, heap per epoch):** an epoch becomes the helper's natural rebuild unit; no in-game double peak to keep.
- **S3 (on-demand per-lot clone, idle lots freed):** turns a helper restart into per-lot rebases (no world-wide stall) and makes the ring the only way lots reach the clone. **Highest value for this note.**
- **Portal references as values or ids (new, S6):** drop the clone's refcounts on live portals (H20); saves the grave step under `+0x918` in-process too and is a hard prerequisite out of process.
- **Position-independent payloads (new, S7):** footprint copies and lot copies laid out with offsets, not pointers into the replay heap, so the same bytes can sit in the ring.
- **Per-lot sequence numbers instead of the global `g_spWrites` (docs/189 phase 2):** the helper's staleness test needs only the CUT vector.
- **S1 (pool reserve 96 -> 48 MB):** moot once the pools move; build it anyway (low cost, helps the interim release).
- **Avoid new in-process live reads in clone-run features:** each new one is an IPC item later (list them in docs/94 §17.2 when added).

## 10. Open questions and inferred claims

| Id | Claim / question | Closes it |
|---|---|---|
| I-194-1 | MB regained: ~100-105 at load, ~110-195 after 10-16 min (BP) | phase 3 memory runs |
| I-194-2 | IPC hop 2-5 us spinning / 10-40 us by event on this machine | phase 0 microbenchmark |
| I-194-3 | no clone object holds a pointer into ts3native.asi or a mod DLL other than through keys | phase 1 (helper faults on a foreign pointer) |
| I-194-4 | cross-process Dekker on section memory behaves as in-process | phase 2 selftest with two processes |
| I-194-5 | the §12.3.1 field list rebuilds the whole clone-run part of a path | phase 3 byte compare |
| I-194-6 | stage ranges can be reserved at the same VAs in the helper | phase 1 |
| I-194-7 | the search follows the clone's live portal references | read `p4clone.cpp` H20 users (~30 min) |
| I-194-8 | `0x83B3A0` callers dereference returned shapes | disassembly of `0x654CC0` (~30 min) |
| I-194-9 | page fetch cost 10-50 us | phase 0 (only if option 2 is needed) |
| I-194-10 | `[HeightCache]` gives the same heights as `0xC2A570` | its selftest |
| I-194-11 | answer serialize + rebuild 30-150 us | phase 3 counters |
| I-194-12 | no anti-cheat in TS3 | community check |
| I-194-13 | NRaas GoHere puts its own handler on the lots it manages, so F3 proxies those lots | read GoHere source (docs/184, cite only) + phase 0 handler readout |
| I-194-14 | simler90's and ErrorTrap's replaced assemblies keep the ~550 S-closure methods' IL unchanged | F2 hash over Rodney's install (offline, `managed-callgraph.ts`) |
| I-194-15 | native share ~95-99% of asks on EA-only installs | phase 0 `dnNative` / `dnAsk[F1..F8]` |
| Q1 | Answered: the ~48% is the host share of the on-arm sim wait in p6-integration rung 1 `ip-p6int-p6-ab` (2026-10-25, `p6 waits arm 1` lines, log `probe-out/queue/p6-integration-2026-10-25-ip-p6int-p6-ab-0427-ts3native.log`; rung crashed at off #29, so partial). Recheck on the ladder rerun. | closed |
| Q2 | Is the extra ~70-100 MB over slimming worth ~60-95 h on Rodney's set (Error 12 > 1 GB away, docs/66 §7)? It matters for heavy-CC players near the cap | Rodney, after slimming lands and docs/190 metrics show players' headroom |

## 2026-11-20 measurement: what routesnap holds in the v0.3.0 release config

Static + existing run data, no game run (Rodney, 2026-11-20; gate: >= ~50 MB held = build the helper). Result: **~7 MB held (peak ~13 MB), far under the gate; do not build the helper for address space.** Page: `docs/data/results/2026-11-20-routesnap-held-rc1119b.md`.

- Release ini `[RouteSnap]` `Enabled=1 Mode=0 Count=1 Lots=none`: `Enabled=1` = P3 world images only (`snap.cpp`, strategy `kSpLotNone`: no lot images, no lot audits), `Mode=0` = audit only, `Count=1` = pass-through counters. No clone and no pools: `PlInstall()` (96 MB reserve) is only called from the replay install (`replay.cpp:3816`, `Lots=replay`), so `g_rpClone`, `rpKB`, `plKB`, `plResKB` stay 0.
- Measured (rc1119b release-verify `bp-lptrim-memtravel-x3-rc1119-r1`, 10 min, Apex): `spKB` 6,623 at load, 6,626 after, `spKBMax` 13,356; `rpKB` 0, `plKB` 0, `plResKB` 0. IP (unseen-rate-ab 2026-11-19, 30 min): `spKB` 6,790, `spKBMax` 13,575. The old 112 / 204 MB cost (docs/66 §7) was `Lots=replay` P5/P6, which the release ini does not use.
- Not measured: the largest-free-block delta A vs B (`Enabled=0`). Bound: counters 6.6-13.6 MB plus unmeasured heap slack; both are well under 50 MB, so the A/B run was skipped.
