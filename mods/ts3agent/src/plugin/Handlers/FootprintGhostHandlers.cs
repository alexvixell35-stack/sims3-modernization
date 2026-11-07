using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

// Repro footprint-ghost (docs/89 §16, §17): the lost removal made on demand. EA's ImpassableRegion sequence around a slot, Cleanup while
// a route job is held at its start by ts3native ([Discourage] GhostTest ops 20-29 on the 0x87 channel, no counter offsets, so the same
// handler drives a master and a v0.2.9 pair), then a plan to the slot. The region's DynamicSlot object goes with its footprint still
// in the lot map when the removal is lost. stove: from past the wall. oven: same region, plan from 1 m, as the player report.
// fridge: the fridge's routing slot. jig: a SocialJigOnePerson on the fridge slot, destroyed in the hold, the shape stove-wall-repeat
// caught in play: any object destroyed with a job running loses its removal the same way.
namespace Ts3Agent
{
    public static partial class Handlers
    {
        static void RegisterFootprintGhost()
        {
            Dispatcher.Register("footprint_ghost_run", FootprintGhostRun);
            Dispatcher.Register("footprint_ghost_check", FootprintGhostCheck);
            Dispatcher.Register("footprint_ghost_op", FootprintGhostOp);
            Dispatcher.Register("footprint_ghost_verdict", FootprintGhostVerdict);
        }

        static readonly int[,] sFgTally = new int[2, 4];  // per arm: reps, job held, ghosts, plans failed
        static bool sFgFar;

        // Sam plans somewhere else: the job the Cleanup falls into. Two ends, so each order is a real route.
        static void FgStartJob(Sim sam, Lot lot)
        {
            sFgFar = !sFgFar;
            Vector3 pos = WalkableClick(sam, sFgFar ? lot.EntryPoint() : sSwSpot);
            InteractionInstance ii = Terrain.GoHere.GetSingleton(sam, pos).CreateInstance(Terrain.Singleton, sam,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            SetClickHit(ii, pos);
            if (!sam.InteractionQueue.Add(ii)) throw new McpException("could not queue Sam's Go Here");
        }

        static object FootprintGhostRun(Args a)
        {
            RequireLive();
            if (sSwStove == null) throw new McpException("stove_wall_setup first");
            int arm = (int)a.Long("arm", -1);
            string target = a.Str("target", "stove"), armS = arm.ToString(CultureInfo.InvariantCulture);
            bool keep = a.Bool("keep", false), jig = target == "jig", fridge = target == "fridge" || jig, oven = target == "oven";
            Sim s = Util.ActiveSim(), sam = null;
            foreach (Sim o in s.Household.Sims) if (o != s) { sam = o; break; }
            if (sam == null) throw new McpException("needs a second Sim in the household");
            if (!MemSend(27, arm == 1 ? 1u : 0u)) throw new McpException("no footprint ghost ops: ini [Discourage] GhostTest=1, [UserDiag] on");
            GameObject obj = fridge ? (GameObject)sSwFridge : sSwStove;
            int kind = fridge ? 2 : 0;
            Vector3 slot = obj.GetPositionOfSlot(Slot.RoutingSlot_0), from = oven ? slot + sSwStove.ForwardVector * 1f : sSwSpot;
            SwPlace(s, false);
            s.SetPosition(from);
            Simulator.Sleep(10);
            string before = SwPlan(s, armS, "ghost " + target + " before", false, kind, slot);
            ImpassableRegion region = null;
            GameObject fp;
            if (jig)
            {
                fp = GlobalFunctions.CreateObjectOutOfWorld("SocialJigOnePerson") as GameObject;
                if (fp == null) throw new McpException("no SocialJigOnePerson");
                fp.SetPosition(slot);
                fp.AddToWorld();
            }
            else
            {
                s.SetPosition(slot);
                Simulator.Sleep(10);
                region = new ImpassableRegion();
                region.AddMember(s);
                region.AddMember(obj);
                region.UpdateFootprint();
                Simulator.Sleep(10);
                fp = region.DynamicFootprint;
                if (fp == null) throw new McpException("the region made no footprint object");
            }
            Simulator.Sleep(10);
            ulong id = fp.ObjectId.Value;
            MemSend(23, (uint)id);
            MemSend(24, (uint)(id >> 32));
            MemSend(20, 1500);
            FgStartJob(sam, s.LotCurrent);
            bool held = false;
            for (int i = 0; i < 300 && !held; i++)
            {
                held = MemSend(21, 0);
                if (!held) Simulator.Sleep(1);
            }
            if (jig) fp.Destroy(); else region.Cleanup();
            MemSend(22, 0);
            Simulator.Sleep(30);
            bool ghost = MemSend(25, 0);  // applies what is queued first
            sam.InteractionQueue.CancelAllInteractions();
            s.SetPosition(from);
            Simulator.Sleep(10);
            string after = SwPlan(s, armS, "ghost " + target + " after", false, kind, slot);
            bool failed = after.IndexOf("Failed", StringComparison.Ordinal) >= 0;
            if (ghost && !keep) MemSend(26, 0);
            if (arm == 0 || arm == 1)
            {
                sFgTally[arm, 0]++;
                if (held) sFgTally[arm, 1]++;
                if (ghost) sFgTally[arm, 2]++;
                if (failed) sFgTally[arm, 3]++;
                // the queue's verdict lines: nothing in the repro calls footprint_ghost_verdict, so each run writes the running total (the last line per arm is the verdict)
                SwLine("footprint-ghost: verdict arm " + arm + " reps " + sFgTally[arm, 0] + " held " + sFgTally[arm, 1] + " ghosts " + sFgTally[arm, 2] + " plans failed " + sFgTally[arm, 3]);
            }
            SwLine("footprint-ghost: arm " + armS + " " + target + " id 0x" + id.ToString("X16") + " job held " + (held ? 1 : 0) + ", map entry " + (ghost ? 1 : 0)
                + ", plan " + (failed ? "failed" : "ok") + (before.IndexOf("Failed", StringComparison.Ordinal) >= 0 ? ", already failed before the region" : ""));
            Dictionary<string, object> r = Util.Obj();
            r["id"] = "0x" + id.ToString("X16");
            r["held"] = held;
            r["ghost"] = ghost;
            r["failed"] = failed;
            return r;
        }

        // after a reload: the id of the last run stays in ts3native, the stove is found again
        static object FootprintGhostCheck(Args a)
        {
            RequireLive();
            Lot lot = HomeLot();
            foreach (Stove st in lot.GetObjects<Stove>()) { sSwStove = st; break; }
            if (sSwStove == null) throw new McpException("no stove on the home lot");
            string label = a.Str("label", "check");
            bool ghost = MemSend(25, 0);
            Sim s = Util.ActiveSim();
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            string line = SwPlan(s, "0", "ghost " + label, false, 0, slot);
            bool failed = line.IndexOf("Failed", StringComparison.Ordinal) >= 0;
            SwLine("footprint-ghost: check " + label + " map entry " + (ghost ? 1 : 0) + ", plan " + (failed ? "failed" : "ok"));
            Dictionary<string, object> r = Util.Obj();
            r["ghost"] = ghost;
            r["failed"] = failed;
            return r;
        }

        static object FootprintGhostOp(Args a)
        {
            bool ok = MemSend((int)a.Long("op"), (uint)a.Long("value", 0));
            Dictionary<string, object> r = Util.Obj();
            r["ok"] = ok;
            return r;
        }

        static object FootprintGhostVerdict(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            for (int arm = 0; arm < 2; arm++)
            {
                SwLine("footprint-ghost: verdict arm " + arm + " reps " + sFgTally[arm, 0] + " held " + sFgTally[arm, 1] + " ghosts " + sFgTally[arm, 2] + " plans failed " + sFgTally[arm, 3]);
                r["arm" + arm] = sFgTally[arm, 0] + "/" + sFgTally[arm, 1] + "/" + sFgTally[arm, 2] + "/" + sFgTally[arm, 3];
            }
            return r;
        }
    }
}
