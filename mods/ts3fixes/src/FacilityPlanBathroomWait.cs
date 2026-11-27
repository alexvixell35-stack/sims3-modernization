using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bathroomwait's decisions when the facility planner is on (docs/686 §36.7; BathroomWait.cs calls these with
    // facilityplan + facilityplanhh on): when a Sim needs to wait, the order of the waiters, and where to send one.
    public static partial class Fixes
    {
        // The bladder reaches EA's urgent level within facilitychain's horizon (2 sim-hours).
        static bool FpBwSoon(Sim s)
        {
            float acc, v;
            return FpNeed(s, FacilityPlanCore.kToilet, out acc, out v) <= FacilityPlanCore.kChainSoonMinutes;
        }

        static int BwByBladder(Sim a, Sim b)
        {
            float va = a == null || a.Motives == null ? 100f : a.Motives.GetValue(CommodityKind.Bladder);
            float vb = b == null || b.Motives == null ? 100f : b.Motives.GetValue(CommodityKind.Bladder);
            return va.CompareTo(vb);
        }

        // The object with the best ETA for this Sim's bladder (toilets; showers too with showerpeeplan while its Hygiene
        // is below 70), returned only when that best one is free now: else null and the Sim keeps waiting for it.
        static GameObject FpBwPick(Sim s, Lot home)
        {
            GameObject best = null; float bestEta = FacilityPlanCore.kInfinite; bool bestFree = false;
            bool shower = ShortcutOn("showerpeeplan") && ShlScope(s, ShortcutOn("showerpeeplanhh")) && ShpSimOk(s)
                && s.Motives.GetValue(CommodityKind.Hygiene) < FacilityPlanCore.kQuickHygiene;
            for (int pass = 0; pass < (shower ? 2 : 1); pass++)
            {
                int kind = pass == 0 ? FacilityPlanCore.kToilet : FacilityPlanCore.kShower;
                GameObject[] objs = FpObjects(home, kind);
                if (objs == null) continue;
                foreach (GameObject o in objs)
                {
                    if (o == null || o.HasBeenDestroyed || !o.InWorld || (o.Repairable != null && o.Repairable.Broken)) continue;
                    bool join; Sim user;
                    float busy = FpBusy(o, s, kind, out join, out user);
                    bool free = !o.InUse && busy <= 0f && !home.RoomLockedByPrivacy(s, o.RoomId);
                    if (!free && busy <= 0f) busy = FpMean(kind) * 0.5f;  // in use or locked but not recorded
                    float eta = FacilityPlanCore.Eta(FpTravel(s, o), busy, 0f);
                    if (eta < bestEta) { bestEta = eta; best = o; bestFree = free; }
                }
            }
            return bestFree ? best : null;
        }

        // A shower that fills the bladder too (showerpeeplan): EA's Take Shower at the same priority bathroomwait uses.
        static InteractionInstance FpBwShower(GameObject o, Sim s, out string use)
        {
            use = "Shower.TakeShower (showerpeeplan)";
            InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionInstance ii = Shower.TakeShower.Singleton.CreateInstance(o, s, pr, true, true);
            if (ii == null || !ii.Test()) { use += " test failed"; return null; }
            return ii;
        }
    }
}
