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
    // Repro for ts3fixes houseboatlegmemo (docs/853 §2, HouseboatLegMemo.cs): EA's houseboat boat leg that finds no path, planned
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
    // callbacks saw (options, result, ms; an answered leg makes no callback) and the 1378-1381 deltas of that try.
    // That is mode "undock" (the boat leg). The default mode "docked" (docs/853 §3, houseboatgangwayrescue) runs the flow seen in
    // play: the stranding spot is where EA's boat trip home drops the rider (the gangway leg follows a successful kind-3 boat
    // replan), not a spot we choose (2026-11-19 phase-1523: every spot 15-22 m off the gangway walked fine). An NPC owner of a
    // DOCKED houseboat (port gangway, jig) is sent by EA's Teleport Me Here (setup_sims) to the nearest world-lot coastal land spot
    // NOT connected by land to the houseboat, then EA's Sim.MakeSimGoHome(sim, true) (its own boat when usable there, else
    // GoHome plans the boat trip with EA's NPC boat). The result watches `waitS` (150) seconds: the boat it sat in, the get-out
    // spot and its distance to the gangway, the first gangway leg (boat and water planning off inside CheckAndUpdateForHouseboats:
    // result, ms, Sim position), whether the rescue fired (ts3fixes.moved), the houseboat lot reached, 1378-1381 / 1382-1391.
    public static partial class Handlers
    {
        static void RegisterHouseboatLegRepro()
        {
            Dispatcher.Register("houseboat_leg_repro", HouseboatLegRepro);
        }

        class HlrSim { public Sim S; public Houseboat Hb; public string Scope, Error, HbName, HbStatus; public bool Resident; public Vector3 Spot = Vector3.Invalid;
            public List<object> Probes = new List<object>(), Plans = new List<object>(); public bool ProbeFailed;
            // docked mode: the boat trip home
            public bool Pushed, Home, InBoat, Rescued; public int HomeMs = -1, GetOutMs = -1; public string LotAfter, Boat, FirstLeg;
            public float GangwayM = -1f, GetOutM = -1f; public Vector3 Gangway = Vector3.Invalid, GetOut = Vector3.Invalid; }

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

        // what ts3fixes' IsPlayerSim decides for this Sim (its rule mirrored: the agent cannot call ts3fixes)
        static string HlrScope(Sim s)
        {
            Household act = Household.ActiveHousehold, h = s.SimDescription != null ? s.SimDescription.Household : null;
            if (s == PlumbBob.SelectedActor) return "selected (exempt)";
            if (act == null) return "no active household (unknown scope, exempt)";
            if (h == null) return "no household (unknown scope, exempt)";
            if (h == act || s.IsSelectable) return "household (exempt)";
            return "npc";
        }

        static int HlrCtr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 1381 ? c[1378 + k] : -1; }
        // houseboatgangwayrescue 1382-1391 (docs/853 §3)
        static readonly string[] kHlrGr = { "marked", "rescued", "refused", "errors", "refused_exempt", "refused_houseboat", "refused_hour", "refused_busy", "refused_lot_far_stale", "refused_nospot" };
        static readonly int[] sHlrGrBase = new int[10];
        static int HlrGr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 1391 ? c[1382 + k] : -1; }
        static string sHlrMode = "docked";
        static int sHlrWaitS = 150;
        static readonly Dictionary<Sim, HlrSim> sHlrDockedWatch = new Dictionary<Sim, HlrSim>();
        // docked mode runs the Sim SEEN (2026-11-19 phase-1608: unseen, the NPCs were put home by the unseen shortcuts in 0.2-5 s, no
        // boat trip): the camera follows the first Sim, and with shortcutsOff (default true) these ts3fixes switches are forced off
        // for the run (the same ts3fixes.switches override as `patches`), restored at the end or on stop
        static readonly string[] kHlrShortcuts = { "gohome", "unseenboat", "unseenboatstart", "unseenboatland", "unseenfbteleport", "unseenfbpre", "unseenfbtrip", "unseenfbarrive", "unseenroute" };
        static Dictionary<string, int> sHlrPrior;  // switch -> 1 on, 0 off, -1 unset before the run
        static bool sHlrCamera, sHlrShortcutsOff = true;

        static void HlrShortcutsOff()
        {
            if (sHlrPrior != null) return;
            Dictionary<string, bool> sw = HlrSwitches();
            sHlrPrior = new Dictionary<string, int>();
            bool v;
            foreach (string n in kHlrShortcuts) { sHlrPrior[n] = sw.TryGetValue(n, out v) ? (v ? 1 : 0) : -1; sw[n] = false; }
            Util.SwitchesChanged();
            DiagLine("houseboat_leg_repro: unseen shortcuts off for the run: " + string.Join(", ", kHlrShortcuts));
        }

        static void HlrShortcutsRestore()
        {
            if (sHlrPrior == null) return;
            Dictionary<string, bool> sw = HlrSwitches();
            foreach (KeyValuePair<string, int> kv in sHlrPrior) { if (kv.Value < 0) sw.Remove(kv.Key); else sw[kv.Key] = kv.Value == 1; }
            sHlrPrior = null;
            Util.SwitchesChanged();
            DiagLine("houseboat_leg_repro: unseen shortcuts restored");
        }

        static void HlrCameraOff()
        {
            if (!sHlrCamera) return;
            sHlrCamera = false;
            try { CameraController.DisableObjectFollow(); } catch (Exception) { }
        }

        static Dictionary<string, bool> HlrSwitches()
        {
            AppDomain dom = AppDomain.CurrentDomain;
            Dictionary<string, bool> sw = dom.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { sw = new Dictionary<string, bool>(); dom.SetData("ts3fixes.switches", sw); }
            return sw;
        }

        // houseboat_leg_repro {start: true, mode?: "docked" | "undock", sims?: 2, tries?: 4 (undock), undock?: true (undock mode),
        //   waitS?: 150 (docked: seconds to watch the trip home), shortcutsOff?: true (docked: unseen shortcuts off for the run)} | {result: true} | {stop: true}
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
                sHlrWaitS = Math.Max(5, (int)a.Long("waitS", 150));
                sHlrShortcutsOff = a.Bool("shortcutsOff", true);
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
            if (a.Has("stop")) { sHlrGen++; HlrUnhook(); HlrShortcutsRestore(); HlrCameraOff(); r["stopped"] = true; return r; }
            Dictionary<string, object> ctr = Util.Obj();
            string[] cn = { "recorded", "answered", "answered_ms", "exempt_or_error" };
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); ctr[cn[k] + "_" + (1378 + k)] = v < 0 ? -1 : v - sHlrBase[k]; }
            for (int k = 0; k < 10; k++) { int v = HlrGr(k); ctr[kHlrGr[k] + "_" + (1382 + k)] = v < 0 ? -1 : v - sHlrGrBase[k]; }
            r["done"] = sHlrDone; r["error"] = sHlrError; r["counters"] = ctr; r["houseboatlegmemo"] = ModOn("houseboatlegmemo");
            r["houseboatgangwayrescue"] = ModOn("houseboatgangwayrescue"); r["mode"] = sHlrMode;
            r["active_household"] = Household.ActiveHousehold != null ? Household.ActiveHousehold.Name : null;
            List<object> rows = new List<object>();
            foreach (HlrSim h in sHlrSims)
            {
                Dictionary<string, object> o = Util.Obj();
                o["sim"] = h.S.FullName; o["scope"] = h.Scope; o["houseboat"] = h.HbName; o["houseboat_status"] = h.HbStatus; o["resident"] = h.Resident;
                o["spot"] = h.Spot != Vector3.Invalid ? Math.Round(h.Spot.x, 1) + "," + Math.Round(h.Spot.z, 1) : null;
                if (sHlrMode == "docked") o["start_needs_boat"] = h.ProbeFailed;  // docked: start not connected by land (true = wanted)
                else o["probe_failed"] = h.ProbeFailed;
                o["probes"] = h.Probes; o["plans"] = h.Plans; o["error"] = h.Error;
                if (sHlrMode == "docked")
                {
                    o["start_gangway_m"] = Math.Round(h.GangwayM, 1); o["gohome_pushed"] = h.Pushed; o["boat"] = h.Boat;
                    o["get_out"] = h.GetOut != Vector3.Invalid ? Math.Round(h.GetOut.x, 1) + "," + Math.Round(h.GetOut.z, 1) : null;
                    o["get_out_gangway_m"] = Math.Round(h.GetOutM, 1); o["get_out_ms"] = h.GetOutMs;
                    o["gangway"] = h.Gangway != Vector3.Invalid ? Math.Round(h.Gangway.x, 1) + "," + Math.Round(h.Gangway.z, 1) : null;
                    o["first_gangway_leg"] = h.FirstLeg; o["rescued"] = h.Rescued; o["reached_home"] = h.Home; o["home_ms"] = h.HomeMs;
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
                    h.Scope = HlrScope(s);
                    picks.Add(h);
                }
            if (picks.Count == 0) throw new McpException("houseboat_leg_repro: no NPC fits (known non-active household, not selectable or selected, teen or older human)");
            return picks;
        }

        static void HlrAbout(Route r, string routeType, Vector3 point)
        {
            if (sHlrDockedWatch.Count > 0 && r != null && r.Follower != null && r.Follower.Target is Sim && sHlrDockedWatch.ContainsKey((Sim)r.Follower.Target))
            { sHlrT0[r] = System.Environment.TickCount; return; }
            if (sHlrWatch == null || r == null || r == sHlrOuter || r.Follower == null || r.Follower.Target != sHlrWatch) return;
            sHlrT0[r] = System.Environment.TickCount;
        }

        static void HlrPost(Route r, string routeType, string result)
        {
            HlrSim dw;
            if (sHlrDockedWatch.Count > 0 && r != null && r.Follower != null && r.Follower.Target is Sim && sHlrDockedWatch.TryGetValue((Sim)r.Follower.Target, out dw))
            { HlrDockedLeg(dw, r, routeType); return; }
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
            sHlrWatch = null; sHlrOuter = null; sHlrLegs = null; sHlrT0.Clear(); sHlrDockedWatch.Clear();
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
                h.S = s; h.Resident = true; h.Hb = hb; h.HbName = HlrHbName(hb); h.HbStatus = hb.CurrentStatus.ToString(); h.Scope = HlrScope(s);
                picks.Add(h);
            }
            if (picks.Count == 0) throw new McpException("houseboat_leg_repro docked: no NPC (known non-active household, teen or older human) lives on a Docked houseboat with a port gangway; houseboats: "
                + (seen.Length > 0 ? seen : "none") + " (mode undock repros the boat leg instead)");
            return picks;
        }

        // the nearest world-lot coastal land spot (sea within 12 m) that is NOT connected by land to the houseboat: GoHome from
        // there needs EA's boat trip. Rings 80-480 m around the houseboat, 24 directions, nearest ring first.
        static Vector3 HlrFarCoast(Houseboat hb, out int tested)
        {
            Vector3 c = hb.Position;
            tested = 0;
            for (float rad = 80f; rad <= 480f; rad += 40f)
                for (int i = 0; i < 24; i++)
                {
                    double ang = i * Math.PI / 12;
                    Vector3 p = new Vector3(c.x + rad * (float)Math.Cos(ang), 0f, c.z + rad * (float)Math.Sin(ang));
                    if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) continue;
                    Lot lot = LotManager.GetLotAtPoint(p);
                    if (lot != null && !lot.IsWorldLot) continue;
                    bool coast = false;
                    for (int k = 0; k < 8 && !coast; k++)
                    {
                        double a2 = k * Math.PI / 4;
                        coast = (TerrainType)World.GetTerrainType(p.x + 12f * (float)Math.Cos(a2), p.z + 12f * (float)Math.Sin(a2), 0) == TerrainType.WorldSea;
                    }
                    if (!coast) continue;
                    p.y = World.GetTerrainHeight(p.x, p.z);
                    tested++;
                    if (Terrain.ArePointsConnectedByLand(p, c)) continue;
                    return p;
                }
            return Vector3.Invalid;
        }

        // a plan of a watched Sim (HlrPost): the first gangway leg (boat and water planning off, planned inside
        // CheckAndUpdateForHouseboats) is kept with the Sim's position; the other plans are listed (cap 40)
        static void HlrDockedLeg(HlrSim h, Route r, string routeType)
        {
            int t0;
            int ms = sHlrT0.TryGetValue(r, out t0) ? unchecked(System.Environment.TickCount - t0) : -1;
            sHlrT0.Remove(r);
            bool boat = r.GetOption2(Route.RouteOption2.EnablePlanningAsBoat), water = r.GetOption(Route.RouteOption.EnableWaterPlanning);
            bool leg = !boat && !water && Houseboat.sHouseboatStackDepth > 0;
            Vector3 p = h.S.Position;
            string line = routeType + " " + r.PlanResult.mType + " " + ms + " ms" + (leg ? " GANGWAY LEG" : "") + " boat " + boat + " water " + water
                + ", sim " + Math.Round(p.x, 1) + "," + Math.Round(p.z, 1) + (h.Gangway != Vector3.Invalid ? " (" + Math.Round((p - h.Gangway).Length(), 1) + " m from the gangway)" : "")
                + ", posture " + (h.S.Posture != null ? h.S.Posture.GetType().Name : "-");
            if (h.Plans.Count < 40) h.Plans.Add(line);
            if (leg && h.FirstLeg == null)
            {
                h.FirstLeg = line;
                DiagLine("houseboat_leg_repro: " + h.S.FullName + " first gangway leg: " + line);
            }
        }

        static void HlrRunDocked(int gen)
        {
            if (sHlrShortcutsOff) HlrShortcutsOff();
            HlrHook();
            try
            {
                for (int i = 0; i < sHlrSims.Count && gen == sHlrGen; i++)
                {
                    HlrSim h = sHlrSims[i];
                    try { HlrDockedSend(h, gen); }
                    catch (Exception e) { h.Error = e.GetType().Name + ": " + e.Message; DiagLine("houseboat_leg_repro: " + h.S.FullName + " error " + h.Error); }
                }
                int t0 = System.Environment.TickCount, left = 0;
                foreach (HlrSim h in sHlrSims) if (h.Pushed) { left++; sHlrDockedWatch[h.S] = h; }
                Dictionary<string, object> moved = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
                while (left > 0 && gen == sHlrGen && unchecked(System.Environment.TickCount - t0) < sHlrWaitS * 1000)
                {
                    Simulator.Sleep(10);
                    int now = unchecked(System.Environment.TickCount - t0);
                    foreach (HlrSim h in sHlrSims)
                    {
                        if (!h.Pushed || h.Home) continue;
                        Sims3.Gameplay.Objects.Vehicles.SittingInBoat sib = h.S.Posture as Sims3.Gameplay.Objects.Vehicles.SittingInBoat;
                        if (sib != null && !h.InBoat)
                        {
                            h.InBoat = true;
                            h.Boat = sib.Container != null ? sib.Container.GetType().Name : "?";
                            DiagLine("houseboat_leg_repro: " + h.S.FullName + " in a " + h.Boat + " after " + now + " ms");
                        }
                        else if (sib == null && h.InBoat && h.GetOut == Vector3.Invalid)
                        {
                            h.GetOut = h.S.Position; h.GetOutMs = now;
                            h.GetOutM = h.Gangway != Vector3.Invalid ? (h.GetOut - h.Gangway).Length() : -1f;
                            Lot gl = LotManager.GetLotAtPoint(h.GetOut);
                            DiagLine("houseboat_leg_repro: " + h.S.FullName + " got out at " + Math.Round(h.GetOut.x, 1) + "," + Math.Round(h.GetOut.z, 1) + ", "
                                + Math.Round(h.GetOutM, 1) + " m from " + h.HbName + "'s gangway, lot " + (gl != null ? gl.Name : "none") + ", after " + now + " ms");
                        }
                        if (moved == null) moved = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
                        object mv;
                        if (!h.Rescued && moved != null && moved.TryGetValue(h.S.FullName, out mv) && mv is string && ((string)mv).StartsWith("houseboatgangwayrescue"))
                        {
                            h.Rescued = true;
                            DiagLine("houseboat_leg_repro: " + h.S.FullName + " rescue fired after " + now + " ms: " + mv);
                        }
                        Lot hl = h.Hb.GetHouseboatLot();
                        if (hl != null && h.S.LotCurrent != null && h.S.LotCurrent.LotId == hl.LotId)
                        {
                            h.Home = true; h.HomeMs = now; h.LotAfter = hl.Name; left--;
                            DiagLine("houseboat_leg_repro: " + h.S.FullName + " on " + hl.Name + " after " + now + " ms");
                        }
                    }
                }
                foreach (HlrSim h in sHlrSims)
                {
                    if (!h.Pushed) continue;
                    if (!h.Home) h.LotAfter = h.S.LotCurrent != null ? h.S.LotCurrent.Name : "none";
                    DiagLine("houseboat_leg_repro: " + h.S.FullName + (h.Home ? " reached home in " + h.HomeMs + " ms" : " NOT home after " + sHlrWaitS + " s, on " + h.LotAfter)
                        + ", boat " + (h.Boat ?? "none") + ", get-out " + (h.GetOut != Vector3.Invalid ? Math.Round(h.GetOutM, 1) + " m from the gangway" : "none")
                        + ", first gangway leg " + (h.FirstLeg ?? "none") + ", rescued " + h.Rescued);
                }
            }
            finally { HlrUnhook(); HlrShortcutsRestore(); HlrCameraOff(); }
            string s = "";
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); s += (k > 0 ? ", " : "") + "[" + (1378 + k) + "] +" + (v < 0 ? -1 : v - sHlrBase[k]); }
            for (int k = 0; k < 10; k++) { int v = HlrGr(k); s += ", [" + (1382 + k) + "] +" + (v < 0 ? -1 : v - sHlrGrBase[k]); }
            DiagLine("houseboat_leg_repro: done (docked), counters " + s);
        }

        // EA's Teleport Me Here (setup_sims) to the far coastal spot, wait until there (10 s at most), then EA's MakeSimGoHome
        static void HlrDockedSend(HlrSim h, int gen)
        {
            Sim s = h.S;
            h.Gangway = h.Hb.GetPortGangway().GetPositionOfSlot(Slot.RoutingSlot_0);
            int tested;
            Vector3 spot = HlrFarCoast(h.Hb, out tested);
            Dictionary<string, object> o = Util.Obj();
            o["coast_spots_tested"] = tested;
            o["spot"] = spot != Vector3.Invalid ? Math.Round(spot.x, 1) + "," + Math.Round(spot.z, 1) : null;
            h.Probes.Add(o);
            if (spot == Vector3.Invalid) throw new McpException("no world-lot coastal spot 80-480 m off " + h.HbName + " that is not connected to it by land (" + tested + " coastal spots tested)");
            h.Spot = spot;
            SetupSimsAt(new List<Sim> { s }, null, spot, true, true, true, false);  // EA's teleport, motives, moodlets, autonomy off
            int t0 = System.Environment.TickCount;
            while (gen == sHlrGen && (s.Position - spot).Length() > 3f && unchecked(System.Environment.TickCount - t0) < 10000) Simulator.Sleep(10);
            h.GangwayM = (s.Position - h.Gangway).Length();
            h.ProbeFailed = !Terrain.ArePointsConnectedByLand(s.Position, h.Hb.Position);  // "probe_failed" = the start needs a boat
            if (h == sHlrSims[0])  // SEEN: the camera on the first Sim, following it for the whole trip
            {
                Vector3 p = s.Position;
                try
                {
                    if (CameraController.IsMapViewModeEnabled()) Sims3.Gameplay.Core.Camera.ToggleMapView();
                    CameraController.SetPositionAndTarget(new Vector3(p.x, p.y + 12f, p.z - 12f), p);
                    CameraController.EnableObjectFollow(s.ObjectId.Value, Vector3.Zero);
                    sHlrCamera = true;
                }
                catch (Exception e) { DiagLine("houseboat_leg_repro: camera error " + e.GetType().Name); }
                Simulator.Sleep(1);
            }
            h.Pushed = Sim.MakeSimGoHome(s, true);
            Lot at = s.LotCurrent;
            DiagLine("houseboat_leg_repro: " + s.FullName + " (" + h.Scope + ") at " + (int)s.Position.x + "," + (int)s.Position.z + " (lot " + (at != null ? at.Name : "none")
                + "), " + Math.Round(h.GangwayM) + " m from " + h.HbName + "'s gangway, land-connected " + !h.ProbeFailed + ", camera " + (sHlrCamera && h == sHlrSims[0] ? "following" : "elsewhere")
                + ", MakeSimGoHome " + h.Pushed);
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
            for (int k = 0; k < 4; k++) { int v = HlrCtr(k); s += (k > 0 ? ", " : "") + "[" + (1378 + k) + "] +" + (v < 0 ? -1 : v - sHlrBase[k]); }
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
                for (int k = 0; k < 4; k++) { int v = HlrCtr(k); d["" + (1378 + k)] = v < 0 ? -1 : v - c0[k]; ds += (k > 0 ? " " : "") + (1378 + k) + "+" + d["" + (1378 + k)]; }
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
