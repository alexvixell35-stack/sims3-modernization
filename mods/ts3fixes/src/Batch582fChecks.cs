using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness checks for Batch582fLogic.cs (docs/582 batch 7). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // BoatStaleSwimmerCheck run: keys 1(gone),2(live) -> EA throws, the fix removes 1 first. Controls: a stale last
        // key does not throw in EA; all live -> nothing removed.
        public static object BoatStaleSwimmerCheck(string what)
        {
            string missing = sBsWater == null ? "BoatOwnable.mPassengersInTheWater" : "";
            string counters = B582Counters("boatstaleswimmer", 2905, new[] { "stale keys removed", "would throw with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "boatstaleswimmer <run|result>";
            bool on = ShortcutOn("boatstaleswimmer");
            System.Predicate<ulong> live = delegate (ulong k) { return k % 2 == 0; };
            List<ulong> keys = new List<ulong> { 1, 2 };
            bool eaThrows = BsThrows(keys, live);
            List<ulong> stale = BsStale(keys, live);
            bool fixedNow = on && eaThrows && stale.Count == 1 && stale[0] == 1 && !BsThrows(new List<ulong> { 2 }, live);
            bool controls = !BsThrows(new List<ulong> { 2, 1 }, live) && BsStale(new List<ulong> { 2, 4 }, live).Count == 0;
            return B582Line("boatstaleswimmer", on, "stale swimmer removed before EA's loop", fixedNow, "stale last key / all live unchanged=" + controls, controls, missing, counters);
        }

        // OppCategoryProtectCheck run: category taken -> early (no protection). Controls: free category -> EA's accept.
        public static object OppCategoryProtectCheck(string what)
        {
            string missing = sOaShared == null || sOaValues == null ? "GenericManager.sDictionary/mValues" : "";
            string counters = B582Counters("oppcategoryprotect", 2908, new[] { "early returns kept unprotected", "seen with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "oppcategoryprotect <run|result>";
            bool on = ShortcutOn("oppcategoryprotect");
            bool fixedNow = on && OaEarly(true, true) && OaEarly(false, false);
            bool controls = !OaEarly(true, false);
            return B582Line("oppcategoryprotect", on, "category taken / no shared data skip the protection", fixedNow, "free category runs EA=" + controls, controls, missing, counters);
        }

        // NoCommercialCarCheck run: 0 other commercial lots -> idle loop. Control: 1 lot -> EA's drive.
        public static object NoCommercialCarCheck(string what)
        {
            string missing = sNcCar == null ? "CarNpcWithNoDriver+LiteDriver.mCar" : "";
            string counters = B582Counters("nocommercialcar", 2911, new[] { "cars idled instead of throwing", "seen with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "nocommercialcar <run|result>";
            bool on = ShortcutOn("nocommercialcar");
            bool fixedNow = on && NcNoTarget(0), controls = !NcNoTarget(1);
            return B582Line("nocommercialcar", on, "no other commercial lot -> car waits, then drives off", fixedNow, "a lot to drive to runs EA=" + controls, controls, missing, counters);
        }

        // ScavengerNoLotCheck run: 0 community lots -> base OnAdded only. Control: 1 lot -> EA's spawn.
        public static object ScavengerNoLotCheck(string what)
        {
            string counters = B582Counters("scavengernolot", 2914, new[] { "accepts without community lots handled", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "scavengernolot <run|result>";
            bool on = ShortcutOn("scavengernolot");
            bool fixedNow = on && ScNoLots(0), controls = !ScNoLots(1);
            return B582Line("scavengernolot", on, "no community lot -> accepted without the throw", fixedNow, "community lots run EA=" + controls, controls, "", counters);
        }

        // PartyLotListenerCheck run: no host -> stale. Controls: a hosted party keeps its listener.
        public static object PartyLotListenerCheck(string what)
        {
            string missing = sPlWaitM == null || sPlHappenM == null ? "NpcParty child OnSimLotChanged" : "";
            string counters = B582Counters("partylotlistener", 2917, new[] { "stale listeners removed", "would throw with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "partylotlistener <run|result>";
            bool on = ShortcutOn("partylotlistener");
            bool fixedNow = on && PlStale(true, false) && PlStale(false, false);
            bool controls = !PlStale(true, true);
            return B582Line("partylotlistener", on, "hostless party listener removed", fixedNow, "hosted party kept=" + controls, controls, missing, counters);
        }
    }
}
