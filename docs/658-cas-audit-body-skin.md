# 658. CAS audit: the "body lighter and greyer than the face" glitch, plus other CAS bugs

2026-11-19 (UTC). Static only: decompiled script code of the 1.67 game assemblies (read only, never in git), checked
where it mattered with `ilspycmd -il`, searched with `ast-grep` and `grep`. Branch `feat/cas-audit`. **No fix built**:
the managed skin-tone candidates are rejected (see §2), and the best-fitting mechanism (§3) cannot be confirmed without
reading native code. Paths are relative to `game-copy/decompiled/`.

Symptom (no mods): rarely, in CAS, the Sim's body skin (neck, shoulders, back) shows lighter and greyer than the face. It
was seen in swimwear and also in the mirror. Changing the hair, or leaving CAS and coming back, clears it.

## 1. Skin and highlight write graph [verified]

How the native builder state is written. The full `mUtils.SimBuilder*` list was taken with
`grep -rhoE 'mUtils\.SimBuilder[A-Za-z]+' SimIFace`.

- The native skin tone has two writers: `SimBuilderSetSkinTone` and `SimBuilderSetSkinToneIndex`. They are called only
  from the `SimBuilder.SkinTone` / `SkinToneIndex` setters (`SimIFace/Sims3.SimIFace.CAS/SimBuilder.cs:86-105`). There
  is **no native getter** for the key or the index, and no native "load outfit" call. Outfits are applied field by field
  in managed code (`OutfitUtils.SetOutfit`, `Sims3GameplaySystems/Sims3.Gameplay.CAS/OutfitUtils.cs:1233-1335`).
  Nothing else in managed code changes the native skin.
- The managed skin setters themselves have no equality check. Only `CASLogic.ExecuteSetSkinToneIndex` and
  `ExecuteSetSkinTone` (CASLogic.cs:12760, 12780) compare first. They are called only from the undo/redo steps
  `SkinToneStep` and `SkinTonePresetStep` (CASLogic.cs:2966-3016) and from the randomize call at 9099.
- The highlight and selection state uses the setters `SimBuilderSetHighlightMode`, `SetHighlightIndex` and
  `SetSelectionIndex`. These are write-only from managed code, cached in `CASLogic` (`mHighlightMode`,
  `mHighlightIndex`, `mSelectionIndex`; CASLogic.cs:7309-7356), and guarded by `if (cache != value)`. Mode and index are
  deferred to the CAS task loop (CASLogic.cs:8438-8447). The selection is pushed at once (7352).
- What a hair change runs: `ExecuteAddPart` (CASLogic.cs:13683) calls `mBuilder.AddPart` and sets
  `mbHairNeedsPropagation`. The next tick of the task loop then runs `IsDirty()`, `SetAutomaticModifiers` (only if the
  rig is dirty), `BuildModel(0)` and `UpdateCASRoom` (CASLogic.cs:8448-8475). Entering the Hair tab first sets
  `SelectionIndex = 1` and `HighlightIndex = 0` (`UI/Sims3.UI.CAS/CASHair.cs:1329-1330`). Leaving CAS disposes
  `CASLogic` and its builder (CASLogic.cs:8067-8074, `CleanBuilder`). On the next entry a new `SimBuilder` is made
  (8315) and the caches are reset.

## 2. The previous agent's skin candidates: all rejected

| # | Candidate | Verdict | Evidence |
|---|---|---|---|
| 1 | `SetOutfit` applies skin only if mask bit `0x4000…` is set | **Rejected** | The bit is in `outfit.GetComponents() & uniformComponentMask` (OutfitUtils.cs:1235). That is the outfit's *uniform component* mask, not an outfit category. Full outfits return `ulong.MaxValue` (`ExecuteSetOutfit` clears the builder exactly when this holds, CASLogic.cs:13742-13748). Partial uniform layers are meant to keep the skin already in the builder. Swimwear is not special here. |
| 2 | `ExecuteSetSkinTone*` skips when equal to the managed cache, and native could drift | **Rejected** | Native can only be written through the same setters that update the cache (§1), so the two cannot drift. Only undo/redo/randomize use these methods. |
| 3 | `SetDefaultBuilderValues` sets the key but not the index | **Real but harmless** | CASLogic.cs:13716. Every later full `SetOutfit` sets both the key and the index (OutfitUtils.cs:1293-1297). See also §4 B3. |
| 4 | Per-outfit-category skin on the Edit in CAS / household path | **Rejected as the cause of this symptom** | Stored outfits can carry a different skin (§4 B1), but one skin value is applied to the whole builder. A wrong skin key or index changes the face and the body together. It cannot make the body differ from the face. |

The key argument: a skin key or index is one value for the whole Sim. A glitch that affects only the body meshes and not
the face mesh must come from per-part state.

## 3. Best-fitting mechanism: stale CAS part highlight or selection [inferred, needs one native or live check]

CAS shows hover and selection by tinting **part meshes** lighter. The hover tint uses the pick id of the part under the
mouse; the selection uses the selected part's body type. A swimsuit is a FullBody/UpperBody part, and its mesh includes
the neck, shoulder and back skin. The face is a separate part. A tint stuck on that part would look exactly like this:
lighter and greyer body skin, a normal face, mostly bare skin visible in swimwear, and the same look in Mirror mode. It
also fits both cures: the Hair tab resets the index (CASHair.cs:1329-1330), and leaving CAS gives a new builder with
reset caches.

Code defects that can leave the tint set [verified in code]:

- **H1.** `CASClickOnSim.Dispose` sets `HighlightMode = None` but never resets `HighlightIndex`
  (`UI/Sims3.UI.CAS/CASClickOnSim.cs:421-435`). `CASClickOnSim.SetState` turns hover off with
  `SimSelectionModeEnabled = false` (1642) and also leaves the index alone. When a later panel sets the mode back to
  `Parts` (`CASTattoo.cs:640`, `CASController.cs:532`, `CASCompositorController.cs:5083`, `CAPFBDAdvanced.cs:196`), the
  old pick id lights up again. While hover is off, no event comes to clear it.
- **H2.** The hover tint is cleared only by a scene-window `MouseMove` that picks no part (`CASController.cs:970-990`,
  `OnSimHover` 1021-1044), or by scene focus loss (1396-1402). If the mouse leaves the Sim straight onto an overlapping
  panel, the last pick id stays set until the mouse comes back over the scene.
- **H3.** The clothing tab sets `SelectionIndex = (uint)wornPart.BodyType` whenever the worn part has patterns
  (`CASClothingCategory.cs:533`, 1209). It resets the selection only for the Summary, None and Glasses states, and only
  outside the Designer (1385-1390). The Hair tab is the other place that resets it.

Not confirmed: whether the native renderer tints with `SelectionIndex`, or only with `HighlightIndex` and mode
`Parts`/`Patterns`. That decides which of H1-H3 is the trigger. This is why no fix was built.

**Agent repro (cheap, decisive).** In CAS, on swimwear:
1. Read `CASLogic.GetSingleton().mHighlightIndex`, `mSelectionIndex` and `mHighlightMode` every second and take a
   screenshot each time.
2. (a) Hover the torso, then call `CASController.Singleton.SimSelectionModeEnabled = false` and set the mode
   None→Parts. (b) Call `CASModel.SelectionIndex = (uint)BodyTypes.FullBody` while not in the Designer.
3. If the lighter torso shows up, set the index or selection to 0 and look for the cure.

Fix shape once a variant is confirmed: in `CASClickOnSim.Dispose` / `SetState` (H1), and wherever hover is switched off,
also set `HighlightIndex = 0`. That is EA's own reset value, at the point EA skipped it.

## 4. Other CAS bugs found (ranked)

- **B1 [verified, IL].** `CASLogic.PropagatePhysicalProperties` **drops outfits** whose age or gender differs from the
  builder. It runs `RemoveOutfit(cat, 0, release:false)` and `simOutfit.Uncache()`, then
  `bne.un IL_078d` jumps to the loop step without calling `AddOutfit` (IL_04b0 to IL_0550 in
  `ilspycmd -il -t Sims3.Gameplay.CAS.CASLogic`; C# at CASLogic.cs:13574-13591). The outfit is lost from
  `mCurrentSimData` without being released (a leak), and the category can be left empty. Trigger: commit with
  `bAllCategories` or `bSimSave` (every category or index switch, CASLogic.cs:12580 and 12618) while a stored outfit has
  the wrong age or gender (legacy saves, or an age/gender step that did not convert every category). Repro: in Edit in
  CAS, give one outfit the wrong age, switch category, then count outfits per category before and after.
- **B2 [verified].** The same method skips the performance-career Career[0] outfit and the outfits of non-primary
  categories. Those outfits keep the **old skin, morphs and blends** after a skin change (CASLogic.cs:13582-13586). After
  CAS this gives an in-world outfit with the wrong skin tone (the whole Sim, not this glitch).
- **B3 [verified].** `LoadDB` assigns `mSkinToneKey = item6` on **every** ramp of the `KeySearch`
  (CASLogic.cs:8159-8163). The default skin is therefore whichever ramp happens to be enumerated last, which depends on
  package order. `SetDefaultBuilderValues` then uses it without an index. This is the "last item wins" loop pattern.
- **B4 [verified].** Swallowed exceptions. `SetCurrentOccultTypeSynchronous` (CASLogic.cs:12084-12092) hides any failure
  in an occult change and leaves `mLastOccultType` updated. `AgeChangeWerewolfOutfit` (3779-3818) hides failures part
  way through and can leave the Supernatural outfit half built. Found by ast-grep `try { $$$ } catch ($T) { }` over the
  CAS folders: 8 hits, the others are benign (`CASPuck` 350/1506/1544, `CASBasics` 1725, `CASController` 1826, `CASSim`
  329 animation abort).
- **B5 [verified, cosmetic].** `PetCollar` is listed twice in both body-type arrays of `PropagatePhysicalProperties`
  (13404-13495). The duplicate is harmless but wastes work.

Candidates checked and found not to be bugs: `SimBuilder.GetSecondaryNormalMapWeights` hands out its internal array,
but no caller writes to it (grep). `CASSim` animation lock has a generation check (`mPendingThread`, CASSim.cs:282-300).

## 5. Notes

- The slots 4190-4195 named in the first brief are inside docs/656's range 4170-4199; the fixes below use 4200-4219.
- Not run in game.
- Correction to §4 B1: the dropped outfit does **not** leak. `SimOutfit.Uncache()` runs before the age / gender test
  (IL_0516, ahead of IL_0533 / IL_0550), and it is the same `SimOutfitUncache` that `RemoveOutfit(release: true)` uses. See §6.1.

## 6. Fixes (2026-11-19, branch `feat/cas-fixes`, built, all default off, NOT RUN)

Each fix is a switch with a pure core, a selftest in `tools/selftest/<switch>/` and capped log lines. Counters 4200-4219
(docs/registry.md).

### 6.1 `caspropagateoutfits` (CasPropagate.cs, CasPropagateCore.cs): B1, what EA intended

The IL of `CASLogic.PropagatePhysicalProperties` [verified, `ilspycmd -il`]: `RemoveOutfit(cat, 0, false)` (IL_04b0), the
current-outfit re-add (IL_04ca), the performance Career[0] re-add (IL_0501), then `SimOutfit.Uncache()` (IL_0516), and only
after that the age test (IL_0533 `bne.un IL_078d`) and the gender test (IL_0550). So EA releases every outfit it takes off,
the dropped one included, and the drop is intended: EA's own debug outfit checks (`OutfitUtils` validators) want every
outfit to match the Sim's age and gender; `CompleteSimDescription(bAllCategories)` rebuilds every empty primary category
with `MakeCategoryAppropriate`, and `ExecuteSetCategory` does the same for an empty category on visit. Keeping or
re-adding the outfit would put back an outfit EA treats as broken; disposing it again would double-release. **Neither is
needed.** The real defect is the index: the drop moves the later outfits of the category down, but `mCurrentOutfitIndex`
and the Sim's `CareerOutfitIndex` stay. EA's tail then reloads the builder from `GetOutfit(cat, mCurrentOutfitIndex)`: the
wrong outfit, or null (the builder then keeps no parts at all). Fix: note the current `SimOutfit` object before EA's pass
(EA re-adds that same object), find it again after, move `CareerOutfitIndex` down by the Career drops below it, and reload
the builder exactly as EA's tail does when the index moved. Trigger: a stored outfit of another age or gender (legacy saves,
an undone gender step: `SetGenderStep.Undo` restores the gender but not the other categories). Counters 4200-4203.

### 6.2 `caspropagateextra` (CasPropagate.cs): B2

After EA's pass, the same rebuild EA does for the primary outfits (builder physique, the outfit's own clothing list, hair
as EA propagates it, `MakeCategoryAppropriate`, `CacheOutfit` with EA's name, in place) for the performance-career Career[0]
and for the human categories MartialArts, AuxCareer, Makeover, SkinnyDippingTowel, Massage and ChildImagination. Special
and Supernatural stay out (occult forms and special outfits carry their own skin; EA's in-world sweeps
`OccultRobot.FixupOutfits_OnRemoval` and `CompleteSimDescription` skip them too) and Singed is dropped by EA anyway. An
outfit of another age or gender is left as it is. Its own switch: it rewrites stored outfits (save data). Counters
4204-4206. [inferred] why EA skipped the performance Career[0]: not found in code; the rebuild keeps its clothing.

### 6.3 `casdefaultskin` (CasDefaultSkin.cs): B3

After EA's `LoadDB`, `mSkinToneKey` = the builder's skin while a CAS session is open (LoadDB re-runs on new content; EA's
`SkinToneKey` getter would sync it on its next read anyway), else `CASSkinTones.HumanSkinTone` (the ramp EA's own code sets
for humans in ThumbnailHelper and Genetics) when installed, else EA's pick. Every LoadDB is logged, switch on or off (10 a
minute, 100 a game): ramp count, EA's last-enumerated ramp and the one used. [inferred] the default cannot cause the
body-only tint (one skin for the whole Sim, §2), and an Edit in CAS sets the skin from the outfit; the log settles the
suspicion. Counters 4207-4209.

### 6.4 `casoccultlog` (CasOccultLog.cs): B4

No throw could be identified statically, so nothing is "fixed" blind. `HandleOccultChangeSynchronous`,
`StoreWerewolfOutfitToSupernaturalData` and `SynchronizeSimAndBuilderHairColorToOutfit` get a log-and-rethrow wrapper
(EA's own catch still swallows, so behaviour is unchanged); `AgeChangeWerewolfOutfit` logs when it returned without
reaching the outfit save (a throw in its `OutfitUtils` steps). Each line has the exception type, message and first stack
frames, and notes a null PreviewSim (the one callee that can be null on that path). The next step is to fix the throw these
lines name. Noticed, not changed: `AgeChangeWerewolfOutfit` passes `singleton.OutfitCategory` (the category on screen) to
`MakeAgeAppropriate` for the Supernatural outfit. Counters 4210-4212.

### 6.5 Duplicate PetCollar (B5): documented, not changed

Both arrays feed `RemoveParts` (a second removal is a no-op) and `ArrayList.Contains` (a duplicate changes nothing). The
duplicate is probably a typo for another pet body type, so "fixing" it means guessing which type EA meant, which would
change behaviour. Left as is; our own copy of the list in CasPropagate.cs has it once.

### 6.6 `castintreset` (CasTintReset.cs): UNCONFIRMED candidate for the tint (§3 H1)

After EA's `CASClickOnSim.Dispose` and `CASClickOnSim.SetState`, `CASModel.HighlightIndex = 0` when it was left non-zero
(EA's reset value). H2 and H3 are not covered. Do not turn it on by default until `cas-skin-probe` shows the stale index on
the body part. Counters 4213-4214.

## 7. Lighting leads (coordinator, 2026-11-19)

### 7.1 `elevatorlightrig` (ElevatorLightRig.cs): confirmed in code, fix built

`ElevatorDoors.OnEnterElevator` sets `World.ObjectSetCustomLightRig(sim, "CAS")` when a rider is registered at the door;
only `OnExitElevator` clears it. Paths checked [verified, decompiled]: the normal exit (`OnPortalStop`), the trapped-door
exit, the teleport exit, `OnLaneUnlocked` of a destroyed Sim and the woohoo reject all call `OnExitElevator`. Paths that do
not: `ElevatorPortalComponent.OnPortalActionReset` (a rider's reset; the doors forward theirs to it),
`ElevatorOnRouteActionsFinished` (route ended inside: EA resets the Sim), `ElevatorPortalComponent.OnReset`
(`mAssignedSims.Clear()`, which our `elevatorresetriders` then resets), EA's 30-minute failsafe reset and our
`elevatorridertimeout`'s (both reach `OnPortalActionReset` through the Sim reset), and our `owelevidle` idle-entry drop
(`UnregisterSim` only). Fix: after those paths, EA's own `World.ObjectSetCustomLightRig(sim, null)` for a valid Sim that
was riding and is not registered any more (a Sim handed to the next floor keeps it). All Sims (pure EA bug fix).
[inferred] whether a native Sim reset clears the rig on its own: not in script; the fix is harmless if it does. Live repro
idea: on an elevator lot, reset an NPC mid-ride and screenshot it in live mode, off and on. Counters 4215-4217; repro
`elevatorlightrig-check` (pure rule + elevator census).

### 7.2 CAS lighting stack (`LightTuning.PushValues` / `PopValues`): lead, no fix

`CASLogic.Initialize` pushes as its first statement; `Deactivate` pops as its last, after `CleanBuilder`,
`DestroyHousehold`, `DestroyPreviewSim` and `DestroyCASRoom`. `Initialize` runs from `Simulate` only while `mActive` is
false and returns false only if `SetPosition` fails (it never does), so a double push needs a throw inside `Initialize`
followed by a second entry. `Deactivate` runs from `CASModeState.Shutdown` (and `Stop`) unconditionally, so a CAS exit
before the CAS task ran `Initialize` pops without a push, and a throw in any of the four destroy calls skips the pop. All
three are code paths; none is shown to happen. Repro idea for `cas_skin_probe`: a log-only hook counting pushes and pops
on `Initialize` / `Deactivate`, read at each probe. Full moon (`LunarCycleManager` sets `FullMoon` / `DefaultLighting`
from alarms): every CAS mode state (mirror, dresser, stylist, Plan Outfit) runs `CASModeState.Startup`, which locks the
game speed to Pause, so the sim clock and its alarms stop during CAS [verified, CASModeState.cs]; the push saves the
full-moon values and the pop restores them. No bug found.

### 7.3 Options menu in CAS (`optionsnoopapply`, OptionsNoopApply.cs): lead, guard built

Player: the tint appeared after opening Options in CAS and leaving it. [verified, decompiled OptionsDialog] OK runs
`ApplyGraphicsChanges` (each `DeviceConfig.SetOption` guarded by `mNewGraphicsData != mOldGraphicsData`) and the other
`Apply*Changes` (all guarded the same way), then `UIManager.ApplyOptions()` (= native `DeviceConfig.ApplyOptions`) only
when one of them set something, and `UIManager.ChangeResolution` when the resolution / refresh / full-screen pick differs.
Cancel only ends the dialog. `SetupGraphicsControls(fromDefault: false)` fills `mOldGraphicsData` with the raw
`DeviceConfig.GetOption` values (minus 1 for the 1-based keys) and ends with `mNewGraphicsData = mOldGraphicsData`; the
slider and combo handlers write `mNewGraphicsData`. EA unsubscribes the resolution menu's handler around its programmatic
`CurrentSelection` (so such a set fires the event), but `SetupRefreshMenu` sets the refresh menu's `CurrentSelection`
without that guard. If those events arrive after the copy, OK sees a difference with no player change and re-applies
options natively; if they arrive at once, the copy hides them. Which happens is native UI behaviour, not shown. So the
claim "OK always applies" is not supported by the script code. The guard: before EA's `ApplyGraphicsChanges`, each
differing field is compared with the live DeviceConfig value and a differing mode with `DeviceConfig.GetCurrentResolution`,
and logged switch on or off ("optionsnoopapply: ..." lines are the evidence either way); with the switch on a no-op set has
the dialog's old copy aligned, so EA skips it (and ApplyOptions / ChangeResolution when nothing else changed). A real
change is applied as before. Other graphics paths in CAS: no other script caller of `UIManager.ApplyOptions` or of a
graphics `DeviceConfig.SetOption` (the remaining `SetOption` calls are gameplay flags in InWorldSubState and
SeasonsManager). Counters 4218-4219. The `cas-skin-probe` repro calls `UIManager.ApplyOptions` directly through
`reflect_call` and probes plus screenshots right after: a lighter torso there confirms the native re-apply as the trigger.

### 7.4 Photo booth: lead only

`GreetingCardTakePhotoHelper` calls `ThumbnailManager.GenerateThumbnailForSimsUsingObject` once per photo: one synchronous
native render. No script code sets a light rig or a lighting value around it, so no early exit (`DoneWithPhoto` paths, a
reset, a cancel) can leave script state behind; the portrait lighting during the photo is intended. Whether the native
render leaves a rig on the live Sim is beyond the scripts: a native lead (Ghidra on the thumbnail path) only if a probe ever
shows lighting left after a photo.

## 8. Diagnostic: `cas_skin_probe` and the repros

Agent step `cas_skin_probe` (CasHandlers.cs, read only): `highlight_index`, `selection_index`, `highlight_mode`, CASLogic's
`mHighlightModeDirty` / `mHighlightIndexDirty`, `sim_selection_mode_enabled`, the pick id decoded by
`SimBuilder.GetBodyTypeAndRegionFromPickData` (`highlight_body_type`, `highlight_region`), `selection_body_type`, the
builder's `skin_tone_key` / `skin_tone_index`, `default_ramp_field` (LoadDB's `mSkinToneKey`, read as a field because the
property overwrites it), `ramp_last_enumerated`, `human_base_ramp_installed`, the outfit category / index, the builder's
`worn_body_types`, the stored outfit's body types and skin, and `world_update_light_rigs`. The per-part light rig is native
(CAS enables custom light rigging in `CASLogic.Init`; no script assigns a rig to CAS parts), so the probe cannot read it.

Repro `cas-skin-probe`: CAS in swimwear, then the clothing tab (tops), body, swimwear, everyday, basics, hair, then a direct
`UIManager.ApplyOptions`, each followed by `cas_skin_probe` and a screenshot. The harness has no shell step, so a
full-resolution `spectacle -b -n -f -o` capture is not possible; the `screenshot` step (the game's own key C, at the game's
resolution) is used. Repro `cas-propagate`: category switches in CAS with `caspropagateoutfits`, `caspropagateextra` and
`casoccultlog` (natural trigger; N/A when the Sim has none of the outfits). Repro `elevatorlightrig-check`: the pure rule
plus an elevator census.
