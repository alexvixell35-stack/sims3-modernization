using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro stove-oven-layout (2026-11-07): the player's failures are all on the oven path, BakingDish_Prepare then BakingDish_CookInOven, result
    // FailedBecauseNoPathToDestination to the stove's RoutingSlot_0 from about 1 m, candidates 1/1/0. stove_wall_cook {oven: true} orders a baked recipe, SwOvenWatch logs
    // plans and objects at each stage. Lines "stove-oven:" and the usual "stove-wall:" ones, all with the savecleanup: prefix.
    //   stove_oven_setup {others_tile}: the active Sim has Cooking at its top level and knows every baked recipe, the rest of the household stands on others_tile; error if none fits the meal time.
    //   stove_oven_status {}: {seen, in_oven, done}: done = CookInOven ran, or the order failed, or the Sim stopped without it (two idle polls; "chain ended" lines say why). Poll with until.
    //   stove_oven_takeout {arm, at: "near" | "past"}: with the dish in the oven the Sim stops, stands 1 m out of the slot (near) or on its spot past the wall
    //     (past), plans, and CookInOven is ordered again.
    //   stove_oven_clear {arm}: cancels, removes dish and food, so the next arm can cook.
    public static partial class Handlers
    {
        static GameObject sSwDish;
        static bool sSwOven, sSwProbing, sSwAtOven, sSwInOven, sSwOvenSeen;
        static string sSwOvenFailed;
        static int sSwIdlePolls;

        static void RegisterStoveOven()
        {
            Dispatcher.Register("stove_oven_setup", StoveOvenSetup);
            Dispatcher.Register("stove_oven_status", StoveOvenStatus);
            Dispatcher.Register("stove_oven_takeout", StoveOvenTakeout);
            Dispatcher.Register("stove_oven_clear", StoveOvenClear);
        }

        // the Bake and Mix, Bake processes (CookingProcessData), the only ones through BakingDish. From the stove EA picks at random among the recipe's stove processes (CookingProcess ctor): all must bake
        static bool SwIsOven(Recipe rc)
        {
            if (rc == null || rc.CookingProcessData == null) return false;
            bool any = false;
            foreach (CookingStepsAndTargetPair pair in rc.CookingProcessData.OrderOfStepsList)
            {
                if (pair.PrimaryCookObjectType == null || !pair.PrimaryCookObjectType.IsAssignableFrom(typeof(Stove))) continue;
                if (!pair.Steps.Contains(CookingStep.PutInBakingTray) || !pair.Steps.Contains(CookingStep.CookInOven)) return false;
                any = true;
            }
            return any;
        }

        static string SwWho(ulong id)
        {
            if (id == 0) return "";
            GameObject o = GameObject.GetObject(new ObjectGuid(id));
            return " " + (o != null ? SwKind(o) + " at " + V3(o.Position) : "no object");
        }

        // a region's footprint is a "DynamicSlot" with script class Null, Model hidden (DynamicRegionBase.UpdateFootprint); other Nulls are props
        static string SwKind(GameObject o)
        {
            return o is Null && (o.GetHiddenFlags() & HiddenFlags.Model) != 0 ? "region footprint (Null, DynamicSlot)" : o.GetType().Name;
        }

        static object StoveOvenSetup(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            Cooking c = s.SkillManager.GetSkill<Cooking>(SkillNames.Cooking) ?? s.SkillManager.AddElement(SkillNames.Cooking) as Cooking;
            if (c == null) throw new McpException("no Cooking skill on the active Sim");
            // CookingProcess.InteractionTest refuses every step below the recipe's level: run 1 had level 0, the menu offered Ratatouille by the known list, CuttingBoard_Prepare never started
            c.ForceSkillLevelUp(c.MaxSkillLevel);
            if (c.KnownRecipes == null) c.KnownRecipes = new List<string>();
            Recipe.MealTime meal = Food.GetCurrentMealTime();
            int known = 0;
            List<string> fits = new List<string>();
            foreach (Recipe rc in Recipe.Recipes)
            {
                if (!SwIsOven(rc)) continue;
                known++;
                if (!c.KnownRecipes.Contains(rc.Key)) c.KnownRecipes.Add(rc.Key);  // the list, not AddRecipe: no learn events, no opportunities
                if (rc.IsAvailableFor(meal)) fits.Add(rc.Key);
            }
            if (fits.Count == 0) throw new McpException("no baked recipe of " + known + " fits " + meal + ": set the clock for one");
            Vector3 park = SwTile(HomeLot(), a.List("others_tile"), 0);
            foreach (Sim o in s.Household.Sims)
            {
                if (o == s) continue;
                o.InteractionQueue.CancelAllInteractions();
                o.SetPosition(park);
            }
            SwLine("stove-oven: setup cooking level " + c.SkillLevel + ", " + known + " baked recipes known, " + fits.Count + " fit " + meal + ": " + string.Join(" ", fits.GetRange(0, Math.Min(fits.Count, 12)).ToArray()));
            Dictionary<string, object> r = Util.Obj();
            r["baked"] = known;
            r["fit"] = fits.Count;
            r["meal"] = meal.ToString();
            return r;
        }

        // one stage of the bake: the three plans, then everything near the slot, hidden too if the query gives it. Own plans stay out of the route lines.
        static void SwOvenProbe(Sim s, string arm, string label)
        {
            if (sScQuiet) return;  // stove-oven-cancel: the flags only, a cell must not lose ticks here
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            sSwProbing = true;
            try
            {
                SwPlan(s, arm, label + " to-slot", false, 0, slot);
                SwPlan(s, arm, label + " to-slot-point", false, 1, slot);
                SwPlan(s, arm, label + " own-spot", false, 1, s.Position);
            }
            finally { sSwProbing = false; }
            GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(slot, 3f);
            string line = "";
            for (int i = 0; i < near.Length && i < 60; i++)
            {
                GameObject o = near[i];
                bool covers = false;
                uint[] hashes = o.GetFootprintNameHashes();
                for (int k = 0; hashes != null && k < hashes.Length && k < 4 && !covers; k++)
                    covers = World.ObjectIsFootprintEnabled(o.ObjectId, hashes[k]) && World.ObjectFootprintContainsPoint(o.ObjectId, hashes[k], slot);
                line += SwKind(o) + " 0x" + o.ObjectId.Value.ToString("X") + " " + (o.Position - slot).Length().ToString("0.0", CultureInfo.InvariantCulture) + " m"
                    + (o.GetHiddenFlags() != HiddenFlags.Nothing ? " hidden " + o.GetHiddenFlags() : "") + (o.InWorld ? "" : " out of world") + (covers ? " COVERS SLOT" : "") + "; ";
                if (line.Length > 330) { SwLine("stove-oven: arm " + arm + " " + label + " near " + line); line = ""; }
            }
            SwLine("stove-oven: arm " + arm + " " + label + " near " + line + "objects within 3 m " + near.Length);
        }

        // SwWatch, once per poll while an oven order runs
        static void SwOvenWatch(Sim s, string arm, InteractionInstance cur, double t)
        {
            string n = cur.GetType().Name;
            if (n.StartsWith("BakingDish_", StringComparison.Ordinal)) sSwDish = cur.Target as GameObject;
            if (n == "BakingDish_Prepare" && !sSwAtOven && (s.Position - sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0)).Length() <= 1.2f)
            {
                sSwAtOven = true;
                SwLine("stove-oven: arm " + arm + " at the oven at " + t + " s");
                SwOvenProbe(s, arm, "put-in-oven");
            }
            if (sSwDish != null && !sSwInOven && sSwDish.Parent is Stove)
            {
                sSwInOven = true;
                SwLine("stove-oven: arm " + arm + " dish in the oven at " + t + " s");
                SwOvenProbe(s, arm, "in-oven");
            }
            if (n == "BakingDish_CookInOven" && !sSwOvenSeen)
            {
                sSwOvenSeen = true;
                SwLine("stove-oven: arm " + arm + " CookInOven seen at " + t + " s");
                SwOvenProbe(s, arm, "cook-in-oven");
            }
        }

        // the Sim stopped before CookInOven: where the food is and whether its step would pass EA's own test
        static void SwChainEnd(Sim s)
        {
            Cooking c = s.SkillManager.GetSkill<Cooking>(SkillNames.Cooking);
            SwLine("stove-oven: arm " + sSwArm + " chain ended after " + string.Join(">", sSwSteps.ToArray()) + ", exit " + s.ExitReason + ", cooking level " + (c != null ? c.SkillLevel : -1) + ", at " + V3(s.Position));
            foreach (GameObject o in sSwStove.LotCurrent.GetObjects<GameObject>())
            {
                IPartOfCookingProcess part = o as IPartOfCookingProcess;
                if (part == null || part.CookingProcess == null) continue;
                CookingProcess p = part.CookingProcess;
                SwLine("stove-oven: arm " + sSwArm + " chain food " + o.GetType().Name + " on " + (o.Parent != null ? o.Parent.GetType().Name : "nothing") + ", step " + p.CurrentStep + ", recipe " + p.Recipe.Key + " level " + p.Recipe.CookingSkillLevelRequired + ", in use " + o.InUse + ", step test " + p.InteractionTest(s, o));
            }
        }

        static object StoveOvenStatus(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            bool idle = System.Environment.TickCount - sSwT0 > 20000 && s.CurrentInteraction == null && s.InteractionQueue.Count == 0;
            sSwIdlePolls = idle ? sSwIdlePolls + 1 : 0;
            if (sSwIdlePolls == 2 && !sSwOvenSeen) SwChainEnd(s);
            Dictionary<string, object> r = Util.Obj();
            r["seen"] = sSwOvenSeen;
            r["in_oven"] = sSwInOven;
            r["failed"] = sSwOvenFailed;
            r["done"] = sSwOvenSeen || sSwOvenFailed != null || sSwIdlePolls >= 2;  // a job with no CookInOven still runs the next arm, logRequire fails it at the end
            return r;
        }

        static void SwStop(Sim s)
        {
            for (int i = 0; i < 12; i++)
            {
                s.InteractionQueue.CancelAllInteractions();
                Simulator.Sleep(20);
                if (s.CurrentInteraction == null) break;
            }
        }

        static object StoveOvenTakeout(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            string arm = a.Long("arm", -1).ToString(), at = a.Str("at", "near"), label = "takeout " + at;
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = false;
            if (sSwDish == null || !(sSwDish.Parent is Stove))
            {
                SwLine("stove-oven: arm " + arm + " " + label + " no dish in the oven");
                return r;
            }
            SwStop(s);
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            s.SetPosition(at == "near" ? slot + sSwStove.ForwardVector * 1f : sSwSpot);
            Simulator.Sleep(20);
            SwOvenProbe(s, arm, label);
            SwWatch(s, arm, label);
            GameObjectHit hit = HitFor(sSwDish, a);
            foreach (InteractionObjectPair iop in sSwDish.GetAllInteractionsForActor(s))
            {
                Type def = iop.InteractionDefinition.GetType();
                if (def.DeclaringType == null || def.DeclaringType.Name != "BakingDish_CookInOven") continue;
                InteractionInstanceParameters p = new InteractionInstanceParameters(iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
                InteractionInstance ii = iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                bool test = ii.Test(), q = s.InteractionQueue.Add(ii);
                SwLine("stove-oven: arm " + arm + " " + label + " order from " + V3(s.Position) + ", test " + test + ", queued " + q);
                r["test"] = test;
                r["queued"] = q;
                return r;
            }
            SwLine("stove-oven: arm " + arm + " " + label + " the dish has no CookInOven");
            return r;
        }

        static object StoveOvenClear(Args a)
        {
            RequireLive();
            Sim s = Util.ActiveSim();
            SwWatchStop();
            SwStop(s);
            int n = 0;
            List<GameObject> gone = new List<GameObject>();
            foreach (Slot slot in new Slot[] { Slot.ContainmentSlot_2, Slot.ContainmentSlot_3 })
            {
                GameObject held = sSwStove.GetContainedObject(slot) as GameObject;
                if (held != null) gone.Add(held);
            }
            foreach (GameObject o in sSwStove.LotCurrent.GetObjects<GameObject>())
                if ((o is IPartOfCookingProcess || o is ServingContainer || o is Snack) && !gone.Contains(o)) gone.Add(o);
            foreach (GameObject o in gone) { o.Destroy(); n++; }
            sSwDish = null;
            SwLine("stove-oven: arm " + a.Long("arm", -1) + " clear removed " + n + ", stove cooking " + sSwStove.IsCookingSomething() + ", usable counter " + Food.LotHasUsableCounter(sSwStove.LotCurrent));
            Dictionary<string, object> r = Util.Obj();
            r["removed"] = n;
            return r;
        }
    }
}
