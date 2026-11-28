namespace Ts3Fixes
{
    // Harness checks for EpBatch6Logic.cs (docs/587 §12, batch EP6). Pure decisions with controls; nothing in the world
    // is touched.
    public static partial class Fixes
    {
        // FloorSwitchUseListCheck run: a Sim still on the plate's list is taken off. Control: a Sim not on it is left.
        public static object FloorSwitchUseListCheck(string what)
        {
            bool on = ShortcutOn("floorswitchuselist");
            return Ep4Check("floorswitchuselist", 3334, new[] { "Sims taken off a floor switch's use list", "left on with switch off", "errors" }, EpLive(typeof(BEp6FsHook), 1), what,
                "still on -> removed", CuRemove(on, true), "not on -> left", !CuRemove(on, false) && !CuRemove(false, true));
        }

        // DonationBuffChanceCheck run: roll 10 with 15% fires, roll 50 does not. Control: off is EA's inverse.
        public static object DonationBuffChanceCheck(string what)
        {
            bool on = ShortcutOn("donationbuffchance");
            return Ep4Check("donationbuffchance", 3337, new[] { "rolls that changed outcome", "rolls with switch off", "errors" }, EpLive(typeof(BEp6DbHook), 2), what,
                "10 < 15 fires, 50 does not", DbFires(on, 10f, 15f) && !DbFires(on, 50f, 15f), "off: 50 fires, 10 does not",
                DbFires(false, 50f, 15f) && !DbFires(false, 10f, 15f));
        }

        // VampireFruitSnackCheck run: Outstanding (10) at threshold 10 gives the Snack. Controls: 9 does not; Perfect does.
        public static object VampireFruitSnackCheck(string what)
        {
            bool on = ShortcutOn("vampirefruitsnack");
            return Ep4Check("vampirefruitsnack", 3340, new[] { "Sanguine Snacks given at the threshold", "Sated given with switch off", "errors" },
                EpJoin(EpMiss("kQualityForSated", sVfSated, "kQualityForSanguineSnack", sVfSnack),
                    EpLive(typeof(BEp6VfHook), 1, "kQualityForSated", sVfSated, typeof(Sims3.Gameplay.Objects.Quality), "kQualityForSated", sVfSated, "static",
                        "kQualityForSanguineSnack", sVfSnack, typeof(Sims3.Gameplay.Objects.Quality), "kQualityForSanguineSnack", sVfSnack, "static")), what,
                "10 >= 10 snack", VfSnack(on, 10, 10), "9 no, 11 yes, off 10 no", !VfSnack(on, 9, 10) && VfSnack(on, 11, 10) && !VfSnack(false, 10, 10));
        }

        // FallenRockCollectedCheck run: a collected rock is not destroyed by the timer. Control: an uncollected one is.
        public static object FallenRockCollectedCheck(string what)
        {
            bool on = ShortcutOn("fallenrockcollected");
            return Ep4Check("fallenrockcollected", 3343, new[] { "collected rocks kept", "destroyed with switch off", "errors" }, EpJoin(EpMiss("mCollected", sFrCollected), EpLive(typeof(BEp6FrHook), 1, "mCollected", sFrCollected, typeof(bool))), what,
                "collected kept", FrSkip(on, true), "uncollected times out as EA", !FrSkip(on, false) && !FrSkip(false, true));
        }
    }
}
