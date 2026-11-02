# 145. Staggered cleanups: NRaas Overwatch's nightly alarm and other one-tick maintenance work

Written 2026-10-21, static and build work only (no game run). Follows docs/96 §4.1 (class B) and §6 fixes B1/B2, which were never built.

Tags: **[measured]** = counted from existing captures with a tool; **[verified]** = read in the source, IL or our build; **[inferred]** = reasoning, still to confirm in game.

Sources: NRaas source at github.com/Chain-Reaction/NRaas (master, read 2026-10-21 through the GitHub API; the repo has no license file, so nothing of it is copied here, behaviour is described in our own words); the installed `NRaas_Overwatch.package` (V123, `NRaasOverwatch.dll` extracted read-only to the session scratchpad with `tools/extract-assemblies.ts` and checked against the GitHub master for every path used below: same behaviour, master is V124); the game's script DLLs (`game-copy/decompiled`); every profiler capture since 2026-10-16 through `tools/harness/worst-hitches.ts --min 300 --json` (211 runs, 1,924 profiled minutes).

## 1. Answer

- Overwatch's whole nightly alarm (`NRaas.Overwatch.OnTimer`) runs **22 cleanup types in one FunctionTask tick**, once per sim-day at the alarm hour (default 3:00) [verified]. Over all captures, class "TaskObjects burst" is 675 iterations >= 300 ms, 347.8 s, 12.7% of game-only >= 300 ms time, 21.1 per profiled hour; **75 of the 101** bursts with managed samples have Overwatch's `CleanupInventory` on a sample [measured]. docs/96 (55 runs) had 207 iterations, 104 s, 23%.
- The cost is one line of bookkeeping: the inventory cleanup asks for the **localized catalog name of every object in the world** before checking whether the object has an inventory at all; the name only feeds its log text [verified]. With heavy store content every lookup walks many string tables (`STBL_Find 0x59AF50`, docs/54) [measured in docs/96].
- Built:
  1. **Upstream patch** for NRaas (`docs/data/upstream/nraas-overwatch-staggered-cleanup.patch`, 3 commits, compiles against their project): skip the name lookup for objects without an inventory; the nightly inventory run sleeps every 25 inventories; each cleanup type at its own time (`Set Alarm Spacing`, default 10 sim-minutes, 0 = old behaviour); a public marker `NRaas.Overwatch.StaggeredCleanupVersion`. PR text in §5. Not sent: forking, pushing and opening the PR are Rodney's call.
  2. **ts3fixes hotfix** `overwatchspread` (off by default, `OverwatchSpread.cs`): the same staggering from outside, our own inventory pass (written from the behaviour spec in §2.2), and a hand-off rule: when the installed Overwatch carries the marker, ours does nothing.
- Other one-tick maintenance work, ranked in §6: EA's object spawners, ErrorTrap's load-time dereference scan, EA's nightly lot cleanup (`LotManager.CleanUpLotsAndInventories` + the stuck-check helpers it starts), the pet pool alarm, NPC relationship decay.

## 2. Overwatch inventory

### 2.1 Every cleanup the nightly alarm runs

`OnTimer` collects every type implementing `IAlarmOption` in the Overwatch module (their `DerivativeSearch.Find`, type order), then calls `PerformAlarm` on each in turn inside the alarm's own FunctionTask; each option returns at once when its setting is off. Notices raised during the loop are collected and shown as one message afterwards [verified]. Each setting is a field of `PersistedSettings` (persisted in the save; in game: Overwatch > Settings), seeded from a tunable `k*` value that only applies to new saves or after Overwatch > Reset (docs/07 §2.2, `tools/setup/build-overwatch-tuning.ts`) [verified].

| # | Option (setting, default) | What it touches | Per-item cost driver | Spread safe? |
|---|---|---|---|---|
| 1 | ResetAllGnomes (`mResetAllGnomes`, on) | every magic gnome: reset | gnome count (few) | yes |
| 2 | DeleteMagicalGnomes (`mDeleteAllMagicGnomes`, off) | destroys magic gnomes and memorials | few | yes |
| 3 | TurnOffStereos (`mTurnOffStereos`, on) | every stereo not near the active household (unless `mAffectActiveLot`): off/reset | stereos | yes; meant for the night, stays in the night with a 10-minute gap |
| 4 | TurnOffTelevisions (`mTurnOffTelevisions`, on) | same for TVs | TVs | yes, as 3 |
| 5 | RecoverMissingSims (`mRecoverMissingSims`, on) | per household: residents without a Sim in the world are instantiated or reset | households × residents | yes |
| 6 | RecoverStrandedToddlers (`mRecoverStrandedToddlers`, on) | toddlers away from home without an adult: sent home | all Sims; already sleeps per toddler | yes |
| 7 | KillAllHomeless (`mKillAllHomeless`, off) | homeless households | households | yes |
| 8 | PurgeGeneticHair (`mPurgeGeneticHair`, off) | genetic hair on every SimDescription | SimDescriptions | yes |
| 9 | CleanupConcert (`mCleanupConcert`, on) | every show venue: reset searchlights etc. | venues | yes |
| 10 | **CleanupInventory** (`mCleanupInventory`, on) | every inventory: drops broken stacks and items, re-parents items, deletes corrupt jam jars | **one localized name lookup per world object** (the measured cost), one catalog-name read per inventory item | yes, if each inventory is finished in one tick and destroyed owners are skipped |
| 11 | CleanupVehicles (`mCleanupVehicles`, on; hourly variant `mCleanupVehiclesHourly`, off) | orphaned cars, boats, bikes, parking | vehicles, parking spaces, residents | yes |
| 12 | CleanupLaundromat (`mCleanupLaundromat`, on) | stuck washers/dryers on every lot: reset | every object on every lot (per-lot object query) [inferred: may be the second most expensive] | yes |
| 13 | CleanupOutfits (`mCleanupSinged`, on) | singed outfits on every SimDescription | SimDescriptions × outfits | yes |
| 14 | CleanupSkillModifiers (`mCleanupSkillModifiers`, on) | broken skill modifiers on every SimDescription | SimDescriptions | yes |
| 15 | CleanupElevators (`mCleanupElevators`, on) | every elevator: assigned Sims and the object reset | elevators | yes |
| 16 | RestartDigitalPhotos (`mRestartDigitalPhotos`, on) | digital frames toggled | frames | yes |
| 17 | ResetCheck (`mResetCheck`, on) | Sims in the same interaction since last night: reset | actors; compares with its own last-night record | yes (its record is per night, not per tick) |
| 18 | StuckCheck (`mStuckCheckV2`, on) | idle NPC Sims on unroutable spots: one route plan each, reset | one plan per Sim | yes; already one plan per tick with `overwatchstuck`; off for new saves with `overwatch-no-stuckcheck` |
| 19 | CleanupRelationships (`mCleanupRelationships`, on) | broken relationships | every relationship (thousands in old saves) [inferred cost] | yes |
| 20 | CleanupSituations (`mCleanupSituations`, off) | broken situations | situations | yes |
| 21 | CleanseTheDead (`mCleanseTheDead`, off) | dead Sims' records, family-level compression | dead SimDescriptions | yes |
| 22 | RunAlarmCommands (console, `mAlarmCommands`, empty) | the player's stored nightly console commands | - | yes |

No option reads another option's output, and the order is only the type search order [verified]; none needs the others to have run in the same tick. What changes when they are spread: each option's notice shows on its own instead of in one batch [verified], and stereos/TVs are turned off up to ~3.5 sim-hours later in the night [verified arithmetic: 21 slots × 10 min].

Other Overwatch periodic work, not in `OnTimer` [verified]: CleanupVehicles' hourly run (off by default), the aging check every 4 sim-hours (every SimDescription's aging alarms), the skill-gain check every sim-hour (actors × skills), the instantiation check (daily), the lunar-cycle handler (daily), and about 60 load-up options at world load (class C, docs/96 §4.2). None of them shows in the class B samples [measured: 0 of the 101 sampled bursts]; they are left alone.

### 2.2 What the inventory cleanup does (behaviour spec, our words)

For every object in the world that has an inventory, then for each household's shared family and fridge inventory: in each stack, a missing stack or item list marks the stack for removal; going through the items from the last: an item without an object is dropped; an item whose object has no item component is dropped unless the inventory belongs to a household inventory object; an item whose object was destroyed is dropped; otherwise an item without a parent inventory gets the owner's inventory as its parent, and a jam jar whose catalog name cannot be read is disposed, destroyed and dropped (the name read is attempted on every item, but only a jam jar's failure has an effect). A stack left empty is removed. An exception aborts that inventory only [verified in the V123 IL and GitHub master].

So two lookups have no effect on the result: the name per world object (log text) and the catalog-name read on items that are not jam jars. Both the upstream patch (first one) and our pass (both) drop them.

## 3. ts3fixes `overwatchspread` (built, not run)

- **Files:** `mods/ts3fixes/src/OverwatchSpread.cs` (hook, our inventory pass, trigger), `OverwatchSpreadCore.cs` (game-independent decisions), switch wiring in `Fixes.cs`/`Instantiator.cs` (tuning `kOverwatchSpread`, default **off**), agent `patches` keys `owspread_*` and `overwatchspread_log`.
- **Hook:** `NRaas.Overwatch.OnTimer` swapped (same mechanism as `overwatchstuck`), applied at startup or at world load. Before patching it checks: Overwatch loaded, `OnTimer()` static void, the option search `Find<IAlarmOption>()`, `IAlarmOption.PerformAlarm()`, the `Settings` property and `mCleanupInventory` bool; anything missing = log `overwatchspread: Overwatch.OnTimer or its option list differs from V123, off`, nothing patched. No Overwatch = `Overwatch not loaded, off`.
- **On:** the alarm takes Overwatch's own option list and gives each type its own slot `i × 10` sim-minutes after the alarm (`AlarmManager` one-shot alarms, never persisted), each slot in its own `OneShotFunctionTask`. Every type except the inventory cleanup runs Overwatch's own `PerformAlarm` there: same code, same setting check, later time. The inventory cleanup is our pass (§2.2): it reads the player's `mCleanupInventory` and does nothing when it is off; it never writes Overwatch's settings. It collects the inventories first, then checks at most 25 inventories or 2 ms per tick, then `Simulator.Sleep(0)`; owners destroyed meanwhile are skipped; the world shutting down ends the pass. One log line per pass with the repairs per kind.
- **Off** (switch off, faked marker): Overwatch's own `OnTimer` runs unchanged.
- **Hand-off rule (marker contract):** an Overwatch build that spreads the cleanups itself carries `public static readonly int StaggeredCleanupVersion` on the public type `NRaas.Overwatch`. We only check that the field exists at apply time (reading it would run Overwatch's static constructor early), then patch nothing; at world load we read the value and log `overwatchspread: upstream spreading found (vN), ours off` (counter [165] = N). The player's Overwatch settings are untouched in every case. `overwatchspreadfakeupstream` (test switch) makes the hook behave as if the marker were present (counter [165] = -1, same log line with "(faked)").
- **Verify:** `overwatchspreadverify` counts, with our §2.2 rules and no changes, what is left to repair before and after a pass of either path (Overwatch's own or ours). Both paths should end at 0; the first pass's "before" equals its repairs. Limit: the count uses our reading of their rules, so it shows that the two paths agree with that reading, not an independent count of Overwatch's own repairs (its log lines go to its message logger only) [inferred equivalence, from the spec].
- **Counters** (registry, `int[167]`): [156] alarms taken over, [157] types scheduled, [158] Overwatch types run at their slot, [159] our inventory passes, [160] inventories checked, [161] repairs, [162] ticks, [163] longest tick µs, [164] pending after the last verified pass, [165] upstream marker, [166] alarms left to Overwatch's path.
- **Trigger:** `Ts3Fixes.Fixes.OverwatchSpreadNow(type, gapMinutes)` through the agent's `reflect_call`: `"all"` runs the nightly alarm now through the path the switches pick; a type name (`"CleanupInventory"`, `"StuckCheck"`, ...) runs only that type now (ours or Overwatch's).
- **Repros** (`tools/harness/repros.json`): `bp-overwatch-spread-inventory` (both paths with verify), `bp-overwatch-spread-all` (22 types one sim-minute apart), `bp-overwatch-spread-upstream-yield` (faked marker).
- **Queue** (`tools/harness/queues/overwatch-spread.json`, not run): repro under `/taskstall`, the yield repro, then the seeded BP 10-minute in-load A/B `--ab overwatchspread`, 2-minute slices. **Pass rule:** `worst-hitches.ts` class "TaskObjects burst" 3-4 per run in the off slices -> 0-1 in the on slices, no NRaas `CleanupInventory` on a >= 300 ms managed sample in on slices, verify "after 0" on both paths, no `failed` line.
- **Selftest:** `node tools/selftest/overwatchspread/run.ts` (marker found / absent / wrong type / private, the yield path wins over the switch, slots, slices): PASS. `node tools/setup/build-fixes.ts` and `node tools/harness/build-agent.ts`: built.

Not built, on purpose: a catalog-name cache (docs/96 B2). With the lookups gone there is nothing left to cache in this path; B2 stays open for the product-list cases of class D.

### 3.1 Budget in inventory items (2026-10-21, docs/147 rule)

Rule from Rodney: a slice's budget counts the work items inside an object, so one heavy object is split across ticks. Our inventory pass now counts **inventory items**, not inventories: each inventory's work is built at its turn as one unit per item (Overwatch's order, last to first, per stack), then one unit for the stack clean-up of the stacks seen at the turn; a tick ends after `kMaxItemsPerSlice` = 128 items or 2 ms and the next tick resumes inside the same inventory (`EaSpreadCore.AddItemUnits` + `DailyPairPass`, shared with `easpread`). Each unit finds its item again in the stack (at its old index, else by a search), so items the game takes or adds while we sleep are handled: a taken item is not checked, an added one waits for the next night (Overwatch reads the stack at its turn too, in one tick). An exception leaves the rest of that inventory as it is, as Overwatch does; an owner destroyed while we sleep ends its inventory. Repairs per item are the same function as before (`OwCheckItem`, also used by the verify census), so the verify census still compares the two paths. Counter [210] `owspread_max_items_tick` (most items in one tick); [163] stays the longest tick. Expected: no tick over ~2-3 ms even for a fridge or family inventory with hundreds of items [inferred]; the measured ticks before were already short (no big inventories on BP), so this guards the heavy-save case. Selftest (`tools/selftest/overwatchspread`): a 700-item inventory in 3 stacks plus two small ones at 128 items per slice: >= 5 slices, result equal to the one-shot check (same items and stacks left, same order); with the game taking 10 items and adding 5 mid-split, every remaining item checked once, taken ones never, added ones not at all.

**Overwatch's other cleanup types (run whole at their slot, Overwatch's own code):** none of them shows on a >= 300 ms managed sample (docs/145 §6), but by their per-object shape (the cost drivers in the §2.1 table, read in their source on 2026-10-21; not re-read here) these iterate a big list in one go and are upstream work (a sleep per N items inside their loop, like commit 2 of the patch), not ours to slice: `CleanupLaundromat` (every object on every lot), `CleanupRelationships` (every relationship), `CleanupOutfits` and `CleanupSkillModifiers` (every SimDescription x outfits / skills), `RecoverMissingSims` (households x residents), `ResetCheck` (every actor). `StuckCheck` is already one plan per tick with `overwatchstuck`. To add to the upstream PR as a follow-up once the A/B shows any of them in a long frame.

## 4. Upstream patch (NRaas Overwatch)

v1 (this section; since 2026-10-23 the patch file holds v3, §5.2, and v1 is in its git history): `git am`-able on Chain-Reaction/NRaas master (checked: applies cleanly on the fetched files; the repo's `NRaasOverwatch` project compiles with it, Roslyn C# 7.3 against their `Sims3/Compiler` DLLs, as does the unpatched master). It follows their style (option classes, `k*`/`m*` settings pairs, `Common.AlarmTask`, LF line endings, BOM where their files have one, UTF-16 string table). Three commits:

1. `NRaas/Helpers/Inventories.cs`: `CheckInventories` skips objects without an inventory before looking up their name (3 lines). Same objects checked, same repairs. This file is shared by all NRaas mods; ErrorTrap's load-time scan calls the same function and gets the same saving once rebuilt [verified: ErrorTrap's delayed world-load step calls it].
2. A `CheckInventories` overload with `yieldEvery`; the nightly `CleanupInventory` sleeps every 25 inventories (the menu action still runs at once). Destroyed objects are skipped and the household list copied when sleeping. The old overload keeps its behaviour for the other mods.
3. `OnTimer` schedules each option as its own `AlarmTask`, `mAlarmSpacing` sim-minutes apart (new integer setting `Set Alarm Spacing`, tunable `kAlarmSpacing = 10`, 0 = all at once as before; English strings added); `public static readonly int StaggeredCleanupVersion = 1` on `NRaas.Overwatch`.

Open on their side [inferred]: whether an existing save's `PersistedSettings` gets 10 or 0 for the new field (if the save's object keeps 0, old saves keep the old behaviour until Overwatch > Reset); whether `AlarmManager` fires a 0-minute alarm on the next tick (slot 0). Neither changes results.

The ts3fixes side reads only the marker, so it works with any later version number. Our local game test of the patched Overwatch needs a repacked `NRaas_Overwatch.package` (S3SA swap plus the string table); not built here (~1 h), the queue tests the ts3fixes path.

## 5. PR description (posted 2026-10-21 as https://github.com/Chain-Reaction/NRaas/pull/45, three commits; in-game A/B numbers to add as a PR comment)

> **Overwatch: spread the nightly cleanups over time**
>
> On a heavily modded game (all EPs/SPs, lots of store content) Overwatch's nightly alarm causes the worst regular hitch we measure: one ~0.4-0.5 s frame per sim-day. Over 211 profiled 10-minute runs it was 12-23% of all frames slower than 300 ms; 75 of 101 sampled stalls are in Cleanup Inventory, almost all of it in `GetLocalizedName` for the log text of every object in the world (each lookup walks many string tables).
>
> - Cleanup Inventory: skip the name lookup for objects without an inventory (same results), and the nightly run sleeps every 25 inventories.
> - Nightly alarm: new setting "Set Alarm Spacing" (default 10 sim-minutes) gives each cleanup its own time, so the costs no longer add up in one frame. Set it to 0 for the old all-at-once run. Every cleanup keeps its own on/off setting and does the same work; notices then show one by one.
> - `Overwatch.StaggeredCleanupVersion`: a public marker so performance mods that spread these cleanups themselves (ours: ts3fixes `overwatchspread`) can see that Overwatch does it and stay out of the way. Bump it if this behaviour changes.
>
> Tested: builds against the repo's DLLs. In game: <fill in after the A/B: stall count per 10 minutes before/after, same cleanup results>.
>
> 🤖 Assisted with AI together with @rodneypantonial

### 5.1 v2: spread by work, behaviour proof (2026-10-21, static + offline, not pushed)

`docs/data/upstream/nraas-overwatch-staggered-cleanup-v2.patch`: 2 new commits on the PR head `351665d` (`git am` clean, tree equal to the authored branch; `NRaasOverwatch` compiles, Roslyn C# 7.3 against their `Sims3/Compiler` DLLs). Rodney's rules: spread by work items, same cleanups, same corrections, same order.

1. `Inventory cleanup: sleep by time used, also inside large inventories`: `CheckInventories(log, debugLog, checkDestroyed, sleepAfter)` now takes milliseconds per frame (Overwatch passes 10; v1 counted 25 inventories, so one inventory with hundreds of items, each reading its catalog name, still ran in one frame) and sleeps before each inventory and before each item. Without a timer (0: the menu action, every other NRaas mod) the code path is the old one.
2. `Nightly alarm: run the spaced cleanups one after another`: v1 gave every cleanup an alarm at `i × spacing`; v2 chains them.

**Per changed loop: what a sleep can change and how v2 matches the one-frame run** [verified in their source and the game's `Inventory`/`ScriptCore` code unless marked]:

| Loop | Can change while we sleep | v2 handling | Why the result equals the unpatched run |
|---|---|---|---|
| World objects (`Queries.GetObjects`) | objects created, destroyed, their inventory removed | the array is a snapshot (new objects not visited); after a sleep a destroyed object is skipped; `Inventory` read at its turn | a destroyed object's inventory is emptied by its dispose (`Inventory.Dispose` -> `DestroyItems` -> `mItems.Clear`), so checking it would change nothing; new objects did not exist at the alarm |
| Households | added, removed, moved | list copied (v1 already); each shared inventory read at its turn | a household gone with its shared inventory needs no repair; its shared inventory objects are also visited by the first loop [inferred: they are game objects] |
| Stacks of one inventory (`mItems`) | the game adds, removes (an emptied stack is deleted by `RemoveInternal`), replaces stacks | pairs copied at the inventory's turn; a stack is skipped when its key no longer maps to the same stack object (checked at its start and after each sleep) | a stack gone with its items needs no repair; stacks added later were not there |
| Items of a stack (`ListSorted`) | items taken out or added | items copied; after a sleep each one is found again by reference, skipped when gone; removal at its current index | same rule per item on the item's current state; items added later are not in the copy |
| Empty-stack removal | the game refilled the stack | kept when it maps to a non-empty stack now | the unpatched removal happened before the refill |
| Own change: a corrupt jam jar's destroy empties its stack | - | throws the same way the dictionary loop does without a timer: that inventory's check stops with the exception logged | unpatched: the enumerator is invalidated, same stop, same log path [verified offline, 117 cases] |
| World shutting down / task reset | - | the run stops; `ResetException` is rethrown (v1's catch swallowed it while sleeping) | the unpatched run never sleeps |

One known difference, by design: an item the game adds to an inventory before that inventory's turn (while earlier ones are checked) is checked tonight instead of the next night: the same rule, one night earlier, and only broken items change [inferred; offline E below].

**Nightly alarm chain (commit 2), behaviour review of v1 and fixes:**

- Order: v1's alarms could overlap (Cleanup Inventory now sleeps, the next option fired at +10 min regardless) and v1 slot 0 waited for a 0-minute alarm. v2: the first cleanup runs in the alarm task itself; each next one starts `Set Alarm Spacing` sim-minutes after the previous one **finished**, in the same `DerivativeSearch` order.
- Notices: v1 showed them one by one. v2 collects them while each cleanup runs and shows one message at the end, as before. A menu action between two cleanups notifies at once, as outside the alarm today.
- Exceptions: v1 kept the other alarms running after one threw. v2: an exception stops the night's remaining cleanups and still shows the notices, as before.
- Existing saves: the new field gets 10, not 0 [verified: `ScriptCore.PersistedClass` builds a loaded object with its parameterless constructor, which runs the field initialisers, then reads only the fields in the save's schema]. §4's open point is closed: old saves get the spacing too; 0 still gives the old run.
- Safety: spacing capped at 720 / option count minutes (whole chain within 12 h); a chain still pending when the next night's alarm fires runs its rest first, in order; a world load clears the chain state (a stale `AlarmTask` handle disposed in a new world could remove an unrelated alarm).
- Left as is, by nature of spreading: each option reads its on/off setting at its own time (a toggle within the ~3.5 sim-hours applies tonight); alarms are never persisted, so after save + quit mid-chain the remaining cleanups run the next night; stereos and TVs go off later in the night.

**Offline proof** (`node tools/selftest/nraas-upstream/run.ts <NRaas clone> <ref>`: extracts the real `CheckInventories`/`CheckInventory` from master and from the ref into `build/selftest`, compiles them with our stubs of the game types, nothing of NRaas stored here): 400 random worlds with every corruption kind (null objects, no item component in household and other inventories, destroyed items, missing parents, corrupt names, corrupt jam jars incl. the stack-emptying one, null and empty stacks): master, v2 without timer, v2 sleeping before **every** item (111,929 sleeps) and v2 with a timer that never fires give the same log lines in the same order, the same exceptions and the same final state. Then 400 worlds where a fake game changes things at every sleep (62,981 changes: items taken out, items and stacks added, owners destroyed, stacks replaced, households dropped): no exception escapes or is logged, all 9,573 removed items were broken when removed (1,126 of them added before their inventory's turn), every original item left in a live inventory is clean. PASS on both PR branches; a planted bug (no jam-jar stop, removed items not skipped) fails it.

**Local test packages and queue** (not run): `node tools/setup/build-nraas-upstream.ts <NRaas clone> [owRef] [etRef] [baseRef]` builds both assemblies at master and at the patched refs with the same pipeline and repacks each into a copy of the installed package (only the S3SA swapped; the Set Alarm Spacing strings and ErrorTrapTuning7.xml are not repacked: the menu shows the key, the tunable default applies) under `build/nraas-upstream/{base,v2}/`. Install note: test-only fixes `nraas-upstream-base` / `nraas-upstream-v2` (`node tools/setup/install-fixes.ts install --only nraas-upstream-v2`, backs up the installed packages; `uninstall --only ...` restores them). Queue `tools/harness/queues/nraas-upstream-verify.json` (2 fresh loads, repro `bp-nraas-upstream-verify`): same corrections required, plus the markers seen and no >= 250 ms row from the inventory check on the v2 side.

**PR comment draft (#45):**

> Follow-up after more review, two commits on top:
>
> - Cleanup Inventory now sleeps by time used (10 ms per frame), between items as well as between inventories, so one big inventory no longer stalls a frame either (every item reads its catalog name). With sleeping on, the helper copies stacks and items first and checks after each sleep what the game may have changed: removed or replaced stacks and items are skipped, an empty stack refilled meanwhile is kept, destroyed objects are skipped, and the corrupt-jam-jar case stops that inventory exactly as before. Without sleeping (menu action, other mods) it is the old code path. `ResetException` is passed on now.
> - The spaced nightly cleanups run one after another again: each starts Set Alarm Spacing minutes after the previous one finished, in the same order, notices collected into one message at the end, and an exception stops the rest as before. Spacing is capped to keep the night's run within 12 hours. Existing saves get the default spacing (the game builds the settings object with its constructor before reading the saved fields).
>
> Checked offline by running master's and the patched `CheckInventories` side by side on 400 generated towns (same log lines, same order, same final state, also when sleeping before every item), and with the game changing inventories at every sleep. In-game A/B still to come.
>
> 🤖 Assisted with AI together with @rodneypantonial

### 5.2 v3: one cleanup per frame, one Stuck Check plan per frame (2026-10-23, compiled, not run as a patched build, not pushed)

`docs/data/upstream/nraas-overwatch-staggered-cleanup.patch` now holds 2 new commits on the PR #45 head `e6aa54c` (the v1 commits 1-3 are in this file's git history and on the PR; commits 4-5 in `-v2.patch`). `git apply --check` clean on `e6aa54c`; `NRaasOverwatch` compiles (Roslyn C# 7.3, their `Sims3/Compiler` DLLs). PR #45 base = Chain-Reaction/NRaas `master` `2f57186` (their latest push, 2026-07-29). It does upstream what ts3fixes `overwatchchain` v2 + `overwatchstuck` do (§11, PLAN 2026-10-23):

1. `Nightly alarm: one cleanup per frame without spacing`: in the chain loop of commit 5, when no spacing applies, `Common.Sleep()` (their helper: sleeps only when `CheckYieldingContext(false)`) between two cleanups, then the "newer run took over" check. Default `kAlarmSpacing` 10 -> 0, so the cleanups stay at the alarm hour (stereos/TVs as before) as in our chain; tunable comment and English prompt updated.
2. `Stuck Check: one route check per frame; StaggeredCleanupVersion 2`: a Sim that needed `SimEx.IsPointInLotSafelyRoutable` gets `Common.Sleep()` at the end of its iteration, after its reset decision (our hook sleeps inside the call, before the decision; upstream keeps plan + decision in one frame); Sims destroyed while sleeping are skipped at the loop top. Marker 1 -> 2.

**Call context [verified in their source]:** the daily alarm is `Common.AlarmTask` -> `AlarmManager` callback -> `FunctionTask.Perform(OnTimer)`, so `OnTimer` -> `RunAlarms` -> `PerformAlarm` -> `StuckCheck.PrivatePerformAction` runs in its own task where yielding is allowed (our off-arm counters agree: `overwatch_yields` = `overwatch_checks` on that stack, §11.1); the menu action runs in its interaction. Elsewhere `Common.Sleep()` returns without sleeping (old one-frame behaviour).

**Kept parts and why:** commit 1 (skip the name lookup: removes the cost, not only spreads it), commits 2/4 (inventory sleeps by time: one cleanup per frame still leaves Cleanup Inventory itself as one long frame on big towns), commit 3/5 settings (`Set Alarm Spacing` > 0 stays as an option for players who want the cleanups apart; the chain code is the loop commit 6 sleeps in). None is redundant.

**ts3fixes stand-down:** `overwatchspread`/`overwatchchain` stand down for any marker (v1 already spreads the types). `overwatchstuck` now stands down (counts, no own yield) for marker >= 2 (`OverwatchSpreadCore.OurStuckYield`, `kStuckYieldVersion` = 2; version read at world load, log "..., stuck-check yields theirs"); selftest PASS. Not built here (worktree has no game DLLs).

**PR #45 body (edit) and comment (posted by Rodney/coordinator):** one day run, noisy, numbers from our ts3fixes equivalent (`overwatch-loop4`, asi.0333, 10 firings per arm), not from a build of this branch: off max 294 ms; on max 69 ms, 0 iterations >= 100 ms; StuckCheck yields 1,167 = checks 1,167. Clean-night confirmation as a follow-up comment. Comment text:

> Two more commits on top:
>
> - Nightly alarm: with Set Alarm Spacing at 0 (now the default) the cleanups run one per frame at the alarm hour instead of all in one frame; spacing above 0 keeps the chained alarms.
> - Stuck Check: one route check per frame (sleep after the Sim's decision); `StaggeredCleanupVersion` = 2.
>
> In game (one day run on one machine, flagged noisy, so indicative only; measured with our mod's equivalent of these two commits on the released Overwatch, 10 alarm firings per arm): off max 294 ms, on max 69 ms with 0 frames >= 100 ms; Stuck Check yielded after 1,167 of 1,167 route checks. A clean-night confirmation will follow as another comment.
>
> How to test: build `NRaasOverwatch`, install it over the released package; load a populated world with Overwatch logging, Cleanup Inventory and Stuck Check on, NPCs idle for a sim-day; set Alarm Hour to the next hour and let time pass it. The old one-frame hitch at the alarm hour should be gone, and the Overwatch log should list the same cleanups and repairs/resets in the same order as master. Alarm Spacing 10 runs them 10 sim-minutes apart, notices still as one message.
>
> 🤖 Assisted with AI together with @rodneypantonial

**Maintainer test without our tools:** as in the comment; with our tools: `nraas-upstream-v2`-style package from `tools/setup/build-nraas-upstream.ts <clone> <v3 ref>`, repro `bp-overwatch-ab-loop2` with `overwatchchain`/`overwatchspread` off (the marker makes them stand down anyway), pass = no Overwatch-led iteration >= 100 ms, `overwatch_checks` rising with `overwatch_yields` flat (ours stood down).

## 6. Other one-tick maintenance work

From the same 211 runs: per callback, the game-only >= 300 ms iterations with at least one managed sample under that callback (`AlarmTimerCallback`/`Function.Invoke`/`FunctionTask` roots; 870 of 4,067 iterations have managed samples) and their wall time [measured; a sample under a callback in a long frame is strong evidence, not proof]. Effort = wall-clock for Claude with agents.

| Rank | Work (owner) | When it fires | Measured | Per-item cost | Stagger like Overwatch? | Est. saving | Effort |
|---|---|---|---|---|---|---|---|
| 1 | Overwatch nightly alarm (NRaas) | once per sim-day | 35 iterations, 37.2 s under `AlarmOption.PerformAction` (33 TaskObjects); class B 347.8 s | name lookup per world object | **built** (this doc) | ~11 s/h of >= 300 ms time (class B 21.1/h × ~0.5 s) [inferred] | done; A/B ~30 min |
| 2 | `ObjectSpawner.TriggerSpawn` (EA: insects, fish, gems, seasonal) | each spawner's own random alarm | 38 iterations, 53.3 s (30 ThreadedObjects) | object creation + placement per spawn | no batch to spread: each spawner already has its own alarm; cut the per-spawn placement search instead (cf. docs/110 wildflowers) [inferred] | up to ~1.6 s/h [inferred upper bound] | capture first: 1 `/taskstall` run (~15 min), then ~2-4 h |
| 3 | ErrorTrap `DereferenceController.Perform` (NRaas; **re-measured in docs/146**: the hitch is one 0.5-0.9 s load-up tick, mostly `CheckInventories`; these 29 iterations are probe-run overhead) | once, 1 sim-minute after world load | 29 iterations, 52.3 s | reflection walk over every tracked object's fields | yes: per controller and per object batch with a sleep (their loop already checks world quit between controllers); upstream patch like §4 | ~0.25 s per load [inferred: 52 s over ~211 loads] | ~2-3 h (upstream patch) |
| 4 | EA nightly lot cleanup `LotManager.CleanUpLotsAndInventories` + `RunStuckCheckHelper` | daily at `kVenueCleanupTime` (disabled when NRaas StoryProgression runs its per-lot scenarios instead) | 10 iterations, 4.2 s (9 TaskObjects) for the cleanup; `RunStuckCheckHelper.Execute` 23 iterations, 27.8 s (12 route search) | per lot `MagicallyCleanUp`; then one stuck-check helper per actor, each planning routes | yes: one lot per tick in a ts3fixes task (hook the alarm, keep EA's per-lot body), stuck helpers already run as per-Sim tasks, their cost is routing (FewerPlans, async routing) | ~0.3 s per sim-day for the lot part [measured order]; the helpers belong to the routing work | ~2-3 h + 1 repro |
| 5 | `PetPoolManager.AlarmCallBack` (EA) | pet pool alarm (minutes-scale, repeats until the pool is full) | 13 iterations, 26.4 s | one random pet built through CAS part search per call | partly: the unit is one pet; move the CAS build to a sliced task or prebuild [inferred] | ~0.8 s/h [inferred] | ~3-5 h |
| 6 | `SocialComponent.RelationshipDecay` (EA) | one global relationship decay alarm | 4 iterations, 21.9 s (long ones) | every relationship (thousands in old saves, docs/09) | yes: decay a slice of relationships per tick with the elapsed time per pair | ~0.7 s/h on the measured saves; grows with save age [inferred] | ~3-4 h |

Not cleanups, seen in the same table: `Responder.FireSimMinuteChanged` (UI, 16 iterations), door close alarms (14), `Sim.RunIdleLogic` (23), `MetaAutonomyManager.CalculateTotalDesiredNumSims` (7), `StrayPets.StrayPetHelper` (10, 3.8 s) and `WildflowerTaskHook` (9; docs/110). EA StoryProgression is off in every run (`-disablestoryprogression`) and NRaas StoryProgression is not installed, so their nightly scenarios have no data; NRaas SP's `NightlyLotScenario` already runs the lot cleanup per lot through its scenario queue [verified in their source]. Of the installed mods (docs/132 list) only Overwatch and ErrorTrap show up.

## 7. Open risks

- Not run in game. First run: the repro under `/taskstall` before any timing A/B (memory rule: correctness first).
- `MethodInfo.Invoke` of a generic `Find<T>` and of an interface method in Mono 1.2.3: built and type-checked, not exercised [inferred works; a failure is caught and the alarm falls back to Overwatch's own path with a log line].
- Our inventory pass equals Overwatch's by our reading of their rules (§2.2); the verify census uses the same reading (§3).
- A save during our sliced pass: each inventory is changed in one tick, so a save sees whole inventories; a save between slots sees some types done and some not, as with any alarm that has not fired yet [inferred].
- Spreading moves notices and the stereo/TV switch-off later in the night; with `mAlarmHour` late in the day the last slots run into the morning.

## 8. Ledger

docs/51 §208: I-145-1 .. I-145-6.

## 9. 2026-10-22: clean-night spreads pair (BP, SV): what ran, what remained, SV -7.4%

Data: `clean-night-0.2.0` 2026-10-21 `{bp,sv}-spreads-{offfirst,onfirst}` (`--ab easpreadlots,easpreadpets,easpreaddecay,overwatchspread`, repro `night-maintenance-clock`). Covers docs/147 too.

1. **Did our code run in the counted slices? Not shown.** The switch lines are in every job log at every slice [verified], and the hooks read the switch when the alarm fires [verified source]. But no ts3fixes counter or log was captured: the queue row's `counters` and `-ab-native.jsonl` are ts3native only (row delta `{}`), and the profiler runs no samplers in `--ab` mode (`-simstack.jsonl` empty) [verified]. Fixed in the harness: `profile-world.ts` now writes `<tag>-ab-fixes.jsonl` (ts3fixes counters at every switch, the last line with `easpread_log` / `overwatchspread_log`), and `--fixes-require` / queue `profile.fixesRequire` turns counter rules into the job verdict (`tools/harness/fixes-require.ts`, self-check in the file).
2. **Fire timing [measured rate; fire times inferred from it].** `rng.json`: SV 20,748.6 -> 29,353.5 sim-minutes, of which 732 are the jump from 09:48 to 22:00, so 13.1 sim-minutes per real second (a sim-day ~110 s); BP 10.8 (~134 s). The repro assumed 9.5-11. Midnight and 3 AM first fire ~9-25 s in (warm-up slice), then once per ~110 s (SV) / ~134 s (BP): every 2-minute slice holds about one night, and in SV the fire moves ~10 s earlier per slice, so a fire near a slice edge belongs to whichever switch state its alarm saw. The off-side TaskObjects bursts recur every ~112 s in SV (244.7, 357.7, 469.9 s in `sv-spreads-onfirst`, 175-180 ms) [measured]: on these saves the daily burst is about a third of §1's class B (~500 ms).
3. **Why a burst remained [verified in source; which part fired in the data is not known].** Three parts of the "on" path were one-tick work outside the timed slices: (a) our Overwatch inventory pass scanned every world object for an inventory in one tick before slicing (`OwInventories`), not counted in [163]; (b) Overwatch's 21 other types run their own code whole at their slot (by design, §3.1), untimed; `CleanupLaundromat` (every object on every lot) and `CleanupRelationships` are the likely heavy ones (§2.1, [inferred]); (c) EA's `DecideTodaysHotSpotsAndDeadZones` after the last venue-cleanup slice (docs/147 §6.1). Built: (a) the world query is timed and the per-object check runs 512 objects or 2 ms per tick (`kMaxScanPerSlice`, selftest `WorldScanSlices`); (b) each type is timed on its own stopwatch: [223] longest type µs, [224] types >= 50 ms, each with an `overwatchspread_log` line naming it (a type that sleeps, e.g. `RecoverStrandedToddlers`, is not counted as one tick); (c) see docs/147 §6.1. (b) stays Overwatch's code: a type that shows goes to the upstream PR (#45 follow-up).
4. **SV -7.4% it/s [measured stage split; cause of the rest inferred].** Per iteration, on minus off: off-first +0.89 ms (ThreadedObjects +0.84, of it route +0.35; TaskObjects +0.09), on-first +0.67 ms (ThreadedObjects +0.30, locomotion +0.11, TaskObjects +0.09). Our passes are one-shot Tasks, which run in TaskObjects (docs/36: every Task, no budget) [verified], and TaskObjects rose by only 0.09 ms per iteration in both orders, about 1% of a ~10 ms iteration; the 10-second TaskObjects series shows no sustained on-side rise, only the daily spikes on both sides. No polling loop or re-armed alarm exists: our tasks live only while a pass runs and each alarm schedules once per fire [verified source]. The rest of the gap is in ThreadedObjects (Sim work and route plans) [measured]; that it comes from the world diverging after the first pass (stuck-check arming, decay and Overwatch types at other times) rather than from our code is [inferred]; the same SV world swings 88-102.5 it/s between 2-minute slices of one job (`sv-unseen-play-*`). Read per fire with the new snapshots before calling it a regression.

Next: `tools/harness/queues/scriptfix-ab.json`: `bp-spreads-verify` / `sv-spreads-verify` first (5 min, daytime, counters prove every pass finished and every tick of ours stayed < 8 ms incl. the scan), then the clean-night pairs. Ledger docs/51 §218: I-218-1, I-218-2, I-218-5.

## 10. 2026-10-22: the ~350 ms tick with `overwatchstuck` on is the other cleanups, not the stuck-check plans; `overwatchchain`

Data: `freeze-measure-2026-10-22-bp-freeze-pan-600`, `overwatch-loop-2026-10-22-bp-overwatch-ab-loop-720`, `tool-check-2026-10-22-bp-tool-check-300` hitch lines (docs/166 task attribution: `tasks[].from/to`, plus `routes`/`byEntry` of the same iteration). `overwatchstuck` on, `overwatchspread` off, so the alarm is `OverwatchSpreadHook.TimerHook` -> Overwatch's own `OnTimer` (task name `FunctionTask>OverwatchSpreadHook.OnTimer`, or `OneShotFunctionTask>...OverwatchSpreadNow` from the trigger).

### 10.1 What one night's runs look like [measured]

| run | Overwatch task runs in time order (ms; resume -> yield) | route plans in the long iteration |
|---|---|---|
| freeze-pan night 1 | 351 (fresh -> end, no yield) | 0 |
| freeze-pan night 2 | 23 (fresh -> `ResetSimTask.Perform` Sleep), 377 (that Sleep -> first `IsPointInLotSafelyRoutable`), then 13, 28 (plan -> plan) | 2 `Route_PlanFromPoint`, 13.2 ms |
| freeze-pan night 3 | 279 (fresh -> first plan), then 10, 9, 5 | 1, 5.1 ms |
| overwatch-loop firing 1 | 256 (fresh -> end) | 0 |
| overwatch-loop firings 2, 3 | 312 / 301 (`ResetSimTask` Sleep -> first plan), then 91 / 92 (plan -> plan) | 4 / 5, 16.6 / 27.2 ms; the 91/92 ms runs are one 87-88 ms plan |
| tool-check | 29 (fresh -> Sleep), 52 (Sleep -> Sleep), 361 (Sleep -> end) | - |

1. **The stuck-check plans are not the cost.** The long iteration holds 0-5 `PlanFromPoint` plans worth 0-27 ms, and two firings with **no plan at all** (first night: Overwatch has no last position yet, so it plans nobody) still took 256 and 351 ms [measured]. `SimEx.IsPointInLotSafelyRoutable` is one `PlanFromPoint` per Sim with 8 radial destinations (4 corners + 4 edge midpoints of the lot), not 8 plans [verified, NRaas source]. After the first plan `overwatchstuck` already gives every plan its own tick (5-28 ms, one 87-88 ms plan).
2. **The resume point.** `NRaas.Common.Sleep < ResetSimTask.Perform` is the one Sleep inside `ResetSimTask.Perform(sim, fadeOut: true)`: destroy the Sim, Sleep, re-instantiate it at home [verified source]. Inside the alarm two types call it inline: `CleanupElevators` (Sims assigned to an elevator) and `ResetCheck`. The re-instantiation alone is 15-53 ms: the same shape (resume at that Sleep, run to the end) in StuckCheck's own `ResetTask.OnPerform` and NRaas `ResetSimTask` tasks, n = 25 over the four 2026-10-22 runs [measured].
3. **What is left: ~250-330 ms of the other Overwatch types between the reset and StuckCheck [measured as a total; split inferred].** Type order = `DerivativeSearch.Find<IAlarmOption>()` over the module's types; the TypeDef row order of the installed `NRaasOverwatch.dll` (`managed-callgraph.ts subtypes ... --mods`) is DeleteMagicalGnomes, ResetAllGnomes, CleanseTheDead, CleanupConcert, **CleanupElevators**, **CleanupInventory**, CleanupLaundromat, CleanupOutfits, CleanupRelationships, CleanupSituations, CleanupSkillModifiers, CleanupVehicles, KillAllHomeless, PurgeGeneticHair, RecoverMissingSims, RecoverStrandedToddlers, ResetCheck, RestartDigitalPhotos, StuckCheck, TurnOffStereos, TurnOffTelevisions (+ console RunAlarmCommands) [verified metadata; that `GetTypes` returns row order is inferred]. So the run that starts at an elevator Sim's re-instantiation carries the inventory cleanup and every type after it up to StuckCheck's first plan; the fresh 23-29 ms runs end before the inventory cleanup. The bulk is the inventory cleanup's per-object name lookup (§1, 75 of 101 sampled class B bursts) [inferred: no sampler in these runs]; Laundromat/Relationships/Outfits/SkillModifiers/RecoverMissingSims/ResetCheck are the rest (§3.1) [inferred].

So the ~350 ms = one Sim re-instantiation (15-53 ms) + Overwatch's inventory cleanup and the other whole-list types (~250-310 ms) + StuckCheck's actor walk and first plan (5-27 ms).

### 10.2 Fix: `overwatchchain` (ts3fixes, tuning `kOverwatchChain`, default **off**)

Yielding before the first plan or between corner plans cannot help (one plan, and the time is before it). `overwatchspread` covers it but moves the types 10 sim-minutes apart (stereos/TVs up to ~3.5 sim-hours later, §2.1). `overwatchchain` keeps Overwatch's timing within a few ticks:

- In `TimerHook`, when Overwatch's own path would run (`overwatchspread` off, no upstream marker, hook in place; `OverwatchSpreadCore.WithChain`): Overwatch's own option list, in its order, inside the alarm's own FunctionTask (`MayYield`; else a one-shot task), `Simulator.Sleep(0)` between types (21 yields), each type through `OwRunType` = Overwatch's own `PerformAlarm` (same setting check, same code) except the inventory cleanup, which is our sliced pass (§2.2/§3.1: same inventories and repairs, no name lookups, <= 128 items or 2 ms per tick). Within a type Overwatch's own sleeps stay (RecoverStrandedToddlers, `ResetSimTask`) and `overwatchstuck` still yields after each plan.
- Notices: Overwatch's `sCollectNotices`/`sAlarmNotice` set for the chain and the batch shown once at the end through `Common.Notify(string)`, as `OnTimer` does (fields looked up by reflection; missing = notices one by one).
- Differences from Overwatch's own path: the types run over ~22+ ticks instead of one; an exception in one type is logged and the next type still runs (Overwatch's outer catch would end the alarm; each `AlarmOption` already catches its own) [verified source].
- What can still exceed 50 ms in one tick [measured above]: one stuck-check plan (up to 87-88 ms seen) and a Sim re-instantiation after `ResetSimTask`'s own Sleep (15-53 ms); a type of Overwatch's that takes >= 50 ms whole is logged by name ([224], `overwatchspread_log`).
- Counters (registry): [244] alarms run as a chain, [245] yields between types (agent `owchain_alarms`, `owchain_yields`); chained types also count in [158]/[223]/[224]. Array `int[246]`.
- Trigger: `OverwatchSpreadNow("all", 1)` runs the chain when the switch is on. Repro `bp-overwatch-ab-loop2` now flips `overwatchchain` (with `overwatchspread` held off), ABBA, 10 firings per arm. Pass: off arms keep one 250-380 ms Overwatch run per firing; on arms no Overwatch run >= 50 ms except a single plan or a re-instantiation, [244] +1 and [245] +21 per firing, one inventory pass line per firing.
- Selftest `node tools/selftest/overwatchspread/run.ts`: PASS (adds chain vs spread vs marker vs hook cases). `build-fixes.ts`, `build-agent.ts`: built. Not run in game.

Ledger docs/51 §271: I-145-7 .. I-145-9.

## 11. 2026-10-23: why StuckCheck got no yield inside the first chain; `overwatchchain` v2 through Overwatch's own loop

Data: `rerun-0333-2026-10-23-bp-overwatch-ab-loop3` (asi.0333, ts3fixes with the 679efd78 delegate call; `-repro.jsonl` per-firing counters, `-1320-hitches.jsonl`). Target (Rodney, 2026-10-23): every Overwatch-led iteration < 100 ms; ~300 ms counts as stutter.

### 11.1 What the loop showed [measured]

- Off arm (Overwatch's own `OnTimer` via `TimerHook` -> `TimerOrig`, `overwatchstuck` on): `overwatch_yields` = `overwatch_checks` (+54..+67 per firing); the trigger task's runs per firing are one 249-273 ms run (every type up to StuckCheck's first plan) and then ~50 ms runs (one plan each).
- On arm (first chain): `owchain_yields` +21 per firing (our `Sleep(0)` between types worked), `overwatch_yields` +0; `overwatchspread_log` names only StuckCheck as a type >= 50 ms (377-582 ms in one tick, 10 of 10 firings); every other type < 50 ms in its own tick, our inventory pass longest 1.4-2.7 ms per tick.

### 11.2 The two yield gates [verified, disassembly of TS3W.exe]

`MayYield()` (RouteBackoff.cs) is `CheckYieldingContext(false) && !NOYIELD`:

1. `CheckYieldingContext(false)` never runs its IL: the transform turns it into opcode `0x1A8` (`0xE5451A`, docs/119). It answers false when the global yielding-disabled flag `[0x11F414C]` is set (`Simulator_SetYieldingDisabledImpl` `0xD70BD0`; `Simulator.YieldingDisabled`, set briefly by EA's pregnancy and career code), else it asks `0xE53550`: task state `+0x148` set, and no frame record up to the current depth running a finally (`+0x128 != 0`) or named `.ctor`/`.cctor`. `Sleep` (opcode `0x1A6`, `0xE5939C`) asks only `0xE53550`, not the flag.
2. NOYIELD (ts3native `GetOptionHook` bit 58|61) = `!CanYield()`: the current interpreter state `[0x11F4170]` (saved and restored by every `0xE53EB0` activation) must be the running task's root state.
3. `MethodInfo.Invoke` runs `interp_runtime_invoke` `0xE5A600` on a stack-built state with `+0x148` clear: both gates refuse inside it (as docs/130 §1.2).

### 11.3 The delegate path is not a nested call [verified static; refutes PLAN's "likely nested too"]

- A delegate `Invoke` is compiled to Mono's delegate-invoke wrapper: the transform (`0xE64518`) swaps `Invoke`/`BeginInvoke`/`EndInvoke` of a `MulticastDelegate` subclass for `0xE8F720`/`0xE8F510`/`0xE8A960`; the wrapper ends in `calli` [inferred from Mono 1.2.3; the wrapper's IL was not decoded], and `calli` (`0xE547F7`) takes the same common push (`0xE54B44` -> `0xE56287`) as `call`; only P/Invoke targets go elsewhere. `callrun` (`0xE53D50`) handles only delegate `.ctor` and array `Set`/`Get`/`Address`.
- `Delegate.CreateDelegate` (`CreateDelegate_internal` `0xE53370`) fills the same fields as the `ldftn` constructor `0xE52790` (target, runtime method, method object).
- `GetInterfaceMapData` (`0xE7B870`) builds the interface methods with the interface as reflected class, as `GetMethodsByName` (`0xE7DBA0`) does for `alarm.GetMethod("PerformAlarm")`, so the identity compare in `OwPerformDelegate` should match and the delegate was built [inferred].
- Live: OwChain's own `Sleep(0)` ran 21 times per firing in a frame reached through a delegate call (`OneShotFunctionTask.Simulate` -> `mFunction()`) [measured].

So which gate refused inside the chained StuckCheck is **not known** [unknown]: the yielding-disabled flag, a `0xE53550` refusal from a frame below StuckCheck, or ts3native's root-state answer all fit the counters. v2 logs it once per session if it happens again: `overwatchspread_log` line "StuckCheck plan without a yield: context <bool>, yielding disabled <bool>, ts3native no-yield <bool>" (OverwatchStuck.cs).

### 11.4 Fix: `overwatchchain` v2 (built, not run)

Instead of calling each type from our code, the chain now runs Overwatch's own loop:

- `TimerHook` in chain mode starts a one-shot task `OwChainTheirs` (its name marks the chain's runs in hitch lines) that calls `TimerOrig` = Overwatch's own `OnTimer`: its option list and order, its try/catch, its notice batching, its settings.
- A body swap on `NRaas.OverwatchSpace.Alarms.AlarmOption.PerformAlarm` (the base of every cleanup type except the console's `RunAlarmCommands`; called only from `OnTimer`) = `OwAlarmHook.PerformHook`: outside a chain it calls the original. In a chain it sleeps one tick before every type but the first when `MayYield()` (else no sleep: Overwatch's one-tick loop), runs our sliced inventory pass for `CleanupInventory` (setting on, yield allowed; else Overwatch's own), and times every other type (`[223]`, `[224]` + log line at >= 50 ms, `kSlowTypeUs` was already 50 ms).
- The call stack into StuckCheck is the off arm's (task -> `TimerHook` -> `OnTimer` interface call) plus one plain frame, so `overwatchstuck`'s per-plan `Sleep(0)` sees what it sees in Overwatch's own path, where it yields on every plan [inferred from the off-arm counters; to verify].
- Removed: our option loop and the notice batching copy (Overwatch's own `OnTimer` does both). The delegate/Invoke call stays for the `overwatchspread` slots and the single-type trigger. 2026-10-30: the `MethodInfo.Invoke` fallback (`sOwPerform.Invoke`, used when no interface-map delegate could be made) is gone: the interface method dispatches to the swapped `AlarmOption.PerformAlarm` and Mono throws `TargetException` there (same cause as 06cbc07b). The fallback is now a delegate on the option type's own `PerformAlarm`, and with no delegate the type is skipped with a log line. `tools/selftest/swapinvoke` now matches swapped methods by name, whatever type they are looked up on (interface lookups included).
- Pure decisions in `OverwatchSpreadCore.SleepBefore`/`ChainType`, selftest `node tools/selftest/overwatchspread/run.ts`: PASS. `build-fixes.ts`: see the commit. Counters unchanged ([244] chains, [245] ticks between types: 20 per firing with 21 `AlarmOption` types).
- What can still pass 50 ms in one chained tick: one StuckCheck plan run (42-56 ms in the loop3 off arm, one 87-88 ms seen in §10.1) and a Sim re-instantiation after `ResetSimTask`'s Sleep (15-53 ms). Both stay < 100 ms on the data so far.

Queue `tools/harness/queues/overwatch-loop4.json` (`bp-overwatch-ab-loop4`, repro `bp-overwatch-ab-loop2`, asi.0333, 10 firings per arm): pass = `owchain_alarms>=10,owchain_yields>=150,owspread_inv_passes>=10` and hitch-tasks cap rule `OwChainTheirs@100<1` (new `<N` form in `tools/harness/hitch-tasks.ts`: no iteration with a >= 100 ms run of the chain task). Dry-run only.

### 11.5 Fallback ranked (not built)

| Path | Effort | Risk | Floor per tick |
|---|---|---|---|
| A. v2 above: Overwatch's own loop + PerformAlarm hook | built (~1 h), 1 loop job | the unknown gate of §11.3 also refuses inside `OnTimer` when reached from our task (then: diag line, no regression vs off arm) | one plan (<= ~90 ms seen) |
| B. our own StuckCheck (and any type >= 50 ms) time-sliced on the sim thread, 5 ms budget, Overwatch's type skipped while ours is on | ~3-6 h + selftest, 2 game runs (verdict parity, loop) | verdict parity: Overwatch keeps last position/interaction per Sim in private state, its routability uses its own route options; first night after switching differs | still one plan: a route plan cannot be split, and plans touch game objects, so not off-thread |

B has the same per-plan floor as A, so it only helps if A's yields fail. No other type reached 50 ms in the loop3 chain arm [measured], so nothing else needs slicing now.

Ledger docs/51 §290: I-145-10 .. I-145-13.

## 12. 2026-10-24: what Overwatch's timer costs per call, and `overwatchstuckslice` (built, not run)

Question (after the v0.1.5 soaks, `docs/data/results/2026-10-24-v015-hitch-causes.md`): the BP soak shows 85 runs of `FunctionTask>OverwatchSpreadHook.OnTimer` (17 of 250-463 ms), the SV soak 89 (2 >= 250 ms, 39 >= 100 ms), while `overwatchchain` counters from a live run look right (`owchain_alarms` 14, `owchain_yields` 280, `owspread_max_type_us` 57,458, applied log "StuckCheck (Overwatch's own code) took 57.5 ms in one tick"). Is there per-call work in `OnTimer` that the chain does not split?

### 12.1 What `OnTimer` does on every call [verified, NRaas master + call graph of the installed V123]

- `OnTimer` has no work of its own: set the notice-batching flag, `DerivativeSearch.Find<IAlarmOption>()` (cached after the first call: copies a 21-item list), call `PerformAlarm` on each, clear the flag, show the batched notice. No per-Sim or per-object loop, no name lookup, no route check outside the types.
- It is called **once per sim-day**: `RestartAlarm` (world load, settings change) makes one `Common.AlarmTask(mAlarmHour, DaysOfTheWeek.All, OnTimer)` = `AddAlarmDay`; each firing runs it in a new `Common.FunctionTask`. Nothing else in Overwatch calls it (our trigger `OverwatchSpreadNow` does, in repros only).
- So the 85 / 89 "runs" are **task runs** (resume to next yield) of ~17 BP / ~25 SV nightly firings [measured: the >= 250 ms BP runs come every 140-182 s real, one per sim-day at the soak's speed; SV every 100-120 s]. Per firing: one fresh run (every type up to StuckCheck's first route check, or from `ResetSimTask`'s Sleep to it), then one run per StuckCheck route check (overwatchstuck's yield).

### 12.2 Why the soaks show Overwatch's one-tick path [measured]

- Every Overwatch run in both soaks is the alarm's own `FunctionTask>OverwatchSpreadHook.OnTimer`; 0 runs of `OwChainTheirs` (the chain's task). In chain mode `TimerHook` only queues `OwChainTheirs`, so the mode at those alarms was `Theirs` (switch off).
- The installed `ts3fixes.package` in both soaks has sha256 prefix `93b16bb8ffbd` (`docs/data/change-ledger.jsonl`, change set `a12d9a4b7cd8`; that hash was installed from 2026-10-23 07:14Z to 2026-10-24 06:22Z). The v0.1.5 package (`docs/RELEASE.md`: `949dac596eda...`, `kOverwatchChain`/`kStuckCheckAsync`/`kErrorTrapSpread` True) is **in no ledger row**. So the soaks ran an older dev build whose tuning had the chain off [inferred from task names + hashes; the old package's tuning XML was not read]. Same for `stuckcheckasync` (the soak's stuck-check stacks run `RunOrig` under `AlarmTimerCallback.Invoke`). The results doc's "shipped switches never engage" is a test-setup finding, not a package bug, until a soak on `949dac596eda` says otherwise.
- The 17 BP freezes are the chain-off shape of §10.1 / §11.1 (the whole type loop up to the first route check in one tick); the live chain counters above fit the chain working when on.

### 12.3 What the chain does not split: the Stuck Check's Sim walk [measured + inferred]

- SV soak: 20 runs of 100-336 ms **from one `IsPointInLotSafelyRoutable` yield to the next** (both ends in `StuckCheck.PrivatePerformAction`), each with 1 route plan of 0.6-12 ms (`byEntry.Route_PlanFromPoint`), `rtLock`/`rtPlanWait`/`rtWaits` 0, `gcMark` 0, `taskObjects` = the run [measured, `-2700-hitches.jsonl`]. BP soak plan-to-plan runs stay < 100 ms. The live chain run's StuckCheck took 57.5 ms in one tick, i.e. with no route check at all [measured, counter 223].
- Between two route checks StuckCheck walks `new List<Sim>(LotManager.Actors)` (63-112 Sims in these worlds, `-activity.jsonl`): per Sim a dictionary record, `InWorld`, `Proxy`, Overwatch's `SimTypes.IsSelectable(Sim)`, parent is a bed, robot shutdown, current interaction, `LotCurrent.IsRoomHidden(RoomId)`, position compares, `ResetTask.Perform` (an `AlarmManager` alarm) for hidden-room / stationary Sims; and for an unmoved Sim with the same interaction `SimEx.IsPointInLotSafelyRoutable` (route setup `CreateRouteAsAdult` + `SetRouteOptions`: vehicle, stroller, situation and mermaid checks; then one `PlanFromPoint` with 8 radial goals) [verified, call graph of the installed `NRaasOverwatch.dll`]. Which of these carries the 100-336 ms is **not known** (no managed samples inside these runs: the SV soak has 2 Overwatch samples, both CleanupInventory). The walk is the only part that runs per Sim with no yield, so it is the target [inferred].
- `overwatchchain` cannot help: it only puts ticks between types, and StuckCheck is one type. `overwatchstuck` yields after each route check only.

### 12.4 Fix: `overwatchstuckslice` (ts3fixes, tuning `kOverwatchStuckSlice`, default **off**)

- `OverwatchStuck.cs`: a body swap on Overwatch's own `NRaas.CommonSpace.Helpers.SimTypes.IsSelectable(Sim)` (its copy of NRaas Common; the walk's first call per Sim, after `InWorld` and `Proxy`). Outside a Stuck Check run it is the original.
- The `AlarmOption.PerformAlarm` hook (`OverwatchSpread.cs`, already in place for the chain) marks StuckCheck as running around Overwatch's own `PerformAlarm` in **both** paths (chain and Overwatch's own loop) and starts a stretch watch; overwatchstuck's route-check yield ends a stretch and starts the next.
- In the hook, when the stretch has reached `kStuckSliceUs` = 8 ms, the switch is on, no upstream marker >= 3 (§12.5) and `MayYield()`: `Simulator.Sleep(0)`; then if the Sim was destroyed, left the world or lost its proxy during that tick, answer "selectable" so Overwatch's loop takes the same branch it takes for a Sim not in the world (record dropped, nothing else); otherwise Overwatch's own `IsSelectable` and the rest of that Sim's checks run after the tick, on fresh state.
- Differences from Overwatch's own walk: later Sims are looked at a few ticks later (as with overwatchstuck's yields today); a Sim that left the world during our tick is treated as not in the world. Same Sims, same order, same decisions.
- Floor: one Sim's checks plus at most 8 ms; a route check (0.6-88 ms seen) stays as before.
- Counters (registry 255-259): [255] ticks taken, [256] Sims walked, [257] longest stretch without a tick (µs), [258] Stuck Check runs, [259] stretch time summed (ms); agent `owstuck_slices`, `owstuck_sims`, `owstuck_max_stretch_us`, `owstuck_runs`, `owstuck_ms`. [256]-[259] count with the switch off too, so any run with this build attributes the walk ([257] = worst stretch). Array `int[260]`.
- Selftest `node tools/selftest/overwatchspread/run.ts`: PASS (adds `SliceDue`, `SkipAfterSlice`, `OurStuckSlice`). Compile: ts3fixes and the agent plugin compile (csc, C# 5, against the main checkout's `game-copy/assemblies`). Not run in game.
- Repro draft `bp-overwatch-stuckslice-loop` (repros.json): release set held (`overwatchchain` on, `overwatchspread` off, `overwatchstuck` on), ABBA flip of `overwatchstuckslice`, `OverwatchSpreadNow("all", 1)`, 10 firings per arm. Pass: on arms `owstuck_slices` > 0 and no `OwChainTheirs` run >= 50 ms inside StuckCheck except one route check; same resets in both arms. Read the off arms' `owstuck_max_stretch_us` first: if the walk stays < 50 ms in BP, the SV soak's 100-336 ms runs need an SV run with this build before the switch is worth turning on.
- Before any of this: run one natural soak (BP or SV) with the actual v0.1.5 package (`949dac596eda`) installed and check `owchain_alarms` > 0 (§12.2); that alone should remove the 250-463 ms BP freezes.

### 12.5 Upstream (NRaas PR #45)

The v3 commits sleep between cleanups and after each Stuck Check route check (§5.2), not inside the walk. Proposed third v3 commit (not written, not pushed): `Stuck Check: sleep in the Sim walk by time; StaggeredCleanupVersion 3`: a `SleepWatch` (their Common helper: `Common.Sleep()` once its interval has passed, so only where yielding is allowed) created at the start of `StuckCheck.PrivatePerformAction` with a 10 ms interval (their default is 100), `watch.Sleep()` at the top of each Sim's iteration before v3 commit 2's destroyed-Sim skip, marker 2 -> 3. ts3fixes stands down at marker >= 3 (`OverwatchSpreadCore.OurStuckSlice`; its counters keep measuring). Add it after an in-game result from `bp-overwatch-stuckslice-loop`.

Ledger docs/51 §315: I-145-14 .. I-145-16.

### 12.6 Verdict 2026-10-27: 0 slices is the right answer, not a code fault [measured]

- `untested-v020` rerun 2026-10-26 (build 0dc5fca8, SV, `sv-overwatchstuckslice-verify`, 8 forced firings ABBA, `probe-out/profile/untested-v020-2026-10-26-sv-overwatchstuckslice-verify-repro.jsonl`): `owstuck_runs` 1 -> 7, ~84 Sims per run, `overwatch_yields` ~66 per run (a route check, and so an overwatchstuck yield, for most Sims), `owstuck_max_stretch_us` 21,741-26,434, **`owstuck_max_select_us` 1,039**, `owstuck_slices` 0 on every arm.
- [302] is the slice check's own view: the longest stretch at a Sim's start never got past ~1 ms, well under `kStuckSliceUs` 8,000. The 21-26 ms stretches in [257] all end in a route check (the plan itself), which the IsSelectable hook cannot split. So the switch never had a stretch to slice; the hook and its counters behave as designed.
- With `overwatchstuck` on, the walk between two Sims is already short; the 100-336 ms SV soak runs of §12.3 were plan-to-plan runs from builds before the shipped chain (§12.2) [inferred]. `overwatchstuckslice` stays **off** and is not a v0.2.0 candidate; redesign only if a run shows `owstuck_max_select_us` >= 8,000.
- Queue rules changed: `untested-v020` `sv-overwatchstuckslice-verify` and `owstuckslice-verify2` `sv-owstuckslice-soak-verify` now require `owstuck_runs>=1` only (`owstuck_slices>=1` dropped); read `owstuck_max_select_us`.

## 13. 2026-10-24: `overwatchstucknotice`, one summary instead of a notice per reset Sim (built, not run)

Player report: Overwatch's Stuck Check V2 shows one notice per reset Sim ("StuckCheck:Unroutable"), too many. Rodney's decision: Overwatch's resets stay exactly as they are; only those per-Sim notices are held and replaced by one summary at most every 2 sim-hours.

Where the notice comes from [verified, NRaas source `NRaasOverwatch/OverwatchSpace/Alarms/StuckCheck.cs`, read only]: every Stuck Check reset, from the nightly walk (hidden room, unroutable spot, stationary a sim-day) and from the V2 route-failure listener, goes through `StuckCheck.ResetTask.Perform(sim, destination, suffix)`, a one-shot `Common.AlarmTask` one sim-minute later. Its `OnPerform` puts the Sim in a safe spot or resets it (with the player's `mStuckCheckReset` on), then calls `Common.Notify(mSim, Common.Localize("StuckCheck:" + mSuffix, ...))`; suffix "Stationary" shows nothing outside NRaas debug builds. `ResetCheck.cs` has its own per-Sim notices (`<prefix>:<reason>One` / `Two`, through `Overwatch.Notify` / `AlarmNotify`) for a different option (Sims stuck in an interaction); they are not touched here.

- **Hook** (`mods/ts3fixes/src/OverwatchNotice.cs`): `StuckCheck+ResetTask.OnPerform` swapped (same `Patch` mechanism as `overwatchstuck`; applied at startup or world load; the method first runs a sim-minute after a reset, so it is not compiled before). NRaas's `Common.Notify` is not patched: Overwatch's `Notify` queues every notice into the static list `Common.sNotices` whenever it is not null (its load-up buffering). The hook puts a list of ours there for the length of the original `OnPerform`, then puts the old value back (only if nothing replaced ours meanwhile, e.g. a world quit). Of what was queued, the one notice whose text equals Overwatch's own `Localize("StuckCheck:" + mSuffix, sim.IsFemale, sim)` is held; anything else goes on unchanged (into Overwatch's list, or shown through its `Common.Notify(StoredNotice)`).
- **Summary** (`OverwatchNoticeCore.cs`): the first held notice opens a window and starts a never-persisted one-shot alarm 120 sim-minutes later; the alarm shows one `kGameMessagePositive` notice "Overwatch reset N stuck Sims in the last 2 hours: A, B, C (+M more)" (plain English, three names, a Sim reset twice in one window counted once) and closes the window. Nothing held = no notice; two summaries are always >= 2 sim-hours apart. World quit clears the window unshown (world load again).
- **Stand-down:** no `NRaasOverwatch` assembly, or `ResetTask.mSim`/`mSuffix`, `Common.sNotices` (`List<StoredNotice>`), `StoredNotice.mText`, `Common.Localize(string, bool, object[])` or `Common.Notify(StoredNotice)` missing or typed differently: one applied-log line, nothing patched. A reflection error inside the hook is logged once and leaves that notice to Overwatch.
- **Switch:** `overwatchstucknotice`, tuning `kOverwatchStuckNoticeSummary`, default off. **Counters** (registry 290-292, `int[293]`): [290] notices held, [291] summaries shown, [292] Sims named (agent `owstucknotice_held`, `owstucknotice_summaries`, `owstucknotice_named`).
- **Tests:** `tools/selftest/overwatchnotice` (window, per-Sim count, text, alarm flow: summaries >= 2 h apart, every held notice in exactly one summary). Repro `owstucknotice-summary` (UNVERIFIED draft): the trigger `Ts3Fixes.Fixes.OverwatchStuckNoticeNow(4)` calls Overwatch's own `ResetTask.Perform(sim, Vector3.Invalid, "Unroutable")` for 4 non-household Sims (forced: they are not really stuck), then ultra speed past the window; expect 4 held, 1 summary, 3 named, no per-Sim notices.
- **Inferred, to confirm in game:** `ResetTask.OnPerform` does not yield (alarm callback; `ResetSimTask.Perform` queues a task), so no other code's notices land in our list; if one did, it is sent on unchanged. Overwatch builds other than V123 with the same member names behave the same.

## 14. 2026-10-24: `owprobeplans`, Stuck Check V2 no longer counts the game's stuck-check probes (built, not run)

Player report (v0.1.5): V2's route-failure reset (`StuckCheck.OnPostPlan`, separate from the nightly walk above) reset a sleeping Sim after counting the 10-plan probe rounds of EA's own 3 AM stuck check as her failures; the reset notice is the one §13 summarises. Overwatch's own stack-trace skip for `RunStuckCheck` never matches with ts3fixes (our hooks hide that frame). Fix and evidence: docs/163 §9 (ts3fixes `OverwatchProbe.cs`, switch `owprobeplans`, tuning `kOverwatchIgnoreStuckProbes`, default on, counters 293-294; only `OnPostPlan` is patched, it returns early for plans made inside the game's stuck check; the rest of Overwatch unchanged). Upstream note for NRaas: matching the stack frame by method text breaks under any hook of `RunStuckCheck`; not sent.
