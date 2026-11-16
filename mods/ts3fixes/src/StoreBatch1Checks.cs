namespace Ts3Fixes
{
    // Harness checks for StoreBatch1Logic.cs (docs/587 §13, store batch 1). Pure decisions with controls; nothing in the
    // world is touched.
    public static partial class Fixes
    {
        // SpellBookSummonLoadCheck run: Summon (0) with progress is kept. Controls: no progress is dropped; off drops Summon.
        public static object SpellBookSummonLoadCheck(string what)
        {
            bool on = ShortcutOn("spellbooksummonload");
            return Ep4Check("spellbooksummonload", 1621, new[] { "Summon progress restored", "dropped with switch off", "errors" }, "", what,
                "Summon 0.5 kept", SbKeep(on, 0, 0.5f), "no progress dropped, others as EA, off drops Summon",
                !SbKeep(on, 0, 0f) && SbKeep(on, 2, 0.5f) && !SbKeep(false, 0, 0.5f));
        }

        // ChocFountainGeyserCheck run: a live geyser alarm is removed before the refill. Control: none live, nothing done.
        public static object ChocFountainGeyserCheck(string what)
        {
            bool on = ShortcutOn("chocfountaingeyser");
            return Ep4Check("chocfountaingeyser", 1624, new[] { "old geyser alarms removed", "stacked with switch off", "errors" }, EpMiss("mGeyserAlarm", sCfGeyser), what,
                "live alarm removed", CfRemove(on, true), "none live left", !CfRemove(on, false) && !CfRemove(false, true));
        }

        // HypnoTrickReleaseCheck run: the linked, locked loop is released. Controls: unlinked or unlocked loops are left.
        public static object HypnoTrickReleaseCheck(string what)
        {
            bool on = ShortcutOn("hypnotrickrelease");
            return Ep4Check("hypnotrickrelease", 1627, new[] { "volunteers released", "left locked with switch off", "errors" },
                EpMiss("BeHypnotizedMainLoop", sHtLoop, "RouteLock", sHtLock), what,
                "linked locked released", HtRelease(on, true, true), "unlinked / unlocked left", !HtRelease(on, false, true) && !HtRelease(on, true, false) && !HtRelease(false, true, true));
        }

        // VoodooFairyFadeInCheck run: a still-hidden fairy is shown. Control: an already shown one is left.
        public static object VoodooFairyFadeInCheck(string what)
        {
            bool on = ShortcutOn("voodoofairyfadein");
            return Ep4Check("voodoofairyfadein", 1630, new[] { "hidden fairies shown", "left hidden with switch off", "errors" },
                EpMiss("TransformSim", sVfType, "mVfxFairyHandle", sVfHandle), what,
                "hidden -> shown", VfShow(on, true), "shown left", !VfShow(on, false) && !VfShow(false, true));
        }

        // SaunaTubClearCheck run: a tub still held by this Sim is cleared. Control: another Sim's hold is left.
        public static object SaunaTubClearCheck(string what)
        {
            bool on = ShortcutOn("saunatubclear");
            return Ep4Check("saunatubclear", 1633, new[] { "tubs cleared", "left held with switch off", "errors" }, EpMiss("mSimInTub", sStTub), what,
                "own hold cleared", StClear(on, true), "other hold left", !StClear(on, false) && !StClear(false, true));
        }

        // SlotMachineDefsCheck run: InitFromID on a shared entry is skipped. Control: a non-shared definition is set.
        public static object SlotMachineDefsCheck(string what)
        {
            bool on = ShortcutOn("slotmachinedefs");
            return Ep4Check("slotmachinedefs", 1636, new[] { "shared entries kept", "rewritten with switch off", "errors" }, EpMiss("PlaySlotDefs", sSmDefs), what,
                "shared skipped", SmSkip(on, true), "own copy set", !SmSkip(on, false) && !SmSkip(false, true));
        }

        // TreeWishingWellCheck run: a store WishingWell counts. Controls: no well false; EA's interface count kept.
        public static object TreeWishingWellCheck(string what)
        {
            bool on = ShortcutOn("treewishingwell");
            return Ep4Check("treewishingwell", 1639, new[] { "wells found", "missed with switch off", "errors" }, "", what,
                "1 well -> true", TwHas(on, 0, 1), "none false, EA count kept, off false", !TwHas(on, 0, 0) && TwHas(on, 1, 0) && !TwHas(false, 0, 1));
        }

        // PlaypenTalkCastCheck run: a live broadcaster is disposed before a new one. Control: none live.
        public static object PlaypenTalkCastCheck(string what)
        {
            bool on = ShortcutOn("playpentalkcast");
            return Ep4Check("playpentalkcast", 1642, new[] { "old broadcasters disposed", "orphaned with switch off", "errors" }, EpMiss("mLearnToTalkBroadcaster", sPpCast), what,
                "live disposed", PpDispose(on, true), "none live left", !PpDispose(on, false) && !PpDispose(false, true));
        }

        // OvenMenuNullCheck run: a null food list returns no pick. Control: a list runs EA's pick.
        public static object OvenMenuNullCheck(string what)
        {
            bool on = ShortcutOn("ovenmenunull");
            return Ep4Check("ovenmenunull", 1645, new[] { "null lists skipped", "would throw with switch off", "errors" }, EpMiss("GetFoodsAtOrBelowCost", sOmFoods), what,
                "null -> no pick", OmSkip(on, true), "list -> EA", !OmSkip(on, false) && !OmSkip(false, true));
        }

        // RouletteHackResetCheck run: a set hack is cleared on reset. Control: no hack left.
        public static object RouletteHackResetCheck(string what)
        {
            bool on = ShortcutOn("roulettehackreset");
            return Ep4Check("roulettehackreset", 1654, new[] { "hacks cleared on reset", "left hacked with switch off", "errors" }, EpMiss("mCurrentHack", sRhHack), what,
                "Good -> None", RhClear(on, 1), "None left", !RhClear(on, 0) && !RhClear(false, 2));
        }

        // CoopEggQualityCheck run: index 7 of the coop array -> Quality 8 (Great). Controls: other arrays keep EA's index.
        public static object CoopEggQualityCheck(string what)
        {
            bool on = ShortcutOn("coopeggquality");
            return Ep4Check("coopeggquality", 1657, new[] { "eggs one tier up", "one tier low with switch off", "errors" }, sCeWeights == null ? "kEggQualityWeights" : "", what,
                "7 -> 8", CeIndex(on, true, 7, 11) == 8, "10 -> 11, other arrays and off as EA",
                CeIndex(on, true, 10, 11) == 11 && CeIndex(on, false, 7, 11) == 7 && CeIndex(false, true, 7, 11) == 7);
        }

        // FvStandSkillCurveCheck run: the frequency-built curve is rebuilt. Control: off leaves it.
        public static object FvStandSkillCurveCheck(string what)
        {
            bool on = ShortcutOn("fvstandskillcurve");
            return Ep4Check("fvstandskillcurve", 1648, new[] { "curves rebuilt", "frequency curve left with switch off", "errors" }, "", what,
                "rebuilt", FvRebuild(on, true), "own curve / off left", !FvRebuild(on, false) && !FvRebuild(false, true));
        }

        // PokerDeadPlayerCheck run: a seat whose Sim is gone is emptied. Controls: a live Sim or an empty seat is left.
        public static object PokerDeadPlayerCheck(string what)
        {
            bool on = ShortcutOn("pokerdeadplayer");
            return Ep4Check("pokerdeadplayer", 1651, new[] { "gone players unseated", "left seated with switch off", "errors" }, EpMiss("mPlayers", sPkPlayers, "mPlayersToAdd", sPkToAdd), what,
                "gone emptied", PkEmpty(on, 5, false), "live / empty seat left", !PkEmpty(on, 5, true) && !PkEmpty(on, 0, false) && !PkEmpty(false, 5, false));
        }
    }
}
