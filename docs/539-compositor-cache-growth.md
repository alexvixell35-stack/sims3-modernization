# 539. ResourceCache compositor growth: holder, liveness, in-process fix (2026-11-07)

Static RE on Steam TS3W.exe 1.67 (Capstone disassembly of `game-copy/bin/TS3W.exe`, decompiled scripts, `native/ts3native/castrim.cpp`); no game run. Measured numbers come from Dalexlol's runs on his old save (PR #284, #285, `docs/data/results/2026-11-05-alexvixell35-stack-*`), treated as data and not re-measured here. The probes and Mode 3 of §6 and §7 are built (§7, ledger §446), selftest only, never run in the game. Goal (Rodney, 2026-11-07): keep it in process, stop the growth, reuse and release what is unused, household Sims untouched.

Tags: [verified] = read in the disassembly or scripts, or logged in a run; [measured] = a run's numbers; [community]; [inferred] = reasoning, needs a probe. Ledger: docs/51 §445, §446.

## 0. Short answer

- **Holder:** the CAS compositor service registry, a hash map at SimService `+0x104` (buckets `+0x108`, count `+0x10C`, size `+0x110`), node copy AddRef `0x5ED8B8` [verified code; measured: 78% of its entries sit at exactly 2 references]. The brief's "second reference is elsewhere" is already answered by PR #284.
- **Leak or cache:** a leak with a working cache behind it. The registry has no release path in play. When an outfit is dropped (`Uncache` `0x5EE5A0`) its compositors stay in the registry for good, so the ResourceCache never sees them go cold.
- **New in this doc:** EA already computes which compositors are live, at every save (`0x5EFD60`, §2.3). Anything in the registry outside that set would not survive a save and reload in the unmodified game. That gives a sharper "unused" test than CasTrim's age and op-list rules, and it corrects ledger I-942-2 (the registry is read at save, not only by export code).
- **Size, read this first:** these entries are small. CasTrim erased 120k of them and `priv` fell 19 MB, `eaRes` 8 MB, largest free block no better (662 vs 675 MB, noise) [measured, al247 vs al251]. The "~1 KB per entry" in the brief was a correlation. Two bounds stand: 170-270 B each from the code and `priv` (§2.4), 75-170 B each from Dalexlol's reading of the same run pair (issue #40). **P7 decides.** **For Error 12 this is a long-session fix, not a 60-minute one:** 14-52 MB per hour on that save across both bounds [inferred], so it should show from hour 3 or later, and in `rcN` straight away.
- **Recommended:** `[CasTrim] Mode=3`, a save sweep on top of the existing module (§6, option 1), after the probes in §7 (both built, off by default, not run). Also read the outfit map's size (§6, option 3): it may hold more bytes than the registry.

## 1. Lifecycle of one compositor

### 1.1 Object

Size `0x68` (`EA_AllocTagged`), type `0x033A1435` vtable `0xFE68D8` (ctor `0x6004C0`) or `0x0341ACC9` vtable `0xFE68F4` (ctor `0x600560`, calls the first) [verified]. Key at `+8..+0x14`; child op vector `+0x28`; op array pointer `+0x5C` with count byte `+0x60`, stored inline at `+0x38` up to 4 entries, on the general heap above that (`0x5FE520`); texture handle slots start at -1 [verified].

### 1.2 Who creates it

| Path | Address | What it does | Instance |
|---|---|---|---|
| SimBuilder slot | `0x5E2C10`, under the static lock `0x11CE638` | find or create in the part's map (`part+0x70`, keyed by a u32 slot), tag `CAS/SimBuilder/SetPartCompositor` | random, `0x5DFDB0` |
| Pattern key | `0x5E2D60`, from `IC SimBuilderGetPatternKey` `0x5A565A` and `0x5C0C23` | same slot call, always the `0x0341ACC9` class | random, `0x5DFDB0` |
| Preset applied to a part | `0x5E2FA0`, from `ApplyPresetToPart` `0x5AB57A`, `InjectHairColors` `0x5ABAC9`, `InjectEyebrowColor` `0x5ABDAA`, `SetPartCompositor` (`0x5E3140` <- `0x5A55CF`) | a compositor built from the Complate preset (`0x5BFAB0` -> `0x5C1B30`) takes the slot's key, replaces the slot's object (AddRef return `0x5E306C`), is registered, goes into the part's arrays (`part+4` or `part+0x28`, RefPtr `0x9DC510`, AddRef return `0x9DC52B`) | the slot's |
| Outfit fallback | `0x5BFAB0(list, 1, 1)` from Parse `0x5EA8EE` and ConstructBuilder `0x5E8A65` | rebuilds from the preset when the part cannot load its stored compositors, or the part record's `+0x64` flag is set (§5.3); registered at once | random, `0x5C00C0` (type `0x033A1435`) |
| Lot objects | `0xAD4680` | designed objects, not SimBuilder; keys read from a stream, kept by `Lot/CompositorManager` | stored |

Random source [verified]: `0x5DFDB0` draws two 32-bit values from the builder's RNG at `+0x200`, instance = high word from the first, low from the second, group 0. It retries up to 1000 times while the manager's cache (`+0x58`) or a database (`+0x44`) already has the key, or the builder's own check (`[+0x10]` vtable `+0x34`) reports a clash. `0x5C00C0` is the same loop on the static generator `0x11CE4A4`. The instance carries no information about the inputs.

### 1.3 Into ResourceCache and the registry

1. Register: manager vtable `+0x5C` = `0x4B00C0` with flag 1 -> cache Add `0x732A00`. The cache takes one reference (`0x732AC5`) and sets the object's cache pointer `+0x24` [verified; docs/191].
2. `SimBuilder.CacheOutfit` -> `IC` `0x5A5D40` -> `0x5E2A00`: a new outfit (`0x2D0`, `CAS/SimBuilder/CacheOutfit`), filled by `0x5E1290`, stored in SimService's outfit map (`+0xD4`) by `0x5EF1D0`, then `0x5E2930` walks the part list (`+0x1E0`), calls the insert `0x5EECD0` on every compositor in each part's map and clears that map (`0x515510`) [verified].
3. Insert `0x5EECD0`: registers with flag 1 again, inserts the node (`0x5EE2E0`, unique key; node copy `0x5ED870`, the kept AddRef at `0x5ED8B8`) [verified]. Other callers: ConstructBuilder `0x5E8AAC`, Parse `0x5EA93E`, download content `0x99C294` [verified].

### 1.4 Every reference

| Holder | Takes | Drops |
|---|---|---|
| ResourceCache | Add `0x732AC5` | remove (`+0x5C` flag 0), trim |
| Registry node | `0x5ED8B8` | `0x5EE260` from `0x5EE640`, only from clear-all `0x5EEA90` <- `0x5F0D50` (service reset) [verified] |
| Part map | `0x5E2D18`, `0x5E306C` | `0x5E2930` at CacheOutfit, part removal `0x5E2ED0` -> `0x515510` |
| Part arrays | `0x9DC510` | part removal, builder destroy |
| Outfit slots | `0x5AF160` (`0x5E8ACB`, `0x5EA95D`), copies via `0x9EC420` | outfit destroyed |
| Outfit part records | `rec+0x14`, `rec+0x38`, 5 slots each, records `0x68` at outfit `+0x98..+0x9C` (read by `0x5E7690`) | same |
| Finds in progress | cache Find `0x7324E0`, get `0x736718` | caller |
| Lot manager | `0xAD4250`: node `{key, count +0x10, compositor +0x14}` | counted release `0xAD4390` |

Only the registry node has no release path in play. A compositor at 2 references is the cache's plus the registry's.

### 1.5 Who frees

Release `0x731D40`: at the last reference, vtable `+0x14` = `0x607970`. On the main thread it calls `0x606C80` (drops the handles and the op array, tells the material compositor) and returns 1, then vtable `+8` `0x601AF0` deletes. Off the main thread it posts `0x607040` through `0x72A680` and does not delete yet [verified]. So a sweep must not assume memory comes back on the calling thread.

### 1.6 The other candidates in the brief

| Candidate | Verdict |
|---|---|
| Compositor MRU (`CAS/CompositorCache`) | not the holder: `0x5ED8B8` is; holds finished textures [inferred, docs/191] |
| Bake queue, finished-bake list | not seen as a long-term holder: `CasSim_StepProcessCompositorRequest` holds requests; requests end in `CasSim_DestroyRequest` `0x5EE860` [verified symbol; not traced per reference] |
| Sim outfit records | the third reference for live outfits (29,999 of 135,261 at 3+) [measured] |
| Scene or model | via the outfit; not separately traced |
| Managed handle | no: the script `SimOutfit` wrapper holds a key and an outfit handle, not a compositor reference [verified script]; a finalizer cannot be the holder of 2-reference entries, since the registry accounts for them [measured] |
| CAS geometry `0x5C7906` | allocated in `CAS/GeometryResource` load `0x5C75E0`, buffer at `+0x80`, size `[+0x7C]` x `[+0x78]` [verified]. Who keeps the object alive is not traced; `ctGeoKB` counts bytes allocated, RcCensus the live ones |
| CAS blend data `0x5A2FFF` | allocated in `CAS/BlendGeometryResource/BlendRegionData` load `0x5A2D70` (from `0x5A3320`), buffer at `+0x30` [verified]. Holder not traced; `ctBlendKB` as above |
| `Lot/CompositorManager` | second holder: 292 of 1,031 sampled, none in the registry; counted per lot object, intended data [verified counted add and remove exist; release not read in full] |

## 2. Leak, retained cache or intended data

### 2.1 ResourceCache eviction rule [verified]

- Refcount at `+4`, steps of `0x100`; the low byte is a short busy mark.
- Release to exactly 1 reference with a cache pointer set: `0x7328B0` puts the entry on the flush list (`+0x3C`, bytes `+0x98`). AddRef from 1 to 2: `0x7320D0` takes it off. "Cold" `0x731E30` = on that list.
- Trim `0x732E90` walks only the flush list, down to budget `+0x8C` (docs/198 §4.1).

So an entry at 2 references can never be evicted. That is why these are "not cold" and why every trim, Apex MemoryGuard's included, leaves them.

### 2.2 Why it is a leak

`Uncache` `0x5EE5A0` unregisters the outfit and erases its map node, with no count check and no call to the registry [verified]. The outfit's slots release, its compositors fall to 2, and nothing drops them. The registry was built as an in-memory database for compositors that have no package (al246: 0 of the old ones has a database) and the matching delete was never written. Some of it is intended data: the outfits of Sims that exist. The rest is garbage.

Growth [measured]: 106,830 inserts in 35 min, 0 erases; `rcN` +96% explained by the registry; about 46 inserts/s.

### 2.3 EA's own liveness test, at save [verified]

The SimService save `0x5EFD60` (entries `0x5F0360`, vtable data `0xFE5134`, and `0x5F0390`; both gated on `[0x11ECBC4]+0x45`) does this:

1. **Inventory** (`CAS/SimService/OutfitCompositorInventory`): for each outfit in the map at `+0xD4`, skip it if its count `+0x14` is 0 and `+0x130` is set; otherwise `0x5E7690` adds every slot compositor and, through `0x5E6FC0` and `0x6038E0`, its children by key.
2. **Cross reference** (`CAS/SimService/OutfitCompositorsCrossReferenced`): walks the registry and keeps only entries also in the inventory.
3. `0x5ED3E0` (one caller, `0x5EFFD7`, the cross-referenced set is its fourth argument) writes those to resource `0x342779A7`; `0x5EFB50` writes the outfits.

The count comes from script: `OutfitCategoryMap`'s persist writer calls `sSimOutfitAddRef` for every outfit it writes -> `0x5EE090`, which sets `+0x130` and adds 1. In this range nothing decrements it (`0x5EE0C2` is the only add; no sub) [verified scan of `0x5EB9C0-0x5F16F0`]. So at a save the live set is "named by a persisted SimDescription's outfit map, or marked by an earlier save".

Consequence: registry entries outside the cross-referenced set are not in the save. After a reload the unmodified game does not have them. That is also why the old save starts at 29k and grows from there [inferred].

Correction: I-942-2 said the map is read only by export code (`castrim.cpp`'s comment is fixed). `0x5EFD60` and the service teardown `0x5F0BA0` read it too.

### 2.4 What an entry costs

- Fixed: `0x68` + registry node `0x1C` + cache node + heap headers, about 170-200 B; the cache's own figure is `0x68 + 4 x ops` (`0x607970`). Child vector data on top [inferred, not sized].
- al247 vs al251: 111k fewer entries, `priv` -19 MB = 170 B each. If the off arm did not install CasTrim's 11 MB tables (`VirtualAlloc`, committed) the real figure is about 30 MB = 270 B [inferred; not stated in the run].
- Dalexlol's reading of the same pair (issue #40, 2026-11-07): `eaRes` -8 MB and `priv` -19 MB over 111k fewer entries = **75-170 B each**, so 170-270 B starts at his upper bound. With al247's off arm (`rcN` +142k in 45 min, about 190k/h) that is **14-32 MB/h**, not 33-52.
- **P7 is the decider** between the two; every size figure in this doc stays a bound until it has run.
- `eaRes` -8 MB is a low bound: freed heap slots stay in the core.
- The "1 KB" in the brief is `eaRes` over `rcN` in the same window, which does not hold up: with the trim on, `eaRes` still grew +96 MB against +108 MB.
- Rate: off arm 3.2k entries per minute after minute 15. At 170-270 B that is 33-52 MB/h (at 75-170 B, 14-32 MB/h), linear, plus bucket arrays (two tables at 300k entries are about 1-2 MB each and move on rehash; `eaDirN` +54 off vs +9 on) [inferred].

## 3. What creates them

The driver is any script path that ends in `CacheOutfit`, or in the rare native fallback. Script instance names [verified grep of the decompiled scripts]:

| Event | Script | Unseen NPCs? |
|---|---|---|
| New Sim / NPC | `Genetics.MakeSim`, `GeneticsPet`, `SimDescription.CreateSimDescriptionInternal`, `NpcDriversManager.CreateNpcDriver`, `CityHall.TryCreateNPCMayor`, `RoleManagerTask.FillForeignRole` -> `BuildOutfitsOfType` | yes |
| Missing outfit category at a switch | `Sim.SwitchToOutfitDelayed`, `Sim.GetCategoryAndIndexToUse` -> `MakeCategoryOutfitUsingEveryday` (new builder, 1024) | yes, any Sim lacking the category |
| Uniforms | `Occupation.ApplyOccupationUniform`, `TryApplyUniformToOutfit` (28 sites: bars, resort workers, mechanical bull, costume chest), `Sim.CreateAndAddTempOutfit`, `BuffSinged.SetupSingedOutfit` | yes |
| Age and body | `SimBuilderTask` (`Aging_*`), `BuildPregnantOutfit`, `ApplyFacePaintToOutfits`, `SetSkinAndHairForAllOutfits`, `RemoveBodyTypeOnAllOutfits`, `ReplaceHiddenCasPartsForOutifts` | yes |
| Occult, buffs | `OccultPlantSim`, `OccultMermaid`, `OccultRobot`, `BuffTransformation`, `BuffTrendsetter`, `BonehildaCoffin` | yes |
| Objects | `Shower` (`MakeCategoryOutfitUsingEveryday_*`), `DaySpa`, `ClothingPedestal`, `Mannequin`, `FirewalkPit` | yes |
| CAS | `CASLogic` (`CommitBuilder`, `CloneOutfits`, `GetCurrentOutfit` via `0x5A5D40`) | no |
| Save fix-ups | `SimDescription.FixData` (only from `CASLogic.FixSimsFunction`) | no |
| Mods | NRaas `CASParts.cs:310`, Dresser `AddOutfit.cs:79`, MasterController `CASBase.cs:1895`, Ani pedestal | depends |

Not drivers by themselves [verified code]: LOD change (`CasSim_StepLoadOutfit` `0x5EEFB0` reuses outfit data; only the `+0x64` parts rebuild), thumbnails (`ThumbnailManager_BuiltCompositors` `0xD4FD59` calls the Complate build but not the insert).

**Which one dominates stays unknown.** 46 inserts/s is about 2-5 outfit builds per second at 10-20 compositors each [inferred], which looks high for autopilot with story progression off. The first probe (§7, P1) names it from `CacheOutfit`'s instance name.

## 4. Reuse: can identical inputs share one entry?

Inputs: the preset XML text (`CAS/Complate/*`), the op list with its parameter blocks (`0x602380`), child keys (parameter `0xDCFF6D7B`), fabric and colour values. All are readable, so a content hash is possible [inferred].

Against it:

- **Mutable:** `0x5E2C10` finds the part's slot by key and `0x5E2FA0` replaces its object on the next preset; a shared entry edited in CAS would change another Sim. Hash-keyed entries would need copy on write, and a preset edit in CAS is the main use.
- **Per-Sim state:** tint and skin parameters are in the op parameters, so most NPC outfits will not match unless they are the same outfit. The real hit rate is unknown.
- **Save format:** keys go into the save and into outfit streams; deterministic keys are fine, but a key changing meaning between versions is not.
- **Household rule:** a shared compositor is a direct path from an NPC change to a controlled Sim's texture.

The likely gain is small: it would save at most the 170-270 B per duplicate, and that is the same memory a release saves without the risk. **Not recommended.** One narrow case is worth a count: identical compositors from `MakeCategoryOutfitUsingEveryday` (same Sim, same everyday) if the probe shows that path dominates. Dedupe at script level, "reuse the outfit already made", would avoid the build itself and gain stutter too.

## 5. Recycle and release

### 5.1 Release points

| Point | Safe? | Notes |
|---|---|---|
| After bake | no | the outfit still names it; rebuilds need it (`0x605CE0`) |
| On LOD down | no | same |
| On outfit `Uncache` `0x5EE5A0` | **yes, for direct compositors at 2 references** | another outfit sharing it holds a RefPtr, so 3+; children by key need a `Named` check |
| On Sim destroy | indirect | `ClearOutfits` / `RemoveOutfitInternal` call `Uncache` [verified]; that a destroy reaches them is [inferred]; same point |
| After a save `0x5EFD60` | **yes, strongest** | EA's own live set; §2.3 |

### 5.2 Pool

No. The objects are never freed today, so a pool has nothing to recycle, and once they are freed the EA small-block bins reuse them already. `eaRes` stopping at 873 MB while `rcN` grew shows the heap reusing slack [measured]. Allocator work is also Apex's area.

### 5.3 Cap, and what happens when an evicted entry is wanted [verified paths; result on screen not observed]

| Asker | On a miss |
|---|---|
| Parent rebuild `0x605CE0` -> `0x6057F0` | get fails, handle -1, that texture is not built |
| Parent Load `0x60733E` | rebuilt from the parent's own data |
| Get, async, blit | load from a database; none has these keys (al246) |
| Outfit load, Parse `0x5EA754` / `0x5EA7E6` -> `0x5E55B0` | a failed request sets a flag; at `0x5EA84A`, if the outfit's component mask at `+0xD0/+0xD4` is all ones and its flags `+0x38 & 0xCF00` are neither 0 nor `0x100`, it **rebuilds from the preset with a new random key** (`0x5BFAB0`, `0x5EA93E`) |

The last row answers Dalexlol's open option 3 (PR #285, al250): a failed outfit-part load recovers for ordinary outfits; uniform-masked outfits and those flags do not, and the part is left without that compositor. No crash path was seen [inferred: no null use read past that branch].

A plain cap (evict oldest past N) can hit a live outfit. It is what age-based CasTrim does, and age alone was unsafe (6,293 would-miss, al243/al244).

## 6. Fix options, ranked

**Error 12 bar:** the largest free block is the only bar. None of these moves it in a 60-minute A/B; all of them cut a linear loss. Rodney's "memory gains any size" rule applies, and this note is the flag: it stops a long-session loss, and is not the main eater of address space. Household: every option below keeps refs >= 3 untouched, and an explicit household and selected-Sim keep list is required in code.

| # | Option | Hook | Effect | Risk | Effort |
|---|---|---|---|---|---|
| 1 | **`[CasTrim] Mode=3`, save sweep** | detour `0x5ED3E0` (one caller), fourth argument = live set; snapshot registry entries at exactly 2 refs, part vtable, not live, not `Named`, not on the keep list; erase on the insert thread as today | registry = live set after each save; `rcN` flat across saves; ~14-52 MB/h kept on the old save (§2.4); replaces the age rule and needs no 11 MB tables | a session with no save trims nothing; relies on script AddRef running before the native save (P5) | agent build ~45 min; game below |
| 2 | Uncache release | detour `0x5EE5A0`: gather `0x5E7690` closure first, after EA's call erase those at 2 refs and not `Named` | immediate, O(parts), no save needed; covers only outfits that get `Uncache`d | children shared by key with a live parent: `Named` needed | ~45 min, after P1 shows its share |
| 3 | Outfit map reaper (research first) | read `+0xE0` and the count-0 share; if it grows, reap count-0 outfits nobody uses | unknown; each is 0x2D0 + records, and may pin CAS geometry (`0x5C7906` +16.6 MB in 20 min, al239) | an outfit in use by a script field; needs a third-reference test | probe ~30 min; design after |
| 4 | Keep Mode 1 (age + `Named` + `Async`) | exists | 79% less `rcN` growth, 0 misses in 120k [measured] | 5 would-miss in count mode, no gain in `big` | none; baseline arm |
| 5 | Script reuse of a just-made outfit | `MakeCategoryOutfitUsingEveryday` | also saves build time (stutter) | NPC only; only if P1 shows it | ~1 h after P1 |
| 6 | Content-key reuse | `0x5E2C10` | small | mutable state, §4 | rejected |
| 7 | Pool | allocator | none | Apex area | rejected |

**Mode 3 design notes:**

- Keep rule, as built: erase only when (1) refs == 2, (2) vtable is a part compositor, (3) not in EA's live set, (4) not named by any survivor (`Named`), (5) key not on the keep list, (6) last insert or find older than `MinAgeSec` (60 s, for builds in flight), (7) never found through the async loader `0x729E10` (`Async`: al249's 6 would-miss all came that way, from an outfit load `0x5E9AA0` -> `0x5E55B0` -> `0x5E53E0`, and al251's 0 misses depended on it; Dalexlol, issue #40). All must hold, so the order only decides which counter a kept entry lands in.
- `Named` runs last and to a fixed point (up to 64 rounds): every survivor of any kind names its children, a named candidate survives and names its own. A full table or no fixed point keeps everything.
- Refusals, logged, nothing swept: no keep list, list incomplete or stale (10 min), no script mark between the keep list and the native save (the P5 order), CAS or Edit Town, a save off the insert thread, an empty live set. Mode 4 logs the same reason as "would refuse".
- **Household and selected Sim:** ts3fixes (`CasTrimKeep.cs`, detour of `OptionsModel.SaveGameSynchronous`) pushes the outfit keys of the active household, the selected Sim, their `CurrentOutfit` and maternity maps before a save; the native side closes them over `0x5E7690`. A Sim of unknown scope counts as household. They are in EA's set already, so this is a belt on top, and a key on the list that EA's set misses is logged.
- **Stand-down:** no sweep while CAS or Edit Town is open (conservative: exports read the registry there) and none during a save; erase batches run after the save returns.
- **Export:** `Contains` `0x5EDAB0` (Sim, lot and world export). Live outfits are kept, so a live Sim exports as today; a manual check stays in the matrix, as for CasTrim.
- **Save data:** no change to what is written, since the sweep only removes what EA did not write. No mod data in saves.

**Apex area:** docs/191 puts native leaks and cache caps there, and Apex Radiance's source has no reference to any of these addresses (grep of `game-copy/apex-src`: 0 hits). Under "Apex-area experiments first" we build and test off by default; a proven result goes to them as a report. The pool, had it been kept, would be theirs (allocator). `[ResourceCache] TrimAfterCleanup` is unrelated: it trims cold entries, and these are never cold.

## 7. Test plan

Built 2026-11-07, ts3native 0.36.187, branch `feature/castrim-mode3`, selftest only (ledger §446). Switch `[CasTrim] Mode=3` (runtime `ctMode`, `--ab castrim`), off by default, `Mode=4` = count only, `Probe=1` = P1-P3, P6. Counters v171 (docs/registry.md): `ctSaves`, `ctLive`, `ctDoomed`, `ctSwept`, `ctKeptHousehold` and the rest of the block, plus the existing `ctWouldMiss`, `ctMissed`, `ctOtherThread`. Script channel: text ops 14-19 (0x87), answered false below Mode 3. Tables grow from 11 to 14 MB when installed. Queues: `tools/harness/queues/compositor-probe.json` (Baseline-BP, Mode 4, then Mode 3), `compositor-probe-oldsave.json` (Dalexlol, `1111 kit5`, 65 min, Mode 4, all probes).

**Stress repro `compositor-churn`** (in `repros.json`, extends `castrim-checks`; memory work uses stress cycles, not soaks; `outfit_change` stands in for a uniform, the agent has no uniform command): six NPCs and two household Sims, each cycle uniform on, uniform off, new NPC, age a townie (`age_up` `pick: townie`), save to a scratch file; a mem mark per cycle: `rcN`, RcCensus per type, registry size, outfit map size, `eaRes`, `big`, `priv`. Pass: `rcN` and registry flat across cycles, `ctMissed` 0, no missing texture in live and CAS shots at start, after a sweep, after reload, after travel. Then the old-save run on Dalexlol's setup (repro `compositor-oldsave`; contributor harness rule).

**Probes for what static work cannot prove:**

| # | Probe | Hook | Answers |
|---|---|---|---|
| P1 | count per `CacheOutfit` name prefix | `0x5A5D40` | which event drives the growth (names only: seen vs unseen does not show natively) |
| P2 | count per caller of `0x5EECD0` | returns `0x5E299E`, `0x5E8AB1`, `0x5EA943`, `0x99C299` | builder vs fallback paths |
| P3 | sizes each mem line | read `+0x110`, `+0xE0`, count-0 share | option 3 |
| P4 | Mode 4 at each save | `0x5ED3E0` | live vs registry; doomed count; later finds of doomed keys, `ctDoomedAsync` for those through `0x729E80` (a key found there is one Mode 3 must not lose); MISSED uses the same 2^18 erased-key table as Mode 1 |
| P5 | order and timing | `0x5EE090` vs `0x5EFD60` | script marks before the native save; what `[0x11ECBC4]+0x45` means; which thread saves |
| P6 | fallback branch count | `0x5EA84A` | how often a miss rebuilds, or fails |
| P7 | bytes per entry, **decides §2.4** | AllocSites on one tag: every cycle marks `cc-cycle`, so its `cyc` rows give growth per cycle | replaces both bounds of §2.4 |
| P8 | `+0xE0` outfit map size, CAS geometry and blend bytes | P3's line; call sites `0x5C7901`, `0x5A2FFA` | option 3; lead 4 of §9 (`ctGeoKB`, `ctBlendKB`: bytes allocated, not live) |

**Effort** (agent build ~10-60 min, live job ~8-15 min):

| Step | Agent | Game |
|---|---|---|
| P1-P6 in one MemDiag round | done | 1 job, ~24 min; old save ~65 min (Dalexlol) |
| Mode 3 + selftest (fake registry, fake live set, keep list) | done | `compositor-churn` ~24 min |
| Texture checks | | `castrim-checks` ~64 min |
| Option 2, if P1 supports it | ~45 min | 1 job ~15 min |

No clean night needed: this is correctness plus marks, not timing.

## 8. Existing fixes checked

| Source | Result |
|---|---|
| NRaas (`game-copy/nraas-src`, read only) | no registry or compositor handling; `CASParts.cs:373`, `CustomPedestal.cs:2046` only call `Uncache`, and mods add `CacheOutfit` callers, so they feed the growth |
| ErrorTrap, Overwatch (docs/184, docs/145) | clean managed data, not this |
| Apex Radiance (`game-copy/apex-src`) | no hit on these addresses; MemoryGuard's shrink cannot touch 2-reference entries (§2.1) |
| S3SS, S3PP | no known feature (docs/191 prior art) |
| Web search, 2026-11-07 | no public report or fix naming this; only general leak threads ([EA forum: 64-bit memory leak](https://forums.ea.com/discussions/the-sims-3-technical-issues-en/re-current-issue-64-bit-ts3-64-bit-memory-leak/11146003)) [community] |
| Our tree | `[CasTrim]` (imported from PR #285, off, ledger §942), `[MemDiag] RcCensus`, `RcRefCallers`; PR #260 (closed, sgr and DXVK memory options) has no holder fix; open PR #300 `[CasFrameBudget]` is stutter, not memory |

## 9. Still unproven

1. Which event drives 46 inserts/s (P1). §3 is a list, not a ranking.
2. That script's AddRef pass runs before the native save in every save type: autosave, travel, quit (P5). A save that does not pass `SaveGameSynchronous` has no keep list and refuses, so it sweeps nothing.
3. Bytes per entry: §2.4 holds two bounds from one run pair (P7 decides). Whether al247 had the tables installed.
4. Why `eaRes` stops at ~873 MB in one run but grows in others; what the remaining +96 MB is. Lead: `0x5C7906` CAS geometry and `0x5A2FFF` blend data (owners in §1.6, holders not traced); the outfit map (P3).
5. On-screen effect of handle -1 and of the uniform-masked no-rebuild branch. Marked no crash [inferred], and both are kept away by the keep rules.
6. Whether outfits outside the map name compositors (the 5 would-miss of al250: an outfit reloaded by key, no database has the key). Their key source is not traced; Mode 4 shows whether doomed keys are found later. The fallback (§5.3) should cover them.
7. Part record `+0x64` (Parse `0x5EA6B7`, set after a lookup-table hit `0x5DF2E0`): how many outfits rebuild compositors at every parse (P2). If many, that is a second, separate growth path.
8. The Lot manager's release side was not read in full.
9. Rodney's own saves: all growth numbers are from one long-history save with a mod stack.

## 11. Live Mode 3 run `bp-castrim-live-mode3`: the travel_home FAIL (2026-11-08, ledger §455; static, no game run)

Job: queue `castrim-live.json`, ts3native 0.36.190 (pair g2pair1108), `[CasTrim] Mode=3 Install=1 Probe=1 MinAgeSec=60`, repro `compositor-churn`, 00:49-01:09 UTC. FAIL line: `repro step travel_home failed: no reply to 'travel_home' (game paused, loading or blocked?)`. Files: `probe-out/queue/castrim-live-2026-11-08-bp-castrim-live-mode3{.log,-0049-ts3native.log}`, `chainc-castrim-live.out`. §10 (static map, emulation) is on branch `docs/castrim-emu-verify`.

### 11.1 The stall: the repro, not CasTrim [verified (log, repro source); travel outcome inferred]

- The repro sent `travel_home` 5 s after `travel_vacation` was queued, with no wait for arrival: `"cmd": "travel_vacation" ... "waitS": 5` then `"cmd": "travel_home", "waitS": 5` (repros.json, `compositor-churn`). Timeline: `01:09:33 ... travel_vacation: {"ok":true,... "queued":true}`, `01:09:46 ... travel_home: {"ok":false,"error":"no reply ..."}`.
- In those 13 s the game was in the travel save: `save: result 0 travel 1/1 total 10914 ms: ... write 10902`, then the France load starts. A travel load blocks the agent. The same trip without CasTrim (`wake-park-ab-2026-11-01-bp-wake-park-travel`) took `until met after 56 s` to arrive, because that repro waits on `travel_state` (`on_vacation` true, `in_travel` false) before `travel_home`.
- The travel save is as long as without CasTrim: here world save 9125 ms + post-save listeners 1781 ms = 10914 ms; wake-park travel save 8516 + 1453 = 9991 ms.
- CasTrim did nothing on that save: `cas trim: save #4: no sweep: no keep list for this save`. `CtSweepSave` returns before the walk in Mode 3 (`castrim.cpp:473-477`), so nothing was doomed or pending. The save #3 sweep had finished long before: `cas trim: sweep done: swept 3053 in total` (log line 6964 of 8770); the last diag line `save-end reg=36135 ... doomed=3053 swept=3053`.
- No lock or wait in `castrim.cpp` (no `EnterCriticalSection`, SRW, `Sleep` or `WaitFor*`). EA's own clear-all at a world unload (`0x5EEA90` -> `0x5EE640`) passes no hook of ours; we call `0x5EE640` only from our sweep (`CT_ERASE`, `castrim.cpp:46`), and no sweep was pending. So CasTrim cannot have erased something the loading world used.
- What is not proven: that the France load and the trip home finish under Mode 3. The harness ended the game at the FAIL, 13 s in. The rerun with the fixed repro shows it.

Fix (repro): `compositor-churn` `travel_vacation` and `travel_home` now wait on `travel_state` (every 10 s, max 900 s), copied from the wake-park travel repro.

### 11.2 `ctRefused` 1: the travel save, a correct refusal [verified (log, source)]

The one refusal is save #4, the travel save (`LoadSaveManager.SaveTravel` from `GameStates`), which does not pass the ts3fixes `SaveGameSynchronous` hook, so no keep list was sent: `CtRefuse` answers `"no keep list for this save"` (`castrim.cpp:420`) and Mode 3 sweeps nothing. That is the designed safe path (§10.6 b). The pass rule `ctRefused 0` was wrong for a repro with a trip; a round trip has 2 travel saves.

Rule fix (queue `castrim-live.json`): the `ctRefused <= 0` early gate is gone; `logForbid` `^cas trim: save #\d+: no sweep: (?!no keep list for this save)` fails any other reason (thread, CAS open, incomplete keep list, P5 order, empty live set); `logRequire` 3 `mode 3: registry` lines makes each of the 3 scratch saves sweep, so a missing keep list on a normal save still fails. Also fixed: the duplicate `logRequire` key `^cas trim: save #d+ mode 3: registry ` (no backslash) could never match and would have failed every run.

### 11.3 The exceptions: startup, first chance, in every run [verified (log)]

`C0000005` at `spfileq.dll+0x672C`, `+0x666C`, `setupapi.dll+0x2A3A3` (thread 28220, the thread that loaded ts3native) and two `E06D7363` (28220, 29324) are at log lines 281-289, before CasTrim's first insert (`cas trim: first insert on thread 13164`, line 482). The 40 newest ts3native logs in probe-out/queue all have the setupapi AV once and `E06D7363` twice at the same place (line 261-286), CasTrim off in all but this one. The log header says `first 16 first-chance exceptions`. Not a crash, not ours to triage.

### 11.4 Registry growth between saves: live working set, not a leak [verified (log); held-not-live cause inferred]

Per save (Mode 3 save lines): registry = refs-kept + young + Async + Named + doomed, exactly.

| Save | Registry | Live | Doomed this save | Kept refs | Young | Async | Named | Registry - doomed - live |
|---|---|---|---|---|---|---|---|---|
| #1 | 37527 | 34939 | 1981 | 34993 | 541 | 0 | 12 | 607 |
| #2 | 35725 | 34979 | 531 | 35063 | 49 | 18 | 64 | 215 |
| #3 | 36192 | 35231 | 541 | 35379 | 163 | 18 | 91 | 420 |

- The +467 from #2 to #3 is live +252 (the cycle's new NPC and aged townie; outfits 1768 -> 1775), young +114 (built in the last 60 s, judged at the next save), Named +27, and held-not-live (kept refs - live) 84 -> 148.
- Without the sweep the same churn on the same save grows steadily: `compositor-probe-2026-11-07-bp-compositor-probe` save-starts 36721, 37333, 38585 (+612, +1252); after the reload Mode 3 loaded at 35476 against 34928 at the first load.
- Held-not-live (54, 84, 148) is the one part that grows; it is entries above 2 references that EA's live set does not list (outfits in the map not worn, builds in flight; leak path e of §10.6) [inferred]. 3 points are too few to call it growth.

Pass rule: not "flat". Doomed swept at each save, and (registry - doomed) - live bounded by young + Named + Async plus a few hundred held-not-live, with live growing only with the outfits the repro adds. Written into the queue note and the repro.

### 11.5 Verdict

Not ours: the stall is a repro wait missing on the trip, and `ctRefused` 1 is the designed travel-save refusal. Fixes are in the repro and the queue checks, no code change. Rerun (needs Rodney's OK): `bp-castrim-live-mode3` as is, same build; pass also needs the trip round trip to finish and the `*-compositor-*-travel.jpg` shots to match. CasTrim Mode 3 can ship ON this week only after that rerun passes: the sweep, the reload (`load_save` met after 50 s, reload shots taken) and the 3 sweeps are seen, the trip round trip under Mode 3 is not.

## 12. Rerun `bp-castrim-live-mode3` 01:26 UTC: travel-save crash at `0x5E772C` (2026-11-08, ledger §456; static, no game run)

Job: queue `castrim-live.json`, ts3native 0.36.190 (g2pair1108), `[CasTrim] Mode=3 Install=1 Probe=1 MinAgeSec=60`, repro `compositor-churn` with the travel waits of §11.1. Saves #1-#3 swept (doomed totals 894, 1752, 2048; household 0), `load_save` (world load #2), CAS, then `travel_vacation` 01:46:35. The game died at 01:46:56 in the travel save: `ACCESS_VIOLATION reading 0x00000001` at `0x005E772C`, thread 50516 (the sim thread, the insert thread), EA report `xcpt <machine> 26-11-08 09.46.51.txt`, no minidump.

### 12.1 The fault: EA's outfit inventory in the SimService save [verified: disassembly TS3W 1.67, EA report, ts3native log]

- `0x5E772C` is in `0x5E7690` (thiscall `(outfit, set, children)`, `ret 8`), the outfit inventory that the save's live set and our keep list both use (§10). It walks the outfit's part records (`[outfit+0x98]` to `[outfit+0x9C]`, 0x68 bytes each). Each record has two arrays of 5 compositor pointers, `[rec+0x14]` and then `[rec+0x38]`. For each non-null pointer it finds or inserts the compositor's key (`comp+8`, `0x5E67E0`) in the set, AddRefs it through vtable slot 0, releases the cell's old value (slot 1) and, with children = 1, closes over its children (`0x5E6FC0`).
- The fault is in the second array (`[rec+0x38]`), slot 1 (`ebx` = 4): `esi` = `0x40A00000`, `edx` = `[esi]` = 1, and `mov eax,[edx]` (the vtable read) faults. The set cell was new (`edi` = 0). So that slot holds something that is not a compositor (a part compositor's first dword is vtable `0xFE68D8` or `0xFE68F4`). The frame comes from the EA report's stack data (esp `0x44B5759C`): record `0x334789A0`, outfit `0x35F8D460`, record end `0x33478CE0` (8 records), set `0x44B575D4`, children 1.
- The caller is `0x5EFE11` in the SimService save `0x5EFD60`, in its loop over the outfit map (`svc+0xD8` / `+0xDC`). This is the inventory pass that builds the live set, before the writer `0x5ED3E0`. The save itself was called from `0x5F0378` (the post-save listener, gated on `[0x11ECBC4]+0x45`; log `save: > write: post-save listeners (+9484 ms)` right before the dump) through our `CtSaveHook`. Stack ref `ts3native+0x30DEF` is the return from `call [g_ctSave]` in the g2pair1108 asi, two instructions after its read of `[0x11ECBC4]+0x45`.
- The second exception (`eip 0`, stack refs `004D7001`, `004D7140`, `0058D945`) happened on the same thread after the first: `call edx` at `0x4D6FFF` with `edx` 0, under frames of EA's crash-report path (`0x58D9xx`, the area of EA's own `0x58D9B6` crash). It is part of the crash handling, not a second cause and not the travel path [inferred].

### 12.2 CasTrim's part: no erase reaches this pointer [verified: log, source; slot ownership inferred]

- The only CasTrim code on the stack is `CtSaveHook` (`castrim.cpp:561-577`): counters, one read of `[0x11ECBC4]+0x45`, then EA's save. `CtWriteHook`, `CtSweepSave` and `CtKeepEa` (our own `0x5E7690` calls) run after the writer returns, so after this pass. The crash came first: there is no `cas trim: save #4` line (in the 01:09 run the same travel save logged `no sweep: no keep list` at that point).
- No erase in the window: the last erase is the save #3 sweep (`sweep done: swept 2048 in total`, log line 7588). World load #2 is at line 8469 and the travel save at line 9238, and no sweep runs between them (`saves 3 ... swept 2048` at line 8785). A sweep only runs after a save that is not refused, and the travel save is refused (§11.2). So CasTrim erased nothing in the ~4 min from the reload to the crash.
- Even if entries from before the reload had survived (the reload resetting the service is inferred: the registry was 35628 at the load, about save #3's written set of 35391 plus new inserts), no erased compositor can be in a slot:
  - Everything `0x5E7690` reaches from the outfit map at save #3 is in EA's live set, which rule 3 keeps.
  - The erase checks again that it is the same object, still at 2 references and not found since it was doomed (`castrim.cpp:515`).
  - An erased compositor is unregistered from the cache (`0x5EE640`, flag 0), so no later find can put it into a slot.
  - That a slot holds its own reference is inferred. It fits `0x5E7690`'s AddRef / Release and rule 1's counts (§10.4).
- Q2 cases: the travel save sweeps nothing (refused). The keep list, household temp outfits and Named children only decide what is kept, and they ran at saves #1-#3, long before. Async is a keeping bit too. Nothing for a travelling Sim's outfit is erased on the trip.
- The other CasTrim hooks are passive in this window. The insert, find, mark, CacheOutfit-name and fallback-flag hooks only count. The 3 probe call sites at `0x5C7901` / `0x5A2FFA` match EA's `0x4E69D0` (thiscall, 2 arguments, `ret 8`, the same as our `__fastcall (heap, edx, size, flag)`). The rebuild site was never hit (`fbRebuilt=0`). P3's `CtMemLine` reads the outfit map from the memory worker, read-only under `__try`.

### 12.3 Seen before? [verified: search]

- `5E772C`, `5E7690` as a fault, and `5EFE16`: only this run's files (`docs/data`, all `probe-out/queue` logs; one other hit was an object address, `35EFE160`). The 10 EA reports in Documents (11-06 to 11-08) contain 7 at `0x58D9B6`, 1 at null and 1 address-space report, and none at `0x5E772C`.
- 01:09 run (`...-0049-ts3native.log`): the travel save #4 passed this same pass (`save: result 0 travel 1/1`, `castrim #17 save-end` logged), with no exception after the startup first-chance ones (lines 281-289). The §11 verdict on it stands: no fault; the harness ended the game at the FAIL. It had the same build, the same sequence (3 sweeps, `load_save`, CAS, trip) and swept more (3053). So the fault does not happen on every run.
- No Mode 0 run of this repro (with `load_save` + CAS + trip) on g2pair1108 exists to compare.

### 12.4 Verdict

The CasTrim Mode 3 sweep is ruled out as the writer of this pointer: it made no erase in the window, and the set it erases excludes everything this pass walks [verified log + source]. Where the bad slot came from is not known. It is either a freed compositor whose memory was reused (`0x40A00000` is the start of a 64 KB page, where a mimalloc small page puts its first block [inferred]) or a part record overwritten by something else. It is neither proven EA's nor proven ours. Under the crash rule it stays "our build" until the logging run below names the slot's history (g2pair1108 also runs other code during the trip: P5c deferred object releases, the unseen movers). No code fix yet: a fix without a cause would only be a guess.

### 12.5 Logging around the site, and the rerun

Built 2026-11-08 as ts3native 0.36.194, pair castrimdiag1108 (selftest only, not run; queue `castrim-live.json`, held). As built: the outfit's records hold *pointers* to the two 5-slot arrays (`mov eax,[rec+0x14]; mov esi,[ebx+eax]`) [verified disassembly], so the detector reads the array pointer first; EA's `esi` at the call is the outfit map node (`0x5EFE0E`), kept by a naked stub for the key; the off arm logs too (the save hook calls the per-save line in every mode); the erase ring also records Mode 1's erases; the EA report decoder is in `crash-ledger.ts triage`; the fault decoder also covers the first array's read at `0x5E76DA`.

Add to `castrim.cpp` (crash-triage build window):

1. `CtInstallProbes` (`castrim.cpp:680`): a 4th call site `{0x005EFE11, 0x005E7690, CtInvHook}`. `CtInvHook` is `__fastcall (outfit, edx, set, children)` (thiscall, `ret 8`, as `CtCloseFn` at `castrim.cpp:401`). Before it calls EA, it walks the outfit's records and both 5-slot arrays under `__try`. For a non-null slot whose first dword is neither part vtable it logs (first 20 per session) a `cas trim: inventory bad slot` line with:
   - the outfit node key, record index / count, array (`+0x14` / `+0x38`), slot, value and its first 4 dwords;
   - the 0x68 record bytes;
   - `CtSlot(value)` (seen / used / cand ages);
   - whether our sweep erased that address (item 2);
   - the save number, the travel flag and the ticks since the last world load.

   Then it calls EA as before. It is a detector, not a guard. It also checks the outfit node itself and the record range (end >= begin, a multiple of 0x68, under 64 records). Cost: ~1780 outfits x 8 records x 10 slots, about 140k reads a save, under 5 ms.
2. `CtSweepStep` (`castrim.cpp:519`): a 4096-entry ring of erased object addresses with tick and key hash, so item 1 and the crash dump can say "erased by CasTrim N s ago" or "never erased by CasTrim".
3. Crash forensics (`crashfx.cpp`): on a fault at `0x5E772C`, decode `[esp+0x10]` (record) and `[esp+0x14]` (outfit), the faulting `esi`, and the ring lookup of item 2.

The earlier detectors stay on (P5c rings, draw and scene guards, heap check, allocsites).

Rerun, after the logging build:
- `bp-castrim-live-mode3` (as queued) twice. The fault came in 1 of 2 runs, so it is not a deterministic repro and needs 2 clean runs.
- One off arm of the same repro with `Mode=0 Install=1 Probe=1` and the detector.

Pass: both Mode 3 runs finish the trip round trip (I-455-4) with no `inventory bad slot` line and no crash. If the off arm shows a bad slot or the crash, the cause is not the sweep. If a bad slot names an address CasTrim erased, the sweep is the cause and the fix goes into rule 1 / the erase re-check.

Ship ON this week: not yet. It needs the 2 clean Mode 3 runs above, including the trip, which also closes I-455-4.

## 13. `bp-castrim-release-smoke` 05:54 UTC: AV at `0xD4B156` in an outfit's destructor after the travel load (2026-11-08, ledger §458; static, no game run)

Job: queue `castrim-live.json`, pair castrimrel1108 (ts3native 0.36.196, ts3fixes bookhand1108), release ini `[CasTrim] Mode=3 Detect=1 Probe=0`, repro `compositor-churn`. Saves #1-#3 swept (doomed totals 1163, 1660, 2353), `load_save` 06:10:32 (world load #2), CAS, `travel_vacation` 06:13:24. The travel save #4 passed (`save: result 0 travel 1/1`, `no sweep: no keep list for this save`, inventory check `bad 0` over 1774 outfits), world load #3 (France) logged, then at 06:14:19 `ACCESS_VIOLATION reading 0x00000004` at `0x00D4B156`, thread 12968. EA report `xcpt <machine> 26-11-08 14.14.19.txt`, no minidump. Log: `probe-out/queue/castrim-live-2026-11-08-bp-castrim-release-smoke-0554-ts3native.log` (forensics from line 7883).

### 13.1 The fault: EA's release of a finished async request in a SimOutfit destructor [verified: disassembly TS3W 1.67, EA report stack data, ts3native log]

- `0xD4B150` is EA's async-request callback (`mov ecx,[esp+4]; mov eax,[ecx]; mov edx,[eax+4]; jmp edx`): a virtual Release (vtable slot 1) on the request's result. The argument is `0x40A00000` (`ebx` = `ecx` = `[esp+4]`), its first dword (the vtable) is 0, so `[0+4]` faults. Nothing EA checks is null; the result pointer points at memory that holds no object.
- Thread 12968 is the main thread (the decompress table names it `main`; it is the thread that loaded ts3native). CasTrim inserts and sweeps on thread 39896.
- The chain, from the EA stack and the return addresses in the stack data (esp `0x001AFCBC`):

| Return | Function | What it does there |
|---|---|---|
| `0x59ED59` | `ServiceList_Update` | main-thread service slot +0x1C |
| `0x5F0E86` | `SimService_Update 0x5F0E50` | under the SimService lock (`+0x80`) it calls `0x5ED140` on the list at `+0x70` |
| `0x5ED162` | `0x5ED140` | for each node: Release `[node+8]` (vtable slot 1), free the node: SimService's deferred releases |
| `0x731DA3` | refcounted Release (`0x731D82` path) | count to 0: vtable +8 with 1 (deleting destructor) |
| `0x5E7A08` | `0x5E7A00` | SimOutfit deleting destructor |
| `0x5E75CC` | `0x5E75A0` | SimOutfit destructor (vtable `0xFE4A10`), calls the clear `0x5E6E20` |
| `0x5E6E65` | `0x5E6E20` | releases the job at `+0xB8`, then `0x5AF640` over the array `[outfit+0xBC, +0xC0)`, then the 0x68-byte part records (`0x5E5AF0`) |
| `0x5AF66D` | `0x5AF640` | per non-null entry: the async loader `[0x11D3C84]` vtable +0x3C (entry address, callback `0xD4B150`) |
| `0x728DD4` | the loader's +0x3C | calls `0x728760` |
| `0x7287EC` | `0x728760` | entry tagged (bit 0): a pending request, unlinked and recycled; untagged: a finished result, `callback(result)` |
| fault | `0xD4B156` | `[result]` = 0 |

- The frame values: outfit `0x7B781A80`, array `0x409E07A0` to `0x409E095C` (111 entries), entry 63 at `0x409E089C` = `0x40A00000`. Entries 0-62 went through the same loop without a fault.
- What the array holds: the outfit's Load step `0x5E55B0` passes `&[outfit+0xBC][i]` as the result cell of `0x5E53E0`, an async request by key (`[0x11D3C84]` +0x34, callback `0xD4B150`) [verified disassembly]. The al249 count run traced compositor finds from this request (castrim.cpp header, docs/191), so the cells hold part compositors once finished [inferred from that trace; the request's type argument `0xEB54B802` is not the TXTC type `0x033A1435`, so the result type is not proven].
- The second exception (`eip 0`, refs `004D7001 004D7140 0058D945`) is EA's crash-report path, as in §12.1 [inferred, same pattern].

### 13.2 Our code on the path, and CasTrim's erases [verified: log, source, grep]

- No hook of ours is on this chain. `native/` names none of `0x5AF640`, `0x5E6E20`, `0x5E75A0`, `0x5E7A00`, `0x728760`, `0x5ED140`, `0x5F0E50`, `0xD4B150`, `0x5E53E0` except in comments; docs/68 lists none of them. CasTrim's code sites are the insert `0x5EECD0`, the cache find `0x7324E0`, the save hook, the inventory call site `0x5EFE11` and the probe call sites (Probe=0 here).
- No CasTrim erase in the window. Mode 3 erases only in the sweep after a save that is not refused. The last sweep ended after save #3 (`sweep done: swept 2353 in total`, line 6748, after the 06:08:31 save [time inferred from the job log]). Then `load_save` reloaded the world (06:10:32-06:11:20), save #4 refused (`no sweep: no keep list for this save`, line 7822), and the Mode 1 step is off (`walked 0, marked old 0, erased 0`). The fault came ~5.5 min after the last erase, across a full reload and a world switch.
- Is `0x40A00000` an erased compositor? Not answered for this site: the erase ring lookup (§12.5 item 2) runs only for faults at `0x5E76DA` / `0x5E772C` and for the inventory detector, and the detector reads the record arrays `[rec+0x14]` / `[rec+0x38]`, not `[outfit+0xBC]`. Against it: an erased compositor would leave its own heap address in the cell; the same value `0x40A00000` in two runs with different builds and heap layouts does not fit that [inferred].
- The other CasTrim parts ran no code here: no save, no sweep and no probe in the travel load. P5 state at the fault: route job idle, P4 not armed, heap check 0 bad frees; the P5c released ring's newest release is 57 s old [verified log].

### 13.3 Seen before? [verified: search]

- `D4B156`: only this run's files (`docs/data`, all `probe-out/queue` logs, the 10 EA reports in Documents).
- The value `0x40A00000` (float 5.0) as an object pointer in a CAS slot, three times:

| When | Build | Site | Slot | `[0x40A00000]` | CasTrim |
|---|---|---|---|---|---|
| 2026-10-15 (docs/67 §3) | 365b9c27d8ba | `0x5CEB9A`, ModelBuilder part record ref list, released from `SimService_Update` on main | record `+0x44` inline vector | 8 | did not exist (`castrim.cpp` added 2026-11-06) |
| 2026-11-08 01:46 (§12) | 0.36.190 | `0x5E772C`, outfit inventory in the travel save, sim thread | `[rec+0x38]` slot 1 | 1 | Mode 3, no erase in the window |
| 2026-11-08 06:14 (this) | 0.36.196 | `0xD4B156`, SimOutfit destructor from SimService's deferred releases, main | `[outfit+0xBC]` entry 63 | 0 | Mode 3, no erase in the window |

- The other `40A00000` hits in `probe-out/queue` are float fields (footprint `+0x10` in the 10-24 watchpoint logs, two jsonl rows), not pointers.
- So: a CAS object's pointer cell overwritten with float 5.0 is an older crash family that predates CasTrim. Three different cells and owners, the same value, and the address mapped each time with different contents. The 10-15 crash rules out CasTrim as its only cause [verified]. That CasTrim makes it more frequent is not ruled out [inferred: only 2 off-arm runs exist].

### 13.4 Facts for the ON decision (no decision here)

- Mode 3 runs of this repro today, all reaching the trip: 00:49 (no crash; travel_home stalled, §11), 01:26 (crash §12), 03:22 and 03:48 (clean, castrimdiag1108), 05:54 (crash, this). 2 crashes in 5 Mode 3 runs, both in the travel, both with `0x40A00000` in an outfit's cell. Off arm (`Mode=0`): 2 runs (04:14, 04:55), 0 crashes. If the crashes were independent of the mode, both falling in Mode 3 runs would have chance 10/21 = 0.48: the sample cannot separate the arms [verified count from `castrim-live-2026-11-08.jsonl`].
- Neither crash has a CasTrim erase between the reload and the fault, and no CasTrim code is on either stack except `CtSaveHook` passing through in §12 [verified].
- The inventory detector at the travel save found no bad record slot in 1774 outfits (§12.5 coverage only) [verified].
- Not known: who writes 5.0 into the cells, and whether a Mode 3 sweep before the reload frees memory that a float writer reuses later.

### 13.5 Logging to name it (all earlier detectors stay)

Crash-triage build window (`castrim.cpp`, `crashfx.cpp`):

1. `CtInvCheck` also walks `[outfit+0xBC, +0xC0)`: untagged non-null entries must start with a part compositor vtable (or, until the result type is proven, log the first 20 vtables seen per save, grouped). Bad entries go through `CtInvBad` (erase ring, CasTrim slot, first dwords), with the entry index and 8 neighbours.
2. A value watch: any cell in the record arrays or `+0xBC` equal to `0x40A00000`, or any value whose first dword is below `0x10000`, logs `cas trim: float-5 cell` with the outfit key and the record bytes, in every mode (also Mode 0), so the off arm can show it.
3. A call-site detour at `0x5E6E60` (the clear's `call 0x5AF640`) and `0x5E7633` (the destructor's): the same check on the destructor path, main thread, before EA runs; a detector only, EA runs unchanged.
4. `crashfx.cpp`: on a fault at `0xD4B156` or `0x5AF67E` (the same release inlined in `0x5AF640`) decode the `0x5AF640` frame (slot `[esp+0x2C]`, begin, end, outfit from the saved registers), the value's first 4 dwords, `CtGoneText` / `CtSlotText` on it, and ±8 cells.
5. Cell history (round 2, only if 1-4 catch a bad cell before the fault): a DR write watch on the bad cell's neighbours at the next save, with the writer's stack (the watchpoint code from the 10-24 P5 drift runs).

Kept: inventory detector and erase ring (§12.5), P5c rings, draw and scene guards, heap check, allocsites.

### 13.6 Rerun

Not deterministic (2 of 5 Mode 3 runs, two different sites). After the logging build:
- `bp-castrim-release-smoke` twice (release ini, Mode 3).
- `bp-castrim-live-off` twice more (4 off arms in total).

Pass: both Mode 3 runs finish the trip round trip with no crash and no bad-cell or float-5 line. A float-5 cell in an off arm means CasTrim is not the cause. A bad cell that `CtGoneText` names as erased by CasTrim makes the sweep the cause. A fix, once there is a cause, needs 2 clean runs of the same job.

### 13.7 Built: castrimdiag1108b, items 1-4 and CellGuard (2026-11-08)

Pair castrimdiag1108b: ts3native.asi.castrimdiag1108b bd9923e30a34 = 0.36.198 from a489d620 (master = castrimrel1108 0.36.196 + bookhand + 13 small fixes ON, plus this) and ts3fixes = master (`bookhand1108d` file, mods/ts3fixes unchanged since 99f5ae36). Selftest only, not run in game. Item 5 not built (round 2).

As built [verified disassembly, TS3W 1.67]:
- `0x5AF640` is thiscall `(&outfit[+0xBC], begin, end)`, `ret 8`, called from `0x5E6E60` (in the clear `0x5E6E20`, return `0x5E6E65`) and `0x5E7633` (in the destructor `0x5E75A0`, return `0x5E7638`, after the clear, so usually on an empty range). For each non-null cell it calls the loader's `+0x3C` (`0x728DB0` -> `0x728760`: tagged = pending, unlinked; untagged = `0xD4B150(result)`), then releases an untagged cell inline (`0x5AF67A`). The 11-08 crash went destructor `0x5E75CC` -> clear -> `0x5AF640` from `0x5E6E65` (the EA report's frame, decoded below).
- The clear frees the part records after `0x5AF640` (`0x5E5AF0` over `[outfit+0x98, +0x9C)`, then end = begin). `0x5E5AF0` releases `[rec+0x14, +0x18)` and `[rec+0x38, +0x3C)` as begin / end ranges; the save's `0x5E7690` reads 5 fixed slots of the same arrays.
- Frames for the decoders: `0xD4B156` from `0x5AF640`: `[esp]` `0x7287EC`, `+4` the value, `+0x14` `0x728DD4`, `+0x18` / `+0x2C` the cell, `+0x28` `0x5AF66D`, `+0x34` saved edi (= outfit + 0xBC), `+0x38` saved esi (= outfit), `+0x3C` return, `+0x40` begin, `+0x44` end. `0x5AF67E`: esi = cell, ecx = value, `[esp+4]` outfit, `+8` return, `+0xC` begin, `+0x10` end. The 11-08 report decodes to cell `0x409E089C`, entry 63 of 111, outfit `0x7B781A80`, return `0x5E6E65` (`crash-ledger.ts` self-check and the real report).

What it does (`castrim.cpp`):
1. Item 1: the save inventory (`CtInvHook`, `0x5EFE11`) also walks `[outfit+0xBC, +0xC0)`: an untagged non-null cell must be readable with a first dword inside TS3W `.rdata` (the result type is not proven; the vtables seen are counted, 24 per save). Bad: `cas trim: outfit cell bad` (first 20; erase-ring and CasTrim slot lookup, first 4 dwords, cells -8..+8). Per save: `cas trim: inventory cells save #N: cells, bad, float-5 ...; result vtables: ...`.
2. Item 2: `cas trim: float-5 cell` (first 20) for a record slot or cell equal to `0x40A00000` or with a first dword below `0x10000`, on every path, in every mode where the sites are in (Detect=1; off arm jobs set it).
3. Item 3: `CtRelHook` on both release call sites: with Detect the part records (as `0x5E5AF0` reads them: begin / end; a record slot of another type with a TS3W vtable is counted, not bad) and always the cells, before EA. Per save: `cas trim: outfit release check ... calls, cells, bad, ... max us a call; result vtables`.
4. Item 4: `crash forensics: cas trim outfit release fault` (first 2) at `0xD4B156` / `0x5AF67E`: frame, value + 4 dwords, ring and slot lookups, cells -8..+8, the outfit's last inventory-check save, release counters, mode, save / travel phase. `crash-ledger.ts triage` decodes the same from an EA report.
5. CellGuard (Rodney 2026-11-08, scope change): `[CasTrim] CellGuard=1` (code default 1, repo and release ini 1) installs the two release sites in every Mode, also Mode 0 with nothing else installed. A cell that is `0x40A00000`, unreadable, first dword below `0x10000`, or a first dword outside TS3W `.rdata` is set to 0 before EA runs: EA neither asks the loader nor releases it, that entry leaks, the other cells go as before. `cas trim: cell guard skipped` (first 40): outfit, entry / count, value, cells -8..+8 (before the write), ring lookup of the value and the outfit, the save # of the last inventory check that saw the outfit, 6 exe return addresses up the stack, CasTrim mode, in-a-save, save # / travel flags / world load. Counters v176 `ctCellSeen`, `ctCellSkip`. This is a guard for a cause still unknown (who writes 5.0), not its fix.

Earlier detectors kept: §12.5 inventory slot check, erase ring, `0x5E772C` / `0x5E76DA` decoder, P5c rings, draw / scene guards, heap check, allocsites.

Probe safety (native selftest `TestCasTrimCells`): a dummy call site laid out as `0x5E6E5C` (push end, push begin, `mov ecx`, `call`), patched with `PatchCalls` to `CtRelHook`, run for real: guard off = EA's fake sees all 7 cells unchanged (5 checked, 3 bad, 2 float-5); guard on = the zero-vtable, `0x40A00000` and non-.rdata cells are 0, the good and tagged ones unchanged, 3 skips; a clean outfit is released exactly as before (0 skips); bad ranges and no cells log and still call EA; the save path counts and does not write; both fault decoders on fake frames, another caller, unreadable memory.

Cost: selftest 2.3 us per 111-cell outfit through `CtRelHook` (warm cache, 20000 calls); in game a save adds about outfits x that (~1780 outfits: ~4 ms plus cache misses) on top of §12.5's 0.7-3.9 ms, and each outfit release a few us on the main thread. The log lines carry the real ms per save and the max us per release call: the queue result must report them.

Rerun queued (not run, `tools/harness/queues/castrim-live.json`, stopQueueOnFail false): `bp-castrim-release-smoke-b1` / `-b2` (release ini: Mode 3, Detect 1, Probe 0, CellGuard 1) and `bp-castrim-live-off-b1` / `-b2` (Mode=0 Install=1 Detect=1). Gates: no crash; install lines (inventory site, release sites, `CellGuard 1`); inventory check and inventory cells lines >= 3; 0 `inventory bad slot`, `outfit cell bad`, `float-5 cell`, `cell guard skipped` and both crash-forensics lines. A guard skip fails the job on purpose: it is the bad cell this round is meant to catch, and its line is the evidence.

Repro: none. Writing 5.0 into a cell needs a native write op that maps a non-household Sim to one of its outfits' `+0xBC` cells; there is no such mapping in the agent today. The selftest covers the guard path.

### 13.8 `bp-castrim-live-off-b2` 08:37 UTC: main-menu hang is EA's package-lock ABBA deadlock, not CasTrim (2026-11-08; static, no game run)

Job: queue `castrim-live`, pair castrimdiag1108b (ts3native 0.36.198, map timestamp `6af01d96` = the dump's module), ini `[CasTrim] Mode=0 Install=1 Detect=1 CellGuard=1`. "Mod Scripts Found" accepted 08:36:13, fail-fast `world load: nothing moved ... for 60 s` at 08:37:45, kill dump `probe-out/dumps/ts3w-22140-20261108T083745.dmp`. Not a world load: managed top `UIManager_GetSaveGameMetadataImpl` <- `MainMenuModel.GetSaveFileDetails` <- `Ts3Agent.Handlers.LoadSave` (the agent's save-list read at the menu); `ts3native.log` has no world load line. The same pair passed 3 other jobs today, off-b1 with the identical ini.

Lock owners from the dump [verified: scratchpad minidump reader, `CRITICAL_SECTION` at the waiter's `edi`, `OwningThread` / `RecursionCount`; stacks by return-address scan, each preceded by a call]:

| Thread | Holds | Waits for | Stack (innermost first) |
|---|---|---|---|
| main 44904 | CS `0x0DD8FDA0` (object `0x0DD8FCE0` + 0xC0, recursion 2) | CS `0x0DD8FD28` (same object + 0x48) via `Lock_Acquire 0x4E16F0` | `EnterCriticalSection` < `0x4E170D` (Lock_Acquire body) < ts3native `LockAcquireHook+0xB7` < `0x735358` < `0x735358` < `0x735633` / `0x7355F2` < `0x73767D` < `0x7377FC` < `0x59ED59` < `0x588E1D` < `0xECAA55` < `0xECBD71` < `0xECBE4C` < `0xF7F201` < `0xF7F827` (WinMain): the resource-system update |
| worker 44468 (MSVCR80 pool thread) | `0x0DD8FD28` and `0x0DC73F48` (both recursion 1) | `0x0DD8FDA0`, direct `EnterCriticalSection` at `0x734725` | `0x734725` < `0x734591` < `0x8BA7D5` < `0x8AA846` < `0x8AE5D4` < `0x8BCB78` < ... < `0x4E2EDE` (EA thread start): the package open path |
| sim 44740 | - | `0x0DC73F48` (owner 44468) via `Lock_Acquire`, `GetPackageSource 0x4AFFC0` (key `0x4AFFD6`, `0x00FB2CD0` pushed) | `LockAcquireHook+0xB7` < `0x4AFFD6` < `0x4AFDB1` < `0x7D8126` < `0x7366DD` < `0xAAB96C` ... (Mono interpreter): blocked behind the cycle |

Main holds +0xC0 and wants +0x48; the worker holds +0x48 and wants +0xC0. A lock-order (ABBA) deadlock between two EA paths on one package-database object, the same as docs/67 §23.

Our code on the stacks [verified: dump code bytes, `out/ts3native.map` of 0.36.198, `wt-ctdiag2` source]:
- `Lock_Acquire 0x4E16F0` starts with `e9` to ts3native `+0x52B30` = `LockAcquireHook` (route-gate detour, `routegate.cpp:430`, installed in every job since 0.36.x). Its return `+0x52BE7` (`+0xB7`) is on the main and sim stacks above `0x4E170D`. For a lock other than `+0x918` from a thread that is not the job owner or a sync plan session (the case at the menu), `LockAcquirePlain` calls `g_lockOrig` with no lock, wait or pump of its own: a pass-through. The waits are inside EA's own `EnterCriticalSection`.
- The worker's wait at `0x734725` is a direct `EnterCriticalSection`, no ts3native frame on its whole stack.
- CasTrim: the release sites are patched (`0x5E6E60` and `0x5E7633` hold `e8` calls to ts3native, `CtRelHook`), but no `CtRelHook`, `CtInvHook` or castrim frame is on any of the 150 stacks. The log has only the install line (§13.7 item 5). Other ts3native frames in the dump: `WorkerMain+0x2F` (route worker, idle kernel wait), `UdMemMain+0xB0` (idle), two EA job threads in a kernel wait with stale `LockAcquireHook` / `SppHook` values; the sim stack's `AsFreeHook+0x4E` value lies inside ntdll's wait frame (stale).

Seen before [verified: same reader on the earlier kill dumps]: the identical cycle, same addresses and same caller chains (main `0x735358` < `0x735633` < `0x7377FC`, worker `0x734725` < `0x8AA846` < `0x8AE5D4` < `0xAD3F6E` < `0x8BCBA4`, sim `0x4AFFD6`), in `ts3w-16664-20261026T182520.dmp` (`unseen-fallback-c` 10-26, 0.36.100/103, docs/67 §23), `ts3w-41676-20261031T041930.dmp` (`mintcall-verify` 10-31, 0.36.127) and `ts3w-4012-20261107T104249.dmp` (`stove-wall-repeat` 11-07, 0.36.168). All four at the menu save-list read after "Mod Scripts Found", across unrelated builds and switch sets (unseen fallback, mint calls, stove fix, now CasTrim Mode 0); CasTrim did not exist on 10-26. `probe-out/queue/*.log`: 8 logs with `world load: nothing moved` out of ~2100 `cycle.ts --save` loads; the 4 others (`chain5`, `chain58`, `clean-night-2` 10-18 on 0.22.2, `snap-p4` 10-19 on 0.32.8) had no hang capture, cause unknown. No vanilla (no ts3native) run on record with this signature, but vanilla runs are few, so vanilla is not shown clean either [verified: grep; vanilla rate unknown].

Verdict: **EA** [verified for the lock cycle; "not caused by our detour" inferred from the pass-through path and four builds]. Not CasTrim, not CellGuard: no CasTrim code runs at the menu and none is on any stack. Rate in our harness ~4 captured in ~2100 loads. The agent's `LoadSave` reads save metadata seconds after the dialog, while the main thread still updates resources; a player opening the Load screen at once may hit the same race.

Action: no fix in our code. Rerun the job (counts as N/A, not a CasTrim result). docs/67's "known rare hang" rule stays: rerun once, a second hang in a row stops the chain. Optional, not built: (1) the hang capture (`game-alive.ts`) names each `Lock_Acquire` / `EnterCriticalSection` waiter's CS owner from `edi` and prints the cycle, so the next one is triaged from the summary line without a dump; (2) `cycle.ts` waits for a quiet resource update before `LoadSave` or retries the load once on this exact signature. An EA-side fix (lock order in `0x734710` vs `0x735358`) needs a static study of that object first; not worth it at this rate unless players report menu freezes. (Superseded by §13.9: the static study is done and a fix is designed, per the "fix EA bugs too" rule.)

### 13.9 Fix design: EA package-lock ABBA deadlock (2026-11-08; static design; built as ts3native 0.36.216, not run: §13.9.9)

Follows §13.8 (and docs/67 §23). Standing rule: EA bugs get our fix too, behind a switch with counters, selftest and repro. Steam 1.67 exe, Ghidra (`tools/ghidra/DecompileTo.java`, `CallerTree.java`) plus capstone on `game-copy/Bin/TS3W.exe`. Names follow docs/67 §16.1: **D** = the DBPF database (vtable `0xFFE078`), **O** = D - 8 (outer object, vtable `0xFFE0D0`). Ledger: docs/51 §465.

#### 13.9.1 The two locks [verified disassembly]

| Lock | Address | Taken by | Kind |
|---|---|---|---|
| **DL** (database lock) | D+0x40 = O+0x48 | `0x72C6C0(1)`: `add ecx,0x40; jmp Lock_Acquire 0x4E16F0` with deadline pointer `0xFFD74C` (holds `0xFFFFFFFF`, wait forever); `0x72C6C0(0)` = `Lock_Release 0x4E17B0` | EA `Lock` in CS mode (byte +0x24 set), recursive, count at +0x20 |
| **FL** (file / open-count CS) | D+0xB8 = O+0xC0 | direct `EnterCriticalSection` | plain CS, recursive |

The dump's pair (§13.8) is exactly these two: object `0x0DD8FCE0` = O, `+0x48` = DL, `+0xC0` = FL.

Every `EnterCriticalSection(FL)` site in the class (scan of `0x733000`-`0x738000`): `0x73471F` (in `0x734710`), `0x7347CE` (`0x7347C0`), `0x735332` (`0x735320`), `0x7355D0` (`0x7355A0`). `0x72C6C0` has 14 call sites, all in `0x73445E`-`0x735416` (this class only).

#### 13.9.2 What each path does [verified unless tagged]

- **`0x734710` = D slot `0x50` (`0xFFE0C8`), "begin access"**: FL; open count `D+0xD8 += 2`; if the result is 2 (file was closed) it reopens the file (`0x72D790(1,6,0)`) and, **under FL**, takes DL (`0x73475B`) to drop the stale index `D+0xB0`, then releases DL (`0x73477D`); leaves FL. Order inside: **FL -> DL** (only when reopening).
- **`0x7347C0` = D slot `0x54`, "end access"**: FL; count -= 2; at 1 it stores the QPC time in `D+0xE0/E4` (= O+0xE8/EC, the idle timer); leaves FL. No DL.
- **`0x734550` = D slot `0x30` (`0xFFE0A8`), the read path the worker is in** (return `0x734591` after `call [vt+0x50]`): DL (`0x734558`), then `0x734710` -> FL. Order **DL -> FL**. `0x7351E0` (DL at `0x7351F4`, then slot `0x50`) and `0x7345D0` (slot `0x34`, docs/67 §16.1) take DL first the same way.
- **`0x7355A0` = O slot `0x18` (`0xFFE0E8`), "close if idle", the main thread**: if `O+0x104 == 1` and `O+0xE0` (= D+0xD8, the open count) `== 1` (file open, no reader): FL; recheck; unless arg `force`, return 0 while QPC - last end < `O+0xF0/F4`; else call `0x735320`. `0x735320`: FL again (recursion 2, as in the dump), recheck count 1, **DL at `0x735353`** (return `0x735358` = the main thread's wait in all four dumps), write the shadow index (`ResourceSystem/DatabaseShadow/*`), close (`0x72C8F0`), count = 0, release DL, then FL. Order **FL -> DL**.
- **Caller of O slot `0x18`**: resource-system update `0x737560`, loop `0x737672`-`0x737682` under the manager lock `M+0x218`: `push 0` (force 0), `call [vt+0x18]` for each database, return value ignored. No direct `call` to `0x7355A0`; `0x735320` has one caller (`0x73562E`); one vtable reference each (`0xFFE0E8`, `0xFFE0C8`, `0xFFE0A8`, `0xFFE0E0`).

So DL -> FL (every read) against FL -> DL (main idle close): the ABBA of §13.8. The window: a worker's first read of a package whose file the main thread is closing because it sat idle for the `O+0xF0` interval. At the menu the save-list read opens many packages right after a quiet spell, which fits "only at the menu, ~0.2% of loads" [inferred].

A second FL -> DL path exists [verified code; never seen in a dump]: `0x734420` (O slot `0x10`, `0xFFE0E0`) calls D slot `0x50` with no DL held, then slot `0x54` (a "probe open"). If it reopens a closed file while a reader holds DL and waits for FL, the same cycle forms without the main thread [inferred: needs a caller of O slot `0x10` on another thread at that moment].

#### 13.9.3 Options

| # | Fix | Effect | Risk | Verdict |
|---|---|---|---|---|
| A | **Try-DL before FL in the idle close** (hook O slot `0x18`): when the unlocked prechecks say a close may run, take DL with EA's own `Lock_Acquire` and an expired deadline (one `TryEnterCriticalSection`, -2 if busy); busy = skip this pass and return 0 (the same value as "not idle long enough"); got it = call the original, then `Lock_Release`. Order becomes DL -> FL -> DL (recursive) | the main thread can no longer hold FL while waiting for DL, and never blocks on DL at all, so no new main-thread stall | a busy package closes one update later (the loop runs every update; the idle timer keeps counting) | **pick (Mode 1)** |
| B | Blocking DL before FL in the idle close | consistent order DL -> FL | the main thread may wait behind a long worker read where it used to wait anyway, but now also when the close was going to be throttled | only for `force != 0` |
| C | Pre-take DL in `0x734710` (D slot `0x50`) when the calling thread does not own it | closes the `0x734420` variant: every FL entry that can reach DL then runs DL -> FL | one extra recursive `Lock_Acquire` per access (nearly every caller already holds DL) | **Mode 2, only if the detector counts unowned entries** |
| D | Swap the order inside EA's readers (`0x734550` and the others) | | four readers to change, more hook sites | no |
| E | Harness-only: wait for a quiet resource update before `LoadSave`, or retry the load | hides it from our tests only; players still hang | none | no |

Option A is the smallest: one vtable slot, no code bytes patched, no change to EA's readers; the only behaviour change is "close later" while a reader holds the database.

#### 13.9.4 Design (Mode 1 = A, Mode 2 = A + C)

Switch `[PkgLock] Mode=0|1|2` (default 0, new-switch rule), `Detect=0|1`.

- **Hook site 1**: O vtable slot `0x18` at `0xFFE0E8` (`.rdata`, `VirtualProtect` + one dword; install checks the old value is `0x7355A0`, else logs "not installed: slot holds X"). Thiscall `PkgCloseHook(O, char force)`:
  1. `O[0x104] != 1 || O[0xE0] != 1` -> return the original (no lock, unchanged fast path).
  2. `force == 0`: `deadline = 0; r = Lock_Acquire(O+0x48, &deadline)` (through the game entry, so `LockAcquireHook` still sees it). `r < 0` -> `pkCloseDefer++`, return 0. Else `r = orig(O, 0); Lock_Release(O+0x48); pkCloseOk++`.
  3. `force != 0` (no caller found, kept for safety): blocking `0x72C6C0(1)` on `O+8` first (option B), then the original, then release; `pkCloseForce++`.
  4. Household rule: not applicable (resource system, no Sim). Results equal vanilla: the file closes at a later pass.
- **Hook site 2** (installed when `Detect=1` or Mode 2): D vtable slot `0x50` at `0xFFE0C8`, `PkgBeginHook(D)`: if `((CRITICAL_SECTION*)(D+0x40))->OwningThread != GetCurrentThreadId()` -> `pkBeginUnowned++`; in Mode 2 also take DL (blocking) before the original and release after. Same owner read as `crashfx.cpp` (D lock owner check).
- **Counters** (reserve `native` slots at build time, docs/registry.md): `pkCloseOk`, `pkCloseDefer`, `pkCloseForce`, `pkBeginUnowned`, `pkAbbaSeen`, `pkDeferMax` (longest run of consecutive defers on one O).
- **Hang detector (`Detect=1`, also in Mode 0)**: the hooks store `{O, thread, GetTickCount()}` before calling the original and clear it after. The memory worker (`UdMemMain`, `userdiag.cpp`; wait capped at 1000 ms while `Detect=1`) checks: an entry older than 3 s whose thread owns FL while DL is owned by another thread `T` -> one line `[PkgLock] ABBA: tid A holds O+0xC0, waits O+0x48 owned by tid T (O=...)`, `pkAbbaSeen++`, log flushed. Read-only: it never breaks the cycle (a Mode 0 run stays deadlocked, as vanilla).
- **Harness**: `queue.ts` / `game-alive.ts` map a `[PkgLock] ABBA` line to signature `pkg-abba` (known EA hang, §13.8 rerun rule) instead of a generic `world load: nothing moved`.
- **Selftest** (`--self-check` in the native selftest): fake O/D with two real CSs and a fake vtable: (a) DL held by another thread -> hook returns 0, original not called, `pkCloseDefer` 1, FL never entered; (b) DL free -> original runs with DL held, DL count back to 0 after; (c) precheck false -> original runs, no DL taken; (d) `force` 1 -> DL taken blocking; (e) slot value mismatch -> not installed; (f) two threads, fake original FL -> DL, peer DL -> FL, 10k rounds in Mode 1 -> no deadlock within 5 s (Mode 0 control arm run with a timeout, expected to hang).

#### 13.9.5 Repro (forced, no waiting)

The natural rate (~4 in ~2100 menu loads) is far too low to wait for. Repro `pkg-abba` (repros.json entry + agent action):
1. Test-only switch `[PkgLock] ReproWiden=N` (off in release inis): in the slot `0x18` hook, skip the idle interval (close whenever the open count is 1, like `force`) and, in Mode 0, enter FL in the hook, `Sleep(N)`, then call the original (which re-enters FL recursively and then wants DL). That holds the FL -> DL window open on every update.
2. Agent action `pkgAbbaRepro`: at the main menu, run the save-list read (`MainMenuModel.GetSaveFileDetails`, the first step of the `LoadSave` handler) 30 times over all saves, so the worker reopens closed packages (DL -> FL).
3. Arms: Mode 0 + `ReproWiden=50` + `Detect=1` -> expected hang with a `[PkgLock] ABBA` line within the 30 reads (proves repro and detector; the job ends on the detector line and counts as expected). Mode 1 + same -> 30 reads done, `pkCloseDefer` > 0, `pkAbbaSeen` 0, then a normal world load and save.
4. Deterministic off/on repro: one clean run per arm (crash-cause rule); then a Mode 1 smoke without `ReproWiden` on BP (load, 5 min play, save, quit).

If the Mode 0 arm does not hang within 30 reads at 50 ms widening, the window model in §13.9.2 is wrong: stop and re-read the dump before building more.

#### 13.9.6 Risks

- A deferred close keeps a package file open longer while readers are active; EA closes it on a later idle pass [inferred]. `pkDeferMax` shows a package that never closes.
- `force != 0` callers of O slot `0x18`: none in the class; vtable calls from elsewhere cannot be ruled out statically [inferred]. They get option B (consistent blocking order), not the skip.
- `Lock_Acquire` with deadline 0 in CS mode does one `TryEnterCriticalSection`, then `GetTickCount() >= 0` -> returns -2 [verified decompile `0x4E16F0`]. Our `LockAcquireHook` detour passes locks other than `+0x918` to the original (§13.8) [verified source per §13.8].
- Apex Radiance's resource cache (`game-copy/apex-src/features/resource_cache.cpp`) touches nothing in `0x7343B0`-`0x7356xx` [verified grep], so no overlap. The resource system is near Apex's area (lookups): per the coordination rule we build it ourselves, off by default, and offer it to them once the repro proves it.
- Vanilla rate unknown (§13.8). The cycle is verified in EA code, so the fix is justified either way; default ON only after the repro passes and a smoke shows no new hitch (rare-crash rule).

#### 13.9.7 Existing fixes checked

docs/24 has no entry for a menu hang or this lock pair; Apex's source patches nothing in `0x734xxx`-`0x7356xx` [verified grep]. A web search (EA and thesims forums, 2026-11-08) finds only generic "frozen at main menu" advice (windowed mode, clear caches, remove mods), no lock or deadlock fix [verified search]. Logged in docs/24.

#### 13.9.8 Effort (wall clock)

| Step | Time |
|---|---|
| Native build: two slot hooks, detector in `UdMemMain`, counters, ini, `ReproWiden` | ~45 min agent build |
| Selftest cases (a)-(f) | ~20 min |
| Agent action `pkgAbbaRepro`, repros.json entry, `pkg-abba` signature in queue.ts | ~30 min |
| Game runs: Mode 0 / Mode 1 repro (one each), Mode 1 BP smoke | 3 jobs, ~10-15 min each |
| Docs, counter registry, PLAN line | ~15 min |

About 2 h of work plus 3 game jobs. Open: Mode 2 only after a nonzero `pkBeginUnowned` in real runs; whether any non-update caller passes `force != 0`.

#### 13.9.9 Build status (2026-11-08): built, not run

Built on `bugfix/ea-pkglock` (from `docs/ea-package-lock-design`, master 91269f04 merged): ts3native **0.36.216**, pair **pkglock-1108** (`docs/data/builds.json`: asi sha256 `710f91ce`, ts3fixes = the master1108 package `f2826284`, agent `game-copy/ts3agent.package.pkglock-1108` sha256 `ec1de5f0`). No game run, no Bin install, not merged.

- `native/ts3native/pkglock.cpp`: `[PkgLock] Mode=0|1|2`, `Detect`, `ReproWiden` as §13.9.4, with these changes from the design: (1) the detector runs on its own watcher thread (1 s), not in `UdMemMain`, so it works without `[UserDiag]`; it also writes a `pkglock:` counter line per minute when the counters moved; (2) `ReproWiden` widens only the first 300 closes (the menu reads), so a world load after the reads is not slowed; (3) install checks the slot value and EA's first bytes of `0x7355A0` (the `+0x104` precheck) and `0x734710` (`+0xB8`); (4) a DL whose kind byte `+0x24` is 0 (not CS mode) is passed to EA unchanged. Repo ini: `Mode=0 Detect=0 ReproWiden=0`. Counters v177 `pk*` at `0x5EC8` (docs/registry.md). Mode 2 is built and EXPERIMENTAL; Mode 1 is the shipping mode.
- Selftest `TestPkgLock` (design cases a-f, real CRITICAL_SECTIONs laid out as EA's, fake FL -> DL original): all pass; Mode 1 10k closes vs 10k DL -> FL readers finish (one run: 6033 closes ran, 3967 deferred, longest defer run 427 in a tight loop); the Mode 0 + ReproWiden control deadlocks as EA does and the detector writes one ABBA line. selftest-all, hook-sites --check ok.
- Harness: agent command `pkg_abba_repro` (`PkgAbbaHandlers.cs`; the game's mscorlib has no `System.IO.Directory`, so it reads the job's save 30 times, plus `files` when given, instead of "all saves"), `cycle.ts` polls an arm reply with `pending: true` under the stall guard, `game-alive.ts` adds `pkg-abba: <line>` to a "nothing moved" stall when the log has a `[PkgLock] ABBA:` line, `queue.ts` job key `expectHang` (caught = PASS) and the deadlock retry also on that label, repro `pkg-abba` (repros.json), realism row (stress).
- Queue `tools/harness/queues/pkglock.json` (BP, ~10-15 min each): `bp-pkglock-smoke` (Mode 1, no widening), `bp-pkg-abba-m0` (Mode 0 + ReproWiden=50 + Detect=1, expectHang), `bp-pkg-abba-m1` (Mode 1 + same: reads finish, deferred closes, no ABBA line, load and save). Run: `TS3_AGENT_PACKAGE=game-copy/ts3agent.package.pkglock-1108 node tools/harness/queue.ts tools/harness/queues/pkglock.json --no-smoke`.

Next: run the queue. Mode 0 arm must hang with the ABBA line (else the window model is wrong: stop, re-read the dump); Mode 1 arm clean; then per the rare-crash rule set `Mode=1` in the release ini (Detect stays 0) and offer it to Apex (resource-system area). Open risks: whether 30 reads of one save reopen it often enough to hit the 50 ms window (pass `files` with more saves if not); the Mode 0 hang happens on a sim task, so the job ends by the stall guard after ~60 s, not at the line.
