# 53. Materials and shaders (M-D3D4)

Static map of how TS3W.exe (Steam 1.67) loads, creates, selects and binds shaders, and how materials pick a shader set. Done on a Ghidra copy (`game-copy/ghidra-d3d4`) with our symbols applied; no game runs. Costs come only from existing samples: `probe-out/profile/verify-2026-10-15-{bp,ip}-verify-mainstack.jsonl` (main thread, in world, Live mode, 1,978 + 1,013 samples) and the 36 `*simstack*.jsonl` files (10,251 sim-thread samples). Anchors came from docs/37 §9. Names are in `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (tag `<docs/53>`, 32 rows).

## 1. Summary

- **No runtime shader compiler.** TS3W.exe imports only `D3DXGetShaderSize` and `D3DXFillVolumeTexture` from `d3dx9_31.dll`. Nothing in it calls `D3DXCompileShader`, `D3DXAssembleShader` or `D3DCompile` [verified: import table]. All shaders are precompiled D3D9 bytecode in one file, `Game\Bin\Shaders_Win32.precomp` (the path is built from `"Shaders_"` + platform + `".precomp"` in `0x79B450`) [verified].
- **The package is big, and the load keeps only one variant per shader.** The file is 92.6 MB. Its `LINK` table has 15,689 shader entries: VS 3_0 3,323, VS 2_0 4,338, PS 3_0 4,907, PS 2_0 3,104, plus 17 shaders at 1_1. Together they hold 20.6 MB of bytecode [measured: file scan]. `CTAB` constant tables are stripped from the bytecode (0 hits) [measured]. Register binding comes from the package's own parameter records instead. Each entry has a 16-bit mask. The loader keeps an entry only when the mask matches both the shader-model bit (`1 << Shader_DetectModelLevel`) and the shadow-mode bit (`0x100/0x200/0x400` from `RenderShadowMode`) [verified code]. That this mask logic matches the file's mask values is [inferred]. On SM3 with shadow mode 1 the load keeps about **3,250 VS + 3,213 PS (8.7 MB of bytecode)** [measured from the file, using that inferred mask logic].
- **Creation timing has two modes.** `Shader_ReadBytecode 0x799910` calls `Create` (vtable slot `+0xC` -> `CreateVertexShader`/`CreatePixelShader`) right away unless the ShaderSystem "deferred" byte `+0x11` is set. In deferred mode, creation waits until `ShaderDB_GetOrCreate 0x79A7A0` runs on first lookup of a shader set (`ShaderLibrary_FindSet 0x79B790` -> `ShaderSet_Prepare`) [verified]. In both modes the bytecode buffer is freed right after a successful create [verified]. The deferred byte comes from a startup flag in `0x589080` [verified]. Its value in normal play was not traced [open item]. In eager mode, every kept shader (~6,460) is created at ShaderSystem init, before the main menu [inferred from code].
- **Everything runs on the main thread** [verified call graph + docs/42/44 thread owners]: package load at startup, material creation (SimBuilder steps, terrain, lot LOD, thumbnails), and shader binding inside each draw. On the sim thread, these ranges show up in 0.03% of samples (shader) and 0.04% (material), any frame [measured].
- **Constant uploads are already lean.** `Shader_Bind 0x79E400` skips `SetVertexShader`/`SetPixelShader` when the pass did not change. `Shader_SetFloatConstants 0x79DFE0` uploads a parameter only when its value stamp changed or the shader changed, and it merges adjacent registers into one `Set{Vertex,Pixel}ShaderConstantF` call [verified]. In world, the shader code's own time is ~1.6-1.7% of main-thread samples, and the D3D calls it makes directly take 0.2-0.3% [measured, §7].

## 2. Function list

Shader block (`0x799780`-`0x7A1000`; `0x799000`-`0x799780` holds generic container helpers with callers all over the engine, not shader code [verified callers]):

| Address | Name | Role | Tag |
|---|---|---|---|
| `0x7A08D0` | `ShaderSystem_Create` | Registers `Services/ShaderSystem` (id `0x175F805`). Called twice from `0x589080` (at startup, from `App_StartupFlags`). One call passes `(1, 0)` and skips the shader DB (mode `+0x6C8 == 2`); the normal call passes `(0, flag)` | [verified] |
| `0x7A07A0` | `ShaderSystem_ctor` | `+0x10` = skip shader DB, `+0x11` = deferred creation; sets up the param table (`0x79A0A0`, 0x400 slots), library maps and the watcher | [verified] |
| `0x7A0810` | `ShaderSystem_Init` | Vtable `0x1015DF4` slot 4. Sets the global param table `[0x11D7530] = this+0x18`, reads config (`0x79B450`), then `ShaderSystem_LoadPackage(0)` | [verified] |
| `0x79B450` | (`ShadowMode_ReadConfig`, docs/44 name) | Actually the ShaderSystem config: `RenderForceMinspecShaders`, `RenderShadowMode`/`RenderShadowsEnabled` -> shadow bit `+0x44`, package path, dev paths `SourcePath`/`CommonPath`/`ShaderSetPath`/`BinaryPath`. The name is kept (CSV rule) | [verified] |
| `0x79AEC0` | `Shader_DetectModelLevel` | From caps: 0 = VS/PS 3.0, 1 = 2.0 with extended caps, 3 = 2.0, 4/5 = below 2.0; forced SM3 path when `device+0xF50` is set | [verified] |
| `0x79F690` | `Shader_LevelCompatible` | Keeps a stored level when the detected one allows it | [verified] |
| `0x79B870` | `ShaderSystem_LoadPackage` | Opens the `.precomp` file (`FileStream_Open`, 64 KB buffered), mask = `1 << level` (level 2 when below 4 and minspec is forced) `| shadow bit`, parses it, and in eager mode prepares every shader set. Arg 1 = reload (builds `ShaderReloadMap`) | [verified] |
| `0x79CE00` | `ShaderPackage_Parse` | `SPKG` v2 chunks: `SHDB` (shader DB: `LINK` + `DBUF`), `SMDB` (samplers), `SETS`, `SHDS`, `KNM ` (key-name map), `END ` | [verified] |
| `0x79A930` | `ShaderDB_ReadLinkTable` | Per entry: VS or PS object (`VertexShader_ctor 0x799760` / `PixelShader_ctor 0x7997E0`, vtables `0x1015830` / `0x1015848`). Mask filter; skipped entries are seeked over. Then the `DBUF` default-value buffer (3.8 MB in the file) | [verified] |
| `0x799910` | `Shader_ReadBytecode` | `VSHD`/`PSHD` tag, size, malloc + read, then `Create` unless deferred | [verified] |
| `0x799780` / `0x799810` | `Shader_CreateVertexShader` / `Shader_CreatePixelShader` | Vtable slot `+0xC`: device `+0x16C` / `+0x1A8`, then frees the bytecode | [verified] |
| `0x79A7A0` | `ShaderDB_GetOrCreate` | Index -> shader object; `IsCreated` (slot `+8`) false -> `Create` | [verified] |
| `0x79AC30` | `ShaderDB_ReadSamplerStates` | `SMPS` chunk | [verified] |
| `0x7A0BE0`, `0x79EED0` | `ShaderTechnique_Read`, `ShaderPass_ReadStates` | `TECH` chunk; passes of 0x18C bytes with render, sampler and texture state lists | [verified] |
| `0x79B790` | `ShaderLibrary_FindSet` | Shader set by name hash, fallback hash `0xA2A9B074`; in deferred mode prepares the set on first lookup | [verified] |
| `0x79A570` -> `0x7A09E0` -> `0x79E7F0` | `ShaderSet_Prepare` -> `ShaderTechnique_Prepare` -> `ShaderPass_Resolve` | Resolves the pass VS/PS through `ShaderDB_GetOrCreate` (D3D creation happens here in deferred mode), plus param, state and sampler ids | [verified] |
| `0x79A300` | `ShaderSet_Draw` | 59 callers (lights, CAS, shadows, terrain, UI, Swarm, water...); calls `ShaderTechnique_DrawPasses` | [verified] |
| `0x7A0A70` | `ShaderTechnique_DrawPasses` | Per pass: `Shader_Bind`, then the caller's draw callback | [verified] |
| `0x79E400` | `Shader_Bind` | Sets PS/VS if changed (cached in `device+0x54`), render states, sampler states, up to 16 textures, then VS and PS float constants | [verified] |
| `0x79DFE0` | `Shader_SetFloatConstants` | Dirty check per parameter, merges register runs, `+0x178` (VS) / `+0x1B4` (PS) | [verified] |
| `0x79D2A0` | `Shader_SetBoolConstants` | Bool params (type 2): `+0x188` / `+0x1C4` | [verified] |
| `0x79D620` | `ShaderParam_Pack` | Per changed param: packs the value into the 16-byte register scratch | [inferred] |
| `0x7A0D40` | `ShaderWatcher_Poll` | Every 3 s: `_wstat64` mtime of the package; reload through `ShaderSystem_LoadPackage(1)`. Caller not resolved (no direct or vtable ref found) | [verified body] |

Material side (`0x6C9000`-`0x6CBF90`; `0x6CBF90`-`0x6CE000` is the texture compositor, docs/40):

| Address | Name | Role | Tag |
|---|---|---|---|
| `0x6CAC30` | `Material_Create` | `Renderer/Material`; 30 callers: `Renderer_Init` (per-frame material), SimBuilder steps, terrain, lot LOD, roofs, thumbnails | [verified] |
| `0x6CAB20` | `Material_Init` | Looks up the shader set (`ShaderLibrary_FindSet`), builds the binding | [verified] |
| `0x6CA5B0` | `Material_FindShaderSet` | Picks the shader set by material kind (switch on name hashes, kinds 1/3/4/5/6/9/10); falls back to `"shadererror"` | [verified] |
| `0x6CA7D0` | `MaterialShaderBinding_Create` | `Renderer/MaterialShaderBinding`; from model building (`0x690900` <- SimBuilder, model LOD `0x6DC150`) | [verified] |
| `0x6CB1E0` | `SubstitutedMaterialDef_Create` | Material substitution (CAS, thumbnails) | [verified] |
| `0x6C9BA0` | (material items) | Resolves texture params and their sizes (`TextureFactory_ScaleDims`) | [verified callees; role inferred] |

`Renderer/MaterialShaderBindingCache` is created in `RenderSystem_ReadConfig 0x6E05A0`. `0x6CBB80`/`0x6CBD50` take a lock around a binding list; who else takes that lock was not traced [verified lock calls; purpose inferred].

## 3. Shader lifecycle and threads

1. **Startup (main):** `App_StartupFlags` -> `0x589080` -> `ShaderSystem_Create`. Service init -> `ShaderSystem_Init` -> `ShaderSystem_LoadPackage(0)` reads the whole `.precomp` through a 64 KB buffer, keeps the entries for the detected model level and shadow mode, and reads samplers, sets, techniques and parameters [verified].
2. **Create:** eager mode -> about 6,460 `CreateVertexShader`/`CreatePixelShader` calls inside that load, on the main thread, before the menu [inferred count]. Deferred mode -> on the first `ShaderLibrary_FindSet` for each set, from `Material_Init` (main thread: SimBuilder, terrain, lot LOD, thumbnails) [verified path].
3. **Use (main, per draw):** draw code -> `ShaderSet_Draw` -> `ShaderTechnique_DrawPasses` -> `Shader_Bind` per pass -> draw callback [verified].
4. **Reload:** `ShaderWatcher_Poll` checks the package mtime every 3 s and can reload it (`LoadPackage(1)`) [verified body]. This is a development hot-reload path. It costs one `stat` every 3 s; no change -> no reload.
5. **Release:** the bytecode is freed after a successful create; the D3D object stays [verified].

No function in `0x799000`-`0x7A0000` or `0x6C9000`-`0x6CE000` calls `GetSimThreadId 0x7D99F0` [verified: `RangeMap` callees; `0x7A0000`-`0x7A1000` not scanned]. Every caller chain ends in main-thread owners (startup, `Renderer_*`, `SimService_Update`, `WorldTerrain_Update`, `ThumbnailManager_Update`, `LotLODCreator_*`) [verified call trees; owners from docs/41/42/44].

## 4. Permutation selection

Selection happens at two levels, both at load or material build, never per draw [verified]:
- **Load time:** shader-model level x shadow mode picks one bytecode per `LINK` slot. Changing `RenderShadowMode` or the minspec option needs a new load.
- **Material build:** `Material_FindShaderSet` maps the material kind to a shader-set hash. The set's techniques/passes are fixed. Per-draw variation comes only from parameter values and textures through the global param table (`[0x11D7530]`, slots of 16 bytes with a context id; a slot from another context reads the default at `0x1015860`) [verified].

## 5. Constant-upload path

`Shader_Bind` -> `Shader_SetFloatConstants(VS params +0x168, VS)` and `(PS params +0x178, PS)`. Per parameter record (0x1C bytes): look up the global slot, compare its value stamp with the cached one in the record, and convert changed values with `ShaderParam_Pack`. Adjacent registers are merged and flushed as one `SetVertexShaderConstantF` / `SetPixelShaderConstantF` per contiguous run. A shader change forces all of them [verified]. Bool params go through `Shader_SetBoolConstants`. Only two places in the exe set shader constants [verified, 2026-11-17, capstone scan of all virtual calls through `+0x178` (SetVertexShaderConstantF) / `+0x1B4` (SetPixelShaderConstantF) in Steam 1.67 `.text`; agrees with Apex research note 15 §6]: `Shader_SetFloatConstants 0x79DFE0` (VS and PS, register from the parameter record; device calls at `0x79E0E0`/`0x79E148` VS and `0x79E102`/`0x79E15D` PS) and the instancing code `0x696980` (VS only, calls at `0x696A70`/`0x696A86`, count `0x28` = c40 and c0). Earlier text listing `0x527E60` and UI `0x77C030` as setters was wrong: `0x527E60` calls a two-argument bool-returning virtual `+0x178` on another object (`0x527E90`), and `0x77C030` is `GridGeneric` (docs/43) with no such call [verified]. Other `+0x178`/`+0x1B4` virtual calls (`0x502645`, `0x502812`, `0x523EDB`, `0x8AC8B5`, `0x8BA005`) are also on non-device objects [inferred from shape: receiver is not the device, bool result; not traced further]. Volume per draw = the changed registers of the pass. That number depends on content and was not measured.

## 6. D3DShaderReplacer and DXVK

- **D3DShaderReplacer.asi** (LazyDuchess; local copy and `.cfg` in `game-copy/bin`, `Mode = Replace`, `Format = Assembly`, `ExtraData = True`): it imports `D3DCompile`/`D3DDisassemble` (D3DCompiler_43) and `D3DXAssembleShaderFromFileW` (d3dx9_43), and it patches code with thread suspend + `SetThreadContext` + `VirtualProtect` [verified imports]. So it compiles or assembles replacement shaders **at runtime**, which TS3W itself never does. It hooks at the D3D9 device level [community, docs/03], so it sees the game's `CreateVertexShader`/`CreatePixelShader` calls. In eager mode that is a startup-time cost; in deferred mode it lands at first use [inferred]. Because `CTAB` is stripped, extracted assembly has no constant names [measured: 0 CTAB].
- **DXVK:** DXVK replaces `d3d9.dll`. Under DXVK, `CreateVertexShader`/`CreatePixelShader` translate the bytecode to SPIR-V, and pipelines compile later [community: DXVK behaviour]. With graphics-pipeline-library support (DXVK 2.x on a GPU driver that exposes it; RTX 30 does), shaders compile on worker threads at create time, so eager creation becomes "prewarm for free" [community/inferred]. Without it, the first draw of each new state combination compiles on DXVK's submission path, which is the first-use hitch [community]. Our main-thread samples include `d3d9.dll` frames in 33-40% of samples overall (all draws, present). That figure is not specific to shaders.

## 7. Main-thread cost attribution (existing samples only)

Stack-scan samples; "self" = the innermost TS3W frame is in the range; "any" includes the draw callbacks below `ShaderTechnique_DrawPasses`, so "any" is an upper bound [measured]:

| Range | bp self | bp any | ip self | ip any |
|---|---|---|---|---|
| Shader block `0x799780`-`0x7A1000` | 32 / 1,978 (1.6%) | 7.0% | 17 / 1,013 (1.7%) | 8.3% |
| of which D3D call on top (`d3d9.dll` leaf) | 6 (0.3%) | | 2 (0.2%) | |
| `Shader_SetFloatConstants` + `ShaderParam_Pack` | 10 (0.5%) | 1.4% | 5 (0.5%) | 1.4% |
| `Shader_Bind` | 3 (0.15%) | 0.4% | 2 (0.2%) | 0.6% |
| Material (`0x6C9000`-`0x6CBF90`) | 10 (0.5%) | 5.4% (mostly `0x6C9BA0` material items) | 2 (0.2%) | 0.6% |
| Sim thread, all 36 files | shader any 3 / 10,251 (0.03%), material any 4 (0.04%) | | | |

Shader binding and constant uploads are a small slice of the main thread. The bp material share comes from material builds (SimBuilder/lot LOD) during the run, not from per-draw work [inferred from the callers in the stacks].

## 8. Candidates

| # | Candidate | Value | Risk | Notes |
|---|---|---|---|---|
| MS1 | **Confirm the creation mode first:** a `[Verify]` probe counting `Shader_CreateVertexShader`/`Shader_CreatePixelShader` calls per phase (startup, world load, play) and the first-bind time per shader | needed for MS2/MS3 | none (read-only hook) | Decides whether first-use hitches can come from the game at all |
| MS2 | **Prewarm** in deferred mode: run `ShaderSet_Prepare` for all sets at load, or the reverse in eager mode. Under DXVK, prefer DXVK 2.x with graphics pipeline library and its shader cache over a game-side draw prewarm | first-use hitches | low (startup time) | A game-side draw prewarm (drawing every pass once) is large work and still misses DXVK state combos |
| MS3 | **Deferred creation to save address space:** set ShaderSystem `+0x11` so only the shaders actually used get created; under DXVK each created shader keeps SPIR-V and metadata in our 4 GB process | some MB of address space (unmeasured) | first-use hitches return | Trade-off against MS2; measure VA with both settings |
| MS4 | Fewer constant uploads (per-frame constants hoisted, larger dirty groups) | ≤0.5% main | medium | Already dirty-checked and merged; low value |
| MS5 | Skip `ShaderWatcher_Poll` (dev hot-reload) | negligible CPU | low | Removes a 92 MB reload path if the file mtime ever changes during play; nice-to-have |
| MS6 | Document the D3DShaderReplacer + DXVK pairing: replacements compiled at create time land at startup in eager mode | user guidance | none | Keep it out of the default set (docs/07) |

None of these targets the sim thread. Shaders are not a sim-thread stutter source (0.03%).

## 9. Method

String scan for `Shader|Material|d3dx|permut|effect|compile` (`StringXrefs`), `RangeMap` over the anchor blocks, a vtable-slot call-site scan for every shader and constant device slot (`VCallSites`; most matches at the same offsets belong to other COM vtables, so only matches inside the anchor blocks were kept), `CallerTree`, `DumpPtrs` for the shader vtables, `DecompileTo` for the chain. A local PE import parse checked for D3DX compilers, and a byte scan of `Shaders_Win32.precomp` counted tags, versions and masks. No EA bytes or decompiled code are kept in the repo.

## 10. Open items

- Value of the ShaderSystem deferred byte (`+0x11`) in normal play: it comes from a startup flag in `0x589080` (`[ESP+0x14]` set from bytes `+0x6B1`..`+0x6B3` of the startup config) whose meaning was not traced. MS1 answers it by measurement.
- What the `LINK` entry mask bits `0x0010`-`0x0020` and `0xF800` mean, beyond model level and shadow mode.
- Who calls `ShaderWatcher_Poll`, and whether it runs in release play.
- Constant registers per draw, by pass (needs a hook count).
