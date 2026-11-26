using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Harness check for PetPutDownWait.cs (docs/578 §2). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // PetPutDownWaitCheck run: a pet's Started wait in PutDownPet whose carrier dropped the linked PutDownPet -> ends.
        //   Controls: the carrier's PutDownPet still running or queued, the carrier already syncing with the pet, a wait
        //   for another level, the carrier's own (human) side -> EA's wait unchanged.
        public static object PetPutDownWaitCheck(string what)
        {
            string missing = ((typeof(Sim).GetNestedType("PutDownPet") == null ? "Sim+PutDownPet " : "")
                + (typeof(InteractionQueue).GetMethod("IsRunning", new Type[] { typeof(Sims3.Gameplay.Interactions.InteractionInstance), typeof(bool) }) == null ? "InteractionQueue.IsRunning " : "")
                + (sSwCorePatched ? "" : "WaitForSynchronizationLevelWithSim hook")).Trim();
            int[] c = PatchCounters();
            string counters = "petputdownwait: waits ended 2703=" + c[3976] + ", seen with switch off 2704=" + c[3977] + ", errors 2705=" + c[3978] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "petputdownwait <run|result>";
            bool on = ShortcutOn("petputdownwait");
            bool fixedNow = PpwEnd(on, true, true, false, false);
            bool ctlAlive = !PpwEnd(on, true, true, true, false);
            bool ctlSyncing = !PpwEnd(on, true, true, false, true);
            bool ctlLevel = !PpwEnd(on, false, true, false, false);
            bool ctlHuman = !PpwEnd(on, true, false, false, false);
            bool controls = ctlAlive && ctlSyncing && ctlLevel && ctlHuman;
            return "petputdownwait check (switch " + (on ? "on" : "off") + "): pet wait on a carrier that gave up ended=" + fixedNow
                + "; control: carrier still putting down=" + ctlAlive + ", carrier syncing=" + ctlSyncing + ", other level=" + ctlLevel
                + ", carrier side=" + ctlHuman + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
