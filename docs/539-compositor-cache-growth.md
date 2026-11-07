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
