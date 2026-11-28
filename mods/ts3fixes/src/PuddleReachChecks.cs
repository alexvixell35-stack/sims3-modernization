using System;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // Harness check for PuddleReach.cs (docs/578 §3). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // PuddleReachCheck run: a Sim whose splash Run just failed on the same lot and puddle set -> Test says no.
        //   Controls: a new puddle (count changed), another lot, memo older than 30 sim-min, no memo, puddles gone,
        //   a user-directed test -> EA's Test unchanged.
        public static object PuddleReachCheck(string what)
        {
            string missing = (sPrDef == null ? "Sim+AutonomousSplashInPuddle+Definition " : "")
                + (typeof(Sim).GetNestedType("AutonomousSplashInPuddle", System.Reflection.BindingFlags.NonPublic) == null ? "Sim+AutonomousSplashInPuddle" : "");
            missing = missing.Trim();
            int[] c = PatchCounters();
            string counters = "puddlereach: tests withheld 2706=" + c[3979] + ", seen with switch off 2707=" + c[3980] + ", errors 2708=" + c[3981]
                + ", memos " + sPrMemo.Count + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "puddlereach <run|result>";
            bool on = ShortcutOn("puddlereach");
            bool fixedNow = PrSkip(on, true, true, true, 9, 9, 5f);
            bool ctlNew = !PrSkip(on, true, true, true, 10, 9, 5f);
            bool ctlLot = !PrSkip(on, true, true, false, 9, 9, 5f);
            bool ctlOld = !PrSkip(on, true, true, true, 9, 9, 30f);
            bool ctlNone = !PrSkip(on, true, false, true, 9, 9, 5f);
            bool ctlDry = !PrSkip(on, true, true, true, 0, 0, 5f);
            bool ctlUser = !PrSkip(on, false, true, true, 9, 9, 5f);
            bool controls = ctlNew && ctlLot && ctlOld && ctlNone && ctlDry && ctlUser;
            return "puddlereach check (switch " + (on ? "on" : "off") + "): splash withheld after a failed run on the same puddles=" + fixedNow
                + "; control: new puddle=" + ctlNew + ", other lot=" + ctlLot + ", memo expired=" + ctlOld + ", no memo=" + ctlNone
                + ", puddles gone=" + ctlDry + ", not autonomous=" + ctlUser + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
