using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Agent
{
    // Repro for ts3fixes houseboatlegmemo (docs/642 §6, HouseboatLegMemo.cs): EA's houseboat boat leg that finds no path, planned
    // again after every plan of the Sim's route home. Chain [verified, decompiled]: ScriptCore Route.Plan* -> PostPlanCallback ->
    // SimRoutingComponent.PostPlanRouteCallback -> CheckAndUpdateForHouseboats (start on the world lot, goal on a houseboat lot)
    // -> EnterHouseboatThroughBoat when the houseboat is not Docked (CanUseGangway false) and the Sim is not swimming at a
    // StationaryAtSea houseboat (the ladder branch) and the Sim stands > kBoatToHouseboatMaxRadius (15 m) from RoutingSlot_0:
    // a new route of the Sim, EnablePlanningAsBoat + EndAsBoat + DestinationMustBeInDeepWater (+ DisablePlanningOnLand when the
    // Sim and the slot are in the same body of water), a 5-15 m radial goal off the slot, PlanFromPoint(start) = ts3fixes kind 2.
    // Picks: NPCs as setup_sims (known non-active household, not selectable or selected), houseboat residents first; houseboats
    // that are not Docked, the Sim's own first. Placement: candidate spots 17 m off RoutingSlot_0 (sea first, then land on the
    // world lot), each probed with EA's exact leg (built here as EnterHouseboatThroughBoat builds it) with houseboatlegmemo forced
    // off for the probe only; the first spot whose leg finds no path is used (else the first candidate, flagged "probe never
    // failed"). Then `tries` plans of the Sim's own route to the houseboat (Plan, then ReplanFromPoint / Plan in turn), each through
    // ScriptCore.Route, so the real EA chain runs the leg each time. Per try: the outer kind, result and ms, the leg(s) the
    // callbacks saw (options, result, ms; an answered leg makes no callback) and the 3870-3873 deltas of that try.
    // That is mode "undock" (the boat leg). The default mode "docked" (docs/642 §7, houseboatgangwayrescue) repros the case seen in
    // play: an NPC owner of a DOCKED houseboat (port gangway, jig) left on shore ~17 m (15-22 m) from the gangway's RoutingSlot_0
    // at a world-lot land spot connected by land to the houseboat (so EA turns water and boat planning off) whose test plan to
    // the slot, built as EnterHouseboatThroughGangway builds it (PlanToPointFromPoint, water and boat planning off), finds no path;
    // then a GoHome is pushed (High priority, autonomous) and the result polls for `waitS` seconds: whether the Sim reached its
    // houseboat lot, GoHome still queued, the legs the callbacks saw, the 3870-3873 and 4040-4049 deltas.
    public static partial class Handlers
    {
        static void RegisterHouseboatLegRepro()
        {
            Dispatcher.Register("houseboat_leg_repro", HouseboatLegRepro);
        }

        class HlrSim { public Sim S; public Houseboat Hb; public string Scope, Error, HbName, HbStatus; public bool Resident; public Vector3 Spot = Vector3.Invalid;
            public List<object> Probes = new List<object>(), Plans = new List<object>(); public bool ProbeFailed;
            // docked mode: the GoHome outcome
            public bool Pushed, Home; public int HomeMs = -1; public string LotAfter; public float GangwayM = -1f; }

        static int sHlrGen;
        static bool sHlrDone;
        static string sHlrError;
        static List<HlrSim> sHlrSims = new List<HlrSim>();
        static readonly int[] sHlrBase = new int[4];
        // the leg plans the callbacks see during one try (filled by HlrAbout / HlrPost)
        static Sim sHlrWatch;
        static Route sHlrOuter;
        static List<object> sHlrLegs;
        static readonly Dictionary<Route, int> sHlrT0 = new Dictionary<Route, int>();

        // undock (default on): with every houseboat Docked, one whose residents are all NPCs (never the active household's) leaves
        // its port with EA's own Houseboat.LeavePort (the first step of every Helm move: MoveToPort, GoCruising, AbandonPort,
        // RouteToSlot): the dock footprint, a boat route to a 0-10 m radial goal off the gangway's lead slot, DoRoute, which ends
        // StationaryAtSea [verified, decompiled Houseboat.cs LeavePort / DoRoute]. Driver null (no Sim at the helm, no route-fail
        // animation). Not moved back: the test session is never saved.
        static Houseboat sHlrUndock;
        static Dictionary<string, object> sHlrUndockInfo;

        static int HlrCtr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 3873 ? c[3870 + k] : -1; }
        // houseboatgangwayrescue 4040-4049 (docs/642 §7)
        static readonly string[] kHlrGr = { "marked", "rescued", "refused", "errors", "refused_exempt", "refused_houseboat", "refused_hour", "refused_busy", "refused_lot_far_stale", "refused_nospot" };
        static readonly int[] sHlrGrBase = new int[10];
        static int HlrGr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 4049 ? c[4040 + k] : -1; }
        static string sHlrMode = "docked";
        static int sHlrWaitS = 90;

        // houseboat_leg_repro {start: true, mode?: "docked" | "undock", sims?: 2, tries?: 4 (undock), undock?: true (undock mode),
        //   waitS?: 90 (docked: seconds to watch the GoHome)} | {result: true} | {stop: true}
        static object HouseboatLegRepro(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                sHlrGen++;
                int gen = sHlrGen, tries = Math.Max(1, (int)a.Long("tries", 4));
                sHlrMode = a.Str("mode", "docked");
                if (sHlrMode != "docked" && sHlrMode != "undock") throw new McpException("houseboat_leg_repro: mode is docked (default) or undock, not " + sHlrMode);
                sHlrWaitS = Math.Max(5, (int)a.Long("waitS", 90));
                sHlrUndock = null; sHlrUndockInfo = null;
                sHlrSims = sHlrMode == "docked" ? HlrPickDocked((int)a.Long("sims", 2)) : HlrPick((int)a.Long("sims", 2), a.Bool("undock", true));
                sHlrDone = false; sHlrError = null;
                for (int k = 0; k < 4; k++) sHlrBase[k] = HlrCtr(k);
                for (int k = 0; k < 10; k++) sHlrGrBase[k] = HlrGr(k);
                DiagLine("houseboat_leg_repro: start, mode " + sHlrMode + ", " + sHlrSims.Count + " sims, tries " + tries + ", houseboatlegmemo " + ModOn("houseboatlegmemo")
                    + ", houseboatgangwayrescue " + ModOn("houseboatgangwayrescue")
                    + ", active household " + (Household.ActiveHousehold != null ? Household.ActiveHousehold.Name : "none"));
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    try { if (sHlrMode == "docked") HlrRunDocked(gen); else HlrRun(gen, tries); }
                    catch (Exception e) { sHlrError = e.GetType().Name + ": " + e.Message; DiagLine("houseboat_leg_repro: error " + sHlrError); }
                    finally { if (gen == sHlrGen) sHlrDone = true; }
                }));
                r["started"] = true;
                List<object> names = new List<object>();
                foreach (HlrSim h in sHlrSims) names.Add(h.S.FullName + ", " + h.Scope + ", houseboat " + h.HbName + " (" + h.HbStatus + (h.Resident ? ", home" : "") + ")");
                r["sims"] = names;
                if (sHlrUndock != null) r["undock"] = "pending: " + HlrHbName(sHlrUndock) + " (" + sHlrUndock.CurrentStatus + ")";
                return r;
            }
            if (a.Has("stop")) { sHlrGen++; HlrUnhook(); r["stopped"] = true; return r; }
            Dictionary<string, object> ctr = Util.Obj();
            string[] cn = { "recorded", "answered", "answered_ms", "exempt_or_error" };
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); ctr[cn[k] + "_" + (3870 + k)] = v < 0 ? -1 : v - sHlrBase[k]; }
            for (int k = 0; k < 10; k++) { int v = HlrGr(k); ctr[kHlrGr[k] + "_" + (4040 + k)] = v < 0 ? -1 : v - sHlrGrBase[k]; }
            r["done"] = sHlrDone; r["error"] = sHlrError; r["counters"] = ctr; r["houseboatlegmemo"] = ModOn("houseboatlegmemo");
            r["houseboatgangwayrescue"] = ModOn("houseboatgangwayrescue"); r["mode"] = sHlrMode;
            r["active_household"] = Household.ActiveHousehold != null ? Household.ActiveHousehold.Name : null;
            List<object> rows = new List<object>();
            foreach (HlrSim h in sHlrSims)
            {
                Dictionary<string, object> o = Util.Obj();
                o["sim"] = h.S.FullName; o["scope"] = h.Scope; o["houseboat"] = h.HbName; o["houseboat_status"] = h.HbStatus; o["resident"] = h.Resident;
                o["spot"] = h.Spot != Vector3.Invalid ? Math.Round(h.Spot.x, 1) + "," + Math.Round(h.Spot.z, 1) : null;
                o["probe_failed"] = h.ProbeFailed; o["probes"] = h.Probes; o["plans"] = h.Plans; o["error"] = h.Error;
                if (sHlrMode == "docked")
                {
                    o["gangway_m"] = Math.Round(h.GangwayM, 1); o["gohome_pushed"] = h.Pushed; o["reached_home"] = h.Home; o["home_ms"] = h.HomeMs;
                    o["lot_after"] = h.LotAfter ?? (h.S.LotCurrent != null ? h.S.LotCurrent.Name : null);
                }
                rows.Add(o);
            }
            r["sims"] = rows;
            r["undock"] = sHlrUndockInfo;
            return r;
        }

        static List<Houseboat> HlrHouseboats()
        {
            List<Houseboat> l = new List<Houseboat>();
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || !lot.IsHouseboatLot()) continue;
                Houseboat hb = lot.GetHouseboat();
                if (hb != null && hb.GetHouseboatJig() != null) l.Add(hb);
            }
            return l;
        }

        static string HlrHbName(Houseboat hb) { Lot l = hb.GetHouseboatLot(); return l != null ? l.Name : "?"; }

        // a Docked houseboat whose residents are all NPCs (no active-household, selectable or selected member), with a port gangway
        // (LeavePort needs it to move); residents among the NPC picks first, then any NPC-owned one, then an empty one
        static Houseboat HlrUndockPick(List<Houseboat> all, List<Sim> npcs)
        {
            Houseboat best = null;
            int bestRank = -1;
            foreach (Houseboat hb in all)
            {
                if (hb.CurrentStatus != Houseboat.Status.Docked || hb.GetPortGangway() == null) continue;
                Lot lot = hb.GetHouseboatLot();
                Household h = lot != null ? lot.Household : null;
                if (h != null && h == Household.ActiveHousehold) continue;
                bool npcOnly = true, picked = false;
                if (h != null)
                    foreach (SimDescription d in h.AllSimDescriptions)
                    {
                        Sim s = d != null ? d.CreatedSim : null;
                        if (d == null || (s != null && (s.IsSelectable || s == PlumbBob.SelectedActor))) { npcOnly = false; break; }
                        if (s != null && npcs.Contains(s)) picked = true;
                    }
                if (!npcOnly) continue;
                int rank = picked ? 2 : h != null ? 1 : 0;
                if (rank > bestRank) { bestRank = rank; best = hb; }
            }
            return best;
        }

        static List<HlrSim> HlrPick(int n, bool undock)
        {
            List<Houseboat> all = HlrHouseboats(), sea = new List<Houseboat>();
            string seen = "";
            foreach (Houseboat hb in all)
            {
                seen += (seen.Length > 0 ? ", " : "") + HlrHbName(hb) + " " + hb.CurrentStatus;
                if (hb.CurrentStatus != Houseboat.Status.Docked) sea.Add(hb);
            }
            List<Sim> npcs = new List<Sim>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (!BgsNpc(s) || s.HasBeenDestroyed || !s.InWorld || s.Parent != null) continue;
                SimDescription d = s.SimDescription;
                if (d == null || !d.IsHuman || !d.TeenOrAbove || d.IsPregnant) continue;
                npcs.Add(s);
            }
            if (sea.Count == 0 && undock)
            {
                sHlrUndock = HlrUndockPick(all, npcs);
                if (sHlrUndock != null) sea.Add(sHlrUndock);
            }
            if (sea.Count == 0) throw new McpException("houseboat_leg_repro: no houseboat away from its dock (a Docked one takes the gangway, not the boat leg)"
                + (undock ? " and none to undock (all NPC residents, a port gangway)" : ", undock off") + "; houseboats: " + (seen.Length > 0 ? seen : "none"));
            // residents of a houseboat at sea first, then any NPC with the next houseboat at sea
            List<HlrSim> picks = new List<HlrSim>();
            for (int pass = 0; pass < 2 && picks.Count < n; pass++)
                foreach (Sim s in npcs)
                {
                    if (picks.Count >= n) break;
                    bool taken = false;
                    foreach (HlrSim p in picks) if (p.S == s) taken = true;
                    if (taken) continue;
                    Lot home = s.LotHome;
                    Houseboat own = home != null && home.IsHouseboatLot() ? home.GetHouseboat() : null;
                    bool resident = own != null && sea.Contains(own);
                    if (pass == 0 && !resident) continue;
                    HlrSim h = new HlrSim();
                    h.S = s; h.Resident = resident; h.Hb = resident ? own : sea[picks.Count % sea.Count];
                    h.HbName = h.Hb.GetHouseboatLot() != null ? h.Hb.GetHouseboatLot().Name : "?"; h.HbStatus = h.Hb.CurrentStatus.ToString();
                    h.Scope = BnpScope(s);
                    picks.Add(h);
                }
            if (picks.Count == 0) throw new McpException("houseboat_leg_repro: no NPC fits (known non-active household, not selectable or selected, teen or older human)");
            return picks;
        }

        static void HlrAbout(Route r, string routeType, Vector3 point)
        {
            if (sHlrWatch == null || r == null || r == sHlrOuter || r.Follower == null || r.Follower.Target != sHlrWatch) return;
            sHlrT0[r] = System.Environment.TickCount;
        }

        static void HlrPost(Route r, string routeType, string result)
        {
            if (sHlrWatch == null || sHlrLegs == null || r == null || r == sHlrOuter || r.Follower == null || r.Follower.Target != sHlrWatch) return;
            int t0;
            int ms = sHlrT0.TryGetValue(r, out t0) ? unchecked(System.Environment.TickCount - t0) : -1;
            sHlrT0.Remove(r);
            Dictionary<string, object> o = Util.Obj();
            o["type"] = routeType; o["result"] = r.PlanResult.mType.ToString(); o["ms"] = ms;
            o["end_as_boat"] = r.GetOption2(Route.RouteOption2.EndAsBoat);
            o["deep_water"] = r.GetOption2(Route.RouteOption2.DestinationMustBeInDeepWater);
            o["no_land"] = r.GetOption2(Route.RouteOption2.DisablePlanningOnLand);
            o["boat"] = r.GetOption2(Route.RouteOption2.EnablePlanningAsBoat);  // off with water off too = the gangway leg (docked mode)
            o["water"] = r.GetOption(Route.RouteOption.EnableWaterPlanning);
            if (sHlrLegs.Count < 200) sHlrLegs.Add(o);
        }

        static void HlrHook()
        {
            Route.AboutToPlanCallback = (Route.AboutToPlanDelegate)Delegate.Combine(Route.AboutToPlanCallback, new Route.AboutToPlanDelegate(HlrAbout));
            Route.PostPlanCallback = (Route.PostPlanDelegate)Delegate.Combine(Route.PostPlanCallback, new Route.PostPlanDelegate(HlrPost));
        }

        static void HlrUnhook()
        {
            Route.AboutToPlanCallback = (Route.AboutToPlanDelegate)Delegate.Remove(Route.AboutToPlanCallback, new Route.AboutToPlanDelegate(HlrAbout));
            Route.PostPlanCallback = (Route.PostPlanDelegate)Delegate.Remove(Route.PostPlanCallback, new Route.PostPlanDelegate(HlrPost));
            sHlrWatch = null; sHlrOuter = null; sHlrLegs = null; sHlrT0.Clear();
        }

        // EnterHouseboatThroughBoat's leg, as EA builds it for routingObject s (standing at its start already)
        static RoutePlanResult HlrLeg(Sim s, Houseboat hb, Vector3 start)
        {
            Route route = s.CreateRoute();
            Vector3 slot = hb.GetPositionOfSlot(Slot.RoutingSlot_0);
            route.SetOption(Route.RouteOption.MakeDynamicObjectAdjustments, true);
            route.SetOption(Route.RouteOption.DoNotEmitDegenerateRoutesForRadialRangeGoals, true);
            route.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, false);
            route.SetOption(Route.RouteOption.CheckForFootprintsNearGoals, true);
            route.SetOption2(Route.RouteOption2.EnablePlanningAsBoat, true);
            route.SetOption2(Route.RouteOption2.EndAsBoat, true);
            route.SetOption2(Route.RouteOption2.DestinationMustBeInDeepWater, true);
            route.SetOption2(Route.RouteOption2.DestinationMustBeOnLand, false);
            if (World.ArePointsInSameBodyOfWater(s.Position, slot)) route.SetOption2(Route.RouteOption2.DisablePlanningOnLand, true);
            RadialRangeDestination d = new RadialRangeDestination();
            d.mCenterPoint = slot;
            d.mfMinRadius = Houseboat.kBoatToHouseboatMinRadius;
            d.mfMaxRadius = Houseboat.kBoatToHouseboatMaxRadius;
            d.mfPreferredSpacing = 0.15f;
            d.ScoreFunctionWeights[2] = 1f;
            d.mConeVector = -hb.GetForwardOfSlot(Slot.RoutingSlot_0);
            d.mfConeAngle = (float)Math.PI / 2f;
            route.AddDestination(d);
            return route.PlanFromPoint(start);
        }

        static void HlrPlace(Sim s, Vector3 p)
        {
            s.InteractionQueue.CancelAllInteractions();
            s.SetPosition(p);
            if (s.Posture != s.Standing) s.Posture = s.Standing;
        }

        // candidate starts 17 m off the slot on the world lot: the sea (the leg then also has DisablePlanningOnLand), then land
        static List<Vector3> HlrCandidates(Houseboat hb)
        {
            Vector3 slot = hb.GetPositionOfSlot(Slot.RoutingSlot_0);
            List<Vector3> sea = new List<Vector3>(), land = new List<Vector3>();
            for (float rad = 17f; rad <= 30f; rad += 6.5f)
                for (int i = 0; i < 16; i++)
                {
                    double ang = i * Math.PI / 8;
                    Vector3 p = new Vector3(slot.x + rad * (float)Math.Cos(ang), 0f, slot.z + rad * (float)Math.Sin(ang));
                    Lot lot = LotManager.GetLotAtPoint(p);
                    if (lot != null && !lot.IsWorldLot) continue;  // CheckAndUpdateForHouseboats needs the start on the world lot
                    bool isSea = (TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea;
                    p.y = isSea ? Math.Max(World.GetSeaLevel(), World.GetTerrainHeight(p.x, p.z)) : World.GetTerrainHeight(p.x, p.z);
                    if (isSea && World.ArePointsInSameBodyOfWater(p, slot)) sea.Add(p);
                    else if (!isSea) land.Add(p);
                }
            List<Vector3> all = new List<Vector3>();
            for (int i = 0; i < sea.Count && i < 8; i++) all.Add(sea[i]);
            for (int i = 0; i < land.Count && i < 8; i++) all.Add(land[i]);
            return all;
        }

        static void HlrUndockRun()
        {
            Houseboat hb = sHlrUndock;
            Dictionary<string, object> o = Util.Obj();
            sHlrUndockInfo = o;
            Vector3 p0 = hb.Position;
            o["houseboat"] = HlrHbName(hb); o["status_before"] = hb.CurrentStatus.ToString(); o["at_before"] = (int)p0.x + "," + (int)p0.z;
            Lot lot = hb.GetHouseboatLot();
            o["household"] = lot != null && lot.Household != null ? lot.Household.Name : null;
            int t0 = System.Environment.TickCount;
            bool ok = false;
            try { ok = hb.LeavePort(null); }
            catch (Exception e) { o["error"] = e.GetType().Name + ": " + e.Message; }
            Vector3 p1 = hb.Position;
            o["leave_port"] = ok; o["ms"] = unchecked(System.Environment.TickCount - t0);
            o["status_after"] = hb.CurrentStatus.ToString(); o["at_after"] = (int)p1.x + "," + (int)p1.z; o["moved_m"] = Math.Round((p1 - p0).Length(), 1);
            DiagLine("houseboat_leg_repro: undock " + o["houseboat"] + " (household " + (o["household"] ?? "none") + ") LeavePort " + ok + " in " + o["ms"] + " ms, "
                + o["status_before"] + " -> " + o["status_after"] + ", moved " + o["moved_m"] + " m" + (o.ContainsKey("error") ? ", error " + o["error"] : ""));
            foreach (HlrSim h in sHlrSims) if (h.Hb == hb) h.HbStatus = hb.CurrentStatus.ToString();
            if (hb.CurrentStatus == Houseboat.Status.Docked) throw new McpException("undock: " + o["houseboat"] + " still Docked after LeavePort (" + ok + ")");
        }

        // docked mode: NPCs (as setup_sims picks them) whose home is a Docked houseboat with a port gangway and a jig
        static List<HlrSim> HlrPickDocked(int n)
        {
            string seen = "";
            foreach (Houseboat hb in HlrHouseboats()) seen += (seen.Length > 0 ? ", " : "") + HlrHbName(hb) + " " + hb.CurrentStatus;
            List<HlrSim> picks = new List<HlrSim>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (picks.Count >= n) break;
                if (!BgsNpc(s) || s.HasBeenDestroyed || !s.InWorld || s.Parent != null) continue;
                SimDescription d = s.SimDescription;
                if (d == null || !d.IsHuman || !d.TeenOrAbove || d.IsPregnant) continue;
                Lot home = s.LotHome;
                Houseboat hb = home != null && home.IsHouseboatLot() ? home.GetHouseboat() : null;
                if (hb == null || hb.CurrentStatus != Houseboat.Status.Docked || hb.GetPortGangway() == null || hb.GetHouseboatJig() == null) continue;
                HlrSim h = new HlrSim();
                h.S = s; h.Resident = true; h.Hb = hb; h.HbName = HlrHbName(hb); h.HbStatus = hb.CurrentStatus.ToString(); h.Scope = BnpScope(s);
                picks.Add(h);
            }
            if (picks.Count == 0) throw new McpException("houseboat_leg_repro docked: no NPC (known non-active household, teen or older human) lives on a Docked houseboat with a port gangway; houseboats: "
                + (seen.Length > 0 ? seen : "none") + " (mode undock repros the boat leg instead)");
            return picks;
        }

        // land spots on the world lot 15-22 m off the gangway's RoutingSlot_0 (17 m ring first), connected by land to the houseboat
        static List<Vector3> HlrShoreCandidates(Houseboat hb, Vector3 slot)
        {
            List<Vector3> all = new List<Vector3>();
            float[] radii = { 17f, 15f, 19f, 22f };
            foreach (float rad in radii)
                for (int i = 0; i < 16 && all.Count < 48; i++)
                {
                    double ang = i * Math.PI / 8;
                    Vector3 p = new Vector3(slot.x + rad * (float)Math.Cos(ang), 0f, slot.z + rad * (float)Math.Sin(ang));
                    Lot lot = LotManager.GetLotAtPoint(p);
                    if (lot != null && !lot.IsWorldLot) continue;
                    if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) continue;
                    p.y = World.GetTerrainHeight(p.x, p.z);
                    if (!Terrain.ArePointsConnectedByLand(p, hb.Position)) continue;  // else EA leaves water planning on
                    all.Add(p);
                }
            return all;
        }

        static void HlrRunDocked(int gen)
        {
            HlrHook();
            try
            {
                for (int i = 0; i < sHlrSims.Count && gen == sHlrGen; i++)
                {
                    HlrSim h = sHlrSims[i];
                    try { HlrDockedPlace(h); }
                    catch (Exception e) { h.Error = e.GetType().Name + ": " + e.Message; DiagLine("houseboat_leg_repro: " + h.S.FullName + " error " + h.Error); }
                }
                // watch every pushed GoHome at once; legs of all picked Sims go to the first Sim's list (one watched Sim at a time)
                int t0 = System.Environment.TickCount, left = 0;
                foreach (HlrSim h in sHlrSims) if (h.Pushed) left++;
                if (sHlrSims.Count > 0) { sHlrWatch = sHlrSims[0].S; sHlrOuter = null; sHlrLegs = sHlrSims[0].Plans; }
                while (left > 0 && gen == sHlrGen && unchecked(System.Environment.TickCount - t0) < sHlrWaitS * 1000)
                {
                    Simulator.Sleep(10);
                    foreach (HlrSim h in sHlrSims)
                    {
                        if (!h.Pushed || h.Home) continue;
                        Lot hl = h.Hb.GetHouseboatLot();
                        if (hl != null && h.S.LotCurrent != null && h.S.LotCurrent.LotId == hl.LotId)
                        {
                            h.Home = true; h.HomeMs = unchecked(System.Environment.TickCount - t0); h.LotAfter = hl.Name; left--;
                            DiagLine("houseboat_leg_repro: " + h.S.FullName + " on " + hl.Name + " after " + h.HomeMs + " ms");
                        }
                    }
                }
                foreach (HlrSim h in sHlrSims)
                {
                    if (!h.Pushed) continue;
                    if (!h.Home) h.LotAfter = h.S.LotCurrent != null ? h.S.LotCurrent.Name : "none";
                    bool queued = false;
                    foreach (InteractionInstance ii in h.S.InteractionQueue.InteractionList) if (ii is GoHome) queued = true;
                    DiagLine("houseboat_leg_repro: " + h.S.FullName + (h.Home ? " reached home in " + h.HomeMs + " ms" : " NOT home after " + sHlrWaitS + " s, on " + h.LotAfter)
                        + ", GoHome queued " + queued);
                }
            }
            finally { HlrUnhook(); }
            string s = "";
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); s += (k > 0 ? ", " : "") + "[" + (3870 + k) + "] +" + (v < 0 ? -1 : v - sHlrBase[k]); }
            for (int k = 0; k < 10; k++) { int v = HlrGr(k); s += ", [" + (4040 + k) + "] +" + (v < 0 ? -1 : v - sHlrGrBase[k]); }
            DiagLine("houseboat_leg_repro: done (docked), counters " + s);
        }

        // probe shore spots with EnterHouseboatThroughGangway's leg (no probe is planned inside CheckAndUpdateForHouseboats, so
        // neither houseboatlegmemo nor the rescue sees it), stand the Sim on the first no-path spot, push GoHome
        static void HlrDockedPlace(HlrSim h)
        {
            Sim s = h.S;
            SetupSimsAt(new List<Sim> { s }, null, s.Position, false, true, true, false);  // cancel, motives, autonomy off; no teleport
            Vector3 slot = h.Hb.GetPortGangway().GetPositionOfSlot(Slot.RoutingSlot_0);
            List<Vector3> cands = HlrShoreCandidates(h.Hb, slot);
            if (cands.Count == 0) throw new McpException("no world-lot land spot 15-22 m off " + h.HbName + "'s gangway connected by land");
            foreach (Vector3 c in cands)
            {
                Route route = s.CreateRoute();
                route.SetOption(Route.RouteOption.EnableWaterPlanning, false);
                route.SetOption2(Route.RouteOption2.EnablePlanningAsBoat, false);
                int t0 = System.Environment.TickCount;
                RoutePlanResult res = route.PlanToPointFromPoint(c, slot);
                int ms = unchecked(System.Environment.TickCount - t0);
                Dictionary<string, object> o = Util.Obj();
                o["spot"] = Math.Round(c.x, 1) + "," + Math.Round(c.z, 1); o["m"] = Math.Round((c - slot).Length(), 1); o["result"] = res.mType.ToString(); o["ms"] = ms;
                o["blocker"] = res.mBlockerType.ToString();
                h.Probes.Add(o);
                DiagLine("houseboat_leg_repro: " + s.FullName + " gangway probe " + o["spot"] + " (" + o["m"] + " m) " + res.mType + " " + ms + " ms, blocker " + res.mBlockerType);
                if (res.mType == RoutePlanResultType.FailedBecauseNoPathToDestination && res.mBlockerType != RouteBlockerType.Sim && res.mBlockerType != RouteBlockerType.Vehicle)
                { h.Spot = c; h.ProbeFailed = true; break; }
                Simulator.Sleep(0);
            }
            if (h.Spot == Vector3.Invalid) h.Spot = cands[0];
            HlrPlace(s, h.Spot);
            Simulator.Sleep(1);
            h.GangwayM = (s.Position - slot).Length();
            InteractionInstance gh = GoHome.Singleton.CreateInstance(s.LotHome, s, new InteractionPriority(InteractionPriorityLevel.High), true, true);
            h.Pushed = gh != null && s.InteractionQueue.Add(gh);
            DiagLine("houseboat_leg_repro: " + s.FullName + " (" + h.Scope + ") on shore at " + (int)h.Spot.x + "," + (int)h.Spot.z + ", " + Math.Round(h.GangwayM, 1)
                + " m from " + h.HbName + "'s gangway" + (h.ProbeFailed ? " (probe: no path)" : " (probe never failed)") + ", GoHome pushed " + h.Pushed);
        }

        static void HlrRun(int gen, int tries)
        {
            if (sHlrUndock != null) HlrUndockRun();
            HlrHook();
            try
            {
                for (int i = 0; i < sHlrSims.Count && gen == sHlrGen; i++)
                {
                    HlrSim h = sHlrSims[i];
                    try { HlrOne(h, tries, gen); }
                    catch (Exception e) { h.Error = e.GetType().Name + ": " + e.Message; DiagLine("houseboat_leg_repro: " + h.S.FullName + " error " + h.Error); }
                }
            }
            finally { HlrUnhook(); }
            string s = "";
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); s += (k > 0 ? ", " : "") + "[" + (3870 + k) + "] +" + (v < 0 ? -1 : v - sHlrBase[k]); }
            DiagLine("houseboat_leg_repro: done, counters " + s);
        }

        // the switch forced off for a probe (the probe must not record the spot); restored to what it was
        static void HlrProbeSwitch(bool off, ref int prior)
        {
            AppDomain dom = AppDomain.CurrentDomain;
            Dictionary<string, bool> sw = dom.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { sw = new Dictionary<string, bool>(); dom.SetData("ts3fixes.switches", sw); }
            bool v;
            if (off) { prior = sw.TryGetValue("houseboatlegmemo", out v) ? (v ? 1 : 0) : -1; sw["houseboatlegmemo"] = false; }
            else if (prior < 0) sw.Remove("houseboatlegmemo");
            else sw["houseboatlegmemo"] = prior == 1;
            Util.SwitchesChanged();
        }

        static void HlrOne(HlrSim h, int tries, int gen)
        {
            Sim s = h.S;
            SetupSimsAt(new List<Sim> { s }, null, s.Position, false, true, true, false);  // cancel, motives, autonomy off; no teleport
            List<Vector3> cands = HlrCandidates(h.Hb);
            if (cands.Count == 0) throw new McpException("no world-lot spot 17-30 m off " + h.HbName + "'s RoutingSlot_0");
            int prior = -1;
            HlrProbeSwitch(true, ref prior);
            try
            {
                foreach (Vector3 c in cands)
                {
                    if (gen != sHlrGen) return;
                    HlrPlace(s, c);
                    Simulator.Sleep(1);
                    int t0 = System.Environment.TickCount;
                    RoutePlanResult res = HlrLeg(s, h.Hb, s.Position);
                    int ms = unchecked(System.Environment.TickCount - t0);
                    bool sea = (TerrainType)World.GetTerrainType(c.x, c.z, 0) == TerrainType.WorldSea;
                    Dictionary<string, object> o = Util.Obj();
                    o["spot"] = Math.Round(c.x, 1) + "," + Math.Round(c.z, 1); o["sea"] = sea; o["result"] = res.mType.ToString(); o["ms"] = ms;
                    o["blocker"] = res.mBlockerType.ToString();
                    h.Probes.Add(o);
                    DiagLine("houseboat_leg_repro: " + s.FullName + " probe " + o["spot"] + (sea ? " sea" : " land") + " " + res.mType + " " + ms + " ms, blocker " + res.mBlockerType);
                    if (res.mType == RoutePlanResultType.FailedBecauseNoPathToDestination && res.mBlockerType != RouteBlockerType.Sim && res.mBlockerType != RouteBlockerType.Vehicle)
                    { h.Spot = c; h.ProbeFailed = true; break; }
                }
            }
            finally { HlrProbeSwitch(false, ref prior); }
            if (h.Spot == Vector3.Invalid) h.Spot = cands[0];
            HlrPlace(s, h.Spot);
            Simulator.Sleep(1);
            Vector3 dest = h.Hb.Position;
            Lot destLot = LotManager.GetLotAtPoint(dest);
            DiagLine("houseboat_leg_repro: " + s.FullName + " (" + h.Scope + ") at " + (int)h.Spot.x + "," + (int)h.Spot.z + (h.ProbeFailed ? " (probe: no path)" : " (probe never failed)")
                + ", houseboat " + h.HbName + " " + h.HbStatus + ", dest lot " + (destLot != null ? destLot.Name + (destLot.IsHouseboatLot() ? " (houseboat lot)" : "") : "none"));
            Route outer = s.CreateRoute();
            PointDestination pd = new PointDestination(); pd.mPoint = dest; outer.AddDestination(pd);
            sHlrOuter = outer; sHlrWatch = s;
            for (int t = 0; t < tries && gen == sHlrGen; t++)
            {
                if ((s.Position - h.Spot).LengthSqr() > 0.25f) HlrPlace(s, h.Spot);  // moved since the last try: the same start again
                int[] c0 = new int[4];
                for (int k = 0; k < 4; k++) c0[k] = HlrCtr(k);
                sHlrLegs = new List<object>();
                string kind = t % 2 == 0 ? "Plan" : "ReplanFromPoint";
                int t0 = System.Environment.TickCount;
                RoutePlanResult res = t % 2 == 0 ? outer.Plan() : outer.ReplanFromPoint(s.Position);
                int ms = unchecked(System.Environment.TickCount - t0);
                Dictionary<string, object> o = Util.Obj();
                o["try"] = t + 1; o["kind"] = kind; o["result"] = res.mType.ToString(); o["ms"] = ms; o["legs"] = sHlrLegs;
                Dictionary<string, object> d = Util.Obj();
                string ds = "";
                for (int k = 0; k < 4; k++) { int v = HlrCtr(k); d["" + (3870 + k)] = v < 0 ? -1 : v - c0[k]; ds += (k > 0 ? " " : "") + (3870 + k) + "+" + d["" + (3870 + k)]; }
                o["counters"] = d;
                h.Plans.Add(o);
                string legs = "";
                foreach (object lo in sHlrLegs)
                {
                    Dictionary<string, object> l = lo as Dictionary<string, object>;
                    legs += (legs.Length > 0 ? "; " : "") + l["result"] + " " + l["ms"] + " ms" + ((bool)l["end_as_boat"] ? " EndAsBoat" : "") + ((bool)l["deep_water"] ? " DeepWater" : "") + ((bool)l["no_land"] ? " NoLand" : "");
                }
                DiagLine("houseboat_leg_repro: " + s.FullName + " try " + (t + 1) + " " + kind + " " + res.mType + " " + ms + " ms, legs seen " + sHlrLegs.Count + (legs.Length > 0 ? " (" + legs + ")" : "") + ", counters " + ds);
                sHlrLegs = null;
                Simulator.Sleep(1);
            }
            sHlrOuter = null; sHlrWatch = null;
        }
    }
}
