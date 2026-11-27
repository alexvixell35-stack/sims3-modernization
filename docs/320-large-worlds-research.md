# 320. Large worlds (4x area and unbounded), memory headroom, fog-of-war streaming (2026-11-22)

Status: research only (round 1 `docs/large-worlds-research-1122`; round 2 `feature/large-worlds-1122`: open A1 sites
read, generator `tools/world-gen.ts`, first-proof queue `large-worlds-load-1122` written and dry-run, NOT RUN,
streaming design §7-§8). Implementation of the cap removal moves to a separate project (§1.4).
Tags: [verified] = disassembly, file decode, decompiled code or our source, cited; [measured] = from a run, cited;
[inferred] = reasoning, to be confirmed; [community] = reported by others.

Rodney's questions (2026-11-22):
- A. Can we do performance work first that takes larger worlds into account (4x the area of today's 2048 m worlds,
  i.e. 4096 x 4096 m)? Reframed by Rodney: "it's not about whether the engine supports it but can we implement it".
- B. "Memory is not a problem because we offloaded almost everything we can out of Sims 3 to Apex": true?
- C. Fog-of-war world streaming: load parts of the world only as needed, throttle and cut off everything beyond the
  visible Sims. Good idea?

## 0. Short answers

- **A1: no TS3W cap blocks exactly 4096 m** (round 2, §1.2). World size comes from the world file in metres
  (`WM+0xCC/+0xD0` = u32 metres, not cell counts); grids are sized from it. Two limits sit at exactly 4096 m (blend
  map 0x3D8632D0 u16 tile index; physics quadtree root), one at 8192 m (R 16-bit chunk index), one is soft at 2048 m
  (scene quadtree culling box, overridable by a config key). The terrain cell grid is sized from the WTXT paint
  resource, so the data must grow it. CAW's 2048 m object-placement limit is not in TS3W. So 4096 m is a data job:
  generator `tools/world-gen.ts` (§1.5) + first-proof queue (§1.6, not run). Past 4096 m: format changes (§8 a6).
- **A2: biggest scaling risks for a 4x world:** failing world A* floods (multi-second freezes, ~linear in area,
  4 s BP / 7.4 s SV crowd today), GC cost and address space (heap grows with objects), routing clone memory, water
  grid build at load, then routing and unseen-Sim ms/s (§2).
- **B: partly true.** The bridge moved the renderer stack (~430-640 MB, the largest movable owner) out and Error 12
  has not shown up in normal play since the leak fixes, but ~1.5 GB of unmovable game memory stays in the 32-bit
  process and headroom is ~1.5-1.7 GB after 10 min on standard worlds. A 4x world would plausibly eat
  +0.5-1.35 GB of that [inferred], which brings Error 12 risk back (§4).
- **C: possible and staged, with sidecar saves allowed (§7.8, re-evaluated 2026-11-22).** The sim fog of war exists
  and is the right shape (`FarFromViewers`, unseen caps/skips/fakes, go-live). Region streaming (lot objects and NPC
  instances paged per 2048 m tile, R / bounds resident, household tiles always active, state in a sidecar next to the
  save) is the enabling path for dense 4096 m and larger worlds (§7, phases §8); for today's 2048 m worlds it is still
  not worth it (little memory to gain, §5.3).

## 1. A1: where world size lives and what implementing 4096 m takes

Round 2 (2026-11-22, branch `feature/large-worlds-1122`): static reads of the open sites (ts3-static-re agent on
`game-copy/bin/TS3W.exe` 1.67), file decode of Sunset Valley, `GPE8_Future` and `SWE7UniversityTest` (base game
`NonPackaged/Worlds`), generator `tools/world-gen.ts` (§1.5) and a first-proof queue (§1.6, not run). Ledger docs/51 §575.

### 1.1 World size source

- `WorldManager_CreateLotsFromLayer 0xC65E00` opens resource **0x90624C1B** (push at `0xC65E0F`) and reads, through
  `0x4DD540` (u16) / `0x4DD700` (u32, byte swap when the stream flag is not 1): u16 version (13 SV, 14 GPE8 / SWE7),
  f32 min x @2 -> `WM+0xB4`, f32 min z @6 -> `WM+0xB8`, u32 size x @10 -> `WM+0xCC`, u32 size z @14 -> `WM+0xD0`, then
  (version >= 7) more u32 fields (four distances 35000 / 40000 / 35000 / 10000 in GPE8) [verified, disassembly + decode].
- **Correction of round 1:** `WM+0xCC/+0xD0` hold **metres** (2048 in SV and GPE8, 512 in SWE7), not 256 m cell counts.
  `[[WM+0x54]+0x38]` is the integer 1, read from WTXT 0x9063660D at `0xC2DD6E`; `0xC62663..0xC626B1` computes
  max = min + size x 1 as an int multiply [verified]. I-575-1, I-575-2.
- The same size sits in four more resources, all of which the generator rewrites: 0x06CE4804 u32 @28 / @32,
  heightmap 0x2AD195F2 (u32 w, u32 h = size + 1, f32 scale, f32 1.0, u32 1, u16 samples), WTXT 0x9063660D u32 @6 / @10,
  routing graph R 0x05CD4BB3 (size / 32 chunks per side) [verified, decode of 3 worlds].

### 1.2 Per site

| Site | Sized from | Cap | 4096 m | Patch needed | Tag |
|---|---|---|---|---|---|
| World bounds `0xC65E00` (0x90624C1B) | file, u32 metres | none (u32) | fine | generator writes the size | [verified] |
| Point-in-world `0xC6BAB0`, camera checks `0x96ECB0`, `0x9713E0`, `0x9729D0`, `0x97C410`, world centre `0x96C010` | `WM+0xB4..+0xC4` | none; no 2048 / 4096 constants in `0x960000..0x980000` | fine | none (camera clamp routine itself not found) | [verified] checks |
| Terrain cell grid (TerrainData `0xC2DA80`, `0xC2E550`) | WTXT +6 / +10 (object +0x3C / +0x40): cols / rows = ceil(size / 256), cell vectors +0xAC / +0xBC | none | **WTXT must be grown**, else no terrain cells past 2048 m | generator `--wtxt size` (default) | [verified] *[Corrected 2026-11-26, Boundless Atlas docs/017 §7: `0xC2DA80` runs for WTXT version 7 only (fills every entry); version 8 and later are loaded by `0xC2DC70`]* |
| Terrain cell lookups `0xC2A470`, `0xC2A4B0`; rect clamp `0xC29C10`; checks `0xC29F43`, `0xC2B5D9`, `0xC2C164` | unsigned check against cols / rows, null when outside | - | no OOB | none; callers' null handling [inferred] | [verified] |
| Heightmap 0x2AD195F2 (`0xC2A420..0xC2A4D7`) | dims in file; cell = coord >> 8 checked against the row count; terrain chunk 0x101 samples (`0xC2AA24`) | none | 33.6 MB vs 8.4 MB | generator | [verified] |
| Blend / paint weight map **0x3D8632D0** (loader `0xC238E0`, header `0xC21A20`) | magic `0xA5FF03xx`, u32 w / h, 16 x 16 m tiles only (`0xC239EE`), tile count ceil(w/16) x ceil(h/16) (`0xC23880`) | **tile index u16** (hi / lo bytes, `0xC23BD4`): 65,536 tiles = 256 x 256 = **4096 m max**; layer id u8 | exactly fits | > 4096 m: wider index in format + loader + saver (`0xC22A70`, [inferred] mirror) + CAW writer | [verified] loader |
| Blend reads `0xC21AE0` (0x3D8632D0, world-builder layer 0x05DA8AF6) | x / z checked against the map's own w / h | - | a 2048 map in a 4096 world reads 0 outside | none (generator leaves them at template size) | [verified] |
| Physics LooseQuadTree `0x681A70` (one call, `0x682344`) | root 4096.0 fixed (`0xFEFBCC`, read only at `0x682337`), half 2048, 11 levels; insert `0x6818F0` rejects any shape outside [0, 4096] (counter +0x14) | **4096 m** | exactly fits (0..4096) | > 4096 m: raise `0xFEFBCC` or pass the world extent at `0x682337` | [verified]; effect of a reject on collision [inferred] |
| Scene quadtree `0xEC9CD0` (renderer culling) | default box 0..2048 (`0x10795A8`), y +-512, depth 6; never resized from the world; outside objects clamp into edge nodes (`0x705738..0x705771`) | soft (culling only) | edge nodes get crowded past 2048 m | config key `SceneQuadTree=X,Y,depth` read by `0x58B160` from the Default.ini family (`0xEC9D5D..0xEC9D91`): `SceneQuadTree=4096,512,7`, no code patch | [verified] key + parse; section and cost [inferred] |
| Per-256 m terrain resources 0x033B2B66 (`0xC2EDF2`) / 0xAE39399F | one per cell (two LODs for 0xAE39399F), instance z << 16 \| x in metres (u16 each) | 65,535 m | 256 cells | generator `--cells clone` (default) or `none` | [verified] instance; behaviour with missing cells unread |
| Water grid G (docs/100) | `(maxX-minX)/4+1` by `(maxZ-minZ)/4` | none | 1,049,600 cells (~17 MB), build ~2-3 s [inferred] | none | [verified] |
| Routing graph R (docs/99) | chunk counts w x h from file; ref = 16-bit chunk index | 65,536 chunks = **8192 m** | 128 x 128 chunks | generator validates every ref (no bounds check in game) | [verified] |
| 0x044735DD (routing loader `0xB80E40`, `0xB81E23..0xB81E9B`) | (size / 256)^2 8-byte entries into a growable vector | none | 256 entries | generator | [verified] count; meaning [inferred] |
| 0x06DC847E instance 1 (`0xC2BBC0`, `0xC2BE80`; GPE8 / SWE7 only) | u32 1, w, h, 0, then u32 + u8 plane of 4 m cells | none seen | 1 M cells | generator | [inferred] |
| Footprint grid `P+0xDC`, lot hash grid `0xC688A0` | world size / origin | none | small | none | [verified] (round 1) |
| Houseboat strip `WM+0xC4`, 4096.0f at `0x810B48` / `0x811B52` | 2048.0f strip beyond max z; 64 m squared-distance test | not size caps | - | none | [verified] (round 1) |
| 4096.0f / 0x1000 in `0xC36FA0..0xC40189` | wall cutout texture (next power of two up to 2048 texels, `0xC370CF..0xC37117`); wall UVs as s16 x 4096 (`0x1077164`) | not world size | - | none | [verified] |
| Object placement past 2048 m | no 2048.0f / 2048.0 read in TS3W except the houseboat strip and the scene quadtree | **not in TS3W**: the 2048 limit SandrineD hit is CAW's | - | none | [verified] scan, CAW-only [inferred] |
| Map view (`Camera_EnableMapViewMode`, ClipPlaneMapViewDistances `0x96C273`) | camera mode + clip distances | none found | - | none | [verified] scan; no world map texture [inferred] |
| Save format | managed persistence (`LoadSaveManager`, `NativePersistWriter`); positions are float Vector3 | none found | - | none | field types [verified]; native object save not traced |
| Managed code | grep of decompiled assemblies for 2048 / 4096: no world bounds | - | - | - | [verified] (round 1) |

**Caps in TS3W, summary:** none blocks exactly 4096 m. Two sit **at** 4096 m (0x3D8632D0 u16 tile index; physics
quadtree root) and one at 8192 m (R 16-bit chunk index); one is soft at 2048 m (scene quadtree, ini key). The only
must-do for 4096 m is in the data: grow WTXT with the rest (the generator does). Open: the camera clamp routine,
the minimap, the native object save path, and how callers of the null terrain-cell lookup behave.

### 1.3 Community

[community] CAW offers 256 to 2048 m per side. SandrineD (MTS thread 453513, Aug 2011) grew a Large CAW world past
2048 m by importing types 0x2AD195F2, 0x3D8632D0, 0x05CD4BB3, 0x06DC847E, 0x044735DD (group 0, instance 1) and
0x9063660D (WTXT) from a bigger file; she read 4096 x 4096 as the 0x3D8632D0 format maximum (confirmed above,
I-575-3). It loaded and saved in CAW, terrain past 2047 existed, but CAW would not place objects past 2048 and the
layer file refused to save; no in-game test was reported. Others: no working world over 2048 m reported; big custom
worlds crash on memory, not on a size check.
- https://forums.thesims.com/en_uS/discussion/963528/map-size
- https://forums.thesims.com/en_US/discussion/comment/12041122
- https://forums.thesims.com/en_us/discussion/519878/caw-crashing-cant-create-large-worlds

### 1.4 Scope: research here, implementation in a separate project

Rodney 2026-11-22: large worlds are **research only in this repo**; the actual removal of size caps moves to a
separate project (name TBD). The goal is enabling work: make larger and unbounded worlds possible, not ship a bigger
world now. This repo keeps the cap inventory (§1.2), the generator as a test tool (§1.5), the first-proof queue (§1.6)
and the streaming design (§7, §8). No ts3native patches or switches for caps here.

### 1.5 Generator: `tools/world-gen.ts` (test tool, prototype)

`node tools/world-gen.ts grow <template.world> <out.world> --size 4096 [--cells clone|none] [--wtxt size|keep]
[--fill median|edge]`; `check <file.world>`; `install <file.world> [--uninstall]` (copies to
`<Documents>\Electronic Arts\The Sims 3\InstalledWorlds`); `--self-check` (synthetic 512 m template, no game data).
Node, no dependencies; reads the template with `tools/extract-assemblies.ts` and writes with `tools/dbpf.ts`.
Output stays outside git (it carries the template's EA data).

- Grows: 0x90624C1B and 0x06CE4804 sizes; heightmap (template in the 0..size quadrant, rest flat at the template's
  median sample, or `--fill edge` = clamp); R (template chunks keep their place, new chunks empty, same file version);
  0x044735DD cell list; 0x06DC847E 4 m grid when present (template cells kept, rest = the template's most common
  value); WTXT size fields (paint data itself not grown); per-cell 0x033B2B66 / 0xAE39399F (a copy of the template's
  smallest cell per LOD, coordinates patched).
- Leaves: blend maps (bounds-checked reader), lots and every other resource byte for byte (still compressed). Lots stay
  in the template quadrant: "no lots" mode skipped, the per-lot resource set is not decoded; the proof loads a new game
  to the family picker, so there is no active household anyway.
- Guards: refuses a size that is not a multiple of 256, smaller than the template, or over 4096 (0x3D8632D0 tile
  index); refuses a template whose R has a ref that leaves its own grid (the game decodes chunk deltas with no bounds or
  row-wrap check, docs/99: a bad edge is a crash) and re-validates every ref of the grown R; checks each size field
  holds the template size before writing.
- Verified offline: self-check (grow 512 -> 1024: fields, sample placement, fills, R row remap and dz refs, cell clones,
  trailer, control identity, version-1 R byte for byte, the three refusals). On the real Sunset Valley:
  `grow --size 2048` gives a package whose 6,766 resources are all content-identical to the template (control), and
  `--size 4096` writes 103.6 MB with bounds 4096, heightmap 4097^2, R 128 x 128 (39,265 waypoints, 0 bad refs), 256
  cells, WTXT 4096 [measured, offline].

### 1.6 First proof: queue `large-worlds-load-1122` (written, dry-run clean, NOT RUN)

`tools/harness/queues/large-worlds-load-1122.json`, repros `lw-load-2048-control`, `lw-load-4096`,
`lw-load-4096-nocells`. Bin state apex; set e1122; `ProbeValues.Enabled=1` logs the WM bounds (float bits: 2048.0 =
`0x45000000`, 4096.0 = `0x45800000`) and the G cell count (262,656 / 1,049,600), each a `logRequire` with regex cases
from real log lines. Each job: new game up to the family picker (PlayFlow map view: no household, nothing saved, save
guard on), camera poses over the template and the grown quadrants, `memory: true` (largest free block per step), load
time = the load step time, fail-fast (load until 600 s, timeout 25 min). Job 1 = control (a FAIL is the install path
or the harness, not size), job 2 = default 4096, job 3 = without cloned cells. Prep / cleanup commands in the queue
note. ~35 min of game time. Risk: that a bare .world in InstalledWorlds is listed is [inferred] (I-575-13); fallback in
the note.

## 2. A2: costs that scale with area, lots or population

Proxy data: BP 82 lots, IP 119 lots [measured, docs/66]. Hitch p99 BP 85 ms, IP 115 ms, SV < 50 ms; hitches
>= 100 ms per 10 min BP 182, IP 530, SV 87 [measured, docs/161]. No doc records SV's lot count, heap size by world,
or any world's dimensions in a run, so every 4x number below is [inferred] from BP vs IP.

| # | Cost | Scales with | Today | 4x world [inferred] | Our fix, state |
|---|---|---|---|---|---|
| 1 | Failing world A* (floods the whole reachable graph before "no path", no node/time cap, `0x676260`) | area x entry portals tried | SV ~27k waypoints / ~110k edges; worst plans 7,361 / 7,350 ms SV crowd, 4,060 ms BP [verified docs/142; measured] | ~4x per failed call: 15-30 s freezes | `[RouteEarlyOut]` (docs/142): acting mode built and run 2026-10-22 (docs/142 §10): exact (132 shadows, 0 diff) but ~0 saved, the failed plans' seconds are not in the world A\*; corrected 2026-11-22 (docs/142 §11) |
| 2 | GC per tick (Boehm full cycles only) | live heap | BP ~120 ms/s, ~1.0 s cycle every ~8.4 s, live 90-100 MB in 356 MB heap [measured, docs/281]; sample share BP 9.9%, SV 17% [measured, docs/28] | 1.5-2.5x GC ms/s; less address space | `[GC] IdleGate=1` (docs/281) no valid verdict; generational GC (docs/290) static; mark helpers (docs/287) need the gate |
| 3 | Routing clone / snapshot memory | lots, objects | BP 126.7k clone-map entries, IP 122.6k [measured, docs/66]; resync rebase 12.6 ms/s BP [measured, docs/94] | ~4x entries, +tens of MB | per-lot on-demand clone, docs/66 A4: planned, 8-12 h, ~25-35 MB |
| 4 | Water grid G build at load (sim thread) | area | 688 ms BP, 505 ms IP [verified live, docs/100, docs/112] | ~2-3 s per load | none |
| 5 | Routing per tick (planner, A*, height raycasts) | Sims x trip length | N3 14.5% of sim samples; IP route bucket ~79 ms/s [measured, docs/28] | 1.5-2x | `[RouteAsync]` own-task Kinds unseen-only (docs/288) built, not run; `ForeignKinds=4` off; out-of-process routing (docs/194) design, ~60-95 h |
| 6 | Unseen-Sim decisions | unseen population | autonomy S2 11.1% [measured, docs/28]; interaction-step ceiling BP 61 / IP 23 / SV 49 ms/s [inferred, docs/243]; `autunseen` +6.6 BP / +11.2 IP ms/s [measured, docs/222] | linear in townies | `autunseen`; D1 park at boundary (docs/243), 20-40 ms/s central [inferred] |
| 7 | World lot alarm pass (hitch) | lots x alarms | worst 956 ms [measured, docs/247 §4] | 1-4 s spikes | none, no owner doc |
| 8 | Lot scoring / streaming pass | lots | 30 / 54 / 116 ms single calls at load [measured, docs/65]; world code ~4% [measured, docs/38] | > 128 lots leaves the stack buffer; ~0.3-0.5 s calls | EA `Max Active Lots`, S3SS lot streaming; docs/278 "do not build" |
| 9 | Lot update + reaction broadcasters | lots with Sims | S3 10.2%, broadcasters 4.5% after F12 [measured, docs/28] | ~linear | `autoffbcast` (docs/208) off |
| 10 | Autonomy lot/object scans | objects in scored lots | ~150-lot table, 100-250 rows [design, docs/122] | ~4x table | `[AutShadow]` / offload (docs/129) |
| 11 | Boat replans on the sim thread | water area x boat trips | IP 87% of GetNextEvent (49.2 of 56.5 s) [measured, docs/247] | worse with more water | `[RouteAsync] ForeignKinds=4` (docs/176) off |
| 12 | Save size / time | Sims, objects, lots | BP save+reload 178 s; each in-process reload -56..-94 MB largest free [measured, docs/204] | longer, lower largest free | `[MemDiag]`, Apex MemoryGuard |
| 13 | Story progression, situations, services | Sims, lots | S11 ~2% (story 0.7%, situations 0.5%), services 0.8% [measured, docs/28] | ~2x, stays small | poor value (docs/129) |
| 14 | Load L1-L9 (packages, catalog, mods) | CC and mod count, not area | 284 s iteration on the many-mods tester [measured, docs/302] | L8 mod world scans grow with lots | Apex caches; docs/302 plan |

Rank for a 4x world: 1 (freezes), 2 and 3 (address space), 4 (load time), then 5 and 6 (steady ms/s). The rest stay small.

## 3. A3: work that pays off now and for larger worlds

| # | Item | Why it scales | Agent time | Game runs |
|---|---|---|---|---|
| 1 | ~~`[RouteEarlyOut]` apply mode (docs/142)~~ built and run 2026-10-22, ~0 gain (docs/142 §10, §11); the multi-second plans belong to docs/164's phase split and `PlanDeadlineMs` | failed-plan cost ~ area; biggest freeze today | ~30-60 min build | 2 jobs (BP + SV rare-route, crowd) |
| 2 | `[GC] IdleGate=1` clean verdict (docs/281) | GC ~ heap | none (fix SV DoorMemoX self-check first, ~15-30 min) | 3-4 jobs, longer ABBA BP + SV |
| 3 | Generational GC P0a (docs/290) | mark cost ~ heap; biggest structural GC win | several lanes, ~2-4 h each | ~4 jobs |
| 4 | Water grid G cache across loads or off the sim thread (docs/100; phase probe `[WaterGrid] Prof` built 2026-11-22, RUN FIRST queue, docs/100 §13) | ~ area, 0.5-0.7 s per load now, ~2-3 s at 4x | ~1-2 h design + build + offline exactness check | 1-2 load jobs |
| 5 | Per-lot on-demand routing clone (docs/66 A4, docs/194 S3) | memory ~ lots; ~25-35 MB now | ~8-12 h incl. per-lot usage readout | 2-3 jobs |
| 6 | Own-task `[RouteAsync]` unseen-only (docs/288) + `ForeignKinds=4` boats (docs/176) | routing ~ Sims x trip length | built | 2-4 jobs IP + BP |
| 7 | D1 park at boundary (docs/243) | ~ unseen population | ~1 h | 2 jobs |
| 8 | `kHoursMemo` (docs/276) | per-lot venue lookups ~ lots [inferred] | built | 2 clean jobs |
| 9 | Distance-band split in unseen probes (§5.4 step 1) | tells which distance band costs what before any big-world work | ~20-40 min | smoke + 3 x 10-min jobs |
| 10 | Bridge AllocSites memory rescan + one big-save run (§4 gap) | memory headroom for big worlds | none (queued idea) | 2 jobs |

Total for 1-8: about 18-24 live jobs, 1-2 clean nights.

## 4. B: memory headroom

### 4.1 What still lives in TS3W.exe (Bridgeport; mostly measured before the bridge)

| Owner | MB | Movable? | Ref |
|---|---|---|---|
| Boehm GC heap | 376-408, +16-35 per run, never returned | no | [measured] docs/215 #1, docs/198 |
| Module images | ~426 (NVIDIA 32-bit driver ~111 and DXVK 7.5 leave with the bridge) | no | [measured] docs/66 §3.1; [inferred] what leaves |
| ResourceCache in-use | ~360-400 | no (Apex's area) | [measured] docs/66 §3.3, docs/190 |
| EA heap: resource read buffers | 112-192 load peak | yes, next target | [measured] docs/215 #3a, §4 |
| EA heap: animation clips | 48-112, grows with Sims | no | [measured] docs/215 #3b |
| EA heap: unnamed (lot imposters, EASTL) | 80-112, grows with lots | unknown | [measured] docs/215 #3c |
| Mono native heap | ~100, +12-25 per run | no | [measured] docs/215 #5 |
| Thread stacks | ~100-140 reserved | size cap only | [measured] docs/215 #6 |
| S3SS mimalloc arenas | 64 (128 after 9th arena) | no | [measured] docs/215 #8 |
| Reservations without a game frame | 42-125 | unknown | [measured] docs/215 #7 |
| String tables | ~20 with S3PP (~90 stock, ~290 big setups) | yes | [measured]/[community] docs/215 #10 |
| Package indexes, catalog, CAS geometry | ~18.5 + ~8 + 26-50 | small | docs/66, docs/215 |
| Pagefile-backed view at save | 86.8 | unknown | [measured] docs/215 #9 |
| Terrain, routing grids, water, lot data | never measured by name (inside ~940-1,135 MB with no owner) | routing out of process parked | docs/66 §3.3 |

What moved out: the bridge runs DXVK and the NVIDIA Vulkan driver 64-bit; before it that stack was ~430-640 MB in
process [measured parts, inferred sum, docs/215 #2/#4]; Apex says ~600 MB more free [community, docs/254]; Dalexlol
measured 1,937 MB largest free on a bridge run (#316/#40) [measured]. A D3D9 front stays in process. Apex FastMemory
changes allocator behaviour only (spin count, worker release, 60 s trim, top-down) and MemoryGuard keeps a reserve
freed at save [verified source / community, docs/148 #9]; neither moves memory out. No AllocSites growth rescan on
the bridge yet, so "the untracked 16 MB driver blocks are gone" is still [inferred].

### 4.2 Largest free block

- Vanilla, no DXVK: 176 MB after load, then crash; DXVK: 1,853 MB [measured, docs/05].
- DXVK BP live: 1,533 at start, 1,340 after 7-30 min; 30-min soak 1,485 -> 1,229 [measured, docs/66].
- Apex + bridge v0.3.1 after 10 min (README): mods off 1,711 / 1,753 / 1,790, on 1,586 / 1,531 / 1,676 (BP / IP / SV);
  our mods cost 115-220 MB; IP ranged 1,359-1,704 between runs [measured].
- Bridge 11-21: 1,686 -> 1,586 in 6.5 min; 1,816 -> 1,549 in 5 min with mode switching [measured].
- Heavy CC (DXVK era): one-time jumps of 160-220 MB per session, not returned on CC removal; 6 CC cycles -276 /
  -343 MB [measured, docs/201 §13, docs/200].
- Big old save (Dalexlol's, before the bridge): 662-675 MB [measured, docs/539]. Compositor registry leak
  14-52 MB per hour [inferred, docs/539].
- Error 12: 80 h of play without it after the 11-13 leak fixes [player-reported, not a soak]; no bridge
  long-session or big-save memory run.

### 4.3 4x-world estimate (all [inferred])

> Per-consumer version of this table for 2048 / 4096 / 8192 m, with the streaming decision: Boundless Atlas docs/011 (2026-11-26, memory budget by consumer, "stream only what is heavy").

| Owner | Today MB | 4x area | Why |
|---|---|---|---|
| Boehm heap | ~380 | +150-400 | more objects and lots; Sims ~0.15 MB each (docs/66 M11), more if population scales |
| EA unnamed cores (lot imposters) | 80-112 | +150-300 | grow with lots shown |
| Terrain, routing, water grids | unmeasured | +50-200 | heightmap 33.6 MB, G ~17 MB, R ~3 MB, blend layers up to 16 MB each (§1.2) |
| Save buffers | ~87 + 16 MB blocks | +80-250 | bigger world file |
| Animation clips | 48-112 | +30-100 | only if population scales |
| ResourceCache in use | ~400 | +0-100 | bounded by active lots and camera, not area |
| Mono native, stacks, mimalloc, images, STBL, indexes | ~800 | ~0 | fixed or CC-driven |
| **Total** | | **+460-1,350** | |

Against ~1,500-1,700 MB free after 10 min, a 4x world would start at ~400-1,100 MB free; with normal session loss
(100-250 MB), CC jumps and save-time blocks, long sessions land in the 662 MB old-save range or below, where Error 12
risk returns [inferred].

### 4.4 Verdict: partly true

1. True: the renderer stack (~430-640 MB) was the largest movable owner and the bridge took it out.
2. True in practice for today's worlds: 80 h without Error 12, ~1.5-1.7 GB free after 10 min.
3. Not true that "almost everything" moved: ~1.5 GB of unmovable owners remain (Boehm, ResourceCache, images, Mono,
   stacks, EA cores). FastMemory and MemoryGuard move nothing.
4. Still movable and not moved: resource read buffers (112-192 MB), string tables, the parked routing clone.
5. Unmeasured: bridge growth rescan, bridge long session, big save on the bridge, any large world. For 4x worlds,
   memory is a real constraint again.

## 5. C: fog-of-war world streaming

### 5.1 What already exists

- EA lot streaming only moves objects in and out of the scene; every lot's objects stay loaded from
  `Lot::Load 0xACF520` until world unload [verified, docs/28 §5.1, docs/38]. Scoring and `SetActive` on the main
  thread (`WorldManager_ScoreAndStreamLots 0xC6C290` -> `Lot_SetActive 0xAC20E0`); add/remove in the sim-thread pump
  with no budget [verified, docs/38]. docs/38 C7: "inactive lots only lose scene nodes" [verified].
- Terrain distance LOD: `WorldTerrain_Update 0xC845C0` picks LOD 1/3/4 per chunk by camera distance [verified,
  docs/38]. Open items are LOD churn and hysteresis (C5/C6), not unloading.
- Always resident: terrain heightmap; routing grid G for the whole world, built at load (`0x835390`, every lot's
  footprints rasterised in) [verified, docs/100 §1, §5]; all lots' object data, all Sims and households;
  ResourceCache (200 MB budget, 341-460 MB held in BP) [measured, docs/39].
- Camera streaming (async finalize `0x7297C0`, imposters, floors/walls-first `0xAEA680`): covered; docs/278 "do not
  build", ~0-3 ms [inferred].
- S3SS Lot Streaming Optimizations throttle `0xAC1130` [community docs/03; bytes verified docs/28 §5.4]. Our
  `[LotStream]` cost probe (`lotstream.json`) is built, never run [verified, docs/28 §5.5].
- Our sim-side fog of war is live: `FarFromViewers` (`mods/ts3fixes/src/Fixes.cs`) = outside camera reach (radius
  grows with zoom) and far from every active-household Sim [verified, code], which is exactly "only as far as our
  visible Sims"; native `[UnseenTable]` / `UnObjClass` per-tick class [verified, docs/227 §6]; unseen route caps,
  skips, fakes, relaxed routing, `autunseen` go-live [measured, docs/222, docs/243]; `unseenpark` -1.3 / +0.7 / -1.7%
  [measured, docs/243 §9-10]; `[UnseenRootMotion]` Mode 2 (docs/228), `[UnseenAnim]` Every=4 ship. Unseen Sims own
  89-99% of locomotion updates [measured, docs/143]; unseen ceiling ~240-500 ms/s per world, "a ceiling, not a gain"
  [inferred, docs/243 §3].

### 5.2 What is new and what it breaks

- **Unload terrain, routing, lot data far away:** little memory to gain (lot node 0x4C0 per lot, "noise",
  docs/38 C7 [verified]; the big owners are caches and the renderer, already evicting or out of process). Route plans
  need G and the per-lot quadtrees; `blockedNodeMap` and the high-cost maps depend on footprint insertion order
  [verified, docs/100 §3, §5], so rebuilding on wake risks stuck Sims [inferred]. No partial unload/reload path exists:
  `Lot::Load`, G and the quadtrees are built only at world load [verified docs; inferred absence of another path].
- **Suspend simulation of distant areas:** tick skipping is measured dead (five script throttles, whole-update
  decimation, RM Mode 2 came back ~0; the cost is per decision, not per tick) [measured, docs/243]. A separate thread
  for unseen work cannot be made safe (no locks, 58-88% non-SAFE IC leaves) [verified, docs/243 §3].
- **Household rule (CLAUDE.md):** household Sims are never unseen, even off screen, and their results must equal
  vanilla. A household Sim at work, at a rabbit hole or across town keeps that whole area live; freezing "their
  region" changes their routes, encounters and arrivals. This alone rules out area-level suspension.
- Story progression (EA and NRaas scenarios queue across lots): stalls or bursts on wake [inferred, docs/243 §5].
- Careers and rabbit holes: alarm pushes (`Career.RegularWorkDayGoToWorkHandle`) and per-lot alarms must keep
  running [verified, docs/243].
- Services (maid, mail, repo, `Services.Simulate`, `RoleManagerTask`) pick Sims across lots and must reach the
  household lot from fogged areas [verified, samples].
- Visitors and arrivals: come from fogged areas; GoHome and staged arrival already teleport only when
  `FarFromViewers` holds at the destination [verified, Fixes.cs]. A route across an unloaded region has no grid.
- Saves: region state cannot go in the .sims3 (no mod data in saves); every save, reset and travel would have to
  wake the whole world first [inferred, docs/243 §1 rule 7].
- Wake-up time skew: motives, positions, alarms must catch up; class flips happen 2.9/s in BP [measured, docs/243 §3].

### 5.3 Feasibility, risk, effort

- Data streaming part: new engine lifecycle code in a closed 32-bit exe, high risk (crashes, stuck Sims, save
  corruption), days of RE plus many builds, ~0 MB of memory that matters [inferred]. Not worth it.
- Sim part as fog by distance band on top of existing fallbacks: feasible, low-medium risk, every step with the
  household and selected-Sim exemption. Designed items first: `[UnseenThrottle] Rate=1` revival (~45 min + 1
  correctness + 1 timing job, docs/286 §0, ~10-20 ms/s inferred), `kHoursMemo` (docs/276 rank 1),
  `[UnseenAnim]` Every=8 (~15 min + 1 timing job).

### 5.4 Safe first step (measurement only, no new behaviour)

1. Distance-band split (inside reach / 80-200 m / 200 m+) in the existing `[UnseenProbe]` counters (`unAgentUs`,
   `unWaitUs`, `UnObjClass` classes); `FarCompute` already has the squared distance. ~20-40 min agent build; 1 smoke
   + 3 x 10-min jobs (BP / IP / SV).
2. Run the parked `[LotStream]` queue (`lotstream.json`, 3 jobs) for the per-lot add cost on camera pans.
3. A per-band memory line (`/rescache` style) to confirm distant lots hold little memory (docs/38 C6 open "measure").
4. Only if a band owns > ~5 ms/s: tighten the existing caps, skips and park thresholds for that band. No new mechanism.

Verdict: **partly**. Keep the sim fog of war (exists) and extend it by distance band after measuring; drop data
streaming / unloading.
Re-evaluated 2026-11-22 with sidecar saves allowed and unbounded worlds as the goal: **possible and staged** (§7.8);
the "drop data streaming" verdict stands for today's 2048 m worlds only.

## 6. Gaps

- A1: no 4096 m world loaded yet (queue `large-worlds-load-1122`); camera clamp routine, minimap, native object save path,
  callers of the null terrain-cell lookup, WTXT per-cell paint layout, per-lot resource set (no-lots mode) unread.
- No run records world dimensions, SV's lot count, heap size or GC ms/s by world or population.
- (corrected 2026-11-22) RouteEarlyOut was run 2026-10-22 (docs/142 §10): the BP 4 s car-only plan spends 0 ms in world A\*; SV/IP slow plans 0-43% A\*, none provable; the split by phase is docs/164 `[RouteJobPhases] SyncPlans`;
  the SV 7.3 s crowd plans have no named cause.
- Water grid build time on SV: 0.97 s (docs/39 §10.1, `[LoadProf]`); its phase split is the RUN FIRST queue of docs/100 §13; scaling with water area not measured.
- World lot alarm pass (956 ms) has no owner doc.
- Save time vs object count not measured.
- Memory: no AllocSites rescan on the bridge, no bridge long session, no big save on the bridge; terrain/routing/lot
  memory has never been measured by name.
- docs/143, 176, 194 §7, 222, 228, 276, 288 read from their index lines only; D1 build state not checked.

## 7. Design: larger and unbounded worlds by region streaming (sidecar saves allowed)

Rodney 2026-11-22: the goal is to make larger and unbounded worlds possible; sidecar files next to the save are
allowed for anything the .sims3 cannot hold (no mod data inside saves). Design only; implementation belongs to the
separate project (§1.4). All [inferred] unless cited.

### 7.1 Two levels

- **Level 1, bounded big world (up to 4096 m):** everything resident, as today. Needs only the data (generator) and
  the §1.2 sites; the sim-side fog (FarFromViewers, unseen caps / park / go-live, §5.1) keeps the per-tick cost of far
  areas low. Memory is the limit: +0.5-1.35 GB for a fully built 4x world (§4.3).
- **Level 2, region-streamed world:** the world is a grid of regions (tiles); only an active set holds lot objects,
  instantiated NPCs and render data. World bounds are fixed at load to the full extent (they are cached, below); the
  contents inside are paged. This is what makes "unbounded" possible inside 32-bit memory: the active set stays near
  today's 2048 m content, the rest lives on disk.

### 7.2 Regions and the active set

- Tile = 2048 m (8 x 8 terrain cells; matches today's worlds, so one tile's content ~ one shipped world) or 1024 m if
  page-in hitches are too long. Tile ids from the 256 m cell instance scheme (z << 16 | x).
- Active set = tiles within a radius of the camera, **plus every tile that holds a household Sim, the selected Sim, a
  Sim of unknown scope, or the destination of one of their routes or alarms** (hard rule: household Sims are always
  simulated and their surroundings stay loaded; results for them must equal vanilla, so they never wait on a fogged
  region they are walking into: the tile pages in ahead, by route destination and career / rabbit hole alarm).
- Hysteresis: a tile leaves the set only after N sim minutes outside every reason, never during a save, travel, reset
  or Edit Town.

### 7.3 Per data owner: resident or paged

| Owner | Size at 4096 m | Plan | Why / hard part |
|---|---|---|---|
| World bounds `WM+0xB4..+0xD0`, physics / scene quadtree roots, terrain cell vectors, G dims | small | resident, sized once for the full extent | cached at load (§1.2): growing at runtime means reallocating and re-pointing every cached copy in a closed exe |
| Terrain heightmap + WTXT cells | 33.6 MB heightmap; cells per 256 m | heightmap resident to 4096 m; past that page per cell | the cell vectors already allow empty cells: lookups return null past range (`0xC2A470`), so "absent" exists in principle; render chunks live in a growable vector (docs/38) |
| Routing graph R | ~3 MB at 4096 m, ~0.2 MB per km² | **resident** | small; world A* must see the whole graph so routes cross tiles; refs have no bounds check, so paging R records is a crash risk for no memory gain |
| Water / footprint grid G | ~17 MB at 4096 m (4 m cells) | resident to 4096 m; past that a paged grid behind hooks on every G accessor | one allocation sized at load and every lot footprint rasterised in at load (`0x835390`, docs/100); insertion order matters for `blockedNodeMap` (docs/100 §3, §5) |
| Lot hash / footprint grid, lot list | small | resident | managed `LotManager.AllLots` and native lists hold every Lot; keep the Lot objects, page only their contents |
| Lot objects (furniture, buildings, scene nodes) | the big owner with lots | **paged per lot** | `Lot::Load 0xACF520` builds them only at world load (§5.2); needs a lot-level unload / reload path (§7.5) |
| Instantiated NPC Sims | ~0.15 MB each (docs/66 M11) + animation | paged: drop `CreatedSim`, keep the `SimDescription` | EA already runs most townies with no `CreatedSim`; GoHome / vanish destroy the instance today; careers and alarms live on the description |
| Household Sims and their lots | - | always resident and simulated | hard rule |
| ResourceCache, render data | bounded by camera | unchanged | already camera-driven (docs/39) |

### 7.4 Crossing region edges

- NPC routes into an unloaded tile: no walk; the Sim is de-instantiated at the edge and re-instantiated on arrival
  (the existing staged-arrival / teleport-when-`FarFromViewers` pattern, unseen only).
- Household / selected-Sim routes: the destination tile is paged in before the route starts; the route itself is
  vanilla (R is resident, so the world A* path is the same). A wait for page-in is the only difference, and it happens
  before the route is planned.
- Visitors and services coming from fogged tiles spawn at the active-set edge (as off-lot arrivals already spawn).
- Objects never cross: an object's tile is its lot's tile.

### 7.5 Lot page-out / page-in (the hardest part)

- Candidate path: the lot library serializer (Edit Town "save to library" / place from the bin) already turns a built
  lot into a package and back onto a plot; page-out = serialize the lot's objects to a sidecar lot package, then
  delete them through EA's normal destroy path (as a player bulldozing furniture); page-in = place from that package
  onto the same plot (no fee, same ids where possible).
- Quiesce rules before a page-out: no Sim on the lot, no route job in flight that touches it, no interaction or alarm
  holding one of its objects, no save in progress; else retry later.
- Object identity: other objects, Sims and managed code hold `ObjectGuid`s and native pointers into the lot (route
  plans, footprints, quadtree and scene nodes, Mono handles). Destroy goes through EA's own path, so dangling native
  pointers are EA's normal delete case; managed references by id must be re-bound on page-in (sidecar keeps an old ->
  new id map) or the objects must keep their ids.

### 7.6 Sidecar save

- Next to the save: `<Save>.sims3` + `<Save>.lw/` (manifest JSON, per-lot packages of paged-out lots, per-tile
  version hash of the world file it was built from). Nothing in the .sims3.
- Save: EA saves the loaded part as today; paged-out lots are already in the sidecar. Simplest safe first version:
  page every lot in before EA's save (slow, never inconsistent), then optimise.
- Load: EA restores the loaded tiles; the manifest says which lots were paged out, which are then cleared and served
  from the sidecar on demand. Missing or mismatched sidecar = those lots come back as the world file has them, logged
  (clean up our damage on load rule).
- Travel, moving, Edit Town, reset: page everything in first.

### 7.7 What the closed 32-bit exe makes hard

1. **Pointers into region data:** route plans, footprints, quadtree / scene nodes, script handles; unload must quiesce
   (§7.5) and use EA's delete path only.
2. **World bounds cached at load:** `WM` bounds, G dims, physics root (4096), scene quadtree box, terrain cell vectors;
   so "unbounded" means a fixed, generous extent chosen at load, contents streamed inside it. A truly endless world
   would need coordinate rebasing, which the exe cannot do; f32 positions keep ~1-2 mm steps out to ~16 km, so a fixed
   extent of several km is not limited by float precision.
3. **Format caps past 4096 m:** 0x3D8632D0 u16 tile index, physics root, R 16-bit chunk index (8192 m), 256 m cell
   instance in u16 metres (65,535 m). Each needs a loader hook or a data split (e.g. several blend maps by tile).
4. **Mono-side world refs:** lists of lots, rabbit holes, world objects, venue caches; keep the Lot objects alive and
   page only contents, so these lists stay valid.
5. **Address space:** the active set must stay bounded; page-in of a tile is a burst of allocations (heap
   fragmentation, largest free block), measured per tile before any rollout.
6. **Story progression and services** (EA and NRaas) iterate all lots and Sims: they must see paged-out lots as
   "present but empty" or skip them; a census of those loops comes first.

### 7.8 Verdict C re-evaluated (sidecar saves allowed)

- The save blocker of §5.2 is gone: region state, paged-out lots and NPC bookkeeping fit a sidecar.
- The household rule is met by design (household tiles always active, page-in before their routes).
- What remains is engine lifecycle work: a lot-level object unload / reload (§7.5) and, past 4096 m, paged G and wider
  formats. Memory gain is real only when a world holds more content than the 32-bit process can keep resident.
- **New verdict: possible and staged.** Still not worth it for today's 2048 m worlds (little memory to gain, §5.3);
  it is the enabling path for dense 4096 m worlds and for anything past 4096 m. Level 1 (lifted caps) comes first and
  is useful on its own.

## 8. Phases, effort, first proof (for the separate project)

Wall-clock agent time; game runs listed separately.

**(a) Lifted caps, to 4096 m (level 1)**

| Phase | Work | Agent time | Game runs |
|---|---|---|---|
| a0 | Cap inventory (§1.2), generator (§1.5), queue (§1.6) | done | - |
| a1 | **First proof:** run `large-worlds-load-1122`; read bounds, G, load time, largest free, far-corner render | ~15 min prep | 3 jobs (~35 min) |
| a2 | Per broken site: cause from the fault, then a fix (data in the generator, or a hook / patch behind a default-off switch in that project); `SceneQuadTree=4096,512,7` key test | ~30-60 min per site | 1 job per site |
| a3 | Grow WTXT paint data (decode its per-cell layers) so the grown area can be painted | ~1-2 h | 1 job |
| a4 | Lots across the whole area (copy / offset template lots, R edges across quadrants, lot footprints into G) | ~2-4 h | 3-5 jobs |
| a5 | Save / reload matrix, memory and timing profile vs SV | ~1 h | save matrix + 3-4 timing jobs |
| a6 | Past 4096 m: 0x3D8632D0 wider index (loader `0xC238E0` / saver hook or split maps), physics root `0xFEFBCC`, R past 8192 m | ~4-8 h per format | 2-3 jobs each |

**(b) Region streaming (level 2)**

| Phase | Work | Agent time | Game runs |
|---|---|---|---|
| b0 | Measure: distance-band split in the unseen probes + per-lot memory line (§5.4) | ~1 h | smoke + 3 jobs |
| b1 | NPC instance paging: drop `CreatedSim` of NPCs in far tiles (unseen only), memory per tile | ~2-3 h | 2-3 jobs |
| b2 | **First proof:** one far empty-of-Sims lot serialized to a sidecar package, cleared and restored; object list and ids compared before / after, no script errors, household untouched | ~4-6 h RE + build | 3-4 jobs |
| b3 | Tile manager: active set, household / route prefetch, quiesce rules, manifest | ~8-12 h | 5+ jobs |
| b4 | Sidecar save / load / travel with page-all-in first, clean-up on load | ~4-6 h | save matrix |
| b5 | Story progression / services census over paged-out lots | ~2-3 h static | 2 jobs |
| b6 | Past 4096 m: paged G behind accessor hooks | ~8-16 h RE + build | 3-5 jobs |

First proof per track: (a) the queued 4096 m load (a1); (b) the single-lot round trip (b2), the smallest test of the
hardest lifecycle question.
