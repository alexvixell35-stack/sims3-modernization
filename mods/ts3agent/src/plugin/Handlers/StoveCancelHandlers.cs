using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro stove-oven-cancel (2026-11-07): is something left behind at the stove or the fridge when an owner of a region or a claim is cut short, and does it add up?
    // Player reports: household Sims fail the stove and the fridge slot with FailedBecauseNoPathToDestination, candidates 1/1/0, blocker None, "eventually". One pass, one arm:
    //   offsets: from the first tick of BakingDish_CookInOven plans at fixed offsets, to split timing from a switch effect (the Sim's own, and a second Sim 1 m out).
    //   matrix: stage (put-in-oven, in-oven, cook-in-oven, after-bake) x action (cancel, gohere, cook, second, save, need, reset).
    //   snacks: Fridge_GetRandomSnack by the active Sim, the second Sim, an NPC x plain, cancel, reset, while the other two cross in front of the fridge.
    // After each cell, with nobody using anything: census plus plans to both slots from here, 1 m out and past the wall, then again after 5 s if something is left.
    // What is left is logged, added to the arm's growth total and removed, so the next cell starts clean. The census is this repro's growth count: nothing of it ships.
    // Lines "stove-cancel:" and "stove-wall:", all with the savecleanup: prefix.
    //   stove_cancel_run {arm (1 on, 0 off), cycles?: 2}: starts one pass in its own task, numbered per arm. stove_cancel_status {}: {done, cell}. Poll with until.
    public static partial class Handlers
    {
        static readonly string[] kScStages = { "put-in-oven", "in-oven", "cook-in-oven", "after-bake" };
        static readonly string[] kScActions = { "cancel", "gohere", "cook", "second", "save", "need", "reset" };
        static readonly int[] kScOffsets = { 0, 5, 15, 30, 60, 120 };
        const int kScStageMs = 180000, kScSnackMs = 40000;
        static bool sScQuiet, sScDone = true;
        static string sScCell = "";
        static Sim sScSam, sScNpc;
        static Vector3 sScSamPark, sScNpcHome;
        static readonly List<ulong> sScSeen = new List<ulong>();  // region footprints plans named: Queries.GetObjects(point, radius) never returns one, the global list is unproven
        static readonly int[] sScRegions = new int[2], sScClaims = new int[2], sScReps = new int[2];
        static int sScBlind;

        static void RegisterStoveCancel()
        {
            Dispatcher.Register("stove_cancel_run", StoveCancelRun);
            Dispatcher.Register("stove_cancel_status", StoveCancelStatus);
        }

        static object StoveCancelRun(Args a)
        {
            RequireLive();
            int arm = (int)a.Long("arm", 0), cycles = (int)a.Long("cycles", 2), rep = ++sScReps[arm];
            sScDone = false;
            sScCell = "start";
            sScQuiet = true;
            RunAsync("stove cancel", delegate
            {
                try { ScPass(arm, rep, cycles); }
                catch (Exception e) { SwLine("stove-cancel: arm " + arm + " rep " + rep + " pass error in " + sScCell + ": " + DescribeError(e)); }
                finally { sScQuiet = false; sScDone = true; SwWatchStop(); }
            });
            Dictionary<string, object> r = Util.Obj();
            r["started"] = true;
            return r;
        }

        static object StoveCancelStatus(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["done"] = sScDone;
            r["cell"] = sScCell;
            return r;
        }

        static void ScPass(int arm, int rep, int cycles)
        {
            Sim s = Util.ActiveSim();
            if (sScSam == null || sScSam.HasBeenDestroyed)
                foreach (Sim o in s.Household.Sims) if (o != s) { sScSam = o; sScSamPark = o.Position; }
            int cells = 0;
            ScOffsets(s, arm, rep);
            foreach (string stage in kScStages)
                foreach (string action in kScActions) { ScCell(s, arm, rep, stage, action); cells++; }
            for (int i = 0; i < cycles; i++)
                foreach (string who in new string[] { "self", "second", "npc" })
                    foreach (string act in new string[] { "plain", "cancel", "reset" }) { ScSnack(s, arm, rep, i, who, act); cells++; }
            SwLine("stove-cancel: arm " + arm + " rep " + rep + " pass end, cells " + cells + ", arm growth regions " + sScRegions[arm] + " claims " + sScClaims[arm] + ", census blind " + sScBlind);
        }

        static string ScName(Sim s) { return s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "none"; }

        // a fresh bake at dinner: the clock would leave the meal time over a pass, and with it every baked recipe
        static void ScCook(Sim s, int arm)
        {
            if (SimClock.Hours24 < 17 || SimClock.Hours24 > 20)
            {
                Dictionary<string, object> h = Util.Obj();
                h["hour"] = 18L;
                ClockHour(new Args(h));
            }
            Dictionary<string, object> d = Util.Obj();
            d["arm"] = (long)arm;
            d["oven"] = true;
            StoveWallCook(new Args(d));
        }

        static bool ScReached(Sim s, string stage)
        {
            int t0 = System.Environment.TickCount;
            while (System.Environment.TickCount - t0 < kScStageMs)
            {
                string n = ScName(s);
                if (sSwOvenFailed != null) return false;
                if (stage == "put-in-oven" && sSwAtOven) return true;
                if (stage == "in-oven" && sSwInOven) return true;
                if (stage == "cook-in-oven" && n == "BakingDish_CookInOven" && sSwStove.IsActorUsingMe(s)) return true;  // SimStartedCooking, right after the region
                if (stage == "cooking" && n == "BakingDish_CookInOven") return true;
                if (stage == "after-bake" && sSwOvenSeen && n != "BakingDish_CookInOven") return true;
                if (System.Environment.TickCount - t0 > 20000 && s.CurrentInteraction == null && s.InteractionQueue.Count == 0) return false;
                Simulator.Sleep(0);
            }
            return false;
        }

        // the first tick of CookInOven on: its route runs first, the region comes after it (BakingDish_CookInOven.Run). The second Sim stands 1 m out
        static void ScOffsets(Sim s, int arm, int rep)
        {
            sScCell = "offsets";
            string a = arm.ToString(), l = "r" + rep + " offset ";
            ScCook(s, arm);
            bool ok = ScReached(s, "cooking");
            SwLine("stove-cancel: arm " + arm + " rep " + rep + " offsets " + (ok ? "CookInOven running" : "never ran") + ", doing " + ScName(s));
            if (ok)
            {
                Vector3 slot = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
                if (sScSam != null) sScSam.SetPosition(slot + sSwStove.ForwardVector * 1f);
                int at = 0;
                sSwProbing = true;
                foreach (int o in kScOffsets)
                {
                    if (o > at) Ticks((uint)(o - at));
                    at = o;
                    SwPlan(s, a, l + o + " own to-slot", false, 0, slot);
                    SwPlan(s, a, l + o + " own own-spot", false, 1, s.Position);
                    if (sScSam != null)
                    {
                        SwPlan(sScSam, a, l + o + " 1m to-slot", false, 0, slot);
                        SwPlan(sScSam, a, l + o + " 1m to-slot-point", false, 1, slot);
                    }
                }
                sSwProbing = false;
            }
            ScEnd(s, arm, rep, "offsets");
        }

        static void ScCell(Sim s, int arm, int rep, string stage, string action)
        {
            string label = stage + " " + action;
            sScCell = "r" + rep + " " + label;
            ScCook(s, arm);
            bool ok = ScReached(s, stage);
            SwLine("stove-cancel: arm " + arm + " rep " + rep + " " + label + " stage " + (ok ? "reached" : "NOT reached") + ", doing " + ScName(s));
            if (ok)
            {
                ScOwnerPlan(s, arm, rep, label);  // names the live slot for the census, the region stays what the game made
                try { ScAct(s, action); }
                catch (Exception e) { SwLine("stove-cancel: arm " + arm + " rep " + rep + " " + label + " action failed: " + e.GetType().Name + " " + e.Message); }
                Ticks(90);
                int regions, claims;
                SwLine(ScCensus(arm, rep, label + " settled, doing " + ScName(s), out regions, out claims));
            }
            ScEnd(s, arm, rep, label);
        }

        static void ScOwnerPlan(Sim s, int arm, int rep, string label)
        {
            sSwProbing = true;
            SwPlan(s, arm.ToString(), "r" + rep + " " + label + " live own-spot", false, 1, s.Position);
            sSwProbing = false;
        }

        static void ScAct(Sim s, string action)
        {
            InteractionInstance cur = s.CurrentInteraction;
            switch (action)
            {
                case "cancel":
                    if (cur != null) s.InteractionQueue.CancelInteraction(cur.Id);
                    break;
                case "gohere":
                    ScGoHere(s, sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0) + sSwStove.ForwardVector * 2f);
                    break;
                case "cook":
                    ScOrder(s, sSwStove, "Stove_Have");
                    break;
                case "second":
                    if (sScSam != null)
                    {
                        sScSam.SetPosition(sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0) + sSwStove.ForwardVector * 1.5f);
                        ScOrder(sScSam, sSwStove, "Stove_Have");
                    }
                    break;
                case "save":
                    Dictionary<string, object> d = Util.Obj();
                    d["name"] = "Stove-Cancel-Scratch";  // never the loaded Baseline save
                    SaveGame(new Args(d));
                    Simulator.Sleep(30);
                    for (int i = 0; i < 1200 && Sims3.Gameplay.UI.Responder.Instance.OptionsModel.SaveGameInProgress; i++) Ticks(3);
                    break;
                case "need":
                    s.AddExitReason(ExitReason.MoodFailure);  // what a failing motive adds; the need itself is not lowered, autonomy is off
                    break;
                case "reset":
                    s.SetObjectToReset();
                    break;
            }
        }

        static void ScGoHere(Sim s, Vector3 pos)
        {
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, pos).CreateInstance(Terrain.Singleton, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            SetClickHit(ii, pos);
            s.InteractionQueue.Add(ii);
        }

        // the first enabled pie menu entry of that interaction class, as a player's click
        static void ScOrder(Sim s, GameObject target, string type)
        {
            GameObjectHit hit = HitFor(target, new Args(Util.Obj()));
            foreach (MenuEntry e in BuildMenu(s, target, hit))
            {
                Type def = e.Iop.InteractionDefinition.GetType();
                if (!e.Enabled || def.DeclaringType == null || def.DeclaringType.Name != type) continue;
                InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
                InteractionInstance ii = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                if (ii != null) s.InteractionQueue.Add(ii);
                return;
            }
            SwLine("stove-cancel: no enabled " + type + " for " + s.FullName);
        }

        static void ScStopAll(Sim s)
        {
            SwStop(s);
            if (sScSam != null && !sScSam.HasBeenDestroyed) SwStop(sScSam);
            if (sScNpc != null && !sScNpc.HasBeenDestroyed) SwStop(sScNpc);
        }

        // nobody uses anything now: what is still there has no owner
        static void ScEnd(Sim s, int arm, int rep, string label)
        {
            SwWatchStop();
            ScStopAll(s);
            Ticks(30);
            int fails = ScPlans(s, arm, rep, label + " stopped");
            int regions, claims;
            SwLine(ScCensus(arm, rep, label + " stopped, doing " + ScName(s), out regions, out claims));
            if (regions > 0 || claims > 0 || fails > 0)
            {
                Ticks(150);
                fails = ScPlans(s, arm, rep, label + " later");
                SwLine(ScCensus(arm, rep, label + " later", out regions, out claims));
            }
            sScRegions[arm] += regions;
            sScClaims[arm] += claims;
            SwLine("stove-cancel: arm " + arm + " rep " + rep + " " + label + " verdict " + (regions > 0 || claims > 0 ? "LEAK" : fails > 0 ? "PLANS FAIL" : "clear") + ", regions " + regions + ", claims " + claims + ", failed plans " + fails
                + ", growth arm total regions " + sScRegions[arm] + " claims " + sScClaims[arm]);
            ScClean(arm);
            Dictionary<string, object> d = Util.Obj();
            d["arm"] = (long)arm;
            StoveOvenClear(new Args(d));
            if (sScSam != null && !sScSam.HasBeenDestroyed) sScSam.SetPosition(sScSamPark);
        }

        // both slots, slot goal and point goal: the player's 1/1/0 is a goal that passes and a search that fails
        static int ScPlans(Sim s, int arm, int rep, string label)
        {
            Vector3 stove = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0), fridge = sSwFridge.GetPositionOfSlot(Slot.RoutingSlot_0), was = s.Position;
            string a = arm.ToString(), l = "r" + rep + " " + label + " ";
            int fails = 0;
            sSwProbing = true;
            foreach (string at in new string[] { "here", "1m", "past" })
            {
                if (at == "1m") s.SetPosition(stove + sSwStove.ForwardVector * 1f);
                if (at == "past") s.SetPosition(sSwSpot);
                if (at != "here") Simulator.Sleep(10);
                string[] lines = {
                    SwPlan(s, a, l + at + " to-slot", false, 0, stove),
                    SwPlan(s, a, l + at + " to-slot-point", false, 1, stove),
                    SwPlan(s, a, l + at + " fridge-slot", false, 2, fridge),
                    SwPlan(s, a, l + at + " fridge-point", false, 1, fridge) };
                foreach (string line in lines) if (line.IndexOf("Failed", StringComparison.Ordinal) >= 0) fails++;
            }
            sSwProbing = false;
            s.SetPosition(was);
            return fails;
        }

        // a region's footprint within 8 m of the stove (the world count for scale), the claims at stove and fridge
        static List<GameObject> ScLive(out int world)
        {
            List<GameObject> near = new List<GameObject>();
            world = 0;
            foreach (Null n in Sims3.Gameplay.Queries.GetObjects<Null>())
            {
                if ((n.GetHiddenFlags() & HiddenFlags.Model) == 0) continue;
                world++;
                if ((n.Position - sSwStove.Position).Length() <= 8f) near.Add(n);
            }
            foreach (ulong id in sScSeen)
            {
                GameObject o = GameObject.GetObject(new ObjectGuid(id));
                if (o == null || near.Contains(o)) continue;
                near.Add(o);
                sScBlind++;
            }
            return near;
        }

        static string ScCensus(int arm, int rep, string label, out int regions, out int claims)
        {
            int world;
            List<GameObject> live = ScLive(out world);
            string ids = "";
            foreach (GameObject o in live) ids += " 0x" + o.ObjectId.Value.ToString("X") + " at " + V3(o.Position);
            int stove = sSwStove.ActorsUsingMe.Count, fridge = sSwFridge.ActorsUsingMe.Count, line = sSwFridge.Line.MemberCount();
            regions = live.Count;
            claims = stove + fridge + line;
            return "stove-cancel: arm " + arm + " rep " + rep + " census " + label + ": regions " + regions + ids + ", world " + world + ", users stove " + stove + " fridge " + fridge + ", fridge line " + line + ", blind " + sScBlind;
        }

        static void ScClean(int arm)
        {
            int world;
            foreach (GameObject o in ScLive(out world)) o.Destroy();
            sScSeen.Clear();
            foreach (Sim u in new List<Sim>(sSwStove.ActorsUsingMe)) sSwStove.RemoveFromUseList(u);
            foreach (Sim u in new List<Sim>(sSwFridge.ActorsUsingMe)) sSwFridge.RemoveFromUseList(u);
            foreach (Sim u in new List<Sim>(sSwFridge.Line.SimsInQueue)) sSwFridge.Line.RemoveFromQueue(u);
        }

        static Sim ScFindNpc(Sim s)
        {
            if (sScNpc != null && !sScNpc.HasBeenDestroyed) return sScNpc;
            foreach (Sim o in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (o.Household == s.Household || o.IsSelectable || !o.IsHuman || !o.SimDescription.TeenOrAbove || o.InteractionQueue == null) continue;
                sScNpc = o;
                sScNpcHome = o.Position;
                return o;
            }
            return null;
        }

        static void ScSnack(Sim s, int arm, int rep, int cycle, string who, string act)
        {
            string label = "snack " + who + " " + act + " " + cycle;
            sScCell = "r" + rep + " " + label;
            Sim npc = ScFindNpc(s);
            Sim a = who == "self" ? s : who == "second" ? sScSam : npc;
            if (a == null) { SwLine("stove-cancel: arm " + arm + " rep " + rep + " " + label + " no such Sim"); return; }
            Vector3 slot = sSwFridge.GetPositionOfSlot(Slot.RoutingSlot_0), fwd = sSwFridge.ForwardVector, side = new Vector3(fwd.z, 0f, -fwd.x);
            SwWatch(s, arm.ToString(), label);
            foreach (Sim o in new Sim[] { s, sScSam, npc })
            {
                if (o == null) continue;
                o.InteractionQueue.CancelAllInteractions();
                o.SetPosition(slot + fwd * 1.5f + side * (o == a ? 0f : o == s ? 1.5f : -1.5f));
            }
            if (npc != null) npc.GreetSimOnLot(sSwStove.LotCurrent);
            Simulator.Sleep(5);
            // the other two cross in front of the single approach, each to the far side
            foreach (Sim o in new Sim[] { s, sScSam, npc })
                if (o != null && o != a) ScGoHere(o, slot + side * (o == s ? -1.5f : 1.5f));
            InteractionInstance ii = Fridge_GetRandomSnack.GetRandomSnackSingleton.CreateInstance(sSwFridge, a, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            bool q = ii != null && a.InteractionQueue.Add(ii);
            int t0 = System.Environment.TickCount;
            bool at = false;
            while (q && !at && System.Environment.TickCount - t0 < kScSnackMs)
            {
                at = ScName(a).StartsWith("Fridge_", StringComparison.Ordinal) && (a.Position - slot).Length() < 0.5f;
                Simulator.Sleep(0);
            }
            SwLine("stove-cancel: arm " + arm + " rep " + rep + " " + label + " by " + a.FullName + ", queued " + q + ", at the fridge " + at + " after " + (System.Environment.TickCount - t0) + " ms, doing " + ScName(a));
            if (at)
            {
                Ticks(10);  // into the region: Fridge_Have.Run raises it right after the route
                ScOwnerPlan(a, arm, rep, label);
                if (act == "cancel" && a.CurrentInteraction != null) a.InteractionQueue.CancelInteraction(a.CurrentInteraction.Id);
                if (act == "reset") a.SetObjectToReset();
                t0 = System.Environment.TickCount;
                while (act == "plain" && ScName(a).StartsWith("Fridge_", StringComparison.Ordinal) && System.Environment.TickCount - t0 < kScSnackMs) Ticks(3);
                Ticks(60);
            }
            ScEnd(s, arm, rep, label);
            if (npc != null && !npc.HasBeenDestroyed) npc.SetPosition(sScNpcHome);
        }
    }
}
