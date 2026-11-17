using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Services;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // petfireplan (tuning kPetFirePlan, off by default; docs/546 §10). Exact speedup, every Sim.
    //
    // FireSource.ReactToFire.Run makes a route and calls PlanToNearestFire(route) for every actor, then switches on the
    // species: only DoHumanReactions takes the route. The dog, cat, horse, unicorn, deer and raccoon reactions use only the
    // object PlanToNearestFire returns, and the route is a local that is dropped when Run returns [verified in the
    // decompiled game code]. Its RoomReplan callback runs only while a route is followed, which no pet does with this one.
    // So for an animal the one or two route plans (PlanToPointRadialRange, then a retry ignoring the fire) are thrown away.
    // Measured (phase-20261114-1113, Appaloosa Plains): a fire on the household lot burned 18 sim hours and its broadcaster
    // re-pushed ReactToFire on the family's two pets ~40 times an hour; 1,358 of those plans failed, at 19-112 ms each.
    //
    // The hook answers for a non-human actor with the same nearest-object choice (closest Fire on the target's lot, else
    // the closest Sim on fire) and no plan. Humans, and an actor without a description, run EA's body unchanged.
    // Inferred, not proven: that the native planner keeps no state another plan reads (it has no managed RNG use).
    // Counter [2320] plans skipped, [2321] errors (registry fixes-2320). Harness check: PetFireCheck (EventChecks.cs).
    public static partial class Fixes
    {
        static void ApplyPetFirePlan()
        {
            Patch(typeof(FireSource.ReactToFire), "PlanToNearestFire", typeof(PetFirePlanHook), "PlanHook", "PlanOrig");
        }

        public class PetFirePlanHook
        {
            public IGameObject PlanHook(Route r)
            {
                IGameObject nearest;
                if (PetFireNearest((FireSource.ReactToFire)(object)this, out nearest)) return nearest;
                return PlanOrig(r);
            }
            public IGameObject PlanOrig(Route r) { return null; } // receives FireSource.ReactToFire.PlanToNearestFire's original body
        }

        // True when the actor is an animal and nearest holds EA's choice without the plan EA would throw away.
        static bool PetFireNearest(FireSource.ReactToFire rtf, out IGameObject nearest)
        {
            nearest = null;
            if (!ShortcutOn("petfireplan")) return false;
            try
            {
                Sim actor = rtf.Actor;
                if (actor == null || actor.SimDescription == null || actor.SimDescription.Species == CASAgeGenderFlags.Human
                    || rtf.Target == null || rtf.Target.LotCurrent == null) return false;
                // the same two lookups as EA's PlanToNearestFire, in the same order
                List<Fire> fires = new List<Fire>(rtf.Target.LotCurrent.GetObjects<Fire>());
                nearest = GlobalFunctions.GetClosestObject(fires, actor);
                if (nearest == null) nearest = GlobalFunctions.GetClosestObject(rtf.Target.LotCurrent.GetSims(FirefighterSituation.IsSimOnFire), actor);
                PatchCounters()[2320]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2321]++; nearest = null; return false; }
        }
    }
}
