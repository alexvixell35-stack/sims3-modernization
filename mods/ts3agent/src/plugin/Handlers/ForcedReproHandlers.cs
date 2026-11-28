using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Beds;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Agent
{
    // Set-up steps of the forced repros of docs/686 §11-§19 (petrelax-force, reachuse-force, makebedboth-force, the share
    // repros, viewlenient-force, socialreach-force, unstuck-force). Test set-up only:
    // the orders themselves stay the player's (do_interaction).
    public static partial class Handlers
    {
        static void RegisterForcedRepro()
        {
            Dispatcher.Register("add_puddle", AddPuddleNear);
            Dispatcher.Register("bed_setup", BedSetup);
            Dispatcher.Register("put_in_front", PutInFront);
            Dispatcher.Register("order_at", OrderAt);
            Dispatcher.Register("add_trash", AddTrash);
            Dispatcher.Register("pet_jigs", PetJigs);
            Dispatcher.Register("go_at", GoAt);
            Dispatcher.Register("add_platter", AddPlatter);
            Dispatcher.Register("contemplate", Contemplate);
            Dispatcher.Register("route_points", RoutePoints);
            Dispatcher.Register("box_in", BoxIn);
            Dispatcher.Register("slot_close", SlotClose);
        }

        // The place `forward` metres along the target's facing (negative = behind) and `right` metres to its right.
        static Vector3 PlaceNear(Args a)
        {
            GameObject t = FindTarget(a, "near");
            Vector3 f = t.ForwardVector;
            f.y = 0f;
            float len = f.Length();
            if (len < 0.001f) throw new McpException("the target has no facing");
            f = f * (1f / len);
            Vector3 right = new Vector3(-f.z, 0f, f.x);
            return t.Position + f * (float)a.Double("forward", 0) + right * (float)a.Double("right", 0);
        }

        // The player's click on a lot's floor at p: the terrain's pie menu with a lot-terrain hit (Terrain.MopPuddle's and
        // the pets' Lie Down Here tests want it), the entry matching `order` queued for the Sim.
        static void OrderAtPoint(Sim sim, Vector3 p, string order, Dictionary<string, object> r)
        {
            Lot lot = LotManager.GetLotAtPoint(p);
            if (lot == null) throw new McpException("no lot at the place");
            GameObjectHit hit = new GameObjectHit(GameObjectHitType.LotTerrain);
            hit.mPoint = p;
            hit.mId = lot.LotId;
            MenuEntry e = MatchEntry(BuildMenu(sim, Terrain.Singleton, hit), order);
            InteractionInstanceParameters ip = new InteractionInstanceParameters(e.Iop, sim,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                InteractionFlags.UserDirected, hit, null, false);
            InteractionInstance inst = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref ip);
            if (inst == null || !sim.InteractionQueue.Add(inst)) throw new McpException("the game refused to queue '" + e.Label + "'");
            r["queued"] = e.Label;
            r["actor"] = sim.FullName;
            r["queue"] = QueueList(sim);
        }

        // add_puddle {near: target, forward?: metres along the target's facing (negative = behind), right?: metres,
        //   no_add?: true, order?: "mop"}: the game's own PuddleManager.AddPuddle at that place; order "mop" then orders the selected Sim
        //   to mop it through the pie menu at that point.
        static object AddPuddleNear(Args a)
        {
            RequireLive();
            Vector3 p = PlaceNear(a);
            Dictionary<string, object> r = Util.Obj();
            r["pos"] = Pos(p);
            if (!a.Bool("no_add", false))
            {
                LotLocation loc = PuddleManager.AddPuddle(p);
                r["added"] = loc != LotLocation.Invalid;
            }
            string order = a.Str("order", null);
            if (order != null) OrderAtPoint(Util.ActiveSim(), p, order, r);
            return r;
        }

        // sim: a name or id, "pet" = the active household's first pet in the world (an adopted pet's name is the pool's), default the selected Sim
        static Sim OrderSim(Args a)
        {
            if (!a.Has("sim")) return Util.ActiveSim();
            if (a.Str("sim").Trim().ToLowerInvariant() != "pet") return Util.FindSim(a, "sim");
            Household hh = Household.ActiveHousehold;
            if (hh != null)
                foreach (Sim s in hh.AllActors)
                    if (s != null && s.IsPet && s.InWorld) return s;
            throw new McpException("the active household has no pet in the world");
        }

        // go_at {near: target, forward?, right?, sim?: as order_at, exact?: true}: go_here to the place given from a target
        //   (the door of a built layout), the same Go Here order.
        static object GoAt(Args a)
        {
            RequireLive();
            Vector3 p = PlaceNear(a);
            Dictionary<string, object> q = new Dictionary<string, object>();
            q["sim"] = OrderSim(a).FullName;
            q["x"] = (double)p.x; q["y"] = (double)p.y; q["z"] = (double)p.z;
            q["exact"] = a.Bool("exact", true);
            Dictionary<string, object> r = Util.Obj();
            r["pos"] = Pos(p);
            r["queue"] = GoHere(new Args(q));
            return r;
        }

        // order_at {near: target, forward?, right?, order: menu label part or "def:<class>", sim?: as OrderSim,
        //   list?: true}: the player's click on the floor at that place and the pie menu entry (a pet's Lie Down Here, Go Here
        //   is go_here). list: the menu's entries instead of an order.
        static object OrderAt(Args a)
        {
            RequireLive();
            Vector3 p = PlaceNear(a);
            Sim sim = OrderSim(a);
            Dictionary<string, object> r = Util.Obj();
            r["pos"] = Pos(p);
            if (a.Bool("list", false))
            {
                Lot lot = LotManager.GetLotAtPoint(p);
                if (lot == null) throw new McpException("no lot at the place");
                GameObjectHit hit = new GameObjectHit(GameObjectHitType.LotTerrain);
                hit.mPoint = p;
                hit.mId = lot.LotId;
                List<object> rows = new List<object>();
                foreach (MenuEntry e in BuildMenu(sim, Terrain.Singleton, hit))
                    rows.Add(e.Label + " [" + (e.Iop != null && e.Iop.InteractionDefinition != null ? e.Iop.InteractionDefinition.GetType().FullName : "?") + "]" + (e.Enabled ? "" : " (off)"));
                r["menu"] = rows;
                return r;
            }
            OrderAtPoint(sim, p, a.Str("order"), r);
            return r;
        }

        // add_trash {near: target, forward?, right?}: a trash pile on the floor at that place, as the game makes one
        //   (GlobalFunctions.CreateObject("TrashPileIndoor", ...), TrashPile.CreateFairyTrapTrashPiles' object).
        static object AddTrash(Args a)
        {
            RequireLive();
            Vector3 p = PlaceNear(a);
            LotLocation loc = default(LotLocation);
            World.GetLotLocationAtFloor(p, ref loc);
            GameObject g = GlobalFunctions.CreateObject("TrashPileIndoor", p, loc.mLevel, Vector3.UnitZ) as GameObject;
            if (g == null) throw new McpException("the game made no trash pile");
            CreatedObjects().Add(g.ObjectId.Value);
            Dictionary<string, object> r = Util.Obj();
            r["id"] = Util.Id(g.ObjectId.Value);
            r["type"] = g.GetType().Name;
            r["pos"] = Pos(g.Position);
            r["room"] = g.RoomId;
            r["in_world"] = g.InWorld;
            return r;
        }

        // add_platter {near: target, forward?, right?, recipe?: recipe key part (default the first group recipe), servings?: N}
        //   | {census: true}
        //   A finished group meal on the floor at that place, made as the game makes one (Recipe.CreateFinishedFood, Group):
        //   the cooking is skipped, Grab a Serving is the player's order. servings: the platter's count is set to N (the
        //   last-serving case). census: every platter of the selected Sim's lot with its servings left and users, and the
        //   single plates of the lot.
        static object AddPlatter(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            System.Reflection.FieldInfo left = typeof(Sims3.Gameplay.Objects.CookingObjects.ServingContainerGroup).GetField("mNumServingsLeft",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (!a.Bool("census", false))
            {
                Vector3 p = PlaceNear(a);
                string want = a.Str("recipe", "").ToLowerInvariant();
                Sims3.Gameplay.Objects.FoodObjects.Recipe recipe = null;
                foreach (Sims3.Gameplay.Objects.FoodObjects.Recipe c in Sims3.Gameplay.Objects.FoodObjects.Recipe.Recipes)
                    if (c != null && !string.IsNullOrEmpty(c.GroupServingContainer) && c.SingleServingContainer != "PlateChild"
                        && !Sims3.Gameplay.Objects.FoodObjects.Recipe.Snacks.Contains(c)
                        && (c.Key ?? "").ToLowerInvariant().IndexOf(want) >= 0) { recipe = c; break; }
                if (recipe == null) throw new McpException("no group recipe '" + want + "'");
                Sims3.Gameplay.Objects.CookingObjects.ServingContainerGroup g = recipe.CreateFinishedFood(
                    Sims3.Gameplay.Objects.FoodObjects.Recipe.MealQuantity.Group, Sims3.Gameplay.Objects.Quality.Nice)
                    as Sims3.Gameplay.Objects.CookingObjects.ServingContainerGroup;
                if (g == null) throw new McpException("recipe " + recipe.Key + " made no group serving");
                g.SetPosition(p);
                g.SetForward(Vector3.UnitZ);
                g.AddToWorld();
                g.BeingUsedAsServingContainer = true;
                if (a.Has("servings"))
                {
                    if (left == null) throw new McpException("ServingContainerGroup.mNumServingsLeft not found");
                    left.SetValue(g, (int)a.Long("servings"));
                }
                CreatedObjects().Add(g.ObjectId.Value);
                r["id"] = Util.Id(g.ObjectId.Value);
                r["type"] = g.GetType().Name;
                r["recipe"] = recipe.Key;
                r["pos"] = Pos(g.Position);
                r["room"] = g.RoomId;
                r["servings"] = g.NumServingsLeft;
                return r;
            }
            Lot lot = Util.ActiveSim().LotCurrent;
            List<object> rows = new List<object>(), plates = new List<object>();
            if (lot != null)
            {
                foreach (Sims3.Gameplay.Objects.CookingObjects.ServingContainerGroup g in lot.GetObjects<Sims3.Gameplay.Objects.CookingObjects.ServingContainerGroup>())
                {
                    Dictionary<string, object> o = Util.Obj();
                    o["id"] = Util.Id(g.ObjectId.Value);
                    o["type"] = g.GetType().Name;
                    o["servings"] = g.NumServingsLeft;
                    o["in_use"] = g.InUse;
                    o["line"] = g.SimLine != null ? g.SimLine.MemberCount() : 0;
                    rows.Add(o);
                }
                foreach (Sims3.Gameplay.Objects.CookingObjects.ServingContainerSingle s in lot.GetObjects<Sims3.Gameplay.Objects.CookingObjects.ServingContainerSingle>())
                {
                    Sim holder = s.Parent as Sim;
                    plates.Add(s.GetType().Name + (holder != null ? " held by " + holder.FullName : s.Parent != null ? " on " + s.Parent.GetType().Name : " loose"));
                }
            }
            r["platters"] = rows;
            r["plates"] = plates;
            r["plate_count"] = plates.Count;
            return r;
        }

        // contemplate {sim?: as OrderSim, user?: false}: the Genius trait's Contemplate, queued as the game's own
        //   ContemplateSomethingAutonomous does (the pie menu has no entry for it: a Sim only starts it by itself):
        //   Terrain.Contemplate at the Sim's own place, autonomous unless user: true. The answer lists the objects the game
        //   can pick (within TraitTuning.GeniusTraitContemplateDistance, repossessable, no jig) with their rooms. The Sim must
        //   not have the Fascinated moodlet (the game's test; setup_sims moodlets: true takes it off).
        static object Contemplate(Args a)
        {
            RequireLive();
            Sim sim = OrderSim(a);
            bool user = a.Bool("user", false);
            Terrain.Contemplate c = Terrain.Contemplate.Singleton.CreateInstance(Terrain.Singleton, sim,
                new InteractionPriority(user ? InteractionPriorityLevel.UserDirected : InteractionPriorityLevel.Autonomous), !user, true) as Terrain.Contemplate;
            if (c == null) throw new McpException("the game made no Contemplate");
            c.SetDestination(sim.Position);
            List<object> picks = new List<object>();
            int near = 0;
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(sim.Position, Sims3.Gameplay.TuningValues.TraitTuning.GeniusTraitContemplateDistance))
            {
                near++;
                if (o.CanBeRepossessed(false) && !(o is Jig)) picks.Add(o.GetType().Name + " room " + o.RoomId);
            }
            Dictionary<string, object> r = Util.Obj();
            r["actor"] = sim.FullName;
            r["at"] = Pos(sim.Position);
            r["room"] = sim.RoomId;
            r["picks"] = picks;
            r["objects_near"] = near;
            r["queued"] = sim.InteractionQueue.Add(c);
            r["queue"] = QueueList(sim);
            return r;
        }

        // route_points {step?: 0.25}: the points of the last sim_route_to plan ("@route"), one every `step` metres along
        //   its way (x,z pairs), and its length: where a plan passes a row of furniture.
        static object RoutePoints(Args a)
        {
            if (sLastRoute == null || sLastRoute.RouteHandle == 0) throw new McpException("no live route: run sim_route_to first");
            float len = sLastRoute.GetRouteLength(), step = (float)a.Double("step", 0.25);
            if (step < 0.05f) step = 0.05f;
            List<object> pts = new List<object>();
            for (float d = 0f; d <= len && pts.Count < 400; d += step)
            {
                Vector3 tangent = Vector3.Zero;
                int node = 0;
                Vector3 p = sLastRoute.GetPointAtDistanceOnPathway(d, ref tangent, ref node);
                pts.Add(p.x.ToString("0.00") + "," + p.z.ToString("0.00"));
            }
            Dictionary<string, object> r = Util.Obj();
            r["length"] = len;
            r["points"] = pts;
            return r;
        }

        // box_in {sim?: as OrderSim, type?: "Fridge", x?, z?}: eight objects of that class bought (buy_object type) and put on the
        //   eight tiles round the Sim, each with its back to the Sim. A fridge's footprint is flush at its back and sides and
        //   short at its front: a ring with a front towards the Sim or towards a neighbour leaves a slit the planner walks
        //   through (session p4-m1, 2026-11-22: rings of counters and of fridges all facing one way did not hold a Sim).
        //   x, z: where the Sim must stand (within 0.5 m), else nothing is bought (run sr-r2: the Sim was still on a community
        //   lot when the step came, and the fridges went there). The answer has that Sim's plan to a place 6-8 m away
        //   (boxed: it fails).
        static object BoxIn(Args a)
        {
            RequireLive();
            Sim sim = OrderSim(a);
            Vector3 c = sim.Position;
            if (a.Has("x") && a.Has("z"))
            {
                float dx = c.x - (float)a.Double("x"), dz = c.z - (float)a.Double("z");
                if (dx * dx + dz * dz > 0.25f) throw new McpException("box_in: " + sim.FullName + " stands at " + c.x.ToString("0.0") + ", " + c.z.ToString("0.0") + ", not at the place");
            }
            string type = a.Str("type", "Fridge");
            // dx, dz, facing (degrees, 0 = +z): the three tiles on +z face +z, the three on -z face -z, the two beside face away
            int[] ring = { -1, 1, 0, 0, 1, 0, 1, 1, 0, -1, -1, 180, 0, -1, 180, 1, -1, 180, -1, 0, 270, 1, 0, 90 };
            List<object> put = new List<object>();
            for (int i = 0; i < ring.Length; i += 3)
            {
                Vector3 p = new Vector3(c.x + ring[i], c.y, c.z + ring[i + 1]);
                Dictionary<string, object> q = new Dictionary<string, object>();
                q["type"] = type;
                q["x"] = (double)p.x; q["y"] = (double)p.y; q["z"] = (double)p.z;
                Dictionary<string, object> bought = BuyOfType(new Args(q)) as Dictionary<string, object>;
                if (bought == null || (string)bought["where"] != "placed") throw new McpException("box_in: object " + (i / 3) + " was not placed");
                Dictionary<string, object> t = new Dictionary<string, object>();
                t["target"] = bought["id"];
                GameObject g = FindTarget(new Args(t), "target");
                g.SetPosition(p);
                g.SetForward(FacingVector(ring[i + 2]));
                put.Add(Pos(g.Position));
            }
            Route probe = sim.CreateRoute();
            probe.DoRouteFail = false;
            probe.PlanToPointRadialRange(c, 6f, 8f, RouteDistancePreference.PreferNearestToRouteOrigin, RouteOrientationPreference.NoPreference, 0UL, null);
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = sim.FullName;
            r["centre"] = Pos(c);
            r["objects"] = put;
            r["plan_out"] = probe.PlanResult.mType.ToString();
            r["boxed"] = !probe.PlanResult.Succeeded();
            return r;
        }

        // pet_jigs {unlock?: true}: the floor jigs of lying pets on the selected Sim's lot: place, pet, and whether the
        //   jig's routing footprint is locked with its own id as key (SocialJig.OnStartup). unlock: a probe, not a player
        //   step: takes that lock off (RoutingFootprintLocking.RemoveLockKey), to see what the lock does to other Sims' plans.
        static object PetJigs(Args a)
        {
            RequireLive();
            Lot lot = Util.ActiveSim().LotCurrent;
            List<object> rows = new List<object>();
            if (lot != null)
                foreach (Sims3.Gameplay.ActorSystems.PetSurfacePosture.PetSurfaceFloorJig j in lot.GetObjects<Sims3.Gameplay.ActorSystems.PetSurfacePosture.PetSurfaceFloorJig>())
                {
                    Dictionary<string, object> o = Util.Obj();
                    o["id"] = Util.Id(j.ObjectId.Value);
                    o["at"] = Pos(j.Position);
                    o["pet"] = j.ContainedSim != null ? j.ContainedSim.FullName : null;
                    if (a.Bool("unlock", false)) RoutingFootprintLocking.RemoveLockKey(j.ObjectId, RoutingFootprintLocking.LockType.MustHaveKey, j.ObjectId.Value);
                    o["locked"] = RoutingFootprintLocking.HasLockKey(j.ObjectId, RoutingFootprintLocking.LockType.MustHaveKey, j.ObjectId.Value);
                    rows.Add(o);
                }
            Dictionary<string, object> r = Util.Obj();
            r["jigs"] = rows;
            return r;
        }

        // put_in_front {target: the object to move, of: the object it goes in front of, forward?: metres (default 1)}
        //   Sets the place directly (as the moveobjects cheat lets a player): the Buy tool refuses a fridge on the tile in
        //   front of a toilet (run bu-r9, 2026-11-22: "the tool placed nothing"), which is the case blockeduse is for.
        static object PutInFront(Args a)
        {
            RequireLive();
            GameObject o = FindTarget(a, "target"), of = FindTarget(a, "of");
            Vector3 f = of.ForwardVector;
            f.y = 0f;
            float len = f.Length();
            if (len < 0.001f) throw new McpException("'of' has no facing");
            Vector3 p = of.Position + f * ((float)a.Double("forward", 1) / len);
            o.SetPosition(p);
            Dictionary<string, object> r = Util.Obj();
            r["moved"] = o.GetType().Name;
            r["position"] = Pos(o.Position);
            return r;
        }

        // slot_close {target, close?: "fridge" (catalogue name part), slot?: 0}: one object of that product on the target's
        //   routing slot `slot` (RoutingSlot_0..3), as bed_setup does for a bed side: the slot is closed by furniture. A
        //   shower's entry slot lies beside its facing, and the Buy tool does not hit the same tile in every run (ws-r1).
        //   Answers the selected Sim's plan to that slot before and after.
        static object SlotClose(Args a)
        {
            RequireLive();
            GameObject t = FindTarget(a, "target");
            Slot[] names = { Slot.RoutingSlot_0, Slot.RoutingSlot_1, Slot.RoutingSlot_2, Slot.RoutingSlot_3 };
            long n = a.Long("slot", 0);
            if (n < 0 || n >= names.Length) throw new McpException("slot: 0-3");
            Slot slot = names[n];
            Vector3 at = t.GetPositionOfSlot(slot);
            string close = a.Str("close", "fridge").ToLowerInvariant();
            BuildBuyProduct prod = null;
            foreach (BuildBuyProduct c in Catalog())
                if ((c.CatalogName ?? "").ToLowerInvariant().IndexOf(close) >= 0) { prod = c; break; }
            if (prod == null) throw new McpException("no catalogue product '" + close + "'");
            Sim sim = Util.ActiveSim();
            Dictionary<string, object> r = Util.Obj();
            r["target"] = t.GetType().Name;
            r["slot_at"] = Pos(at);
            r["plan_before"] = SlotPlan(sim, t, slot);
            GameObject g = GlobalFunctions.CreateObjectOutOfWorld(prod.ProductResourceKey) as GameObject;
            if (g == null) throw new McpException("the product could not be created");
            g.SetPosition(at);
            g.SetForward(t.ForwardVector);
            g.AddToWorld();
            CreatedObjects().Add(g.ObjectId.Value);
            r["closed_with"] = prod.CatalogName;
            r["plan"] = SlotPlan(sim, t, slot);
            return r;
        }

        static bool SlotPlan(Sim sim, GameObject t, Slot slot)
        {
            Route route = sim.CreateRoute();
            return route != null && route.PlanToSlot(t, slot).Succeeded();
        }

        // bed_setup {target?: "@type:Bed", unmake?: all | closed | none (default none), close?: catalogue name part}
        //   Reports each part: area, made, in use, whether the selected Sim's plan to its routing slots succeeds, the slots.
        //   close: when every part can be reached, objects of that product are put on the first part's routing slots
        //   (a bed side closed by furniture). unmake: sets the parts' made flags only (the look is the model's pose).
        static object BedSetup(Args a)
        {
            RequireLive();
            Dictionary<string, object> q = new Dictionary<string, object>();
            q["target"] = a.Str("target", "@type:Bed");
            Bed bed = FindTarget(new Args(q), "target") as Bed;
            if (bed == null || bed.PartComp == null) throw new McpException("target is no bed with parts");
            Sim sim = Util.ActiveSim();
            Dictionary<string, object> r = Util.Obj();
            r["bed"] = bed.GetType().Name;
            r["at"] = Pos(bed.Position);
            List<BedData> parts = new List<BedData>(bed.PartComp.PartDataList.Values);
            string close = a.Str("close", null);
            int closed = 0;
            foreach (BedData p in parts) if (!BedPlan(sim, bed, p)) closed++;
            if (close != null && closed == 0 && parts.Count > 1)
            {
                BuildBuyProduct prod = null;
                foreach (BuildBuyProduct c in Catalog())
                    if ((c.CatalogName ?? "").ToLowerInvariant().IndexOf(close.ToLowerInvariant()) >= 0) { prod = c; break; }
                if (prod == null) throw new McpException("no catalogue product '" + close + "'");
                List<object> put = new List<object>();
                foreach (Slot s in parts[0].RoutingSlot)
                {
                    GameObject g = GlobalFunctions.CreateObjectOutOfWorld(prod.ProductResourceKey) as GameObject;
                    if (g == null) continue;
                    g.SetPosition(bed.GetPositionOfSlot(s));
                    g.SetForward(bed.ForwardVector);
                    g.AddToWorld();
                    CreatedObjects().Add(g.ObjectId.Value);
                    put.Add(Pos(g.Position));
                }
                r["closed_with"] = prod.CatalogName;
                r["closed_at"] = put;
            }
            string unmake = a.Str("unmake", "none");
            List<object> rows = new List<object>();
            foreach (BedData p in parts)
            {
                bool plan = BedPlan(sim, bed, p);
                if (unmake == "all" || (unmake == "closed" && !plan)) p.BedMade = false;
                Dictionary<string, object> o = Util.Obj();
                o["area"] = p.Area.ToString();
                o["made"] = p.BedMade;
                o["in_use"] = p.InUse;
                o["plan"] = plan;
                List<object> slots = new List<object>();
                if (p.RoutingSlot != null) foreach (Slot s in p.RoutingSlot) slots.Add(Pos(bed.GetPositionOfSlot(s)));
                o["slots"] = slots;
                rows.Add(o);
            }
            r["parts"] = rows;
            return r;
        }

        static bool BedPlan(Sim sim, Bed bed, BedData part)
        {
            if (part.RoutingSlot == null || part.RoutingSlot.Length == 0) return false;
            Route route = sim.CreateRoute();
            return route != null && route.PlanToSlot(bed, part.RoutingSlot).Succeeded();
        }
    }
}
