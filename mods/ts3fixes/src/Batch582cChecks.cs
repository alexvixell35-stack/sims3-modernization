using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for Batch582cLogic.cs (docs/582 batch 5). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // RoleNullGuardCheck run: a target with no role gets "no" before EA's throwing test. Control: a target with a role -> EA's code.
        public static object RoleNullGuardCheck(string what)
        {
            string counters = B582Counters("rolenullguard", 2861, new[] { "answered no role", "seen with switch off", "errors" }, sRnMissing);
            if (what == "result") return counters;
            if (what != "run") return "rolenullguard <run|result>";
            bool on = ShortcutOn("rolenullguard");
            bool fixedNow = RnSkip(on, true) && RnSocialThrows(false, false, true, true), controls = !RnSkip(on, false) && !RnSocialThrows(false, true, true, true) && !RnSocialThrows(false, false, true, false);
            return B582Line("rolenullguard", on, "no-role target answered before EA's throw", fixedNow, "target with a role runs EA's code=" + controls, controls, sRnMissing, counters);
        }

        // HomeworkSkillBonusCheck run: level 5 of 10 adds k/2. Controls: level 10 adds nothing (EA right there), level 0 nothing.
        public static object HomeworkSkillBonusCheck(string what)
        {
            string counters = B582Counters("homeworkskillbonus", 2864, new[] { "rates raised", "seen with switch off", "errors" }, sHwMissing);
            if (what == "result") return counters;
            if (what != "run") return "homeworkskillbonus <run|result>";
            bool on = ShortcutOn("homeworkskillbonus");
            bool fixedNow = HwMissing(on, 5, 10, 5f, 1f) == 2.5f && HwMissing(on, 5, 10, 5f, 2f) == 5f;
            bool ctlMax = HwMissing(on, 10, 10, 5f, 1f) == 0f, ctlZero = HwMissing(on, 0, 10, 5f, 1f) == 0f;
            return B582Line("homeworkskillbonus", on, "level 5/10 bonus 2.5 (x2 copying 5)", fixedNow, "max level unchanged=" + ctlMax + ", level 0 unchanged=" + ctlZero, ctlMax && ctlZero, sHwMissing, counters);
        }

        // HorseRanchChallengeCheck run: no horse -> hidden. Controls: a horse -> active; EA's other answers untouched.
        public static object HorseRanchChallengeCheck(string what)
        {
            string counters = B582Counters("horseranchchallenge", 2867, new[] { "answers hidden", "seen with switch off", "errors" }, sHrMissing);
            if (what == "result") return counters;
            if (what != "run") return "horseranchchallenge <run|result>";
            bool on = ShortcutOn("horseranchchallenge");
            bool fixedNow = HrHide(on, true, 0);
            bool ctlHorse = !HrHide(on, true, 2), ctlOther = !HrHide(on, false, 0);
            return B582Line("horseranchchallenge", on, "no-horse household hidden", fixedNow, "household with a horse active=" + ctlHorse + ", other answers untouched=" + ctlOther, ctlHorse && ctlOther, sHrMissing, counters);
        }

        // DigSiteEmptyPickCheck run: an empty pick keeps the site's treasure. Control: a pick with treasure spends one.
        public static object DigSiteEmptyPickCheck(string what)
        {
            string counters = B582Counters("digsiteemptypick", 2870, new[] { "empty digs kept the treasure", "seen with switch off", "errors" }, sDsMissing);
            if (what == "result") return counters;
            if (what != "run") return "digsiteemptypick <run|result>";
            bool on = ShortcutOn("digsiteemptypick");
            bool fixedNow = !DsSpend(on, 0, 0), controls = DsSpend(on, 1, 0) && DsSpend(on, 0, kDsMaxEmpty);
            return B582Line("digsiteemptypick", on, "empty pick keeps the treasure", fixedNow, "real pick / capped empty run spends one=" + controls, controls, sDsMissing, counters);
        }

        // OppChainWorldCheck run: source world set, target Undefined -> world-change branch. Controls: same worlds, both
        // Undefined, EA's other clause true (EA already branches), target world set (EA already right): untouched.
        public static object OppChainWorldCheck(string what)
        {
            string counters = B582Counters("oppchainworld", 2873, new[] { "world-change branch taken", "seen with switch off", "errors" }, sOcMissing);
            if (what == "result") return counters;
            if (what != "run") return "oppchainworld <run|result>";
            bool on = ShortcutOn("oppchainworld");
            bool fixedNow = OcMissed(on, WorldName.China, WorldName.Undefined, false);
            bool controls = !OcMissed(on, WorldName.China, WorldName.China, false) && !OcMissed(on, WorldName.Undefined, WorldName.Undefined, false)
                && !OcMissed(on, WorldName.China, WorldName.Undefined, true) && !OcMissed(on, WorldName.Undefined, WorldName.China, false);
            return B582Line("oppchainworld", on, "source-only world change detected", fixedNow, "same / none / EA-branched / target-set untouched=" + controls, controls, sOcMissing, counters);
        }

        // TeleportCommunityLotCheck run: no valid lot -> refused. Controls: lots exist -> EA's true; EA's false unchanged.
        public static object TeleportCommunityLotCheck(string what)
        {
            string counters = B582Counters("teleportcommunitylot", 2876, new[] { "options hidden", "seen with switch off", "errors" }, sTcMissing);
            if (what == "result") return counters;
            if (what != "run") return "teleportcommunitylot <run|result>";
            bool on = ShortcutOn("teleportcommunitylot");
            bool fixedNow = TcRefuse(on, true, 0), ctlLots = !TcRefuse(on, true, 3), ctlFalse = !TcRefuse(on, false, 0);
            return B582Line("teleportcommunitylot", on, "no-lot world refused", fixedNow, "lots exist kept=" + ctlLots + ", EA false unchanged=" + ctlFalse, ctlLots && ctlFalse, sTcMissing, counters);
        }

        // PhotoSubjectStarsCheck run: 50/100 points, 25/50 value, weights 1/1 -> 0.5 (EA's int division gives 0).
        //   Controls: the maximum subject is 1 either way; a zero maximum gives 0 (EA's guard).
        public static object PhotoSubjectStarsCheck(string what)
        {
            string counters = B582Counters("photosubjectstars", 2879, new[] { "values corrected", "seen with switch off", "errors" }, sPsMissing);
            if (what == "result") return counters;
            if (what != "run") return "photosubjectstars <run|result>";
            bool on = ShortcutOn("photosubjectstars");
            bool fixedNow = on && PsDifficulty(50, 100, 25, 50, 1f, 1f) == 0.5f;
            bool controls = PsDifficulty(100, 100, 50, 50, 1f, 1f) == 1f && PsDifficulty(5, 0, 5, 10, 1f, 1f) == 0f;
            return B582Line("photosubjectstars", on, "mid subject 0.5 not 0", fixedNow, "max subject 1 and zero max 0=" + controls, controls, sPsMissing, counters);
        }
    }
}
