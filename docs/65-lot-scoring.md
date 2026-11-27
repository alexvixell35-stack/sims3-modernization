# 65. Lot scoring (`0xC6C290`) and its callees

Task G13 follow-up (docs/28 §5, N16). Written 2026-10-16 from a private Ghidra copy (`game-copy/ghidra-ls2`, copied from the UI4 project, `tools/ghidra/symbols/ts3w-1.67.symbols.csv` applied first). Static and data only: the game was not run for this doc. Steam TS3W.exe 1.67, image base `0x400000`, all addresses absolute.

Tags: **[verified]** = read in the decompile or disassembly; **[measured]** = from the `[LotStream]` counters (`probe-out/queue/lotstream-2026-10-16.jsonl`, docs/data/results/2026-10-16-queue-lotstream.md) or the main-thread samples of the `verify-2026-10-16` runs; **[inferred]** = reasoning, still to confirm.

## 1. Short answer

- **In live play, lot scoring is cheap.** During the 6-minute pan profiles one call costs 15 µs (BP, S3SS lot streaming on), 30 µs (BP, off) and 23 µs (IP, on) on average, and the sum stays at or below 1.6 / 3.5 / 5.5 ms in any one second (0.02-0.05% of wall time) [measured]. It is not a live stutter source; moving it to a worker or making it incremental would gain nothing measurable.
- **The 30 / 54 / 116 ms single calls happen during world load, not during the pan.** `lotScoreMaxUs` was already 30 144 (BP on), 53 780 (BP off) and 116 364 µs (IP on) in the snapshot taken before the profile started, and did not change during the profile [measured]. Mean cost per call in that pre-profile window is 289 / 629 / 573 µs, 10-20 times the live mean [measured].
- **Why a call can take tens of ms:** at the end of a scoring pass, `WorldManager_AnyActiveLotPending` `0xC69FF0` calls `SceneManager_Draw` / `Renderer_WaitFrameJobsAndDraw` `0x6F3C90` (wait for the cull jobs, cull, draw the main view) **whenever the renderer's scene rendering flag `Renderer+0x29D` is clear** [verified code]. That flag gates the scene part of `Renderer_BeginFrame` and is set through `0x6E8390` by the loading screen and the lot imposter jobs [verified writers], so it is clear while the loading screen is up [inferred]. So during a load, a scoring call may do a full extra scene draw on the main thread [inferred, matches the load-only maxima].
- **S3SS interplay:** S3SS does not patch `0xC6C290`. Its `Throttle Lot LoD Transitions = true` live setting changes which branch runs: with the throttle on, `0xC69FF0` (and with it the forced draw) only runs when a lot is due to be activated; with it off (game default), it runs at the end of **every** scoring pass that gets past the camera speed gate [verified branch]. This matches S3SS off costing about twice as much per call, live and at load [measured].

## 2. Function list

| Address | Name | Thread | What it does |
|---|---|---|---|
| `0xC6D570` | `WorldManager_Update` | main | Calls scoring at `0xC6D6E4` unless `WM+0x258` (map view, S3SS sets it) [verified]. Arguments: frame dt (float), the camera point (from `0x962300` on the active camera `0x6E8400`), `param_4` = `!camera[+0x6DF]` (`0x961490`), and the camera [verified; meaning of `+0x6DF` not traced] |
| `0xC6C290` | `WorldManager_ScoreAndStreamLots` | main | The whole pass, §3 [verified] |
| `0xC65CB0` | `WorldManager_PushCameraSpeed` | main | Appends one speed sample to the list at `WM+0x310` (free list `WM+0x318`); the pass keeps the last 10 [verified shape] |
| `0xC6A080` | `WorldManager_ActivateAllLots` | main | `WM+0xD8` set: `SetActive` on every lot (only the household lot when `WM+0xDA`), no scoring [verified code] *[Corrected 2026-11-26, Boundless Atlas docs/021 §11: deactivates every lot (`SetActive(0)`), the lot at `WM+0x170` gets 1 only when `WM+0xDA`; "ActivateAllLots" is the wrong name]* |
| `0xC68680` | `WorldManager_RebuildLotIndex` | main here | Runs when `WM+0x1CC` is dirty; rebuilds the lot vectors and a 16 m cell grid under the global lock `0x10786C8` (docs/60) [verified] |
| `0xC60D30` | `WorldManager_FindLotById` | main | Finds the lot with id `WM+0x170/+0x174` (the active household's lot [inferred]) by a linear recursive scene-node search over `WM+0x9C`, once per pass [verified] |
| `0xC7E1B0` | `LotGrid_FindLotAtPoint` | main | Lot under the camera point, on the object at `[0x11ECE58]`: rebuilds its grid when dirty (`0xC7DE60`), tests the large-lot list (`+0x38..+0x3C`), then the 16 m grid cell (`0xC7E100`) [verified] |
| `0xC7DE60` | `LotGrid_Rebuild` | main | Fills the 16 m cell grid from every lot's bounds; flag `+0xB0` [verified] |
| `0xC7E100` | `LotGrid_FindLotInCell` | main | Tests the lots of one grid cell against the point [verified] |
| `0xAB1870` | `Lot_GetStreamPriority` | main | `lot+0xC4` [verified] |
| `0xC62D80` | `WorldManager_LotScore` | main | Squared distance camera-to-lot, minus `Active Lot Bias` `WM+0xE0` for an active lot (JZ `0xC63015`). Two modes on `[0x11843F8]`: lot centre, or distance to the lot box in lot space (point times the lot renderer's matrix, then clamp to `lot+0x174` x `lot+0x178`) [verified]. Box mode is the one running in game: all 5 samples inside it return to `0xC62DB4`, the box branch [measured] |
| `0xAE1B10` | `LotRenderer_GetMatrix` | main | Lot renderer (`lot+0x364`) matrix: a 64-byte copy from `+0x1B0`, or computed through `0xAE0C20` and `0x561E60` when `+0x2A0` is set [verified code; that `0x561E60` is a matrix inverse is inferred]. Called once per lot per pass |
| `0xC66690` | `LotScores_Resize` | main | Resizes the 16-byte score array; inline buffer of 128 entries on the stack, heap (`World/WorldManager/LotScores`) above that [verified] |
| `0xC64050` | `LotScores_PartialSort` | main | `std::partial_sort` shape: `0xC63300` make-heap, sift with `0xC620F0`, `0xC63370` sort-heap. Order: higher priority first, then lower score [verified] |
| `0xC69FF0` | `WorldManager_AnyActiveLotPending` | main | Rebuild index if dirty; **draw the scene when `Renderer+0x29D` is clear** (`0x6F3C90` at `0xC6A01A`); then for each active lot: pending when `lot+0xC2` is clear or `0x700DC0(lotId)` reports pending scene models [verified code; meaning of `+0xC2` inferred] |
| `0x700DC0` | `SceneObjMgr_LotHasPendingModels` | main | Hash lookup of the lot id in the scene object manager (`0x6FDE10` returns `[0x11D1CF8]`), then `0x6EDBB0` per entry [verified shape; "pending models" inferred] |
| `0x6E8390` | `Renderer_SetSceneRenderEnabled` | main | Writes `Renderer+0x29D` (and `+0x29E`) unless `+0x290`; callers: `LoadingScreen_Present_A`, `LotImposterJob_Step` (sets 1), `0xADAB00`, `0xADA570`, `0xEC1720` [verified writers; name inferred] |
| `0xC644D0` | `Lot_DrawOutlineDebug` | main | Lot outline lines (`World/LotOutlineEAVerts`, colours by `lot+0x408`); only when `WM+0x1B4 == 2` [verified code; debug-only inferred] |
| `0xAC20E0` | `Lot_SetActive` | main posts | Unchanged from docs/28 §5.2 step 3: posts the add/remove to the sim thread and sends lot message `0x421FEFD` synchronously [verified] |

## 3. One scoring pass, step by step

1. Store the new camera point at `WM+0x3A0`. Return at once if `WM+0xD9`. If `WM+0xD8`: `ActivateAllLots` and return [verified].

*Correction 2026-11-26 (Boundless Atlas `docs/021-s1-imposter-fog-camera.md` §1, §11 item 1): with `WM+0xD8` set, `0xC6A080` deactivates every lot, it does not activate them; "ActivateAllLots" is the wrong name.*
2. **Camera speed gate** (only when `param_4`): speed = distance moved / dt (0 when dt is tiny), pushed into the 10-sample list; when the mean is **above** `Camera speed threshold` `WM+0xEC`, return [verified]. The pan profiles hit this path most of the time, which is why the live mean is ~15 µs [inferred].
3. Rebuild the lot index if dirty (takes lock `0x10786C8`) [verified].
4. Size the score array to the lot count (`WM+0x204..+0x208`). Priority 4 = the household lot (`FindLotById`), 3 = the lot under the camera (`LotGrid_FindLotAtPoint`, only with `param_4` or `WM+0xDA`), else `lot+0xC4`; with `param_4`, a priority below `Force LoD Min Priority` `[0x118432C]` becomes 0 [verified].
5. **Per lot** (skipping `lot+0xC9`): `LotScore` (one lot-matrix fetch, a 4x4 transform and a clamp) and one 16-byte array write [verified].
6. Keep count = `Max Active Lots` `WM+0xE4`, adjusted by the number of priority lots, capped at the lot count; `partial_sort` the array to that count [verified].
7. **Second per-lot loop:** `SetActive(0)` for lots past the keep count, or priority 0 and score >= `Lot LOD dist.`^2 (`WM+0xDC`); otherwise count it and, if inactive, `SetActive(1)`. With the throttle (`[0x11ECBC0]` and `WM+0xE4 <= [0x1184328]`), only the first inactive lot of the pass may be activated, and only when `AnyActiveLotPending` says no active lot is still pending (call at `0xC6C73B`) [verified]. `Lot_SetActive` returns at once when the state is unchanged, so most of these calls are a two-byte compare [verified].
8. Debug outline when `WM+0x1B4 == 2` [verified].
9. **End:** throttle on: `WM+0xDB` = "a lot was held back"; throttle off: `WM+0xDB` = `AnyActiveLotPending()` (call at `0xC6C7D7`), which runs every pass that got past step 2 [verified]. Free the heap score array if one was used.

## 4. Cost drivers

| Driver | Per pass | When | Evidence |
|---|---|---|---|
| **Forced scene draw** in `0xC69FF0` (`0x6F3C90`: wait for up to five cull jobs, cull, draw the main view) | one full main-view draw | Only while `Renderer+0x29D` is clear; throttle off: every pass past the speed gate; throttle on: only when a lot waits for activation | [verified code]; flag clear under the loading screen [inferred]; matches max 30-116 ms only before the profile [measured] |
| Per-lot `LotScore` (matrix fetch, possibly computed through `0x561E60`, 4x4 transform) | N lots | Every pass past the speed gate | 5 of the 7 main-thread samples in the scoring chain are in `0xAE1B10` [measured] |
| `AnyActiveLotPending` loop (hash lookup per active lot, `0x6EDBB0` per entry) | active lots | as the forced draw | Explains S3SS off at about 2x the live mean (30 vs 15 µs) [inferred] |
| `partial_sort` | N log k | every pass past the gate | 1 sample [measured] |
| `RebuildLotIndex` / `LotGrid_Rebuild` | N lots x cells, under a global lock | only when dirty (lot added/moved, world load) | [verified]; lock contention with the sim thread not measured |
| `FindLotById` | linear recursive scene search | every pass | 0 samples [measured] |
| `SetActive` posts and lot message `0x421FEFD` | per lot that changes | rare (`lotSetOn` 0-8 in a pan) | [measured] |

Main-thread samples (`probe-out/profile/verify-2026-10-16-*-mainstack.jsonl`, 7-min loads with `[LotStream]` off): BP 1 of 1 966 samples inside the scoring chain, IP 6 of 1 942 (0.3%): 5 in `LotRenderer_GetMatrix` via `LotScore` (BP 1, IP 4), 1 in `LotGrid_FindLotAtPoint`, 1 under the partial sort [measured; the sampler scans the stack, so outer frames can be stale]. Almost all other `WorldManager_Update` samples (BP 29 in all) return to the `WorldTerrain_Update` call at `0xC6D68F`, not to lot scoring [measured]: on the main thread the world service cost is terrain, not lots.

Call rate: 16-24 scoring calls per second during the pans (5 640 / 352 s, 6 025 / 353 s, 8 278 / 345 s) [measured], so `WorldManager_Update` does not run every rendered frame, or the hook missed nested calls [inferred; not checked].

## 5. S3SS interplay

| S3SS piece (docs/03 §1, docs/28 §5.4) | Touches scoring? | Effect on `0xC6C290` |
|---|---|---|
| `Throttle Lot LoD Transitions` = true, threshold 12 (live settings `[0x11ECBC0]`, `[0x1184328]`) | **Yes, by data** | Moves `AnyActiveLotPending` (and the forced draw) from every pass to "only when a lot is due"; at most one activation per pass [verified branch]. Halves the live mean, and the load-time mean and max (289 vs 629 µs, 30 vs 54 ms) [measured] |
| Visibility override, JZ -> JMP at `0xC63015` | Yes, one byte | Inside `LotScore`: no `Active Lot Bias` for active lots, so less hysteresis [verified]; no cost effect |
| Map-view blocker (`WM+0x258`) | Skips the call | No scoring while the map view is open [verified gate] |
| Object throttle, detour of `0xAC1130` | No (sim thread) | Explains the extra `lotAdds` (4 617 in BP on vs 2 off, all before the profile): S3SS splits the world-load object adds into re-posted chunks (`lotSetOn` stays 2). The pump runs until its queue is empty, and the re-posts land in the same pass: 194 passes for 4 617 adds, up to 34 in one pass, `pumpMaxUs` 60 ms on vs 33 ms off [measured counts; same-pass mechanism inferred, docs/28 §5.4 risk]. That is the sim-thread side (N16), not scoring |

## 6. Candidates

1. **Measure the forced draw (probe, no behaviour change).** In `[LotStream]`, also time `0xC69FF0` and count the calls where `[Renderer_Get()+0x29D]` (renderer global `[0x11D1860]`) was 0 at entry, and split `lotScoreUs` into "scene render off" and "on". Repro: any world load plus `lotstream-pan` (queue `lotstream.json`, BP on/off). Confirms or refutes §1 bullet 3. New counter slots need a registry reservation first (docs/registry.md, counters after v19).
2. **Rate-limit the load-time draw (only if 1 confirms it).** Skip the `0x6F3C90` call in `0xC69FF0` when the main loop already drew in this frame, or allow it once per N ms. Gain at most the load-time scoring total: 0.49 s (BP on), 0.99 s (BP off), 0.77 s (IP on) per load [measured upper bound]. **Risk:** the draw probably exists so scene models request their resources while the loading screen hides the scene; skipping it could leave lots pending longer and lengthen the load [inferred]. A/B on load time with a runtime switch, plus a check that no lot stays on its imposter after load.
3. **Turn the throttle on without S3SS (config only).** Setting `[0x11ECBC0]` = 1 gives the cheaper branch and one activation per pass; it is what S3SS does already, so only relevant for setups without S3SS. No code change for us.
4. **Rejected: worker offload or incremental scoring.** Live cost is 15-30 µs per call and at most 5.5 ms per second [measured]. Inputs are not thread-safe anyway: lot fields (`+0xC1`, `+0xC4`, `+0xC9`, bounds, lot renderer matrix) are written by the sim thread during `Lot::Load` and by `SetActive`; the lot vectors are rebuilt under lock `0x10786C8` but read without it (docs/60); `SetActive` and its synchronous lot message must stay on the main thread [verified call shape]. Caching the per-lot matrix (`0xAE1B10`) would save a few µs per pass; not worth a hook.
5. **Repro gap.** In the pans, `lotScoreChanged` moved by 0 (BP on), 2 (BP off) and 13 (IP) [measured]: the pan mostly stays above the camera speed threshold, so scoring returns at step 2. To exercise activation, the agent's `camera_pan` should stop for a few seconds over each new block of lots (below `WM+0xEC`). Worth doing before any N16 pump work, not for scoring.

## 7. Open items

- Meaning of `camera+0x6DF` (`param_4` = not set) and of `WM+0xD8/+0xD9/+0xDA` [to find].
- Whether `0x6F3C90` from `0xC69FF0` really runs at load (candidate 1) and how long one such draw takes.
- Defaults of `Max Active Lots` `WM+0xE4` and `Camera speed threshold` `WM+0xEC` (registered by `0xC671E0`, values not read).
- Lot count per world (decides whether the 128-entry inline score buffer spills to the heap).

## 8. Names

18 new rows in `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (tag `<docs/65>`), applied to `game-copy/ghidra-ls2`. No existing row changed.
