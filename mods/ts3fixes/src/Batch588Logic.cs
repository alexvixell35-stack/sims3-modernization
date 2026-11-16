using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.CelebritySystem;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.StoryProgression;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Batch 10 (docs/588 B10): the story progression leads. User 2026-11-15: "fix, make it as EA intended, even for our
    // household" -- no household exemption beyond what EA's own code has; the household-visible ones say so. All default
    // off. simler90 UPDATE242 changes none of the hooked methods except StoryProgressionService.TryOneAction, where it adds
    // one notification call before Execute; sprecheck makes that same call (found by reflection, only when the loaded
    // gameplay assembly references simler90Extras).
    public static partial class Fixes
    {
        static Type SpType(string name) { return typeof(Demographics).GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic); }
        static readonly Type sSpHist = SpType("HistogramDemographic");
        static readonly MethodInfo sSpAddItem = sSpHist == null ? null : sSpHist.GetMethod("AddItem", kInstAll, null, new Type[] { typeof(float) }, null);
        static readonly MethodInfo sSpBucketIndex = sSpHist == null ? null : sSpHist.GetMethod("BucketIndex", kInstAll, null, new Type[] { typeof(float) }, null);
        static readonly FieldInfo sSpErrCurve = sSpHist == null ? null : sSpHist.GetField("mErrorCurve", kInstAll);
        static readonly FieldInfo sSpTuning = typeof(Demographics.Demographic).GetField("mTuning", kInstAll);


        // ---- B10 §1 sprelcount (tuning kSpRelCount) / §2 spcollectnull (tuning kSpCollectNull) ----------------------------
        // [verified, decompiled] Demographics.Collect builds every histogram with the incremental Add(SimDescription). For
        // NumberFriends, NumberBestFriends, NumberEnemies and HighestRomance that Add also moves each related Sim's entry up
        // one (meant for a Sim joining a world already counted): during Collect the related Sim is either not added yet (a
        // phantom entry one bucket up) or added once already (moved twice). The histograms skew toward "more friends / more
        // romance", and the cost is O(Sims x relationships^2). Fix (sprelcount on): during Collect those four add only the
        // Sim's own count (EA's own GetXFor helper), so the histogram is the true per-Sim count. Actions' predictions keep
        // EA's incremental Add. [latent] Collect skips special households with `h != null && h.IsSpecialHousehold`, so a null
        // entry is dereferenced; spcollectnull skips it. Collect is EA's otherwise, in EA's order.
        // Counters: [2929] Collects with own-count histograms, [2930] Collects EA built incrementally (switch off), [2931]
        // errors; [2932] null households skipped, [2933] null households seen with the switch off, [2934] errors.
        static readonly FieldInfo sCoLot = typeof(Demographics).GetField("mLotDemographics", kInstAll);
        static readonly FieldInfo sCoHh = typeof(Demographics).GetField("mHouseholdDemographics", kInstAll);
        static readonly FieldInfo sCoSim = typeof(Demographics).GetField("mSimDemographics", kInstAll);
        static readonly FieldInfo sCoAll = typeof(Demographics).GetField("mDemographics", kInstAll);
        static Dictionary<Type, MethodInfo> sCoOwn;
        static string sCoMissing = "";

        static void ApplySpRelCount()
        {
            sCoOwn = new Dictionary<Type, MethodInfo>();
            string[][] own = { new[] { "NumberFriends", "GetNumFriendsFor" }, new[] { "NumberBestFriends", "GetNumFriendsFor" },
                new[] { "NumberEnemies", "GetNumEnemiesFor" }, new[] { "HighestRomance", "GetHighestRomanceFor" } };
            string miss = "";
            foreach (string[] o in own)
            {
                Type t = SpType(o[0]);
                MethodInfo m = t == null ? null : t.GetMethod(o[1], kInstAll, null, new Type[] { typeof(SimDescription) }, null);
                if (m == null) miss += o[0] + "." + o[1] + " ";
                else sCoOwn[t] = m;
            }
            sCoMissing = (SpNames("Demographics.mLotDemographics", sCoLot, "mHouseholdDemographics", sCoHh, "mSimDemographics", sCoSim,
                "mDemographics", sCoAll, "HistogramDemographic.AddItem", sSpAddItem) + " " + miss).Trim();
            if (sCoMissing.Length > 0) { AppliedLog().Add("sprelcount/spcollectnull: not found: " + sCoMissing); return; }
            Patch(typeof(Demographics), "Collect", null, typeof(BSpCollectHook), "CollectHook", "CollectOrig");
        }

        // Pure model of one histogram build. adj[i] = the Sims i counts (symmetric); counts[] = the histogram by value.
        // ownOnly: the fix; else EA's incremental Add in list order (RemoveItem drops an entry only when one is there).
        internal static int[] SpRelHistogram(int[][] adj, bool ownOnly, int maxValue)
        {
            int[] h = new int[maxValue + 2];
            for (int i = 0; i < adj.Length; i++)
            {
                if (!ownOnly)
                    foreach (int f in adj[i])
                    {
                        int c = Math.Min(adj[f].Length, maxValue);
                        if (h[c] > 0) h[c]--;
                        h[Math.Min(c + 1, maxValue + 1)]++;
                    }
                h[Math.Min(adj[i].Length, maxValue)]++;
            }
            return h;
        }

        // Pure: does a relationship histogram take the Sim's own count only? (EA: the incremental Add of SpRelHistogram)
        internal static bool SpOwnOnly(bool on, bool relHistogram) { return on && relHistogram; }

        // Pure: does Collect skip this household? (EA: special only; fix: null too)
        internal static bool SpCollectSkips(bool on, bool isNull, bool special) { return isNull ? on : special; }

        public class BSpCollectHook
        {
            public void CollectHook()
            {
                bool rel = ShortcutOn("sprelcount"), nul = ShortcutOn("spcollectnull");
                if (sCoMissing.Length > 0 || (!rel && !nul))
                {
                    if (sCoMissing.Length == 0)
                    {
                        PatchCounters()[2930]++;
                        foreach (Household h in Household.sHouseholdList) if (h == null) { PatchCounters()[2933]++; break; }
                    }
                    CollectOrig();
                    return;
                }
                Demographics d = (Demographics)(object)this;
                List<Demographics.Demographic> lots, hhs, sims, all;
                try
                {
                    lots = (List<Demographics.Demographic>)sCoLot.GetValue(d);
                    hhs = (List<Demographics.Demographic>)sCoHh.GetValue(d);
                    sims = (List<Demographics.Demographic>)sCoSim.GetValue(d);
                    all = (List<Demographics.Demographic>)sCoAll.GetValue(d);
                }
                catch (Exception) { PatchCounters()[2931]++; CollectOrig(); return; }
                // EA's Collect, in EA's order
                foreach (Demographics.Demographic dm in all) dm.Clear();  // EA's private Demographics.Clear
                foreach (Lot lot in LotManager.AllLots)
                    foreach (Demographics.Demographic dm in lots) dm.Add(lot);
                foreach (Household h in Household.sHouseholdList)
                {
                    if (h == null)
                    {
                        if (!SpCollectSkips(nul, true, false)) throw new NullReferenceException("Demographics.Collect: null household");  // EA's dereference
                        PatchCounters()[2932]++;
                        continue;
                    }
                    if (SpCollectSkips(nul, false, h.IsSpecialHousehold)) continue;
                    foreach (Demographics.Demographic dm in hhs) dm.Add(h);
                    foreach (SimDescription sd in h.AllSimDescriptions)
                    {
                        if (sd.IsGhost && !sd.IsPlayableGhost) continue;
                        foreach (Demographics.Demographic dm in sims)
                        {
                            if (!sd.IsHuman && dm.IgnorePets) continue;
                            if (SpOwnOnly(rel, sCoOwn.ContainsKey(dm.GetType())) && SpAddOwn(dm, sd)) continue;
                            dm.Add(sd);
                        }
                    }
                }
                d.TotalError = 0f;
                foreach (Demographics.Demographic dm in all)
                {
                    dm.CustomCollect();
                    dm.Complete();
                    d.TotalError += dm.Error;
                }
                if (rel) PatchCounters()[2929]++;
            }

            public void CollectOrig() { }
        }

        // The fix's add for the four relationship histograms: the Sim's own count only. false: not one of the four.
        static bool SpAddOwn(Demographics.Demographic dm, SimDescription sd)
        {
            MethodInfo own;
            if (!sCoOwn.TryGetValue(dm.GetType(), out own)) return false;
            try
            {
                int n = (int)own.Invoke(dm, new object[] { sd });
                sSpAddItem.Invoke(dm, new object[] { (float)n });
                return true;
            }
            catch (Exception) { PatchCounters()[2931]++; return false; }
        }

        // ---- B10 §3 spclonedict (tuning kSpCloneDict) --------------------------------------------------------------------
        // [verified, decompiled] HouseholdTooBigDemographic and HouseholdTooSmallDemographic copy constructors assign
        // `mLotsOverfull = other.mLotsOverfull` (resp. Underfull): every clone made to score an action shares the live
        // dictionary, so a scored-but-not-run action's Add/Remove changes the real demographic, and later scores in the
        // round depend on the scoring order. Fix: Clone gives the copy its own dictionary (the int counters were already
        // copied by value). Counters: [2935] clones given their own dictionary, [2936] clones sharing it (switch off), [2937]
        // errors.
        static readonly Type sCdBig = SpType("HouseholdTooBigDemographic"), sCdSmall = SpType("HouseholdTooSmallDemographic");
        static readonly FieldInfo sCdBigDict = sCdBig == null ? null : sCdBig.GetField("mLotsOverfull", kInstAll);
        static readonly FieldInfo sCdSmallDict = sCdSmall == null ? null : sCdSmall.GetField("mLotsUnderfull", kInstAll);
        static string sCdMissing = "";

        static void ApplySpCloneDict()
        {
            sCdMissing = SpNames("HouseholdTooBigDemographic.mLotsOverfull", sCdBigDict, "HouseholdTooSmallDemographic.mLotsUnderfull", sCdSmallDict);
            if (sCdMissing.Length > 0) { AppliedLog().Add("spclonedict: not found: " + sCdMissing); return; }
            Patch(sCdBig, "Clone", null, typeof(BSpBigHook), "CloneHook", "CloneOrig");
            Patch(sCdSmall, "Clone", null, typeof(BSpSmallHook), "CloneHook", "CloneOrig");
        }

        // Pure: the dictionary the clone gets.
        internal static Dictionary<ulong, int> SpCloneDict(bool on, Dictionary<ulong, int> live)
        {
            return on && live != null ? new Dictionary<ulong, int>(live) : live;
        }

        static void SpCloneFix(object clone, FieldInfo f)
        {
            try
            {
                Dictionary<ulong, int> live = f.GetValue(clone) as Dictionary<ulong, int>;
                bool on = ShortcutOn("spclonedict");
                if (!on) { PatchCounters()[2936]++; return; }
                f.SetValue(clone, SpCloneDict(true, live));
                PatchCounters()[2935]++;
            }
            catch (Exception) { PatchCounters()[2937]++; }
        }

        // ---- B10 §4 splotlookup (tuning kSpLotLookup) --------------------------------------------------------------------
        // [verified, decompiled] HouseholdTooBig, HouseholdTooSmall and HorsePopulation Add(HouseholdCreationSpec) look the
        // spec's home up with Household.Find(spec.HomeLotId): a lot id passed to a household-id lookup, always null, so a
        // predicted move (MoveToLarger/Smaller, MoveIn, CreateAndMoveIn) never counts toward too-big, too-small or box
        // stalls and those actions get no credit for what they fix. TooSmall also reads LotHome before its null test. Fix:
        // the lot is LotManager.GetLot(spec.HomeLotId) (the id every caller stores there), with one null-safe dorm /
        // apartment test; the rest of each Add is EA's. Counters: [2938] specs counted on their lot, [2939] specs EA dropped
        // that the lot would have counted (switch off), [2940] errors.
        static readonly Type sLlHorse = SpType("HorsePopulationDemographic");
        static readonly FieldInfo sLlBigSims = sCdBig == null ? null : sCdBig.GetField("mSimsOver", kInstAll);
        static readonly FieldInfo sLlSmallSims = sCdSmall == null ? null : sCdSmall.GetField("mSimsUnder", kInstAll);
        static readonly FieldInfo sLlHorses = sLlHorse == null ? null : sLlHorse.GetField("mNumberOfHouseholdsWithHorses", kInstAll);
        static readonly FieldInfo sLlStalls = sLlHorse == null ? null : sLlHorse.GetField("mNumberOfHouseholdsWithBoxStalls", kInstAll);
        static string sLlMissing = "";
        static readonly Type[] kSpSpecArg = { typeof(SimUtils.HouseholdCreationSpec) };

        static void ApplySpLotLookup()
        {
            sLlMissing = SpNames("HouseholdTooBigDemographic.mLotsOverfull", sCdBigDict, "mSimsOver", sLlBigSims, "HouseholdTooSmallDemographic.mLotsUnderfull", sCdSmallDict,
                "mSimsUnder", sLlSmallSims, "HorsePopulationDemographic", sLlHorse, "mNumberOfHouseholdsWithHorses", sLlHorses, "mNumberOfHouseholdsWithBoxStalls", sLlStalls);
            if (sLlMissing.Length > 0) { AppliedLog().Add("splotlookup: not found: " + sLlMissing); return; }
            Patch(sCdBig, "Add", kSpSpecArg, typeof(BSpBigHook), "SpecHook", "SpecOrig");
            Patch(sCdSmall, "Add", kSpSpecArg, typeof(BSpSmallHook), "SpecHook", "SpecOrig");
            Patch(sLlHorse, "Add", kSpSpecArg, typeof(BSpHorseHook), "SpecHook", "SpecOrig");
        }

        // Pure: the Sims over (big) or under (small) the lot's beds; 0 when the lot does not count.
        internal static int SpLotExcess(bool lotFound, bool dormOrApartment, int sims, int beds, bool tooBig)
        {
            if (!lotFound || dormOrApartment) return 0;
            int n = tooBig ? sims - beds : beds - sims;
            return n > 0 ? n : 0;
        }

        static Lot SpSpecLot(SimUtils.HouseholdCreationSpec spec) { return spec == null ? null : LotManager.GetLot(spec.HomeLotId); }

        static int SpSpecExcess(SimUtils.HouseholdCreationSpec spec, bool tooBig)
        {
            Lot lot = SpSpecLot(spec);
            if (lot == null) return 0;
            Lot.LotMetrics metrics = default(Lot.LotMetrics);
            if (!lot.IsDormitoryLot && !lot.IsApartmentLot) lot.GetLotMetrics(ref metrics);
            return SpLotExcess(true, lot.IsDormitoryLot || lot.IsApartmentLot, spec.Sims.Count, metrics.BedCount, tooBig);
        }

        // Shared body of the TooBig / TooSmall fix (EA's Add with the lot lookup). true: handled (switch on), skip EA's.
        static bool SpSpecAdd(object demo, SimUtils.HouseholdCreationSpec spec, FieldInfo dictF, FieldInfo sumF, bool tooBig)
        {
            try
            {
                bool on = ShortcutOn("splotlookup");
                Dictionary<ulong, int> dict = (Dictionary<ulong, int>)dictF.GetValue(demo);
                if (spec == null || dict.ContainsKey(spec.HomeLotId)) return on;
                int n = SpSpecExcess(spec, tooBig);
                if (!on) { if (n > 0) PatchCounters()[2939]++; return false; }
                if (n > 0)
                {
                    dict.Add(spec.HomeLotId, n);
                    sumF.SetValue(demo, (int)sumF.GetValue(demo) + n);
                    PatchCounters()[2938]++;
                }
                return true;
            }
            catch (Exception) { PatchCounters()[2940]++; return false; }
        }

        public class BSpBigHook
        {
            public Demographics.Demographic CloneHook()
            {
                Demographics.Demographic c = CloneOrig();
                if (c != null && sCdMissing.Length == 0) SpCloneFix(c, sCdBigDict);
                return c;
            }
            public Demographics.Demographic CloneOrig() { return null; }

            public void SpecHook(SimUtils.HouseholdCreationSpec household)
            {
                if (sLlMissing.Length == 0 && SpSpecAdd(this, household, sCdBigDict, sLlBigSims, true)) return;
                SpecOrig(household);  // EA's
            }
            public void SpecOrig(SimUtils.HouseholdCreationSpec household) { }
        }

        public class BSpSmallHook
        {
            public Demographics.Demographic CloneHook()
            {
                Demographics.Demographic c = CloneOrig();
                if (c != null && sCdMissing.Length == 0) SpCloneFix(c, sCdSmallDict);
                return c;
            }
            public Demographics.Demographic CloneOrig() { return null; }

            public void SpecHook(SimUtils.HouseholdCreationSpec household)
            {
                if (sLlMissing.Length == 0 && SpSpecAdd(this, household, sCdSmallDict, sLlSmallSims, false)) return;
                SpecOrig(household);  // EA's
            }
            public void SpecOrig(SimUtils.HouseholdCreationSpec household) { }
        }

        public class BSpHorseHook
        {
            public void SpecHook(SimUtils.HouseholdCreationSpec household)
            {
                bool stall = false;
                try
                {
                    Lot lot = SpSpecLot(household);
                    stall = lot != null && lot.CountObjects<IBoxStall>() != 0;
                }
                catch (Exception) { PatchCounters()[2940]++; }
                if (sLlMissing.Length > 0 || !stall) { SpecOrig(household); return; }
                if (!ShortcutOn("splotlookup")) { PatchCounters()[2939]++; SpecOrig(household); return; }
                SpecOrig(household);  // EA's: the horse count, and its stall lookup (finds no household, adds nothing)
                sLlStalls.SetValue(this, (uint)sLlStalls.GetValue(this) + 1u);
                PatchCounters()[2938]++;
            }
            public void SpecOrig(SimUtils.HouseholdCreationSpec household) { }
        }

        // ---- B10 §5 spbucketindex (tuning kSpBucketIndex) ----------------------------------------------------------------
        // [verified, decompiled] HistogramDemographic.GetActionItem collects the under-filled buckets in list2 and picks
        // one by weight, num being its position in list2. It stores `ForcedBucketNumber = num`, but StoryAction.IsInBucket
        // compares ForcedBucketNumber with BucketOf(sim), a bucket index: targeted actions pick Sims and households from
        // the wrong bucket (or none). The increase/decrease choice also reads `mErrorCurve.Points[list2[num +- 1]].y`,
        // bucket numbers used as curve point indices. Fix: ForcedBucketNumber = list2[num]; the neighbour test compares the
        // error EA recorded for those two buckets (the first point of each, as EA's loop meets them). Everything else,
        // including the draw order of the random calls, is EA's. Counters: [2941] targeted actions with the bucket
        // index, [2942] targeted draws EA made (switch off), [2943] errors.
        static readonly FieldInfo sBiForced = typeof(StoryProgressionService.StoryActionCreationParams).GetField("ForcedBucketNumber");
        static string sBiMissing = "";

        static void ApplySpBucketIndex()
        {
            sBiMissing = SpNames("HistogramDemographic", sSpHist, "mErrorCurve", sSpErrCurve, "BucketIndex", sSpBucketIndex, "Demographic.mTuning", sSpTuning);
            if (sBiMissing.Length > 0) { AppliedLog().Add("spbucketindex: not found: " + sBiMissing); return; }
            Patch(sSpHist, "GetActionItem", null, typeof(BSpHistHook), "ActionHook", "ActionOrig");
        }

        // Pure: increase (true) or decrease for pick num of count, given the errors of list2[num-1] and list2[num+1].
        internal static bool SpIncrease(int num, int count, float yPrev, float yNext)
        {
            if (num == 0) return true;
            if (num == count - 1) return false;
            return !(yPrev < yNext);
        }

        // Pure: the forced bucket number stored.
        internal static int SpForced(bool on, int num, List<int> buckets) { return on && num >= 0 && num < buckets.Count ? buckets[num] : num; }

        public class BSpHistHook
        {
            public StoryAction ActionHook()
            {
                if (sBiMissing.Length > 0) return ActionOrig();
                DesireCurve err;
                DemographicTuning tun;
                try
                {
                    err = sSpErrCurve.GetValue(this) as DesireCurve;
                    tun = sSpTuning.GetValue(this) as DemographicTuning;
                }
                catch (Exception) { PatchCounters()[2943]++; return ActionOrig(); }
                if (err == null || tun == null) return ActionOrig();  // EA's untargeted coin flip, nothing to fix
                if (!ShortcutOn("spbucketindex")) { PatchCounters()[2942]++; return ActionOrig(); }
                // EA's GetActionItem with the bucket index stored
                List<string> list = null;
                int num = -1;
                List<int> buckets = new List<int>();
                if (!RandomUtil.RandomChance01(Demographics.kChanceOfRandomHistogramAction))
                {
                    List<float> weights = new List<float>(), ys = new List<float>();
                    Vector2[] pts = err.Points;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        if (!(pts[i].y < 0f)) continue;
                        int b = (int)sSpBucketIndex.Invoke(this, new object[] { pts[i].x });
                        if (b == -1 || buckets.Contains(b)) continue;
                        float w = tun.GetBucketWeight(b) * pts[i].y;
                        buckets.Add(b);
                        weights.Add(0f - w);
                        ys.Add(pts[i].y);
                    }
                    if (buckets.Count > 0)
                    {
                        num = RandomUtil.GetWeightedIndex(weights.ToArray());
                        bool inc = num == 0 || num == buckets.Count - 1 ? SpIncrease(num, buckets.Count, 0f, 0f) : SpIncrease(num, buckets.Count, ys[num - 1], ys[num + 1]);
                        list = inc ? tun.IncreaseActions : tun.DecreaseActions;
                    }
                }
                if (list == null) list = !RandomUtil.CoinFlip() ? tun.IncreaseActions : tun.DecreaseActions;
                if (list == null || list.Count == 0) return null;
                StoryProgressionService.StoryActionCreationParams p = new StoryProgressionService.StoryActionCreationParams(RandomUtil.GetRandomObjectFromList(list));
                if (num != -1)
                {
                    p.ForcedBucketNumber = SpForced(true, num, buckets);
                    p.TriggeringDemographic = (Demographics.Demographic)(object)this;
                    PatchCounters()[2941]++;
                }
                return StoryProgressionService.CreateAction(p);
            }

            public StoryAction ActionOrig() { return null; }
        }

        // ---- B10 §6 spcreatemovein (tuning kSpCreateMoveIn) --------------------------------------------------------------
        // [verified, decompiled] "Create and Move In" scores through CombinationAction.UpdateDemographics: CreateHousehold
        // adds the new family's spec (HomeLotId 0), then MoveInHousehold adds the same spec again. The prediction counts the
        // family twice and no lot becoming occupied. Fix: for CreateAndMoveIn only, the spec is added once with HomeLotId set
        // to the lot MoveIn picked (restored right after: MoveIn's IsValid needs it 0), then its Sims as CreateHousehold
        // does. Counters: [2944] predictions made once on the lot, [2945] double predictions (switch off), [2946] errors.
        static readonly Type sCmType = typeof(StoryAction).Assembly.GetType("Sims3.Gameplay.StoryProgression.CreateAndMoveIn");
        static readonly Type sCmCreate = typeof(StoryAction).Assembly.GetType("Sims3.Gameplay.StoryProgression.CreateHousehold");
        static readonly FieldInfo sCmSpec = sCmCreate == null ? null : sCmCreate.GetField("NewHousehold", kInstAll);
        static readonly FieldInfo sCmLot = typeof(MoveInHousehold).GetField("mLotId", kInstAll);
        static string sCmMissing = "";

        static void ApplySpCreateMoveIn()
        {
            sCmMissing = SpNames("CreateAndMoveIn", sCmType, "CreateHousehold.NewHousehold", sCmSpec, "MoveInHousehold.mLotId", sCmLot);
            if (sCmMissing.Length > 0) { AppliedLog().Add("spcreatemovein: not found: " + sCmMissing); return; }
            Patch(typeof(CombinationAction), "UpdateDemographics", null, typeof(BSpComboHook), "UpdateHook", "UpdateOrig");
        }

        // Pure: how many times the family spec is added, and does it carry a lot.
        internal static int SpSpecAdds(bool on) { return on ? 1 : 2; }

        public class BSpComboHook
        {
            public void UpdateHook(IEnumerable<Demographics.Demographic> demographics)
            {
                CombinationAction ca = (CombinationAction)(object)this;
                if (sCmMissing.Length > 0 || ca.GetType() != sCmType) { UpdateOrig(demographics); return; }
                SimUtils.HouseholdCreationSpec spec = null;
                ulong lotId = 0;
                try
                {
                    spec = ca.ActionA == null ? null : sCmSpec.GetValue(ca.ActionA) as SimUtils.HouseholdCreationSpec;
                    MoveInHousehold mi = ca.ActionB as MoveInHousehold;
                    lotId = mi == null ? 0 : (ulong)sCmLot.GetValue(mi);
                }
                catch (Exception) { PatchCounters()[2946]++; spec = null; }
                if (spec == null || lotId == 0) { UpdateOrig(demographics); return; }
                if (SpSpecAdds(ShortcutOn("spcreatemovein")) != 1) { PatchCounters()[2945]++; UpdateOrig(demographics); return; }
                ulong was = spec.HomeLotId;
                spec.HomeLotId = lotId;
                try
                {
                    foreach (Demographics.Demographic d in demographics) d.Add(spec);
                    foreach (SimUtils.SimCreationSpec sim in spec.Sims)
                        foreach (Demographics.Demographic d in demographics) d.Add(sim);
                    PatchCounters()[2944]++;
                }
                finally { spec.HomeLotId = was; }
            }

            public void UpdateOrig(IEnumerable<Demographics.Demographic> demographics) { }
        }

        // ---- B10 §7 splotdensity (tuning kSpLotDensity) ------------------------------------------------------------------
        // [verified, decompiled] LotDensity counts lots that are not world, community, EP1 player-ownable or EP9 apartment
        // lots, but counts every household with a home lot as occupying one. Households on the left-out lots overstate
        // density (fewer move-ins, more move-outs in worlds with such lots). Fix: one predicate (EA's own lot test) for
        // the lots and for the households' (and predicted specs') home lots. AllowMoreStoryProgressionMoveIns is left as
        // EA's: its uint difference is cast to int, so the doc's underflow already reads as a negative and refuses.
        // Counters: [2947] households / specs counted by the lot test, [2948] off-lot households EA counted (switch off),
        // [2949] errors.
        static readonly Type sLdType = SpType("LotDensity");
        static readonly FieldInfo sLdOcc = sLdType == null ? null : sLdType.GetField("mNumOccupiedLots", kInstAll);
        static string sLdMissing = "";

        static void ApplySpLotDensity()
        {
            sLdMissing = SpNames("LotDensity", sLdType, "LotDensity.mNumOccupiedLots", sLdOcc);
            if (sLdMissing.Length > 0) { AppliedLog().Add("splotdensity: not found: " + sLdMissing); return; }
            Patch(sLdType, "Add", new Type[] { typeof(Household) }, typeof(BSpDensityHook), "AddHook", "AddOrig");
            Patch(sLdType, "Remove", new Type[] { typeof(Household) }, typeof(BSpDensityHook), "RemoveHook", "RemoveOrig");
            Patch(sLdType, "Add", kSpSpecArg, typeof(BSpDensityHook), "SpecHook", "SpecOrig");
        }

        // EA's lot test from LotDensity.Add(Lot).
        static bool SpStoryLot(Lot lot)
        {
            return lot != null && !lot.IsWorldLot && lot.ResidentialLotSubType != ResidentialLotSubType.kEP1_PlayerOwnable
                && lot.ResidentialLotSubType != ResidentialLotSubType.kEP9_Apartment && !lot.IsCommunityLot;
        }

        // Pure: does a household on this home lot count? (EA: any home lot)
        internal static bool SpDensityCounts(bool on, bool hasLot, bool storyLot) { return hasLot && (!on || storyLot); }

        // 0: EA's path, 1: counts, -1: the fix leaves it out.
        static int SpDensityStep(Lot lot)
        {
            if (lot == null || sLdMissing.Length > 0) return 0;
            bool story;
            try { story = SpStoryLot(lot); }
            catch (Exception) { PatchCounters()[2949]++; return 0; }
            if (SpDensityCounts(ShortcutOn("splotdensity"), true, story)) { if (!story) PatchCounters()[2948]++; return 0; }
            PatchCounters()[2947]++;
            return -1;
        }

        public class BSpDensityHook
        {
            public void AddHook(Household household)
            {
                if (household != null && SpDensityStep(household.LotHome) < 0) return;
                AddOrig(household);
            }
            public void AddOrig(Household household) { }

            public void RemoveHook(Household household)
            {
                if (household != null && SpDensityStep(household.LotHome) < 0) return;
                RemoveOrig(household);
            }
            public void RemoveOrig(Household household) { }

            public void SpecHook(SimUtils.HouseholdCreationSpec household)
            {
                if (household != null && household.HomeLotId != 0 && SpDensityStep(LotManager.GetLot(household.HomeLotId)) < 0) return;
                SpecOrig(household);
            }
            public void SpecOrig(SimUtils.HouseholdCreationSpec household) { }
        }

        // ---- B10 §8 sproommatejob (tuning kSpRoommateJob) ----------------------------------------------------------------
        // [verified, decompiled] AddRoommate.Execute, outside University worlds, picks a random rabbit-hole career and
        // location, builds AcquireOccupationParameters and drops them: the roommate never gets the job. Fix (after EA's
        // Execute, roommate with no occupation, not University): the same pick, then SimDescription.AcquireOccupation with
        // those parameters (no UI, no lessons, as EA built them). Counters: [2950] roommates given the job, [2951]
        // roommates EA left jobless (switch off), [2952] errors.
        static readonly Type sRjType = typeof(StoryAction).Assembly.GetType("Sims3.Gameplay.StoryProgression.AddRoommate");
        static readonly FieldInfo sRjSd = sRjType == null ? null : sRjType.GetField("mSimDescription", kInstAll);
        static string sRjMissing = "";

        static void ApplySpRoommateJob()
        {
            sRjMissing = SpNames("AddRoommate", sRjType, "AddRoommate.mSimDescription", sRjSd);
            if (sRjMissing.Length > 0) { AppliedLog().Add("sproommatejob: not found: " + sRjMissing); return; }
            Patch(sRjType, "Execute", null, typeof(BSpRoommateHook), "ExecHook", "ExecOrig");
        }

        // Pure: does the roommate need EA's dropped job?
        internal static bool SpRoommateNeedsJob(bool university, bool hasSim, bool hasOccupation) { return !university && hasSim && !hasOccupation; }

        public class BSpRoommateHook
        {
            public void ExecHook()
            {
                ExecOrig();
                if (sRjMissing.Length > 0) return;
                try
                {
                    SimDescription sd = sRjSd.GetValue(this) as SimDescription;
                    if (!SpRoommateNeedsJob(GameUtils.IsUniversityWorld(), sd != null, sd != null && sd.Occupation != null)) return;
                    if (!ShortcutOn("sproommatejob")) { PatchCounters()[2951]++; return; }
                    List<Career> careers = Career.ValidRabbitHoleCareers(true);
                    if (careers.Count == 0) return;
                    Career c = RandomUtil.GetRandomObjectFromList(careers);
                    if (c.Locations == null || c.Locations.Count == 0) return;
                    sd.AcquireOccupation(new AcquireOccupationParameters(c.Guid, RandomUtil.GetRandomObjectFromList(c.Locations), false, false));
                    PatchCounters()[2950]++;
                }
                catch (Exception) { PatchCounters()[2952]++; }
            }

            public void ExecOrig() { }
        }

        // ---- B10 §9 spoccultpick (tuning kSpOccultPick) ------------------------------------------------------------------
        // [verified, decompiled] StoryAction.FindOccultSimDescription builds the whole candidate list, then returns the
        // first occult in household-list order: every Remove* action strips the same household's occult. Fix: the same
        // candidates (EA's household and protection filters, via EA's SelectRandomSimDescription) restricted to the occult
        // type, picked at random as the other selectors do. Counters: [2953] random occult picks, [2954] first-in-list
        // picks (switch off), [2955] errors.
        static void ApplySpOccultPick()
        {
            Patch(typeof(StoryAction), "FindOccultSimDescription", null, typeof(BSpOccultHook), "OccultHook", "OccultOrig");
        }

        // Pure: is this Sim a candidate? (predicate, then the occult type)
        internal static bool SpOccultCandidate(bool predicateOk, bool hasOccult) { return predicateOk && hasOccult; }

        public class BSpOccultHook : StoryAction
        {
            public BSpOccultHook() : base(null) { }
            protected override bool IsMajorAction { get { return false; } }
            public override void UpdateDemographics(IEnumerable<Demographics.Demographic> demographics) { }
            public override void Execute() { }

            protected SimDescription OccultHook(SimDescriptionPredicate predicate, StoryProgressionService.StoryActionCreationParams actionParams, OccultTypes occultType)
            {
                if (!ShortcutOn("spoccultpick")) { PatchCounters()[2954]++; return OccultOrig(predicate, actionParams, occultType); }
                try
                {
                    SimDescription sd = SelectRandomSimDescription(delegate (SimDescription s)
                    {
                        return SpOccultCandidate(predicate == null || predicate(s), s.OccultManager != null && s.OccultManager.HasOccultType(occultType));
                    }, actionParams);
                    PatchCounters()[2953]++;
                    return sd;
                }
                catch (Exception) { PatchCounters()[2955]++; return OccultOrig(predicate, actionParams, occultType); }
            }

            protected SimDescription OccultOrig(SimDescriptionPredicate predicate, StoryProgressionService.StoryActionCreationParams actionParams, OccultTypes occultType) { return null; }
        }

        // ---- B10 §10 spjobatwork (tuning kSpJobAtWork) -------------------------------------------------------------------
        // [verified, decompiled] CareerManager.AcquireOccupation leaves the old job with LeaveJob(kTransfered); for a Sim at
        // work Career.LeaveJob only defers it (mLeaveJobOnExit) and the new career is attached at once. When the Sim leaves
        // work, LeaveJobOnExitCheck runs the OLD career's LeaveJobNow, whose Occupation.LeaveJobNow calls
        // AssignOccupation(null): CareerManager.AssignJob(null) drops the NEW job. The Sim is jobless yet still listed in
        // the new location's workers. Story GetJob reaches this for NPCs at work, a player's job change at work for
        // household Sims. Fix: Occupation.AssignOccupation(null) from an occupation that is no longer the Sim's current
        // one leaves the current one alone; the old career's own cleanup (location, coworkers, alarms, events) is EA's.
        // Household-visible: yes (a household Sim who takes a job while at work keeps it).
        // Counters: [2956] new jobs kept, [2957] new jobs EA dropped (switch off), [2958] errors.
        static void ApplySpJobAtWork()
        {
            Patch(typeof(Occupation), "AssignOccupation", null, typeof(BSpAssignHook), "AssignHook", "AssignOrig");
        }

        // Pure: does AssignOccupation(arg) from `me` clear somebody else's current occupation?
        internal static bool SpStaleClear(bool argNull, bool hasCurrent, bool currentIsMe) { return argNull && hasCurrent && !currentIsMe; }

        public class BSpAssignHook
        {
            public bool AssignHook(Occupation occupation)
            {
                if (occupation == null)
                {
                    bool stale = false;
                    try
                    {
                        Occupation me = (Occupation)(object)this;
                        CareerManager cm = me.OwnerDescription == null ? null : me.OwnerDescription.CareerManager;
                        Occupation cur = cm == null ? null : cm.Occupation;
                        stale = SpStaleClear(true, cur != null, cur == me);
                    }
                    catch (Exception) { PatchCounters()[2958]++; }
                    if (stale)
                    {
                        if (ShortcutOn("spjobatwork")) { PatchCounters()[2956]++; return true; }
                        PatchCounters()[2957]++;
                    }
                }
                return AssignOrig(occupation);
            }

            public bool AssignOrig(Occupation occupation) { return false; }
        }

        // ---- B10 §11 sprecheck (tuning kSpRecheck) -----------------------------------------------------------------------
        // [inferred, race] Story actions choose their target when they are made; TryOneAction then yields about 50 times
        // (Simulator.Sleep(0)) before it executes the pick, and most IsValid overrides never re-test what every EA selector
        // excludes (the active and travel households). A household switch, or a target joining the active household, in
        // that window lets MoveOut, MoveTo*, job, fitness or occult actions hit a played Sim. EA's own exclusion is the intent
        // (SelectRandom*, and AddRoommate's dorm branch, all skip the active household). Fix: TryOneAction is EA's (plus
        // simler90's notification when its DLL is the loaded one), with one test right before Execute: an action whose
        // Household / SimDescription / Lot fields, or household, Sim or lot ids (also inside combination actions), reach the
        // active household, the travel household or the selected Sim's household is not executed, as an invalid pick.
        // A Sim id that resolves to nothing cannot be in the active household (its members always resolve), so it does
        // not block. Household-visible: yes (protects it). Counters: [2959] actions not executed on a played household,
        // [2960] rounds run with the switch off, [2961] errors.
        static readonly FieldInfo sRcDemo = typeof(StoryProgressionService).GetField("mDemographics", kInstAll);
        static readonly FieldInfo sRcMult = typeof(StoryProgressionService).GetField("mCategoryMultiplier", kInstAll);
        static readonly FieldInfo sRcLast = typeof(StoryProgressionService).GetField("mCategoryLastUpdateTime", kInstAll);
        static readonly FieldInfo sRcTun = typeof(StoryProgressionService).GetField("mDemographicTuning", kInstAll);
        static readonly MethodInfo sRcUpdate = typeof(StoryProgressionService).GetMethod("UpdateMultiplier", kInstAll, null, Type.EmptyTypes, null);
        static readonly FieldInfo sRcTarget = typeof(StoryProgressionService).GetField("kTravellingTargetError", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sRcDrop = typeof(StoryProgressionService).GetField("kDropActionsXPercentOfMaxScore", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static MethodInfo sRcNotify;
        static readonly Dictionary<Type, FieldInfo[]> sRcFields = new Dictionary<Type, FieldInfo[]>();
        static string sRcMissing = "";

        static void ApplySpRecheck()
        {
            sRcMissing = SpNames("StoryProgressionService.mDemographics", sRcDemo, "mCategoryMultiplier", sRcMult, "mCategoryLastUpdateTime", sRcLast,
                "mDemographicTuning", sRcTun, "UpdateMultiplier", sRcUpdate, "kTravellingTargetError", sRcTarget, "kDropActionsXPercentOfMaxScore", sRcDrop);
            sRcNotify = null;
            bool simler = false;
            foreach (AssemblyName an in typeof(StoryProgressionService).Assembly.GetReferencedAssemblies()) if (an.Name == "simler90Extras") simler = true;
            if (simler)
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType("simler90Extras.StoryProgressionStuff", false);
                    if (t == null) continue;
                    sRcNotify = t.GetMethod("StoryProgressionNotification", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(StoryAction) }, null);
                    break;
                }
                if (sRcNotify == null) sRcMissing = (sRcMissing + " simler90Extras.StoryProgressionStuff.StoryProgressionNotification").Trim();
            }
            if (sRcMissing.Length > 0) { AppliedLog().Add("sprecheck: not found: " + sRcMissing); return; }
            Patch(typeof(StoryProgressionService), "TryOneAction", null, typeof(BSpTryHook), "TryHook", "TryOrig");
        }

        // Pure: is a household with these facts played? (active, travel, the selected Sim's)
        internal static bool SpPlayed(bool exists, bool active, bool travel, bool selectedSims) { return exists && (active || travel || selectedSims); }

        // Pure: what an id field holds, by its name. 0 household, 1 lot, 2 Sim.
        internal static int SpIdKind(string field)
        {
            if (field.IndexOf("Household") >= 0) return 0;
            if (field.IndexOf("Lot") >= 0) return 1;
            return 2;
        }

        static bool SpPlayedHousehold(Household h)
        {
            if (h == null) return false;
            Sim sel = PlumbBob.SelectedActor;
            return SpPlayed(true, h.IsActive, h == GameStates.TravelHousehold, sel != null && sel.Household == h);
        }

        static FieldInfo[] SpActionFields(Type t)
        {
            FieldInfo[] fs;
            if (sRcFields.TryGetValue(t, out fs)) return fs;
            List<FieldInfo> list = new List<FieldInfo>();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(kInstAll | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    if (ft == typeof(Household) || ft == typeof(SimDescription) || typeof(Lot).IsAssignableFrom(ft) || ft == typeof(ulong) || typeof(StoryAction).IsAssignableFrom(ft))
                        list.Add(f);
                }
            fs = list.ToArray();
            sRcFields[t] = fs;
            return fs;
        }

        // Does the action (or a part of a combination) reach a played household?
        static bool SpTouchesPlayed(StoryAction a, int depth)
        {
            if (a == null || depth > 4) return false;
            foreach (FieldInfo f in SpActionFields(a.GetType()))
            {
                object v = f.GetValue(a);
                if (v == null) continue;
                Household h = null;
                if (v is StoryAction) { if (SpTouchesPlayed((StoryAction)v, depth + 1)) return true; continue; }
                if (v is Household) h = (Household)v;
                else if (v is SimDescription) h = ((SimDescription)v).Household;
                else if (v is Lot) h = ((Lot)v).Household;
                else if (v is ulong)
                {
                    ulong id = (ulong)v;
                    if (id == 0) continue;
                    int kind = SpIdKind(f.Name);
                    if (kind == 0) h = Household.Find(id);
                    else if (kind == 1) { Lot lot = LotManager.GetLot(id); h = lot == null ? null : lot.Household; }
                    else { SimDescription sd = SimDescription.Find(id); h = sd == null ? null : sd.Household; }
                }
                if (SpPlayedHousehold(h)) return true;
            }
            return false;
        }

        public class BSpTryHook
        {
            public float TryHook(bool ignoreAllCooldownsAndAttenuation)
            {
                if (sRcMissing.Length > 0 || !ShortcutOn("sprecheck")) { if (sRcMissing.Length == 0) PatchCounters()[2960]++; return TryOrig(ignoreAllCooldownsAndAttenuation); }
                bool ignore = ignoreAllCooldownsAndAttenuation;
                StoryProgressionService svc = (StoryProgressionService)(object)this;
                // EA's TryOneAction
                sRcUpdate.Invoke(svc, null);
                Demographics demo = (Demographics)sRcDemo.GetValue(svc);
                demo.Collect();
                if (ignore && demo.TotalError <= (float)sRcTarget.GetValue(null)) return demo.TotalError;
                List<StoryAction> list = demo.GenerateActions(ignore);
                if (list.Count == 0) return demo.TotalError;
                float[] mult = (float[])sRcMult.GetValue(svc);
                float[] array = new float[list.Count];
                int num = 0;
                foreach (StoryAction item in list)
                {
                    if (!ignore) Simulator.Sleep(0u);
                    if (ignore || item.IsValid())
                    {
                        float external = 1f;
                        int category = item.Tuning.Category;
                        if (category >= 0 && category < mult.Length && !ignore) external = mult[category];
                        float score = demo.ScoreAction(item, external);
                        if (score > 0f) array[num] = score;
                    }
                    num++;
                }
                if (!ignore) Simulator.Sleep(0u);
                float max = 0f;
                num = 0;
                foreach (StoryAction item2 in list)
                {
                    if (!ignore && !item2.IsValid()) array[num] = 0f;
                    else if (array[num] > max) max = array[num];
                    num++;
                }
                float drop = (float)sRcDrop.GetValue(null);
                for (int i = 0; i < array.Length; i++) if (array[i] < max * drop) array[i] = 0f;
                int pick = RandomUtil.GetWeightedIndex(array);
                StoryAction action = list[pick];
                if (array[pick] > 0f && action.IsValid())
                {
                    bool played = false;
                    try { played = SpTouchesPlayed(action, 0); }
                    catch (Exception) { PatchCounters()[2961]++; played = true; }  // unknown scope counts as household
                    if (played) { PatchCounters()[2959]++; return demo.TotalError; }
                    if (sRcNotify != null) sRcNotify.Invoke(null, new object[] { action });
                    action.Execute();
                    int category2 = action.Tuning.Category;
                    DemographicTuningTable tun = (DemographicTuningTable)sRcTun.GetValue(svc);
                    DemographicTuningTable.DelayTuning value;
                    if (category2 >= 0 && category2 < mult.Length && tun.CategoryTuning.TryGetValue(category2, out value) && value.Max > 0f)
                    {
                        ((float[])sRcLast.GetValue(svc))[category2] = SimClock.ElapsedTime(TimeUnit.Days);
                        mult[category2] = 0f;
                    }
                    if (ignore) return demo.CalculateNewError(action);
                }
                return demo.TotalError;
            }

            public float TryOrig(bool ignoreAllCooldownsAndAttenuation) { return 0f; }
        }

        // ---- B10 §12 sppensionprompt (tuning kSpPensionPrompt) -----------------------------------------------------------
        // [verified, decompiled] CareerManager.AcquireOccupation leaves the old job (after its "leave your job?" prompt)
        // before it asks "give up your pension?" when a retired Sim rejoins the career it retired from: answering no
        // returns false with the old job already gone. Fix: in that case (a non-school job to leave, rejoining the retired
        // career, EA's prompt conditions, EA's request and transfer tests passing) the pension prompt is asked first; no
        // returns false with nothing changed, yes runs EA's method, whose own identical pension prompt is then answered yes
        // for the player. The pension is still ended where EA ends it, after the job change went through.
        // Household-visible: yes (a player dialog). Counters: [2962] pension prompts asked first, [2963] prompts EA asked
        // after the leave (switch off), [2964] errors.
        static readonly FieldInfo sPpSd = typeof(CareerManager).GetField("mSimDescription", kInstAll);
        static readonly MethodInfo sPpValid = typeof(CareerManager).GetMethod("ValidateOccupationRequest", kInstAll, null, new Type[] { typeof(AcquireOccupationParameters) }, null);
        static readonly MethodInfo sPpTransfer = typeof(CareerManager).GetMethod("IsLocationTransfer", kInstAll, null, new Type[] { typeof(AcquireOccupationParameters) }, null);
        static string sPpMissing = "", sPpAuto;

        static void ApplySpPensionPrompt()
        {
            sPpMissing = SpNames("CareerManager.mSimDescription", sPpSd, "ValidateOccupationRequest", sPpValid, "IsLocationTransfer", sPpTransfer);
            if (sPpMissing.Length > 0) { AppliedLog().Add("sppensionprompt: not found: " + sPpMissing); return; }
            Patch(typeof(CareerManager), "AcquireOccupation", null, typeof(BSpAcquireHook), "AcquireHook", "AcquireOrig");
            Patch(typeof(Occupation), "ShowYesNoCareerOptionDialog", null, typeof(BSpAcquireHook), "YesNoHook", "YesNoOrig");
        }

        // Pure: does EA ask the pension prompt after leaving a job?
        internal static bool SpPensionAfterLeave(bool hasJob, bool isSchool, bool rejoinsRetired, bool uiOrSelectable, bool requestOk)
        {
            return hasJob && !isSchool && rejoinsRetired && uiOrSelectable && requestOk;
        }

        // The pension prompt's text when EA would ask it after a leave, else null.
        static string SpPensionText(CareerManager cm, AcquireOccupationParameters p)
        {
            Occupation job = cm.Occupation, retired = cm.RetiredCareer;
            SimDescription sd = sPpSd.GetValue(cm) as SimDescription;
            if (sd == null || retired == null) return null;
            Sim sim = sd.CreatedSim;
            bool selectable = sim != null && sim.IsSelectable;
            bool ok = (bool)sPpValid.Invoke(cm, new object[] { p }) && !(bool)sPpTransfer.Invoke(cm, new object[] { p });
            if (!SpPensionAfterLeave(job != null, job is School, retired.Guid == p.TargetJob, p.ShowUserInterface || selectable, ok)) return null;
            return Localization.LocalizeString(sd.IsFemale, "Gameplay/Careers/Career:ConfirmPensionLoss", sd, retired.CareerName);
        }

        public class BSpAcquireHook
        {
            public bool AcquireHook(AcquireOccupationParameters occupationParameters)
            {
                string text = null;
                if (sPpMissing.Length == 0)
                {
                    try { text = SpPensionText((CareerManager)(object)this, occupationParameters); }
                    catch (Exception) { PatchCounters()[2964]++; text = null; }
                }
                if (text == null) return AcquireOrig(occupationParameters);
                if (!ShortcutOn("sppensionprompt")) { PatchCounters()[2963]++; return AcquireOrig(occupationParameters); }
                PatchCounters()[2962]++;
                if (!Occupation.ShowYesNoCareerOptionDialog(text)) return false;  // nothing changed yet
                sPpAuto = text;
                try { return AcquireOrig(occupationParameters); }
                finally { sPpAuto = null; }
            }

            public bool AcquireOrig(AcquireOccupationParameters occupationParameters) { return false; }

            public static bool YesNoHook(string displayText)
            {
                if (sPpAuto != null && displayText == sPpAuto) { sPpAuto = null; return true; }  // the player already said yes
                return YesNoOrig(displayText);
            }

            public static bool YesNoOrig(string displayText) { return false; }
        }

        // ---- B10 §13 hhcleanupdup (tuning kHhCleanupDup) -----------------------------------------------------------------
        // [verified, decompiled; trigger inferred] Household.Cleanup (load, FixHouseholdsInWorld) folds a smaller household
        // sharing a member into a larger one. Its inner guard re-tests sHousehold3 where sHousehold4 was meant
        // (copy-paste): the larger household may be special or already queued for deletion, and gets the lot anyway. The
        // members of the smaller one that the larger does not have are cleared and left pointing at a disposed household.
        // The passes also scan with List.Contains, O(H^2 n m). Fix (switch on): EA's Cleanup, in EA's order, with the guard
        // on sHousehold4 (and the stop-after-merge EA's re-test gave, kept as a break), the unshared members moved into the
        // larger household (EA's Household.Add, as its own re-home pass does), dictionaries for the membership tests, and
        // null list entries skipped. No household exemption (user: as EA intended, even for ours). Household-visible:
        // possibly (a load where a household Sim is in two households). Counters: [2965] cleanups run fixed, [2966] loads
        // with a member in two households seen with the switch off, [2967] errors.
        static readonly FieldInfo sHdLotHome = typeof(Household).GetField("mLotHome", kInstAll);
        static readonly FieldInfo sHdNpc = typeof(Household).GetField("sNpcHousehold", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sHdTourist = typeof(Household).GetField("sTouristHousehold", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static string sHdMissing = "";

        static void ApplyHhCleanupDup()
        {
            sHdMissing = SpNames("Household.mLotHome", sHdLotHome, "Household.sNpcHousehold", sHdNpc, "Household.sTouristHousehold", sHdTourist);
            if (sHdMissing.Length > 0) { AppliedLog().Add("hhcleanupdup: not found: " + sHdMissing); return; }
            Patch(typeof(Household), "Cleanup", Type.EmptyTypes, typeof(BHhCleanupHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: may household 3 fold into household 4? (EA's re-test of 3 vs the fix's test of 4)
        internal static bool HdMayFold(bool on, bool h4Listed, bool h4Special) { return !on || (!h4Listed && !h4Special); }

        static bool HdSharedMember()
        {
            Dictionary<SimDescription, Household> seen = new Dictionary<SimDescription, Household>();
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null) continue;
                foreach (SimDescription sd in h.AllSimDescriptions)
                {
                    if (sd == null) continue;
                    Household other;
                    if (seen.TryGetValue(sd, out other) && other != h) return true;
                    seen[sd] = h;
                }
            }
            return false;
        }

        public class BHhCleanupHook
        {
            public static void CleanupHook()
            {
                if (sHdMissing.Length > 0) { CleanupOrig(); return; }
                if (!ShortcutOn("hhcleanupdup"))
                {
                    try { if (HdSharedMember()) PatchCounters()[2966]++; } catch (Exception) { PatchCounters()[2967]++; }
                    CleanupOrig();
                    return;
                }
                List<Household> all = Household.sHouseholdList;
                List<Household> list = new List<Household>();
                Dictionary<Household, bool> listed = new Dictionary<Household, bool>();
                Household npc = sHdNpc.GetValue(null) as Household;
                foreach (Household h in all)
                {
                    if (h == null) continue;
                    if (h.LotHome != null)
                    {
                        if (h.LotHome.Household != h) sHdLotHome.SetValue(h, null);
                        else if (h == npc) h.MoveOut();
                    }
                    if (h.VirtualLotHome != null && !h.VirtualLotHome.VirtualSlotHouseholds.Contains(h)) h.SetVirtualLot(null);
                    h.AllSimDescriptions.RemoveAll(delegate (SimDescription s) { return s == null; });
                }
                foreach (Household h in all)
                {
                    if (h == null) continue;
                    h.LogDumpSummary();
                    if (h.IsSpecialHousehold) continue;
                    if (h.Proxy == null || (h.InWorld && h.AllSimDescriptions.Count == 0)) { HdMark(h, list, listed); continue; }
                    Dictionary<SimDescription, bool> mem = new Dictionary<SimDescription, bool>();
                    foreach (SimDescription sd in h.AllSimDescriptions)
                    {
                        if (mem.ContainsKey(sd)) { HdMark(h, list, listed); break; }
                        mem[sd] = true;
                    }
                }
                foreach (Household h3 in all)
                {
                    if (h3 == null || listed.ContainsKey(h3) || h3.IsSpecialHousehold) continue;
                    List<SimDescription> m3 = h3.AllSimDescriptions;
                    foreach (Household h4 in all)
                    {
                        if (listed.ContainsKey(h3)) break;  // EA's re-test of h3: folded once, stop
                        if (h4 == null || h4 == h3 || !HdMayFold(true, listed.ContainsKey(h4), h4.IsSpecialHousehold)) continue;
                        List<SimDescription> m4 = h4.AllSimDescriptions;
                        if (m3.Count >= m4.Count) continue;
                        Dictionary<SimDescription, bool> in4 = new Dictionary<SimDescription, bool>();
                        foreach (SimDescription sd in m4) in4[sd] = true;
                        bool shared = false;
                        foreach (SimDescription sd in m3) if (in4.ContainsKey(sd)) { shared = true; break; }
                        if (!shared) continue;
                        if (h3.LotHome != null && h3.LotHome.Household == h3)
                        {
                            Lot lotHome = h3.LotHome;
                            h3.MoveOut();
                            lotHome.MoveIn(h4);
                        }
                        foreach (SimDescription sd in new List<SimDescription>(m3)) if (!in4.ContainsKey(sd)) h4.Add(sd);  // re-home, not orphan
                        h3.AllSimDescriptions.Clear();
                        HdMark(h3, list, listed);
                    }
                }
                foreach (Household h5 in all)
                {
                    if (h5 == null || listed.ContainsKey(h5) || h5.IsSpecialHousehold) continue;
                    int i = 0;
                    while (i < h5.CurrentMembers.AllSimDescriptionList.Count)
                    {
                        SimDescription sd = h5.CurrentMembers.AllSimDescriptionList[i];
                        if (sd.Household == null) sd.OnHouseholdChanged(h5, true);
                        else if (sd.Household != h5)
                        {
                            if (!listed.ContainsKey(sd.Household))
                            {
                                Household other = sd.Household;
                                h5.Remove(sd);
                                if (!other.Contains(sd)) other.Add(sd);
                                continue;
                            }
                            sd.OnHouseholdChanged(h5, true);
                        }
                        i++;
                    }
                    if (h5.CurrentMembers.Count == 0) HdMark(h5, list, listed);
                }
                Household tourist = sHdTourist.GetValue(null) as Household;
                bool dropTourist = false;
                if (tourist != null && !GameUtils.IsInstalled(ProductVersion.EP1))
                {
                    HdMark(tourist, list, listed);
                    dropTourist = true;
                }
                foreach (Household h in list)
                {
                    if (h.Proxy != null) { Simulator.DestroyObject(h.ObjectId); continue; }
                    h.Dispose();
                    all.Remove(h);
                }
                if (dropTourist) sHdTourist.SetValue(null, null);
                PatchCounters()[2965]++;
            }

            public static void CleanupOrig() { }
        }

        static void HdMark(Household h, List<Household> list, Dictionary<Household, bool> listed)
        {
            if (listed.ContainsKey(h)) return;
            listed[h] = true;
            list.Add(h);
        }

        // ---- B10 §14 virtualhomeshare (tuning kVirtualHomeShare) ---------------------------------------------------------
        // [verified, decompiled] SimDescription.FindSuitableVirtualHome draws from the first
        // Math.Max(Count * kShellsLivableByServiceSims, Count - 1) + 1 lots, always every lot, so the 0.5 tuning ("lower end
        // percentage of buildable shells habitable by service sims"; AllHabitableShellLots is sorted by cost) does nothing,
        // and a value above 1 indexes past the list. Fix: Min, at least one lot. The rest is EA's. Counters: [2968] homes
        // drawn from the tuned share, [2969] draws over every lot (switch off), [2970] errors.
        static readonly FieldInfo sVhShare = typeof(SimDescription).GetField("kShellsLivableByServiceSims", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static string sVhMissing = "";

        static void ApplyVirtualHomeShare()
        {
            sVhMissing = SpNames("SimDescription.kShellsLivableByServiceSims", sVhShare);
            if (sVhMissing.Length > 0) { AppliedLog().Add("virtualhomeshare: not found: " + sVhMissing); return; }
            Patch(typeof(SimDescription), "FindSuitableVirtualHome", new Type[] { typeof(List<Lot>) }, typeof(BVhHook), "HomeHook", "HomeOrig");
        }

        // Pure: the last lot index drawn from.
        internal static int VhLast(bool on, int count, float share)
        {
            int part = (int)((float)count * share);
            return on ? Math.Max(0, Math.Min(part, count - 1)) : Math.Max(part, count - 1);
        }

        public class BVhHook
        {
            public void HomeHook(List<Lot> lots)
            {
                SimDescription sd = (SimDescription)(object)this;
                Service by = sd.CreatedByService;
                if (sVhMissing.Length > 0 || (by != null && by.ServiceType == ServiceType.GrimReaper) || (by == null && !sd.IsFrankenstein)) { HomeOrig(lots); return; }
                bool place = lots == null;
                List<Lot> use = lots ?? LotManager.AllHabitableShellLots;
                float share;
                try { share = (float)sVhShare.GetValue(null); }
                catch (Exception) { PatchCounters()[2970]++; HomeOrig(lots); return; }
                if (use.Count == 0 || VhLast(true, use.Count, share) == VhLast(false, use.Count, share)) { HomeOrig(lots); return; }
                if (!ShortcutOn("virtualhomeshare")) { PatchCounters()[2969]++; HomeOrig(lots); return; }
                // EA's draw over the tuned share
                int last = VhLast(true, use.Count, share);
                float max = 0f;
                float[] w = new float[last + 1];
                for (int i = 0; i <= last; i++)
                {
                    w[i] = use[i].VirtualSlotsFilled;
                    if (w[i] > max) max = w[i];
                }
                for (int j = 0; j <= last; j++) w[j] = max - w[j] + 1f;
                int pick = RandomUtil.GetWeightedIndex(w);
                if (pick < use.Count)
                {
                    use[pick].VirtualMoveIn(sd);
                    if (place) use[pick].PlaceInNPCDoor(sd);
                }
                PatchCounters()[2968]++;
            }

            public void HomeOrig(List<Lot> lots) { }
        }

        // ---- B10 §15 celebalarmnull (tuning kCelebAlarmNull) -------------------------------------------------------------
        // [verified, decompiled; trigger inferred] CelebrityManager.RemoveFreeStuffAlarm and RemoveDeflectScandalCooldown
        // call OwnerSim (Owner.CreatedSim).RemoveAlarm with no null test: for a celebrity who is not instantiated (story
        // CelebrityLevelModification, or a level change off-lot) they throw and the level change stops half done. Fix:
        // owner not instantiated, valid handle: AlarmManager.Global.RemoveAlarm(handle), which is what Sim.RemoveAlarm does,
        // then EA's handle reset. OnLevelModified's `<=` (equal levels run OnLevelUp) is left: with equal levels it only
        // re-checks the skill and journal, no notice. Counters: [2971] alarms removed with no Sim, [2972] would have thrown
        // (switch off), [2973] errors.
        static readonly FieldInfo sCaFree = typeof(CelebrityManager).GetField("mFreeStuffAlarmHandle", kInstAll);
        static readonly FieldInfo sCaDeflect = typeof(CelebrityManager).GetField("mDeflectScandalCoolDownAlarmHandle", kInstAll);
        static string sCaMissing = "";

        static void ApplyCelebAlarmNull()
        {
            sCaMissing = SpNames("CelebrityManager.mFreeStuffAlarmHandle", sCaFree, "mDeflectScandalCoolDownAlarmHandle", sCaDeflect);
            if (sCaMissing.Length > 0) { AppliedLog().Add("celebalarmnull: not found: " + sCaMissing); return; }
            Patch(typeof(CelebrityManager), "RemoveFreeStuffAlarm", null, typeof(BCaHook), "FreeHook", "FreeOrig");
            Patch(typeof(CelebrityManager), "RemoveDeflectScandalCooldown", null, typeof(BCaHook), "DeflectHook", "DeflectOrig");
        }

        // Pure: does EA's removal throw?
        internal static bool CaThrows(bool validHandle, bool ownerSim) { return validHandle && !ownerSim; }

        // true: handled with no Sim.
        static bool CaRemove(CelebrityManager cm, FieldInfo f)
        {
            if (sCaMissing.Length > 0) return false;
            try
            {
                AlarmHandle h = (AlarmHandle)f.GetValue(cm);
                if (!CaThrows(h != AlarmHandle.kInvalidHandle, (cm.Owner != null && cm.Owner.CreatedSim != null))) return false;
                if (!ShortcutOn("celebalarmnull")) { PatchCounters()[2972]++; return false; }
                AlarmManager.Global.RemoveAlarm(h);
                f.SetValue(cm, AlarmHandle.kInvalidHandle);
                PatchCounters()[2971]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2973]++; return false; }
        }

        public class BCaHook
        {
            public void FreeHook() { if (!CaRemove((CelebrityManager)(object)this, sCaFree)) FreeOrig(); }
            public void FreeOrig() { }
            public void DeflectHook() { if (!CaRemove((CelebrityManager)(object)this, sCaDeflect)) DeflectOrig(); }
            public void DeflectOrig() { }
        }

        // live paths of the checks (CheckVerdict.cs CkLive): hook swaps in place, reflection handles usable
        static readonly int sLive_agingalarmcancel = LiveAdd("agingalarmcancel", () => EpLive(typeof(BAcHook), 1, "sAcGrad", sAcGrad, null));
        static readonly int sLive_celebalarmnull = LiveAdd("celebalarmnull", () => EpLive(typeof(BCaHook), 2));
        static readonly int sLive_disposevirtualslot = LiveAdd("disposevirtualslot", () => EpLive(typeof(BDvHook), 1));
        static readonly int sLive_hhcleanupdup = LiveAdd("hhcleanupdup", () => EpLive(typeof(BHhCleanupHook), 1, "sHdNpc", sHdNpc, null, "sHdNpc", sHdNpc, "static", "sHdLotHome", sHdLotHome, null, "sHdTourist", sHdTourist, null, "sHdTourist", sHdTourist, "static"));
        static readonly int sLive_petpoolghost = LiveAdd("petpoolghost", () => EpLive(typeof(BPgHook), 1, "sPpgIds", sPpgIds, null));
        static readonly int sLive_spbucketindex = LiveAdd("spbucketindex", () => EpLive(typeof(BSpHistHook), 1, "sSpErrCurve", sSpErrCurve, null, "sSpTuning", sSpTuning, null, "sSpBucketIndex", sSpBucketIndex, 1));
        static readonly int sLive_spclonedict = LiveAdd("spclonedict", () => EpJoin(EpLive(typeof(BSpBigHook), 2), EpLive(typeof(BSpSmallHook), 2)));
        static readonly int sLive_spcollectnull = LiveAdd("spcollectnull", () => EpLive(typeof(BSpCollectHook), 1, "sCoLot", sCoLot, null, "sCoHh", sCoHh, null, "sCoSim", sCoSim, null, "sCoAll", sCoAll, null));
        static readonly int sLive_spcreatemovein = LiveAdd("spcreatemovein", () => EpLive(typeof(BSpComboHook), 1, "sCmSpec", sCmSpec, null, "sCmLot", sCmLot, typeof(ulong)));
        static readonly int sLive_spjobatwork = LiveAdd("spjobatwork", () => EpLive(typeof(BSpAssignHook), 1));
        static readonly int sLive_splotdensity = LiveAdd("splotdensity", () => EpLive(typeof(BSpDensityHook), 3));
        static readonly int sLive_splotlookup = LiveAdd("splotlookup", () => EpJoin(EpJoin(EpLive(typeof(BSpBigHook), 2), EpLive(typeof(BSpHorseHook), 1, "sLlStalls", sLlStalls, null, "sLlStalls", sLlStalls, typeof(uint))), EpLive(typeof(BSpSmallHook), 2)));
        static readonly int sLive_spoccultpick = LiveAdd("spoccultpick", () => EpLive(typeof(BSpOccultHook), 1));
        static readonly int sLive_sppensionprompt = LiveAdd("sppensionprompt", () => EpLive(typeof(BSpAcquireHook), 2));
        static readonly int sLive_sprecheck = LiveAdd("sprecheck", () => EpLive(typeof(BSpTryHook), 1, "sRcUpdate", sRcUpdate, 0, "sRcDemo", sRcDemo, null, "sRcTarget", sRcTarget, typeof(float), "sRcTarget", sRcTarget, "static", "sRcMult", sRcMult, null, "sRcDrop", sRcDrop, typeof(float), "sRcDrop", sRcDrop, "static", "sRcNotify", sRcNotify, 1, "sRcTun", sRcTun, null, "sRcLast", sRcLast, null));
        static readonly int sLive_sprelcount = LiveAdd("sprelcount", () => EpLive(typeof(BSpCollectHook), 1, "sCoLot", sCoLot, null, "sCoHh", sCoHh, null, "sCoSim", sCoSim, null, "sCoAll", sCoAll, null));
        static readonly int sLive_sproommatejob = LiveAdd("sproommatejob", () => EpLive(typeof(BSpRoommateHook), 1, "sRjSd", sRjSd, null));
        static readonly int sLive_virtualhomeshare = LiveAdd("virtualhomeshare", () => EpLive(typeof(BVhHook), 1, "sVhShare", sVhShare, typeof(float), "sVhShare", sVhShare, "static"));
        static readonly int sLive_virtualmoveindup = LiveAdd("virtualmoveindup", () => EpLive(typeof(BVmHook), 1));
    }
}
