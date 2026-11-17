namespace Ts3Fixes
{
    // Harness checks for Batch582hLogic.cs (docs/582 batch 8b). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // InnerBeautyOnceCheck run: the same event a second time is skipped. Controls: a new event / switch off run EA.
        public static object InnerBeautyOnceCheck(string what)
        {
            string counters = B582Counters("innerbeautyonce", 2923, new[] { "duplicate calls skipped", "duplicates seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "innerbeautyonce <run|result>";
            bool on = ShortcutOn("innerbeautyonce");
            bool fixedNow = on && IbSkip(true, true, true) && IbSkip(true, false, false);
            bool controls = !IbSkip(true, false, true) && !IbSkip(false, true, false);
            return B582Line("innerbeautyonce", on, "one mean social handled once", fixedNow, "new event / off unchanged=" + controls, controls, "", counters);
        }

    }
}
