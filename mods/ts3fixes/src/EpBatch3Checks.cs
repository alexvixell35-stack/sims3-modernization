using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Misc;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Register;
using Sims3.Gameplay.Objects.Rewards;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for EpBatch3Logic.cs (docs/587 §8, batch EP3). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static bool Ep3Near(float a, float b) { return Math.Abs(a - b) < 0.001f; }

        // FortuneTellerScamOddsCheck run: scam level 5 gets the scam interpolation. Controls: level 0 and the mystic row as EA.
        public static object FortuneTellerScamOddsCheck(string what)
        {
            string miss = EpJoin(EpMiss("kBasePsychicReadingChances", sFtBase, "kMaxScamArtistPsychicReadingChances", sFtScam, "kMaxMysticPsychicReadingChances", sFtMystic),
                EpLive(typeof(BEp3FtHook), 1, "kBasePsychicReadingChances", sFtBase, typeof(float[]), "kBasePsychicReadingChances read", sFtBase, "static",
                    "kMaxScamArtistPsychicReadingChances", sFtScam, typeof(float[]), "kMaxScamArtistPsychicReadingChances read", sFtScam, "static",
                    "kMaxMysticPsychicReadingChances", sFtMystic, typeof(float[]), "kMaxMysticPsychicReadingChances read", sFtMystic, "static"));
            string counters = B582Counters("fortunetellerscamodds", 3911, new[] { "scam rows rebuilt", "shared rows with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "fortunetellerscamodds <run|result>";
            bool on = ShortcutOn("fortunetellerscamodds");
            float[] bas = { 25f, 25f, 25f, 25f }, scam = { 5f, 25f, 30f, 40f }, mystic = { 5f, 10f, 35f, 50f };
            float[] r5 = FtScamRow(on, 5, bas, scam, mystic), r0 = FtScamRow(on, 0, bas, scam, mystic), m5 = FtScamRow(false, 5, bas, scam, mystic);
            bool fixedNow = Ep3Near(r5[1], 25f) && Ep3Near(r5[3], 32.5f);
            bool controls = Ep3Near(r0[0], 25f) && Ep3Near(m5[1], 17.5f) && Ep3Near(m5[3], 37.5f);
            return B582Line("fortunetellerscamodds", on, "scam L5 = 15/25/27.5/32.5", fixedNow, "L0 base, mystic L5 as EA=" + controls, controls, miss, counters);
        }

        // ConsignmentSkillCurveCheck run: level 10 gives 1 and level 5 gives .75. Controls: the curve math (EA's extrapolation).
        public static object ConsignmentSkillCurveCheck(string what)
        {
            string miss = sCsMissing;
            Type[] hooks = { typeof(BEp3Cs0), typeof(BEp3Cs1), typeof(BEp3Cs2) };
            for (int i = 0; i < 3; i++)
                miss = EpJoin(miss, EpLive(hooks[i], 1, sCgTypes[i].Name + ".sSkillVsSaleChanceCurve", sCsCurve[i], typeof(Curve), sCgTypes[i].Name + ".sSkillVsSaleChanceCurve read", sCsCurve[i], "static",
                    sCgTypes[i].Name + ".kSaleProbabilitySkillXValues", sCsX[i], typeof(float[]), sCgTypes[i].Name + ".kSaleProbabilitySkillYValues", sCsY[i], typeof(float[])));
            string counters = B582Counters("consignmentskillcurve", 3914, new[] { "curves rebuilt", "frequency curve with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "consignmentskillcurve <run|result>";
            bool on = ShortcutOn("consignmentskillcurve");
            float[] eaAtSkillX = new float[kCsSkX.Length];
            for (int j = 0; j < kCsSkX.Length; j++) eaAtSkillX[j] = CsLerp(kCsFqX, kCsFqY, kCsSkX[j]);
            bool fixedNow = CsRebuild(on, eaAtSkillX, kCsSkY) && Ep3Near(CsSkillFactor(on, 10f), 1f) && Ep3Near(CsSkillFactor(on, 5f), 0.75f);
            bool controls = !CsRebuild(on, kCsSkY, kCsSkY) && Ep3Near(CsLerp(kCsFqX, kCsFqY, 10f), -2.25f) && Ep3Near(CsLerp(kCsSkX, kCsSkY, 0f), 0.5f) && Ep3Near(CsLerp(kCsFqX, kCsFqY, 4f), 0.45f);
            return B582Line("consignmentskillcurve", on, "frequency curve rebuilt, L10 = 1, L5 = .75", fixedNow, "right curve kept, curve math incl. EA's -2.25 extrapolation=" + controls, controls, miss, counters);
        }

        // ConsignmentStockLeakCheck run: a dropped out-of-world object is destroyed. Controls: in world, in inventory, dead.
        public static object ConsignmentStockLeakCheck(string what)
        {
            string miss = sStMissing;
            Type[] hooks = { typeof(BEp3St0), typeof(BEp3St1), typeof(BEp3St2) };
            for (int i = 0; i < 3; i++)
            {
                List<object> t = new List<object>(new object[] { sCgTypes[i].Name + ".mObjectsForSale", sStList[i], typeof(IList),
                    kStData[i] + ".mObject", sStObj[i], typeof(IGameObject), kStData[i] + ".IsExpired", sStExpired[i], typeof(bool) });
                if (i > 0) t.AddRange(new object[] { kStData[i] + ".IsRotatable", sStRotatable[i], typeof(bool) });  // magic-hour rotation (PotionShop, BotShop)
                miss = EpJoin(miss, EpLive(hooks[i], i == 0 ? 1 : 2, t.ToArray()));
            }
            string counters = B582Counters("consignmentstockleak", 3917, new[] { "objects destroyed", "leaked with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "consignmentstockleak <run|result>";
            bool on = ShortcutOn("consignmentstockleak");
            bool fixedNow = OrphanDestroy(on, true, false, false);
            bool controls = !OrphanDestroy(on, true, true, false) && !OrphanDestroy(on, true, false, true) && !OrphanDestroy(on, false, false, false);
            return B582Line("consignmentstockleak", on, "dropped stock object destroyed", fixedNow, "world / inventory / dead kept=" + controls, controls, miss, counters);
        }

        // HerbQualityFloorCheck run: a Horrifying herb is not decreased. Controls: Putrid and Perfect decrease as EA.
        public static object HerbQualityFloorCheck(string what)
        {
            string miss = EpLive(typeof(BEp3HqHook), 1);
            string counters = B582Counters("herbqualityfloor", 3920, new[] { "decreases floored", "below floor with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "herbqualityfloor <run|result>";
            bool on = ShortcutOn("herbqualityfloor");
            bool fixedNow = HqFloored(on, 1), controls = !HqFloored(on, 2) && !HqFloored(on, 11);
            return B582Line("herbqualityfloor", on, "Horrifying stays", fixedNow, "Putrid / Perfect decrease=" + controls, controls, miss, counters);
        }

        // SnackBowlSlobVfxCheck run: the slob effect is a one-shot. Control: the patched type was found.
        public static object SnackBowlSlobVfxCheck(string what)
        {
            string miss = EpJoin(EpMiss("SnackBowl+HaveSomeSnack", sSbSnack), EpLive(typeof(BEp3SbHook), 1, "SnackBowl+HaveSomeSnack", sSbSnack, typeof(InteractionInstance)));
            string counters = B582Counters("snackbowlslobvfx", 3923, new[] { "one-shots fired", "leaked with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "snackbowlslobvfx <run|result>";
            bool on = ShortcutOn("snackbowlslobvfx");
            bool fixedNow = !SbLeaks(on), controls = sSbSnack != null;
            return B582Line("snackbowlslobvfx", on, "one-shot effect", fixedNow, "type found=" + controls, controls, miss, counters);
        }

        // WorldPondSkateTestCheck run: a failed shared test refuses Skate. Controls: EA refusal and a passing test as EA.
        public static object WorldPondSkateTestCheck(string what)
        {
            string miss = EpJoin(EpMiss("WorldPond.mSkatingAreas", sWpAreas), EpLive(typeof(BEp3WpHook), 1, "WorldPond.mSkatingAreas", sWpAreas, typeof(List<WorldPondSkatingArea>)));
            string counters = B582Counters("worldpondskatetest", 3926, new[] { "skate entries refused", "offered against test with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "worldpondskatetest <run|result>";
            bool on = ShortcutOn("worldpondskatetest");
            bool fixedNow = !WpOffered(on, true, false), controls = WpOffered(on, true, true) && !WpOffered(on, false, true);
            return B582Line("worldpondskatetest", on, "failed shared test refuses", fixedNow, "pass offered, EA refusal kept=" + controls, controls, miss, counters);
        }

        // PingPongOpponentCheck run: a pet and a toddler are removed. Controls: a human child and adult stay.
        public static object PingPongOpponentCheck(string what)
        {
            string miss = EpLive(typeof(BEp3PpHook), 2);
            string counters = B582Counters("pingpongopponent", 3929, new[] { "Sims removed", "non-players with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "pingpongopponent <run|result>";
            bool on = ShortcutOn("pingpongopponent");
            bool fixedNow = !PpKeep(on, false, true) && !PpKeep(on, true, false), controls = PpKeep(on, true, true);
            return B582Line("pingpongopponent", on, "pet and toddler removed", fixedNow, "human teen+ kept=" + controls, controls, miss, counters);
        }

        // MermaidDehydrateLookCheck run: look-ats come back after an entered run. Control: a run that never entered.
        public static object MermaidDehydrateLookCheck(string what)
        {
            string miss = EpJoin(EpMiss("OccultMermaid+Dehydrate", sMdType), EpLive(typeof(BEp3MdHook), 1, "OccultMermaid+Dehydrate", sMdType, typeof(InteractionInstance)));
            string counters = B582Counters("mermaiddehydratelook", 3932, new[] { "look-ats re-enabled", "left disabled with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "mermaiddehydratelook <run|result>";
            bool on = ShortcutOn("mermaiddehydratelook");
            bool fixedNow = MdReenable(on, true), controls = !MdReenable(on, false);
            return B582Line("mermaiddehydratelook", on, "look-ats re-enabled", fixedNow, "not entered untouched=" + controls, controls, miss, counters);
        }

        // MeteorMagnetOrphanCheck run: an out-of-world fragment is destroyed. Controls: in inventory or on the magnet kept.
        public static object MeteorMagnetOrphanCheck(string what)
        {
            string miss = EpJoin(EpMiss("AttractSpaceObject.mMeteoriteFragment", sMmFrag), EpLive(typeof(BEp3MmHook), 1, "AttractSpaceObject.mMeteoriteFragment", sMmFrag, typeof(IGameObject)));
            string counters = B582Counters("meteormagnetorphan", 3935, new[] { "fragments destroyed", "leaked with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "meteormagnetorphan <run|result>";
            bool on = ShortcutOn("meteormagnetorphan");
            bool fixedNow = OrphanDestroy(on, true, false, false), controls = !OrphanDestroy(on, true, false, true) && !OrphanDestroy(on, true, true, false);
            return B582Line("meteormagnetorphan", on, "orphan fragment destroyed", fixedNow, "inventory / world kept=" + controls, controls, miss, counters);
        }

        // DaycareLateAlarmCheck run: a gone or unmonitored child skips the alarm. Controls: a present child and force removal.
        public static object DaycareLateAlarmCheck(string what)
        {
            string miss = EpJoin(EpMiss("DaycareWorkdaySituation+RemoveChildHelper", sDcHelper, "mSituation", sDcSit, "mChildSimDescId", sDcId, "mForceRemove", sDcForce, "mChildMonitors", sDcMonitors),
                EpLive(typeof(BEp3DcHook), 1, "mSituation", sDcSit, typeof(DaycareWorkdaySituation), "mChildSimDescId", sDcId, typeof(ulong),
                    "mForceRemove", sDcForce, typeof(bool), "mChildMonitors", sDcMonitors, typeof(IDictionary)));
            string counters = B582Counters("daycarelatealarm", 3938, new[] { "stale alarms skipped", "would throw with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "daycarelatealarm <run|result>";
            bool on = ShortcutOn("daycarelatealarm");
            bool fixedNow = DcSkip(on, false, false, true) && DcSkip(on, false, true, false);
            bool controls = !DcSkip(on, false, true, true) && !DcSkip(on, true, false, false);
            return B582Line("daycarelatealarm", on, "gone / unmonitored child skipped", fixedNow, "present child, force removal as EA=" + controls, controls, miss, counters);
        }

        // BoxOfMysteryWooHooCheck run: a failed woohoo is not a success. Controls: a real woohoo and EA's false as EA.
        public static object BoxOfMysteryWooHooCheck(string what)
        {
            string miss = EpJoin(EpLive(typeof(BEp3BmHook), 1), EpLive(typeof(BEp3BmPostHook), 1));
            string counters = B582Counters("boxofmysterywoohoo", 3941, new[] { "failed woohoos, no roll", "rolled after failure with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "boxofmysterywoohoo <run|result>";
            bool on = ShortcutOn("boxofmysterywoohoo");
            bool fixedNow = !BmSucceeded(on, true, false), controls = BmSucceeded(on, true, true) && !BmSucceeded(on, false, true);
            return B582Line("boxofmysterywoohoo", on, "no woohoo, no pregnancy roll", fixedNow, "real woohoo rolls, EA false kept=" + controls, controls, miss, counters);
        }

        // BodySculptorWeightCheck run: Thin on -0.5 at 50% gives -0.75. Controls: Fat on 0.5 and Strong on 0.2 as EA.
        public static object BodySculptorWeightCheck(string what)
        {
            string miss = EpJoin(EpMiss("BodySculptor+ChangeShape", sBsShape, "ChangeShape+ItemDefinition.mOption", sBsOption, "ChangeShape.mFailure", sBsFailure, "BodySculptor.mPowerLevel", sBsPower),
                EpJoin(EpLive(typeof(BEp3BsHook), 1, "BodySculptor+ChangeShape", sBsShape, typeof(InteractionInstance), "ChangeShape+ItemDefinition.mOption", sBsOption, typeof(int),
                        "ChangeShape.mFailure", sBsFailure, typeof(bool), "BodySculptor.mPowerLevel", sBsPower, typeof(int)),
                    EpLive(typeof(BEp3BsDescHook), 1)));
            string counters = B582Counters("bodysculptorweight", 3944, new[] { "weights corrected", "mirrored with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "bodysculptorweight <run|result>";
            bool on = ShortcutOn("bodysculptorweight");
            bool fixedNow = Ep3Near(BsWeight(on, -0.5f, kBsThin, 0.5f), -0.75f) && Ep3Near(BsWeight(on, -0.2f, 3, 1f), -0.2f);
            bool controls = Ep3Near(BsWeight(on, 0.5f, kBsFat, 0.5f), 0.75f) && Ep3Near(BsWeight(on, 0.2f, 3, 1f), 0.2f) && Ep3Near(BsWeight(on, 0f, kBsThin, 0.5f), -0.5f);
            return B582Line("bodysculptorweight", on, "thin -0.5 -> -0.75, strong keeps -0.2", fixedNow, "fat / strong / thin from 0 as EA=" + controls, controls, miss, counters);
        }

        // NectarConsignValueCheck run: the roll tops at p3. Control: an equal range.
        public static object NectarConsignValueCheck(string what)
        {
            string miss = EpJoin(EpMiss("ConsignmentRegister+ConsignmentRegisterData", sNvData, "mParameters", sNvParams),
                EpLive(typeof(BEp3NvHook), 1, "ConsignmentRegisterData.mParameters", sNvParams, typeof(string[])));
            string counters = B582Counters("nectarconsignvalue", 3947, new[] { "values re-rolled", "minimum with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "nectarconsignvalue <run|result>";
            bool on = ShortcutOn("nectarconsignvalue");
            bool fixedNow = NvMax(on, 30, 60) == 60, controls = NvMax(on, 45, 45) == 45 && NvMax(on, 60, 30) == 60;
            return B582Line("nectarconsignvalue", on, "30-60 range rolls to 60", fixedNow, "equal / reversed range as EA=" + controls, controls, miss, counters);
        }

        // StrayGnomeTnsCheck run: the notice names the new type, once for 3 gnomes. Controls: same type, one gnome.
        public static object StrayGnomeTnsCheck(string what)
        {
            string miss = EpJoin(EpMiss("StrayWildlifeGnome+DisallowStrayWildlife", sGnRun, "Definition.mDisallowType", sGnType),
                EpJoin(EpLive(typeof(BEp3GnHook), 1, "StrayWildlifeGnome+DisallowStrayWildlife", sGnRun, typeof(InteractionInstance),
                        "DisallowStrayWildlife+Definition", sGnDef, typeof(InteractionDefinition), "Definition.mDisallowType", sGnType, typeof(StrayWildlifeGnome.DisallowType)),
                    EpLive(typeof(BEp3GnTnsHook), 1)));
            string counters = B582Counters("straygnometns", 3950, new[] { "runs with right notice", "stale notices with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "straygnometns <run|result>";
            bool on = ShortcutOn("straygnometns");
            bool fixedNow = GnTnsType(on, 0, 2) == 2 && GnTnsCount(on, 3) == 1;
            bool controls = GnTnsType(on, 2, 2) == 2 && GnTnsCount(on, 1) == 1 && GnTnsCount(on, 0) == 0;
            return B582Line("straygnometns", on, "new type, one notice", fixedNow, "same type, one gnome as EA=" + controls, controls, miss, counters);
        }
    }
}
