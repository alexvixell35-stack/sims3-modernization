using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using GameCounter = Sims3.Gameplay.Objects.Counters.Counter;  // plain Counter is Handlers.Counter, see StoveWallHandlers.cs

namespace Ts3Agent
{
    // Repro stove-wall-repeat (2026-11-07): player reports say the stove fails "eventually"; the b02e08a4 log fails on the third bake of a session, Sim already on the slot, result 16, 1/1/0.
    // A kitchen as the player's: one long run along a straight wall (fridge, counters both sides of the stove, dishwasher), sink, food processor; the selected household Sim, real orders only.
    // Main loop: full bakes through the stove's menu, as one player click: Fridge_Have, FoodTray_Prepare, CuttingBoard_Prepare, BakingDish_Prepare, BakingDish_CookInOven, then eat,
    // ServingContainer_CleanUp, UseDishwasher. The chain does what it does; a step it skips gets the player's click on the plate. Every 4th bake starts with a Go Here to X, past the wall.
    // If no bake fails, a mix follows: stove and fridge orders, a cancel soon after the start or at the use, snacks. A stove left busy by a cancel gets its next order from the dish on it: kind "finish".
    // Nothing is cleaned by the agent, nothing is placed after setup.
    // Failure = a plan from where the Sim stands fails, a real plan to the target or to X fails with no path and no Sim as blocker, the order never uses the target, or 2 of the 30 s progress checks miss.
    // A no-path with a Sim as blocker counts in the state line and goes on: EA replans those.
    // On the first failure: no more orders, the world stays, plans and a census at +5, +30, +120 s. No failure proves nothing: the end line says so.
    // Needs are set to full, hunger to the middle so the Sim eats, before each order, and the clock back to dinner when it leaves it (cheats, the only ones after setup).
    // Lines "stove-repeat:" and "stove-wall:", all with the savecleanup: prefix.
    // Two NPCs walk in the west room for the whole run, a new Go Here to a far lot and back each second: the RE verdict (DeferFootprint2 + DestroyParkDx) needs a route job running when a region goes.
    // The state line's "dead" counts regions while nobody uses stove or fridge.
    //   stove_repeat_setup {}: the Sim on X. The run from the stove's own position and facing, 3 tiles each way along the wall: counters, the fridge, the stove, a dishwasher. The fridge takes the first tile where its slot is planned from X
    //     (a run 2026-11-07 by tile numbers put a counter between stove and slot); a sink across a lane 2 tiles wide, a food processor on the last counter.
    //     Then every slot is planned from X: a failed plan is an error, "layout blocks the slot", and stops the job before the first order.
    //   stove_repeat_run {cycles?: 50, mix?: 50}: starts the run in its own task, full bakes, then mix orders. stove_repeat_status {}: {done, cycle, failed}. Poll with until.
    public static partial class Handlers
    {
        static readonly string[] kSrMix = { "stove cancel-start", "fridge snack", "stove cancel-use", "fridge meal", "stove stove", "fridge cancel-start", "fridge cancel-use" };
        const int kSrWallEvery = 4, kSrUseMs = 300000, kSrBackMs = 120000, kSrCheckMs = 30000, kSrCancelMs = 1500, kSrIdleMs = 3000;
        static bool sSrDone = true;
        static int sSrCycle, sSrPlans, sSrSimBlocked, sSrFull, sSrWalkOrders;
        static bool sSrWalk;
        static double sSrReach, sSrRan;
        static readonly List<string> sSrSteps = new List<string>();
        static string sSrFailed = "", sSrWhy = "", sSrFirstPlan, sSrNoPath, sSrPlanLine;
        static int sSrAte, sSrTrans00, sSrTrans11, sSrTransOther, sSrWalk60, sSrPerm, sSrNoUse, sSrNoUseRow, sSrNoUsePlanFail;
        static Sim sSrSim;
        static GameObject sSrTarget;
        static Vector3 sSrGoal;

        static void RegisterStoveRepeat()
        {
            Dispatcher.Register("stove_repeat_setup", StoveRepeatSetup);
            Dispatcher.Register("stove_repeat_run", StoveRepeatRun);
            Dispatcher.Register("stove_repeat_status", StoveRepeatStatus);
        }

        static object StoveRepeatSetup(Args a)
        {
            RequireLive();
            if (sSwStove == null) throw new McpException("stove_wall_setup first");
            Sim s = Util.ActiveSim();
            Dictionary<string, object> r = Util.Obj();
            Vector3 at = sSwStove.Position, fwd = sSwStove.ForwardVector, side = new Vector3(-fwd.z, 0f, fwd.x);
            sSwFridge = null;
            int spot = 0;
            string tried = "";
            foreach (int k in new int[] { -2, -1, 1, 3 })
            {
                sSwFridge = SrFridge(s, at + side * k, fwd, r, ref tried);
                if (sSwFridge != null) { spot = k; break; }
            }
            if (sSwFridge == null) throw new McpException("layout blocks the slot: the fridge fits no tile of the run: " + tried);
            List<GameObject> run = new List<GameObject>();
            run.Add(sSwStove);
            run.Add(sSwFridge);
            foreach (int k in new int[] { -3, -2, -1, 1, 3 })
            {
                if (k == spot) continue;
                run.Add(SwCreate("counter", typeof(GameCounter), at + side * k, fwd, r, "counter", kSwCounterObjd));
            }
            GameObject last = run[run.Count - 1];
            run.Add(SwCreate("dishwasher", typeof(Dishwasher), at + side * 2f, fwd, r, "dishwasher"));
            run.Add(SwCreate("sink", typeof(Sink), at + fwd * 3f + side * 3f, fwd * -1f, r, "sink"));
            GameObject proc = SwCreate("food processor", typeof(FoodProcessor), last.Position, fwd, r, "processor");
            proc.ParentToSlot(last, Slot.ContainmentSlot_0);
            SwLine("stove-repeat: setup counter " + r["counter"] + ", dishwasher " + r["dishwasher"] + ", sink " + r["sink"] + ", processor " + r["processor"]);
            string blocked = "", fronts = "";
            int counters = 0, frontsOk = 0;
            foreach (GameObject o in run)
            {
                Route route = s.CreateRoute();
                bool counter = o is GameCounter;
                if (counter) route.PlanToPoint(o.Position + fwd * 1f); else route.PlanToSlot(o, Slot.RoutingSlot_0);
                RoutePlanResult p = route.PlanResult;
                string line = o.GetType().Name + (counter ? " front " : " slot ") + p.mType + ", path " + p.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m, candidates " + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + "/" + p.mNumRejectedPoints + ", blocker " + p.mBlockerType + SwWho(p.mBlockerObjectId);
                SwLine("stove-repeat: from X to " + line);
                bool failed = p.mType.ToString().IndexOf("Failed", StringComparison.Ordinal) >= 0;
                if (counter) { counters++; if (failed) fronts += line + "; "; else frontsOk++; }  // a point in front of a counter is no slot the game routes to: only a warning
                else if (failed) blocked += line + "; ";
            }
            if (blocked.Length == 0 && counters > 0 && frontsOk == 0) blocked = "no counter front: " + fronts;
            if (blocked.Length > 0) throw new McpException("layout blocks the slot: " + blocked);
            if (fronts.Length > 0) SwLine("stove-repeat: warning, " + frontsOk + " of " + counters + " counter fronts ok, failed " + fronts);
            SwLine("stove-repeat: layout ok");
            return r;
        }

        // one tile of the run: turned round if its slot lies behind it, kept only if the slot is planned from X and lies in the kitchen's room. Else a line with what was there, and it goes
        static Fridge SrFridge(Sim s, Vector3 pos, Vector3 fwd, Dictionary<string, object> r, ref string tried)
        {
            Fridge f = SwCreate("fridge", typeof(Fridge), pos, fwd, r, "fridge") as Fridge;
            Vector3 toSlot = f.GetPositionOfSlot(Slot.RoutingSlot_0) - pos;
            bool turned = toSlot.x * fwd.x + toSlot.z * fwd.z < 0f;
            if (turned) f.SetForward(fwd * -1f);
            Vector3 slot = f.GetPositionOfSlot(Slot.RoutingSlot_0), kitchen = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0);
            Route route = s.CreateRoute();
            route.PlanToSlot(f, Slot.RoutingSlot_0);
            RoutePlanResult p = route.PlanResult;
            bool room = World.GetRoomId(slot) == World.GetRoomId(kitchen), wall = World.WallExistsBetweenPoints(f.LotCurrent.LotId, slot, kitchen, eRoomDefinition.PlacementBlocking);
            string line = "fridge at " + V3(f.Position) + ", forward " + V3(f.ForwardVector) + (turned ? " turned" : "") + ", slot " + V3(slot) + " room " + World.GetRoomId(slot) + ", wall to the stove slot " + wall + ", " + p.mType + ", candidates " + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + "/" + p.mNumRejectedPoints + ", blocker " + p.mBlockerType + SwWho(p.mBlockerObjectId);
            bool ok = p.mType.ToString().IndexOf("Failed", StringComparison.Ordinal) < 0 && room && !wall;
            if (!ok)
                foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(slot, 2f))
                    if (o != f) line += "; " + o.GetType().Name + " at " + V3(o.Position);
            SwLine("stove-repeat: " + line.Substring(0, Math.Min(line.Length, 440)));
            if (ok) return f;
            tried += line.Substring(0, Math.Min(line.Length, 200)) + " | ";
            f.Destroy();
            return null;
        }

        static object StoveRepeatRun(Args a)
        {
            RequireLive();
            int cycles = (int)a.Long("cycles", 50), mix = (int)a.Long("mix", 50);
            sSrDone = false;
            sSrCycle = 0;
            sSrFailed = "";
            sScQuiet = true;
            RunAsync("stove repeat", delegate
            {
                try { SrPass(cycles, mix); }
                catch (Exception e) { sSrFailed = "error"; SwLine("stove-repeat: end, error at cycle " + sSrCycle + ": " + DescribeError(e)); }
                finally { sScQuiet = false; sSrDone = true; sSrWalk = false; RoutingComponent.sPostPlanProfileCallback = null; }
            });
            Dictionary<string, object> r = Util.Obj();
            r["started"] = true;
            return r;
        }

        static object StoveRepeatStatus(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["done"] = sSrDone;
            r["cycle"] = sSrCycle;
            r["failed"] = sSrFailed;
            return r;
        }

        static void SrPass(int cycles, int mix)
        {
            Sim s = Util.ActiveSim();
            sSrSim = s;
            sSrFull = sSrAte = sSrTrans00 = sSrTrans11 = sSrTransOther = sSrWalk60 = sSrPerm = sSrNoUse = sSrNoUseRow = sSrNoUsePlanFail = 0;
            RoutingComponent.sPostPlanProfileCallback = SrPlanned;
            SrWalkers(s);
            SwLine("stove-repeat: start, " + cycles + " bakes then " + mix + " mix orders, " + s.FullName + " at " + V3(s.Position));
            for (int i = 0; i < cycles + mix; i++)
            {
                sSrCycle = i + 1;
                bool bake = i < cycles;
                string[] mixed = kSrMix[(i - cycles + kSrMix.Length * cycles) % kSrMix.Length].Split(' ');
                bool stove = bake || mixed[0] == "stove";
                string kind = bake ? (i % kSrWallEvery == kSrWallEvery - 1 ? "bake-wall" : "bake") : mixed[1];
                if (stove && sSwStove.IsCookingSomething()) kind = "finish";
                string label = "cycle " + (i + 1) + " " + (stove ? "stove" : "fridge") + " " + kind;
                string why = SrCycle(s, label, stove, kind);
                if (why == null) { sSrNoUseRow = sSrNoUsePlanFail = 0; continue; }
                if (SrTransient(s, label, stove, why)) continue;  // a live region that clears: counted, not a stop
                if (why.StartsWith("no use", StringComparison.Ordinal) && SrNoUse(s, label, stove, why)) continue;  // the Sim chose something else (free will is on): counted, not a stop
                sSrPerm++;
                sSrFailed = label + ": " + why;
                sSrWalk = false;  // no more orders, the NPCs' too
                SwLine("stove-repeat: FIRST FAILURE " + sSrFailed + ", at " + V3(s.Position) + ", doing " + ScName(s) + ", full bakes before it " + sSrFull);
                int t0 = System.Environment.TickCount;
                foreach (int at in new int[] { 5, 30, 120 })
                {
                    while (System.Environment.TickCount - t0 < at * 1000) Ticks(3);
                    SrDump(s, label + " +" + at + " s");
                }
                SwLine("stove-repeat: end, failed at " + sSrFailed + "; " + SrTotals());
                return;
            }
            SwLine("stove-repeat: end, no permanent failure in " + (cycles + mix) + " orders (not proof), " + SrTotals() + ", " + sSrSimBlocked + " no-path plans with a Sim as blocker");
        }

        // the end line's counts: bakes (the chain reached the oven), ate (and cleaned up too), the transients split by the plan's candidates, the wide-search walk-aways, the stops
        static string SrTotals()
        {
            return "full bakes " + sSrFull + " (ate and cleaned up " + sSrAte + "), transients " + (sSrTrans00 + sSrTrans11 + sSrTransOther) + " (candidates 0/0/0 " + sSrTrans00 + ", 1/1/0 " + sSrTrans11 + ", other " + sSrTransOther
                + "), 60-candidate walk-aways " + sSrWalk60 + ", no use cycles " + sSrNoUse + ", permanent fails " + sSrPerm;
        }

        // an order the Sim never used the target for (free will picked another interaction, 11-07 cycle 9: AutonomousSplashInPuddle): counted, what is left is cancelled, the Sim is
        // waited for, a plan from where it stands is checked, and the run goes on. A stop only when it repeats StoveRepeatCore.NoUseStop cycles in a row with a plan failure among them
        static bool SrNoUse(Sim s, string label, bool stove, string why)
        {
            sSrNoUse++;
            sSrNoUseRow++;
            SwLine("stove-repeat: no use " + label + ", " + why);
            SrFree(s, 120000);
            GameObject target = stove ? (GameObject)sSwStove : sSwFridge;
            Vector3 slot = target.GetPositionOfSlot(Slot.RoutingSlot_0);
            sSwProbing = true;
            string plan = SwPlan(s, "r", label + " after no use", false, stove ? 0 : 2, slot);
            sSwProbing = false;
            if (StoveRepeatCore.Failed(plan)) sSrNoUsePlanFail++;
            return !StoveRepeatCore.NoUseStop(sSrNoUseRow, sSrNoUsePlanFail);
        }

        // the Sim idle for kSrIdleMs, as a player waits before the next click; after maxMs what is left is cancelled
        static void SrFree(Sim s, int maxMs)
        {
            int t0 = System.Environment.TickCount, idle = 0;
            while (true)
            {
                Ticks(3);
                int now = System.Environment.TickCount;
                idle = SrIdle(s, ScName(s)) ? (idle == 0 ? now : idle) : 0;
                if (idle != 0 && now - idle >= kSrIdleMs) return;
                if (now - t0 >= maxMs) { s.InteractionQueue.CancelAllInteractions(); return; }
            }
        }

        // a failed plan is a stop only when it stays with the Sim free: still failing on two rechecks 30 s apart while no interaction runs, or the same blocker id staying "no object". A fail
        // while the Sim is in a social or any running interaction is that interaction's live region and jig (11-07 cycle 26: SocialInteractionB, all probes fine 30 s after the stop): the
        // interaction is cancelled as a player would, the Sim is waited for, and the run goes on. True = a transient, go on
        static bool SrTransient(Sim s, string label, bool stove, string why)
        {
            bool planFail = why == "plan from here failed", noPath = why.StartsWith("no path to the target", StringComparison.Ordinal);
            if (!planFail && !noPath) return false;
            string line = planFail ? sSrPlanLine : sSrNoPath;
            if (line == null) return false;
            string doing = ScName(s);
            bool busy = !SrIdle(s, doing);
            GameObject target = stove ? (GameObject)sSwStove : sSwFridge;
            Vector3 slot = target.GetPositionOfSlot(Slot.RoutingSlot_0);
            List<string> lines = new List<string>();
            List<bool> free = new List<bool>();
            string at = null, recheck = null;
            int t0 = System.Environment.TickCount, last = 0;
            for (int i = 0; i < 2; i++)
            {
                if (i == 0 && !busy) while (System.Environment.TickCount - t0 < 5000) Ticks(3);
                if (i == 1) while (System.Environment.TickCount - last < 30000) Ticks(3);
                if (!SrIdle(s, ScName(s))) { s.InteractionQueue.CancelAllInteractions(); SrFree(s, 120000); }
                sSwProbing = true;
                string plan = SwPlan(s, "r", label + " recheck " + (i + 1), false, stove ? 0 : 2, slot);
                sSwProbing = false;
                last = System.Environment.TickCount;
                lines.Add(plan);
                free.Add(SrIdle(s, ScName(s)));
                if (StoveRepeatCore.Failed(plan)) continue;
                at = "recheck " + (i + 1);
                recheck = plan;
                break;
            }
            if (at == null && StoveRepeatCore.PermanentFail(lines.ToArray(), free.ToArray())) return false;
            if (at == null) at = "no recheck cleared, not both with the Sim free";
            string split = StoveRepeatCore.Split(line);
            if (split == "0/0/0") sSrTrans00++;
            else if (split == "1/1/0") sSrTrans11++;
            else sSrTransOther++;
            if (StoveRepeatCore.WalkAway(recheck)) sSrWalk60++;
            SwLine("stove-repeat: transient " + label + ", plan " + line + ", doing " + doing + (busy ? " (busy)" : " (free)") + ", cleared at " + at + ", then " + recheck);
            SrFree(s, 120000);
            return true;
        }

        // NPCs only, placed by cheat: a long plan each second keeps a route job in flight when a bake's region goes. Nobody of the household walks here
        static void SrWalkers(Sim s)
        {
            Lot home = sSwStove.LotCurrent, far = null;
            float best = 0f;
            foreach (Lot o in LotManager.AllLots)
            {
                if (o == home || o.IsWorldLot) continue;
                float d = (o.Position - home.Position).Length();
                if (d > best) { best = d; far = o; }
            }
            Vector3 park = sSwSpot - sSwStove.ForwardVector * 1f, goal = far != null ? far.EntryPoint() : home.EntryPoint();
            List<Sim> walkers = new List<Sim>();
            foreach (Sim o in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (walkers.Count == 2) break;
                if (o.Household == s.Household || o.IsSelectable || !o.IsHuman || !o.SimDescription.TeenOrAbove || o.InteractionQueue == null) continue;
                walkers.Add(o);
                o.InteractionQueue.CancelAllInteractions();
                o.SetPosition(park);
                o.GreetSimOnLot(home);
            }
            SwLine("stove-repeat: walkers " + walkers.Count + " NPCs, far goal " + V3(goal) + ", " + best.ToString("0", CultureInfo.InvariantCulture) + " m");
            sSrWalk = walkers.Count > 0;
            sSrWalkOrders = 0;
            RunAsync("stove repeat walkers", delegate
            {
                for (int i = 0; sSrWalk; i++)
                {
                    foreach (Sim o in walkers)
                    {
                        if (o.HasBeenDestroyed || o.InteractionQueue == null) continue;
                        o.InteractionQueue.CancelAllInteractions();
                        ScGoHere(o, i % 2 == 0 ? goal : park);
                        sSrWalkOrders++;
                    }
                    Ticks(30);
                }
            });
        }

        // EA's post-plan hook, on the sim thread inside the plan: nothing may throw out of it
        static void SrPlanned(Route r, string routeType, string result)
        {
            try
            {
                if (sSwProbing || r.Follower == null || r.Follower.Target != sSrSim) return;
                sSrPlans++;
                GameObject to = r.DestObj as GameObject;
                if ((to == null || to != sSrTarget) && (r.GetDestPoint() - sSrGoal).Length() > 1f) return;
                RoutePlanResult p = r.PlanResult;
                string type = p.mType.ToString();
                string line = type + ", path " + p.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture) + " m, candidates " + p.mNumCandidatePointsGenerated + "/" + p.mNumAcceptedPoints + "/" + p.mNumRejectedPoints
                    + ", blocker " + p.mBlockerType + " 0x" + p.mBlockerObjectId.ToString("X") + SwWho(p.mBlockerObjectId);
                if (sSrFirstPlan == null) sSrFirstPlan = line;
                if (type.IndexOf("NoPath", StringComparison.Ordinal) < 0) return;
                if (p.mBlockerType.ToString().IndexOf("Sim", StringComparison.Ordinal) >= 0) sSrSimBlocked++;
                else if (sSrNoPath == null) sSrNoPath = routeType + " " + line;
            }
            catch (Exception) { }
        }

        static void SrAim(GameObject target, Vector3 goal)
        {
            sSrTarget = target;
            sSrGoal = goal;
            sSrFirstPlan = sSrNoPath = null;
        }

        // one order, the meal after a bake, the way back. Null = fine, else why it failed
        static string SrCycle(Sim s, string label, bool stove, string kind)
        {
            GameObject target = stove ? (GameObject)sSwStove : sSwFridge;
            Vector3 slot = target.GetPositionOfSlot(Slot.RoutingSlot_0);
            bool bake = kind.StartsWith("bake", StringComparison.Ordinal), finish = kind == "finish";
            foreach (Sim o in s.Household.Sims) o.Motives.MaxEverything();
            s.Motives.SetValue(CommodityKind.Hunger, 0f);
            if (SimClock.Hours24 < 17 || SimClock.Hours24 > 20)
            {
                Dictionary<string, object> h = Util.Obj();
                h["hour"] = 18L;
                ClockHour(new Args(h));
            }
            string why = kind == "bake-wall" ? SrBack(s, label) : null;
            if (why != null) { SwLine(SrState(label)); return why; }
            SrFree(s, 120000);  // plans only when the Sim is free: a plan during a social interaction meets that interaction's live region (the 11-07 stop at cycle 3)
            sSrPlans = 0;
            sSrSteps.Clear();
            SrAim(target, slot);
            sSwProbing = true;
            string plan = SwPlan(s, "r", label + " plan", false, stove ? 0 : 2, slot);
            sSwProbing = false;
            sSrPlanLine = plan;
            if (StoveRepeatCore.Failed(plan)) return "plan from here failed";
            string recipe = finish ? SrFinish(s) : SrOrder(s, target, stove, kind);
            if (recipe == null) return "no use: " + sSrWhy;
            why = SrWait(s, slot, stove, kind);
            string first = sSrFirstPlan ?? "none";
            double reach = sSrReach, ran = sSrRan;
            if (why == null && ran < 0 && kind != "cancel-start") why = "no use, doing " + ScName(s);
            if (why == null && (bake || finish)) why = SrMeal(s, slot);
            SwLine("stove-repeat: " + label + " recipe " + recipe + ", first real plan " + first + ", plans " + sSrPlans + ", reach " + reach.ToString("0.0", CultureInfo.InvariantCulture) + " s, ran " + (ran >= 0 ? "y " + ran.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "n"));
            string steps = string.Join(">", sSrSteps.ToArray());
            SwLine("stove-repeat: " + label + " steps " + steps.Substring(0, Math.Min(steps.Length, 300)));
            // a full bake = the chain reached the oven and the order ended without a failure. The old test also wanted Eat and CleanUp in the steps, and the chain often skips Eat
            // ("no entry Eat": the cycles 1 and 2 of the 11-07 run reached BakingDish_CookInOven and counted 0); those are counted apart as "ate and cleaned up"
            if (bake && why == null && StoveRepeatCore.Baked(sSrSteps)) { sSrFull++; if (StoveRepeatCore.Ate(sSrSteps)) sSrAte++; }
            if (why == null && !bake && !finish) why = SrBack(s, label);
            SwLine(SrState(label));
            return why;
        }

        // the order until the Sim is idle or the cancel is through, with the fail-fast checks. Sets sSrReach, sSrRan, sSrSteps
        static string SrWait(Sim s, Vector3 slot, bool stove, string kind)
        {
            int t0 = System.Environment.TickCount, check = t0, idle = 0, misses = 0, cancelled = 0;
            sSrReach = sSrRan = -1;
            Vector3 last = s.Position;
            string lastName = ScName(s);
            while (true)
            {
                Ticks(3);
                int now = System.Environment.TickCount;
                string n = ScName(s);
                if (n != "none" && (sSrSteps.Count == 0 || sSrSteps[sSrSteps.Count - 1] != n)) sSrSteps.Add(n);
                if (sSrReach < 0 && SrAt(s, slot)) sSrReach = (now - t0) / 1000.0;
                if (sSrRan < 0 && SrUsing(s, n, stove, slot)) sSrRan = (now - t0) / 1000.0;
                if (sSrNoPath != null) return "no path to the target: " + sSrNoPath;
                if (cancelled == 0 && (kind == "cancel-start" && now - t0 >= kSrCancelMs || kind == "cancel-use" && sSrRan >= 0))
                {
                    if (s.CurrentInteraction != null) s.InteractionQueue.CancelInteraction(s.CurrentInteraction.Id);
                    cancelled = now;
                }
                if (cancelled != 0 && now - cancelled >= kSrIdleMs) return null;
                idle = SrIdle(s, n) ? (idle == 0 ? now : idle) : 0;
                if (idle != 0 && now - idle >= kSrIdleMs) return null;
                if (now - t0 >= kSrUseMs) { s.InteractionQueue.CancelAllInteractions(); return null; }
                if (now - check < kSrCheckMs) continue;
                bool waiting = (n.StartsWith("Fridge_", StringComparison.Ordinal) || n.StartsWith("Stove_", StringComparison.Ordinal)) && !SrAt(s, slot) && !SrAt(s, sSwFridge.GetPositionOfSlot(Slot.RoutingSlot_0));
                misses = (s.Position - last).Length() > 0.3f || n != lastName || SrLong(n) && !waiting ? 0 : misses + 1;
                check = now;
                last = s.Position;
                lastName = n;
                if (misses >= 2) return "no progress, doing " + n;
            }
        }

        // what the chain left out of eat and clean up gets the player's click on the plate. A missing entry is logged, the failure is the plan's or the Sim's
        static string SrMeal(Sim s, Vector3 slot)
        {
            foreach (string want in new string[] { "Eat", "ServingContainer_CleanUp" })
            {
                if (sSrSteps.Contains(want)) continue;
                GameObject plate = s.GetObjectInRightHand() as ServingContainer;
                if (plate == null)
                    foreach (ServingContainer c in sSwStove.LotCurrent.GetObjects<ServingContainer>()) { plate = c; break; }
                if (plate == null) { sSrSteps.Add("no plate for " + want); return null; }
                if (SrClick(s, plate, want) == null) { sSrSteps.Add("no entry " + want); continue; }
                string why = SrWait(s, slot, false, "meal");
                if (why != null) return why;
            }
            return null;
        }

        // Go Here to X, as a click on the floor
        static string SrBack(Sim s, string label)
        {
            SrAim(null, sSwSpot);
            ScGoHere(s, sSwSpot);
            int t0 = System.Environment.TickCount, check = t0, misses = 0;
            Vector3 last = s.Position;
            while (true)
            {
                Ticks(3);
                int now = System.Environment.TickCount;
                if ((s.Position - sSwSpot).Length() <= 0.6f)
                {
                    SwLine("stove-repeat: " + label + " back on X after " + ((now - t0) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s, plan " + (sSrFirstPlan ?? "none"));
                    return null;
                }
                if (sSrNoPath != null) return "no path back to X: " + sSrNoPath;
                if (now - t0 >= kSrBackMs) return "not back on X, doing " + ScName(s);
                if (now - check < kSrCheckMs) continue;
                misses = (s.Position - last).Length() > 0.3f ? 0 : misses + 1;
                check = now;
                last = s.Position;
                if (misses >= 2) return "no progress back to X, doing " + ScName(s);
            }
        }

        static bool SrAt(Sim s, Vector3 slot)
        {
            return (s.Position - slot).Length() <= 1.8f && !World.WallExistsBetweenPoints(s.LotCurrent.LotId, s.Position, slot, eRoomDefinition.PlacementBlocking);
        }

        static bool SrUsing(Sim s, string n, bool stove, Vector3 slot)
        {
            if (stove) return n.IndexOf("CookOnStove", StringComparison.Ordinal) >= 0 || n.IndexOf("CookInOven", StringComparison.Ordinal) >= 0;
            return n.StartsWith("Fridge_", StringComparison.Ordinal) && (s.Position - slot).Length() < 0.6f;
        }

        // steps of an order: an autonomous one of these is still the meal
        static bool SrLong(string n)
        {
            foreach (string part in new string[] { "_Prepare", "Cook", "Fridge_", "Stove_", "Eat", "Serve", "PutDown", "CleanUp", "UseDishwasher" })
                if (n.IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static bool SrIdle(Sim s, string n)
        {
            InteractionInstance cur = s.CurrentInteraction;
            if (cur == null) return s.InteractionQueue.Count == 0;
            return n == "CustomIdle" || cur.Autonomous && s.InteractionQueue.Count <= 1 && !SrLong(n);
        }

        // the kind's recipe among the enabled menu entries, a single serving if there is one, as a player's click
        static string SrOrder(Sim s, GameObject target, bool stove, string kind)
        {
            GameObjectHit hit = HitFor(target, new Args(Util.Obj()));
            string type = stove ? "Stove_Have" : "Fridge_Have";
            MenuEntry pick = null;
            Recipe chosen = null;
            int rank = -1, have = 0;
            string refused = "";
            foreach (MenuEntry e in BuildMenu(s, target, hit))
            {
                Type def = e.Iop.InteractionDefinition.GetType();
                if (def.DeclaringType == null || def.DeclaringType.Name != type) continue;
                have++;
                if (!e.Enabled) { if (refused.Length < 120) refused += e.Label + ": " + e.Reason + "; "; continue; }
                FieldInfo fr = def.GetField("ChosenRecipe"), fq = def.GetField("Quantity");
                Recipe rc = fr != null ? fr.GetValue(e.Iop.InteractionDefinition) as Recipe : null;
                if (rc == null || rc.CookingProcessData == null) continue;
                bool single = fq != null && (Recipe.MealQuantity)fq.GetValue(e.Iop.InteractionDefinition) == Recipe.MealQuantity.Single;
                int k = SrRank(rc, stove, kind) * 2 + (single ? 1 : 0);
                if (k > rank) { rank = k; pick = e; chosen = rc; }
            }
            Lot lot = target.LotCurrent;
            if (pick == null)
            {
                if (!stove)
                {
                    InteractionInstance snack = Fridge_GetRandomSnack.GetRandomSnackSingleton.CreateInstance(sSwFridge, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                    if (snack != null && s.InteractionQueue.Add(snack)) return "random snack";
                }
                sSrWhy = type + " entries " + have + ", none enabled, usable counter " + Food.LotHasUsableCounter(lot) + ", usable stove " + Food.LotHasUsableStove(lot) + ", funds " + s.FamilyFunds + ", " + refused;
                return null;
            }
            InteractionInstanceParameters p = new InteractionInstanceParameters(pick.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
            if (kind.StartsWith("bake", StringComparison.Ordinal) && !SwIsOven(chosen)) { sSrWhy = "no baked recipe on the menu, best " + chosen.GenericName; return null; }
            InteractionInstance ii = pick.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
            if (ii == null || !s.InteractionQueue.Add(ii)) { sSrWhy = "order refused: " + pick.Label; return null; }
            return chosen.GenericName;
        }

        // bakes and cancel-use bake (the player's failures are on the oven path), the other stove kinds cook on top, the fridge's cancels are snacks
        static int SrRank(Recipe rc, bool stove, string kind)
        {
            if (stove)
            {
                bool oven = SwIsOven(rc), top = !oven && rc.CookingProcessData.UsesAStove;
                return kind != "stove" && kind != "cancel-start" ? (oven ? 2 : top ? 1 : 0) : (top ? 2 : oven ? 1 : 0);
            }
            return (kind == "meal") != rc.IsSnack ? 1 : 0;
        }

        // a cancel can leave a dish in the oven or a pan on the hob, and the stove's menu greys out: the player clicks the dish
        static string SrFinish(Sim s)
        {
            foreach (GameObject o in sSwStove.LotCurrent.GetObjects<GameObject>())
            {
                if (o.Parent != sSwStove) continue;
                string by = SrClick(s, o, "Cook") ?? SrClick(s, o, "Serve");
                if (by != null) return "finish " + o.GetType().Name + " by " + by;
            }
            sSrWhy = "the stove is cooking something and no entry finishes it";
            return null;
        }

        // the first enabled menu entry of an interaction class with that name part, as a player's click
        static string SrClick(Sim s, GameObject o, string part)
        {
            GameObjectHit hit = HitFor(o, new Args(Util.Obj()));
            foreach (MenuEntry e in BuildMenu(s, o, hit))
            {
                Type def = e.Iop.InteractionDefinition.GetType();
                string name = def.DeclaringType != null ? def.DeclaringType.Name : def.Name;
                if (!e.Enabled || name.IndexOf(part, StringComparison.Ordinal) < 0) continue;
                InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None, InteractionFlags.UserDirected, hit, null, false);
                InteractionInstance ii = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
                if (ii != null && s.InteractionQueue.Add(ii)) return name;
            }
            return null;
        }

        static string SrState(string label)
        {
            int world;
            List<GameObject> live = ScLive(out world);
            Lot lot = sSwStove.LotCurrent;
            int users = sSwStove.ActorsUsingMe.Count + sSwFridge.ActorsUsingMe.Count;
            return "stove-repeat: " + label + " state regions " + live.Count + ", dead " + (users == 0 ? live.Count : 0) + ", world " + world + ", walker orders " + sSrWalkOrders + ", users stove " + sSwStove.ActorsUsingMe.Count + " fridge " + sSwFridge.ActorsUsingMe.Count + ", fridge line " + sSwFridge.Line.MemberCount()
                + ", stove dirty " + sSwStove.Cleanable.NeedsToBeCleaned + ", cooking " + sSwStove.IsCookingSomething() + ", usable counter " + Food.LotHasUsableCounter(lot) + ", sim-blocked " + sSrSimBlocked + ", left " + SrLeft(lot);
        }

        // cooking gear and food on the lot, by where it lies and by class: a full counter or dirty pans may be the cause, and nothing here is cleaned
        static string SrLeft(Lot lot)
        {
            int stove = 0, counter = 0, held = 0, floor = 0;
            Dictionary<string, int> types = new Dictionary<string, int>();
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                string ns = o.GetType().Namespace ?? "";
                if (!ns.EndsWith(".CookingObjects", StringComparison.Ordinal) && !ns.EndsWith(".FoodObjects", StringComparison.Ordinal)) continue;
                if (o.Parent is Stove) stove++; else if (o.Parent is GameCounter) counter++; else if (o.Parent is Sim) held++; else floor++;
                int n;
                types.TryGetValue(o.GetType().Name, out n);
                types[o.GetType().Name] = n + 1;
            }
            string line = stove + " on the stove, " + counter + " on counters, " + held + " held, " + floor + " elsewhere:";
            foreach (KeyValuePair<string, int> kv in types)
            {
                if (line.Length > 170) { line += " .."; break; }
                line += " " + kv.Key + " " + kv.Value;
            }
            return line;
        }

        // nothing walks: plans from where the Sim is now, a census, what lies near the slot
        static void SrDump(Sim s, string label)
        {
            Vector3 stove = sSwStove.GetPositionOfSlot(Slot.RoutingSlot_0), fridge = sSwFridge.GetPositionOfSlot(Slot.RoutingSlot_0);
            sSwProbing = true;
            SwPlan(s, "r", label + " to-slot", false, 0, stove);
            SwPlan(s, "r", label + " to-slot-point", false, 1, stove);
            SwPlan(s, "r", label + " fridge-slot", false, 2, fridge);
            SwPlan(s, "r", label + " fridge-point", false, 1, fridge);
            SwPlan(s, "r", label + " to-x", false, 1, sSwSpot);
            SwPlan(s, "r", label + " toward-stove-0.5m", false, 1, stove - sSwStove.ForwardVector * 0.5f);
            Vector3 fwd = sSwStove.ForwardVector, side = new Vector3(fwd.z, 0f, -fwd.x);
            SwPlan(s, "r", label + " ring-1m-out", false, 1, stove + fwd * 1f);
            SwPlan(s, "r", label + " ring-1m-side", false, 1, stove + side * 1f);
            sSwProbing = false;
            SwLine(SrState(label));
            sScQuiet = false;  // SwOvenProbe's near list
            try { SwOvenProbe(s, "r", label); }
            finally { sScQuiet = true; }
        }
    }
}
