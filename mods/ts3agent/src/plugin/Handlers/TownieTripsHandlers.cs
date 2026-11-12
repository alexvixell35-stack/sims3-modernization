using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // townie_trips {max?: 40, cancel?: false, seed?} (docs/544 §5 leak soaks): every instantiated NPC townie queues the game's
    // own VisitCommunityLot to a random community lot other than the one it stands on, user-directed priority, so many car,
    // taxi, bike and boat get-ins happen in a short run (the vehicle leak paths L1-L3 fire on get-in and arrival).
    // Never touched: household Sims, selectable Sims, Sims of unknown scope, role Sims (AssignedRole), service Sims
    // (CreatedByService), Sims already in a vehicle and Sims whose queue already holds a lot trip.
    public static partial class Handlers
    {
        static readonly Random sTtRand = new Random(12345);

        static void RegisterTownieTrips()
        {
            Dispatcher.Register("townie_trips", TownieTrips);
        }

        static object TownieTrips(Args a)
        {
            RequireLive();
            int max = (int)a.Long("max", 40);
            bool cancel = a.Bool("cancel", false);
            List<Lot> lots = new List<Lot>();
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l != null && l.IsCommunityLot && !l.IsWorldLot) lots.Add(l);
            }
            if (lots.Count < 2) throw new McpException("fewer than 2 community lots");
            InteractionPriority pri = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            int queued = 0, skipped = 0, refused = 0, considered = 0;
            Dictionary<string, int> skip = new Dictionary<string, int>();
            List<object> sent = new List<object>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (queued >= max) break;
                string why = null;
                try
                {
                    considered++;
                    SimDescription d = s == null ? null : s.SimDescription;
                    if (s == null || s.HasBeenDestroyed || d == null) why = "unknown";
                    else if (s.IsSelectable || d.Household == Household.ActiveHousehold || d.Household == null) why = "household or unknown";
                    else if (d.AssignedRole != null) why = "role";
                    else if (d.CreatedByService != null) why = "service";
                    else if (!d.IsHuman || d.ChildOrBelow) why = "not a teen+ human";
                    else if (s.Posture != null && s.Posture.GetType().Name.Contains("Vehicle")) why = "in a vehicle";
                    else if (s.InteractionQueue == null) why = "no queue";
                    else
                    {
                        foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                            if (ii is VisitCommunityLot || ii is VisitLot) { why = "already on a trip"; break; }
                    }
                }
                catch (Exception) { why = "unknown"; }
                if (why != null) { skipped++; int n; skip.TryGetValue(why, out n); skip[why] = n + 1; continue; }
                try
                {
                    Lot here = s.LotCurrent, to = null;
                    for (int i = 0; i < 8 && (to == null || to == here); i++) to = lots[sTtRand.Next(lots.Count)];
                    if (cancel) s.InteractionQueue.CancelAllInteractions();
                    if (s.InteractionQueue.Add(VisitCommunityLot.Singleton.CreateInstance(to, s, pri, false, true)))
                    {
                        queued++;
                        if (sent.Count < 10) sent.Add(s.FullName + " -> " + to.Name);
                    }
                    else refused++;
                }
                catch (Exception) { refused++; }
            }
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = queued; r["refused"] = refused; r["skipped"] = skipped; r["considered"] = considered;
            Dictionary<string, object> sk = Util.Obj();
            foreach (KeyValuePair<string, int> kv in skip) sk[kv.Key] = kv.Value;
            r["skip_reasons"] = sk; r["community_lots"] = lots.Count; r["samples"] = sent;
            return r;
        }
    }
}
