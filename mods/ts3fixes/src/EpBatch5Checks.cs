namespace Ts3Fixes
{
    // Harness checks for EpBatch5Logic.cs and EpBatch5bLogic.cs (docs/587 §11, batch EP5). Pure decisions with controls; nothing in the world
    // is touched.
    public static partial class Fixes
    {
        static object Os5Check(int fam, string what)
        {
            string sw = kOsFam[fam];
            bool on = ShortcutOn(sw);
            return Ep4Check(sw, kOsSlot[fam], new[] { "effects auto-destroyed", "effects dropped with switch off", "errors" },
                sOsMissing, what, "dropped effect auto-destroyed", OsAutoDestroy(on, OsIsSiteEffect(kOsNames[0])),
                "no effect / other effects untouched",
                !OsAutoDestroy(on, false) && !OsAutoDestroy(false, true) && OsIsSiteEffect("ep7WandFail_main") && !OsIsSiteEffect("ep7WandFail") && !OsIsSiteEffect(null));
        }

        public static object ChemistryVfxCheck(string what) { return Os5Check(0, what); }
        public static object DartboardVfxCheck(string what) { return Os5Check(1, what); }
        public static object WardrobeWandVfxCheck(string what) { return Os5Check(2, what); }
        public static object PetDroolVfxCheck(string what) { return Os5Check(3, what); }
        public static object TimePortalVfxCheck(string what) { return Os5Check(4, what); }
        public static object HayStackVfxCheck(string what) { return Os5Check(5, what); }

        // HayScatterTimeCheck run: the scatter time is zeroed after a scatter. Control: off keeps EA's value.
        public static object HayScatterTimeCheck(string what)
        {
            bool on = ShortcutOn("hayscattertime");
            return Ep4Check("hayscattertime", 1575, new[] { "scatter times reset", "kept with switch off", "errors" }, EpMiss("mScatterTime", sHsTime), what,
                "6 -> 0 after a scatter", HsAfter(on, 6f) == 0f, "off keeps 6", HsAfter(false, 6f) == 6f);
        }

        // HauntedGnomeInvCheck run: a gnome in an inventory is cleaned. Controls: a placed gnome or the switch off is not.
        public static object HauntedGnomeInvCheck(string what)
        {
            bool on = ShortcutOn("hauntedgnomeinv");
            return Ep4Check("hauntedgnomeinv", 1578, new[] { "inventory gnomes cleaned", "alarm or fear kept with switch off", "errors" },
                EpMiss("HauntedAddToInventoryEvent", sHgAdd, "mGhostAlarm", sHgAlarm), what,
                "inventory gnome cleaned", HgClean(on, true), "placed gnome as EA", !HgClean(on, false) && !HgClean(false, true));
        }

        // HoudiniGnomeLeakCheck run: a created, unplaced gnome is destroyed. Controls: placed or not created is left.
        public static object HoudiniGnomeLeakCheck(string what)
        {
            bool on = ShortcutOn("houdinignomeleak");
            return Ep4Check("houdinignomeleak", 1581, new[] { "orphan gnomes destroyed", "fallback skipped, no home lot", "errors" }, "", what,
                "unplaced gnome destroyed", HgOrphanDestroy(on, true, false), "placed / none created / off left",
                !HgOrphanDestroy(on, true, true) && !HgOrphanDestroy(on, false, false) && !HgOrphanDestroy(false, true, false));
        }

        // ShowStageFxEndCheck run: a show end during a one-shot ends the one-shot; the EP6 failure roll maps to cases 1-2.
        // Controls: a show end with no one-shot, another event, and the off failure case (EA's 0 = no effect).
        public static object ShowStageFxEndCheck(string what)
        {
            bool on = ShortcutOn("showstagefxend");
            return Ep4Check("showstagefxend", 1584, new[] { "one-shots ended at show end / stale turn-ons / failures fired / effects disposed", "show ends dropped with switch off", "errors" },
                EpMiss("mOneShot", sE6OneShot, "mSavedEffect", sE6SavedEffect, "mSavedEffectDelay", sE6SavedDelay, "mEffect", sE6Effect, "mDelay", sE6Delay), what,
                "end during one-shot handled, roll 0 -> case 1", FxEndOneShot(on, true, true) && E6FailCase(on, 0) == 1,
                "no one-shot / other event as EA, off roll 0 -> 0", !FxEndOneShot(on, true, false) && !FxEndOneShot(on, false, true) && E6FailCase(false, 0) == 0);
        }

        // SoakedPetSelfReactCheck run: the shaker is not queued. Control: other Sims are.
        public static object SoakedPetSelfReactCheck(string what)
        {
            bool on = ShortcutOn("soakedpetselfreact");
            return Ep4Check("soakedpetselfreact", 1587, new[] { "self reactions skipped", "shakes run with switch off", "errors" },
                EpMiss("ShakeWaterOff", sSoType, "AddPuddle", sSoPuddle, "kReactionRadius", sSoRadius), what,
                "shaker skipped", !SpQueue(on, true), "others queued", SpQueue(on, false) && SpQueue(false, true));
        }

        // TagBagSurfaceLeakCheck run: an unplaced surface is destroyed. Controls: placed, destroyed or in world is left.
        public static object TagBagSurfaceLeakCheck(string what)
        {
            bool on = ShortcutOn("tagbagsurfaceleak");
            return Ep4Check("tagbagsurfaceleak", 1590, new[] { "surfaces destroyed", "leaked with switch off", "errors" }, "", what,
                "unplaced surface destroyed", TbDestroy(on, false, false, false),
                "placed / destroyed / in world left", !TbDestroy(on, true, false, false) && !TbDestroy(on, false, true, false) && !TbDestroy(on, false, false, true));
        }

        // EquestrianTrophyNameCheck run: the horse name key is kHorseName. Control: off reads EA's rider key.
        public static object EquestrianTrophyNameCheck(string what)
        {
            bool on = ShortcutOn("equestriantrophyname");
            return Ep4Check("equestriantrophyname", 1593, new[] { "horse names restored", "rider name kept with switch off", "errors" }, EpMiss("mHorseName", sEtHorse), what,
                "key 183657686", EtHorseKey(on) == 183657686u, "off key 183657670", EtHorseKey(false) == 183657670u);
        }

        // RubbleUseListCheck run: a Sim still on the pile's list is taken off. Control: a Sim not on it is left.
        public static object RubbleUseListCheck(string what)
        {
            bool on = ShortcutOn("rubbleuselist");
            return Ep4Check("rubbleuselist", 1596, new[] { "Sims taken off a pile's use list", "left on with switch off", "errors" }, "", what,
                "still on -> removed", CuRemove(on, true), "not on -> left", !CuRemove(on, false) && !CuRemove(false, true));
        }

        // BackpackLotLeakCheck run: an in-use backpack drops its handler; a dead one drops itself. Control: a live idle one.
        public static object BackpackLotLeakCheck(string what)
        {
            bool on = ShortcutOn("backpacklotleak");
            return Ep4Check("backpacklotleak", 1600, new[] { "lot handlers dropped", "left subscribed with switch off", "errors" }, EpMiss("OnLotChanged", sBpOnLot), what,
                "in use / no parent dropped", BpDrop(on, true, true) && BpDrop(on, false, false), "live idle kept", !BpDrop(on, false, true) && !BpDrop(false, true, true));
        }

        // StreetArtMasterBoostCheck run: a large mural [Med, High, Master] boosts index 2. Controls: a full row set boosts 3;
        // off boosts 3 only when it exists.
        public static object StreetArtMasterBoostCheck(string what)
        {
            bool on = ShortcutOn("streetartmasterboost");
            string[] large = { "Med", "High", "Master" }, full = { "Low", "Med", "High", "Master" };
            return Ep4Check("streetartmasterboost", 1603, new[] { "Master rows boosted", "boost missed with switch off", "errors" },
                EpMiss("StreetArtData", sSaData, "sStreetArtData", sSaList, "mWeights", sSaWeights, "mQuality", sSaQuality, "GetDuration", sSaDuration, "GetStreetArtVariationString", sSaVarString), what,
                "large mural boosts 2", SaBoostIndex(on, large) == 2, "full set 3, off large none",
                SaBoostIndex(on, full) == 3 && SaBoostIndex(false, large) == -1);
        }

        // BubblingSpringsResetCheck run: a missing broadcaster is recreated. Control: a present one is left.
        public static object BubblingSpringsResetCheck(string what)
        {
            bool on = ShortcutOn("bubblingspringsreset");
            return Ep4Check("bubblingspringsreset", 1606, new[] { "broadcasters recreated", "left missing with switch off", "errors" },
                EpMiss("SetBroadcaster", sBsSet, "mBubblingSpringsBroadcaster", sBsCast), what,
                "missing -> recreated", BsRecreate(on, true), "present left", !BsRecreate(on, false) && !BsRecreate(false, true));
        }
    }
}
