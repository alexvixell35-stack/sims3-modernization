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
