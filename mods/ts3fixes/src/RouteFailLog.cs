using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Skills;

using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // routefaillog (docs/226, player report: a child's Show Off Badges social fails with "cannot go there"). Logging only, on
    // by default (switch "routefaillog"); lines go to ts3native.log through the [UserDiag] channel (UserDiag.cs DiagLine).
    // One "routefail:" line per failed route plan (Route.Plan / Replan / PlanFromPoint / ReplanFromPoint, RouteBackoff.cs
    // hooks) of an active-household Sim: Sim, current interaction, kind, whether the plan waited on a ts3native async job,
    // plan result and blocker, goal, Sim spot, lot and room, ms. No DoRoute hook: that swap was logging only and refused
    // every Sim saved mid-route at load (docs/537).
    // "showoff:" lines: every plan of a Sim whose current interaction is Sim+DoShowOffMove, also on success, and the route
    // end of the two repro Sims (RoutingComponent.OnRouteActionsFinished). The jig placement hook went with DoRoute: logging
    // only, on a yielding path. Cap: 30 lines a real minute, 400 a session. Off path (a successful plan outside DoShowOffMove): one Follower
    // read and one type compare. [687] routefail lines, [688] showoff lines, [689] lines dropped by the cap.
    // docs/197 §7 (player report 2026-11-07, Go Here fails on the home lot): routefail lines of the active household are never
    // dropped and carry ", route X" (ts3native appends that job's fields, "routefail job:"). A user-directed interaction of an
    // active-household Sim is a "cmd": gohere start / goal / plan / near / end lines (near: only after a failed plan, 3 times a
    // cmd at most, 8 objects within 2 m; end: EA's route-end event). routefail lines of a cmd say "cmd N". Failed plans of every Sim are counted per Sim and
    // per lot, objects added / removed per lot; "routefail summary" lines at world quit and every 15 min with new failures.
    // Off path (autonomous plan, no failure): one flag read.
    public static partial class Fixes
    {
        static readonly string[] kRflKind = { "Plan", "Replan", "PlanFromPoint", "ReplanFromPoint" };
        static Type sShowOffType;
        static bool sShowOffLooked;
        static readonly Dictionary<ulong, bool> sRflAsync = new Dictionary<ulong, bool>();
        static int sRflMinute = -1, sRflInMinute, sRflTotal;
        class RflCmd { public WeakReference Ia;  // weak (issue #40): an ended command kept its interaction and Sim reachable
            public int Id, Attempts, Fails, Near, Lines; public uint Route, T0; public Vector3 Goal; public string Last; }
        class RflRow { public string Name; public int Seen, Unseen; public bool Household; }
        static readonly Dictionary<ulong, RflCmd> sRflCmds = new Dictionary<ulong, RflCmd>();  // by Sim object id
        static readonly Dictionary<ulong, RflRow> sRflSims = new Dictionary<ulong, RflRow>();
        static readonly Dictionary<ulong, int[]> sRflLots = new Dictionary<ulong, int[]>();    // fails, added, removed, added and removed in build/buy
        const int kRflMaxRows = 512;
        static int sRflCmdSeq, sRflFails, sRflSeen, sRflUnseen, sRflHousehold, sRflTownie, sRflHome, sRflOther, sRflSummaryN, sRflSummaryFails, sRflSummaryAt, sRflBbAt;
        static EventHandler sRflPlaced, sRflGone;

        static void ApplyRouteFailLog()
        {
        }

        // at each world load: the object counters; -= first, the world's event lists outlive a quit
        static void RflWorldLoad()
        {
            if (sRflPlaced == null)
            {
                sRflPlaced = delegate(object o, EventArgs e) { World.OnObjectPlacedInLotEventArgs a = e as World.OnObjectPlacedInLotEventArgs; if (a != null) RflObjectEvent(a.LotId, 1); };
                sRflGone = delegate(object o, EventArgs e) { World.OnObjectPendingDestructionEventArgs a = e as World.OnObjectPendingDestructionEventArgs; if (a != null) RflObjectEvent(a.LotId, 2); };
            }
            World.OnObjectPlacedInLotEventHandler -= sRflPlaced;
            World.OnObjectPlacedInLotEventHandler += sRflPlaced;
            World.OnObjectPendingDestructionEventHandler -= sRflGone;
            World.OnObjectPendingDestructionEventHandler += sRflGone;
        }

        static int[] RflLot(ulong lot)
        {
            int[] row;
            if (sRflLots.TryGetValue(lot, out row)) return row;
            if (sRflLots.Count >= kRflMaxRows) return new int[5];
            return sRflLots[lot] = new int[5];
        }

        static void RflObjectEvent(ulong lot, int slot)
        {
            try
            {
                int[] row = RflLot(lot);
                row[slot]++;
                if (!Sims3.Gameplay.GameStates.IsBuildBuyLikeState) return;
                row[slot + 2]++;
                int now = System.Environment.TickCount;
                if (unchecked(now - sRflBbAt) < 60000 && sRflBbAt != 0) return;
                sRflBbAt = now;  // one line per burst: when build/buy touched which lot
                DiagLine("routefail buildbuy: lot 0x" + lot.ToString("X") + (slot == 1 ? " object added" : " object removed") + ", session add " + row[3] + " remove " + row[4]);
            }
            catch (Exception) { }
        }

        static void RflCount(Sim sim)
        {
            bool seen = UfWhy(sim) != 0, hh = sim.IsInActiveHousehold;
            sRflFails++;
            if (seen) sRflSeen++; else sRflUnseen++;
            if (hh) sRflHousehold++; else sRflTownie++;
            Lot lot = sim.LotCurrent;
            if (lot != null && lot == sim.LotHome) sRflHome++; else sRflOther++;
            RflLot(lot != null ? lot.LotId : 0)[0]++;
            ulong id = sim.SimDescription.SimDescriptionId;
            RflRow r;
            if (!sRflSims.TryGetValue(id, out r))
            {
                if (sRflSims.Count >= kRflMaxRows) return;
                r = new RflRow();
                r.Name = sim.FullName + " 0x" + id.ToString("X");
                r.Household = hh;
                sRflSims[id] = r;
            }
            if (seen) r.Seen++; else r.Unseen++;
            int now = System.Environment.TickCount;
            if (sRflSummaryAt == 0) sRflSummaryAt = now;
            if (unchecked(now - sRflSummaryAt) >= 900000) RflSummary("15 min");
        }

        // cumulative: a crash after the last line still leaves the numbers up to it
        static void RflSummary(string why)
        {
            try
            {
                if (sRflFails == sRflSummaryFails && why != "world quit") return;
                sRflSummaryAt = System.Environment.TickCount;
                sRflSummaryFails = sRflFails;
                sRflSummaryN++;
                DiagLine("routefail summary #" + sRflSummaryN + " (" + why + "): failed plans " + sRflFails + ", seen " + sRflSeen + " unseen " + sRflUnseen
                    + ", household " + sRflHousehold + " townie " + sRflTownie + ", on own home lot " + sRflHome + " elsewhere " + sRflOther
                    + ", Sims " + sRflSims.Count + ", lots " + sRflLots.Count);
                List<KeyValuePair<ulong, RflRow>> sims = new List<KeyValuePair<ulong, RflRow>>(sRflSims);
                sims.Sort(delegate(KeyValuePair<ulong, RflRow> a, KeyValuePair<ulong, RflRow> b) { return (b.Value.Seen + b.Value.Unseen).CompareTo(a.Value.Seen + a.Value.Unseen); });
                List<string> items = new List<string>();
                for (int i = 0; i < sims.Count && i < 12; i++)
                {
                    RflRow r = sims[i].Value;
                    items.Add(r.Name + (r.Household ? " household" : " townie") + " seen " + r.Seen + " unseen " + r.Unseen);
                }
                DiagList("routefail summary Sims: ", items);
                List<KeyValuePair<ulong, int[]>> lots = new List<KeyValuePair<ulong, int[]>>(sRflLots);
                lots.Sort(delegate(KeyValuePair<ulong, int[]> a, KeyValuePair<ulong, int[]> b) { return (b.Value[0] + b.Value[1] + b.Value[2]).CompareTo(a.Value[0] + a.Value[1] + a.Value[2]); });
                items = new List<string>();
                for (int i = 0; i < lots.Count && i < 8; i++)
                {
                    int[] v = lots[i].Value;
                    items.Add("0x" + lots[i].Key.ToString("X") + " fails " + v[0] + " added " + v[1] + " removed " + v[2] + " build/buy +" + v[3] + " -" + v[4]);
                }
                DiagList("routefail summary lots: ", items);
            }
            catch (Exception) { }
        }

        // the user-directed interaction this Sim runs, null = none. shortcut: an interaction a mod queues at user priority counts too
        static RflCmd RflCmdOf(Sim sim)
        {
            InteractionInstance ia = sim.CurrentInteraction;
            if (ia == null || ia.Autonomous || ia.GetPriority().Level != InteractionPriorityLevel.UserDirected || !sim.IsInActiveHousehold) return null;
            if (!ShortcutOn("routefaillog")) return null;
            ulong id = sim.ObjectId.Value;
            RflCmd c;
            if (sRflCmds.TryGetValue(id, out c) && ReferenceEquals(c.Ia.Target, ia)) return c;
            if (sRflCmds.Count >= 64) sRflCmds.Clear();
            c = new RflCmd();
            c.Ia = new WeakReference(ia);
            c.Id = ++sRflCmdSeq;
            sRflCmds[id] = c;
            return c;
        }

        static string RflLoc(Vector3 v)
        {
            LotLocation l = LotLocation.Invalid;
            ulong lot = World.GetLotLocation(v, ref l, eRoomDefinition.Default);
            return V(v) + " lot 0x" + lot.ToString("X") + " level " + l.mLevel + " room " + l.mRoom;
        }

        static string RflObj(ulong id)
        {
            GameObject o = id != 0 ? GameObject.GetObject(new ObjectGuid(id)) : null;
            return o != null ? o.GetType().Name : "none";
        }

        static void RflCmdStart(RflCmd c, Sim sim, Vector3 goal)
        {
            InteractionInstance ia = c.Ia.Target as InteractionInstance;
            if (ia == null) return;  // collected; RflCmdOf just matched it to the current interaction, so not expected
            Household h = sim.Household;
            int why = UfWhy(sim);
            DiagLine("gohere start: cmd " + c.Id + " " + sim.FullName + " 0x" + sim.SimDescription.SimDescriptionId.ToString("X") + " follower " + sim.ObjectId.Value.ToString("X16")
                + ", household " + (h != null ? h.Name : "none") + (sim.IsInActiveHousehold ? " active" : "") + (sim == Sim.ActiveActor ? " selected" : ""
                ) + (sim.IsSelectable ? " selectable" : "") + ", why " + why + (why != 0 ? " seen" : " unseen") + ", interaction " + ia.GetType().Name
                + (ia.Target != null ? " target " + ia.Target.GetType().Name : "") + ", from " + RflLoc(sim.Position));
            var rc = sim.SimRoutingComponent;  // EA's own route-end event (no DoRoute swap, docs/537); -= first, one subscription a Sim
            if (rc != null) { rc.OnRouteActionsFinished -= RflCmdEnd; rc.OnRouteActionsFinished += RflCmdEnd; }
            FieldInfo f = ia.GetType().GetField("Hit", BindingFlags.Public | BindingFlags.Instance);  // InteractionGameObjectHit<,>.Hit: where the player clicked
            string click = "no click point";
            if (f != null && f.FieldType == typeof(GameObjectHit))
            {
                GameObjectHit hit = (GameObjectHit)f.GetValue(ia);
                click = "click " + RflLoc(hit.mPoint) + " on " + hit.mType + " " + RflObj(hit.mId);
            }
            DiagLine("gohere goal: cmd " + c.Id + " goal " + RflLoc(goal) + " terrain " + World.GetTerrainType(goal) + (World.IsPositionOutside(goal) ? " outside" : " inside") + ", " + click);
        }

        static void RflCmdPlan(RflCmd c, ScriptCore.Route r, Sim sim, string what, RoutePlanResult res, uint t0)
        {
            Vector3 goal = r.GetApproximateOriginalDestination();
            if (c.Attempts++ == 0) RflCmdStart(c, sim, goal);
            bool ok = res.mType == RoutePlanResultType.Succeeded;
            c.Route = r.Handle; c.Goal = goal; c.T0 = t0; c.Last = what + " blocker " + res.mBlockerType;
            string b = res.mBlockerType.ToString();
            if (res.mBlockerType != RouteBlockerType.None) b += " 0x" + res.mBlockerObjectId.ToString("X") + " " + RflObj(res.mBlockerObjectId) + " at " + V(res.mBlockerLocation);
            DiagLine("gohere plan: cmd " + c.Id + " attempt " + c.Attempts + ", " + what + ", " + (ok ? "ok" : "failed") + " blocker " + b + ", path " + res.mDistanceTotal.ToString("0.0", CultureInfo.InvariantCulture)
                + " m, candidates " + res.mNumCandidatePointsGenerated + "/" + res.mNumAcceptedPoints + "/" + res.mNumRejectedPoints + ", " + (unchecked(SrcNowUs() - t0) / 1000) + " ms, route " + r.Handle.ToString("X8"));
            if (ok) return;
            c.Fails++;
            if (c.Near++ < 3) RflNear(c, goal);
        }

        // end of a cmd's route: how far from the goal the Sim stopped
        static void RflCmdEnd(GameObject o, bool ok)
        {
            try
            {
                Sim sim = o as Sim;
                RflCmd c;
                if (sim == null || !sRflCmds.TryGetValue(sim.ObjectId.Value, out c) || c.Attempts == 0) return;
                DiagLine("gohere end: cmd " + c.Id + " route " + (ok ? "ok" : "false") + ", last plan " + c.Last + (sim.HasExitReason() ? " exit " + sim.ExitReason : "")
                    + ", " + (sim.Position - c.Goal).Length().ToString("0.0", CultureInfo.InvariantCulture) + " m from goal " + V(c.Goal) + ", at " + V(sim.Position) + ", plans " + c.Attempts
                    + " failed " + c.Fails + ", routefail lines " + c.Lines + ", " + (unchecked(SrcNowUs() - c.T0) / 1000) + " ms, route " + c.Route.ToString("X8"));
            }
            catch (Exception) { }
        }

        // around a failed goal. Walls by direction, bit 0 +x, 1 -x, 2 +z, 3 -z, 1 m out; a fence blocks placement, not light [inferred]
        static void RflNear(RflCmd c, Vector3 goal)
        {
            LotLocation l = LotLocation.Invalid;
            ulong lot = World.GetLotLocation(goal, ref l, eRoomDefinition.Default);
            int light = 0, place = 0;
            Vector3[] dir = { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f) };
            for (int i = 0; i < 4 && lot != 0; i++)
            {
                if (World.WallExistsBetweenPoints(lot, goal, goal + dir[i], eRoomDefinition.LightBlocking)) light |= 1 << i;
                if (World.WallExistsBetweenPoints(lot, goal, goal + dir[i], eRoomDefinition.PlacementBlocking)) place |= 1 << i;
            }
            List<string> items = new List<string>();
            GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(goal, 2f);
            for (int i = 0; i < near.Length && i < 40 && items.Count < 8; i++)
            {
                GameObject o = near[i];
                if (o is Sim && o.Position == goal) continue;
                bool fp = false;
                uint[] hashes = o.GetFootprintNameHashes();
                for (int k = 0; hashes != null && k < hashes.Length && k < 4 && !fp; k++)
                    fp = World.ObjectIsFootprintEnabled(o.ObjectId, hashes[k]) && World.ObjectFootprintContainsPoint(o.ObjectId, hashes[k], goal);
                items.Add(o.GetType().Name + " " + (o.Position - goal).Length().ToString("0.0", CultureInfo.InvariantCulture) + " m level " + o.Level + " room " + o.RoomId + (fp ? " COVERS GOAL" : ""));
            }
            DiagLine("gohere near: cmd " + c.Id + " level " + l.mLevel + " room " + l.mRoom + ", walls light 0x" + light.ToString("X") + " placement 0x" + place.ToString("X") + ", objects within 2 m " + near.Length);
            DiagList("gohere near: cmd " + c.Id + " ", items);
        }

        // AsyncWait (RouteBackoff.cs): this task's plan waits on a ts3native job. Read and cleared by RflPlan.
        static void RflAsyncMark()
        {
            try { sRflAsync[Simulator.CurrentTask.Value] = true; } catch (Exception) { }
        }

        static bool InShowOff(Sim sim)
        {
            if (!sShowOffLooked) { sShowOffLooked = true; sShowOffType = typeof(Sim).GetNestedType("DoShowOffMove", BindingFlags.NonPublic | BindingFlags.Public); }
            InteractionInstance ia = sim.CurrentInteraction;
            return ia != null && sShowOffType != null && ia.GetType() == sShowOffType;
        }

        // after each plan hook; t0 = SrcNowUs() before the plan
        static void RflPlan(ScriptCore.Route r, int kind, uint t0, RoutePlanResult res)
        {
            try
            {
                bool async = false;
                if (sRflAsync.Count > 0)
                {
                    ulong task = Simulator.CurrentTask.Value;
                    if (sRflAsync.ContainsKey(task)) { async = true; sRflAsync.Remove(task); }
                    if (sRflAsync.Count > 64) sRflAsync.Clear(); // a task reset mid-wait never reaches here
                }
                if (kind == 3) HcBoatEnd(r, t0, res, async); // boatreplanlog (HitchCause.cs)
                bool ok = res.mType == RoutePlanResultType.Succeeded;
                IScriptProxy f = r.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                if (sim == null) return;
                if (sim.IsInActiveHousehold) PatchCounters()[866]++;  // fails per plan: same four hooks as the routefail lines (issue #304, kd-household-ab.ts)
                bool show = InShowOff(sim);
                if (show && sim == sSoChild) { sSoSeen = true; sSoPlans++; if (!ok) sSoPlanFails++; if (async) sSoAsync++; }
                if (show && sim == sSoAdult && !ok && sSoAdultRoute == -1) sSoAdultPlanFails++; // asyncjigsync repro: the partner's plans before its DoRoute (RouteToJigB to an unplaced jig); later plans (e.g. a locked-portal PlanFromPoint after the social) do not count
                RflCmd cmd = RflCmdOf(sim);
                string what = kRflKind[kind & 3] + (async ? " async" : " sync") + " " + res.mType;
                if (cmd != null) RflCmdPlan(cmd, r, sim, what, res, t0);
                if (ok && !show) return;
                if (!ShortcutOn("routefaillog")) return;
                if (!ok) RflCount(sim);
                if (!show && !sim.IsInActiveHousehold) return;
                RflLine(show, sim, what + " blocker " + res.mBlockerType + RflBlocker(res.mBlockerObjectId), r.GetApproximateOriginalDestination(), t0, r.Handle, cmd);
            }
            catch (Exception) { }
        }

        static string RflBlocker(ulong id)
        {
            if (id == 0) return "";
            GameObject o = GameObject.GetObject(new ObjectGuid(id));
            if (o == null) return " 0x" + id.ToString("X") + " no object";
            // a region's footprint is a "DynamicSlot" with script class Null, Model hidden (DynamicRegionBase.UpdateFootprint); other Nulls are props
            return " 0x" + id.ToString("X") + " " + (o is Null && (o.GetHiddenFlags() & HiddenFlags.Model) != 0 ? "region footprint (Null, DynamicSlot)" : o.GetType().Name) + " at " + V(o.Position);
        }

        static void RflLine(bool show, Sim sim, string what, Vector3 goal, uint t0, uint route, RflCmd cmd)
        {
            int minute = (int)(SrcNowUs() / 60000000u); // real minutes (SrcNowUs wraps after ~71 min: one short bucket)
            if (minute != sRflMinute) { sRflMinute = minute; sRflInMinute = 0; }
            int[] c = PatchCounters();
            if (!sim.IsInActiveHousehold && (sRflInMinute >= 30 || sRflTotal >= 400)) { c[689]++; return; }
            sRflInMinute++; sRflTotal++;
            c[show ? 688 : 687]++;
            InteractionInstance ia = sim.CurrentInteraction;
            Vector3 p = sim.Position;
            Lot lot = sim.LotCurrent;
            string line = (show ? "showoff: " : "routefail: ") + sim.FullName + " 0x" + sim.SimDescription.SimDescriptionId.ToString("X")
                + " " + sim.SimDescription.Age + (sim.IsInActiveHousehold ? " active" : "") + ", " + (ia != null ? ia.GetType().Name : "none") + ", " + what
                + ", goal " + V(goal) + ", at " + V(p) + ", lot 0x" + (lot != null ? lot.LotId.ToString("X") : "0") + " room " + sim.RoomId
                + ", " + (unchecked(SrcNowUs() - t0) / 1000) + " ms, " + (UfWhy(sim) != 0 ? "seen" : "unseen")
                + (cmd != null ? ", cmd " + cmd.Id : "") + (route != 0 ? ", route " + route.ToString("X8") : "");
            if (cmd != null) cmd.Lines++;
            DiagLine(line);
        }

        static string V(Vector3 v)
        {
            return v.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // repro Sims only (subscribed in setup): EA's own route-end event, no swap
        static void RflRouteEnd(GameObject o, bool ok)
        {
            try
            {
                Sim sim = o as Sim;
                if (sim == null || !InShowOff(sim)) return;
                if (sim == sSoChild) { sSoSeen = true; sSoRoute = ok ? 1 : 0; }
                else if (sim == sSoAdult) sSoAdultRoute = ok ? 1 : 0;
                else return;
                if (ShortcutOn("routefaillog")) RflLine(true, sim, "route end " + (ok ? "ok" : "false"), sim.Position, SrcNowUs(), 0, null);
            }
            catch (Exception) { } // logging never changes the route's answer
        }

        // ---- agent check for repro showoff-badges-route (docs/226) ----
        static Sim sSoChild, sSoAdult;
        static bool sSoSeen;
        static int sSoPlans, sSoPlanFails, sSoAsync, sSoRoute = -1, sSoAdultPlanFails, sSoAdultRoute = -1;
        static uint sSoFiredAt;
        static readonly List<string> sSoArms = new List<string>();
        static readonly List<string> sSoFailed = new List<string>();

        // What "state" returns (reflect_call describes its fields, so a repro "until" can poll them).
        public class SoState { public bool done, queued, seen; public int plans, planFails, async, route; }

        static bool SoQueued(Sim s)
        {
            Type t = typeof(Sim).GetNestedType("ShowOffNewSkills", BindingFlags.NonPublic | BindingFlags.Public);
            InteractionInstance cur = s.CurrentInteraction;
            if (cur != null && (cur.GetType() == t || InShowOff(s))) return true;
            foreach (InteractionInstance ii in s.InteractionQueue.InteractionList) if (ii.GetType() == t || ii.GetType() == sShowOffType) return true;
            return false;
        }

        static void SoPlace(Sim sim, Vector3 near)
        {
            Vector3 pos, fwd;
            if (!Sims3.Gameplay.GlobalFunctions.FindGoodLocation(sim, new World.FindGoodLocationParams(near), out pos, out fwd))
                throw new InvalidOperationException("showoff setup: no good location near " + V(near) + " for " + sim.FullName);
            sim.InteractionQueue.CancelAllInteractions();
            PlaceSim(sim, pos, fwd);
        }

        // Agent check (repro showoff-badges-route): the active household's child shows off scouting badges to one of its adults.
        //   "setup outside" / "setup inside": the first child (Scouting raised to 1 if lower: ShowOffNewSkills.Test needs level
        //            >= 1 for Badges) and the first young adult / adult / elder of the active household, both placed on the
        //            home lot: outside next to the mailbox, inside next to the first ground-floor object in a room. Test
        //            shortcut: the Sims are placed, not walked there.
        //   "fire":  queues EA's ShowOffNewSkills (Badges definition) on the child with the adult as target, as the pie menu does.
        //   "state": SoState; done once the child's DoShowOffMove ran its DoRoute, or the social left
        //            the queue (or never ran within 30 s).
        //   "record <arm>": notes the arm's outcome (route ok = DoRoute true); never throws, so later arms still run.
        //   "verdict": throws when any recorded arm failed (lists every arm), else returns them.
        public static object ShowOffBadgesCheck(string arg)
        {
            if (arg.StartsWith("fire social "))
            {
                // asyncjigsync smoke (docs/240): an EA social by its action key (e.g. "Friendly Hug"), adult on the child, as the pie menu queues it
                if (sSoChild == null || sSoAdult == null) throw new InvalidOperationException("showoff fire social: setup first");
                Sims3.Gameplay.Socializing.SocialInteractionA.Definition sd = new Sims3.Gameplay.Socializing.SocialInteractionA.Definition(arg.Substring(12), new string[0], null, false);
                InteractionInstance si = sd.CreateInstance(sSoChild, sSoAdult, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                return sSoAdult.FullName + ": " + arg.Substring(12) + " on " + sSoChild.FullName + " queued " + sSoAdult.InteractionQueue.Add(si);
            }
            if (arg == "fire")
            {
                if (sSoChild == null || sSoAdult == null) throw new InvalidOperationException("showoff fire: setup first");
                Type t = typeof(Sim).GetNestedType("ShowOffNewSkills", BindingFlags.NonPublic | BindingFlags.Public);
                Type dt = t != null ? t.GetNestedType("Definition", BindingFlags.NonPublic | BindingFlags.Public) : null;
                ConstructorInfo ctor = dt != null ? dt.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(ShowOffMoveType) }, null) : null;
                if (ctor == null) throw new InvalidOperationException("showoff fire: Sim+ShowOffNewSkills+Definition(ShowOffMoveType) not found");
                InteractionDefinition def = (InteractionDefinition)ctor.Invoke(new object[] { ShowOffMoveType.Badges });
                InteractionInstance ii = def.CreateInstance(sSoAdult, sSoChild, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                sSoSeen = false; sSoPlans = 0; sSoPlanFails = 0; sSoAsync = 0; sSoRoute = -1; sSoAdultPlanFails = 0; sSoAdultRoute = -1;
                sSoFiredAt = SrcNowUs();
                InShowOff(sSoChild); // resolves sShowOffType
                bool queued = sSoChild.InteractionQueue.Add(ii);
                return sSoChild.FullName + ": Show Off Badges on " + sSoAdult.FullName + " queued " + queued;
            }
            if (arg == "state")
            {
                SoState st = new SoState();
                st.queued = sSoChild != null && SoQueued(sSoChild);
                st.seen = sSoSeen; st.plans = sSoPlans; st.planFails = sSoPlanFails; st.async = sSoAsync; st.route = sSoRoute;
                bool late = unchecked(SrcNowUs() - sSoFiredAt) > 30000000u; // 30 s real time
                st.done = sSoRoute != -1 || (!st.queued && (sSoSeen || late));
                return st;
            }
            if (arg.StartsWith("record "))
            {
                string what = arg.Substring(7) + ": " + (sSoRoute == 1 ? "route ok" : sSoRoute == 0 ? "route false (balloon)"
                    : sSoSeen ? "ended before DoRoute" : "DoShowOffMove never ran")
                    + " (plans " + sSoPlans + ", failed " + sSoPlanFails + ", async " + sSoAsync
                    + ", adult plan fails " + sSoAdultPlanFails + ", adult DoRoute " + sSoAdultRoute + ")";
                sSoArms.Add(what);
                if (sSoRoute != 1 || sSoAdultPlanFails > 0 || sSoAdultRoute == 0) sSoFailed.Add(what); // adult: asyncjigsync repro (docs/240)
                if (sSoChild != null) sSoChild.InteractionQueue.CancelAllInteractions();
                if (sSoAdult != null) sSoAdult.InteractionQueue.CancelAllInteractions();
                return what;
            }
            // "verdict ab" (asyncjigsync, docs/240): arms named "off ..." must fail at least once (the race reproduced), every other arm never
            if (arg == "verdict ab")
            {
                int offN = 0, offBad = 0, onBad = 0, total = sSoArms.Count;
                foreach (string s in sSoArms) if (s.StartsWith("off ")) offN++;
                foreach (string s in sSoFailed) if (s.StartsWith("off ")) offBad++; else onBad++;
                string sum = "off arm " + offBad + "/" + offN + " failed, other arms " + onBad + "/" + (total - offN) + " failed";
                string all2 = string.Join("; ", sSoArms.ToArray());
                sSoArms.Clear(); sSoFailed.Clear(); sSoChild = null; sSoAdult = null;
                if (onBad > 0 || offBad == 0 || offN == 0 || offN == total) throw new InvalidOperationException("showoff verdict ab: " + sum + (offBad == 0 ? " (off arm did not reproduce)" : "") + ": " + all2);
                return sum;
            }
            if (arg == "verdict")
            {
                string all = string.Join("; ", sSoArms.ToArray());
                bool bad = sSoFailed.Count > 0 || sSoArms.Count == 0;
                sSoArms.Clear(); sSoFailed.Clear(); sSoChild = null; sSoAdult = null;
                if (bad) throw new InvalidOperationException("showoff verdict: route failed in some arm: " + (all.Length > 0 ? all : "no arm recorded"));
                return "every arm routed: " + all;
            }
            float dist = 1.5f; // "setup outside 8": the adult about 8 m away (asyncjigsync repro)
            if (arg.StartsWith("setup ") && arg.LastIndexOf(' ') > 6) { float.TryParse(arg.Substring(arg.LastIndexOf(' ') + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out dist); if (!(dist > 0f)) dist = 1.5f; arg = arg.Substring(0, arg.LastIndexOf(' ')); }
            if (arg != "setup outside" && arg != "setup inside") return "showoff <setup outside|setup inside|fire|state|record <arm>|verdict|verdict ab> (setup takes an optional distance in m)";
            Household h = Household.ActiveHousehold;
            if (h == null || h.LotHome == null) throw new InvalidOperationException("showoff setup: no active household with a home lot");
            Sim child = null, adult = null;
            foreach (Sim s in h.Sims)
            {
                if (child == null && s.SimDescription.Child) child = s;
                else if (adult == null && s.SimDescription.YoungAdultOrAbove) adult = s;
            }
            if (child == null || adult == null) throw new InvalidOperationException("showoff setup: the active household needs a child and an adult (make_sim {age: Child} first)");
            Skill k = child.SkillManager.GetElement(SkillNames.Scouting) ?? child.SkillManager.AddElement(SkillNames.Scouting);
            if (k == null) throw new InvalidOperationException("showoff setup: " + child.FullName + " cannot learn Scouting (Generations not installed?)");
            if (k.SkillLevel < 1) k.ForceSkillLevelUp(1);
            Lot lot = h.LotHome;
            Vector3 spot = Vector3.Invalid;
            if (arg == "setup outside")
            {
                Mailbox mb = lot.FindMailbox();
                if (mb == null) throw new InvalidOperationException("showoff setup outside: no mailbox on " + lot.Name);
                spot = mb.Position;
            }
            else
            {
                ulong best = ulong.MaxValue;
                foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>(lot))
                    if (!(o is Sim) && !(o is Door) && o.RoomId > 0 && o.Level == 0 && o.ObjectId.Value < best) { best = o.ObjectId.Value; spot = o.Position; }
                if (best == ulong.MaxValue) throw new InvalidOperationException("showoff setup inside: no ground-floor object in a room on " + lot.Name);
            }
            SoPlace(child, spot);
            SoPlace(adult, child.Position + new Vector3(dist, 0f, 0f));
            if (sSoChild != null) sSoChild.SimRoutingComponent.OnRouteActionsFinished -= RflRouteEnd;
            if (sSoAdult != null) sSoAdult.SimRoutingComponent.OnRouteActionsFinished -= RflRouteEnd;
            child.SimRoutingComponent.OnRouteActionsFinished += RflRouteEnd;
            adult.SimRoutingComponent.OnRouteActionsFinished += RflRouteEnd;
            sSoChild = child; sSoAdult = adult;
            return arg + ": " + child.FullName + " (Scouting " + k.SkillLevel + ") at " + V(child.Position) + " room " + child.RoomId
                + ", " + adult.FullName + " " + adult.SimDescription.Age + " at " + V(adult.Position) + " room " + adult.RoomId + ", lot " + lot.Name;
        }
    }
}
