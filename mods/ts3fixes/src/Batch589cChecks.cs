using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // Harness checks for Batch589cLogic.cs (docs/589 batch 11). Pure decisions with controls; nothing is touched.
    public static partial class Fixes
    {
        public static object BuffRemoveDelayedCheck(string what)
        {
            string counters = B582Counters("buffremovedelayed", 3034, new[] { "live buffs removed after a pending timer", "left on the Sim (switch off)", "errors" }, sBrdMissing);
            if (what == "result") return counters;
            if (what != "run") return "buffremovedelayed <run|result>";
            bool on = ShortcutOn("buffremovedelayed");
            int[] c = PatchCounters();
            bool fixedNow = on && BrLeft(true, true, true);
            bool controls = !BrLeft(true, true, false) && !BrLeft(false, true, true) && !BrLeft(true, false, true);
            return B582Line("buffremovedelayed", on, "live buff left is caught", fixedNow, "removed / not live / no timer untouched=" + controls, controls, sBrdMissing, counters);
        }

        public static object TvDiscListenerCheck(string what)
        {
            string counters = B582Counters("tvdisclistener", 3037, new[] { "old listeners removed", "old listeners kept (switch off)", "errors" }, sTdMissing);
            if (what == "result") return counters;
            if (what != "run") return "tvdisclistener <run|result>";
            bool on = ShortcutOn("tvdisclistener");
            int[] c = PatchCounters();
            bool fixedNow = on && c[3039] == 0;
            bool controls = c[3039] == 0;
            return B582Line("tvdisclistener", on, "no listener error", fixedNow, "errors=0" + controls, controls, sTdMissing, counters);
        }

        public static object FridgeTestNullCheck(string what)
        {
            string counters = B582Counters("fridgetestnull", 3040, new[] { "tests answered without the throw", "tests that threw (switch off)", "errors" }, sFtMissing);
            if (what == "result") return counters;
            if (what != "run") return "fridgetestnull <run|result>";
            bool on = ShortcutOn("fridgetestnull");
            int[] c = PatchCounters();
            bool fixedNow = on && FtAnswer(false, false, true);
            bool controls = !FtAnswer(true, true, true) && !FtAnswer(true, false, false);
            return B582Line("fridgetestnull", on, "no-component fridge answered", fixedNow, "open secondary false / no inventory false=" + controls, controls, sFtMissing, counters);
        }

        public static object CancelPostureDefCheck(string what)
        {
            string counters = B582Counters("cancelposturedef", 3043, new[] { "duplicate stand-ups not queued", "cancels run as EA (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "cancelposturedef <run|result>";
            bool on = ShortcutOn("cancelposturedef");
            int[] c = PatchCounters();
            bool fixedNow = on && CpAlready("d", "d");
            bool controls = !CpAlready("d", "e") && !CpAlready(null, null);
            return B582Line("cancelposturedef", on, "same definition at head found", fixedNow, "other / none=" + controls, controls, "", counters);
        }

        public static object FacialIdleWeightCheck(string what)
        {
            string counters = B582Counters("facialidleweight", 3046, new[] { "lists built with float shares", "lists with a truncated share (switch off)", "errors" }, sFiMissing);
            if (what == "result") return counters;
            if (what != "run") return "facialidleweight <run|result>";
            bool on = ShortcutOn("facialidleweight");
            int[] c = PatchCounters();
            bool fixedNow = on && FiShare(true, 6, 7) > 0.85f;
            bool controls = FiShare(false, 6, 7) == 0f && FiShare(true, 6, 3) == FiShare(false, 6, 3);
            return B582Line("facialidleweight", on, "7 idles share 6/7", fixedNow, "EA truncates to 0 / even split same=" + controls, controls, sFiMissing, counters);
        }
    }
}
