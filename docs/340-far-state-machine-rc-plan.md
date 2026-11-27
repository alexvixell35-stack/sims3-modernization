# 340. Next RC plan (v0.4.0): far-loop state machines, lazy instantiation, large test world (2026-11-26)

Plan only: no feature code. It turns Rodney's five decisions of 2026-11-26 into ordered work packages for the release
candidate after v0.3.2. It builds on docs/324 (far-Sim model, branch `feature/far-sim-model-1122`), docs/331 (far-object
model), docs/332 (88 work packages, handoff format, lanes), docs/322 (distance bands), docs/320 / 321 (large worlds, caps
sweep), docs/123 (record / replay lessons) and docs/143 / 144 (what EA already does for unseen Sims).

Claim marks: `[verified]` = read in a doc that cites disassembly, decompiled code or a measurement; `[measured]` = a game
run; `[community]`; `[inferred]` = not checked yet, and no code may depend on it before the named package confirms it.
This doc was written from the summaries of the docs above, not from a fresh REA pass: every `[verified]` here is "verified
in the cited doc", and phase R re-checks the ones the build depends on.

Effort is wall-clock time for Claude with agents (CLAUDE.md rule); game runs and Rodney's own time are listed separately.

## 0. Short answer

- **What the RC is:** far from the household, Sims and objects stop running EA's live loop. Each one is an explicit,
  seeded, replayable state machine. Household Sims, the selected Sim, anything of unknown scope and everything near them
  keep EA's vanilla loop. Coming near ("go-live") hands a valid live state back to EA through EA's own setters.
- **Lazy instantiation** comes second and is gated by a static proof (phase R, verdict package RX-9): far objects, and
  where possible far Sims, exist as records until they come near. EA scripts query objects world-wide, so the plan has
  four stages (L0 kept and inert, L1 stripped, L2 record only, L3 never loaded) and only goes as far as the proof allows.
- **Large test world:** WS-0 starts now: Rodney builds a 2048 m world in the Create a World tool with agent guidance,
  and the performance proofs run on it first. **Repo split (Rodney 2026-11-26):** this repo holds only Sims 3
  performance work. Boundless Atlas (private repo) holds all CAW patches and every Sims 3 patch that is not about
  performance: world-size caps, streaming, the world generator, the CAW test harness. Packages that moved there are
  listed here by id only (§6, column Repo).
- **Packages: 85 in this repo** in 7 phases (§6), plus 13 cross-references to Boundless Atlas. 56 keep their docs/332
  id, 29 are new. Agent effort **~54.3 h hard (Fable, Opus as fallback) + ~13.7 h mechanical (Fable first, Sonnet as
  fallback)**, game time roughly 35 h serial [inferred from docs/332 §4 plus the new runs], Rodney ~3-6 h in CAW [inferred].
- **Running now:** two REA lanes of this repo on Fable (far-Sim tick loop, lazy object instantiation; §4); the CAW
  lane runs in Boundless Atlas. **Launch next (§11):** CP-0, WS-0a, ST-1, ST-4, ST-6, plus the mechanical ME-4, ST-9. Fable goes to RE, design and the hard state-machine and
  lazy code first, and takes the mechanical packages too while the window lasts (§5).
- **Everything defaults off** until its gates pass. Hard gates on every game run: `demotedHousehold` 0,
  `parkedHousehold` 0, `strippedHousehold` 0, script errors 0, save round-trip equal to vanilla.

## 1. Goal and scope

### 1.1 In this RC

1. Far-Sim state machine (docs/324 tiers T2 / T3) built, calibrated in shadow, then live behind default-off switches,
   with a timing verdict by distance band.
2. Far-object state machine (docs/331: parked lots, held timers, computed classes, event log) to the same level.
3. A determinism proof: same seed and same inputs give the same trace, offline and in game.
4. Lazy instantiation: the REA proof (phase R), the design (SD-4), stage L1, and stage L2 only if RX-9 says go.
   Far Sims as `SimDescription` only (docs/324 T4) stays opt-in.
5. WS-0: one purpose-built test world (2048 m first) with scenario sheets, and the performance runs it unblocks
   (WS-5, FS-20, FO-16).

### 1.2 Out of this RC

- Everything that is not performance work: cap removal, region streaming, tile manager, paged water grid, the world
  generator, CAW analysis, CAW patches and the CAW test harness. All of it lives in Boundless Atlas (private).
  Growing the test world past 2048 m and the first 4096 m loads are Atlas packages too.
- Stage L3 (far objects never built at load) unless RX-4 finds a clean load hook; otherwise it waits for streaming.
- Parallelising the simulation. The far machines run on the sim thread; moving their evaluation to a worker is a later
  offload item (CLAUDE.md long-term goal) and is noted per package, not built here.
- Caps leftovers CP-5 .. CP-12: they continue under docs/332 unchanged and are not counted here.
- Any change to household Sims. `[Experimental] HouseholdToo` does not apply to anything in this plan: every site is a
  skip, park, fake or teleport class site (docs/271).

### 1.3 Hard rules carried into every package

- 32-bit exe: memory work can only waste less address space. Stage L1 / L2 are judged on private bytes and largest free
  block, not on installed RAM.
- Household untouched: household Sims and lots, the selected Sim, lots with a household Sim or its route target, the
  active and visited lot, objects a household Sim owns, uses or reserves, and anything of unknown scope are never far,
  parked, stripped or recorded. A stale household table means nothing goes far. Shared predicates `HhExempt` /
  `Fixes.HouseholdExempt` are not used to lift anything here.
- Nothing of ours inside the `.sims3`. Sidecar files only; a missing or stale sidecar must load as vanilla.
- Removing the mod leaves a save that loads vanilla (load-time cleanup ships with every state we add).
- No world-size constants: radii in metres from the ini, bounds read at use, work proportional to what is near.
- No gaps: every writer that bypasses a hook gets its own hook or the site stays live.
- No leaks: every table and index drops on destroy, resets on quit to menu, has a size counter and a queue check.
- Measure before build: each build phase starts with a probe or shadow package that proves the gain path.

## 2. What happens to docs/324, 331 and 332

docs/324 and docs/331 stay the design references for needs, day plans, drift, visits, timer classes and exceptions.
This doc changes three things: (a) the model is written as explicit state machines with a fixed transition table and a
determinism harness, not as loose pure functions; (b) lazy instantiation is a phase of its own with a proof gate;
(c) WS-0 and CAW are no longer parked, and the non-performance packages move to Boundless Atlas. docs/332 keeps its
handoff block format, its lane rules and its package ids.

Ids are kept wherever the goal is unchanged. Handoffs go to `docs/handoff/` from now on (one file per package or
lane, docs/332 §3 block format); `docs/data/unbounded-packages-handoff.md` is not appended to any more.

| docs/332 id | Here | Note |
|---|---|---|
| FS-1 | FS-1 (changed) | Shadow core becomes a table-driven state machine per SD-1; adds seed, version, trace hash |
| FS-2 .. FS-6, FS-8 .. FS-15 | kept | FS-2 / FS-3 compare the machine's state, not ad-hoc functions |
| FS-7 | FS-7 (changed) | Go-far / go-live follow the SD-3 contract (§3.5) |
| FS-16 .. FS-19 | kept, moved to phase L | T4 is the Sim half of lazy instantiation; now gated by RX-9 and SD-4 |
| FS-20 | kept, phase W | Unblocked by WS-0 |
| FO-1 .. FO-5, FO-7 .. FO-15 | kept | |
| FO-6 | FO-6 (changed) | Closed-form classes written as machine states per SD-2 |
| FO-16 | kept, phase W | Needs LZ-9 instead of WS-8 |
| ST-1 .. ST-9, ST-12 .. ST-15 | kept, phase R | ST-7b and ST-9 are mechanical |
| ST-16, ST-17 | Boundless Atlas | Quadtree key and paint-layer decode are world-size work |
| ST-10 (lot page-out RE) | superseded by RX-3 + RX-4 | Split into instantiation path and save id handling |
| ST-11 (SP over parked lots) | superseded by RX-2a | Part of the world-wide query census |
| ME-4, ME-6, ME-7, ME-10 | kept | |
| ME-2, ME-5 | Boundless Atlas | First 4096 m load and its digest |
| ME-8 (per-lot memory line) | superseded by LZ-1 | The lazy probe measures bytes per lot and per object |
| ME-0, ME-1, ME-3, ME-9 | docs/332 | ME-1 ran [verified: `docs/data/results/2026-11-23-queue-farmodel-bands-1123.md` exists]; not re-planned here |
| WS-0 | WS-0a, WS-0c here; WS-0b, WS-0d in Boundless Atlas | Design sheet and hand build here; CAW copy and export / generator input there |
| WS-1 .. WS-4b | Boundless Atlas | Fault causes, quadtree key, world generator |
| WS-5 | kept, phase W | Runs on the 2048 m test world first |
| WS-6, WS-7, WS-8 | superseded by LZ-2, LZ-3, LZ-9 | Same steps, now with the proxy design |
| CP-0 | kept, first package | Brings docs/324 and the lane branches to master |
| CP-1 .. CP-4 | done | Built; `[LotHub]` is in the v0.3.2 release gates [verified: commit a63473d4d] |
| CP-5 .. CP-12 | docs/332 | Not in this plan |
| new | RX-1 .. RX-6, RX-9, SD-1 .. SD-4, DT-1, DT-2, LZ-1 .. LZ-9, RC-1 .. RC-4 | §6 |
| new | RX-7, RX-8 | Boundless Atlas (CAW RE) |

## 3. Architecture

Everything in this section is the design proposal [inferred] that SD-1 .. SD-4 turn into a spec; the measured and
verified inputs are cited.

### 3.1 Inputs we already have

- Beyond 150 m from the viewers, NPCs own 85-89% of NPC sim-thread time (BP 250, IP 251, SV 195 ms/s attributed); a Sim
  costs about 4 ms/s wherever it is [measured, docs/322]. Tick skipping gains ~0 because cost is per decision
  [measured, docs/243 via docs/324]. So the lever is to make no decisions for far Sims.
- Far non-household lot and object work is 27.7 / 33.9 / 11.8 ms/s (BP / IP / SV) plus 8.9-10.5 ms/s of far lot alarm
  callbacks plus ~39-40 ms/s of world-lot alarms and yielding tasks [measured, docs/331 §0].
- EA puts an object's alarms on its lot's `AlarmManager`; only Sims and lot-less objects use the global one
  [verified, docs/331]. The lot is the unit for objects.
- EA already swaps off-screen walking Sims to a cheaper brain and keeps low-LOD Sims out of `SimUpdate`
  [verified, docs/143 / 144]. Our throttles on top of that failed their gates (docs/144 §9).
- EA already has Sims without a Sim object: homeless townies, service and role Sims built on demand, other-world Sims as
  `MiniSimDescription`, and NPC children as `Sim.Placeholder` [verified, docs/324 §5.1]. **Not verified:** that a
  resident adult can stay `SimDescription`-only for hours with every reader happy. That is ST-1 + ST-6.
- Address-based hashes and heap layout break naive replay [verified, docs/123]. The machines must not read either.

### 3.2 Boundary and hysteresis

Viewer points: camera target, camera position, every household Sim (same as docs/322 and `FarCompute`).

| Zone | Distance to the nearest viewer point | Who runs it |
|---|---|---|
| H | household, selected, unknown scope, any distance | EA, always |
| Near (T1) | < 150 m, or any trigger: on a household or visited lot, interacting with a household Sim, target of a call, invite or opportunity, observed in UI | EA |
| Hand-off (T2) | 150-450 m | EA at coarse cadence; safe points are found here |
| Far (T3) | > 450 m (per-world key, ~300 m after calibration), or parked in a rabbit hole / unseen lot for 1 sim-hour | state machine |
| Far lot | > 200 m, no EA-owned Sim on it, not household | lot machine |

- Go-far needs the outer radius **and** 30 sim-minutes with no trigger. Go-live fires at the inner radius plus a
  pre-expand ring sized by time-to-view, so a Sim is live before it can be seen. The two radii never touch, so a Sim
  on the edge does not flap; a `flap` counter (go-far within 10 sim-minutes of go-live) is a queue check.
- A Sim tied to the household (family, romance, best friend, coworker, classmate, pending opportunity or call) stays one
  tier nearer than its distance says (docs/324 §1.3).
- One owner per Sim and per lot at any time: EA or the machine. The ownership table is the only place that says which.

### 3.3 State sets

**Far Sim** (first cut for SD-1; day plan from docs/324 §2.3):

| State | Meaning | Leaves on |
|---|---|---|
| `Live` | EA owns the Sim | go-far at a safe point |
| `HomeIdle` | at home, awake; need buckets (eat, hygiene, fun, social) resolved in closed form | schedule edge, seeded pick |
| `Sleep` | at home, asleep | wake time, energy full |
| `Commute` | between two places; position is a point on a straight or stored path at time t | arrival time |
| `Work` / `School` | inside the rabbit hole | shift end |
| `Venue` | on a community lot or in a non-work rabbit hole | stay length (seeded) |
| `VisitProposed` | the machine proposes a visit; EA spawns and runs it | EA accepts (go-live) or the offer lapses |
| `PendingLive` | go-live requested, waiting in the spread queue | build done |
| `Excluded` | never far: pregnant, in a situation, on a date, dying, ageing up, service on duty, mod-owned (docs/324 §3.5-3.6) | reason gone |

Needs are not states. They are closed-form functions of (state, time in state, traits) evaluated on read
(docs/324 §2.2). Relationship, skill and career drift are seeded and bounded, and never touch a relationship with a
household Sim (docs/324 §2.7).

**Far lot and its objects** (docs/331 §4 classes; SD-2 fixes the table):

| Kind | Machine | Outcome at go-live |
|---|---|---|
| Plants, food spoil, dishes, trash, puddles, fish tanks, wildflowers, fishing spots | closed form of elapsed time | fields set through EA code |
| Spawners, harvestables | seeded schedule | EA spawn call per logged event |
| Reaction pulses, lot callbacks, commercial pulses | exposure table (no Sim present = no effect) | none or logged event |
| Generic alarm with no model yet | recorded fire list | replayed through EA's own callback in time order |
| Fire | seeded, promotes the lot when near a household lot, Sim or object (E2) | EA fire |
| Traffic | seeded spawn rate by band | none (ambient) |
| Door privacy, zombies, own-thread objects | paused with the lot; pause hook per class (ST-8) or the class stays live (E4) | resume |
| Seasons lot effects | computed from the world season loop, which stays live (E5) | fields set |

Exceptions E1-E6 of docs/331 §5 stand: household-facing timers pass through at their real time, unknown scope parks
nothing, mod classes without a pause hook stay live.

### 3.4 Tick model and determinism

- **No per-tick work per far Sim or far lot.** Each machine stores `state`, `enteredAt` and `nextDue` (sim time). A due
  queue keyed by `nextDue` is drained on the sim thread with a per-tick budget. Cost follows the number of transitions
  due now, not the population times the tick rate.
- Between transitions nothing runs. Reads (facade, docs/324 §5.4) evaluate closed forms at the read time.
- Catch-up is bounded: a machine that was not drained for a long time advances whole days with the periodic day plan
  instead of replaying each transition. SD-1 has to show the worst catch-up cost per go-live.
- **Seed:** `seed = H(world seed, stable id, model version)`. Stable id = `SimDescriptionId` for Sims, lot id plus
  object id for objects (id stability is RX-4's question). The world seed is created once and kept in the sidecar.
- **Random numbers are counter-based:** value = `PRNG(seed, transition index, draw index)`. Order of evaluation, thread
  and frame rate then cannot change a result. The machines never call EA's random helpers, never read wall-clock time,
  never iterate a hash-ordered collection and never use an address.
- **Inputs are closed:** snapshot at go-far, sim time, and a recorded world-history ring (weather, season, day of week,
  holidays; docs/331). Anything else that can influence a transition is written to the event log first.
- **Replayable:** the trace is the list of (id, transition index, from, to, sim time). DT-1 replays it offline from a
  recorded input file and compares hashes; DT-2 does the same in game from one rewind snapshot run twice.
- `model version` is part of the seed and of the sidecar. A build that changes a transition table bumps it; a sidecar
  with another version is discarded and far state is re-derived from EA state at load (vanilla result).

### 3.5 Go-far and go-live

**Go-far (EA to machine)** only at a safe point: no interaction running, not routing, not in a situation, not holding
or reserved by anything, not in a household-facing queue. The snapshot credits partial effects of the last interaction.
A Sim that never reaches a safe point stays live (counter `noSafePoint`).

**Go-live (machine to EA)** must produce a state EA itself could have reached. The SD-3 contract, each line a repro
check:

1. One instance per `SimDescription` (FS-5 guard) and one owner.
2. Position: a valid spot from EA's own placement (home lot spawn, rabbit-hole slot, venue door), never a raw computed
   point inside geometry.
3. Motives in range and written through EA's setters; no forged interaction. EA's autonomy picks the next action on
   its next run. If the machine state implies an activity (asleep, at work), the Sim is placed so EA's own logic enters
   that activity, or it simply starts from idle.
4. Held alarms re-armed with their remaining time; alarms that came due while far are fired in time order through EA's
   callbacks, household-facing ones were never held.
5. Drift (relationships, skills, career) applied through EA's setters, never to a household relationship.
6. Outfit, age and career state valid; no dangling object reference.
7. Budget: go-live work goes through the spread queue with a per-frame cap; priority is time-to-view.
8. Failure at any step: the Sim is handed to EA at its home lot through EA's own go-home path, logged once
   (`goLiveFallback`), never left half-built. Which EA entry is safe for this is RX-1's question.

Before every save, every machine flushes: all far Sims and lots are written back through EA code so EA saves EA state.

### 3.6 Lazy instantiation and proxies

> **Changed 2026-11-26:** Boundless Atlas docs/011 (2026-11-26, memory budget by consumer, "stream only what is heavy"). Stage L2 for objects is parked at every
> world size up to a filled 4096 m world (objects are 88-200 MB there [inferred] and hard to page); L1 is no-go
> (docs/344). L2 Sims (T4) stays and is the lazy item that counts. L3 stays out.

| Stage | Far object | Far Sim | Saves | Risk | Gate |
|---|---|---|---|---|---|
| L0 kept | object exists, parked, timers held | T3: Sim kept, hidden, inert | CPU only | low; static check in docs/324 §5 | FO-5, FS-10 |
| L1 stripped | script object and id stay as the proxy; native model, rig, footprint and render resources released | - | native memory | medium: which native parts can go and come back is RX-3's question | LZ-3 |
| L2 record only | object destroyed; a compact lot record (class, id, transform, persisted fields) replaces it | T4: `SimDescription` only, through EA's `Sim.Placeholder` path on the home lot | managed + native memory, world-wide loops get shorter | high: every world-wide query, id reuse, mods | RX-9, LZ-6 |
| L3 never loaded | far lots are not built at load | - | load time and load-time peak | highest; needs a load hook | out of this RC unless RX-4 finds one |

**Proxy rules (SD-4 writes the spec):**

- At L1 the script object is its own stub, so `Queries.GetObjects`, lot object lists, story progression and autonomy
  scoring still see it and ids are stable by construction. That is why L1 comes first.
- At L2 a **query facade** sits at the choke points RX-2 finds. Per call-site class: count and existence questions are
  answered from the record index; "nearest to a household Sim" style questions cannot hit a far lot and are answered
  without it; a site that iterates and reads or writes far objects **materialises the lot first**; an unknown or
  unhooked site also materialises. The facade never returns a wrong or partial answer to save time.
- A lot that keeps being materialised by the same site is pinned live and logged (`lazyThrash` counter); the fix is a
  better facade row, not a longer timer.
- Ids: an object must come back with the same id it had. If RX-3 shows ids cannot be chosen at create time, L2 is
  dropped for objects that anything references by id, and the plan stays at L1 for them.
- Save: materialise everything before the save, so EA writes EA state and the `.sims3` equals vanilla. After the save
  far lots go back to their stage through the spread queue. This keeps the save-time peak at today's level; it does not
  lower it. Lowering the load and save peaks is L3 and belongs to the separate project.
- Load: after a load, far lots are stripped again by the periodic pass (L1 / L2 give no gain during the load itself).
- Never lazy: household set (§1.3), rabbit holes and lots that EA services or story progression are using right now,
  lots with a fire, lots a mod marked through a compat row.

### 3.7 Sidecar data

One format for the far-Sim event log, the far-object event log and lot records (ST-14, spec in docs/343):
`ts3native\far\<key>.far.jsonl` in the game's user folder, outside the save folder (Rodney 2026-11-26: EA's save deletes
and recopies the save folder, docs/15 step 9, so a file inside it would not survive), versioned, written after the
`.sims3` succeeds. Contents: world seed, model version, per-Sim and per-lot event
logs since the last flush, drift ledgers. Because every save flushes to EA state first, the sidecar is never needed to
load a save correctly: missing, stale or other-version sidecar = discard, new seed, vanilla load. Size counter and a
cap with compaction are part of ST-14.

## 4. Phase R: REA proof before building

**Status 2026-11-26: running.** Three REA lanes started on Fable today. Two write their doc and a handoff in this
repo (`docs/handoff/`); the CAW lane writes its doc in Boundless Atlas. This plan does not repeat their work. The RX ids below are the names later packages use for their
answers, and each later package depends on the lane verdict, not on a rerun.

| Lane (running) | Doc it writes | Covers | Verdict that later packages wait for |
|---|---|---|---|
| Far-Sim tick loop | `docs/<N>-far-sim-tick-re.md` | RX-1, RX-5, RX-6 | loop entry and safe points (SD-1, SD-3, FS-7), allowed inputs (SD-1, SD-2), readers that need a Sim object (ST-6, FS-16) |
| Lazy object instantiation | `docs/<N>-lazy-instantiation-re.md` | RX-2a, RX-3, RX-4 | query choke points (LZ-1, LZ-4), create-with-id and strip (LZ-3), save ids and load skip (LZ-5, LZ-7) |
| CAW | in Boundless Atlas | RX-7, RX-8 (cross-reference) | this repo only waits for "the 2048 m test world loads" (WS-0c) |

The split of RX ids over the lanes is this plan's reading of the lane titles [inferred]; the lane docs decide. A
question in the table below that a lane doc leaves open becomes a follow-up package under the same RX id (the
estimates in §6.1 are the budget for that); a question it answers is closed with a pointer to the lane doc and its
docs/51 section. RX-2b (mods) and RX-9 (verdict) are not running: RX-9 starts when the two lane docs of this repo and ST-6 are in.

Static work with REA (Ghidra on copies of `TS3W.exe` under `game-copy/`, static .NET analysis of the script DLL copies;
method in docs/04 "How this was done"). Agent type `ts3-static-re`. Every answer goes to docs/51 with a section reserved
by `node tools/registry.ts reserve ledger`, marked verified or inferred, and into the handoff file. Decompiled code
stays local; docs quote an address, a method name or a few instructions at most.

| ID | Questions it must answer |
|---|---|
| RX-1 | Where does EA's per-Sim and per-object loop enter and leave? Managed simulator task list, the native task fetch (`0x76CA60` is named in docs/333; confirm), `SimUpdate`, autonomy, interaction queue, routing start. Which tasks does one Sim own, how are they removed and re-added, and which EA entry safely sends an NPC home (go-live fallback)? |
| RX-2a | Every world-wide object and Sim query in EA's scripts: `Queries.GetObjects` family, lot lists, household and SimDescription lists, native object-manager enumerations. Per site: caller, what it asks (count, exists, nearest, iterate-read, iterate-write), how often [static], and the smallest set of choke points that covers them. Includes story progression and services over lots that are not live (old ST-11). |
| RX-2b | Same census for the top-100 mod set (docs/328), in RX-2a's format. |
| RX-3 | The object instantiation path end to end: managed create calls, the native create, where the object id is allocated, proxy binding, add to the simulator, `OnCreation` / `OnStartup`, and the destroy path. Can an object be created with a chosen id? Which native parts (model, rig, footprint) can be released and rebuilt while the script object lives? |
| RX-4 | Save and load of object ids: how the `.sims3` writes objects and references to them, whether ids are stable over save / load, what a managed reference to a missing object becomes at load, and whether lot contents can be skipped at load (L3). |
| RX-5 | Every source of non-determinism the machines could touch: EA random helpers, time sources, hash-ordered collections, address-keyed tables. Output: the allowed-inputs list for SD-1 / SD-2. |
| RX-6 | Which readers assume a resident has a Sim object (`CreatedSim` and equivalents) outside ST-1's four UI paths: careers, school, services, parties, relationship decay, ageing, death. Feeds ST-6's verdict. |
| RX-7 | Moved to Boundless Atlas (cross-reference only). |
| RX-8 | Moved to Boundless Atlas (cross-reference only). |
| RX-9 | **Verdict.** From RX-2 .. RX-4 and RX-6: go / no-go per stage (L1, L2 objects, L2 Sims, L3), the facade row list for LZ-4, and the list of sites that force "materialise". Kill rule in §9. |

ST-1 .. ST-9 and ST-12 .. ST-17 of docs/332 run in the same phase with their goals unchanged.

## 5. Lanes and models

Lane ownership is docs/332 §5 (N, MS, FS, FO, T, S) plus one new lane:

| Lane | Owns | Packages |
|---|---|---|
| LZ (lazy) | `mods/ts3fixes/src/Lazy*.cs`, native strip helpers in their own file | LZ-1 .. LZ-9 |

- At most 4 build agents + 4 static agents at once (default, Rodney may override; docs/332 said 3); 0 agents during a timing run; game runs serial (docs/332 §5).
- One package per agent, fresh context, handoff written to `docs/handoff/` in the docs/332 §3 block format. Agents never wait idle: commit, hand off, stop.
- **Model policy (Rodney 2026-11-26):** Opus and Sonnet are allowed, but Fable goes first for anything Fable can do,
  to use the reset window. Every package row is marked `F>O` or `F>S`: run it on Fable while the window has budget;
  the second letter is where it goes when Fable is short or its lanes are full.
  `F>O` = hard work (RE, design, state-machine and lazy code, native hooks, merges, triage of FAIL / VOID jobs): falls
  back to Opus, never to Sonnet. `F>S` = mechanical work (queue JSON, digests from a given log, selftest harnesses
  with a fixed API, mod census in a given format, registry and index lines, release notes from given facts): falls
  back to Sonnet, and these rows are the first to leave Fable when budget gets tight. A Sonnet result that looks
  doubtful is re-checked before it is relaunched on Fable or Opus. The minute totals in §6.8 are by fallback class;
  with Fable first, all ~68 h can land on Fable.

## 6. Work packages

Columns: **M** = model, first choice > fallback (F Fable, O Opus, S Sonnet; policy in §5). **Est** = agent minutes + game runs. Every build package ends with
`node tools/selftest-all.ts` 0 FAIL, the native selftest when native code changed, a registered build and a dry-run
queue (docs/332 §2). **Gate** lists what must hold on top; "std" = selftests + smoke with `demotedHousehold` /
`parkedHousehold` / `strippedHousehold` 0 and script errors 0. Packages with a docs/332 id keep the goal written there;
only changes are spelled out. **Repo:** Mod = this repo; Atlas = Boundless Atlas (private), listed by id only.

### 6.1 Phase R: REA proof (22 packages here, 4 in Atlas)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| RX-1 | Mod | Sim / object loop entry map, go-home entry | F>O | 60 | - | every row has an address or method [verified] |
| RX-2a | Mod | World-wide query census, EA scripts. **Done 2026-11-26: docs/356** (object half by type in docs/344 §3; 1,270 sites classified in `docs/data/world-query-census.csv` by `tools/world-query-census.ts`; second native choke point `0xD74C00` for counts; 88 types on the materialise list; 6 open points in its §7, none for Rodney) | F>O | 60 | - | every site classified; choke points named |
| RX-2b | Mod | Same for the top-100 mods | F>S | 60 | RX-2a | every mod row classified or "none" |
| RX-3 | Mod | Instantiation and destroy path, id allocation | F>O | 60 | - | create-with-id answered yes / no [verified] |
| RX-4 | Mod | Save / load object ids, load skip | F>O | 60 | - | id stability answered [verified] |
| RX-5 | Mod | Non-determinism sources | F>O | 45 | - | allowed-inputs list |
| RX-6 | Mod | Readers that need a Sim object | F>O | 45 | - | reader table |
| RX-7 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| RX-8 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| RX-9 | Mod | Feasibility verdict per lazy stage | F>O | 45 | RX-2a, RX-2b, RX-3, RX-4, RX-6, ST-6 | go / no-go per stage, signed off by Rodney |
| ST-1 .. ST-6, ST-7a, ST-8, ST-12 .. ST-15 | Mod | docs/332 §2.2 (12 packages) | F>O | 485 | as docs/332 | as docs/332 |
| ST-16, ST-17 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| ST-7b, ST-9 | Mod | docs/332 §2.2 | F>S | 80 | as docs/332 | as docs/332 |

### 6.2 Phase D: design (4 packages)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| SD-1 | Mod | Far-Sim machine spec: state and transition table, guards, due queue, catch-up bound, seed and PRNG. **Done 2026-11-26: docs/345** (12 open questions in its §16) | F>O | 60 | RX-1, RX-5 | table complete; every transition names its inputs; household rows explicit |
| SD-2 | Mod | Far-lot / object machine spec per kind (docs/331 §4 as states). **Done 2026-11-26: docs/350** (11 open questions in its §17, none for Rodney; Q1-Q3 answered 2026-11-26; class table `docs/data/far-object-class-machines.csv`) | F>O | 60 | RX-5, ST-4, ST-7a | every census class has a row or an exception |
| SD-3 | Mod | Boundary, hysteresis and the go-far / go-live contract (§3.5), with one repro per contract line. **Done 2026-11-26: docs/355** (near scan on viewer movement, ring as a lead segment, parking mechanism c with EA sleep-forever and `Simulator.Wake`; 19 repros; 7 open points in its §8, none for Rodney) | F>O | 45 | RX-1, SD-1 | repro list covers lines 1-8 |
| SD-4 | Mod | Lazy and proxy design: facade rows, id rule, save flush, stage ladder. **Save flush done 2026-11-26: docs/353** (hub on the static `LoadSaveManager` forwarders; 7 open questions in its §13, Q1 closed); facade rows, id rule and stages as docs/344 §3, §4, §6 | F>O | 60 | RX-9 | every "materialise" site listed; unknown-site default written |

### 6.3 Phase S: far-Sim state machine (21 packages)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| CP-0 | Mod | Merge wave: docs/324, docs/326 and the open lane branches to master | F>O | 45 | - | merger selftests, dry-runs |
| FS-1 | Mod | Machine core per SD-1 (no hooks), trace hash | F>O | 60 | CP-0, SD-1 | fixed-seed selftest; same seed twice = same hash |
| DT-1 | Mod | Offline replay tool: recorded inputs in, trace hash out, 2 runs + shuffled evaluation order | F>S | 45 | FS-1 | hashes equal in all 3 runs |
| FS-2 | Mod | Shadow compare hooks | F>O | 60 | FS-1, ST-1 | std |
| FS-3 | Mod | Drift and visit proposals in shadow | F>O | 45 | FS-2, ST-3, ST-13 | std |
| DT-2 | Mod | In-game replay: one rewind snapshot run twice, `[Diag] FarTrace` hash per sim-hour | F>S | 45 + 2 jobs | FS-3 | hashes equal across the two runs |
| ME-4 | Mod | Digest of the ME-1 band rerun, T3 edge per world | F>S | 25 | - | per-world edge written |
| FS-4 | Mod | Shadow calibration queue BP / IP / SV | F>S | 20 + ~50 min | FS-3, ME-4 | 3 jobs PASS; error tables read |
| FS-5 | Mod | One-instance guard | F>O | 60 + 2 jobs | CP-0 | 2 repros PASS |
| FS-6 | Mod | Ownership table, T2 hand-off tier | F>O | 60 | FS-4, FS-5 | std |
| FS-7 | Mod | Go-far / go-live per SD-3 | F>O | 60 | FS-6, SD-3 | std; flush before save |
| FS-8 | Mod | Held alarms, on-demand guard; computed work shifts (`FarWork` / `FarSchool`) behind `farsimwork`, default off: EA runs shifts in the first build (docs/345 §6.2, Rodney 2026-11-26) | F>O | 60 | FS-7, FO-3 | std |
| FS-9 | Mod | Observation hooks, drift live | F>O | 45 | FS-8, ST-5 | std; `driftHousehold` 0 |
| FS-10 | Mod | Repros + correctness queue incl. SD-3 lines and a save round-trip | F>S | 45 + ~130 min | FS-9 | queue PASS; household counters 0; script errors 0; save equal |
| FS-11 | Mod | Spread queue, hysteresis, pre-expand ring | F>O | 60 | FS-10 | std; `flap` 0 in the sweep job |
| ME-10 | Mod | EA visit-rate baseline | F>S | 30 + 25 min | ST-13 | rate table |
| FS-12 | Mod | Prebuild, loading-screen batching, visits live | F>O | 45 + 3 jobs | FS-11, ME-10 | 3 jobs PASS |
| FS-13 | Mod | Timing A/B by band, incl. the arm EA shifts against `farsimwork` (docs/345 §17 M20) | F>S | 30 + ~130 min | FS-12 | §7 verdict |
| FS-14 | Mod | NRaas StoryProgression precedence | F>O | 60 | FS-10, ST-2 | std |
| FS-15 | Mod | EA and NRaas story nights on far Sims | F>S | 30 + 3 jobs | FS-14 | 3 jobs PASS; `ownerConflict` 0 |
| ME-7 | Mod | Native-create split for build spacing | F>O | 45 + smoke + 3 jobs | CP-0 | split sums to the native row |

### 6.4 Phase O: far-object state machine (16 packages)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| FO-1 | Mod | `[Diag] FarObjProbe` | F>O | 60 | CP-0, ST-4 | std |
| FO-2 | Mod | Report + split queue | F>S | 45 + ~80 min | FO-1, ST-9 | queue PASS |
| ME-6 | Mod | Day-run queue | F>S | 20 + 2 x 25 min | ST-9 | 2 jobs PASS |
| FO-3 | Mod | Shared hold hook core | F>O | 60 | ST-15, FO-2 | std |
| FO-4 | Mod | Park / promote / owner flag | F>O | 60 | FO-3 | std |
| FO-5 | Mod | History ring, sidecar, save flush, shadow | F>O | 60 + 4 jobs | FO-4, ST-14 | queue PASS; `parkedHousehold` 0; save equal |
| FO-6 | Mod | Closed-form and seeded classes per SD-2 | F>O | 60 | FO-5, SD-2, ST-7a | selftest per class; DT-1 covers them |
| FO-7 | Mod | Recorded fires, event log, ordered materialisation | F>O | 60 | FO-6 | std |
| FO-8 | Mod | Repros + queue | F>S | 30 + 8 jobs | FO-7 | queue PASS |
| FO-9 | Mod | Spawners, harvests, exposure tables | F>O | 60 | FO-8, ST-7b | std |
| FO-10 | Mod | Door privacy, zombies, own-thread pause hooks | F>O | 60 + 4 jobs | FO-9, ST-8 | 4 repros PASS |
| FO-11 | Mod | Fire model | F>O | 60 | FO-10 | std; `fireNearHousehold` 0 |
| FO-12 | Mod | Traffic, seasons lot effects | F>O | 60 + 4 jobs | FO-11 | 4 repros PASS |
| FO-13 | Mod | Shadow calibration 2 sim-days | F>S | 30 + 3 x 25 min | FO-12 | error tables |
| FO-14 | Mod | Timing A/B, alone and with far Sims on | F>S | 30 + ~220 min | FO-13, FS-13 | §7 verdict |
| FO-15 | Mod | World-lot and lot-less objects | F>O | 60 + ~60 min | FO-14 | repros PASS |

### 6.5 Phase L: lazy instantiation (13 packages)

> **Parked 2026-11-26 (Atlas docs/011):** LZ-2 .. LZ-7 and LZ-9 (object record, strip, facade, object L2, L3 probe,
> page-out API). **Kept:** LZ-1 (probe: bytes per class and lot) and LZ-8 (memory A/B, for far Sims). **Added:**
> LZ-0, static read of the lot imposter allocation `0x5C32E2` and its callers (docs/215 gap G3), ~1 h, no game
> run; it decides whether an imposter cap is a build. **Result 2026-11-26: not a build (docs/344 §9).** The rows below are kept for the record.

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| LZ-0 | RE | Static read of `0x5C32E2` and its callers (docs/215 gap G3). **Done 2026-11-26, docs/344 §9: not a build.** It is the untagged main-heap `Malloc` helper `0x5C32D0`; the measured row is 3-5 main heap cores triggered at world load; the only owner measured behind it is the lot message pool (docs/541, `[LotPool] Trim` exists). Left: pool peak on the first filled 4096 m load | - | done (~1 h) | - | verdict written; ledger docs/51 §629 |
| LZ-1 | Mod | `[Diag] LazyProbe` (off, read-only): far objects by class, bytes per lot and object, hits per RX-2 site on far lots per sim-minute | F>O | 60 + smoke + 3 jobs | RX-2a, CP-0 | 3 worlds PASS; MB at stake and hit rate per site in a table |
| LZ-2 | Mod | Lot record: write one parked far lot's objects to a record and read it back offline, no clear | F>O | 60 | SD-4, FO-5 | round-trip equal in selftest |
| LZ-3 | Mod | Stage L1: strip and restore one far empty lot, ids and fields compared | F>O | 60 + 3 jobs | LZ-2, RX-3 | repro PASS; ids equal; script errors 0 |
| LZ-4 | Mod | Query facade at the RX-9 choke points, counter per site, unknown site = materialise | F>O | 60 | LZ-3, RX-9 | selftest per facade row |
| LZ-5 | Mod | Stage L2 for the classes RX-9 allows: on-demand materialise guard, spread-queue build, save flush, thrash pin | F>O | 60 | LZ-4, FS-11 | std; `lazyThrash` reported |
| LZ-6 | Mod | Repros, save matrix, correctness queue with the mod short list (then the top-100 set before any ON) | F>S | 45 + save matrix + 8 jobs (~150 min) | LZ-5 | queue PASS; `strippedHousehold` 0; script errors 0; save equal; uninstall load clean |
| LZ-7 | Mod | Stage L3 probe only if RX-4 says a load skip exists: skip one far lot at load behind a test switch | F>O | 60 + 3 jobs | LZ-6, RX-4 | repro PASS or written no-go |
| LZ-8 | Mod | Memory and timing A/B (stress runs, not soaks) | F>S | 30 + ~130 min | LZ-6 | §7 verdict |
| LZ-9 | Mod | Page-out precondition API that Boundless Atlas streaming can call (old WS-8) | F>O | 45 | LZ-6, FO-15 | selftest; API note |
| FS-16 | Mod | T4 placeholder variant | F>O | 60 | FS-13, ST-6, RX-9 | std |
| FS-17 | Mod | NRaas rebuild copies overridden | F>O | 60 | FS-16, ST-2 | repro 15 PASS |
| FS-18 | Mod | Inventory, build spacing, sidecar log, career drift | F>O | 60 | FS-17, ST-14, ME-7 | std |
| FS-19 | Mod | Save matrix + repros + timing | F>S | 30 + save matrix + 4 jobs | FS-18 | save matrix PASS |

### 6.6 Phase W: test world (5 packages here, 9 in Atlas)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| WS-0a | Mod | World design sheet with Rodney: layout, sizes, scenario sheet per test | F>O | 45 | - | Rodney OK |
| WS-0b | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-0c | Mod | Hand build in CAW at 2048 m (Rodney, with agent guidance) and a playable save with a test household | - | Rodney ~3-6 h | WS-0a | loads in the game; save loads |
| WS-0d | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-3 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-4a | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-4b | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| ME-2 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| ME-5 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-1 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-2 | Atlas | moved to Boundless Atlas; cross-reference only | | | | |
| WS-5 | Mod | Save matrix, memory and timing vs SV on the test world | F>S | 30 + ~90 min | WS-0c | matrix + 3 jobs |
| FS-20 | Mod | Far-Sim proof on the test world | F>S | 45 + 3 jobs | FS-19, WS-0c | 3 jobs PASS |
| FO-16 | Mod | Far-object + lazy proof on the test world | F>O | 60 + 3 jobs | FO-15, LZ-9, WS-0c | 3 jobs PASS |

### 6.7 Phase A: RC assembly (4 packages)

| ID | Repo | Goal | M | Est | Dep | Gate |
|---|---|---|---|---|---|---|
| RC-1 | Mod | Merge wave + rule review (`ts3-merger`, `ts3-rule-reviewer`) | F>O | 60 | lanes done | review clean; queues repinned |
| RC-2 | Mod | Steam 1.67 chains: smoke, bundle per world, save matrix | F>S | 30 + ~4 h | RC-1 | all PASS |
| RC-3 | Mod | EA 1.69 parity port and correctness on the EA laptop | F>O | 60 + EA runs | RC-1 | correctness PASS (timing not required on EA) |
| RC-4 | Mod | Switch defaults, `verified-switches.json`, release notes, changelog | F>S | 45 | RC-2, RC-3 | release gates pass |

### 6.8 Totals

| Phase | Packages | `F>O` min | `F>S` min |
|---|---:|---:|---:|
| R REA proof | 22 | 860 | 140 |
| D design | 4 | 225 | 0 |
| S far Sims | 21 | 705 | 270 |
| O far objects | 16 | 660 | 155 |
| L lazy | 13 | 585 | 105 |
| W world | 5 | 105 | 75 |
| A assembly | 4 | 120 | 75 |
| **Total (this repo)** | **85** | **3260 (~54.3 h)** | **820 (~13.7 h)** |

The 13 Atlas packages (RX-7, RX-8, ST-16, ST-17, WS-0b, WS-0d, WS-1 .. WS-4b, ME-2, ME-5) are not counted.

Game time is the real bound, as in docs/332 §4: far Sims ~10 h, far objects ~12 h, lazy ~6 h, world ~2.5 h, assembly
~4 h, about 35 h serial [inferred; the first two from docs/332, the rest summed from the tables above]. Clean nights
for timing: FS-13, FO-14, LZ-8, WS-5.

**Critical path to the first timing verdict:** CP-0, RX-1, RX-5 (both in the running tick-loop lane), SD-1, FS-1, FS-2, FS-3, FS-4, FS-6, FS-7, FS-8, FS-9,
FS-10, FS-11, FS-12, FS-13 = 800 min (~13.5 h) agent + ~6 h game. FS-8 also needs FO-3 (shared hold hook), so the
FO-1, FO-2, FO-3 chain starts as soon as ST-4 and ST-15 are in and must not become the long pole.

## 7. Measurement plan

- **Setup:** Apex + bridge (Bin state `apex`), worlds BP / IP / SV, later the WS-0 world. In-load A/B with runtime
  switches, ABBA, 10 minutes per arm, `"rewind": "auto"`, a smoke job before any queue over ~1 h (per-arm split, heavy
  tools off, switch values read back), no agents during timing, external-load check on every run.
- **Sim thread, ms/s by band:** `[UnseenProbe] Bands=150,200,450,1000` (docs/322) for Sims and `[Diag] NonSimSplit`
  (docs/329) for lots and objects, per arm. The verdict table is ms/s per band, off vs on, per world.
- **Memory, MB:** private bytes, largest free block and managed heap from the `[MemDiag]` lines (docs/190), plus LZ-1's
  bytes per lot and per object. Stress runs (fast camera tours across far lots, forced go-live bursts) rather than
  soaks.
- **Hitches:** p99, p99.9 and max frame time per arm, and separately for the go-live burst jobs (camera sweep, travel,
  load screen exit). A go-live hitch is judged by a repro that forces it, not by the natural run.
- **Correctness counters read in every job:** `demotedHousehold`, `parkedHousehold`, `strippedHousehold`,
  `driftHousehold`, `fireNearHousehold`, `ownerConflict`, `noSafePoint`, `goLiveFallback`, `flap`, `lazyThrash`, table
  size counters, script errors.
- **Determinism:** DT-1 offline and DT-2 in game are part of the correctness queues from FS-10 on.
- **Save round-trip:** save with far state on, reload with the mod removed, compare against the vanilla arm with
  the existing save matrix tooling (docs/TESTING.md); plus sidecar missing, stale and other-version cases.
- **Public numbers:** baseline vs latest only.

Verdict rule: a consistent 3-5 ms/s gain with correctness passed = default ON candidate; a gain that is shown
statically to scale with world size may be ON with no gain on today's worlds; memory gains count at any size as long
as nothing regresses. Until then every switch stays off.

## 8. WS-0: building the test world with CAW

Default (Rodney may override): **2048 m first**, built by Rodney in CAW with agent guidance, about 3-6 h.

1. **WS-0a design sheet (this repo).** Content Rodney asked for on 2026-11-23: large ocean areas, houseboats and boat
   routes, dense city blocks, rabbit holes, community and commercial lots, far NPC homes placed at the 150 / 200 / 450 /
   1000 m band edges and well beyond. One scenario sheet per test (WS-5 save matrix, FS-20, FO-16, LZ-8): which lots,
   households, Sims and camera tour each needs. It also gives the step list an agent walks Rodney through in CAW.
2. **WS-0c hand build (this repo, Rodney).** Largest map size CAW offers (2048 m per side [community, docs/320]);
   terrain, roads, water, lots and routing painted in CAW so the content is real, not generated. Export, install, and
   make a playable save with a test household. The CAW install is used as shipped: no patch, no analysis here.
3. **WS-5, then the FS-20 and FO-16 proofs** run on that world.
4. **In Boundless Atlas (cross-reference only):** WS-0b, RX-7, RX-8, WS-0d, WS-3, WS-4a, WS-4b, ME-2, ME-5, WS-1, WS-2,
   ST-16, ST-17. Growing the world to 4096 m and loading it happen there; the proofs of step 3 are rerun on the grown
   world when it exists.

Game files, the world and its saves never go into git; only the design sheet, scenario sheets, scripts and results do.

## 9. Risks and kill criteria

| Risk | Sign | Action |
|---|---|---|
| A household Sim, lot or object is touched | any household counter > 0 in any job | stop the lane, fix first; never ship around it |
| Go-live state is not one EA could reach | SD-3 repro FAIL, script error at go-live, `goLiveFallback` above a few per sim-day | switch stays off; fix the contract line; after one fix round with no clean result, drop that state from the machine (Sim stays live in it) |
| The machine is not replayable | DT-1 or DT-2 hash differs | find the input (RX-5 list), no live switch until equal |
| World-wide queries make L2 unsafe | RX-9: unhooked iterate-write sites on far objects, or ids cannot be chosen at create | L2 no-go for those classes; RC ships L0 / L1 only |
| Mods query or tick far objects | RX-2b rows, `lazyThrash` high with the compat set | compat row pins the lot live; stand down per class |
| Save differs from vanilla | save round-trip diff, uninstall load not clean | feature stays opt-in and off; no ON without a clean save matrix |
| Lazy gives no memory | LZ-1 shows far objects hold little memory in process, or LZ-8 shows no gain after bookkeeping | stop phase L after LZ-1 (probe only); keep the CPU work |
| No CPU gain | FS-13 / FO-14 below a clean 3-5 ms/s and no static scaling argument | park after one fix round (throttles park rule) |
| Go-live hitches | p99.9 or max worse in the sweep jobs | spread-queue budget and pre-expand ring first; ON only when not worse than off |
| NRaas rebuilds far Sims | `ownerConflict` > 0 | T4 stays opt-in; precedence hooks per copy (docs/324 §5.6) |
| Save-time peak with everything materialised | save hitch or memory peak above vanilla | must equal vanilla by design; if above, flush in slices before the save starts |
| WS-0 slips (hand build) | no world by the time FS-19 is done | RC ships without the test-world proofs; FS-20 / FO-16 move to the next RC |
| Scope too large for one RC | phase S not at FS-13 when phase O reaches FO-8 | cut line (accepted as the default, Rodney may override): RC = phases R, D, S + FO-1 .. FO-8 + LZ-1 .. LZ-3; the rest moves |

## 10. RC assembly

Checklist: `docs/RELEASE.md` (release build, defaults in one step, "Before publishing", publishing) and docs/279 for
the experimental pre-release and `docs/data/verified-switches.json`. For this RC on top of that:

- Both editions: Steam 1.67 gets the full correctness and timing set; EA 1.69 gets the parity port and correctness
  only (EA laptop).
- Switch rules: every new switch defaults off; ON in the RC only with a passed correctness queue, clean household
  counters, a clean save matrix and the §7 verdict. Anything that changes what is saved stays opt-in. T4 and L2 stay
  opt-in in this RC whatever the numbers say.
- Compat: a mod short list runs first in LZ-6 and FS-15, then the top-100 set before any ON decision (default, Rodney
  may override); stand-down rows per docs/328.
- RC chains run after contributor PR merges; public sync through the open-PR flow; releases need Rodney's OK.

## 11. Fable budget for this reset window

Rule: Fable goes first to the work that cannot be done by Sonnet and that everything else waits for. Order: RE, then
design, then the hard code. Sonnet lanes run beside it and during game runs that allow agents.

**Wave 1 (running, or launch now; parallel):**

| Lane | Package | Model | Why first |
|---|---|---|---|
| static 1 | far-Sim tick loop lane (RX-1, RX-5, RX-6) | Fable, **running** | SD-1 and the go-live fallback wait for it |
| static 2 | lazy instantiation lane (RX-2a, RX-3, RX-4) | Fable, **running** | decides whether phase L exists at all, and the id rule |
| (Atlas) | CAW lane (RX-7, RX-8) | Fable, **running** in Boundless Atlas | cross-reference only |
| merge | CP-0 | Fable (`ts3-merger`), launch now | master must hold docs/324 and the lane branches before any build |
| design | WS-0a | Fable, with Rodney, launch now | the hand build is the longest human step |
| mechanical | ME-4, ST-9 | Fable first, else Sonnet | no dependencies |

**Wave 2 (as static lanes free up):** ST-1, ST-4, ST-6, ST-7a and any RX follow-up a lane doc leaves open;
mechanical: RX-2b, ST-7b.
**Wave 3:** SD-1, SD-2 (after the tick-loop lane verdict), ST-14, ST-15, ST-3, ST-13; first builds FS-1 and FO-1;
mechanical: DT-1.
**Wave 4:** RX-9 verdict, SD-3, SD-4; FS-2, FS-3, FS-5, LZ-1; mechanical: DT-2, FS-4, FO-2, ME-6.
**Wave 5 on:** FS-6 .. FS-12 and FO-3 .. FO-7 as two build lanes, LZ-2 .. LZ-5 as the third, tools lane on WS-3 / WS-4a
once the world exists. Timing packages take the clean nights.

Fable spend by wave [inferred from the estimates above]: wave 1 ~4.5 h, wave 2 ~6.75 h, wave 3 ~7.25 h, wave 4 ~6.25 h =
about 25 h of Fable agent time, front-loaded: phase D complete, every phase R package a build waits for, and the
first builds. The remaining static packages (ST-2, ST-5, ST-8, ST-12, ~2.5 h) fill free static lanes (up to 4). If the window runs
short, the cut order is: `F>S` rows to Sonnet first, then FS-14 .. FS-19 to Opus or the
next window; never the REA lanes, RX-9 or SD-1 .. SD-4.

**Checkpoints so work survives a reset:**

- Every package writes its handoff block in `docs/handoff/` and commits before it ends, done or not.
- Every RX and ST answer is a docs/51 section, not a chat summary.
- SD-1 .. SD-4 are doc files (numbers reserved with `node tools/registry.ts reserve doc`), with their index lines.
- Gate records in PLAN.md, one line each: G-R (RX-9 verdict), G-S (FS-4 shadow error), G-C (FS-10 / FO-8 / LZ-6
  correctness), G-T (FS-13 / FO-14 / LZ-8 verdicts).
- After each wave the orchestrator updates a status table in `docs/handoff/` and the PLAN.md pointer, so a fresh
  session can start from "next unblocked packages" without reading this doc again.

## 12. Questions and working answers

Question 5 is closed by Rodney. The others carry a working answer: **default, Rodney may override.**

| # | Question | Answer |
|---|---|---|
| 1 | Version and freeze date | v0.4.0, freeze date TBD (default, Rodney may override) |
| 2 | Fallback cut line of §9 (phases R, D, S + FO-1 .. FO-8 + LZ-1 .. LZ-3) | OK (default, Rodney may override) |
| 3 | WS-0 size | 2048 m first; growing to 4096 m is Boundless Atlas work (default, Rodney may override) |
| 4 | Who builds the world in CAW | Rodney, with agent guidance, 3-6 h (default, Rodney may override) |
| 5 | Where CAW and world-size work lives | **Closed (Rodney 2026-11-26):** this repo holds only Sims 3 performance work. Boundless Atlas (private) holds all CAW patches and every Sims 3 patch that is not about performance: world-size caps, streaming, the world generator, the CAW test harness. The CAW REA lane writes its doc there. |
| 6 | T3 edge | 450 m with a per-world key, then calibrate per world from FS-4 (default, Rodney may override) |
| 7 | Go-live from idle when the machine says mid-activity | Yes, only while the Sim is unseen (default, Rodney may override) |
| 8 | T4 (far Sims as `SimDescription` only) | Opt-in in this RC (default, Rodney may override) |
| 9 | Mods that must pass with lazy objects on | A short list first, then the top-100 set (default, Rodney may override). The short list itself is still to be named. |
| 10 | Static lanes during this window | Up to 4 (default, Rodney may override) |
| 11 | Handoff location | `docs/handoff/` only (default, Rodney may override) |
