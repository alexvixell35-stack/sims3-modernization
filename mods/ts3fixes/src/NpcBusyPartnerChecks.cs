using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Harness check for NpcBusyPartner.cs (docs/577 §4). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // NpcBusyPartnerCheck run: two NPCs, the starter's Started wait while the partner's linked interaction is only
        //   queued behind another action -> the wait ends at once. Controls: a household waiter, a household partner, the
        //   linked interaction already head / current, the partner already at the level, a wait for another level ->
        //   EA's wait unchanged.
        public static object NpcBusyPartnerCheck(string what)
        {
            string missing = ((typeof(Sim).GetMethod("OnWaitForSynchronizationLevelWithSimFailed") == null ? "Sim.OnWaitForSynchronizationLevelWithSimFailed " : "")
                + (typeof(InteractionQueue).GetMethod("HasInteraction", new Type[] { typeof(Sims3.Gameplay.Interactions.InteractionInstance) }) == null ? "InteractionQueue.HasInteraction " : "")
                + (sSwCorePatched ? "" : "WaitForSynchronizationLevelWithSim hook")).Trim();
            int[] c = PatchCounters();
            string counters = "npcbusypartner: waits ended 2696=" + c[2696] + ", seen with switch off 2697=" + c[2697] + ", errors 2698=" + c[2698] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "npcbusypartner <run|result>";
            bool on = ShortcutOn("npcbusypartner");
            bool fixedNow = NbpGiveUp(on, true, true, true, true, false, false);
            bool ctlHhWaiter = !NbpGiveUp(on, true, false, true, true, false, false);
            bool ctlHhPartner = !NbpGiveUp(on, true, true, false, true, false, false);
            bool ctlStarted = !NbpGiveUp(on, true, true, true, true, true, false);
            bool ctlAtLevel = !NbpGiveUp(on, true, true, true, true, false, true);
            bool ctlLevel = !NbpGiveUp(on, false, true, true, true, false, false);
            bool controls = ctlHhWaiter && ctlHhPartner && ctlStarted && ctlAtLevel && ctlLevel;
            return "npcbusypartner check (switch " + (on ? "on" : "off") + "): NPC wait on a queued partner ended=" + fixedNow
                + "; control: household waiter=" + ctlHhWaiter + ", household partner=" + ctlHhPartner + ", partner started=" + ctlStarted
                + ", partner at level=" + ctlAtLevel + ", other level=" + ctlLevel + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
