# simdescindex handoff: description index for `SimDescription.Find` (docs/359)

### Description index, built default off, not run (2026-11-26 PHT, sub-agent / Fable)
- State: built, selftests pass, both queues dry-run. **No game run.** Not merged.
- Branch: `feature/simdesc-index-1126` (from master 632f130c7), worktree `.claude/worktrees/simdesc-index-1126`
- Changed:
  - `mods/ts3fixes/src/SimDescIndexCore.cs` (new: `DescIndexCore`, `UrnSet`; game-independent)
  - `mods/ts3fixes/src/SimDescIndex.cs` (new: the `Find` hook, 16 writer hooks, urn notes, mod scan, counters)
  - `mods/ts3fixes/src/DescWriterScan.cs` (opt-in `CallTargets`, `AliasLocals`, `FreshReceiver`; off for other scans)
  - `mods/ts3fixes/src/DeadRefsPin.cs`, `DeadRefsPerLot.cs` (one call each: urn init / destroy note in the existing hooks)
  - `mods/ts3fixes/src/Fixes.cs` (apply call, switch defaults, counter map line), `CheckLiveMap.cs` (generated)
  - `mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs` (switch names, `simdescindex_*` counters)
  - `tools/selftest/simdescindex/` (new), `tools/selftest-all.ts` (csc entry)
  - `tools/static/desc-writer-scan.ts`, `desc-writer-scan/Scan.cs` (`DWS_SIMDESC=1` mode)
  - `tools/harness/queues/simdesc-index-correct-1126.json`, `simdesc-index-time-1126.json` (new)
  - docs: `docs/359-simdesc-index.md` (new), `docs/51` §628, `docs/index.md`, `docs/registry.md`, `docs/356` §6, `PLAN.md`
- Builds (docs/data/builds.json): `ts3fixes.package.simdesc-index-1126c` (builds `...-1126` and `...-1126b` are
  superseded: a stood down for NRaas, b lacks the rule-review fixes), `ts3agent.package.simdesc-index-1126`;
  asi and ini are master set m1126b (no native change)
- Switches: `simdescindex` OFF, `simdescindexverify` OFF (no tuning entry yet)
- Counters / slots: 1899-1922 (`fixes-1899`), doc 359 (`doc-359`), ledger §628 (`ledger-628`); the three
  reservations are in this branch's `docs/data/registry-reservations.json`. **Merger:** `docs/registry.md` here says
  "1923+ next free"; run `node tools/registry.ts fix-next docs/registry.md` after the merge
- Tests: `node tools/selftest/simdescindex/run.ts` (566,558 checks: fixed cases, 12 random runs against a model of
  the game's walk with hooked and unhooked writers, the urn set, 20 scan cases, the helper shapes: readers and a writer of each); `tools/selftest-all.ts`: see the commit
- Household: results equal vanilla for every Sim (the game's own object or the game's own code); applies to all Sims
- Pause / CAS: no gate needed (pure lookup)
- 4x / streamed world: removes an O(residents) walk per lookup and a world-wide object query per miss

Facts found
- Writers of the three inputs of the walk: docs/359 §3 [verified, V-628-1..3]
- The id setter cannot be hooked (inlined, docs/293); its 3 EA callers act on a new description [verified, V-628-3]
- A constructor cannot be hooked by `Fixes.Patch`; `Household(bool)` adds an empty household [verified]
- The game's mscorlib has no `System.Threading.Interlocked` (compile error) [verified]
- NRaas direct writes: 3 removals and 1 setter on a new description [verified, V-628-4]

Open (docs/359 §8)
1. NRaas stand-down: fixed in build b. The scan follows a list through locals, `IEnumerable` parameters, mod
   callees, list-returning helpers, mod fields and two EA callees; one NRaas Register constructor is covered by IL
   hash (docs/359 §5). Offline the NRaas full set has no writer method. Still to see in game: `untrusted` = 0 with
   NRaas (job bp-sdi-nraas-full); the offline tool cannot read 42 methods the game can [I-628-6]
2. `simdescindex_ready` must read 7 in the smoke job (hooks in, urn hook in, scan done) [I-628-4]
3. Inferences to confirm in the first run: I-628-1 (load events bracket the load), I-628-2 / -3 (urn set is a
   superset), I-628-6 (scan reads all of Ts3Fixes in game)
4. The scan's in-game time is unknown; it runs once per process
5. Call rate not measured before the build (Rodney 2026-11-26); read `calls` / `hits` / `misses` in bp-sdi-correct
6. Rule review of 704db4966 (build c): both majors and the minors are in, except the Sim-thread test: the game's
   mscorlib has no `Thread` class and no DllImport, so it needs a ts3native internal call (docs/359 §8 item 7).
   Decision for Rodney
7. ON gates, not verified statically: I-628-1, -2, -3, -5, -8, -9 (docs/51 §628)

Next
- Run `simdesc-index-correct-1126` (any machine, ~3 h 10 min, 12 jobs incl. the NRaas full set), then `simdesc-index-time-1126` (quiet machine)
- If the smoke fails on `untrusted`: ts3native.log line `simdescindex scan: writer ...` names the method
- If bp-sdi-nraas-full reads `untrusted` > 0: the log line names the method; add the shape to the scan or cover it by hash
