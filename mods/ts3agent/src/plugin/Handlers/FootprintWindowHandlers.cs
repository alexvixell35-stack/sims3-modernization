using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.SimIFace;

// Repro footprint-window (docs/89 §18): the one-off result 16 of the player report on v0.2.10. As footprint-ghost, but the plan runs right
// after the region's Cleanup with the job still held, so it meets the queued removal. Needs a /DTS3N_RFTEST build (v0210d): ops 30-34,
// [Discourage] GhostTest=1. LostRemovalFix stays on in both arms; arm 0 = RemovalFirst off, arm 1 = on.
// Route.Plan on a route already given its destination: PlanToSlot's setup calls could wait for the held job and close the window.
namespace Ts3Agent
{
    public static partial class Handlers
    {
        static void RegisterFootprintWindow()
        {
            Dispatcher.Register("footprint_window_run", FootprintWindowRun);
            Dispatcher.Register("footprint_window_verdict", FootprintWindowVerdict);
        }

        static readonly int[,] sFwTally = new int[2, 5];  // per arm: reps, job held, window plans failed, later plans failed, map entries

        static void FwVerdictLines()
        {
            for (int arm = 0; arm < 2; arm++)
                SwLine("footprint-window: verdict arm " + arm + " reps " + sFwTally[arm, 0] + " held " + sFwTally[arm, 1] + " window fails " + sFwTally[arm, 2]
                    + " later fails " + sFwTally[arm, 3] + " map entries " + sFwTally[arm, 4]);
        }

        static object FootprintWindowRun(Args a)
        {
            RequireLive();
            if (sSwStove == null) throw new McpException("stove_wall_setup first");
            int arm = (int)a.Long("arm", -1);
            if (arm != 0 && arm != 1) throw new McpException("arm 0 or 1");
            string target = a.Str("target", "stove"), armS = arm.ToString(CultureInfo.InvariantCulture);
            if (target != "stove" && target != "oven") throw new McpException("target stove or oven");
            Sim s = Util.ActiveSim(), sam = null;
            foreach (Sim o in s.Household.Sims) if (o != s) { sam = o; break; }
            if (sam == null) throw new McpException("needs a second Sim in the household");
            if (!MemSend(27, 1u)) throw new McpException("no footprint ghost ops: ini [Discourage] GhostTest=1, [UserDiag] on");
            if (!MemSend(30, (uint)arm)) throw new McpException("no RemovalFirst op: needs a /DTS3N_RFTEST build (v0210d)");
            Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0), from = target == "oven" ? slot + sSwStove.ForwardVector * 1f : sSwSpot;
            SwPlace(s, false);
            s.SetPosition(from);
            Simulator.Sleep(10);
            Route route = s.CreateRoute();
            route.PlanToSlot(sSwStove, Slot.RoutingSlot_0);
            bool beforeFailed = route.PlanResult.mType.ToString().IndexOf("Failed", StringComparison.Ordinal) >= 0;
            s.SetPosition(slot);
            Simulator.Sleep(10);
            ImpassableRegion region = new ImpassableRegion();
            region.AddMember(s);
            region.AddMember(sSwStove);
            region.UpdateFootprint();
            Simulator.Sleep(10);
            GameObject fp = region.DynamicFootprint;
            if (fp == null) throw new McpException("the region made no footprint object");
            s.SetPosition(from);
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
            // from here to route.Plan's START no sleep: the record must be young and the job running
            region.Cleanup();
            MemSend(31, 0);
            uint t0 = (uint)System.Environment.TickCount;
            route.Plan();
            uint ms = (uint)System.Environment.TickCount - t0;
            RoutePlanResult p = route.PlanResult;
            string type = p.mType.ToString();
            bool windowFailed = type.IndexOf("Failed", StringComparison.Ordinal) >= 0;
            string window = type + ", blocker " + p.mBlockerType + ", path " + p.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m, candidates "
                + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + ", plan " + ms + " ms";
            MemSend(22, 0);
            Simulator.Sleep(30);
            bool entry = MemSend(25, 0);  // applies what is queued first
            sam.InteractionQueue.CancelAllInteractions();
            s.SetPosition(from);
            Simulator.Sleep(10);
            string later = SwPlan(s, armS, "window " + target + " later", false, 0, slot);
            bool laterFailed = later.IndexOf("Failed", StringComparison.Ordinal) >= 0;
            if (entry) MemSend(26, 0);
            bool parked = MemSend(32, 0), capped = MemSend(33, 0);
            MemSend(34, 0);
            sFwTally[arm, 0]++;
            if (held) sFwTally[arm, 1]++;
            if (windowFailed) sFwTally[arm, 2]++;
            if (laterFailed) sFwTally[arm, 3]++;
            if (entry) sFwTally[arm, 4]++;
            SwLine("footprint-window: arm " + armS + " " + target + " id 0x" + id.ToString("X16") + " job held " + (held ? 1 : 0) + ", window " + window + ", map entry " + (entry ? 1 : 0)
                + ", later plan " + (laterFailed ? "failed" : "ok") + (beforeFailed ? ", already failed before the region" : ""));
            if (arm == 0 && held && windowFailed && !laterFailed && !entry) SwLine("footprint-window: transient arm 0 " + target);
            if (arm == 1 && parked) SwLine("footprint-window: parked arm 1 " + target);
            // logForbid: each is a broken expectation; a held = 0 rep proves nothing and is not one
            if (beforeFailed || laterFailed || entry || capped || (held && arm == 1 && windowFailed))
                SwLine("footprint-window: FAIL arm " + armS + " " + target + ": before failed " + (beforeFailed ? 1 : 0) + ", window failed " + (windowFailed ? 1 : 0)
                    + ", later failed " + (laterFailed ? 1 : 0) + ", map entry " + (entry ? 1 : 0) + ", capped " + (capped ? 1 : 0));
            FwVerdictLines();  // the last pair is the verdict, as footprint-ghost
            Dictionary<string, object> r = Util.Obj();
            r["held"] = held;
            r["windowFailed"] = windowFailed;
            r["laterFailed"] = laterFailed;
            r["entry"] = entry;
            return r;
        }

        static object FootprintWindowVerdict(Args a)
        {
            FwVerdictLines();
            Dictionary<string, object> r = Util.Obj();
            for (int arm = 0; arm < 2; arm++)
                r["arm" + arm] = sFwTally[arm, 0] + "/" + sFwTally[arm, 1] + "/" + sFwTally[arm, 2] + "/" + sFwTally[arm, 3] + "/" + sFwTally[arm, 4];
            return r;
        }
    }
}
