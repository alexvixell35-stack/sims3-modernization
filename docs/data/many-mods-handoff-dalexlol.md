# Many-mods items handed over (2026-11-20)

Spec: `docs/302-many-mods-performance.md` (§ references below). These items moved from our lane to the contributor
lane on 2026-11-20. Our lane keeps §3 #1 ModTime (on master, docs/298), #2 spread fixes (rechecked, all ON since
v0.1.5 / v0.2.0, docs/145 §15, 146 §8, 147 §9) and #3 the per-task-run attribution seam.

Branches named below (`feature/many-mods-fixes-1120`, `wip/modbudget-1120`, `feature/mod-conflict-scan-1119`) are in
the maintainer's repo; ask for them to be pushed when you start an item.

Already on master for every item below: `[Diag] ModTime` (docs/298; `MdtOwnerOf(DWORD monoMethod, MdtOwner*)` in
`native/ts3native/modtime.cpp` resolves a MonoMethod to its mod, cached, any thread), the queue key `modSet` with the
script set `docs/data/many-mods-s.json` (`tools/mod-set.ts`; the NRaas packages need a manual download from the
official pages), the load split line in `cycle.ts` and the queues `many-mods-load-split-1120`, `-age-`, `-modtime-`,
`-pie-cost-1120` (docs/302 §9).

## Rules for every item

- Every new switch defaults **off** (ts3native.ini or ts3fixes tuning); diagnostics off unless the design says always-on.
- Anything that changes Sims: explicit household, selected-Sim and unknown-scope exemption in code (unknown scope counts
  as household), using the shared predicates `HhExempt` (native) / `Fixes.HouseholdExempt` (managed). Results for
  controlled Sims must equal vanilla. `[Experimental] HouseholdToo` must not lift deferrals, skips or cuts.
- Every regex (code, `--self-check`, queue `logRequire` / `logForbid`, repros.json) gets `regexCases` with a real
  matching line and a real non-matching line.
- Every queue reads back the switch values and the build version; correctness jobs `scriptErrorsMax: 0`; dry-run every
  queue; Bin state `apex` (Apex + RTX Remix bridge) only; smoke before any queue over ~1 h.
- Counters, doc numbers and docs/51 ledger sections: reserve with `node tools/registry.ts reserve ...`, never by hand.
- Verify every address per edition (Steam 1.67, retail 1.67, EA 1.69) in the disassembly before hooking; mark claims
  `[verified]` or `[inferred]`, new inferences go to docs/51.
- Builds registered in `docs/data/builds.json` with sha256; `node tools/selftest-all.ts` 0 FAIL + native selftest.

## §3 #4 `[ModBudget]` per-mod fair share (docs/302 §4)

- **Spec:** §4.1 (only task runs at the resume boundary), §4.2 (owner per task entry), §4.3 (deficit round-robin,
  `B` = 6 ms (2-20) scaled by game speed, quantum `B / active owners`, deficit max 2x quantum, own rotating cursor,
  hold only, never reorder), §4.4 (soft hold off by default; notice; `Test=<package>` opt-in hourly A/B; `Never=`
  list), §4.5 failure modes, §4.6 measurement, §4.7 gate.
- **Files/addresses:** per-task resume `0xD7F000` thiscall(host, &entry, bool, bool), entry `+8` = task state;
  `0xEA1090` invoke path; ThreadedObjects `0xD7F8B0` (EA 5 ms slice, cursor `host+0x1700`), TaskObjects `0xD7FC60`
  (docs/36 §1-2, docs/166, docs/04a §1-2). The attribution seam (#3, our lane) is the hook this policy sits on: build
  on top of it, do not add a second detour at the same site.
- **Built:** the seam (#3) is `[Diag] TaskSeam=0` (docs/308, `native/ts3native/taskseam*.{cpp,h}`, Steam + retail; off
  on EA 1.69, whose exe is encrypted on disk so the addresses are 0); `tskSpare[8]` at 0x6B28 is held for ModBudget
  counters. A policy draft (deficit round-robin core, eligibility, `Never` / `Test` lists, stand-down, unit tests) is
  on branch `wip/modbudget-1120` (one WIP commit on the pre-strip seam files: not built, not run, older than the
  committed seam, so rebase the policy onto it). `[inferred]` (docs/51 I-556-3): under the household rule the policy
  would defer almost nothing, because most mod managers are global (unknown scope); the RUN FIRST job below decides.
- **Left:** policy core as a game-independent `*Core` file with selftests; hold = skip one resume (an extra
  `Sleep(0)`), never a mid-run cut, never an automatic skip of a mod; only provably-NPC-scoped runs of non-EA, non-ours,
  non-core-mod assemblies; hard ceiling `MaxHold` = 2 iterations (1-10) and 250 ms wall; hold per owner; stand-down
  when another hook owns the seam (docs/68) or held tasks throw more than off; counters held runs, max hold, ceiling
  hits, held-then-threw.
- **Test steps:** RUN FIRST `taskseam-smoke-1120` (correctness), then `taskseam-manymods-1120` on the many-mods set (NPC-scoped share of mod task time); ON only
  if >= 20% (§4.7). Then correctness off/on: household route-wait, idle %, selected-Sim interaction start latency equal
  to the off arm; then a timing ABBA (needs a quiet machine), smoke first.

## §3 #5 Mod startup attribution + per-mod spread (docs/302 §2.1 L8)

- **Spec:** L8 row; §6.3 jobs 1-2.
- **Built:** the attribution half is in ModTime on master: sampling starts at the first script iteration and the
  summary has a load block (top 5 mods, seconds covered). Whether the front end runs script iterations before the
  world-load iteration is `[inferred]` (ledger I-544-6), checked by the ModTime smoke.
- **Left:** run `many-mods-modtime-1120` (both arms) and `many-mods-load-split-1120`; for each mod that tops the load
  block, a spread like `kErrorTrapSpread` (docs/146) behind its own default-off switch, never during save, load or
  travel; world-load-finished work only.
- **Test steps:** load split before/after on the script set, ModTime load block before/after, `scriptErrorsMax: 0`,
  the mod's own state checks (settings and per-Sim data intact after the spread).

## §3 #7 Script tuning cache that stays on (docs/302 §2.1 L7, docs/581 §2.1)

- **Files/addresses:** gate `CacheManager_IsCachingAllowed 0x73C6C0`, `scriptCache.package`.
- **Built (WIP, not for merge):** branch `feature/many-mods-fixes-1120` commits `406a60d92` (docs only, doc 307) and
  `b839e7cef` (code, marked WIP): `[ScriptCache] Enabled=0` adds a package-list fingerprint (path, size, last-write
  time, priority) to the signature and opens the gate on it. `[verified]`: the gate fails whenever any package
  overrides a GameplayData resource; the game then clears the cache and rewrites only the signature (the 260 B file).
  `[inferred]`: the signature misses mod tuning XML stored outside the GameplayData database.
- **Left:** prove the fingerprint covers every source of tunables (a changed mod must invalidate); never stale tunables.
- **Test steps:** RUN FIRST `scriptcache-boot-time-1120` (quiet machine) to size the gain, then `bp-scriptcache-1..3`
  in order (cold, warm, warm after a mod change).

## §3 #8 Pie-menu / autonomy Test cost (docs/302 §2.2 S4)

- **Built:** measurement only: repro `pie-menu-cost` (120 menu builds) and queue `many-mods-pie-cost-1120` on master;
  needs MasterController + interaction mods from the script set. The repro cannot target another Sim yet, so the
  social pie menu on other Sims is not measured.
- **Left:** run it with ModTime on; a Test memo only if the cost is big, and only for Tests proven not to read live
  state that changes between calls.
- **Test steps:** ModTime hitch stacks around the menu builds, per-mod share, p99/max of the menu build time.

## §3 #9 managed part: thumbnail dispose (docs/302 §2.2 S6, docs/193 §3.3)

- **Files/addresses:** `GetThumbnailImage 0x782D00`, `UIImage` finalizer `0x779430`. The engine part
  (CASThumbnails bound) is the Apex-fork lane, not this item.
- **Built (WIP):** ts3fixes `thumbdispose` (default off, counters [1492-1493]) on `feature/many-mods-fixes-1120`
  (`mods/ts3fixes/src/ThumbDispose.cs`). `[verified]`: Build/Buy `AddGridItem` leaves one thumbnail per cell for the
  finalizer. It captures per script task because both methods can yield.
- **Left:** CAS grids are not covered; private bytes / handle read-back.
- **Test steps:** job `bp-thumbdispose` in `many-mods-fixes-correct-1120.json`: page Build/Buy and CAS catalogs N times,
  compare private bytes and counters off/on. UI only, no Sim touched.

## §3 #12 `GetCachedPart` random-part EA bug (docs/302 §2.1 L5, docs/550)

- **Files/addresses:** `0x5A44A0`; `[verified]` Steam and retail bytes: in the collect loop the three mismatch jumps
  at `0x5A48B2`, `0x5A48C9`, `0x5A48CE` land on the all-ages test at `0x5A48DC` instead of the next-entry walk at
  `0x5A4922`, so an all-ages request collects every cached part; the random source `0x11CE400` is used only here.
  Retail patch site `0x5A50ED`; EA 1.69 has no address yet (skips there).
- **Built (WIP):** `[CasPartPick] Enabled=0`, a 3-byte patch with a selftest (`native/ts3native/manymods.cpp`).
- **Left:** EA 1.69 address; argue in the doc why only the buggy pick changes (EA bug fix, behind a switch).
- **Test steps:** job `bp-caspartpick-on` (outfit pick for an all-ages request is deterministic and correct).

## §3 #13 In-game duplicate / conflict report (docs/302 §2.3 M5)

- **Built:** `tools/mod-conflicts.ts` (docs/295) on `feature/mod-conflict-scan-1119`, not merged: duplicate keys with the
  load-order winner, unloaded packages, duplicate assemblies, shared `Singleton` stores.
- **Left:** merge it; the conflict tool then goes to Apex as a PR (no AI in the tool); the PR text is drafted and
  waits for the maintainer's OK.
- **Test steps:** `--self-check` cases; run on a known install with planted duplicates and check the winner column.
