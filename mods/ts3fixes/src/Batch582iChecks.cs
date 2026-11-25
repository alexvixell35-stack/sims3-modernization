namespace Ts3Fixes
{
    // Harness checks for Batch582iLogic.cs (docs/582 batch 9). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // TraitRemoveNullCheck run: a trait missing from the dictionary on a live Sim throws in EA. Controls: trait in the
        // dictionary / Sim not instantiated are EA's code.
        public static object TraitRemoveNullCheck(string what)
        {
            string counters = B582Counters("traitremovenull", 3693, new[] { "removals finished", "would have thrown (switch off)", "errors" }, sTnMissing);
            if (what == "result") return counters;
            if (what != "run") return "traitremovenull <run|result>";
            bool on = ShortcutOn("traitremovenull");
            bool fixedNow = on && TnThrows(false, true);
            bool controls = !TnThrows(true, true) && !TnThrows(false, false);
            return B582Line("traitremovenull", on, "missing-trait removal finished", fixedNow, "in dictionary / not instantiated unchanged=" + controls, controls, sTnMissing, counters);
        }

        // HomeworkCopyOldCheck run: copying old homework runs at the old multiplier (2 -> 1). Controls: same age / off.
        public static object HomeworkCopyOldCheck(string what)
        {
            string counters = B582Counters("homeworkcopyold", 3696, new[] { "loop ticks at the old rate", "seen with switch off", "errors" }, sHcMissing);
            if (what == "result") return counters;
            if (what != "run") return "homeworkcopyold <run|result>";
            bool on = ShortcutOn("homeworkcopyold");
            bool fixedNow = on && HcRate(true, 8f, true, false, 2f, 1f) == 4f;
            bool controls = HcRate(true, 8f, false, false, 2f, 1f) == 8f && HcRate(false, 8f, true, false, 2f, 1f) == 8f && HcRate(true, 8f, true, false, 0f, 1f) == 8f;
            return B582Line("homeworkcopyold", on, "old homework copied at x1", fixedNow, "current homework / off / k 0 unchanged=" + controls, controls, sHcMissing, counters);
        }

        // LaundryMoodEachCheck run: a second Sim's buff gets the multiplier once. Controls: no multiplier / already raised / off.
        public static object LaundryMoodEachCheck(string what)
        {
            string counters = B582Counters("laundrymoodeach", 3820, new[] { "buffs given the multiplier", "Sims EA left without it (switch off)", "errors" }, sLmMissing);
            if (what == "result") return counters;
            if (what != "run") return "laundrymoodeach <run|result>";
            bool on = ShortcutOn("laundrymoodeach");
            bool fixedNow = on && LmTarget(true, 10, 10, 1.5f) == 15;
            bool controls = LmTarget(true, 10, 10, 1f) == 10 && LmTarget(true, 15, 10, 1.5f) == 15 && LmTarget(true, 22, 10, 1.5f) == 22 && LmTarget(false, 10, 10, 1.5f) == 10;
            return B582Line("laundrymoodeach", on, "each Sim's buff multiplied once", fixedNow, "x1 / already raised / never doubled / off unchanged=" + controls, controls, sLmMissing, counters);
        }

        // PartyCleanupNullCheck run: a missing host or Sim makes EA's CleanUp throw. Controls: everyone present.
        public static object PartyCleanupNullCheck(string what)
        {
            string counters = B582Counters("partycleanupnull", 3823, new[] { "cleanups finished null-safe", "would have thrown (switch off)", "errors" }, sPcMissing);
            if (what == "result") return counters;
            if (what != "run") return "partycleanupnull <run|result>";
            bool on = ShortcutOn("partycleanupnull");
            bool fixedNow = on && PcThrows(false, true) && PcThrows(true, false);
            bool controls = !PcThrows(true, true) && PcSimsOk(new System.Collections.Generic.List<Sims3.Gameplay.Actors.Sim>(), true)
                && !PcSimsOk(new System.Collections.Generic.List<Sims3.Gameplay.Actors.Sim> { null }, true);
            return B582Line("partycleanupnull", on, "gone host / guest cleaned up", fixedNow, "all present runs EA=" + controls, controls, sPcMissing, counters);
        }

        // SafeLocUnhideCheck run: the reset fallback unhides. Controls: no fallback move / off.
        public static object SafeLocUnhideCheck(string what)
        {
            string counters = B582Counters("safelocunhide", 3826, new[] { "fallback placements unhidden", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "safelocunhide <run|result>";
            bool on = ShortcutOn("safelocunhide");
            bool fixedNow = on && SuUnhide(true, true);
            bool controls = !SuUnhide(true, false) && !SuUnhide(false, true);
            return B582Line("safelocunhide", on, "fallback-placed Sim unhidden", fixedNow, "found spot / off unchanged=" + controls, controls, "", counters);
        }

        // MidlifeUiEndingCheck run: EA's end test as the getter sees it. Controls: time left does not end.
        public static object MidlifeUiEndingCheck(string what)
        {
            string counters = B582Counters("midlifeuiending", 3829, new[] { "reads that would have ended the crisis", "seen with switch off", "errors" }, sMuMissing);
            if (what == "result") return counters;
            if (what != "run") return "midlifeuiending <run|result>";
            bool on = ShortcutOn("midlifeuiending");
            bool fixedNow = on && MuEnds(60f, 600f);
            bool controls = !MuEnds(1440f, 600f) && !MuEnds(600f, 600f);
            return B582Line("midlifeuiending", on, "tooltip read ends nothing", fixedNow, "time left unchanged=" + controls, controls, sMuMissing, counters);
        }

        // NemesisNoRomanceCheck run: a relative (romance test false) becomes a nemesis. Controls: pet / toddler / off.
        public static object NemesisNoRomanceCheck(string what)
        {
            string counters = B582Counters("nemesisnoromance", 3832, new[] { "nemeses made where EA's romance test refused", "seen with switch off", "errors" }, sInMissing);
            if (what == "result") return counters;
            if (what != "run") return "nemesisnoromance <run|result>";
            bool on = ShortcutOn("nemesisnoromance");
            bool fixedNow = on && InFires(true, true, false, true);
            bool controls = InFires(false, true, true, true) && !InFires(true, false, false, true) && !InFires(true, true, false, false) && !InFires(false, true, false, true);
            return B582Line("nemesisnoromance", on, "relative can become a nemesis", fixedNow, "romance pair / pet / toddler / off unchanged=" + controls, controls, sInMissing, counters);
        }
    }
}
