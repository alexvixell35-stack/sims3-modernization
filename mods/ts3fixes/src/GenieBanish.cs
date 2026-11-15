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
    // geniebanish: found by the docs/559 rule scan. Off by default. Harness check: GenieBanishChecks.cs.
    // geniebanish (tuning kGenieBanish; docs/559 §4). OccultGenie+RemoveSim.Run picks the destination lot with
    // allCommunityLots[RandomUtil.GetInt(allCommunityLots.Count)]; RandomUtil.GetInt(max) is inclusive (min +
    // (max-min+1)*NextDouble) [verified], so 1 cast in Count+1 throws ArgumentOutOfRange after the liking hit and after
    // the target's queue was cancelled: no banish, no magic points used, the genie is reset. A world with no community
    // lot throws every time. Fix: the same Run with GetInt(Count - 1) and EA's own failure exit for an empty list.
    // Counters [2453] banishes run through the fixed copy, [2454] errors.
    public static partial class Fixes
    {
        static FieldInfo sGbCost, sGbLtr;

        static void ApplyGenieBanish()
        {
            sGbCost = typeof(OccultGenie.RemoveSim).GetField("kMagicPointCost", kStaticAll);
            sGbLtr = typeof(OccultGenie.RemoveSim).GetField("kLTRHitFromRemoving", kStaticAll);
            if (sGbCost == null || sGbLtr == null) AppliedLog().Add("geniebanish: RemoveSim tunables not found, not patched");
            else Patch(typeof(OccultGenie.RemoveSim), "Run", typeof(GbHook), "RunHook", "RunOrig");
        }

        public class GbHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("geniebanish")) return RunOrig();
                PatchCounters()[2453]++;
                return GbRun((object)this as OccultGenie.RemoveSim);
            }
            public bool RunOrig() { return false; } // receives OccultGenie+RemoveSim.Run's original body
        }

        // The pick both ways for a given NextDouble() r (check only): EA's GetInt(Count) is inclusive, so Count itself comes
        // up; the fixed GetInt(Count - 1) stays in range (-1 when empty).
        static int GbPickIndex(int count, double r, bool fixedPick)
        {
            int max = fixedPick ? count - 1 : count;
            if (fixedPick && max < 0) return -1;
            return (int)((double)(max + 1) * r);  // RandomUtil.GetInt(0, max) with NextDouble() = r
        }

        // EA's Run (OccultGenie+RemoveSim, docs/559 §4) with the in-range lot pick; everything else unchanged.
        static bool GbRun(OccultGenie.RemoveSim ia)
        {
            Sim actor = ia.Actor, target = ia.Target;
            ia.StandardEntry();
            ia.BeginCommodityUpdates();
            if (actor.SimDescription.ChildOrBelow) actor.PlaySoloAnimation("c_genie_removeSim_x", true, ProductVersion.EP6);
            else actor.PlaySoloAnimation("a_genie_removeSim_x", true, ProductVersion.EP6);
            ia.EndCommodityUpdates(true);
            Relationship relationship = Relationship.Get(actor, target, true);
            relationship.LTR.UpdateLiking((float)sGbLtr.GetValue(null));
            target.InteractionQueue.CancelAllInteractions();
            List<Lot> lots = LotManager.GetAllCommunityLots();
            if (lots == null || lots.Count == 0) { ia.StandardExit(); return false; }  // no community lot: EA's give-up exit
            int num = 0;
            Lot lot;
            do
            {
                lot = lots[RandomUtil.GetInt(lots.Count - 1)];  // EA: GetInt(lots.Count), inclusive
                if (num++ > lots.Count) { ia.StandardExit(); return false; }
            }
            while (lot.CommercialLotSubType == CommercialLotSubType.kEP6_BigShow);
            Vector3 pos = lot.GetCenterPosition();
            Vector3 fwd = new Vector3(target.ForwardVector);
            World.FindGoodLocationParams fglParams = new World.FindGoodLocationParams(pos);
            fglParams.BooleanConstraints = FindGoodLocationBooleans.Routable;
            fglParams.RequiredRoomID = 0;
            if (GlobalFunctions.FindGoodLocation(target, fglParams, out pos, out fwd))
            {
                Terrain.TeleportMeHere.Definition definition = new Terrain.TeleportMeHere.Definition(true, "_Genie");
                InteractionObjectPair iop = new InteractionObjectPair(definition, Terrain.Singleton);
                InteractionInstanceParameters parameters = new InteractionInstanceParameters(iop, target, new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), false, false);
                Terrain.TeleportMeHere teleport = ((InteractionDefinition)definition).CreateInstanceFromParameters(ref parameters) as Terrain.TeleportMeHere;
                teleport.SetAndReserveDestination(pos);
                target.InteractionQueue.Add(teleport);
                EventTracker.SendEvent(EventTypeId.kRemoveSim, actor, target);
            }
            ia.StandardExit();
            OccultGenie genie = actor.OccultManager.GetOccultType(OccultTypes.Genie) as OccultGenie;
            if (genie != null) genie.MagicPoints.UsePoints((int)sGbCost.GetValue(null));
            return true;
        }
    }
}
