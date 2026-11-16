namespace Ts3Fixes
{
    // Harness checks for EpBatch1aLogic.cs / EpBatch1bLogic.cs (docs/587, batch EP1). Pure decisions with controls;
    // nothing in the world is touched. Each "run" prints BROKEN with the switch off and FIXED with it on.
    public static partial class Fixes
    {
        static string EpMiss(params object[] namesAndMembers)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (int i = 0; i + 1 < namesAndMembers.Length; i += 2)
                if (namesAndMembers[i + 1] == null) b.Append(b.Length == 0 ? "" : ", ").Append((string)namesAndMembers[i]);
            return b.ToString();
        }

        // SuatGradeBonusCheck run: GPA A uses kGradeBonus[0] (best), school A uses [0]. Controls: GPA C and school B
        //   unchanged, GPA F and no grade get no multiplier.
        public static object SuatGradeBonusCheck(string what)
        {
            string miss = EpJoin(EpMiss("TakeAptitudeTest.kGradeBonus", sSuBonus), EpLive(typeof(BSuHook), 1, "UniversityWelcomeKit+TakeAptitudeTest", sSuTest, null, "TakeAptitudeTest.kGradeBonus", sSuBonus, typeof(float[])));
            string counters = B582Counters("suatgradebonus", 3241, new[] { "scores corrected", "differ with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "suatgradebonus <run|result>";
            bool on = ShortcutOn("suatgradebonus");
            bool fixedNow = SuIndex(on, true, 4) == 0 && SuIndex(on, false, 0) == 0 && SuIndex(on, true, 1) == 3 && SuCorrects(on, true, 4) && SuCorrects(on, false, 0);
            bool controls = SuIndex(on, true, 2) == 2 && SuIndex(on, false, 1) == 1 && SuIndex(on, true, 0) == -1 && SuIndex(on, false, -1) == -1
                && SuClamp(50) == 100 && SuClamp(520) == 400 && !SuCorrects(on, true, 2) && !SuCorrects(on, false, 1) && !SuCorrects(on, true, 0);
            return B582Line("suatgradebonus", on, "GPA A, school A best bonus, GPA D second worst", fixedNow, "GPA C / school B / F / no grade / clamp unchanged=" + controls, controls, miss, counters);
        }

        public static object CatToyPickerRelCheck(string what)
        {
            string miss = EpJoin(EpMiss("CatToyWallDancer.IsValidSim", sCtValidSim, "CatToyWallDancer.IsValidPet", sCtValidPet),
                EpLive(typeof(BCtHook), 1, "CatToyWallDancer.IsValidSim", sCtValidSim, 1, "CatToyWallDancer.IsValidPet", sCtValidPet, 1));
            string counters = B582Counters("cattoypickerrel", 3244, new[] { "strangers added", "left out with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "cattoypickerrel <run|result>";
            bool on = ShortcutOn("cattoypickerrel");
            bool fixedNow = CtAdmit(on, false, 0f, -10f);
            // (the positive-threshold control used to expect EA to leave the stranger out: EA admits it, so it failed with the switch off)
            bool controls = CtAdmit(on, true, -5f, -10f) && !CtAdmit(on, true, -20f, -10f) && CtAdmit(on, false, 0f, 10f) == !on;
            return B582Line("cattoypickerrel", on, "stranger listed at -10", fixedNow, "liking above / below as EA, positive threshold: stranger out only with the fix=" + controls, controls, miss, counters);
        }

    }
}
