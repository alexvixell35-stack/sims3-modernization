using System;

namespace Ts3Fixes
{
    // Harness checks for ObjectUseLogic.cs (docs/575 §2-4). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // HandsetResetCheck run: a wall phone whose call was cut by a reset (ReplaceHandset with no state machine).
        //   Controls: a normal hang-up (state machine present, the exit animation shows the handset) and a smartphone are
        //   left to EA; at load a landline in use or with the handset flag set is left alone, an idle one is put back.
        public static object HandsetResetCheck(string what)
        {
            string missing = ((sOuSetState == null ? "Phone.SetHandsetState " : "") + (sOuReplace == null ? "Phone.ReplaceHandset " : "")
                + (sOuHandset == null ? "Phone.HandsetPickedUp" : "")).Trim();
            string counters = PhCounters("handsetreset", 2679, "handsets put back", missing);
            if (what == "result") return counters;
            if (what != "run") return "handsetreset <run|result>";
            bool on = ShortcutOn("handsetreset");
            bool fixedNow = HrPutBack(on, true, true, false, 0, false) && HrPutBack(on, true, false, false, 0, true);
            bool ctlNormal = !HrPutBack(on, true, false, false, 0, false);
            bool ctlSmart = !HrPutBack(on, false, true, false, 0, false);
            bool ctlInUse = !HrPutBack(on, true, false, false, 1, true) && !HrPutBack(on, true, false, true, 0, true);
            bool controls = ctlNormal && ctlSmart && ctlInUse;
            return "handsetreset check (switch " + (on ? "on" : "off") + "): handset back after a cut call and on an idle landline at load=" + fixedNow
                + "; control: normal hang-up left to EA=" + ctlNormal + ", smartphone=" + ctlSmart + ", landline in use at load=" + ctlInUse
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // FlowersInUseCheck run: a rejected Give Flowers whose bouquet is still in use in the giver's inventory.
        //   Controls: an accepted one (bouquet already gone from the giver) and, at load, a Sim with a Give Flowers
        //   queued or an interaction on the bouquet are left alone.
        public static object FlowersInUseCheck(string what)
        {
            string missing = (sOuSocialCleanup == null ? "SocialInteractionA.Cleanup " : "")
                + (typeof(Sims3.Gameplay.Actors.Sim.GiveFlowers).GetField("FlowerObject") == null ? "Sim.GiveFlowers.FlowerObject" : "");
            missing = missing.Trim();
            string counters = PhCounters("flowersinuse", 2681, "bouquets freed", missing);
            if (what == "result") return counters;
            if (what != "run") return "flowersinuse <run|result>";
            bool on = ShortcutOn("flowersinuse");
            bool fixedNow = FiFree(on, true, false, true, false) && FiFree(on, true, true, false, false);
            bool ctlAccepted = !FiFree(on, false, false, true, false);
            bool ctlQueued = !FiFree(on, true, true, true, false) && !FiFree(on, true, true, false, true);
            bool controls = ctlAccepted && ctlQueued;
            return "flowersinuse check (switch " + (on ? "on" : "off") + "): rejected bouquet freed at cleanup and at load=" + fixedNow
                + "; control: accepted bouquet untouched=" + ctlAccepted + ", Give Flowers or interaction on it at load=" + ctlQueued
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // UseListPruneCheck run: a use list holding a destroyed Sim, one with no proxy, and a null entry.
        //   Controls: a live Sim with a proxy stays.
        public static object UseListPruneCheck(string what)
        {
            string missing = (typeof(Sims3.Gameplay.Abstracts.GameObject).GetProperty("ActorsUsingMe") == null ? "GameObject.ActorsUsingMe " : "")
                + (typeof(Sims3.Gameplay.Abstracts.GameObject).GetProperty("ReferenceList") == null ? "GameObject.ReferenceList " : "")
                + (typeof(Sims3.Gameplay.Abstracts.GameObject).GetMethod("ValidateAllReferenceListsAndUseLists") == null ? "GameObject.ValidateAllReferenceListsAndUseLists" : "");
            missing = missing.Trim();
            string counters = PhCounters("uselistprune", 2683, "use/reference entries dropped", missing);
            if (what == "result") return counters;
            if (what != "run") return "uselistprune <run|result>";
            bool on = ShortcutOn("uselistprune");
            bool fixedNow = UlDrop(on, false, true, false) && UlDrop(on, false, false, true) && UlDrop(on, true, false, false);
            bool ctlLive = !UlDrop(on, false, false, false);
            return "uselistprune check (switch " + (on ? "on" : "off") + "): destroyed, proxy-less and null entries dropped=" + fixedNow
                + "; control: live Sim kept=" + ctlLive + "; controls ok=" + ctlLive
                + "; verdict: " + GlVerdict(on, fixedNow, ctlLive, missing.Length == 0) + "; " + counters;
        }
    
        // ChestUseListCheck run: a treasure-chest open cut by a reset leaves the Sim on the chest's use list.
        //   Control: after a normal open (StandardExit already took the Sim off) nothing changes.
        public static object ChestUseListCheck(string what)
        {
            string missing = (sCuType == null ? "TreasureChest+OpenWithAnimation" : sCuCleanup == null ? "TreasureChest+OpenWithAnimation.Cleanup" : "");
            string counters = PhCounters("chestuselist", 2685, "Sims taken off a chest's use list", missing);
            if (what == "result") return counters;
            if (what != "run") return "chestuselist <run|result>";
            bool on = ShortcutOn("chestuselist");
            bool fixedNow = CuRemove(on, true);
            bool ctlNormal = !CuRemove(on, false);
            return "chestuselist check (switch " + (on ? "on" : "off") + "): Sim taken off after a cut open=" + fixedNow
                + "; control: normal open unchanged=" + ctlNormal + "; controls ok=" + ctlNormal
                + "; verdict: " + GlVerdict(on, fixedNow, ctlNormal, missing.Length == 0) + "; " + counters;
        }
    }
}
