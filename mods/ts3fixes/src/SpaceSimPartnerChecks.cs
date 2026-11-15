using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // spacesimpartner harness checks (docs/559 §5, §10). // Harness checks for this family: throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // SpaceSimPartnerCheck ride: IN GAME, shows the RideOver timing (§10). The selected Sim rides a Space Simulator on the
        //   active lot with another instantiated Sim on that lot as partner (EA's RideWith, Medium, pushed user-directed;
        //   costs EA's ride fee off the owner's lot). Wait for the ride to end (~kTimeRideLasts sim-minutes), then 'result':
        //   off BROKEN = 2467 +1 (partner finished at the ride's start); on FIXED = 2468 +1 (partner rode to the end).
        // SpaceSimPartnerCheck run: a throwaway simulator (uninitialized, never placed) in the state RideWith leaves after
        //   a cancelled wait (rider in, ride not started), rider = partner placeholder objects (no queue touched).
        //   off BROKEN = the release step is skipped (partner waits); on FIXED = it runs (decision only; nothing cancelled).
        public static object SpaceSimPartnerCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "spacesimpartner: released 2455=" + c[2455] + ", stranded seen 2456=" + c[2456] + ", errors 2457=" + c[2457];
            counters += ", ride ends: partner already gone 2467=" + c[2467] + ", partner still riding 2468=" + c[2468];
            if (what == "result") return counters;
            if (what == "ride") return SsPushRide();
            if (what != "run") return "spacesimpartner <run|ride|result>";
            bool on = ShortcutOn("spacesimpartner");
            Sim rider = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Sim partner = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            SpaceSimulator ss = (SpaceSimulator)FormatterServices.GetUninitializedObject(typeof(SpaceSimulator));
            ss.SimA = rider;
            ss.SimB = partner;
            ss.SimAInRide = true;
            ss.RideOver = false;
            ss.SimAFailedToRoute = false;
            bool stranded = SsPartnerStranded(ss, rider);
            ss.RideOver = true;
            bool afterRide = SsPartnerStranded(ss, rider);  // a ride that ran must never release
            string verdict = !stranded || afterRide ? "UNEXPECTED" : on ? "FIXED" : "BROKEN";
            return "spacesimpartner check (switch " + (on ? "on" : "off") + "): stranded partner detected=" + stranded + ", after a ride=" + afterRide + ", release " + (on ? "runs" : "skipped") + "; verdict: " + verdict + "; " + counters;
        }

        static string SsPushRide()
        {
            Sim a = Sims3.Gameplay.Actors.Sim.ActiveActor;
            if (a == null || a.LotCurrent == null) return "spacesimpartner ride: no selected Sim on a lot (N/A)";
            SpaceSimulator ss = null;
            foreach (SpaceSimulator x in a.LotCurrent.GetObjects<SpaceSimulator>()) { if (x != null && !x.InUse) { ss = x; break; } }
            if (ss == null) return "spacesimpartner ride: no free Space Simulator on the selected Sim's lot (N/A)";
            Sim b = null;
            foreach (Sim s in a.LotCurrent.GetAllActors()) { if (s != null && !ReferenceEquals(s, a) && s.SimDescription != null && s.SimDescription.ChildOrAbove && !s.IsPet) { b = s; break; } }
            if (b == null) return "spacesimpartner ride: no second Sim on the lot (N/A)";
            Type defType = typeof(SpaceSimulator.RideWith).GetNestedType("RideWithDefinition", BindingFlags.NonPublic | BindingFlags.Public);
            if (defType == null) return "spacesimpartner ride: RideWithDefinition not found";
            SpaceSimulator.RideItems item = new SpaceSimulator.RideItems(SpaceSimulator.RideType.Medium, 10, "Medium");
            InteractionDefinition def = (InteractionDefinition)Activator.CreateInstance(defType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { item }, null);
            InteractionObjectPair iop = new InteractionObjectPair(def, ss);
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, a, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            InteractionInstance inst = def.CreateInstanceFromParameters(ref p);
            List<object> sel = new List<object>();
            sel.Add(b);
            inst.SelectedObjects = sel;
            bool added = a.InteractionQueue.Add(inst);
            return "spacesimpartner ride: " + a.FullName + " rides with " + b.FullName + " (queued=" + added + ", switch " + (ShortcutOn("spacesimpartner") ? "on" : "off") + "); read 'result' after the ride";
        }
    }
}
