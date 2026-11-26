using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Harness checks for Batch589Logic.cs (docs/589 batch 11, U1-U5). Pure decisions with controls; nothing is touched.
    public static partial class Fixes
    {
        // EatPostOnceCheck run: a finished eat (not pending) is not posted again by Cleanup. Control: a cut-off eat is.
        public static object EatPostOnceCheck(string what)
        {
            string counters = B582Counters("eatpostonce", 2986, new[] { "Cleanup posts skipped", "doubled posts (switch off)", "cut-off eats posted by Cleanup" }, "");
            if (what == "result") return counters;
            if (what != "run") return "eatpostonce <run|result>";
            bool on = ShortcutOn("eatpostonce");
            bool fixedNow = on && !EpCleanupPosts(true, false);
            bool controls = EpCleanupPosts(true, true) && EpCleanupPosts(false, false);
            return B582Line("eatpostonce", on, "finished eat not posted twice", fixedNow, "cut-off eat posted / off as EA=" + controls, controls, "", counters);
        }

        // MotiveMultOnceCheck run: pre 10 + deltas 10,10 at x1.25 gives 37.5 once (EA stacks it to 47.66; s58 tested "> 50", never true). Controls: lemon loss, chip, sign.
        public static object MotiveMultOnceCheck(string what)
        {
            string counters = B582Counters("motivemultonce", 2989, new[] { "updates with the multiplier once", "stacked updates (switch off)", "errors" }, sMmMissing);
            if (what == "result") return counters;
            if (what != "run") return "motivemultonce <run|result>";
            bool on = ShortcutOn("motivemultonce");
            float once = MmApplyOnce(30f, true, true, false, false, 1.25f, 0.25f, 1.2f);
            float ea = MmEaStacked(10f, new float[] { 10f, 10f }, 1.25f);
            bool fixedNow = on && once == 37.5f && ea > once + 10f;
            bool controls = MmApplyOnce(-8f, true, false, true, false, 1.25f, 0.25f, 1.2f) == -2f && MmApplyOnce(10f, false, false, false, true, 1.25f, 0.25f, 1.2f) == 12f
                && MmApplyOnce(-10f, true, true, false, false, 1.25f, 0.25f, 1.2f) == -10f && MmEaStacked(0f, new float[] { 8f }, 1.25f) == MmApplyOnce(8f, true, true, false, false, 1.25f, 0.25f, 1.2f);
            return B582Line("motivemultonce", on, "Carefree x1.25 applied once", fixedNow, "lemon / chip / loss / one delta as EA=" + controls, controls, sMmMissing, counters);
        }

        // FightBroadcastEndCheck run: reports the counters (the dispose has no pure decision; the hook is the fix).
        public static object FightBroadcastEndCheck(string what)
        {
            string counters = B582Counters("fightbroadcastend", 2992, new[] { "broadcasters ended at Cleanup", "left running (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "fightbroadcastend <run|result>";
            bool on = ShortcutOn("fightbroadcastend");
            int[] c = PatchCounters();
            bool fixedNow = on && c[2994] == 0;
            bool controls = c[2994] == 0;
            return B582Line("fightbroadcastend", on, "no error disposing", fixedNow, "errors=0 " + controls, controls, "", counters);
        }

        // ReactionCopyCheck run: a later Sim starts from the caller's reaction. Control: off carries it over (EA).
        public static object ReactionCopyCheck(string what)
        {
            string counters = B582Counters("reactioncopy", 2995, new[] { "Sims given the caller's reaction", "Sims given an earlier Sim's trait reaction (switch off)", "errors" }, sRcoMissing);
            if (what == "result") return counters;
            if (what != "run") return "reactioncopy <run|result>";
            bool on = ShortcutOn("reactioncopy");
            bool fixedNow = on && RcStart(true, ReactionTypes.Shocked, ReactionTypes.TraitCoward) == ReactionTypes.Shocked;
            bool controls = RcStart(false, ReactionTypes.Shocked, ReactionTypes.TraitCoward) == ReactionTypes.TraitCoward;
            return B582Line("reactioncopy", on, "caller's reaction per Sim", fixedNow, "off carries over as EA=" + controls, controls, sRcoMissing, counters);
        }

        // CarpoolSchoolSkipCheck run: a school child with no job gets the school ride. Controls: teen with a job, car.
        public static object CarpoolSchoolSkipCheck(string what)
        {
            string counters = B582Counters("carpoolschoolskip", 2998, new[] { "skipping riders sent on their own", "Runs that threw (switch off)", "errors" }, sCpsMissing);
            if (what == "result") return counters;
            if (what != "run") return "carpoolschoolskip <run|result>";
            bool on = ShortcutOn("carpoolschoolskip");
            bool fixedNow = on && CsRide(true, true, false) == 1;
            bool controls = CsRide(true, true, true) == 1 && CsRide(false, false, true) == 2 && CsRide(false, true, false) == 0;
            return B582Line("carpoolschoolskip", on, "school child on the bus sent to school", fixedNow, "teen with job to school / carpool to work / none=" + controls, controls, sCpsMissing, counters);
        }
    }
}
