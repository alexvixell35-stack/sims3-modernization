using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI;

namespace Ts3Fixes
{
    // docs/596: Seasons weather outfits. Both switches off by default; both act on household Sims too (EA bug fixes, no
    // skip or throttle; user-directed picks keep EA's answer).
    //
    // outerwearautoloop (kOuterwearAutoLoop; docs/596 §1). [verified, decompiled] The autonomous Tests of Sim.ChangeIntoOuterwear,
    // Sim.ChangeOutOfOuterwear and ChangeToddlerClothes (outside, <= 55 F) do not test the guards that make their Run do
    // nothing: Run goes through Sim.GetOutfitForClothingChange (TemperatureTooCold / RemovingOuterwear), which answers "no
    // change" when the occult or a buff disallows clothes changes, the Sim wears the skinny-dipping towel, is the Grim Reaper,
    // or the player changed its clothes less than kClothingChangeHoursPlayerChoiceIsSticky (2) hours ago (both reasons are
    // in the "sticky" set's default branch). The Test passes, Run returns true without a change, the motive is unchanged, and
    // autonomy picks the same interaction again: for 2 sim-hours after the player dresses a Sim (or a toddler through
    // "Change Clothes", which marks the change user-directed) the Sim (or the parent) keeps re-running it. Fix: after EA's
    // Test passes (EA's own RNG use kept), an autonomous Test answers false when Run's guards certainly refuse. Guards that
    // depend on the interaction running at Run time (IDisallowClothingChange) are not used, so the answer is "no" only
    // where Run is a no-op. Counters: [2149] into answered no, [2150] out answered no, [2151] toddler answered no,
    // [2152] would loop with the switch off, [2153] errors.
    //
    // outerwearrestore (kOuterwearRestore; docs/596 §2). [verified, decompiled] Every path into outerwear (GoingOutside,
    // GoingToOutsideRoom, GoingOffLot, GoingToWork, GoingToSchool) stores the outfit to go back to in
    // Sim.OutfitCategoryToUseWhenSpinOutOfOuterwear; TemperatureTooCold (ChangeIntoOuterwear) does not, and RemovingOuterwear
    // reads and clears it. So after ChangeIntoOuterwear the Sim returns to Everyday or to a stale category left by an earlier
    // trip whose outerwear came off another way (swim, bed, bath). Fix: when ChangeIntoOuterwear's Run actually put the Sim
    // into outerwear from another category, that category is stored, as GoingOutside does. Counters: [2154] stored,
    // [2155] not stored (switch off), [2156] errors.
    public static partial class Fixes
    {
        static readonly Type sOwIntoRun = typeof(Sim).GetNestedType("ChangeIntoOuterwear", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sOwOutRun = typeof(Sim).GetNestedType("ChangeOutOfOuterwear", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sOwIntoDef = sOwIntoRun == null ? null : sOwIntoRun.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sOwOutDef = sOwOutRun == null ? null : sOwOutRun.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sOwTodDef = typeof(ChangeToddlerClothes).GetNestedType("ChangeToddlerClothesDefinition", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sOwLastPlayer = typeof(Sim).GetField("mLastPlayerDirectedClothingChange", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sOwSticky = typeof(Sim).GetField("kClothingChangeHoursPlayerChoiceIsSticky", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo sOwEaSticky = typeof(Sim).GetMethod("TestClothingChangeReasonAgainstPlayerDirectedChange", BindingFlags.Instance | BindingFlags.NonPublic);

        static string OwMissing()
        {
            string m = "";
            if (sOwIntoDef == null) m += " Sim+ChangeIntoOuterwear+Definition";
            if (sOwOutDef == null) m += " Sim+ChangeOutOfOuterwear+Definition";
            if (sOwTodDef == null) m += " ChangeToddlerClothes+ChangeToddlerClothesDefinition";
            if (sOwLastPlayer == null || sOwLastPlayer.FieldType != typeof(DateAndTime)) m += " Sim.mLastPlayerDirectedClothingChange";
            if (sOwSticky == null || sOwSticky.FieldType != typeof(float)) m += " Sim.kClothingChangeHoursPlayerChoiceIsSticky";
            return m.Trim();
        }

        static void ApplyOuterwearAutoLoop()
        {
            string miss = OwMissing();
            if (miss.Length > 0) { AppliedLog().Add("outerwearautoloop: not found: " + miss); return; }
            Type[] test = new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() };
            Patch(sOwIntoDef, "Test", test, typeof(OwTestHook), "IntoHook", "IntoOrig");
            Patch(sOwOutDef, "Test", test, typeof(OwTestHook), "OutHook", "OutOrig");
            Patch(sOwTodDef, "Test", test, typeof(OwTestHook), "TodHook", "TodOrig");
        }

        static void ApplyOuterwearRestore()
        {
            if (sOwIntoRun == null) { AppliedLog().Add("outerwearrestore: not found: Sim+ChangeIntoOuterwear"); return; }
            Patch(sOwIntoRun, "Run", Type.EmptyTypes, typeof(OwRunHook), "RunHook", "RunOrig");
        }

        // Pure: does Run's GetOutfitForClothingChange certainly answer "no change"? (EA's guard order; the sticky test only
        // when AlwaysChangeClothesAutonomously is off; both reasons fall in the sticky set's default branch)
        internal static bool OwRunRefuses(bool occultNo, bool buffNo, bool towel, bool reaper, bool sticky, bool always)
        {
            return occultNo || buffNo || towel || reaper || (sticky && !always);
        }

        // Pure: answer "no" after EA's Test passed? Only autonomous tests; user-directed keeps EA's answer.
        internal static bool OwAnswerNo(bool on, bool eaPassed, bool autonomous, bool refuses)
        {
            return on && eaPassed && autonomous && refuses;
        }

        // Pure: store the outfit to return to? (Run went from another category into outerwear)
        internal static bool OwRecord(bool on, OutfitCategories before, OutfitCategories after)
        {
            return on && before != OutfitCategories.Outerwear && after == OutfitCategories.Outerwear;
        }

        // Our copy of the player-directed sticky test (EA: SimClock.ElapsedTime(Hours, last) < sticky -> a reason outside the
        // listed set is refused). The check's 'run' compares it with EA's private method on live Sims.
        static bool OwSticky(Sim s)
        {
            DateAndTime last = (DateAndTime)sOwLastPlayer.GetValue(s);
            return SimClock.ElapsedTime(TimeUnit.Hours, last) < (float)sOwSticky.GetValue(null);
        }

        static bool OwRefuses(Sim s)
        {
            if (s == null || s.SimDescription == null || s.BuffManager == null) return false;
            bool occult = s.SimDescription.OccultManager != null && s.SimDescription.OccultManager.DisallowClothesChange();
            bool buff = s.BuffManager.DisallowClothesChange();
            bool towel = s.CurrentOutfitCategory == OutfitCategories.SkinnyDippingTowel;
            bool reaper = s.SimDescription.Service != null && s.SimDescription.Service.ServiceType == ServiceType.GrimReaper;
            return OwRunRefuses(occult, buff, towel, reaper, OwSticky(s), Sim.AlwaysChangeClothesAutonomously);
        }

        // After EA's Test: true keeps EA's answer, false answers "no".
        static bool OwAfterTest(bool ea, Sim subject, bool isAutonomous, int slot)
        {
            if (!ea || !isAutonomous) return ea;
            try
            {
                bool on = OwTestHook.On();
                if (!OwRefuses(subject)) return true;
                if (OwAnswerNo(on, true, true, true)) { PatchCounters()[slot]++; return false; }
                PatchCounters()[2152]++;
            }
            catch (Exception) { PatchCounters()[2153]++; }
            return true;
        }

        public class OwTestHook
        {
            internal static bool On() { return ShortcutOn("outerwearautoloop"); }

            public bool IntoHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool ea = IntoOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                return OwAfterTest(ea, a, isAutonomous, 1943);
            }
            public bool IntoOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // Sim+ChangeIntoOuterwear+Definition.Test

            public bool OutHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool ea = OutOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                return OwAfterTest(ea, a, isAutonomous, 1944);
            }
            public bool OutOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // Sim+ChangeOutOfOuterwear+Definition.Test

            // The toddler is the target; the autonomous Run uses TemperatureTooCold on it.
            public bool TodHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool ea = TodOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                return OwAfterTest(ea, target, isAutonomous, 1945);
            }
            public bool TodOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // ChangeToddlerClothes+ChangeToddlerClothesDefinition.Test
        }

        public abstract class OwRunHook : Interaction<Sim, Sim>
        {
            public bool RunHook()
            {
                Sim a = Actor;
                OutfitCategories before = a != null ? a.CurrentOutfitCategory : OutfitCategories.None;
                bool r = RunOrig();
                try
                {
                    if (a != null && !a.HasBeenDestroyed)
                    {
                        OutfitCategories after = a.CurrentOutfitCategory;
                        bool on = ShortcutOn("outerwearrestore");
                        if (OwRecord(on, before, after)) { a.OutfitCategoryToUseWhenSpinOutOfOuterwear = before; PatchCounters()[2154]++; }
                        else if (OwRecord(true, before, after)) PatchCounters()[2155]++;
                    }
                }
                catch (Exception) { PatchCounters()[2156]++; }
                return r;
            }
            public bool RunOrig() { return false; } // Sim+ChangeIntoOuterwear.Run
        }

        // OuterwearAutoLoopCheck run: the rule answers "no" only for an autonomous test EA passed whose Run refuses; live: our
        // sticky test equals EA's private one on every Sim, and where we say Run refuses, EA's GetOutfitForClothingChange
        // (TemperatureTooCold, pure) answers no change. 'result': counters + the cost of one weather-outfit decision.
        public static object OuterwearAutoLoopCheck(string what)
        {
            string counters = B582Counters("outerwearautoloop", 2149, new[] { "into answered no", "out answered no", "toddler answered no", "would loop (switch off)", "errors" }, OwMissing());
            if (what == "result") return counters + "; " + OwTiming();
            if (what != "run") return "outerwearautoloop <run|result>";
            bool on = ShortcutOn("outerwearautoloop");
            bool fixedNow = OwAnswerNo(on, true, true, true);
            bool controls = !OwAnswerNo(true, true, false, true) && !OwAnswerNo(true, false, true, true) && !OwAnswerNo(true, true, true, false)
                && !OwAnswerNo(false, true, true, true) && OwRunRefuses(false, false, false, false, true, false) && !OwRunRefuses(false, false, false, false, true, true)
                && !OwRunRefuses(false, false, false, false, false, false);
            string missing = OwMissing();
            int n = 0, same = 0, refused = 0, implied = 0;
            try
            {
                if (missing.Length == 0)
                    foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        if (s == null || s.HasBeenDestroyed || s.SimDescription == null || s.BuffManager == null) continue;
                        n++;
                        bool eaOk = sOwEaSticky == null || (bool)sOwEaSticky.Invoke(s, new object[] { Sim.ClothesChangeReason.TemperatureTooCold });
                        if (sOwEaSticky == null || eaOk == !OwSticky(s)) same++;
                        if (OwRefuses(s))
                        {
                            refused++;
                            OutfitCategories c;
                            if (!s.GetOutfitForClothingChange(Sim.ClothesChangeReason.TemperatureTooCold, out c)) implied++;
                        }
                    }
                controls &= same == n && implied == refused;
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            return B582Line("outerwearautoloop", on, "autonomous outerwear test answers no when Run cannot change", fixedNow,
                "user-directed/EA-failed/Run-can-change/off unchanged, sticky same as EA (" + same + "/" + n + "), refusals EA also refuses (" + implied + "/" + refused + ")",
                controls, missing, counters);
        }

        // Cost of one weather-outfit decision (the user's cache idea, docs/596 §4): EA's GetOutfitForClothingChange for
        // GoingOutside (the door callback's call) on the active Sim, field it may write restored, 20 calls.
        static string OwTiming()
        {
            try
            {
                Sim a = Sim.ActiveActor;
                if (a == null) return "timing: no active Sim";
                OutfitCategories keep = a.OutfitCategoryToUseWhenSpinOutOfOuterwear;
                OutfitCategories c;
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) a.GetOutfitForClothingChange(Sim.ClothesChangeReason.GoingOutside, out c);
                uint t1 = SrcNowUs();
                a.OutfitCategoryToUseWhenSpinOutOfOuterwear = keep;
                for (int i = 0; i < 20; i++) OwRefuses(a);
                uint t2 = SrcNowUs();
                return "timing: GoingOutside decision " + ((t1 - t0) / 20u) + " us, our refuse test " + ((t2 - t1) / 20u) + " us (temperature "
                    + Sims3.Gameplay.Seasons.SeasonsManager.Temperature + " F; one decision per door or room crossing per Sim)";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }

        // OuterwearRestoreCheck run: stored only on a real change into outerwear; controls: already outerwear, no change, off.
        public static object OuterwearRestoreCheck(string what)
        {
            string miss = sOwIntoRun == null ? "Sim+ChangeIntoOuterwear" : "";
            string counters = B582Counters("outerwearrestore", 2154, new[] { "stored", "not stored (switch off)", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "outerwearrestore <run|result>";
            bool on = ShortcutOn("outerwearrestore");
            bool fixedNow = OwRecord(on, OutfitCategories.Sleepwear, OutfitCategories.Outerwear);
            bool controls = !OwRecord(true, OutfitCategories.Outerwear, OutfitCategories.Outerwear) && !OwRecord(true, OutfitCategories.Everyday, OutfitCategories.Everyday)
                && !OwRecord(false, OutfitCategories.Everyday, OutfitCategories.Outerwear);
            return B582Line("outerwearrestore", on, "ChangeIntoOuterwear stores the outfit to return to", fixedNow,
                "already in outerwear/no change/off store nothing=" + controls, controls, miss, counters);
        }
    }
}
