using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // elevatorlightrig (tuning kElevatorLightRig, default off; docs/658 §7.1). [verified, decompiled ElevatorDoors /
    // ElevatorInterior] ElevatorDoors.OnEnterElevator sets World.ObjectSetCustomLightRig(sim, "CAS") when a rider is
    // registered; only OnExitElevator sets it back to null. EA's exit paths without OnExitElevator: the rider's portal
    // action reset (ElevatorPortalComponent.OnPortalActionReset, the doors forward theirs to it), the route ending while
    // still in the elevator (ElevatorOnRouteActionsFinished, which resets the Sim), the elevator's own OnReset
    // (mAssignedSims cleared), and our owelevidle's drop of an idle entry (that hook calls ElrAfter only once owelevidle,
    // docs/655, is in the tree; not in this branch). [inferred] Nothing in script clears the rig on a
    // Sim reset, so such a Sim can walk around lit by the CAS rig. Fix: on those paths, after EA's code, EA's own
    // World.ObjectSetCustomLightRig(sim, null), the same call OnExitElevator makes, for a valid Sim that was riding and is
    // not registered with the elevator any more. Applies to every Sim (a pure EA bug fix; no visible change except losing
    // the wrong lighting). Rule: ElevatorLightRigCore. Counters [4215] rigs cleared, [4216] seen with the switch off,
    // [4217] errors. Log "elevatorlightrig: ..." capped.
    public static partial class Fixes
    {
        static readonly LineCapCore sElrCap = new LineCapCore(10, 60000, 100);

        static void ApplyElevatorLightRig()
        {
            Type pc = typeof(ElevatorInterior.ElevatorPortalComponent);
            Patch(pc, "OnPortalActionReset", new Type[] { typeof(Sim) }, typeof(ElrHook), "ActionResetHook", "ActionResetOrig");
            Patch(pc, "ElevatorOnRouteActionsFinished", new Type[] { typeof(GameObject), typeof(bool) }, typeof(ElrHook), "RouteDoneHook", "RouteDoneOrig");
            Patch(pc, "OnReset", Type.EmptyTypes, typeof(ElrHook), "ResetHook", "ResetOrig");
        }

        static bool ElrValid(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && Sims3.SimIFace.Objects.IsValid(s.ObjectId);
        }

        // after EA's exit path: wasRiding taken before it ran
        internal static void ElrAfter(ElevatorInterior.ElevatorPortalComponent pc, Sim s, bool wasRiding, string where)
        {
            int[] c = PatchCounters();
            try
            {
                bool still = pc != null && s != null && pc.IsSimInElevator(s);
                int d = ElevatorLightRigCore.Decide(ShortcutOn("elevatorlightrig"), ElrValid(s), wasRiding, still);
                if (d == ElevatorLightRigCore.kNone) return;
                if (d == ElevatorLightRigCore.kSeenOff) { c[4216]++; return; }
                World.ObjectSetCustomLightRig(s.ObjectId, null);
                c[4215]++;
                if (sElrCap.Take(System.Environment.TickCount))
                    DiagLine("elevatorlightrig: " + s.FullName + " left the elevator by " + where + " without OnExitElevator: CAS light rig cleared");
            }
            catch (Exception ex) { c[4217]++; if (sElrCap.Take(System.Environment.TickCount)) DiagLine("elevatorlightrig: error " + ex.GetType().Name); }
        }

        static bool ElrRiding(ElevatorInterior.ElevatorPortalComponent pc, Sim s)
        {
            try { return pc != null && s != null && (pc.IsSimInElevator(s) || ElevatorInterior.IsSimInsideElevator(s)); }
            catch (Exception) { PatchCounters()[4217]++; return false; }
        }

        // Repro elevatorlightrig-check (tools/harness/repros.json): 'run' evaluates the rule with controls and counts the
        // elevators in the world and their registered riders (read-only); 'result' shows the counters.
        public static object ElevatorLightRigCheck(string what)
        {
            string counters = B582Counters("elevatorlightrig", 4215, new[] { "rigs cleared", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "elevatorlightrig <run|result>";
            bool on = ShortcutOn("elevatorlightrig");
            bool fixedNow = ElevatorLightRigCore.Decide(on, true, true, false) == ElevatorLightRigCore.kClear;
            bool ctl = ElevatorLightRigCore.Decide(on, true, true, true) == ElevatorLightRigCore.kNone
                && ElevatorLightRigCore.Decide(on, true, false, false) == ElevatorLightRigCore.kNone
                && ElevatorLightRigCore.Decide(on, false, true, false) == ElevatorLightRigCore.kNone;
            int elevators = 0, riders = 0;
            try
            {
                foreach (ElevatorDoors d in Sims3.Gameplay.Queries.GetObjects<ElevatorDoors>())
                {
                    elevators++;
                    ElevatorInterior ei = d.InteriorObj;
                    IDictionary m = ei == null || ei.ElevatorPortal == null || sErAssigned == null ? null : sErAssigned.GetValue(ei.ElevatorPortal) as IDictionary;
                    if (m != null) riders += m.Count;
                }
            }
            catch (Exception) { PatchCounters()[4217]++; }
            return B582Line("elevatorlightrig", on, "rider reset out of an elevator gets the CAS light rig cleared", fixedNow,
                "still riding / never rode / no Sim untouched=" + ctl, ctl, "", counters) + " | elevators " + elevators + ", registered riders " + riders;
        }

        public class ElrHook
        {
            public void ActionResetHook(Sim sim)
            {
                ElevatorInterior.ElevatorPortalComponent pc = (ElevatorInterior.ElevatorPortalComponent)(object)this;
                bool was = ElrRiding(pc, sim);
                ActionResetOrig(sim);
                ElrAfter(pc, sim, was, "a portal action reset");
            }
            public void ActionResetOrig(Sim sim) { } // receives ElevatorPortalComponent.OnPortalActionReset

            public void RouteDoneHook(GameObject routingObject, bool routeSuccess)
            {
                ElevatorInterior.ElevatorPortalComponent pc = (ElevatorInterior.ElevatorPortalComponent)(object)this;
                Sim s = routingObject as Sim;
                bool was = ElrRiding(pc, s);
                RouteDoneOrig(routingObject, routeSuccess);
                ElrAfter(pc, s, was, "a route that ended inside it");
            }
            public void RouteDoneOrig(GameObject routingObject, bool routeSuccess) { } // receives ElevatorOnRouteActionsFinished

            public void ResetHook()
            {
                ElevatorInterior.ElevatorPortalComponent pc = (ElevatorInterior.ElevatorPortalComponent)(object)this;
                List<Sim> riders = null;
                try
                {
                    IDictionary d = sErAssigned == null ? null : sErAssigned.GetValue(pc) as IDictionary;
                    if (d != null && d.Count > 0)
                    {
                        riders = new List<Sim>();
                        foreach (DictionaryEntry e in d)
                        {
                            SimDescription sd = e.Key as SimDescription;
                            if (sd != null && sd.CreatedSim != null) riders.Add(sd.CreatedSim);
                        }
                    }
                }
                catch (Exception) { PatchCounters()[4217]++; }
                ResetOrig();
                if (riders != null) foreach (Sim s in riders) ElrAfter(pc, s, true, "the elevator's reset");
            }
            public void ResetOrig() { } // receives ElevatorPortalComponent.OnReset (after elevatorresetriders' hook when both)
        }
    }
}
