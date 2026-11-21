# 557. Static scan of UI, Sims3StoreObjects and Sims3Metadata; shared-buff and store-event fixes (2026-11-14)

**In-game status:** `buffstate`, `storeworldevents`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

First static bug hunt over the three EA script assemblies nobody had decompiled yet: `UI.dll`, `Sims3StoreObjects.dll`,
`Sims3Metadata.dll` (the gameplay pair is docs/555). Every hit below was read in context. No game run. Built (in-game status above):
`buffstate` and `storeworldevents`, both off by default.

Tags: **[verified]** = read in the decompiled code (and IL where marked); **[inferred]** = reasoning about reachability or
in-game effect, not checked in the game.

## 1. Tools and how to rerun

All output stays local (`game-copy/`, scratchpad). Run heavy tools one at a time (`nice -n 10`, dotnet capped with
`DOTNET_GCHeapHardLimit=0x60000000`): on 2026-11-14 a parallel Ghidra + `ilspycmd -genpdb` run pushed the machine into
earlyoom.

| Step | Tool | Command / recipe | Notes |
|---|---|---|---|
| Decompile | ilspycmd 11.2 (`dotnet tool install -g ilspycmd`, `DOTNET_ROLL_FORWARD=Major` on SDK 10) | `ilspycmd -p -o game-copy/decompiled/<A> -r game-copy/assemblies game-copy/assemblies/<A>.dll` | same project layout as the earlier four; UI 888 files / 220k lines, Store 80 / 86k, Metadata 81 / 11k |
| IL null / leak analysis | Microsoft InferSharp 1.5 (MIT; release tarball in `~/.local/opt/infersharp-v1.5`) | `tools/static/infersharp.sh <name> <Assembly>` | Cilsil translates only methods with debug symbols: without a PDB it translates 0 methods. The script makes a portable PDB with `ilspycmd -genpdb` (cached), so report lines match `game-copy/decompiled`. One assembly per call; Sims3GameplaySystems needs more than the 1.5 GB cap in Cilsil (OutOfMemory, not run; ~3 GB `DOTNET_GCHeapHardLimit=0xC0000000` expected) |
| Roslyn analyzers | Roslynator CLI + Roslynator.Analyzers 4.12.11 | scratch csproj per assembly: `net10.0`, `NoStdLib`, `DisableImplicitFrameworkReferences`, `GenerateTargetFrameworkAttribute=false`, `GenerateDependencyFile=false`, `Compile Include` of the decompile, `Reference` to every other DLL in `game-copy/assemblies`; then `roslynator analyze <csproj> -a ~/.nuget/packages/roslynator.analyzers/4.12.11/analyzers/dotnet/roslyn4.7/cs --severity-level info` | all three compile with 0 errors |
| Semgrep | 1.x, `tools/static/ea-script-logic.semgrep.yml` (docs/555) + new `tools/static/ea-store-ui.semgrep.yml` | rerun lines in the file headers | new rules: `buff-shared-field-write`, `static-event-instance-handler-no-remove` |
| ast-grep | `~/.local/opt/astgrep` | empty-catch rule (`kind: catch_clause`, regex on the text) | C# patterns with a `$T $X` declaration do not parse in rule files; foreach-modify stays in semgrep |
| CodeQL | 2.27, `codeql/csharp-queries` 1.11 (`codeql pack download`) | `database create --language=csharp --build-mode=none --ram=2048` on a real copy of the three trees | a source root of symlinks extracts 0 lines (CodeQL does not follow them) |
| Model-event subscriptions | new `tools/static/ui-subscription-scan.ts` | `node tools/static/ui-subscription-scan.ts game-copy/decompiled/UI` | `+=` on a model / `Responder.Instance` / singleton / static event with no `-=` of the same event and handler in the file; `--self-check` |

Can ts3fixes patch these assemblies? **Yes**: `Patch` swaps any loaded method body, and UI.dll methods are already
patched (`BuyController`, `SimDisplay`, `CASPuck`, ...); `build-fixes.ts` references `UI`, `Sims3StoreObjects` and
`Sims3Metadata`. The store repack's duplicate `StoreObjects*` types never load (docs/128 §1), so the EA types are the ones
that run.

## 2. Counts

| Tool | UI | Sims3StoreObjects | Sims3Metadata | Triage |
|---|---:|---:|---:|---|
| InferSharp null dereference | 6 | 1 | 0 | §4: 1 real store bug (VoodooDoll, low), 1 data-driven (CompositorUtil), 4 `Tag` casts and tree lookups that hold in practice |
| InferSharp resource leak | 428 | 0 | 2 | noise: `Layout`, `UIImage`, `*Drawable`, `*Effect` all have finalizers that free the native handle; Metadata's 2 are an `XmlTextReader` on a string |
| Roslynator (info+) | 6,361 | 2,957 | 371 | style dominates; read: `RCS1215` always-true (12), `CS0252` (9, CASPuck window compares: intended), `CA2245` self-assign (5: setters re-applied on purpose), `CA2011` (2, ContentInstallationController line-wrap recursion, ends), `CA2241` (1) |
| Semgrep (docs/555 rules) | 183 | 60 | 0 | noise except the listener/broadcaster rows that led to §3 |
| Semgrep (new rules) | - | 18 (+ 1 in SGS) | - | 17 real (§3), 1 false positive (`BuffFairyAuraProjecting`: a nested `BuffInstance` subclass) |
| ast-grep empty catch | 26 | 5 | 6 | all `catch (Exception) {}` around online/store calls, index guards or `ResetException`; none hides a state change |
| ui-subscription-scan | 29 | - | - | 22 already fixed (`hudunsub`, `casuihandlers`), 7 bounded or dead (§5) |
| CodeQL security-and-quality (171 queries) | 7,187 | 1,505 | 168 | §6: 3,531 `may be null` and 3,085 `readonly`/style rows dominate; nothing new above the items in §3 |

InferSharp on the other assemblies (for the docs/555 audit, not triaged here): ScriptCore 1 null + 1 leak, SimIFace
3 leaks, Sims3GameplayObjects 30 null dereferences (mostly `Dictionary.TryGetValue` misses fed to `ParseData`), all in
`game-copy/analysis/infersharp/<name>/report.txt`.

## 3. Findings, ranked by player impact

| # | Where | Bug | Effect | Status |
|---|---|---|---|---|
| 1 | `BuffDowntrodden` (SGS, Late Night bars) | per-Sim alarm handle kept on the shared Buff object | a Sim saved with Downtrodden, or the first of two downtrodden Sims, keeps a persisted 5-minute alarm that queues the hidden `BeDowntrodden` whenever its queue is empty, for good | **[verified]** code; effect **[inferred]**; fixed: `buffstate` |
| 2 | `World` / `LoadSaveManager` static events from store `OnStartup` (DeepFryerVegas, IceCreamMaker, VoodooDoll, ChocolateFountain, WalkerClassic) | handlers never removed; statics never reset | each deleted store object and every earlier world's store objects stay reachable (lot and object graph) for the session; one more dead handler per object per load | **[verified]** code (docs/192 #2); memory size **[inferred]**; fixed: `storeworldevents` |
| 3 | `MassageTable+HighFertilityStore` | shared `mAlreadyHadTrait` (never cleared), `mPregoSim` (last Sim only), listener added only while the field is null | the added Fertility Treatment trait (saved) stays for good on earlier Sims, on every Sim after one that already had it, and in every later world of the session | **[verified]**; fixed: `buffstate` |
| 4 | `VoodooDoll+VoodooCurseBuffStore`, `BabyDragon+DragonCurseBuffStore` | shared `mWokeUpListener` | two cursed Sims: the first gets Bad Night's Sleep on every wake for the session, the second's curse loses its effect early | **[verified]**; fixed: `buffstate` |
| 5 | `Hypnotizer+HypnotizedBuffStore` | `QueueChanged` handler never removed; it tests the shared `mOwner` | handlers pile up on every hypnotized Sim's queue; an earlier subject's queue change can end the current subject's trance | **[verified]**; fixed: `buffstate` |
| 6 | `BabySwing+QueasyBuffStore` | OnRemoval uses shared `mOwner`; unsubscribes only while still in the swing | two queasy toddlers: the first keeps crying, the last is flagged hungry on buff changes; a toddler who left the swing keeps the handler | **[verified]**; fixed: `buffstate` |
| 7 | `CABBasicsSheet.InitVoice` (Create-a-Bot basics tab) | four CASLogic undo/redo handlers never removed (Unload removes six others) | every return to the tab leaves the old sheet subscribed; each undo/redo then sets the pitch slider and voice buttons of each closed sheet | **[verified]**; bounded by the CAS session (CASLogic is shut down at CAS exit); candidate |
| 8 | `CASRequiredItemsDialog.InitTraits` | `TraitsUpdated += OnTraitsChanged` never removed (Cleanup removes only the lifetime-wish handler) | each time the "needs name / traits" dialog opens, a closed dialog stays subscribed and updates its dead grid on every trait change | **[verified]**; bounded by the CAS session; candidate |
| 9 | `BuyController.DisableEmptyCategories` | builds the full featured + product list for every sub-category button only to test `Count <= 0` | a burst of catalogue queries on each Buy-by-room tab click (hitch candidate) | **[verified]** code; cost **[inferred]**; candidate (measure first) |
| 10 | `VoodooDoll+TransformSim.Definition.CreateInstance` and `TransformSim` Run | `GetBoundSim()` dereferenced with no null test | NRE (script error, interaction lost) when the bound Sim is not instantiated between menu and run | **[verified]** (InferSharp); low |
| 11 | `CompositorUtil.ApplyPreset` | a `DrawFabric` step with no `pattern` attribute is dereferenced | NRE in Create-a-Style for a malformed (CC) preset | **[verified]** (InferSharp); low, data-driven |
| 12 | `CompositorUtil.CanObjectBeDesigned` / `CanBuildBuyItemBeDesigned` | `Length >= 0` (always true) where `> 0` was meant | an empty design preset counts as designable | **[verified]** (Roslynator RCS1215); effect **[inferred]**; low |
| 13 | `MainMenu` disc-authentication prompt | EP6 tested twice, the first shows the EP7 text | wrong pack named in a disc prompt (not seen on Steam) | **[verified]**; cosmetic |

### 3.1 Why buffs share state [verified]
`BuffManager.BuffDictionary` holds one `BuffInstance` per buff guid, built once from the buff XML; its `mBuff` is the
Buff subclass object, and `CreateBuffInstance` hands every Sim a new instance pointing at that same Buff. So
`OnAddition(bm, bi, ...)` / `OnRemoval(bm, bi)` run on one shared object; any field they write is shared by every Sim.
EA's own pattern for per-Sim data is a `BuffInstance` subclass (several of these classes declare one and leave it unused).
`OnAddition` is not rerun at a normal load (`BuffManager.OnLoadFixup` only validates instances), only on travel.

### 3.2 Downtrodden detail [verified]
`OnAddition` adds `actor.PushDowntrodden` as a repeating 5-sim-minute alarm, `AlarmType.AlwaysPersisted`, and stores the
handle in the Buff; `OnRemoval` removes the stored handle with `AlarmManager.Global.RemoveAlarm` (no owner check).
`PushDowntrodden` queues `BeDowntrodden` (hidden, autonomous priority) whenever the queue is empty. The buff is given at
nightlife venues (slapped, failed flirts, drinking with low Social), so overlap among NPCs at a bar is normal, and a save
made at a bar keeps the alarm without the handle.

## 4. Fixes built

### 4.1 `buffstate` (BuffSharedState.cs, counters 2428-2434, repro `buff-shared-state`)
One switch for the family; each Sim gets its own state, a single Sim behaves as vanilla:
- Downtrodden: OnAddition first removes an alarm the Sim still has; OnRemoval looks up this Sim's own `PushDowntrodden`
  alarms in `AlarmManager` (owner index, callback target) and hands EA's body that handle. The hourly pass removes the
  alarm from Sims without the buff, which also repairs saves (vanilla never does).
- Curses: each Sim's listener is kept in a table; EA's OnRemoval gets that Sim's own listener (none after a load: nothing
  removed, as vanilla).
- High Fertility: per-Sim had-trait flag; our own `kNewBaby` listener per world; at the next baby (EA's trigger, any
  baby, unchanged) every Sim the buff gave the trait to loses it. EA's listener stands down while ours is armed.
- Hypnotized / Queasy: the shared handler EA just added is swapped for a per-Sim handler object that does EA's test for
  its own Sim; OnRemoval removes it (Queasy: also when the toddler left the swing, and EA's body is pointed at the
  right toddler).
Harness check `BuffStateCheck`: two NPCs, Downtrodden OnAddition A, B, OnRemoval A through the patch. Off arm BROKEN
(A keeps 1 alarm, B 0), on arm FIXED (A 0, B 1); cleanup in both arms.

### 4.2 `storeworldevents` (StoreWorldEvents.cs, counters 2435-2437, repro `store-world-events`)
At world load finished and hourly, removes from `World.sOnWorldQuitEventHandler`, `World.sOnObjectPlacedInLotEventHandler`
and the LoadSaveManager singleton's `ObjectGroupSaving` every handler whose target is a script object with no simulator
proxy (destroyed / previous world). Live objects keep theirs; no gameplay change. Harness check
`StoreWorldEventsCheck`: a fryer handler bound to an uninitialized `DeepFryerVegas` (no proxy) is added and the pass run:
off arm BROKEN (still listed), on arm FIXED (removed). Also counted with the switch off (2435), so a natural run shows how
many dead handlers pile up. MemSuspects' `wq`/`wpl`/`ogs` counts (docs/191) measure the same lists.

## 5. Checked and dropped [verified]
- `MapTagController.MapTagVisibilityUpdate.Simulate` leaves (IL `leave` to `ret`) when a tag is added mid-pass. Not a bug:
  the simulator calls a `Task`'s `Simulate` again every tick until the task is destroyed (`OneShotFunctionTask` relies on
  that), so the return restarts the pass.
- `MapTagController` leaves `MapTagUpdate` on the model, but the model is destroyed right after (`DestroyMapTagsModel`).
- `CASController` compositor events and scene-window mouse handlers: removed in `OnClosingDown`, or die with the
  per-visit compositor.
- `CASFamilyScreen.DependentBounds` null spouse lookup: the CAS genealogy only links Sims of the household being edited.
- `casuihandlers` (docs/545) handles CASLogic events from the hourly live-mode pass, but CASLogic is shut down at every CAS
  exit, so its CASLogic part only matters for a CASLogic that outlives CAS; items 7-8 above are bounded the same way.
- UI resource "leaks" (428): finalizer-backed native handles; freed at GC, not leaked.

## 6. CodeQL [verified where read]
8,760 rows (`codeql/csharp-queries:codeql-suites/csharp-security-and-quality.qls`, ~10 min at `--ram=2048 --threads=2`).
Read in context, all harmless or unreachable:
- `Dereferenced variable is always null` `PieMenu.MenuButtonMouseUp`: the else branch of `if (menuItem != null)` reads
  `menuItem.mListObjs`; only reached for a pie button whose `Tag` is not a `MenuItem` [inferred: never, every pie button
  gets one]. Would be an NRE, not a state bug.
- `Off-by-one` `SimDisplay` dream slot `num <= Length`: `NextEmptyDreamSlot` returns `< Length` or -1, so unreachable.
- `Equals on incomparable types` `CompositorUtil.GetNumPatterns`: `Dictionary.Equals("")` is always false; the null test
  before it does the work.
- `Comparison of identical values` (40 Store, 13 UI): ILSpy re-reads of the same field in `while`/`if` (e.g.
  `actor.LotCurrent != actor.LotHome` across two locals of one value) and enum flag checks, not EA typos.
- `Futile conditional` (6), `Container contents never accessed` (14), `Property value not used` (3): dead code only.
- `Dereferenced variable may be null` (3,531): `as` casts on `Window.Tag` / `GetChildByID`; same family as InferSharp's
  UI rows, which ranked them better (6 rows).

## 7. Candidates (not built)
- Items 7-8: remove the handlers in `CABBasicsSheet.Unload` / `CASRequiredItemsDialog.Cleanup` (a Dispose-time hook like
  `hudunsub`); low value while CASLogic dies with each CAS session.
- Item 9: measure a Buy-by-room tab click with the hitch watch before any change.
- Items 10-12: null guards / `> 0`; low.
- Run InferSharp on Sims3GameplaySystems with a ~3 GB cap when the machine is idle.
