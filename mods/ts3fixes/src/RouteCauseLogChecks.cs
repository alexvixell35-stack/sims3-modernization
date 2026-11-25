using System;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for RouteCauseLog.cs (docs/580). Pure line filter with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // RouteCauseLogCheck run: a locked-door failure (any Sim) and an NPC DisposeOutside failure get a line.
        //   Controls: a success, a household DisposeOutside failure, an NPC failure in another interaction -> no line.
        public static object RouteCauseLogCheck(string what)
        {
            string missing = (typeof(Sims3.Gameplay.Actors.SimRoutingComponent).GetField("LockedDoorsDuringPlan") == null ? "SimRoutingComponent.LockedDoorsDuringPlan" : "");
            int[] c = PatchCounters();
            string counters = "routecauselog: lines 2709=" + c[2709] + ", dropped 2710=" + c[2710] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "routecauselog <run|result>";
            bool on = ShortcutOn("routecauselog");
            bool fixedNow = RclWants(on, false, "UseToilet", RoutePlanResultType.FailedBecauseDestinationPortalLocked)
                && RclWants(on, true, "DisposeOutside", RoutePlanResultType.FailedBecauseNoPathToDestination);
            bool ctlOk = !RclWants(on, true, "DisposeOutside", RoutePlanResultType.Succeeded);
            bool ctlHh = !RclWants(on, false, "DisposeOutside", RoutePlanResultType.FailedBecauseNoPathToDestination);
            bool ctlOther = !RclWants(on, true, "Fridge_Have", RoutePlanResultType.FailedBecauseNoPathToDestination);
            bool controls = ctlOk && ctlHh && ctlOther;
            return "routecauselog check (switch " + (on ? "on" : "off") + "): locked-door and NPC zero-plan failures logged=" + fixedNow
                + "; control: success=" + ctlOk + ", household DisposeOutside=" + ctlHh + ", other NPC interaction=" + ctlOther
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
