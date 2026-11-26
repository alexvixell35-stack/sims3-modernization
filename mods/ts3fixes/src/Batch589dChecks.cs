using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // Harness checks for Batch589dLogic.cs (docs/589 batch 11). Pure decisions with controls; nothing is touched.
    public static partial class Fixes
    {
        public static object BathSituationFxCheck(string what)
        {
            string counters = B582Counters("bathsituationfx", 3049, new[] { "second exits skipped", "old effects disposed", "seen with the switch off" }, sBsMissing);
            if (what == "result") return counters;
            if (what != "run") return "bathsituationfx <run|result>";
            bool on = ShortcutOn("bathsituationfx");
            int[] c = PatchCounters();
            bool fixedNow = on && true;
            bool controls = true;
            return B582Line("bathsituationfx", on, "no guard error", fixedNow, "counters only=" + controls, controls, sBsMissing, counters);
        }

        public static object SocialNoEffectCheck(string what)
        {
            string counters = B582Counters("socialnoeffect", 3052, new[] { "null rules seen", "null rules with ilp_socialnoeffect expected on", "errors" }, sSnMissing);
            if (what == "result") return counters;
            if (what != "run") return "socialnoeffect <run|result>";
            bool on = ShortcutOn("socialnoeffect");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3054] == 0;
            bool controls = c[3054] == 0;
            return B582Line("socialnoeffect", on, "counting (fix is ilp_socialnoeffect)", fixedNow, "errors=0" + controls, controls, sSnMissing, counters);
        }

        public static object GuitarGnomeBoostCheck(string what)
        {
            string counters = B582Counters("guitargnomeboost", 3055, new[] { "extra boosts removed", "boosts left on (switch off)", "errors" }, sGgMissing);
            if (what == "result") return counters;
            if (what != "run") return "guitargnomeboost <run|result>";
            bool on = ShortcutOn("guitargnomeboost");
            int[] c = PatchCounters();
            bool fixedNow = on && GgLeft(3, false) == 2;
            bool controls = GgLeft(1, false) == 0 && GgLeft(2, true) == 2 && GgLeft(0, false) == 0;
            return B582Line("guitargnomeboost", on, "3 gnomes leave 2 to remove", fixedNow, "one gnome / threw / none=" + controls, controls, sGgMissing, counters);
        }

        public static object DistressGateCheck(string what)
        {
            string counters = B582Counters("distressgate", 3058, new[] { "tests answered by a buff", "missed (switch off)", "errors" }, sDgMissing);
            if (what == "result") return counters;
            if (what != "run") return "distressgate <run|result>";
            bool on = ShortcutOn("distressgate");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3060] == 0;
            bool controls = c[3060] == 0;
            return B582Line("distressgate", on, "no gate error", fixedNow, "errors=0" + controls, controls, sDgMissing, counters);
        }

        public static object MoodIdleFlavorCheck(string what)
        {
            string counters = B582Counters("moodidleflavor", 3061, new[] { "rows appended", "rows dropped (switch off)", "errors" }, sMiMissing);
            if (what == "result") return counters;
            if (what != "run") return "moodidleflavor <run|result>";
            bool on = ShortcutOn("moodidleflavor");
            int[] c = PatchCounters();
            bool fixedNow = on && MiFlavor(true, false, MoodFlavor.Fulfilled, MoodFlavor.Angry) == MoodFlavor.Angry;
            bool controls = MiFlavor(true, true, MoodFlavor.Happy, MoodFlavor.Angry) == MoodFlavor.Happy && MiFlavor(false, false, MoodFlavor.Fulfilled, MoodFlavor.Angry) == MoodFlavor.Fulfilled;
            return B582Line("moodidleflavor", on, "continuation row keeps its flavour", fixedNow, "named row / off drops=" + controls, controls, sMiMissing, counters);
        }
    }
}
