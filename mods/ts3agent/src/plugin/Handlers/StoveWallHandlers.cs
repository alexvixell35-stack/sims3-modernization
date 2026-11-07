using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI.Hud;
using GameCounter = Sims3.Gameplay.Objects.Counters.Counter;  // plain Counter is Handlers.Counter, a delegate in CasHandlers.cs: every object failed `is`

namespace Ts3Agent
{
    // Player report 2026-11-07 (v0.2.8): a household Sim next to a stove, a wall between them, cannot reach it, while a wide way
    // round exists. Repros stove-wall-built / stove-wall-kitchen (tools/harness/repros.json): setup once, per arm plan probes, real bakes, probes.
    //   stove_wall_setup {layout?, existing?, stove_tile?, face?, fridge_tile?, counter_tile?, sim_tile?, bin_tile?, bin_park_tile?, level?}
    //     layout: build_layout built the room: its stove, its bin where the map has it, the Sim on X. Only fridge and counter are added
    //     (the map has none, cooking needs them), by the same tile args; stove_wall_camera then shows the whole room.
    //     existing: the lot's own stove, the Sim spot = a routable point within 3 m of its routing slot behind a wall.
    //     Else stove, fridge and counter are created on the given home-lot tiles (a wall drawn before by the repro's build steps).
    //     Always a trash can, parked on bin_park_tile. No wall between Sim spot and slot = error (the layout proves nothing).
    //   stove_wall_probe {arm (1 on, 0 off), label, bin, region?}: the Sim on its spot, plans only (nothing walks), "stove-wall:" lines.
    //     region: then EA's own ImpassableRegion sequence with the Sim on the slot (Actor + stove, as BakingDish_Prepare and
    //     BakingDish_CookInOven: DynamicRegionBase.Cleanup destroys the slot and never calls Remove), and the plans again.
    //   stove_wall_cook {arm, bin}: Sim to its spot, the stove's own menu entry (Stove_Have, which pushes Fridge_Have), a baked recipe if the
    //     menu has one. The menu decides what fits the meal time: a recipe picked by hand was refused 29 times of 29 (Waffles, breakfast only).
    //   stove_wall_end {arm}: where the bake got to.
    public static partial class Handlers
    {
        static void RegisterStoveWall()
        {
            Dispatcher.Register("stove_wall_setup", StoveWallSetup);
            Dispatcher.Register("stove_wall_probe", StoveWallProbe);
            Dispatcher.Register("stove_wall_cook", StoveWallCook);
            Dispatcher.Register("stove_wall_end", StoveWallEnd);
            Dispatcher.Register("stove_wall_place", StoveWallPlace);
            Dispatcher.Register("stove_wall_goto", StoveWallGoto);
            Dispatcher.Register("stove_wall_camera", StoveWallCamera);
            Dispatcher.Register("stove_wall_status", StoveWallStatus);
            Dispatcher.Register("clock_hour", ClockHour);
        }

        static Stove sSwStove;
        static Fridge sSwFridge;
        static GameObject sSwBin;
        static Vector3 sSwSpot, sSwBinOn, sSwBinOff;
        static bool sSwBinIsOn, sSwLayout;
        // what stove_wall_status reports for the last order
        static int sSwWatch, sSwT0;
        static double sSwReachS, sSwCookS;
        static string sSwPlanLine = "", sSwArm = "";
        static List<string> sSwSteps = new List<string>();
        static Sim sSwSim;
        static int sSwRouteLines;

        // shortcut: "savecleanup: " has its own 50000-line cap in every asi, household3 has no "stove-wall:" tag and cut these at the shared 40 lines; drop the prefix once the job's asi has that tag
        static void SwLine(string line) { DiagLine("savecleanup: " + line); }

        static Vector3 SwTile(Lot lot, List<object> t, int level)
        {
            int x = Convert.ToInt32(t[0], CultureInfo.InvariantCulture), z = Convert.ToInt32(t[1], CultureInfo.InvariantCulture);
            Vector3 a = Vector3.Zero, b = Vector3.Zero, c = Vector3.Zero;
            World.GetWorldPosition(lot.LotId, new LotLocation(x, z, (sbyte)level, 0), ref a);
            World.GetWorldPosition(lot.LotId, new LotLocation(x + 1, z, (sbyte)level, 0), ref b);
            World.GetWorldPosition(lot.LotId, new LotLocation(x, z + 1, (sbyte)level, 0), ref c);
            return a + (b - a) * 0.5f + (c - a) * 0.5f;
        }

        // Straight piece (Section.Base) of each base-game counter suite: Mission, Modern, Traditional, Country, Contemporary, group 0.
        // OBJD + OBJK in FullBuild0, script class Counters.Mimics.Counter*Suite [verified on disk 2026-11-07; all five created live in the 5433c4d2 rerun].
        static readonly ulong[] kSwCounterObjd = { 0x635, 0x5F6, 0x769, 0xA37, 0x5F3 };

        // k created and placed if its object passes `want`, else null and a line in tried
        static GameObject SwPut(ResourceKey k, string label, Type want, Vector3 pos, Vector3 fwd, Dictionary<string, object> r, string key, List<string> tried)
        {
            GameObject g;
            try { g = GlobalFunctions.CreateObjectOutOfWorld(k) as GameObject; }
            catch (Exception e) { tried.Add(label + " threw " + e.GetType().Name); return null; }
            if (g == null) { tried.Add(label + " no object"); return null; }
            if (!want.IsInstanceOfType(g))
            {
                string chain = g.GetType().Name;
                for (Type b = g.GetType().BaseType; b != null; b = b.BaseType) chain += ":" + b.FullName;
                tried.Add(label + " " + chain);
                g.Destroy();
                return null;
            }
            g.SetPosition(pos);
            g.SetForward(fwd);
            g.AddToWorld();
            BuyModePlaced(g);  // what Buy does after a drop, modular counters join here
            CreatedObjects().Add(g.ObjectId.Value);
            r[key] = label + " " + g.GetType().Name;
            return g;
        }

        // the objd keys if given, else catalog products with this name part; the object class decides, not the name. Failure names what was tried, with base chains.
        static GameObject SwCreate(string name, Type want, Vector3 pos, Vector3 fwd, Dictionary<string, object> r, string key, params ulong[] objd)
        {
            List<string> tried = new List<string>();
            foreach (ulong inst in objd)
            {
                GameObject g = SwPut(new ResourceKey(inst, 0x319E4F1Du, 0u), "objd 0x" + inst.ToString("X"), want, pos, fwd, r, key, tried);
                if (g != null) return g;
            }
            if (objd.Length == 0)
                foreach (BuildBuyProduct p in Catalog())
                {
                    if ((p.CatalogName ?? "").ToLowerInvariant().IndexOf(name) < 0) continue;
                    GameObject g = SwPut(p.ProductResourceKey, p.CatalogName, want, pos, fwd, r, key, tried);
                    if (g != null) return g;
                }
            string list = string.Join("; ", tried.GetRange(0, Math.Min(tried.Count, 16)).ToArray());
            throw new McpException("no product '" + name + "' of class " + want.Name + ", " + tried.Count + " tried: " + list);
        }

        static object StoveWallSetup(Args a)
        {
            RequireLive();
            Lot lot = HomeLot();
            Sim s = Util.ActiveSim();
            int level = (int)a.Long("level", 0);
            Dictionary<string, object> r = Util.Obj();
            sSwLayout = a.Bool("layout", false);
            if (sSwLayout)
            {
                foreach (Stove st in lot.GetObjects<Stove>()) { sSwStove = st; break; }
                foreach (Trashcan t in lot.GetObjects<Trashcan>()) { sSwBin = t; break; }
                if (sSwStove == null || sSwBin == null) throw new McpException("build_layout's stove and bin are not on the home lot");
                Vector3 pos = SwTile(lot, a.List("fridge_tile"), level), front = SwTile(lot, a.List("face"), level) - pos;
                sSwFridge = SwCreate("fridge", typeof(Fridge), pos, front.Normalize(), r, "fridge") as Fridge;
                SwCreate("counter", typeof(GameCounter), SwTile(lot, a.List("counter_tile"), level), front.Normalize(), r, "counter", kSwCounterObjd);  // by class: a name match gave a counter sink, no Counter
                sSwSpot = s.Position;
                sSwBinOn = sSwBinOff = sSwBin.Position;
                r["bin"] = Util.ObjectName(sSwBin);
            }
            else if (a.Bool("existing", false))
            {
                foreach (Stove st in lot.GetObjects<Stove>()) { sSwStove = st; break; }
                foreach (Fridge f in lot.GetObjects<Fridge>()) { sSwFridge = f; break; }
                if (sSwStove == null || sSwFridge == null) throw new McpException("the home lot needs a stove and a fridge: pick a save with a kitchen");
                Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0), best = Vector3.Invalid;
                float bestD = float.MaxValue;
                for (int ring = 2; ring <= 6; ring++)
                    for (int i = 0; i < 16; i++)
                    {
                        double t = i * Math.PI / 8;
                        Vector3 p = slot + new Vector3((float)Math.Cos(t), 0f, (float)Math.Sin(t)) * (ring * 0.5f);
                        if (!World.WallExistsBetweenPoints(lot.LotId, slot, p, eRoomDefinition.PlacementBlocking) || !s.CreateRoute().IsPointRoutable(p)) continue;
                        float d = (p - slot).Length();
                        if (d < bestD) { bestD = d; best = p; }
                    }
                if (best == Vector3.Invalid) throw new McpException("no routable spot behind a wall within 3 m of the stove slot");
                sSwSpot = best;
                Vector3 along = new Vector3(slot.z - best.z, 0f, best.x - slot.x);
                sSwBinOn = best + along.Normalize() * 1.5f;
                sSwBinOff = lot.EntryPoint();
            }
            else
            {
                Vector3 pos = SwTile(lot, a.List("stove_tile"), level), front = SwTile(lot, a.List("face"), level) - pos;
                sSwStove = SwCreate("stove", typeof(Stove), pos, front.Normalize(), r, "stove") as Stove;
                sSwFridge = SwCreate("fridge", typeof(Fridge), SwTile(lot, a.List("fridge_tile"), level), front.Normalize(), r, "fridge") as Fridge;
                SwCreate("counter", typeof(GameCounter), SwTile(lot, a.List("counter_tile"), level), front.Normalize(), r, "counter", kSwCounterObjd);
                sSwSpot = SwTile(lot, a.List("sim_tile"), level);
                sSwBinOn = SwTile(lot, a.List("bin_tile"), level);
                sSwBinOff = SwTile(lot, a.List("bin_park_tile"), level);
            }
            if (!sSwLayout) sSwBin = SwCreate(a.Str("bin_name", "trash"), typeof(Trashcan), sSwBinOff, Vector3.UnitZ, r, "bin");
            sSwBinIsOn = false;
            Vector3 sl = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            if (!World.WallExistsBetweenPoints(lot.LotId, sSwSpot, sl, eRoomDefinition.PlacementBlocking))
                throw new McpException("no wall between the Sim spot and the stove slot: the layout proves nothing");
            r["slot"] = V3(sl);
            r["spot"] = V3(sSwSpot);
            r["straight_m"] = Math.Round((sl - sSwSpot).Length(), 2);
            r["spot_room"] = World.GetRoomId(sSwSpot);
            r["slot_room"] = World.GetRoomId(sl);
            SwLine("stove-wall: setup " + (sSwLayout ? "layout" : a.Bool("existing", false) ? "existing" : "built") + " slot " + V3(sl) + " room " + r["slot_room"] + ", spot " + V3(sSwSpot) + " room " + r["spot_room"] + ", " + r["straight_m"] + " m apart, bin " + r["bin"] + ", counter " + (r.ContainsKey("counter") ? r["counter"] : "own"));
            return r;
        }

        static string V3(Vector3 v) { return v.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture); }

        static void SwPlace(Sim s, bool bin)
        {
            if (sSwStove == null) throw new McpException("stove_wall_setup first");
            if (bin != sSwBinIsOn) { sSwBin.SetPosition(bin ? sSwBinOn : sSwBinOff); sSwBinIsOn = bin; }
            s.InteractionQueue.CancelAllInteractions();
            s.SetPosition(sSwSpot);
        }

        // one plan, nothing walks. kind 0 = the stove's routing slot (BakingDish_CookInOven's plan), 1 = point
        static string SwPlan(Sim s, string arm, string label, bool bin, int kind, Vector3 to)
        {
            Route route = s.CreateRoute();
            uint t0 = (uint)System.Environment.TickCount;
            if (kind == 0) route.PlanToSlot(sSwStove, Slot.RoutingSlot_0); else route.PlanToPoint(to);
            uint ms = (uint)System.Environment.TickCount - t0;
            RoutePlanResult p = route.PlanResult;
            string line = "stove-wall: arm " + arm + " " + label + " bin " + (bin ? 1 : 0) + ", " + p.mType + ", blocker " + p.mBlockerType + " 0x" + p.mBlockerObjectId.ToString("X")
                + (p.mBlockerType != RouteBlockerType.None ? " at " + V3(p.mBlockerLocation) : "")
                + ", path " + p.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m, candidates " + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + "/" + p.mNumRejectedPoints
                + ", plan " + ms + " ms, from " + V3(s.Position) + " room " + World.GetRoomId(s.Position);
            SwLine(line);
            return line;
        }

        // near the slot and no wall in between: from the wrong side of a wall the Sim can stand 1.5 m from it
        static bool SwReached(Sim s)
        {
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            return (s.Position - slot).Length() <= 1.8f && !World.WallExistsBetweenPoints(s.LotCurrent.LotId, s.Position, slot, eRoomDefinition.PlacementBlocking);
        }

        // every plan the Sim makes for the order, failed ones too: EA's post-plan hook, on the sim thread inside the plan, so nothing may throw out of it
        static void SwPlanned(Route r, string routeType, string result)
        {
            try
            {
                if (r.Follower == null || r.Follower.Target != sSwSim || sSwRouteLines++ > 400) return;
                RoutePlanResult p = r.PlanResult;
                InteractionInstance cur = sSwSim.CurrentInteraction;
                GameObject to = r.DestObj as GameObject;
                SwLine("stove-wall: arm " + sSwArm + " route " + routeType + " for " + (cur != null ? cur.GetType().Name : "none") + ", posture " + (sSwSim.Posture != null ? sSwSim.Posture.GetType().Name : "none")
                    + ", to " + (to != null ? Util.ObjectName(to) : "a point") + " at " + V3(r.GetDestPoint()) + ", from " + V3(sSwSim.Position) + ", " + result + " " + p.mType + ", blocker " + p.mBlockerType
                    + ", path " + p.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m, candidates " + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + "/" + p.mNumRejectedPoints);
            }
            catch (Exception) { }
        }

        static void SwWatchStop()
        {
            sSwWatch++;
            RoutingComponent.sPostPlanProfileCallback = null;
        }

        // from the order on: the plan it starts with, then the first moment at the stove, the first interaction on it, every interaction in order, every route (SwPlanned)
        static void SwWatch(Sim s, string arm, string label)
        {
            sSwArm = arm;
            sSwPlanLine = SwPlan(s, arm, label + " plan", false, 0, Vector3.Zero);
            sSwT0 = System.Environment.TickCount;
            sSwReachS = sSwCookS = -1;
            sSwSteps = new List<string>();
            sSwSim = s;
            sSwRouteLines = 0;
            RoutingComponent.sPostPlanProfileCallback = SwPlanned;
            int watch = ++sSwWatch;
            RunAsync("stove wall watch", delegate
            {
                while (watch == sSwWatch && System.Environment.TickCount - sSwT0 < 180000)
                {
                    double t = Math.Round((System.Environment.TickCount - sSwT0) / 1000.0, 1);
                    if (sSwReachS < 0 && SwReached(s)) sSwReachS = t;
                    InteractionInstance cur = s.CurrentInteraction;
                    if (cur != null)
                    {
                        string n = cur.GetType().Name;
                        if (sSwSteps.Count == 0 || sSwSteps[sSwSteps.Count - 1] != n)
                        {
                            sSwSteps.Add(n);
                            GameObject target = cur.Target as GameObject;
                            SwLine("stove-wall: arm " + arm + " interaction " + n + " at " + t + " s, target " + (target != null ? Util.ObjectName(target) : "none") + ", posture " + (s.Posture != null ? s.Posture.GetType().Name : "none") + ", from " + V3(s.Position));
                        }
                        if (sSwCookS < 0 && (cur.Target == sSwStove || n.IndexOf("Oven", StringComparison.Ordinal) >= 0 || n.IndexOf("Stove_", StringComparison.Ordinal) < 0 && n.IndexOf("Stove", StringComparison.Ordinal) >= 0)) sSwCookS = t;
                    }
                    Ticks(3);
                }
                if (watch == sSwWatch) RoutingComponent.sPostPlanProfileCallback = null;
            });
        }

        static object StoveWallProbe(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            string arm = a.Long("arm", -1).ToString(), label = a.Str("label", "before");
            bool bin = a.Bool("bin", false);
            SwPlace(s, bin);
            Simulator.Sleep(30);
            List<object> lines = new List<object>();
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            lines.Add(SwPlan(s, arm, label + " to-slot", bin, 0, slot));
            lines.Add(SwPlan(s, arm, label + " to-slot-point", bin, 1, slot));
            lines.Add(SwPlan(s, arm, label + " own-spot", bin, 1, s.Position));
            if (a.Bool("region", false))
            {
                s.SetPosition(slot);
                Simulator.Sleep(10);
                ImpassableRegion region = new ImpassableRegion();
                region.AddMember(s);
                region.AddMember(sSwStove);
                region.UpdateFootprint();
                Simulator.Sleep(30);
                lines.Add(SwPlan(s, arm, label + " region-live at-slot", bin, 0, slot));
                region.Cleanup();
                Simulator.Sleep(30);
                lines.Add(SwPlan(s, arm, label + " region-gone at-slot", bin, 0, slot));
                lines.Add(SwPlan(s, arm, label + " region-gone own-spot", bin, 1, s.Position));
                s.SetPosition(sSwSpot);
                Simulator.Sleep(10);
                lines.Add(SwPlan(s, arm, label + " region-gone to-slot", bin, 0, slot));
                lines.Add(SwPlan(s, arm, label + " region-gone to-slot-point", bin, 1, slot));
            }
            Dictionary<string, object> r = Util.Obj();
            r["lines"] = lines;
            int fails = 0;
            foreach (object l in lines) if (((string)l).IndexOf("Failed", StringComparison.Ordinal) >= 0) fails++;
            r["failed"] = fails;
            return r;
        }

        static object StoveWallCook(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            string arm = a.Long("arm", -1).ToString();
            bool bin = a.Bool("bin", false);
            SwPlace(s, bin);
            s.Motives.MaxEverything();
            SwWatch(s, arm, "cook");
            GameObjectHit hit = HitFor(sSwStove, a);
            List<MenuEntry> menu = BuildMenu(s, sSwStove, hit);
            MenuEntry pick = null;
            Recipe chosen = null;
            int rank = -1, have = 0, enabled = 0;
            string refused = "";
            foreach (MenuEntry e in menu)
            {
                Type def = e.Iop.InteractionDefinition.GetType();
                if (def.DeclaringType == null || def.DeclaringType.Name != "Stove_Have") continue;
                have++;
                if (!e.Enabled) { if (refused.Length < 120) refused += e.Label + ": " + e.Reason + "; "; continue; }
                enabled++;
                Recipe rc = def.GetField("ChosenRecipe").GetValue(e.Iop.InteractionDefinition) as Recipe;
                if (rc == null || rc.CookingProcessData == null) continue;
                int k = rc.CookingProcessData.UsesAStove ? 1 : 0;
                foreach (CookingStepsAndTargetPair pair in rc.CookingProcessData.OrderOfStepsList)
                    if (pair.Steps.Contains(CookingStep.PutInBakingTray) && pair.Steps.Contains(CookingStep.CookInOven)) k = 2;
                if (k > rank) { rank = k; pick = e; chosen = rc; }
            }
            Lot lot = sSwStove.LotCurrent;
            SwLine("stove-wall: arm " + arm + " cook menu " + menu.Count + " entries, Stove_Have " + have + ", enabled " + enabled + ", meal time " + Food.GetCurrentMealTime() + ", stove test " + sSwStove.CommonMakeTest(s)
                + ", usable counter " + Food.LotHasUsableCounter(lot) + ", usable stove " + Food.LotHasUsableStove(lot) + ", funds " + s.FamilyFunds + (refused.Length > 0 ? ", greyed " + refused : ""));
            Dictionary<string, object> r = Util.Obj();
            r["stove_have"] = have;
            r["enabled"] = enabled;
            if (pick == null) { r["queued"] = false; return r; }
            InteractionInstanceParameters p = new InteractionInstanceParameters(pick.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
            InteractionInstance ii = pick.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
            if (ii == null) throw new McpException("the game could not create '" + pick.Label + "'");
            bool test = ii.Test();
            bool q = s.InteractionQueue.Add(ii);
            SwLine("stove-wall: arm " + arm + " cook order " + ii.GetType().Name + " on the stove, entry '" + pick.Label + "', recipe " + chosen.GenericName + ", test " + test + ", queued " + q);
            r["recipe"] = chosen.GenericName;
            r["test"] = test;
            r["queued"] = q;
            return r;
        }

        // Player-view helpers for the recorded GIF cases (2026-11-07): the selected household Sim stands on its spot behind the wall, the camera is on it
        // and the stove, the order is a player's Go Here click (user-directed, the same path as the pie menu), the verdict is a plain sentence.
        //   stove_wall_place {bin?}: the Sim (selected) on its spot, nothing planned or ordered.
        //   stove_wall_goto {}: Go Here to the stove's routing slot, queued as the player's click: user-directed, click hit set.
        //   stove_wall_camera {dist?: 4, height?: 2.2}: fixed camera in front of the stove (its forward side), 3-5 m, low-medium angle, looking at it.
        //   stove_wall_status {}: {text}: "reached the stove" / "did not reach the stove, N m away, doing X".
        //   clock_hour {hour}: advances the sim clock with SimClock.AddOneHour until it is that hour (daylight for a GIF).
        static object StoveWallPlace(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            // the Sim may be in the middle of something (2026-11-07 run: in the bath tub, so the GIF showed the bath): cancel and wait until it is out of it
            for (int i = 0; i < 12; i++)
            {
                s.InteractionQueue.CancelAllInteractions();
                Simulator.Sleep(20);
                if (s.CurrentInteraction == null) break;
            }
            s.Motives.MaxEverything();  // nothing to need: no autonomous bath or snack in the recorded window
            SwPlace(s, a.Bool("bin", false));
            Dictionary<string, object> r = Util.Obj();
            r["at"] = V3(s.Position);
            r["current"] = s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "none";
            return r;
        }

        static object StoveWallGoto(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            Vector3 pos = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, pos).CreateInstance(Terrain.Singleton, s,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            SetClickHit(ii, pos);
            SwWatch(s, a.Long("arm", -1).ToString(), "walk");
            if (!s.InteractionQueue.Add(ii)) throw new McpException("could not queue Go Here");
            Dictionary<string, object> r = Util.Obj();
            r["to"] = V3(pos);
            return r;
        }

        static object StoveWallCamera(Args a)
        {
            RequireLive();
            // fixed view (Rodney 2026-11-07): in the kitchen, facing the front of the stove from 3 to 5 m, low-medium angle, never moved during the action
            Vector3 fwd = sSwStove.ForwardVector;
            fwd.y = 0f;
            fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : new Vector3(0f, 0f, 1f);
            Vector3 stove = sSwStove.Position;
            if (sSwLayout)
            {
                // the whole room as the player drew it: from the map's bottom side, looking at its top wall, X left, stove right; the near wall cut away
                try { CameraController.DisableObjectFollow(); CameraController.Untether(); } catch (Exception) { }
                Lot lot = HomeLot();
                Vector3 ez = Minus(TileCorner(lot, 0, 1), TileCorner(lot, 0, 0));
                Vector3 c = (sSwSpot + sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0)) * 0.5f + ez * 2f;
                c.y = stove.y + 0.5f;
                if (LotManager.GetWallsViewMode() != 302u) LotManager.SetWallsViewMode(302u);
                float back = (float)a.Double("dist", 8), up = (float)a.Double("height", 9);
                CameraController.SetPositionAndTarget(new Vector3(c.x + ez.x * back, c.y + up, c.z + ez.z * back), c);
                Dictionary<string, object> v = Util.Obj();
                v["target"] = V3(c);
                return v;
            }
            float dist = (float)a.Double("dist", 4), h = (float)a.Double("height", 2.2);
            // the game follows the selected Sim while it walks (run 3 and 4: the view moved bedroom to kitchen): stop that first, the view stays where it is put
            try { CameraController.DisableObjectFollow(); CameraController.Untether(); } catch (Exception) { }
            Vector3 target = new Vector3(stove.x, stove.y + 0.9f, stove.z) + fwd * 0.6f;
            CameraController.SetPositionAndTarget(new Vector3(stove.x + fwd.x * dist, stove.y + h, stove.z + fwd.z * dist), target);
            Dictionary<string, object> r = Util.Obj();
            r["target"] = V3(target);
            r["from"] = V3(new Vector3(stove.x + fwd.x * dist, stove.y + h, stove.z + fwd.z * dist));
            return r;
        }

        static object StoveWallStatus(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            float d = (s.Position - slot).Length();
            InteractionInstance cur = s.CurrentInteraction;
            string doing = cur != null ? cur.GetType().Name : "nothing";
            Dictionary<string, object> r = Util.Obj();
            bool reached = sSwReachS >= 0 || SwReached(s);
            r["text"] = reached ? "Sim reached the stove (" + Math.Round(d, 1) + " m from it now)" + (sSwReachS >= 0 ? " after " + sSwReachS + " s" : "") : "Sim did NOT reach the stove: " + Math.Round(d, 1) + " m away, doing " + doing;
            r["distance"] = Math.Round(d, 2);
            r["reached"] = reached;
            r["reach_s"] = sSwReachS;
            r["cook_s"] = sSwCookS;
            string steps = string.Join(">", sSwSteps.ToArray());
            SwLine("stove-wall: arm " + sSwArm + " result reached " + reached + " after " + sSwReachS + " s, cook started " + (sSwCookS >= 0) + " after " + sSwCookS + " s, now " + Math.Round(d, 1) + " m away, doing " + doing
                + ", steps " + steps.Substring(0, Math.Min(steps.Length, 250)));  // the line limit is 480; the plan has its own line
            return r;
        }

        static object ClockHour(Args a)
        {
            RequireLive();
            int want = (int)a.Long("hour", 12), n = 0;
            while (SimClock.Hours24 != want && n++ < 24) SimClock.AddOneHour();
            Dictionary<string, object> r = Util.Obj();
            r["hour"] = SimClock.Hours24;
            r["steps"] = n;
            return r;
        }

        static object StoveWallEnd(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            InteractionInstance cur = s.CurrentInteraction;
            string line = "stove-wall: arm " + a.Long("arm", -1) + " cook end, at " + V3(s.Position) + " room " + World.GetRoomId(s.Position) + ", interaction " + (cur != null ? cur.GetType().Name : "none")
                + ", queue " + s.InteractionQueue.Count + ", exit " + s.ExitReason;
            SwWatchStop();
            SwLine(line);
            s.InteractionQueue.CancelAllInteractions();
            Dictionary<string, object> r = Util.Obj();
            r["line"] = line;
            return r;
        }
    }
}
