# 191. Memory leak detection plan

Status: plan (2026-10-25); step 1 done (docs/192, `tools/script-leak-scan.ts`, `docs/data/script-leak-candidates.csv`); step 2 done (docs/193, `docs/data/native-leak-candidates.csv`); module 1 (`diag: mem` + validation) built in ts3native 0.36.69, module 2 (forced-GC probe + suspect counters) in 0.36.74, neither run in game (sections below). Companion to docs/190 (memory metrics spec) and docs/66 (address-space budget).

## Why

TS3W.exe is 32-bit with a 4 GB cap. Players report that long sessions end in Error 12 or a crash on save, and the common workaround is restarting the game every few hours. That pattern points to memory that grows and never comes back. docs/190 found that the release build keeps no per-owner byte counts (EA's allocation tags are dropped, the memory tracker only runs in debug builds) and about 1 GB of private memory has no known owner. Every leak number has to come from our own reads, walks, dumps or hooks.

## What a leak is here

- **Native true leak:** a heap block nothing points to any more and nobody frees.
- **Managed leak:** an object the Mono GC cannot free because something still references it (a static collection, an event listener), although the game never uses it again.
- **Unbounded cache:** entries that are still referenced but never evicted. Not a leak in the strict sense, but freeable memory.
- **World-switch leftovers:** memory that should be released on travel or world change and is not.

## Steps

| # | Step | Kind | What it finds | Tooling | Effort |
|---|---|---|---|---|---|
| 1 | Static script-leak scan | static | static collections with adds but no removes; event subscriptions with no unsubscribe; caches with no eviction, in EA script DLLs and every installed mod | `tools/managed-callgraph.ts` (static-write and event tags, docs/78), `tools/offload-override-scan.ts` style package reading | ~3-5 h |
| 2 | Static native cache scan | static | containers with insert callers but no erase or evict callers; AddRef paths without Release on error paths; for the docs/190 caches first | Ghidra headless (XrefsTo, StoreScan, CallerTree) | ~4-8 h, more false positives |
| 3 | Growth slopes | runtime, passive | which bucket grows at steady play | address-space lines (master 0.36.66) + docs/190 phase 1 `diag: mem` | built / ~2-4 h |
| 4 | World-switch snapshots | runtime, passive | memory left after travel or a world change | `diag: mem` at world unload and after the next load | ~1-2 h |
| 5 | Offline unreachable-block scan | runtime dump + offline | native true leaks: heap blocks with no inbound pointer, grouped by allocation site | process dump from a correctness or soak run (world-image dump, docs/123), conservative pointer scan offline; allocation-site tags from an opt-in allocator hook (docs/190 phase 3) | ~8-14 h + 1-2 runs |
| 6 | Mono class census + root paths | runtime, soak | managed classes whose live count only rises, and the root (static field, event list) that holds them | managed sampler + Mono runtime map (docs/57, docs/130), Boehm heap walk | ~6-10 h + 1-2 runs |
| 7 | Cache entry ages | runtime | cache entries untouched for hours but never evicted | docs/190 phase 2 counters | with docs/190 phase 2 |
| 8 | Soak queue | runtime | the above over 2-4 h of unattended play | existing soak jobs + memory lines | ~1 h to set up |

Order: 1 (cheap, covers mods), 3 and 4 with the docs/190 phase 1 build, then 5 and 6 in soak runs, 2 and 7 as follow-ups.

## Goal: a correct memory measuring tool first (Rodney, 2026-10-25)

The steps above are built as one reusable tool, not as one-off probes, so later leaks are found and traced to a cause quickly. Requirements:

- **Correct before useful.** Every number the tool reports is validated against a known answer before it is trusted: synthetic allocations and synthetic leaks injected in self-tests and in a test build (known size, known site, known root), plus a cross-check of the totals against the OS view (VirtualQuery / private bytes) within a stated tolerance. A reading that cannot be validated is marked unvalidated in the output.
- **Attribution to a cause, not only a bucket.** For native memory: allocation site (caller chain) and owner cache; for managed memory: class and the root path (static field, event list, cache) that keeps it alive. A finding names the code to look at.
- **Growth over time and around events.** Per owner and per site: at load, every 10 minutes, and before/after marked events (travel, world switch, catalogue open/close, Build mode, save), so a leak shows as growth tied to an action.
- **One pipeline for all inputs.** Player logs (`[UserDiag]` lines, numbers only), our soak runs (full detail), and offline dumps (unreachable-block scan) feed the same report tool and database, the way the call-site database does (`tools/callsite-db.ts`).
- **Cheap enough to leave on where it is safe.** Passive levels stay on in correctness and soak runs (cost counters, light-tool budget); heavy levels (allocator hooks, heap walks, dumps) are opt-in per job; player builds get only the counts.
- **Repro-driven.** Each suspected leak gets a repro action in the agent and an entry in `tools/harness/repros.json`, and a fix is proven by the tool showing the growth gone in the same repro.

Build order stays as above; each step adds a validated module to this tool.

### Module 1: `diag: mem` + validation (built ts3native 0.36.69, 2026-10-25, not run in game)

docs/190 phase 1, the subset with verified addresses. Code: `native/ts3native/memdiag.cpp` (rides on `[UserDiag]`'s memory worker and its one VirtualQuery walk in `userdiag.cpp`), selftest `memdiag-test.h`, report `tools/mem-report.ts`, agent `mem_mark`, repro `memdiag-build-save`, queue `tools/harness/queues/mem-diag-verify.json`.

- **Switch:** `[MemDiag] Enabled=0` (default off until the verify queue passes; needs `[UserDiag] Enabled=1`), `SelfTest=0` (correctness runs only). Numbers only, so a player build can turn it on as is.
- **Line:** `diag: mem #N <tag> [ev=<event>] key=value... t=<s>` after every `memory at` line. Tags: `load`, `save-start`, `save-end`, `periodic` (10 min), `exit`, `mark`, `selftest`. Events: `travel-save` / `traveling` at a save start, `travel`, `world-switch` at a load (world name from the ts3fixes `world:` line), any tag sent by `mem_mark` (Build mode in/out in the repro). One value per key (docs/190's compound `rs=`/`gc=`/`rc=` forms split, so each key carries its own validation mark).
- **Fields:** OS `priv` (PrivateUsage); walk `wPriv` (MEM_PRIVATE committed), `wCom`, `free`, `big`, `img`, `map`; `stkRes`/`stkCom` (allocations with a PAGE_GUARD region) and `miRes`/`miCom` (64 / 128 MiB private reserves, S3SS mimalloc arenas by size only [inferred owner]); EA heap direct blocks `eaDir`/`eaDirN` (`[0x11CB864]+0x48C/+0x488`); routesnap `rp`/`pl`/`sp`; Boehm `gcHeap`/`gcFree`/`gcN`; ResourceCache `rc`/`rcCold`/`rcBudget`/`rcN` and AnimationCache `anim`; `rest` = priv minus the readable owners (includes EA heap cores, so the ResourceCache sits in it).
- **EA heap cores (0.36.135, docs/209 #96):** `eaCom` / `eaRes` / `eaCoreN` = committed MB, reserved MB and count of the GeneralAllocator cores of MemoryMan `[0x11CB864]` and the script heap `[0x11D4BC8]` (counted once when the same object): ring through core `+0x18` headed at heap `+0x448` (first core `[heap+0x460]`), core `+4` committed, `+8` reserved [verified exe bytes, `AddCore 0x4E4E30`]; read without the heap lock, unvalidated (`?`); `eaCom` is subtracted in `rest`. Host selftest checks the walk on a fake two-core ring. **`rc` is not address space under DXVK** (docs/66 §3 caveat, docs/209 #96): `tools/memory-report.ts` prints that note under the table.
- **Validation marks:** no suffix = a self-test check passed, `?` = none yet, `!` = failed this session's self-test; a legend line lists both sets. Host selftest (every build, `TestMemDiag`): the walk classifier on synthetic regions, the game-structure reads on fakes, the line format, then the self-test on its own process. Game self-test (`SelfTest=1`, 30 s after the first load, memory worker): 48 MB commit (priv, wPriv, wCom, free; mi/stk stay 0), 64 MiB reserve + 16 MB commit (miRes, miCom), 4 MB thread stack (stkRes), 32 MB file mapping (map: since 0.36.75 the walk must count all 32 MB of our own view as MEM_MAPPED while mapped and 0 after the unmap; the whole-process map total moves with game streaming (run 2026-10-25 14:01: +36.12 / back -30.62, FAIL) and only triggers a retry, logged as "other mappings"), 4 MB routesnap heap (rp), 8 MB EA direct block via `Malloc 0x4E69D0` flag 8 (eaDir, eaDirN, priv); up to 3 tries per step, up and back down; then a **synthetic leak** of 8 x 1 MB EA direct blocks kept to exit, bracketed by `selftest-leak-before/after` samples; then the OS cross-check (|priv - wPriv| <= max(64 MB, 10%), rest >= -16 MB). Tolerances: 1 MB host, 3 MB game for OS/walk sums, 0.5 MB classifiers, 0.1 MB EA counter.
- **Unvalidated (`?`) by design in module 1:** `big`, `img`, `stkCom`, `pl`, `sp`, Mono `gc*` (no safe managed injection from native; check = the GC.Collect probe of step 6), `rc*`/`anim` (read only), `rest`. `eaDir`/`eaDirN` count as validated only in a session whose self-test passed until the verify queue passes (`g_mdGameVerified`).
- **Not built:** docs/190 `stbl`, `casp`, `comp`, `idx`, `shd`, `cat`, `async`, `xfer`, `aud`, `pre`, `dcc` (RE Q1-Q6 first), `st` (verify.cpp: `[0x11D8628]` is not a live-bytes total, against docs/190's row 27), `ea` top and free-in-bins (cores built in 0.36.135), `ours`. Markers for natural Build mode and catalogue open/close: TODO (needs a ts3fixes state hook; today only the agent's `mem_mark`).
- **Report:** `node tools/mem-report.ts merge <ts3native.log> [--job id]` into `probe-out/memdiag/db.json` (runs keyed by the log's first line, samples by number: merging twice changes nothing); `node tools/mem-report.ts report [--run id] [--log file]` prints growth per field (first, last, min, max, MB/h) and the change at each event sample against the sample before.
- **Verify (not run):** `node tools/harness/queue.ts tools/harness/queues/mem-diag-verify.json` with `game-copy/ts3native.asi.memdiag2` (0.36.75; job 2 still `ts3native.asi.memdiag`, 0.36.69) and the main checkout's ts3agent rebuilt (`mem_mark`). Job 1 BP: self-test PASS incl. the EA step, no `!`, lines at load / leak / build marks / save; job 2: travel round trip to France, `travel-save` and two `world-switch` loads.

### Module 2: forced-GC probe + suspect counters (built ts3native 0.36.74, 2026-10-25, not run in game)

docs/191 step 6 start and the docs/192 / docs/193 §4 reads. Code: `native/ts3native/memdiag.cpp` (relay + wanted slot), `mods/ts3fixes/src/MemSuspects.cs` (counts + self-test), `mods/ts3agent/src/plugin/Handlers/MemHandlers.cs` (`mem_gc`, `catalog_loop`, `buy_sell_loop`), report `tools/mem-report.ts`, repros `leak-catalogue-loop`, `leak-overwatch-errortrap-reload`, `leak-place-object-loop`, queue `tools/harness/queues/mem-suspects-verify.json` (asi `game-copy/ts3native.asi.memsus`).

- **Marks are sampled inline** (on the marking thread) since 0.36.74, so a mark's line exists before the agent's next step: `gc-before` is walked before the collection. Worker samples (load, save, 10 min) are unchanged.
- **Forced GC (`mem_gc {rounds: 2}`):** mark `gc-before`, then per round `GC.Collect()` + 2 sim ticks, mark `gc-after`. **No `GC.WaitForPendingFinalizers()`:** in TS3W.exe it returns at once and runs nothing (icall `0xE78F80` waits on `pending_done_event [0x11F4308]`, which no code writes, and `gc_thread [0x11F4310]` is never set) [verified static, ledger §351]; finalizers run only from `MonoScriptHost_Simulate` (`mono_gc_invoke_finalizers 0xE697F0`, docs/130), so each round sleeps two ticks for that drain and the next collection frees what the finalizers released. The report's forced-GC section shows gc-after minus gc-before: a drop = collectable but uncollected (docs/193 L2/L3), a suspect count that stays = rooted (L1).
- **Suspect counters (`diag: memsus #N <tag> [ev=] k=v ... lag=<ms> t=`):** after every `diag: mem` line native leaves a wanted slot (sample number, tag, event); ts3fixes polls it every 10 ticks over the `[UserDiag]` text channel (ops 3 on / 4 wanted / 5 self-test wanted; one GetOption2 call per poll, task only when `[MemDiag]` is on) and answers on the sim thread; ts3agent `mem_mark` / `mem_gc` call `Fixes.MemSusPoll` right after a mark. N = the `diag: mem` sample answered, so `mem-report.ts` merges the counts into that sample. Fields: `cc` / `ccIcon` (`UIManager.mCustomControlInstanceDict` count / `CustomContentIcon` values, L1), `ppc` (`Route.PostPlanCallback`, #4), `etRef` (ErrorTrap `ScriptCore.ExceptionTrap.OnLoadReference`, #5, `-` without ErrorTrap), `gsc` (`Responder.Instance` `GameStateChanging`, #1), `mapv` (`CameraController.OnCameraMapViewEnabledCallback`, #1), `wq` / `wpl` (`World` world-quit / object-placed-in-lot, #2), `ogs` (`LoadSaveManager` `ObjectGroupSaving`, #2), `smh` (`GameEntryMovingModel.SplitMergeHouseholdsComplete`, #3), `gcCnt` (`GC.CollectionCount(0)`), `rdMs` (read cost).
- **Validation:** `[MemDiag] SelfTest=1` also asks ts3fixes once per session: each counter must read +1 after one known entry is added through the same field (a delegate list gets its own first entry appended, then the original delegate object is put back; the dictionary gets one entry under an unused key, `ccIcon` an existing icon under a second key, then removed) and the start value after; synchronous on the sim thread, nothing else sees the entry. A counter empty or unreadable at that time (no catalogue opened yet, no ErrorTrap) stays `?` and is retried at each poll. `gcN` (native, `0x11F2F90`) is cross-checked against managed `gcCnt` over each gc pair in the report (`ok` = the native address reads the same counter). Host selftest: wanted slot, relay line, ops, channel routing; `mem-report.ts --self-check`: merge by number, forced-GC deltas, gcN check.
- **Unvalidated by design:** that `cc` growth is catalogue icons (only `ccIcon` and the repro say so); `gsc` / `mapv` growth per placement (#1 needs an inventory-placement action, not built: `leak-place-object-loop` drives #2 only); Mono class counts (docs/191 step 6 census, not built).
- **Verify (not run):** `node tools/harness/queue.ts tools/harness/queues/mem-suspects-verify.json` after `node tools/setup/build-fixes.ts` and `node tools/harness/build-agent.ts` from this branch. Jobs: catalogue loop (self-tests, 3 gc pairs, 5 rounds), 3 loads in one session (etRef / ppc per load), 20 buy + sell rounds of a store object.

### CC soak (queue `tools/harness/queues/cc-soak.json`, built 2026-10-26, not run)

- **Sets:** `tools/cc-gen.ts` output under `game-copy/cc-synth/<set>` (smoke-12, cc-1k, cc-2k; 5k only on request, ~8.5 GB disk). Job field `ccSet` in `tools/harness/queue.ts` moves the set's packages into `Mods/Packages/cc-synth` before the load and back after the job (same volume: renames, never a second copy), through the content stash's manifest-first `stashContent`/`restoreContent`; an interrupted run is undone by `queue.ts --restore-content`, and the queue refuses to start while a manifest is left.
- **Origin:** every arm runs content `no-cc`, so the agent's origin `cc` (DownloadContent category, Mods content reports `kLocalUserCreated` [verified 2026-10-15 for Store repacks]) should be the synthetic set only [inferred, checked by the smoke job's `cas_parts` count].
- **Load check first:** `bp-cc-load-smoke` (repro `cc-load-smoke`): >= 6 `CCGen` products in the catalogue, >= 6 cc CAS parts, one object bought through Buy mode, CAS browsed, one household Sim wearing a synthetic part (live mode `wear_parts`: the agent cannot commit a CAS edit), no crash, no new `ScriptError_*.xml` (job field `scriptErrorsMax`, known EA errors excluded).
- **Soak:** repro `cc-soak-play`, 8 phases of ~15 min: Buy browse + 3 synthetic objects placed by catalogue name, CAS browse, household in synthetic Hair/UpperBody/FullBody parts, a labelled stress step adding 20 synthetic objects to the active Sim's inventory (agent, not a player flow), save to `CC-Soak-Scratch`, `mem_gc` after phases 4 and 8, `mem_mark` per phase step (`ccsoak-pN-buy|cas|inv`). The 0 arm runs the two leak repros only.

### Player memory lines (built ts3native 0.36.79 + ts3fixes, 2026-10-26, not run in game)

Always-on numbers for player logs and the CC soak (docs/188 §7 has the player-facing view). Code: `memdiag.cpp` `ApplyMemDiag`, `mods/ts3fixes/src/MemSuspects.cs` (alive check, type census), report `tools/memory-report.ts` (with `--self-check`), queue `tools/harness/queues/cc-soak.json` (declared smoke `bp-memlite-check`).

- **Switch:** `[MemDiag] Light=1` (default on, also in the shipped ini) turns on the module 1/2 lines without any self-test; `Enabled=1` keeps meaning the full tool (`SelfTest` only there). The memory worker samples every 5 min of play instead of 10 when either is on.
- **When:** world load (travel arrival = `ev=travel`, world change = `ev=world-switch`), save start / end, every 5 min of play, exit; `diag: memsus` answers each, then one `diag: memsus alive` line; `diag: memsus types` once per load.
- **Destroyed but alive:** a swap of `ScriptObject.Destroy` (the base every script destroy reaches: `World.RemoveObjectFromObjectManager` + `Simulator.DestroyObject`, docs/74 §12, docs/84) counts destroys and puts every 32nd destroyed object into a ring of 512 reused `WeakReference` slots (one Mono GC handle + one Boehm disappearing link each, docs/57; no new finalizable object per sample). At each answer, a slot whose object survived 2+ collections since its destroy counts as alive; counts by type, EA core classes by name, other assemblies as `<assembly>/<class>`. Ring overwrite: the oldest slot goes after 512 x 32 destroys (a world shutdown fills the ring at once). Signal = alive growing over samples, not the absolute value (the conservative stack scan can hold a few). Objects whose `Destroy` override skips the base are not seen. Ledger §354.
- **Cost:** walk ~27-30 ms on the memory worker per sample (0.36.74 runs, ~4,500 regions; now every 5 min), memsus read 0.23-0.45 ms on the sim thread (same runs) [verified, logs]; the alive read (512 weak targets + a type count) and the once-per-load `GetObjects<GameObject>` census print their own `ms=`, first numbers from `bp-memlite-check` [inferred: alive < 1 ms, census ~5-20 ms once per load]. Destroy hook: a bool and an increment per destroy, a weak target set every 32nd.
- **Not built (design note):** native per-call-site allocation tracking behind an ini switch (off): an `[Alloc]`-style detour of EA `Malloc 0x4E69D0` / `Free` recording the caller return address per block in a side table (sampled 1 in N blocks, by size class), dumped as top sites by live bytes at each `diag: mem` sample; its cost and the S3SS mimalloc / Apex allocator overlap (docs/187) decide whether it stays a correctness-run tool. Also not built: a Mono class census beyond `GameObject` types (step 6).

### ResourceCache census (built 2026-11-05, run on the old save the same day)

In a 60-min old-save run `rcN` grew 100k -> 348k entries while `rc` stayed ~500-600 MB
(docs/data/results/2026-11-05-alexvixell35-stack-memory-fixes-long-runs.md). Every erase path ends in the node erase
`0x732130`, so the entries that stay are referenced or never made cold. `[MemDiag] RcCensus=1` (off) answers which.

- **What:** at each `diag: mem` line except exit, the memory worker takes the cache's own lock (`CRITICAL_SECTION` at
  cache `+0x58`), walks the map and logs one total line and the top 10 types by entry count plus up to 5 more by growth:
  `n`, `MB` (the resources' size `+0x20`), `cold`, `ref1` / `ref2` / `ref3+` (refs = `[+4] >> 8`; the cache holds one),
  `ref1held` (only the cache holds it but it is not cold, so a trim never drops it), each with the change since the last census.
- **Layout [verified disassembly]:** Add `0x732A00`, find `0xAD3F40` (hash = key dword 3 xor dword 0), AddRef `0x731DB0`,
  cold test `0x731E30`, lock wrapper `0x4E16F0` (enabled byte `+0x7C`, else no lock). Code: `memdiag.cpp` `RcCensus`.
- **Holders:** `RcCensusHolders=N` (off): once, at the Nth census, 64 entries of the type that grew most are sampled under
  the lock; after it the memory worker scans every committed readable region for dwords within 0x40 bytes of a sample and
  names each holder by the nearest TS3W.exe vtable at or below it (no RTTI in the exe: the vtable is an address to read in
  a disassembly). Lines: `diag: rc holders #n vt 0x... +off delta d n= covers k of 64`. Not a proof: stale dwords can match.
- **Builders, graph:** `RcCensusBuilders=1` counts the live CAS SimBuilders and the compositors their part maps hold;
  `RcCensusGraph=1` maps compositor-to-compositor references (child vector `+0x28`). Both off.
- **Cost:** the game waits for the lock while the walk runs (the line gives `walk` ms). Test runs only.
- **Selftest:** a 4-bucket fake map (types by count, the refs / cold split, a bad key, changes), a lock owned by another
  thread (skipped), a cycle (stopped, lock released), nothing at exit.

### Who holds the part compositors (`RcRefCallers`, built 2026-11-05, ledger §941)

The census runs found ~90% of the `rcN` growth in two in-memory TXTC types, `0x033A1435` and `0x0341ACC9`: the part
compositors that `CAS/SimBuilder/SetPartCompositor` (`0x5E2C10`) makes, each at 2 references (the cache's + one holder)
and never cold. The live SimBuilders hold only 0-40 of them, so the holder is something else. `[MemDiag] RcRefCallers=1`
(off; its own switch, not under `RcCensus`) counts who takes and drops the references.

- **Hooks [verified disassembly]:** AddRef / Release are slots 0 / 1 of the two types' vtables `0xFE68D8` / `0xFE68F4`
  (`0x723130` -> `0x731D00`, `0x5B07A0` -> `0x731D40`); only these two types reach the replacements, so no type filter
  runs per call. The cache's Find (vtable `0xFFDD94` slot 6, `0x7324E0`) AddRefs what it returns through a direct call to
  `0x731DB0`, so a find that returns a compositor counts as an AddRef by the find's caller. The only other direct calls to
  the three are the cache's Find and Add (`0x732542`, `0x732AC5`) [verified: every `E8` / `E9` to them]: the cache's own
  reference (Add) is the one AddRef not counted.
- **Per caller:** each `diag: mem` line (but exit) logs `diag: rc ref callers` totals (AddRef, finds, Release, net, since
  the start and since the last line) and the top 20 callers by net (return address, AddRef, finds, Release, net, net since
  the last line) plus the 5 most negative (who drops them). `diag: rc find sites` gives the finds' caller chains.
- **Per object (`RcRefCallersSample=32`):** 1 in 32 new compositors (an AddRef at 0 references) keeps its first 16 events.
  At each line the live ones are grouped by event list, and those at net +1 in the counted events (with the cache's
  reference that makes 2) by their last AddRef or find: the reference not dropped since.
- **Registry:** the CAS texture compositor service (`[0x11CE6A0]`) keeps every new compositor in a hash map at `+0x104`
  (insert `0x5EECD0`; the node copy `0x5ED870` AddRefs at `0x5ED8B8`; erases: the clear-all `0x5EEA90` and the per-key
  `0x5EE640`). `diag: rc registry` gives its size and change and how many sampled compositors it holds, so a net +1 group
  can be checked against it. Node `{ResourceKey, compositor +0x10, next +0x18}` (next: the bucket free loop `0x5ED83F`).
  Read without the service's lock.
- **Cost:** the install times our part of a hooked call on a fake key before the hooks go in (selftest: 6 ns; with the
  sampling path) and a find chain (56 ns); each line reports the estimated ms since the last line and its share of the
  interval, to subtract from the run's timing. The tables are static (1.4 MB), not growth.
- **Counters v908:** `rrcOn` (1 hooks, 2 and sampling), `rrcAddRefs`, `rrcReleases`, `rrcFinds`, `rrcCallers`,
  `rrcSampled`, `rrcRegistry`, `rrcHookNs`; the totals are updated at each line.
- **Selftest:** a wrong vtable slot refuses; 3 + 1 AddRef, 1 Release and 2 finds by caller, sorted by net; a sampled
  object at net +1 in a 2-bucket fake registry (next `+0x18`), one freed, one at net +3.
- **Result (2026-11-05, `old-rrc-35`, docs/data/results/2026-11-05-alexvixell35-stack-rc-ref-callers.md):** the holder is
  the registry. t = 300 s to 2400 s: `rcN` +100,641, the registry +96,456 (96%), 0 erases; at the end 105,262 of its
  135,261 entries (78%) have exactly 2 references (the cache's and the registry's). A second, smaller holder outside the
  registry: 292 of the 1,031 sampled net +1 compositors were last AddRef'd in `0xAD4250` (`Lot/CompositorManager`).
  Hook cost 0.001% of play.

### Step 2: trim the compositor registry (`[CasTrim]`, built 2026-11-05, not run in game, ledger §942)

If step 1 names the registry (I-941-2), the leak is a list that never trims: the registry's only erase, `0x5EE640`, runs
from the service's clear-all, i.e. on a service reset [verified]. Its map is read only by the export code (`Contains`
`0x5EDAB0` from ExportLot, ExportWorld, Sim outfit export, thumbnails) [verified: callers and their strings; corrected
2026-11-07: the save `0x5EFD60` and teardown `0x5F0BA0` walk it too, docs/539 §2.3]; in play a
compositor is reached through the ResourceCache by key, which the insert registered (manager vtable `+0x5C`, flag 1).

- **What:** a detour on the insert `0x5EECD0` walks `Buckets` (16) more buckets after each insert, on the inserting
  thread (the map has no lock; inserts from a second thread are counted, never walked from). A part compositor (vtables
  `0xFE68D8` / `0xFE68F4`) is old at exactly 2 references (the cache's and the registry's: a SimBuilder part, a find in
  progress or any other holder makes 3+) and no insert or find by key for `AgeSec` (1800 s). Mode 1 erases old ones
  with EA's own `0x5EE640` (find, unregister with flag 0, free the node and its reference), at most `MaxErase` (8) per
  insert; mode 2 only marks them. Off by default; `--ab castrim` runs mode 1 against off.
- **The safety evidence:** a detour on the cache's Find `0x7324E0` (vtable `0xFFDD94` slot 6, no direct callers) counts
  a find of a marked compositor as `ctWouldMiss` (mode 2) and a failed find of an erased key that nothing can build
  again as `ctMissed` (mode 1, must stay 0), the first 20 events with caller, three more exe frames, age and whether a
  database holds the key. A find that misses means a Sim wanted that texture back.
- **Age alone is not safe (al243 / al244, old save, count mode, 2026-11-05):** at `AgeSec=600` compositors were found
  by key again after any time unused: re-found after 10-30 min 4,627 times and after 30-60 min 2,591 (the 60 min run's
  limit), 6,293 would-miss in all. Who asks, and what each does on a miss [verified disassembly]:
  - a parent compositor's texture rebuild: the texture code (`0x6CBF20` / `0x6CCA90` / `0x6CCC50`) calls `0x605CE0`
    ("handle, or rebuild it" when the handle is -1) -> `0x605C70` -> `0x6057F0` (strings
    `CAS/TextureCompositorResource/FabricOpKeyList`, `GetTextureBuilder`). Each op of type `0x34210A5` in the parent's
    list (`+0x5C`, count byte `+0x60`) names a child by key (parameter `0xDCFF6D7B`, key from `0x600850`), fetched
    through the manager's get `0x7366B0` (all logged would-miss: `0x736718` < `0x605A75`). A failed get returns handle
    -1: the texture is not built. Parent and child are both part compositors (ctor `0x600560` sets `0xFE68F4`), so a
    child at 2 references is still named by a live parent;
  - the get (`0x736718`), the async loader `0x729E10` (`0x729E80`, `ResourceLoad/AsyncLoader`) and the blit `0x5FA670`
    (`0x5FA6D4`, then a request by key `0x5F8170`): on a cache miss they load the key from a database (manager `+0x44`
    = `0x4AFDA0` -> `0x4AFFC0` finds which one; the get then creates it through the type's factory and registers it);
  - the parent's Load `0x607070` (`0x60733E`): a miss rebuilds the child from the parent's own data (new `0x68`, ctor
    `0x600560`, Load again, register `0x7D9100`).
- **`NeedDb` (default 0):** a compositor is old only if a database holds its key (manager `+0x44`, the lookup EA itself
  makes at the start of every get, `0x7D80E0`; asked once per compositor, again after `AgeSec`). Every way back then
  exists. al246 (old save, count mode, 60 min): 0 of the old ones has a database (384,058 asks, 11.3 s), so it trims
  nothing. EA's disk caches (`compositorCache.package`, `simCompositorCache.package`) hold finished textures, not the
  compositors [inferred: strings and handle flow].
- **`Named` (default 1, 2026-11-05):** a compositor is old only if no other part compositor in the registry, itself not
  marked old, names its key as a child. The children are read as `0x6057F0` reads them: parameter block `0x602380`,
  the `0xDCFF6D7B` parameter (name table `[0x1157EB8]`, lower bound `0x600280`), key `0x600850`; both take EA's
  recursive critical section `0x11CEB38`. The names are collected per full walk of the map (a cycle, restarted when the
  bucket array changes); a mark needs the last full cycle and the current one, and a new compositor's names count from
  its insert. A parent marked old stops naming, so a dead outfit's tree is marked top down, one level per two cycles.
  Only type `0x0341ACC9` (vtable `0xFE68F4`) answers the child cast `0x3420F34` that `0x6057F0` makes. The count run
  shows whether every would-miss was named.
  - al249 (count, `Named` on, old save, 60 min, PASS; al248 with the same build crashed at 7.6 min in the render path,
    the al208 family): would-miss 6 (age alone: 6,293), none from a parent's rebuild. All 6 came from the async loader
    `0x729E10` (return `0x729E80`), from an outfit's load: `CAS/SimOutfitResource/Load` `0x5E9AA0` -> `0x5E55B0` ->
    `0x5E53E0` (an async request by key through `[0x11D3C84]` +0x34, callback `0xD4B150`). Each was unused ~50 min and no
    database has its key [verified disassembly of the chain; the outfit's own key source not traced].
- **`Async` (default 1, 2026-11-06):** a compositor the async loader has found by key is never marked old. Every find of a
  known compositor sets a bit for its caller (async loader, get `0x736718`, blit `0x5FA6D4`, parent Load `0x60733E`,
  other); the would-miss events print the bits from before. If an outfit's parts are found there when the outfit first
  loads, this keeps them; one built in play and loaded again later without an earlier async find is not covered. The
  count run shows which.
- **Not covered:** the export paths read the registry itself (`Contains`): a lot or Sim exported to the library after its
  compositors were trimmed would miss those textures in the export [inferred]. Mode 2 on an export would show it as no
  counter; the export check is a manual step of the step 3 checks.
- **Counters v912 (0x5CF0, built as v909 in #285):** `ctMode` (switch), `ctOn`, `ctInserts`, `ctScanned`, `ctCand`, `ctErased`, `ctWouldMiss`,
  `ctMissed`, `ctFull`, `ctOtherThread`. Tables: 11 MB, allocated only when installed (14 MB with the pending table; `ctMode` 3 and 4: docs/539 §6).
- **Selftest:** a fake map (two buckets, four nodes), fake EA erase / insert / Find: an old compositor marked (count) then
  erased (trim), one at 3 references and one found 11 s before left alone, one without a database kept (`NeedDb`, not
  asked again before `AgeSec`), a would-miss, a failed find of the erased key loaded again while a fake database holds
  it and missed once none does, the parent-Load find, `Named` (a child kept while a live compositor names it, marked
  two cycles after its parent is marked, kept again by a new compositor's insert), `Async` (a compositor the async
  loader found stays unmarked, and is marked with `Async` off), the second-thread insert, a walk fault (mode off).
- **Checks before any default (Rodney's step 3):** count mode first on the old save (would-miss near 0 over 60 min with a
  travel and a save + reload); then off vs on, same seed: `rcN` / `eaRes` flat after minute 15, the largest free block
  at the end, no missing or wrong textures in CAS, live mode, after a travel and after a save and reload, no new
  exceptions, stuck Sims or crashes, hitches not worse. Repro `castrim-outfits`, queue `tools/harness/queues/castrim.json`.

## Step 7 experiment: ResourceCache trim and Boehm heap return (2026-10-26, docs/198)

Static check of giving the Boehm heap back (docs/198 §2-§3: no unmap code; decommit feasible for RAM only, release rejected) and the `[ResourceCache] TrimAfterCleanup` experiment (docs/198 §4, ts3native 0.36.80, off, stands down with Apex; queue `tools/harness/queues/rctrim-exp.json`, not run). Apex's area: nothing is proposed to Apex until a run shows a proven gain (Rodney, 2026-10-26).

## Stress in Build/Buy and Edit Town (2026-10-26, docs/200)

Saturation cycles with the same marks every cycle (`<prefix>-full` / `-empty` + forced-GC pair) in live mode (`live-cc-cycle`: town objects, dressed Sims, inventory), Buy/Build mode (`bb_cc_cycle`: placement cursor + sell tool) and Edit Town (`et_lot_cycle`: library lot place + bulldoze on one plot); queue `tools/harness/queues/cc-build-stress.json` (not run), live cycles with `[ResourceCache] TrimAfterCleanup` off and on.

## Ownership

Script and mod leaks are ours to find; fixes in mods go upstream (NRaas: patches, never copied code, docs/184). Native engine leaks and cache caps are Apex Radiance's area (engine, allocator, memory): we measure and send PRs or reports. Our own code (worker threads, P5/P6 clone, pools) is checked by the same soak runs.

## Prior art

Researched 2026-10-25 (web, GitHub API, NRaas source clone). URLs and licenses in docs/24 "Added 2026-10-25". Evidence: **measured** = the source publishes numbers or a capture; **claim** = author statement without data; **anecdote** = forum player or moderator.

| Source | What it says or does | Evidence | Maps to |
|---|---|---|---|
| EA forums / Steam threads on Error 12 (moderators) | memory "only rises" over a session; travel keeps part of the previous world; a save adds a spike; save below ~3.4 GB in Task Manager; long CAS / Build-Buy / Edit Town sessions are heaviest; restart after travel | anecdote | steps 3, 4 (the claims we test) |
| EA Answers, Mac 64-bit build | the 64-bit Mac client keeps growing until physical + page file limits (one case: endless routing of a large dog) | anecdote | without the 4 GB cap growth is still reported: real growth, not only fragmentation [inferred] |
| Catalogue Lag and Memory Leak Fix (Sonja92yu, Nexus 242) | EA builds ~80 hidden UI windows per catalogue (previews, EP badges); reopening catalogues grows memory by hundreds of MB; LAYO-only fix | claim | the only named EA leak; new repro for steps 3/4 (docs/47 window tree) |
| S3PP `StblBucketLayoutTuning` (Nexus 235, its ini) | shrinks STBL bucket arrays, "~290 MB in a large save"; `LocalizedStringCache` capped at 16,384 entries | claim; ours: -71 MB commit, +64 MB largest free (docs/66, docs/17) | fixed cost at load, not growth; docs/190 `stbl` |
| S3SS issues #42, #60, #63 | "+1 GB since 1.4.0" was the new, more accurate memory monitor (maintainer); one case fixed by redoing GraphicsRules + DXVK; advice: DXVK `d3d9.textureMemory` | anecdote + maintainer | no leak found by S3SS; its features (mimalloc, stack fix, monitor) cut fixed cost |
| Apex Radiance Room to Save (`MemoryGuard`) | 128 MB reserve released before every save; under 320 MB largest free it calls the game's own shrink of both ResourceSystem caches (idle entries only, `this+0x1E0`/`+0x1E4`, trim `0x732E90`, shrink-both `0x733E70`); partial 16 MiB steps keeping 64 MiB idle | static verified by Apex; RAM gain not established (their 2.14 notes) | step 7 (ResourceCache is a cache, not a leak); reuse their addresses |
| Apex native allocation doc | EA allocator trim `0x4E4F90` returned 0 bytes in a session (core tops already returned on free); Mono heap/free counters `0x12225A0`/`0x12225B4`; Boehm grows through `VirtualAlloc` wrappers `0xD70900`/`0xD9AAF0` outside the EA allocator | static + one measured trim | step 5 (heap slack is not trim-reclaimable), docs/190 `gc` |
| Apex memory analysis / snapshot tool | per-owner counters for Apex's own allocations, game allocation categories via named entries only, external VirtualQueryEx snapshot | diagnostics only, game attribution "unverified" | same gap as docs/190; compare snapshots |
| TS3VASManager (ZanderFrieze, GPL-3.0, fork of TS3-64bit-Patch) | 1 GB in-process arena for large allocations; reports large blocks are "recycled cleanly, zero leaks inside the proxy" in long stress tests; Mono GC is blind to native pressure, so it signals a managed `GC.Collect()` + `WaitForPendingFinalizers()` whose finalizers free native backing | claim (graph published, no leak data) | steps 5, 6: leaks, if any, are in small blocks; finalizer-backed native memory is a testable cause |
| NRaas ErrorTrap (source) | 650 `Deref*` performers + 8 controllers null dangling references **once after each load**; `DereferenceDelegate` notes delegates re-added on every load without removal | verified source (docs/184) | frees managed garbage only across save/load, never mid-session; step 1 must check load-time subscriptions |
| NRaas Overwatch alarms (source) | nightly cleanups: outfits, vehicles, relationships, inventories, dead Sims, homeless, situations, skill modifiers | verified source (docs/145) | game-data bloat (managed); step 6 census with and without |
| Memories / save cleaners (MasterController, Saver, Regul, docs/09) | memories and relationships bloat saves and slow play; "Save As" advice | anecdote | step 6 class counts (memories, relationships) |
| S3PP review (simscommunity.info) | slowdown after 30-40 min, entering Build mode and back clears it for a while | anecdote | step 3 marker: what Build mode flushes |

**Key takeaways.**
- Nobody has published a measured leak list for TS3. Every public tool (S3SS, S3PP, Apex, TS3VASManager, TS3-64bit-Patch) lowers fixed cost, reserves headroom or frees caches before a save; none attributes session growth to an owner.
- The only concrete EA leak claim is the catalogue UI windows (Nexus 242). The only concrete mechanism claim is TS3VASManager's: collectable managed wrappers hold native memory until a full collect runs finalizers.
- ResourceCache growth is cache behaviour the game can already shrink (Apex calls the game's own trim); the EA heap returns core tops itself, so allocator slack is not where leaks hide.
- ErrorTrap and Overwatch fix data (dangling references at load, nightly entity cleanup), not native memory, and ErrorTrap acts only at load: in-session managed growth is uncovered.

**Not covered by anyone:** allocation-site attribution of session growth (step 5), a Mono class census with root paths (step 6), world-switch measurements (step 4), a static scan of script and mod subscriptions and static collections (step 1), and a check of the catalogue-window and finalizer claims.

**Changes to the steps from this research.**
- Step 3: add markers and a loop repro for catalogue open/close (Build/Buy, CAS, Create-a-Style) and Build-mode entry; check Nexus 242's "hundreds of MB" on our setup, with and without its LAYO.
- Step 4: travel round trip (home -> vacation -> home) as the world-switch repro, per the forum claims.
- Step 6: start with a cheap probe: `diag: mem` before and after a test-agent `GC.Collect()` + `WaitForPendingFinalizers()` at minute 0 and minute 60. A large drop = collectable but uncollected (finalizer-backed native or Boehm budget), not a rooted leak (~1 h + 1 run).
- Step 7: reuse Apex's ResourceSystem cache addresses (`0x732E90`, `0x733E70`, `+0x1E0`/`+0x1E4`) instead of our own RE; cache caps stay Apex's area.
- Step 5: weight the scan to small blocks (EA heap bins, mimalloc), since large blocks are reported to recycle cleanly.
- Step 1: list subscriptions added in `OnWorldLoadFinished` / `OnStartupApp` without a matching removal (ErrorTrap's per-load delegate note).
