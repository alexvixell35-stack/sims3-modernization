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
    // docs/642 boattaximemo (default off). An NPC whose route EA gives a water leg gets a per-trip BoatTaxi when it has no
    // boat of its own (Sim.GetNpcBoat: always for pets and tourists). GetInBoatRouteAction seats the rider as the taxi's
    // driver and replans the same route with the taxi as follower (kind 3). When that replan finds no path, the route
    // fails and nothing takes the rider out again: every later route of the rider starts as a boat from the same spot
    // (SimRoutingComponent: a Sim whose parent is an IBoat plans with the boat as follower), fails, and EA's route-fail
    // behaviour replans it toward the blocker (radial goal, ~2 s in the candidate phase). Twinbrook, one dog, all night.
    // This memo: after a taxi plan with an NPC rider fails with no path (blocked by neither a Sim nor a vehicle), the pair
    // (rider, destination: lot or 4 m cell) is remembered for kBoatTaxiMemoHours sim-hours with the taxi's spot.
    //   - the rider's own route to that destination plans with the boat off (EnablePlanningAsBoat, the stray rule): walk
    //     or car, or EA's ordinary failure; it never boards a taxi that already failed there.
    //   - a taxi plan for that rider and destination from the remembered spot (0.5 m, the stranded case) answers the
    //     recorded no-path failure without searching; no blocker, so the route-fail replan is skipped too.
    // Any occupant household, selected or of unknown scope (IsPlayerSim, never lifted by HouseholdToo: a skip): untouched.
    // Successes are never memoed; an answered plan does not extend its entry (after the hours a real plan proves it again).
    // Counters [3864-3869], see Fixes.cs.
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
        }

        const int kBtmMax = 128;
        static readonly Dictionary<BtmKey, BtmEntry> sBtm = new Dictionary<BtmKey, BtmEntry>();
        static object sBtmLandRoute;  // the rider route BtmPre turned the boat off for (its result is counted in BtmPost)
        static int sBtmMinute = -1, sBtmLogged;

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
        static bool BtmTaxiExempt(BoatTaxi taxi)
        {
            Sim[] occ = taxi.Occupants;
            if (occ == null || occ.Length == 0) return true;
            for (int i = 0; i < occ.Length; i++)
                if (occ[i] == null || IsPlayerSim(occ[i])) return true;
            return false;
        }

        static bool BtmDest(Vector3 d) { return d != Vector3.Origin && d != Vector3.Invalid; }

        public partial class RouteBackoffHook
        {
            // before the plan (UfPlan). true = r is the answer (stranded taxi, proven failing); false = plan as usual
            // (with the boat off when the rider's own route goes where its taxi failed)
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
                        if (IsPlayerSim(sim)) { c[3869]++; return false; }
                        SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, false);
                        sBtmLandRoute = this;
                        c[3865]++;
                        return false;
                    }
                    BoatTaxi taxi = t as BoatTaxi;
                    Sim rider = taxi != null ? taxi.Driver : null;
                    if (rider == null) return false;
                    Vector3 dt = GetApproximateOriginalDestination();
                    if (!BtmDest(dt) || !BtmLive(new BtmKey(rider.ObjectId.Value, dt), out e)) return false;
                    if ((taxi.Position - e.Start).LengthSqr() > 0.25f) return false;  // the taxi moved: not the proven start
                    if (BtmTaxiExempt(taxi)) { c[3869]++; return false; }
                    c[3867]++;
                    c[3868] += e.Ms;
                    r = FailFast(RoutePlanResultType.FailedBecauseNoPathToDestination);
                    answered = true;
                    return true;
                }
                catch (Exception) { c[3869]++; return false; }
            }

            // after the plan (not after an answered one): count the boat-off route, record a taxi's no-path failure
            void BtmPost(RoutePlanResult r, int ms)
            {
                int[] c = PatchCounters();
                try
                {
                    if (sBtmLandRoute != null && ReferenceEquals(sBtmLandRoute, this))
                    {
                        sBtmLandRoute = null;
                        if (r.mType == RoutePlanResultType.Succeeded) c[3866]++;
                        return;
                    }
                    if (r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination
                        || r.mBlockerType == RouteBlockerType.Sim || r.mBlockerType == RouteBlockerType.Vehicle) return;
                    IScriptProxy f = Follower;
                    BoatTaxi taxi = f != null ? f.Target as BoatTaxi : null;
                    Sim rider = taxi != null ? taxi.Driver : null;
                    if (rider == null || BtmTaxiExempt(taxi)) return;
                    Vector3 d = GetApproximateOriginalDestination();
                    if (!BtmDest(d)) return;
                    if (sBtm.Count >= kBtmMax) BtmPrune();
                    long now = SimClock.CurrentTicks;
                    BtmEntry e = new BtmEntry();
                    e.From = now;
                    e.Until = now + SimClock.ConvertToTicks(Instantiator.kBoatTaxiMemoHours, TimeUnit.Hours);
                    e.Start = taxi.Position;
                    e.Ms = ms;
                    sBtm[new BtmKey(rider.ObjectId.Value, d)] = e;
                    c[3864]++;
                    BtmLog("recorded " + rider.FullName + ", taxi plan " + ms + " ms no path, dest " + (int)d.x + "," + (int)d.z
                        + ", " + Instantiator.kBoatTaxiMemoHours + " sim-h");
                }
                catch (Exception) { c[3869]++; }
            }
        }
    }
}
