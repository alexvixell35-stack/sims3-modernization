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
            return Ep4Check("spellbooksummonload", 3346, new[] { "Summon progress restored", "dropped with switch off", "errors" }, EpLive(typeof(BSt1SbHook), 1), what,
                "Summon 0.5 kept", SbKeep(on, 0, 0.5f), "no progress dropped, others as EA, off drops Summon",
                !SbKeep(on, 0, 0f) && SbKeep(on, 2, 0.5f) && !SbKeep(false, 0, 0.5f));
        }

        // ChocFountainGeyserCheck run: a live geyser alarm is removed before the refill. Control: none live, nothing done.
        public static object ChocFountainGeyserCheck(string what)
        {
            bool on = ShortcutOn("chocfountaingeyser");
            return Ep4Check("chocfountaingeyser", 3349, new[] { "old geyser alarms removed", "stacked with switch off", "errors" }, EpJoin(EpMiss("mGeyserAlarm", sCfGeyser), EpLive(typeof(BSt1CfHook), 1, "mGeyserAlarm", sCfGeyser, typeof(Sims3.Gameplay.Utilities.AlarmHandle))), what,
                "live alarm removed", CfRemove(on, true), "none live left", !CfRemove(on, false) && !CfRemove(false, true));
        }

        // HypnoTrickReleaseCheck run: the linked, locked loop is released. Controls: unlinked or unlocked loops are left.
        public static object HypnoTrickReleaseCheck(string what)
        {
            bool on = ShortcutOn("hypnotrickrelease");
            return Ep4Check("hypnotrickrelease", 3352, new[] { "volunteers released", "left locked with switch off", "errors" },
                EpJoin(EpMiss("BeHypnotizedMainLoop", sHtLoop, "mRouteLock", sHtLock), EpJoin(EpLive(typeof(BSt1HtHook), 1, "BeHypnotizedMainLoop", sHtLoop, typeof(Sims3.Gameplay.Interactions.InteractionInstance),
                    "mRouteLock", sHtLock, typeof(bool)), sHtLoop != null && sHtLock != null && !sHtLock.DeclaringType.IsAssignableFrom(sHtLoop) ? "mRouteLock not on BeHypnotizedMainLoop" : "")), what,
                "linked locked released", HtRelease(on, true, true), "unlinked / unlocked left", !HtRelease(on, false, true) && !HtRelease(on, true, false) && !HtRelease(false, true, true));
        }

        // VoodooFairyFadeInCheck run: a still-hidden fairy is shown. Control: an already shown one is left.
        public static object VoodooFairyFadeInCheck(string what)
        {
            bool on = ShortcutOn("voodoofairyfadein");
            return Ep4Check("voodoofairyfadein", 3355, new[] { "hidden fairies shown", "left hidden with switch off", "errors" },
                EpJoin(EpMiss("TransformSim", sVfType, "mVfxFairyHandle", sVfHandle),
                    EpLive(typeof(BSt1VfHook), 1, "TransformSim", sVfType, typeof(Sims3.Gameplay.Interactions.InteractionInstance), "mVfxFairyHandle", sVfHandle, typeof(Sims3.SimIFace.VisualEffect))), what,
                "hidden -> shown", VfShow(on, true), "shown left", !VfShow(on, false) && !VfShow(false, true));
        }

        // SaunaTubClearCheck run: a tub still held by this Sim is cleared. Control: another Sim's hold is left.
        public static object SaunaTubClearCheck(string what)
        {
            bool on = ShortcutOn("saunatubclear");
            return Ep4Check("saunatubclear", 3358, new[] { "tubs cleared", "left held with switch off", "errors" }, EpJoin(EpMiss("mSimInTub", sStTub), EpLive(typeof(BSt1StHook), 1, "mSimInTub", sStTub, typeof(Sims3.Gameplay.Actors.Sim))), what,
                "own hold cleared", StClear(on, true), "other hold left", !StClear(on, false) && !StClear(false, true));
        }

        // SlotMachineDefsCheck run: an autonomous play on a shared entry gets a copy. Controls: user picks and copies are left.
        public static object SlotMachineDefsCheck(string what)
        {
            bool on = ShortcutOn("slotmachinedefs");
            return Ep4Check("slotmachinedefs", 3361, new[] { "autonomous plays on a copy", "shared entry rewritten with switch off", "errors" }, EpJoin(EpMiss("PlaySlotDefs", sSmDefs, "InitFromID", sSmInit),
                    EpJoin(EpLive(typeof(BSt1SmHook), 1, "PlaySlots+Definition", sSmDef, typeof(Sims3.Gameplay.Interactions.InteractionDefinition), "PlaySlotDefs", sSmDefs, typeof(System.Array),
                        "PlaySlotDefs", sSmDefs, "static", "InitFromID", sSmInit, 1),
                        sSmDef != null && sSmDef.GetConstructor(kInstAll, null, System.Type.EmptyTypes, null) == null ? "PlaySlots+Definition has no parameterless constructor" : "")), what,
                "autonomous shared -> copy", SmCopy(on, true, true), "user pick / own copy / off left", !SmCopy(on, false, true) && !SmCopy(on, true, false) && !SmCopy(false, true, true));
        }

        // TreeWishingWellCheck run: a store WishingWell counts. Controls: no well false; EA's interface count kept.
        public static object TreeWishingWellCheck(string what)
        {
            bool on = ShortcutOn("treewishingwell");
            return Ep4Check("treewishingwell", 3364, new[] { "wells found", "missed with switch off", "errors" }, EpLive(typeof(BSt1TwHook), 1), what,
                "1 well -> true", TwHas(on, 0, 1), "none false, EA count kept, off false", !TwHas(on, 0, 0) && TwHas(on, 1, 0) && !TwHas(false, 0, 1));
        }

        // PlaypenTalkCastCheck run: a live broadcaster is disposed before a new one. Control: none live.
        public static object PlaypenTalkCastCheck(string what)
        {
            bool on = ShortcutOn("playpentalkcast");
            return Ep4Check("playpentalkcast", 3367, new[] { "old broadcasters disposed", "orphaned with switch off", "errors" }, EpJoin(EpMiss("mLearnToTalkBroadcaster", sPpCast), EpLive(typeof(BSt1PpHook), 1, "mLearnToTalkBroadcaster", sPpCast, typeof(Sims3.Gameplay.Autonomy.ReactionBroadcaster))), what,
                "live disposed", PpDispose(on, true), "none live left", !PpDispose(on, false) && !PpDispose(false, true));
        }

        // OvenMenuNullCheck run: a null food list returns no pick. Control: a list runs EA's pick.
        public static object OvenMenuNullCheck(string what)
        {
            bool on = ShortcutOn("ovenmenunull");
            return Ep4Check("ovenmenunull", 3370, new[] { "null lists skipped", "would throw with switch off", "errors" }, EpJoin(EpMiss("GetFoodsAtOrBelowCost", sOmFoods), EpLive(typeof(BSt1OmHook), 1, "GetFoodsAtOrBelowCost", sOmFoods, 1)), what,
                "null -> no pick", OmSkip(on, true), "list -> EA", !OmSkip(on, false) && !OmSkip(false, true));
        }

        // RouletteHackResetCheck run: a set hack is cleared on reset. Control: no hack left.
        public static object RouletteHackResetCheck(string what)
        {
            bool on = ShortcutOn("roulettehackreset");
            return Ep4Check("roulettehackreset", 3379, new[] { "hacks cleared on reset", "left hacked with switch off", "errors" }, EpJoin(EpMiss("RouletteTable.mCurrentHack", sRhHack[0], "PokerTable.mCurrentHack", sRhHack[1], "BlackjackTable.mCurrentHack", sRhHack[2],
                    "RouletteTable.ResetHack", sRhReset[0], "PokerTable.ResetHack", sRhReset[1], "BlackjackTable.ResetHack", sRhReset[2]),
                    EpJoin(EpLive(typeof(BSt1RhHook0), 1, "RouletteTable.mCurrentHack", sRhHack[0], typeof(int), "RouletteTable.ResetHack", sRhReset[0], 0),
                        EpJoin(EpLive(typeof(BSt1RhHook1), 1, "PokerTable.mCurrentHack", sRhHack[1], typeof(int), "PokerTable.ResetHack", sRhReset[1], 0),
                            EpLive(typeof(BSt1RhHook2), 1, "BlackjackTable.mCurrentHack", sRhHack[2], typeof(int), "BlackjackTable.ResetHack", sRhReset[2], 0)))), what,
                "Good -> None", RhClear(on, 1), "None left", !RhClear(on, 0) && !RhClear(false, 2));
        }

        // CoopEggQualityCheck run: index 7 of the coop array -> Quality 8 (Great). Controls: other arrays keep EA's index.
        public static object CoopEggQualityCheck(string what)
        {
            bool on = ShortcutOn("coopeggquality");
            return Ep4Check("coopeggquality", 3382, new[] { "eggs one tier up", "one tier low with switch off", "errors" }, EpJoin(sCeWeights == null ? "kEggQualityWeights" : "",
                    EpLive(typeof(BSt1CeHook), 1, "kEggQualityWeights", sCeField, typeof(float[]), "kEggQualityWeights", sCeField, "static")), what,
                "7 -> 8", CeIndex(on, true, 7, 11) == 8, "10 -> 11, other arrays and off as EA",
                CeIndex(true, true, 10, 11) == 11 && CeIndex(true, false, 7, 11) == 7 && CeIndex(false, true, 7, 11) == 7);  // the rules, not the switch (s58: off failed its own control)
        }

        // FvStandSkillCurveCheck run: the frequency-built curve is rebuilt. Control: off leaves it.
        public static object FvStandSkillCurveCheck(string what)
        {
            bool on = ShortcutOn("fvstandskillcurve");
            return Ep4Check("fvstandskillcurve", 3373, new[] { "curves rebuilt", "frequency curve left with switch off", "errors" }, EpLive(typeof(BSt1FvHook), 1), what,
                "rebuilt", FvRebuild(on, true), "own curve / off left", !FvRebuild(on, false) && !FvRebuild(false, true));
        }

        // PokerDeadPlayerCheck run: a seat whose Sim is gone is emptied. Controls: a live Sim or an empty seat is left.
        public static object PokerDeadPlayerCheck(string what)
        {
            bool on = ShortcutOn("pokerdeadplayer");
            return Ep4Check("pokerdeadplayer", 3376, new[] { "gone players unseated", "left seated with switch off", "errors" }, EpJoin(EpMiss("mPlayers", sPkPlayers, "mPlayersToAdd", sPkToAdd), EpLive(typeof(BSt1PkHook), 3, "mPlayers", sPkPlayers, typeof(ulong[]), "mPlayersToAdd", sPkToAdd, typeof(ulong[]))), what,
                "gone emptied", PkEmpty(on, 5, false), "live / empty seat left", !PkEmpty(on, 5, true) && !PkEmpty(on, 0, false) && !PkEmpty(false, 5, false));
        }
    }
}
