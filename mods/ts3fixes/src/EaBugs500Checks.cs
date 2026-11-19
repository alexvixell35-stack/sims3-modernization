namespace Ts3Fixes
{
    // Harness checks of the docs/500 fixes (EaBugs500.cs): "run" evaluates each fix's decision with controls (read-only) and
    // the live swaps; "result" shows the counters and members found. Repros: tools/harness/repros.json <switch>-check.
    public static partial class Fixes
    {
        // PlaypenFlagsCheck run: an aborted run's own marks are cleared. Controls: another toddler's or adult's mark, a mark set
        //   before the run, and no mark are left.
        public static object PlaypenFlagsCheck(string what)
        {
            bool on = ShortcutOn("playpenflags");
            return Ep4Check("playpenflags", 4017, new[] { "marks cleared after an aborted run", "left set with switch off", "errors" },
                EpJoin(EpMiss("mToddlerBeingPickedUp1", sE5Pick1, "mToddlerBeingPickedUp2", sE5Pick2, "mPlayingWithOutsideSim", sE5Playing, "mOutsideSim", sE5Outside, "mSyncing", sE5Sync, "PlaySocial.mPP", sE5SocialPP, "PlayWithOutsideSim.Definition.mPlaypen", sE5OutsideDefPen),
                    EpJoin(EpLive(typeof(E500TakeOutHook), 1), EpJoin(EpLive(typeof(E500OutsideHook), 1), EpLive(typeof(E500SocialHook), 1)))), what,
                "own marks cleared", on && Eb500Core.PpClearId(5, 5) && Eb500Core.PpClearOutside(true, 7, 7) && Eb500Core.PpClearSync(false, true),
                "other / earlier / no mark left", !Eb500Core.PpClearId(5, 6) && !Eb500Core.PpClearId(0, 0) && !Eb500Core.PpClearOutside(true, 7, 8) && !Eb500Core.PpClearOutside(false, 7, 7) && !Eb500Core.PpClearSync(true, true) && !Eb500Core.PpClearSync(false, false));
        }

        // VoodooBindWaitCheck run: a cancelled binder or a destroyed sleeper ends the wait. Control: a plain sleep keeps waiting.
        public static object VoodooBindWaitCheck(string what)
        {
            bool on = ShortcutOn("voodoobindwait");
            return Ep4Check("voodoobindwait", 4020, new[] { "waits ended on cancel or gone", "sleeping-target waits on EA's body (switch off)", "errors" },
                EpLive(typeof(E500BindHook), 1), what,
                "cancel / gone ends the wait", on && Eb500Core.VbStop(true, false) && Eb500Core.VbStop(false, true),
                "plain sleep waits", !Eb500Core.VbStop(false, false));
        }

        // SaunaWooHooPregCheck run: the switch decides alone (a visibly pregnant target fails first); the swap must be live.
        public static object SaunaWooHooPregCheck(string what)
        {
            bool on = ShortcutOn("saunawoohoopreg");
            return Ep4Check("saunawoohoopreg", 4023, new[] { "pregnant targets refused", "offered with switch off", "unused" },
                EpLive(typeof(E500SaunaHook), 1), what,
                "pregnant target refused", on, "other targets to EA's test", true);
        }

        // LotEntryPetCheck run: a non-human's failed validation is undone and a poisoned cache is repaired. Controls: a human's,
        //   an already validated cache and a found spot are left.
        public static object LotEntryPetCheck(string what)
        {
            bool on = ShortcutOn("lotentrypet");
            return Ep4Check("lotentrypet", 4026, new[] { "caches unvalidated after a non-human failure", "validated with switch off", "errors", "lots unvalidated at world load" },
                EpJoin(EpMiss("Lot.mBestRouteIntoLotLocation", sE5LotSpot), EpLive(typeof(E500LotEntryHook), 1)), what,
                "pet failure unvalidated", on && Eb500Core.LeUnvalidate(false, true, false, true) && Eb500Core.LeRepair(true, true),
                "human / earlier / spot found left", !Eb500Core.LeUnvalidate(false, true, true, true) && !Eb500Core.LeUnvalidate(true, true, false, true) && !Eb500Core.LeUnvalidate(false, true, false, false) && !Eb500Core.LeRepair(true, false) && !Eb500Core.LeRepair(false, true));
        }

        // HorseLotEntryCheck run: a non-player horse on the ground with an off-ground door spot, a mailbox and refusing
        //   portals skips the door plan. Controls: a human, a player horse, a horse already upstairs, a ground door spot, no
        //   mailbox, an open portal and a portal with no openings are left to EA.
        public static object HorseLotEntryCheck(string what)
        {
            bool on = ShortcutOn("horselotentry");
            return Ep4Check("horselotentry", 4030, new[] { "doomed horse door plans skipped", "doomed plans seen with switch off", "mispredicted (off arm, must stay 0)", "errors", "ms in PlanRouteIntoLot, off arm", "ms in PlanRouteIntoLot, on arm" },
                EpJoin(EpMiss("Lot.mBestRouteIntoLotLocation", sE5LotSpot), EpLive(typeof(E500LotEntryHook), 1)), what,
                "doomed plan skipped", on && Eb500Core.HlDoomed(true, false, true, 0, 1, true, true) && Eb500Core.HlOneLevel(new[] { 1, 1 }),
                "others left to EA", !Eb500Core.HlDoomed(false, false, true, 0, 1, true, true) && !Eb500Core.HlDoomed(true, true, true, 0, 1, true, true)
                    && !Eb500Core.HlDoomed(true, false, true, 1, 1, true, true) && !Eb500Core.HlDoomed(true, false, true, 0, 0, true, true)
                    && !Eb500Core.HlDoomed(true, false, true, 0, 1, false, true) && !Eb500Core.HlDoomed(true, false, true, 0, 1, true, false)
                    && !Eb500Core.HlOneLevel(new int[0]) && !Eb500Core.HlOneLevel(new[] { 0, 1 }));
        }
    }
}
