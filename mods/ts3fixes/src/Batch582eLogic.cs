using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.ChildAndTeenUpdates;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.Register;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 6 (docs/582 B6; Roslyn pass 2, docs/583 §6). All default off. simler90 (Gameplay Core Mod) leaves every site
    // below as EA wrote it (ilspy of its Sims3GameplaySystems / Sims3GameplayObjects copies).
    public static partial class Fixes
    {
        // ---- B6 §1 disposebase (tuning kDisposeBase) ----------------------------------------------------------------------
        // [verified, IL] JunkPile, ShoppingRegister, PetBedReward, BubbleColumn and FogMarker override Dispose and never call
        // base.Dispose (IL: no call to GameObject::Dispose; PetBed and Column do not override it, so the base is GameObject's
        // for all five). GameObject.Dispose is skipped: Sims' queued interactions on the object are not purged, its alarms
        // and reactions stay, its commodity-map and room look-at entries stay, its components are not disposed and
        // kEventObjectDisposed is never sent. Fix at the source: after the override's own cleanup, GameObject.Dispose runs,
        // as in every other EA object. Household-visible: a household Sim's queued interaction on such an object is purged.
        // Counters: [2892] base disposes run, [2893] disposes seen with the switch off, [2894] errors.
        static void ApplyDisposeBase()
        {
            Patch(typeof(JunkPile), "Dispose", Type.EmptyTypes, typeof(BDbJunkHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(ShoppingRegister), "Dispose", Type.EmptyTypes, typeof(BDbRegisterHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(PetBedReward), "Dispose", Type.EmptyTypes, typeof(BDbPetBedHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(BubbleColumn), "Dispose", Type.EmptyTypes, typeof(BDbBubbleHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(FogMarker), "Dispose", Type.EmptyTypes, typeof(BDbFogHook), "DisposeHook", "DisposeOrig");
        }

        // Pure: does GameObject.Dispose run after the override?
        internal static bool DbRunBase(bool on) { return on; }

        static bool DbBefore()
        {
            if (DbRunBase(ShortcutOn("disposebase"))) return true;
            PatchCounters()[2893]++;
            return false;
        }

        // Each hook derives from GameObject so `base.Dispose()` is a non-virtual call to GameObject.Dispose (the swapped
        // body runs as the target's Dispose). One class per target: each needs its own stub.
        public abstract class BDbJunkHook : GameObject
        {
            public void DisposeHook()
            {
                DisposeOrig();
                if (!DbBefore()) return;
                try { base.Dispose(); PatchCounters()[2892]++; } catch (Exception) { PatchCounters()[2894]++; }
            }
            public void DisposeOrig() { } // receives JunkPile.Dispose
        }
        public abstract class BDbRegisterHook : GameObject
        {
            public void DisposeHook()
            {
                DisposeOrig();
                if (!DbBefore()) return;
                try { base.Dispose(); PatchCounters()[2892]++; } catch (Exception) { PatchCounters()[2894]++; }
            }
            public void DisposeOrig() { } // receives ShoppingRegister.Dispose
        }
        public abstract class BDbPetBedHook : GameObject
        {
            public void DisposeHook()
            {
                DisposeOrig();
                if (!DbBefore()) return;
                try { base.Dispose(); PatchCounters()[2892]++; } catch (Exception) { PatchCounters()[2894]++; }
            }
            public void DisposeOrig() { } // receives PetBedReward.Dispose
        }
        public abstract class BDbBubbleHook : GameObject
        {
            public void DisposeHook()
            {
                DisposeOrig();
                if (!DbBefore()) return;
                try { base.Dispose(); PatchCounters()[2892]++; } catch (Exception) { PatchCounters()[2894]++; }
            }
            public void DisposeOrig() { } // receives BubbleColumn.Dispose
        }
        public abstract class BDbFogHook : GameObject
        {
            public void DisposeHook()
            {
                DisposeOrig();
                if (!DbBefore()) return;
                try { base.Dispose(); PatchCounters()[2892]++; } catch (Exception) { PatchCounters()[2894]++; }
            }
            public void DisposeOrig() { } // receives FogMarker.Dispose
        }

        // ---- B6 §2 boardingschoolmoney (tuning kBoardingSchoolMoney; world-load pass) -------------------------------------
        // [verified, decompiled] BoardingSchoolData(XmlDbRow) reads NeedMoneyRange into RewardMin and NeedMoneyMin, then
        // RewardMoneyRange into NeedMoneyMin (overwritten) and RewardMax; NeedMoneyMax is never set (0). The "need money"
        // call asks RandomUtil.GetInt(RewardMoneyRange[0], 0), an inverted range (an amount from 1 to that minimum), and the
        // reward call starts from NeedMoneyRange[0]. Each range belongs to its own pair; EA's fallbacks (100 and 500 in both
        // branches) are each pair's min and max. EA parses at startup, before ts3fixes is up, so the fix re-reads EA's own
        // BoardingSchool table at world load and sets the four fields of each parsed school. Household-visible: the
        // amounts in the boarding-school phone calls (the household's funds).
        // Counters: [2895] schools corrected, [2896] schools that would change with the switch off, [2897] errors.
        static readonly string[] kBsFields = { "PhoneCallNeedMoneyMin", "PhoneCallNeedMoneyMax", "PhoneCallRewardMin", "PhoneCallRewardMax" };

        // Pure: {needMin, needMax, rewardMin, rewardMax} from the two parsed ranges (null when absent or short).
        internal static int[] BsRanges(List<int> need, List<int> reward)
        {
            int[] r = { 100, 500, 100, 500 };
            if (need != null && need.Count >= 2) { r[0] = need[0]; r[1] = need[1]; }
            if (reward != null && reward.Count >= 2) { r[2] = reward[0]; r[3] = reward[1]; }
            return r;
        }

        internal static void BoardingSchoolMoneyWorldLoad()
        {
            if (BoardingSchool.BoardingSchoolData.sBoardingSchoolDataList == null) return;
            try
            {
                bool on = ShortcutOn("boardingschoolmoney");
                XmlDbData x = XmlDbData.ReadData("BoardingSchool");
                XmlDbTable t;
                if (x == null || x.Tables == null || !x.Tables.TryGetValue("BoardingSchool", out t) || t == null) return;
                FieldInfo[] f = new FieldInfo[4];
                for (int i = 0; i < 4; i++) { f[i] = typeof(BoardingSchool.BoardingSchoolData).GetField(kBsFields[i], kInstAll); if (f[i] == null) return; }
                foreach (XmlDbRow row in t.Rows)
                {
                    BoardingSchool.BoardingSchoolTypes type = row.GetEnum("GUID", BoardingSchool.BoardingSchoolTypes.Art);
                    BoardingSchool.BoardingSchoolData d;
                    if (!BoardingSchool.BoardingSchoolData.sBoardingSchoolDataList.TryGetValue(type, out d) || d == null) continue;
                    List<int> need, reward;
                    if (!ParserFunctions.ParseCommaSeperatedInt(row["NeedMoneyRange"], out need)) need = null;
                    if (!ParserFunctions.ParseCommaSeperatedInt(row["RewardMoneyRange"], out reward)) reward = null;
                    int[] want = BsRanges(need, reward);
                    bool differs = false;
                    for (int i = 0; i < 4; i++) if ((int)f[i].GetValue(d) != want[i]) differs = true;
                    if (!differs) continue;
                    if (!on) { PatchCounters()[2896]++; continue; }
                    for (int i = 0; i < 4; i++) f[i].SetValue(d, want[i]);
                    PatchCounters()[2895]++;
                }
            }
            catch (Exception) { PatchCounters()[2897]++; }
        }

        // ---- B6 §3 freshclothingresume (tuning kFreshClothingResume) ------------------------------------------------------
        // [verified, decompiled; effect inferred] LaundryManager.OnClothesChanged saves the Fresh Clothing buff's remaining
        // time in mFreshClothingTimeouts when a Sim changes into swimwear or naked (and removes the buff); the buff's own
        // removal (BuffFreshClothing.OnRemoval) records 0 when it ends; a new dryer cycle clears the map. Nothing reads the
        // map: AddLaundryBuff always adds the buff at full length, so a swim or a shower restarts it. Fix at the reader:
        // AddLaundryBuff with a saved entry and no Fresh Clothing buff on the Sim resumes the saved time (entry then used
        // up), or adds nothing when the saved time is 0 (the buff already ran out on this laundry). No entry, or the buff
        // still on the Sim: EA's add. Household-visible: Fresh Clothing (a household moodlet) lasts as EA's bookkeeping says.
        // Counters: [2898] buffs resumed, [2899] adds skipped (worn off), [2900] seen with the switch off, [2901] errors.
        static readonly FieldInfo sFcMap = typeof(LaundryManager).GetField("mFreshClothingTimeouts", kInstAll);

        internal const int kFcEa = 0, kFcResume = 1, kFcSkip = 2;
        // Pure: what AddLaundryBuff does with the switch on.
        internal static int FcDecide(bool on, bool hasEntry, float saved, bool buffOn)
        {
            if (!on || !hasEntry || buffOn) return kFcEa;
            return saved > 0f ? kFcResume : kFcSkip;
        }

        static void ApplyFreshClothingResume()
        {
            if (sFcMap == null) { AppliedLog().Add("freshclothingresume: not found: LaundryManager.mFreshClothingTimeouts"); return; }
            Patch(typeof(LaundryManager), "AddLaundryBuff", new Type[] { typeof(Sim) }, typeof(BFcHook), "AddHook", "AddOrig");
        }

        static BuffInstance FcBuff(Sim s)
        {
            return s.BuffManager.GetElement(BuffNames.FreshClothing) ?? s.BuffManager.GetElement(BuffNames.VeryFreshClothing);
        }

        public class BFcHook
        {
            public static void AddHook(Sim actor)
            {
                int what = kFcEa;
                float saved = 0f;
                PairedListDictionary<ulong, float> map = null;
                ulong id = 0;
                try
                {
                    if (actor != null && actor.LotHome != null && actor.LotHome.LaundryManager != null && actor.BuffManager != null)
                    {
                        map = sFcMap.GetValue(actor.LotHome.LaundryManager) as PairedListDictionary<ulong, float>;
                        id = actor.SimDescription.SimDescriptionId;
                        bool has = map != null && map.TryGetValue(id, out saved);
                        bool on = ShortcutOn("freshclothingresume");
                        what = FcDecide(on, has, saved, FcBuff(actor) != null);
                        if (!on && FcDecide(true, has, saved, FcBuff(actor) != null) != kFcEa) PatchCounters()[2900]++;
                    }
                }
                catch (Exception) { PatchCounters()[2901]++; what = kFcEa; }
                if (what == kFcSkip) { PatchCounters()[2899]++; return; }
                AddOrig(actor);
                if (what != kFcResume) return;
                try
                {
                    BuffInstance b = FcBuff(actor);
                    if (b != null && b.TimeoutCount > saved) b.TimeoutCount = saved;
                    map.Remove(id);
                    PatchCounters()[2898]++;
                }
                catch (Exception) { PatchCounters()[2901]++; }
            }
            public static void AddOrig(Sim actor) { } // receives LaundryManager.AddLaundryBuff
        }

        // ---- B6 §4 twinbellyshape (tuning kTwinBellyShape) ----------------------------------------------------------------
        // [verified, decompiled] Pregnancy+InternalHaveBabyHome.Run calls SetPregnancy(1 - BabyIndex / TotalCount) with
        // integer division (BabyIndex 1..TotalCount): the belly stays full until the last baby. Intent: 1 - i/n. The Run
        // hook records its baby index for its own sim task (Run yields in AnimateSim); SimDescription.SetPregnancy(float)
        // called from that task with EA's 1 gets 1 - i/n instead. Household-visible (cosmetic, twins and triplets).
        // Counters: [2902] shapes corrected, [2903] seen with the switch off, [2904] errors.
        static Type sPgType;
        static FieldInfo sPgIndex, sPgTotal;
        static readonly Dictionary<ulong, float> sPgByTask = new Dictionary<ulong, float>();
        static string sPgMissing = "";

        // Pure: the value SetPregnancy receives. ea: EA's argument; i, n: baby index (1-based) and count.
        internal static float PgShape(bool on, float ea, int i, int n)
        {
            if (!on || n <= 1 || i < 1 || i > n || ea != 1f - i / n) return ea;
            return 1f - (float)i / n;
        }

        static void ApplyTwinBellyShape()
        {
            sPgType = typeof(Pregnancy).GetNestedType("InternalHaveBabyHome", BindingFlags.NonPublic | BindingFlags.Public);
            sPgIndex = sPgType == null ? null : sPgType.GetField("BabyIndex", kInstAll);
            sPgTotal = sPgType == null ? null : sPgType.GetField("TotalCount", kInstAll);
            sPgMissing = sPgIndex == null || sPgTotal == null ? "Pregnancy+InternalHaveBabyHome.BabyIndex/TotalCount" : "";
            if (sPgMissing.Length > 0) { AppliedLog().Add("twinbellyshape: not found: " + sPgMissing); return; }
            if (!Patch(sPgType, "Run", Type.EmptyTypes, typeof(BPgRunHook), "RunHook", "RunOrig")) return;
            Patch(typeof(SimDescription), "SetPregnancy", new Type[] { typeof(float) }, typeof(BPgSetHook), "SetHook", "SetOrig");
        }

        public class BPgRunHook
        {
            public bool RunHook()
            {
                ulong task = Simulator.CurrentTask.Value;
                bool mine = false;
                try
                {
                    int i = (int)sPgIndex.GetValue(this), n = (int)sPgTotal.GetValue(this);
                    if (n > 1) { sPgByTask[task] = i * 1000 + n; mine = true; }  // packed: index and count
                }
                catch (Exception) { PatchCounters()[2904]++; }
                try { return RunOrig(); }
                finally { if (mine) sPgByTask.Remove(task); }
            }
            public bool RunOrig() { return false; } // receives Pregnancy+InternalHaveBabyHome.Run
        }

        public class BPgSetHook
        {
            public void SetHook(float percentPregnant)
            {
                float v;
                bool refresh = false;
                if (sPgByTask.Count > 0 && sPgByTask.TryGetValue(Simulator.CurrentTask.Value, out v))
                {
                    int packed = (int)v, i = packed / 1000, n = packed % 1000;
                    bool on = ShortcutOn("twinbellyshape");
                    float fixedV = PgShape(true, percentPregnant, i, n);
                    if (fixedV != percentPregnant)
                    {
                        if (on) { percentPregnant = fixedV; refresh = fixedV > 0f; PatchCounters()[2902]++; }
                        else PatchCounters()[2903]++;
                    }
                }
                SetOrig(percentPregnant);
                // EA's SetPregnancy only stores the value when the Sim is already visually pregnant (the belly is redrawn
                // only for 0); review 2026-11-15: without this the corrected value never showed.
                if (refresh)
                    try { ((SimDescription)(object)this).RefreshBodyShape(); } catch (Exception) { PatchCounters()[2904]++; }
            }
            public void SetOrig(float percentPregnant) { } // receives SimDescription.SetPregnancy(float)
        }
    }
}
