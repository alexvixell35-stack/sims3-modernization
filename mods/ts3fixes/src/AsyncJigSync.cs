using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // asyncjigsync (docs/240, v0.2.4 player report docs/226): paired socials (Show Off Badges, hugs, dances, 45 EA files)
    // create a SocialJig out of the world, hand it to the partner's linked interaction, then plan (PlanToPoint, PlaceJig's
    // PlanToPointRadius) and only after that place the jig and AddToWorld. Vanilla plans synchronously, so the partner never
    // runs in between. With [RouteAsync] the initiator's task sleeps in AsyncWait, the partner plans RouteToJigB to the
    // unplaced jig (goal -20000) and fails: "can't go there", social cancelled. While the task's Sim's current interaction or
    // its linked interaction holds a SocialJig with InWorld false, its plans stay synchronous (vanilla). Switch "asyncjigsync"
    // (default on: restores vanilla order inside the shipped RouteAsync). Counters in RouteBackoff.cs AsyncEligible.
    public static partial class Fixes
    {
        static readonly Predicate<object> sJigUnplaced = delegate(object j) { return !((SocialJig)j).InWorld; };

        // 0 = no unplaced jig (or switch off), 1 = in the Sim's current interaction, 2 = only in its linked interaction
        static int JigPending(Sim sim)
        {
            if (sim == null || !ShortcutOn("asyncjigsync")) return 0;
            try
            {
                InteractionInstance ia = sim.CurrentInteraction;
                if (ia == null) return 0;
                if (AsyncJigSyncCore.Holds(ia, typeof(SocialJig), sJigUnplaced)) return 1;
                return AsyncJigSyncCore.Holds(ia.LinkedInteractionInstance, typeof(SocialJig), sJigUnplaced) ? 2 : 0;
            }
            catch (Exception) { return 0; } // never blocks a plan; the async path stays as before
        }
    }
}
