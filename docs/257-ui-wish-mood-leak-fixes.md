# 257. UI, wish, mood and relationship leak fixes (ts3fixes, 2026-11-11)

Static RE plus live trigger runs, 2026-11-11 (SteamOS/Wine, Isla Paradiso fresh world, test saves `ts3phase-*` only).
**Status: 18 switches built, all off by default.** Each was run in game with the fix off, then on, in the same session
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
| `petwishleak` | RemoveActiveNode does not take pet-subject wishes (types 11, 12) out of mPotentialRemovalNodes | counter fixed to count only nodes really left; 0 seen | guard | 0 |

Built and installed without errors, but nothing to act on in these runs: `seasondup` (56 duplicate Motives season
listeners at the start of a fresh world, none on reload or household switch), `blogorphan` (the blog app was not used),
`dnpupdateguard` (no wish update threw), `attractionalarm`, `uncleguard` (no case in this world).

Disproven: `familyinvghost` (destroyed family-inventory items still listed). 64 probe rounds with the hook counting in
both halves found 0: Destroy removes the item before ItemComponent.Dispose runs [measured]. It stays in the source, off,
and is not part of any run list.

No crash in 3 hours of trigger runs with the switches on, except the three caused by `buycellrecycle` v1/v2 (§3).

## 2. Findings that shaped the tests

- **Wish listener bugs are session-only [verified + measured].** A save load rebuilds wish managers with
  OnLoadFixup, which uses the right event, and re-creates no orphan roots. A reload with the fixes on showed 0 new
  cases. They come back whenever a Sim gets a new wish manager in play: a new game, a Sim becoming playable, or a
  household switch (28 orphan listeners and 7 wrong-event managers for one switch round trip). The `switchhh` phase
  (switch_household @other, then @previous) is the trigger.
- **Mood drift happens in normal play [measured].** One household Sim drifted +10 on Uncomfortable with only Well
  Rested and Delighted active, so the drift was left by a buff that had already gone. The census does not name it yet.
- **Sibling one-way links were a census error.** Genealogy builds mSiblings lazily and AddChild clears it for the
  whole family; the census now counts an unbuilt list separately (`genealogy_sibling_list_unbuilt`).
- **Remaining Buy leak [verified IL, not fixed].** About 96-144 icons per visit stay after `buycellrecycle`: grids
  other than the current one are never walked by BuyController.Dispose (SelectCatalog repoints mCatalogGrid), and
  ShowCatalogGrid(false) clears the grid without caching its cells (back button, catalog switch).

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

- Buy: dispose the cells of non-current grids at BuyController.Dispose, and cache cells in ShowCatalogGrid(false).
- Name the buff behind household mood drift (log each buff's removal per Sim in the census).
- Listener cost: count dispatches and time per event id (EventTracker.ProcessEvent).
- Inventory audit items A1/A3/D1/C1 and the Downtrodden / Torched / Lunacy shared fields (static only so far).
