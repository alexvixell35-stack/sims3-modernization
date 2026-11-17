# 37. D3D9 device wrapper (M-D3D1)

Static map of how TS3W.exe 1.67 (Steam) creates and drives its Direct3D 9 device. Ghidra on a copy of the exe (`game-copy/ghidra-d3d1`), no game run. Names are in `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (88 rows, tag `<docs/37>`). Tags: [verified] = decompiled or disassembled; [measured] = earlier game measurement; [inferred] = not confirmed yet.

## 1. Summary

- **One device wrapper, one raw device.** EA's wrapper object lives inside the `Services/Canvas` service (vtable `0xFCBD84`, wrapper at service +0x10). The singleton pointer is `[0x11CECC8]` (`GraphicsDevice_Get 0x60E2C0`). The raw `IDirect3DDevice9*` sits at wrapper +0x0C and in `[0x11CECCC]`, `IDirect3D9*` at wrapper +0x08 and in `[0x11CECD0]` [verified].
- **Almost all D3D calls go through the wrapper block `0x60E000-0x61B000`** (state cache, draw calls, reset, resource wrappers for textures, surfaces, VB/IB). Outside it, only the shader block `0x799000-0x7A0000` and about 12 renderer functions call the device directly (§4) [verified for every function that reads the device from one of its four sources, see §8].
- **Device creation:** `Device_Create 0x611080`: `Direct3DCreate9(D3D_SDK_VERSION 0x20)`, `CreateDevice(adapter, D3DDEVTYPE_HAL, hwnd, flags, pp, &dev)`. Flags are only `D3DCREATE_HARDWARE_VERTEXPROCESSING` (0x20) when `D3DDEVCAPS_HWTRANSFORMANDLIGHT` is set, else `SOFTWARE_VERTEXPROCESSING` (0x40). **No `D3DCREATE_MULTITHREADED`, no `FPU_PRESERVE`, no `PUREDEVICE`** [verified: disassembly at `0x6112A4`-`0x6112B9`].
- **Thread:** the device belongs to the thread that creates it (id saved at wrapper +0x10, checked by `Device_IsOwnerThread 0x60E820`). Creation runs from the service init at app start and every BeginScene/Present runs on the main thread [verified for Present: docs/04 F19; creation thread inferred from the init chain]. The sim thread `[0x11D8A8C]` has no lock it could use: the wrapper has no critical sections [verified: no lock or thread API in the block except `GetCurrentThreadId` in `0x60E820` and `0x611080`].
- **VRAM detection is broken in two ways** (§6): it reads DxDiag `szDisplayMemoryEnglish` for the adapter whose GUID matches D3D9's, and converts MB to bytes in 32 bits, so the value wraps at 4096 MB (8, 12, 16 GB cards read as 0). Under DXVK the GUID never matches, so it reads 0, which explains the measured 32 MB fallback (docs/05) [verified code; GUID cause inferred].
- **`GetAvailableTextureMem` and `EvictManagedResources` are never called** [verified within the scanned set, §8].
- **d3dx9_31** is used only by the null device (`D3DXGetShaderSize`) and by CAS (`D3DXFillVolumeTexture`) [verified].

## 2. Device creation (`Device_Create 0x611080`, called by `Canvas_InitDevice 0x588B60`)

`Canvas_InitDevice` (Services/Canvas vtable slot +0x10) stores the wrapper with `GraphicsDevice_Set 0x60E2D0`, sets the mode through small setters (`0x60E550` size, `0x60E540` windowed, `0x60E680`, `0x60E520`, `0x60F700`), reads `[Config] FSAALevel` (`0x60E650`), then calls `Device_Create(fake)` with fake = Services/Canvas +0x15C4 [verified].

Steps in `Device_Create` [verified]:
1. Allocates `Services/Device/PrivateData` (0x1CC bytes, wrapper +0xE0) and `Services/Device/StateStack`.
2. Fills `D3DPRESENT_PARAMETERS` at PrivateData +0x14C (table below).
3. `IDirect3D9`: reuses `[0x11CECD0]` (created early by `D3D9_CreateGlobal 0x60E700` from app init `0xECB000`), else `Direct3DCreate9(0x20)`; with fake = 1 it uses the null `D3DFakeW32` (`0x611D30`, vtable `0xFE7BC8`) and `D3DDeviceFakeW32` (`0x611DA0`, vtable `0xFE7C10`).
4. Adapter = wrapper +0x3C; `GetAdapterCount`; FSAA checked with `CheckDeviceMultiSampleType` (`0x60E590`, falls back with `0x60E5F0`).
5. `GetDeviceCaps(adapter, HAL)`; behaviour flags 0x20 or 0x40 (above); `CreateDevice`; wrapper +0x44 counts creations; `GetAdapterIdentifier` (VendorId kept at wrapper +0xF54) [inferred field: the stack offset lines up with VendorId].
6. Backup copy of the present parameters at PrivateData +0x184 (used by Reset); `[0x11CECCC]` = device; wrapper +0x10 = `GetCurrentThreadId()`.
7. `GetDeviceCaps` (device copy at PrivateData +0x1C), `GetDisplayMode`, ATI instancing probe `Device_DetectAtiInstancing 0x60E740` (`CheckDeviceFormat` FOURCC `INST`, then `SetRenderState(D3DRS_POINTSIZE, 'INST')`), `CreateQuery(D3DQUERYTYPE_EVENT)` support flag at wrapper +0xD5.
8. Wrappers: `Services/Device/BackBuffer` +0x90, `DepthBuffer` +0x94 (`0x60F430`), `ImmVB` (`0x60F350`), `ImmQuadIB` +0x88 (a static quad index list), `VertexFormatCache`.

| Present parameter | Value | Source |
|---|---|---|
| BackBufferWidth/Height | wrapper +0x2C / +0x30 | mode setters |
| BackBufferFormat | `D3DFMT_A8R8G8B8` (0x15) | constant |
| BackBufferCount | 0 (= 1) | constant |
| MultiSampleType | `tbl_MultiSampleTypes 0xFE7A60[+0x38]` | FSAALevel |
| SwapEffect | `COPY` (3) if wrapper +0x26, else `DISCARD` (1) | flag |
| hDeviceWindow | wrapper +0x28 | |
| Windowed | wrapper +0x24 | |
| EnableAutoDepthStencil / format | 1 / `D3DFMT_D24S8` (0x4B) | constant |
| Flags | 0 (no `LOCKABLE_BACKBUFFER`) | constant |
| FullScreen_RefreshRateInHz | 0 when windowed, else wrapper +0x34 | |
| PresentationInterval | `ONE` if wrapper +0x25 (vsync), else `IMMEDIATE` | option |

## 3. Wrapper functions (`0x60E000-0x61B000`, ~336 functions, 506 external callers)

| Group | Functions |
|---|---|
| Lifetime | `Device_Create 0x611080`, `Device_Destroy 0x610960`, `D3D9_CreateGlobal 0x60E700`, `D3D9_ReleaseGlobal 0x60E720`, `D3D9_GetGlobal 0x60F730`, `Device_GetD3DDevice 0x60F740` (raw device getter), `GraphicsDevice_Get 0x60E2C0` / `_Set 0x60E2D0` |
| Frame | `Device_BeginScene 0x611620`, `Device_Present 0x611680`, `Device_EndSceneAndPresent 0x611760` (names from docs/04 F19) |
| Lost/reset | `Device_TryReset 0x610AB0`, `Device_NotifyLost 0x610590`, `Device_ReleaseDefaultPool 0x610850`, `Device_ApplyModeAndReset 0x6115C0`, `Canvas_ApplyPendingMode 0x588E20` |
| State (cached) | `Device_SetRenderStateCached 0x60E990` / `_B 0x60E9E0`, `Device_SetSamplerStateCached 0x60EA50`, `Device_ApplySamplerAndRenderStates 0x60EAD0`, `Device_SetTexture_A/B/C 0x60ED60/0x60EDA0/0x60EDE0`, `Device_ReadBackStateCache 0x60F4C0`, `Device_UnbindShadersAndStreams 0x60E7A0` |
| Targets | `Device_SaveTargets 0x60E2E0`, `Device_RestoreTargets 0x60E410`, `Device_SetRenderTarget 0x60EF20`, `Device_SetDepthStencil 0x60EF70`, `Device_SetViewport 0x60FFE0`, `Device_SetScissorRect 0x60EE50`, `Device_SetClipPlane 0x60EE90`, `Device_Clear 0x60F270`, `Device_CopyRenderTarget 0x610660` (readback) |
| Geometry | `Device_SetVertexDeclaration 0x60EFB0`, `Device_SetStreamSource 0x60F020`, `Device_SetIndices 0x60F0F0`, `Device_DrawPrimitive 0x60F150`, `Device_DrawIndexedPrimitive 0x60F180`, `Device_DrawIndexedInstanced 0x60F1D0`, `Device_ImmQuadFlush 0x60FF40` |
| Resources | `Texture_ctor 0x619BC0`, `Texture_Init 0x619980`, `Texture_CreateD3D 0x619540`, `Texture_LockRect 0x618DF0`, `Texture_UnlockRect 0x619160`, `Texture_GetSurfaceLevel 0x6194B0`, `Surface_CreateRTorDS 0x617C50`, `Surface_StretchRect 0x617DE0`, `VertexBuffer_CreateD3D 0x61A1C0` / `_B 0x61A2A0`, `IndexBuffer_CreateD3D 0x616C40`, `IndexBuffer_Lock 0x616BB0` / `_Unlock 0x616C10`, `VertexDecl_Create 0x61A010` |
| Caps / config | `DeviceCaps_Probe 0x613CB0`, `Adapter_EnumModes 0x612EB0`, `VRAM_DxDiag 0x612640`, `VRAM_DirectDraw 0x612130`, `NvCpl_Query 0x612050`, `DeviceInfo_FormatDriver 0x613210`, `Device_CheckTextureFormat 0x60F5F0`, `Device_IsMultiSampleSupported 0x60E590`, `Device_IsOwnerThread 0x60E820` |

Resource wrappers keep the D3D interface at +0x30 (textures: `LockRect` +0x4C, `UnlockRect` +0x50, `GetSurfaceLevel` +0x48) and +0x14 (VB/IB: `Lock` +0x2C, `Unlock` +0x30) [verified call shapes]. Device listeners live on two intrusive lists in the wrapper: +0xD4/+0xD8 (slot +0x14 = on lost, +0x10 = on reset, returns bool) and +0xE0/+0xE4 (slot 0 = lost notify, slot +4 = reset done) [verified].

## 4. Call sites per D3D method

`IDirect3DDevice9` slots from the public d3d9.h order. "Wrapper" = inside `0x60E000-0x61B000`.

| Slot | Method | Callers |
|---|---|---|
| +0x08 | Release | `Device_Destroy` |
| +0x0C | TestCooperativeLevel | `Device_TryReset` |
| +0x1C | GetDeviceCaps | `Device_Create` |
| +0x20 | GetDisplayMode | `Device_Create`, `0x70BD40` (RenderSystem) |
| +0x30 | ShowCursor | `0x70BD40` |
| +0x40 | Reset | `Device_TryReset` only |
| +0x44 | Present | `Device_Present` only |
| +0x54 | SetGammaRamp | `Device_ApplyGammaRamp 0x60F630`, `Device_TryReset` |
| +0x5C/+0x60/+0x64 | CreateTexture / Volume / Cube | `Texture_CreateD3D 0x619540` only |
| +0x68 | CreateVertexBuffer | `0x61A1C0`, `0x61A2A0` |
| +0x6C | CreateIndexBuffer | `0x616C40` |
| +0x70 | CreateRenderTarget | `0x60F350`, `0x617C50`, `0x6E3D60` (RenderSystem) |
| +0x74 | CreateDepthStencilSurface | `0x617C50` |
| +0x7C | UpdateTexture | `0x60EC10` |
| +0x80 | GetRenderTargetData | `0x610660`, `0x5FBCE0` (CAS), `0x6B4B10` (lights), `0x9DE890` (video capture, docs/48) |
| +0x88 | StretchRect | `0x610660`, `0x617DE0`, `0x6EB990` (Scene), `0x709570` (ScreenCaptureEffects) |
| +0x8C | ColorFill | `0x6ED0A0` (SceneManager) |
| +0x90 | CreateOffscreenPlainSurface | `0x9DEA80` (video capture, docs/48) |
| +0x94/+0x98 | Set/GetRenderTarget | wrapper (`0x60E410`, `0x60EF20`, `0x60E2E0`, `0x60F430`, `0x610660`), `0x6945C0`, `0x6EB990`, `0x9DF410` |
| +0x9C/+0xA0 | Set/GetDepthStencilSurface | wrapper (`0x60E410`, `0x60EF70`, `0x60E2E0`, `0x60F270`, `0x60F430`) |
| +0xA4 / +0xA8 | BeginScene / EndScene | `Device_BeginScene` / `Device_Present` |
| +0xAC | Clear | `Device_Clear` |
| +0xBC/+0xC0 | Set/GetViewport | wrapper |
| +0xDC/+0xE0 | Set/GetClipPlane | wrapper |
| +0xE4/+0xE8 | Set/GetRenderState | wrapper (`0x60E740`, `0x60E990`, `0x60E9E0`, `0x60EAD0`, `0x60F270`; get in `0x60F4C0`) |
| +0x104 | SetTexture | wrapper (`0x60ED60`, `0x60EDA0`, `0x60EDE0`, reset) |
| +0x110/+0x114 | Get/SetSamplerState | wrapper |
| +0x12C/+0x130 | Set/GetScissorRect | wrapper |
| +0x144 / +0x148 | DrawPrimitive / DrawIndexedPrimitive | wrapper (`0x60F150`, `0x60F180`, `0x60F1D0`, `0x60FF40`) |
| +0x158/+0x15C | CreateVertexDeclaration / Set | `0x61A010` / wrapper |
| +0x16C / +0x1A8 | CreateVertexShader / CreatePixelShader | `Shader_CreateVertexShader 0x799780` / `Shader_CreatePixelShader 0x799810` |
| +0x170 / +0x1AC | SetVertexShader / SetPixelShader | `Shader_Bind 0x79E400`, `0x60E7A0` |
| +0x178 / +0x1B4 | Set VS / PS ConstantF | `Shader_SetFloatConstants 0x79DFE0`; VS also `0x696980` (InstancingEmulation) |
| +0x188 / +0x1C4 | Set VS / PS ConstantB | `Shader_SetBoolConstants 0x79D2A0` |
| +0x190 / +0x198 / +0x1A0 | SetStreamSource / Freq / SetIndices | wrapper |
| +0x1D8 | CreateQuery | `Device_Create` |
| never | GetAvailableTextureMem (+0x10), EvictManagedResources (+0x14), DrawPrimitiveUP/IndexedUP (+0x14C/+0x150), CreateStateBlock (+0xEC), SetFVF (+0x164), ProcessVertices | [verified in the scanned set] |

`IDirect3D9` slots: GetAdapterCount, GetAdapterIdentifier, GetDeviceCaps, CreateDevice (`Device_Create`, `DeviceCaps_Probe`); GetAdapterModeCount/EnumAdapterModes (`Adapter_EnumModes`); CheckDeviceFormat (`0x60E740`, `0x60F5F0`); CheckDeviceMultiSampleType (`0x60E590`, `0x60E5F0`); GetAdapterMonitor (`0x60F5E0`, `0x6103B0`) [verified].

**Callers of the wrapper by subsystem** (506 external callers, by address block; nearest named ancestors from `ExtCallers`): renderer core `0x68-0x72` (~230), shaders `0x79` (10), UI/world picking `0x7C-0x80` (`IC_World_GetScreenSpacePosImpl`, `IC_World_PickFloorAtWindowPoint`, SACS), SceneManager `0x9D` and video capture `0x9DE000-0x9DF000` (18 together; docs/48), world, lots, roofs, water, terrain `0xA4-0xC8` (~160, reached from `WorldManager_Update` and `World_LotStreaming`), thumbnails and CAS images `0xD2-0xD6` (~30, reached from `IC_ThumbnailManager_*` and `IC_UIManager_Get*ThumbnailImageImpl`), app/loading `0xEC` (12) [verified call graph].

## 5. Device lost and reset (crash relevance)

[verified unless marked]
- `Device_Present`: optional pre-present callback (wrapper +0xEC/+0xF0); EndScene; `Present(NULL, NULL, hwnd, NULL)`. On `D3DERR_DEVICELOST` (0x88760868) it sets the lost flag (wrapper +0x8D) and calls `Device_NotifyLost`. On **any** failure it then calls `Device_TryReset(0)`, so a lost device is retried once per frame. With the lost flag set, Present is skipped and only the retry runs.
- `Device_BeginScene`: returns false while lost (the frame is skipped); if BeginScene itself fails it calls `Device_TryReset(0)` up to 2 times.
- `Device_CopyRenderTarget 0x610660` (readback) also sets the lost flag on DEVICELOST.
- `Device_TryReset(force)`: records the time; unless forced, returns at once when the game is fullscreen (backup Windowed = 0) and the foreground window belongs to another process (alt-tab). `TestCooperativeLevel`: any error other than `D3DERR_DEVICENOTRESET` (0x88760869) returns false (wait). Then: lost listeners (+0x14), `Device_ReleaseDefaultPool`, identity gamma ramp (fullscreen), `Reset` with the stored parameters (HRESULT kept at PrivateData +0x1C8), restore backup parameters, re-wrap back and depth buffers, re-read the viewport, unbind 16 textures, 2 streams and the index buffer, recreate ImmVB, reset listeners (+0x10). Only if every listener returns true is the lost flag cleared; then reset-done listeners run and, if wrapper +0x8E is set, a message `0x76CBB23D` is posted (mode change done).
- `Device_ApplyModeAndReset 0x6115C0` (from `Canvas_ApplyPendingMode 0x588E20` and RenderSystem `0x6E31E0`) is the resolution/window-mode change path: forced reset.

Crash and freeze relevance:
1. **A failed Reset leaves the game rendering nothing, retried every frame.** Reset fails with `D3DERR_INVALIDCALL` while any `D3DPOOL_DEFAULT` resource is alive [community: D3D9 rules]; a listener that does not release one turns every alt-tab or display change into a black, frozen-looking screen. Same when a listener's OnReset returns false [inferred].
2. **Device removed or driver error (TDR) never recovers:** `TestCooperativeLevel` then returns `D3DERR_DRIVERINTERNALERROR` or similar, which the code treats as "wait" forever, and the lost flag is not set because Present did not return DEVICELOST [inferred from the HRESULT checks].
3. **No `D3DCREATE_MULTITHREADED`:** any device call from a thread other than the creator is unsynchronised. Thumbnail and UI-image ICs reach texture lock/unlock (`Texture_FillLevels 0x618570`; `0x619770` is `Texture_ClampToCaps`, a size clamp with no device call, not texture creation: correction from docs/43); if an IC runs on the sim thread and does not hand the work to the main thread, that is a race with rendering [inferred: the hand-off was not traced; open item for M-D3D5].
4. **No `D3DCREATE_FPU_PRESERVE`:** native D3D9 switches the creating (main) thread's x87 FPU to single precision [community: D3D9 docs]. The sim thread is not affected; main-thread maths (camera, lot scoring) runs at 24-bit precision [inferred].

## 6. GPU and VRAM detection (feeds GraphicsRules.sgr)

`DeviceConfig_Init 0x5928F0` (Services/DeviceConfigManager, from app init `0xECB000`) opens `DeviceConfig.log` and `Options.ini`, detects Transgaming/Mac (`DeviceConfig_DetectTransgaming 0x58E110`: ntdll `IsTransgaming`, `TGGetOS("MacOSX")`), calls `DeviceCaps_Probe 0x613CB0`, then evaluates `GraphicsRules.sgr` (`0x5902B0`) and writes `Options.ini` [verified calls; the rules evaluator was not read].

`DeviceCaps_Probe` [verified]: temporary `IDirect3D9`; adapter count check; `GetAdapterIdentifier`; vendor 0x10DE, 0x12D2 or 0x10B4 (NVIDIA ids) runs `NvCpl_Query 0x612050` (`NVCPL.dll` `NvCplGetDataInt`, absent on current drivers); `GetDeviceCaps`; display modes (`Adapter_EnumModes`) for A8R8G8B8 then X8R8G8B8 at 1024x768, then 1024x600 (no mode: probe fails); stores the shader version bytes and a caps bit; VRAM = `VRAM_DirectDraw` on Mac, else `VRAM_DxDiag`; result at DeviceConfigManager +0xC4.

`VRAM_DxDiag 0x612640` [verified]: `CoCreateInstance(DxDiagProvider)`, container `DxDiag_DisplayDevices`, for each device compares `szDeviceIdentifier` (as a GUID) with `D3DADAPTER_IDENTIFIER9.DeviceIdentifier` (+0x438); on a match parses `szDisplayMemoryEnglish` with `atof` and returns `(((int)MB + 4) << 20) & 0xFF800000) >> 20` (label `VRAM_DxDiag_round 0x6128C5`: `ADD EBP,4; SHL EBP,0x14; AND EBP,0xFF800000`).
- **Wrap at 4 GB:** the byte count is a 32-bit register, so the MB value is taken modulo 4096 and rounded down to 8 MB: 6144 MB reads as 2048, 8192 / 12288 / 16384 MB read as 0 [verified arithmetic]. `szDisplayMemoryEnglish` on WDDM is the total (dedicated + shared) memory, so the reading on a modern laptop is effectively random [community: DxDiag semantics].
- **No match = 0:** when no DxDiag display device has the D3D9 adapter GUID, the result stays 0. DXVK reports its own adapter identity, so the match fails and the game reads 0 MB, which `GraphicsRules.sgr` turns into the 32 MB fallback measured in docs/05 [verified symptom, cause inferred: compare the GUID line in `DeviceConfig.log` with `dxdiag /t` under DXVK to confirm].
- `textureMemory` only selects the preset tier (thresholds 112/240/368 MB, docs/05) [measured]; the fixes layer already sets the fallback to 1024 MB (`graphicsrules-vram`).

## 7. d3dx9_31

| Import | Caller | Use |
|---|---|---|
| `D3DXGetShaderSize` | `FakeDevice_CreateVertexShader 0x611F40`, `FakeDevice_CreatePixelShader 0x611FA0` | size of shader bytecode for the null device (`Graphics/FakeVertexShader`, `FakePixelShader`) |
| `D3DXFillVolumeTexture` | `CAS_HSVToRGBOp_FillLUT 0x5F9A10` | fills a 16x16x16 HSV-to-RGB lookup volume texture once (`CAS/HSVToRGBOp/Texture`), callback `0x5F95C0` |

Both are trivial to replace for an x64 build or to drop (docs/32) [verified].

## 8. Method and limits

- Scripts (in `game-copy/d3d1-work/`, not committed): `VCalls` (decompiles a set of functions and classifies every computed call by the object it is made on: `[P0+0xC]` in the wrapper, `[GraphicsDevice_Get()+0xC]`, `[0x11CECCC]`, `Device_GetD3DDevice()`), `ImpCallers` (import callers), `FixOrphans` (creates the 22 functions Ghidra had missed around device references, e.g. `0x617C50`, `0x799780`), `InsnScan` (program-wide `call [reg+disp]` and `mov reg,[reg+disp]; call reg` by slot), `RefsTo`, `Asm`; plus the committed `BoundaryCalls`, `ExtCallers`, `DumpPtrs`, `DecompileTo`.
- Scanned set: `0x5F0000-0x730000`, the callers of `GraphicsDevice_Get`, `Device_GetD3DDevice`, `D3D9_GetGlobal`, the readers of the three globals, the shader block. Code that copies the device pointer into its own object would be missed; the program-wide slot scan for the device-only slots (+0x144 and up) found no other device users (its other hits were XML, routing and other vtables) [verified]. Slots below +0x100 cannot be checked program-wide this way.
- `Device_IsOwnerThread` callers: `0x7DBBE0`, `0x7E7610`, `0xB32DE0` (code that checks before touching the device).

## 9. Anchors for M-D3D2 to M-D3D6

From the allocator tags (`Renderer/...`) and the call graph [verified tags, block roles inferred from tag names]:

| Row | Anchors |
|---|---|
| **D3D2 frame loop** | `Frame_RenderAndPresent 0xEC9F00`, main loop `0xECA960`, `Device_BeginScene` callers = render passes: `0x5FC170` (CAS), `0x6945C0`, `0x699300` (shadows), `0x6B31D0`/`0x6B32F0`/`0x6B3510` (lights), `0x6DFEC0`, `0x6EEF40` (scene), `0x9DA020` (SceneManager), `0xADDEA0` (lot imposters), `0xB63C50` (water), `0xC296E0`/`0xC2FEE0`, `0xD62950`/`0xD64730` (thumbnails), `0xEC56F0`/`0xEC6640` (loading); RenderSystem `0x6E0000-0x6E4000` and `0x70B000-0x70C000`; Services/Canvas vtable `0xFCBD84`; `SimRender_TripleBufferSync 0x593590` |
| **D3D3 scene and culling** | SceneObjectManager `0x6DE000`, `0x705000`; SceneCullFilter `0x6DF000`, `0x6F1000`; SceneManager `0x6ED000-0x6F3000` (`0x9DE000-0x9DF000`, listed here before, is video capture `VideoRecording/VidCapEngine`, `VideoCapture_RenderFrame 0x9DE140`, not SceneManager: correction from docs/48); SceneLODScheduler `0x6E7000`; SceneModel/Instance/Array `0x6F5000-0x703000`; maps incl. `mSpeedTreeClusters` `0x703000`; FixupLOD family `0x6FE000-0x700000` |
| **D3D4 materials and shaders** | Shader block `0x799000-0x7A0000` (`Shader/*` tags, `Shader_CreateVertexShader 0x799780`, `Shader_CreatePixelShader 0x799810`, `Shader_SetBoolConstants 0x79D2A0`, `Shader_SetFloatConstants 0x79DFE0`, `Shader_Bind 0x79E400`); Material, MaterialShaderBinding `0x6C9000-0x6CE000`; `sShaderIncludeTable 0x6F7000` |
| **D3D5 textures and uploads** | Texture wrapper `0x618D00-0x61A000` (`Texture_CreateD3D 0x619540`, lock/unlock), VB/IB `0x616B00-0x617600`, `0x61A010-0x61A400`; TextureFactory `0x722000`, RadTextureFactory `0x9E6000`, AnimatedTexture `0x687000`, TextureCompositor `0x6CE000`, `0x6DB000`; MeshPool `0x6CF000`; Model factories (AsyncModelFactory) `0x6D8000-0x6DA000`; VRAM `VRAM_DxDiag 0x612640` |
| **D3D6 lighting, shadows, post** | AOSolver `0x689000-0x68B000`; shadows `0x692000-0x699000` (InteriorShadow, ExteriorBlobShadow); LightManager / LightObjectMap `0x6B0000-0x6B5000`; PostProcess `0x6E8000`; GammaBlitter, WaterReflection `0x6F0000`, `0x725000`; ScreenCaptureEffects `0x706000-0x709000`; Swarm effects `0x71A000-0x721000`; `LotImposter_Build 0xADAF60` (+ `0xADDEA0`, `0xADE030`); thumbnails `0xD62200-0xD65800` |
| **UI5 (UI drawing)** | `0x7E4620`-`0x7E8200` (UI images), `0x7EB000` SceneMgrWindow, EATextRenderer `0x691000`, `0x71B000` |

## 10. Candidates

| # | Candidate | Kind | Notes |
|---|---|---|---|
| C1 | **VRAM fix:** clamp the DxDiag MB value before the shift at `0x6128C5`, and when no adapter matches (0, DXVK) return a sane value (DXGI `DedicatedVideoMemory` by adapter LUID, or a config value) | ts3native patch, opt-in | Makes vanilla 8/12/16 GB cards and DXVK pick presets without editing `GraphicsRules.sgr`. The sgr fallback fix already covers the DXVK case; this one also fixes vanilla wrapping. Low risk, one function |
| C2 | **Reset diagnostics:** log `Device_TryReset` results (TestCooperativeLevel HRESULT, Reset HRESULT at PrivateData +0x1C8, listener failures) | ts3native hook, log only | Needed before any device-lost fix; ties "black screen / freeze after alt-tab" reports to a cause |
| C3 | **Device-removed recovery:** treat `D3DERR_DEVICEREMOVED`/`DRIVERINTERNALERROR` as fatal with a clean save prompt, or recreate the device | design only | Needs C2 data first; DXVK rarely reports loss [community] |
| C4 | **Skip DxDiag at start-up** (the provider init is slow) when a VRAM value is configured | ts3native, opt-in | Start-up time only; measure first [inferred cost]. **Built 2026-11-17 as `[Startup] DxDiagCache` (docs/284, default 0): cached replay of the same answer, not a configured value** |
| C5 | **Offload rule:** no device call may leave the main thread (no MULTITHREADED flag). Render-side offload (docs/23 #17) must hand D3D work back to the main thread, or create the device with `D3DCREATE_MULTITHREADED` (one-byte flag change at `0x6112A4`/`0x6112B9`, adds a lock to every call; DXVK honours it [inferred]) | design input | Check the thumbnail/UI-image IC paths (§5 item 3) in M-D3D5 |
| C6 | **DXVK interplay:** DXVK's GTX 580 identity triggers `NvCpl_Query` (harmless, DLL absent); DXVK's `d3d9.presentInterval` overrides the vsync flag; `FPU_PRESERVE` absent, so DXVK also applies single-precision FPU on the main thread [inferred]; adding `FPU_PRESERVE` is a one-flag experiment | measurement | Compare frame times and any precision-related glitches |

## 11. Open items

- Thread of the IC texture paths (`0x618570`; `0x619770` makes no device call, docs/43; from `IC_UIManager_LoadUIImageImpl`, `IC_ThumbnailManager_*`): posted to the main thread or not (M-D3D5).
- When Services/Canvas +0x15C4 (null device) is set; probably a headless or no-render option [inferred].
- The GraphicsRules evaluator (`0x5902B0` and neighbours) and how DeviceConfigManager +0xC4 reaches `textureMemory` (rows M-D3D5 or a short follow-up).
- Main-thread cost of the wrapper: needs the main-thread sampler (PLAN item 7 priority 0).
