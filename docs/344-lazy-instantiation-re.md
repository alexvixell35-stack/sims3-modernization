# 344. Lazy instantiation: can far objects and far Sims stay not instantiated? (2026-11-26)

Static only, nothing built or run. Steam 1.67 (`TS3W.exe` copy under `game-copy/`, read-only Ghidra project, decompile
kept under `game-copy/analysis/decomp/lazy/`) plus the decompiled script assemblies under `game-copy/decompiled/`
(local only). EA 1.69 not checked. Ledger: docs/51 §613. Handoff: `docs/handoff/lazy-inst-re-1126.md`.

This is the phase R input of docs/340 for RX-3 (instantiation path, id allocation), RX-4 (save and load of ids),
the object half of RX-2a (world-wide queries) and a draft of RX-9 (verdict per lazy stage L0-L3 of docs/340 §3.6).
It builds on docs/45 (object model), docs/50 (lifecycle), docs/25 and docs/26 (save), docs/105 §10.4.3 (load order),
docs/38 and docs/28 §5 (lot streaming). Claims taken from those docs are marked with the doc; claims read again or
read new for this doc are marked [verified] with the address or file.

> **Follow-up 2026-11-26:** Boundless Atlas docs/011 (2026-11-26, memory budget by consumer, "stream only what is heavy") keeps this verdict and narrows the work: far Sims as
> `SimDescription` only is one of the two streaming targets for a filled 4096 m world and must hold from load (the
> managed heap is never returned, §5); **objects stay resident at 4096 m**, so the object L2 pilot (§6, Z0-Z3) is
> parked. Object paging survives only as Atlas reserve R1 (far furniture and leaf decor, allow-list). New
> measurement owed here: a static read of the lot imposter allocation `0x5C32E2` (docs/215 gap G3), the largest
> world-size consumer in that table [inferred]. **Done, §9:** it is the untagged main-heap `Malloc` helper, not an
> imposter cache; no cap to build.

## 1. Verdict

| Stage (docs/340 §3.6) | Verdict | Why, in one line |
|---|---|---|
| L0: object kept, parked | **go** (docs/331, unchanged) | Sim-thread gain only; nothing here blocks it |
| L1: script object and id stay, native parts released | **no-go as a memory item** | A native object is one allocation with its components inside; no component can be released alone. EA streaming already drops the scene nodes of inactive lots. Nothing is left to strip |
| L2 objects: destroyed, record only | **no-go on EA worlds; pilot only on the WS-0 test world, allow-list of leaf classes** | Possible natively (same id can be chosen), but every save needs every object back, managed references are direct pointers, and the gain today is about 15-40 MB of which the managed part returns no address space |
| L2 Sims (docs/324 T4: `SimDescription` only) | **go, first** | EA code already treats a resident without a Sim object as a normal state; a Sim costs about 0.15 MB (docs/66 M11) plus animation clips; household and selected Sims exempt |
| L3: far lots never loaded | **no-go inside a `.sims3`** | The script state of the whole save is one graph with global indices; a lot cannot be skipped at load or copied at save. Only a world that keeps far lots outside the `.sims3` could do it (separate project) |

Short answer to the task question: **far Sims yes, far objects no** on today's worlds. Far objects cost little memory
(about 2-4.5 KB each, about 11k objects per EA world) and their sim-thread cost is removed by parking (L0) without
destroying them. Object L2 becomes worth a pilot only on a world several times larger than EA's, and then only for
classes that nothing refers to.

## 2. How objects come to exist today (question 1)

### 2.1 Two halves per object

- **Native half:** one `Sims3::Objects::Object` block, tag `Objects/Object`, from `Object_Allocate 0x9361D0`. The size is
  0x38 + 4 bytes per component after the first + every component body, aligned per component; the component
  constructors run in place inside that block [verified: decompile of `0x9361D0`, popcount of the mask, one allocator
  call, constructor per set bit]. There is no per-component allocation of the body, so a component cannot be freed
  while the object lives.
- **Managed half:** a `ScriptProxy` (ScriptCore/ScriptProxy.cs:19-21: id, target, execute type) plus its target, a
  `GameObject` subclass. The proxy is what the script host keeps in its per-class maps; the target holds the game
  state [verified: ScriptCore/Simulator.cs:311-326, ScriptProxy.cs:154-178].
- The 64-bit `ObjectGuid` is the key of both: the native id hash in `Objects/Service` (docs/45 §4) and the host maps
  that answer `Simulator.GetProxy` (docs/45 §8).

### 2.2 Create paths and where the id comes from

| Path | Chain | Id | Status |
|---|---|---|---|
| Script create (`GlobalFunctions.CreateObject*` -> `Simulator.CreateObject`) | `IC_Simulator_CreateObjectImpl 0x76D0E0` (key and override bytes only, no id argument) -> host -> `ObjectFactory_CreateFromKey 0x937F60` -> `ObjectService_CreateObject 0x93A1E0` | The host pushes 0, 0 as the id (`0xD7BAC8`, `0xD7BAC9`, register cleared at `0xD7BAA6`); `ObjectService_ReserveId 0x9399F0` then draws a random unused 64-bit id (`0x939680`) | [verified: disassembly of the call site, decompile of the four functions] |
| Stream create (world load, lot load) | `LotObjects_LoadFromStream 0xC70320` -> `ObjectService_CreateFromStream 0x939B60` (call at `0xC71067`) | The id pair is read from the lot's object stream and passed in; `ReserveId` returns it when no live object has it, and the create fails when one has | [verified: decompile of both] |
| Clone, catalog / inventory import | docs/50 rows B and D | new id | docs/50 |

**Can an object be created with a chosen id?** Natively yes: `0x93A1E0` and both factories (`0x937F60`, `0x9376D0`) take
the id as their last two arguments and only the script-create caller passes zero [verified]. From managed code no:
no script call carries an id for a game object. `Simulator.AddObjectWithId` / `AddObjectPostLoad`
(SimIFace/Simulator.cs:402-410) register a task or a proxy under a given id but create no native object
[verified, managed].

### 2.3 Managed side of a create

`ScriptProxy.CreateLogic(className)` builds the target, `SetLogic` calls `Init(postLoad)`; `GameObject.Init`
(GameObject.cs:6124-6199) is sealed and runs: product lookup, `OnCreation` or `OnLoad`, `LotManager.PlaceObjectOnLot`,
`OnStartup`, then `OnLoad` / `OnStartup` of every object component, then owner-lot update [verified]. `Destroy`
(ScriptObject.cs:78-82, GameObject.cs:4350-4377) removes the object from its inventory, destroys its slot children,
removes it from the object manager and calls the native destroy [verified].

### 2.4 World load and what lot streaming defers

- At world load **every lot's objects are created**, native and managed: per lot the routing data, then the object
  layer (docs/105 §10.4.3 steps 1-3); the script state of the whole save is read in one pass and then bound per object
  (steps 4-5) [verified there].
- Per object the lot stream's script component record carries the object's **index into the save-wide script state**
  and a second index for a saved task stack; `ScriptComponent_LoadFromStream 0x950280` queues (id, index, stack index)
  in the host through `0x94FCE0`, or (id, class name) through `0x9501B0` when the record has no index or a global
  "ignore saved script state" byte (`0x11E90F4`) is set [verified: decompile; the meaning of the byte is inferred from
  the branch].
- A stream object that cannot be created posts script message `0x7E44AD3` with the lot and object ids
  [verified: `0xC70320`].
- **EA lot streaming defers only the scene.** `WorldManager_ScoreAndStreamLots 0xC6C290` -> `Lot_SetActive 0xAC20E0` ->
  `Lot_AddLotObjectsToScene 0xAC1130` / remove `0xAB4C00` add and remove scene nodes; inactive lots keep every native
  object and every script object from `Lot::Load` until world unload (docs/38 C7, docs/320 §5.1, docs/50 row F)
  [verified there]. S3SS's lot streaming patches throttle that same scene add (docs/03).
- Models, textures and animation are fetched through the resource system and shared per catalog entry; they are held
  by the ResourceCache budget, which stays near 400 MB whatever the budget setting (docs/66 §1). They are not owned
  per object.

So today: script object, native object and id for every object in the world exist from load to unload. Only scene
presence and render resources follow the camera.

## 3. Who would notice a missing object (question 2)

### 3.1 Choke points [verified]

| Choke point | What funnels through it | Evidence |
|---|---|---|
| The ten `Query_*` script calls (ScriptCore/Queries.cs:10-37), entries `0x749610`-`0x74A0C0` | Every `Queries.GetObjects` / `CountObjects` overload in Sims3.Gameplay/Queries.cs:29-124 and `Lot.GetObjects` | binding table names; managed wrappers |
| Host method behind them, `0xD798F0` (host vtable `+0xC0`) | World-wide form: walks the **script host's** per-class object maps (`+0x1658`, `+0x1678`) and filters by managed class. Lot and range forms: take a native id list and look each id up in the host map (`0xD726F0`) | decompile of `0x749630` and `0xD798F0` |
| `Simulator_GetTaskImpl 0x76CA60` | id -> proxy: `Simulator.GetProxy`, `ObjectGuid.ObjectFromId`, `GlobalFunctions.ConvertGuidToObject`, `ScriptObject.HasBeenDestroyed` | Simulator.cs:427-439, ScriptObject.cs:26 |
| `ObjectId_Lookup 0x939100` | every native call that takes an object id (222 script calls, docs/45) incl. `Objects_IsValid` | docs/45 §4 |

World-wide queries enumerate the host maps, not the native object service. An object whose proxy stays (L0, and the
L1 idea) is still returned by every query; an object without a proxy (L2) is invisible to all of them at once.

### 3.2 Census of EA script call sites [verified: grep counts over Sims3GameplaySystems, Sims3GameplayObjects, Sims3StoreObjects, UI]

| Query | Sites | Asks | L2 needs |
|---|---|---|---|
| `lot.GetObjects<T>` and friends on a `Lot` | 701 (+ 96 per room, 168 counts) | iterate one lot | materialise that lot (lot id is an argument of the native call) |
| `Queries.GetObjects<T>(lot ...)` | 343 | iterate one lot | same |
| `Queries.GetObjects<T>()` world-wide | 297, about 200 distinct `T` | iterate the world | materialise every record assignable to `T` |
| `Queries.GetObjects<T>(position, radius)` | 172 | physics range query | nothing if the centre is in a live area; the native range query only sees native objects |
| `Queries.CountObjects<T>` | 101 | count | answer from the record table |
| id -> object (`ObjectFromId`, `ConvertGuidToObject`, `GameObject.GetObject`) | 532 (+ 72 `Simulator.GetProxy`) | resolve one id | materialise on a miss that is in the record table |
| `HasBeenDestroyed` | 371 | proxy present? | must stay "alive" for a record, or cleanup code deletes references |
| `Objects.IsValid` | 148 | native object present? | same |
| `LotManager.AllLots*`, `GetLot`, `Actors` | 154 / 312 / 75 | lots and Sims | none (lots stay) |
| `AddAlarm*` | 920 | timers, most owned by an object | owner must exist when the timer fires (docs/331 holds them) |
| `EventTracker.AddListener` | 320 | listeners, delegates bound to objects | delegate keeps the managed object alive |
| `RabbitHole.GetRabbitHolesOfType` and static lists | 24 | rabbit holes | never lazy |
| `SimDescription.Find*` / `GetAll*` | 788 | residents | none (descriptions stay; this is why Sim L2 works) |

Most frequent world-wide types: `Sim` 30, `Urnstone` 17, `RabbitHole` 10, `WorldBuilderDataObject` 9, `Subway` 9,
`UnchartedIslandMarker` 7, `GameObject` 6, school / police / mausoleum / city hall interfaces 3-5 each, `Lot` 3,
`Household` 3 (lots and households are script objects in the same host maps).

### 3.3 Ranking by frequency and importance

1. **Lot-scoped and range queries** are the hot ones in play (`Query_GetObjectsInRange` 2.6% and `Query_GetObjectsOnLot`
   1.8% of sim-thread self time, docs/04). They are cheap to serve for L2 because the lot is known: a lot is either
   live or a record set, never mixed.
2. **Autonomy candidate scoring** reads lot object lists and each object's interaction list
   (`GameObject.mInteractions`, `mCurrentCommodityInteractionMap`); it runs for Sims on live lots, so it follows rule 1.
   Meta-autonomy picks a *lot* first (venue type, docs/65), not objects; lots stay. [inferred from docs/65, docs/121;
   not re-read here]
3. **World-wide by type** (297 sites): mostly rare paths (load fix-ups, cheats, map tags, seasons, opportunities). Six
   sites ask for `GameObject` itself and so would materialise the world; they are load-time validators
   (`GameObject.cs:6240-6247` is one) plus our own sweeps (docs/333) and NRaas-style cleanup mods.
4. **Id resolution** (532 sites) is the common way gameplay refers to an object across time; a facade on
   `0x76CA60` covers it.
5. **Hard managed references** are not queries and have no choke point: inventories, `mReferenceList`,
   `mActorsUsingMe`, `mRoutingReferenceList` (GameObject.cs), 30 collection fields typed with `GameObject` in
   Sims3GameplaySystems, delegates of alarms and listeners, slot parents, `Lot` object caches (`Lot.cs:985`), reaction
   broadcasters. A destroyed object that is still referenced stays in memory and is written into the next save through
   the reference walk (docs/26 §1.3).
6. **Story progression, services, careers, school** work on households, `SimDescription`s and rabbit holes, not on
   lot clutter [inferred from the type list above; RX-6 owns the Sim-object readers].

**Proxy or stub needed:** none at L0. At L2 a facade on the two host entries (`0xD798F0`, `0x76CA60`) plus the lot
activation hook answers rows 1-4 exactly by materialising; row 5 cannot be answered by a facade, only avoided by class
choice.

## 4. Save and load (question 3)

- **Ids are persisted and restored.** The lot object list (resource `0x4D1A5589`, `Lot_SaveObjectGroup 0xC6F520`,
  docs/25, docs/26 §1.2) stores each object's id; load passes it to `ReserveId` (§2.2). So an object has the same id
  after save and load [verified]. This answers docs/51 I-611-4: ids are stable across a reload.
- **Script state is one stream for the whole save.** One `PersistWriter` per save (LoadSaveManager.cs:396-401); per lot
  `SaveObjectGroup` and per object `SaveObject` return a global index into it (LoadSaveManager.cs:404-443); the group
  record also lists object ids (ScriptObjectGroup.cs `Save`: `mObjectGuids`); references between objects are indices in
  that one table (PersistWriter.cs:446-498) [verified; same reading as docs/26 §1.3].
- **Every lot is written from live memory on every save** (`World_Save 0xC6D460`, docs/26 §1.2). Unchanged *world*
  resources are copied from the base package, lots are not.
- `ObjectService_ExportObjects 0x939870` writes an arbitrary id vector to a stream; its callers are the lot save helper
  `0xABC880` and `IC_DownloadContent_StoreInventoryImpl` [verified: decompile and xrefs]. Together with `0x939B60` this
  is EA's own "objects out, same objects back with the same ids" pair, natively.

**Does a never-instantiated object round-trip unchanged? No.** An object that is not in memory at save time is in
neither the lot object list nor the script stream, so the save simply does not contain it. Consequences:

1. L2 must **materialise every record before every save** (the rule docs/331 already has for timers). Then the
   save holds exactly EA's content and a player who removes the mod loads a normal save. No data of ours goes into the
   `.sims3`; L2 needs no sidecar at all (after a load everything is instantiated by EA and we strip again).
2. That flush is a create burst and a memory peak at the moment the game is closest to Error 12 (docs/215: the save
   already maps a 86.8 MB view). On EA worlds this alone cancels the gain.
3. L3 (skip a lot at load, copy its resources at save) cannot keep the script state: the lot record and each object's
   script component record hold global indices into the one script stream (§2.4), which is rewritten on every save.
   Splitting it would change the save format, which the no-mod-data-in-saves rule forbids.
4. Managed identity: after destroy and re-create the object is a new managed instance. Holders from §3.3 row 5 keep the
   old instance; it then reports `HasBeenDestroyed`, and EA code, our dead-reference sweep (docs/330) and cleanup mods
   remove it from wherever it sat. For a household-referenced object that is a visible change, so those objects are
   never lazy.

## 5. Memory per object (question 4)

All [inferred from verified structure sizes]; no measurement exists (docs/190 row 31: "unknown").

| Part | Bytes | Basis |
|---|---|---|
| Native block, typical decor (Transform 0x40, Model 0x90, Script 0x28, Footprint 0x78, Slot 0x68, Lighting 0x28, VisualState 0x1C) | ~0.6 KB | component sizes docs/45 §3 + header, `0x9361D0` |
| Native side data (transform parents, model compositor array, slot lists, footprint polygons, routing footprint rows, id hash link) | ~0.3-1 KB | tags in docs/45 §3; sizes not read |
| Proxy + host map nodes | ~0.1 KB | ScriptProxy fields; map node size not read |
| `GameObject` base: about 22 instance fields | ~0.1 KB | GameObject.cs field list |
| Interaction list, components, class state | ~0.5-2.5 KB | `mInteractions` holds one pair object per interaction |
| **Per object** | **~2-4.5 KB** | |

World totals: BP / IP / SV hold 10.9k / 10.8k / 11.5k objects (docs/333 [measured]), so **all objects of a world are
about 22-50 MB**, the far share (lots beyond 200 m, no household link) perhaps 15-40 MB. Cross-check: the whole
script-state stream of a save is 5.5-8.5 MB on disk for 170k-300k records, Sims and households included (docs/123).

What that buys in address space, the only measure that counts (memory goals 2026-10-26):

- The managed half lives in the Boehm heap, which is taken as one 352 MB block at start, grows in 32 MB steps and is
  never returned; 151 MB of it was free at 6 minutes (docs/66 §3.3). Freeing managed objects on an EA world returns
  **zero** address space. It only delays the next 32 MB step.
- The native half (about 10-18 MB for all objects) goes back to EA's general allocator cores, which are shared with
  everything else; it lowers fragmentation pressure a little and the largest free block not at all in a way we could
  measure against run-to-run spread (IP ranged 1,359-1,704 MB, docs/320 §4.2).
- A typical big EA world therefore saves **under 20 MB native, about 0 MB largest free block**. A 4x-area world with
  4x objects: about 60-160 MB of objects, inside the +150-400 MB Boehm growth docs/320 §4.3 expects; there L2 could hold
  back two to five 32 MB heap steps.
- Sims for comparison: about 0.15 MB each (docs/66 M11) plus their share of the 48-112 MB animation clip cores that
  grow with Sims (docs/320 §4.1). One far Sim is worth 30-70 objects.

Sim-thread time: parked objects (docs/331) already run nothing. What is left is linear work of world-wide walks: our
own sweeps at 3.65 us per object (docs/333) and EA's rare world-wide queries. Skipping parked objects in our own
walks gets that without destroying anything.

## 6. Phased design (question 5)

### 6.1 Order

| Phase | What | Gate |
|---|---|---|
| Z0 | **Measure** (docs/340 LZ-1): per class: object count, native block bytes (mask -> descriptor sizes), managed bytes (heap census by class), band (docs/333 M-1 bands), inbound-reference count from a heap walk; Boehm free and largest free block before / after a forced destroy of one far lot on a test save | no code depends on §5's estimates before this |
| Z1 | **Far Sims as `SimDescription` only** (docs/324 T4, docs/340 FS-16..19) through EA's own `Sim.Destroy` and `SimDescription.Instantiate` (SimDescription.cs:4709-4797). Never household, selected Sim, a Sim a household Sim interacts with, or unknown scope | RX-6 reader table |
| Z2 | **Skip parked objects in our own world walks** (docs/333 LC packages). No object changes | none |
| Z3 | **Object L2 pilot, WS-0 world only, default off, never in the experimental build**: allow-list of leaf classes, whole far lots only | Z0 shows far objects >= 60 MB native + managed on that world, and the allow-list covers >= 60% of it |
| Z4 | Decide from Z3's largest-free-block A/B. L3 stays out | Rodney's sign-off (RX-9) |

### 6.2 Shape of the L2 record and the facade (Z3)

- **Record, in RAM only:** catalog resource key, script class, id, lot id, transform, level, design preset, and the
  bytes `0x939870` writes for the object. No sidecar: every record is materialised before a save, a travel, a world
  switch and quit.
- **Go-far:** a whole lot at a time, only when every object on it is on the allow-list or stays live. Managed
  `Destroy` per object (EA's path), after the record is taken.
- **Go-live (materialise):** EA create for the class at the recorded transform. To keep the id the create must enter
  at `0x937F60` with the id pair instead of zero (thiscall, four stack arguments; argument count and `ret` to be read
  in the disassembly before any call, AGENTS.md) or go through `0x939B60` with the recorded bytes. If the
  first attempt fails, fall back to a script create with a new id (allowed only because leaf classes have no id
  holders).
- **Triggers that materialise a lot:** a household Sim, the selected Sim or the camera within the promotion radius of
  docs/331; the lot becoming active (`Lot_SetActive 0xAC20E0`); a lot-scoped query naming that lot (`0xD798F0` with a
  lot id list is empty for a record lot, so hook the managed `Lot` accessors or the lot-id form of the script call); a
  world-wide query whose `T` matches a recorded class; an id miss in `0x76CA60` that is in the record table; build /
  buy, Edit Town, save, travel.
- **Thrash guard** as docs/340 §3.6: a lot materialised twice by the same site is pinned live and logged.
- Hook sites `0xD798F0` and `0x76CA60` are also AutShadow / worker read points (docs/125, docs/333); run
  `node tools/hook-sites.ts --check` and read docs/68 before any hook there. Both run on the sim thread only when
  called from script; the worker proxies them (docs/333), so a facade must refuse worker context.

### 6.3 Classes

- **First (leaf candidates, to be proven per class in Z0):** decor with no script state beyond `GameObject` defaults:
  sculptures and paintings without components, plants that are not harvestable, rugs, curtains, fences and columns
  that are script objects, clutter. Condition per class: no fields of its own that are persisted, no object component,
  no alarm, no listener, no slot that holds another object, not a routing portal, no `ISaveScriptObjectStack`.
- **Later, with a computed model (docs/331 classes):** harvestables, spawners, fish tanks; only if Z3 pays.
- **Never:** anything on the household's home lot, the active lot, a lot a household Sim is on, visits or has a route
  to; household and Sim inventories (inventory objects have native objects too); anything a household Sim owns, uses,
  reserves or stole (`mReferenceList`, `mActorsUsingMe`, `GameObjectStolenFrom`); rabbit holes; urnstones
  and mausoleums (they carry dead Sims); doors, stairs, elevators, subways and other routing portals (the routing data
  is loaded before the objects and refers to them, docs/105); vehicles; `WorldBuilderDataObject`, map-tag and marker
  objects; lot and household script objects; objects on the world lot; objects with a saved task stack; any class from
  a mod assembly; any object of unknown scope.

### 6.4 Kill criteria

1. Z0: far objects under 60 MB on the WS-0 world, or the allow-list under 60% of the far bytes -> object L2 stops.
2. Z3: largest free block after 30 minutes not better than the off arm by more than the run-to-run spread -> stop.
3. Any save from the on arm that differs from the off arm's save in object count, ids of non-leaf objects or script
   record count after a flush -> stop (save change matrix).
4. The pre-save flush adds more than 2 s or lowers the largest free block during the save below the off arm -> stop.
5. `lazyThrash` pins more than 20% of far lots in an hour of play -> stop.
6. Any household-visible difference (object missing, reset, moved, changed id that something held) -> stop.
7. A mod in the compat set (docs/328) that walks `GameObject` world-wide on a timer (NRaas ErrorTrap, Overwatch) makes
   the record set empty in practice -> stand down while that assembly is loaded.

## 7. Open questions

1. No measured bytes per object or per class (Z0). §5 is an estimate.
2. The native drain of the pending (id, index) list and the caller of `BindLoadedObject` are still not traced
   (docs/50 §8, docs/105). Needed only if a go-live should rebind a kept managed object instead of creating a new one.
3. Meaning of the byte at `0x11E90F4` (script state ignored at stream load) and who sets it; it may be EA's lot-import
   switch and would be the natural entry for a sidecar-backed world.
4. `0x937F60` calling convention at instruction level, and whether the two factories differ in more than the key
   lookup.
5. Which native side data an object owns (model compositor arrays, footprint rows): sizes not read.
6. RX-2b: the same census over the top-100 mods; NRaas world-wide walks (criterion 7).
7. EA 1.69 addresses for everything in §2-§4.
8. Whether any EA world-wide query runs on a timer in steady play (static frequency per site not done; the 297 sites
   are classified by type only).

## 8. Behaviour on a 4x or streamed world

Z1 and Z2 scale with nearby Sims and lots. Object L2 is the only item here whose value grows with world size; it is
built to whole-lot granularity so a lot unload hook (docs/321 lane A) can drop its records.

## 9. LZ-0: what `0x5C32E2` is (docs/215 gap G3, 2026-11-26)

Static read of Steam 1.67 `game-copy/bin/TS3W.exe` with Capstone (call and immediate scans over `.text`, function
bounds and names from the Ghidra export), plus a re-read of the three `alloc-sites4` logs of 2026-10-26 that docs/215
row 3c came from. Nothing built or run. EA 1.69 not checked. Ledger: docs/51 §629.

### 9.1 Answer

- **`0x5C32E2` is not an imposter site.** It is the return address inside `0x5C32D0`, a six-instruction helper:
  untagged `Malloc 0x4E69D0` on the main EA heap `[0x11CB864]`, flags 0 [verified]. No imposter texture and no lot
  model is allocated through it. "Lot imposter block" in docs/215 came from the address range of the one sampled
  caller, `0xAE00C5`, which is the add-core call of the lot message pool `0x11EB590` (docs/541) [verified].
- **The 48-80 MB row is not owned bytes.** In the logs it is `k=virtual at=0x004E4E87 from=0x005C32E2`, `n=3` (CC job,
  48 MB) and `n=5` (Buy/Build job, 80 MB): three and five 16 MB cores of the main heap whose creation was triggered
  by a request that came through `0x5C32D0`. Each core then serves every user of the main heap. The count is the same
  in the first and the last sample of both jobs and nothing was freed, so the cores date from the world load, not
  from Buy cycles; "grows with lots shown / Buy cycles" in docs/215 is a difference between two runs [measured, logs].
- **The only owner measured behind it** is the lot message pool: `k=heap at=0x005C32E2 from=0x00AE00C5`, 19.4 -> 25.9 MB
  in 768 blocks (travel job) [measured, log]; 4.45 MiB on kit5 and 20.2 MiB on Bridgeport in the docs/541 census, with
  99% of the slots free after the load [measured, docs/541].
- **Bound by lots loaded, not lots shown** [inferred]: the pool grows to the largest number of lot messages alive at
  once, which is the world-load burst (docs/541 §0), and it has 270 allocating callers in the lot code. Camera and
  LOD do not enter. The kit5 / Bridgeport ratio (132 / 599 cores) follows world content.
- **No cap, no LRU, no ini key in EA code:** the fixed pool adds cores and frees them only at exit (docs/541 §1);
  main heap cores are released only at heap shutdown (docs/201 §12.2). No S3SS setting on either is listed in
  docs/68; S3PP only changes the core size immediates (16 -> 4 MB, docs/201 §12.2), which makes each triggered core
  smaller. Ours: `[LotPool] Trim=1` (docs/541, built, off by default) gives the free pool cores back to the main heap.
- **Verdict: the imposter cap is not a build.** There is no imposter cache behind this address to cap or evict. The
  part that scales with world size is already covered by `[LotPool] Trim`; what is left is to measure the pool peak
  on the first filled 4096 m load (counters `lp*`, docs/541) and decide its default there.

### 9.2 Who allocates through `0x5C32D0` [verified: scans]

| Group | Sites | What |
|---|---|---|
| Static fixed pools, built by the pool constructor `0x4E43D0` (it stores `0x5C32D0` / `0x4E8590` as the default alloc / free callbacks; arguments of 0 keep them, `0x4E4350`) | 12 | see §9.3 |
| Per-instance fixed pools that pass the pair themselves | 8 (`0x5C3ADE`, `0x5FEE35` .. `0x5FFA75`) | CAS preset and texture compositor op lists, items of 8-24 bytes |
| Callback pairs handed to a library | `0x69234A` (text renderer set-up, with a realloc `0x6916A0`), `0x9E6043` (in-game ad SDK object) | library heaps |
| Direct calls | 202 in 98 functions | 150 in `0x9F7810`-`0xA08562` (web service response parsing), 45 in `0xF612D3`-`0xF7E756` (network and UPnP code), 3 audio (`0x8A65C9`, `0x8A6EB9`, `0x8A7029`, the add-core paths of three pools below), `0x4E2C12`, `0x4E2D61` (thread create), `0x502135`, `0x502145` |

Nothing in the lot imposter builder (`LotLODCreator`, docs/44 §6) is on this list. Its textures are D3D resources and
its models go through tagged allocations and the resource system (docs/44, docs/278).

### 9.3 The 12 static pools [verified: constructor arguments]

Core bytes = (items per core + 2) x item size + 12 (`0x4E42F0`, docs/541 §1).

| Pool | Item bytes | Core bytes | Allocator | Owner |
|---|---|---|---|---|
| `0x11CD8B4` | 0x19B30 | 13,684,332 (13.05 MiB) | `0x5748A0`, 21 callers | animation rig poses (docs/22) |
| `0x11EB590` | 0x110 | 35,372 | `0xAE00A0`, 270 callers | lot messages (docs/541) |
| `0x11DA028` | 0x388 | 117,532 | `0x8A6FF0` | audio |
| `0x11D6718` | 0x80 | 16,652 | `0x76ADB0`, 45 callers | script-side message pool [owner inferred from the symbol name] |
| `0x11D9FD0`, `0x11D9FFC`, `0x11D9D44` | 0x70, 0x18, 0x14 | 14,572 / 3,132 / 2,612 | `0x8A6590`, `0x8A6E80`, `0x8A0A30` | audio |
| `0x11DA700`, `0x11DA7D0`, `0x11DA9B0` | 0x30, 0xB0, 0x64 | 6,252 / 22,892 / 13,012 | inline in `0x8D9D20`-`0x8FFCD0` | effects (Swarm) [owner inferred from the symbol names] |
| `0x11E9118` | 0x60 | 12,492 | lock taken at 50+ sites in routing, footprint and physics code | routing / physics messages [inferred] |
| `0x11D3F7C` | 0x40 | 4,236 (0x40 items per core) | `0x7425A0`, 24 callers | not named |

- **Rig pose pool, new:** one core is 13.05 MiB for 130 poses and is never freed. The main heap sends a request of
  128 KB or more that does not fit its top block to a direct `VirtualAlloc` block (threshold `+0x494` = 0x20000 from
  `0x4E584B`, switch `+0x46D` = 1 from `0x4E5B3F`, branch at `0x4E6194`; the audio and Mono heaps switch it off,
  docs/201 §12.2) [verified; an indirect option call on the main heap is not excluded]. So a rig pose core is either
  carved from a fresh 16 MB core or is its own 13 MiB region. No `at=0x004E5130 from=0x005C32E2` row is in the top
  rows of the travel log, so the live count is probably one core [inferred]. It follows Sims animating at once, not
  lots.
- Every other pool core is below 128 KB and comes from the 16 MB cores, which is why these requests can trigger a
  core and show as the `k=virtual` row.

### 9.4 Where a cap or evict would go

- **Imposters:** nowhere at this address. The EA switch for dropping far imposters is the SGR key
  `AllowImpostorUnload` (docs/581 §2.3), stored at `+0x396` by `0xAE38A1`. A scan for byte accesses with that
  displacement finds that store and one store in an unrelated class (`0x48A6A3`), no reader, so the key looks dead
  in 1.67 [inferred: an access through another base offset would not match]. Imposter memory itself was not
  measured here; it sits in the resource cache and renderer rows (docs/215 rows 2 and 3a, Atlas docs/011 row 20).
- **Lot message pool:** the hook already exists (`lptrim.cpp`, under the pool mutex `0x11EB5B0`, after each world
  load). A hard cap at the add-core call `0xAE00C0` is not possible without back-pressure: the 270 callers get a
  null slot when add-core fails [verified pop path; callers not read]. A trimmed core costs one 35 KB `Malloc` to add
  again: no rebuild, no hitch.
- **Main heap cores:** never returned (docs/201 §12.2); Apex and S3PP own that area (docs/68).

### 9.5 Megabytes [inferred unless marked]

| | 2048 m | 4096 m, four towns | 8192 m, two to three towns (Atlas content budget) | 8192 m, sixteen towns |
|---|---|---|---|---|
| Saved by an imposter cap at this site | 0 | 0 | 0 | 0 |
| Lot message pool peak at load (linear in world content from the Bridgeport value) | 20-26 [measured] | 80-104 | 40-78 | 320-415 |
| Given back to the main heap by `[LotPool] Trim` (about 99% of the peak) | 19-25 | 78-102 | 39-76 | 315-410 |

The trim does not lower the address space at once: the freed blocks stay in the main heap and later requests reuse
them, so the heap grows less afterwards (docs/541 §0). The peak during the load is unchanged.

### 9.6 Open

- 2026-11-26: the main heap's cores, free bytes and largest free run can now be read live (docs/362 §2: `ts3native_mainheap`, `[MemDiag] MainHeap`, counters `mhc*`; built, not run).

1. Pool peak on a filled 4096 m world (first load of the test world, `lp*` counters): is it linear, and does the
   load drain messages between lots?
2. Which request triggers each main heap core at load: `[MemDiag] AllocSitesOwners` on that load (Atlas M2).
3. Live rig pose cores (one read of the core list at `0x11CD8C0`).
4. Readers of `AllowImpostorUnload`, and bytes per shown imposter (not on this path).

*Correction 2026-11-26 (Boundless Atlas `docs/021-s1-imposter-fog-camera.md` §5, §11 item 3): `AllowImpostorUnload` has a reader. `LotRenderer+0x396` = `imposter+0x26` is read in `0xAD9F80` and `0xADA2B0` and passed as the third argument of the model setter `0x6F4860` [verified]. It is a load-mode switch (False: model loaded at once; True: load deferred), not a distance unload.*
5. EA 1.69 addresses.
