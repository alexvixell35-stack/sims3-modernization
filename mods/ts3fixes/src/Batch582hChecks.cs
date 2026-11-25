namespace Ts3Fixes
{
    // Harness checks for Batch582hLogic.cs (docs/582 batch 8b). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // MidlifeWishSaveCheck run: a saved count is restored. Controls: key missing / switch off keep EA's value.
        public static object MidlifeWishSaveCheck(string what)
        {
            string missing = sMcWishes == null || sMcDate == null ? "MidlifeCrisisManager.mWishesFulfilled/mLastUpdatedDate" : "";
            string counters = B582Counters("midlifewishsave", 3651, new[] { "values saved or restored", "exports with wishes dropped (switch off)", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "midlifewishsave <run|result>";
            bool on = ShortcutOn("midlifewishsave");
            bool fixedNow = on && McRestore(true, 3);
            bool controls = !McRestore(true, uint.MaxValue) && !McRestore(false, 3);
            return B582Line("midlifewishsave", on, "wish count kept across travel", fixedNow, "missing key / off unchanged=" + controls, controls, missing, counters);
        }

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

        // BetrayedNullSimCheck run: selectable victim + no betrayer Sim -> EA throws, handled. Controls: live betrayer / NPC victim.
        public static object BetrayedNullSimCheck(string what)
        {
            string missing = sBtLoc == null ? "RomanceVisibilityState.LocalizeReputationKey" : "";
            string counters = B582Counters("betrayednullsim", 3654, new[] { "notices shown and break-ups done", "seen with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "betrayednullsim <run|result>";
            bool on = ShortcutOn("betrayednullsim");
            bool fixedNow = on && BtThrows(false, true);
            bool controls = !BtThrows(true, true) && !BtThrows(false, false);
            return B582Line("betrayednullsim", on, "betrayer without a Sim: notice and break-up", fixedNow, "live betrayer / NPC victim run EA=" + controls, controls, missing, counters);
        }

        // TopicTestNullCheck run: no Sim / no Athletic -> EA throws. Controls: live Sim / with Athletic run EA.
        public static object TopicTestNullCheck(string what)
        {
            string missing = sTtData == null || sTtFn == null ? "ActiveTopicData.sData/TestFunction" : "";
            string counters = B582Counters("topictestnull", 3657, new[] { "throws avoided", "seen with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "topictestnull <run|result>";
            bool on = ShortcutOn("topictestnull");
            bool fixedNow = on && TtThrows(0, false, true) && TtThrows(1, true, false) && TtThrows(2, false, true);
            bool controls = !TtThrows(0, true, true) && !TtThrows(1, false, true) && !TtThrows(4, true, false);
            return B582Line("topictestnull", on, "topic tests answer instead of throwing", fixedNow, "live Sim / Athletic present run EA=" + controls, controls, missing, counters);
        }

        // OppTrackerRelistenCheck run: EA's re-add path with the listener registered -> duplicate. Controls: any precondition off.
        public static object OppTrackerRelistenCheck(string what)
        {
            string counters = B582Counters("opptrackerrelisten", 3690, new[] { "duplicate registrations avoided", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "opptrackerrelisten <run|result>";
            bool on = ShortcutOn("opptrackerrelisten");
            bool fixedNow = on && OtDuplicate(true, false, true, true, true, true);
            bool controls = !OtDuplicate(false, false, true, true, true, true) && !OtDuplicate(true, true, true, true, true, true) && !OtDuplicate(true, false, true, true, false, true) && !OtDuplicate(true, false, true, true, true, false);
            return B582Line("opptrackerrelisten", on, "completion listener registered once", fixedNow, "queue full / has target / Sim gone / not registered unchanged=" + controls, controls, "", counters);
        }
    }
}
