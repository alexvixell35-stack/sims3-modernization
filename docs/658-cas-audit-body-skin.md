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

- The slots 4190-4195 named in the brief are inside docs/656's range 4170-4199. A later fix needs new slots.
- Not run in game.
