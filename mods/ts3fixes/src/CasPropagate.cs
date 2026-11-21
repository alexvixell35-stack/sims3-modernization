using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // caspropagateoutfits and caspropagateextra (tunings kCasPropagateOutfits / kCasPropagateExtra, default off;
    // docs/658 §6.1-6.2). One hook on CASLogic.PropagatePhysicalProperties(bool), run on every CAS commit with all
    // categories (category or outfit index switch, save; CASLogic.CommitBuilder).
    //
    // caspropagateoutfits [verified, IL IL_04b0-IL_0550 + decompiled]: EA takes each stored outfit off its category list,
    // uncaches it (SimOutfit.Uncache = SimOutfitUncache, the same release RemoveOutfit(release: true) does) and drops it
    // when its age or gender differs from the builder. The drop is EA's intent (EA's debug outfit checks want every outfit
    // to match the Sim; CompleteSimDescription and ExecuteSetCategory rebuild an empty category with
    // MakeCategoryAppropriate), and the outfit is released, so it does not leak. What EA misses: the drop shifts the later
    // outfits of that category down, while mCurrentOutfitIndex and the Sim's CareerOutfitIndex stay. EA's tail then loads
    // the current outfit back into the builder by the stale index: another outfit, or none (builder left with no parts).
    // Fix: before EA runs, note the current SimOutfit object (EA re-adds that same object) and the Career drops below
    // CareerOutfitIndex; after EA, point mCurrentOutfitIndex at that object again and move CareerOutfitIndex down by the
    // drops below it; when the current index moved, reload the builder from the right outfit exactly as EA's tail does.
    // Counters [4200] outfits EA dropped (age / gender), [4201] current index realigned, [4202] CareerOutfitIndex
    // realigned, [4203] errors.
    //
    // caspropagateextra [verified, decompiled]: EA's loop covers only the primary categories, and re-adds the
    // performance-career Career[0] unchanged, so those outfits keep the old skin, morphs and blends after a CAS edit (the
    // in-world outfit then shows the old skin tone). Fix: after EA's pass, the same rebuild EA does for the others (builder
    // physique, the outfit's own clothing, hair as EA propagates it, MakeCategoryAppropriate, CacheOutfit with EA's name)
    // for Career[0] of a performance career and for the human categories CasPropagateCore.kExtraHuman, in place (same
    // index), then the builder back to the current outfit as EA's tail leaves it. Outfits whose age / gender differ are
    // left as they are (not dropped). Counters [4204] outfits rebuilt, [4205] left (age / gender), [4206] errors.
    // Log "caspropagate: ..." capped (CasPropLog).
    public static partial class Fixes
    {
        static readonly LineCapCore sCppCap = new LineCapCore(10, 60000, 200);

        static void CasPropLog(string s)
        {
            if (sCppCap.Take(System.Environment.TickCount)) DiagLine("caspropagate: " + s);
        }

        static void ApplyCasPropagate()
        {
            Patch(typeof(CASLogic), "PropagatePhysicalProperties", new Type[] { typeof(bool) }, typeof(CasPropHook), "PropagateHook", "PropagateOrig");
        }

        // the clothing body types EA's loop strips from the builder and copies from each outfit (its two lists, without the
        // duplicate PetCollar, docs/658 §6.5): makeup only when EA is not propagating a makeup change
        static readonly BodyTypes[] kCppTypesBase = {
            BodyTypes.Hair, BodyTypes.Scalp, BodyTypes.Face, BodyTypes.FullBody, BodyTypes.UpperBody, BodyTypes.LowerBody,
            BodyTypes.Shoes, BodyTypes.Necklace, BodyTypes.Earrings, BodyTypes.LeftEarring, BodyTypes.RightEarring,
            BodyTypes.Glasses, BodyTypes.Bracelet, BodyTypes.Ring, BodyTypes.Gloves, BodyTypes.Socks, BodyTypes.Armband,
            BodyTypes.LeftGarter, BodyTypes.RightGarter, BodyTypes.PetBlanket, BodyTypes.PetBreastCollar, BodyTypes.PetCollar,
            BodyTypes.PetSaddle };
        static readonly BodyTypes[] kCppTypesMakeup = { BodyTypes.EyeShadow, BodyTypes.EyeLiner, BodyTypes.Blush, BodyTypes.FirstFace, BodyTypes.CostumeMakeup };

        static BodyTypes[] CppTypes(bool makeupPending)
        {
            if (makeupPending) return kCppTypesBase;
            BodyTypes[] t = new BodyTypes[kCppTypesBase.Length + kCppTypesMakeup.Length];
            kCppTypesBase.CopyTo(t, 0);
            kCppTypesMakeup.CopyTo(t, kCppTypesBase.Length);
            return t;
        }

        static readonly OutfitCategories[] kCppHuman = { OutfitCategories.Career, OutfitCategories.Formalwear, OutfitCategories.Athletic,
            OutfitCategories.Swimwear, OutfitCategories.Sleepwear, OutfitCategories.Everyday, OutfitCategories.Naked, OutfitCategories.Outerwear };

        public abstract class CasPropHook : CASLogic
        {
            int Species()
            {
                CASAgeGenderFlags s = mBuilder.Species;
                return s == CASAgeGenderFlags.Human ? CasPropagateCore.kHuman : s == CASAgeGenderFlags.Horse ? CasPropagateCore.kHorse : CasPropagateCore.kOtherPet;
            }

            bool Mismatch(SimOutfit o)
            {
                if (o == null) return false;
                CASAgeGenderFlags ags = o.AgeGenderSpecies;
                return mBuilder.Age != (ags & CASAgeGenderFlags.AgeMask) || mBuilder.Gender != (ags & CASAgeGenderFlags.GenderMask);
            }

            bool PerfCareer()
            {
                return mSimDescEditExisting != null && mSimDescEditExisting.OccupationAsPerformanceCareer != null;
            }

            // EA's drops in one category, as its loop will see them
            bool[] CppDrops(OutfitCategories cat)
            {
                ArrayList l = mCurrentSimData.GetOutfits(cat);
                bool[] m = new bool[l.Count];
                for (int i = 0; i < m.Length; i++) m[i] = Mismatch(l[i] as SimOutfit);
                return CasPropagateCore.Drops(m, cat == mCurrentOutfitCategory ? mCurrentOutfitIndex : -1,
                    cat == OutfitCategories.Career && PerfCareer() ? 0 : -1);
            }

            public void PropagateHook(bool bSimSave)
            {
                bool fix = ShortcutOn("caspropagateoutfits"), extra = ShortcutOn("caspropagateextra");
                if (!fix && !extra) { PropagateOrig(bSimSave); return; }
                int[] c = PatchCounters();
                bool runs = false, makeupPending = mbMakeUpNeedsPropagation, hairPending = mbHairNeedsPropagation;
                ResourceKey hairBefore = mSelectedHairPart.Key;
                SimOutfit current = null;
                OutfitCategories curCat = mCurrentOutfitCategory;
                int curIdx = mCurrentOutfitIndex, careerDropsBelow = 0;
                try
                {
                    runs = mBuilder != null && mCurrentSimData != null && CasPropagateCore.Runs(Species(), (uint)curCat,
                        (uint)OutfitUtils.GetHumanPrimaryOutfitCategories(), (uint)OutfitCategories.PrimaryHorseCategories);
                    if (runs && fix)
                    {
                        current = mCurrentSimData.GetOutfit(curCat, curIdx);
                        if (Species() == CasPropagateCore.kHuman)
                            foreach (OutfitCategories cat in kCppHuman)
                            {
                                if (cat == OutfitCategories.Outerwear && !GameUtils.IsInstalled(ProductVersion.EP8)) continue;
                                bool[] d = CppDrops(cat);
                                for (int i = 0; i < d.Length; i++)
                                    if (d[i]) { c[4200]++; CasPropLog("EA drops " + cat + "[" + i + "] (age / gender differ from the builder " + mBuilder.Age + " " + mBuilder.Gender + ")"); }
                                if (cat == OutfitCategories.Career) careerDropsBelow = CasPropagateCore.DropsBelow(d, mCurrentSimData.CareerOutfitIndex);
                            }
                    }
                }
                catch (Exception ex) { c[4203]++; runs = false; CasPropLog("error before EA's pass: " + ex.GetType().Name); }
                PropagateOrig(bSimSave);
                if (!runs) return;
                bool reload = false;
                if (fix)
                {
                    try
                    {
                        if (current != null && mCurrentOutfitCategory == curCat && mCurrentOutfitIndex == curIdx)
                        {
                            ArrayList l = mCurrentSimData.GetOutfits(curCat);
                            int ni = CasPropagateCore.Realign(l.IndexOf(current), curIdx, 0, l.Count);
                            if (ni != curIdx)
                            {
                                c[4201]++; reload = true;
                                CasPropLog("current " + curCat + " index " + curIdx + " -> " + ni + " (EA dropped outfits below it)");
                                mCurrentOutfitIndex = ni;
                            }
                        }
                        if (careerDropsBelow > 0)
                        {
                            int old = mCurrentSimData.CareerOutfitIndex;
                            int ni = CasPropagateCore.Realign(-1, old, careerDropsBelow, mCurrentSimData.GetOutfitCount(OutfitCategories.Career));
                            if (ni != old) { c[4202]++; mCurrentSimData.CareerOutfitIndex = ni; CasPropLog("CareerOutfitIndex " + old + " -> " + ni); }
                        }
                    }
                    catch (Exception ex) { c[4203]++; CasPropLog("error after EA's pass: " + ex.GetType().Name); }
                }
                if (extra && Species() == CasPropagateCore.kHuman)
                {
                    try { if (CppExtra(bSimSave, makeupPending, hairPending || mSelectedHairPart.Key != hairBefore, c)) reload = true; }
                    catch (Exception ex) { c[4206]++; reload = true; CasPropLog("error in the extra pass: " + ex.GetType().Name); }
                }
                if (reload)
                {
                    try { CppReloadCurrent(); }
                    catch (Exception ex) { c[4203]++; CasPropLog("error reloading the current outfit: " + ex.GetType().Name); }
                }
            }

            // EA's tail: the builder back to the current outfit's parts
            void CppReloadCurrent()
            {
                mBuilder.RemoveParts(Enum.GetValues(typeof(BodyTypes)) as BodyTypes[]);
                SimOutfit o = mCurrentSimData.GetOutfit(mCurrentOutfitCategory, mCurrentOutfitIndex);
                if (o == null) return;
                foreach (CASPart p in o.Parts)
                {
                    mBuilder.AddPart(p);
                    OutfitUtils.ApplyPresetStringToPart(mBuilder, p, o.GetPartPreset(p.Key));
                }
            }

            // true when the builder was changed (the caller reloads the current outfit)
            bool CppExtra(bool bSimSave, bool makeupPending, bool hairChanged, int[] c)
            {
                BodyTypes[] types = CppTypes(makeupPending);
                ArrayList typeList = ArrayList.Adapter(types);
                List<OutfitCategories> cats = new List<OutfitCategories>();
                if (PerfCareer()) cats.Add(OutfitCategories.Career);
                foreach (uint u in CasPropagateCore.kExtraHuman) cats.Add((OutfitCategories)u);
                bool touched = false;
                foreach (OutfitCategories cat in cats)
                {
                    object[] outfits = mCurrentSimData.GetOutfits(cat).ToArray();
                    for (int i = 0; i < outfits.Length; i++)
                    {
                        SimOutfit o = outfits[i] as SimOutfit;
                        if (o == null) continue;
                        bool mismatch = Mismatch(o);
                        if (cat == OutfitCategories.Career)
                        {
                            // only the performance-career Career[0]: EA rebuilt the others itself
                            if (i > 0) break;
                            if (!CasPropagateCore.PerfCareerZero(true, true, (uint)cat, i, (uint)mCurrentOutfitCategory, mCurrentOutfitIndex, mismatch))
                            {
                                if (mismatch) c[4205]++;
                                break;
                            }
                        }
                        else
                        {
                            if (cat == mCurrentOutfitCategory && i == mCurrentOutfitIndex) continue;
                            if (mismatch) { c[4205]++; continue; }
                        }
                        touched = true;
                        o.Uncache();
                        mBuilder.RemoveParts(types);
                        foreach (CASPart p in o.Parts)
                        {
                            if (p.BodyType == BodyTypes.Hair)
                            {
                                if (hairChanged && mCurrentSimData.PropagateHairStyle && mSelectedHairPart.Key != ResourceKey.kInvalidResourceKey
                                    && OutfitUtils.PartMatchesSim(mBuilder, (uint)cat, mSelectedHairPart))
                                {
                                    mBuilder.AddPart(mSelectedHairPart);
                                    mBuilder.SetPartPreset(mSelectedHairPart.Key, null, mSelectedHairPartPreset);
                                }
                                else
                                {
                                    mBuilder.AddPart(p);
                                    string preset = o.GetPartPreset(p.Key);
                                    if (preset != null) mBuilder.SetPartPreset(p.Key, null, preset);
                                }
                                ExecuteUpdateHairColors(BodyTypes.Hair);
                            }
                            else if (typeList.Contains(p.BodyType))
                            {
                                mBuilder.AddPart(p);
                                OutfitUtils.ApplyPresetStringToPart(mBuilder, p, o.GetPartPreset(p.Key));
                            }
                        }
                        OutfitUtils.MakeCategoryAppropriate(mBuilder, cat, mCurrentSimData);
                        ResourceKey key = mBuilder.CacheOutfit("CASCore.PropagatePhysicalProperties_" + GetOutfitName(cat, i), bSimSave);
                        mCurrentSimData.RemoveOutfit(cat, i, false);
                        mCurrentSimData.AddOutfit(new SimOutfit(key), cat, i);
                        c[4204]++;
                        CasPropLog("rebuilt " + cat + "[" + i + "] with the builder's skin, morphs and blends");
                    }
                }
                return touched;
            }

            public void PropagateOrig(bool bSimSave) { } // receives CASLogic.PropagatePhysicalProperties's original body
        }
    }
}
