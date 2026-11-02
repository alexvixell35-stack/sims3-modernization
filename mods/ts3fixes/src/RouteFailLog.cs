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
    // hooks) or failed DoRoute (SimRoutingComponent.DoRoute) of an active-household Sim: Sim, current interaction, kind,
    // whether the plan waited on a ts3native async job, plan result and blocker, goal, Sim spot, lot and room, ms.
    // "showoff:" lines: every plan, DoRoute and jig placement (SocialJigTwoPerson.PlaceJigAlongRoute) of a Sim whose current
    // interaction is Sim+DoShowOffMove, also on success, so the sequence PlanToPoint -> jig -> RouteToJigA -> DoRoute reads
    // in order. Cap: 30 lines a real minute, 400 a session. Off path (a successful plan outside DoShowOffMove): one Follower
    // read and one type compare. [687] routefail lines, [688] showoff lines, [689] lines dropped by the cap.
    public static partial class Fixes
    {
        static readonly string[] kRflKind = { "Plan", "Replan", "PlanFromPoint", "ReplanFromPoint" };
        static Type sShowOffType;
        static bool sShowOffLooked;
        static readonly Dictionary<ulong, bool> sRflAsync = new Dictionary<ulong, bool>();
        static int sRflMinute = -1, sRflInMinute, sRflTotal;

        static void ApplyRouteFailLog()
        {
            Patch(typeof(SimRoutingComponent), "DoRoute", typeof(RflRouteHook), "DoRouteHook", "DoRouteOrig");
            Patch(typeof(SocialJigTwoPerson), "PlaceJigAlongRoute", new Type[] { typeof(Sim), typeof(Sim), typeof(Route) }, typeof(RflJigHook), "PlaceHook", "PlaceOrig");
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
                bool ok = res.mType == RoutePlanResultType.Succeeded;
                IScriptProxy f = r.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                if (sim == null) return;
                bool show = InShowOff(sim);
                if (show && sim == sSoChild) { sSoSeen = true; sSoPlans++; if (!ok) sSoPlanFails++; if (async) sSoAsync++; }
                if (ok && !show) return;
                if (!show && !sim.IsInActiveHousehold) return;
                if (!ShortcutOn("routefaillog")) return;
                RflLine(show, sim, kRflKind[kind & 3] + (async ? " async" : " sync") + " " + res.mType + " blocker " + res.mBlockerType,
                    r.GetApproximateOriginalDestination(), t0);
            }
            catch (Exception) { }
        }

        static void RflLine(bool show, Sim sim, string what, Vector3 goal, uint t0)
        {
            int minute = (int)(SrcNowUs() / 60000000u); // real minutes (SrcNowUs wraps after ~71 min: one short bucket)
            if (minute != sRflMinute) { sRflMinute = minute; sRflInMinute = 0; }
            int[] c = PatchCounters();
            if (sRflInMinute >= 30 || sRflTotal >= 400) { c[689]++; return; }
            sRflInMinute++; sRflTotal++;
            c[show ? 688 : 687]++;
            InteractionInstance ia = sim.CurrentInteraction;
            Vector3 p = sim.Position;
            Lot lot = sim.LotCurrent;
            string line = (show ? "showoff: " : "routefail: ") + sim.FullName + " 0x" + sim.SimDescription.SimDescriptionId.ToString("X")
                + " " + sim.SimDescription.Age + (sim.IsInActiveHousehold ? " active" : "") + ", " + (ia != null ? ia.GetType().Name : "none") + ", " + what
                + ", goal " + V(goal) + ", at " + V(p) + ", lot 0x" + (lot != null ? lot.LotId.ToString("X") : "0") + " room " + sim.RoomId
                + ", " + (unchecked(SrcNowUs() - t0) / 1000) + " ms";
            DiagLine(line);
        }

        static string V(Vector3 v)
        {
            return v.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public class RflRouteHook : SimRoutingComponent
        {
            public bool DoRouteHook(Route r)
            {
                uint t0 = SrcNowUs();
                bool ok = DoRouteOrig(r);
                try
                {
                    Sim sim = OwnerSim;
                    if (sim != null && sim == sSoChild && InShowOff(sim)) { sSoSeen = true; sSoRoute = ok ? 1 : 0; sSoRoutePlan = r != null ? r.PlanResult.mType.ToString() : "?"; }
                    if (sim != null && r != null && (!ok || InShowOff(sim)) && (InShowOff(sim) || sim.IsInActiveHousehold) && ShortcutOn("routefaillog"))
                    {
                        RoutePlanResult pr = r.PlanResult;
                        RflLine(InShowOff(sim), sim, "DoRoute " + (ok ? "ok" : "false") + " plan " + pr.mType + " blocker " + pr.mBlockerType
                            + (sim.HasExitReason() ? " exit " + sim.ExitReason : ""), r.GetApproximateOriginalDestination(), t0);
                    }
                }
                catch (Exception) { } // logging never changes the route's answer
                return ok;
            }
            public bool DoRouteOrig(Route r) { return false; } // receives SimRoutingComponent.DoRoute's original header
        }

        public class RflJigHook : SocialJigTwoPerson
        {
            public bool PlaceHook(Sim actorA, Sim actorB, Route routeToActorB)
            {
                uint t0 = SrcNowUs();
                bool ok = PlaceOrig(actorA, actorB, routeToActorB);
                try
                {
                    if (actorA != null && actorA == sSoChild && InShowOff(actorA)) { sSoSeen = true; sSoJig = ok ? 1 : 0; }
                    if (actorA != null && actorB != null && InShowOff(actorA) && ShortcutOn("routefaillog"))
                    {
                        RoutePlanResult pr = routeToActorB != null ? routeToActorB.PlanResult : default(RoutePlanResult);
                        RflLine(true, actorA, "jig " + (ok ? "placed" : "NOT placed") + " at " + V(Position) + " (" + (Position - actorB.Position).Length().ToString("0.0", CultureInfo.InvariantCulture)
                            + " m from " + actorB.FullName + " " + actorB.SimDescription.Age + " room " + actorB.RoomId + "), route-to-B plan " + pr.mType,
                            actorB.Position, t0);
                    }
                }
                catch (Exception) { }
                return ok;
            }
            public bool PlaceOrig(Sim actorA, Sim actorB, Route routeToActorB) { return false; } // receives SocialJigTwoPerson.PlaceJigAlongRoute's original header
        }

        // ---- agent check for repro showoff-badges-route (docs/226) ----
        static Sim sSoChild, sSoAdult;
        static bool sSoSeen;
        static int sSoPlans, sSoPlanFails, sSoAsync, sSoJig = -1, sSoRoute = -1;
        static string sSoRoutePlan;
        static uint sSoFiredAt;
        static readonly List<string> sSoArms = new List<string>();
        static readonly List<string> sSoFailed = new List<string>();

        // What "state" returns (reflect_call describes its fields, so a repro "until" can poll them).
        public class SoState { public bool done, queued, seen; public int plans, planFails, async, jig, route; }

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
        //   "state": SoState; done once the child's DoShowOffMove ran its DoRoute, the jig was not placed, or the social left
        //            the queue (or never ran within 30 s).
        //   "record <arm>": notes the arm's outcome (route ok = DoRoute true); never throws, so later arms still run.
        //   "verdict": throws when any recorded arm failed (lists every arm), else returns them.
        public static object ShowOffBadgesCheck(string arg)
        {
            if (arg == "fire")
            {
                if (sSoChild == null || sSoAdult == null) throw new InvalidOperationException("showoff fire: setup first");
                Type t = typeof(Sim).GetNestedType("ShowOffNewSkills", BindingFlags.NonPublic | BindingFlags.Public);
                Type dt = t != null ? t.GetNestedType("Definition", BindingFlags.NonPublic | BindingFlags.Public) : null;
                ConstructorInfo ctor = dt != null ? dt.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(ShowOffMoveType) }, null) : null;
                if (ctor == null) throw new InvalidOperationException("showoff fire: Sim+ShowOffNewSkills+Definition(ShowOffMoveType) not found");
                InteractionDefinition def = (InteractionDefinition)ctor.Invoke(new object[] { ShowOffMoveType.Badges });
                InteractionInstance ii = def.CreateInstance(sSoAdult, sSoChild, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                sSoSeen = false; sSoPlans = 0; sSoPlanFails = 0; sSoAsync = 0; sSoJig = -1; sSoRoute = -1; sSoRoutePlan = null;
                sSoFiredAt = SrcNowUs();
                InShowOff(sSoChild); // resolves sShowOffType
                bool queued = sSoChild.InteractionQueue.Add(ii);
                return sSoChild.FullName + ": Show Off Badges on " + sSoAdult.FullName + " queued " + queued;
            }
            if (arg == "state")
            {
                SoState st = new SoState();
                st.queued = sSoChild != null && SoQueued(sSoChild);
                st.seen = sSoSeen; st.plans = sSoPlans; st.planFails = sSoPlanFails; st.async = sSoAsync; st.jig = sSoJig; st.route = sSoRoute;
                bool late = unchecked(SrcNowUs() - sSoFiredAt) > 30000000u; // 30 s real time
                st.done = sSoRoute != -1 || sSoJig == 0 || (!st.queued && (sSoSeen || late));
                return st;
            }
            if (arg.StartsWith("record "))
            {
                string what = arg.Substring(7) + ": " + (sSoRoute == 1 ? "route ok" : sSoRoute == 0 ? "DoRoute false (balloon), plan " + sSoRoutePlan
                    : sSoJig == 0 ? "jig not placed" : sSoSeen ? "ended before DoRoute" : "DoShowOffMove never ran")
                    + " (plans " + sSoPlans + ", failed " + sSoPlanFails + ", async " + sSoAsync + ", jig " + sSoJig + ")";
                sSoArms.Add(what);
                if (sSoRoute != 1) sSoFailed.Add(what);
                if (sSoChild != null) sSoChild.InteractionQueue.CancelAllInteractions();
                if (sSoAdult != null) sSoAdult.InteractionQueue.CancelAllInteractions();
                return what;
            }
            if (arg == "verdict")
            {
                string all = string.Join("; ", sSoArms.ToArray());
                bool bad = sSoFailed.Count > 0 || sSoArms.Count == 0;
                sSoArms.Clear(); sSoFailed.Clear(); sSoChild = null; sSoAdult = null;
                if (bad) throw new InvalidOperationException("showoff verdict: route failed in some arm: " + (all.Length > 0 ? all : "no arm recorded"));
                return "every arm routed: " + all;
            }
            if (arg != "setup outside" && arg != "setup inside") return "showoff <setup outside|setup inside|fire|state|record <arm>|verdict>";
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
            SoPlace(adult, child.Position + new Vector3(1.5f, 0f, 0f));
            sSoChild = child; sSoAdult = adult;
            return arg + ": " + child.FullName + " (Scouting " + k.SkillLevel + ") at " + V(child.Position) + " room " + child.RoomId
                + ", " + adult.FullName + " " + adult.SimDescription.Age + " at " + V(adult.Position) + " room " + adult.RoomId + ", lot " + lot.Name;
        }
    }
}
