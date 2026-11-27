# 44. Lighting, shadows, AO, post effects, Swarm VFX, lot imposters, thumbnails (M-D3D6)

Static map of TS3W.exe (Steam 1.67), done on a Ghidra copy with our symbols applied. No game runs. Sim-thread costs come only from the existing `probe-out/profile/*simstack*.jsonl` files; main-thread cost is not measured. Anchors came from docs/37 §9. Names are in `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (tag `<docs/44>`).

## 1. Summary

- **Every block here runs on the main (render) thread** [verified: call graph]. The frame entry points are `Renderer_PreRenderUpdate 0x6EBB70` (calls `LightManager_FrameUpdate 0x6B0500`) and `Renderer_RenderFrame 0x6E8810`. Their only callers are `MainLoop 0xECA960`, `WndMsg_Render 0xECA2A0`, `Frame_RenderCallback 0xECA030`, a UI path `0x7EB6D0`, and the imposter pump (§6). The sim thread reaches the renderer only through `PostRenderMessage 0x692F70` -> `RenderMessageBuffer_Post 0x593E60`, and through script icalls (Swarm, ThumbnailManager, World imposter calls).
- **Sim-thread self time of all these blocks together is under 0.1%** [measured, §8]. They are not a sim-thread stutter source. The one sim-thread cost is the **imposter wait during saves**: the sim thread blocks while the main thread builds each lot's imposter (docs/25: ~0.24 s wait + ~0.13 s build per save) [measured there].
- **Lot imposters are rebuilt on every save for each lot that passes two checks: it has a renderer (`lot+0x364`) and `lot+0x298` is set.** No "unchanged" check is in the path [verified for `0xADAF60`; what the two fields mean is inferred]. The build is a main-thread job (mask 1) with up to 8 steps. A sim-thread caller can only wait for it; a main-thread caller pumps whole frames until it finishes.

*Correction 2026-11-26 (Boundless Atlas `docs/021-s1-imposter-fog-camera.md` §3, §11 item 2): the "no unchanged check" claim is wrong. The check exists one step later: the job's first step `0xADAB00` goes through the gate `0xAD9730`, and an unchanged lot is copied, not rendered. The save-time cost measured in docs/25 is then the job round trip and the copy, or the lots that are dirty.*
- **Quality knobs** come from `GraphicsRules.sgr` options through `Config_GetBool 0x58AAD0` / `Config_GetInt 0x58ABA0` (group "Config"). LightingQuality sets the shadow map size and the light probe count, VisualEffects sets SwarmQuality and post-processing, and GeneralReflections sets the water reflections (§7).
- **Memory:** the shadow map, post targets, the imposter render target, thumbnails and screen capture are all DEFAULT pool, so they hold no copy inside the process under native D3D9 [verified flags]. The exceptions are the AO solver textures, which are MANAGED (system copy inside the process), and the imposter readback textures, which are SYSTEMMEM (temporary) [verified flags].

## 2. Lighting (`0x6AFDC0`-`0x6B6000`)

| Address | Name | Role | Thread | Tag |
|---|---|---|---|---|
| `0x6AFDC0` | `LightingSystem_Init` | Builds the lighting system. Reads `InteriorLightProbeCount` (code default 12) and calls the probe set-up `0x6B4F70` | main, at init | [verified] |
| `0x6B0500` | `LightManager_FrameUpdate` | Per-frame light update, called from `Renderer_PreRenderUpdate` and from the imposter pump | main | [verified callers; role inferred] |
| `0x6B1190` | (occluders) | `Lighting/3DDiscOccluder`, `Lighting/3DRectOccluder` | main | [verified tags] |
| `0x6B1AA0` | (LightObjectMap) | `Renderer/LightObjectMap` | main | [verified tag] |
| `0x6B3690`, `0x6B49F0`, `0x6B4F70` | (light probes) | Diffuse and specular probes, cube maps, probe vertex buffers (`Lighting/SpecVB`, `DiffuseVB`); exterior and sharp specular probes | main | [verified tags] |
| `0xADD2C0`, `0xADD440` | (LotLightingManager) | Per lot: room solvers, sunny/foggy/black-lit room sets, strobe delay | main [inferred] | [verified tags] |

External callers: 69, mostly world streaming (`World_LotStreaming`), `WorldTerrain_Update`, the loading screen and CAS scene icalls [verified, `ExtCallers`].

## 3. Shadows and gamma (`0x692000`-`0x699A00`, shadow map `0x70BD40`-`0x70C400`)

| Address | Name | Role | Tag |
|---|---|---|---|
| `0x692480`, `0x697740` | (InteriorShadow) | Interior shadow geometry, `Renderer/InteriorShadow/VertexBuffer` | [verified tags] |
| `0x6925E0`, `0x697E70`, `0x692790` | (ExteriorBlobShadowManager) | Sim and object blob/drop shadows (`simBlobShadowTexture`, `GenericBlobShadow`, `ExteriorSimDropShadow`) | [verified tags] |
| `0x699850`, `0x6998B0` | (InteriorShadowManager) | Scene objects and render data, `SimShadowDepthScale` | [verified tags] |
| `0x692E50`, `0x6940A0` | (GammaBlitter) | `NoGammaTextures`, `GammaEnable`, `GammaBlit` | [verified tags] |
| `0x70BEB0` | `ShadowMap_Create` | Shadow map: a square depth texture of `ShadowMapSize` (code default 2048) with flags `0x10` (DEPTHSTENCIL, DEFAULT), plus a color target. The color target is 1x1 when `ShadowMapAllowNullColor` is set and the format check passes, else full size. `ShadowMapForceDF16Available` picks the DF16 depth format | [verified] |
| `0x70BD40` | `ShadowMap_ColorFormat` | `ShadowColorFormatWorkaround` (Intel fix in GraphicsRules) | [verified] |
| `0x6E2760` | `RenderSystem_CreateTargets` | Calls `ShadowMap_Create` (twice, once as a retry path) and creates the default 1x1 textures | [verified callers; role inferred] |
| `0x79B450` | `ShadowMode_ReadConfig` | `RenderShadowMode` (1-4, code default 3) and `RenderShadowsEnabled`. Mode 1/2/other gives a size of 1024/512/256 for a secondary shadow buffer; which buffer was not traced | [verified reads; owner inferred] |

`PostRenderMessage 0x692F70` sits inside this address range but is the generic sim-to-render message post (108 callers). It is not shadow code.

## 4. Ambient occlusion (`0x6883A0`-`0x68C000`)

| Address | Name | Role | Tag |
|---|---|---|---|
| `0x6883A0` | `AOSolver_LoadIni` | Reads `AO.ini`: `BlurFilterSkip`, `BlurFilterWidthMeters`, `MinimumBlurWidth`, `DetailZeroBlurWidth`, `MaxGroundOcclusion`, `MaxRoofOcclusion` | [verified strings] |
| `0x689400` | `AOSolver_CreateMapTexture` | `AoSolver` texture, 1024 x N, flags 2 = **MANAGED** | [verified] |
| `0x6895E0`, `0x689EA0`, `0x68B010` | (AORoofSolver / AOSolver) | Blur filter buffers, ray results, `mAOVisValues` | [verified tags] |
| `0x68A7F0` | `AORoofSolver_CreateTexture` | Square `AoSolver` texture, MANAGED | [verified] |

This is precomputed AO (the roof and ground occlusion maps of a lot), not screen-space AO. It is reached from the wall/lighting texel code `0x6A4260` <- `0x6A2740` <- `0x6A7CF0` [verified callers]. When it runs (lot build or load) was not traced [inferred: lot build]. The range `0x688000`-`0x692000` also holds unrelated helpers, such as distant-terrain flags (`0x68E620`) and small getters with 60-100 callers each.

## 5. Post effects, reflections, screen capture

| Address | Name | Role | Tag |
|---|---|---|---|
| `0x6E9270` | `Renderer_ReadQualityConfig` | `RenderPostProcessEnabled`, `RenderReflectionsEnabled`, `ComplexPostProcessEnabled`, `WaterReflectionDetail`, `WaterReflectionAreaThreshold`, `CASLightMapWidth`, `CASLightMapFilteringEnabled`, `CASSimpleHairShaderEnabled` | [verified] |
| `0x6E05A0` | `RenderSystem_ReadConfig` | `RenderShadowsEnabled`, `HighRezTranslucency`, `WaterReflectionDetail`, `RenderForceMinspecShaders`, `DisablePerPixelFog`, `DisallowL16RTT`; dynamic VB sizes | [verified] |
| `0x6EB610` | `Renderer_InitScene` | `Renderer/Scene`, `Renderer/SceneLODScheduler`; calls `Renderer_ReadQualityConfig` | [verified] |
| `0x6E89F0` | (PostProcess) | `Renderer/PostProcess` object; capability checks `0x523860(0x8000 / 0x40000)` | [verified tag] |
| `0x6ED2B0`, `0x6EDE30` | (lighting tweaks, bloom) | `LightingTweaks`, `SimLightingTweaks1-3`, `BloomData`, `ShadowDebug` | [verified strings] |
| `0x6EF780` | `Renderer_RegisterDebugOptions` | Debug toggles (`Scene Shadow`, `Shadow Camera`, `Multithreaded Cull`, `Scene Post`); creates the water reflection manager and the gamma blitter | [verified] |
| `0x725C10` | (WaterReflectionManager) | `mReflectionMarkerMap` | [verified tag] |
| `0x706AA0` | `ScreenCapture_CreateTempTexture` | Capture-sized render target (flags 1 = RENDERTARGET, DEFAULT) | [verified] |
| `0x707410`, `0x709200`-`0x709570` | (ScreenCaptureEffects) | Effect bins, ramps, filters, `SketchEffect`; used by screenshots and by the DownloadContent export of households, lots and Sims | [verified tags, callers] |

## 6. Lot imposters (`0xAD9000`-`0xAE1000`)

**Who builds and when** [verified call graph]:
- `Lot_Save 0xACB8A0`: if `lot+0x364 != 0`, it first calls `Lot_RebuildWorldImposter 0xAB2A90` when `lot+0xBA` is set, then always calls `Lot_BuildImposter 0xAE0540` with the save package. On failure it logs "Error during construction of imposter for lot %I64u".
- `Lot_RebuildWorldImposter 0xAB2A90`: called from `World_CreateHouseboatLot`, from bulldoze and houseboat delete (through `0xACEB20`/`0xA48CC0`), from `0xD41630` and, through `0xABBF40`, from `Lot_MarkImposterDirty 0xAC3680`. It builds into the world package and then clears `lot+0xBA`.
- `Lot_MarkImposterDirty 0xAC3680` is the native side of `World.RegenImposter` (script). It sets `lot+0xBA` and the rebuild flags, and queues thumbnail work (`0xD61710`).
- `World.SetImposterMode` and camera code (`0x96A1F0`-`0x96B1C0`) only set `world+0xD8` (`World_SetImposterModeFlag 0xC5FB10`). That is the show-imposter mode, not a build.
- A debug toggle "Force Imposter Generation" is at `0xC7D8F0`.

**How it builds** [verified]:
1. `LotImposter_Build 0xADAF60` returns early unless the package is set and `lot+0x298 != 0`. For a lot that is not active (`Lot_IsActive`), it copies the lot's object ID list. It starts a timer, creates a job, sets its thread mask `+0x20 = 1` (main thread, docs/36 §3), submits it, and records whether the caller is the main thread (`GetCurrentThreadId == GetMainThreadId 0x7D99D0`).
2. `LotImposter_WaitOrPump 0xAD97E0`: on a non-main thread (the sim thread during a save, or a script icall) it calls `JobManager_Wait` -> `JobManager_WaitLocked 0x59A220`, a semaphore wait. On the main thread it loops until the job ends, running a whole frame each time: `LightManager_FrameUpdate`, `0x588E00`, `0xC7CEA0`, `Renderer_RenderFrame`.
3. `LotImposterJob_Step 0xADAE50` is an 8-state machine (prepare `0xADAB00`, child job `0xADA400`, `0xAD9E30`, child job `0xADA250`, `0xAD9780`, `0xADA570`, done, fail). Both child jobs also use mask 1, so each step runs when the main thread drains its job queue, roughly once per frame [inferred].
4. Rendering: `LotLODCreator_Render 0xADA460` -> `LotLODCreator_RenderPass 0xADFB40` -> `LotLODCreator_CreateRTT 0xADDEA0` (DEFAULT render target plus a SYSTEMMEM readback) and `LotLODCreator_CreateLODTexture 0xADE030` (SYSTEMMEM, filled by lock and copy). `LotLODCreator_BuildImposterModel 0xADFE70` / `LotLODCreator_BuildModel 0xADE200` produce `imposterTexture`, `imposterTextureAOandSI`, `imposterTextureWater` and the holiday-light strips (`0xADE580`, `0xADAD20`).

**Why saves wait:** the job needs the D3D device, which is main-thread only (docs/37), so the sim thread cannot run it. Each lot's wait is about as long as the main thread takes to run the job's steps, a few frames [inferred]. This matches docs/25's ~15 waits of ~10 ms [measured there]. Frame time, vsync and the frame limiter therefore lengthen the save wait directly [inferred].

### 6.1 Season change rebuilds (issue #82, 2026-10-29, static)

- **Script callers** [verified, decompiled]: only `SeasonalLotMarker.SetSeason` and `EventLotMarker.SetLayout` call `World.RegenImposter`; both end with `RegenImposter(lot)` + `SetImposterMode(false)` for a community or inactive lot, only when `Simulator.CheckYieldingContext` is true. `SetImposterMode` only sets `world+0xD8` (above), so the cost is the `RegenImposter` wait: 0.3-1.4 s per lot whose models are not loaded, 100-380 ms warm [community, issue #82].
- **Path** [verified]: 3 AM `LotManager.CleanUpLotsAndInventories` -> per lot `MagicallyCleanUp` (ends with the season kick) or `MagicallyChangeSeason` -> `Lot.ChangeSeason` on a 1-sim-minute yielding alarm (1 h for a community lot with Sims on it, which are sent home) -> community lot: Sims reset and sent home, `Sleep(0)` -> `UpdateSeason` -> `SetSeason`. Every kicked lot's alarm is due in the same sim minute, each in its own alarm task, so several lots can reach their rebuild in one tick [inferred: task order, not measured]. `easpreadlots` spreads the kicks a few lots per tick, which spreads the alarms the same way.
- **Rest of `SetSeason`** [verified]: opacity pass over every lot object, resets of the old season's items and of objects whose owner lot has a family inventory (then 2x `Sleep(0)`), `RemoveFromWorld` / `AddToWorld` of the seasonal items with floor cutouts, objects intersecting a new item reset and moved by `FindGoodLocationNearby` (or destroyed), map tags, `World.LotSnowUpdate` (~0 ms [community]). Terrain and weather change in `SeasonsManager`, not in this alarm. Only the rebuild is long per call [community timing]; the item swap is not measured.
- **Fix** `seasonimposter` (ts3fixes `SeasonImposter.cs`, on since 2026-11-25 as a rare-case fix, ts3fixes m1124f; `seasonimpostercold` stays off): the call is queued; warm lots rebuild one per 2 s, cold lots only with `seasonimpostercold` (nearest first) or before the next save (`DisposeSaveHook` flush, so saved imposters are current); travel flushes, world unload drops. Repros `bp-seasonimposter-loop` / `bp-seasonimposter-season`, queue `tools/harness/queues/seasons-spread.json`, self-check `tools/selftest/seasonimposter`. Not built: the native "no wait" variant (queue the job without `LotImposter_WaitOrPump`), which needs proof that the job is safe while the sim thread keeps changing the lot.
- **Default-on review** (2026-11-25, static, docs/51 §610): the hook sits on `World.RegenImposter` itself, so every managed caller is covered (game: `SeasonalLotMarker.SetSeason`, `EventLotMarker.SetLayout`; none in the NRaas sources) [verified]; the queue holds lot ids only, once each, so it is bounded by the lot count; a gone lot is dropped; live mode only, never on a worker or during a save or travel [verified]. Every save goes through `OptionsModel.SaveGame(bool, bool, bool)`, which the flush hooks [verified]; travel saves use `LoadSaveManager.SaveTravel` instead and are only flushed when the drain task sees `IsTravelling` first, which a drain parked in its 30-tick wait (only cold lots left) can miss [verified code path, miss window inferred]. Then only the in-session far view of those lots is stale: `Lot_Save` builds the imposter of each lot that passes its two checks into the save anyway (§6) [verified for normal saves, assumed for `SaveTravel`]. Lots are not Sims; the active lot is never queued (EA's own `IsCommunityLot || !IsActive` test) and a household's home lot counts as warm, so it rebuilds within 2 s. Live A/B `bp-seasonimposter-ab-m1124d` PASS (4 ids queued once per firing, flush 4, 0 dropped, 0 warm); the real season path repros found no lot to kick in that world (hollow, not a fix failure).

## 7. Swarm VFX and thumbnails

**Swarm** (`0x719000`-`0x722000`): `Services/SwarmSystem` (`0x71E430`), `Renderer/SwarmService` with a message queue (`Renderer/SwarmService/Msg`, `0x71DA00`), `Renderer/SwarmRenderer` (particles, index lists, single models; `0x720620`, `0x720C70`) and `Swarm/ResourcesBase` (`0x720440`) [verified tags]. Script drives it through the `IC_Swarm_*` icalls `0x7766E0`-`0x777490` on the sim thread. Those post to the service and the main thread renders [inferred from the Msg tag and the MainLoop callers]. `Swarm_ApplyQuality 0x9164F0` reads `SwarmQuality` (code default 3) and re-applies it to every effect when it changes [verified].

**Thumbnails** (`0xD5C000`-`0xD66000`): `ThumbnailManager` (keys, scene model, textures, pregeneration), the CAS thumbnail searches, the DownloadContent thumbnail export, and `ThumbnailRenderer`: `ThumbnailRenderer_RTT 0xD62200` (DEFAULT render target), `ThumbnailRenderer_Render 0xD62640` (shadow plane, UV scale, clear color) and `ThumbnailRenderer_CreatePrelim 0xD62950` (`PrelimThumbsTex`, `TempRTT`) [verified]. Rendering is main-thread work (`0xD62640` calls `Renderer_RegisterDebugOptions`' scene set-up). The thumbnail service also has a sim slot `0xD4D6D0` (docs/36 C7).

## 8. Config knobs (GraphicsRules.sgr -> Config)

| Option (Options menu) | Config props it sets | Read by | Cost it controls |
|---|---|---|---|
| LightingQuality Low/Med/High | `ShadowMapSize` 1024/1024/2048, `RenderShadowsEnabled` false/true/true, `InteriorLightProbeCount` 3/8/20, `LowDetailLightmaps` | `ShadowMap_Create`, `RenderSystem_ReadConfig`, `ShadowMode_ReadConfig`, `LightingSystem_Init` | Shadow map: 4 MB (1024²) or 16 MB (2048²) of depth in VRAM, plus the same again for a full-size color target when null color is not allowed [inferred bytes: 4 B/texel]. Probe count scales probe updates |
| VisualEffects Low/Med/High | `GraphicEffects` 1/2/3, `SwarmQuality` 1/2/3, `RenderPostProcessEnabled` false on Low, `ComplexPostProcessEnabled` only on High | `Swarm_ApplyQuality`, `Renderer_ReadQualityConfig` | Particle count and quality; post passes |
| GeneralReflections Off/Low/Med/High | `RenderReflectionsEnabled`, `WaterReflectionDetail` 0/0/1/2, `WaterReflectionAreaThreshold` 1000/1000/300/50 | `Renderer_ReadQualityConfig`, `RenderSystem_ReadConfig` | Water reflection pass |
| SimQuality | `CASLightMapWidth` 1024, `CASLightMapFilteringEnabled` | `Renderer_ReadQualityConfig` | CAS light map |
| AdvancedRendering off | `RenderPostProcessEnabled` false, `RenderShadowsEnabled` false | as above | Turns off post processing and shadows |
| (device rules, not in the menu) | `RenderShadowMode` 1/2/4, `ShadowMapAllowNullColor`, `ShadowMapForceDF16Available`, `ShadowColorFormatWorkaround` | `ShadowMode_ReadConfig`, `ShadowMap_Create`, `ShadowMap_ColorFormat` | Depth format; whether the shadow color target is a full-size texture |
| `AO.ini` | blur and occlusion limits | `AOSolver_LoadIni` | Quality of the precomputed AO |

[verified: GraphicsRules.sgr lines and the readers' strings and defaults.] The defaults in code apply when a prop is missing: `ShadowMapSize` 2048, `RenderShadowMode` 3, `SwarmQuality` 3, `InteriorLightProbeCount` 12.

## 9. Costs

**Sim thread** (all 35 simstack files, 10,238 samples, BP/IP/SV, mixed configs) [measured, leaf = self]:

| Block | Self | Any frame |
|---|---|---|
| AO solver `0x688000-0x68C000` | 0.03% | 0.18% |
| Shadows/gamma (without PostRenderMessage) | ~0% | ~0.04% |
| `PostRenderMessage` / `RenderMessageBuffer_Post` | 0.01% / 0.13% | 2.99% / 1.92% |
| Lights `0x6B0000-0x6B6000` | 0.01% | 0.08% |
| Renderer core `0x6E7000-0x6F2000` | 0.01% | 0.11% |
| Screen capture | 0.02% | 0.07% |
| Swarm renderer / Swarm icalls | 0% / 0.01% | 0.08% / 0.12% |
| Imposter block `0xAD9000-0xAE1000` | 0.01% | 1.27% |
| Thumbnails | 0% | 0.24% |
| Shadow map, water reflection, probe init | 0% | 0% |

Most of the "any frame" hits sit under a Mono interpreter leaf (`0xE53EB0`). Example: 116 of 130 imposter hits are `0xADD800` (lot lighting) below script frames, from `0xB01770`. These are lot-lighting queries made by script and posts of render messages (the 191 PostRenderMessage hits come from `0x9497C0`), not rendering [inferred: the sampler scans the stack, so stale return addresses are possible]. The save-time imposter wait is in docs/25, not in these files.

**Main thread:** not measured. The main-thread sampler should attribute: `Renderer_PreRenderUpdate 0x6EBB70`, `LightManager_FrameUpdate 0x6B0500`, `Renderer_RenderFrame 0x6E8810`, shadows `0x692000-0x699A00`, PostProcess `0x6E89F0` and `0x6ED000-0x6EF000`, Swarm `0x719000-0x722000`, water reflection `0x724000-0x726000`, screen capture `0x705000-0x70BE00`, imposter job `LotImposterJob_Step 0xADAE50` and LotLODCreator `0xADD800-0xAE1000`, thumbnails `0xD62200-0xD63000`, AO `0x688000-0x68C000`.

## 10. Candidates

| # | Candidate | Expected effect | Risk / blocker | Tag |
|---|---|---|---|---|
| L1 | **Skip unchanged imposters at save**: in `Lot_Save`, when `lot+0xBA` is clear and the package already holds the lot's imposter, keep the old resource instead of calling `Lot_BuildImposter` | Up to the whole imposter cost of a save, ~0.37 s (docs/25) | Changes save data, so opt-in and the full save matrix. Need to confirm how the old resource is copied and when `lot+0x298` is set | [inferred] |
| L2 | **Pipeline imposter jobs**: submit all lots' jobs before the lot loop and wait at each lot (docs/25 #3). Refinement from this map: the job is main-thread-only and each step runs about once per frame, so the gain comes from overlapping the main thread's frames with the sim thread's object writes | -0.1 to -0.25 s per save | The job captures the object ID list when it is submitted, so pre-submitting must not race with lot changes during the save | [inferred] |
| L3 | **Higher main-thread frame rate during saves** (frame limiter or vsync off while the save runs) to shorten each imposter wait | Proportional to frame time | Needs a hook on save start and end; check S3SS's tick limiter | [inferred] |
| L4 | LightingQuality Medium on weak GPUs: 1024² shadow map instead of 2048² (4x fewer texels) and 8 probes instead of 20 | Main-thread and GPU time; ~12-24 MB VRAM | Visual; measure the main thread first | [inferred] |
| L5 | `ShadowMapAllowNullColor 1` where the device allows it: drops the full-size shadow color target | ~16 MB VRAM at 2048 | GraphicsRules already sets it on some vendor paths; a device-format check guards it | [inferred] |
| L6 | AO solver textures from MANAGED to DEFAULT | Small in-process saving (system copies) | They may be read back on the CPU; trace before changing | [inferred] |
| L7 | VisualEffects/SwarmQuality: VFX cost is ~0.1% of the sim thread; any gain is on the main thread | Unknown until measured | None (setting only) | [measured sim side] |

No offload candidate for the sim thread: these blocks already run on the main thread. The imposter wait is a cross-thread wait, so it is a design change (L1-L3), not a move.

## 11. Method

Ghidra copy with symbols imported. Scripts: `StringXrefs` (tag and config-key regex), `RangeMap` and `ExtCallers` on each block, `CallerTree` (depth 4) on the imposter and renderer entry points, `DecompileTo` on the imposter path and the config readers. The sample split used the leaf TS3W frame for self time and any TS3W frame for "any frame" (scratch scripts, not committed).
