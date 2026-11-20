using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro for ts3fixes boattaximemo (docs/851, BoatTaxiMemo.cs): an NPC Sim or pet seated as the Driver of a BoatTaxi whose
    // route plan fails with no path, replanned over and over. Each pick gets a taxi the way GetInBoatRouteAction makes one
    // (Vehicle.CreateTaxiBoat, the path GetNpcBoat takes for every pet and tourist), placed on the sea off the shore, the rider
    // seated with PutInDriver; its route is the one SimRoutingComponent builds for a Sim whose parent is an IBoat (CreateRoute:
    // Follower = the boat, EnablePlanningAsBoat, BeginAsBoat), forced the same way when the parent is not set yet. The route goes
    // to the farthest non-world lot (or x/z) with pedestrian planning off (boatOnly, default true) so the boat cannot reach it,
    // then plans `tries` times: Plan, then Replan / ReplanFromPoint (the live kind 3) in turn, all through ScriptCore.Route, so
    // ts3fixes UfPlan -> BtmPre/BtmPost see every one. Expected with boattaximemo on: try 1 recorded [1264], later tries
    // answered [1267] in ~0 ms. NPC picks follow the household rule (known non-active household, not selectable or selected);
    // with no active household every Sim is of unknown scope for ts3fixes (IsPlayerSim) and the memo skips them all ([1269]
    // stays 0, the skip line says "exempt rider"): `active_household` in the result says which case this is.
    public static partial class Handlers
    {
        static void RegisterBoatNoPathRepro()
        {
            Dispatcher.Register("boat_nopath_repro", BoatNoPathRepro);
        }

        class BnpSim { public Sim S; public Boat Boat; public bool Pet; public Vector3 Dest; public string Error, Scope; public bool Seated, ParentBoat; public List<object> Plans = new List<object>(); }

        static int sBnpGen;
        static bool sBnpDone;
        static string sBnpError;
        static List<BnpSim> sBnpSims = new List<BnpSim>();
        static readonly int[] sBnpBase = new int[6];

        // boat_nopath_repro {start: true, sims?: 2, pet?: true, tries?: 4, boatOnly?: true, x?, z?} | {result: true} | {stop: true}
        static object BoatNoPathRepro(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                BnpCleanup();
                int gen = ++sBnpGen, tries = Math.Max(1, (int)a.Long("tries", 4));
                bool boatOnly = a.Bool("boatOnly", true);
                Vector3 dest = a.Has("x") && a.Has("z") ? new Vector3((float)a.Double("x"), 0f, (float)a.Double("z")) : Vector3.Invalid;
                sBnpSims = BnpPick((int)a.Long("sims", 2), a.Bool("pet", true));
                sBnpDone = false; sBnpError = null;
                int[] c = BgsCounters();
                for (int k = 0; k < 6; k++) sBnpBase[k] = c != null && c.Length > 1269 ? c[1264 + k] : 0;
                DiagLine("boat_nopath_repro: start, " + sBnpSims.Count + " riders, tries " + tries + ", boatOnly " + boatOnly + ", boattaximemo " + ModOn("boattaximemo")
                    + ", active household " + (Household.ActiveHousehold != null ? Household.ActiveHousehold.Name : "none"));
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    try { BnpRun(gen, tries, boatOnly, dest); }
                    catch (Exception e) { sBnpError = e.GetType().Name + ": " + e.Message; DiagLine("boat_nopath_repro: error " + sBnpError); }
                    finally { if (gen == sBnpGen) sBnpDone = true; }
                }));
                r["started"] = true;
                r["riders"] = BnpNames();
                return r;
            }
            if (a.Has("stop")) { sBnpGen++; BnpCleanup(); r["stopped"] = true; return r; }
            int[] cc = BgsCounters();
            Dictionary<string, object> ctr = Util.Obj();
            string[] names = { "recorded", "boat_off", "boat_off_ok", "answered", "ms_saved", "exempt_or_error" };
            for (int k = 0; k < 6; k++) ctr[names[k] + "_" + (1264 + k)] = cc != null && cc.Length > 1269 ? cc[1264 + k] - sBnpBase[k] : -1;
            r["done"] = sBnpDone; r["error"] = sBnpError; r["counters"] = ctr; r["boattaximemo"] = ModOn("boattaximemo");
            r["active_household"] = Household.ActiveHousehold != null ? Household.ActiveHousehold.Name : null;
            List<object> rows = new List<object>();
            foreach (BnpSim b in sBnpSims)
            {
                Dictionary<string, object> o = Util.Obj();
                o["sim"] = b.S.FullName; o["pet"] = b.Pet; o["scope"] = b.Scope; o["seated"] = b.Seated; o["parent_is_boat"] = b.ParentBoat;
                o["boat"] = b.Boat != null ? b.Boat.GetType().Name : null; o["driver_is_rider"] = b.Boat != null && b.Boat.Driver == b.S;
                o["dest"] = b.Dest != Vector3.Invalid ? (int)b.Dest.x + "," + (int)b.Dest.z : null; o["error"] = b.Error; o["plans"] = b.Plans;
                rows.Add(o);
            }
            r["riders"] = rows;
            return r;
        }

        // what ts3fixes' IsPlayerSim decides for this Sim (its rule mirrored: the agent cannot call ts3fixes)
        static string BnpScope(Sim s)
        {
            Household act = Household.ActiveHousehold, h = s.SimDescription != null ? s.SimDescription.Household : null;
            if (s == PlumbBob.SelectedActor) return "selected (exempt)";
            if (act == null) return "no active household (unknown scope, exempt)";
            if (h == null) return "no household (unknown scope, exempt)";
            if (h == act || s.IsSelectable) return "household (exempt)";
            return "npc";
        }

        static List<BnpSim> BnpPick(int n, bool pet)
        {
            List<BnpSim> picks = new List<BnpSim>();
            int humans = 0, pets = 0, wantPets = pet ? 1 : 0;
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (humans >= n && pets >= wantPets) break;
                if (!BgsNpc(s) || s.HasBeenDestroyed || !s.InWorld || s.Parent != null || s.Posture is SittingInVehicle || s.Posture is SittingInBoat) continue;
                SimDescription d = s.SimDescription;
                bool isPet = d.IsHorse || d.IsADogSpecies;
                if (isPet) { if (pets >= wantPets || !d.AdultOrAbove) continue; pets++; }
                else { if (humans >= n || !d.IsHuman || !d.TeenOrAbove || d.IsPregnant) continue; humans++; }
                BnpSim b = new BnpSim();
                b.S = s; b.Pet = isPet; b.Dest = Vector3.Invalid; b.Scope = BnpScope(s);
                picks.Add(b);
            }
            if (picks.Count == 0) throw new McpException("boat_nopath_repro: no NPC fits (known non-active household, teen or older human, adult horse or dog)");
            return picks;
        }

        static List<object> BnpNames()
        {
            List<object> l = new List<object>();
            foreach (BnpSim b in sBnpSims) l.Add(b.S.FullName + (b.Pet ? " (pet)" : "") + ", " + b.Scope);
            return l;
        }

        static void BnpRun(int gen, int tries, bool boatOnly, Vector3 destArg)
        {
            Sim anchor = PlumbBob.SelectedActor as Sim ?? sBnpSims[0].S;
            Vector3 shore, dir;
            if (!BgsShore(anchor.Position, out shore, out dir)) throw new McpException("no sea within 400 m of " + anchor.FullName);
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            for (int i = 0; i < sBnpSims.Count && gen == sBnpGen; i++)
            {
                BnpSim b = sBnpSims[i];
                try { BnpOne(b, shore + side * (8f * i), dir, tries, boatOnly, destArg); }
                catch (Exception e) { b.Error = e.GetType().Name + ": " + e.Message; DiagLine("boat_nopath_repro: " + b.S.FullName + " error " + b.Error); }
            }
            int[] c = BgsCounters();
            string s = "";
            for (int k = 0; k < 6; k++) s += (k > 0 ? ", " : "") + "[" + (1264 + k) + "] +" + (c != null && c.Length > 1269 ? c[1264 + k] - sBnpBase[k] : -1);
            DiagLine("boat_nopath_repro: done, counters " + s);
        }

        static void BnpOne(BnpSim b, Vector3 landSpot, Vector3 dir, int tries, bool boatOnly, Vector3 destArg)
        {
            Sim s = b.S;
            // AGENTS.md "Repro Sim setup": cancel, teleport to the shore, motives, autonomy off
            SetupSimsAt(new List<Sim> { s }, null, landSpot, true, true, true, false);
            Simulator.Sleep(2);
            Boat boat = Vehicle.CreateTaxiBoat();
            if (boat == null) throw new McpException("CreateTaxiBoat gave null");
            b.Boat = boat;
            Vector3 pos = landSpot + dir * 14f, fwd = dir;
            if (!GlobalFunctions.FindGoodLocationNearby(boat, ref pos, ref fwd)) { pos = landSpot + dir * 14f; fwd = dir; }
            boat.PlaceAt(pos, fwd, s);  // GetInBoatRouteAction's placement (adds it to the world)
            boat.SimIsGettingIn = true;
            if (boat.Driver == null) boat.PutInDriver(s);
            boat.SimIsGettingIn = false;
            b.Seated = boat.Driver == s;
            b.ParentBoat = s.Parent is IBoat;
            b.Dest = destArg != Vector3.Invalid ? destArg : BnpFarLot(boat.Position);
            if (b.Dest == Vector3.Invalid) throw new McpException("no destination lot");
            b.Dest.y = World.GetTerrainHeight(b.Dest.x, b.Dest.z);
            DiagLine("boat_nopath_repro: " + s.FullName + (b.Pet ? " pet" : "") + " (" + b.Scope + ") driver of " + boat.GetType().Name + " at " + (int)boat.Position.x + "," + (int)boat.Position.z
                + (b.Seated ? "" : " NOT SEATED") + (b.ParentBoat ? "" : ", parent not the boat") + ", dest " + (int)b.Dest.x + "," + (int)b.Dest.z);
            Route route = s.CreateRoute();  // SetRouteOptions: Parent is IBoat -> Follower = boat, EnablePlanningAsBoat, BeginAsBoat
            // GetInBoatRouteAction's settings, in case the parent is not the boat yet
            route.Follower = boat.Proxy;
            route.FollowerAgeGenderSpecies = (uint)boat.GetBoatSpecies();
            route.SetOption2(Route.RouteOption2.EnablePlanningAsBoat, true);
            route.SetOption2(Route.RouteOption2.BeginAsBoat, true);
            route.SetOption2(Route.RouteOption2.UseFollowerStartOrientation, boat.UsesTurnHelperFootprint());
            route.SetOption(Route.RouteOption.RouteAsGhost, false);
            if (boatOnly) route.SetOption(Route.RouteOption.DisablePlanningAsPedestrian, true);
            for (int t = 0; t < tries; t++)
            {
                string kind = t == 0 ? "Plan" : t % 2 == 1 ? "Replan" : "ReplanFromPoint";
                int t0 = System.Environment.TickCount;
                RoutePlanResult res = t == 0 ? route.PlanToPoint(b.Dest) : t % 2 == 1 ? route.Replan() : route.ReplanFromPoint(boat.Position);
                int ms = unchecked(System.Environment.TickCount - t0);
                Dictionary<string, object> o = Util.Obj();
                o["try"] = t + 1; o["kind"] = kind; o["result"] = res.mType.ToString(); o["ms"] = ms; o["blocker"] = res.mBlockerType.ToString();
                o["blocker_is"] = res.mBlockerObjectId == 0 ? "none" : res.mBlockerObjectId == boat.ObjectId.Value ? "the boat" : res.mBlockerObjectId == s.ObjectId.Value ? "the rider" : res.mBlockerObjectId.ToString("X");
                b.Plans.Add(o);
                DiagLine("boat_nopath_repro: " + s.FullName + " try " + (t + 1) + " " + kind + " " + res.mType + " " + ms + " ms, blocker " + res.mBlockerType + " (" + o["blocker_is"] + ")");
                Simulator.Sleep(1);
            }
        }

        // the center of the non-world lot farthest from p (inland, across the town)
        static Vector3 BnpFarLot(Vector3 p)
        {
            Vector3 best = Vector3.Invalid;
            float bd = -1f;
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || lot.IsWorldLot) continue;
                Vector3 c = lot.GetCenterPosition();
                float d = (c - p).LengthSqr();
                if (d > bd) { bd = d; best = c; }
            }
            return best;
        }

        static void BnpCleanup()
        {
            foreach (BnpSim b in sBnpSims)
                try
                {
                    if (b.Boat != null && !b.Boat.HasBeenDestroyed && b.Boat.Driver == b.S) b.S.SetObjectToReset();  // the reset takes it out (taxi: destroyed on release)
                    else if (b.Boat != null && !b.Boat.HasBeenDestroyed && !b.Boat.HasAnyOccupants()) b.Boat.Destroy();
                }
                catch (Exception) { }
            sBnpSims = new List<BnpSim>();
        }
    }
}
