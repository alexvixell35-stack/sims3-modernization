# 545. UI, wish, mood and relationship leak fixes (ts3fixes, 2026-11-11)

Static RE plus live trigger runs, 2026-11-11 (SteamOS/Wine, Isla Paradiso fresh world, test saves `ts3phase-*` only).
**Status: `mooddrift` and `moodpending` ON by default since 2026-11-12 (Rodney's decision: EA mood target bug, the off/on
runs below are green with 0 errors). Since 2026-11-13 (Rodney OK, after `bp-m1112b-leaks-offon` PASS on master1112b:
no crash, 0 script errors, every switch read back per 2-min slice) also ON: `casrowleak`, `compositorleak`,
`compositorghost`, `buyiconleak`, `buycellrecycle`, `buyclearrecycle`, `buygridsweep`, `sellcursorleak`, `filtergridleak`,
`caspuckevents`, `casfiltergrid`, `famcursorleak`, `casexitsweep`, `seasonlistener`, `dnpdisposelistener`,
`dreamrootorphan`, `blogorphan`, `genealogycycle`, `uncleguard`, `seasondup`, `dnpupdateguard` (and the #337/#353/#357/
#365/#366 imports, PLAN.md). Still off: `relremovesafe`, `spouseunlink`, `attractionalarm` (they change saved data:
relationship tables, spouse links, persisted alarms; AGENTS.md "save data stays opt-in"), `familyinvghost` (disproven,
§1).** No switch turned on here writes anything into a save (UI objects, listeners, session caches, exact guards), so
none needs a load-time cleanup. Each was run in game with the fix off, then on, in the same session
(`phasesoak3.sh` phases, `leak_census` after each phase). Every change acts only on UI objects, on wish/listener
bookkeeping, or on data that is already wrong; household Sims are never moved, skipped or throttled.

Sources: the 1.67 script DLLs decompiled with ilspycmd into `game-copy/decompiled/` (git-ignored; no EA code is
reproduced here, only class and method names); UI.dll read as IL (instruction indexes from our IL printer).
Claim marks: **[verified]** = read in the decompiled code or IL; **[measured]** = seen in a live run; **[inferred]** =
our reading, not proven.

## 1. Results

Counters are ts3fixes slots (docs/registry.md). "Off" = counted with the switch off in the same run.

| Switch | EA bug | Off | On | Errors |
|---|---|---|---|---|
| `mooddrift` | MoodManager's target mood is kept by AddEffect/RemoveEffect pairs; some buffs change a live instance's value without them, so the target drifts from the sum of the buffs | 16 Sims drifted (3 household), 3,419 points | drift 0 within one phase, 5,812 / 6,499 points corrected (two runs) | 0 |
| `casrowleak` | CASClothingCategory.AddGridItem wraps a row (CASClothingRow), and when CreateGridItems returns nothing it returns without adding or destroying it | 10-19 rows per CAS phase left registered | 14 of 14 disposed, 0 rows left | 0 (v2) |
| `compositorleak` (v2) | CompositorController unload leaves stale entries in UIManager.mCustomControlInstanceDict | 4-8 per visit | 152-673 stale entries removed | 0 |
| `buyiconleak` | BuyController.Dispose disposes cached icons but never removes their dict entries | 146 disposed icons left | all swept | 0 |
| `buycellrecycle` | BuyController.PopulateGridTask takes a cell per product, AddGridItem returns false for hidden / Passport-locked / disabled products without returning it to the cache | 132 cells dropped per visit, +228 live icons per visit | 132 / 198 cells recycled, +96 / +144 per visit (about 58% less growth) | 0 |
| `sellcursorleak` | BBSellPanelController.RegisterDragCursors registers InventoryItemWin cursors that Dispose never removes | +2 to +4 per visit | removed on dispose | 0 |
| `dnpdisposelistener` | DreamsAndPromisesManager's constructor listens for the destroyed-subject event on the wrong id (kDnPSubjectDestroyed instead of kEventObjectDisposed); OnLoadFixup uses the right one | 4-11 managers per session | 7 of 7 new managers re-listened on a household switch | 0 |
| `dreamrootorphan` | DreamTree.ActivateTreeForSim creates a tree root with its listener on and drops it on kThrowAway / kNotSupported without removing the listener | 13-14 per fresh world, 28 per household switch (round trip) | 28 of 28 removed | 0 |
| `deadrefguards` (existing) | Relationship.Get(SimDescription) adds a table for a removed Sim when called from Sim.Dispose (RomanceVisibilityState) | +14 to +20 invalid tables per run | flat across 79 removals | 0 |
| `relremovesafe` | RemoveSimDescriptionRelationships uses a plain indexer on the other Sim's table (throws when absent) and never removes one-sided entries | EA's throw never seen in these runs | 155 removals, 0 errors | 0 |
| `genealogycycle`, `spouseunlink` | Genealogy.get_Ancestors / ClearDerivedData have no visited set; ApplySpouseUpdate never clears the old spouse's back-link | no loops or one-way spouses in this world | 133 spouse updates, 0 errors | 0 |
| `petwishleak` | RemoveActiveNode does not take pet-subject wishes (types 11, 12) out of mPotentialRemovalNodes | counter fixed to count only nodes really left; 0 seen | guard; dropped at import (duplicate of `dnppetkeys`, PR #366; Rodney 2026-11-12) | 0 |
| `moodpending` | WellRested (from a bed) and ConqueredTheBull call ChangeMoodValue on their new buff from OnAddition, before AddElement lists it and adds its value: the mood target gets the value twice and loses it once | 8-10 calls in two story-day rounds (household Sims included) | 4 of 4 skipped in the third round, 0 drift | 0 |
| `filtergridleak` | CatalogProductFilter.Init turns DisposeOnDetach off on its child grid and nothing turns it back on | +1 ItemGrid per Buy or Build visit | 4 of 4 restored, ItemGrid flat across 3 Build visits | 0 |
| `buyclearrecycle` | ShowCatalogGrid(false) (back button, catalog switch) clears the grid without caching its cells | 118 cells per Buy navigation visit, +274 to +403 icons per visit | 118 of 118 cached, +220 per visit | 0 |
| `buygridsweep` (v2) | SelectCatalog / ShowAllItems repoint mCatalogGrid; BuyController.Dispose cleans the icons of the current grid only, and AddGridItem turns the icons' DisposeOnDetach off, so the cells left in BuyController.mExpandableCatalogGrid's own ItemGrid keep their icons registered | +220 live icons per Buy navigation visit | 220 of 220 handed back to EA's detach per visit, +0 per visit (two visits) | 0 |
| `compositorghost` | every Buy, Build or CAS visit leaves one invisible, parentless CASCompositorController window and its dict entry (not the controller Load returned; nothing detaches it) | +1 per visit (10 in 10 visits) | 16 of 16 cleaned up with OnCustomDetach's steps, 0 left | 0 |
| `caspuckevents` | CASPuck.Init subscribes to the static DownloadDashboard.DashboardStateUpdate and to the session-long OptionsModel.OptionsChanged; CASPuck.Dispose removes neither (PuckController removes both) | every closed CAS screen stays subscribed: 10 of 10 | 10 of 10 handler pairs removed | 0 |
| `casfiltergrid` | CASPuck's content-type CatalogProductFilter (child 153931569) has the same DisposeOnDetach(false) grid as Buy's filter; CASPuck.Unload shuts its layout down without it | +1 ItemGrid per CAS exit (5 in 5) | 10 of 10 restored before CASPuck.Unload, ItemGrid flat | 0 |
| `famcursorleak` | CASFamilyScreen.Init keeps two windows of a "CAFThumbItem" layout as drag cursors and nothing disposes them (EA's SceneMgrWindow.UnregisterDragCursors pattern does) | 18 cursor windows per 3 family-tree opens | 18 of 18 disposed per 3 opens | 0 |
| `casexitsweep` | Clothing browsing leaves the part cells' CustomContentIcons (id 35) as live, visible windows with no native parent (IUIManager.GetParentWindow == 0); no CAS code reparents them, the engine detaches them when their cells go away. At CAS exit, entries registered since CASPuck.Load that are live and natively detached get OnCustomDetach's cleanup and WindowBase.Dispose(false) | +75 entries per clothing visit | v1: +5 per visit (65 of 70 cleaned per visit, 3 visits). v2, which also rechecks still-parented entries at the next CAS load: flat over 4 visits (97 → 92), 230 cleaned | 0 |
| `seasonlistener` | Motives' kSeasonTransition listener (global, no owner) is never removed by Autonomy.Dispose (Sim teardown: it drops itself only at the next season change, keeping the destroyed Sim reachable until then), by Autonomy.RecreateAllMotives (age up/down, trait changes, time traveler: the old Motives stays subscribed for good and on each season change edits Desires in the Sim's live InteractionScorer [inferred]), or by Motives.RecreateMotives (re-adds the same listener; EventTracker does not dedupe) [verified] | +110 season listeners over 3 story phases (Sims +31) | 241 → 239 over 3 phases (Sims +15); 111 removed | 0 |

Built and installed without errors, but nothing to act on in these runs: `seasondup` (56 duplicate Motives season
listeners at the start of a fresh world, none on reload or household switch), `blogorphan` (the blog app was not used),
`dnpupdateguard` (no wish update threw), `attractionalarm`, `uncleguard` (no case in this world).

Disproven: `familyinvghost` (destroyed family-inventory items still listed). 64 probe rounds with the hook counting in
both halves found 0: Destroy removes the item before ItemComponent.Dispose runs [measured]. It stays in the source, off,
and is not part of any run list.

No crash in about 4 hours of trigger runs with the switches on, except the three caused by `buycellrecycle` v1/v2 (§3).
One run (phase-20261111-2105) crashed 40 s into live mode with every switch off: an access violation on the main
thread in native code during routing (eip 0xFFDBD1B5), not on the script thread; the same build re-run
(phase-20261111-2108, 38 min, three rounds) had no crash [measured; cause not known].

## 2. Findings that shaped the tests

- **Wish listener bugs are session-only [verified + measured].** A save load rebuilds wish managers with
  OnLoadFixup, which uses the right event, and re-creates no orphan roots. A reload with the fixes on showed 0 new
  cases. They come back whenever a Sim gets a new wish manager in play: a new game, a Sim becoming playable, or a
  household switch (28 orphan listeners and 7 wrong-event managers for one switch round trip). The `switchhh` phase
  (switch_household @other, then @previous) is the trigger.
- **Mood drift happens in normal play [measured], and its main cause is WellRested [verified decompiled].**
  One household Sim drifted +10 on Uncomfortable with a live WellRested. BuffWellRested.OnAddition calls
  Bed.UpdateWellRestedBuffBase, which calls BuffManager.ChangeMoodValue(bi, 10) on the instance before AddElement lists
  it and before AddElement's own AddEffect: the strength goes into the mood target twice and comes out once, +10 per
  night in a bed, household Sims included (the target is not saved, so a load resets it). BuffConqueredTheBull does
  the same (+15 / +30 Happy). Other breaks of the AddEffect/RemoveEffect pairing (static RE, rarer): OnReAddition
  overrides that change the live instance and return true (Fear, MissingAppointment, PhoneJunkie, SuccessfulPrank),
  and code that edits another live buff's value (GoodNectar, WashAwayTheWorry, Werewolf on NiceNails, LaundryManager
  FreshClothing, TraitListener Wow buffs, VaultOfAntiquity). Recomputing the target as the sum of mEffectValue per
  axis (`mooddrift`) equals EA's intended value: only BuffManager writes the target, AddEffect neither clamps nor
  scales, trait scaling and the HotHeaded/Neurotic axis switch are applied to the instance before the add, and
  MeanSpirited's contribution is added and taken back inside UpdateMoodLerp. `moodpending` (MoodPending.cs) fixes
  WellRested and ConqueredTheBull at the source: ChangeMoodValue on an instance that is not yet the live buff only sets
  its value.
- **Sibling one-way links were a census error.** Genealogy builds mSiblings lazily and AddChild clears it for the
  whole family; the census now counts an unbuilt list separately (`genealogy_sibling_list_unbuilt`).
- **Buy navigation and Build mode [verified IL + measured, phase-20261111-2047].** With the back button and catalog
  switches (`buynav` phase: ts3agent `buy_category` `@back` / `@catalog:<name>`), each Buy visit left +338 live,
  parentless icons with the fixes off. ShowCatalogGrid(false) ends with mCatalogGrid.Clear(), which only detaches the
  cells; RepopulateCatalogGrid caches them first, the back-button path does not. `buyclearrecycle` (BuyCellRecycle.cs)
  does that caching step for the grid's own catalog type: 118 of 118 cells per visit kept, growth +220 per visit
  (35% less), 0 errors. The other ~220 per visit come from a path not found yet. `buygridsweep` (dispose the icons of
  catalog grids other than the current one at BuyController.Dispose) v0 counted 0 icons in the four named grids. An icon
  tracer (cells added per visit, classified just before Dispose, their icons checked after) then put all ~220 leftover
  icons per visit in one ItemGrid that no controller field points to: the grid inside BuyController.mExpandableCatalogGrid
  (ExpandableCatalogGrid.PreInit, control id 174056508). v1 disposed those icons directly, and 220 new live entries came
  back after BuyController.Unload: the sub-layout's shutdown detach re-registered a wrapper for each native icon through
  UIManager.WrapperFromHandle. v2 turns the icons' DisposeOnDetach back on instead, so EA's OnCustomDetach handles them:
  +0 per visit.
  Build mode is much cleaner: BuildController has no cell cache and every grid clear goes through the native detach
  (OnCustomDetach), so a category click leaves nothing [verified IL]. Three Build visits added 6 entries: per Buy or
  Build visit, one ItemGrid and one live, parentless CASCompositorController. The ItemGrid is CatalogProductFilter's
  child grid (export id 189775728), whose DisposeOnDetach Init turns off and nothing turns back on.
  `filtergridleak` (FilterGridLeak.cs) sets it back on just before BuyController.Unload / BuildController.Unload, so
  EA's own OnCustomDetach handles it. The compositor controller: probes on CASCompositorController.Load and Unload
  showed the leftover entry is never the controller Load returned, and the engine still reports a compositor custom
  control under its handle (a live, invisible, parentless native window). `compositorghost` gives such a window
  OnCustomDetach's steps after Unload (DeRegisterAllEvents, remove, WindowBase.Dispose(false)).
  Only five methods in UI.dll set DisposeOnDetach: BuyController.AddGridItem, ItemGrid.Clear(bool),
  CatalogProductFilter.Init (fixed above), InventoryPanel.RepopulateInventory (per cell, from a local), and
  CASFamilyScreen.Init (its two drag cursor windows, which nothing disposes: `famcursorleak`, below). InventoryPanel is
  not a leak: RepopulateInventory calls Clear(true) on the primary grid (`ldloc.1; ldc.i4.0; ceq`), which turns
  DisposeOnDetach back on before the detach [verified IL].
- **Household route failures with no destination are expected [verified decompiled].** The repeated
  "Replan sync FailedBecauseNoDestinationSpecified" lines for household Sims come from EA's wait-and-poll on an
  obstructed goal (DestinationObstructedRouteInterruptAction / ObstacleEncounteredRouteInterruptAction: Replan every
  2-3 animation cycles, at most 50 sim-minutes, goals rejected while another Sim stands there). Skipping them would not
  equal vanilla; nothing built.
- **The native crash of phase-20261111-2105** is on the Swarm (effects) service path in the main loop (return
  addresses 0x59ED59, 0x71E6D2, 0x9162EB: the same stack as docs/67 Crash A, a wild call through a freed Swarm object),
  with ts3native [Alloc] off, so docs/67 lead 2 (a plain EA use-after-free in Swarm) [inferred].

## 3. The Buy crash: a hook with the wrong return type

`buycellrecycle` v1 and v2 crashed the game at the first Buy visit (EA int3 at `0x58D9B6`), even with the switch off.
The assertion, read from the exe's string table at the call `0xE5CDC9` (args file, line `0x492`, condition): Mono's
interpreter `transform.c:1170`, `td.sp >= td.stack` [verified]. BuyController.AddGridItem returns bool (false on the
drop exits, true after ItemGrid.AddItem); the hook was declared void. SwapMethod copies the hook's MonoMethod header,
signature included, so PopulateGridTask's `pop` after the call underflowed the transform. A third run with both new
CAS/Buy switches off crashed the same way, which confirmed it and cleared `casrowleak`.

Fixes: the hook returns EA's value and uses it (a cell is recycled only when AddGridItem returned false, the grid's
four slot fields did not change, and the cell is not in the grid). `Patch` now refuses a hook or stub whose
void-ness, argument count or value-type return differs from the original, and logs "not patched" instead of
crashing. No existing hook was refused.

The CAS fix had its own bug: CASClothingRow.Dispose unhooks mGridWindows[i], which only Init fills, so it threw a
NullReferenceException on a row that was never shown. For such a row the pass now calls WindowBase.Dispose(false),
the step the override ends with.

## 4. Test tooling added

- ts3agent `leak_census`: `fix_counters`, `fix_applied`, sections `inventories` (InventoryCensus.cs) and `moods`
  (MoodCensus.cs, drift per Sim with the buff list), invalid relationship key samples.
- ts3agent probes: `inv_ghost`, `buff_drift`, `buff_churn`; `kill_sim` `home:true` (moves a spawned Sim next to the
  active Sim so EA lets it die).
- Build lint: `build-fixes.ts` refuses `[ThreadStatic]` / `[ContextStatic]`: EA's Mono has no MonoThread, so any
  access crashes in mono_get_special_static_data (the CAS crash of 2026-11-11 was our own `[ThreadStatic]`).

## 5. Next

- Combined run (2026-11-12, phase-20261112-0414, all 14 measured switches on from the start; story, Buy navigation,
  Build, CAS clothing, household switch, family tree, story, Buy, CAS): 0 errors in every fix's error counter, 0
  script errors, no new crash log. Custom-control entries went 78 → 115 over the whole tour, season listeners stayed
  flat (126 → 131) while instantiated Sims went 53 → 83. The InventoryItemWin growth in that run (+8 in Build, +2 per
  Buy navigation) is the sell panel's drag cursors: with `sellcursorleak` added, two Build and two Buy visits stayed
  flat at 73 entries (phase-20261112-0439). The CAS icons that detached after `casexitsweep` ran (+5 / +20 per visit)
  are handled by its v2 recheck at the next CAS load.
- Buy room tabs: 6 detached TabControl entries appeared once, after a Buy navigation visit (phase-20261112-0439),
  and did not come back in 3 more visits with room / collection / store catalog switches (phase-20261112-0452, flat
  at 78). Not reproduced; BuyController.SelectCatalog rebuilds tabs with TabContainer.RemoveAllTabs (DestroyChild:
  RemoveChild, then Dispose) [verified IL].
- Celebrity alarms (ResetCallOverPaparazziCount / FalselyAccuseCheck are re-added by CelebrityManager.OnStartup on
  every Sim instantiation): not a leak, the count equals the instantiated Sims (62 / 82) [measured].
- CAS icons: answered by `casexitsweep` (above). The census now reads the native parent handle: the managed
  `Parent` is null both for a detached window and for one whose parent class has no managed type, so "no parent"
  alone was not proof. About 5 to 20 icons per visit detach only after the sweep runs; v2 cleans them at the next CAS load. The row-Dispose and
  DestroyAllChildren hooks found nothing and were removed (`casrowicons`, replaced by `casexitsweep`; `destroychildren`, 0 in CAS and on the live family tree, where FamilyTreeDialog.RefreshTree uses it).
- Family-tree CAFThumbs (+15 per open, live and natively detached): measured only by calling CASFamilyScreen.Load /
  Unload inside single-Sim CAS, which players cannot reach (the game opens it from Create a Family,
  CAF.ShowRelationshipsPanel), so this may be a harness artifact [inferred]. Needs a Create a Family route before any
  fix; `casexitsweep` would also cover thumbs registered during a CAS visit.
- Live test of `moodpending` (a Sim sleeping in a bed until WellRested: target 20 vs sum 10 off, equal on).
- Listener cost: count dispatches and time per event id (EventTracker.ProcessEvent).
- Inventory audit items A1/A3/D1/C1 and the Downtrodden / Torched / Lunacy shared fields (static only so far).
