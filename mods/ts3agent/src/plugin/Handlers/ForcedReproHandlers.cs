using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Beds;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Agent
{
    // Set-up steps of the forced repros of docs/686 §12 and §13 (reachuse-force, makebedboth-force). Test set-up only:
    // the orders themselves stay the player's (do_interaction).
    public static partial class Handlers
    {
        static void RegisterForcedRepro()
        {
            Dispatcher.Register("add_puddle", AddPuddleNear);
            Dispatcher.Register("bed_setup", BedSetup);
            Dispatcher.Register("put_in_front", PutInFront);
        }

        // add_puddle {near: target, forward?: metres along the target's facing (negative = behind), right?: metres,
        //   no_add?: true, order?: "mop"}: the game's own PuddleManager.AddPuddle at that place; order "mop" then orders the selected Sim
        //   to mop it through the pie menu at that point (do_interaction on the lot's floor).
        static object AddPuddleNear(Args a)
        {
            RequireLive();
            GameObject t = FindTarget(a, "near");
            Vector3 f = t.ForwardVector;
            f.y = 0f;
            float len = f.Length();
            if (len < 0.001f) throw new McpException("the target has no facing");
            f = f * (1f / len);
            Vector3 right = new Vector3(-f.z, 0f, f.x);
            Vector3 p = t.Position + f * (float)a.Double("forward", 0) + right * (float)a.Double("right", 0);
            Dictionary<string, object> r = Util.Obj();
            r["pos"] = Pos(p);
            if (!a.Bool("no_add", false))
            {
                LotLocation loc = PuddleManager.AddPuddle(p);
                r["added"] = loc != LotLocation.Invalid;
            }
            Lot lot = LotManager.GetLotAtPoint(p);
            string order = a.Str("order", null);
            if (order != null)
            {
                if (lot == null) throw new McpException("no lot at the puddle's place");
                // the player's click on a lot's floor: the terrain's menu with a lot-terrain hit (Terrain.MopPuddle's Test wants it)
                Sim sim = Util.ActiveSim();
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
                r["queue"] = QueueList(sim);
            }
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
