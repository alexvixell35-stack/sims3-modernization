# 146. ErrorTrap's load-up scan: one long frame per load, spread over ticks

Written 2026-10-21, static and build work only (no game run). Follows the ErrorTrap row of docs/145 §6 with the same pattern as the Overwatch work: upstream patch + ts3fixes hotfix that yields to it.

Tags: **[measured]** = counted from existing captures with a tool; **[verified]** = read in the source, IL or our build; **[inferred]** = reasoning, still to confirm in game.

Sources: NRaas source at github.com/Chain-Reaction/NRaas (master `2f57186`, read 2026-10-21 through the GitHub API; the repo has no license file, so nothing of it is copied here, behaviour is described in our own words); the installed `NRaas_ErrorTrap.package` (`NRaasErrorTrap.dll` extracted read-only to the session scratchpad with `tools/extract-assemblies.ts`, IL compared with master for every path used below: same calls, `VersionStamp.sVersion` 100, no marker); the 10 `/taskstall` captures in `probe-out/*-taskstall.jsonl` (2026-10-13 to 10-19); `tools/harness/worst-hitches.ts --min 300 --json` over 213 runs and the 139 `*-managed.jsonl` sample files in `probe-out/profile`.

## 1. Answer

- ErrorTrap starts its load-up checks one sim-minute after every world load (an alarm task from `OnWorldLoadFinished`), not once a minute [verified]. Everything up to the first sleep of its dereference scan runs in **one FunctionTask tick: 497-897 ms, median 774 ms, in 7 of the 7 `/taskstall` captures that covered a load** [measured]. 12 of the 16 managed samples in those stalls are in the inventory check's per-object name lookup (`Inventories.GetName` -> `GetLocalizedName` -> catalog name / string table), 4 in the dereference controllers' first pass (`PreProcess`) [measured].
- The name only feeds ErrorTrap's log text, and the per-inventory check returns at once for an object without an inventory, so nearly every lookup is wasted [verified]. It is the same shared NRaas helper as Overwatch's nightly cleanup (docs/145 §2.2): the first commit of the posted Overwatch PR (Chain-Reaction/NRaas#45) already removes these lookups for every NRaas mod once rebuilt.
- docs/145 §6 sized this from `worst-hitches.ts` (29 iterations, 52.3 s). Re-checked: 35 iterations / 58.0 s have an ErrorTrap sample, but **34 of them are in probe runs** (`snap-*`, `value-probe`) where every frame is slow; in the 157 normal runs only one >= 300 ms iteration (419 ms) has one [measured]. The managed sampler only runs from minute 1, so it misses the load-up tick; the real cost is the one stall per load above.
- The rest of the scan (dereference `Perform` pass) is already sliced: it sleeps inside `ObjectLookup.BuildReference` [verified] and shows as a median of 3 samples (~0.6 s of sim-thread time over minutes 1-4) per normal run, almost never in a long frame [measured].
- Built:
  1. **Upstream patch** for NRaas ErrorTrap (`docs/data/upstream/nraas-errortrap-spread-scan.patch`, 2 commits, `git am` on master clean, compiles with Roslyn C# 7.3 against their `Sims3/Compiler` DLLs): the load-up checks share NRaas's own `SleepWatch` and sleep after the role/recovery/outfit/lot checks, after the inventory check and between controller items once the frame used `kScanSleepInterval` ms (new tunable `ErrorTrapTuning7`, default 10, 0 = old one-frame run); marker `NRaas.ErrorTrap.SpreadScanVersion = 1`. PR text in §5. Not sent: fork, push and PR are Rodney's call.
  2. **ts3fixes hotfix** `errortrapspread` (off by default, `ErrorTrapSpread.cs`): takes over ErrorTrap's copy of the inventory check, skips the name lookup for objects without an inventory, runs ErrorTrap's own per-inventory check for the rest with the same names, at most 2 ms per tick, with a tick break before and after; stands down on the marker.

## 2. What runs in the one tick, and why it is slow

`OnDelayedWorldLoadFinished` (ErrorTrap's alarm task, in order) [verified in master and the installed IL]:

| # | Step | Sleeps? | Cost driver |
|---|---|---|---|
| 1 | every `ICheck.Finish`, role manager check, `CheckRecovery`, `CheckOutfits`, `CheckLotObjects` | no | per household / lot object; not on a stall sample [measured: 0 of 16] |
| 2 | `Inventories.CheckInventories` (shared NRaas helper, ErrorTrap's own copy) | no | **one localized name per world object** before the inventory test; 12 of 16 samples [measured] |
| 3 | every dereference controller: `Clear`, `Add` the reference lists collected at load (`ObjectLookup.List`, filled by the game's load callbacks `OnLoadReference`/`OnLoadArrayReference`), `PreProcess` | no | per collected reference: a wrapper, two dictionary lookups (post-disposed set, per-pass set) and an (almost always empty) callback; 4 of 16 samples [measured] |
| 4 | `ObjectLookup.LogCounts` | no | returns at once unless ErrorTrap debugging is on [verified] |
| 5 | every controller's `Perform` (event listeners, game objects, Sim descriptions, delegates, ...) | yes, inside `BuildReference` (every 10,000 references scanned and once per lookup) | `BuildReference` is a linear scan of all references of the object's type per looked-up object; `DereferenceManager.Perform` walks the reference chain through ~650 dereference performers per step |
| 6 | `PostPerform`, delete queued objects (`ProcessToBeDeleted` sleeps per object), `ObjectLookup.Clear` | partly | small |

The stall ends at the first sleep in step 5, so its length is steps 1-4 plus the start of 5 [measured: the last sample of each stall is in step 3 or 2]. In the Perform pass, 95% of ErrorTrap samples are under the event-listener controller (listeners whose target object was destroyed: one `BuildReference` scan each, then a check walk and, when found, the removal walk) [measured: 891 of 938 samples over all runs].

Same results under spreading: the scan already lets other tasks run between and inside controller items (step 5 sleeps), the post-disposed set exists for exactly that, and the reference lists are only filled during loading [verified]. Extra sleep points in steps 1-3 add more of the same kind; each check still does the same work in the same order [inferred].

## 3. ts3fixes `errortrapspread` (built, not run)

- **Files:** `mods/ts3fixes/src/ErrorTrapSpread.cs` (hook, pass, trigger), `ErrorTrapSpreadCore.cs` (marker name, slice rule; the mode rule is `OverwatchSpreadCore.Decide`), switch wiring in `Fixes.cs`/`Instantiator.cs` (tuning `kErrorTrapSpread`, default **off**), agent `patches` keys `etspread_*` and `errortrapspread_log`.
- **Hook:** `NRaas.CommonSpace.Helpers.Inventories.CheckInventories(Logger, Logger, bool)` in the `NRaasErrorTrap` assembly swapped (same mechanism as `overwatchstuck`), applied at startup or at world load (the check runs one sim-minute after load, so it is not compiled yet). Only ErrorTrap's load-up task calls it [verified: code search]. Before patching it checks the method, `CheckInventory(Logger, Logger, string, Inventory, bool)` and the private `GetName(IGameObject)`; anything missing = log `errortrapspread: ErrorTrap's CheckInventories differs from the known build, off`. No ErrorTrap = `ErrorTrap not loaded, off`. The two `Logger` delegates are taken as `object` in our hook and stub (reference types) [inferred safe in mint; the repro checks it].
- **On:** one tick break first (steps 1 above stay in their tick), then the world object list and a copy of the household list as ErrorTrap would read them; per object: skipped if destroyed while we slept, skipped without a name lookup if it has no inventory, else ErrorTrap's own `CheckInventory` with ErrorTrap's own `GetName` (so its log text is unchanged); then each household's family and fridge inventory with ErrorTrap's names. At most 2 ms per tick (`Simulator.Sleep(0)` when the context may yield), the world shutting down ends the pass, a tick break at the end so the controllers start in a fresh tick. One log line per pass. An exception = log line, counter [176], ErrorTrap's own check runs.
- **Off** (switch off or faked marker): ErrorTrap's own check runs unchanged.
- **Hand-off rule (marker contract):** an ErrorTrap build that spreads the scan itself carries `public static readonly int SpreadScanVersion` on `NRaas.ErrorTrap`. We only check that the field exists at apply time (reading it would run ErrorTrap's static constructor, which boots the mod, early; the selftest checks the constructor does not run), then patch nothing; at world load we read the value and log `errortrapspread: upstream spreading found (vN), ours off` (counter [173] = N). `errortrapspreadfakeupstream` (test switch) behaves as if it were present (counter [173] = -1, same line with "(faked)").
- **Verify:** `errortrapspreadverify` wraps ErrorTrap's two log delegates with counting ones that pass every line on, for either path; counter [175] = corrections logged by the last pass. Running ours, then theirs, theirs must log 0 (and the other order on a fresh load).
- **Counters** (registry, `int[197]` since the docs/147 merge): [167] our passes, [168] inventories checked, [169] ticks used, [170] longest tick µs, [171] name lookups skipped, [172] objects destroyed while we slept, [173] upstream marker, [174] checks left to ErrorTrap's path, [175] corrections logged by the last verified pass, [176] our passes that failed.
- **Trigger:** `Ts3Fixes.Fixes.ErrorTrapSpreadNow(path)` through the agent's `reflect_call`: `"ours"`, `"theirs"` or `"hook"` (what the switches pick) runs the inventory check now in its own task with ErrorTrap's own log methods. The other load-up steps cannot be repeated in one load (the reference lists are cleared after the scan), so the load-time effect needs a fresh load.
- **Repros** (`tools/harness/repros.json`): `bp-errortrap-spread-inventory` (ours then theirs with verify), `bp-errortrap-spread-upstream-yield` (faked marker).
- **Queue** (`tools/harness/queues/errortrap-spread.json`, not run): repro under `/taskstall` with the switch on for the whole run, the yield repro, then a two-load A/B (`bp-errortrap-load-off` / `-on`, fresh load, `/taskstall 250`, seeded BP, 5 min each). **Pass rule:** off load shows the 0.5-0.9 s ErrorTrap row with `Inventories.GetName` samples; on load has no row with `GetName`/`CheckInventories` samples, `etspread_passes` 1, longest tick < ~5 ms, ErrorTrap's remaining first tick (controllers' `PreProcess`) reported with its length (~0.15-0.3 s expected [inferred]). [inferred: the switch set by `--fixes-on` while paused lands before the alarm fires, as the 7 earlier captures caught the stall inside the window; the off job's `etspread_theirs` 1 and the on job's `etspread_passes` 1 confirm it.]
- **Selftest:** `node tools/selftest/errortrapspread/run.ts` (marker found / absent / Overwatch's name / wrong type / private, static constructor not run, yield rule, slices): PASS; `tools/selftest/overwatchspread` still PASS after `HasMarker(Type, string)` was split out. `node tools/setup/build-fixes.ts` and `node tools/harness/build-agent.ts`: built.

Not built, on purpose: walk-level sleeps inside `DereferenceManager.Perform` (no long frame measured there in normal runs), and a reverse reference index for `BuildReference` (would cut the scan's total work, not a hitch; a bigger change to their code).

### 3.1 Budget in inventory items (2026-10-21, docs/147 rule)

Rule from Rodney: a slice's budget counts the work items inside an object. Here the per-object work is **ErrorTrap's own** `CheckInventory` (called by reflection, behaviour not reimplemented: their log text and `checkDestroyed` handling stay theirs), so ours cannot resume inside an inventory without rewriting their check. What changed: the pass budget now counts the **items** of the inventories checked in a tick (128 items or 2 ms; an inventory counts at least 1), so a tick no longer stacks several big inventories; a single inventory bigger than the budget still runs whole in one tick. Counters [211] `etspread_largest_inventory_items` (largest inventory run whole, items) and [212] `etspread_max_items_tick`; [170] stays the longest tick. If [211] shows inventories of hundreds of items in real saves, splitting inside `CheckInventory` is **upstream work** (a sleep every N items inside their per-inventory loop, next to commit 2 of §4) [inferred need: no big inventory seen on BP]. Selftest `tools/selftest/errortrapspread`: budget by items or time, and a pass model with a 900-item and a 200-item inventory among small ones (each big one ends a tick; a tick goes over the budget only by its last inventory).

## 4. Upstream patch (NRaas ErrorTrap)

`docs/data/upstream/nraas-errortrap-spread-scan.patch`, `git am`-able on Chain-Reaction/NRaas master `2f57186` (checked on a sparse clone: both commits apply cleanly, the tree equals the authored branch; the `NRaasErrorTrap` project compiles with it, Roslyn C# 7.3 against their `Sims3/Compiler` DLLs, as does the unpatched master). It follows their style: their own `SleepWatch` helper (added to the project's linked helpers, as StoryProgression does), a new `ErrorTrapTuningN` class with its `XML/ErrorTrapTuning7.xml` and csproj `Content` line like `ErrorTrapTuning5`/`6`, the tunable listed in the Version prompt, LF line endings and BOM as in their files. Two commits:

1. `Load-up checks: spread the delayed load-up scan over several frames`: `DereferenceManager.StartScanSleep/ScanSleep/StopScanSleep` around `OnDelayedWorldLoadFinished`; sleeps after the role/recovery/outfit/lot checks, after `CheckInventories` and between controller items (inside the existing per-item `try`, after the world-quit check), each only when `kScanSleepInterval` ms of the frame are used. `kScanSleepInterval = 0` = old behaviour.
2. `Add ErrorTrap.SpreadScanVersion marker`: `public static readonly int SpreadScanVersion = 1` on `NRaas.ErrorTrap`.

With only this patch the inventory check itself still runs in one go (~three quarters of today's stall); PR #45's first commit removes its name lookups. Both PRs together leave no measured one-tick step [inferred].

## 5. PR description (posted 2026-10-21 as https://github.com/Chain-Reaction/NRaas/pull/46, two commits; in-game before/after to add as a PR comment)

> **ErrorTrap: spread the load-up checks over several frames**
>
> On a heavily modded game (all EPs/SPs, lots of store content) ErrorTrap's delayed load-up checks cause one long frame a sim-minute after every load: 0.5-0.9 s in 7 of 7 measured loads. About three quarters of it is `CheckInventories` looking up the localized name of every object in the world for its log text (fixed in the shared helper by #45), the rest is the dereference controllers' first pass over every reference collected at load. Both run before the scan's first sleep.
>
> - The load-up checks now use the existing `SleepWatch`: they sleep after the role/recovery/outfit/lot checks, after the inventory check and between controller items, once they have used `kScanSleepInterval` ms of the frame (new tunable `ErrorTrapTuning7`, default 10). The scan already sleeps inside `BuildReference`, so this only adds more of the same sleep points; every check does the same work in the same order. Set it to 0 for the old one-frame run.
> - `ErrorTrap.SpreadScanVersion`: a public marker so performance mods that spread this scan themselves (ours: ts3fixes `errortrapspread`) can see that ErrorTrap does it and stay out of the way. Bump it if this behaviour changes.
>
> Tested: builds against the repo's DLLs. In game: <fill in after the A/B: load-up stall before/after, same corrections logged>.
>
> 🤖 Assisted with AI together with @rodneypantonial

### 5.1 v2: spread by work, behaviour proof (2026-10-21, static + offline, not pushed)

`docs/data/upstream/nraas-errortrap-spread-scan-v2.patch`: 1 new commit on the PR head `e4343e6` (`git am` clean, tree equal to the authored branch; `NRaasErrorTrap` compiles, Roslyn C# 7.3 against their DLLs). `Load-up checks: spread the inventory check over frames too`:

- The inventory check (about three quarters of the stall, §2) used to run in one go even with v1. It now calls the shared helper's time-based overload with `kScanSleepInterval` (sleeps before each inventory and each item) and skips the name lookup for objects without an inventory. `NRaas/Helpers/Inventories.cs` is **byte-identical** to the one in PR #45 v2 (docs/145 §5.1): test merges of both v2 branches onto master, in both orders, are clean and give the same tree.
- A world quit during the new early sleeps: v1 would have gone on checking a quitting world and then reset `DereferenceManager.Logger.OnWorldQuit` before the dereference pass. v2 checks ErrorTrap's own `Loading` flag (set by its `OnWorldQuit`, cleared at load finish [verified]) before the inventory check and before each controller's first pass and skips to the end, as a quit during the dereference pass already does (that path also runs `ProcessToBeDeleted`, `ObjectLookup.Clear`, `RecordErrors`).
- Between controller items the sleep now comes before the world-quit check, so a quit during it stops before the next item (v1 processed one more item first).

**Per changed loop** (beyond docs/145 §5.1's table for the shared inventory check, which applies unchanged):

| Loop | Can change while we sleep | v2 handling | Why the result equals the unpatched run |
|---|---|---|---|
| Steps 1 -> 2 -> 3 (checks, inventory check, controllers' first pass) | world quit; objects destroyed | quit: skip to the end (above); destroyed objects: the inventory check as in docs/145; the controllers already skip objects disposed after load (`ObjectLookup.WasPostDisposed`) | the reference lists are only filled while loading; the scan already sleeps inside step 5 with the same rules |
| Controller items (`DereferenceController.Perform`, used by `PreProcess` and `Perform`) | world quit | sleep, then the quit check | same items in the same order; `performed` is local to the pass |

Unchanged on purpose, reported upstream as a separate finding: `DereferenceManager.Logger.OnWorldQuit` stays true after a world quit until the reset just before the dereference pass, so on the second world loaded in one session each controller's `PreProcess` returns after the first item of each list [verified by reading; effect inferred]. v1 and v2 keep that behaviour exactly.

Offline proof: the shared helper check of docs/145 §5.1 (`tools/selftest/nraas-upstream/run.ts`) passes on this branch too. The ErrorTrap-only changes (quit flag, sleep order) are reviewed, not simulated. Local test package and queue: docs/145 §5.1 (`nraas-upstream-base` / `nraas-upstream-v2`, queue `nraas-upstream-verify`: same ErrorTrap correction count after the load-up scan, `errortrapspreadverify` residual 0 on both loads).

**PR comment draft (#46):**

> One more commit: the inventory check now sleeps too. It uses the shared helper's time-based overload from #45 with `kScanSleepInterval`, so it sleeps between objects and between items of large inventories (it was ~three quarters of the stall). `NRaas/Helpers/Inventories.cs` is the same file as in #45, so the two merge cleanly in either order. With sleeps before the dereference pass, a world quit during them now skips to the end like a quit during the dereference pass, and between controller items the sleep comes before the quit check.
>
> Unrelated to this PR, noticed while reading: `DereferenceManager.Logger.OnWorldQuit` is set on world quit and only reset right before the dereference pass, so on the second world loaded in a session it is still true during `PreProcess`, which then stops after the first item of each list. I left that as it is.
>
> Checked offline: master's and the patched `CheckInventories` run side by side on 400 generated towns give the same log lines, order and final state, also when sleeping before every item and when the game changes inventories meanwhile. In-game before/after still to come.
>
> 🤖 Assisted with AI together with @rodneypantonial

## 6. Open risks

- Not run in game. First run: the repro under `/taskstall` before the load A/B (memory rule: correctness first).
- The hook takes ErrorTrap's `Logger` delegates as `object` (foreign reference type in a swapped header): built, not exercised; a wrong call would fail at the first load-up check, logged by ErrorTrap's own exception handler [inferred works].
- `MethodInfo.Invoke` per inventory and `DynamicInvoke` in verify mode in Mono 1.2.3: slower than a direct call, but only for objects with an inventory [inferred cost < 1 ms per tick slice].
- Objects destroyed while our pass sleeps are skipped; ErrorTrap's one-tick check would have checked them first. Their inventory goes with them, so nothing stays broken [inferred].
- A save during our pass sees some inventories checked and some not, as during ErrorTrap's own sliced `Perform` pass today [inferred].
- Upstream: `kScanSleepInterval` comes from the tuning XML only for builds that ship `ErrorTrapTuning7.xml`; without it the field default (10) applies [inferred from how the game applies tunables].

## 7. Ledger

docs/51 §210: I-146-1 .. I-146-5.

## 8. 2026-11-20: recheck on v0.3.0 + Apex + bridge (ON since v0.1.5)

Queue `spread-recheck-1120`, job `bp-errortrap-ab-1120` (repro `hitch-errortrap-check`, 8 firings ABBA of the load-up inventory check, rc-1119b = rel030, Baseline-BP-Spring, Bin state apex-rewind): PASS, 0 script errors, `etspread_passes` / `etspread_theirs` 4 each, `etspread_failed` 0, per-firing read-back `Theirs` / `Ours`.

| Arm | Firings | Frames >= 300 ms | Longest frame | Longest ErrorTrap step |
|---|---|---|---|---|
| off (ErrorTrap's one tick) | 4 | 4 | 860 ms | 799 ms |
| on (`errortrapspread`) | 4 | 0 | 97 ms | 10 ms |

[measured] The inventory part of the load-up stall is still 0.7-0.8 s per run on this build (with `kErrorTrapLookup` on), and the spread removes it. The dereference pass (the other quarter) cannot be re-run in one load and was not part of this check. Upstream comment text for PR #46: drafted for Rodney (not posted).
