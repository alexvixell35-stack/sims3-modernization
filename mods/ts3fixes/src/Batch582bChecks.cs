using Sims3.Gameplay.Abstracts;

namespace Ts3Fixes
{
    // Harness checks for Batch582bLogic.cs (docs/582 batch 4). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // DoorLockOwnerCheck run: a SelectedActor lock to a gone owner opens; a SelectedHousehold lock to a gone owner is
        // re-pointed to a resident; an owner list loses the gone ids. Controls: resident owner kept; Nobody / VIPRoom / Pets
        // untouched; a player-chosen empty owner list kept; an owner list with only residents kept.
        public static object DoorLockOwnerCheck(string what)
        {
            string counters = B582Counters("doorlockowner", 2834, new[] { "locks fixed at removal or move-in", "seen with switch off", "saved stale fixed at load", "owner-list callbacks re-registered at load", "errors" }, sDlMissing);
            if (what == "result") return counters;
            if (what != "run") return "doorlockowner <run|result>";
            bool on = ShortcutOn("doorlockowner");
            // the decision itself is the same either way; with the switch off nothing applies it
            bool fixedNow = on && DlDecide(CommonDoor.tLock.SelectedActor, false, true, 0, 0) == kDlAnybody
                && DlDecide(CommonDoor.tLock.SelectedHousehold, false, true, 0, 0) == kDlRepoint
                && DlDecide(CommonDoor.tLock.SelectedHousehold, false, false, 0, 0) == kDlAnybody
                && DlDecide(CommonDoor.tLock.OwnerList, false, true, 3, 1) == kDlPrune
                && DlDecide(CommonDoor.tLock.OwnerList, false, false, 2, 2) == kDlAnybody;
            bool ctlResident = DlDecide(CommonDoor.tLock.SelectedActor, true, true, 0, 0) == kDlKeep && DlDecide(CommonDoor.tLock.SelectedHousehold, true, true, 0, 0) == kDlKeep;
            bool ctlOther = DlDecide(CommonDoor.tLock.Nobody, false, false, 0, 0) == kDlKeep && DlDecide(CommonDoor.tLock.VIPRoom, false, true, 0, 0) == kDlKeep
                && DlDecide(CommonDoor.tLock.Pets, false, true, 0, 0) == kDlKeep && DlDecide(CommonDoor.tLock.Anybody, false, false, 0, 0) == kDlKeep;
            bool ctlList = DlDecide(CommonDoor.tLock.OwnerList, false, true, 0, 0) == kDlKeep && DlDecide(CommonDoor.tLock.OwnerList, false, true, 2, 0) == kDlKeep;
            bool controls = ctlResident && ctlOther && ctlList;
            return B582Line("doorlockowner", on, "locks to gone owners opened / re-pointed / pruned", fixedNow,
                "resident owner kept=" + ctlResident + ", Nobody/VIP/Pets/Anybody untouched=" + ctlOther + ", empty or all-resident owner list kept=" + ctlList, controls, sDlMissing, counters);
        }

        // ElevatorKittenCheck run: a kitten or puppy is refused at planning, as the doors refuse it at the ride.
        //   Controls: other Sims unchanged; an answer that already refuses unchanged.
        public static object ElevatorKittenCheck(string what)
        {
            string counters = B582Counters("elevatorkitten", 2839, new[] { "kitten/puppy refused at planning", "seen with switch off", "errors" }, sEkMissing);
            if (what == "result") return counters;
            if (what != "run") return "elevatorkitten <run|result>";
            bool on = ShortcutOn("elevatorkitten");
            bool fixedNow = EkRefuse(on, true, true);
            bool ctlOther = !EkRefuse(on, true, false), ctlRefused = !EkRefuse(on, false, true);
            return B582Line("elevatorkitten", on, "kitten/puppy refused at planning", fixedNow,
                "adult or human unchanged=" + ctlOther + ", already refused unchanged=" + ctlRefused, ctlOther && ctlRefused, sEkMissing, counters);
        }

        // ElevatorResetRidersCheck run: a rider waiting with a live task is released at the elevator reset.
        //   Controls: an entry not yet waiting, a waiting entry with no live task (saved entry), a missing Sim: untouched.
        public static object ElevatorResetRidersCheck(string what)
        {
            string counters = B582Counters("elevatorresetriders", 2842, new[] { "waiting riders released at reset", "seen with switch off", "errors" }, sErMissing);
            if (what == "result") return counters;
            if (what != "run") return "elevatorresetriders <run|result>";
            bool on = ShortcutOn("elevatorresetriders");
            bool fixedNow = ErRelease(on, true, true, true);
            bool ctlNotWaiting = !ErRelease(on, false, true, true), ctlNoTask = !ErRelease(on, true, false, true), ctlNoSim = !ErRelease(on, true, true, false);
            return B582Line("elevatorresetriders", on, "waiting rider released at elevator reset", fixedNow,
                "not waiting untouched=" + ctlNotWaiting + ", no live task untouched=" + ctlNoTask + ", no Sim untouched=" + ctlNoSim, ctlNotWaiting && ctlNoTask && ctlNoSim, sErMissing, counters);
        }

        // ElevatorSavedLogCheck run: the load census classifies saved entries (log only; FIXED here means "the census runs").
        //   Controls: a sleeping entry with a live rider counts in neither column.
        public static object ElevatorSavedLogCheck(string what)
        {
            string missing = sErAssigned == null ? "ElevatorPortalComponent.mAssignedSims" : "";
            string counters = B582Counters("elevatorsavedlog", 2845, new[] { "elevators with saved entries", "entries not sleeping", "rider missing or no live task", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "elevatorsavedlog <run|result>";
            bool on = ShortcutOn("elevatorsavedlog");
            int[] r = EsClassify(new[] { false, true, true }, new[] { false, false, true }, new[] { true, true, true });
            bool fixedNow = on && r[0] == 1 && r[1] == 2;
            int[] k = EsClassify(new[] { true }, new[] { true }, new[] { true });
            bool controls = k[0] == 0 && k[1] == 0;
            return B582Line("elevatorsavedlog", on, "saved entries classified (not sleeping 1, stale 2)", fixedNow,
                "live sleeping entry counted nowhere=" + controls, controls, missing, counters);
        }

        // StarLightResetCheck run: the selected Sim is not added to the light during its own reset.
        //   Controls: another Sim added; an add outside the StarLight's reset (SwitchLight, any other object) kept.
        public static object StarLightResetCheck(string what)
        {
            string counters = B582Counters("starlightreset", 2849, new[] { "selected-Sim resets skipped", "seen with switch off", "errors" }, sSlMissing);
            if (what == "result") return counters;
            if (what != "run") return "starlightreset <run|result>";
            bool on = ShortcutOn("starlightreset");
            bool fixedNow = SlSkip(on, true, true);
            bool ctlOther = !SlSkip(on, true, false), ctlOutside = !SlSkip(on, false, true);
            return B582Line("starlightreset", on, "selected Sim not reset by the light's reset", fixedNow,
                "other Sim kept=" + ctlOther + ", add outside the reset kept=" + ctlOutside, ctlOther && ctlOutside, sSlMissing, counters);
        }

        // MoonDialListenerCheck run: Dispose removes the held moon listener. Control: none held -> nothing removed.
        public static object MoonDialListenerCheck(string what)
        {
            string counters = B582Counters("moondiallistener", 2852, new[] { "listeners removed at Dispose", "seen with switch off", "errors" }, sMdlMissing);
            if (what == "result") return counters;
            if (what != "run") return "moondiallistener <run|result>";
            bool on = ShortcutOn("moondiallistener");
            bool fixedNow = MdlRemove(on, true);
            bool controls = !MdlRemove(on, false);
            return B582Line("moondiallistener", on, "held listener removed at Dispose", fixedNow, "none held unchanged=" + controls, controls, sMdlMissing, counters);
        }

        // SnubListenerCheck run: the destroyed Sim's SnubManager listener is removed. Control: other listeners kept.
        public static object SnubListenerCheck(string what)
        {
            string counters = B582Counters("snublistener", 2855, new[] { "listeners removed at Sim.Destroy", "seen with switch off", "errors" }, sSnlMissing);
            if (what == "result") return counters;
            if (what != "run") return "snublistener <run|result>";
            bool on = ShortcutOn("snublistener");
            bool fixedNow = SnlRemove(on, true);
            bool controls = !SnlRemove(on, false);
            return B582Line("snublistener", on, "dead Sim's age-up listener removed", fixedNow, "other managers' listeners kept=" + controls, controls, sSnlMissing, counters);
        }
        // ElevatorRiderTimeoutCheck run: a rider waiting 31 minutes while a newer rider came 5 minutes ago is released.
        //   Controls: a lone rider (own = shared) fires at the same time either way; a rider under 30 minutes keeps waiting.
        public static object ElevatorRiderTimeoutCheck(string what)
        {
            string counters = B582Counters("elevatorridertimeout", 2858, new[] { "rides run by the replacement", "own-30-minute failsafe resets", "errors (EA ran)" }, sE2Missing);
            if (what == "result") return counters;
            if (what != "run") return "elevatorridertimeout <run|result>";
            bool on = ShortcutOn("elevatorridertimeout");
            bool fixedNow = E2Fire(on, 31f, 5f);
            bool ctlLone = E2Fire(on, 31f, 31f) == E2Fire(!on, 31f, 31f) && !E2Fire(on, 29f, 29f) == !E2Fire(!on, 29f, 29f);
            bool ctlYoung = !E2Fire(on, 10f, 5f);
            return B582Line("elevatorridertimeout", on, "rider released at its own 30 minutes", fixedNow,
                "lone rider same either way=" + ctlLone + ", rider under 30 minutes waits=" + ctlYoung, ctlLone && ctlYoung, sE2Missing, counters);
        }
    }
}
