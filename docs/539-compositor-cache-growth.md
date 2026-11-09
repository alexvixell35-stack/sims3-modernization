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

#### 13.9.10 First run and the forced-defer repro (2026-11-09)

correctness-1109 (logs `probe-out/queue/correctness-1109-2026-11-08-bp-pkg-abba-m0*`, `-m1*`, `-bp-pkglock-smoke*`): **m0** reproduced the hang [verified, run]: `[PkgLock] ABBA: tid 53144 holds O+0xC0, waits O+0x48 owned by tid 46420 (in idle close for 3438 ms, Mode 0)`, widened 203. **m1** passed 30 of 30 reads but proved nothing: its first summary line says `idle closes run 226, deferred 0, widened 226`; the job's `deferred [1-9]` logRequire matched the natural deferrals of later minutes (11-21 during play), and repro.ts printed "PASS: steps done (no watch)" without checking `pkCloseDefer`. The plain smoke (Mode 1, no widening) deferred 5-17 times naturally.

Why m1 never collided [verified, code]: in 0.36.216 the widening sat in `PkCallClose` (hold FL 50 ms, then EA's close), which Mode 1 calls only after its DL try has succeeded (`pkglock.cpp:133` try, `:146` widened call). So the widened Mode 1 close held DL and FL for 50 ms and readers queued on DL behind it; the try itself still only met a reader by chance, the same rare window as without widening.

0.36.217 (`bugfix/pkglock-repro-force-defer`, pair **pkglock-1109**, asi sha256 `db3504ec`, ts3fixes/agent = the pkglock-1108 packages): `ReproWiden=N` now waits up to N ms for another thread to own DL at the point where each mode's order makes the collision: Mode 0 while holding FL (EA's FL -> DL then deadlocks, as before), Mode 1 right before its try (the try fails, the close defers). A reader whose begin access (D slot `0x50`, now also hooked when only ReproWiden is set) runs while owning DL on the package whose widened close waits holds DL N ms first, so the close sees it even though Mode 1 holds no lock while waiting. The first widened Mode 1 defer writes `pkglock: ReproWiden defer: tid <reader> owns O+0x48 (O=...), Mode 1 idle close deferred`; the window-end and minute lines add widened deferrals and reader holds. Selftest case (g): the forced Mode 1 collision defers once; begin hold only for a DL owner of the waiting package. `cycle.ts` now fails an agentArm whose reads != want.

Queue `tools/harness/queues/pkglock-abba-proof.json`: 3 runs per arm in ABBA order (m0 m1 m1 m0 m0 m1). m0: expectHang + logRequire an idle-close ABBA line; m1: logRequire the `ReproWiden defer:` line (deferred >= 1 inside the window), logForbid ABBA, 30 of 30 reads, load + 4 min + save. Run: `TS3_AGENT_PACKAGE=game-copy/ts3agent.package.pkglock-1108 node tools/harness/queue.ts tools/harness/queues/pkglock-abba-proof.json --no-smoke` (~40-45 min). Inferred, not run: that a menu read reaches begin access while a widened close waits (m0's hang says readers do meet the widened close).

## 14. `bp-leak40-memtravel-x3` 20:15 UTC: AV at `0xD4B154` in a ModelBuilder teardown on world unload, the 4th float-5 hit (2026-11-09, ledger §468; static, no game run)

Job: queue `correctness-1109`, pair master1109 (ts3native 0.36.216, asi b0b0005c, ts3fixes 679b533c), repo ini = release ini (`[CasTrim] Mode=3 Detect=1 CellGuard=1`), `MemDiag` / `UserDiag` on, save-matrix `--travel --cases travel --to france --trips 3 --mem-marks`. Travel save #12 (the third trip out) passed, then on the world unload `ACCESS_VIOLATION reading 0x40a00000` (`State: MEM_RESERVE`) at `0x00D4B154`, main thread 36936. EA report `xcpt <machine> 26-11-09 04.15.11.txt`; log `probe-out/queue/correctness-1109-2026-11-08-bp-leak40-memtravel-x3-2006-ts3native.log` (fault line 3897).

### 14.1 The fault [verified: disassembly TS3W 1.67, EA report registers and stack data]

- `0xD4B150` is the same async-request callback as §13.1 (`mov ecx,[esp+4]; mov eax,[ecx]; mov edx,[eax+4]; jmp edx`). This time the value `0x40A00000` was reserved, not committed, so the first read (`0xD4B154`, the vtable) faulted, not the slot read at `0xD4B156`.
- Chain (stack code refs and the stack data): SimService `0x5F1680` -> `0x5F07BF` -> `RefCounted_AtomicRelease 0x692745` -> `ModelBuilder_DeletingDtor 0x5DE368` -> `ModelBuilder_Dtor` (return `0x5DC53E`) -> `0x5D83B0` (return `0x5D83B9`) -> `0x5D82A0` (return `0x5D8377`) -> loader +0x3C `0x728DB0` (return `0x728DD4`) -> `0x728760` (return `0x7287EC`) -> `0xD4B150`.
- `0x5D82A0` (thiscall `(ModelBuilder)`, plain `ret`) first calls `0x5D8240`, then runs three release loops, each through the loader's +0x3C with callback `0xD4B150` and an inline release of an untagged cell (vtable slot 1, cell set to 0): the list at `+0x1C` (cell node+0x1C, return `0x5D82CF`), the list at `+0x28` (cell node+8, return `0x5D831A`), and the 0x74-byte part records `[builder+0xC, +0x10)`: a record whose first dword is non-zero gets its 4 cells rec+4..+0x10 released (return `0x5D8377`). Tagged cells (bit 0 or 1) are pending requests the loader recycles.
- Callers: `0x5D82A0` is called only at `0x5D83B4` (in `0x5D83B0`, itself called from `ModelBuilder_Dtor` `0x5DC539` and from `0x5D9AD9`) and at `0x5D96AD` (a holder's release, ecx = `[holder+0xC]`) [verified: E8/E9 scan of .text].
- Frame at the callback (esp `0x1AF634`): `[esp]` `0x7287EC`, `+4` value `0x40A00000`, `+0xC` record end `0x33CF6530` and `+0x10` record `0x33CF6278` (`0x728760` saved the loop's ebp / ebx), `+0x14` `0x728DD4`, `+0x18` cell `0x33CF627C` (= rec+4, cell 0), `+0x20` loop counter 4, `+0x28` `0x5D8377`, `+0x38` saved esi = builder `0x77BEC220`, `+0x44` `0x5D83B9`, `+0x50` `0x5DC53E`. The record is the 6th from the end.

### 14.2 Our code and CasTrim [verified: log, source]

- No hook of ours in `0x5D82A0`-`0x5D96B2`, `0x5DC450`, `0x5DE360` or `0x692720` before this build (docs/68). CasTrim Mode 3 swept after save #11 (`sweep done: swept 1657`, log line 3533); save #12 got no sweep (`no keep list for this save`). The release-site and inventory checks of §13.7 saw 0 bad cells and 0 float-5 through save #12 (lines 3873-3875): they do not read ModelBuilder records.
- Whether the record, the builder or `0x40A00000` overlapped an erased compositor is not known: the 0.36.216 decoder did not cover `0xD4B154` or this frame.

### 14.3 The family, 4 hits [verified: §13.3 table + this crash]

| When | Build | Site | Cell |
|---|---|---|---|
| 2026-10-15 (docs/67 §3) | 365b9c27d8ba | `0x5CEB9A` | ModelBuilder part record (0x74 bytes) `+0x44` inline vector, from `ModelBuilder_Dtor`'s last loop |
| 2026-11-08 01:46 (§12) | 0.36.190 | `0x5E772C` | outfit inventory `[rec+0x38]` |
| 2026-11-08 06:14 (§13) | 0.36.196 | `0xD4B156` | `[outfit+0xBC]` entry 63 |
| 2026-11-09 20:15 (this) | 0.36.216 | `0xD4B154` | ModelBuilder part record rec+4, from `0x5D82A0` |

Two of the four are the same ModelBuilder 0x74-byte part record type, both on a ModelBuilder teardown from SimService on main [inferred: same record stride and owner; field roles not mapped]. The 10-15 hit predates CasTrim.

### 14.4 Built: float5-1109 (ts3native 0.36.218, counters v178)

1. Detector (`CtMbHook`, `castrim.cpp`): the call sites `0x5D83B4` and `0x5D96AD` -> `0x5D82A0` are patched (`PatchCalls`) when `[CasTrim] Detect=1` (or `MbGuard=1`), in any Mode. Before EA runs, every record with a non-zero first dword has its untagged non-null cells rec+4..+0x10 checked: `0x40A00000`, unreadable, or a first dword that is not a 4-aligned TS3W `.rdata` vtable = bad. A bad cell logs `cas trim: modelbuilder cell bad` (first 40): reason, thread, caller (dtor `0x5D83B4` / holder `0x5D96AD`), builder, record index / count / address, cell index, value (`float 5.0` / `float-like` tag), its first 4 dwords, MbGuard, CasTrim slot and erase-ring lookups of the value, the record and the builder (`ERASED by CasTrim ... s ago` or `never erased`), the last sweep (`last CasTrim sweep done N s ago, after save #M`), mode / in-a-save / save # / travel flags / world load, the record's 0x74 bytes and the builder's first 0x40 bytes. A bad record range or a fault while reading is logged too. Per save: `cas trim: modelbuilder check (0x5D83B4 / 0x5D96AD, session)`: calls, records, cells, bad, erased overlaps, skips, ms and max us a call, result vtables seen.
2. `[CasTrim] MbGuard` (default 0, repo ini 0): 1 sets a bad cell to 0 before EA, so EA neither asks the loader nor releases it (that result leaks). Off: log only, EA runs unchanged.
3. Crash decoder (`crashfx.cpp` -> `CtRelFault`): any fault at `0xD4B150`-`0xD4B159` (and `0x5AF67E` as before) first tries the `0x5D82A0` frame (§14.1 offsets; all three loops; builder from saved esi, or `[holder+0xC]` from `0x5D96AD`): `crash forensics: cas trim modelbuilder release fault` with the same lookups, the record and builder bytes and the v178 counters; otherwise the §13.7 outfit decoder. `tools/harness/crash-ledger.ts triage` decodes the same from an EA report (`modelBuilderFault`, self-check fixture = this report).
4. Counters v178 at `0x5EE8`: `mbCalls`, `mbCells`, `mbBad`, `mbSkip` (docs/registry.md).
5. Kept: every §12.5 / §13.7 detector, CellGuard, the inventory checks, the erase ring, P5c rings, draw / scene guards, heap check, allocsites.

Selftest (`TestCasTrimMb`): a dummy call site patched to `CtMbHook` and run for real: MbGuard off = EA's fake sees every cell unchanged (6 checked, 3 bad: float 5, zero vtable, non-.rdata); on = those 3 set to 0, a pending and good cells and a record with first dword 0 unchanged; a clean builder after that adds nothing; bad range, unreadable records and a null builder still call EA; the decoder on the 11-09 frame (dtor and holder callers, the list loop, the outfit frame left to `CtRelFaultText`, unreadable stack). `hook-sites.ts --check` passes.

### 14.5 Stress run

Queue `tools/harness/queues/float5-stress.json`, one job (Rodney: stress until break with the culprit logged, no off arms): the same memtravel case on Baseline-BP with `--trips 8 --mem-marks`, the float5-1109 asi, Mode 3 as shipped (Detect 1, CellGuard 1, MbGuard 0), ts3fixes master1109 (build/), timeout 60 min, stopQueueOnFail. Any `modelbuilder cell bad`, `float-5 cell`, `outfit cell bad`, `cell guard skipped` or crash-forensics line fails the job: that line is the evidence this round wants. Pass (no break in 8 trips) proves nothing about the cause; the per-save `modelbuilder check` lines must show calls > 0 so the sites ran.

## 15. `bp-float5-memtravel-x8` (queue fixes-1109): memcpy to NULL in the outfit save, the 5th float-5 hit (2026-11-09, ledger §469; static, no game run; guard build 0.36.225)

Job: queue `fixes-1109`, pair master1109b (ts3native 0.36.219), release ini `[CasTrim] Mode=3 Detect=1 CellGuard=1 MbGuard=0`, `--travel --cases travel --to france --trips 8 --mem-marks` on Baseline-BP. Files: `probe-out/queue/fixes-1109-2026-11-08-bp-float5-memtravel-x8.log`, `...-2223-ts3native.log` (fault line 4353), EA report `<Documents>\Electronic Arts\The Sims 3\xcpt <machine> 26-11-09 06.33.40.txt`. `crash-ledger.ts triage`: change set 64d6cfd560da.

### 15.1 The fault [verified: EA report registers and stack data, disassembly TS3W 1.67 / MSVCR80 8.0.50727.9680]

- `ACCESS_VIOLATION` writing `0x00000000` at MSVCR80+0x173D0 (the memcpy SSE loop, `movdqa [edi],xmm0`), sim thread, `ebx = esi = 0x40A00000`. Second exception (eip 0) = the secondary fault in the handler / unwind.
- Chain: outfit writer `0x5E8620` (tag `0x0D160D16`) -> `0x5E7A20` (this = the outfit; builds a vector of part-record pointers from `[outfit+0x98, +0x9C)`, 0x68-byte records with `[rec+8] != 0`) -> `0x5E61A0` at `0x5E7BC2` (thiscall (outfit; &vector, 3 more args), `ret 0x10`, return `0x5E7BC7`; the only caller, E8/E9 scan) -> per record `obj = [rec+0x5C]` (0: skipped at `0x5E61FF`), `0x5BE810(obj)` at `0x5E620A`, `0x5BF4C0(obj, &str)` at `0x5E6217` (return `0x5E621C`) -> with `[obj+0x28] == 0`, `str.assign([obj+0x2C], [obj+0x30])` (`0x5BF5C9`) -> `0x408E20` -> `0x408B90` append(first `0x40A00000`, last `0x011C7E54`): `n = (last - first) >> 1` is negative, the unsigned compare takes the grow path, `EA_AllocTagged 0x4E3F90` returns NULL, memcpy to NULL (return `0x408C3A`).
- `0x011C7E54` is EA's empty UTF-16 string: `0x5E61A0` starts its locals as `{0x11C7E54, 0x11C7E54, 0x11C7E56}` (begin, end, capacity end). So the object's string was empty and only its begin dword became 5.0f.
- The object: the 0x60-byte class built by `0x5BF260` (allocated 0x60 at `0x9C3B40`): vtable `0xFE2A80`, `+0x28` a pointer (0 here), string `{+0x2C begin, +0x30 end, +0x34 capacity end, +0x38 allocator 0xFE2A58}` initialised to the same empty triple. Its destructor `0x5BF710` (vtable slot 2) frees the buffer only when `([+0x34] - [+0x2C]) & ~1 > 2` and begin != 0, the same rule as every EA string16 free here (`0x408C46`, `0x5E6289`, `0x5E62F9`).
- Stack (esp `0x49777210`): `0x5E621C` at `A = 0x49777310`; `A-0x58` record `0x3D619820` (0x5BF4C0's saved esi), `A-0x60` / `A-0x5C` the begin / end it passed (`0x40A00000`, `0x011C7E54`), `A-0x64` `0x5BF5E0`, `A-0x6C` object `0x3C39B580` (0x408E20's saved esi), `A+0xC` outfit `0x6583D250` (0x5E61A0's saved esi), `A+0x54` `0x5E7BC7`; `0x408C3A` at `0x4977724C`.
- When: inside SimService_Save of save #13 (arrival save, NewDowntownWorld, trip 4 home), right after CtSweepSave's scan line (doomed 1403 new, no erase yet: erases only run after the save returns, op 17/18).

### 15.2 Detectors and CasTrim [verified: log]

- Not the ModelBuilder site: `mbBad` 0 every save (save #12: calls 391, records 4108, cells 4272, bad 0, skips 0). Outfit release / inventory checks bad 0, float-5 0 through save #12. None of them reads `[rec+0x5C]+0x2C`. The crashfx decoder (`0xD4B150`-`0xD4B159`, `0x5AF67E`) and crash-ledger `modelBuilderFault` did not fire: a new site.
- Sweeps this session: only after save #9 (swept 1438). Saves #10-#12 wrote outfits fine. No erase between the reload into NewDowntownWorld and the fault. Whether the record or the object overlapped an erased compositor is not known: the erase ring was not consulted at this site [open, ledger I-469-5].

### 15.3 The family, 5 hits

| When | Build | Site | Value at |
|---|---|---|---|
| 2026-10-15 (docs/67 §3) | 365b9c27d8ba | `0x5CEB9A` | ModelBuilder part record `+0x44` vector |
| 2026-11-08 01:46 (§12) | 0.36.190 | `0x5E772C` | outfit inventory `[rec+0x38]` |
| 2026-11-08 06:14 (§13) | 0.36.196 | `0xD4B156` | `[outfit+0xBC]` entry 63 |
| 2026-11-09 20:15 (§14) | 0.36.216 | `0xD4B154` | ModelBuilder part record rec+4 |
| 2026-11-09 (this) | 0.36.219 | MSVCR80 memcpy from `0x408B90` | `[[outfit part record]+0x5C]+0x2C` (string begin) |

Three of five are outfit part records (0x68 bytes) or objects they point at; every hit since 11-08 is in a session with CasTrim Mode 3, one (10-15) predates CasTrim [inferred: common writer not found].

### 15.4 Built: float5guard-1109 (ts3native 0.36.225, counters v180)

1. Detector (`CtOsHook`, `castrim.cpp`): the call `0x5E7BC2` -> `0x5E61A0` is patched (`PatchCalls`) with `[CasTrim] Detect=1` or `OutfitStrGuard=1`, in any Mode. Before EA, each record pointer of the vector (0-1024) is read and `obj = [rec+0x5C]` (0 skipped as EA does). Bad = object unreadable; first dword not a 4-aligned TS3W `.rdata` vtable; string begin = end = `0x40A00000`; begin > end; length 64 KB or more; `[begin, end)` unreadable (one byte a page); a pointer equal to `0x40A00000`; end past the capacity end. A bad object logs `cas trim: outfit string bad` (first 40): reason, thread, caller, outfit (and the save # the inventory check last saw it), record index / count / address, object, OutfitStrGuard and what it did, erase-ring lookups of the object and the record, the last sweep, mode / in-a-save / save # / travel flags / world load, the object's 0x60 bytes, the record's 0x68 bytes, the outfit's first 0x40 bytes. Good objects' vtables are counted. Per save: `cas trim: outfit string check (0x5E7BC2, session)`: calls, records, objects, bad, erased overlaps, resets, ms, max us a call, object vtables. The owner Sim is not reachable from the outfit through a verified field, so it is not logged; the inventory check's outfit map key (§12.5) names the outfit when it ran.
2. `[CasTrim] OutfitStrGuard` (code default 0; release ini 1): resets the string to EA's empty triple `{0x11C7E54, 0x11C7E54, 0x11C7E56}` only when all hold: vtable `0xFE2A80`, `[obj+0x28] == 0`, begin != end, and begin > end by less than 2 GB or `[begin, end)` (under 64 KB) unreadable. Everything else stays log only. The allocator at `+0x38` is not touched.
3. Crash decoder (`crashfx.cpp`, every access violation, since this one faults outside TS3W): esp..esp+0x400 is searched for the §15.1 frame (`0x5E621C` at A with `0x5BF5E0` at A-0x64 and `0x5E7BC7` at A+0x54); found: `crash forensics: cas trim outfit string copy fault` with begin / end, the object (vtable, +0x28, its string now, bytes), the record and whether `[rec+0x5C]` is still the object, the outfit, erase-ring lookups, the v180 counters, whether `0x408C3A` is below the frame (first 2). `crash-ledger.ts triage` prints the same from an EA report (`outfitStringFault`; self-check fixture = this report's stack lines).
4. Counters v180 at `0x5F28`: `osCalls`, `osObjs`, `osBad`, `osFix` (docs/registry.md); `0x5EF8`-`0x5F27` held for v913 (PR #313) and v179 (watchdogasync), so sizeof is 6094 int32 on this branch.
5. Kept: every §12.5 / §13.7 / §14 detector, CellGuard, MbGuard, the decoders, the erase ring.
6. EA app 1.69: not installed. Master's ts3native skips every patch when the host is not `TS3W.exe` at 0x400000, and CasTrim (with its Detect / CellGuard / MbGuard sites) is not in the v0.2.10 EA port. Map `0x5E7BC2`, `0x5E61A0`, `0x5E621C`, `0x5BF5E0`, `0x5E7BC7`, `0xFE2A80` with `tools/sigmap.ts` when CasTrim moves to the EA port (each site verified by hand, as sigmap says).

Selftest (`TestCasTrimOs`): a dummy call site patched to `CtOsHook` and run for real, 7 records: none, a good string, the 11-09 string, the same with `+0x28` set, the same on another vtable, an unreadable range, EA's empty string. Guard off: EA's fake sees every string unchanged and its own arguments (6 objects, 4 bad, 0 resets). Guard on: only the 11-09 string and the unreadable one reset to the triple (allocator and +0x28 untouched); the `+0x28` and other-vtable ones unchanged; a clean rerun adds the 2 left. Bad and unreadable vectors and a null vector still call EA. Decoder: the 11-09 stack layout (eip in the CRT), the record no longer pointing at the object, no `0x5BF5E0`, unreadable esp.

### 15.5 Safety of OutfitStrGuard [verified: disassembly TS3W 1.67]

- **It fires only where EA would certainly crash.** With `[obj+0x28] == 0`, `0x5BF4C0` always reaches `0x408E20([obj+0x2C], [obj+0x30])` (`0x5BF5C9`-`0x5BF5DB`; the `je` at `0x5BF5D2` only skips when the source is the destination string itself, which `&obj+0x2C != &local` rules out). (a) begin > end by d < 2 GB: `0x408E20` computes `(end - begin) sar 1` < 0, its unsigned compare with the destination size takes the append path (`0x408E3E`), and `0x408B90` sets the new length to `size + (end - begin) / 2` (unsigned, about `2^32 - d / 2`), asks `0x4E3F90` for `2 * (length + 1)` bytes, which wraps to about `2^32 - d` > 2 GB, then copies `(end - begin) & ~1` = about `2^32 - d` > 2 GB from begin (`0x408C2D`). A fresh block of more than 2 GB plus a separate readable source of more than 2 GB cannot both exist in a 4 GB address space that also holds the exe: the allocation returns NULL and memcpy writes to NULL (the 11-09 crash: `d = 0x3F8381AC`, request `0xC07C7E56`), or the copy runs off mapped memory. A fault either way. (d >= 2 GB would wrap the request small; that case stays log only.) (b) begin < end, under 64 KB, a page of `[begin, end)` unreadable: memcpy reads it: a fault. A valid string16 always has begin <= end inside its readable buffer, so neither condition holds for any string EA built.
- **The value written is one EA itself uses for this object.** `{0x11C7E54, 0x11C7E54, 0x11C7E56}` is what the constructor `0x5BF260` stores, so every reader of this class already handles it: `0x5BE810` skips an empty string (`0x5BE82E`), `0x5BF4C0` copies 0 characters, the destructor `0x5BF710` and every string16 free here skip it (`(0x11C7E56 - 0x11C7E54) & ~1 = 2`, not > 2), and an append to it allocates fresh (`0x408B90`: capacity `2 / 2 - 1 = 0`). The old buffer, if any, leaks (bytes, once).
- **It only writes into an object of that class.** The vtable must be `0xFE2A80` and the object must be the record's live `[rec+0x5C]` at the moment EA reads it. If the memory were already freed and reused with that dword intact, the write lands in another owner, but only in a state where EA's next instructions crash anyway.
- **Household Sims.** Not a behaviour change for any Sim: a save that would crash writes an empty string for that part field instead. The field's role is not mapped [open, ledger I-469-6]; the next load reads an empty string where the corrupt one would have been unwritable.
- What it does not prove: the cause. The float-5 writer is still unknown; the guard turns this one site into a logged, saved game.

### 15.6 Queue

`tools/harness/queues/float5-guard.json` (`--no-smoke`, stopQueueOnFail false), both on pair float5guard-1109 with ts3fixes / agent = master1109b (build/):
- (a) `bp-float5-memtravel-x8-m0`: the crashed case, Mode 0 (no sweep), Detect 1, CellGuard 1, MbGuard 0, OutfitStrGuard 0 (log only). Control: does any float-5 site hit without Mode 3?
- (b) `bp-float5-memtravel-x8-guard`: Mode 3 as shipped plus OutfitStrGuard 1. Stops at the first detector hit (that line is the evidence) or after 8 trips.

Each needs `outfit string check ... calls > 0` per save (the site ran) and 8 travel saves; any bad / float-5 / guard / crash-forensics line stops the job. ~30-35 min a job, ~70 min for both (60 min cap each).

### 15.7 Quarantine arm (pair float5guard-1109b, same version 0.36.225)

The static pass (11-09) found no writer of ours and could not rule out CasTrim's erase exposing an EA stale pointer: Mode 3 frees thousands of compositors mid-session, and the dlmalloc-like allocator can hand a freed 0x68 chunk back as another type [inferred]. `[CasTrim] Quarantine=N` (test only; code and release ini 0) takes that free away:
- In `CtSweepStep`, before each `0x5EE640` erase, one extra reference through the compositor's vtable slot 0 (`0x723130` -> `0x731D00`, thiscall, no args, `lock xadd +0x100`; the AddRef EA's keep-set insert uses at `0x5E76D8`) [verified disassembly]. It is never released, so an erased compositor stays at 2 references (cache + ours) and never returns to the heap. At N held the sweep stops erasing for the session (`cas trim: quarantine full`, once).
- Each save (with the per-save summary lines) scans every held compositor's 0x68 bytes (the `+0x28` child vector header included) for `0x40A00000`: `cas trim: quarantine hit: offset +0xNN, compositor 0x..., save #N` (first 40) and `cas trim: quarantine save #N: held H of N, dwords 0x40A00000 in held blocks now K`. v180 has no free slot: log only.
- Selftest (`TestCasTrimSweep`): Quarantine=1, two candidates: the first is AddRef'd (fake) and erased, the second stays (full); a later hold is refused; the scan finds `0x40A00000` at `+0x28` of a held block.

Queue `float5-guard.json` now: (a1, a2) Mode 0 control twice, (b) Mode 3 + OutfitStrGuard 1, (c) `bp-float5-memtravel-x8-quar`: Mode 3 + Quarantine 4096 + OutfitStrGuard 0 (detect only), stops at the first detector or quarantine hit. Reading (c): a quarantine hit = stale writer into an erased compositor (the offset names the field; next: a DR0 watch on that field of the next erased one); float-5 elsewhere = not caused by our erase's free; clean while (b) or earlier Mode 3 runs hit = the free path our erase triggers. ~30-35 min a job, ~2-2.5 h for four (60 min cap each).

## 16. Float-5 triage cycle 3: write trap on the outfit cells, and the realm shape check (2026-11-09, ledger §470; built 0.36.227, pair float5trap-1109, not run)

Inputs: the Mode 0 control `bp-float5-memtravel-x8-m0-a1` (release-verify-1109, master1109c): float-5 hit three outfit result cells with no CasTrim sweep or erase (outfit `0x5E6F15C0` entry 87 of 121 at saves #5 / #6; `0x5E459330` entry 3 of 81 at #29 / #30; `0x3DAF5900` entry 87 of 91 at #33; all outfits vtable `0xFE4A10`, found by the save inventory check `0x5EFE11`) [verified: log]. So CasTrim's erase is not the cause [verified for that run]; EA vs our other features is open. Then `bp-float5-memtravel-x8-guard` (Mode 3) crashed at a new float-5 site, `0x6820F4` in Realm_RemoveShape (§16.6), with every outfit detector clean: the stray 5.0f store is heap-wide, not outfit-only [inferred].

### 16.1 `[CasTrim] WriteTrap` (test only; code and release ini 0; needs Detect=1)

- `CtTrapNote` (`castrim.cpp`, in the save inventory pass `CtOutfitCells`, path 0): for outfits with vtable `0xFE4A10`, entries 87 and 3 of `[outfit+0xBC]` are candidates; 87 of 121 / 91 and 3 of 81 (the hit shapes) first, other sizes second (256 each a save).
- `CtTrapArm` (start of `CtInvSaveLog`, after each save, on the save's thread): up to 4 candidates, rotated by save number (`(save * 4 + i) % n`) so coverage grows across saves, go to `WpTrapSet` (`watchpt.cpp`): trap k = DR slot k (skipped if `[Watchpoint]` Lot= holds it), len 4, write only; all other trap slots are cleared; one pass over every thread of the process (the existing `WpApplyAll`: a helper thread suspends each thread, sets DR0-DR3 / DR7 with `SetThreadContext(CONTEXT_DEBUG_REGISTERS)`, resumes it). Line: `cas trim: write trap save #N: K cell(s) watched, T thread(s) set; candidates: ...` with outfit, entry, cell and its value per DR.
- New threads: Windows starts a thread with DR7 = 0 and the asi calls `DisableThreadLibraryCalls`, so there is no thread-attach hook. A thread started after the arm gets the registers at the next save, or within 30 s from `CtTrapRefresh` (`WpRefresh` every 30 s; `cas trim: write trap refresh: T thread(s) set` when the count changes). A writer thread that starts and ends within 30 s between saves is missed [shortcut].
- Hit (`WpVeh` -> `CtTrapHit`, on the writing thread after the store, eip = the next instruction): `cas trim: float5 WRITER DRk` when the cell now holds `0x40A00000` and the watch is live, else `cas trim: write trap hit DRk` (`float5 write on a stale watch` when the outfit's vtable or `[outfit+0xBC]` moved since the arm). Logged: cell, entry, outfit, save armed, value, eip as module+offset, the EA function guessed from MSVC's `0xCC` padding (`CtFnStart`, approximate: check in Ghidra), thread (the save's = sim thread, main, other), registers, ebp chain (8) and the raw after-call stack scan (`CrStack`, 10). First 200 writes and first 100 float-5 writes a session are logged; every write is counted (`trHits`, `trF5`) and its eip goes into a 24-entry table printed each save (`cas trim: write trap session at save #N: ... writer eips:`). The game continues.
- Disarm: `CtRelHook` (outfit clear `0x5E6E60` / destructor `0x5E7633`) calls `CtTrapDrop` first, so EA's release and our CellGuard store are never trapped; each save replaces the set anyway. Our detectors only read the cells (write-only watches). `XcptLog` skips our single-step hits (`WpOwnsXcpt`), so they use none of its 16 lines.
- Counters v181 at `0x5F38`: `trArms`, `trHits`, `trF5`, `trGone` (docs/registry.md).
- Cost: one thread pass per save and per 30 s (suspend / get / set / resume per thread); a trapped write costs one exception plus the log line. Watched cells are written rarely (result set / clear) [inferred; the per-save eip table measures it].
- Conflicts: the 4 debug registers are shared with `[Watchpoint]` Lot=, `[GcRootCheck]` TailWatch (arenatail.cpp DR0 / DR1), `[MonoJit]` trap arms (per thread), Frida and debugger hardware breakpoints: whoever sets a thread's DR7 last wins (docs/68). None is on in the release ini.
- Selftest `TestCasTrimTrap`: candidate order, real DR hits through `WpVeh` (a legit value, float-5, a read not caught), a thread started after the arm caught after `WpRefresh`, drop at the clear, a stale watch, an empty save clears all trap slots; the lines go to `%TEMP%\ts3native-castrim-trap-selftest.log`.

### 16.2 Queue `float5-trap.json` (pair float5trap-1109)

- (a) `bp-float5-features-off`: the crashed case (Baseline-BP, France x8, mem marks) with CasTrim Mode 0, Detect 1, CellGuard 1, and every other ts3native feature off (list in the job note and its `ini`); MemDiag / UserDiag stay on for the mem marks; ts3fixes stays master1109c. A float-5 or realm detector line there = EA's bug (or ts3fixes), not one of our native features. Stops at the first such line (`logForbid`).
- (b) `bp-float5-writetrap`: release ini (Mode 3, Detect 1, CellGuard 1) + WriteTrap 1, 8 trips; pass = no crash and the trap armed; the report is the `cas trim: float5 WRITER` lines (eip, EA function, stacks).
- Every `logRequire` / `logForbid` regex has `regexCases` (real lines from the m0-a1 log and the selftest log, or the format string, plus lines it must not match), checked by `queue.ts` at parse and `--dry-run` (new `regexCases` key, AGENTS.md rule).

### 16.6 Realm shape check (`[CasTrim]` Detect / `RealmGuard`), after the 0x6820F4 crash

The fault [verified: EA report registers, disassembly TS3W 1.67]: AV writing `0x40A0003C` at `0x6820F4` `mov [ecx+0x3C],eax`, ecx = `0x40A00000`, in Realm_RemoveShape `0x682080` on the sim thread during world teardown (trip 3, after world quit). `0x682080` (thiscall (realm, shape), `ret 4`): the realm vector `[realm+8, +0xC)` part (index `[shape+0x38]`, bounds and identity checked at `0x682094` / `0x68209B`), then the node from `0x681FF0` and the node vector `[node+0x1C, +0x20)`: `arr[[shape+0x3C]] = last; [arr[idx]+0x3C] = idx`, unchecked. The node vector's last entry was `0x40A00000`. Realm_AddShape `0x6823E0` (thiscall (realm, shape, node; 0 = root `[realm]`), `ret 8`) appends to both vectors and sets `[s+0x38]`, `[s+0x3C]`, `[s+0x50] = node`. Callers: remove `0x6821FA` (loop `0x6821E0` empties a node, esi = node, shape = its last entry), `0x686BE1`, `0x686FBE`; add `0x686BFA`, `0x686F46` (all `E8` calls) [verified].

Built (with Detect=1 or RealmGuard=1, any Mode): the five calls are patched (`PatchCalls`, call sites only; `[RouteGate] Sites=1`'s entry detours still run behind them). Before EA: every node vector entry (node = `[shape+0x50]`; for a `0x40A00000` shape from `0x6821FA` the loop's esi; for an add its node or the root) and the realm vector's last entry are checked for `0x40A00000` that is not a real shape at that index; a `0x40A00000` shape argument is flagged too. `cas trim: realm shape bad: ...` (first 40) names realm, node (the address to target next run), entry, caller, thread, save and the stack scan; `cas trim: realm shape check (0x682080 / 0x6823E0, session) at save #N` per save. Counters v182 at `0x5F48`: `rlCalls`, `rlBad`, `rlFix`, `rlSkip`.

`[CasTrim] RealmGuard` (code and release ini 0; run it on before any default change). Safety [verified disassembly; reasoning on EA's invariants]:
- It writes only a slot that holds `0x40A00000` and is not a real shape at that index (`[v+0x3C] != i` or unreadable). EA's next remove in that node writes through such an entry (`0x6820F4`) or reads `[0x40A00000+0x38]` (`0x68208E`, from `0x6821FA`): a certain fault while `0x40A00000` is unmapped (it was in the crash).
- Restore: EA keeps `[s+0x50] = node` and `[s+0x3C] = node index` for every listed shape, and every shape is in the realm vector. The one realm-vector shape naming that node and index is the pointer the store overwrote; putting it back gives the node vector EA built (two candidates = ambiguous, not used).
- Drop (no shape names it): the slot is removed as EA's own swap-remove does (last entry into the slot, its `+0x3C` updated, end - 4). A lost shape, if one exists, is in no node list; a later remove of it passes the realm checks only if it is still listed there, then does the same unchecked store EA would do, now on a valid vector [inferred risk: logged].
- Realm last entry `0x40A00000`: dropped (end - 4). EA's realm part checks index < size and identity, so a later remove of a shape no longer listed skips that part.
- A `0x40A00000` shape argument is not passed to EA (removing nothing changes nothing); from the `0x6821FA` loop its node entry was repaired first, so the loop continues with the new last entry.
- Household Sims: no Sim behaviour changes; a teardown that would crash continues.
- Cost: a node vector scan per add / remove (nodes are small; the root holds the large shapes) plus one realm entry [inferred small; the per-save line shows the calls].
- Selftest `TestCasTrimRealm`: log only, restore, swap-remove, realm last dropped, `0x40A00000` shape skipped / passed, add on the root, clean add.
- Not done (by request): no DR watch on a neighbouring slot; the logged node address is the target for a next run. The crash's stack values (`0x936CB7`, `0xC6E396`, `0xAA94A7`) are not resolved yet.

## 17. Float-5 triage cycle 4: `[UafCheck]` delayed-free quarantine (2026-11-09, ledger §471; built 0.36.228, pair float5uaf-1109, not run)

Inputs: §16 (writer not named yet); the stray `0x40A00000` sits in pointer-sized slots at different offsets of unrelated heap objects (ModelBuilder record rec+4, outfit string begin `[obj+0x2C]`, `[outfit+0xBC]` cells, the physics group shape vector). Hypothesis: a writer through a stale pointer into memory that was freed and reused [inferred]. The 0x6820F4 crash's stack data holds `0x4E5954` and `0x4E3F84` (return addresses inside EA Free / EA_Free) above the fault, so a free ran shortly before on that thread [verified: EA report stack values; that it freed the victim is inferred].

### 17.1 Free path and S3SS Mimalloc [verified: disassembly TS3W 1.67, S3SS log and config]

- Every victim class allocates and frees through EA's main heap: `EA_AllocTagged 0x4E3F90` -> `GeneralAllocator_Malloc 0x4E69D0` on `[0x11CB864]`, `EA_Free 0x4E3F70` -> `Free 0x4E5920` on the same heap. The group shape vector grows in `0x8621C0` (`0x4E3F90` at `0x862244`, old buffer `0x4E3F70` at `0x8622A9`); the "node" of §16.6 is the physics `Group` (0x40 bytes, `[shape+0x50]`), its shape vector `[+0x1C, +0x20)`.
- The allocator is dlmalloc 2.7-like (alloc.cpp): 16-byte header, size word at p-0xC, fastbins only for chunks <= 64 bytes, larger chunks split and reused across types. Its free entries are `0x4E5920` (307 call sites + ICoreAllocator::Free `0x4E3EF0`) and the realloc core; pools with their own free lists (CAS node free list `0x5EE260`, physics pools) bypass it.
- **S3SS Mimalloc is not active as far as the files show.** `S3SS.toml` has `[patches.Mimalloc] enabled = true`, but `S3SS_LOG.txt` (2026-11-09 09:37) lists Mimalloc only under "Registered", with no install line (every other enabled patch logs "Installing" / "installed"), as docs/05 found. Either way S3SS Mimalloc detours only MSVCR80 `malloc` / `free` (docs/68), never EA's GeneralAllocator, so it cannot hand out or reuse these objects. The "0x40A00000 = a mimalloc page start" reading (I-456-9) needs an active Mimalloc and a CRT allocation; neither applies to these EA heap objects [inferred]. The build logs the runtime answer: `uaf check (install | <first verify>): MSVCR80 malloc ...; S3SS Mimalloc not active (CRT malloc untouched)` or `MAY BE ACTIVE (CRT malloc redirected)` (a jmp at the CRT entry, or no exe IAT slot pointing at it).

### 17.2 Built (`uafcheck.cpp`, test only; code and release ini `[UafCheck] Enabled=0`)

- Hook: `0x4E5920` (thiscall (ptr), `ret 4`) via allocsites.cpp `AsDetour` (chains `[Alloc]` / AllocSites jmps; installed after them and after `[CrashForensics]`, whose heap check reads the prologue at its own install). Stands down when Apex Radiance is loaded.
- Held: a block of the main heap with `MinSize` (64) <= user bytes <= `MaxSize` (4096), a plain in-use chunk (alloc.cpp's test: no direct / fastbin / debug flags, next chunk's bit 0). Its user bytes (chunk - 0x10; the next chunk's header is untouched) are filled with `0xFEEEFEEE` and the block goes into a FIFO ring with chunk size, free tick, save # and world load # (userdiag) and 4 exe return addresses (stack scan from the hook's return address, allocator wrappers `0x4E3D00`-`0x4E9000` skipped). The allocator still sees it in use. Fastbin-sized blocks are skipped by default: they are reused only at the same size until a consolidation, and every victim so far is >= 0x40 bytes. No size list: the victims' sizes are the reusing owners', not the size of the stale pointer's object, so the filter is a range [inferred].
- Eviction: over `MaxBlocks` (262144) or `MaxMB` (48) the oldest blocks (up to 8 a call) are verified and really freed through the original Free; a block larger than the whole cap passes straight to EA.
- Verify: every dword still the pattern; else `uaf: write after free: block, size, offset +X value V (float 5.0), N dword(s) changed, others +Y=..; freed T ms ago at save #S world load #W (now ...); free stack ...; found at eviction | save end | world quit | world load | DR release, thread` (first 100); the block is refilled (a second write logs again). All held blocks are verified at each save end (`UdSaveEnd`), the ts3fixes "world quit" line and each "world:" load line, with a status line `uaf: <when> (save #N, world load #W): held B block(s) M MB, oldest freed S s ago, K written now, ms; session held, freed, written (0x40A00000), passed, DR arms`. The oldest age is the detection window.
- Second free of a held block (first two dwords the pattern and the block in the ring): absorbed, `uaf: second free of held block ... absorbed` (first 20). EA would have freed it twice.
- DR combo (`DrArms` 2, `DrHoldSec` 600): the first hit (a `0x40A00000` hit replaces an earlier other one) sets the target (chunk size, offset). The next `DrArms` freed blocks of that chunk size are held aside; a helper thread puts a write watch on the offset (`WpArm`, len 4; with all four registers in use it takes a `[CasTrim] WriteTrap` slot, which WriteTrap's next set then skips), logs `uaf: DRk write watch on block ...` and refreshes threads every 10 s while armed. Any write there is a write after free: `watchpoint DRk hit n: <addr> len 4 written by eip ... (value now ...)`, thread, stack (WpVeh, 16 a slot). After the hold: disarm, verify, free.
- Counters v183 at `0x5F58`: `uafHeld`, `uafFreed`, `uafHit`, `uafF5`, `uafPass`, `uafArms` (docs/registry.md).
- Memory: held blocks <= 48 MB of already-committed heap memory (the heap grows by up to that much), ring 36 bytes a block (9 MB at 262144); `MaxMB` is cut so both stay <= 64 MB of the 32-bit space.
- Behaviour change: frees are delayed (memory above), and code that reads freed memory now reads `0xFEEEFEEE` instead of stale data: a crash with `0xFEEEFEEE` in a register names a stale reader (a finding too). No Sim behaviour change; household Sims untouched.
- Shortcuts: realloc frees through the core; other heaps and pools are not covered; frees that find our lock busy (a thread suspended by the GC, or a verify-all running) pass through to EA (`uafPass`).

Selftest `TestUafCheck` (fake chunks, fake main heap, fake Free, hook called directly): 4 held and filled, header untouched; the 5th evicts the clean oldest; a float-5 store found by the verify-all, counted and refilled; a write found at eviction; a second free absorbed; a live block starting with the pattern still held; other heap, too small, not in use, NULL pass through; byte cap; DR target, hold-aside, arm step, release with a hit.

### 17.3 Queue `float5-uaf.json` (pair float5uaf-1109)

(c) `bp-float5-uaf`: the crashed case (Baseline-BP, France x8, mem marks), release ini (CasTrim Mode 3, Detect 1, CellGuard 1) + WriteTrap 1 + UafCheck 1 (defaults). Pass = no crash, the hook in, 8 travel saves and a `uaf: save end` line each save. REPORT: every `uaf:` line, every `watchpoint DR` hit, `0xFEEEFEEE` in any crash. ~35 min (60 min cap).

Reading: a `uaf: write after free` with `0x40A00000` gives the stale object's size and field offset and the free stack (who freed it); the DR line then names the writer's eip. Clean while float-5 detectors hit elsewhere = the stale write lands outside the window (oldest age) or outside the main heap range; then widen (MinSize 0, larger window) or quarantine the pools.

## 18. Float-5 triage cycle 5: `[SlotGuard]` at the SlotComponent teardown, `[UafCheck]` Object blocks (2026-11-09; built 0.36.230, pair float5slot-1109, not run)

New site: 0x951A00 in SlotComponent clear 0x9519C0, ContainmentSlots array pointer `[comp+0x14]` = 0x40A00000 (cell Object+0xEC, 0xC mod 16 like the earlier cells), path ObjMgr_RemoveObject 0xC70150 -> DestroyById 0x939420 -> 0x935DF0 -> Object_Destruct 0x935860 -> 0x952F60 -> 0x952D20 -> 0x9519C0.

### 18.1 Was DestroyPark / P5cPark / Discourage live with RouteAsync off? [verified: source + run log]
No. `InstallDiscourage` returns before reading `DestroyPark`, `DestroyParkDx` or `DestroyApply` when `[RouteGate] Enforce=0` (discourage.cpp `if (!rgEnforce) return`); in `RtEnter` the park needs `enforce` and `DiscOnDestroy` runs only inside `if (enforce && !worker)` (rtsites.cpp RT_DESTROY); the standalone park also needs a running async job. Log `float5-bisect-...-bp-float5-rasync-off-1-0240`: `route gate closures off (Enforce=0)`, no `OK   destroy park` / `discourage deferral` line, every save line `parked 0` (52 of 52). So with RouteAsync off DestroyById ran in EA's order and the crash there also clears DestroyPark, P5cPark and the Discourage destroy gate.

### 18.2 Built (0.36.230)
- `[SlotGuard]` (slotguard.cpp, code and release ini `Enabled=0 Guard=0`): hooks 0x9519C0 (thiscall(comp), ret; before EA), 0x951F20 load (thiscall(comp; data, reset), ret 8; after EA) and 0x951AE0 clone (thiscall(comp; src), ret 4, al = 1; after EA) [verified disassembly]. Pairs {count, array} at +0x08..+0x2C. Bad = 0x40A00000, misaligned / outside user space, unreadable, or cookie != 0 with a first-element vtable outside the exe. Line `slot guard: float5 slot bad: comp X obj Y off +N val V count C (pair, reason); Object id / type key / refs; at <teardown | clear before load | clear before clone | load end | clone end> from <ret>; last checked OK N ms ago | never seen; ...; stack scan`, plus `[UafCheck]` lookups on the component, its Object and the value. `Guard=1` zeroes the pair (the teardown frees nothing; the old array leaks).
- `[UafCheck]` Object blocks [verified disassembly]: ObjectService's allocator `[svc+0x1040]` is the default ICoreAllocator 0x11DC168 (ctor 0x93A510 argument 0 from 0x93AA20), whose Free 0x4E3EF0 calls the hooked heap Free 0x4E5920 on the main heap. So Object blocks were already quarantined when <= MaxSize; the brief's "invisible" was wrong. Now tagged by the return chain (0x4E3EFD, then 0x935E1B), the Object id kept, `ObjectMaxSize` (0 = MaxSize) admits larger Object blocks; uaf lines end with `; Object block id ...`. No new detour.
- Crash fix: pair float5uaf-1109b with `UafCheck.Enabled=1` and AllocSites off died at load (AV writing 0 in memcpy, ts3native+0xC5D39, `AsDetourCore` from `ApplyUafCheck`): the trampoline page `g_asTramp` was made only by AllocSites' `AsInit`, so the prologue was copied to NULL. The bytes at 0x4E5920 were EA's (`starts 56`), so S3SS was not the cause. `AsDetourCore` now makes its own page; `ApplyUafCheck` refuses a jmp at 0x4E5920 into another module (`SKIP uaf check: EA heap Free 0x4E5920 is already patched by another module`).
- Pair ini `ts3native.ini.float5slot-1109`: branch release ini + `[SlotGuard] Enabled=1 Guard=1`, `[UafCheck] ObjectMaxSize=16384` (UafCheck Enabled stays 0; queues set it).

## 19. Float-5 triage cycle 6: `[Float5Free]` at EA Free and the SACS Walker request queue (2026-11-09; built 0.36.231, pair float5free-1109, not run)

New site (float5-split `bp-float5-split-b-1`, 0.36.229, xcpt 11.26.02): GeneralAllocator_FreeCore 0x4E51BF read `[0x40A00000-0xC]` (AV at 0x409FFFF4), eax = 0x40A00000, during world unload on travel.

### 19.1 Static [verified disassembly TS3W 1.67 unless marked]
- Stack: EA Free 0x4E5920 (ret 0x4E5945) <- EA_Free 0x4E3F70 (ret 0x4E3F84) <- 0x727E5C in `eastl::deque` dtor 0x727E10 <- 0x800C99 in SACS Walker dtor 0x800C10 <- 0x801638 Walker deleting dtor 0x801630 <- 0x8050D2 in 0x8050B0 (StateMachineDriver walker clear: vector `+0x10..+0x14` of `{key, Walker*}`, calls each Walker's vt[0](1), sets `+0x145 = 1`) <- 0x805135 (0x805120) <- 0x8051D8 <- 0x801C47; 0x94E922 / 0x94EA93 lower on the stack are SacsComponent's `Objects/SacsComponent/mDrivers` walk 0x94E8D0.
- Walker: vtable 0x102000C (no RTTI), ctor around 0x803E80 (strings `SACS/StateMachineDriver/Walker`, `.../Walker/ActorInst`, `SACS/Walker/ClipMonitor`). `+0x2C` = eastl::deque `SACS/Walker/mRequestQueue` (allocator name 0x1020238; 8-byte elements, 0x100-byte subarrays), `+0x58` = list `SACS/Walker/mMessageQueue` (0x102021C), `+0x64` / `+0x08` owned objects released through their vtables, `+0xA8..+0xB0` a buffer freed at 0x800C5F.
- Deque dtor 0x727E10 (thiscall(deque), plain ret; callers 0x7282DC, 0x800C94, 0x803F0D, 0xAA4C31): frees each nonzero pointer from the begin slot `[dq+0x14]` to the end slot `[dq+0x24]` inclusive, then the pointer array `[dq+0]`. The faulting free was one of those subarray pointers.
- From the xcpt stack dump: saved esi (deque) 0x3B2D7EEC = Walker 0x3B2D7EC0 + 0x2C (Walker 16-aligned); saved edi = the slot 0x33CDF4DC, ebx (loop bound = end slot + 4) 0x33CDF4E0, so the bad slot was the end slot, 0x33CDF4DC = 0xC mod 16 like the earlier cells, = user + 0xC of a 0x20-byte chunk. The next chunk's header at 0x33CDF4E0 reads prev-size 0x20, size 0x30 with the in-use bit clear: the pointer array's own chunk was already free at the crash [inferred from the dump; the dump is at crash time, after the store]. So the Walker held a dangling pointer array and the 0x40A00000 sat in freed (or freed and reused) memory: the same use-after-free shape as §17, now in SACS animation teardown.

### 19.2 Built (0.36.231)
- `[Float5Free]` (float5free.cpp; code and release ini `Enabled=0 Guard=0`, pair ini 1 / 1). One detour on EA Free 0x4E5920 for `[UafCheck]` and `[Float5Free]` (`UafHookInstall` in uafcheck.cpp: the first installs it, the second reuses it; `[Float5Free]` works with UafCheck off, the hook then only checks and passes on). A pointer in 0x40A00000..0x40A0FFFF, misaligned (`& 7`) / below 0x10000 / at or above 0xFFFE0000, with an unreadable chunk header (`p-0xC`), or a plain chunk whose next chunk's in-use bit is clear (a free of freed memory) logs, and Guard=1 skips that free (leak):
  `float5 free: ptr 0x%08lX (%s) caller 0x%08lX heap 0x%08lX[ (main)][; deque 0x%08lX (SACS Walker mRequestQueue, Walker 0x%08lX vt 0x%08lX)]; free tag %s (site ts3native+0x%lX); free skipped (Guard=1)|passed to EA (Guard 0); thread %lu; save #%ld, %ld s after world load #%ld; bad %ld, skipped %ld; stack scan (%d): ...`
  (the deque part only when the caller is 0x727E5C: 0x727E10's saved esi sits at hook `ra[6]`; caller = `ra[2]` behind EA_Free 0x4E3F84 / ICoreAllocator Free 0x4E3EFD, else `ra[0]`). With UafCheck on, `UafNearLog` also runs on the pointer.
- Deque check: detour on 0x727E10 (prologue `56 8B F1 8B 46 08`, all four callers): the pointer array (same test), begin / end slots inside it (`(end - array) / 4 < [dq+4]`), and every nonzero subarray pointer, before EA frees anything. Bad logs, Guard=1 skips the whole destruction (the queue leaks):
  `float5 deque: deque 0x%08lX[ (SACS Walker 0x%08lX mRequestQueue)] caller 0x%08lX: %s, value 0x%08lX at 0x%08lX (pointer array 0x%08lX size %lu, begin slot 0x%08lX, end slot 0x%08lX); destruction skipped, queue leaks (Guard=1)|passed to EA (Guard 0); thread ...; bad %ld, skipped %ld, checks %ld; stack scan (%d): ...`
  The Walker owner is named when the caller is 0x800C99. This catches the dangling pointer array itself ("chunk not in use (freed)"), not only a 0x40A00000 slot.
- Log budget: first 50 lines of each kind a session, then every 100th; counts in every line. Install line: `float5 free on (CRASH-TRIAGE, docs/539 §19): ...`. No counters (log only). All earlier detectors unchanged.
- Self-test `TestFloat5Free` (float5free-test.h): the bad-pointer reasons, the Free hook with UafCheck off at Guard 0 / 1, the Walker owner from the crash's frame, the deque cases (0x40A00000 end slot, end slot past the array, freed pointer array) and the deque hook at Guard 0 / 1.
- Pair ini `ts3native.ini.float5free-1109`: the float5slot-1109 pair ini + `[Float5Free] Enabled=1 Guard=1`.
- Next: rerun the float-5 travel job on this pair; a `float5 deque:` line names the Walker whose request queue was freed under it; then find who frees a live Walker's pointer array (deque grow 0x7xxxxx realloc path, or a Walker freed twice through 0x8050B0).

### 19.3 Range rule false positives, fixed in 0.36.233 (pair float5mono-1109b, not run)
- float5-round4 `all-off-1` (0.36.232, `Guard=1`): 26 `float5 free:` lines, every one with reason `0x40A00000 range (float 5.0)`, 26 distinct pointers 0x40A001F0..0x40A07BD0 plus 0x40A00000 itself, each freed once, all 16-byte aligned like EA user pointers, heap = the main heap, callers vary (0x57A9EB, 0x4461AA, 0x575CC7, 0xC3A250, ...), 22 at world load #0, 3 at #1, 1 at #4 [verified log]. The range test ran before the header checks, so the headers were never read; but the pattern is ordinary allocation traffic in a heap region that also held live blocks at earlier sites (0x409E089C, 0x40E65EE0, §13), and the exact 0x40A00000 free came from 0xC3A250 67 s into world load #1, not from the Walker deque. So they were false positives [inferred: pattern + range ordering; the 0.36.233 pass line below confirms it in game], and Guard=1 leaked 26 real blocks and changed the heap layout of the run.
- The 11-09 crash itself faulted reading `[0x40A00000-0xC]`: the float 5.0 had no readable chunk header. That case does not need the range test.
- Rule now (`F5fBad`): a pointer is bad when misaligned / outside user space, its chunk header is unreadable, or its plain chunk is not in use (next chunk's in-use bit clear); in 0x40A00000..0x40A0FFFF also when the plain header's size is below 0x10 (zero memory, no chunk). In the range the reason is prefixed `0x40A00000 range (float 5.0), `. A pointer in the range with an in-use header passes to EA untouched, Guard or not, and logs `float5 free: ptr 0x%08lX in the 0x40A00000 range has an in-use chunk header (size word 0x%08lX): passed to EA; ...; range passes %ld` (first 20 a session), so a float 5.0 that lands on a real block still shows up.
- Self-test: unmapped 0x40A00000 flagged (header unreadable); a region committed at 0x409F0000 with an in-use block at 0x40A07BD0 and at exactly 0x40A00000 (both pass, and the Free hook with Guard=1 hands 0x40A07BD0 to EA), a freed block at 0x40A01010 (flagged, skipped) and zero memory at 0x40A04000 (flagged, header invalid).
- Queue regexes `^float5 (free|deque): ` still match both the bad lines and the new pass line (report-only caps).

## 20. `[MonoSigGuard]`: EA reads params[0] of a zero-parameter delegate signature (2026-11-09; built 0.36.232, pair float5mono-1109, not run)

Crash: float5-round3 `all-off-1` and `all-off-2` (0.36.230 / 0.36.231, every ts3native feature section off) both died with an AV reading 0x7 at 0xF4AC64 (`movzx eax,byte [eax+6]`, eax = edi = 1), right after a save + travel, on the travel loading screen [verified log]. Not a float-5 hit: a vanilla EA path.

### 20.1 Static [verified disassembly TS3W 1.67]
- `GameUtils.SwapLoadScreen(callback)` (UI `LoadingScreenController.OnTick`, loading screens older than 2 s; delegate type `SwapLoadScreen()` with no parameters) -> IC thunk 0xEC0F20 -> 0x7F1760 stores the delegate in global 0x11D8C4C and posts 0x7F1480, which calls the script host's vtable +0x134 = ScriptHost_InvokeDelegate 0xD75FA0 with no arguments.
- 0xD75FA0 calls 0xD70F90 at 0xD75FD3 (its only caller; eax = the delegate handle, answer in al, plain `ret`, esi / edi saved). 0xD70F90: mono_get_delegate_invoke 0xEA13C0, mono_method_signature 0xE85040, then `mov edi,[esi+0x10]` (params[0]) and `call 0xF4AC60` (mono_type_get_type) **before** `cmp word [esi+4],1` (param_count) and `cmp eax,0x1D` (SZARRAY) plus the element class name check. Any param_count != 1 answers false (`0xD70FEB`: pop edi; xor al,al; pop esi; ret).
- A zero-parameter signature is 0x10 bytes, so `[sig+0x10]` is the first dword of whatever block follows it. Vanilla reads that on every SwapLoadScreen and only crashes when it is not a readable pointer (here 1). Heap layout decides which, so any config (with or without our sections) can hit it [inferred: layout].
- Existing fixes checked first: docs/24, docs/68 §1 (S3SS 1.6.3, S3PP 2.1, MonoPatcher, Apex Radiance hook lists), the S3PP 1.1 source in game-copy (lists the IC only) and the NRaas source: none touches 0xD70F90, 0xD75FA0 or SwapLoadScreen.

### 20.2 Built (0.36.232)
- `[MonoSigGuard]` (monosig.cpp; code default and release ini `Enabled=1 Log=1`: EA crash fix, seen twice with the cause in the disassembly, rare-crash rule). The 9 bytes at 0xD70FB2 (`mov edi,[esi+0x10]; push edi; call 0xF4AC60`) become a jmp to a stub (the 10 bytes after them are checked, not written): param_count == 1 runs the same three instructions and returns to 0xD70FBB; any other count jumps to EA's false tail 0xD70FEB. Same answer as vanilla in every case, minus the out-of-bounds read.
- Log: `mono sig guard: delegate sig param_count %u, params[0] 0x%08lX[ (past a 0x10-byte signature: the next block's data)], caller 0x%08lX (of ScriptHost_InvokeDelegate 0xD75FA0); not read, EA's not-one-Object[] answer (false); skips %ld`, first 20 a session then every 1000th (caller = 0xD75FA0's return address, `[ebp+0x6C]` of its frame; SwapLoadScreen's is 0x7F14B1).
- Counters v184 at 0x5F70: `msgOn`, `msgSkip` (param_count != 1 seen), `msgParamCount` (last one), `msgSpare`.
- Self-test `TestMonoSigGuard` (monosig-test.h): one parameter reads EA's way (type in eax, params[0] in edi and on the stack), zero and two parameters go to the false tail with edi / esi / ebp kept, the caller read from a fake 0xD75FA0 frame, patch refuses foreign bytes and a second patch.
- Pair ini `ts3native.ini.float5mono-1109`: the float5free-1109 pair ini + `[MonoSigGuard] Enabled=1 Log=1`.
- Next: rerun `bp-float5-r3-all-off` once on this pair: expect `mono sig guard:` lines with param_count 0 at each travel loading screen and no crash at 0xF4AC64; two clean runs before calling it fixed.

### 20.3 Log only the param_count 0 case (0.36.233, pair float5mono-1109b, not run)
- float5-round4 `all-off-1`: 124 `mono sig guard:` lines and 103,000 skips, param_count 13 / 6 / 4 / 3 only, none with param_count 0 [verified log]. For param_count > 1 vanilla reads an in-bounds params[0], calls 0xF4AC60 (pure read), then `cmp word [esi+4],1; jne 0xD70FEB`: the same "no" tail the stub jumps to [verified from the checked bytes kMsgSite at 0xD70FBB..0xD70FC4]. So the skip returns EA's own false and only the noise was wrong.
- Now every skip counts (`msgSkip`, `msgParamCount` unchanged), but lines are written only for param_count 0 (params[0] past the 0x10-byte signature, the SwapLoadScreen crash), first 20 then every 1000th of those; the line adds `(param_count 0: %ld)`. Self-test: param_count 2 counts without a line counter step, param_count 0 steps it.

## 21. Float-5 writer catch: `[Float5Sticky]`, `[Float5Watch]`, `[UafCheck]` TravelHold (2026-11-09; built 0.36.234, pair float5catch-1109, not run)

Inputs: float5-round5 all-off-1 (0.36.233) and float5-round4 all-off-2 [verified log]: `[UafCheck]` found 0x40A00000 at ONE absolute address per session, in different freed blocks: 0x3364623C (block 0x33646150 +0xEC, sizes 0x220 / 0x500, world loads #3 / #6 / #8) and 0x335A5FBC (4 hits, loads #4 / #6 / #8 / #11), main thread, at the eviction right after a save, free tag none. The 0x220 free is a transient EASTL string buffer (`0x9C1FE0` <- `0x9C0460` <- `0x9CD9E0`), so the victim blocks are bystanders. The run's crash (AV reading 0x41650000 at 0x64ED7B, Lot_Save -> ... -> portal save list `0x62AD40` -> Portal_Save `0x64E9A0`) was a live Portal whose vector begin `[portal+0x5C]` sat at 0x3364623C and held 0x40A00000 [verified: EA report registers, disassembly TS3W 1.67]. So the writer keeps one raw address across world loads and stores 5.0f there around saves [inferred]. The DR arms of §17.2 watched the hit offset of other new blocks of the same size and never fired: the target must be the absolute address.

### 21.1 `[Float5Sticky]` (f5watch.cpp; code and release ini `Enabled=0 PortalCheck=0 PortalGuard=0`, pair ini 1 / 1 / 1)
- The first 0x40A00000 address any detector reports (`[UafCheck]` write after free = block + offset, `[CasTrim]` cell / record slot / modelbuilder cell / outfit string / realm entry, `[SlotGuard]` pair, `[Float5Free]` deque slot, the `[Float5Watch]` sweep, the portal check) is remembered (lock-free, also from under the quarantine lock) and a helper thread puts a hardware write watch on it: watchpt.cpp `WpArm(addr, 4, write, F5sHit, keep)`; `keep` = no other arm takes the slot (WriteTrap's set skips non-trap slots; plain arms may take only free, WriteTrap or `[Float5Watch]` rotation slots). The helper re-applies the registers to every thread every 10 s and at each save start / world load end, and retries every 10 s while no register is free. The watch never moves; later float-5 addresses log `float5 sticky: another float-5 address 0x%08lX (%s), watch stays on 0x%08lX; save #%ld, world load #%ld` (first 20).
- Lines: `float5 sticky: armed DR%d on 0x%08lX (first float-5 at %s %lu ms ago; holds 0x%08lX now), %ld thread(s) set; kept for the session; save #%ld, world load #%ld` (`re-armed` / `NOT ARMED (no debug register free, retry in 10 s)`).
- Every write (WpVeh, on the writing thread, eip = the next instruction): `float5 sticky: write to 0x%08lX value 0x%08lX[ (float 5.0)] eip 0x%08lX (<module>+0x..[, EA function ~0x.. +0x..]) thread %lu (sim|main|other); eax .. esp ..; stack scan (%d): ...; ebp chain (%d): ...; save #%ld, world load #%ld; DR%d, writes %ld (float 5.0 %ld)`. All 0x40A00000 writes (first 1000), other values first 200 then every 100th. With `[UafCheck]` on, its own refills (0xFEEEFEEE, eip ts3native+..) show too when the address sits in a held block.
- Early scan: `float5 sticky: check at <save start|travel save start|world load> #%ld: 0x%08lX holds 0x%08lX[ (float 5.0)]; DR%ld, writes %ld (float 5.0 %ld)`.
- PortalCheck [verified disassembly TS3W 1.67]: `0x62AD40` calls Portal_Save `0x64E9A0` at `0x62AF81` (E8; thiscall (portal; stream), `ret 4`, returns al = 1 at `0x64EE98`); on al = 0 the caller clears its ok flag (`0x62AF8F`), saves no further portals and the lot save fails the game's own way. Portal: vtable `0xFED048`, ctor `0x64EEF0` zeroes the 0x38-byte element vector `+0x5C` begin / `+0x60` end / `+0x64` capacity; the save loop `0x64ECF7` walks begin..end. The call site is patched (`PatchCalls`, call target checked). Before EA: 0x40A00000 in begin / end / capacity, misaligned or non-user-space pointers, end < begin, size not a multiple of 0x38 or over 65536 elements, or an unreadable first / last element = `float5 portal: portal 0x%08lX (vtable %08lX, Portal|NOT the Portal vtable): <what>; vector begin .. end .. capacity .. at 0x%08lX; <Portal_Save not called, al = 0: this lot save fails (PortalGuard=1) | passed to EA (PortalGuard 0)>; thread ..; save #, world load #; bad, skipped, checks; stack scan; portal +0..+0x70` (first 40). PortalGuard=1 turns the AV at 0x64ED7B into a failed lot save (the save data on disk is not written by that save) [inferred from the caller's flag path; not run]. A float-5 in the vector also feeds the sticky address.

### 21.2 `[Float5Watch]` (code and release ini `Enabled=0 DrWatch=0`, pair ini 1 / 1)
- Last OK: a 16384-entry direct-mapped table (lossy) of objects a float-5 check read clean, with tick, check name, save # and world load #: save inventory (`CtInvHook`) and outfit clear (`CtRelHook` path 1) per outfit (the destructor's release removes it), SlotComponent load / clone end (`SgCheck`; teardown removes it), realm add / remove per node (notes, not swept). One-shot teardown checks (modelbuilder `0x5D82A0`, deque `0x727E10`, EA Free) have no earlier read: "never".
- Each float-5 hit: `float5 window: <site> obj 0x%08lX slot 0x%08lX bad (value 0x%08lX, float 5.0); last OK %lu ms ago at <check> (save #%ld, world load #%ld; registered %lu ms ago as <outfit|slot component|note>) | last OK never (no clean check of this object recorded); events in window: <save start|travel save start|save end|world quit|world load> #n N ms ago, ...; thread %lu (..); sim thread %lu now: eip <module+off>, stack scan (n): ... ; main thread %lu now: ...; our events in window[ (last 2 s)]: <CrashForensics OurEvents tags and counts by thread | ring off>; window lines %ld` (first 60, then every 100th). The sim and main threads are suspended for a context and a 2 KB stack copy only (no lock, heap or log while suspended), formatted after the resume. Sim thread id = `[MonoThread]`'s, else the thread of the last save start / world load line.
- Sweep (worker, every `SweepMs` 250, `BudgetUs` 4000 per sweep, round-robin): registered outfits (vtable unchanged before and after; `[+0xBC]` cells, part records `+0x14 / +0x18 / +0x38 / +0x3C / +0x5C`, the `+0x14` / `+0x38` arrays, the string object's `+0x2C..+0x34`) and SlotComponents (vtable `0x1044320`, pair pointers `+0x0C..+0x2C`) are read for exactly 0x40A00000 that is not a real in-use block (`F5fBad`). Clean = last OK "sweep"; one window line per bad slot (`sweep: outfit cell [+0xBC] entry i of n`, `sweep: outfit record r array +0x14 slot k`, `sweep: outfit record r string object [rec+0x5C] +0x2C`, `sweep: slot component +0x14 (ContainmentSlots)`, ...). Status every 60 s: `float5 watch: sweeps .. (every .. ms, budget .. us), entries .. (outfits, slot comps, notes; registered, gone), full passes .. (last .. ms over .. sweeps), per sweep mean .. us max .. us, reads .., bad finds .., window lines ..; DR arms .., writes .. (WRITER ..); sticky 0x.. DR.. writes .. (float 5.0 ..), checks ..; portal checks .. bad ..`.
- Cost [inferred, measure with the status line]: ~6 reads per SlotComponent, ~100-400 per outfit (cells + up to 64 records); with ~2000 outfits and ~10000 components a full pass is ~1M reads, a few ms of one worker core; at 4 ms a sweep every 250 ms the pass takes ~1 s (the window bound) and costs ~1.6% of one core, off the sim thread. Selftest: an empty table pass 120-280 us.
- DrWatch: candidates are SlotComponent `+0x14` at load / clone end (creation) and, per outfit first seen by the save inventory, its `[+0xBC]` cell 87 / 63 / 3 (else the first of 16) whose address is 0xC mod 16 (every bad slot so far was). For `DrWindowSec` (30) after a travel save start, world quit or world load end, every `RotateSec` (5) the newest `DrSlots` (4; half each kind first) created up to 30 s before that event get a write watch (fn arm without keep). `float5 watch: DR rotation at <event> #n +ms: k watch(es): DRk <desc> slot 0x.. (created N ms ago); ...`; hits `float5 watch: WRITER DR%d: slot 0x%08lX (<desc>, object live|object gone or moved (a write after its teardown?)) value now 0x%08lX (float 5.0) by eip 0x%08lX (<module+off>[, EA function ~..]), thread %lu (..), write %ld of this watch, armed %lu ms ago (window <event> #n), created %lu ms ago; save #, world load #; regs; ebp chain; stack scan` (`write DR%d` for other values; first 100 / 200). Watches stay until the next rotation.
- shortcut: outfits are registered at their first save inventory / clear check, not at construction (no outfit ctor hook verified yet).

### 21.3 `[UafCheck]` TravelHold (code and release ini 0, pair ini `TravelHold=1 TravelMaxMB=52`)
- From a travel save start (`UdSaveStart` travel flag) to the next world load end (ts3fixes `world:` line) the byte cap is `TravelMaxMB` (0 = MaxMB; with the ring clamped to 64 MB in all, so 52 MB at MaxBlocks 262144); the ring's MaxBlocks still applies. After the hold, normal eviction drains back to MaxMB (8 blocks a free). A hold without a world load ends after 15 min. `uaf: travel hold start at travel save start #N: blocks MinSize-MaxSize bytes freed from now to the next world load end held up to X MB (normal cap Y MB); held now ..` / `uaf: travel hold end at world load #W after S s: held B block(s) M MB (cap X MB), K freed into the hold, E evicted during it`.
- Size range: MinSize / MaxSize already accept 0..65536 (blocks under 16 user bytes do not exist on this heap); the pair keeps 64..4096, where every victim so far was (0x140..0x500).
- Every `uaf: write after free` with 0x40A00000 now also feeds `[Float5Sticky]`.

### 21.4 Selftest `TestFloat5Watch` (f5watch-test.h; lines in `%TEMP%\ts3native-f5watch-selftest.log`)
Sticky arm on a real variable with real DR hits (5.0 and another value logged), a second address ignored, plain arms cannot take the keep slot, refresh keeps it; portal vector checks and the hook at PortalGuard 0 / 1; outfit / SlotComponent registration, candidates (cell entry 3 / 87 by the 0xC rule), last-OK and event texts; sweep finds in a cell, a record array, the string object and a SlotComponent `+0x14`, one line per slot; DR rotation on a travel save start with a real WRITER hit while the sticky slot stays; vtable change unregisters; another thread's eip and stack captured; travel hold (cap, hold, drain) and the 64 MB clamp.

### 21.5 Next
Run the float-5 travel job (all-off or full features) on pair float5catch-1109 with `[UafCheck] Enabled=1`. Report every `float5 sticky:` line (the 0x40A00000 write's eip and stack name the writer), `float5 portal:`, `float5 watch: WRITER`, `float5 window:` and the `uaf: travel hold` lines. Queue regexes: `^float5 sticky: write to 0x[0-9A-F]{8} value 0x40A00000`, `^float5 sticky: armed DR\d`, `^float5 portal: `, `^float5 watch: WRITER DR\d`, `^float5 window: `.

### 21.6 DR own-write safety (2026-11-09; built 0.36.235, pair float5catch-1109b, not run)
bp-float5-catch-1 (pair float5catch-1109) crashed after world load #5: `exception 80000004 at ts3native+0xB661A` on the main thread, the `[UafCheck]` 0xFEEEFEEE fill (`UafFill`) writing an outfit cell / SlotComponent `+0x14` under a `[Float5Watch]` rotating DR. 16 earlier hits of the same kind were handled. Root cause [verified in code, inferred for the crash from the log order]: `WpDisarm` (and a `WpArm` steal, `WpTrapSet`) clears the slot's address first and sets the threads' registers afterwards (`WpApply` suspends each thread in turn), so a thread can still trap on the dropped address. `WpVeh` checked only `g_wp[i].addr`, read 0, declined the single-step, and `XcptLog` (`WpOwnsXcpt` false) logged it and EA's filter took it. `F5wRotate` disarms all its slots at each travel window, the same moment the travel hold's frees fill quarantined blocks on the main thread.
Fix (watchpt.cpp):
- A DR6 bit is ours when the thread's DRi equals the slot's address now, or one of the last 8 addresses that slot held (`WpForget` before every address change in `WpArm`, `WpTrapSet`, `WpDisarm`). A hit on a dropped address is consumed and counted (`stale`), not handed to the slot's handler. An address never armed, or a trace-flag single-step, still passes on. `WpOwnsXcpt` uses the same test.
- Every consumed hit rewrites the thread's DR0-DR3 / DR7 from the slot table before the continue, so a dropped watch is gone at once and a pass that set the thread while it was in the handler is not undone by the continue.
- Own writes: a hit with eip inside ts3native (UafCheck fill / refill, any of our stores) is counted (`own`) and not handed to `[Float5Watch]` / `[Float5Sticky]` / WriteTrap / the generic line, unless the slot now holds 0x40A00000 (the float-5 hunt must still see us). A hit inside a hit's handler (per-thread depth) is counted (`nested`) only.
- Counts in the `float5 watch:` status line and the `uaf:` verify line: `DR hits by ts3native N, on a dropped address N` (the float5 line adds `nested N`).
Selftest `TestWatchpointOwn` (watchpt-test.h): a real DR on a block `UafFill` fills (own, counted, handler not called, no crash), an own float-5 store handed on, a handler that writes its own watched dword (nested, counted), the bp-float5-catch-1 gap (slot address cleared before the pass: hit handled, DR taken off the thread, the next write not trapped), and the classifier on a made-up context (dropped address ours, unknown address and BS flag passed on).
Pair float5catch-1109b = float5catch-1109 with ts3native 0.36.235 (same pair ini, ts3fixes / ts3agent copies). Next: the float5-catch queue on this pair can turn `[Float5Watch] DrWatch` back on.

## 22. `[S3SSCompat] StaleWriteGuard`: S3SS 1.6.3's stale camera-threshold write (2026-11-09; built 0.36.236, pair s3ssguard-1109, not run)
The float-5 writer caught by §21 (`float5 sticky: write to 0x40AC7F7C value 0x40A00000 ... Sims3SettingsSetter+0xE6FFB`, world load #5; earlier `uaf: write after free` at block offset +0x4C, same value) is S3SS 1.6.3, not EA [verified: DR eip in S3SS's CRT `memcpy`, return chain below, S3SS source read for behaviour only, never copied]. S3SS has no license: we hook its binary at verified offsets and copy none of its code.
- `LotStreamingOptimizations` -> `streamingSettings` (default on) sets the live setting `Camera speed threshold` = 5.0 once and keeps its address in the patch's maintained-write list. That setting is `WorldManager+0xEC` (docs/28, docs/65; WorldManager = `[0x11ECBC4]`). On every later VariableRegistry pass (S3SS re-fires on `MT Time Step`, once per world load) `OptimizationPatch::OnSettingsRefired` (S3SS+0x28C90) calls `SafeReassert` (S3SS+0x28760) for each entry, which compares and writes the 4 bytes again (`VirtualProtect`, `memcpy`, SEH). The address is never refreshed.
- EA frees that WorldManager on travel: `[0x11ECBC4]` is cleared at `0xC6B507` (shutdown `0xC6B500`) and `0xC7E281` (before the new 0x1F0-byte allocation), and set only at the end of `WorldManager_Init 0xC671E0` (`0xC67883`) and in `WorldManager_LoadWorld` (`0xC6D0CC`) [verified disassembly]. So from the second world on, S3SS writes 5.0f into freed or reused EA heap.
- `SafeReassert` convention [verified disassembly, S3SS 1.6.3]: `ecx` = target address, `edx` = data, `[esp+4]` = size, plain `ret` (the caller's `add esp,4` at +0x28CE3), `al` = result, ignored by the caller. One caller in the loop: `E8` at S3SS+0x28CDB.
Fix (s3sscompat.cpp, `[S3SSCompat] StaleWriteGuard=1 Log=1`, code and release ini on, rare-crash rule: seen, repro, writer caught):
- The `E8` at S3SS+0x28CDB goes to `S3cThunk` (same registers and stack), which asks `S3cFix(addr, data, size)` and tail-jumps to `SafeReassert` with the answer, or returns `al=1` for a skip. A 4-byte target outside TS3W.exe's image that is not the live `[0x11ECBC4]+0xEC` is the stale camera threshold (the only 4-byte heap target in 1.6.3 [inferred from S3SS source: the other two LotStreaming targets are the exe globals `0x11ECBC0` / `0x1184328`, BradyBunchBegone writes 12 bytes]): Steam 1.67 redirects it to the live `WorldManager+0xEC` (S3SS's intent, same value, so the feature works again after travel); no WorldManager (between worlds) = skipped. Exe-global, live and other-size targets pass untouched.
- Known-buggy only: the S3SS file's sha256 must be in `kS3cBuggy` (1.6.3 release asset `69ce87ee...c13a`), and the 22-byte call window at +0x28CD0 plus the 75-byte `SafeReassert` head (relocated absolute dwords masked) must match; nothing in S3SS is written before both pass. Any other build logs `s3ss compat: stale write guard stands aside (S3SS <sha8> not in the known-buggy list)` (or `... (S3SS 1.6.3 sha <sha8>: <what> differ; call window +0x28CD0: <bytes>)`) and does nothing, so a fixed S3SS release turns the guard off by itself. S3SS not loaded at our DllMain: `s3ss compat: Sims3SettingsSetter.asi not loaded at start, stale write guard not installed`.
- EA app 1.69 (`TS3A_TABLE` build): `[0x11ECBC4]` maps to `0x1246C54` in `ts3addr-ea169.h`, but `+0xEC` is not verified there, so stale writes are skipped, never redirected. The EA branch also needs `[S3SSCompat]` in its ini allowlist.
- Log: `s3ss compat: stale write guard on (S3SS 1.6.3 sha 69ce87ee, known-buggy): SafeReassert call S3SS+0x28CDB retargeted; ...` once; per correction (first 20 a session, then every 100th) `s3ss compat: stale maintained write 0x%08lX -> 0x%08lX|skipped (value 0x%08lX, size N); WorldManager 0x%08lX; world load #W; stale N (redirected N, skipped N), calls N`.
- Not covered: S3SS's `Uninstall` (UI toggle) restores the original bytes to the same stale address through its own patch helper, not `SafeReassert` [inferred from source; not seen in our runs]. Player workaround without ts3native: `[patches.LotStreamingOptimizations] streamingSettings = false` in `S3SS.toml`.
Selftest `TestS3ssCompat` (s3sscompat-test.h): fake module with the 1.6.3 bytes and relocated absolute dwords: unknown sha and a changed call-window or `SafeReassert` byte stand aside with nothing written; known sha (either case) retargets the call to `S3cThunk`; `S3cFix` and the thunk (called as S3SS calls it): exe-global, live and 12-byte targets untouched, a stale heap target redirected to the live `WorldManager+0xEC` with the same data and size, `[0x11ECBC4]` = 0 skipped.
Pair s3ssguard-1109 = float5catch-1109b with ts3native 0.36.236 and its ini plus `[S3SSCompat] StaleWriteGuard=1 Log=1` (every float-5 detector kept, so the effect is measurable). Next: the float-5 travel job on this pair: expect `s3ss compat: stale maintained write ... -> 0x...` at world load #2 and later, and no `float5 sticky: write ... Sims3SettingsSetter+0xE6FFB` / `uaf: write after free ... value 0x40A00000`.
