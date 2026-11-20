using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/851 boattaximemo (default on, proven in game 2026-11-19). Every boat a Sim rides comes from GetInBoatRouteAction
    // on a route planned with EnablePlanningAsBoat: Sim.GetBoatForGetInBoatRouteAction gives an owned usable boat (GetOwnedAndUsableBoat), else for
    // an NPC GetNpcBoat (a BoatTaxi for pets and tourists, else a generated BoatOwnable), else a BoatTaxi. The action seats
    // the rider as the boat's driver and replans the same route with the boat as follower (kind 3). Any Boat subclass
    // (owned sail/speed/fishing boats, water scooters, the taxi) is handled alike below. When that replan finds no path,
    // the route fails and nothing takes the rider out again: every later route of the rider starts as a boat from the same
    // spot (SimRoutingComponent: a Sim whose parent is an IBoat plans with the boat as follower), fails, and EA's route-fail
    // behaviour replans it toward the blocker (radial goal, ~2 s in the candidate phase). Twinbrook: one dog in a taxi all
    // night; next run every sail/speed/fishing boat plan (39, 0.2-0.8 s each, none succeeded).
    // This memo: after a boat plan with an NPC rider fails with no path (blocked by neither a Sim nor a vehicle), the pair
    // (rider, destination: lot or 4 m cell) is remembered for kBoatTaxiMemoHours sim-hours with the boat's spot.
    //   - the rider's own route to that destination plans with the boat off (EnablePlanningAsBoat, the stray rule): walk
    //     or car, or EA's ordinary failure; with the boat leg gone no GetInBoatRouteAction runs, so no boat of any kind.
    //   - a boat plan for that rider and destination from the remembered spot (0.5 m, the stranded case) answers the
    //     recorded no-path failure without searching; no blocker, so the route-fail replan is skipped too.
    // Any occupant household, selected or of unknown scope (IsPlayerSim, never lifted by HouseholdToo: a skip): untouched.
    // Successes are never memoed; an answered plan does not extend its entry (after the hours a real plan proves it again).
    // Counters [1264-1269], see Fixes.cs; log lines "boattaximemo: recorded|answered|boat off <BoatType> ..." with a running
    // per-type hit count (answered + boat off).
    public static partial class Fixes
    {
        struct BtmKey : IEquatable<BtmKey>
        {
            public readonly ulong Rider, Lot;
            public readonly int X, Z;
            public BtmKey(ulong rider, Vector3 dest)
            {
                Rider = rider;
                Lot lot = LotManager.GetLotAtPoint(dest);
                if (lot != null && !lot.IsWorldLot) { Lot = lot.LotId; X = Z = 0; }
                else { Lot = 0; X = (int)Math.Floor(dest.x / 4f); Z = (int)Math.Floor(dest.z / 4f); }
            }
            public bool Equals(BtmKey o) { return Rider == o.Rider && Lot == o.Lot && X == o.X && Z == o.Z; }
            public override bool Equals(object o) { return o is BtmKey && Equals((BtmKey)o); }
            public override int GetHashCode() { return unchecked((int)Rider ^ (int)(Rider >> 32) * 31 ^ (int)Lot * 17 ^ X * 7 ^ Z); }
        }

        struct BtmEntry
        {
            public long From, Until;
            public Vector3 Start;
            public int Ms;
            public string Type;  // the boat's class name, for the hit log
        }

        const int kBtmMax = 128;
        static readonly Dictionary<BtmKey, BtmEntry> sBtm = new Dictionary<BtmKey, BtmEntry>();
        static object sBtmLandRoute;  // the rider route BtmPre turned the boat off for (its result is counted in BtmPost)
        static int sBtmMinute = -1, sBtmLogged;
        static readonly Dictionary<string, int> sBtmHits = new Dictionary<string, int>();

        // one memo hit for this boat type: its running total
        static int BtmHit(string type)
        {
            int n;
            sBtmHits.TryGetValue(type, out n);
            sBtmHits[type] = ++n;
            return n;
        }

        static void BtmLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sBtmMinute) { sBtmMinute = minute; sBtmLogged = 0; }
            if (sBtmLogged++ < 10) DiagLine("boattaximemo: " + s);
        }

        // a live entry for this key, or false (expired entries are dropped here)
        static bool BtmLive(BtmKey k, out BtmEntry e)
        {
            if (!sBtm.TryGetValue(k, out e)) return false;
            long now = SimClock.CurrentTicks;
            if (e.From <= now && now < e.Until) return true;  // From <= now: a clock that went back (another save) drops it
            sBtm.Remove(k);
            return false;
        }

        static void BtmPrune()
        {
            long now = SimClock.CurrentTicks;
            List<BtmKey> old = new List<BtmKey>();
            foreach (KeyValuePair<BtmKey, BtmEntry> kv in sBtm)
                if (kv.Value.From > now || now >= kv.Value.Until) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) sBtm.Remove(old[i]);
            if (sBtm.Count >= kBtmMax) sBtm.Clear();
        }

        // true = some occupant is household, selected or of unknown scope (no occupant at all = unknown)
        static bool BtmBoatExempt(Boat boat)
        {
            Sim[] occ = boat.Occupants;
            if (occ == null || occ.Length == 0) return true;
            for (int i = 0; i < occ.Length; i++)
                if (occ[i] == null || IsPlayerSim(occ[i])) return true;
            return false;
        }

        static bool BtmDest(Vector3 d) { return d != Vector3.Origin && d != Vector3.Invalid; }

        public partial class RouteBackoffHook
        {
            // before the plan (UfPlan). true = r is the answer (stranded boat, proven failing); false = plan as usual
            // (with the boat off when the rider's own route goes where its boat failed)
            bool BtmPre(out RoutePlanResult r, out bool answered)
            {
                r = default(RoutePlanResult);
                answered = false;
                int[] c = PatchCounters();
                try
                {
                    if (sBtm.Count == 0) return false;
                    IScriptProxy f = Follower;
                    object t = f != null ? f.Target : null;
                    Sim sim = t as Sim;
                    BtmEntry e;
                    if (sim != null)
                    {
                        if (sim.Parent is IBoat || !GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat)
                            || GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat) || GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater)) return false;
                        Vector3 d = GetApproximateOriginalDestination();
                        if (!BtmDest(d) || !BtmLive(new BtmKey(sim.ObjectId.Value, d), out e)) return false;
                        if (IsPlayerSim(sim)) { c[1269]++; return false; }
                        SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, false);
                        sBtmLandRoute = this;
                        c[1265]++;
                        BtmLog("boat off " + e.Type + " #" + BtmHit(e.Type) + ", " + sim.FullName + ", dest " + (int)d.x + "," + (int)d.z);
                        return false;
                    }
                    Boat boat = t as Boat;
                    Sim rider = boat != null ? boat.Driver : null;
                    if (rider == null) return false;
                    Vector3 dt = GetApproximateOriginalDestination();
                    if (!BtmDest(dt) || !BtmLive(new BtmKey(rider.ObjectId.Value, dt), out e)) return false;
                    if ((boat.Position - e.Start).LengthSqr() > 0.25f) return false;  // the boat moved: not the proven start
                    if (BtmBoatExempt(boat)) { c[1269]++; return false; }
                    c[1267]++;
                    c[1268] += e.Ms;
                    string type = boat.GetType().Name;
                    BtmLog("answered " + type + " #" + BtmHit(type) + ", " + rider.FullName + ", stands for " + e.Ms + " ms, dest "
                        + (int)dt.x + "," + (int)dt.z);
                    r = FailFast(RoutePlanResultType.FailedBecauseNoPathToDestination);
                    answered = true;
                    return true;
                }
                catch (Exception) { c[1269]++; return false; }
            }

            // after the plan (not after an answered one): count the boat-off route, record a boat's no-path failure
            void BtmPost(RoutePlanResult r, int ms)
            {
                int[] c = PatchCounters();
                try
                {
                    if (sBtmLandRoute != null && ReferenceEquals(sBtmLandRoute, this))
                    {
                        sBtmLandRoute = null;
                        if (r.mType == RoutePlanResultType.Succeeded) c[1266]++;
                        return;
                    }
                    if (r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) return;
                    IScriptProxy f = Follower;
                    Boat boat = f != null ? f.Target as Boat : null;
                    if (boat == null) return;
                    string skip = null;  // why a boat's no-path plan is not recorded (logged: a silent skip hid the s111/s113 misses)
                    Sim rider = boat.Driver;
                    Vector3 d = GetApproximateOriginalDestination();
                    if (r.mBlockerType == RouteBlockerType.Sim || r.mBlockerType == RouteBlockerType.Vehicle) skip = "blocker " + r.mBlockerType
                        + (rider != null && r.mBlockerObjectId == rider.ObjectId.Value ? " (the rider)" : r.mBlockerObjectId == boat.ObjectId.Value ? " (the boat)" : "");
                    else if (rider == null) skip = "no driver (" + (boat.Occupants != null ? boat.Occupants.Length : -1) + " aboard)";
                    else if (BtmBoatExempt(boat)) skip = "exempt rider " + rider.FullName;
                    else if (!BtmDest(d)) skip = "no destination";
                    if (skip != null)
                    {
                        BtmLog("skip " + boat.GetType().Name + ", " + skip + ", plan " + ms + " ms");
                        return;
                    }
                    if (sBtm.Count >= kBtmMax) BtmPrune();
                    long now = SimClock.CurrentTicks;
                    BtmEntry e = new BtmEntry();
                    e.From = now;
                    e.Until = now + SimClock.ConvertToTicks(Instantiator.kBoatTaxiMemoHours, TimeUnit.Hours);
                    e.Start = boat.Position;
                    e.Ms = ms;
                    e.Type = boat.GetType().Name;
                    sBtm[new BtmKey(rider.ObjectId.Value, d)] = e;
                    c[1264]++;
                    BtmLog("recorded " + e.Type + ", " + rider.FullName + ", boat plan " + ms + " ms no path, dest " + (int)d.x + "," + (int)d.z
                        + ", " + Instantiator.kBoatTaxiMemoHours + " sim-h");
                }
                catch (Exception ex) { c[1269]++; BtmLog("error " + ex.GetType().Name); }
            }
        }
    }
}
