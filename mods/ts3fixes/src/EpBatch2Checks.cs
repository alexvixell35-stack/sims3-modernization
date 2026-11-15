namespace Ts3Fixes
{
    // Harness checks for EpBatch2Logic.cs (docs/587 §7, batch EP2). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // ConsignmentLastDayCheck run: a day-5 sale reads the last price, a day-6 item does not roll. Controls: days 1-4.
        public static object ConsignmentLastDayCheck(string what)
        {
            string counters = B582Counters("consignmentlastday", 1466, new[] { "last-day prices / returns", "would throw with switch off", "errors" }, sCgMissing);
            if (what == "result") return counters;
            if (what != "run") return "consignmentlastday <run|result>";
            bool on = ShortcutOn("consignmentlastday");
            bool fixedNow = CgPriceIndex(on, 5, 5) == 4 && !CgRolls(on, 6, 5);
            bool controls = CgPriceIndex(on, 1, 5) == 1 && CgPriceIndex(on, 4, 5) == 4 && CgRolls(on, 5, 5) && CgRolls(on, 1, 5);
            return B582Line("consignmentlastday", on, "day 5 in range, day 6 returned", fixedNow, "days 1-5 roll, days 1-4 prices as EA=" + controls, controls, sCgMissing, counters);
        }

        // BartendingHourlyPayCheck run: a 30 wage is paid. Control: no wage pays nothing.
        public static object BartendingHourlyPayCheck(string what)
        {
            string counters = B582Counters("bartendinghourlypay", 1469, new[] { "wages paid", "unpaid with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "bartendinghourlypay <run|result>";
            bool on = ShortcutOn("bartendinghourlypay");
            bool fixedNow = BhPaid(on, 30) == 30, controls = BhPaid(on, 0) == 0;
            return B582Line("bartendinghourlypay", on, "30 wage paid", fixedNow, "no wage pays nothing=" + controls, controls, "", counters);
        }

        // InfluenceFollowerBonusCheck run: 250 of 500 followers gives 0.5. Controls: 0, 500, 1000 and 5000 as EA.
        public static object InfluenceFollowerBonusCheck(string what)
        {
            string miss = EpMiss("mInfluenceNerdSkillModifier", sIfFields[0], "mInfluenceRebelSkillModifier", sIfFields[1], "mInfluenceSocialiteSkillModifier", sIfFields[2]);
            string counters = B582Counters("influencefollowerbonus", 1472, new[] { "modifiers scaled", "stepped with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "influencefollowerbonus <run|result>";
            bool on = ShortcutOn("influencefollowerbonus");
            bool fixedNow = IfValue(on, 250, 500) == 0.5f && IfValue(on, 750, 500) == 1.5f;
            bool controls = IfValue(on, 0, 500) == 0f && IfValue(on, 500, 500) == 1f && IfValue(on, 1000, 500) == 2f && IfValue(on, 5000, 500) == 2f;
            return B582Line("influencefollowerbonus", on, "250 -> 0.5, 750 -> 1.5", fixedNow, "0 / 500 / 1000 / clamp as EA=" + controls, controls, miss, counters);
        }

        // BroomArenaChoiceCheck run: a usable chosen broom is used. Controls: unusable or no choice -> EA's pick.
        public static object BroomArenaChoiceCheck(string what)
        {
            string miss = EpMiss("MagicBroomArena+RideBroomArena", sBaRide);
            string counters = B582Counters("broomarenachoice", 1475, new[] { "chosen brooms used", "dropped with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "broomarenachoice <run|result>";
            bool on = ShortcutOn("broomarenachoice");
            bool fixedNow = BaUsesChoice(on, true, true), controls = !BaUsesChoice(on, true, false) && !BaUsesChoice(on, false, true);
            return B582Line("broomarenachoice", on, "chosen broom ridden", fixedNow, "gone broom / no choice use EA's pick=" + controls, controls, miss, counters);
        }
    }
}
