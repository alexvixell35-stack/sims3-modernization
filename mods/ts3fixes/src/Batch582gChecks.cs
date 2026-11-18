namespace Ts3Fixes
{
    // Harness checks for Batch582gLogic.cs (docs/582 batch 8). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // TrashPileImportCheck run: imported pile with a live alarm -> release; Ancient -> through Old. Control: a new
        // Fresh pile (no alarm, no effects) runs EA's code unchanged.
        public static object TrashPileImportCheck(string what)
        {
            string missing = sTpAlarm == null || sTpBroad == null || sTpToAncient == null ? "TrashPile.mAgeAlarm/mDisgustedEffectBroadcast/AlarmCallbackOldToAncient" : "";
            string counters = B582Counters("trashpileimport", 3545, new[] { "stale age setups released", "Ancient piles built through Old", "seen with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "trashpileimport <run|result>";
            bool on = ShortcutOn("trashpileimport");
            bool fixedNow = on && TpPlan(true, false, false) == 1 && TpPlan(true, false, true) == 3 && TpPlan(false, false, true) == 2;
            bool controls = TpPlan(false, false, false) == 0;
            return B582Line("trashpileimport", on, "imported pile releases its first age setup; Ancient built through Old", fixedNow, "new Fresh pile runs EA=" + controls, controls, missing, counters);
        }

        // CatGymWatchCheck run: no reference + cat on a gym -> fill. Controls: reference set or cat off the gym -> EA.
        public static object CatGymWatchCheck(string what)
        {
            string counters = B582Counters("catgymwatch", 3612, new[] { "gym references filled", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "catgymwatch <run|result>";
            bool on = ShortcutOn("catgymwatch");
            bool fixedNow = on && CgDecide(true, false, true) == 1 && CgDecide(false, false, true) == 2;
            bool controls = CgDecide(true, true, true) == 0 && CgDecide(true, false, false) == 0;
            return B582Line("catgymwatch", on, "broadcast watch gets its gym", fixedNow, "set reference / cat off gym run EA=" + controls, controls, "", counters);
        }

        // BadSecretKeyCheck run: EA's text equals the wrong key's text -> replace. Controls: other text, empty wrong text.
        public static object BadSecretKeyCheck(string what)
        {
            string counters = B582Counters("badsecretkey", 3615, new[] { "texts corrected", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "badsecretkey <run|result>";
            bool on = ShortcutOn("badsecretkey");
            bool fixedNow = on && SxIsWrong("\"Gameplay/Socializing/SecretsEnemy\"", "\"Gameplay/Socializing/SecretsEnemy\"");
            bool controls = !SxIsWrong("\"some gossip\"", "\"Gameplay/Socializing/SecretsEnemy\"") && !SxIsWrong("\"\"", "\"\"") && !SxIsWrong(null, "\"x\"");
            return B582Line("badsecretkey", on, "wrong-key secret text replaced by the right key", fixedNow, "gossip / empty / null kept=" + controls, controls, "", counters);
        }

        // TraitListenerRemoveCheck run: live trait with a listener -> removed. Controls: switch off / no listener -> EA.
        public static object TraitListenerRemoveCheck(string what)
        {
            string counters = B582Counters("traitlistenerremove", 3639, new[] { "listeners removed", "left with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "traitlistenerremove <run|result>";
            bool on = ShortcutOn("traitlistenerremove");
            bool fixedNow = on && TlRemove(true, true);
            bool controls = !TlRemove(false, true) && !TlRemove(true, false);
            return B582Line("traitlistenerremove", on, "lost trait's listener removed", fixedNow, "off / no listener unchanged=" + controls, controls, "", counters);
        }

        // WeddingListenerCheck run: 1 registered listener -> 1 removed. Controls: switch off / none registered -> 0.
        public static object WeddingListenerCheck(string what)
        {
            string missing = sWlList == null ? "WeddingParty.mEventListeners" : "";
            string counters = B582Counters("weddinglistener", 2920, new[] { "listeners removed", "left with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "weddinglistener <run|result>";
            bool on = ShortcutOn("weddinglistener");
            bool fixedNow = on && WlRemove(true, 1) == 1;
            bool controls = WlRemove(false, 1) == 0 && WlRemove(true, 0) == 0;
            return B582Line("weddinglistener", on, "ended wedding party drops its marriage listener", fixedNow, "off / none registered unchanged=" + controls, controls, missing, counters);
        }
    

        // PortAskNoHomeCheck run: target at home, homeless actor -> EA throws, the fix returns false. Controls: target away /
        // same home / both homes set -> EA's test.
        public static object PortAskNoHomeCheck(string what)
        {
            string counters = B582Counters("portasknohome", 3642, new[] { "throws avoided", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "portasknohome <run|result>";
            bool on = ShortcutOn("portasknohome");
            bool fixedNow = on && PtThrows(true, false, true, false) && PtThrows(true, false, false, true);
            bool controls = !PtThrows(false, false, true, true) && !PtThrows(true, true, true, true) && !PtThrows(true, false, false, false);
            return B582Line("portasknohome", on, "homeless Sim: port test returns false instead of throwing", fixedNow, "away / same home / both homes run EA=" + controls, controls, "", counters);
        }

        // LectureBroadcasterCheck run: a repeat lecture disposes the earlier broadcaster. Controls: switch off / first lecture.
        public static object LectureBroadcasterCheck(string what)
        {
            string missing = sLbParams == null || sLbCallback == null ? "Education.kGreatLectureBroadcastParams/BeIntriguedCallback" : "";
            string counters = B582Counters("lecturebroadcaster", 3645, new[] { "earlier broadcasters disposed", "repeat lectures with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "lecturebroadcaster <run|result>";
            bool on = ShortcutOn("lecturebroadcaster");
            bool fixedNow = on && LbReplace(true, true);
            bool controls = !LbReplace(false, true) && !LbReplace(true, false);
            return B582Line("lecturebroadcaster", on, "one lecture broadcaster per lecturer", fixedNow, "off / first lecture unchanged=" + controls, controls, missing, counters);
        }
    

        // ResidentSpawnWalkCheck run: an upper-level elevator in a public residential room is open to the resident (closed
        // to the selected Sim), and a walk that reaches outside accepts the slot. Controls: a private-room elevator stays
        // closed, a child / pet never rides, a household resident and a walk that stays inside keep EA's verdict.
        public static object ResidentSpawnWalkCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "residentspawnwalk: points accepted by the walk 1255=" + c[3648] + ", would accept with switch off 1256=" + c[3649] + ", errors 1257=" + c[3650] + "; " + GlMembers(EpJoin(sRsMissing, CkLive("residentspawnwalk")));
            if (what == "result") return counters;
            if (what != "run") return "residentspawnwalk <run|result>";
            bool on = ShortcutOn("residentspawnwalk");
            bool fixedNow = on && RsElevatorOpen(true, true, true) && RsWalkDecide(true, false, false, true);
            bool controls = !RsElevatorOpen(true, false, true) && !RsElevatorOpen(true, true, false) && !RsWalkDecide(true, false, true, true)
                && !RsWalkDecide(true, false, false, false) && !RsWalkDecide(false, false, false, true) && RsWalkDecide(false, true, false, false);
            return B582Line("residentspawnwalk", on, "resident's NPC-door slot judged by the room walk with its own rules", fixedNow,
                "private elevator / child / household / walk inside / switch off unchanged=" + controls, controls, sRsMissing, counters);
        }
    }
}
