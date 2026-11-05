# 67. Crash triage: the 2026-10-14 Swarm wild call, the 2026-10-15 ModelBuilder teardown, the 2026-10-18 lighting room hash (§5), the 2026-10-19 lot-0 footprint remove (§6), the 2026-10-19 Frida handler call to NULL beside the int3 sampler (§7) the 2026-10-19 A\* node on a free list (§8), the 2026-10-19 shared empty hash bucket (§9) and the 2026-10-19 room list free and orphaned EA heap lock (§10)

Static only: Ghidra headless on a private copy of the UI4 project (`game-copy/ghidra-crash`, symbols CSV imported first), the two EA crash reports (`<Documents>/Electronic Arts/The Sims 3/xcpt <machine> 26-10-14 11.58.36.txt` and `... 26-10-15 15.38.26.txt`; no `.mdmp` exists for either, only for 2026-10-11), `crash-ledger.ts triage`, a byte scan of the installed ASI files, and a read of `native/ts3native/alloc.cpp` and the agent's `TravelHandlers.cs`. The game was not run. Times are UTC; the report file names keep the local clock.

Tags: **[verified]** = read in the disassembly, the crash report, the PE header or our source; **[inferred]** = reasoning, still to confirm.

## 1. Summary

| | Crash A (2026-10-14 03:58 UTC) | Crash B (2026-10-15 07:38 UTC) |
|---|---|---|
| Report | ACCESS_VIOLATION writing `0x00694690` (TS3W image, `PAGE_EXECUTE_READ`), EIP `0x080469DB` "unknown module" | ACCESS_VIOLATION reading `0x0000000C` at `0x005CEB9A` |
| Thread | main (frame loop `0x59ED59` -> `0x71E640` Swarm service) [verified stack] | main (`0x59ED59` -> `SimService_Update 0x5F0E50`) [verified stack] |
| Where | `Swarm_EntryOwner_Reset 0x930380` -> `Swarm_ReleaseEntryRange 0x91A440` -> virtual Release on a dead object -> wild call into heap | `SimService_ReleaseRequestRefs 0x5F0820` -> `RefCounted_AtomicRelease 0x692720` -> `ModelBuilder_DeletingDtor 0x5DE360` -> `ModelBuilder_Dtor 0x5DC450` -> `ModelBuilder_PartRecord_Dtor 0x5CEB60` |
| What is bad | the object at entry `+0x38` holds text (`<NoText></`), not vtable pointers: use after free | a part record's inline ref list holds `0x40A00000` (float 5.0) instead of a pointer; its "vtable" is 8 |
| Mod or our code patching the path | none [verified for ts3native source; byte scan for the others] | none [same] |
| Active change set | `5fa9c931fe86`, **ts3native `[Alloc]` on**, run right after a Frida `--taskstall` run | `365b9c27d8ba`, matrix-tested asi, `[Alloc]` off [inferred from PLAN; current ini `Enabled=0`] |
| Lead | `[Alloc]` per-thread cache: a double free becomes two live owners; same-thread back-to-back double free makes a self-looping bin [verified code read] | CAS model build released on main while its records were already torn or reused; shared refs from sim-thread ThumbnailManager/CAS internal calls [inferred] |

`0x694690` is **not** patched code and nobody hooks it. It is the address of a small device callback thunk (`mov ecx,[esp+4]; jmp 0x6945C0`) that `FrameDb_RegisterDeviceCallback 0x6946A0` stores in the graphics device's callback list [verified]. The value `0x694690` therefore lives in heap data; garbage code that took that dword as a pointer and wrote through it produced the "write to code page" [inferred]. The PLAN note ("check which mod patches that block") can be closed: no mod patches there.

## 2. Crash A: wild call from the Swarm entry release

### 2.1 Stack, decoded [verified: report + disassembly]

| Return address | Function | What it does at that point |
|---|---|---|
| `0x59ED59` | main loop | |
| `0x71E6D2` | `0x71E640` (Swarm service range `0x719000-0x722000`, docs/44 §7) | `call [[this+0x1C]]+0x48` = `0x9160B0` |
| `0x9162EB` | `0x9160B0` (Swarm engine, next to `Swarm_ApplyQuality 0x9164F0`) | |
| `0x91C3D1` | `0x91BAB0` | |
| `0x91A84B` | `0x91A7C0` | walks a pending list, calls slot +4 of the secondary base = `0x930380` |
| `0x930488` | `Swarm_EntryOwner_Reset 0x930380` | releases `+0x22C`/`+0x234`, calls virtual +4 on every 0x58-byte entry's `+0x38`, then `Swarm_ReleaseEntryRange(begin,end)` on the vector at `+0x21C` |
| `0x91A46E` | `Swarm_ReleaseEntryRange 0x91A440` | per entry: `EA_Free(+0x48)`, then `call [[+0x38]]+0x3C` |
| (stale `0x9158AD` at `esp-4`) | `RefCounted_ReleaseAt8 0x915890` | `--[obj+8]`; at 0: `call [[obj+4]]` slot 0 with arg 1 (deleting dtor) |
| EIP `0x080469DB` | anonymous | faulting write |

Registers at the fault: `esp = 0x1AFCE8`, `[esp] = 1`, `[esp+4] = 0x91A46E`, `[esp-4] = 0x9158AD`, `edx = 0x9158AD`, `ecx = 0x3AF674A0`, `esi`/`edi` = current entry and end of the entry vector (7 entries left) [verified]. The pattern fits `RefCounted_ReleaseAt8` reaching refcount 0, pushing 1 and calling the deleting destructor through `[obj+4]`, with the first garbage instruction popping its return address into `edx` [inferred, strong: it explains `[esp]=1`, the stale `0x9158AD` and `edx` together]. `ecx` = `obj+4`; its memory reads `bc 74 f6 3a  b8 2b fe 00 ... "t>\r\n  <NoText></"` [verified], i.e. the object's storage now belongs to an XML text buffer: the entry at `+0x38` points to freed and reused memory.

### 2.2 Where `0x080469DB` is

- Not a module: no image covers `0x08000000-0x08FFFFFF` in the module list [verified].
- Not a hook stub: the same 64 KB area holds ordinary game objects in the outer frames of the same stack (`0x08007860`, `0x08016200`, `0x08039920`, `0x08044710`, `0x0804BC80`) [verified stack data], so EIP sits inside early heap memory [inferred, strong].
- Heap is executable here: `TS3W.exe` has `DllCharacteristics = 0` (no `NX_COMPAT`) [verified PE header], so DEP is off for the process by default, and S3SS reports 393 MB of "Writeable Code" [verified report]. A bad vtable call therefore runs heap bytes instead of faulting at once, and the fault happens wherever the garbage first writes to a read-only page.
- No installed ASI holds `0x91A440`, `0x930380`, `0x915890`, `0x694690`, `0x6945C0`, `0x6946A0` or `0x693780` as a literal [verified byte scan of `Sims3Performance.asi`, `Sims3SettingsSetter.asi`, `MonoPatcher.asi`, `ts3native.asi`, `D3DShaderReplacer.asi`]. S3SS/S3PP can find sites by signature, so the scan does not exclude them alone; but no hook is on the stack and the call came through the game's own vtable dispatch [verified]. ts3native hooks `0x693780` only in the 0.10.7 `[Verify]` build (`vfFdbHolds`), which did not exist on 2026-10-14 [verified source].
- D3DShaderReplacer is not loaded in either report [verified module lists].

### 2.3 Root cause hypothesis

1. **`[Alloc]` cache aliasing (leading).** The run had `[Alloc]` on (PLAN; ledger: `ts3native.ini` changed just before). `EaFreeHook` decides "in use" from the next chunk's bit 0 and the chunk's own flags. A chunk it caches keeps exactly that state, so a **second `Free` of the same pointer passes the same test and is pushed again** [verified: `alloc.cpp` `EaFreeHook`]. On the same thread with no free in between, `*(p-4) = b->head` makes `p` link to itself: every later `Malloc` of that size class on that thread returns the same `p`, and `b->count` underflows because `AcPop` checks `head`, not `count` [verified]. Across threads, two threads each hand out `p` once. Either way two owners share one chunk: one writes text into a Swarm object, the next Release calls through text. The stock heap may tolerate or corrupt differently on a double free; the cache turns it into silent aliasing [inferred].
2. **Plain game use-after-free in Swarm.** A Swarm entry keeps a raw pointer at `+0x38` to a refcounted object that someone else already freed; the cache's LIFO same-size reuse makes the freed memory come back within frames instead of minutes [inferred]. Swarm is driven by `IC_Swarm_*` on the sim thread and updated on main (docs/44 §7), so a cross-thread stop/destroy is a candidate.
3. **Frida leftovers** from the previous `--taskstall` session: unlikely, different process, and no Frida script names any address on this stack [verified grep of `tools/`].

### 2.4 Fix or workaround

- Keep `[Alloc] Enabled=0` (default, current ini) until the cache is fixed [verified ini].
- **Built 2026-10-16 (ts3native 0.10.9, not run in game):** `[Alloc] Dedupe=1` walks the bin (at most `count` links) before a push and drops a Free of a chunk already there (counter `allocDupFrees`, v23; runtime switch `allocDedupe`); `AcPop` treats `count == 0` as empty; `AcEmpty` walks at most `count` links, so an old looped bin cannot hang a flush. Same-thread only; the cross-thread marker is left until `allocDupFrees` shows dups. Selftest covers both same-thread cases (dup at the head and behind another link). Repro `alloc-dupfree` (no VFX action yet).
- Cache fix (code change for a later task): in `EaFreeHook`, reject a pointer that is already cached: walk the bin (at most `PerBin` = 32 links) before pushing, count it in a new counter, and pass nothing to the game's Free (the first free still owns it); in `AcPop`, treat `count == 0` as empty. A cross-thread double free needs a marker in the chunk (for example a tag word in the unused header slot, cleared on pop) or a global set; measure the cost before choosing.

### 2.5 Repro and confirmation

- Selftest first (offline): free the same pointer twice into the hook and assert one push, then the same across two threads. Today's code fails the first assert [inferred from the code read].
- In game: `[Alloc] Enabled=1` plus a counter `allocDupFrees` (duplicate seen in the bin walk) on a Swarm-heavy repro (agent action that starts and stops many VFX: fireworks, fire, spellcasting; add a `repros.json` entry). A non-zero count confirms that the game double frees and that the cache would alias.
- Debug variant: poison cached chunks (fill user bytes with `0xDD` on push, check on pop, count mismatches). Any mismatch is a write after free into a cached chunk and names the size class.
- Ledger: triage lists this crash under `5fa9c931fe86`; a crash-free long `[Alloc]` run after the fix, with the counters above at 0 or handled, closes it.

## 3. Crash B: ModelBuilder teardown on the University enrolment path

### 3.1 Stack, decoded [verified: report stack data + disassembly]

| Return address | Function | What it does |
|---|---|---|
| `0x59ED59` -> `0x608666` | main loop, `TextureCompositorQueue_Update 0x608630` (stale frame) | |
| `0x5F0EE9` | `SimService_Update 0x5F0E50` (main-thread service slot, docs/42) | |
| `0x5F0AD3` | `SimService_ReleaseRequestRefs 0x5F0820` | clears `+0x20`, calls its Release (vtable +4) |
| `0x692745` | `RefCounted_AtomicRelease 0x692720` | `lock xadd [this+4],-1`; at 0 calls vtable +8 with 1 |
| `0x5DE368` | `ModelBuilder_DeletingDtor 0x5DE360` | |
| `0x5DC7D9` | `ModelBuilder_Dtor 0x5DC450` (vtable `0xFE3DC8`, ctor `0x5D7C20`) | last loop: `ModelBuilder_PartRecord_Dtor` on every 0x74-byte record in `[+0xC,+0x10)` |
| EIP `0x5CEB9A` | `ModelBuilder_PartRecord_Dtor 0x5CEB60` | second loop: for each pointer in the inline vector `[+0x28,+0x2C)`: `mov eax,[ecx]; mov edx,[eax+4]; call edx` |

At the fault: record `ebp = 0x33E7C4B8`, vector begin `edi = 0x33E7C4FC` (= record `+0x44`, its own inline buffer), end `ebx = record+0x4C` (two slots), first slot `ecx = 0x40A00000` (float 5.0), `[0x40A00000] = 8`, so `[8+4]` faults at `0x0000000C` [verified]. The record header (begin/end) is consistent but the slot holds a float, not an object pointer. The first loop over `[+0x4C,+0x50)` ran without a fault.

**What is "null":** nothing is null. The object pointer is a float that was written into the slot; the vtable read through it returned 8 [verified].

### 3.2 Mods and our code on the path

- No hook on the stack; ts3native has no address in the CAS range `0x5CB3B0-0x5F16F0` [verified grep of `native/ts3native`]; the installed ASI files hold none of `0x5CEB60`, `0x5DC450`, `0x5DE360`, `0x692720` as a literal [verified scan].
- The agent is managed code on the sim thread; the crash is on the main thread [verified stack]. The `dialog: true` path of `travel_university` calls the game's own `AcademicCareer.EnrollInAcademicCareer` (opens the enrolment dialog) and then `TravelUtil.TriggerTravelToUniversityWorld` [verified `TravelHandlers.cs`]. The agent only triggered the vanilla dialog path; it touches no CAS objects.
- ts3fixes: nothing in the CAS model builder [not checked line by line; no native code].

### 3.3 Root cause hypothesis [inferred]

The ModelBuilder is refcounted with an atomic release and is shared: `RefCounted_AtomicRelease` has callers in SimService (main), ThumbnailManager `0xD4D9F0` (UI thumbnails), `IC_CASUtils_SetModelInGameObjectDelayedImpl` (sim thread) and lot remote calls [verified callers]. The enrolment dialog shows Sim portraits, so thumbnail model builds for the household are in flight; closing it releases them while SimService finishes the build on main [inferred]. A record whose ref list holds a float means the record was torn down or reused before this final teardown: either a second teardown of the same records (a release path that ran the record dtors without clearing the vector), or the record memory reused by a writer of floats (vertex/morph data) after an earlier free. docs/43 already lists the ThumbnailManager internal calls running on the sim thread with no owner check (PLAN crash hardening item 4); this crash is a second symptom on that boundary.

### 3.4 Fix or workaround

- Workaround in place: `travel_university` now defaults to the no-dialog path (`DegreeManager.SetEnrollmentData`, the enrolment event, `TriggerTravelToUniversityWorld`) [verified source]; keep `dialog: true` out of queues until 3.5 is done.
- Native fix candidate (after the probe): in `ModelBuilder_PartRecord_Dtor`, skip and count slots that are not heap pointers (outside the EA heap ranges or misaligned) instead of calling through them. It hides the symptom only; the owner fix is to serialise ThumbnailManager CAS work with SimService (same thread or one lock).

### 3.5 Repro and confirmation

- Repro: `travel_university {dialog: true}` answered with cancel (and once with accept) N times on a household with two or more YAs, `[Alloc]` off. Add a `repros.json` entry; if it crashes again at `0x5CEB9A`, the dialog path is confirmed.
- Probe: count `ModelBuilder_Ctor 0x5D7C20` / `ModelBuilder_DeletingDtor 0x5DE360` per thread, and `RefCounted_AtomicRelease` calls reaching 0 by return address; a release to 0 on the sim thread or from `0xD4D9F0`, or two dtor runs on one object, confirms the race or the double teardown. The existing `[Verify]` thumbnail probes (thread of thumbnail create) give the first half.
- Ledger: triage lists this crash under `365b9c27d8ba` (no ts3native change just before) [verified triage run].

## 4. Open items

- No `.mdmp` for either crash, so the garbage bytes at `0x0804695B` and the exact Swarm object type stay unknown. Keep EA's minidump for the next crash (copy it out of `<Documents>` before the next run) [inferred: the game writes one only sometimes].
- Name the Swarm object types: the entry vector at `+0x21C` (0x58-byte entries) and the class with Release at vtable `+0x3C`.
- Confirm whether EA's `GeneralAllocator_Free 0x4E5920` detects a double free on its own (decides how bad the stock behaviour is without the cache).

## 5. Crash C (2026-10-18 16:11:26 UTC): lighting room hash use after free, 9 s after a direct future-world arrival

Static only (2026-10-19): capstone on the Steam 1.67 exe copy, EA's report `<Documents>/Electronic Arts/The Sims 3/xcpt <machine> 26-10-19 00.11.26.txt` (no `.mdmp`), the job log `probe-out/queue/travel-all-2026-10-18-bp-travel-future-direct.log`, the ts3native and probe logs of the same job, `S3SS.toml`, the change ledger (`5c328696e19f`) and our source. The game was not run.

### 5.1 Facts

| | |
|---|---|
| Report | ACCESS_VIOLATION reading `0x44973010`, State `MEM_FREE`, EIP `0x006A81D6`, main thread (stack `0x1AF9C4`, the same range as crash A's main-thread stack) [verified] |
| Job | `bp-travel-future-direct` (queue `travel-all.json`): agent `travel_future {prepare, direct}`, left NewDowntownWorld 16:10:56, arrived in FutureWorld 16:11:17, autonomy back on 16:11:17, crash 16:11:26, process gone 16:11:31 [verified job log] |
| Change set | `5c328696e19f`: `ts3fixes.package` `dae594e7da39 -> 2b3834b912c1` (door adapter build, `doormirror` off), new `ts3agent.package`, ts3native pinned `tested` 0.10.1 (save compress, footprint index, decompress, flush only) [verified ledger + ts3native log] |
| Modules | S3SS 1.6.3 (`Mimalloc` on, `LotStreamingOptimizations` on), S3PP, MonoPatcher, ts3native, DXVK, the Frida agent of `ts3probe serve` (no opt-in watch started) [verified report, `S3SS.toml`, probe log] |
| Same EIP elsewhere | none: the other nine `xcpt` files, `probe-out` and docs/67 §1-4 have no fault at `0x6A81xx` or `0x6C5Cxx` [verified grep] |
| Script errors | one NRaas ErrorTrap report at 16:11:05 (during the load): "Destroyed Object Found" corrections only, no exception [verified] |

### 5.2 Where it faulted [verified: disassembly + report registers and stack]

EA's printed call stack (`0x1AFAA0`, `0x608BDD`, `0x608BEE`) is stale stack data. The real frame: `0x6A8190` pushes 0x20 bytes, and `[esp+0x20]` = `0x6C5CCD`, the return address after the only call to `0x6A8190` (at `0x6C5CC8`).

| Frame | Function | Role |
|---|---|---|
| `Renderer_PreRenderUpdate 0x6EBB70` | (docs/44 §1) | main-thread frame entry |
| `LightManager_FrameUpdate 0x6B0500` | (docs/44 §2) | calls `0x6C5E20` on the light update tree at `+0xD4` |
| `0x6C5E20` | LightUpdateTree update | two passes over the lots, then a tail jump to `0x6C5C20` |
| `0x6C5C20` | LightUpdateTree pick-next-room (tag `Lighting/LightUpdateTree/RankArray`) | only when no room is in progress (`+0x74` = 0); for each entry of the tree's lot hash `+0x58` (tag `Lighting/LightUpdateTree/mLots`, constructor at `0x6C5340`) and each level -4..7, takes the level's LightingSolver from the lot object (`+0x10 + (level+4)*0x1A4`) and calls `0x6A8190`; then sorts the pairs (`0x6C58B0`) and starts the best room above a threshold (`0x69E860`: room state 2 -> 3) |
| `0x6A8190` | LightingSolver collect-ranked-rooms | walks the solver's room hash at `+0x234` (buckets) / `+0x238` (bucket count) (tag `Lighting/LightingSolver/Rooms`, constructor `0x6A8489`); per node: room = node `+0x10`, rank = `0x69E770(room)` (non-zero only in state 2), push (room, rank); next node = node `+0x80` |
| EIP `0x6A81D6` | read of node `+0x10` | node `esi = 0x44973000` lies in released memory |

Registers: `esi = 0x44973000` (the node), `ebx = 0x77A3057C` (current bucket slot, holds `0x40A0FF90`), `ebp = 0x1AF9FC` (the caller's inline pair vector: begin `0x1AFA10`, end `0x1AFAA0`, so 18 rooms were already collected). The bucket slot's head is not `esi`, so `esi` came from the `+0x80` link of a node that is still mapped: a node was freed without being unlinked from its chain, or its predecessor is stale too [verified registers; reading inferred].

### 5.3 Whose code is on the path

- **ts3native 0.10.1:** no hook in `0x69E000-0x6CA000` or on `0x6B0500` / `0x6EBB70`; its hooks are the save compress commit `0x4A850F` and codec slot, the footprint index `0x666488` / `0x82C940` / `0xAAC320`, the decompress and the save flush [verified ts3native log + source grep]. The decompress reported 0 mismatches [verified log].
- **ts3fixes door adapter (`DoorMirror.cs`):** a managed postfix on `CommonDoor.ImportContent` that runs the game's body first, then only marks its own table invalid and sends `GetOption2` messages on a handle-0 route; the game answers false for handle 0 (docs/18) and 0.10.1 has no door channel. It touches no native lighting or room data [verified source]. `doormirror` was off, so no table was built.
- **Agent:** `FutureDirect` (our copy of `TimePortal.TravelToFuture`'s boarding plus `GameStates.TravelToVacationWorld`, docs/77 §4.2) ran 50 s before the crash, in the old world; after arrival the harness only turned autonomy back on [verified job log + `TravelHandlers.cs`]. The boarding calls match the game's interaction; the order differs only in `SetDepartureTimePortal` (ours before the switch call, the game's after it) and in skipping the walk and animations [verified source compare].
- **Frida probe:** only opt-in watches hook game code; none was started in this job [verified probe log + `agent.js`].
- **S3SS:** `Mimalloc` replaces the game heap; mimalloc returns a segment to the OS once its last block is freed, which is what turns a stale read into `MEM_FREE` [inferred: mimalloc design; the stock EA heap would usually keep the page mapped and the stale read would pass]. `LotStreamingOptimizations` (throttled lot LoD transitions, map view blocker in `WorldManager::Update 0xC6D570`) changes when lots and their lighting load right after arrival [inferred: not read].

### 5.4 Verdict

- Freed object: a node of one lot level's LightingSolver room hash (key = room id, LightSolverRoom pointer at `+0x10`), read by the main-thread light update [verified].
- Who freed it: not found statically. Candidates: a room erase that left the chain linked, or the solver of a lot unloaded or rebuilt right after arrival while the light update tree still lists it [inferred].
- Owner: **game (EA lighting); none of our code runs on the faulting path or writes its data** [verified for ts3native, ts3fixes and the probe; inferred for the agent as trigger]. The rule "our build crashed = our bug until proven" is not closed: the direct trip is our own sequence and the only difference from the portal run of the same queue (16:04 job: arrived 16:07:50, ran 20 s and saved). One run per mode is not enough.
- No fix written: nothing of ours is on the path. A guard (SEH around `0x6A8190`) would protect one reader only; the same hash is walked by `0x6A7CF0`, `0x6A7FF0`, `0x6A80E0`, `0x6A86F0` and `0x6A88B0` [verified `+0x234` readers], so it is not worth building before a repro.

### 5.5 Repro and next steps

- Queue `tools/harness/queues/future-lighting-uaf.json`: three direct trips, two portal trips, one direct trip with S3SS `LotStreamingOptimizations` off; same `tested` asi and ini as `bp-travel-future-direct`. Repro entry `future-direct-lighting-uaf` (`repros.json`) is the short manual version.
- Reading: a crash at `0x6A81D6` (or in another `+0x234` reader) in direct jobs only = our direct sequence leaves lighting state behind (fix in `FutureDirect`, start with the `SetDepartureTimePortal` order); in both modes = game bug, and the S3SS-off job says whether lot streaming triggers it; no crash in 6 runs = rare game race, log it and move on.
- If it repeats: a Frida watch on frees of room hash nodes (allocator tag `Lighting/LightingSolver/Rooms`) per solver names the eraser.
- Effort: 6 game runs of ~4-5 min (~30 min with restarts), then 1-2 hours of analysis if it repeats; a guard, if still wanted, ~2 hours.


**Repro 2026-10-19 13:23-13:43 PHT (`future-lighting-uaf.json`, pinned build, 6 jobs):** 3 direct trips, 2 portal trips, 1 direct trip with S3SS LotStreamingOptimizations off: **0 lighting crashes in 6**. Every job FAILed the same non-crash way: the trip home was not queued (time portal state Inactive, the known future travel_home failure). Verdict per the plan above: a rare game race, logged; no guard. If it shows up again, use the Frida free-watch on `Lighting/LightingSolver/Rooms` nodes.

## 6. Crash D (2026-10-19 00:21:02 UTC): lot-0 footprint remove runs past its range, `bp-snap-p4` on ts3native 0.31.2

Static only (2026-10-19): EA's report `<Documents>/Electronic Arts/The Sims 3/xcpt <machine> 26-10-19 08.21.02.txt` (no `.mdmp`), the job's ts3native log (`probe-out/queue/snap-p4-2026-10-19-bp-snap-p4-0016-ts3native.log`), capstone on the Steam 1.67 exe copy and a map file of an identical 0.31.2 rebuild. Full write-up: docs/94 §21.11; ledger docs/51 §104.

| | Crash D |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000007` at `0x0063576A` (`LotRouting_RemoveObjectFootprints 0x635700`), sim thread, 5.1 min into a 10-min shadow run |
| Change set | `617090506a92`: ts3native 0.31.2 (`a3260bd7…`) and its ini (`[RouteSnap] Enabled=1 Lots=replay Mode=1`, async routing, discourage deferral) [verified ledger] |
| Stack | managed `RemoveDiscourageFootprint` IC (`SnIc19`) → `DiscRemoveHook` → `0x834860` → `0x635700` (lot 0) [verified] |
| Where | the lot-0 walk over map M stepped twice over an FME whose remove was pending in `P+0x3C` and ran from the range end to the bucket sentinel (`esi = 0xFFFFFFFF`) [verified disassembly; reproduced on the game's code in `steerro-test` G2] |
| Whose code | the game's loop; our `0x635700` stub only counts (contract checked) [verified]. Why the remove was pending: inferred (deferral while the Q was iterated; a clone-run deferral into the live maps is one candidate) |
| Verdict | our build's run, so treated as ours: 0.31.4 flushes the id's pending removes before a lot-0 remove (`lrPendFlush`, `lrPendBusy`) and stops clone-run deferrals (`pcDeferStop`). Also fixed: 11 first-chance AVs per load in `SnCloneProbe` that hid later exceptions from the crash log |
| Next | `snap-p4` BP and IP on 0.31.4; two clean runs to call it fixed |
| Other builds (0.31.5) | the 0.31.4 guard sat in the `[RouteSnap]` stub only. The deferral that sets up the walk is the game's own, and no stock, shipped-ini, design-D or async path was shown to create it [inferred, docs/94 §21.12]. All the same, 0.31.5 adds `[LotRemoveGuard]` (default on): the same flush before every lot-0 `0x635700` on every build. Tested offline on D's own lot-0 call (`steerro-test` G2/D) |

## 7. Crash E (2026-10-19 01:04:33 UTC): a second Frida agent's exception handler calls NULL while the int3 sampler runs, `bp-rare-winter-pond`

Static only (2026-10-19): EA's report `<Documents>/Electronic Arts/The Sims 3/xcpt <machine> 26-10-19 09.04.33.txt` (no `.mdmp`), the job's logs (`probe-out/queue/rare-scenarios-2026-10-19-bp-rare-winter-pond*.log`, `-0102-ts3native.log`, `probe-out/profile/...-360.log`), capstone on the Steam 1.67 exe copy and on this machine's `SysWOW64\ntdll.dll` (same base `0x770B0000` as in the report). Ledger docs/51 §107.

| | Crash E |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000000` with EIP 0; stack top `0x4445958A` in the **second** `frida-agent.dll` (base `0x443F0000`, RVA `0x6958A`), then `0x7712289F` in ntdll [verified] |
| Build | `game-copy/ts3native.asi.snapprobe2` (`ed59c43e15c0`, the `[ProbeSnap]` pass-2 indirect-call sampler, `DelaySec=60`), not the tested 0.24.0 asi; same asi and ini as the PASS of 2026-10-18 16:59 UTC [verified queue rows] |
| Thread | sim thread 31192 (ts3native's route gate lines name it) [verified] |
| What was being handled | ESI points at an EXCEPTION_RECORD: code `0x80000003` (breakpoint) at `0x00634B4C`; EBX points at its CONTEXT (flags `0x1007F`) [verified report register memory]. `0x634B4C` is a `call edx` site in `vcallsites2.h`: the sampler's own int3 [verified] |
| Path | ntdll's vectored handler loop (decode, CFG check, `call eax` right before `0x7712289F`) called the second agent's handler, and that handler called a NULL pointer [verified disassembly of the local ntdll; the NULL inside Frida is not analysed] |
| Timing | the second agent is the stutter profiler (`frida_tools.repl`, spawned at the profile start 01:04:31); its log ends "hooks installed", then "Process terminated". Crash 2 s after the spawn, while the sampler was in its second window (window 1 logged 17,640 int3 hits) [verified logs] |
| Not the cause | `test_env` (same second, but its reply came back OK; the second `fetch failed` is the dead process); the ts3agent and harness merges (no code of theirs on the stack); agent.js (first agent, base `0x67FC0000`, not on the stack) [verified] |
| Verdict | ours (our harness combination): a Frida agent attaching while our int3 sampler raises thousands of breakpoints a minute on the sim thread can run its exception handler before it is ready [inferred: race inside Frida's agent start; one crash in about 40 ProbeSnap profile jobs with the same attach] |
| Fix | `queue.ts` passes `--no-prof` to `profile-world.ts` for every job with `ProbeSnap.Enabled=1`: no stutter profiler session (no second agent attach or unload) beside the sampler. ProbeSnap jobs only need the native log; they lose the `<tag>-<secs>-*` hitch files |
| Verify | rerun `rare-scenarios.json --only bp-rare-winter-pond` 2x (and the rest of the queue as usual): no crash, the job log shows no stutter profiler files. A crash with no second agent loaded = refuted |
| Effort | ~1 h analysis + 10 min fix; live: 2 runs of ~8 min |

## 8. Crash F (2026-10-19 01:07:10 UTC): an A\* graph node on a pool free list while the sim thread's search holds it, `bp-snap-p4` on ts3native 0.31.4

Static only (2026-10-19): EA's report `xcpt <machine> 26-10-19 09.07.10.txt`, `probe-out/queue/snap-p4-2026-10-19-bp-snap-p4-0105-ts3native.log` and `-0105-shadow.jsonl`, capstone on the Steam 1.67 exe copy. Ledger docs/51 §107.

| | Crash F |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000000` with EIP 0; return address `0x0061E6E0`, then `0x633599`, `0x640759` (stack refs also `0x664B50`, `0x632DFF`, `0x64E926`, `0x6265FE`, `0x4E3F84`) [verified] |
| Thread | sim thread 24572 (main 29240, worker 28288 per the route gate lines) [verified] |
| Where | in-room A\* `0x61E500` (called from `0x633594`): the node it takes from the open list (`esi = 0x49193A08`) gets flag `0x200000` at `+0x30`, then a virtual call through slot `+0x10`. The node's first dword is `0x9C5C3F18`, a heap address, and `[0x9C5C3F18+0x10] = 0`: call to NULL [verified disassembly + registers] |
| What the node is | `+4` reference count word 2, `+8..+0x10` an empty vector whose allocator name at `+0x14` is `0xFEA058` = "Routing/cAStarGraphNode/mNeighborNodes"; the constructor `0x61B380` writes exactly that layout with vtable `0xFEA080`. `0x9C5C3F18` holds the same layout. A node whose vtable word points at another node of its kind is a node on a pool free list (the deleting destructors `0x61B520` / `0x61AD00` push with node `+0` = old free head) [verified] |
| Our code on the path | the A\* family pools (`0x11CEF50`, `0x11CEF68`, `0x11CEF90`, `0x11CF448`) have their pop and push sites redirected by `pools.cpp` in this build (`plReady 7`); P4 clone runs ran on the worker during the 12 s before the crash (89 shadow records, copies filled) [verified log, counters at start] |
| Cause | the node was pushed to a free list while the sim thread's search still held it: an unlocked free list written by two threads [inferred]. In 0.31.4 two pushes do that by design and are only counted: a P4 clone run (worker, no `+0x918`) freeing a game node pushes it into the game's pool (`rpLivePool`), and a thread that owns no clone work freeing a private A\* node pushes it into the private pool the clone run pops (`plForeign`). The counters at the crash are lost (no end dump), so which one fired is not known |
| Verdict | ours (0.31.4 RouteSnap P4 + private pools; crash D of §6 was the same job on 0.31.2 at a different site) |
| Fix (0.31.5, counters v57 unchanged) | those two pushes go to a sink pool object nobody pops: the node leaks instead of entering a free list another thread uses; the first 8 are logged (`private pools: pool N node ... leaked to the sink`), the counters still count them. Tree pools keep their old foreign path. Selftest updated (`TestPools`), `selftest ok` |
| Verify | build 0.31.5, copy to `game-copy/ts3native.asi.snap0315` (the queue points there), run `snap-p4.json` (BP then IP) 2x each. Pass = no crash; read `rpLivePool` / `plForeign` and any sink lines. Sink lines + no crash = cause confirmed. No sink line and a crash at the same place = refuted: next suspect is a non-clone worker job and a sim-thread search on the game's A\* pools at once (check the `+0x918` hold on the `0x633594` path) |
| Effort | ~1.5 h analysis + 30 min fix; live: 4 runs of ~13 min |
| Second cause (2026-10-19, ts3native 0.32.0) | I-94-220: before 0.32.0 a P4 clone run's planner took P from the live W (`[W+8]` = live P), so the worker ran `LotPlan_Inner` -> `InRoomPlan` -> the in-room A\* `0x61E500` on the live P's `cAStarSearch` (`P+0xBC`) without `+0x918`: crash F's own frames (`0x65F0EA` -> `0x65F11C` -> `0x6410FF` -> `0x640754` -> `0x633594` [verified]). Two threads on one open list fit "a node on a free list while a search holds it" better than the pool pushes [inferred]. 0.32.0 gives the copy its own planner W' (docs/94 §21.13); ledger I-67-11 (docs/51 §108). Verify: `snap-p4` BP and IP on 0.32.0, 2x each |

## 9. Crash G (2026-10-19 02:10:03 UTC): a W' cache node in the game's shared empty bucket array, `bp-snap-p4` on ts3native 0.32.0

Static only (2026-10-19): EA's report `xcpt <machine> 26-10-19 10.10.03.txt` (no `.mdmp`), `probe-out/queue/snap-p4-2026-10-19-bp-snap-p4-0208-ts3native.log` and `-0208-shadow.jsonl`, `chain43.log`, capstone on the Steam 1.67 exe copy, and a map file of a 0.32.0 rebuild (`.text` byte-equal to `game-copy/ts3native.asi.snap0320`, sha256 `874706ad…`). Write-up: docs/94 §21.13 (crash G note); ledger docs/51 §110.

| | Crash G |
|---|---|
| Report | ACCESS_VIOLATION reading `0xBF5D9B01` at `0x009A32C0`, main thread 38212, 2 min after load (`ebp = 0x0114C900`, `ebx = 1`) [verified] |
| Before it | the second P4 clone run (plan 3, the first one that was not start == goal) faulted on the worker 40660 writing `0x64B413C8` at `0x004E5220`, inside EA's heap free; `P4 FAULT` turned P4 off. Both faults are in the same log second [verified] |
| Where (main) | `0x5E5E20`, a hash find: bucket array `[ecx+4]` = `0x114C900`, count 1, i.e. an **empty** EASTL hash on the game's shared empty bucket array. It found a non-zero bucket 0 and followed it into a node that was no longer one [verified disassembly + registers] |
| Where (worker) | `0x650B70` (planner on W, `this` = W') ends by freeing its local hash's nodes (`0x643580` → `0xD2BC10` → `0x4E3F70`). That hash was empty too, so it walked the shared bucket 0 and freed a node with a bad chunk header [verified call chain; the node's origin inferred] |
| Cause | `PcWp` (0.32.0) made W' from the live W's bytes and reset the `+0xC0` / `+0xFC` caches to the shared empty array (count 1, size 0), but kept the live **next-resize** words `+0xD8` / `+0x114`. The insert `0x657800` rehashes only when the new size passes that word (`0x4E09B0`, `jbe` at `0x4E09C3`). With the live threshold it did not rehash and linked its node into `0x114C900` bucket 0. From then on every empty hash in the game held that node: lookups on the main thread read it, and the next empty hash destroyed on any thread freed it [mechanism verified on the game's code offline; the W' cache insert in this run inferred from the frames] |
| Sink lines (pools 2, 3, 5, thread 40660) | the worker after the fault: `PcRelease` (DISCARD) → `PcAstarDrop` ran the private A\* object's deleting dtor `0x61EB20` (the route gate line `Q reset (clear path) ... from 0x61EB28 thread 40660`) after the run had left the pool job, so each private node was a "foreign" push and leaked to the sink. Same on every WRAP. A leak, not the crash [verified code path + log line] |
| Consumer AVs (thread 36616) | the replay consumer (`RpThread` → `RpDrainBody` → `RpApply` → `RpQKey`, map file): the "Q the clone does not have" diagnostic read `+0x1C` of a freed Q. The records were the consumer's own replays (self-records, fixed in 0.32.1) on a clone Q freed by the second load-time build. Caught, but they used 8 of the 16 first-chance log slots [verified frames; Q origin inferred] |
| EIP 0 on the main thread | logged after the fatal fault, deeper on the same stack (`esp 0x1AECBC` < `0x1AFB0C`), game frames `0x4D7001`, `0x58D945`: inside the game's own exception handling, not startup and not ours [inferred] |
| Verdict | ours (0.32.0 W') |
| Fix (0.32.2, counters v57 unchanged; includes 0.32.1) | W' starts with `+0xD8` and `+0x114` at 0, as the W ctor `0x657C50` leaves them; `PcAstarDrop` runs the dtor as P4 pool job work (`g_p4PoolsEnterJob` / `LeaveJob`); `RpQKey` checks the Q is readable first |
| Tests (offline) | `steerro-test` K6: live W with grown caches (`+0xD8` / `+0x114` = 0x61), then the game's insert `0x657BC0` into W'+0xC0 after every acquire; on 0.32.0 code: `K6 FAIL (crash G): ... linked node ... into the shared empty bucket array 0x114C900`; on 0.32.2: 26 inserts per seed pair, array untouched, and W' dropped on another thread with its filled cache. selftest `PcCheckWp` (W' thresholds 0; failed on 0.32.0) and `TestPools` (the A\* teardown's private node goes back to the private pool; on 0.32.0 it went to the sink, `plForeign` +1). Seeds 1 and 2 x 3000 `steerro-test ok`, no K3/K6 WARNING; `hook-sites --check` 446 addresses, 0 missing |
| Verify | park 0.32.2 as `game-copy/ts3native.asi.snap0322`, `snap-p4.json` BP then IP, 2x each. Pass: no crash, no `P4 FAULT`, no sink lines, `snPcAcquired` > 0 with shadow records past plan 3 |
| Effort | ~2 h analysis (logs, map rebuild, disassembly), ~0.75 h fix and tests, ~0.5 h docs: ~3.25 h; live: 4 runs of ~13 min |

## 10. Crash H (2026-10-19, `bp-snap-p4` on ts3native 0.32.4): a clone run's room build frees our heap block through EA's free, then the game hangs on the EA heap lock

Static only (2026-10-19): `probe-out/queue/snap-p4-2026-10-19-bp-snap-p4-0252-ts3native.log`, the job log `snap-p4-2026-10-19-bp-snap-p4.log`, the probe log, capstone on the Steam 1.67 exe copy. No EA report and no `.mdmp` (the process did not crash). Write-up: docs/94 §21.13.2; ledger docs/51 §114.

| | Crash H |
|---|---|
| Run | BP, world loaded 10:53:53 PHT; the first real P4 compares (3 shadow records, all M0, job 21.0 ms vs game 20.9 ms) [verified logs] |
| Fault | `P4 FAULT` on the worker 37208, plan 5 kind 0: C0000005 reading `0xAE286F64` at `0x004E5200` [verified log] |
| Where | `0x4E5200` is in EA's chunk free `0x4E51B0`, called from the general free `0x4E5920` (frame `0x4E5945`) through `0x4E3F70` (`0x4E3F84`). `eax` = chunk `0xA2258B60`, so the freed pointer is `0xA2258B70` (`ebx`); `edx` = its size word at `ptr-0xC` = `0x8C02E400`, `esi` = size `0x0C02E400` (192 MB): not an EA chunk header. The large-chunk path reads the next chunk's header at chunk + size + 4 and faults [verified disassembly + registers] |
| Who freed it | `0x5D1E4A`: the end of a `vector<u16>` grow (`0x5D1D60`): new buffer from `0x4E3F90`, then the old `begin` to EA's free. Caller `0x63D516` in `RoomGraph_FirstUse 0x63D2F0`: the push of a portal index into the room record's `+0x24` list (room record = a 0x44-byte entry of lot graph `+0x90`). Up the stack `0x63F488` (`LotPortalSearch 0x63EC40`) and `0x63FB5B` (`LotPlan_Inner 0x63F6E0`) [verified disassembly; the deeper refs from `0xA8E1F0` on are stale stack] |
| Why it is not an EA block | `replay.cpp` `RpVecCopy` copied the clone's room lists into our private heap (`RpAlloc`: `HeapAlloc`, 16-byte aligned, size at `ptr-8`, raw pointer at `ptr-4`) with capacity = size, so the first push past the copied size grows and hands our block to EA's free. `ptr-0xC` is the NT heap entry header when the raw block is 8 mod 16 (this run: garbage size, fault), or our zeroed padding when it is 0 mod 16: size 0, EA's small-bin path then writes into our block and links it into its bin 0 without a fault [verified `RpAlloc` layout + `0x4E51D3..0x4E51F2`] |
| Why a clone run builds a room | `RoomGraph_FirstUse` builds a room whose built byte `+0` is 0: never built, or reset by `0x630F70` (a footprint change clears only that byte and the lot's `+0x8C`; the room's lists stay) [verified]. The cut (`PcPrepare`) builds only each lot's outside room, so other unbuilt rooms reach the clone run. The build empties `+0x24`, frees every `+0x34` element's buffer (`0x61EE20`), resizes `+0x34` (`0x622A30`) and pushes into both [verified] |
| Second defect on the same path | room `+0x34` is a vector of 16-byte vectors (8-byte entries), not flat data (I-94-133 refuted): the flat copy kept the inner buffers **live**, so on a room reset by `0x630F70` the clone run's `0x61EE20` EA-freed blocks the live room still holds (the sim's next rebuild of that room frees them again). In this run that free came before the fault if the room had been built before [mechanism verified; occurrence not known] |
| Why the game died | EA's alloc and free hold the heap's lock object `[[0x11CB864]+0x4E4]` (a `CRITICAL_SECTION`, depth word `+0x18`) around the call (`0x4E5924..0x4E594E`, `0x4E69D4..0x4E6A05`). `P4Guarded`'s `__except` unwound past `LeaveCriticalSection`, so the worker kept the lock and the next allocation on any other thread waited for ever. The log ends after the fault lines (the A\* drop on the worker, which owns the lock, still ran); the harness saw the heartbeat frozen for 60 s and stopped the job ("game died at 02:55:30 UTC"), no xcpt file [lock path verified; deadlock inferred from the frozen heartbeat] |
| Same in crash G | crash G's worker fault at `0x4E5220` was inside the same free and left the same lock held [verified site]; there the main thread's AV came first |
| Verdict | ours (0.29.0 room copy `RpVecCopy` for `+0x24` / `+0x34`; the P4 fault path since 0.29.0) |
| Fix (0.32.7, counters v57 unchanged) | `replay.cpp` `RpRoomListsCopy`: `+0x24`, `+0x34` and every `+0x34` element's buffer copied as EA blocks (`0x4E3F90` with the vector's own `+0xC` word, as the game's grow does), empty lists as NULL; `RpLotFree` frees them with EA's free. `p4.cpp` `P4EaHeapUnlock`: after a fault or lazy stop in a clone run, every hold of the EA heap lock the worker still owns is released (logged `P4 FAULT: EA heap lock released`). A fault inside EA's free can still leave a half-done free; this one wrote nothing before its read |
| Tests (offline) | `steerro-test` K7: the game's `RoomGraph_FirstUse` on each clone lot's unbuilt room (built byte 0, one portal, empty `+0x24`, one `+0x34` list whose buffer sits on a `PAGE_NOACCESS` page during the call); the fake EA free refuses and counts a non-EA block. With the 0.32.6 copy: `K7 FAIL (crash H): ... touched a live room buffer`; 0.32.7: 10 rooms built per seed pair, no foreign free, live rooms untouched. Selftest: the fault variant takes a fake EA heap lock before faulting; after the run the lock is free. Seeds 1 and 2 x 3000 `steerro-test ok`, no K3/K6 WARNING; `hook-sites --check` 446 addresses, 0 missing |
| Verify | park 0.32.7, `snap-p4.json` BP then IP, 2x each. Pass: no `P4 FAULT`, no hang, shadow records past plan 5. A `P4 FAULT` line followed by `EA heap lock released` and a game that keeps running = the fault path works; then triage the new fault |
| Effort | ~1.5 h analysis (logs, disassembly), ~1 h fix and tests, ~0.5 h docs: ~3 h; live: 4 runs of ~13 min |

## 11. Crash I (2026-10-22 01:11:05 UTC): a route-event handler with a NULL vtable in the follower's event list, `bp-destroypark-offfirst` on ts3native 0.36.14

Static only (2026-10-22): EA report `xcpt <pc> 26-10-22 09.11.05.txt` (registers, stack scan), `probe-out/queue/clean-night-routing-extra-2026-10-22-bp-destroypark-offfirst{.log,-0102-ts3native.log}`, `probe-out/profile/...-offfirst-ab-native.jsonl`, capstone on the Steam exe copy. Ledger docs/51 §216.

### 11.1 Fault

| | Crash I |
|---|---|
| Run | BP, seed 12345, `[Discourage] DestroyPark=1`, `--ab destroypark` off-first (warm-up on 0-120 s, off 120-240, on 240-360, on 360-480, off 480-600); profile start 01:03:54 UTC [verified job log] |
| When | 01:11:05 UTC = 431 s into the profile, **71 s into slice 4 (`dpMode` 1, ON)**, not the last OFF slice [verified: job log `destroypark on` at 01:09:54, ab-native row 3 `dpMode` 1]. The OFF slice before had emptied the graveyard (`p5cFlushed` = `p5cParked` = 127 at 240 s): no ON-to-OFF leftover is involved [verified] |
| Fault | sim thread 25936: `ACCESS_VIOLATION` reading `0x10` at `0x84EAC3` `mov eax, [edx+0x10]`; `edx` = 0 = the vtable word of `ecx` = `0x3D90B098` [verified registers + disassembly] |
| Code | `0x84EA00` (follower `this` = `esi` `0x32548A20`): `0x50ABE0` splices the agent's buffer (`agent+0x68`, `ebx` `0x9786C818`) back into the follower's event list at `this+0x1C`, then walks it: node `edi` `0x97CE7490` -> element `eax` = `[node+8]` `0x3ACB1460` (refcount `+4`, plain dec, deleting destructor at vtable `+0`; byte `+0x24` one-shot; `[node+0xC]` type 3 / 9 / 10 feed the follower counters `+0x174` and level `+0x248` +-1) -> handler `ecx` = `[element+0x1C]` -> `call [[handler]+0x10](element)` (still valid?). The handler's first dword is 0: a freed (or never constructed) block. The element holds the handler raw: no reference is taken here or in `0x849570` / `0x853500`, the two other walkers of the same list [verified disassembly] |
| Path | `Locomotion_Update` `0x83CF52` -> `Locomotion_UpdateAgent 0x957210` (`0x957292`) -> agent step `0x956850` (`0x956C67`) -> `0x9564A0` (`0x95669A`) -> `0x84EA00` [verified EA call stack + call sites]. The previous loop iteration removed an element (return address `0x84EC8E` of its node free still on the stack at `0x43CEFC68`), so the bad element is the second one walked this tick [verified stack scan] |
| Swap window | The agent step first moves the follower's list out (`0x84E900` at `0x956968`: follower `+0x1C` -> `agent+0x68`), runs the follow step `0x84D7E0` (`0x95667F`, many unresolved vcalls), then `0x84EA00` moves it back. In between, the follower's own list is empty [verified disassembly] |

### 11.2 What DestroyPark changes

With the switch on, a destroy during a running job parks one reference (`P5cPark`) and `DestroyById` runs at once (events 5/6, owner callback `0x68EC50` -> `+0x3C`, update-list removal `0x938CA0`, flag `obj+0x28 |= 0x400000`, hash erase, its releases); the deleting destructor (`Object_Destruct`, component destructors) waits for `P5cFlush` [verified code]. In 0.36.14 `P5cFlush` ran at the START / READY / CANCEL polls (`GetOptionHook`, yielding script tasks only: a non-yielding caller plans inline), at save / closing / teardown, **and at every `DestroyById` entry with no job running**. `DestroyById` has 18 callers, several native and mid-update: the locomotion effect list `0x84CE30` (called by `0x84EA00` itself at `0x84EE51` and by the follow family `0x84F2B0` / `0x8504D0` / `0x850E20` / `0x851280`), `Animation_DrainDeferred`, lot unload [verified docs/88 §2 + call sites]. So in 0.36.14 any parked object's destructor could run inside a native caller of `DestroyById`, a place where vanilla never frees a foreign object (vanilla frees an object at its own destroy, or where its last holder releases it). docs/88 §5.1 already banned flush points that can sit inside a native caller mid-update (gate entries); the destroy entry was let through on the argument that an extra holder delaying the free is a vanilla state, which covers *when* the free happens but not *where* [design flaw, verified against the doc].

Counters of the crashed run: 279 parks, all flushed by 360 s, `p5cCountLow` 0 (no double release), `vfRtDestroyOffSim` 0; `p5cAgeMaxUs` 170,093 us in slice 3 (T-G3's 100 ms exceeded: one object parked across more than one job) [verified ab-native]. The on-first twin (`bp-destroypark-onfirst`, same build, 509 parks, ages <= 90 ms) **passed**: no crash [verified jsonl row].

### 11.3 Cause (inferred; two candidates)

| # | Mechanism | For | Against / open | Tag |
|---|---|---|---|---|
| B | **Nested flush**: a parked object's destructor runs at a `DestroyById` entry inside a native caller (the follow step inside the swap window, or a later native destroy), frees the handler (or what owns it), and the element, out of the follower's list at that moment, escapes whatever cleanup the destructor does | the flush is the only new free site; the swap window is exactly where an owner-side cleanup would miss an element | no direct-call chain from `0x84D7E0` to `0x939420` within 8 hops (only through its unresolved vcalls); the handler's class and its cleanup path are not identified | **inferred** |
| A | **Zombie window**: between the destroy body and the deferred destructor (up to 170 ms, several sim ticks) the object is out of the hash and its owner already notified, but its components live and tick; a new element pointing at it is created then, and the destructor (relying on the owner-side cleanup that already ran) does not remove it | the window is new (vanilla's extra holders release within one call); 170 ms age seen in this run | needs a subsystem that registers references to an object after its owner callback; not shown | **inferred** |
| V | vanilla bug unrelated to the park | - | first `0x84EAC3` among the crash reports kept; crash rule: ours until proven | not supported |

What tells them apart: whether the faulting handler is (inside) an object our flush released, and from which flush site. 0.36.19 logs that (below); B is also removed by the fix, so a recurrence with a ring hit at a poll site = A.

### 11.4 Fix and probe (0.36.19, counters unchanged)

- `rtsites.cpp`: the `RT_DESTROY` entry releases parked references **only while the world closes** (`rtClosing`: `ObjectService_DestroyAll` and the shutdown destroys after `RT_CLOSING`). Remaining release points: START / READY / CANCEL polls (yielding script tasks, the IC points from which vanilla's script `Destroy()` frees), `RT_SAVE`, `RT_CLOSING`, `RT_TEARDOWN`. Removes candidate B; the graveyard still empties at the READY poll of the job it parked under (a full graveyard keeps the wait, as before). Applies to P5c's FreeLock windows too (same graveyard).
- `p5c.cpp`: ring of the last 64 releases (object, vtable, count before, flush caller, time); `XcptLog` matches an access violation's registers against it (nearest released object within 0x400 bytes) and logs `P5c: fault register ... released N releases ago by the flush from ...`. A hit with the fix in = candidate A (or a cause B did not cover); no hit = the faulting block was not one our flush released directly (a separately allocated component, or a vanilla free).
- Not done: naming the handler class (`[element+0x1C]` vtable) statically; a live probe of the element's constructor would name it.

| | |
|---|---|
| Tests (offline) | selftest `TestDestroyPark` §2 rewritten: after a job, a native-caller destroy entry releases nothing (3 parked objects stay, canaries intact), the next poll releases them oldest first, the ring names object C for a fault register at C+0x18 and nothing for a stack address; a destroy entry while closing still releases. `selftest ok`; `hook-sites --check` 527 addresses (no new site; 4 pre-existing not in docs/68) |
| Build | `game-copy/ts3native.asi.destroypark-fix`, ts3native 0.36.19 (this branch's 0.36.18 + fix), sha256 `6b1a0cdd37519be7f71fbddfaa26969acc254b7b211c239ee0747a1a755be516` |
| Verify | `tools/harness/queues/destroypark-fix-verify.json` (off-first + on-first as in clean-night-routing-extra.json, clean night). Fixed = 2 clean runs of the queue. Pass also `p5cAgeMaxUs` < 100 ms, `p5cFlushed` within 512 of `p5cParked`, `p5cCountLow` 0 |
| Effort | ~1.5 h analysis, ~20 min fix and tests, ~20 min docs; live 2 x ~25 min |

## 12. Crash J (2026-10-22 ~15:12 PHT): TS3W.exe gone ~10 s into every `[RouteJobPhases]` job (`asi.phases`, `asi.0324`)

- **Symptom**: all 6 jobs with `[RouteJobPhases] Enabled=1` (`route-phases-2026-10-22-*`, `cellmemo-probe-2026-10-22-*`, `routealloc-2026-10-22-*`) lost the process ~10 s after profiling began: no crash report, no xcpt file, `ts3native.log` ends in normal route gate lines. The same `asi.0324` with the switch off (tailunlock, dedupe) passed.
- **Cause [verified]**: `RjpPop` (routephases.cpp) matched the leave thunk's `at` against frame slots top-down with `slot < at` = unwound. 12 of the 15 timed entries end in `ret N`, so `at` = slot + N and the function's own frame looked unwound: the parent's frame was popped and its return address used, so the thunk returned into the parent's caller with the callee's esp. The first nested ret-N return in a worker job hit it; whatever ran next on the corrupted stack ended the process before our filter could report (exact last instruction not captured live [inferred]: wrong esp into the caller, /GS or SEH-chain failure). Offline repro: steerro-test `STEERRO_PHASES=1` on the old code faults at `0x625420` in K8's first plans; the selftest's ret-N pair fails its drop count.
- **Fix**: `RjpPop` searches from the outermost frame for the first `slot <= at` (loadprof's `LpLeave` rule; an outer frame's slot is above the callee's arguments). Branch-into scan of all 15 stolen prologues: none (I-150-2 closed). docs/150 §7 fix note, ledger §250.

| | |
|---|---|
| Tests (offline) | selftest `TestRouteJobPhases` with a `ret 4` site inside a `ret 8` site (old: assert); steerro-test `STEERRO_PHASES=1` seeds 1-3 x 3000 ok (11 of 15 sites, 2560 jobs per seed, `rjpDropped` 0; old: AV); `hook-sites --check`, `check-docs-code` ok |
| Build | `game-copy/ts3native.asi.0326` = master 53bdb56d + fix, sha256 `C13367080FED1473EB925AA7F7B85A8FEBD77B62279E6031A73A51BB4566B63C` |
| Verify | `route-phases.json`, `cellmemo-probe.json`, `routealloc.json` on 0326 (day, 5 min). Fixed = 2 clean runs with the switch on |

## 13. Crash K (2026-10-23 15:30:43 UTC): the sim thread's in-room A\* inside `IC_World_FindPlacesOnShoreline` without `+0x918` while a route job runs, `ip-boat-exit-plandrain-ab` on ts3native 0.36.36 (`asi.0342`)

Static only (2026-10-23): EA's report `xcpt <machine> 26-10-23 23.30.43.txt`, `probe-out/queue/overnight-1023-2026-10-23-ip-boat-exit-plandrain-ab-1454-ts3native.log`, capstone on the Steam 1.67 exe copy, `routegate.cpp`. The PLAN notes of 2026-10-23 call it "crash-F frames without P4" (§8 has the same frames). Ledger docs/51 §309.

| | Crash K |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000000` with EIP 0; call stack `0x0061E6E0`, `0x00633599`, `0x006343B0` (then two non-code words) [verified report] |
| Run | repro `ip-boat-exit-plandrain-loop` (IP, houseboat exits, placement batches, 3 foreign `ReplanFromPosition` plans), `[Discourage] PlanDrain` A/B; `route snapshot P4 off`, so worker jobs plan on the live P/Q and the live A\* pools `0x11CEF50` / `0x11CEF68` / `0x11CEF90` / `0x11CF448`; last job logged: `long job 236 ms, A10 foreign START, IC kind 3` [verified log] |
| Where | `0x61E6D9 mov eax,[edx+0x10]; call eax` in the in-room A\* `0x61E500` on the open list's front node (`Q+0x18`): the node's vtable slot is 0 [verified disassembly]; node reset or freed under the search by a second thread [inferred, as §8] |
| Caller chain | `IC_World_FindPlacesOnShoreline 0x78C440` (call at `0x78C54A`) -> `0xC75920` (call `0xC7630A`) -> `LotSearch_Entry 0x640E00` -> `0x633FA0` -> `InRoomPlan 0x632E40` -> in-room A\* `0x61E500` on `Q = [P+0xBC]` (`0x633579`), the game's only `cAStarSearch` [verified: E8 scan of every `0x640E00` caller (3 inside `Route_PlanLocked`, plus `0xC7630A`), `0xC75920`'s only caller `0x78C54A`, IC table reference; docs/62 `ic-safety.csv` row 1441: PROXY, sync, UNSAFE]. The boat exit reaching FindPlacesOnShoreline fits the repro [inferred: the upper frames are lost] |
| Why unlocked | no `[RouteGate]` site covered `0x78C440` / `0xC75920` / `0x640E00`. Only the leaves `0x63D2F0` (site 2) and `0x628A90` (site 12) lock or block job starts, each for its own call; between leaves and during `0x61E500` the sim held nothing and the worker could START a job on live P/Q [verified routegate.cpp `RgEnter` / `RgLeave`] |
| Our code on the path | none patches the faulting path; the race needs our async route jobs (any owner) on live P/Q [verified] |
| Verdict | ours: `[RouteAsync]` jobs and a sync IC on the same live planner, not PlanDrain [inferred] |
| v0.1.4 exposure | shipped config (`Kinds=0`, `Enforce=1`, P4 off): own-Sim `Route.Plan` jobs run on live P/Q and can overlap a shoreline IC, so v0.1.4 can crash here too, rarely; 2 of 706 logged runs ever show these frames. v0.1.5 candidates (`ForeignKinds=4`, `FallbackAsync`, `M1Retry`) put more and longer plans on the worker [inferred] |
| Fix (ts3native 0.36.37, counters v108) | gate site 16 (`kRgSites` index 15) at `0x78C440` (prologue `55 8B EC 83 E4 F0`, 6 bytes [verified]), coarse like `IC_World_GetConnectedRoomsImpl 0x7929F0`: an outermost non-worker entry during a job waits for `+0x918` through `LockAcquireHook` (which serves the job's host calls, docs/159) and holds it for the IC; with no job its frame keeps job starts out (`g_rgOffSimOpen`) until the IC returns. On with `[RouteGate] Enabled=1` like the other 15 sites (all or nothing), no new switch. Counters `rgUnlocked3` (+0x45C4) and `rgJob3` (+0x45C8). P4 D2 (rebuild writer window) stays on sites 13 and 14 only |
| Cost | the IC was seen in 76 of 629 logged runs (first-seen lines, count per run unknown); worst case one sim wait up to a job's length (`FbJobCapMs` 300) [inferred] |
| Deadlock check | same model as the existing coarse gates: the wait is `LockAcquireHook`, which pumps the job's host calls; the IC body never waits on the worker [inferred] |
| Tests (offline, not built yet) | selftest `TestRouteSites` (site 16 counts in v108, `rgJob3` during a job), `TestRouteEnforce` (site 16 waits for the job's hold and holds `+0x918`; no job = off-sim frame), the stub loop in `TestRouteGate`; `hook-sites --check` |
| Verify | build (`build.cmd out-crashh`), selftest ok, then repro `ip-boat-exit-defer-loop` 2 clean runs on the new build with release defaults; pass = no crash and `rgUnlocked_findPlacesOnShoreline` > 0 (the site is reached; `rgJob_findPlacesOnShoreline` > 0 also shows a wait happened), and `rgGateWaitMaxUs` not above the job cap. Fixed = 2 clean runs (crash-cause-before-fix rule) |
| Effort | ~1 h analysis + ~45 min fix; live: build ~10 min, 2 runs of ~13 min |

## 14. Crash L (2026-10-24 18:35-19:26 PHT, 5 crashes in 6 loads): main-thread draw pass with Apex Radiance 2.15.3, camera pan over streaming lots

Full triage in docs/186. Job: Apex runner file-list-cache camera pan over 8 lots on a copy of an SV save; four crashes on ts3native 0.36.8 + P5 ini, one on 0.36.37 + release ini (`probe-out/apex/triage-B-noprof-ts3native.log`).

| | Crash L |
|---|---|
| Faults | `0x68EEF0` read `0xE0` (`this = 0x20` in the model part draw `0x6F6250`); `0x6C93D0` read `0x44` (NULL resource of member `+0x210` in object draw `0x6FA0B0`); `0x71D56E` write `0x2AC` twice (Swarm effect list draw `0x71D440`, entry instance `+4` = 0); `0x68FDB4` read `0xF` (`0x68FD80`, shader binding table `+0x50` = `0xFFFFFFFF`) [verified disassembly + reports] |
| Path | frame loop -> `Renderer_DrawMainRenderSets 0x6F0DB0` -> draw list `0x6EE5E0` (virtual call `0x6EE872`) or the effect list `0x71D440` [verified] |
| Apex frame | `asi+0x292BE4` = `RigTracker::ModelDrawHook` (pass-through detour of `0x6F6250`), live in 2 of 5, stack residue in 3 [verified] |
| Our code on the path | none: no enabled 0.36.37 hook in the renderer, scene graph, effects or materials; none frees or parks objects (`DestroyPark=0`) [verified log + `hook-sites.ts`] |
| Verdict | Apex side or a game race exposed by Apex's camera-motion features (`SceneNodeBudget`, `LotLightingMotion`), not ours [inferred]; the running "Apex alone" job and one-switch runs (docs/186 §5) decide |
| Fix | none from us; draft issue for Apex in docs/186 §6 (not sent) |

## 15. Crash M (2026-10-25 02:08 UTC): a transform parent our P5c flush had freed 33 s before, plus the audio-thread erase crash, `ip-p6fix-ab` on ts3native 0.36.57 (`asi.p6fix`)

Static only (2026-10-25): `probe-out/queue/p6fix-verify-2026-10-25-ip-p6fix-ab{.log,-0204-ts3native.log}`, chain log `evening-1024b.out`, capstone on the Steam exe copy. No EA xcpt report, no `[CrashReport]` (off). The harness's `TypeError: fetch failed` is the probe losing the process (probe log: `detached: process-terminated`).

| | Crash M |
|---|---|
| Run | IP, in-load ABBA P6 off/on, arm **off #1** (`routesnapplan` 0) during the Go Here crowd + build/live replan repro; `[RouteSnap] FreeLock=1` (P5c window parks), `RecsUnlocked=1` [verified log] |
| Fault 1 | audio-style thread 20296: `C000001D` (illegal instruction) at heap `0x1909C554`; stack refs `0x5EE241 0x8B901E 0x8BFD0A 0x868E87 0x866BDB 0x8B2B3A 0x8B2A00` = the docs/157 §11.1 audio crash path: `0x5EE23A` calls vtable slot 1 of a hash node's `+0x10` object whose vtable now points into the heap [verified stack + disassembly] |
| Fault 2 | sim thread 5032 (routing lock owner): access violation reading `0x14` at `0x958944` `mov edx,[eax+0x14]`, `eax` = vtable 0 of the object `0x952560` returned from the slot list (`+0x48`) of the parent object `[this+0x30]` in `0x958560` (transform world matrix, recursive through `0x958EE0`) [verified disassembly] |
| Ours | `P5c: fault register ecx 4323C800 is +0xC0 into object 4323C740 (vt 01042CA8, count 1 before) released 6 releases ago by the flush from ts3native+0x1EFB9` = the START / READY poll in `GetOptionHook` [verified disassembly of `out-p6fix`; sha differs from `asi.p6fix`, so inferred for the deployed file]; the logged age was a format bug (`%lld` of a LONG), real value 0x01F9DF75 = 33.1 s [verified] |
| Correlate | ErrorTrap `ScriptError ... 10-07-49` (02:07:49 UTC): "Destroyed Object Found: PlateServing" [verified file; link to the object inferred] |
| Cause | docs/67 §11.3 **A (zombie window)** comes back with the 0.36.19 fix in: a child still parented (raw `+0x30`) to an object our flush freed, the free being ours (count 1) [inferred]. Not P6 (off in that arm) |
| Release exposure | none: v0.1.5 / v0.1.6 inis ship `[Discourage] DestroyPark=0` and `[RouteSnap] FreeLock=0` (code default 0), so `P5cPark` never parks and `P5cFlush` has nothing to free [verified ini + code] |

### 15.1 Logging (0.36.65)

- `p5c.cpp`: with `[CrashForensics] Enabled=1`, `P5c park:` and `P5c final release:` lines (first 4096 each) with object, id, vtable, count, flags (destroyed bit), resource key, park age, job state; the released ring keeps id, key type and park age; `P5cRelDump` writes the whole ring once on a fault in TS3W code or any illegal instruction; the fault line prints the age correctly.
- `crashev.h` (OurEvents=1): tags `p5c-park`, `p5c-rel`, `xform-bad`.
- `crashfx.cpp` (Guards=1): check-only entry guard at `0x958560` (9-byte prologue, no branch into it [verified capstone]): parent `[this+0x30]` not vt_Object / building base or unreadable (freed), refcount < 1, or destroyed bit; `crash forensics: XFORM BAD` line (first 16 per kind) with transform, key, parent words, caller, our-events and P5c ring lookups, before the game reads. The dump line adds the three counts. The audio path is the existing `0x5EE1E0` erase guard (Guards=1).
- `ts3native.cpp` `XcptLog`: every logged exception also logs all registers.
- Rerun: `tools/harness/queues/p6fix-ab-rerun.json` (`ip-p6fix-ab` only, same settings, `[CrashForensics] Enabled=1 Guards=1 OurEvents=1`). Fixed = 2 clean runs; a guard hit names the object before the fault.

## 16. Crash N (2026-10-25 04:49:26 UTC): the sim thread's package probe reads a released DBPF database index, and the audio thread walks a broken list node in the same heap area, `ip-p6int-p6-ab` on ts3native 0.36.65 (`asi.p6int`)

Static only (2026-10-25): `probe-out/queue/p6-integration-2026-10-25-ip-p6int-p6-ab-0427-ts3native.log` (fault lines near line 2428), capstone on the Steam exe copy. `[CrashForensics]` off (timing rung), so no P5 heap ring and no guards. No EA xcpt report read.

| | Crash N |
|---|---|
| Run | IP, p6-integration ladder rung 1, in-load ABBA, arm **off #29** (P6 off) during the build/live replan step after the crowd "go home" step; `[RouteSnap] FreeLock=1`, `RecsUnlocked=1`, `ReleaseLock=1`, `[HitchAttr] Holders=1` [verified log + queue] |
| Fault 1 | sim thread 4176: access violation reading `0` at `0x72DBDA` `mov esi,[edx+eax*4]` in `DatabaseIndex_Contains 0x72DBD0` (thiscall, `ecx` = index `0x3D1F2110`; `[ecx+4]` bucket array = 0 and `[ecx+8]` bucket count = 0, so the end-bucket read hits NULL) [verified disassembly + registers] |
| Fault 2 | thread 37440 (audio system thread, start `0x8B2A00`): access violation reading `0x2F6` at `0x8B315E` `mov eax,[ecx]`: the loop at `0x8B3140` walks the intrusive list at audio system `+0x259F0` (next `+0`, object `+8`) and node `esi` `0x3D21C440` holds `0x2F6` as its object [verified disassembly]. Same thread and outer stack as Crash M fault 1 (`0x868E87 0x866BDB 0x8B2B3A 0x8B2A00`), different leaf |
| Heap | the faulting index `0x3D1F2110` and the audio node `0x3D21C440` are 0x2A330 bytes (~170 KB) apart [verified registers] |
| Ours on the path | no ts3native hook in `0x72C000`-`0x736000`, at `0x4AFFC0` (`[Verify] pkgSource` is not on in this ini) or at `0x8B3140`. Shared code only: the `Lock_Acquire` / `Lock_Release` detours (the database lock `D+0x40` ends in `g_lockOrig`: `LockAcquirePlain`, `HoAcquire` and `P5bLock` change only `+0x918` [verified source]) and the decompress hook (`0x4EC065`, every run since 0.36.x) [verified log + `node tools/hook-sites.ts --check`] |
| P5c | no `ecx` / `esi` / `edi` / `ebp` in the released ring dump (64 of 783, newest 16.3 s ago) [verified log]; the P5 heap ring was off |
| Release exposure | none known: `FreeLock` and `RecsUnlocked` are 0 in `ts3native.ini` (code default 0) [verified ini]; v0.1.5 / v0.1.6 ship `FreeLock=0` (§15) |

### 16.1 The code [verified disassembly, Steam 1.67]

- **Stack** (fault 1, scanned code words): `0x7D8126` (a caller of ResourceMan slot `0x44`) -> `0x4AFDB1` (ResourceMan slot `0x40` thunk) -> `GetPackageSource 0x4AFFC0` (`0x4B0011` = return of its probe call). For each registered database it **releases** the ResourceMan lock `+0x48`, calls the database's slot `0x34` with `(key, 0, 1, 6, 1, 0)` and takes the lock back. The database pointer is not AddRef'd across the probe.
- **Slot `0x34` = `0x7345D0`** (DBPF database `D`, vtable `0xFFE078`; outer object `O = D - 8`, vtable `0xFFE0D0`; `esi` `0x0F4E41A8` at the fault = `D`): fast path when args 2 and 6 are 0: `0x72C6C0(1)` = `Lock_Acquire(D+0x40)`, read `[D+0xB0]` (the index), `Contains(key)`, `Lock_Release`. No AddRef of the index: the D lock is what keeps it alive here.
- **DatabaseIndex** (alloc name `ResourceSystem/DatabaseIndex` `0xFFD980`, 0x24 bytes; ctor `0x72DB40`: buckets = `0x114C900` (the shared empty bucket array), count 1, refcount `+0x20` = 0): `AddRef 0x72D890`, `Release 0x72DB80` (`lock xadd [+0x20],-1`; at 0 it frees the nodes `0x6258F0`, the bucket array when count > 1, and the object through `EA_Free 0x4E3F70`). A live index never has bucket count 0 or a NULL array, so `0x3D1F2110` was freed or overwritten when the sim thread read it.
- **Writers of `D+0xB0` (`O+0xB8`)**: create `0x734450` (O slot `0x14`, under the D lock); the open path `0x734710` (D slot `0x50`: the first opener re-opens the file and drops the index **under** the D lock); `0x7351E0` (hands out an AddRef'd index under the lock) and `0x735380`. **Drops without the D lock**: `0x734520` (D slot 8, after `0x72C6A0` closes the file) and `0x7343C0` (O slot `0xC`, when the package file's size or time changed). Handed-out references are released at `0x735099` / `0x7350FB` / `0x735129`.

### 16.2 Suspects (ranked)

| # | Mechanism | For | Against / open | Tag |
|---|---|---|---|---|
| 1 | **Heap reuse or corruption in that area**: something freed or overwrote memory the game still owned, so two unrelated EA objects ~170 KB apart broke at once on two threads | two faults on two threads in the same instant, both "object contents are garbage", both in `0x3D1Fxxxx`-`0x3D21xxxx`; the audio thread also faulted in Crash M (`FreeLock=1`, `RecsUnlocked=1` both times); our P5c flush and the RecsUnlocked consumer (`InsFreeInPlace` footprint copy frees, replay frees through `EA_Free`) are the new free sites of these runs | no P5 heap ring in this run (CrashForensics off); the P5c released ring has no register match; heap corruption by the game itself not excluded | inferred |
| 2 | **EA race: index dropped without the database lock** (`0x734520` close or `0x7343C0` file changed) on another thread while the sim thread's probe holds the D lock and reads it | the two lock-less drop sites are real [verified]; the probe holds only the D lock and no index reference | needs a close or refresh of a mounted package during play (who calls D slot 8 / O slot `0xC` in live mode is not mapped); explains fault 1 only, not the audio node | inferred |
| 3 | `GetPackageSource` probes a database that is being destroyed (no AddRef across the unlocked probe) | the ResourceMan lock is released around each probe [verified] | `D` at `0x0F4E41A8` is an early (load-time) allocation, and its lock and `+0xB0` were usable at the fault | inferred, weak |
| 4 | vanilla, unrelated to our build | - | crash rule: ours until proven | not supported |

Compared with Crash M: same run family (P5c `FreeLock=1` + `RecsUnlocked=1`) and the same audio thread on the second fault. Crash M's sim fault was a P5c-freed transform parent with a ring hit; here the sim fault is in resource code with no P5c ring hit, so candidate A (zombie window, §11.3) does not explain fault 1 directly. If suspect 1 holds, M and N may share one cause: a free of ours that the game still references, landing on whatever reuses the block.

### 16.3 Logging round (0.36.67)

- `crashfx.cpp` `[CrashForensics] DbIndexLog=1` (default off, no counters): a record-only detour at `Release 0x72DB80` (7-byte prologue, no branch into it [verified capstone]) puts every index release in a 1024-entry ring (index, refcount before, database from the caller's `esi`, whether this thread holds the D lock, thread, caller). A check-only detour at `Contains 0x72DBD0` (9-byte prologue, one caller `0x7345FD`) logs `crash forensics: DB INDEX BAD` (bucket array 0, count 0 or refcount < 1; first 16) with the index's ring history and its P5 heap / P5c ring entries, before the game reads. Hook overlap: none known; no S3SS, S3PP or Apex site in `0x72Dxxx`, Apex's resource lookup caches sit above the database and can only skip `Contains` calls [inferred] (docs/68 row `[CrashForensics]` DB index log). The fault dump adds the release and free counts (frees without the D lock separately), every fault register found in the index ring and the newest 8 frees.
- With `Enabled=1` (`HeapCheck` default 1) the existing P5 heap ring and double-free check cover suspect 1: a ring hit on `ecx` / `esi` / the fault address names our free. `Guards=1` keeps the `0x958560` parent guard and the `0x5EE1E0` audio erase guard of §15; `OurEvents=1` the P5c park / release tags.
- Rerun: `tools/harness/queues/crashn-rerun.json` (`ip-p6int-p6-ab-crashn` only, same settings, `asi.crashn` = `out-crashn`, `[CrashForensics] Enabled=1 Guards=1 OurEvents=1 DbIndexLog=1`). Read: a `DB INDEX BAD` line or an index free with "D lock NOT held" = suspect 2 (the caller names `0x734537` close or `0x73440E` refresh); a P5 heap ring hit on the fault registers = suspect 1, ours; neither, with a crash = widen to an EA heap walk around the fault address. Fixed = 2 clean runs.

## 17. Crash O (2026-10-25 11:15:06 UTC): a main-thread wild call into the heap and the sim thread's scene-graph lot lookup on a node with a garbage vtable, `ip-p6int-hold2-ab` on ts3native 0.36.67 (`asi.p6meas`)

Static only (2026-10-25): `probe-out/queue/p6-integration-2026-10-25-ip-p6int-hold2-ab{.log,-1108-ts3native.log}` (fault lines 992-1060), chain log `evening-1024b.out`, capstone on the Steam 1.67 exe copy and on `out-p6meas/ts3native.asi` (sha256 `83816d93...` = the deployed `asi.p6meas` [verified]; loaded at `0x70F00000`). EA's `xcpt ... 26-10-25 19.15.06.txt` is 0 bytes (the process died while writing it). `[CrashForensics]` off (timing rung): no P5 heap ring, no guards.

| | Crash O |
|---|---|
| Run | IP, p6-integration ladder rung 3 (first run where `holdkeyed=2` really took effect), in-load ABBA, arm **on #7**: `native_set` reply `holdkeyed 2`, `p6warm 2` at 11:15:04; crash 3 s after the `crowd_go` step (30+ Go Here plans in one tick) [verified log]. Arm 1 is the only arm with gate-11 waits (`p6 waits arm 1 gates: 0 1320 10 753 11 123 12 5894`) [verified log]. `[RouteSnap] FreeLock=1`, `RecsUnlocked=1`, `InsFreeInPlace=1`, `ReleaseLock=1`, `[Discourage] DeferFootprint=1`, `DestroyPark=0`; no Apex (`compat: Apex Radiance not loaded`), S3SS + S3PP + DXVK [verified log + queue] |
| Fault 1 | main thread 27964: `C000001D` (illegal instruction) at heap `0x9A6FA8F6`, `eax` `0x9A6FA8F0` = eip - 6, `ecx` `0x38A9B0A0`, `ebx` `0x38A9B080`, `edx` = `edi` `0x3D26F240`, `esi` `0x42EEBAC0` [verified log]. Stack word `0x50ED8E` = return of `call eax` at `0x50ED8C` after `mov edx,[ecx]; mov eax,[edx+10h]` in `0x50EB20` (which also stores `ecx` at `[esi+54h]`): a virtual call (slot 4) on an object whose vtable slot points into the heap; caller `0x50B2ED` (ret `0x50B2F2`), further words `0xEC9F93` (a `call edx` = vtable `+0x30` in `Frame_RenderAndPresent 0xEC9F00`), `0xECBD71` [verified disassembly]: the main thread's render frame. Object kind not named [open] |
| Fault 2 | sim thread 15948 (`sim 15948` in the route gate lines; routing lock owner): access violation reading `0xB07B3BD3` at `0x00C5FA93` `mov eax,[edx+40h]` in `SceneNode_FindRecursive 0xC5FA60`, `esi` = node `0x3F9F1C00`, `edx` = its vtable word `0xB07B3B93` (not a pointer into `.rdata`), `ebp` `0x08F30053` [verified log + disassembly] |
| Sim chain | `ts3native+0x68B77` (design U `UsfHook` pass branch: `lock inc` `usfPass`, then the original through the trampoline pointer with `ecx` = world; matches `discourage.cpp` lines 421-424 [inferred: no map file, shape match]) -> `RoutingWorld_UpdateSimFootprint 0x833560` (call `0x82CBF0` at `0x8335EA`) -> `0x82CBF0` (call `0x82C120`, ret `0x82CC20`) -> `0x82C120` (call `WorldManager_FindLotByIdTyped 0xC62D40`, ret `0x82C1AE`) -> `WorldManager_FindLotById 0xC60D30` (loop over `WM+0x9C`, ret `0xC62D55`) -> `SceneNode_FindRecursive 0xC5FA60` root (ret `0xC60D76`) -> the same on a child of a type-2 node (ret `0xC5FACD`) -> fault [verified disassembly of every return site]. So the bad node is a child in the world manager's scene list, not a routing object |
| Ours on the path | sim: only the `UsfHook` pass-through (the game's own footprint update, run as today) [inferred above]; no ts3native hook on `0x82CBF0`, `0x82C120`, `0xC62D40`, `0xC60D30` or `0xC5FA60` [verified source grep; Apex's `ObjectLookupIndex` reads these but Apex was not loaded]. Main: `0xEC9F93` is inside `Frame_RenderAndPresent 0xEC9F00` (render frame, vtable `+0x30` call at `0xEC9F91`); our only byte there is the `[FgRender]` SleepEx nop at `0xEC9FD1`, after the call; no ts3native site at `0x50EB20` / `0x50B2xx` [verified source grep] |
| HoldKeyed=2 sites | none on either stack: sites 1, 2, 10, 11, 15 are the goal-step lot planner, `RoomGraph_FirstUse 0x63D2F0`, goal step `0x75C2D0` / `0x760BFF`, `IC_World_GetConnectedRoomsImpl 0x7929F0`, `IC_World_FindPlacesOnShoreline 0x78C440` (docs/189 §9); no return address in `0x63xxxx` / `0x75Cxxx`-`0x760xxx` / `0x78Cxxx` / `0x792xxx` on the scanned 8 words of either thread [verified log]. An admitted gate holds the real `+0x918` while a lent window runs; the faulting call took the `rgOwnerTid == me` or drain pass of `UsfHook`, which fits the sim owning `+0x918` at the time but does not tell which frame [inferred] |
| P5c | no register of either fault within any of the last 64 released objects (nearest `0x19BAD0` below `esi`); the newest release was 1.2 s before, 122 releases in the session, 58 not listed [verified log] |
| Release exposure | none known: `FreeLock` and `RecsUnlocked` are 0 in `ts3native.ini` and the v0.1.5 / v0.1.6 inis (§15, §16) [verified ini] |

### 17.1 Verdict

Same signature as Crash M (§15) and Crash N (§16): **two threads hit two unrelated EA objects with garbage contents in the same instant** (M: audio wild call into the heap + a P5c-freed transform parent; N: DBPF index + audio list node ~170 KB apart; O: main-thread wild call through a heap vtable slot + a scene node with a non-`.rdata` vtable). All three runs had `FreeLock=1` + `RecsUnlocked=1` + `InsFreeInPlace=1`; M and N crashed in arms with `holdkeyed` 0, O in a `holdkeyed` 2 arm [verified logs]. So:

1. **HoldKeyed=2 is not the cause on its own** [inferred, strong]: the family crashes without it, and no re-keyed site's code is on either stack. It may raise exposure by timing (more sim work under the real `+0x918` inside lent windows, more overlap with the worker's clone and replay frees) [inferred, not measured]. Rung 2 (`hold1`) passed, one run.
2. **Leading suspect stays §16.2 #1: heap reuse from a free of ours** (P5c flush or the RecsUnlocked / InsFreeInPlace replay frees) that the game still references [inferred]. No direct evidence in this run (no heap ring, no P5c register hit). Suspect §11.3 A (P5c zombie window) fits O's sim fault only if the scene node was a P5c-freed object still linked under a lot (the transform-parent pattern of M); the ring says no for the last 64 releases.
3. Not a game bug until proven (crash rule).

No fix is clear: a logging round.

### 17.2 Logging round (0.36.71, no counters)

- `crashfx.cpp` `[CrashForensics] SceneTreeGuard=1` (default off): check-only detour at `SceneNode_FindRecursive 0xC5FA60` (7-byte prologue `53 55 56 8B 74 24 10`, callers `0xC60D71` and `0xC5FAC8` only, no branch into the prologue [verified E8/E9/Jcc scan]; `InstallDetour` skips a patched prologue, e.g. Apex). A node whose vtable is outside TS3W.exe `.rdata` or unreadable logs `crash forensics: SCENE NODE BAD` (node, parent = the caller's `esi`, searched id, caller, thread, P5 heap ring, our-events ring, P5c ring; first 16) before the game reads it. ~5 ns per visited node.
- `CfOnFault` and `XcptLog` (`ts3native.cpp`) now also take `EXCEPTION_ILLEGAL_INSTRUCTION` (a wild call anywhere, eip in the heap): the dump uses eip as the fault address (P5 heap ring lookup on it), logs `[esp]` (the call site) and `ecx` with its vtable word, and `P5cRelMatch` runs on its registers. Before, Crash O's main-thread fault produced no forensics dump at all (AV only, TS3W `.text` only).
- Selftest: `TestCrashForensics` adds a fake wild call in a freed ring block (dump + 2 ring hits) and the node check on dummies (`.rdata` vtable passes, garbage and unreadable count, NULL skipped).
- Rerun: `tools/harness/queues/crasho-rerun.json` (`ip-p6int-hold2-ab-crasho` only, same settings as rung 3, `asi.crasho` = `out-crasho`, `[CrashForensics] Enabled=1 Guards=1 OurEvents=1 DbIndexLog=1 SceneTreeGuard=1`, `noiseCheck` false, `minCounters` 131). Read: `SCENE NODE BAD` names the node and its parent; a P5 heap ring or P5c hit on the node, `ecx` or eip = ours (that free's tag names P5c flush vs replay); a crash with neither = an EA heap walk around the fault addresses next. Fixed = 2 clean runs. If the rerun is clean, an isolating pair (`RecsUnlocked=0` + `InsFreeInPlace=0` vs `FreeLock=0`) on the same rung splits the two free families (not queued).

| Build | `game-copy/ts3native.asi.crasho` = `native/ts3native/out-crasho` (0.36.71), sha256 `5A77EAEDB18AEC48A925BE5B4F6AB58EF325AEA9A90C47C66FF17831CEFEDFCC`; selftest ok, `selftest-all` 112 PASS; docs/68 row + `apex-sites.csv` C2 for `0xC5FA60` |
| Effort | ~1 h analysis, ~20 min logging code, ~15 min build; live: 2 runs of ~25 min |

## 18. Light forensics in every run (0.36.72, Rodney 2026-10-25)

Crashes M, N and O all came in timing runs with `[CrashForensics]` off, and every logging rerun (§15.1, §16.3, §17.2) was clean, so no run had forensics at the first crash. Rule: timing runs keep the light parts on; the guards stay opt-in.

| Part | Where it runs | Per call [inferred] | Calls/s [inferred from counters/logs] | Class |
|---|---|---|---|---|
| P5 heap ring (`CfRec`: every EA new/free of the P5 clone code, `g_rpUse` acquire/release) | our own code, route worker + the cut | ~30-60 ns (one `InterlockedIncrement`, one QPC, 8 stores) | thousands/s at most (InsFreeInPlace 4351 copy frees in 300 s, docs/157 §11.7); 0 with `[RouteSnap]` off | light |
| Heap check (`HeapCheck=1`): chunk header + next chunk bit before each of **our** frees | our frees only, no walk | 2 reads | same as the frees | light |
| P5c released ring (64) + `P5c park:` / `final release:` lines (first 4096 each) | our P5c flush | ring ~50 ns; a log line ~5-20 us | 122 releases in Crash O's session; 0 with `FreeLock=0` | light |
| Our-events ring (`OurEvents`, crashev.h, 8192) | our destroy gate, deferrals, refcount holds, save steps, route jobs | ~30-60 ns | a few hundred/s | light |
| Fault dump (`CfOnFault`: AV in TS3W `.text`, any illegal instruction; first 4) | exception time only | - | 0 in a clean run (the 3 start-up AVs in other modules are skipped) | light |
| `Guards=1`: `0x5EE1E0` map erase, `0x7B0A00` array clear, `0x958560` transform parent | detours on shared game code, every thread | ~5-10 ns | erase not measured (~1 ms/s at 100k/s); transform parent per world-matrix update, thousands/s while Sims move | heavy |
| `DbIndexLog=1`: release ring `0x72DB80` + Contains check `0x72DBD0` | detour on every package probe of a resource lookup | ~20 ns | tens of thousands/s while streaming (~0.2-1 ms/s) | heavy |
| `SceneTreeGuard=1`: `0xC5FA60` node check | detour per visited scene node of every lot lookup | ~5 ns | not measured (~0.1-1 ms/s sim) | heavy |
| `DrawGuards` / `EffectLog` / `ObjResLog` (docs/186) | main-thread draw / Swarm / SceneModel paths | 1-80 ns | up to 50k per frame | heavy |

- `crashfx.cpp` `[CrashForensics] Light=1` (ini default 0, so the release ini is unchanged): `g_cfOn` + heap check + `g_ceOn` (our-events ring) + the dump, **no** detour; `cfReady` bit 16 (and `cdReady` bit 16, our-events on); start line `crash forensics: light (docs/67 §18): ...`. `Enabled=1` installs everything as before (the light parts included).
- Harness: `queue.ts` `lightIni()` adds `CrashForensics.Light=1` to every job's ini (timing jobs too; not worldimage captures, not release tests: a job on a release asi `game-copy/ts3native.asi.rel*` or with `"releaseDefaults": true`, which `release-check.ts` now writes, runs the shipped ini as is); a job sets `CrashForensics.Light: 0` to drop it. Builds before 0.36.72 ignore the key. `hitch-ab.ts toolsOn` prints `CrashForensics.Light` (light, timing yes, taken off as 0 until measured) and `CrashForensics.<switch>` for each guard switch under `Enabled=1` (all heavy in `docs/data/tool-costs.json`: a timing verdict with any of them is invalid).
- Estimate: light set < 0.2 ms/s in all, sim thread < 0.05 ms/s [inferred, not measured]. Cost proof: `tools/harness/queues/forensics-light-cost.json` (BP, two-load ABBA off-1, on-1, on-2, off-2, 10 min each, lean profiler, release defaults; Light is start-time only). Pass: it/s and sim wait within the noise band, no hitch-count increase at 100 / 300 / 1000 ms. Also the pair `bp-tc-cflight-*` in the parked `tool-cost.json` (docs/168 §9).
- What a first crash now shows without a rerun: the dump (fault + registers against the P5 heap ring, P5 state, ring tail), the P5c released ring and park lines, our-events ring hits, any `HEAP` double-free line. Not shown: a guard line before the fault (needs `Enabled=1`).
- Build: `native/ts3native/out-flight` (0.36.72, master de052b30 + this branch: p6meas counters v131, MemDiag, SceneTreeGuard) -> `game-copy/ts3native.asi.flight`, sha256 `4F13C458A50BEEEE28C31A48EBE9ABBFE57C1637543599272187551AE0DFF03B`; native selftest ok, `selftest-all` 113 PASS (2026-10-25 12:08 UTC, queue held). P6 ladder rungs ask + kinds run on it.

## 19. Crash P (2026-10-26 02:01:23 and 02:02:34 UTC): Mono `InitializeArray` on a NULL field handle from a synthetic scripted-CC clone, `bp-ccs-load` / `bp-ccs-cycle` on ts3native 0.36.84 (`asi.allocsites2`)

| | |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000010` at `0x00E7A349`, stack `0xE591D4` <- `0xE5A48E` <- `0xE5A668` (EA `xcpt <pc> 26-10-26 10.01.23.txt`, same for `10.02.34`) [verified] |
| When | ~18 s after launch, before the main menu, both jobs with the set `game-copy/cc-synth/cc-script-100` (100 S3SA clones); the no-CC job before them passed [verified: chain log, queue result] |
| Code | `0xE7A330` = the `RuntimeHelpers.InitializeArray` IC (IC tables: name `0x10B546C`, function `0x10B5E04`); `0xE7A349 mov eax,[edx+0x10]` reads `field->data` of the second argument, which is NULL. The caller is the mint interpreter's native call path (`0xE591CF call 0xD70AC0`) under `interp_runtime_invoke 0xE5A600` [verified disassembly] |
| Cause | Clone IL `ldtoken field [Sims3GameplayObjects]'<PrivateImplementationDetails>{B044CCB2-...}'::'$$method0x...'`. The installed simler90 core mod replaces Sims3GameplayObjects / Systems with its own builds, whose `<PrivateImplementationDetails>` GUIDs differ, so the field ref resolves to NULL and the clone's array initializer (a `.cctor` run at script start) faults [verified: GUIDs in simler90's extracted DLLs; inferred: which of the 17 clones ran first]. Our native code is not involved (the fault is in EA's Mono IC on EA's call path; `[MemDiag] AllocSites` hooks no Mono code) [inferred from the stack] |
| Owner / fix | Ours, the generator `tools/cc-gen-script.ts`: each clone now carries its own copy of the referenced fields and data (`pidSubset`). New set `cc-script-100b`, all 68 blobs verified byte-equal after an ildasm round trip; queue `cc-script.json` points at it (docs/200 §8.1) |
| Note | A real script mod that refers to EA's compiler-generated names breaks the same way under a core mod. The game gives no ScriptError for it, only this crash |
| Effort | ~40 min analysis, ~20 min fix and regeneration; live: the rerun of `cc-script.json` (~35 min) |

## 20. Crash Q (2026-10-26 02:43:46 UTC): not a crash; the sim thread blocked over 60 s in EA's synchronous Library lot placement, `bp-cc-lots` on ts3native 0.36.84 (`asi.allocsites2`)

| | |
|---|---|
| Report | `FAIL: game died at 02:43:46 (agent not answering for 60 s (heartbeat frozen))`; the harness killed the game at 02:44:47. No EA xcpt file, no WER dump and no `ts3native.log` crash section after 02:37 [verified] |
| Hang, not crash | TS3W.exe stayed alive until the kill: `memory.csv` samples run to 10:44:40 PHT, the main thread drew 200-380 frames per 10 s up to 02:44:47 (`*-1920-frames.csv`). The sim thread stopped: the last `*-1920-seconds.csv` row before the kill ends ~02:43:40 after a 2.9 s task iteration, then nothing for ~67 s, and the ts3agent mailbox heartbeat (one increment per sim tick, `Mailbox.Poll`) froze [verified] |
| Where | `et_lot_cycle` started 02:43:33 (`library: true`, `bin: "cclot 01"`, Breezy source, 209 of 222 objects swapped to CC); `cycles_done` 0, so cycle 1, place phase. After the Edit Town summary lines `ts3native.log` shows `RoutingSystem_RebuildLotRouting` from `0xA48E2F` and lot-table entries on the sim thread, then a decompress block where the sim thread had decompressed 555 MB more (5,341 calls) than in the block before it [verified]. Decompress blocks print at most once per 60 s, so that work ran during the frozen minute [inferred]. While the sim was stuck the main thread spent 17-42% of each 10 s in `CAS/TextureCompositor` [verified]. Which EA call held the sim thread (the import `DownloadContent.ImportLotContentsFromExportBin` inside `BeginPlaceLotContents`, or `DownloadContent.ImportLotContents` inside `PlaceLotContents`) is not known: no stack was taken [open] |
| Cause | EA design: the game's own `EditTownPlaceTool.PlaceLotTask` makes the same two blocking native calls (it shows a "Processing" dialog while `ImportLotContents` runs) [verified, decompiled script]. A 10 MB Library lot with ~5,700 swapped texture keys and 209 new CC objects takes over 60 s, and the harness counts any sim stall over 60 s as dead (`game-alive.ts` `LIMIT_MS`) [inferred: progress continued (decompress, compositor), no fault, no loop seen]. Rewritten lot data is not implicated: the import went on to placement and texture work, and cc-lot-gen's read-back found 0 problems. Not `[MemDiag] AllocSites`: no fault and no hook line; at 1 in 64 sampling it can make the import slower, it cannot stop it [inferred] |
| Harness gap | The `game-alive.ts` header says a frozen heartbeat is captured before the FAIL line, but `watchGame` never calls `captureHang`: no `probe-out/queue/cc-lots-*-hang` directory, so no sim stack exists for this stall [verified] |
| Fix | ts3agent `et_lot_cycle` writes `<prefix>-ph-import`, `-ph-place`, `-ph-placed`, `-ph-bulldoze` marks into `ts3native.log` before each blocking call. These are written inline, with no sim tick needed, so the last mark names the call that stalls. Each cycle also records `import_ms` and `place_ms`. Harness, not done here: excuse the heartbeat while a declared long step runs (say up to 5 min after an `-ph-` mark), and call `captureHang` in `watchGame` before the FAIL line. Until then, `cc-lots.json` fails the same way whenever one placement takes more than 60 s |
| Effort | ~45 min triage; live: one rerun after the harness change (~45 min) |

## 21. Crash R (2026-10-26 04:23:57 UTC): the stutter profiler's Frida agent writes its hook into a read-only code page, `bp-cc-steady-same` on ts3native 0.36.85 (`asi.allocsites3`); with it the `bp-cc-steady-fresh` load hang and the `bp-cc-edittown` empty game bin

Static only (2026-10-26, a queue was running: no game run, no bisect). Sources: the job logs `probe-out/queue/steady-state-2026-10-26-bp-cc-steady-{same,fresh}*` and `cc-build-stress-2026-10-26-bp-cc-edittown.log`, EA's report `<Documents>/Electronic Arts/The Sims 3/xcpt <machine> 26-10-26 12.23.57.txt`, the probe event logs `probe-out/20261026-121804` and `-122417`, `dumpbin /disasm` on `game-copy/ts3native.asi.allocsites3`, the decompiled `BinCommon` / `Bin` / `EditTownModel`, and the Application event log (no TS3W.exe entry 12:15-12:30 PHT: EA's handler wrote the report, WER did not run). Ledger docs/51 §365.

### 21.1 `bp-cc-steady-same`: a crash 10 s into the profile, not after 24 min

| | |
|---|---|
| Timeline | game start 04:18:02, world loaded 04:22:59, ready 04:23:41, change set `5e455c65e6c1` 04:23:54, `profile-world.ts` (`--minutes 24 --repro live-cc-steady-same --no-samplers`) 04:23:55, repro step 1 `time` (45 s lead-in) answered 04:23:56, crash 04:23:57, harness `game died` 04:24:05 [verified job log]. "24 min" is the planned profile length. The repro never got past its lead-in, so zero `lss-*` marks is expected |
| Report | `ACCESS_VIOLATION writing to address 0x00593590; ... Type: MEM_IMAGE; Protection: PAGE_EXECUTE_READ` at `0xA1032240` in `frida-agent.dll` (`+0x71240`, temp copy `frida-e061b3a3...`). The call stack has only `frida-agent.dll` frames. Stack data holds `0x00593590`, the page `0x00593000` and `0x1000` [verified xcpt file] |
| Whose write | `0x593590` = `SimRender_TripleBufferSync`, the `publish` stage of `tools/frida/stutter-profile.js` (line 46; `0x593600` `PUBLISH_FRAME` is on the same page). `profile-world.ts` spawns that profiler as a **second** Frida session (`frida_tools.repl -l stutter-profile.js`, line 267) unless `--no-prof`; `--no-samplers` does not stop it. So the stutter profiler was installing its Interceptor hook at `0x593590` 2 s after its spawn, and the page was still read-only when Gum wrote [verified: address, script, spawn path] |
| Our native code at `0x593590` | none: `[Verify]` (the only ts3native probe there, `fdbSync`) logged `verify disabled (Enabled=0)`; no other ts3native hook and no S3SS / S3PP / Apex row at that address (docs/68, `hook-sites.ts`) [verified log + source grep] |
| AllocSites | **not on the faulting stack**, but not cleared. Gum allocates its trampolines near the target through `NtAllocateVirtualMemory`, which AllocSites hooks, and the 0.36.85 frame scan still raises first-chance AVs (§21.2). docs/201 §11 already showed an AV inside these hooks on a Frida thread breaking an agent's start. Whether that happened on the profiler's thread at 04:23:56-57 is unknown: all 16 first-chance slots were used at start-up [inferred] |
| How often | stutter profiler + allocsites3 attached cleanly in 6 other jobs that day (`bp-cc-allocsites`, `bp-bb-allocsites`, `bp-travel-allocsites`, `bp-ccs-nocc` x2, `bp-cc-edittown` 04:28) [verified queue rows]: a race, 1 in 7, the same class as Crash E (§7: a second agent attaching beside our exception-raising code) |
| Change set | `5e455c65e6c1` added the cc-2k-v2 set and a rebuilt `ts3fixes.package` (`f4233b9651a1 -> f74b292874db`, dirty tree) [verified change ledger]. Neither is on the stack, and the fault came 1 s after the repro started, before any CC step |
| Verdict | our harness combination: the stutter profiler's second Frida agent faulted while patching `0x593590`. The open contributor is our AllocSites hooks raising first-chance AVs on any thread [inferred]. Not EA code, not a mod |

### 21.2 The AllocSites hooks still fault in 0.36.85 [verified]

Both steady-state `ts3native.log` files fill all 16 first-chance slots at start-up on the main thread with `C0000005 at ts3native+0x11855`, reads of `0x70B9FFFB` (and `0x5100004C`); registers `ecx = esi = 0x70BA0000`, `edi = 0x70B80000`, `eax = 0x70B81008`. `+0x11855` is `mov dl,[ecx-5]` in `AsCallBefore`, called from `AsCodeRetSafe` (`+0x1199D`). The docs/201 §11 fix (0.36.84) only reads a stack word that lies in a cached module's executable range. The cache row for the module at `0x70B80000` covered `0x70BA0000`, but that memory no longer belonged to it: `g_asMods` rows are never dropped after `FreeLibrary`, so once a DLL unloads and its range is free (or holds another image), the scan reads unmapped bytes. The `__except` catches each one, but each is first dispatched through any Frida exceptor (docs/201 §11).

Fix (branch `fix/allocsites-stale-module` `e9bf7460`, worktree `../s3m-fix-allocsites-stale`, not merged; ts3native 0.36.87 = 0.36.86 v4 + this; asi `game-copy/ts3native.asi.allocsites5`): `AsCodeRetSafe` checks with `VirtualQuery` that the pages holding the 6 bytes before the word are committed image pages whose `AllocationBase` is the cached module; otherwise it drops the row (`hi = 0`) and skips the word. Host selftest: `msimg32.dll` cached, unloaded, then the stale address checked directly and as a stack word under a reservation: 0 access violations, row dropped; full `selftest ok`; `tools/selftest-all.ts` 118 PASS. Not shown: that the old code fails the new case (no separate negative build; it is the same read the log shows). Cost: one or two `VirtualQuery` calls per candidate return address, in the reserve / view hooks only (the heap hooks scan exe frames only).

### 21.3 `bp-cc-steady-fresh`: a load hang, not fallout of §21.1

`cycle.ts --save` started the game 04:24:17, agent up 04:25:15, "Mod Scripts Found" accepted 04:25:45, then `load_save: no reply ... (game paused, loading or blocked?)`; the harness detached and killed the game at 04:27:58 [verified]. The machine log shows TS3W.exe CPU 14 % -> 7.6 % -> 0.2 % from ~04:26:30 with the GPU idle: the process sat waiting, not loading [verified numbers; deadlock inferred]. `ts3native.log` ends after the start-up header with no `world load` line and the same 16 start-up AVs as §21.2. No crash report and no WER entry. Nothing from §21.1 carries over: the crashed job saved nothing and caches are cleared before every job; this is the only `no reply to 'load_save'` in the day's queue logs [verified grep]. Suspect: the AllocSites ntdll hooks taking their locks on a thread that holds the loader or heap lock, or a first-chance AV under the loader lock (docs/201 §11 saw a hang before the menu the same way) [inferred; no stack: the harness took no hang capture, see the §20 harness gap].

### 21.4 Queue bug: the steady-state marks could never match [verified]

`tools/harness/queues/steady-state.json` `logRequire` keys read `^diag: mem #d+ mark ev=lss-full ` (the JSON lost the backslash, so `#d+` matches a literal `d`). Even a clean 24 min run would have reported `0 lines (want >= 6)` for all 12 mark checks. Fixed on the branch (`#\\d+`, every key tested against a sample line); no other queue has the pattern.

### 21.5 `bp-cc-edittown`: the town game bin has no lot-only entry [verified]

Both runs (01:48 on allocsites2, 04:34 on allocsites3) ended within 10 s with `cycles_done 0`. The 04:34 result (after plot fix `c0a14d32`, which added the counts) says `no lot-only game bin entry fits an empty unowned plot (0 entries, 82 plots ...)`. So the plot search was not the problem; the entry list was empty. `EditTownModel.GetGameBinInfoList` -> `BinCommon.GetGameBinInfoList` lists `Bin.Singleton.Lots`, and `Bin.AddLot` (a player packing a lot into the town bin, `StoreLotContents`) is its only adder; `Baseline-BP-Spring` has none, so no `ExportBinType.Lot` entry with `HouseholdId == ulong.MaxValue` exists. The repro's game-bin default cannot work on this save. Fix on the branch: repro `et-lot-cycle` uses `library: true, bin: "breezy"` (the installed Breezy Getaway Library lot, 25 x 30, no CC; docs/200 §10). Risk: §20 showed a 10 MB CC Library lot blocking the sim past the 60 s heartbeat; the plain Breezy package is ~2 MB [inferred: well under 60 s, unmeasured].

### 21.6 Reruns

| Job | Needs | Pass |
|---|---|---|
| `steady-state.json`, both jobs | branch merged (regex, allocsites5) | 6 x each `lss-*` / `lsf-*` mark; no first-chance AV at `ts3native+` in the start-up section |
| `bp-cc-steady-same` crash | the same rerun, twice | no `frida-agent.dll` fault. A repeat with allocsites5 and 0 AllocSites AVs = Frida-side race: then pass `--no-prof` for AllocSites jobs, as §7 did for ProbeSnap |
| `bp-cc-steady-fresh` hang | the same rerun; on a repeat, a hang capture (sim + main stacks) before the kill | world loads; a repeat names the lock |
| `cc-build-stress --only bp-cc-edittown` | branch merged (repro args, allocsites5) | 5 x `et-empty` |

Effort: ~1.5 h triage + fix; live: ~2 x 60 min (steady-state) + ~15 min (edittown).

### 21.7 Root cause of §21.1: two Frida sessions patching page `0x593000` at once [verified static + job file times]

Frida 17.23.3 (installed) patches code through `gum_memory_patch_code_pages_via_mprotect` (frida-gum `gum/gummemory.c`, tag 17.23.3): per transaction it reads each target page's protection, sets it RWX, writes the jumps, then sets it back to RX. Each Frida session injects its own `frida-agent.dll` with its own Interceptor lock, so two sessions do not exclude each other. `profile-world.ts` started the stutter profiler and, about 1 s later without waiting, the `frame-times.js` session, which hooks `readpair 0x593440` and `drain 0x5937B0` on the same page as the profiler's `publish 0x593590`. If one session's "back to RX" lands between the other's VirtualProtect and its write, that write faults on PAGE_EXECUTE_READ (the report's stack holds the page `0x593000` and `0x1000`, the VirtualProtect arguments). The crashed job's files show both installing within 3 ms: the profiler's `-seconds.csv` / `-hitches.jsonl` at 12:23:57.633 PHT, its log ending inside `installStages` (after the task attribution line, before `hooks installed`), and `-frames.csv` created at 12:23:57.636 with the `[frames] hooked ... readpair drain` line. AllocSites is not needed for this (it hooks no `NtProtectVirtualMemory`); it is not excluded as a timing factor, nor is any other per-load cause, but none is required.

Fix (`profile-world.ts`): the side sessions (frames, inroom, livereads2, f4b) start only after the profiler prints `[ts3prof] hooks installed` or `NOT PROFILING`, exits, or 30 s pass. Load order stays profiler first, unload order unchanged. `tests-be.ts` already serialized its two sessions (frames ready, then the profiler). Verify: rerun `steady-state.json` (§21.6); each job's log has the `[frames] hooked` line after `[ts3prof] hooks installed` (file times of `-frames.csv` later than `-seconds.csv` by the profiler's install time), and no `frida-agent.dll` fault. Not covered: side sessions among themselves still start back to back (only frames is on by default).

## 22. Crash S (2026-10-26 07:59:29 UTC): mint `callvirt` on a receiver whose class has no vtable (freed managed object), `sv-ufb-farcrowd-loop` on ts3native 0.36.91 (`asi.unseenfb-all`)

Static only (2026-10-26: no game run, no bisect). Sources: `probe-out/queue/unseen-fallback-2026-10-26-sv-ufb-farcrowd-loop{.log,-0753-ts3native.log}`, EA `xcpt <machine> 26-10-26 15.59.29.txt`, `probe-out/profile/unseen-fallback-2026-10-26-sv-ufb-farcrowd-loop-*`, disassembly of `TS3W.exe` (capstone) and of the asi (map from a rebuild of `b311841e`: code identical, 6 header bytes differ). No minidump (the job is `releaseDefaults`, so no `[CrashForensics] Light`), no WER dump.

| | |
|---|---|
| Timeline | phase A forced ABBA on `unseenfb-far-crowd-loop` (SV, 30 spawned Sims, ultra speed): off #1 07:57:20, on #2 07:58:08, on #3 07:58:56, its `crowd_go` (send the crowd away) 07:59:05, fault 07:59:29 on the sim thread (8520), harness `game died` 07:59:33 [verified job log] |
| Report | ACCESS_VIOLATION reading `0x00000030` at `0x00E5201F`; EA stack `0xE5201F` <- `0xE5A48E` (`interp_runtime_invoke` calling `0xE53EB0`) <- `0xD7F06D` <- `0x4DE50A27` <- `Sims3Performance.asi` x2 <- `0x4DE51A27` <- `0xD7DCFB` <- `0x76B4C6` <- `0x4E2EDE`: an ordinary script task run (S3PP hooks the runner) [verified xcpt]. A second AV at garbage eip `0xDDE4C671` on the same thread follows inside the exception dispatch (EA's handler chain), a consequence |
| Where | `0xE51FC0` = the interpreter's `callvirt` cache-miss lookup (docs/130 §1, D16). `0xE51FDC`: `eax = obj->vtable`; `edi = vtable->klass`; `0xE52019 mov ecx,[edi+0xBC]` = `klass->vtable` (method array) = **NULL**; `0xE5201F mov eax,[ecx+eax*4]` with slot `eax = 0xC` faults at `0x30` [verified disassembly + registers: `ecx 0`, `eax 0xC`, `esi 0x493A1AD0` = callee `MonoMethod`, `edx 0x49622FE8` = its class] |
| What it means | Every live object's class has its vtable set up before the object exists. Here `vtable->klass` = `edi 0x81F17C58`, a GC-heap address, where loader classes live at `0x49xxxxxx`. That is the shape of a **Boehm free-list object**: word 0 links to the next free object, whose word 0 links to the next, and freed memory is cleared (`+0xBC` = 0). So the receiver was **collected while still referenced** (managed use-after-free) [inferred: register shape; the receiver address itself is in `[ebp-0x28]`, not in the report] |
| Our arm | On (`unseenfb` 1, ts3fixes `unseenfbplan` on, `unseenfbteleport` per arm). But **no cut and no teleport ran**: the last minute line before the fault reads `marks 704, sync 52 cut 0, jobs 307 cut 0, cut answers 0` [verified log]. So the cut path, `UfTeleport`, `UfPre`/`UfLeave` placement and the no-retry answers never ran. What did run in on windows: deadline sessions that ended uncut (one `sync plan 402 ms succeeded, 102 ms past its deadline`), the `0x83` MARK / CUT? / camera / colour queries (`UfMsg`, `UfCamMsg`, `UfColourAt`: native reads under `__try`, no managed object touched), and ts3fixes `UfWhy` per plan (`IsObjectOnScreen`, lot lookups, a per-tick `Dictionary<ulong,bool>`). None of it holds a managed reference outside the managed heap, and nothing in ts3native stores a `MonoObject*` (grep: only autshadow/monothread, both off). The native stack refs past the frame (`P5bLock+0x5A`, `LockAcquireAsync+0x4B9`, `0x7324F7` resource-cache find) are stale words in the interpreter's 0x4C74-byte frame [verified map + disassembly, inferred stale] |
| GC handles | The only weak references in our mods are the `[MemDiag]` "alive" ring (`MemSuspects.cs`, 512 reused `WeakReference`s). Mono's handle code in this exe re-registers disappearing links on growth (`0xE78913`-`0xE789F2`) and on `Target` set (`0xE78B19`-`0xE78B4B`) [verified disassembly], and the ring is read only at the 5-min memory samples, not at 07:59:29. Not implicated |
| Verdict | **Not attributed.** Fault in EA's interpreter on a freed managed receiver; our new unseenfb paths that act (cut, teleport) did not run; no mechanism found in ours that can free a referenced managed object. The off arm ran 48 s and on ran ~1.6 windows, so one crash does not separate the arms. Ours stays the default suspect until a rerun names the managed site |
| Fix (0.36.92) | Evidence, not a behaviour change: the first-chance logger (`[Crash] Log`, on in every build, also with `releaseDefaults`) now adds one line for an AV inside the interpreter (`0xE51000`-`0xE5A5FF`): `mono fault: in <caller method> (rm ...), callvirt of <callee method>, receiver <obj> vtable <word 0> klass <word 0 of that>; script stack: <16 frames>` (`MonoFaultText`, reads guarded; caller from `[ebp-0x10]`, receiver `[ebp-0x28]`, callee `esi`). Selftest (in `TestFallback`, after the LogFallbackStack case) builds a fake interpreter frame with a free-list receiver and checks every part, an unreadable frame, and an eip outside the range |
| Rerun | `unseen-fallback.json` `sv-ufb-farcrowd-loop` on `asi.unseenfb-all2` (same ABBA). Pass = no crash; a repeat now logs the `mono fault:` line: a receiver of a ts3fixes type, or a script stack through `UfPlan`/`UfWhy`, puts it on us (fix there); an EA/NRaas/GoHere-only stack with the arm on and no cut = EA GC, report as known-unfixed and keep the switch. A second crash in an **off** window clears the arm |
| Effort | ~1.5 h triage; live: the one job (~15 min) |

### 22.1 Repeat (2026-10-26 08:29:49 UTC) on 0.36.92 (`asi.unseenfb-all2`)

Static only (no game run, no bisect). Sources: `probe-out/queue/unseen-fallback-2026-10-26-sv-ufb-farcrowd-loop{.log,-0823-ts3native.log}`, EA `xcpt <machine> 26-10-26 16.29.49.txt`, `probe-out/profile/unseen-fallback-2026-10-26-sv-ufb-farcrowd-loop-{repro,activity}.jsonl` (the profile files hold the second run only).

| | |
|---|---|
| Fault | Same instruction `0xE5201F`, same shape: `ecx 0` (`klass->vtable` NULL), `eax 0xC` (the same vtable slot as the first crash), sim thread (28768 = `sim` in this run's route gate line) [verified log]. New line: `mono fault: in Sims3.Gameplay.Interactions.Interaction`2.DoLoop (rm 455C1888), callvirt of InsideLoopFunction.Invoke, receiver 8542F488 vtable 8542F348 klass 8542F320; script stack: Interaction`2.DoLoop < Interaction`2.DoLoop < Sims3.Gameplay.Careers.WorkInRabbitHole.InRabbitHole < RabbitHoleInteraction`2.Run < InteractionInstance.RunInteractionWithoutCleanup < Sims3.Gameplay.ActorSystems.Inter...` [verified] |
| What | EA's career loop: a Sim at work (`WorkInRabbitHole.InRabbitHole`) runs `DoLoop` with an `InsideLoopFunction` delegate made by `InRabbitHole` and held only in that task's interpreter frame; each loop pass after a yield invokes it. The delegate object is freed: receiver, word 0 and its word 0 are all GC-heap addresses 0x140 and 0x28 apart (a free-list chain in one small size class), so I-67-22-1 holds [verified: the three words]. A freed delegate held in a live, yielded task frame = the frame was not scanned by the collection that freed it (task state or frame arena chunk not rooted at that moment, docs/57 §arena, docs/130 G-B8 open), or the frame slot was overwritten by a reused arena chunk [inferred; which one is open] |
| Which Sim | Not one of the 30 crowd Sims [inferred]: in every activity sample the crowd Sims run the agent's Go Here (three were on "Work out", none at work), while 24-36 non-crowd townies sit in "Work Until End of Shift" at the day-shift samples. Ultra speed runs a game day in ~120 s, so at 6.2 min the world is in its third day shift. The crowd's `crowd_go` (user-directed Go Here, `cancel` false) touches only the crowd, so the agent did not cancel or push into the faulting Sim |
| Ours in the stack | None: EA frames only (no `UfPlan`, `UfWhy`, `RouteBackoffHook`, `Ts3Fixes` frame). I-67-22-2's expected-if-true holds for the stack. No ts3fixes or ts3agent code references `DoLoop`, `InsideLoopFunction`, careers or rabbit-hole interactions (grep: only `[AutShadow]` name lists, off) [verified grep]. NRaas Careers is not installed (script assemblies line); EA's `WorkInRabbitHole` runs unmodified |
| Arm | Both crashes in the third window (on #3), 24 s (first) and 9 s (second) after its `crowd_go` to the far lot; the last minute lines read `marks 148, sync 83 cut 0, jobs 60 cut 0, cut answers 0` (second) and `marks 704 ... cut 0` (first): the cut and teleport paths never ran in either run [verified]. The ABBA schedule is fixed by the repro, so "on #3 both times" is the same wall/game time both times and does not separate arm from game time; the off #4 window was never reached. The two runs differ by 13 s from the first command to the fault (4:31 vs 4:18) |
| Mechanism check | Nothing of ours allocates from the GC heap off the sim thread on a known path, roots or unroots task states, or touches arena chunks (`0xD71900`/`0xD71A20`/`0xD71B70` are hooked only by `[MonoThread]`, off). The paths both arms share (`[RouteAsync]` mode 1 sim waits, host-call serving) are not arm-specific. `Sims3Performance.asi` sits in the task runner on the first crash's EA stack (docs/67 §22 Report) and is in both arms |
| Verdict | **Not ours on present evidence, not yet cleared.** Repeatable EA managed use-after-free of a career-loop delegate under this repro (30 extra Sims, ultra speed, about 25 Sims in rabbit holes per day shift); the unseenfb acting paths did not run. Our switch is in the on state at both faults, so the arm is not cleared until an off-only run passes the same time |
| Control (not run) | Copy `unseenfb-far-crowd-loop` to a repro `unseenfb-far-crowd-loop-offctl` with `"arms": ["off","off","off","off"]` (loop rules take no arms override) and a job `sv-ufb-farcrowd-loop-offctl` = the same job (asi `unseenfb-all2`, seed 12345, `Baseline-SV-Spring`, release defaults, same ini) plus `GcThreadCheck.Enabled: 1` (counts any GC allocation off the first allocating thread) and `CrashForensics.Light: 1` (minidump: the task state and arena chunk around `[ebp-0x28]`). Crash near 6.2 min with all arms off = EA/repro, keep the switch, report known-unfixed; passes 12 min = back on our on arm (then run the same with `["on","on","on","on"]`) |
| Logging gap | `Log` caps a line at 512 bytes, so the 16-frame script stack is cut after 6 frames (`ActorSystems.Inter`). Split the `mono fault:` text over several lines in the next native build; not built here (no behaviour change, no gain for this verdict) |
| Effort | ~1 h triage; live: the control job (~15 min) |

### 22.2 Control passed; static check of the on arm; `[ArenaGuard]` and narrowing jobs (ts3native 0.36.95, not run)

Static only (no game run). Sources: `probe-out/queue/ufb-crash-control-2026-10-26-sv-ufb-farcrowd-loop-offctl{.log,-0837-ts3native.log,-probe.log}`, the two crash runs' logs (§22, §22.1), the ScriptError files of all three runs, `UnseenFallback.cs`, `RouteBackoff.cs`, `Fixes.cs`, `unseenfb.cpp`, `unseen.cpp`, `GetOptionHook`, docs/57 §3.2, docs/120 §9.1, docs/130 §1.

| | |
|---|---|
| Control | `sv-ufb-farcrowd-loop-offctl` (2026-10-26 08:37-08:52 UTC, `asi.unseenfb-all2`, seed 12345, every arm off): **pass**, no crash in 14.5 min (12 firings, 3 cycles), past the 6.2 min both crashes hit [verified, run record]. Not a pure arm control: it also had `GcThreadCheck` (two detours on the GC slow paths) and `CrashForensics.Light`, and a loop rule `until events 1000` instead of `perArm 60` [verified, both job JSONs]. `GcThreadCheck` logged no `FOREIGN` line (off arms only) [verified log]. So "on arm implicated" rests on 2 crashes vs 1 pass with a timing confound [inferred] |
| Earlier GC use-after-free | None in docs/67 before §22 (Swarm §1 is an EA native refcount free, not the managed heap) [verified grep] |
| What only the on arm runs | ts3fixes: `UfWhy` per plan (game reads, `World.IsObjectOnScreen`, `LotManager.GetLotAtPoint`, `lot.Corners`, posture `Satisfies`, a per-tick `Dictionary<ulong,bool>`), `UfCamAsk` / `UfSend` (`GetOption2` with `0x83` bits), `UfLimiter`, `sOwProbe.Enter/Exit`, and a `try { plan } finally { CUT?; Exit }` frame around a plan that yields (own-task async). ts3native: `UfMsg`, `UfCamMsg` (camera + frustum reads under `__try`, 30 ms cache), deadline sessions and job deadlines on marked plans (cuts 0 in both crashes) [verified source] |
| Checked, not a way to lose a root | (1) **IC/GetOption2 during GC:** `GetOptionHook` answers `0x83` natively; `UfMsg`/`UfCamMsg`/`UfColourAt`/`UfcListMsg` allocate nothing from the GC heap and call no managed code; they run on the sim thread, the one thread Boehm registers and scans (docs/120 §9.5 #3) [verified source]. (2) **Managed refs in native memory:** the channel carries the route's native handle (`g_ufH`), `1`/`2` or float bits; nothing stores a `MonoObject*` outside autshadow/monothread (both off) [verified grep]. (3) **Statics:** every new static is on `Fixes`, which already holds reference statics, so its static area is GC-scanned [inferred, Mono 1.2 `mono_class_vtable`]; `UfLimiter`'s `object Result` was never written (cuts 0). (4) **Other threads:** the camera reads are sim-thread only; the worker side (`UfJobEnd`, `UfOverNote`) writes counters only [verified]. (5) **Stack bounds:** no hook in the on arm touches the GC thread table, `GC_push_other_roots` or the sim stack [verified]. (6) **Method swaps / patch host:** every `SwapMethod` runs at load, the same in both arms; the agent's `patches` only flips `ts3fixes.switches` [verified `PatchHandlers.cs`]; `MethodInfo.Invoke` (`UfStuckDefer`) belongs to `unseenfbstuck`, off in this repro. (7) **Yield inside our try:** allowed by the may-yield test `0xE53550` (it refuses only while a finally runs); our finally has no yield [verified docs/130 §1]; EA yields inside try/finally all the time |
| Script errors | One NRaas ErrorTrap file per run, all at load and identical in content (Out of World bicycle/broom correction log), none in play in any of the three runs [verified]: no task ended by a logged managed exception in an on window |
| The one mechanism that fits the shape | EA's frame-arena pool (docs/120 §9.1, verified in monohost): a task that ends by a managed exception with frames on its chain is released twice (`0xD71B70` never clears `+0x13C`), so its chunk sits in the pool twice; the second copy later goes to another state while a yielded task still uses the chunk. That state's frames overwrite the yielded frame's locals, the GC frees what only those slots held, and the yielded task reads a free-list object: exactly a delegate held only in `DoLoop`'s frame across yields (§22.1 What). The trigger is EA's, the timing decides which yielded frame is hit [inferred: no in-play exception logged, but EA tasks can end by exception without ErrorTrap seeing it]. The other open way is a task state not rooted at one collection (docs/130 G-B8) [open] |
| Verdict | **Not attributed by static analysis.** No on-arm path frees, hides or unroots a managed object. The on arm changes the game's timing (per-plan `UfWhy`, deadline sessions) and with it which yielded frame a latent EA arena duplicate hits [inferred]. Static analysis cannot split arm from timing: narrowing runs below |
| Fix (0.36.95, branch `bugfix/ufb-gc-uaf-arena-guard`) | `[ArenaGuard] Enabled` (default 0 until its run): docs/120 §9.2's arena fix without `[MonoThread]`: own 5-byte detours at `0xD71900` (drop pooled chunks that are in use before each grow; first 8 drops logged) and `0xD71B70` (clear `+0x13C` after release-all, count non-empty chains); skipped when `[MonoThread] GcLock=1` owns the sites. Minute line `arena guard: release-all on a non-empty chain N, pooled chunks in use dropped M` (`ts3native_mtprobe` +0x6C/+0x70). Selftest `TestArenaGuard` (`arenaguard-test.h`): a fake pool reproduces Crash S's pattern (task released twice, yielded task keeps a delegate, the next state's grow gets the same chunk and clears it = negative control), then shows both guard parts stop it. Asi `game-copy/ts3native.asi.unseenfb-all5`; queues on `unseenfb-all4` retargeted to it (no behaviour change with `[ArenaGuard]` off) |
| Narrowing (not run) | `tools/harness/queues/ufb-crash-narrow.json`, all on `unseenfb-all5` with the control's `GcThreadCheck` + `CrashForensics.Light`: (1) `sv-ufb-farcrowd-loop-ctl2` = the crashing ABBA with the control's tools: crash near 6.2 min = arm implicated without the confound (and a minidump); pass = the control pass was timing. (2) `sv-ufb-farcrowd-loop-guard` = the same + `[ArenaGuard]`: pass with `dropped >= 1` = arena duplicate confirmed and fixed (turn it on by default); `dropped 0` = not the arena path. (3) `sv-ufb-farcrowd-loop-planonly` (repro `unseenfb-far-crowd-loop-planonly`: native mode held at 0, only `unseenfbplan` flips, so marks are refused and only `UfWhy` + camera queries run), only if (1) crashed. Stop at the first decisive job |
| Effort | ~2 h static + build; live: up to 3 jobs (~15 min each) |

### 22.3 Narrowing results; static check of the patch host and of frame rooting (no build)

Static only (no game run). Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-{ctl2-0909,guard-0916}-ts3native.log`, `ufb-crash-narrow-1026.out`, `Fixes.cs` (`SwapMethod`, `Patch`, `RabbitHoleHook`), `RouteBackoff.cs` (`PlanHook`), `UnseenFallback.cs` (`UfPlan`), decompiled `RabbitHole.cs` / `SimIFace Route.cs`, capstone reads of `TS3W.exe`.

| | |
|---|---|
| ctl2 (0.36.95, `unseenfb-all5`, crashing ABBA + `GcThreadCheck` + `CrashForensics.Light`) | **CRASH at 6.2 min** in on #3, same instruction `0xE5201F` [verified log]. `mono fault: in Sims3.Gameplay.Abstracts.RabbitHole.EnterOrig, callvirt of BeforeEnteringRabbitHoleDelegate.Invoke, receiver 840467D0 vtable 84046708 klass 840466E0; script stack: RabbitHole.EnterOrig < RabbitHoleHook.RouteNearEntranceAndEnterRabbitHole < RabbitHoleInteraction`2.RouteNearEntranceAndIntoBuilding < MetaRabbitHoleInteraction`2.Run < AutonomouslyGoForMealInRestaurant.Run < ...`: a townie going out to eat; the shipped rabbit-hole hook is on the stack (names are swapped: `EnterOrig` with class `RabbitHole` = the original body, `RabbitHoleHook.RouteNearEntrance...` = `EnterHook`, docs/04 F8). No `FOREIGN` line, no minidump written [verified]. I-67-22-8 holds: the tools were not why the control passed |
| guard (same + `[ArenaGuard]`) | **CRASH at 6.1 min**, `Interaction`2.DoLoop` callvirt `InsideLoopFunction.Invoke` (`WorkInRabbitHole.InRabbitHole`), minute line `arena guard: release-all on a non-empty chain 0, pooled chunks in use dropped 0` [verified]. I-67-22-6 refuted for Crash S: no task ended with frames on its chain, no pooled chunk was in use |
| Both | `cut 0`, `jobs ... cut 0`: the cut and teleport paths never ran (phase A) [verified]. Queue stopped after two crashes; `planonly` not run (resume line in the `.out`) |
| Patch host swap (question 1) | `SwapMethod` copies the 40-byte `MonoMethod` (flags, token, klass, signature, slot; not the name at `+0x18`). After `Patch`, the stub `EnterOrig` carries the original's token and class, so the interpreter builds its `RuntimeMethod` from `RouteNearEntranceAndEnterRabbitHole`'s IL and frame size; `EnterHook` carries the hook's. `RouteNearEntranceAndEnterRabbitHole` is not virtual (decompiled), so `callvirt` binds directly both ways. Arguments travel as in any interpreted call: the caller's eval-stack slots in its arena frame, which the callee reads in place. The swap creates no frame, copy or argument area of its own [verified source + F8; frame layout docs/130 §1] |
| How a yielded task's frames are rooted (G-B8, part) | (a) **State:** the TaskObjects walk allocates the state (`0xE52D70`, `GC_malloc(0x150)`, stored in the EA-heap map entry `+8` at `0xD7FDC5`) and then calls `0xD747C0(state)`, which puts a `GC_malloc(8)` pair `{0, state}` into the host container at `host+0xB84` (`0xD727F0`; node allocator `0xD72010` -> `0xD70CE0`, a `GC_malloc_uncollectable` user per docs/57 §1). Removed only at task end, just before the release (`0xD7F157` -> `0xD71EB0`, then `0xE53E80`; also `0xD72639`) [verified bytes; container nodes uncollectable inferred from the docs/57 row]. (b) **Frames:** arena chunks are `GC_malloc_explicitly_typed(0x7FC, [0x11F424C])`; `[0x11F424C]` = `(GC_new_proc(0xE69520) << 2) | 2` (`0xE695E8`), a mark procedure that pushes every word of `[chunk+8, chunk[0])` = previous-chunk link + all frames up to the bump top through `0xE508D0` (conservative push, mark-stack overflow handled by `0xE4C390`) [verified bytes]. (c) **Saved frame cells** at a yield are `GC_malloc_uncollectable(0x2C)` (docs/130 §10.5). So the delegate in `EnterOrig`'s and `EnterHook`'s argument slots and in `RouteNearEntranceAndIntoBuilding`'s frame is scanned as long as the state is in the container and the chunk top is above it |
| What the freed delegate says | In ctl2 the `BeforeEnteringRabbitHole` delegate is held in at least two frames of one task (`RouteNearEntranceAndIntoBuilding`'s eval stack for the call, `EnterHook`'s for the `EnterOrig` call); in the DoLoop crashes the `InsideLoopFunction` sits in `InRabbitHole`'s and two `DoLoop` frames. A slot overwrite cannot lose all of them, so one collection missed the **whole task** (state not in the container at that moment, or the chunk chain/top wrong) [inferred]. That is EA host or interpreter state, not a slot only our hook adds |
| On-arm code inside this path (question 2) | `route.PlanToSlot` ends in `return Plan()` (virtual) -> `ScriptCore.Route.Plan` = our `RouteBackoffHook.PlanHook` -> `UfPlan`: `UfWhy` (`lot.Corners`, `GetCenterPosition`, per-tick `Dictionary` clear: small GC allocations), `UfCamAsk` / `UfSend` (`GetOption2` `0x83`, native answer, no allocation), the MARK, and `try { own-task async plan, yields } finally { CUT?; Exit }` [verified source]. So in the on arm this task runs extra managed allocations and an extra try/finally frame **below** `EnterOrig` while `beforeEntering` is live. Neither stores, hides nor removes a reference (I-67-22-7); they change where collections happen [inferred]. The DoLoop crashes have none of our frames on the stack |
| Release exposure (question 3) | v0.1.5 ships `RabbitHoleHook`, `GoHomeHook`, the lot-trip hooks and `RouteBackoffHook` (default on in release defaults); `unseenfbplan` is off by default (`Instantiator.kUnseenFallbackPlan = false`) and native `[UnseenFallback] Enabled` defaults to 0 [verified source]. The hooks' argument handling is the game's own (row above), and no Crash S shape (`0xE5201F` free-list receiver) appears in docs/67 before §22 across release soaks and natural runs [verified grep]. **No evidence that shipped releases are exposed**; they are exposed only to whatever EA rooting gap this is, at EA's own rate [inferred] |
| Verdict | **Not attributed; no fix built.** The patch host keeps arguments where the game keeps them; no on-arm path unroots anything; the arena duplicate is refuted. What remains is a whole-task rooting miss that the on arm's timing (plus 30 extra Sims, ultra speed, ~25 Sims in rabbit holes per shift) makes reachable at ~6 min. A `GC.KeepAlive`-style static for `beforeEntering` would only move the fault to the next frame-only object of the same task (the DoLoop crashes have no hook at all), so it was not built |
| Next | (1) `planonly` (queued, resume `--from sv-ufb-farcrowd-loop-planonly`): splits `UfWhy`+camera (allocation timing) from MARK/deadline/try. (2) Evidence for the task miss, next native build: at the `mono fault:` line also log whether the faulting state (`[0x11F4170]`) is in the `host+0xB84` container and its chunk chain with tops (`[state+0x13C]`, `+0`, `+8`), and add a `[GcThreadCheck]`-style counter for collections that run while `host+0xBC8` is set (a task running) vs between tasks. (3) If the miss is EA's: keep `unseenfbplan` off-by-default (it is), report known-unfixed, and test whether forcing a collection only between tasks (`MonoScriptHost_Simulate` GC step) removes it |
| Effort | ~1.5 h static; live: `planonly` (~15 min) |

### 22.4 Task-rooting logging at the `mono fault:` line and `[GcRootCheck]` (ts3native 0.36.96, not run)

Static + build, no game run. Sources: capstone reads of `TS3W.exe` (`0xD747C0`, `0xD727F0`, `0xD71EB0`, `0xD7E625`, `0xE53EB0`, `0xE52D70`), docs/57 §1, docs/130 §1, docs/199 §2.

| | |
|---|---|
| Container `host+0xB84` [verified bytes] | `MonoScriptHost_Init` allocates it with `GC_malloc_uncollectable(0x1A0)` (`0xD7E625`, stored at `0xD7E670` through `host+8`): 101 bucket heads at `+0`, count `+0x198`, `+0x194` = -1. Insert `0xD727F0` and remove `0xD71EB0` hash the state pointer by `% 101` (magic `0x446F8657`); a node is `GC_malloc(8)` `{next, state}` (`0xD747C0`), collectable but held from the uncollectable bucket array, so it is reachable as long as it is linked. This settles the §22.3 "inferred" note on the node allocator |
| Outer state [verified bytes] | `0xE53EB0` saves the previous `[0x11F4170]` at `[ebp-0x60]` (`0xE53ECC`) before it stores its own state; its helpers keep `ebp`, so at a fault in `0xE51000`-`0xE5A5FF` `[ebp-0x60]` is the state of the enclosing invocation (0 or a stale value for a task's outermost `ves_exec`) |
| New fault line | After `mono fault:` the first-chance logger adds `mono fault task: container <tbl> (<n> states), task <id>; current <s> (container 0/1, gc <F/M/-/B/?>/k<kind>; chain <chunk> top <t> end <e> < ...); outer ...; root ... (host+0xBC8 -> slot); frames (c/o/r chunk, ! = at or above top) <base>:<c0> ...; <n> above top; receiver <obj> gc <state>/k<kind>`. Frames = the thread's interpreter context `+8` chain (`MonoInvocation +0x10` arena base) up to 16, each placed in the current, outer or root state's chain; `!` = base at or above that chunk's top, so EA's mark procedure `0xE69520` (pushes `[chunk+8, top)`) did not scan it. Block state from the libgc header: `F` = EA's free bit (`+0x9C`, on a free list), `M` = marked, `B` = free block (invalid map), `?` = no header |
| What each answer means | Root `container 0` = the task was not rooted (the whole-task miss of §22.3, EA host side); root `container 1` + a delegate frame with `!` or `-` = the frame sat outside its chain's scanned range (chain/top wrong); root `container 1`, frames in range, receiver `F` = the frame was scanned at this moment, so the miss was earlier (a collection while the chain or top was different): then `[GcRootCheck]` below is the tool |
| Log cap | `Log` lines are now 1536 bytes (was 512, which cut the 16-frame script stack after 6 frames, §22.1); the `mono fault:` stack buffer is 1100 bytes |
| `[GcRootCheck] Enabled` (default 0, logging only) | Chained onto `GC_notify_event` (`[0x1234360]`, the `0xD7E62A` store; after `[MemDiag]`'s hook when that is on): at event 1 (after `GC_stop_world` in `GC_stopped_mark`, before the final root push) it walks the container: a node or state on a GC free list, a chunk unreadable, top outside `[chunk+0xC, end]`, a chunk on a free list, a chain longer than 256 = mismatch. With a task running (`host+0xBC8` set) also: root not in the container, and any live frame at or above its chunk's top. First 16 mismatches logged (`gc root check: collection N: <why>, state S (method M) chunk C; chain ...`), minute line `gc root check: collections N (task running M), states max, chunks max, mismatches`. Reads only, one container walk per completed cycle |
| Selftest | `TestGcRoots` (`gcroots-test.h`): fake container, states, chunks and libgc headers; block states, membership, chunk lookup, three bad chains, a frame above its top, a root outside the container, a clean world (0 mismatches), the fault line text and truncation in a 64-byte buffer. `build.cmd` selftest and `selftest-all` pass |
| Build | asi `game-copy/ts3native.asi.unseenfb-all6` (branch `bugfix/ufb-gc-root-logging`); `ufb-crash-narrow.json` retargeted to it with `GcRootCheck.Enabled: 1` in all three jobs (no behaviour change: logging only) |
| Next | Resume `ufb-crash-narrow.json --from sv-ufb-farcrowd-loop-planonly` on all6; on the next Crash S read the `mono fault task:` line (root container, `!` frames, receiver state) and the `gc root check:` mismatches before the fault. Collections while a task runs vs between tasks: the minute line's `task running` share |
| Effort | ~1.5 h static + build; live: the queued job (~15 min) |

### 22.5 Fourth crash on 0.36.96: task rooted, frames in range, receiver freed; frame-word mark audit (ts3native 0.36.97, not run)

Static + build, no game run. Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-ctl2-0948-ts3native.log`, capstone reads of `TS3W.exe` (every direct caller of `GC_free` `0xE4BEB0` and of its thunk `0xD71810`, `0xE52DD0`, `0xE53E80`, `0xE535E0`, `0xD714C0`/`0xD84830`, `0xD70DC0`, `0xD718A0`, `0xE50DE0`, `0xE69520`, `0xE508D0`, the method-entry bump `0xE563B2` and return pop `0xE54437`, the yield save `0xE59751`), `unseenfb.cpp`, `unseenfbc.cpp`, `plandeadline.cpp`, `LockAcquireHook`.

| | |
|---|---|
| Run | ctl2 on `asi.unseenfb-all6` (ts3native 0.36.96, `GcRootCheck` + `GcThreadCheck` + `CrashForensics.Light`): **CRASH at 7.8 min**, same instruction `0xE5201F`. `mono fault: in Interaction`2.DoLoop (rm 63F7EC18), callvirt of InsideLoopFunction.Invoke, receiver 855B7CF8 vtable 855B7CD0 klass 855B7CA8; script stack: DoLoop < DoLoop < ReadBook.ReadLoop < ReadBook.DoReadBook < ReadBook.Run < ... < Sim.Simulate < ScriptProxy.Simulate`: a Sim reading a book, not a rabbit hole this time [verified log] |
| Task line | `current 80E50E00 (container 1, gc M/k1; chain 8D7F0000 top 8D7F0354 end 8D7F07FC)`, outer none, root = current, 10 frames all in chunk c0 and below its top (`0 above top`), `receiver 855B7CF8 gc F/k4` (EA free bit set) [verified log] |
| `[GcRootCheck]` | last minute line before the fault: `collections 1953 (task running 0), states max 376, chunks max 427, mismatches 0`; no `FOREIGN` line [verified log]. So every collection ran between tasks, every container state, node and chunk was live with its top inside its chunk at every stopped mark, and the faulting task was rooted with its frames in the range EA's mark proc scans |
| Arm | planonly (native mode 0, only `unseenfbplan` flips) passed; every run with native mode 1 crashed (4 of 4); the all-off control passed (Rodney's summary of the queue) |
| Refuted | I-67-22-12's mechanism (state missing from the container or a top below a live frame): state in the container, frames below top, 0 mismatches over 1,953 collections. Collections inside a task (a nested invoke collecting while a task's frames are half built): 0 of 1,953 [verified log] |
| Explicit free (question 1) | `GC_free` has 16 direct callers plus 3 through `0xD71810` [verified scan]: task-state teardown `0xE52DD0` (saved frame cells, then state `+0x30` outer `MonoInvocation`, `+0x2C` argument buffer, `+0x38`, `+0x14` `ThreadContext`: the buffers `0xE59FD0` allocates at `0xE5A081`-`0xE5A0F4`), the state itself (`0xE53E80`), saved frame cells (`0xE535E0`; `0xD84830` via `0xD714C0`), arena list nodes (`0xD71A20`, `0xD71B70`), the free slot `0xD70DC0` of EA's uncollectable-allocator vtable `0x108B258`, shutdown `0xD80AD3`-`0xD80B4D`, Boehm's finalizer entry free `0xE4AC4C` and `GC_print_all_errors` `0xE4F35F`. None is handed a managed object; the delegate's block is kind 4, the explicitly typed kind none of these buffers use. Finalizers run on demand on the sim thread only (docs/57 §5), and our mods hold no disappearing link near a delegate (§22 GC handles). `mono_delegate_free_ftnptr`-style frees do not exist in this build's call graph to `GC_free` [verified] |
| Other thread (question 2) | No GC allocation slow path off the sim thread (`GcThreadCheck`, both detours, no `FOREIGN`) [verified log]; a collection starts only from an allocation (`GC_collect_or_expand`) or EA's explicit collect on the sim thread. Mode 1 does not change what the route worker calls: its job deadline uses the search exit stubs (no Mono), `UfJobEnd` writes counters; door asks still go through the host-call proxy to the sim thread. Not covered: a `GC_collect` from another thread with no allocation, so 0.36.97 counts collections off the first collector thread [verified source; collect callers inferred] |
| Mode 1 native paths | `UfMsg` / `UfTake` / `UfSyncEnd` (globals, counters), deadline sessions (`PdIcBegin`, QPC and thread flags), `UfLockGate` (a timed `+0x918` acquire before a marked sync plan, through `LockAcquireHook` -> `LockAcquireAsync`, which pumps host calls exactly like every `[RouteAsync]` wait; a nested `mono_runtime_invoke` there uses its own stack state and pool chunks), C1-C4 sites only with `Sites=1`. None allocates from the GC heap, frees, stores a `MonoObject*` or touches arena or container. The only Mono entry is the existing pump, and no collection ran inside a task [verified source + log]. So mode 1 still acts through timing: more pump points inside marked plans, failed/cut plans and the script paths after them [inferred] |
| Stack-slot liveness (question 3) | `InsideLoopFunction loopFunc` is a `DoLoop` argument; across the yield the frame stays in the arena (the sleep path `0xE59751` saves cells and returns without popping; only a normal return `0xE54437` lowers the top) and the C stack and registers are gone, so between tasks the frame word is the only place it can be. Register-only or eval-only liveness does not apply across a yield [verified bytes, inferred for this method's IL] |
| Corruption vs free (question 4) | Receiver word 0 = `855B7CD0`, its word 0 = `855B7CA8`: a 0x28-byte stride chain inside one block, and the receiver's EA free bit is set (`gc F`). A vtable overwrite would not set the free bit; this is a 40-byte kind-4 object on its free list [verified bits; interpretation inferred] |
| New static find: the chunk descriptor sits at the chunk end | `0xD718A0` makes chunks with `GC_malloc_explicitly_typed(0x7FC, [0x11F424C])`; `0xE50DE0` stores that descriptor in the object's last word, `op + size_words*4 - 4` = `+0x7FC` (2048-byte class), and `0xD718CC` sets the chunk end `c[1]` = `chunk + 0x7FC`. So the descriptor is the first word past the frame area. The method-entry bump (`0xE563B2`) allows a frame that ends exactly at `end`; any write past a frame's declared size (`RuntimeMethod +0x38`) at the chunk end replaces the descriptor, and the collector then marks that chunk by the wrong descriptor (a small length scans almost nothing) until the chunk is allocated again, which pool reuse never does [verified bytes; an overflowing writer not found, inferred] |
| Open explanations | (A) the frame word holds the delegate at the freeing collection and the mark skips it: overwritten descriptor, chunk not reached, or a mark-stack loss (`0xE4C390`); (B) no frame word holds it at that collection: the reference is put back into the frame later from somewhere that is not a root; (C) a free not seen statically. The present logging cannot split them: it checks chain shape, not what the mark did |
| Build (0.36.97, branch `bugfix/ufb-frame-mark-audit`) | `[GcRootCheck] Enabled=1` now also runs `GrAudit` at `GC_notify_event` 3 (top of `GC_finish_collection`, marks complete, nothing reclaimed): for every container state with a sound chain, each chunk must be marked and end in `[0x11F424C]`, and every word of `[chunk+8, top)` that resolves by `0xE508D0`'s own map rule to an object that is neither marked nor free-listed (collectable kinds) is a miss. Lines `gc frame audit: collection N: frame word to an unmarked object / chunk not marked / chunk descriptor overwritten, state S (method M) chunk C (gc x/kN) word W = V (kN); chain ...` (first 16), minute line `gc frame audit: marks, frame words, unmarked, chunks not marked, descriptors overwritten, read faults`; `gc root check:` adds `other thread N` (collections off the first collector thread, first 4 logged). The `mono fault task:` line adds the receiver's mark bit under the free bit (`GC_free` sets it, a sweep leaves it clear), its block stamp vs `GC_gc_no`, and `current chain descriptors ok`/value per chunk. Reads only. Selftest `TestGcRoots` extended (healthy audit, free-listed word skipped, unmarked word, overwritten descriptor, unreached chunk + its link, no read faults, fault-line fields); `build.cmd` and `selftest-all` pass. Asi `game-copy/ts3native.asi.unseenfb-all7`; `ufb-crash-narrow.json` retargeted to all7 (`GcRootCheck.Enabled: 1` already in every job) |
| What each answer means | `chunk descriptor overwritten` before the fault = (A) by overflow: fix = rewrite the descriptor on every chunk grant in `[ArenaGuard]`'s `0xD71900` detour and find the method whose frame overruns (the line names the state's method; frame size vs IL max stack). `chunk not marked` = the state's `+0x13C` link was not traced (state block scanned by a wrong descriptor). `frame word to an unmarked object` with chunk marked and descriptor ok = the marker lost the range (mark-stack overflow path). Audit clean through the fault, receiver mark bit 0 = (B): the reference re-entered the frame from outside the roots; next = a `GC_free` ring plus a watch on the frame slot. Receiver mark bit 1 = (C), an explicit free |
| Run | rerun `sv-ufb-farcrowd-loop-ctl2` on all7 (~15 min). Cost: one walk of ~400 chunks per completed mark, ~1-3 ms per collection at ~4 collections/s [inferred], a correctness run, not a timing run |
| Effort | ~2 h static + build; live: one job |

### 22.6 Fifth crash on 0.36.97: the faulting chunk's descriptor is 0; EA's deferred finish explains the audit's unmarked words; descriptor watch and restore (ts3native 0.36.98, not run)

Static + build, no game run. Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-ctl2-1014-ts3native.log`, capstone reads of `TS3W.exe` (`0xE69520`, `0xE508D0`, `0xE49980`, `0xE49DA0`, `0xE49F10`, `0xE49FB0`, `0xE4A320`, `0xE4C060`-`0xE4C26A`, `0xE4BC30`-`0xE4BFDA`, `0xE48FB0`, `0xE50090`-`0xE501ED`, `0xE50DE0`, `0xD718A0`-`0xD71B42`, `0xE56320`-`0xE56444`, `0xE63D0E`, the kind table `0x1194868`).

| | |
|---|---|
| Run | ctl2 on all7: **CRASH at 7.5 min**, AV reading `0x56` at `0xE56370` (`test [edx+0x44],0x100`), not `0xE5201F`: the interpreter's call setup reads the callee `RuntimeMethod` from `[ebp-0x58]` and it is `0x12`. `mono fault: in Interaction`2.DoLoop ... receiver 00000000 ... script stack: unreadable` (the receiver read only applies to the `callvirt` helper). Task line: current = root `818DE000`, container 1, chain one chunk `80017000` top `800172E4` end `800177FC`, 9 frames all in it below top, **`current chain descriptors 00000000`** [verified log]. A delegate invoke on a dead delegate gives a garbage method the same way the `callvirt` path gives a NULL vtable: the same Crash S [inferred] |
| Audit lines | minute lines up to 1 min before the fault: `chunks not marked 0, descriptors overwritten 0, read faults 0`, `unmarked` 36 -> 226 over 192 -> 201 completed marks (collections 1,018 -> 1,439 counted at event 1). The 16 detail lines all went to collections 974 (`Ts3Agent.PollTask.Simulate` 4 words, 2 objects, k0; `ScriptProxy.Simulate` 2) and 1017 (`ScriptProxy.Simulate` 10 words, 5 objects, 7 k4 / 3 k0) [verified log]. No later minute line was written, so the audit's counters do not cover the last minute, and the detail cap hid every later finding |
| (1) What the mark proc scans [verified bytes] | `0xE69520` rewrites its arguments and tail-jumps to `0xE508D0(chunk+8, [chunk], msp, msl)`: every word of `[chunk+8, top)`, dead locals and stale eval slots included, conservatively. There is no live-extent logic, so a word inside that range at the moment the chunk is scanned is always marked |
| EA defers the finish [verified bytes] | `0xE49DA0` (collect-a-little; only caller `0xE49F10`) runs `GC_mark_some` steps, then the stopped mark `0xE49980`; on success it does **not** finish: it adds 1 to `GC_gc_no` (`0xE49EE5`) and sets `[0x11F2F94]`. `0xE49F10` calls the finish `0xE49A50` (event 3) only after its stop function lets it continue (`0xE49F5A`); on the per-frame path (`0xE4A050` -> `0xE49FB0` -> `0xE49F10`, EA's budget stop function) the finish can come a frame later, with scripts running in between |
| EA's guard for that window [verified bytes] | every small allocation calls `0xE4C1E0` (callers `0xE4BC7E`, `0xE4BDFD`, `0xE4BE6E`, `0xE4BFDA`, `0xE50E53`): clear the object's free bit (`+0x9C`) and stamp its block `+0x18 = GC_gc_no`; a new block gets the same stamp (`0xE48FB0`), clear-marks stamps too (`0xE4C0F0`). Reclaim skips a block whose stamp equals `GC_gc_no` (`0xE500B6`, `0xE50182`). So whatever is allocated between mark and finish is kept for this cycle, block-wise |
| Verdict (1) | The audit runs at event 3, after that window, and did not look at stamps: an object allocated in the window and stored in a frame shows as "unmarked" but is not swept. That fits all 16 lines (fresh objects in a running script's own frames, repeated words to the same object). Not proven per word: 0.36.97 printed no stamp, and the rest of the 226 have no line. None of the 21 named objects appears in the crash [verified log]; whether any was dereferenced later cannot be read from this log. Dead slots below top are scanned on purpose, so "stale slot" is not a reason for a miss |
| (2) GC kinds [verified bytes] | kind table `0x1194868` (20 bytes each): 0 PTRFREE (free lists `0x1222EEC`, pop `0xE4BDDC`), 1 NORMAL (`0x12226E8`, `0xE4BE4C`), 2 UNCOLLECTABLE (`0x12236F0`, `0xE4BFC1`), 3 atomic uncollectable (`0x1223EF4`), 4 the explicitly typed kind made at init (free lists `[0x1222404]`, `0xE50DE0`, descriptor in the object's last word): Mono's typed objects (the freed delegates are k4) and every arena chunk. Every chunk is k4 (audit `gc M/k4`; `0xD718A0` and `0xD719E5` both use `0xE50DE0`), never pointer-free. k0 words in frames are atomic objects (strings, value arrays), which hold no references. Managed code (ts3agent, ts3fixes) cannot pick a kind; Mono picks it by class, and the `0x83` answer path returns integers |
| (3) Descriptor 0 [verified bytes; writer unknown] | Frames never cover the last word: `0xD71900` and the method-entry bump `0xE563B2` grant only if `end - top >= [rm+0x38]`, and `[rm+0x38]` is the sum of the four frame areas (`0xE63D0E`), the last being the locals that `0xE5643C` zeroes. A GC sweep is ruled out: reclaim would also clear `+4`, and the end word `800177FC` is intact [inferred from Boehm's clear-on-reclaim]. So something wrote past a frame (or a stray write) into `+0x7FC`; neither `[ArenaGuard]` (off in ctl2) nor any of our code writes chunk memory [verified source]. With descriptor 0 the collector marks the chunk with length 0 and scans none of its frames, so everything only those frames hold is swept: Crash S on any later call through it [inferred; it fits all five crashes and the rooted, in-range, clean-chain readings of §22.4-§22.5]. The 0.36.97 audit missed it because it checks at completed cycles only (~2 per minute), its last minute line came before the crash, and the detail cap was already used |
| (4) Why mode 1 | Not settled. Mode 1 changes timing and nesting (timed `+0x918` waits that pump host calls, nested invokes on pool chunks, §22.5), which changes where frames land in chunks; the overrun needs a frame ending exactly at a chunk end [inferred]. The watch below names the method whose saved frame ends there |
| Build (0.36.98, branch `bugfix/ufb-desc-watch`) | `[GcRootCheck]`: (a) **descriptor watch** at every stopped mark (event 1, ~100 per minute, before `GC_initiate_gc 0xE4C2A0` reads the write-watch bits): every chunk of every container state, of the current state and in EA's pool (`[0x11EE508]`, node `{next, prev, chunk}`) must end in `[0x11F424C]`; first 8 lines `gc desc watch: collection N: chunk C ends in V (want D), state S (method M), top, end, words before, saved frames in it (depth:method base+size, * = ends at end)` (cells `state+0x3C+4*depth`, depth `state+0x140`); minute line `gc desc watch: chunk checks, bad descriptors (pool), restored`. (b) **`RestoreDesc=1`** (default 0): writes the descriptor back on detection; safe because no frame covers that word, and the write lands before the dirty-bit read, so the chunk is rescanned in the same stopped mark. (c) the audit splits unmarked words: `unmarked` (block stamped before this cycle: swept) vs `window` (stamped this cycle: kept, no line). Selftest `TestGcRoots` extended (stamp split, watch over container + pool, cell text with `*`, restore and recheck); `build.cmd` selftest and `selftest-all` pass. Asi `game-copy/ts3native.asi.unseenfb-all8` |
| Queue | `ufb-crash-narrow.json` on all8: `sv-ufb-farcrowd-loop-ctl2` (watch only) and new `sv-ufb-farcrowd-loop-ctl2-restore` (`GcRootCheck.RestoreDesc: 1`), then guard and planonly as before |
| What each answer means | ctl2: a `gc desc watch` line before the fault names the chunk, its task and the saved frame ending at the end (`*`); then read that method's frame size against its IL and the stores to its last local. `unmarked` (swept) > 0 with no bad descriptor = §22.5 (B)/(C) stays open; `window` only = the 0.36.97 numbers were EA's window. ctl2-restore: full ABBA passes with `restored >= 1` = cause confirmed and mitigated (ship candidate, off by default until a soak); crash with `restored 0` = not the descriptor path |
| Cost | one read per chunk per stopped mark (~400-500 chunks, ~100 per minute) [inferred]; logging only unless `RestoreDesc=1` |
| Effort | ~2.5 h static + build; live: two jobs (~15 min each) |

#### 22.6.1 Both 0.36.98 jobs failed at 1.8 min: the watch pushed EA's stopped mark past its budget (ts3native 0.36.99, not run)

| | |
|---|---|
| Run | `sv-ufb-farcrowd-loop-ctl2` and `-ctl2-restore` on all8 (asi hash `0e976ecd22cc` = all8, log `ts3native 0.36.98`: the right build ran; "installed the source build" is cycle.ts's wording for the job asi): `repro step spawn_sims failed: no reply to 'spawn_sims'` 8 s after the call (0.36.95 answered in ~2.5 s, 0.36.96/0.36.97 in ~5.5 s). No hang or fault: `gc desc watch: chunk checks 56,704, bad descriptors 0` (~64 per collection at load, before the container grew), read faults 0 |
| Measured | `probe-out/profile/...-ctl2-840.log` / `-ctl2-restore-840.log`: **`gc 666-1,282 ms` per second, one mark per second (`1 aborted`)**, vs 1.3-3.1 ms in every 0.36.95 farcrowd run (guard, planonly, offctl, unseen-fallback). TS3W CPU 9-14% (no spin) [measured] |
| Why [verified docs/13 Q3 + code] | Event 1 runs inside EA's **abortable** stopped mark: stop function `0xD717B0` aborts once `now - start > [0x11922A4]` (~1,000, range 500-1,500, µs [inferred]); after 40 aborted finishes `0xE49DA0` finishes with `never_stop` = a full stop-the-world mark of the 353 MB heap (~700 ms). 0.36.96's `GrOnGc` already did one VirtualQuery (`Readable`) per chunk (~420 chunks); 0.36.98 added a second container pass (`GrChainBad` again = another ~420 VirtualQuery, plus `GrInTable`). Attempts then miss the budget, so every cycle ends in the forced full mark; the agent's `spawn_sims` (allocation-heavy) runs past its 8 s reply wait. 0.36.96/0.36.97 were already marginal (spawn ~2x slower than 0.36.95) [inferred: no per-call timing in those builds] |
| Fix (0.36.99, branch `bugfix/ufb-desc-watch-cost`) | One pass: the watch is folded into `GrOnGc(true)` (container chains checked once, then the current state if not in the container, then EA's pool). `GrChainBad` reads each chunk under SEH instead of VirtualQuery (also speeds `GrAudit`). Bounds: container walk <= 8,192 states per walk (was 65,536 per bucket; a cyclic bucket gives mismatch `container walk capped (loop?)`), chain <= 256, pool <= 4,096 nodes (counter `pool walks capped`). Coverage unchanged: every container, current and pooled chunk at every stopped mark, so the crash-time catch stays. New: event-1 time (`event 1 time N ms, max N us, >= 300 us N` on the `gc desc watch:` minute line, first 4 calls >= 300 µs logged) |
| Selftest | `TestGcRoots`: watch via `GrOnGc(true)` (same 5 checks, bad/restore cases), pool cycle that misses the sentinel (4 + 4,096 checks, capped 1), self-looped container bucket (stops at 8,192 states, one mismatch, `GrAudit` bounded), unreadable chunk (`chunk unreadable` without VirtualQuery); `build.cmd` selftest passes |
| Queue | `ufb-crash-narrow.json` on `game-copy/ts3native.asi.unseenfb-all9`; same jobs. Pass check before reading Crash S results: `gc` in the profile log back near 0.36.95's few ms and `event 1 max` well under 1,000 µs; if not, the remaining cost is `GrOnGc` itself and the next step is a per-attempt cut (resume cursor) |
| Effort | ~1 h static + build |
### 22.7 Sixth crash on 0.36.99: the descriptor watch saw it 2.5 min early; arena tail pad (fix) and a debug-register writer watch (ts3native 0.36.100, not run)

Static + build, no game run. Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-ctl2-1055-ts3native.log`, capstone reads of `TS3W.exe` (`0xD718A0`-`0xD71BD5`, `0xE56320`-`0xE56450`, `0xE63C80`-`0xE63D8B`, `0xE54430`-`0xE54465`, `0xE69520`, `0xE508D0`-`0xE50A27`; a linear sweep of `.text` for `+0x13C`, `0x7FC` and the arena calls; branch targets into the patched ranges: none).

| | |
|---|---|
| Run | ctl2 on all9 (0.36.99): **CRASH at 6.4 min**, `0xE5201F`, `DoLoop` callvirt `InsideLoopFunction.Invoke` (`WorkInRabbitHole.InRabbitHole`), receiver `83597F00` `gc F/k4`, mark bit 0, block stamp = `GC_gc_no` 307; faulting task `90039C40` in the container, chain `8F8F0800` top `8F8F0AE4`, 9 frames below top, **`current chain descriptors 00000000`** [verified log]. The 0.36.99 cost fix held: `event 1 max 403 us`, 239 ms of event-1 time per minute [verified log] |
| Watch | First `gc desc watch` line at collection 4,994, ~2.5 min before the fault: chunk `8000B000` ends in `00000000` (want `0000002A`), the 3 words before also 0, state `883C6000` (`ScriptProxy.Simulate` > `Sim.Simulate` > `DoInteraction` > `ProcessOneInteraction` > `RunInteractionWithoutCleanup` > `ViewObjects.Run` > ...), **top `8000B7D4`, end `8000B7FC`**; collection 4,999 adds chunk `80008000` of another state with the **same chain and the same top `800087D4`**. 184 bad descriptors in all, pool 0; the audit at collection 5,020 finds 5 chunks with descriptor 0 (`8000B000`, `8000B800`, `80008800`, `80007800`, `8F8F9800`) [verified log]. The frame list was cut after 6 frames (cells buffer 400 bytes), so the `*` frame is not in the log |
| What the 16 zero bytes say | Chunk layout [verified bytes]: `0xD718A0` refills the pool 64 chunks at a time with `GC_malloc_explicitly_typed(0x7FC)`, top `c+0xC`, **end `c+0x7FC` = the descriptor's own address** (`0xE50DE0` stores it in the block's last word). Frame layout (`0xE563F3`-`0xE5643C`, `0xE63D0E`): args `[rm+0x3E]`, eval stack `[rm+0x34]`, vt stack `[rm+0x36]`, locals `[rm+0x3C]` (zeroed), size `[rm+0x38]` = their sum (16-bit adds); both grants (`0xE563B2` inline, `0xD71900`) accept `end - top >= size`, so a frame can end exactly on the descriptor. With top at `+0x7D4` the next frame of that call chain had 0x28 bytes left, `[+0x7D4, +0x7FC)`; zeros over `[+0x7F0, +0x800)` = the last 12 bytes of such a frame plus **one word past its declared size**. In mid-chunk the same write lands above top on dead frames and nothing sees it; only the chunk's last frame turns it into Crash S [inferred: fits all six crashes; the writing instruction is not read yet] |
| (a) freed or reused chunk | Ruled out for the descriptor: Boehm clears a freed or reclaimed object from word 1, and `c[0]`/`c[1]` stay intact in every bad chunk [verified log]; pool 0 (no pooled chunk bad); `[ArenaGuard]` is off in ctl2 and `[MemDiag] AllocSites` hooks the EA heap and ntdll, not GC blocks [verified source]. The only `GC_free` of a chunk is the arena's own large-chunk free (`0xD71AAB`, `0xD71B47`), after the unlink [verified bytes] |
| (b) frame or eval write past top | The remaining explanation (row above). No arena or interpreter path writes the descriptor on purpose: pops reset top to the frame base (`0xE54437`-`0xE54451`, `0xD71A20`), the grant adds `[rm+0x38]` only [verified bytes]. Candidates for the extra word: an IC or vtype return written by its native size into a smaller managed slot, a vt-stack or eval-stack depth the transform under-counts (EA superinstructions, docs/130), a size rounding in `initobj`/`localloc`. Reading the 1.2.3 transform for every such case is open; the watch below names the writer instead |
| (c) signed addresses | **Not the cause** [verified bytes]: every compare in the grant, pop, release and mark paths is on a difference (`0xE563BD jl`, `0xD7191B jge`: `end - top` vs size) or unsigned (`0xD71AB7 jb`, `0xD71ABB jbe`, `0xD71AC8 jb`, `0xD71ACC jb`, `0xE54448 jbe`, `0xE5444F ja`; mark loop `0xE50908 jb`, `0xE50912 ja`, `0xE509E1 jb`, `0xE50A10 jb`); the two `sar` in `0xE508D0` shift in-block offsets (`& 0xFFF`). The chunks sit above `0x80000000` because by minute 6 the GC heap (`gcHeap=356` MB at 5 min) and its late pool refills live in the large-address-aware upper half [inferred] |
| (d) why mode 1 | Mode 1 allocates nothing from either heap and touches no arena or container (§22.5) [verified source]. What it changes is which managed code runs at which depth: for marked plans (302 in this run) ts3fixes runs `try { UfInner } finally { UfSend(2); sOwProbe.Exit }` instead of the plain call, plus native deadline sessions. The two bad chunks above are two Sims in the same `ViewObjects.Run` chain with the same top `+0x7D4`, so a fixed call depth parks a 0x28-byte frame on the descriptor [verified log]; whether that frame is ours (the marked-plan path) or EA's that mode 1's timing puts there is what the watch answers [inferred] |
| Fix (0.36.100, branch `bugfix/ufb-arena-tail-pad`) | **`[ArenaGuard] TailPad=1`** (default 0 until its run, independent of `Enabled`): 6 byte patches in EA's arena code, all or none, at load (before `MonoScriptHost_Init` fills the pool): `0xD718CC` pool chunk end `+0x7FC` -> `+0x7BC`; `0xD71946` grow request `size+0xC` -> `size+0x4C` (pool chunk only when that fits 0x7FC); `0xD719EA` (15 bytes) an own chunk of `size+0x4C` with end `c+size+0xC` (its descriptor >= 0x40 beyond); `0xD71A4E`, `0xD71AEA`, `0xD71B88` pool-chunk check `0x7FC` -> `0x7BC` (else `GC_free`). Every fast path compares with `c[1]`, so no frame comes within 64 bytes of a descriptor and a few-word overrun lands in unused, unscanned bytes, as in mid-chunk. Cost: 64 of 2,032 bytes per pool chunk (3%), no code on any hot path. Chunks made before the patch keep their old end and are freed, not pooled, when released (logged). docs/68 row added |
| Writer watch (0.36.100) | **`[GcRootCheck] TailWatch=1`** (needs `Enabled=1`, logging only, SKIP with `[Watchpoint]`): `0xE563B2` (13 bytes) jumps to `AtBump`, which repeats EA's fit test and, when a new frame leaves < 16 bytes before `c[1]`, arms DR0-DR3 as 4-byte write watches on `[c[1], c[1]+16)` of that chunk on the sim thread (a `RaiseException` our vectored handler answers by setting the debug registers in the thread context; re-armed only for another chunk). A write there is past every frame of the chunk: `arena tail watch: hit N: <addr> (end + k) written by eip E (value V); armed for <method> (rm, base, args/stack/vt/locals = size) in chunk C; interpreter now <method>; script stack; native stack`, first 16. With TailPad on the watched words are the pad (any write = overrun, no crash); with TailPad off they are the descriptor itself. `XcptLog` skips the arm code and our hits. Minute line `arena tail: pad on/off; watch near-end frames N, arms N, hits N`. Also: `gc desc watch` frame list buffer 400 -> 1,100 bytes |
| Selftest | `TestArenaTail` (`arenatail-test.h`): the 6 patches on a fake page (one differing site = nothing written; second apply refused); the original and rewritten `0xD719EA` tails executed on a fake chunk (same top, end 0x40 lower, same stack); grant arithmetic for every size 0-0x1000 (frame end <= `c[1]`, descriptor >= `c[1]` + 64, own chunks never taken for pool chunks); `AtBump` on fake chunks (fits / fits near the end = arm / does not fit = grow path); the DR watch on the test thread (write at end+4 caught with its eip, write below end not, same chunk not re-armed, disarm stops it) [verified: `build.cmd` selftest passes, Windows 11 WOW64] |
| Build | asi `game-copy/ts3native.asi.unseenfb-all10` (sha256 `9c0d99f4c8e7`). `ufb-crash-narrow.json`: `sv-ufb-farcrowd-loop-ctl2` = TailPad + TailWatch (fix check and writer), `-ctl2-restore` = TailPad off + TailWatch + RestoreDesc (writer at the real descriptor, restore mitigates), guard and planonly unchanged; all on all10. The 8 queues on all5 (`ea-spread-ab`, `kind-deadlines`, `plandeadline-verify2`, `unseen-fallback{,-c,-d}`, `unseen-split`, `untested-v020`) retargeted to all10, `ArenaGuard.TailPad: 1` added to their jobs with `UnseenFallback.Enabled: 1` (7) |
| What each answer means | ctl2 passes the full ABBA with `gc desc watch: bad descriptors 0` = fixed (TailPad on by default only after a soak and the save matrix: it changes EA's arena layout); `arena tail watch: hit` lines name the writer: eip in a ts3native/ts3fixes path = ours, fix it there too; eip in the interpreter or an EA IC = EA bug, TailPad stays as the fix. Crash with `bad descriptors` > 0 under TailPad = a write longer than 64 bytes or another writer (read the hit lines). ctl2-restore: hits at the descriptor confirm the same writer without the pad |
| Effort | ~2.5 h static + build; live: two jobs (~15 min each) |

### 22.8 ctl2-restore on 0.36.100 passed; the 16 watch lines were EA's next-chunk header writes, not the writer; filtered watch (ts3native 0.36.101, not run)

Static + build, no game run. Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-ctl2-restore-1140-ts3native.log`, capstone reads of `TS3W.exe` (`0xE56380`-`0xE56460`, `0xE54400`-`0xE54470`, `0xD719F0`-`0xD71AA0`, `0xE63C80`-`0xE63D90`, `0xE64590`-`0xE645C0`, `0xE64930`-`0xE64A90`; a skip-data sweep of `0xD00000`-`0xF00000` for 16-bit stores to `[rm+0x34/0x3C/0x3E]`), the run window's `ScriptError_*.xml` files.

| | |
|---|---|
| Run | ctl2-restore on all10 (TailPad off, TailWatch on, RestoreDesc on): **PASS**, 82 bad descriptors restored (first at collection 4,806), last minute line `watch near-end frames 14947236, arms 180362, hits 4078413` [verified log] |
| The 16 hit lines | All in the first armed chunk `80019000` (end `800197FC`), at load, all at `end+4` / `end+12`. A data breakpoint is a **trap: the reported eip is the instruction after the write** [verified: selftest `g_atLastEip` lies after `AttStore`'s store]. The writes are `0xE563D1 add [eax],edx` (method-entry top bump, trap eip `0xE563D3`), `0xE54451 mov [edx],eax` (frame pop resets top, `0xE54453`), arena `0xD71A03 mov [edi+8],ecx` (`0xD71A06`), `0xD71A12 mov [edi],edx` (`0xD71A14`), `0xD71A83 mov [esi],edi` (`0xD71A85`), `0xD71A85 mov [esi+8],0` (`0xD71A8C`) [verified bytes]. Chunks are 0x800 apart (`GC_malloc_explicitly_typed(0x7FC)` + descriptor word), so `end+4` = the **adjacent** chunk `80019800`'s `c[0]` (top) and `end+12` its `c[2]` (link): every line is EA's own legal header write of the next chunk, not a write past a frame. The 4.08 M hits are the same top writes, at every call and return in that next chunk. §22.7's reading "the interpreter writes one word past the chunk" is withdrawn |
| Why the writer was not seen | (a) one chunk is armed at a time (180 k arms in the run; a bad chunk need not be the armed one when it is written); (b) DRs are per thread and only the sim thread is armed; (c) the 16-line cap went to the first chunk's header noise and the hit counter did not split by offset, so whether any hit was at `end+0` is unknown [verified source] |
| New bad-chunk shapes | 8 `gc desc watch: collection` lines logged (the rest over the line cap): 5 with top `+0x7D4` and the 3 words before the descriptor 0 (as §22.7), but 3 with top far from the end (`+0xCC` twice, `+0x79C`) and **non-zero** words before (e.g. `C30AD0CC C0DCAB38 C3798EA4`); chunk `80005800` lost its descriptor twice (collections 4,807 and 4,880) [verified log]. A single zeroed word at `+0x7FC` under live-looking frame words fits an earlier, deeper frame of the same chain or a one-word store, not only "the last frame's tail ran over" [inferred] |
| Frame size (task 1) | `rm` sizes are set once per method [verified bytes]: eval stack `[rm+0x34]` = `(IL max_stack * 10 + 7) & ~7` (`0xE6494C`-`0xE6495E`; Mono 1.2.3's `(sizeof(stackval) + 2) * max_stack`, 8-byte `stackval`): at least one spare 8-byte slot for every `max_stack` >= 1, none for `max_stack` 0; locals `[rm+0x3C]` = aligned offsets rounded to 8 (`0xE649AC`); args `[rm+0x3E]` = aligned parameter sizes rounded to 8 (`0xE64A6F`; `this` not counted); vt stack `[rm+0x36]` = the transform's `max_vt_sp` (`0xE63D23`); size `[rm+0x38]` = 16-bit sum of the four (`0xE63D0E`); no-header methods get 8/8 (`0xE645AB`). The grant takes exactly `[rm+0x38]`, the layout (`0xE563F3`-`0xE5643C`) is args, stack, vt, locals, and only the locals are zeroed, by their own size. **No off-by-one in the sizes or their sum** [verified bytes]; an overrun would have to be an opcode body writing past `max_stack` + 1 slot or past `max_vt_sp`, which no hit has shown [inferred, open] |
| Fix (task 3) | No frame-size patch: no proven overrun size. `[ArenaGuard] TailPad=1` stays at 64 bytes (the ctl2 job with the pad on is still the fix check); shrinking it needs a measured overrun |
| Vanilla exposure (task 4) | The arena, the interpreter and the descriptor layout are EA's code in `TS3W.exe`, but the writer is still unknown, so vanilla exposure is not shown [inferred: exposed if the writer is EA's]. The `ScriptErrorWindow.Init < UIManager.OnCustomAttach` stack is NRaas ErrorTrap's load-time correction report (`ScriptError_..._19-43-14_...xml` = 11:43 UTC: "Out of World Object Found 2: HiddenAdultBroom / AdultBicycle", destroyed; all 11 files of the day are such reports, one per load), not a script exception from ts3fixes, the test agent or the hitch guard; and those hits were header writes anyway [verified files] |
| Watch v2 (ts3native 0.36.101, branch `bugfix/ufb-tail-watch-filter`) | `[GcRootCheck] TailWatch=1`: DR0 = `end` (the descriptor), DR1 = `end+4` (DR2/DR3 off); hits at `end+4` with trap eip in `0xD71900`-`0xD71BFF` or `0xE563D3` / `0xE54453` are counted as `next-chunk header writes`, not logged; any other hit is logged once per distinct (trap eip, armed method), up to 64, same fields as before (`end + k` tells 0 from 4; the writing instruction is the one before the trap eip), and counted per entry; the minute line adds `(distinct N, past 64 N; per eip E=n ...)` and `next-chunk header writes N`. Selftest `TestArenaTail`: end+0 and end+4 hits from one eip and method share one entry, end+8 no longer watched, the filter applies only at offset 4 [verified: `build.cmd` selftest passes] |
| Build | asi `game-copy/ts3native.asi.unseenfb-all11` (sha256 `6ed3aa1ee696`); `ufb-crash-narrow.json` retargeted to all11 (all 4 jobs). The 8 routing queues on all10 are not touched |
| What each answer means | A hit at `end + 0` with eip in `0xE51000`-`0xE6FFFF` = an interpreter opcode overruns its frame (read the instruction before the eip, then the transform's height count for that opcode): EA bug, TailPad is the fix, vanilla exposed; eip in ts3native/ts3fixes = ours. No `end + 0` line with restored > 0 again = the writer is off the sim thread or hits unarmed chunks: next step is arming at restore time per bad chunk, or a page-guard watch |
| Effort | ~1.5 h static + build; live: one ctl2-restore job (~15 min) |

### 22.9 Writer found on 0.36.101: `initobj` zeroes 12 bytes into 8-byte Int64/UInt64/Double slots (EA's Mono); `[ArenaGuard] InitobjFix` (ts3native 0.36.102, not run)

Static + build, no game run. Sources: `probe-out/queue/ufb-crash-narrow-2026-10-26-sv-ufb-farcrowd-loop-ctl2-restore-1229-ts3native.log` (0.36.101, all11), capstone reads of `TS3W.exe` (`0xE589F7`-`0xE58A0C`, `0xE633A3`-`0xE63468`, `0xE6F5A0`, `0xE9C3A0`-`0xE9C492` + jump table `0xE9C4EC`/`0xE9C520`, `0xE85390`-`0xE85435`, `0xE64945`-`0xE649AC`, `0xE560CD`, `0xE570C8`, `0xE57089`, IAT `0xF95664`), `msvcr80.dll` 8.0.50727.9680 (WinSxS: exports, `memset` at RVA `0x14A20`), IL of ``List`1.RemoveAt`` in EA's `mscorlib.dll` (ilspycmd).

| | |
|---|---|
| Hit | `arena tail watch: hit 5: 89F28FFC (end 89F28FFC + 0) written by eip 711D4A81 (value now 00000000); armed for RouteBackoffHook.UfSend ...`, script stack ``List`1.RemoveAt < Ts3Fixes.OverwatchProbeCore.Exit < RouteBackoffHook.UfPlanBody < ...``, native stack `00E58A09 00E53F45 00E5631B ...`; per eip `711D4A81` = 110 of 119 hits by 20 min, all at `end + 0`, value 0; the only other `end + 0` eip, `6F526921`, writes `0x2A` = our own `RestoreDesc` [verified log]. `UfSend` only armed the chunk (its frame came within 16 bytes of the end); the write came later in the same chunk, from `RemoveAt` |
| Module | `0x711D4A81` = **MSVCR80.dll `memset` + 0x61**, the instruction after `rep stosd` (`memset` RVA `0x14A20`, `0x14A7F rep stosd`, `0x14A81 test edx,edx`; image base `0x711C0000` in that run) [verified offset fit; base inferred: the log has no module list]. TS3W.exe imports `memset` from MSVCR80.dll (thunk `0xF7E8B8` -> IAT `0xF95664`) [verified bytes]. Not ts3native (static CRT, `0x6F500000`-`0x6FA3F000`) |
| Caller [verified bytes] | top native return `0xE58A09` = after `0xE58A04 call 0xF7E8B8` (memset) in the `MINT_INITOBJ` handler `0xE589F7` (`docs/data/mint-opcodes.csv` `0x0F5`): `memset(sp[-1].p, 0, [ip+2])`, size = the 32-bit operand the transform emitted |
| Managed side [verified IL] | ``List`1.RemoveAt`` (EA mscorlib): `.locals init ([0] !T)`, `ldloca.s 0; initobj !T; ldloc.0; stelem !T` (= `_items[_size] = default(T)`); its only local is the last thing in its frame (layout args, stack, vt, locals, §22.8). ts3fixes `OverwatchProbeCore.Exit` calls it on `List<ulong>` twice per marked plan (mode 1: every `UfPlanBody`), deep in `ViewObjects.Run` chains: why mode 1 made it common |
| The size [verified bytes] | transform `0xE63386`-`0xE63468`: class from the token (`0xE6F840`, inflated); `0xE5B180(&klass->byval_arg)` = mint type; `8` (object) -> store null; anything else -> `MINT_INITOBJ` (`0xE63416`) with size `0xE6F5A0(klass)` = **`instance_size - 4`** (`0xE6F5BB`: EA's `MonoObject` is one word). `mono_type_size` `0xE9C3A0` gives I8/U8/R8 size 8, **align 8** (`0xE9C473`, `0xE9C483`), so the value field of `Int64`/`UInt64`/`Double` sits at offset 8 and `instance_size` = 16: initobj size **12** on an **8-byte** slot (locals sized by `mono_type_size` of the inflated header, `0xE85411` -> `0xE84C10`, `0xE6497C`). 4 bytes past the slot are zeroed every time [inferred from Mono's field layout rule; fits the run: a 12-byte memset over `[end-8, end+4)`, the descriptor its 3rd word] |
| Scope | Only `initobj` (`0xE63427`) is wrong: `ldobj` (`0xE560CD`) and `stobj` (`0xE570C8`) copy primitives by type (`byval_arg`); `stobj.vt` (`0xE57089`) uses the value size only for value types, where storage and value size agree [verified bytes]. Affected: `initobj` on Int64, UInt64, Double and enums over Int64/UInt64, i.e. `default(T)` / `out T` resets in generic code (`List<long>.RemoveAt`, `Dictionary<K,long>.TryGetValue` misses, ...). Vanilla exposed: EA code does this too; mid-chunk the 4 bytes land on dead frame bytes or a neighbour slot (harmless or a silent wrong value), at a chunk's last frame they are the GC descriptor = Crash S [inferred]. Into a heap slot (`ldelema`/`ldflda; initobj`) it would clear the next element or field [inferred, not seen] |
| GetOption2 channels | **Not the cause** [verified source]: every ts3fixes channel (`0x82` UnseenViewers, `0x83` UfSend / UfCamAsk / unseenfbsites, `0x84` doors, `0x85` JOBBUSY, `0x86` probe, `0x87` UserDiag, A10 START/READY/CANCEL/NOYIELD) passes one `ulong` by value and gets a `bool`; `GetOptionHook` (`ts3native.cpp`) returns a `u8` and writes no caller memory. No size mismatch exists on that path |
| Fix (0.36.102, branch `bugfix/ufb-initobj-size`) | **`[ArenaGuard] InitobjFix=1`** (off by default until its run): the transform's `call 0xE6F5A0` at `0xE63427` (5 bytes, checked) -> `AtInitobjSize(klass)` = min(EA's value size, `mono_type_size(&klass->byval_arg)`), VAR/MVAR left as EA's. Value types unchanged (both sizes equal), I8/U8/R8/long enums 12 -> 8; never more than EA wrote. Applied at load with TailPad (before `MonoScriptHost_Init`), so every method transforms with it; log `OK   initobj size` and a minute line `initobj size: N initobj sizes clamped`. TailPad stays (covers any other overrun up to 64 bytes). ts3fixes: `OverwatchProbeCore.Exit` uses `RemoveRange(i, 1)` (Shift + `Array.Clear`, no initobj), so mode 1 stops feeding the bug when the switch is off. docs/68 row added |
| Selftest | `TestInitobjSize` (`arenatail-test.h`, run from `TestArenaTail`): EA's layout rule on fake classes (UInt64 12 -> 8; I4, U1, a 12-byte value type, VAR unchanged; one clamp counted); the crash reproduced on a fake chunk: an 8-byte last local below the descriptor, `memset` with EA's size zeroes the descriptor, with ours keeps `0x2A`; the call-site patch on an executable copy (wrong target = nothing written, patched call returns 8, second apply refused). `build.cmd` selftest passes; ts3fixes `tools/selftest/owprobe` passes |
| Build | asi `game-copy/ts3native.asi.unseenfb-all12` (sha256 `7187a11e4d4a`). Queues retargeted to all12 with `ArenaGuard.InitobjFix: 1` next to every `TailPad: 1` (`unseen-fallback{,-c,-d}`, `v020-natural`, `untested-v020`, `kind-deadlines`, `plandeadline-verify2`, `ea-spread-ab`); `ufb-crash-narrow`: all 4 jobs, detectors unchanged (ctl2 = TailPad + TailWatch; ctl2-restore = pad off + TailWatch + RestoreDesc + InitobjFix = the fix check) |
| What each answer means | ctl2-restore passes with `initobj size: N clamped` > 0, `restored 0`, `gc desc watch: bad descriptors 0` and no `end + 0` hit = writer fixed (then InitobjFix on by default after a soak; TailPad can stay off). `end + 0` hits from another eip = a second writer (read it as in §22.8). Restored > 0 with no hit = writer off the sim thread or on unarmed chunks |
| Effort | ~1.5 h static + build; live: one ctl2-restore job (~15 min) |

## 23. Load hang (2026-10-26 18:25 UTC): EA lock-order deadlock between the main thread's resource update and a worker opening a package, `sv-ufc-farwalkers-loop` on ts3native 0.36.103 (`asi.unseenfb-all13`)

- **What the harness saw:** after "Mod Scripts Found", `world load: nothing moved ... for 60 s`. The hang capture shows the sim thread in `Lock_Acquire` 5 of 5 times. Managed top `UIManager_GetSaveGameMetadataImpl` <- `MainMenuModel.GetSaveFileDetails` <- `Ts3Agent.Handlers.LoadSave`. The main thread is also in `Lock_Acquire`. No world was loaded (`ts3native.log` ends after the header, same as §21.3).
- **Lock owners, read from the kill dump** (`probe-out/dumps/ts3w-16664-20261026T182520.dmp`; a minidump parser in the scratchpad read CRITICAL_SECTION `OwningThread` from the waiters' `edi`) [verified]:
  - The main thread (40516) holds CS `0x0C116F60` (recursion 2) and waits for CS `0x0C116EE8`. Its stack is resource-system update `0x7377F0` -> `0x73767D` -> `0x735633` / `0x7355F2` -> `0x73455D` -> `0x735358`.
  - Worker thread 26068 (an MSVCR80 pool thread) holds `0x0C116EE8` and the ResourceMan `GetPackageSource` lock `0x0C000C28`. It waits for `0x0C116F60` in the package open path `0x734710` (`0x734725`), called from `0x734591` <- `0x8BA7D5` <- `0x8AA846`.
  - The sim thread (14152) waits for `0x0C000C28` at `GetPackageSource 0x4AFFC0` (key `0x4AFFD6`, docs/165), behind the cycle.
  - So the two package-database locks (`0x...EE8` / `0x...F60`, one object) are taken in opposite order by the main-thread update and the worker's open: an ABBA deadlock in EA code.
- **Ours?** No [verified]:
  - Every `ufc*` counter is 0 in the hang `native.json`: no C site ran, and none can run at the main menu (no world, no routing). The C sites are patched once at start-up (`ts3native.log` "unseen fallback sites: ready 0x3F" before the menu), not while the game runs them.
  - The only ts3native frames are the `Lock_Acquire` owner-tracking detour on the waiters' stacks (`ts3native+0x292E8` / `+0x2D4A3` above `Lock_Acquire+0x1D`), a pass-through. It is on in every job of this chain, and the 3 later jobs loaded fine. `[Decompress]` (on) had made 1 call.
- **Same as §21.3?** Same phase and symptoms (menu save read, no `world load`, idle CPU), so probably the same deadlock [inferred: §21.3 had no capture]. AllocSites, §21.3's suspect, was off here.
- **Action:** none in our code. Rerun the job. The harness hang capture could name lock owners itself (read `OwningThread` of the CS each waiter's `Lock_Acquire` is in); `HitchAttr Holders` records only finished waits, so it showed 0 records. If it repeats often: a load retry in `cycle.ts` after the fail-fast kill.

## 24. Crash T (2026-10-29 05:49:50 UTC): `[AutShadow]` memo key reads a MonoString with the wrong layout, `ip-autshadow-r` on ts3native 0.36.106 (`asi.mono-cost2`)

- **Job:** `mono-cost2.json` 7/7, Isla Paradiso, `[AutShadow] Mode=1` + `[MonoThread] Mode=1 GcLock=1 FrameTls=1 ArenaFix=1 N2mPrivate=1`, crash after 1.7 min. EA xcpt report: ACCESS_VIOLATION reading `0x4fdc1000` (MEM_FREE) at `ts3native.asi+0x14976`.
- **Fault site [verified]:** a `/MAP` rebuild of the same source (byte-equal code, only the timestamp differs) puts `+0x14976` (section offset `0x13976`) in `AsKeyOf` (`autshadow.cpp`), the inlined `AsStrKey` loop `movzx eax, byte [edi+esi]`. Stack: `AsKeyOf` < `AsSimMemo+0x3E` < mint interpreter (`0xE526C4`) < ... < `MonoScriptHost_Simulate` (`0xD8194D`) < `AsSimEnter+0x27`: the **sim thread**, the native memo of `DeviceConfig_GetOption` (descriptor 25, `strMask` 1; read back from the asi data at `kAsDesc+25*24`). Not the worker: ts3fixes had turned `[AutShadow]` off for the session (§24 item 3 below), so no shadow jobs ran; the native memo thunks run with `Enabled=1` regardless.
- **Cause [verified]:** `AsStrKey` assumed the Mono 1.x layout `{vtable, sync, length +8, chars +12}`. EA's Mono 1.2.3 strings have no sync word: `mono_string_new_size` (`0xEA3FB3`) stores the vtable at +0, the length at +4, the terminator at `+8 + 2*len`. The crash registers show it: string `0x4fc78bd0`, "length" `0x750053` = the first two chars `S`,`u` (the option name `...uppressOpportunit...` from +8), so the loop hashed ~15 MB until it hit a free page. Every memo call hashed garbage of up to megabytes (BP: `memoRecords` 1024 with no fault: luck, the reads stayed in committed memory). `AsIcLog` (autshadow.jsonl lines) had the same wrong offsets (bounded, so it only logged nothing).
- **Not the cause:** the GC lock gate, N2mPrivate, ArenaFix: no frame of theirs on the stack; the faulting read is our own hash loop on a pointer the interpreter passed correctly.
- **Fix (0.36.107):** `AsStrKey` reads the length at +4 and chars at +8; a length outside 0..4096 is not keyed (logged once, `AsKeyOf` returns -1: the sim thread calls the body without recording, the worker aborts with the descriptor's reason). `AsIcLog` likewise. Selftest fixture `AsFakeStr` now has the EA layout.
- **Also in this round:** (2) the `Localization.LocalizeString` worker shims install first in `Fixes.Apply` (were refused "already compiled" in every job, so Modes 1/2 stayed off); (3) the L3 self-check failed because `MethodInfo.DeclaringType` comes from `MonoMethod.klass` (docs/51 §378): after our header swap `CalculateScore` read as unswapped; L3 now also requires the method to be in its declaring class's own method table; (4) `queue.ts` fails a shadow job with zero cuts or entries or an "off for the session" line.
- **Status:** fixed in source, not yet run; two clean `ip-autshadow-r` runs (`mono-cost3.json`) needed to call it fixed.

## 25. Hang U (2026-10-29 10:52:05 UTC): not a crash on quit; the sim thread ran one iteration for over 68 s while the main thread kept drawing, `bp-simbudget-v024` on the v0.2.4 release build (ts3native 0.36.105, `asi.rel020`)

Static triage of existing run data only (no game run, no build). Ledger: docs/51 §384. Times UTC (PHT = +8).

| | |
|---|---|
| Report | Queue `sim-budget-v024` job 1/3 (Bridgeport, release defaults + `[HitchAttr]` Holders, 10-min profile, seed 12345): `FAIL: game died at 10:52:12 (agent not answering for 60 s (heartbeat frozen))`, then the queue's `game crashed` / `CRASH in 12 min` and `no crash report written since 2026-10-29T10:41:30` (`probe-out/queue/chain-1029-simbudget.out`, job log `probe-out/queue/sim-budget-v024-2026-10-29-bp-simbudget-v024.log`). IP and SV on the same asi + ini passed. |
| Corrections to the event report | (1) **Not at quit and not at job end.** The profile ran from 10:43:13; the stall began at ~10:52:05, ~1 min before the 10-min window ended. Nothing in the job quits the game; the harness killed it at 10:53:28 [verified: job log, frames.csv]. (2) **The `decompress:` per-thread block is not a shutdown summary.** It is the periodic counter block: it appears 11 times in this `ts3native.log` (lines 175-808), about once a minute, with growing counts [verified]. (3) The first-chance exceptions at `module+0x672C`/`0x666C` (UTF-16 path text on the stack) are the boot pair of docs/172 §2 (not ts3native, ~89% of all runs, before the world loads) [verified there]. |
| Hang, not crash | No EA xcpt file and no crash-ledger entry since the job start; no Windows Application-log event for TS3W.exe between 18:40 and 19:20 PHT [verified: `Get-WinEvent`]; the queue's last triage line is the 08:17 UTC `frida-agent.dll` fault of an earlier job. The process was alive until the kill: the harness took a 2,276 MB kill dump (`probe-out/dumps/ts3w-42044-20261029T105317.dmp`) and a hang capture (`probe-out/queue/sim-budget-v024-2026-10-29-bp-simbudget-v024-hang/hang-105313-*`) before `taskkill` [verified]. |
| Timeline | 10:48:13-10:51:13 native sampler (`0 sim samples lost`); 10:51:48 normal upkeep pass; **10:52:04.3** last per-second row (38 it/s, max iteration 91.8 ms, ThreadedObjects 581 ms/s, task max 30.7 ms) and last freeze/hitch records (52 ms); **10:52:05.3** sim-to-main messages per frame drop from ~17,700/s to 0 and stay 0 until the kill; 10:52:12 last agent heartbeat; 10:52:22 and 10:52:52 upkeep RPCs `previous request still pending`; 10:53:13-10:53:17 hang capture; 10:53:19-10:53:27 one 7.4 s main frame (the kill dump suspending the process); 10:53:28.9 last frame row, `Process terminated`. The final seconds row (written at profiler unload, as docs/172 §3) has 17 iterations, max 51.9 ms: the sim finished ~17 more iterations after 10:52:04, then one iteration never ended [verified: `probe-out/profile/sim-budget-v024-2026-10-29-bp-simbudget-v024-600-{seconds.csv,frames.csv,freezes.jsonl}`, `-upkeep.log`; QPC aligned to epoch with the last frames row]. |
| Which thread | **The sim thread (21088) stopped finishing iterations; the main thread (25236) kept running** at 300 frames per 5 s with 16-18 ms frames from 10:52:05 to 10:53:19 [verified: frames.csv]. Same signature as docs/172 (bp-natural-2, 2026-10-22, dev build), which had no stack. |
| What the sim thread was doing | Five `/simstack` samples, 1 s apart, 68-72 s into the stall, each with a **different top frame**, so the thread was executing, not parked in one wait [verified]: `0x4E5E40+0x7F` (EA dlmalloc internals, under the allocator `CRITICAL_SECTION`, docs/131 table) x3 under alloc/free (`0x4E69D0`, `0x4E5920`) called from the route-follower chain `0x8538B0` -> `0x852520` -> `0x848590` (docs/80, docs/117: route-follow / turn helpers, callers of `0x84C590`) and `0x84B590` (RouteFollow update helper); one sample in a recursive `0x5877B0`/`0x585990`/`0x584FF0` chain under `0x54E630`/`0x588730` (no doc names it yet); one in `0x4E51B0` (EA free) under `0x849540`. These are raw stack-scan frames, so deeper entries can be stale [inferred]. The bottom of every sample holds two ts3native return addresses: `+0x2D209` (after a `call rel32` to `+0x41980`) and `+0x419DA`, the return from `call [0x10131B8C]` inside `+0x41980`, a thiscall wrapper that calls an original through a pointer in ts3native's `.data` (a detour trampoline) [verified: bytes of `game-copy/ts3native.asi.rel020`]. Which detour that is cannot be named without a `/MAP` build (none exists for rel020; the COFF layout of `out/ts3native.obj` does not reproduce the link order) [unknown]. |
| Locks and other threads | 141 other thread stacks: 89 EA job threads in `0x4DAE40` waits, the rest in kernel waits; the two `frida-agent.dll` threads idle in `ntdll` waits; ts3native's own threads (`+0x8031F`, `+0x75DF0`) in kernel waits; `/native` route-job `busy` 0 [verified: `hang-105313-threadstack.json`, `-native.json`]. The `[HitchAttr]` Holders ring (waits >= 20 ms) has 3 records, longest 82 ms (sim waiting on main for lock `0x143CDEA0` at `0x729E2C`), none in the stall [verified: `hang-105313-lockholders.json`]. So no lock wait and no deadlock: a long or endless loop on the sim thread [inferred from busy stacks + idle threads]. `managedstack.json` is empty: no managed frame was read, so either the loop is native-only or the reader found no interpreter frame [unknown which]. |
| Ruled out | Frida JS-lock stall (docs/172 cause 2): no `frida-agent` frame on any sim sample; sim top frames are EA code that changes per sample [verified]. Mono worker / `[AutShadow]` / `[MonoThread]`: off in the release ini [verified: job ini]. Quit-path crash: no quit, no fault [verified]. Address space: 1,305 MB largest free, 2,354 MB committed at the 10-min diag [verified]. |
| Happened before on release builds? | **No earlier case on a release asi.** The eight earlier `heartbeat frozen` FAILs in `docs/data/results/*.md` (bp-snap-p4 x2 and ip-snap-p4 on 10-19; bp-ut-play x2 on 10-21; bp-natural-2 on 10-22 = docs/172; bp-cc-lots on 10-26 = §20) were all dev or test builds [verified: results files]. docs/172 has the same shape (sim stuck inside one iteration late in a 10-min profile, main fine). |
| Ours or not | **Open; ours until proven otherwise (AGENTS.md crash rule).** For ours: the sim stack runs under a ts3native detour (`+0x41980`) and in EA route-follow code whose inputs our routing features write (async plan results, footprint and discourage deferral, gate closures: all on in the release ini). Against: the top frames are EA allocator and route-follow code, not ts3native, and no lock of ours is held. Not driver/DXVK: the render (main) thread was healthy [verified]. |
| Next steps (static, no game run tonight) | (1) **Read the kill dump**: `probe-out/dumps/ts3w-42044-20261029T105317.dmp` is a full-memory dump taken inside the stall; with cdb/WinDbg (not installed on this machine) get the sim thread's unwound stack, the route follower's `this`, the route/path object and the loop variables. Cheapest decisive step. (2) **Name `+0x41980` and `+0x2D209`**: rebuild the rel020 source with `/MAP` (byte-equal code, as §24 did) after the overnight queue; better, make every release build emit `/MAP` and archive it next to `game-copy/ts3native.asi.<tag>`. (3) Decode `0x5877B0`/`0x585990`/`0x54E630` statically (Ghidra, not during a timing queue). |
| Logging proposal (no code change made) | (a) **Sim stall sampler in ts3native** (works for players, not only under the harness): a watchdog thread that, when no Simulate iteration has finished for 5 s, suspends the sim thread, logs EIP plus a bounded 24-frame walk as `module+rva` with our own detour names (from a table of our hooks, no map needed), then again every 10 s up to 6 times, one line each in `ts3native.log`. (b) In that line, the route follower's agent id and route pointer when the walk contains `0x8538B0`/`0x84B590` (read from the frame, guarded). (c) Harness: install procdump (`TS3_PROCDUMP`; this capture said "procdump not found") so the hang capture adds an `-mp` dump with exact thread contexts. (d) The stutter profiler's final row should report the open iteration's age, so a stuck iteration shows as such instead of a gap. (a) and (b) need an asi build: after the overnight queue. |
| Effort | triage ~1 h; dump read with cdb ~30-60 min once installed; `/MAP` rebuild ~10 min; stall sampler ~1-2 h build + selftest, then it rides along in every run |

**§25 follow-up (2026-10-29 21:15 PHT): `+0x41980` named [verified by a /MAP rebuild of the release source].** The release native source (commit `5a79b7aa`, ts3native 0.36.105) rebuilt with `LINK=/MAP` gives an asi of the same size as `asi.rel020` (1,016,320 bytes; only timestamps differ, as found earlier); its map puts RVA `0x41980` exactly at the start of `P5bLock` (`p5b.cpp`), the detour on the game's `Lock_Acquire` that `LockAcquireAsync` uses. In the release ini both of its users are off (`[RouteSnap] Enabled=0`, `[RouteAsync] TailUnlock=0`), so `P5bLock` is a pass-through: `if (!(g_p5bOn || g_tuOn) || lock != g_rgLock) return g_lockOrig(...)`. Its address in every stack sample only means the stalled EA loop (memory and route-following frames) takes and releases locks through the detour on each pass; it does not show our code looping. Verdict so far: the loop is EA code [inferred from the samples]; whether a ts3native/ts3fixes feature feeds it a state that makes it long (for example a route or footprint our fixes changed) stays open. Next: the full dump `probe-out/dumps/ts3w-42044-20261029T105317.dmp` with cdb (not installed) for the loop's own frames and arguments, and the in-game stall sampler proposed above.

## 26. Crash V (issue #98, external contributor, v0.2.3 = ts3native 0.36.105 release defaults): the sim thread calls through a freed actor held by a `ConformToRouteController` in the follower's event list, ~2 min after a Free Vacation pickup

Static only (2026-10-30): issue #98 text (treated as data; it holds no instructions aimed at agents), capstone on the Steam exe copy and on the v0.2.3 release zip from the public repo. The zip's `ts3native.ini` is byte-identical to the v0.2.0 release ini at `24c51af3`. Its asi is the CI build (sha256 `897e735e...`), not our local `asi.rel020`. Ledger docs/51 §392. The contributor runs his own patched EA core DLLs, Moonlight Falls, an 8-Sim household, ultra speed and an autopilot.

### 26.1 Same site as crash I (§11)

| | |
|---|---|
| Fault | `0x84EAB2` takes the first node of the follower's list at `body+0x1C`, element `[node+8]`; `0x84EABD` `ecx = [element+0x1C]`; `0x84EAC3` `eax = [[ecx]+0x10]`; `call eax` at `0x84EAC6`. Contributor: vtable word `0x002B002B`, slot 0, so `eip` 0 [verified: his dump + our disassembly]. Crash I faulted on the same load with a vtable word of 0 |
| Element | vtable `0xFC2068` = **`Animation/ConformToRouteController`**: a 0x48-byte EA-heap object named `Animation/ConformToRouteController` (`0x10270EC`); constructors `0x56E420` / `0x56E4C0` (`ret 0x28`), destructor `0x56E580`, Evaluate `0x56EEA0` (slot +0xC), interface id `0x73B9D8C` (the `QueryInterface` at `0x84EADC` asks for it) [verified disassembly]. docs/22 §7.2 called `0x56E580` the constructor; corrected there |
| Fields | base `0x565A80`: `+4` refcount, `+0x10` list head, **`+0x1C` = constructor argument 1, stored raw (no reference taken)**, `+0x20` serial from the global counter `[0x114F590]`, `+0x24` byte. `0x56E4C0`: `+0x2C` path (reference, `lock xadd [p+4]`), `+0x30` caller object (reference; the controller registers a listener on `[+0x30]+0xC` via `0x566170` and removes it in the destructor via `0x566590`), `+0x34` animation object (reference), `+0x38` float, `+0x3C` / `+0x40` body fields, `+0x44..+0x46` flags [verified disassembly] |
| `element+0x1C` | the **actor** the controller conforms to the route. Two creation sites, both in `0x84B590` (route-follow body, 5 callers): `0x84B756` passes **`[body+0x124]`** (the body's own actor; docs/144: `AnimatedSimBody` `0x1027294` looks up components on `+0x124`), `0x84B958` passes **`[[body+0xB4] + 4*i]`** (a second actor array, parallel to the records at `body+0xB0`) [verified disassembly]. Slot `+0x10` called at `0x84EAC6` is a bool on that actor |
| Body | `esi` vtables `0x1027294` / `0x1027290` / `0x102727C` = `AnimatedSimBody` (docs/144) [doc claim + his dump]; agent `ebx` `0x7DE51678` (`vt_LocomotionAgent`) |

So the freed object is an actor (a Sim's or an object's native animated object) that an EA controller holds raw, while the controller holds references to everything else it uses. EA's cleanup for such controllers is the `+0x30` listener. An actor freed without that path firing, or freed while the controller sat in the swap buffer `agent+0x68` (§11.1), leaves the raw pointer behind [inferred].

### 26.2 Could a ts3native feature in the v0.2.3 release set free or keep that actor?

Release set (the ini at `24c51af3` = the zip's ini): `Enabled=1` sections `[GC] [RouteAsync] [SaveWrite] [Decompress] [WaterJps] [UserDiag] [RouteGate] [Discourage] [LotRemoveGuard] [OptsInit] [UnseenFallback]`, plus `[CrashReport]`, `[ArenaGuard] TailPad=1 InitobjFix=1` and `[MemDiag] Light=1` [verified ini].

| Feature | What it does to object lifetime | Verdict |
|---|---|---|
| `[Discourage] DestroyPark` (design G, the crash-I mechanism: deferred destructor, flush inside native callers) | **`DestroyPark=0`** in every release ini; nothing parks | not active [verified ini] |
| `[RouteSnap]` FreeLock / P5c graveyard (same park) | `Enabled=0`, `FreeLock=0` | not active [verified ini] |
| `[RouteGate] Enforce=1` destroy gate (key `0x2000003`) | a sim-thread `DestroyById` waits until the running route job ends; then the game's own destroy runs at the same call site, in the same order | delays a destroy, frees nothing itself [verified docs/88 + code] |
| `[Discourage] DestroyApply / DeadSweep / DeferFootprint` | applies or drops queued routing-footprint records at a destroy; footprints only | no actor or controller touched [verified ini text + docs/84 §7.2] |
| `[RouteAsync]` (worker plans, `IcCollect`, `FallbackAsync`, deadlines, `RefcountGuard`) | plans run on the worker and the game's IC copies the results back; `IcCollect` frees the job's own route state; `RefcountGuard` keeps a `+0x918` lock level and takes no object reference | no actor freed; a cut plan only fails the route [verified ini text; inferred for "no actor free": no destroy call in the job path, docs/88 §2] |
| `[UnseenFallback] Enabled=1` | deadline exits only (`Sites=0`, `FinishWalk=0`: no C1-C4 fake answers, no hold, `g_unHoldSkip` stays NULL, so the agent update at `0x83CF52` is the game's) | the route fails for unseen Sims; no lifetime change [verified ini + `unseenfbc.cpp:512`] |
| `[ArenaGuard] TailPad / InitobjFix` | byte patches in Mono's interpreter frame arena | not on this path [verified docs/67 §22.7, §22.9] |
| ts3fixes (v0.2.3 package) | shortcuts for unseen Sims (`kGoHomeShortcut`, `kBuildingTripShortcut`, `kLotTripShortcut`, `kVirtualHomeShortcut`: teleports through script APIs), `kNoRouteFailWalkOffscreen` (homeless townies fade out = EA's own `FadeOut(destroy)`), `kStuckWatchdog` (script reset); `kDisposeSpread` and `kFailRepeat` off [verified `tools/setup/build-fixes.ts`] | they can add **destroys and resets of unseen Sims** through EA's own script paths; none frees an actor natively [inferred] |

Verdict: **open. No ts3native mechanism found that frees that actor or keeps it alive past its owner** (medium confidence, static). The crash-I mechanism (park plus nested flush) is off in releases. EA's controller holds its actor raw (§26.1). The trigger the contributor names, the Free Vacation pickup destroying six Sims through `FreeVacationPickupSituation.Dispose`, is an EA script destroy of Sims that may still be actors in another Sim's route (a second actor in `body+0xB4`: walking together, carried) [inferred]. By the crash rule it stays "ours until proven"; the dump reads below decide it.

### 26.3 Dump reads that settle it (ask the contributor)

1. Element `E = [node+8]`: 0x48 bytes from `E` (refcount `+4`, actor `A = [E+0x1C]`, serial `[E+0x20]`, `+0x24`, `+0x2C`, `+0x30`, `+0x34`, `+0x3C`, `+0x40`) and the global counter `[0x114F590]`. The counter minus `[E+0x20]` says how many controllers were made after this one.
2. Body `esi`: `[esi+0x124]`, `[esi+0xB0]`, `[esi+0xB4]`, `[esi+0xB8]` and the dwords of the `+0xB4` array. **Is `A` equal to `[esi+0x124]` (site `0x84B756`), to an entry of the `+0xB4` array (site `0x84B958`), or to neither** (the controller outlived a change of the body's actor fields)?
3. `A`: `VirtualQuery` (state, allocation base, region size), 0x60 bytes from `A-0x10` (EA chunk header: size word at `A-0xC`, bit 1 = direct block), and what the `0x002B002B` data belongs to.
4. The first 0x40 bytes and vtable of `[esi+0x124]` (alive?), and the agent `ebx`: `[ebx+8]` (path), `[ebx+0x68]` / `[ebx+0x6C]` (swap list, §11.1).
5. From `ts3native.log`: the minute lines (`route async`, `route gate`, `unseen fallback`, `discourage`), any `crash log:` first-chance lines, and the ts3fixes shortcut and stuck counters for the last 5 minutes; whether S3SS, S3PP or Apex Radiance were installed.

How to read them: if `A == [esi+0x124]` and `A` is a Sim the pickup destroyed while its body still updates, EA's agent outlived its owner (then check the log for our destroy gate waiting on that id). If `A` is in the `+0xB4` array, another actor of this Sim's route was destroyed under it (EA's raw pointer). A ts3fixes shortcut, teleport or reset of that Sim in the same seconds points at ts3fixes.

### 26.4 Fix: `[EaCrashFixes] ConformActor` (0.36.121, eafix.cpp, counters v141 `ca*`, off by default; ledger docs/51 §394)

Rule: EA's bug or not, we fix it. What the actor is, and what the call asks [verified disassembly, 2026-10-30]:

| | |
|---|---|
| Actor type | `[body+0x124]` is the owner's **AnimationComponent `+8` interface**: the agent step `0x956850` looks up component bit `[0x11DC484]` (`g_CompBit_Animation`, `0x935770`), adds 8 (`0x956A2C`) and passes it as argument 2 of the `AnimatedSimBody` constructor `0x84EFF0` (stored at `0x84F0FC`). Interface vtable `0x10433EC`; the component's ctor is `0x93E5C0` (only caller `0x93F90D`, the descriptor ctor `0x93F900`), its destructor `0x941CD0` (only caller the deleting dtor `0x942E70` at `0x942E73`, vtable `0x104341C` slot 11). The dtor leaves `0x10433EC` at `+8`: a destroyed component still looks like one until its memory is reused. The `+0xB4` entries' type is not proven [inferred: the same interface of the walked-with object] |
| The call | slot `+0x10` = `0x93E130`: true when the component's rig (`[comp+0x24]+0xB0`) still runs the controller's serial `[ctl+0x20]` (`0x5828B0`), else false; `ret 4` |
| Callers | three walkers of the body's controller list, each with the same 11 bytes `mov ecx,[eax+1Ch] / mov edx,[ecx] / push eax / mov eax,[edx+10h] / call eax`: `0x84EABD` (the crash site), `0x849588` (`0x849570`: false skips the listener registration), `0x853521` (`0x853500`). In `0x84EA00` and `0x853500` false unlinks the node, unregisters the listener (`0x566590`) and releases the controller: EA's own cleanup of a finished controller |

Design: the three asks become `push eax; call CaGuard; nop5` (stdcall, pops the controller like the thiscall did). `CaGuard` calls the game's slot unchanged when the actor is a live AnimationComponent (its `actor - 8` is in a live set kept by call-site hooks on `0x93F90D` and `0x942E73`; the set is filled from process start, before any object exists). Otherwise it answers false ("controller done") when the actor is NULL, when its vtable word is `0x10433EC` but the component is not live (destroyed), or when the word is unreadable (SEH), outside `.rdata` or names a slot `+0x10` outside `.text` (freed memory: `0x002B002B` in #98, 0 in crash I). Any other in-image vtable gets the game's call (another actor type; `caOther` counts them, so a run shows whether `+0xB4` holds other types). The guard writes no game state; the only read of possibly freed memory is the actor's first dword under SEH. All five sites or none (the guard without the set would call every live component dead). The set: 131072 slots (512 KB), open addressing with tombstones, rebuilt when 3/4 used, SRW lock (ctor/dtor any thread, asks on the sim thread); above 65536 live components tracking stops (`caOverflow`) and unknown actors get the game's call.

Behaviour with live actors: identical (same call, same answer). With a destroyed actor: the controller is dropped by EA's own path instead of a call through freed memory. Residual risks: a freed actor whose block now holds another object with a valid vtable (not an AnimationComponent) still gets the call, as in vanilla; a controller whose actor died while it sat in the agent's swap buffer (`agent+0x68`, §11.1) is caught at the next walk after the splice back, which is where crash I faulted.

| | |
|---|---|
| Counters (v141) | `caOn` (sites guarded, 3), `caSkip[3]` (null, destroyed AnimationComponent, bad vtable), `caOther`, `caLive`, `caMaxLive`, `caOverflow`; log `ea fix: ConformActor: controller ... skipped` for the first 8 |
| Tests (offline) | selftest `TestEaCrashFixes`: live component -> the game's call and its answer; destroyed (dtor hook, vtable word left as EA leaves it) -> false, no call; same address re-constructed -> live again; null, released page, `0x002B002B`, 0 -> false; another type -> called; slot outside `.text` -> false; the 11 game bytes run before and after `CaPatchAsk` on an executable copy; 200,000 add/remove pairs with rebuilds, membership exact |
| Repro | `tools/harness/repros.json` `ea-conform-actor-destroy-loop`: 8 spawned Sims walk home at ultra speed and are all removed mid-walk, three rounds, plus a `kill_sim` of a routing Sim (the Free Vacation pickup itself has no agent action). Off/on in separate jobs; on: `caSkip[1]` names destroyed actors (0 possible: the race may not fire) |
| Conflicts | none with S3SS / S3PP / Apex (docs/68); probe builds with `/DTS3N_VCSITES2`/`5` list `0x85352A` inside the third ask: do not combine |

## 27. Crash W (issue #99, same contributor and build): the main thread's Sim thumbnail render reads a released source pose (rig `aurig`)

Static only (2026-10-30): issue #99 and its comment (the ts3native crash file `crashsig-54c97f19`), capstone on the Steam exe copy and on the v0.2.3 CI `ts3native.asi`. Ledger docs/51 §392.

### 27.1 Where the pointer comes from

| | |
|---|---|
| Fault | `0x54E79A` `movaps xmm0, [ecx+eax]` in the virtual pose modifier `0x54E630` (no direct callers): `ecx = [ctx+0x4C]` = source pose, `eax` = bone x 48, `edi` = destination pose, added per bone [verified disassembly + his registers] |
| Context | `ThumbnailManager_Update` -> `0xD5C726` -> **`0xD59C10`** (Sim thumbnail render) builds the pose context on its own stack at `esp0+0x410` (his `ebx 0x1AB760` gives `esp0 = 0x1AB350`): `+0x40 = [esp0+0x7C]` rig object (vtable `0xFC3310`, RIG key `aurig`), `+0x44 = [esp0+0xA8]` model (vtable `0xFF9B50` = `SceneModelFur`; docs/186 §10: allocated as `UI/ThumbnailManager/SceneModelFur`), `+0x4C = [esp0+0x45C]` source pose, `+0x58 = 2`. Destination = a 0x19B30-byte `Animation/RigPose` from `RigPosePool_Alloc 0x5748A0` (`0xD5B828`); evaluation call `0xD5B86F` [verified disassembly] |
| `+0x4C` writers | three branches before `0xD5B7AF`: (a) `0xD5B745-0xD5B77A`: lookup `0x5E0FD0` into a local RefPtr at `esp0+0x28`, copy `[obj+0xC]` to `+0x4C`, then **`RefPtr_Release 0x7460F0` on that local before the pose is evaluated**; (b) `0xD5B705`: `[[list + 4*i]+0xC]`; (c) `0xD5B788`: `[[model->0x6F4A50()]+0x24]+0xC` [verified disassembly]. In every branch the pose pointer is a raw `+0xC` field of an object the render holds no reference to; in (a) it drops its own reference first |
| The freed block | pages from `0xA078E000` are `MEM_FREE`; the pose starts at page offset `0x700`. EA's `Free` of a direct (large) block calls `VirtualFree(MEM_RELEASE)` at once (`0x4E5306`, docs/201) [verified disassembly], so a stale read of a large EA block faults in vanilla too. A direct block's user pointer sits just after its 64 KB-aligned base, so this pose lies inside a larger released region (a resource or pose blob, an EA core, or another allocator's block) [inferred] |

### 27.2 His question: does ts3native change when large blocks go back to the OS?

**No, not with the v0.2.0-v0.2.4 release defaults** [verified: release ini + 0.36.105 code]:

- `[Alloc]` (a thread cache in front of EA's Malloc/Free) is `Enabled=0`: no detour on `0x4E69D0` / `0x4E6A20` / `0x4E5920`. When on, it caches only chunks up to 1 KB and never direct blocks (docs/15).
- `[MemDiag] AllocSites=0`: no hooks on EA's heap entries or on `NtFreeVirtualMemory`. `Light=1` only reads (a `VirtualQuery` walk, heap counters `+0x488` / `+0x48C`) on the memory worker.
- `[CrashForensics] Enabled=0`, `[ResourceCache] TrimAfterCleanup=0`, `[SaveCompress] Enabled=0`, `[ThreadStack] Enabled=0`, `[D3DMultithreaded] Enabled=0`, `[Verify] Enabled=0`. No release feature hooks the thumbnail manager, the pose code, the rig or outfit code, or the resource cache.
- `[Decompress]` writes into the game's own output buffer; allocation and free stay EA's.
- `[GC] InitialHeapMB / GrowthStepMB` only size Boehm's managed heap, and Boehm never unmaps (docs/198). Side effect: fewer collections, so managed finalizers that release native handles run at other times [inferred: timing only].
- `[ArenaGuard] TailPad / InitobjFix` patch Mono's interpreter frame arena, not EA's heaps.

EA's own allocator releases big blocks at once (`0x4E5306`). Apex Radiance hooks that site (docs/187), so the timing differs with Apex installed. S3SS's mimalloc owns only `msvcr80` `malloc` / `free`, not EA's heaps (docs/15).

### 27.3 `ts3native+0x1C992` in his crash file

In the v0.2.3 CI asi, `0x1001C992` is the return address after `call __security_check_cookie` (`0x1001C98D`) in the epilogue of `CfOnFault 0x1001C140` (the [CrashForensics] dump; it returns at once when that is off). Only the vectored handler `XcptLog 0x100817D0` calls it (call at `0x100818BB`), and only for an access violation or `0xC000001D` outside ts3native [verified disassembly of the release asi]. It is **not a call-path frame**: it is a stale word that an earlier first-chance exception on the main thread left behind, now inside a caller's uninitialised locals [inferred]. So the main thread took at least one earlier first-chance access violation or illegal instruction in that process and survived it; `ts3native.log` lists the first 16 (`crash log:` lines). Our local `asi.rel020` (a different toolchain build) has other bytes there: always map offsets against the shipped zip.

### 27.4 Verdict and dump reads

Verdict: **an EA-side lifetime race, not caused by a ts3native release feature** [inferred; medium-high confidence for "not our allocator", the owner of the free still open]. The render keeps raw pose pointers (§27.1). Candidates for the free: the contributor's pack-up path on the sim thread (`GenerateCrossWorldThumbnail(force)`, then dispose and outfit uncache) and his own core's outfit changes. Ask:

1. Frame check: `[0x1AB3CC]` should be `0x7CFCCB80` and `[0x1AB3F8]` `0x7E850E80` (confirms `esp0 = 0x1AB350`).
2. Branch (a): `[0x1AB378]` (the RefPtr local) and, if non-zero, 0x20 bytes of that object (refcount `+4`; is `+0xC` `0xA078E700`?). `[0x1AB450]` (`esp0+0x100`) and its `+0x20C` / `+0x210` vector.
3. `VirtualQuery(0xA078E000)`: the free region's base and size, and the regions just below and above (allocation base, size, state, type). A free region that starts on a 64 KB boundary, with a size matching an EA direct block or core, says whose block it was.
4. `[0x1AB3D4]` (the destination RigPose object) and whether `0x37C7D9E0` is alive.
5. `ts3native.log`: the `crash log:` first-chance lines, the `diag: mem` lines (EA direct blocks) and the `save:` / `diag:` lines of the last minutes; his core's `SimOutfit.Uncache` log lines in the seconds before; whether S3SS, S3PP or Apex were installed.
6. One run with EA's stock core DLLs and the same autopilot (his `IsValid` outfit drop is his only change on outfits).

### 27.5 Fix: `[EaCrashFixes] ThumbPose` (0.36.121, eafix.cpp, counters v141 `tp*`, off by default; ledger docs/51 §394)

Branch (a) of `0xD59C10` keeps its object in a scoped RefPtr at `F+0x28` (`F` = the render's esp after its prologue, 16-aligned) and ends that scope with `RefPtr_Release 0x7460F0` at `0xD5B77A` (release only: decrement, deleting destructor at 0; the local is not cleared and not used again) [verified]. From there to the evaluation call `0xD5B86F` every path is straight (pose pool alloc `0x5748A0`, `0x74B950` / `0x574F50` both `ret 4`, the evaluator thunk `0x54C570`), and at `0xD5B874` the stack is `F` again (`[esp+0x18]` is the loop index stored at `0xD5B8B5`, `[esp+0x6E0]` the evaluator) [verified].

Design: the release call at `0xD5B77A` is retargeted to `TpDefer` (same register contract: `ecx` = the RefPtr, no stack arguments), which keeps the object in a slot keyed by thread and frame (`ecx - 0x28`) instead of releasing it. The 7-byte `mov ecx,[esp+0ACh]` at `0xD5B874` becomes `call TpAfterStub; nop2`: the stub saves every register, releases this frame's kept object through the same `0x7460F0` (same thread, a few calls later), restores the registers and runs the moved instruction (flags are dead there). Branches (b) and (c) keep nothing and are unchanged; they also hold no reference to their pose's owner, which this fix does not change (residual risk, no crash seen there). 8 slots (main and sim thread each use one); all taken = the game's order (`tpNoSlot`). Both sites or none; the release point is written first.

| | |
|---|---|
| Counters (v141) | `tpOn`, `tpHeld` (branch (a) renders kept), `tpLastRef` (ours was the last reference at the release: the game would have evaluated a pose whose owner was already released), `tpNoSlot` |
| Tests (offline) | selftest `TestEaCrashFixes`: last reference kept across the evaluation and freed after it, another frame releases nothing, a shared owner is released without `tpLastRef`, an empty RefPtr keeps nothing, full slots release at once; the naked stub run on a real stack (frame match, release, `ecx` = the moved load, `eax`/`ebx`/`esi`/`edi` kept); patch on dummy bytes (foreign bytes: nothing written) |
| Repro | `tools/harness/repros.json` `ea-thumb-pose-pack-loop`: household thumbnails while 8 spawned Sims are removed, three rounds (the contributor's cross-world thumbnail during pack-up has no agent action). `tpHeld > 0` shows branch (a) ran |
| Conflicts | none with S3SS / S3PP / Apex at `0xD5B77A` / `0xD5B874` (docs/68) |

### 27.6 First dump: an out-of-bounds read, and `[EaCrashFixes] ThumbPoseBones` (on since 2026-11-05)

Dump: Apex Radiance's `ApexRadiance_Crash.dmp` of a contributor's old-save base run (2026-11-04, no fixes, 18.8 min; registers, stacks and pointed-to memory, no memory map). Same fault and `ebx 0x1AB760` as §27.1.

| | |
|---|---|
| Branch | (a) [verified]: RefPtr local `[0x1AB378]` = `0xC3853C90`; its `+0xC` = `0xC412F140` = `ecx` = context `+0x4C` |
| Owner alive | [verified] vtable `0xFC2D30`, refcount `+4` = 1, `+8` = 42 after the release, so ThumbPose (§27.5) would not change this crash |
| Holder | [verified disassembly] ctor `0x574750`: `+8` bone count, `+0xC` a `count x 48`-byte pose (`0x4E3F90`). `0x5E0FD0` builds it once per model (`+0x214`) from the model's rig and returns it only when the counts still match (`0x5E1080`) |
| Read | [verified dump + disassembly] the modifier `0x54E630` looks up `rootWorld`, `L_footWorld`, `R_footWorld` (`0xFC1230` / `0xFC1224` / `0xFC1218`) in the destination rig `[ctx+0x40]` (indices 222, 223, 224 at `[esp+0x40]`) and reads the source pose at those indices; the only size check is `[ctx+0x4C] != 0` (`0x54E754`). 222 x 48 = `0x29A0`, past a 42-bone pose (2016 bytes); it faults only when the read crosses into an unmapped page |
| Fix | `ThumbPoseBones`: at the three stores of the source pose (`0xD5B708` b, `0xD5B76C` a via `0xD5B768`, `0xD5B7A1` c) a `0xFC2D30` holder with fewer bones than the destination rig (`([rig+0x30] - [rig+0x2C]) / 112`, as `0x575650`) stores 0. A holder with as many or more bones, another holder type and an unknown rig keep the game's value. The modifier reads `[ctx+0x4C]` only at `0x54E78D` [verified: whole function], and branch (c) already stores 0 |
| Log | `ea fix: ThumbPoseBones: branch <a|b|c>: source pose <n> bones, destination rig <m>: not used (<dropped> of <seen>)` for the first 8 and every 1024th |
| Tests (offline) | selftest `TestEaCrashFixes`: 42 vs 225 dropped, 225 / 300 / another type / no rig unchanged, unreadable holder 0; the three naked stubs on a real stack (stored value, `ecx` / `esi` / `eax` contract, other registers kept); patch all-or-none, once |
| Game run | [measured, contributor] 2026-11-05, the same old save (DXVK + S3SS + S3PP + Apex Radiance), release defaults + `ThumbPoseBones=1`: 4 autopilot runs PASS, 25.5 + 35.5 + 35.4 + 35.6 min; installed (`OK   ea fix: ThumbPoseBones`); every run reached the 8-line log cap, source pose 42 bones vs destination rig 257, the 8th at 29-35 renders seen (each one an out-of-bounds read without the fix). Two more runs failed in the harness before play (an age-job argument bug, a stuck "Saving..." dialog), not crashes. PR #271; no result file, the runs are in the PR text |
| Review | [verified disassembly] 2026-11-05, import of #271: site bytes match; `[esp+0x7C]` is written at `0xD5B0F6` and copied to `[ctx+0x40]` at `0xD5B7BC`, no jump lands inside a patched range, flags are dead after all three sites, `[ctx+0x4C]` starts 0 (`0x83B124`) and stays 0 on the `0xD5B798` path, so a null source pose is a normal value. `([rig+0x30] - [rig+0x2C]) / 112` matches `0x575650`. No overlap with ThumbPose (`0xD5B77A`, `0xD5B874`); those runs had ThumbPose off, the pair is not run together in game |
| Unverified | that it prevents crash W (2 crash W in ~12 old-save runs on 11-04; needs more runs); which model has a 42-bone rig |
| Conflicts | none with S3SS / S3PP / Apex in `0xD59C10`-`0xD5B8FF` (docs/68) |

## 28. Crash X (2026-10-30, a contributor's 35-minute test session on v0.2.4 release defaults with Apex Radiance): the Sim builder reads a CAS mesh's bone list past its end

Static only (2026-10-30): the EA crash report and capstone on the Steam exe copy. There is no dump.

### 28.1 Fault [verified: report + disassembly]

| | |
|---|---|
| Report | ACCESS_VIOLATION reading `0xA2F900B4` (`MEM_FREE`) at `0x5C6E07`. Main thread. EA's walk shows `0x5D3885`, `0x5DC19C`, `0x5F06C6` (`SimService_Process 0x5EBC50`) and `0x5F120C` (`SimService_Update 0x5F0E50`): the same service path as crash B (§1) |
| Fault site | `0x5C6E00` (13 bytes, `ret 4`): `eax = [ecx+0x58]`, `ecx = [esp+4]`, `eax = [eax+ecx*4]`. It is slot `+0x54` of vtable `0xFE32C0`, which the `CAS/GeometryResource` name follows in `.rdata`. Slot `+0x50` = `0x5C6DF0` returns `([ecx+0x5C] - [ecx+0x58]) >> 2`. So `+0x58`/`+0x5C` are the begin and end of the mesh's bone-hash vector, and slot `+0x54` returns entry `i` with no bounds check. Only `0x5C6EBB` and `0x5C7045` store the vtable, and nothing calls either getter directly |
| Registers | `eax` (vector begin) = `0xA2F8FF00`, `ecx` (index) = `0x6D` = 109. The read at `begin + 109 * 4` lands in a free region that starts `0x100` bytes after `begin`. The vector therefore has at most 64 entries, and the index is past its end. The `+0x58` load succeeded, so the GEOM object itself was readable |
| Caller | `[esp]` = `0x5D1633`. The getter has no frame, so EA's walk skips this frame and shows `0x5D3885` instead (the return of `call 0x5D1010` at `0x5D3880`). `0x5D1010` is the skin pass. It reads each of a vertex's four blend indices as a byte (`0x5D1627`), calls slot `+0x54` at `0x5D1631`, then looks the returned hash up in an EASTL map at `[ebp+0x20]` (the rig's bones). On a miss, `edx` is the map's anchor and the code stores `[anchor+0x14]` (the map's size field) as the bone slot (`0x5D165B`-`0x5D1672`). This is EA's own path for a bone the rig lacks. `edx` is reloaded at `0x5D163B` and `ecx` at `0x5D1633`, so nothing but `eax` is live after the call |

### 28.2 Cause [inferred]

The most likely cause is a GEOM resource whose vertex blend indices name a bone past the mesh's own bone list, probably custom content: a mesh edited without its bone list being updated. The index is a byte, so the getter can read up to 255 entries. Most such reads land in other heap data and return a hash the rig lacks, which takes the miss path above without a crash. The read only crashes when the list sits near the end of a committed region, which is why this crash is rare. The report does not say which mesh it was.

It is not a use-after-free, because the object's own fields were readable. No ts3native hook is on this path. The nearest Apex Radiance site is `CasTriSort 0x5D1960` (docs/data/apex-hooks.csv), and `node tools/hook-sites.ts --check` lists no shared address.

### 28.3 Fix: `[EaCrashFixes] GeomBoneIndex` (0.36.149, eafix.cpp, off by default; ledger docs/51 §411)

The fix points vtable slot `0xFE3314` at `GbGetBone`, which has the same contract: `ecx` = the GEOM, one stack argument, `ret 4`. For an index inside the vector it returns the game's entry. Past the end it returns 0, so the caller's map lookup misses and EA's missing-bone path runs.

A rig bone whose hash is exactly 0 would take that vertex weight instead of missing [inferred: no such bone is known]. The install checks three things before it writes: the slot names `0x5C6E00`, the getter's 13 bytes match, and the count getter's 10 bytes match (they confirm the vector layout). If any check fails, it logs a SKIP and writes nothing. Every virtual caller of slot `+0x54` gets the game's answer for in-range indices.

| | |
|---|---|
| Counters | none. The first 8 out-of-range asks are logged as `ea fix: GeomBoneIndex: GEOM <p> (key <type>:<group>:<instance>) asked for bone <i> of <n> (caller <return address>)` |
| Tests (offline) | selftest `TestEaCrashFixes` covers four cases. (1) In-range bones are unchanged. (2) An index equal to the size, index `0x6D` and an empty list are all answered 0. (3) The game's two getter byte strings run on the fake GEOM, which checks the layout. (4) The patch on a stand-in slot: a foreign count getter means nothing is written, the slot is patched only once, and the patched slot answers like the hook |
| Repro | none. A repro needs a mesh with a bad blend index. The log line names the GEOM pointer, not its resource key |
| Conflicts | none with S3SS / S3PP / Apex at `0xFE3314` / `0x5C6E00` (docs/68) |
| Open | none left from crash X: the mesh is named in §28.4 |

### 28.4 Live check (2026-10-30, one session) [verified]

One 35-minute session on a contributor's test save, with the branch's CI build (then 0.36.143, before the rebase onto 0.36.148) and `GeomBoneIndex=1`. During world load (`ScriptProxy.PostLoad`) it logged 8 lines for one GEOM: `asked for bone 109 / 104 / 113 / 115 of 5 (caller 0x005D1633)`. 109 is crash X's index, and the mesh has 5 bones, so this save loads the bad mesh every time. The game ran on for the whole session. The lines confirm I-409-4: a readable GEOM with a real list (`n` = 5) whose blend indices are past it. Without the fix these reads land past the list; whether they crash depends on the heap layout (§28.2). A second session (same save and switch, plus the other two guards) logged the same 8 asks. Its first line also printed the resource base's fields: instance `0xC014AA218DAFEEA9` at `+8`, type `0x015A1849` (GEOM) at `+0x10`, group `0x009B6C36` at `+0x14`. The CC scan below has exactly that GEOM (5 bones, 512 vertices, filler 115) [verified]. It is a LOD of the EA Store hair `amHairSTORE09FaLayeredSide` (adult male), which the contributor's CC manager had merged into one of its library packages. So store meshes from EA itself carry these filler indices. The log lines now print the key as `<type>:<group>:<instance>`.

A scan of the same install's CC (every GEOM in `Mods\Packages`, read-only) found 251 of 9,856 GEOMs with a bone-assignment index past the mesh's own bone list: 27 with 5 bones, 61 with 1, others up to 58. Their out-of-range values are 115 or 255. **Every out-of-range index sits in a slot whose weight is 0** [verified: scan]: 0 vertices have a weighted index past the list. So these values are filler that the export tool wrote into unused slots, and the skin pass reads all four slots whatever their weight. The fix's answer of 0 changes no weighted bone for any of them (I-409-5). It also means the out-of-range read happens for every such mesh on every build, so crash X can come back on any install with this kind of CC whenever a bone list ends at a free region.

## 29. Issue #80 (2026-10-18 to 10-28, four crashes in about 60 sessions on 0.36.37a, heavy CC): the Sim builder reads a CAS mesh's material through a null pointer

Static, plus the contributor's two full dumps (described in the issue; none is committed).

### 29.1 Fault [verified: dumps + disassembly]

| | |
|---|---|
| Report | ACCESS_VIOLATION reading `0x00000004` at `0x5DDFBD`, main thread, `eax` = 0. Signature `crashsig-95ce882e`. The nearest symbol names it `ModelBuilder_Dtor+0x1B6D`, but it is a separate function: the Sim builder's per-part step `0x5DDB30`, called once per part from the loop at `0x5DE4CA` |
| Site | `0x5DDFA9`-`0x5DDFB0` calls the part's GEOM slot `+0x58`. For the GEOM vtable `0xFE32C0` that slot is `0x580420` (`mov eax,[ecx+0x28]; ret`): the embedded material. `0x5DDFBD` then reads the shader hash at `[eax+4]` with no null check. `0x5DE009`-`0x5DE01C` calls the slot again and copies 16 bytes from it |
| Every path | The step reads the material on every path. When the part joins an existing group it reads it at `0x5DDC45`-`0x5DDC5A`. When it starts a new group it reads it at `0x5DDFB0`-`0x5DDFBD`. No branch between the start check and those reads leaves the function |
| Cause | A CAS VPXY LOD 1-3 entry names a GEOM with shader 0 and vertices: a custom-content morph mesh used as a LOD. Such a GEOM has no embedded material (MTNF). In both dumps it was the same CC hair. EA's own CAS parts have no such entry (the contributor's scan of the install). The VPXY must be the part's mesh VPXY: morph VPXYs (fat, fit, thin, special) name only shader-0 meshes with position, normal and vertex-id data, by design, and are not built through this step [verified: VPXY LOD lists of an EA Store library package] |

### 29.2 Fix: `[EaCrashFixes] GeomNoMaterial` (0.36.149, eafix.cpp, off by default; ledger docs/51 §413)

The step already skips a part whose mesh pointer is null or tagged. That check is at `0x5DDB5D` / `0x5DDB67`, which go to the tail `0x5DE0E2`; the tail returns 1, and the caller ignores the result. The fix replaces the 10 bytes at `0x5DDB63` (`mov eax,[ebx]; test eax,eax; je 0x5DE0E2`) with a jump to `GnStub`. The stub keeps the null check. It also takes the same skip when the GEOM's slot `+0x58` is the game's getter `0x580420` and `[GEOM+0x28]` is 0. Every other part continues at `0x5DDB6D`, which reloads `eax`, `ecx` and `edx`.

Because every path reads the material, the fix changes only parts that crash today [verified static]. The skipped part is not built for that LOD, so a CC hair goes missing at that distance instead of crashing the game. The install checks three things first: the 10 site bytes, the slot `0xFE3318` naming `0x580420`, and the getter's 4 bytes. If any check fails, it logs a SKIP and writes nothing. No instruction branches into `0x5DDB64`-`0x5DDB6C` (scan of `.text`).

| | |
|---|---|
| Counters | none. The first 8 skips are logged as `ea fix: GeomNoMaterial: GEOM <p> (key <type>:<group>:<instance>, builder LOD <n>) has no embedded material` |
| Tests (offline) | selftest `TestEaCrashFixes` runs the stub with landing pads in place of the two targets. Four cases: a GEOM with a material continues, a GEOM without one is skipped and counted, another type with the same field 0 continues, and a null mesh takes the game's skip without being counted. `ebx`, `esi`, `edi` and `ebp` are unchanged at both pads. The game's getter bytes run on the fake GEOM. The patch on a stand-in site: a foreign slot or foreign site bytes mean nothing is written, and the site is patched only once |
| Repro | `node tools/cc-mesh-scan.ts --repro <package> <GEOM instance> <out>` (PR #119) writes a copy of one GEOM without its material; in `Mods\Overrides` for one session it reproduces the fault (§29.3) |
| Conflicts | none with S3SS / S3PP / Apex at `0x5DDB63` / `0xFE3318` / `0x580420` (docs/68). Apex's nearest site is `CasTriSort 0x5D1960` |
| Open | Whether a Sim with the part skipped at one LOD shows anything wrong besides the missing part at that distance [inferred: the same as a part whose mesh failed to load] |

### 29.3 Live A/B with a made repro (2026-10-31) [verified]

No CC with the #80 shape was at hand, so the test made one. It took LOD 3 of the EA Store hair from §28.4 (`015A1849:009B6C36:C014AA218DAFEEA9`), cut its material, set shader 0, and loaded it as a one-resource package from `Mods\Overrides`, one session per arm. The hair's mesh VPXY still names LOD 0-2 with a material, so the part has the §29.1 shape. Both arms used the same contributor save, CI build `a7572875` (0.36.149 plus key logging) and `GeomBoneIndex=1`:

| Arm | Result |
|---|---|
| `GeomNoMaterial=1` | From world load on, 8 log lines (the cap): `GEOM … (key 015A1849:009B6C36:C014AA218DAFEEA9, builder LOD 3) has no embedded material: part skipped`. 20 minutes of play, saved, no script errors. A second session from that save: 30 minutes, the same 8 lines, saved, no errors |
| `GeomNoMaterial=0` | Crash during world load: ACCESS_VIOLATION reading `0x00000004` at `0x5DDFBD`, main thread, `eax` = 0, `edx` = `0x580420` (the getter). This is the fault in the #80 report |

This is I-411-3's check: the skip is logged and the crash does not happen. Nobody looked at the Sim at that distance, so whether it looks right is not checked. With the hair's LOD 3 skipped before the skin pass, `GeomBoneIndex` logged a different filler mesh instead: `015A1849:00BEE823:93A4E607E5383AFE`, bone 2 of 2, from the install's merged overrides package. The second session logged a third one, `015A1849:00BEE823:00000000ECC7C68B`, also bone 2 of 2. The scan finds 59 such meshes in `Mods\Overrides`, all with weight 0.

## 30. Crash Y (2026-10-31, kit4 Baseline-IP, 5 of 5 sessions): the world A\* follows a neighbour reference outside the routing graph

Live and static (2026-10-31): three kit4 jobs (`ip-locktaker-verify-146`, `ip-venuememo-verify`, `ip-deadrefs-saves`), one control with the v0.2.4 release pair, and one run with only `[RouteAsync] Enabled=0`. The EA crash files' registers and stacks were read; capstone was run on the Steam exe. Issue #132, ledger docs/51 §416.

### 30.1 Sites and what they read

| Session | Fault | Function | Read |
|---|---|---|---|
| kit `ip-locktaker-verify-146`, `ip-venuememo-verify` | `0x67664F` | world A\* `0x676260` | the closed-bitset row byte for the neighbour's (chunk + 1, waypoint >> 3) |
| kit `ip-deadrefs-saves` | `0x67663F` | world A\* | the closed-bitset row-pointer table at index chunk + 1 |
| control (release pair) | `0x6743FE` | `RouteCost_Mode3 0x674250` | the parent search node's chunk record: `R+8[chunk]` + `R+0xC`, then `recOff[waypoint]` |
| `[RouteAsync] Enabled=0` | `0x6742B5` | `RouteCost_Mode3` | the same for the neighbour ("to") pair |

All five read past a table's end into pages that are not committed: `MEM_RESERVE` in three sessions, `MEM_FREE` in the two `0x67664F` ones.

### 30.2 The bad pair

- **The A\* decodes a neighbour without a range check [verified, disassembly].** For each reference of the current waypoint (docs/99 §1.3 packing), the A\* computes target chunk = current chunk + (dz - 3) · w + (dx - 3) in 16-bit registers, with w read as a u16 from `R+0`. It masks the waypoint to 10 bits and stores both at `[esp+0x1C]` / `[esp+0x1E]` (`0x67661D`, `0x676622`). The pair then indexes the closed bitset (`0x67663F`, `0x67664F`) and goes to the cost function, which indexes R's tables with it (`0x67429C`, `0x6742B5`). Only `0xFFFF` is tested, in the cost function.
- **Run 168 holds the bad pair [verified, crash file].** The "to" pair on the A\*'s stack is chunk `0xFFD5` (-43) and waypoint `0x207` (519); the fault register shows the same waypoint. Isla Paradiso is 64 x 64 chunks (4,096), and no shipped chunk has more than 254 waypoints. With w = 64, -43 is one chunk north of chunk 21, which is on the map's north edge [inferred: the word could also be garbage from a wrong record].
- **Call stack (run 168):** A\* `0x67667F` <- `0x65F80E` <- `0x661B9B` <- `0x81BF7E` <- `0x82C7B2` <- `0x82F09D` <- `0x74BD8D`.
- **The shipped graph is clean [verified, `tools/rtdata-decode.ts`].** `IslaParadiso.world` reports version 2, 64 x 64 chunks, 42,081 waypoints, 173,104 edges, `refs out of range 0`, `one-way edges 0` and 61 components. So the out-of-map reference, or the wrong current node, appears at runtime. Candidates are a lot update rewriting R (Isla Paradiso is the world where houseboats add lots, docs/200) or a start node built from a position outside the map. The fix's log lines (§30.4) name the current waypoint and the reference word.
- **Not ours [verified, 1 run].** The `[RouteAsync] Enabled=0` session logs `route async disabled (Enabled=0)` and has no route worker; all 14 route-gate lines are on the sim thread. It crashed 15 s after the world loaded. The release ini has no switch that patches inside the A\*: `[RouteBudget]` replaces the cost-function immediates `0x676340`-`0x676368` only when on, and `[RouteEarlyOut]` is off.
- **Save-dependent [measured].**
  - Baseline-IP (kit4) and Baseline-IP-Spring crashed 5 of 5, at 15 s to 5 min.
  - `BigSave-IP` with v0.2.4 had 0 crashes in 8 sessions (2026-10-30, 4 with release defaults and 4 with RouteAsync / GC / SaveWrite / Decompress off).
  - The Bridgeport and Sunset Valley kit jobs on the same build had no crash.

### 30.3 Fix: `[EaCrashFixes] WorldAStarRefs` (0.36.151, `wastar.cpp`, counters v146, off by default)

- **Patch.** The 7 bytes at `0x676627` (`mov edx,[esp+1Ch]; movzx ecx,ax`, right after both stores) become `jmp WaStub; nop2`.
- **The check.** The stub calls `WaRefOk(esp, esi)`, which tests chunk < `w · h` (`R+0`, `R+4`) and waypoint < the chunk's count (the u16 at `R+0xC + R+8[chunk]`, read under SEH).
- **In range:** it runs the two moved instructions and continues at `0x67662E`.
- **Out of range:** it jumps to `0x67673F`, the A\*'s own path for an already closed neighbour. That path reloads the neighbour index (`[esp+0x58]`) and the current record (`[esp+0x2C]`) from the stack, so no register state carries over [verified disassembly].
- **Coverage.** A bad pair then never enters the open list, so the parent-pair site `0x6743FE` is covered as well. Start and goal nodes do not pass through this decode [inferred: no crash at the current-record read `0x6765AF` so far].
- **Effect.** The search behaves as if that edge were absent: a route that only existed through it fails like any other blocked route. Game state is never written.
- **Cost.** One call per expanded neighbour [inferred: under 1% of an A\* expansion; not measured].
- **Counters.** `waOn`; `waSkip[0..2]` = chunk outside the map, waypoint past its chunk's count, record unreadable.
- **Log.** One line for each of the first 8 skips: neighbour index and count, current (chunk, waypoint), record flags, reference word, decoded pair, map size, chunk count.
- **Tests.** Selftest `TestWorldAStarRefs` (`wastar-test.h`): patch bytes; in-range and four out-of-range pairs; the stub runs with both exits recorded and the callee-saved registers kept.

### 30.4 Test plan

- **A/B.** Baseline-IP with the CI asi, `[EaCrashFixes] WorldAStarRefs=1` against `=0`, at least 2 clean sessions per arm. The off arm is expected to crash (5 of 5 so far); the on arm is expected to run 12 minutes with `waSkip` > 0.
- **The skip lines.** They tell whether the bad reference sits in a waypoint's own list (a reference word with dz or dx pointing off the map) or comes from a wrong current node (the record flags, or a count that does not fit).
- **If the reference is R data:** next is the writer, i.e. which lot update writes a reference across the map edge (docs/91 `ApplyLotUpdate`).

### 30.5 The cause: blocked-flag writes through stale waypoint ids (0.36.152, `[EaCrashFixes] PortalFlagRefs`)

Live (run 173, `WorldAStarRefs=1` on Baseline-IP, 12 minutes, no crash) plus the shipped graph and the exe, 2026-10-31.

- **The skipped references are shipped references with bit 15 cleared [verified, 1 run + file].** All 8 skip lines name two reference words.
  - Waypoint (1754, 2), neighbour 0: `0x0C0E` at runtime. The file has `1818/14` there, i.e. dz +1, dx 0, which encodes as `0x8C0E`.
  - Waypoint (1563, 0), neighbour 91: `0x0C17` at runtime. The file has `1627/23` there, i.e. dz +1, which encodes as `0x8C17`.

  With bit 15 cleared, dz + 3 = 4 becomes 0, so dz +1 decodes as dz -3. The targets (1562, 14) and (1371, 23) are in chunks with no waypoints, the case the A\* then reads past.
- **Two writers clear that bit [verified, disassembly].** Only two instructions in the routing code clear bit 7 of a byte at +1 of a computed address: `0x82A200` and `0x82B6F3` (a scan of the exe for `and byte [m+1], 7Fh`, `and word [m], 7FFFh` and `btr word [m], 15`). Both clear a waypoint's blocked flag (record + 1, bit 0x80), and both have `or ..., 80h` partners.
  - `RoutingWorld_RestoreLotBlockedBits 0x82A190` (docs/112) walks a lot's portal objects (flag bit 1 at `+0x58`) and decodes each one's stored id at `+0x4C`: chunk = id & 0x3FFF, waypoint = id >> 14 & 0x3FF, bit 29 = blocked.
  - `RoutingWorld_ApplyLotUpdate 0x82B590` decodes each reference of a lot waypoint with the A\*'s own formula (32-bit chunk here).

  Neither checks the pair. The record address is chunk base + `recOff[waypoint]`, so for a waypoint past its chunk's count the "offset" is whatever the next bytes hold.
- **The two corrupted words are exactly where two such pairs land [verified, offline on the shipped layout].** `rtdata-decode.ts --mem-image` rebuilds the in-memory R. For every chunk and every waypoint index from the chunk's count to 1023, it computes the record address the writers would use.
  - Only (1753, 13) lands on the first word. Chunk 1753 has 11 waypoints.
  - (1563, 20) lands on the second (chunk 1563 has 18), and so does (1478, 737).

  The writers then clear bit 7 of byte + 1, i.e. bit 15 of the reference. Both pairs are 2 past their chunk's count, which fits waypoints a lot update added and later removed while a portal or a lot waypoint kept the stale reference [inferred: the lot update that left them is not identified].
- **Why only some saves [inferred].** The pair depends on which lots carry portals near which chunks. Baseline-IP and Baseline-IP-Spring (the same town, so presumably the same lots) crash; BigSave-IP and the other worlds' kit saves do not.
- **Fix: `[EaCrashFixes] PortalFlagRefs`** (off by default, counters `pfOn`, `pfSkip[2]`).
  - The writer 1 index computation (11 bytes at `0x82A1CF`) becomes a jump to `PfStub1`; the writer 2 one (6 bytes at `0x82B6B1`) becomes a jump to `PfStub2`.
  - Each stub checks the pair with the same test as `WorldAStarRefs` (chunk < w · h, waypoint < count, R at world + 0x54).
  - For a pair outside R it skips that write: writer 1 goes on with the next portal (`0x82A204`); writer 2 goes on with the next reference (`0x82B7D5`, which reloads its state from the stack). Writer 2's skip also leaves out that reference's portal bookkeeping (`0x633EA0`, `0x656120`, the bit 29 copy), which would otherwise run on the same bad pair.
  - The first 8 skips are logged.
  - `WorldAStarRefs` stays useful as a second line for corruption from other writers.
- **Test.** Baseline-IP with `PortalFlagRefs=1` and `WorldAStarRefs=0`, at least 2 sessions.
  - Expected: no crash, `pfSkip` > 0 with the pairs (1753, 13) and (1563, 20) among the skip lines, and with both on, `waSkip` = 0.
  - A crash, or A\* skips with `PortalFlagRefs` on, would point to a third writer.

### 30.6 In-game A/B (the contributor's CI asi, numbered 0.36.150 / 0.36.151 in PR #136 = 0.36.151 / 0.36.152 here, Baseline-IP, 12-minute sessions, 2026-10-31)

| Run | Switches | Result |
|---|---|---|
| 173 | WorldAStarRefs=1 | no crash; 8 A\* skip lines (the two references of §30.5); both end saves returned 12 |
| 174 | off | crash at `0x6742B5`, 24 s after the world loaded |
| 175 | WorldAStarRefs=1 | no crash; 8 A\* skip lines (the same two references); both end saves returned 12 |
| 176 | WorldAStarRefs=1 on BigSave-IP (control, 8 minutes) | no crash, 0 skips, save returned 0 |
| 177 | PortalFlagRefs=1, WorldAStarRefs=0 | crash at `0x6742B5` (sim thread), 7 minutes after the world loaded |
| 178 | PortalFlagRefs=1, WorldAStarRefs=0 | no crash; 8 skip lines, all from writer 1 (lot portal ids): (1563, 18) to (1563, 23) with chunk count 18, (1626, 6) and (1626, 7) with chunk count 6; both end saves returned 12 |
| 179 | off | crash, 3 minutes after the world loaded |

- **`WorldAStarRefs` stops the crash [verified, 2 of 2 sessions; off 8 of 8 crashed].** The off count includes three kit4 jobs, a release-pair control, two `[RouteAsync] Enabled=0` runs and runs 174 / 179.
- **`PortalFlagRefs` is the right writer but not the whole cause [verified, 2 sessions].**
  - Writer 1 does write through stale lot portal ids, and (1563, 20) is among them, as §30.5 predicted.
  - (1753, 13) was not among the 8 logged skips, and run 177 crashed with the switch on. So either more skips came after the 8 logged ones and something else still reaches the A\*, or a third writer clears the first word [inferred; a session with both switches on would show which: A\* skips there mean the corruption still happens].
  - The stale ids are 1 to 6 past their chunk's count. They are lot portals that kept their ids after the chunk lost waypoints [inferred].
- **The end-of-session save failure (Error 12) is this save's, not the fix's [inferred].**
  - BigSave-IP saved normally with `WorldAStarRefs=1`.
  - Baseline-IP without the fix crashes before its first save, so it cannot be compared directly.
  - `PortalFlagRefs` did not change it (run 178), so it is not those flag writes [inferred].

## 31. Isla Paradiso Error 12 (2026-10-31 to 11-02, 13 of 13 saves of Baseline-IP and Baseline-IP-Spring): not memory, not Apex; the world file is written whole, minus the CAS SimService state and 7 lot records

Live (2026-11-02, two local queues `ip-error12-local` and `al35-one-local`, both on `fix/compaction-save-result` at 62f81b7
with the 2026-11-02 asi) and offline (the DBPF index of every `.sims3.bad` folder the harness left since 10-31, read with a
40-line index parser). §30 left the end-of-session Error 12 as "this save's, not the fix's"; this section says what the
failed save is missing.

### 31.1 Arms (all `save normal` at once after the load unless noted; the save returns 12)

| Job | What differs | Memory at save start: free / largest free / commit MB | Result |
|---|---|---|---|
| `ip-e12-wa1-play0` | `WorldAStarRefs=1` (§30) | 1454 / 1379 / 2269 | 12 |
| `ip-e12-wa1-pf1-play0` | `WorldAStarRefs=1`, `PortalFlagRefs=1` | 1485 / 1447 / 2283 | 12 |
| `ip-e12-nofix-play0` | no EA crash fix (the save goes in at ready, before the §30 crash) | 1481 / 1441 / 2284 | 12 |
| `ip-e12-wa1-play10` | 10 min of play first | 1188 / 1074 / 2473 | 12 |
| `ip-e12-apexoff-play0` | `ApexRadiance.asi` moved out of `Bin` | 1540 / 1485 / 2220 | 12 |
| `ip-e12-apexlots-play0` | Apex on, `LotLodStreaming`, `LotObjectThrottle`, `LotVisibilityOverride`, `LotLightingMotion` off | 1446 / 1406 / 2316 | 12 |
| `ip-e12-apexrefpack-play0` | Apex on, `FastCacheCompression` off | 1475 / 1423 / 2291 | 12 |
| `ip-e12-plain-wa1-play0` | the non-Spring Baseline-IP | (the save step hit an agent RPC error; rerun pending) | - |

Bridgeport saves in the same hours, same asi, returned 0 with largest free 730 to 1081 MB (`bp-compaction-*`,
`bp-save-reload`, `old-deadrefs-*`). The save's own need was 55 MB (`bigNeedMB`). **Error 12 here is not address space
and not Apex [verified].**

### 31.2 What EA's save does (ts3native `save:` phases, `UserDiag`)

- `prepare` 0 ms, `info` 0 ms, `write (package)` 0 ms, then `write: world save (lots, script state)` 5.6 to 7.6 s, then
  `result 12`. The game renames the folder `<name>.sims3.bad`. No `ScriptError_*.xml` is written at any of the 8 save
  times [verified].
- **The `.bad` world file (`IslaParadiso_0x0c50c382.nhd`) is a complete DBPF 2.0: header, every resource, and the index at
  the end** (20.0 to 21.3 MB, 7378 to 7392 entries). The source save has 7816 entries in 26.2 MB [verified, 10 of 10 `.bad`
  folders].
- Missing from every one of them, against the source's index:
  - **all 15 resources of type `0x342779A7`**, the native CAS SimService state (`docs/14` §1: outfit compositor and
    material cache of every built Sim, rewritten whole on each save by `FUN_005ed3e0` / `FUN_005ef7f0` / `FUN_005efb50` /
    `FUN_005efd60`): instances `0x10001` (1.75 MB), `0x20001` (2.42 MB) and `0x30001` to `0x3000D` (13 chunks of 0.83
    to 0.89 MB), 15.4 MB uncompressed, 3.9 MB on disk.
  - **the complete resource set of 7 lots**: 119 lot records (`0x0563919E`) in the source, 112 written, and the same 7
    instance ids absent from each of the 35 per-lot types (243 entries in all). The ids: `08F30053536284C0` (17 KB
    record), `08F30053536284F0`, `08F300536B484BF0`, `08F300536F64EAC0`, `08F300546D514280`, `08F30054B95E63B0` (28-byte
    records, lots with nothing in them) and `08F300546D514270` (36 KB). They are not the last lots in the file's order
    (the first of them is lot 61 of 119 by offset, lot 119 is present), so the writer skipped them and went on rather than
    stopping [verified].
  - 6 entries are new in the failed file (`0x0580A2CC`, `0x0580A2CD`, four more `0x00B2D882`): the save's own bookkeeping.
- Every other type has the same count and within 3% the same bytes; the script state (`0x06B981ED`, 6.9 MB) is written
  in full [verified].
- The saves that work on the same world carry the same things: `BigSave-IP` (10-30, 29.7 MB, returned 0 on 10-31 with
  `WorldAStarRefs=1`, §30 run 176) and the player's own `Isla Paradiso test 1` (10-30) have the SimService state (14 entries,
  12 chunks) and 6 of the 7 lot ids; the non-Spring `Baseline-IP` (10-12) has 15 entries and all 7 ids, and its only arm
  did not reach the save [verified].

### 31.3 Reading

- Error 12 is the world save's return after one of its writers failed; the file was still finalized with an index. What
  failed is in the native part: the SimService state writer, or the lot writer for one of the 7 lots, or one failure that
  made both skip (the SimService chunks are the last things written in a good save, the 7 lots are spread through the
  middle) [inferred]. The script state and the other 7,370 resources went through.
- The 13-chunk SimService state is not by itself the limit: Baseline-BP-Spring saves with 14 chunks [verified].
- Five of the 7 lots are 28-byte records, lots with no content; in Isla Paradiso those are the kind the houseboat ports
  and the empty beach lots leave. Whether the 7 are the port lots and what the 17 KB and 36 KB ones are is not known yet;
  the agent has no lot-by-id command.

### 31.4 Next

1. **ts3native, one IP arm:** log the return of the four SimService functions and of the lot writer per lot id during a
   save (`SaveWrite` already wraps the save; the functions are in `game-copy/analysis/functions.csv`). That names the failing
   writer and, if it is a lot, which one. About 1 h with the build, one live job.
2. **Name the 7 lots:** a `lots` agent command (id, name, lot type, household, houseboat / port flags) run once on
   Baseline-IP-Spring, then the same on `BigSave-IP` for the 6 shared ids.
3. **The non-Spring Baseline-IP arm** again (`ip-e12-plain-wa1-play0` with its save step after the agent is idle): it has the
   same 15 SimService entries and all 7 lots, so it separates the Spring conversion from the save's own state.
4. If the SimService writer is the one failing: compare its 15 inputs with BigSave-IP's 14 (the `0x3000D` chunk is the
   extra one) before anything else; a `SaveCleanup`-style trim is not possible for it (`docs/14` §1).

### 31.5 Four more arms: the save's own state, not our builds, not the caches, not the Spring conversion (2026-11-02 night)

Live, on `fix/compaction-save-result` (the compaction verify worktree), one job per arm, save at once after the load.
Each failed file was read with the same index diff as §31.2 [verified for every row]:

| Arm | Save | Setup | Result | Missing from the written world file |
|---|---|---|---|---|
| plain (§31.4 item 3) | `Baseline-IP` | 2026-11-02 asi (WorldAStarRefs on), the branch's ts3fixes | 12 | all 15 SimService entries, the same 7 lots |
| release pair | `Baseline-IP` | the player's own pair: ts3native v0.2.4 (0.36.105) and their ts3fixes | 12, then the §30 A\* crash at `0x6742B5` 15 s later (v0.2.4 has no WorldAStarRefs) | the same |
| fresh world caches | `Baseline-IP` | as plain, with `IslaParadiso_sims.package` (1,071,414,891 B, at the 1 GiB cap) and `_objects` moved aside | 12 | the same |
| control | `BigSave-IP` | as plain | **0**, and the in-session reload save 0 | nothing |

- **Not the Spring conversion, not our builds, not the world caches.** The non-Spring save fails the same way, and so
  does the player's release setup. Fresh caches do not change it. (The original caches went back after the arm.)
- **The world itself saves.** `BigSave-IP` (the same world, the same mods, the same asi) saves today. So it is
  `Baseline-IP`'s own state, which `Baseline-IP-Spring` inherits.
- **The 7 lots, named** (`list_lots` on `Baseline-IP-Spring`): 33 Surf Circle, Shimmering Sands, Ocean View (Isla
  Romantica) and 2 Sharks Bay (4 empty residential lots), Island Jewel Park (`kSmallPark`), Ichtaca Community Garden
  (`kMisc_NoVisitors`) and Sparkling Sands Beach (`kBeach`). None is a port or a houseboat lot (the world has 18
  `kEP10_Port` lots).
- **Their records are not the cause by themselves.** Each of the 5 small records is byte-identical to the other 29
  empty-lot records of the file (`05000000 10000000 ... 02000000 04000000`). The two larger ones are the same size in
  `BigSave-IP`, which writes them.
- **One lot-level difference:** `Baseline-IP` has 119 lot records, `BigSave-IP` 118. The extra one is 33 Surf Circle
  (`08F30053536284F0`), one of the seven. Whether it starts the failure or is only skipped with the others is not known.
- **An attempt without the player's mods was inconclusive.** All of `Mods/Packages` and `Mods/Overrides` were moved
  aside except the harness's three packages; they were moved back after the job and counted. The world loaded in 9 s
  but never reached live mode within 60 s, so there was no save. ts3fixes still listed 16 script assemblies, NRaas ones
  among them, with 3 packages installed; where they loaded from is open.

Next, in this order:

1. Find why `Baseline-IP` has 33 Surf Circle and `BigSave-IP` does not (how each save was made). The game cannot
   take the lot out to test it: Edit Town bulldozes a lot's contents but does not delete the plot, and this one is
   already empty.
2. Native writer logging (§31.4 item 1): which writer returns the failure, the lot writer or the SimService writer.
3. The no-mods arm again, once the 16 assemblies' source is known.

### 31.6 The extra lot was made in play, and the save wrote fine before 2026-10-18 (2026-11-03)

(Correction, §31.8: the extra lot is a world-builder lot, `43_vacant`, from the version of the world this save was
started on; it was not made in play.)

- **33 Surf Circle is not in the shipped world.** `IslaParadiso.world` has no resource under `0x08F30053536284F0`.
  The five other empty lots checked have 68 to 76 each. The player's working `Isla Paradiso test 1` has no such lot, and
  `BigSave-IP` has none either. In the game (`LotManager.GetLot` and `lot_layout` on `Baseline-IP-Spring`), it is a
  residential plot with no name, aligned with the street like its neighbours. It is not a houseboat lot
  (`mbIsEP10HouseboatLot` false), and it is empty except for the mailbox and trash can the game puts on a new lot.
  So it was made while `Baseline-IP` was played. The Edit Town lot tool is the likely way; the agent has no command
  that makes a lot.
- **The lot alone does not cause Error 12.** `Baseline-IP` was written on 2026-10-12 with the lot. `Baseline-IP-Spring`
  was loaded from it and written twice on 2026-10-17 (13:17 and 19:15). All of those saves returned 0 [verified: file
  times; the lot is in both, by the index diff and by `list_lots`]. The same saves fail today on every setup tried. So Error 12 needs this
  save's state plus something on this machine that changed after 2026-10-17.
- **What changed since then, outside the save** (2026-10-18 and 10-19): the core mod in `Mods/Packages`; the 21
  `Default.ini` files (the four CAS compositor cache sizes raised to 1 GiB); the nine stuff-pack FullBuilds (each merged
  with its delta, SP1 to SP3 also without 396 empty entries) and the nine stuff-pack deltas retired; and a 2-byte
  `TS3W.exe` patch (the interpreter stack chunk's GC descriptor moved from `+0x7FC` to `+0xFF8`). The originals of all of
  them are kept. §31.7 undoes each one.
- **The arms that would test it do not run.** Three arms stop at the `cycle.ts` world-state wait, so the save step
  never runs: the player's mods moved aside (twice), and only the core mod moved aside. In each, `game_state` never
  reports a `...State` sub-state within 60 s. Also, with `Mods` moved aside, ts3fixes still listed NRaas and other script
  assemblies (16) while ts3native counted 3 packages. Where those load from is not known. (§31.7: why, and those arms run now.)

Next: §31.7.

### 31.7 None of the post-10-17 changes is the cause (2026-11-03)

One job per arm, `Baseline-IP`, `save normal` at once after the load, the plain job of §31.5 (the player's mods in)
unless noted. Every file an arm changed went back after it and was hash-checked [verified for each arm]:

| Arm | Put back or moved aside | Native save | Result |
|---|---|---:|---|
| core out | the core mod and Tiny UI Fix moved aside: EA's own gameplay DLLs | 5,500 ms | 12 |
| no mods | all 100 entries of `Mods/Packages` and `Mods/Overrides`, and Tiny UI Fix, moved aside (left: ts3agent, ts3fixes, ts3-nointro) | 4,984 ms | 12 |
| bare native | every ts3native section that is on in the release ini switched off (`GC`, `RouteAsync`, `RouteGate`, `Decompress`, `WaterJps`, `Discourage`, `LotRemoveGuard`, `OptsInit`, `UnseenFallback`), `SaveWrite` at Mode 0 (time only); `UserDiag`, `MemDiag` and `WorldAStarRefs` kept | 5,329 ms | 12 |
| old `Default.ini` | the 21 files from before 2026-10-19 (EA's cache sizes) | 5,172 ms | 12 |
| old stuff packs | the nine FullBuilds from before the merge and the nine deltas, copied back | 5,312 ms | 12 |
| original exe | `TS3W.exe` without the chunk patch | 5,032 ms | 12 |

- **No change found outside the save is the cause.** Each one undone on its own still gives 12, with the same save time
  as §31.1. So the §31.6 reading ("this save's state plus something that changed after 10-17") does not hold for any
  change found. Not tested: the DCCache, the thumbnail caches, the GPU driver and Windows updates.
- **Why the earlier arms stopped at the world-state wait** (§31.6): the stash kept the Tiny UI Fix folder in place. Its
  core bridge is built against the core mod; without the core, the world never reports a sub-state. With Tiny UI Fix moved
  aside too, the world loads.
- **The 16 assemblies** of the earlier no-mods arms came from what those stashes left in `Mods`. With everything moved
  aside, ts3fixes lists 3 (ts3agent, its plugin and ts3fixes) and ts3native counts 3 packages.
- The first no-mods try did not reach a result: after the save request ApexRadiance faulted on the main thread, and no
  save phase ran in 900 s (the known Apex save hang). The rerun went through.

Next: native writer logging (§31.4 item 1) is now the main lead: which writer returns 12, the SimService state writer or
the lot writer for one of the 7 lots.

### 31.8 Cause: two lots whose routing build never finishes; fix `[EaCrashFixes] LotGridMiss` (2026-11-03)

Frida scripts attached to `Baseline-IP` (bare native, as §31.7), one step per run. The writers were followed from
`World_Save` 0xC6D460 down to the failing one, then the failing branch, then why it fails.

- **Where the save fails [verified].**
  - `World_SaveLots` 0xC62860 saves each lot entry through the entry's `+0x1C` call.
  - In two entries, the save context's ok flag (`ctx+0x14`) drops to 0 right after `BeginResource 0x046A7235:<lot>`
    (the lot's routing levels). The lots are `08F30053536284C0` and `08F30053536284F0`.
  - The flag is cleared in `LotRouting_SaveLevels` 0x63C690, through 0x63C620 and then 0x7D77C0 (`flag &= arg`).
  - From then on that entry's `BeginResource` 0x7D8290 calls return false, so the rest of the entry is not written:
    the 7 lots of §31.2. `World_Save` returns false (Error 12), and the SimService state of §31.2 is missing too.
- **Why 0x63C690 fails [verified].**
  - The lot's routing record has a "build in progress" byte at `+0x8E`. The level build 0x63CB90 sets it (0x63CC17),
    and 0x631A60 reads it.
  - For both lots the byte is set at save time.
  - 0x63C690 then forces a rebuild (0x82B810), which returns true. The byte is still set (0x63C781), so the function
    takes its failure branch at 0x63C8C1.
- **Why the byte stays set [verified].**
  - Its only clear is 0x631A30, called at 0x82B802 at the end of the lot routing finalize 0x82B590 (one argument, the
    lot).
  - The finalize leaves early, without the clear, at 0x82B608 when both of these hold:
    - `lot+0x4BC == 0`;
    - 0x651DA0 finds no world grid point within 1 m of the lot's centre. The grid has 32 m cells, with points stored
      at 1/8 m.
  - For both lots, every call: `+0x4BC` is 0 and the lookup fails. So the lot is never marked built.
  - The finalize then runs again about once a second: 26 calls in the 45 s before the save, and 16 during the 5 s
    save.
  - Every other lot finalized in the run cleared the byte: 8 found their grid point, 5 had `+0x4BC` 1.
- **The two lots.**
  - `08F30053536284F0` is 33 Surf Circle. It is not in the installed world file (a world-builder lot from another version
    of the world, see below; not made in play as §31.6 inferred).
  - `08F30053536284C0` is a world lot: 71 resources in `IslaParadiso.world`, corners (856.4, 832.8) / (910.3, 784.3).
    There is still no grid point at its centre.
  - The player's `IslaParadiso.world` is a community-fixed version, decompressed (2026-03-14, 295,686,993 bytes, no
    compressed entries). It was in place for the saves that wrote fine on 10-12 and 10-17. Whether EA's original
    world has a grid point there was not tested: the player keeps this world.
- **Why the two lots miss their grid points: the save's layout is from another version of the world [verified, files].**
  - `08F30053536284C0` is Sparkling Sands Beach (world-builder name `78_BeachBigIsland`, `kBeach`).
  - `Baseline-IP` and `Baseline-IP-Spring` put it at (856.42, 20.38, 832.8) in its lot record (`0xD063545B`).
  - The installed world file, and every other IP save on this machine, put it at (861.08, 21.06, 820.44), with the same
    rotation (1.22). The saves checked: `1.sims3` from 10-04, `Isla Paradiso2`, `Isla Paradiso`, both test saves,
    `BigSave-IP` and the compaction saves.
  - `08F30053536284F0` ("33 Surf Circle") is not a lot made in play, as §31.6 inferred. Its record carries the
    world-builder name `43_vacant` (`EP10_World/Empty/LotAddress:Lot17`). Only the Baseline-IP saves have it; the
    installed world file does not.
  - So Baseline-IP was started from a version of `IslaParadiso.world` that has `43_vacant` and the beach at the other
    spot. The installed file has been in place since 2026-03-14 (the folder's write time), so that start was earlier.
    The installed one is a community-fixed version; which version Baseline-IP started from is not known.
  - The world graph's lot node for the beach (chunk 1563, waypoint 0, type 0x1) is at (887.875, 796.125). That fits the
    installed layout, not the save's. The save has no waypoint graph of its own (no `0x05CD4BB3` in the `.nhd`).
  - The shipped waypoint counts of chunks 1563 and 1626 are 18 and 6: the counts that §30's stale lot portal ids,
    (1563, 18-23) and (1626, 6-7), point past. So the §30 crash and this failure have one cause.
  - Not a houseboat move (the player asked). EA's scripts parent only houseboat lots to a boat
    (`Houseboat.DEBUG_SetHouseboatLot`, `Houseboat.PostLoadWorldBuilderDataFixup`, `Lot.PostWorldLoad`). In the save,
    all 10 houseboats are docked at ports.
- **The place-lots prompt [verified].** Every Baseline-IP load shows EA's "new lots available" prompt
  (`InWorldSubState.PlaceLotWizardCheck`) for the four Ambitions venues. Their lots (`Lot01` to `Lot04`) are empty plots
  here, while `1.sims3` has the venues placed. The agent's dialog rule accepts it ("Accept" scores as accept), which
  opens Edit Town's place-lots wizard. In `1.sims3` the prompt does not appear.
- **What builds these lots at load [verified, Frida, 2026-11-03].** The routing update builds their levels on the main
  thread about 137 s after start: `0x836172` -> `0x63D86A` -> `0x63D7B0`, then the finalize from `0x83617A`. Four lots
  get levels built at load; these two are among them. The rebuild `0x82B810` runs only at save, for the 7 lots.
- **Not explained.** Why the 10-12 and 10-17 saves wrote fine with the same layout and the same world file.
  - The 10-17 machine had neither ts3native nor Sims3Performance.
  - Without both on this machine (a do-nothing DLL in place of ts3native, Sims3Performance moved aside), the two lots are
    still built at load the same way. That run then crashed at `0x67664F` (§30's A\* crash, no fix loaded) before its
    save.
- **Live fix test (Frida, in the game's memory only).**
  - The patch: the `je` at 0x82B608 jumps to 0x82B7F4. That is the game's own exit for a lot with nothing to connect
    (0x82B659 `jbe`), which clears the byte and returns.
  - Each lot was finalized once, with the byte cleared, and the once-a-second rerun stopped.
  - `World_Save` returned true, and the log read `save: end #1 result 0`: the first good IP save since 10-17.
  - The written world file has all 7 lots, with 33 to 35 resource types each: 119 lot records against 112 in the failed
    save. The SimService state is back too: 13 entries of `0x342779A7` against 0.
  - The `.nhd` is 24.06 MB against 19.96 MB. `Meta.data` and `1a5tf1l3.dat` were written too; the failed save leaves
    them at their 10-12 versions.

**Fix: `[EaCrashFixes] LotGridMiss`** (`wastar.cpp`, next version, off by default).
- The `je` at 0x82B608 keeps its condition, but its target becomes `LgStub`.
- `LgStub` logs the lot ("ea fix: LotGridMiss: lot 0x...", first 8), then goes on at 0x82B7F4.
- The connection pass is skipped as before. Only the byte now gets cleared.
- Install checks: the 6 bytes at 0x82B608 (`0F 84 F9 01 00 00`) and at 0x82B7F4 (`8B 4B 4C 8B 53 48`) are the game's.
- Selftest (`TestWorldAStarRefs`): a mismatched byte refuses the patch; the patch keeps the opcode and points at
  `LgStub`; a second patch is refused; each hit counts.

Result: §31.9.

### 31.9 Result: `LotGridMiss` on saves Isla Paradiso through save, quit, reload and save (2026-11-03)

Local build `07b9659` (this branch), `Baseline-IP`, release defaults plus `[UserDiag]`, `[MemDiag]` and
`WorldAStarRefs=1` as §31.7. `save-reload.ts --world IP --play 0 --after 2 --no-visits`: a normal save, then a save,
Quit to Main Menu and a reload of that file, 2 min of play, then a save, EA's in-game Load (direct route) and 2 min of
play. Local queue (the save is the player's).

| Arm | Saves (native result) | `LotGridMiss` lines | Job |
|---|---|---|---|
| `LotGridMiss=0` | normal: 12 (5,281 ms); the tool stops there | none | FAIL as expected, 3.4 min |
| `LotGridMiss=1` | normal: 0 (5,968 ms), reload save: 0 (5,078 ms), after the menu reload: 0 (6,281 ms) | `OK` at start; both lots after the first load, again before the first save, after the menu reload, after the direct reload (8 lines, the cap) | PASS, 8.7 min |

- The menu reload and the direct reload both loaded the file that the fixed save wrote, and played 2 min each.
- **Soak** (same build, `--play 15 --after 3` with the Buy and CAS visits, 25.7 min, PASS): after 15 min of autopilot
  play, the three saves gave 0, 0, 0 (6,422 / 5,610 / 6,672 ms). Both reloads played 3 min. 8 hit lines, the cap. The
  main-thread access violations at `0x69F235` in that log are also in the IP runs from before the fix (11-02
  `ip-e12-wa1-play10`, 11-03 `ip-lot33`), so they do not come from it.
- An earlier Frida run (the same patch in memory, then save, Quit to Main Menu, reload, 2 min of play, save) also gave
  `result 0` twice.
- With the fix, the two lots are rebuilt now and then in play, and each rebuild is marked done at once. Without it the
  finalize ran about once a second for good.
- The first hit lines come right after the load, so the load starts these lots' rebuild. Why the
  10-12 and 10-17 saves wrote fine is still not known.
