using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // vehicle_census {samples?: 10, list?: false} (docs/544 §5): read only. One pass over every Vehicle object (in the world,
    // in inventories and out of the world), counted by kind, where it is, who drives it and what holds it; orphans (not held,
    // no live driver or passenger, unused, not in an inventory, not parked on a residential lot) per kind with samples and
    // their age (first sim time this command saw them as orphans, kept between calls); parking-space and mooring-post
    // reservation states per lot type; the docs/544 switches and their counters (ts3fixes VehicleLeaks.cs, slots 2200-2216).
    public static partial class Handlers
    {
        static void RegisterVehicleCensus()
        {
            Dispatcher.Register("vehicle_census", VehicleCensus);
        }

        static readonly Dictionary<ulong, long> sVcFirstOrphan = new Dictionary<ulong, long>();  // vehicle id -> sim ticks first seen as orphan
        static readonly Dictionary<Type, List<FieldInfo>> sVcSitFields = new Dictionary<Type, List<FieldInfo>>();

        static string VcKind(Vehicle v)
        {
            if (v is CarpoolWithDriver) return v is CarLimo || v.GetType().Name == "HoverCarLimo" ? "limo" : "carpool";
            if (v is LightweightTaxi || v is CarNpcWithNoDriver) return "traffic";
            if (v is IceCreamTruck) return "ice_cream";
            if (v is FoodTruckBase) return "food_truck";
            if (v is CarService) return "service";
            if (v is CarMovingVanWithDriver) return "moving_van";
            if (v is CarNpcWithNpcDriver) return "taxi";
            if (v is CarUFO) return "ufo";
            CarOwnable co = v as CarOwnable;
            if (co != null) return co.GeneratedOwnableForNpc ? "npc_car" : "player_car";
            if (v is BoatTaxi) return "taxi_boat";
            BoatOwnable bo = v as BoatOwnable;
            if (bo != null) return bo.GeneratedOwnableForNpc ? "npc_boat" : "player_boat";
            if (v is AdultTaxiBicycle || v is ChildTaxiBicycle) return "taxi_bike";
            if (v is MagicBroom) return "broom";
            if (v is Bicycle) return "bike";
            return "other";
        }

        static bool VcHh(Sim s)
        {
            try { return s == null || s.SimDescription == null || s.IsSelectable || s.Household == Household.ActiveHousehold; }
            catch (Exception) { return true; }
        }

        static string VcWhere(Vehicle v)
        {
            if (v.ItemComp != null && v.ItemComp.InventoryParent != null)
            {
                Sim owner = v.ItemComp.InventoryParent.Owner as Sim;
                if (owner == null) return "inventory_other";
                return VcHh(owner) ? "inventory_household" : "inventory_npc";
            }
            IGameObject parent = v.Parent;
            if (parent is ParkingSpace || parent is MooringPost || parent is BikeRack || parent is BroomStand)
            {
                Lot lot = parent.LotCurrent;
                return lot != null && lot.IsResidentialLot ? "parked_residential" : "parked_community";
            }
            if (parent != null) return "in_world_parented";
            return v.InWorld ? "in_world_loose" : "out_of_world";
        }

        static string VcDriver(Vehicle v)
        {
            Sim d = v.Driver;
            if (d == null) return "none";
            if (d.HasBeenDestroyed) return "destroyed";
            CarNpcManager m = CarNpcManager.Singleton;
            if (m != null && m.NpcDriversManager != null && m.NpcDriversManager.IsNpcDriver(d)) return "pooled_driver";
            return VcHh(d) ? "live_household" : "live_npc";
        }

        static bool VcLiveRider(Vehicle v)
        {
            List<Sim> all = new List<Sim>();
            try { Sim[] occ = v.Occupants; if (occ != null) all.AddRange(occ); } catch (Exception) { }
            try { Car car = v as Car; if (car != null) all.AddRange(car.Passengers); } catch (Exception) { }
            try { Boat b = v as Boat; Sim[] p = b != null ? b.Passengers : null; if (p != null) all.AddRange(p); } catch (Exception) { }
            foreach (Sim s in all) if (s != null && !s.HasBeenDestroyed && s != v.Driver) return true;
            return false;
        }

        // fields of a situation type (and its bases) that can hold a vehicle: a Vehicle, or a collection of them
        static List<FieldInfo> VcSitFields(Type t)
        {
            List<FieldInfo> r;
            if (sVcSitFields.TryGetValue(t, out r)) return r;
            r = new List<FieldInfo>();
            for (Type b = t; b != null && b != typeof(object); b = b.BaseType)
                foreach (FieldInfo f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Type ft = f.FieldType;
                    bool hit = typeof(Vehicle).IsAssignableFrom(ft);
                    if (!hit && ft.IsGenericType && typeof(IEnumerable).IsAssignableFrom(ft))
                        foreach (Type ga in ft.GetGenericArguments()) if (typeof(Vehicle).IsAssignableFrom(ga)) hit = true;
                    if (hit) r.Add(f);
                }
            sVcSitFields[t] = r;
            return r;
        }

        static void VcAddHeld(Dictionary<Vehicle, string> held, object o, string why)
        {
            Vehicle v = o as Vehicle;
            if (v != null) { if (!held.ContainsKey(v)) held[v] = why; return; }
            IDictionary d = o as IDictionary;
            if (d != null) { foreach (object x in d.Values) VcAddHeld(held, x, why); foreach (object x in d.Keys) if (x is Vehicle) VcAddHeld(held, x, why); return; }
            IEnumerable e = o as IEnumerable;
            if (e != null && !(o is string)) foreach (object x in e) VcAddHeld(held, x, why);
        }

        static void VcBump(Dictionary<string, object> d, string k)
        {
            object n;
            d[k] = (d.TryGetValue(k, out n) ? (int)n : 0) + 1;
        }
        static Dictionary<string, object> VcSub(Dictionary<string, object> d, string k)
        {
            object o;
            if (!d.TryGetValue(k, out o)) { o = Util.Obj(); d[k] = o; }
            return (Dictionary<string, object>)o;
        }

        static object VehicleCensus(Args a)
        {
            RequireLive();
            int samples = (int)a.Long("samples", 10);
            bool list = a.Bool("list", false);
            long now = SimClock.CurrentTicks, hour = SimClock.ConvertToTicks(1f, TimeUnit.Hours);
            Dictionary<string, object> r = Util.Obj();

            // holders: reserved by a live Sim, preferred by a resident, a situation field
            Dictionary<Vehicle, string> held = new Dictionary<Vehicle, string>();
            int sitErrors = 0;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed) continue;
                try { Vehicle rv = s.GetReservedVehicle(); if (rv != null) VcAddHeld(held, rv, VcHh(s) ? "reserved_household" : "reserved_npc"); } catch (Exception) { }
            }
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h.SimDescriptions == null) continue;
                foreach (SimDescription sd in h.SimDescriptions)
                {
                    try { Vehicle pv = sd.GetPreferredVehicle() as Vehicle; if (pv != null) VcAddHeld(held, pv, "preferred"); } catch (Exception) { }
                }
            }
            foreach (Situation sit in new List<Situation>(Situation.sAllSituations))
            {
                if (sit == null) continue;
                try { foreach (FieldInfo f in VcSitFields(sit.GetType())) VcAddHeld(held, f.GetValue(sit), "situation"); }
                catch (Exception) { sitErrors++; }
            }

            Dictionary<string, object> byKind = Util.Obj(), byWhere = Util.Obj(), byDriver = Util.Obj(), byHeld = Util.Obj();
            Dictionary<string, object> kindWhere = Util.Obj(), orphans = Util.Obj(), orphanSamples = Util.Obj();
            List<object> all = new List<object>();
            Dictionary<ulong, bool> orphanNow = new Dictionary<ulong, bool>();
            int total = 0, orphanTotal = 0, errors = 0;
            foreach (Vehicle v in Sims3.Gameplay.Queries.GetObjects<Vehicle>())
            {
                if (v == null || v.HasBeenDestroyed) continue;
                try
                {
                    total++;
                    string kind = VcKind(v), where = VcWhere(v), driver = VcDriver(v);
                    string h;
                    if (!held.TryGetValue(v, out h)) h = kind == "carpool" ? "carpool" : "none";
                    VcBump(byKind, kind); VcBump(byWhere, where); VcBump(byDriver, driver); VcBump(byHeld, h);
                    VcBump(VcSub(kindWhere, kind), where);
                    bool liveDriver = driver == "live_household" || driver == "live_npc" || driver == "pooled_driver";
                    // "traffic" (LightweightTaxi, CarNpcWithNoDriver) is EA's ambient street traffic: driverless by design and owned by the
                    // traffic system, so it is counted in by_kind but never as an orphan (2026-11-10 run: a steady 10 on every census).
                    bool orphan = kind != "traffic" && h == "none" && !liveDriver && !VcLiveRider(v) && v.UseCount == 0
                        && !where.StartsWith("inventory") && where != "parked_residential";
                    Lot lot = v.LotCurrent;
                    Dictionary<string, object> o = null;
                    if (orphan || list)
                    {
                        o = Util.Obj();
                        o["type"] = v.GetType().Name; o["id"] = Util.Id(v.ObjectId); o["kind"] = kind; o["where"] = where;
                        o["driver"] = driver; o["held"] = h;
                        o["pos"] = Pos(v.Position);
                        o["lot"] = lot != null && !lot.IsWorldLot ? Util.LotName(lot) : null;
                    }
                    if (orphan)
                    {
                        orphanTotal++;
                        VcBump(orphans, kind);
                        long first;
                        if (!sVcFirstOrphan.TryGetValue(v.ObjectId.Value, out first)) { first = now; sVcFirstOrphan[v.ObjectId.Value] = now; }
                        orphanNow[v.ObjectId.Value] = true;
                        o["age_sim_hours"] = Math.Round((double)(now - first) / hour, 1);
                        List<object> sl = orphanSamples.ContainsKey(kind) ? (List<object>)orphanSamples[kind] : null;
                        if (sl == null) { sl = new List<object>(); orphanSamples[kind] = sl; }
                        if (sl.Count < samples) sl.Add(o);
                    }
                    if (list) all.Add(o);
                }
                catch (Exception) { errors++; }
            }
            // forget vehicles that are no longer orphans (or gone), so a later orphan of the same id starts at age 0
            foreach (ulong id in new List<ulong>(sVcFirstOrphan.Keys)) if (!orphanNow.ContainsKey(id)) sVcFirstOrphan.Remove(id);
            long oldest = 0;
            foreach (long t in sVcFirstOrphan.Values) if (now - t > oldest) oldest = now - t;

            r["sim_time"] = SimClock.CurrentTime().ToString();
            r["total"] = total;
            r["by_kind"] = byKind; r["by_where"] = byWhere; r["by_driver"] = byDriver; r["by_held"] = byHeld;
            r["kind_where"] = kindWhere;
            r["orphans_total"] = orphanTotal;
            r["orphans"] = orphans;
            r["orphan_oldest_sim_hours"] = Math.Round((double)oldest / hour, 1);
            r["orphan_samples"] = orphanSamples;
            r["situation_holders"] = held.Count;
            if (sitErrors > 0) r["situation_errors"] = sitErrors;
            if (errors > 0) r["errors"] = errors;
            if (list) r["vehicles"] = all;

            // parking: spaces and posts per lot type
            Dictionary<string, object> park = Util.Obj();
            foreach (ParkingSpace ps in Sims3.Gameplay.Queries.GetObjects<ParkingSpace>())
            {
                if (ps == null) continue;
                Lot lot = ps.LotCurrent;
                Dictionary<string, object> d = VcSub(park, "spaces_" + (lot != null && lot.IsResidentialLot ? "residential" : "community"));
                VcBump(d, "total");
                IGameObject rv = ps.ReservedVehicle;
                bool parked = ps.GetParkedVehicle() != null;
                if (parked) VcBump(d, rv != null ? "reserved_with_car" : "car_not_reserved");
                else if (rv != null) VcBump(d, VcObjGone(rv) ? "reserved_destroyed" : "reserved_car_elsewhere");
                else VcBump(d, "free");
            }
            foreach (MooringPost mp in Sims3.Gameplay.Queries.GetObjects<MooringPost>())
            {
                if (mp == null) continue;
                Lot lot = mp.LotCurrent;
                Dictionary<string, object> d = VcSub(park, "posts_" + (lot != null && lot.IsResidentialLot ? "residential" : "community"));
                VcBump(d, "total");
                IGameObject rv = mp.ReservedVehicle;
                bool parked = mp.GetParkedBoat() != null;
                if (parked) VcBump(d, rv != null ? "reserved_with_boat" : "boat_not_reserved");
                else if (rv != null) VcBump(d, VcObjGone(rv) ? "reserved_destroyed" : "reserved_boat_elsewhere");
                else VcBump(d, "free");
            }
            r["parking"] = park;

            // the docs/544 switches and counters (ts3fixes VehicleLeaks.cs)
            Dictionary<string, object> sw = Util.Obj();
            foreach (string n in new string[] { "vehparkreserve", "vehgetinleak", "vehseatdestroy", "vehfrankentaxi", "vehprotestcar" }) sw[n] = ModOn(n);
            r["switches"] = sw;
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            r["counters"] = c != null && c.Length > 2216 ? (object)VehLeakCounters(c) : "ts3fixes not loaded or older than docs/544";
            return r;
        }

        static bool VcObjGone(IGameObject o)
        {
            try { GameObject g = o as GameObject; return (g != null && g.HasBeenDestroyed) || !Sims3.SimIFace.Objects.IsValid(o.ObjectId); }
            catch (Exception) { return true; }
        }

        // slots 2200-2216 (docs/registry.md fixes-2200); also merged into the patches reply
        static Dictionary<string, object> VehLeakCounters(int[] c)
        {
            Dictionary<string, object> r = Util.Obj();
            string[] n = { "vehparkreserve_seen_pick", "vehparkreserve_cleared_pick", "vehparkreserve_seen_hourly", "vehparkreserve_cleared_hourly", "vehparkreserve_kept_household",
                "vehgetinleak_seen", "vehgetinleak_released", "vehgetinleak_to_inventory", "vehgetinleak_kept",
                "vehseatdestroy_seen", "vehseatdestroy_refs_cleared", "vehseatdestroy_released",
                "vehfrankentaxi_seen", "vehfrankentaxi_released",
                "vehprotestcar_tracked", "vehprotestcar_left", "vehprotestcar_destroyed" };
            for (int i = 0; i < n.Length; i++) r[n[i]] = c[2200 + i];
            return r;
        }
    }
}
