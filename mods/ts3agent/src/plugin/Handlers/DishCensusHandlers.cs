using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // dish_census {top?: 8, reset?: false} (docs/652): read only. One pass over every ServingContainer (plates, bowls, group
    // servings, pizza boxes, cakes) counted per lot: dirty (EA's autonomous cleanup test: !HasFood || IsSpoiled), spoiled,
    // fresh, held by a Sim, in use or routed to, on a buffet, in an inventory, waiting on EA's auto-destroy alarm, and how many
    // EA's nightly cleanup would throw away now (Lot.DoesObjectNeedCleaning). Each lot row says what EA's 3 AM cleanup does
    // with it (LotManager.CleanUpLotsAndInventories: skipped as the active Sim's home, a controllable venue or a lot with an
    // active-household job; else cleaned), its sinks and dishwashers (ServingContainer_CleanUp needs one) and the dish types.
    // Every call appends (sim time, world dirty, biggest lot) to a history kept between calls, so the harness can call it per
    // snapshot and read the growth (dishes per lot over time). reset clears the history.
    public static partial class Handlers
    {
        static void RegisterDishCensus()
        {
            Dispatcher.Register("dish_census", DishCensus);
        }

        static readonly List<object> sDcHistory = new List<object>();

        class DcRow { public Lot L; public int All, Dirty, Spoiled, Fresh, Held, Used, Buffet, Inv, Pending, Nightly; public Dictionary<string, int> Types = new Dictionary<string, int>(); }

        static object DishCensus(Args a)
        {
            RequireLive();
            if (a.Bool("reset", false)) sDcHistory.Clear();
            int top = (int)a.Long("top", 8);
            Dictionary<ulong, DcRow> rows = new Dictionary<ulong, DcRow>();
            System.Reflection.FieldInfo ad = typeof(ServingContainer).GetField("mAutoDestroyAlarm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            int total = 0, outOfWorld = 0, errors = 0;
            foreach (ServingContainer d in Sims3.Gameplay.Queries.GetObjects<ServingContainer>())
            {
                try
                {
                    if (d == null || d.HasBeenDestroyed) continue;
                    total++;
                    Lot l = d.LotCurrent;
                    if (l == null) { outOfWorld++; continue; }
                    DcRow r;
                    if (!rows.TryGetValue(l.LotId, out r)) { r = new DcRow { L = l }; rows[l.LotId] = r; }
                    r.All++;
                    bool dirty = !d.HasFood || d.IsSpoiled;
                    if (dirty) r.Dirty++; else r.Fresh++;
                    if (d.IsSpoiled) r.Spoiled++;
                    if (d.Parent is Sim) r.Held++;
                    if (d.InUse || (d.ActorsUsingMe != null && d.ActorsUsingMe.Count > 0) || (d.RoutingReferenceList != null && d.RoutingReferenceList.Count > 0)) r.Used++;
                    if (d.Parent is IBuffetTable) r.Buffet++;
                    if (d.InInventory) r.Inv++;
                    if (ad != null && !AlarmHandle.kInvalidHandle.Equals(ad.GetValue(d))) r.Pending++;
                    if (Lot.DoesObjectNeedCleaning(d)) r.Nightly++;
                    string tn = d.GetType().Name + (dirty ? " dirty" : "");
                    int n; r.Types.TryGetValue(tn, out n); r.Types[tn] = n + 1;
                }
                catch (Exception) { errors++; }
            }
            List<DcRow> list = new List<DcRow>(rows.Values);
            list.Sort(delegate(DcRow x, DcRow y) { return y.Dirty != x.Dirty ? y.Dirty.CompareTo(x.Dirty) : y.All.CompareTo(x.All); });
            int worldDirty = 0;
            foreach (DcRow r in list) worldDirty += r.Dirty;
            Sim active = Sim.ActiveActor;
            List<object> lots = new List<object>();
            for (int i = 0; i < list.Count && i < top; i++) lots.Add(DcLot(list[i], active));
            Dictionary<string, object> res = Util.Obj();
            res["sim_time"] = SimClock.CurrentTime().ToString();
            res["servings"] = total; res["out_of_world"] = outOfWorld; res["errors"] = errors;
            res["dirty"] = worldDirty; res["lots_with_dishes"] = list.Count;
            res["active_home"] = Household.ActiveHouseholdLot != null ? Household.ActiveHouseholdLot.Name : null;
            res["lots"] = lots;
            Dictionary<string, object> h = Util.Obj();
            h["sim_time"] = res["sim_time"]; h["dirty"] = worldDirty;
            h["biggest"] = list.Count > 0 ? (list[0].L.Name ?? "?") + " " + list[0].Dirty : null;
            sDcHistory.Add(h);
            if (sDcHistory.Count > 96) sDcHistory.RemoveAt(0);
            res["history"] = sDcHistory;
            return res;
        }

        static Dictionary<string, object> DcLot(DcRow r, Sim active)
        {
            Lot l = r.L;
            Dictionary<string, object> o = Util.Obj();
            o["lot"] = l.Name; o["lot_id"] = "0x" + l.LotId.ToString("X");
            o["kind"] = l.IsWorldLot ? "world" : l.IsCommunityLot ? "community " + l.CommercialLotSubType : "residential";
            o["household"] = l.Household != null ? l.Household.Name : null;
            o["active_home"] = l == Household.ActiveHouseholdLot;
            string nightly;
            try
            {
                // LotManager.CleanUpLotsAndInventories' test and Lot.MagicallyCleanUp's early return, as EA orders them
                if (!((!l.CanSimTreatAsHome(active) && !l.IsControllable) || l.IsBaseCampLotType)) nightly = l.HasVirtualResidentialSlots ? "cleaned (virtual slots)" : "skipped: active Sim's home or controllable venue";
                else nightly = Sims3.Gameplay.Careers.Occupation.DoesLotHaveAnyActiveJobs(l) ? "skipped: active-household job on the lot" : "cleaned";
            }
            catch (Exception e) { nightly = "error " + e.GetType().Name; }
            o["nightly_cleanup"] = nightly;
            int sinks = -1, dishwashers = -1;
            try { if (!l.IsWorldLot) { sinks = l.GetObjects<Sink>().Length; dishwashers = l.GetObjects<Dishwasher>().Length; } } catch (Exception) { }
            o["sinks"] = sinks; o["dishwashers"] = dishwashers;
            int sims = 0, hh = 0;
            foreach (Sim s in l.GetSims()) { sims++; if (s.IsInActiveHousehold) hh++; }
            o["sims"] = sims; o["household_sims"] = hh;
            o["servings"] = r.All; o["dirty"] = r.Dirty; o["spoiled"] = r.Spoiled; o["fresh"] = r.Fresh; o["held"] = r.Held;
            o["used_or_routed"] = r.Used; o["on_buffet"] = r.Buffet; o["in_inventory"] = r.Inv; o["auto_destroy_pending"] = r.Pending;
            o["nightly_would_clean"] = r.Nightly;
            Dictionary<string, object> t = Util.Obj();
            foreach (KeyValuePair<string, int> kv in r.Types) t[kv.Key] = kv.Value;
            o["types"] = t;
            return o;
        }
    }
}
