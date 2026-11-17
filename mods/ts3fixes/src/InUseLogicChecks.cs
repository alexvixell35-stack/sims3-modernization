using System;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Harness checks for InUseLogic.cs (docs/576 §2-3). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // ResetInUseFreeCheck run: an adult reset mid put-down keeps the item in use in its own inventory.
        //   Controls: a toddler (crib stuffed animal) and an item not in use are left alone.
        public static object ResetInUseFreeCheck(string what)
        {
            string missing = ((sIuActor == null ? "InteractionQueue.mActor " : "")
                + (typeof(InteractionQueue).GetMethod("OnReset") == null ? "InteractionQueue.OnReset" : "")).Trim();
            string counters = OuCounters("resetinusefree", 2689, "items freed after a reset", missing);
            if (what == "result") return counters;
            if (what != "run") return "resetinusefree <run|result>";
            bool on = ShortcutOn("resetinusefree");
            bool fixedNow = RiFree(on, false, true, 0);
            bool ctlToddler = !RiFree(on, true, true, 0), ctlFree = !RiFree(on, false, false, 0);
            bool controls = ctlToddler && ctlFree;
            return "resetinusefree check (switch " + (on ? "on" : "off") + "): item freed after the reset=" + fixedNow
                + "; control: toddler left alone=" + ctlToddler + ", item not in use untouched=" + ctlFree + "; controls ok=" + controls
                + "; verdict: " + CkVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // PutDownInUseCheck run: a put-down on a surface cut after the in-use mark (item still in the inventory, in use).
        //   Controls: a finished put-down (item left the inventory) and a ground put-down (never marked) are untouched.
        public static object PutDownInUseCheck(string what)
        {
            string missing = (typeof(PutDown).GetMethod("Cleanup") == null ? "PutDown.Cleanup " : "")
                + (sIuVoodooPutDown == null ? "VoodooDoll+PutDown" : "");
            missing = missing.Trim();
            string counters = OuCounters("putdowninuse", 2691, "put-downs repaired", missing);
            if (what == "result") return counters;
            if (what != "run") return "putdowninuse <run|result>";
            bool on = ShortcutOn("putdowninuse");
            bool fixedNow = PdRepair(on, true, true);
            bool ctlDone = !PdRepair(on, false, false), ctlGround = !PdRepair(on, true, false);
            bool controls = ctlDone && ctlGround;
            return "putdowninuse check (switch " + (on ? "on" : "off") + "): cut put-down repaired=" + fixedNow
                + "; control: finished put-down=" + ctlDone + ", ground put-down=" + ctlGround + "; controls ok=" + controls
                + "; verdict: " + CkVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
