using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Vehicle leak paths in EA's code (docs/544 §3 L1-L5, fix designs §4 S1-S5). Five switches, each off by default (the
    // local test package turns them on with build-fixes.ts --on, see docs/544 §7):
    //   vehparkreserve (S1, L3)  a parking space or mooring post whose reservation points at a destroyed vehicle, or at an
    //                            NPC-generated car now in an inventory, gets the reservation cleared: before EA's
    //                            Vehicle.GetClosestUnreservedParkingSpace picks a space, and in an hourly pass.
    //   vehgetinleak   (S2, L1)  GetInVehicleRouteAction / GetInBoatRouteAction ended (throw, Terminate) with the vehicle
    //                            it got out of any inventory and parent and the Sim not in it: a throwaway taxi kind is
    //                            released the way EA releases it, an NPC-generated car or boat goes into its NPC's inventory.
    //   vehseatdestroy (S3, L2)  a Sim destroyed while seated (kSimDestroyed, posture still set) whose vehicle is still
    //                            there one tick later: dead driver / passenger references cleared, a throwaway kind with no
    //                            live occupant released.
    //   vehfrankentaxi (S4, L4)  the Frankensim intro taxi that never entered the world is released at the intro's Cleanup.
    //   vehprotestcar  (S5, L5)  protest break-up cop cars with no driver, no user and no queued drive-away one sim-hour
    //                            after the outcome started are destroyed.
    // Household rule (CLAUDE.md): nothing is done to a vehicle a household, selected or unknown-scope Sim drives, rides,
    // uses, or routed with (S2 checks the routing Sim), or to a player car or boat (CarOwnable / BoatOwnable not generated
    // for an NPC); bikes, motorcycles and brooms are never touched. Throwaway kinds = CarNpcWithNpcDriver except carpools
    // (limos included: CarLimo is a CarpoolWithDriver) and the moving van, AdultTaxiBicycle and ChildTaxiBicycle (with
    // their scooter subclasses), BoatTaxi. Work that destroys runs in a LoopTask on the next tick and re-checks first.
    // Counters [2200-2216] (docs/registry.md, fixes-2200); every "seen" counter also counts with the switch off.
    public static partial class Fixes
    {
        static readonly Type sVlGiCarType = typeof(Sim).Assembly.GetType("Sims3.Gameplay.Routing.GetInVehicleRouteAction");
        static readonly Type sVlGiBoatType = typeof(Sim).Assembly.GetType("Sims3.Gameplay.Routing.GetInBoatRouteAction");
        static readonly FieldInfo sVlGiCarSim = sVlGiCarType != null ? sVlGiCarType.GetField("mRoutingSim", kInstAll) : null;
        static readonly FieldInfo sVlGiBoatSim = sVlGiBoatType != null ? sVlGiBoatType.GetField("mRoutingSim", kInstAll) : null;
        static readonly FieldInfo sVlGiBoatId = sVlGiBoatType != null ? sVlGiBoatType.GetField("mLastFoundBoatId", kInstAll) : null;
        static readonly Type sVlFrankIntro = typeof(OccultFrankenstein).GetNestedType("MyBestFriendIntroduction", BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyVehicleLeaks()
        {
            Patch(typeof(Vehicle), "GetClosestUnreservedParkingSpace", new Type[] { typeof(ParkingSpace[]), typeof(IUsesParkingSpace) }, typeof(VlParkHook), "PickHook", "PickOrig");  // S1
            if (sVlGiCarType != null) Patch(sVlGiCarType, "PerformAction", Type.EmptyTypes, typeof(VlGetInCarHook), "PerformHook", "PerformOrig");  // S2
            if (sVlGiBoatType != null) Patch(sVlGiBoatType, "PerformAction", Type.EmptyTypes, typeof(VlGetInBoatHook), "PerformHook", "PerformOrig");  // S2
            Patch(typeof(Sim), "GetVehicle", new Type[] { typeof(Lot), typeof(bool) }, typeof(VlGetVehicleHook), "GetHook", "GetOrig");  // S2: the vehicle the get-in got
            if (sVlFrankIntro != null) Patch(sVlFrankIntro, "Cleanup", Type.EmptyTypes, typeof(VlFrankHook), "CleanupHook", "CleanupOrig");  // S4
            Patch(typeof(ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice), "Init", new Type[] { typeof(ImprovedProtestSituation) }, typeof(VlProtestHook), "InitHook", "InitOrig");  // S5
        }

        // ---- common ----

        static AlarmHandle sVlAlarm = AlarmHandle.kInvalidHandle;
        static EventListener sVlDestroyedListener;
        static readonly Dictionary<string, int> sVlLines = new Dictionary<string, int>();

        // Called on every world load (Instantiator): hourly pass (S1 sweep, S5 check) and the kSimDestroyed listener (S3).
        public static void VehicleLeaksWorldLoad()
        {
            sVlProtest.Clear();
            sVlParkSeen.Clear();
            sVlGiActive.Clear();
            sVlGiGot.Clear();
            sVlGiArmed = false;
            if (sVlDestroyedListener != null) EventTracker.RemoveListener(sVlDestroyedListener);
            sVlDestroyedListener = EventTracker.AddListener(EventTypeId.kSimDestroyed, OnVlSimDestroyed);
            if (AlarmManager.Global == null) return;
            if (sVlAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sVlAlarm);
            sVlAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { Simulator.AddObject(new LoopTask(VlHourly)); }, 1f, TimeUnit.Hours, "Ts3Fixes.VehicleLeaks", AlarmType.NeverPersisted, null);
        }

        static void VlHourly()
        {
            try { VlParkPass(); } catch (Exception e) { VlThrew("vehparkreserve", e); }
            try { VlProtestPass(); } catch (Exception e) { VlThrew("vehprotestcar", e); }
        }

        // One ts3native.log line ("ts3fixes: <line>") and one agent recent line; at most 200 per switch and session.
        static void VlLog(string sw, string line)
        {
            int n;
            sVlLines.TryGetValue(sw, out n);
            if (n >= 200) return;
            sVlLines[sw] = n + 1;
            ShortcutLog(line);
            try { DiagLine(line); } catch (Exception) { }
        }
        static void VlThrew(string sw, Exception e)
        {
            string k = sw + ".threw";
            if (sVlLines.ContainsKey(k)) return;
            sVlLines[k] = 1;
            AppliedLog().Add(sw + ": pass threw " + e.GetType().Name + ": " + e.Message);
        }

        static string VlName(IGameObject o)
        {
            if (o == null) return "null";
            try { return o.GetType().Name + " " + o.ObjectId.Value; } catch (Exception) { return o.GetType().Name; }
        }

        static bool VlGone(IGameObject o)
        {
            if (o == null) return true;
            try
            {
                GameObject g = o as GameObject;
                return (g != null && g.HasBeenDestroyed) || !Sims3.SimIFace.Objects.IsValid(o.ObjectId);
            }
            catch (Exception) { return true; }
        }

        // the kinds EA itself destroys on release and nobody owns (taxis, NPC-driven limos excluded: carpool class)
        static bool VlThrowaway(Vehicle v)
        {
            if (v == null || v is CarpoolWithDriver || v is CarMovingVanWithDriver) return false;
            return v is CarNpcWithNpcDriver || v is AdultTaxiBicycle || v is ChildTaxiBicycle || v is BoatTaxi;
        }

        static bool VlNpcGenerated(Vehicle v)
        {
            CarOwnable c = v as CarOwnable;
            if (c != null) return c.GeneratedOwnableForNpc;
            BoatOwnable b = v as BoatOwnable;
            return b != null && b.GeneratedOwnableForNpc;
        }

        static NpcDriversManager VlDrivers() { return CarNpcManager.Singleton != null ? CarNpcManager.Singleton.NpcDriversManager : null; }

        static bool VlPooled(Sim s)
        {
            NpcDriversManager m = VlDrivers();
            return s != null && m != null && m.IsNpcDriver(s);
        }

        // Who is in or using the vehicle: any household / selected / unknown-scope Sim (hh), any other live Sim that is not
        // a pooled NPC driver (other). Destroyed Sims count as neither.
        static void VlWho(Vehicle v, Sim except, out bool hh, out bool other)
        {
            hh = false; other = false;
            List<Sim> all = new List<Sim>();
            try { Sim[] occ = v.Occupants; if (occ != null) all.AddRange(occ); } catch (Exception) { hh = true; }
            try { Car car = v as Car; if (car != null) all.AddRange(car.Passengers); } catch (Exception) { }
            try { Boat boat = v as Boat; Sim[] p = boat != null ? boat.Passengers : null; if (p != null) all.AddRange(p); } catch (Exception) { }
            try { if (v.ActorsUsingMe != null) all.AddRange(v.ActorsUsingMe); } catch (Exception) { hh = true; }
            if (v.Driver != null) all.Add(v.Driver);
            foreach (Sim s in all)
            {
                if (s == null || s == except || s.HasBeenDestroyed) continue;
                if (HhScope(s)) hh = true;
                else if (!VlPooled(s)) other = true;
            }
        }

        // EA's release of a throwaway vehicle (CarNpcWithNpcDriver.DriveOffscreenAndRelease without the drive): pooled
        // drivers back to the pool and destroyed, then the vehicle destroyed.
        static void VlRelease(Vehicle v)
        {
            NpcDriversManager m = VlDrivers();
            List<Sim> seated = new List<Sim>();
            try { Sim[] occ = v.Occupants; if (occ != null) seated.AddRange(occ); } catch (Exception) { }
            if (v.Driver != null && !seated.Contains(v.Driver)) seated.Add(v.Driver);
            foreach (Sim s in seated)
            {
                if (s == null || m == null || !m.IsNpcDriver(s)) continue;
                m.ReleaseNpcDriver(s);
                if (!s.HasBeenDestroyed) s.Destroy();
            }
            if (!v.HasBeenDestroyed) v.Destroy();
        }

        // ---- S1 vehparkreserve (L3) ----

        // why the reservation is dead, or null when it is not
        static string VlParkDead(IGameObject reserved, bool parkedHere, Lot lot)
        {
            if (reserved == null || parkedHere) return null;
            if (VlGone(reserved)) return "destroyed";
            CarOwnable c = reserved as CarOwnable;
            BoatOwnable b = reserved as BoatOwnable;
            bool gen = c != null ? c.GeneratedOwnableForNpc : b != null && b.GeneratedOwnableForNpc;
            Lot home = c != null ? c.LotHome : b != null ? b.LotHome : null;
            GameObject g = reserved as GameObject;
            if (gen && home != lot && g != null && g.ItemComp != null && g.ItemComp.InventoryParent != null) return "npc vehicle in an inventory";
            return null;
        }

        // household rule: the active household's own lot, or a player vehicle that still exists
        static bool VlParkKept(IGameObject reserved, Lot lot)
        {
            try
            {
                Household act = Household.ActiveHousehold;
                if (lot == null || (act != null && (lot.Household == act || act.LotHome == lot))) return true;
                if (!VlGone(reserved) && (reserved is CarOwnable || reserved is BoatOwnable) && !VlNpcGenerated(reserved as Vehicle)) return true;
                return false;
            }
            catch (Exception) { return true; }
        }

        static readonly Dictionary<ulong, ulong> sVlParkSeen = new Dictionary<ulong, ulong>();  // space id -> dead reserved id counted

        // one space or post; hourly = from the pass (else from EA's pick). True when cleared.
        static bool VlParkOne(GameObject space, IGameObject reserved, bool parkedHere, bool hourly, ref int seen, ref int cleared, ref int kept)
        {
            Lot lot = space.LotCurrent;
            string why = VlParkDead(reserved, parkedHere, lot);
            if (why == null) return false;
            int[] c = PatchCounters();
            ulong sid = space.ObjectId.Value, rid = 0;
            try { rid = reserved.ObjectId.Value; } catch (Exception) { }
            ulong prev;
            bool fresh = !sVlParkSeen.TryGetValue(sid, out prev) || prev != rid;
            sVlParkSeen[sid] = rid;
            if (fresh) { c[hourly ? 2202 : 2200]++; seen++; }
            if (VlParkKept(reserved, lot)) { if (fresh) { c[2204]++; kept++; } return false; }
            if (!ShortcutOn("vehparkreserve")) return false;
            ParkingSpace ps = space as ParkingSpace;
            MooringPost mp = space as MooringPost;
            if (ps != null) ps.UnReserveSpot(ps.ReservedVehicle);
            else if (mp != null) mp.UnReserveSpot(mp.ReservedVehicle);
            sVlParkSeen.Remove(sid);
            c[hourly ? 2203 : 2201]++;
            cleared++;
            VlLog("vehparkreserve", "vehparkreserve: " + (hourly ? "hourly" : "pick") + ": cleared " + VlName(space) + " reservation to " + VlName(reserved) + " (" + why + ") on " + (lot != null ? lot.Name : "no lot")
                + "; seen pick " + c[2200] + " hourly " + c[2202] + ", cleared pick " + c[2201] + " hourly " + c[2203] + ", kept household " + c[2204]);
            return true;
        }

        public class VlParkHook
        {
            public static ParkingSpace PickHook(ParkingSpace[] parkingSpaces, IUsesParkingSpace vehicle)
            {
                try
                {
                    if (parkingSpaces != null)
                    {
                        int seen = 0, cleared = 0, kept = 0;
                        foreach (ParkingSpace ps in parkingSpaces)
                            if (ps != null && ps.ReservedVehicle != null) VlParkOne(ps, ps.ReservedVehicle, ps.GetParkedVehicle() != null, false, ref seen, ref cleared, ref kept);
                    }
                }
                catch (Exception e) { VlThrew("vehparkreserve", e); }  // the check must never stop EA's pick
                return PickOrig(parkingSpaces, vehicle);
            }
            public static ParkingSpace PickOrig(ParkingSpace[] parkingSpaces, IUsesParkingSpace vehicle) { return null; } // receives Vehicle.GetClosestUnreservedParkingSpace's original header
        }

        static void VlParkPass()
        {
            int seen = 0, cleared = 0, kept = 0;
            foreach (ParkingSpace ps in Sims3.Gameplay.Queries.GetObjects<ParkingSpace>())
                if (ps != null && ps.ReservedVehicle != null) VlParkOne(ps, ps.ReservedVehicle, ps.GetParkedVehicle() != null, true, ref seen, ref cleared, ref kept);
            foreach (MooringPost mp in Sims3.Gameplay.Queries.GetObjects<MooringPost>())
                if (mp != null && mp.ReservedVehicle != null) VlParkOne(mp, mp.ReservedVehicle, mp.GetParkedBoat() != null, true, ref seen, ref cleared, ref kept);
        }

        // ---- S2 vehgetinleak (L1, L1b) ----

        static readonly Dictionary<Sim, int> sVlGiActive = new Dictionary<Sim, int>();     // Sims inside a get-in action
        static readonly Dictionary<Sim, Vehicle> sVlGiGot = new Dictionary<Sim, Vehicle>(); // what GetVehicle gave them there
        static bool sVlGiArmed;  // repro: reset the next NPC whose get-in got a vehicle (VehicleLeakCheck "getin arm")

        static void VlGiEnter(Sim s)
        {
            int n;
            sVlGiActive.TryGetValue(s, out n);
            sVlGiActive[s] = n + 1;
        }
        static Vehicle VlGiExit(Sim s)
        {
            int n;
            if (!sVlGiActive.TryGetValue(s, out n)) return null;
            if (n > 1) { sVlGiActive[s] = n - 1; return null; }
            sVlGiActive.Remove(s);
            Vehicle v;
            if (sVlGiGot.TryGetValue(s, out v)) sVlGiGot.Remove(s);
            return v;
        }

        // seated = the Sim is the driver, a passenger or in a posture whose container is the vehicle
        static bool VlSeated(Sim s, Vehicle v)
        {
            try
            {
                if (v.Driver == s) return true;
                if (s.Posture != null && s.Posture.Container == v) return true;
                Sim[] occ = v.Occupants;
                if (occ != null) foreach (Sim o in occ) if (o == s) return true;
            }
            catch (Exception) { return true; }
            return false;
        }

        // the vehicle is loose: not destroyed, in no inventory, on no parent (space, post, rack)
        static bool VlLoose(Vehicle v)
        {
            if (v == null || VlGone(v)) return false;
            if (v.ItemComp != null && v.ItemComp.InventoryParent != null) return false;
            return v.Parent == null;
        }

        static void VlGiLeft(Sim s, Vehicle v, string how)
        {
            if (s == null || v == null || !VlLoose(v) || VlSeated(s, v)) return;
            if (!VlThrowaway(v) && !VlNpcGenerated(v) && !(v is CarOwnable) && !(v is BoatOwnable)) return;  // service cars, bikes, brooms: not ours
            PatchCounters()[2205]++;
            if (!ShortcutOn("vehgetinleak")) return;
            Simulator.AddObject(new LoopTask(delegate { try { VlGiFix(s, v, how); } catch (Exception e) { VlThrew("vehgetinleak", e); } }));
        }

        static void VlGiFix(Sim s, Vehicle v, string how)
        {
            if (!VlLoose(v) || VlSeated(s, v)) return;
            int[] c = PatchCounters();
            bool hh, other;
            VlWho(v, s, out hh, out other);
            string act;
            if (HhScope(s) || hh || other) { c[2208]++; act = "kept (household, selected or live Sim with it)"; }
            else if (VlThrowaway(v)) { VlRelease(v); c[2206]++; act = "released"; }
            else if (VlNpcGenerated(v) && !s.HasBeenDestroyed && s.Inventory != null)
            {
                CarOwnable co = v as CarOwnable;
                if (co != null) co.PendingUse = false;
                BoatOwnable bo = v as BoatOwnable;
                if (bo != null) bo.PendingUse = false;
                if (s.Inventory.Contains(v) || s.Inventory.TryToAdd(v)) { c[2207]++; act = "put in the NPC's inventory"; }
                else { c[2208]++; act = "kept (inventory refused it)"; }
            }
            else { c[2208]++; act = "kept (player vehicle or NPC gone)"; }
            VlLog("vehgetinleak", "vehgetinleak: " + VlName(v) + " left by " + s.FullName + "'s get-in (" + how + "): " + act
                + "; seen " + c[2205] + ", released " + c[2206] + ", to inventory " + c[2207] + ", kept " + c[2208]);
        }

        static void VlGiArmedGot(Sim s, Vehicle v)
        {
            if (!sVlGiArmed || v == null || HhScope(s)) return;
            sVlGiArmed = false;
            AppliedLog().Add("vehgetinleak check: resetting " + s.FullName + " inside the get-in of " + VlName(v));
            Simulator.AddObject(new LoopTask(delegate { try { if (!s.HasBeenDestroyed) s.SetObjectToReset(); } catch (Exception) { } }));
        }

        public class VlGetVehicleHook
        {
            public Vehicle GetHook(Lot lot, bool allowUFO)
            {
                Vehicle v = GetOrig(lot, allowUFO);
                try
                {
                    if (sVlGiActive.Count > 0)
                    {
                        Sim s = (Sim)(object)this;
                        if (sVlGiActive.ContainsKey(s)) { sVlGiGot[s] = v; VlGiArmedGot(s, v); }
                    }
                }
                catch (Exception) { }
                return v;
            }
            public Vehicle GetOrig(Lot lot, bool allowUFO) { return null; } // receives Sim.GetVehicle's original header
        }

        public class VlGetInCarHook
        {
            public RouteAction.ActionResult PerformHook()
            {
                Sim s = null;
                try { s = sVlGiCarSim != null ? sVlGiCarSim.GetValue(this) as Sim : null; } catch (Exception) { }
                if (s == null) return PerformOrig();
                VlGiEnter(s);
                RouteAction.ActionResult r = RouteAction.ActionResult.Terminate;
                string how = "threw";
                try
                {
                    r = PerformOrig();
                    how = r.ToString();
                    return r;
                }
                catch (Exception e) { how = "threw " + e.GetType().Name; throw; }
                finally
                {
                    try
                    {
                        Vehicle v = VlGiExit(s);
                        if (r != RouteAction.ActionResult.Continue || how.StartsWith("threw")) VlGiLeft(s, v, how);
                    }
                    catch (Exception) { }
                }
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; } // receives GetInVehicleRouteAction.PerformAction's original header
        }

        public class VlGetInBoatHook
        {
            public RouteAction.ActionResult PerformHook()
            {
                Sim s = null;
                try { s = sVlGiBoatSim != null ? sVlGiBoatSim.GetValue(this) as Sim : null; } catch (Exception) { }
                if (s == null) return PerformOrig();
                RouteAction.ActionResult r = RouteAction.ActionResult.Terminate;
                string how = "threw";
                try
                {
                    r = PerformOrig();
                    how = r.ToString();
                    return r;
                }
                catch (Exception e) { how = "threw " + e.GetType().Name; throw; }
                finally
                {
                    try
                    {
                        // the boat this call got (mLastFoundBoatId is set right after GetBoatForGetInBoatRouteAction); a stale id from an
                        // earlier call names a boat that is seated, parked or gone, which VlGiLeft skips
                        ObjectGuid id = (ObjectGuid)sVlGiBoatId.GetValue(this);
                        if ((r != RouteAction.ActionResult.ContinueAndPopPathAndReplan || how.StartsWith("threw")) && id.IsValid)
                            VlGiLeft(s, GameObject.GetObject(id) as Vehicle, how);
                    }
                    catch (Exception) { }
                }
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; } // receives GetInBoatRouteAction.PerformAction's original header
        }

        // ---- S3 vehseatdestroy (L2) ----

        static ListenerAction OnVlSimDestroyed(Event e)
        {
            try
            {
                Sim s = e.TargetObject as Sim;
                if (s == null || !GameStates.IsLiveState) return ListenerAction.Keep;
                Posture p = s.Posture;
                if (!(p is SittingInVehicle) && !(p is SittingInBoat)) return ListenerAction.Keep;
                Vehicle v = p.Container as Vehicle;
                if (v == null) return ListenerAction.Keep;
                bool hh = HhScope(s);  // at the top of Sim.Destroy: not destroyed yet, so this is the Sim's real scope
                string name = s.FullName;
                Simulator.AddObject(new LoopTask(delegate { try { VlSeatFix(v, s, hh, name); } catch (Exception ex) { VlThrew("vehseatdestroy", ex); } }));
            }
            catch (Exception) { }
            return ListenerAction.Keep;
        }

        static void VlSeatFix(Vehicle v, Sim dead, bool deadHh, string name)
        {
            if (VlGone(v)) return;  // EA's own release destroyed it (taxi drivers, carpools): the normal case
            int[] c = PatchCounters();
            c[2209]++;
            if (!ShortcutOn("vehseatdestroy") || deadHh) return;
            if (!(v is Car) && !(v is Boat) && !VlThrowaway(v)) return;  // owned bikes, brooms: not ours
            if ((v is CarOwnable || v is BoatOwnable) && !VlNpcGenerated(v)) return;  // player vehicle
            bool hh, other;
            VlWho(v, null, out hh, out other);
            if (hh) return;
            // dead references EA leaves (SittingInBoat never releases them; Sim.Dispose does it later for cars only)
            int refs = 0;
            List<Sim> all = new List<Sim>();
            if (v.Driver != null) all.Add(v.Driver);
            try { Car car = v as Car; if (car != null) all.AddRange(car.Passengers); } catch (Exception) { }
            try { Boat boat = v as Boat; Sim[] bp = boat != null ? boat.Passengers : null; if (bp != null) all.AddRange(bp); } catch (Exception) { }
            foreach (Sim s in all)
            {
                if (s == null || !s.HasBeenDestroyed) continue;
                if (s != v.Driver && !(v is Car) && !(v is Boat)) continue;  // a bike's ReleaseSimRef clears the driver whoever s is
                v.ReleaseSimRef(s);
                refs++;
            }
            c[2210] += refs;
            string act = refs + " dead reference(s) cleared";
            if (VlThrowaway(v) && !other) { VlRelease(v); c[2211]++; act += ", released"; }
            VlLog("vehseatdestroy", "vehseatdestroy: " + name + " destroyed while seated in " + VlName(v) + ": " + act
                + "; seen " + c[2209] + ", refs cleared " + c[2210] + ", released " + c[2211]);
        }

        // ---- S4 vehfrankentaxi (L4) ----

        public class VlFrankHook
        {
            public void CleanupHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Sim a = ii.InstanceActor;
                    Vehicle v = a != null ? a.GetReservedVehicle() : null;
                    if (v != null && VlThrowaway(v) && !VlGone(v) && !v.InWorld && !VlSeated(a, v))
                    {
                        PatchCounters()[2212]++;
                        if (ShortcutOn("vehfrankentaxi"))
                        {
                            string name = a.FullName;
                            Simulator.AddObject(new LoopTask(delegate
                            {
                                try
                                {
                                    if (VlGone(v) || v.InWorld || (a != null && !a.HasBeenDestroyed && a.GetReservedVehicle() == v)) return;  // used, or reserved again
                                    bool hh, other;
                                    VlWho(v, null, out hh, out other);
                                    if (hh || other) return;
                                    VlRelease(v);
                                    int[] c = PatchCounters();
                                    c[2213]++;
                                    VlLog("vehfrankentaxi", "vehfrankentaxi: " + VlName(v) + " made for " + name + "'s intro never entered the world: released; seen " + c[2212] + ", released " + c[2213]);
                                }
                                catch (Exception e) { VlThrew("vehfrankentaxi", e); }
                            }));
                        }
                    }
                }
                catch (Exception) { }  // never stop EA's cleanup
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives OccultFrankenstein.MyBestFriendIntroduction.Cleanup's original header
        }

        // ---- S5 vehprotestcar (L5) ----

        sealed class VlCop { public Sim Cop; public CarService Car; public long Made; public bool Counted; }
        static readonly List<VlCop> sVlProtest = new List<VlCop>();

        public class VlProtestHook
        {
            public void InitHook(ImprovedProtestSituation parent)
            {
                try { InitOrig(parent); }
                finally
                {
                    try
                    {
                        ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice o = (ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice)(object)this;
                        if (o.CopCars != null)
                            foreach (KeyValuePair<Sim, CarService> kv in o.CopCars)
                                if (kv.Value != null) { sVlProtest.Add(new VlCop { Cop = kv.Key, Car = kv.Value, Made = SimClock.CurrentTicks }); PatchCounters()[2214]++; }
                    }
                    catch (Exception) { }
                }
            }
            public void InitOrig(ImprovedProtestSituation parent) { } // receives ProtestOutcomeBrokenUpByPolice.Init's original header
        }

        static bool VlDriveAwayQueued(Sim cop, CarService car)
        {
            if (cop == null || cop.HasBeenDestroyed || cop.InteractionQueue == null) return false;
            foreach (InteractionInstance ii in cop.InteractionQueue.InteractionList)
            {
                if (ii is ImprovedProtestSituation.ProtestOutcomeBrokenUpByPolice.PoliceGoToProtesttLocation) return true;
                DriveAwayInServiceCar.Definition d = ii != null ? ii.InteractionDefinition as DriveAwayInServiceCar.Definition : null;
                if (d != null && d.Car == car) return true;
            }
            return false;
        }

        static void VlProtestPass()
        {
            if (sVlProtest.Count == 0) return;
            long hour = SimClock.ConvertToTicks(1f, TimeUnit.Hours), now = SimClock.CurrentTicks;
            int[] c = PatchCounters();
            for (int i = sVlProtest.Count - 1; i >= 0; i--)
            {
                VlCop t = sVlProtest[i];
                CarService car = t.Car;
                if (VlGone(car) || now - t.Made > 48 * hour) { sVlProtest.RemoveAt(i); continue; }
                if (now - t.Made < hour || VlDriveAwayQueued(t.Cop, car)) continue;
                bool hh, other;
                VlWho(car, null, out hh, out other);
                if (hh || other || car.Driver != null || (t.Cop != null && !t.Cop.HasBeenDestroyed && VlSeated(t.Cop, car))) continue;
                if (!t.Counted) { t.Counted = true; c[2215]++; }
                if (!ShortcutOn("vehprotestcar")) continue;
                bool inWorld = car.InWorld;
                car.Destroy();
                sVlProtest.RemoveAt(i);
                c[2216]++;
                VlLog("vehprotestcar", "vehprotestcar: protest break-up " + VlName(car) + (inWorld ? " (in the world)" : " (out of the world)") + " with no driver and no drive-away queued after " + ((now - t.Made) / hour) + " sim-hour(s): destroyed; tracked " + c[2214] + ", left " + c[2215] + ", destroyed " + c[2216]);
            }
        }

        // ---- agent checks (repros veh-park-reserve-dead, veh-seat-destroy, veh-getin-reset in tools/harness/repros.json) ----
        //   "park make":   on the community lot with the most free parking spaces, an NPC-generated car is made out of the
        //                  world, parked into the free space closest to the lot centre without a fade (ParkVehicle doFade
        //                  false: EA's FadeCarIn would drop the reservation of a car whose home is another lot), and destroyed.
        //   "park expect kept" (switch off) / "park expect cleared" (switch on): EA's TryParkCarInSpace path is asked for a
        //                  space on that lot (GetClosestUnreservedParkingSpace through the hooked method). Off: the space
        //                  stays reserved to the destroyed car, seen +1, cleared +0. On: the reservation is gone, cleared +1.
        //   "seat make":   an NPC taxi with no driver (CreateNpcCar, the NPC-drives-itself kind) and a fresh NPC (a taxi
        //                  driver made and released from the pool, so a plain NPC-household Sim) put in as its driver.
        //   "seat destroy": destroys that Sim while it sits in the taxi (kSimDestroyed with SittingInVehicle).
        //   "seat expect left" (off) / "seat expect released" (on): off: the taxi still exists, seen +1, released +0 (the
        //                  check destroys it). On: the taxi is gone, released +1.
        //   "getin arm":   the next NPC whose GetInVehicleRouteAction gets a vehicle is reset one tick later (inside the
        //                  0.5 s fade before PutInDriver). "getin read": the S2 counters.
        static Vehicle sVlCheckCar;
        static ParkingSpace sVlCheckSpace;
        static Sim sVlCheckSim;
        static int sVlCheck0, sVlCheck1;
        public static string VehicleLeakCheck(string what)
        {
            int[] c = PatchCounters();
            if (what == "park make")
            {
                ParkingSpace best = null;
                int bestFree = 0;
                foreach (Lot lot in LotManager.AllLots)
                {
                    if (lot == null || lot.IsWorldLot || !lot.IsCommunityLot) continue;
                    ParkingSpace[] spaces = Sims3.Gameplay.Queries.GetObjects<ParkingSpace>(lot);
                    int free = 0;
                    ParkingSpace pick = null;
                    float d = float.MaxValue;
                    foreach (ParkingSpace ps in spaces)
                    {
                        if (ps is LargeParkingSpace || ps is WideParkingSpace || ps.ReservedVehicle != null || ps.GetParkedVehicle() != null) continue;
                        free++;
                        float dd = (ps.Position - lot.GetCenterPosition()).LengthSqr();
                        if (dd < d) { d = dd; pick = ps; }
                    }
                    if (free > bestFree) { bestFree = free; best = pick; }
                }
                if (best == null) throw new InvalidOperationException("vehparkreserve park make: no community lot with a free parking space");
                CarOwnable car = GlobalFunctions.CreateObjectOutOfWorld("CarUsed1") as CarOwnable;
                if (car == null) throw new InvalidOperationException("vehparkreserve park make: CarUsed1 made no CarOwnable");
                car.GeneratedOwnableForNpc = true;
                best.ParkVehicle(car, false);
                bool reserved = best.ReservedVehicle == car;
                car.Destroy();
                sVlCheckSpace = best;
                sVlParkSeen.Remove(best.ObjectId.Value);
                return VlName(car) + " parked in " + VlName(best) + " on " + best.LotCurrent.Name + " (" + bestFree + " free spaces), reserved " + reserved + ", destroyed; space still reserved " + (best.ReservedVehicle != null);
            }
            if (what == "park expect kept" || what == "park expect cleared")
            {
                ParkingSpace sp = sVlCheckSpace;
                sVlCheckSpace = null;
                if (sp == null) throw new InvalidOperationException("vehparkreserve " + what + ": park make first");
                if (sp.ReservedVehicle == null) throw new InvalidOperationException("vehparkreserve " + what + ": the space was not left reserved, so the case was not built");
                int seen0 = c[2200], clr0 = c[2201];
                CarOwnable probe = GlobalFunctions.CreateObjectOutOfWorld("CarUsed1") as CarOwnable;  // the car asking for a space
                if (probe == null) throw new InvalidOperationException("vehparkreserve " + what + ": CarUsed1 made no CarOwnable");
                ParkingSpace got;
                try { got = Vehicle.GetClosestUnreservedParkingSpace(new ParkingSpace[] { sp }, probe); }
                finally { probe.Destroy(); }
                int seen = c[2200] - seen0, clr = c[2201] - clr0;
                bool on = ShortcutOn("vehparkreserve");
                bool ok = what == "park expect kept" ? !on && sp.ReservedVehicle != null && got == null && seen == 1 && clr == 0
                    : on && sp.ReservedVehicle == null && got == sp && seen == 1 && clr == 1;
                string st = VlName(sp) + ": reserved " + (sp.ReservedVehicle != null) + ", pick " + (got != null ? "returned it" : "skipped it") + ", seen +" + seen + ", cleared +" + clr;
                if (!ok) throw new InvalidOperationException("vehparkreserve " + what + ": wrong outcome (switch " + on + "); " + st);
                return "as expected; " + st;
            }
            if (what == "seat make")
            {
                CarNpcManager cm = CarNpcManager.Singleton;
                NpcDriversManager m = VlDrivers();
                if (cm == null || m == null) throw new InvalidOperationException("vehseatdestroy seat make: no CarNpcManager");
                Vehicle taxi = cm.CreateNpcCar(CarNpcManager.NpcCars.Taxi);
                if (taxi == null) throw new InvalidOperationException("vehseatdestroy seat make: EA made no NPC taxi");
                Sim s = m.CreateNpcDriver(NpcDriversManager.NpcDrivers.Taxi);
                if (s == null) { taxi.Destroy(); throw new InvalidOperationException("vehseatdestroy seat make: EA made no Sim"); }
                m.ReleaseNpcDriver(s);  // a plain NPC now (pool entry while its Sim lives; EA reuses it only once the Sim is gone)
                taxi.PutInDriver(s);
                sVlCheckCar = taxi; sVlCheckSim = s;
                return VlName(taxi) + " with " + s.FullName + " as driver; posture " + (s.Posture != null ? s.Posture.GetType().Name : "none") + ", pooled " + VlPooled(s);
            }
            if (what == "seat destroy")
            {
                if (sVlCheckSim == null) throw new InvalidOperationException("vehseatdestroy seat destroy: seat make first");
                sVlCheck0 = c[2209]; sVlCheck1 = c[2211];
                string p = sVlCheckSim.Posture != null ? sVlCheckSim.Posture.GetType().Name : "none";
                sVlCheckSim.Destroy();
                sVlCheckSim = null;
                return "destroyed in posture " + p;
            }
            if (what == "seat expect left" || what == "seat expect released")
            {
                Vehicle v = sVlCheckCar;
                sVlCheckCar = null;
                if (v == null) throw new InvalidOperationException("vehseatdestroy " + what + ": seat make first");
                int seen = c[2209] - sVlCheck0, rel = c[2211] - sVlCheck1;
                bool on = ShortcutOn("vehseatdestroy");
                bool gone = VlGone(v);
                bool ok = what == "seat expect left" ? !on && !gone && seen == 1 && rel == 0 : on && gone && seen == 1 && rel == 1;
                string st = VlName(v) + ": " + (gone ? "gone" : "still there, driver " + (v.Driver == null ? "none" : v.Driver.HasBeenDestroyed ? "destroyed" : "live")) + ", seen +" + seen + ", released +" + rel;
                if (!gone) { try { v.Destroy(); } catch (Exception) { } }
                if (!ok) throw new InvalidOperationException("vehseatdestroy " + what + ": wrong outcome (switch " + on + "); " + st);
                return "as expected; " + st;
            }
            if (what == "getin arm") { sVlGiArmed = true; return "armed: the next NPC get-in that gets a vehicle is reset in the fade"; }
            if (what == "getin read") return "armed " + sVlGiArmed + "; seen " + c[2205] + ", released " + c[2206] + ", to inventory " + c[2207] + ", kept " + c[2208];
            return "unknown check " + what + " (park make, park expect kept, park expect cleared, seat make, seat destroy, seat expect left, seat expect released, getin arm, getin read)";
        }
    }
}
