# 227. Unseen-check cost probe and the central unseen table

Status 2026-11-02: probe merged to master and built (game-copy/ts3native.asi.unseencost1, ts3fixes.package.unseencost1; native selftest and selftest-all pass), not run yet; first data from the unseen-anim-ab rerun (probe on in both arms). ts3fixes switch `unseencost` (off, test runs only), ts3native `[UnseenCost] Enabled=0` (0.36.154, counters v148 `uc*`), ts3fixes counters 690-743. Queue `tools/harness/queues/unseen-cost-probe.json`, report `tools/harness/unseen-cost-report.ts`.

## 1. Why

Rodney's idea (2026-11-02): every one of our unseen fixes asks "can the player see this Sim?" its own way, several times per Sim per tick. One central, cached answer per tick would make the rules consistent and could save time. Before building it, measure what the checks cost now and how often the same Sim is asked twice in one tick. [inferred] until the probe runs: the checks are cheap one by one; the question is the sum and the repeats.

## 2. What the probe measures

Per check site: calls, top-level calls (not inside another timed site), and 1 call in 16 per site timed (script: `StopWatch` us, wrap-safe uint clock; native: QPC ticks summed in 64 bits). The sites nest (`SimUnseen` -> `FarFromViewers` -> `RefreshViewers`; `UfcTaskSimUnseen` -> `UfcListed` + `UnObjClass`), so per-site ms/s is inclusive and the total adds the top-level calls only.

| Part | Sites (index) |
|---|---|
| script (`UnseenCost.cs`, `kUc*`) | 0 `FarFromViewers`, 1 `RefreshViewers`, 2 `SimUnseen`, 3 `TownieUnseen`, 4 GoHome `Unseen`, 5 `UfPosUnseen`, 6 `UfLotSeen`, 7 `UfGoalUnseen`, 8 `DisposeSpreadUnseen`, 9 `EveryN.Unseen` (the 3 throttle gates), 10 `UfWhy` |
| native (`unseencost.cpp`, `UC_*`) | 0 `UnObjClass`, 1 `UtWhy` (the [UnseenThrottle] list rule), 2 `UtViewerWhy`, 3 `UfcAgentUnseen`, 4 `UfcTaskSimUnseen`, 5 `UfcListed`, 6 `AaNearVisible`, 7 `UnRefreshCamera` |

Also: the script checks' interop calls (`World.IsObjectOnScreen` from the unseen checks, `UfCamAsk`, `RefreshViewers` reads that did the work), and the repeat ratio: script Sim asks / distinct Sims per sim tick (sites 2, 3, 4, 8, 10), native `UnObjClass` asks / distinct objects per locomotion pass (`unPasses`, sim thread). The ratio is what a per-tick table saves on the classification itself.

Counters: ts3fixes `[690 + 4 i]` calls, `[+1]` top-level, `[+2]` timed, `[+3]` timed us (site i 0..10); `[734]` IsObjectOnScreen, `[735]` UfCamAsk, `[736]` RefreshViewers reads, `[737]` Sim asks, `[738]` distinct Sims summed over ticks, `[739]` ticks with the probe on, `[740-743]` calibration pairs / us, floor pairs / us (agent names `unseencost_*`). ts3native v148 at `0x55F0`: `ucReady`, `ucN[8]`, `ucTop[8]`, `ucSampled[8]`, `ucUs[8]`, `ucAsks`, `ucDistinct`, `ucPasses`, `ucSelfN`, `ucSelfNs`, `ucFloorN`, `ucFloorNs` (docs/registry.md).

## 3. The probe's own cost (measurement-tool cost rule)

Each wrapped call pays a counter increment and a depth count; every 16th also two clock reads. That cost is measured in the run, not assumed:

- **calibration:** every ~1024 script calls (top level only, so it never sits inside a timed call) 16 Begin/End pairs plus Sim notes run on scratch counters; every 4096 native calls 64 wrapped no-op calls plus 64 table notes. Their time / count = the probe's cost per call.
- **floor:** one bare clock pair per calibration; each timed sample carries one clock read, so the report subtracts the floor from every sample's mean.
- **nested overhead:** the probe cost of nested calls lies inside the top-level samples; the report subtracts (calls - top-level calls) x cost per call from the total.

The report prints the probe's own ms/s. If it passes ~2-3% of the sim budget, the numbers are suspect (tool-cost rule) and the run is a correctness run only.

## 4. Decision rule

From `node tools/harness/unseen-cost-report.ts unseen-cost-probe-<date>-<job>` on BP and IP (10 min each, release ini + fixes plus every unseen consumer on, so all sites run):

- **Build the central per-tick unseen table** only if the total unseen-check cost (script + native, top level, probe subtracted) is **> ~2 ms/s** on either world, **or** the repeat ratio is **>= 3** (most asks are repeats of one Sim in one tick).
- Else do not build it for speed. The consistency argument alone (one rule set) can still justify it later, as a refactor (docs/23 #18, after Mono).

## 5. Design sketch (only if §4 says build)

- **Native per-frame table** (ts3native, sim thread): at the start of each locomotion pass (where `UnRefreshCamera` runs now), walk the Sims once and set per Sim one bit word: on screen (frustum test, the `UnObjClass` sphere), within camera reach (max(80 m, 2 x camera height)), within 80 m of an active-household Sim (the household table ts3fixes pushes now), active household / selectable, parented. Lots get the same bits from their bounding circle (today's `UfLotSeen` rule). Keyed by object id, a few hundred entries, rebuilt each pass: no stale answers.
- **Script fetch once per tick:** ts3fixes reads the table once per sim tick over the existing GetOption2 channel (a block read keyed by tick number) into a `Dictionary<ulong, int>`; `RefreshViewers`' camera reads move into the same step.
- **All predicates read it:** `FarFromViewers`, `SimUnseen`, `TownieUnseen`, GoHome `Unseen`, `UfWhy`, `UfPosUnseen` (for Sims; free positions keep the point test), `UfLotSeen`, `DisposeSpreadUnseen`, and natively `UnObjClass` callers, `UtViewerWhy`, `AaNearVisible`, the `Ufc*` checks. Each keeps only its own extra conditions (posture, followers, routing state).
- **Consistent rules:** one definition of "unseen" (frustum + reach + household distance + not selectable) for every fix; today the script uses `IsObjectOnScreen` (EA's model bounds) and native a fixed 1 m sphere (`UnObjClass` shortcut), so a Sim can be "seen" by one and "unseen" by the other in the same tick [verified in the source: unseen.cpp `UnObjClass`, Fixes.cs `TownieUnseen`].
- **Cost to beat:** the measured total from §4 minus one table build (one frustum test per Sim per pass) and one fetch per tick.

## 6. Run 1 verdict and the table as built (2026-11-02, ts3native 0.36.155 v149 + ts3fixes 744-749, branch `feature/unseen-table`, not built)

**Verdict [measured, bp-ua-ab-r2, release ini, probe on]:** script unseen checks 4.06 ms/s net (`FarFromViewers` 969 calls/s at 3.9 us, `TownieUnseen` 134/s at 9.2 us, `UfWhy` 19/s at 34 us), native 0.47 ms/s (`UnObjClass` 4,830/s); repeat ratio 1.36 script / 1.61 native. Total > 2 ms/s, so §4 says build. Most `FarFromViewers` calls come from broadcaster pulses (object positions, not Sims) [inferred from the call sites], which a Sim-keyed table does not cover: the expected gain is the Sim checks' share, not the whole 4 ms/s.

**Native (`unseentable.cpp`, ini `[UnseenTable] Enabled=0, Mode=1, Check=0, RadiusM=3`).** A sim-pump callback (`SimPumpAdd`) bumps a tick; the first ask after it builds the table: one camera read, then one row per owner on the locomotion agent list (cap 1024): old native class (1 m sphere or camera reach), on screen with a (RadiusM + 1) m sphere 1 m up, within reach + RadiusM (wide camera = all near, as `UfSphereUnseen`), within 80 m of an active-household Sim and whether that pushed table is fresh, parented, Sim. `UnObjClass` (all native consumers: [UnseenThrottle], [UnseenAnim], [UnseenFallback] C1-C4, [UnseenGhost]) answers from the row on the sim thread; a miss, another thread or `utbMode` 0 runs the old rule. `utbMode` (+0x5694) is the runtime switch `--ab unseentable`. The household table is accepted with only `[UnseenTable]` on (viewer channel), code 25 = ICs ready, 26 = Check on.

**Script (`UnseenTable.cs`).** One `UnseenTableNative.Fetch(ulong[] ids, int[] bits)` per sim tick (rows sorted by id, binary search). `SimUnseen`, `TownieUnseen` (so GoHome `Unseen` and every trip shortcut), `UfWhy` (on screen / near viewers; map view keeps the old tests) and `DisposeSpreadUnseen` (Sims) read the row; household, posture, followers, tomb rooms, lots, held Sims and the social target stay as they were. Position checks (`FarFromViewers` of lots, doors, broadcasters; `UfPosUnseen` of goals; `UfLotSeen`) keep the point test. `EveryN.Unseen` (the throttle gates) asks no visibility itself (it is fed `SimUnseen`'s answer).

**Rules differ on purpose, in the safe direction:** the row's screen sphere and reach are at least as wide as each old test (I-418-5), so most mismatches should be "table seen, old unseen". Check=1 compares every table answer with the old one: native `utbMisUnsafe` / `utbMisSafe`, script `[747]` / `[748]` over `[749]`.

**Queue `tools/harness/queues/unseen-table.json`:** smoke, `bp-utb-check` (snap-back repro) and `ip-utb-check` (Check=1, all Sim consumers on), then 4 in-load A/Bs (`--ab unseentable`, probe on in both arms). Stop rule: unsafe mismatches >= 0.1% or `utbMiss` >= 20% of native asks.


## 7. Probe run 2 and cheap cuts (2026-11-02, branch `feature/unseen-table`, not built)

**Run 2 [measured, `unseen-cost-probe-2026-11-02-bp-uc-probe`, all unseen consumers on]:** script 9.7 ms/s, native 0.47. Per site (ms/s incl. nested, calls/s, mean us): `FarFromViewers` 4.88 (1373, 3.55; 849/s top level, mostly broadcasters, lots, doors), `RefreshViewers` 1.05 (1373, 0.77, one real read per tick), `UfWhy` 4.26 (134, 31.7), `UfLotSeen` 2.94 (194, 15.2), `UfPosUnseen` 2.09 (347, 6.0, 4 `UfCamAsk` each = 1379/s), `TownieUnseen` 1.71, gate 1.48 (677, 2.18), `UfGoalUnseen` 1.30 (63, 20.7).

**Cuts (expected savings inferred; the next probe run shows the real per-site cost, every probe site kept):**
- `FarFromViewers`: squared distances in plain floats (no `Vector3` operator calls, no double sqrt), `sReach²` per tick, the tick test inline so `RefreshViewers` (and its probe pair) runs once per tick, and a per-tick answer cache by the exact point (`UcPosCache`, 256 direct-mapped slots). Same answer (the point and this tick's viewers decide it), up to float rounding exactly at the edge. Plain, no switch.
- `UfPosUnseen`: one `UnseenTableNative.PosUnseen(float[4])` IC (unseentable.cpp, registered with `Fetch`, same answer as viewer codes 3-6, selftest in `TestUnseenTable`) instead of four `UfCamAsk`; no `[UnseenTable]` or an older ts3native = the four calls. Codes 3-6 stay.
- Switch `unseenposcache` (default off until verified; off = old code), because ts3native's camera can refresh within a tick: per-tick answer caches of `UfPosUnseen` (point + radius) and `UfGoalUnseen` (goal), and `UfLotSeen`'s lot shape (centre with its terrain-height IC, bounding radius) kept per lot while its four corners stay put (cleared per world load; a build-mode terraform under the centre keeps the old height).
- `UfWhy`: the interaction target Sim reads its table row (as the Sim itself since §6) instead of `IsObjectOnScreen` + `UfPosUnseen`; no row = old tests, Check compares.
- Left: `UfHeld` (posture chain, 4 `Satisfaction` calls per posture; no cheaper equal answer), `TownieUnseen` and the gate (already on the table since §6).

## 8. Lazy rows and RefreshTicks (2026-11-02, ts3native 0.36.158 v152, branch `feature/utb-lazy`, not built)

**Why [inferred from §6 counters, not measured]:** the §6 build ran at the first ask of every pump and made a row for every owner on the locomotion agent list (avg 16, max 96 rows at ~8 us each + one camera read): ~128 us per build at ~30-48 builds/s = ~4-6 ms/s, likely more than the 4.5 ms/s of checks it replaces.

**Change (unseentable.cpp):**
- Generations: the first ask after a pump reads the camera; a new generation (`g_utbGen`, the hash stamp) starts every `RefreshTicks` pumps (ini 1-8, default 1 = as §6) or at once on a view change: camera position or target moved > 1 m, reach changed > 1 m, camera lost or found, or a world load (userdiag.cpp `UdWorldLoad` bumps `g_utbWorlds`; the camera test also catches map view, lot and portrait jumps). There is no U-A1 view-change wake-all signal in unseenanim.cpp to reuse, so the camera delta is the signal. Runtime switch `unseentablettl` (`utbTtlMode` +0x5958): 0 = one pump per generation, for the in-load A/B.
- Native `UnObjClass`: an object's row is computed at its first ask in the generation (class, kind, position, parent: `UnObjClassBody`, the old native rule) and later asks read it. Objects off the agent list get rows too (§6 ran the old rule for them on every ask); a row without a transform answers -1 as the old rule does.
- Script `Fetch`: walks the agent list once per generation, computes missing rows and adds bits 1-4 (RadiusM screen sphere, reach, viewer, fresh) only there; output is the listed rows sorted by id with the internal bits masked off (unchanged for ts3fixes). A broken list fetches 0 rows but keeps the native rows (§6 emptied the whole table).
- Frustum: bit 1 and bit 0 already use EA's sphere-vs-frustum test (`g_unInFrustum`), the cheapest equal test; nothing to swap.
- Same answers as §6 with RefreshTicks=1, except that each row is taken at its own first ask instead of all at the first ask of the tick (same tick, same camera). Check=1 still compares every native answer with the old rule.

**Counters v152:** `utbRowCalc` (rows computed), `utbRowSampUs10` (1 row in 16 timed, 0.1 us; samples = (utbRowCalc + 15) / 16), `utbTtlMode` (switch), `utbLazyHits` (native asks from a row computed earlier in the generation), `utbForced` (early generations). v149 `utbBuilds` now counts generations, `utbBuildUs` the fetch list walks.

**Expected cost [inferred]:** native: one row per distinct asked object per generation instead of every listed owner per pump (repeat ratio 1.61: ~38% of native asks become lazy hits; objects nobody asks about cost nothing). The fetch walk still covers every listed owner once per generation while ts3fixes fetches each script tick, so RefreshTicks=1 saves only the native-only pumps and the bits 1-4 work for asked-but-unlisted objects; RefreshTicks=4 cuts both to ~1/4 (~1-1.5 ms/s at the §6 numbers) plus forced generations. Risk with RefreshTicks > 1: rows up to 3 pumps old (a Sim walking into view keeps "unseen" that long; an address reused by a new object keeps the old row until the next generation).

**Queue (`unseen-table.json`, asi `game-copy/ts3native.asi.utblazy1`):** `bp-utb-ttl-check` (RefreshTicks=4, Check=1, unseen-snapback repro; stop at unsafe >= 0.1%) then `bp-utb-ttl-ab` (`--ab unseentablettl`, table on in both arms). The queue smoke checks the `unseentable` switch only; `bp-utb-ttl-ab`'s early gate checks `utbTtlMode` read-back.

## 9. Zero-copy script reads (2026-11-02, ts3native 0.36.159 + ts3fixes 750-757, branch `feature/utb-lazy`, not built)

**Why [measured, bp-utb-cost, table + caches on, vs the morning probe]:** engine calls collapsed (IsObjectOnScreen 303->7/s, UfCamAsk 1379->219/s, refresh 1373->37/s) and `far` fell 3.0->2.1 ms/s, but the script total stayed ~11.7 ms/s because `ufwhy` rose 4.3->5.2 ms/s (31.7->37.7 us/call). [inferred] The per-tick `Fetch` (utbFetchRows 889k / 3822 fetches = ~233 rows) is paid by the first asker, mostly `UfWhy`: the native walk computed a row and bits 1-4 for every listed owner, then the copy into two MonoArrays, then a generic `Array.BinarySearch<ulong>` per ask.

**Change:**
- Native (unseentable.cpp): the walk, once per generation, only lists the owners with their object ids (sorted, duplicates dropped); no row is computed in it. New IC `UnseenTableNative::Bits(ulong id)` (registered at the first pump with Fetch and PosUnseen; ulong arrives as DWORD lo, hi as `AsIcAddRow`) binary-searches that list and computes the row + bits 1-4 at the first ask of that object in the generation; -1 = no table (mode 0, another thread), -2 = not listed, no transform or a fault (`UTB_BAD`). `Fetch` keeps its output (every listed row, bits 1-4 computed for each).
- Script (UnseenTable.cs): default path = one `Bits` call per ask, no per-tick copy. Switch `unseentablecopy` (default off) = the §6 copy path, the A/B baseline; an older ts3native without `Bits` (MissingMethodException, caught in a NoInlining wrapper) also falls back to the copy path for the load. Counter 744 now counts ticks with at least one answer from the table on the lookup path.
- Probe sites (unseencost, counters 750-757, ts3agent `unseencost_utfetch_*`, `unseencost_utlookup_*`, unseen-cost-report.ts): `utfetch` = the copy path's Fetch, `utlookup` = one row read (Bits IC or the binary search). Both are nested in the Sim checks, so they never add to the top-level total.
- `PosUnseen(float[])` already reuses one static float[4] (no allocation per call), so it stays. [inferred] `pos`'s 7.9 us/call is mostly the nested `FarFromViewers` (the `far` site) that runs before the IC, not the IC.

**Expected cost [inferred, not measured]:** per ask one IC transition (~0.1-0.2 us on Mono 2.x x86, blittable args) + ~8 compares + one hash find: < 0.5 us; per generation the walk without row work (~233 x 3 reads + qsort, a few us). Rows and bits 1-4 only for the Sims the script asks about (distinct ~ asks / 1.36).

**Risks:** the IC must exist under the exact name (`Ts3Fixes.UnseenTableNative::Bits`); a ts3native that registers a different signature would read garbage (none does). Rows are computed lazily from the Sim's position at its first ask in the generation, as native `UnObjClass` already does (§8). The self-test `TestUnseenTable` covers Bits = fetch bits, one row per asked Sim, not listed / broken list = -2, mode 0 / other thread = -1.

**Measure:** build `utblazy2`, rerun `bp-utb-cost`, then `--ab unseentablecopy`: expect `ufwhy` back below 4.3 ms/s and `utlookup` mean < 0.5 us.
