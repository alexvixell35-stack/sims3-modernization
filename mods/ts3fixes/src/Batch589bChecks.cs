using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // Harness checks for Batch589bLogic.cs (docs/589 batch 11). Pure decisions with controls; nothing is touched.
    public static partial class Fixes
    {
        public static object TvSyncAlarmCheck(string what)
        {
            string counters = B582Counters("tvsyncalarm", 3001, new[] { "handles reset", "stale handles kept (switch off)", "errors" }, sTsMissing);
            if (what == "result") return counters;
            if (what != "run") return "tvsyncalarm <run|result>";
            bool on = ShortcutOn("tvsyncalarm");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3003] == 0;
            bool controls = c[3003] == 0;
            return B582Line("tvsyncalarm", on, "no reset error", fixedNow, "errors=0" + controls, controls, sTsMissing, counters);
        }

        public static object PhotoFundsCheck(string what)
        {
            string counters = B582Counters("photofunds", 3004, new[] { "photo payments taken", "funds doubled (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "photofunds <run|result>";
            bool on = ShortcutOn("photofunds");
            int[] c = PatchCounters();
            bool fixedNow = on && PhBuggy(500, 200, 200);
            bool controls = !PhBuggy(100, 200, -100) && !PhBuggy(500, 200, -50) && !PhBuggy(500, 0, 0);
            return B582Line("photofunds", on, "short funds taken, not added", fixedNow, "paid in full / other spend / broke untouched=" + controls, controls, "", counters);
        }

        public static object LtrHistoryBitsCheck(string what)
        {
            string counters = B582Counters("ltrhistorybits", 3007, new[] { "forced changes with history kept", "history wiped (switch off)", "errors" }, sLtMissing);
            if (what == "result") return counters;
            if (what != "run") return "ltrhistorybits <run|result>";
            bool on = ShortcutOn("ltrhistorybits");
            int[] c = PatchCounters();
            bool fixedNow = on && LtKeep(LongTermRelationship.InteractionBits.HasMet, LongTermRelationship.InteractionBits.HaveBeenFriends | LongTermRelationship.InteractionBits.Romantic, false) == (LongTermRelationship.InteractionBits.HasMet | LongTermRelationship.InteractionBits.HaveBeenFriends);
            bool controls = LtKeep(LongTermRelationship.InteractionBits.None, LongTermRelationship.InteractionBits.HaveBeenFriends, true) == LongTermRelationship.InteractionBits.None;
            return B582Line("ltrhistorybits", on, "HaveBeenFriends kept", fixedNow, "Stranger clears / other bits not restored=" + controls, controls, sLtMissing, counters);
        }

        public static object GoodbyePureCheck(string what)
        {
            string counters = B582Counters("goodbyepure", 3010, new[] { "menu builds kept pure", "side effects run (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "goodbyepure <run|result>";
            bool on = ShortcutOn("goodbyepure");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3012] == 0;
            bool controls = c[3012] == 0;
            return B582Line("goodbyepure", on, "no menu error", fixedNow, "errors=0" + controls, controls, "", counters);
        }

        public static object BathCleanupCheck(string what)
        {
            string counters = B582Counters("bathcleanup", 3013, new[] { "bathers' alarm and footprint kept", "cleared by another Sim's Cleanup (switch off)", "errors" }, sBcMissing);
            if (what == "result") return counters;
            if (what != "run") return "bathcleanup <run|result>";
            bool on = ShortcutOn("bathcleanup");
            int[] c = PatchCounters();
            bool fixedNow = on && BcKeeps(true, "a", "b");
            bool controls = !BcKeeps(true, "a", "a") && !BcKeeps(true, null, "a") && !BcKeeps(false, "a", "b");
            return B582Line("bathcleanup", on, "other bather's state kept", fixedNow, "own bath / empty tub / off as EA=" + controls, controls, sBcMissing, counters);
        }

        public static object TvWatchSkillCheck(string what)
        {
            string counters = B582Counters("tvwatchskill", 3016, new[] { "stray stops skipped", "stray stops (switch off)", "errors" }, sTwMissing);
            if (what == "result") return counters;
            if (what != "run") return "tvwatchskill <run|result>";
            bool on = ShortcutOn("tvwatchskill");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3018] == 0;
            bool controls = c[3018] == 0;
            return B582Line("tvwatchskill", on, "no Cleanup error", fixedNow, "errors=0" + controls, controls, sTwMissing, counters);
        }

        public static object ComputerGameTypeCheck(string what)
        {
            string counters = B582Counters("computergametype", 3019, new[] { "shared picks reset", "picks left in the Singleton (switch off)", "errors" }, sCgtMissing);
            if (what == "result") return counters;
            if (what != "run") return "computergametype <run|result>";
            bool on = ShortcutOn("computergametype");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3021] == 0;
            bool controls = c[3021] == 0;
            return B582Line("computergametype", on, "no reset error", fixedNow, "errors=0" + controls, controls, sCgtMissing, counters);
        }

        public static object MoodTankResetCheck(string what)
        {
            string counters = B582Counters("moodtankreset", 3022, new[] { "tank memories cleared", "kept with a recovered mood (switch off)", "errors" }, sMtMissing);
            if (what == "result") return counters;
            if (what != "run") return "moodtankreset <run|result>";
            bool on = ShortcutOn("moodtankreset");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3024] == 0;
            bool controls = c[3024] == 0;
            return B582Line("moodtankreset", on, "no reset error", fixedNow, "errors=0" + controls, controls, sMtMissing, counters);
        }

        public static object ReactionGrumpyCheck(string what)
        {
            string counters = B582Counters("reactiongrumpy", 3025, new[] { "reactions set", "left at Angry (switch off)", "errors" }, sRgMissing);
            if (what == "result") return counters;
            if (what != "run") return "reactiongrumpy <run|result>";
            bool on = ShortcutOn("reactiongrumpy");
            int[] c = PatchCounters();
            bool fixedNow = on && RgType(false, ReactionTypes.Shocked) == ReactionTypes.Shocked;
            bool controls = RgType(true, ReactionTypes.Cheer) == ReactionTypes.TraitGrumpy;
            return B582Line("reactiongrumpy", on, "negative reaction kept", fixedNow, "positive turns Grumpy=" + controls, controls, sRgMissing, counters);
        }

        public static object TrainSimGoneCheck(string what)
        {
            string counters = B582Counters("trainsimgone", 3028, new[] { "trainee-less runs ended", "runs that threw (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "trainsimgone <run|result>";
            bool on = ShortcutOn("trainsimgone");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3030] == 0;
            bool controls = c[3030] == 0;
            return B582Line("trainsimgone", on, "no guard error", fixedNow, "errors=0" + controls, controls, "", counters);
        }

        public static object StereoUpgradeCheck(string what)
        {
            string counters = B582Counters("stereoupgrade", 3031, new[] { "rebuilds skipped on a silent stereo", "dismissals of other dancers skipped", "off-switch hits" }, sSuMissing);
            if (what == "result") return counters;
            if (what != "run") return "stereoupgrade <run|result>";
            bool on = ShortcutOn("stereoupgrade");
            int[] c = PatchCounters();
            bool fixedNow = on && !SuRebuild(true, false);
            bool controls = SuRebuild(true, true) && SuRebuild(false, false);
            return B582Line("stereoupgrade", on, "silent stereo not rebuilt", fixedNow, "playing stereo / off as EA=" + controls, controls, sSuMissing, counters);
        }
    }
}
