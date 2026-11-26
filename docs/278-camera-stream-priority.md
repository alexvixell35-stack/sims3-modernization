# 278. Camera stream priority: big objects first, small ones a frame or two later

Written 2026-11-17. **Static + existing measurement data only, no game run, nothing built.** Rodney's idea (also sent to
Apex Radiance): when the camera zooms or rotates, keep the frame rate; load and draw the big parts (terrain, floors,
walls, roofs) first and let small, low-priority objects appear 1-2 frames later.

Ledger §498. Owner: Apex Radiance (engine, render thread; memory note "Apex-area experiments first").

## 1. Verdict

- **Most of the idea already exists**, split between EA, S3SS and Apex (§3). Small-object scene placement is already
  budgeted per frame while the camera moves (Apex *Spread new objects over frames*), lot object adds are already chunked
  with large objects first (S3SS object throttle), lot builds already go terrain/floor/roof/wall before anything else
  (EA's load stages), and far lots already show as one low-LOD impostor.
- **What is left does not hit the frame time.** The camera hitches that remain are single indivisible items (one DXT
  encode, one lot load stage, one synchronous impostor build, one CAS build step, the terrain update), not many small
  objects (§2). A per-frame budget cannot split an item, and big-first ordering moves the same work around inside the
  same budget. Expected frame-time gain: about 0 to 2 ms on a moving hitch frame with Apex on [inferred, §4].
- **The one new piece with value is visual**: inside Apex's budgeted scene drain, place nodes with large bounds first,
  so walls and roofs of a streaming lot never wait behind cups and plates. That is pop-in order, not fps.
- **Recommendation:** do not build. First read one existing-tool measurement (Apex Frame Profiler on a camera sweep,
  §6) to check whether any moving hitch on current Apex is led by small-object work. Only if it is, propose the two
  Apex changes in §5 with that data (text needs Rodney's OK before it goes to Apex).

## 2. Where camera-move hitches go (existing data)

| Source | Setup | What leads |
|---|---|---|
| Apex `docs/history/performance.md`, 2026-09-28 [Apex-measured] | 110 s camera test, 1,293 hitches, before Apex's performance features | 8-16 ms: resource lookup in ~65% (4.6-5.3 ms, 246-288 lookups a frame); 16-50 ms while moving: lot room solve in 50-77% (~15-17 ms, the priority lot's 15 ms light budget); >= 50 ms: DXT encode in 22-40% (44-49 ms), RefPack compress in 20-30% (20-37 ms) |
| Apex `docs/history/frame-profiler.md`, combined-build report [Apex-measured] | 51,600 frames, p50 4.38 / p95 14.28 / p99 32.38 ms, 6.86% hitches | "Services (self)" 13.6 ms per hitch (worst 134 ms): CAS SimService, main-thread jobs (async resource finalize `0x7297C0` **2.2 ms per hitch**), CAS TextureCompositor, WorldManager. Terrain update worst 52 ms, Scene::BeginFrame worst 46 ms. Spikes > 50 ms created ~16 textures each |
| Apex `lot-streaming.md` [Apex-measured] | recorded lot loads | up to 7 lots building in one frame, 96 hitches led by load stages, frames up to 134 ms; stage 0 of a large lot alone up to 68 ms |
| Ours, docs/167 §7 + docs/169 [measured] | `freeze-measure-2026-10-22-bp-freeze-pan`, BP, camera pan, asi.0329 | 643 presented frames >= 100 ms: the services stage holds 85% of the time and leads all 18 frames > 300 ms. Inside it: CAS/SimService 29%, World/Service 20% (lot streaming), SwarmSystem 16% (particles) |

Split by kind:

- **Resource load on the main thread**: the async finalize job (factory parse, decompression, GPU object creation, one
  resource per job) runs on the main thread inside the JobManager service; package reads and RefPack decompression run
  synchronously on whichever thread asks. Measured share: ~2.2 ms per hitch for finalize [Apex-measured]; resource
  lookup 4.6-5.3 ms in small hitches (Apex *Faster game file lookups* targets it).
- **Texture work**: DXT encode (lot impostor textures `LotLODCreator/LODTexture`, terrain normal maps, CAS) and RefPack
  compress lead the >= 50 ms frames. Apex *Faster texture compression* (multi-core, exact) targets it.
- **Lot LOD build**: load stages (20/35 ms slice each, several lots per frame), room lighting, wall AO, impostor build.
  Apex *Lot Streaming* (shared 6/4 ms build budget), *Lot Lighting While Moving*, *Wall shading waits while moving* target
  them.
- **Draw/CPU render cost**: Scene::BeginFrame's pending-node drain (materials resolve textures through the resource
  manager); Apex *Spread new objects over frames* budgets it while moving.
- **Sim**: not on the presented-frame path. The render thread does not wait for the sim each frame (Apex
  `main-loop-and-services.md`); the lot object add `0xAC1130` runs on the sim thread (docs/28 §5) and shows as Sims
  freezing, not as camera stutter.

Gap: no measurement on current Apex (4.x) + bridge splits a camera sweep's remaining hitches. All rows above predate
some of the Apex features, and ours ran before the Apex-bridge baseline.

## 3. What EA, S3SS and Apex already do

| Mechanism | Where | Status |
|---|---|---|
| Async resource loads: read job `0x72A4F0` on JobThread workers, finalize job `0x7297C0` on the main thread | JobManager service `0x599A10`; finalize body `0x7294F0` (`0x7297C0` calls it and returns, `0x7297CE`) | [verified 2026-11-17, Steam 1.67 bytes] for `0x7297C0 -> 0x7294F0`; worker/main split [Apex-verified] |
| Main-thread job time slice: stopwatch unit 4 (ms) armed with `[svc+0xCC]` (default 5 ms), only while `[svc+0xC8] != 0`, checked after each job | `0x599A16`..`0x599A56` | [verified 2026-11-17] slice read and arm; default 5 [Apex-verified] |
| ResourceSystem completions, CAS SimService, TextureCompositor: own 5 ms slices each | `0x7377F0`, `0x5F0E50`, `0x608630` | [Apex-verified] |
| Lot LOD: lots outside the detail set show as one impostor; detail promotion skipped while the camera's 10-sample mean speed is above "Camera speed threshold" (`WM+0xEC`) | lot scoring `0xC6C290`, `WorldManager+0x310` | [verified, docs/28 §5] |
| Lot build order: setup, lighting manager, triangulation, **floors, water, roofs, walls**, fences, pools, ceilings, stairs ... then lighting wait | load stages of `0xAEA680` | [Apex-verified] |
| Model LOD bands: a band change re-queues the node | `0x6FCB10` | [Apex-verified] |
| Object add chunking, large and flora objects in the first window | S3SS LSO object throttle, detour `0xAC1130` (docs/68 row `0xAC1130`) | [community-reported, bytes verified docs/28] |
| Scene pending-node drain, newest first, per node `vfunc+0x48`, bounds `0x6FB4B0`, spatial insert `0x6FAD70`, counter `[holder+0x18]` | `0x6E4130` (call from Scene::BeginFrame `0x6EBC49`) | [verified 2026-11-17]: the loop's calls at `0x6E41D5` / `0x6E41DE` / `0x6E41E6` and `add [edi+0x18]` at `0x6E41EF` |
| Budgeted drain while moving: 512 nodes or 2 ms a frame, at least 8; effect/fur nodes never wait; growing budget past 500 ms; full drain when still | Apex `SceneNodeBudget`, CallChain on `0x6EBC49` | Apex, on by default since 2.5.5 |
| Shared lot build budget 6 ms still / 4 ms moving; lot lighting 3 ms while moving; wall AO waits while moving | Apex Lot Streaming / LotLightingMotion / WallShadingWhileMoving | Apex, on |

Not found: **no priority or size ordering** in the finalize queue or the scene drain. The drain is a plain intrusive list
(push_back at the tail by `0x6E42E0`, drained newest first) [Apex-verified; loop order verified here]. Whether JobManager's
main-thread queue has a priority field is open (I-498-3).

## 4. Gain estimate

- Deferrable small-object creation per moving hitch frame = finalize jobs (2.2 ms mean per hitch, capped by the 5 ms
  slice plus one overshooting job) + scene drain past Apex's 2 ms budget. With Apex on, that is <= ~2-5 ms of a frame
  whose hitch is led by a 15-68 ms indivisible item [inferred, §2 numbers].
- Big-first ordering keeps the per-frame work the same; it changes which node waits. Frame-time gain ~0 [inferred].
- Lowering the finalize slice while moving (5 -> 2 ms) could take up to ~3 ms off frames where finalize runs, only on
  frames where it hits the slice; one large resource (a texture create) still runs whole [inferred].
- Ceiling for the whole idea on current Apex: ~0-3 ms on some moving frames; p99 and max unchanged while DXT encode, lot
  stage 0, impostor builds and CAS steps lead the worst frames.

## 5. Design (only if §6 shows small-object work leading moving hitches)

Scope: render-side only. No gameplay effect, so the household rule does not apply, and nothing on the sim thread moves:
`AddLotObjectsToScene` (`0xAC1130`) stays out because `Lot::SetActiveImpl`'s fixups need its objects at once (S3SS
builds large objects first for that reason) and it runs on the sim thread.

**A. Size-first drain (visual).** In Apex's `BudgetedDrain`, while moving: one pre-pass over the pending list reading each
node's cached world bounds (the bounds `0x6FB4B0` returns are computed per node inside the loop, so the pre-pass needs a
cheap cached radius, e.g. from the node's last spatial cell, or it costs a second bounds call), then drain nodes above a
radius threshold (walls, roofs, floors, large furniture; inferred ~1-2 m) first and small ones in the game's order up to
the budget. Must-not-wait nodes (Swarm, fur, short-LOD) keep their rule. Conflict: Apex's maintainer rule says "do not
change the copy's order"; the order change must keep the splice and the re-read of the local sentinel, and their node
lifetime guard is unchanged. Exactness: every node is placed within the existing longest-wait bound (500 ms, then a
growing budget, full drain at 1.5 s), so the final frame equals vanilla once the camera stops.

**B. Finalize slice while moving (frame time).** Write JobManager `[svc+0xCC]` = 2 ms while Apex's shared camera signal
says moving, restore 5 ms 300 ms after it stops; never during loading screens. No code patch: a data write the game reads
at `0x599A20` every update. Only main-thread jobs are affected (async finalize plus other main-thread jobs). Exactness:
the same jobs run, later. Risk: a job that something waits on synchronously (a "Wait for job" in the Frame Profiler)
gets later; measure the wait counter.

Hooks and conflicts: A lives inside Apex's own CallChain on `0x6EBC49`; B is a data write, no hook. Neither touches a
site S3SS or S3PP patches (S3SS patches none of the scene-drain sites, per Apex's scan; Apex removed its own earlier
Service Frame Budget, which also wrote service slices: `removed-features.md`, no perceptible gain). We add no hook, so
`hook-sites.ts` is unaffected.

## 6. Proof plan

Step 0 (no build, one game run, Apex 4.x + bridge, existing tools): Apex Frame Profiler (timing-run preset) on a camera
sweep; read the moving hitches' leads from `ApexRadiance_Hitches.txt` and `ApexRadiance_FrameTimeline.csv`. Decision:
build A or B only if "Scene pending nodes" or job `007297C0` leads >= 25% of moving frames >= 25 ms.

Queue sketch (only if step 0 says go; 10-min arms, smoke first, Apex built from the PR branch per the Apex-area rule):

| Job | World | Repro | Arms | Metrics |
|---|---|---|---|---|
| `bp-camsweep-smoke` | BP | `camera_pan` (docs/162 §4) 2 min | Apex default | profiler file written, camera-moving frames > 0, switch value read back |
| `bp-camsweep-ab` | BP | `camera_pan` at street zoom + `camera_jumps far` (docs/169 `bp-main-lots`) | Apex default vs + candidate, ABBA in one load (Apex developer controls are live) | presented p99 / p99.9, max, counts >= 25 / 50 / 100 ms; "deferred N" and largest backlog; frames until the backlog is 0 after the camera stops |
| `ip-camsweep-ab` | IP | same | same | same |

Missing for Rodney's exact case: the agent has pan and jump commands only; a zoom/rotate sweep (orbit + zoom in/out on a
dense lot) would be a new agent command (~30 min). Pass: p99 and counts >= 25 ms down outside noise, no new pop-in after
the camera stops, Apex developer checks 0. Fail: inside noise -> drop, record in docs/23.

## 7. Effort

- Step 0 read-out: one game run (~15 min) + ~30 min analysis; Apex's own runner (`ts3-apex-run`) can host it.
- B: ~30-60 min Apex patch + Apex build; A: ~2-3 h (pre-pass, threshold, keeps the lifetime guard rules) + correctness
  gates.
- Proof: smoke + two 10-min ABBA jobs per candidate (~1 h of game runs), no clean night needed.

## 8. Open questions

- I-498-3: does JobManager's main-thread queue (`[svc+0x18]` vfunc `+0x24`, called at `0x599A56`) order by a priority?
- Which resource kinds the finalize jobs on a camera sweep are (models vs textures vs impostor parts).
- Whether current Apex already leaves any moving hitch led by small-object work (step 0).
