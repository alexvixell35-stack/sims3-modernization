using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // clipwait / clipwaithh (docs/686 §9, default off). A player's wish: a Sim that waits because another Sim (busy at a
    // mirror, standing, chatting in a hallway) or a pet is in its way walks through it after a moment; clipping is fine,
    // waiting is not. No teleport: the Sim walks its own route.
    //
    // [verified, decompiled Sims3.Gameplay.Routing] Outside portals a walking Sim stands for other Sims in two actions:
    //   - ObstacleEncounteredRouteInterruptAction (the route's way is blocked): a stand-and-wait of up to
    //     kDefaultStandAndWaitDuration (50 sim-minutes in the code, 20 in the tuning file) that replans around people and
    //     pushes the pushable ones each cycle. A Sim in an interaction or a conversation cannot be pushed, so the wait
    //     runs until that Sim leaves or the time is over (route failed).
    //   - DestinationObstructedRouteInterruptAction (something stands on the route's end point): the same wait, pushing
    //     the Sims within 0.6 m of the end each cycle; at its end the route fails with the "Sim in the way" balloon.
    // Fix, after kClipGraceMinutes of such a wait (the game's own pushes and replans come first), ClipWaitCore.Decide:
    //   - only Sims and pets in the way (toddler or older; an object in the way keeps the wait): the route is planned
    //     without "blocked by people" when its plan had failed, the game's ignore-obstacles timer is started for
    //     kClipWaitMinutes and the wait ends with the game's own "continue and follow the path";
    //   - a Sim at the route's end is walked into only on a plain floor spot, or at an object that Sim is not using
    //     (kClipWaitObjectDest); never for a social (the walker's interaction targets a Sim), never on the player's own
    //     Go Here, never when that Sim uses the object the walker goes to. The destination-obstructed event of that route
    //     is then not answered with a new wait. A Sim at the end that may not be walked into does not stop the walker
    //     from passing the Sims on its way there; the game's own handling of the route's end runs when it arrives.
    // The grace is the one tunable simpass and petrelax use too (docs/686 §8.8): simpass (ShareLanes.cs) acts first where
    // it applies (at once near a portal, elsewhere at the same grace; no Sim at the route's end, plan intact); clipwait
    // is the rule for the waits that are left: a Sim at the route's end, a plan that failed on people, horses. Scope: clipwait covers Sims the player does not control, as
    // walker and as the Sim walked through; clipwaithh adds household and selected Sims.
    // Off = compare mode: the hooks count the waits and what the rule would do. clipwaitlog writes one line per walk.
    // Counters 4398-4413. Check: ClipWaitCheck.
    public static partial class Fixes
    {
        const int kClwGrace = 4398, kClwWalked = 4399, kClwDest = 4400, kClwErrors = 4401, kClwWould = 4402, kClwExempt = 4403,
            kClwKeptObject = 4404, kClwKeptInUse = 4405, kClwKeptSocial = 4406, kClwKeptGoHere = 4407, kClwPlanFailed = 4408,
            kClwDobWaits = 4409, kClwDobTenths = 4410, kClwDobFails = 4411, kClwEventSkips = 4412, kClwHousehold = 4413;
        const int kClwSiteObstacle = 0, kClwSiteDest = 1, kClwSiteEvent = 2, kClwSiteDobBegin = 3, kClwSiteDobEnd = 4, kClwSiteGather = 5;
        static readonly string[] kClwSites = { "obstacle wait", "destination wait", "destination event", "destination wait begin", "destination wait end", "gather" };
        static readonly int[] sClwSiteErrors = new int[6];
        const int kClwErrorLines = 3;  // lines per site
        const int kClwMaxTable = 64;

        class ClwWait { public object Action; public DateAndTime Start; public bool Counted, PetCounted; }
        class ClwGrant { public Route Route; public DateAndTime Start; }
        static readonly Dictionary<ulong, ClwWait> sClwDob = new Dictionary<ulong, ClwWait>();      // destination waits running, by Sim
        static readonly Dictionary<ulong, ClwGrant> sClwGrant = new Dictionary<ulong, ClwGrant>();  // routes that may end in a Sim's place, by Sim
        static readonly LineCapCore sClwCap = new LineCapCore(20, 60000, 300);
        static Type sClwDobType;
        static FieldInfo sClwDobExiting, sClwDobReturn;
        static string sClwMissing = "", sClwLast = "", sClwLastError = "";

        static void ClwError(int site, Exception e)
        {
            PatchCounters()[kClwErrors]++;
            sClwLastError = "clipwait: error at " + kClwSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sClwSiteErrors[site]++ < kClwErrorLines) DiagLine(sClwLastError);
        }

        static string ClwErrorSites()
        {
            string r = "";
            for (int i = 0; i < sClwSiteErrors.Length; i++)
                if (sClwSiteErrors[i] > 0) r += (r.Length > 0 ? ", " : "") + kClwSites[i] + " " + sClwSiteErrors[i];
            return r.Length > 0 ? " (" + r + "; last: " + sClwLastError + ")" : "";
        }

        // After ApplyStandWait and ApplyShareLanes: the obstacle wait's hook is StandWait.cs's, the Sim field ShareLanes.cs's.
        static void ApplyClipWait()
        {
            sClwMissing = "";
            sClwDobType = typeof(RouteInterruptAction).Assembly.GetType("Sims3.Gameplay.Routing.DestinationObstructedRouteInterruptAction");
            Type ctl = typeof(RouteInterruptAction).Assembly.GetType("Sims3.Gameplay.Routing.StandAndWaitController");
            if (sClwDobType != null)
            {
                sClwDobExiting = sClwDobType.GetField("mbIsExiting", kInstAll);
                sClwDobReturn = sClwDobType.GetField("mReturnValue", kInstAll);
            }
            if (sClwDobType == null || ctl == null) sClwMissing = "DestinationObstructedRouteInterruptAction";
            else if (sClwDobExiting == null || sClwDobExiting.FieldType != typeof(bool) || sClwDobReturn == null || sClwDobReturn.FieldType != typeof(RouteAction.ActionResult))
                sClwMissing = "mbIsExiting mReturnValue";
            else if (!Patch(sClwDobType, "StandAndWaitCycleHandler", new Type[] { ctl, typeof(object) }, typeof(ClwDobHook), "CycleHook", "CycleOrig")) sClwMissing = "StandAndWaitCycleHandler";
            else if (!Patch(sClwDobType, "PerformAction", Type.EmptyTypes, typeof(ClwDobHook), "PerformHook", "PerformOrig")) sClwMissing = "PerformAction";
            else if (sSpsSim == null || !Patch(typeof(SimSteeringAdaptor), "HandleEventDestinationObstructed", null, typeof(ClwSteerHook), "DestHook", "DestOrig"))
                sClwMissing = "SimSteeringAdaptor.HandleEventDestinationObstructed";
            if (sSwfMissing.Length > 0) sClwMissing = (sClwMissing + " obstacle wait (standwaitfix hooks)").Trim();
            if (sClwMissing.Length > 0) AppliedLog().Add("clipwait: not found: " + sClwMissing);
        }

        // ---- who is in the way ------------------------------------------------------------------------------------------

        static bool ClwIsWalkable(Sim b) { return b != null && !b.HasBeenDestroyed && b.SimDescription != null && b.SimDescription.ToddlerOrAbove; }

        // The walker's interaction (the one a posture transition is for, when it is in one).
        static InteractionInstance ClwIa(Sim sim)
        {
            InteractionInstance ia = sim.CurrentInteraction;
            if (ia == null && sim.InteractionQueue != null) ia = sim.InteractionQueue.GetHeadInteraction();
            return ia;
        }

        // b uses obj: its interaction targets it, or it sits / lies / stands in it.
        static bool ClwUses(Sim b, IGameObject obj)
        {
            if (obj == null) return false;
            InteractionInstance ia = b.CurrentInteraction;
            if (ia != null && object.ReferenceEquals(ia.Target, obj)) return true;
            if (b.InteractionQueue != null)
            {
                InteractionInstance tr = b.InteractionQueue.TransitionInteraction;
                if (tr != null && object.ReferenceEquals(tr.Target, obj)) return true;
            }
            return (b.Posture != null && object.ReferenceEquals(b.Posture.Container, obj)) || object.ReferenceEquals(b.Parent, obj);
        }

        class ClwSeen
        {
            public ClipWaitCore.Case Case;
            public string Names = "";
            public int NameCount;
            public readonly List<Sim> Sims = new List<Sim>(4);
            public List<GameObject> Jigs;  // PetRelax.cs: floor jigs of lying pets that count as their pets

            public bool Add(Sim sim, Sim b, bool scopeAll, bool atDest)
            {
                if (b == sim || Sims.Contains(b)) return false;
                Sims.Add(b);
                Case.Sims++;
                if (!atDest) Case.SimsOnWay++;
                if (!ShlScope(b, scopeAll)) Case.AllInScope = false;
                if (NameCount++ < 3) Names += (Names.Length > 0 ? ", " : "") + b.FullName + " (" + SwfIa(b) + ")";
                return true;
            }
        }

        // What a waiting Sim has in its way. obstruction = the object the game found on the route's end (destination wait).
        static ClwSeen ClwGather(Sim sim, Route path, float elapsed, ObjectGuid obstruction, bool on, bool hhOn)
        {
            bool scopeAll = hhOn || !on;  // off: count what both switches would do
            ClwSeen s = new ClwSeen();
            s.Case.ElapsedMin = elapsed;
            s.Case.GraceMin = HhGraceMin();
            s.Case.ObjectDestAllowed = Instantiator.kClipWaitObjectDest;
            s.Case.SimInScope = ShlScope(sim, scopeAll);
            s.Case.AllInScope = true;

            IGameObject destObj = path.DestObj as IGameObject;
            s.Case.DestKind = destObj == null ? ClipWaitCore.kDestFloor : destObj is Sim ? ClipWaitCore.kDestSim : ClipWaitCore.kDestObject;
            InteractionInstance ia = ClwIa(sim);
            if (ia != null)
            {
                s.Case.Social = ia.Target is Sim && !object.ReferenceEquals(ia.Target, sim);
                s.Case.PlayerGoHere = HhPlayerOrder(ia) && (ia is Terrain.GoHere || ia.GetType().Name.StartsWith("GoHere", StringComparison.Ordinal));
            }

            // the route's end: the game's own 0.6 m
            Vector3 dest;
            if (!path.PlanResult.Succeeded() || !path.GetDestPoint(out dest)) dest = path.GetApproximateOriginalDestination();
            if (dest != Vector3.Invalid && dest != Vector3.Origin)
                foreach (Sim b in Sims3.Gameplay.Queries.GetObjects<Sim>(dest, kSpsDestRadius))
                {
                    if (b == sim || !ClwIsWalkable(b)) continue;
                    s.Add(sim, b, scopeAll, true);
                    s.Case.SimAtDest = true;
                    if (s.Case.DestKind == ClipWaitCore.kDestObject && ClwUses(b, destObj)) s.Case.DestInUse = true;
                }

            if (obstruction.IsValid)
            {
                // destination wait: what the game found on the end point is at the route's end whatever its distance
                Sim b = ClwSimOf(s, sim, GameObject.GetObject(obstruction));
                if (b == null) s.Case.Others++;
                else
                {
                    s.Add(sim, b, scopeAll, true);
                    s.Case.SimAtDest = true;
                    if (s.Case.DestKind == ClipWaitCore.kDestObject && ClwUses(b, destObj)) s.Case.DestInUse = true;
                }
            }
            else if (path.PlanResult.Succeeded())
            {
                ObjectGuid[] list = path.FindObstaclesForSim(sim.Proxy);
                if (list != null)
                    for (int i = 0; i < list.Length; i++) ClwAddObstacle(s, sim, GameObject.GetObject(list[i]), scopeAll);
            }
            else
            {
                // a failed plan names one blocker; the Sims standing in the waiter are the game's own second guess
                RoutePlanResult res = path.PlanResult;
                if (res.mBlockerObjectId != 0) ClwAddObstacle(s, sim, GameObject.GetObject(new ObjectGuid(res.mBlockerObjectId)), scopeAll);
                foreach (Sim b in Sims3.Gameplay.Queries.GetObjects<Sim>(sim.Position, kSwfSelfRadius))
                    if (ClwIsWalkable(b)) s.Add(sim, b, scopeAll, false);
            }
            return s;
        }

        static void ClwAddObstacle(ClwSeen s, Sim sim, GameObject o, bool scopeAll)
        {
            if (o == null || o == sim) return;
            Sim b = ClwSimOf(s, sim, o);
            if (b != null) s.Add(sim, b, scopeAll, false);
            else s.Case.Others++;
        }

        // The Sim or pet an obstacle stands for: itself, or the pet lying in a floor jig that petrelax passes (PetRelax.cs,
        // docs/686 §11; the jig is then ignored by the route when the walk begins). Null = an object.
        static Sim ClwSimOf(ClwSeen s, Sim sim, GameObject o)
        {
            Sim b = o as Sim;
            if (b != null) return ClwIsWalkable(b) ? b : null;
            b = PrxJigPetInScope(o, sim);
            if (b == null) return null;
            if (s.Jigs == null) s.Jigs = new List<GameObject>(2);
            s.Jigs.Add(o);
            return b;
        }

        // ---- the rule ---------------------------------------------------------------------------------------------------

        // One cycle of a wait that the game wants to go on. true = keep waiting. counted / petCounted keep the counters of
        // clipwait and of petrelax (PetRelax.cs, docs/686 §11: small pets, at once only near a portal) to one per wait.
        static bool ClwCycle(int site, Sim sim, Route path, float elapsed, ObjectGuid obstruction, ref bool counted, ref bool petCounted, out bool destAccepted)
        {
            destAccepted = false;
            if (sim.SimRoutingComponent == null) return true;
            int[] c = PatchCounters();
            bool on = ShortcutOn("clipwait"), hhOn = ShortcutOn("clipwaithh");
            ClwSeen s = ClwGather(sim, path, elapsed, obstruction, on, hhOn);
            bool petHousehold, petGrace;
            bool pet = PrxClip(sim, s.Case, s.Sims, ref petCounted, out destAccepted, out petHousehold, out petGrace);
            if (!pet)
            {
                if (!ClipWaitCore.Due(elapsed, HhGraceMin())) return true;
                int kept;
                int d = ClipWaitCore.Decide(on, s.Case, out kept, out destAccepted);
                if (!counted && (d != ClipWaitCore.kNone || kept != ClipWaitCore.kKeptNot))
                {
                    counted = true;
                    c[kClwGrace]++;
                    if (kept != ClipWaitCore.kKeptNot) c[kClwKeptObject + kept - 1]++;
                    else if (d == ClipWaitCore.kWould) c[kClwWould]++;
                    else if (d == ClipWaitCore.kExempt) c[kClwExempt]++;
                }
                if (d != ClipWaitCore.kGo) { destAccepted = false; return true; }
            }
            PrxIgnoreJigs(path, s.Jigs);  // lying pets that count as walked through: their floor jigs leave this route's plans

            if (!path.PlanResult.Succeeded())
            {
                // people only = the plan without them succeeds; anything else is a real block and the wait goes on
                bool ok = false;
                path.SetOption(Route.RouteOption.BlockedByPeople, false);
                try { ok = path.Replan().Succeeded(); }
                finally { if (!ok) path.SetOption(Route.RouteOption.BlockedByPeople, true); }
                if (!ok) { c[kClwPlanFailed]++; destAccepted = false; return true; }
            }
            sim.SimRoutingComponent.StartIgnoringObstacles(Math.Max(0.5f, Instantiator.kClipWaitMinutes));  // the game's own timer
            if (destAccepted)
            {
                if (sClwGrant.Count > kClwMaxTable) sClwGrant.Clear();  // entries leave at the route's next event or wait; this bounds leftovers
                ClwGrant g = new ClwGrant();
                g.Route = path; g.Start = SimClock.CurrentTime();
                sClwGrant[sim.ObjectId.Value] = g;
            }
            if (pet)
            {
                c[destAccepted ? kPrxClipDest : kPrxClip]++;
                if (petHousehold) c[kPrxHousehold]++;
                if (petGrace) sPrxGrace++;
                PrxLog("petrelax: " + sim.FullName + " " + ShlWho(sim) + ", " + SwfIa(sim) + ": " + (destAccepted ? "destination accepted" : "walked through") + " at the " + kClwSites[site]
                    + " after " + elapsed.ToString("0.0") + " sim-min, through: " + s.Names + (s.NameCount > 3 ? " +" + (s.NameCount - 3) : "") + ", at " + V(sim.Position));
                return false;
            }
            c[destAccepted ? kClwDest : kClwWalked]++;
            if (IsPlayerSim(sim)) c[kClwHousehold]++;
            sClwLast = "clipwait: " + sim.FullName + " " + ShlWho(sim) + ", " + SwfIa(sim) + ": " + (destAccepted ? "destination accepted" : "walked through") + " at the " + kClwSites[site]
                + " after " + elapsed.ToString("0.0") + " sim-min, through: " + s.Names + (s.NameCount > 3 ? " +" + (s.NameCount - 3) : "") + ", at " + V(sim.Position);
            if (ShortcutOn("clipwaitlog") && sClwCap.Take(System.Environment.TickCount)) DiagLine(sClwLast);
            return false;
        }

        // StandWait.cs, after the game's obstacle stand-and-wait cycle (and standwaitfix) answered "keep waiting".
        static bool ClwObstacleCycle(object action, Sim sim, Route path)
        {
            try
            {
                if (sim == null || path == null || sSwfMissing.Length > 0) return true;
                SwfState st = SwfStateOf(sim, action);
                bool dest;
                if (ClwCycle(kClwSiteObstacle, sim, path, SimClock.ElapsedTime(TimeUnit.Minutes, st.Start), ObjectGuid.InvalidObjectGuid, ref st.ClwCounted, ref st.PrxCounted, out dest)) return true;
                sSwfReturn.SetValue(action, RouteAction.ActionResult.ContinueAndFollowPath);
                sSwfElected.SetValue(action, true);
                return false;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ClwError(kClwSiteObstacle, e); return true; }
        }

        static bool ClwDobCycle(object action, Sim sim, Route path)
        {
            try
            {
                if (sim == null || path == null) return true;
                ClwWait w;
                if (!sClwDob.TryGetValue(sim.ObjectId.Value, out w) || !object.ReferenceEquals(w.Action, action)) return true;
                Vector3 dest;
                if (!path.GetDestPoint(out dest)) return true;
                ObjectGuid obstruction = path.GetObstructionAtPoint(dest);
                if (!obstruction.IsValid || obstruction == sim.ObjectId) return true;  // the game ends the wait itself next cycle
                bool accepted;
                if (ClwCycle(kClwSiteDest, sim, path, SimClock.ElapsedTime(TimeUnit.Minutes, w.Start), obstruction, ref w.Counted, ref w.PetCounted, out accepted)) return true;
                sClwDobReturn.SetValue(action, RouteAction.ActionResult.ContinueAndFollowPath);
                sClwDobExiting.SetValue(action, true);
                return false;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ClwError(kClwSiteDest, e); return true; }
        }

        // A route that was let into a Sim's place: its next destination-obstructed event starts no new wait.
        static bool ClwGranted(Sim sim, Route route)
        {
            ClwGrant g;
            ulong id = sim.ObjectId.Value;
            if (!sClwGrant.TryGetValue(id, out g)) return false;
            if (object.ReferenceEquals(g.Route, route) && sim.SimRoutingComponent != null && sim.SimRoutingComponent.ShouldIgnoreAllObstacles
                && SimClock.ElapsedTime(TimeUnit.Minutes, g.Start) <= Math.Max(0.5f, Instantiator.kClipWaitMinutes)) return true;
            sClwGrant.Remove(id);  // another route, or the timer ran out: the game's handling again
            return false;
        }

        // ---- hooks ------------------------------------------------------------------------------------------------------

        // `this` is the game's DestinationObstructedRouteInterruptAction (internal); the public base gives its Sim and route.
        public class ClwDobHook : RouteInterruptAction
        {
            public bool CycleHook(object controller, object userData)
            {
                return CycleOrig(controller, userData) && ClwDobCycle(this, mRoutingSim, mInterruptedPath);
            }
            public bool CycleOrig(object controller, object userData) { return true; }  // receives StandAndWaitCycleHandler's original body

            public RouteAction.ActionResult PerformHook()
            {
                Sim sim = mRoutingSim;
                ClwDobBegin(this, sim);
                RouteAction.ActionResult r = RouteAction.ActionResult.Terminate;
                try { r = PerformOrig(); return r; }
                finally { ClwDobEnd(this, sim, r); }
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; }  // receives PerformAction's original body
        }

        static void ClwDobBegin(object action, Sim sim)
        {
            try
            {
                if (sim == null) return;
                if (sClwDob.Count > kClwMaxTable) sClwDob.Clear();  // entries leave in ClwDobEnd; this only bounds a table left behind by errors
                ClwWait w = new ClwWait();
                w.Action = action; w.Start = SimClock.CurrentTime();
                sClwDob[sim.ObjectId.Value] = w;
                PatchCounters()[kClwDobWaits]++;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ClwError(kClwSiteDobBegin, e); }
        }

        static void ClwDobEnd(object action, Sim sim, RouteAction.ActionResult result)
        {
            try
            {
                if (sim == null) return;
                ClwWait w;
                ulong id = sim.ObjectId.Value;
                if (!sClwDob.TryGetValue(id, out w) || !object.ReferenceEquals(w.Action, action)) return;
                sClwDob.Remove(id);
                int[] c = PatchCounters();
                c[kClwDobTenths] += ClipWaitCore.Tenths(SimClock.ElapsedTime(TimeUnit.Minutes, w.Start));
                if (result == RouteAction.ActionResult.Terminate) c[kClwDobFails]++;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ClwError(kClwSiteDobEnd, e); }
        }

        // `this` is the game's SimSteeringAdaptor.
        public class ClwSteerHook
        {
            public void DestHook(RouteContext context, RouteEvent re)
            {
                try
                {
                    if (sClwGrant.Count > 0)
                    {
                        Sim sim = sSpsSim.GetValue(this) as Sim;
                        if (sim != null && context != null && ClwGranted(sim, context.ActiveRoute)) { PatchCounters()[kClwEventSkips]++; return; }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { ClwError(kClwSiteEvent, e); }
                DestOrig(context, re);
            }
            public void DestOrig(RouteContext context, RouteEvent re) { }  // receives HandleEventDestinationObstructed's original body
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string ClwCounters()
        {
            int[] c = PatchCounters();
            return "clipwait: waits that reached the grace with a Sim or pet in the way " + c[kClwGrace] + ", walked through " + c[kClwWalked] + ", destination accepted " + c[kClwDest]
                + " (household walkers of both " + c[kClwHousehold] + "), would " + c[kClwWould] + ", exempt " + c[kClwExempt]
                + "; kept: object in the way " + c[kClwKeptObject] + ", Sim at the object in use " + c[kClwKeptInUse] + ", social " + c[kClwKeptSocial] + ", player Go Here " + c[kClwKeptGoHere]
                + ", plan fails without people too " + c[kClwPlanFailed]
                + "; destination waits " + c[kClwDobWaits] + ", sim-min stood in them " + (c[kClwDobTenths] / 10f).ToString("0.0") + ", ended as route failed " + c[kClwDobFails]
                + ", destination events let through " + c[kClwEventSkips]
                + "; errors " + c[kClwErrors] + ClwErrorSites() + ", open destination waits " + sClwDob.Count + ", open grants " + sClwGrant.Count
                + "; " + GlMembers(sClwMissing) + (sClwLast.Length > 0 ? "; last: " + sClwLast : "");
        }

        static ClipWaitCore.Case ClwCase(float elapsed, int sims, int others, bool atDest, int destKind, bool inUse)
        {
            ClipWaitCore.Case k = new ClipWaitCore.Case();
            k.ElapsedMin = elapsed; k.GraceMin = HhSharedCore.kGraceMin; k.Sims = sims; k.Others = others; k.SimAtDest = atDest; k.DestKind = destKind; k.DestInUse = inUse;
            k.SimsOnWay = atDest ? 0 : sims; k.ObjectDestAllowed = true; k.SimInScope = true; k.AllInScope = true;
            return k;
        }

        // ClipWaitCheck run: the rule with controls (read-only, nothing in the world is touched). Bug case: a Sim has waited
        // past the grace for a Sim busy in its way: the game keeps waiting, clipwait walks through. result: the counters and
        // the last walk's line.
        public static object ClipWaitCheck(string what)
        {
            if (what == "result") return ClwCounters();
            if (what != "run") return "clipwait <run|result>";
            bool on = ShortcutOn("clipwait");
            int kept; bool dest;
            bool fixedNow = ClipWaitCore.Decide(on, ClwCase(3f, 1, 0, false, ClipWaitCore.kDestObject, false), out kept, out dest) == ClipWaitCore.kGo && !dest;
            bool ctlGrace = ClipWaitCore.Decide(true, ClwCase(1f, 1, 0, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == ClipWaitCore.kNone && kept == ClipWaitCore.kKeptNot;
            bool ctlObject = ClipWaitCore.Decide(true, ClwCase(3f, 1, 1, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == ClipWaitCore.kNone && kept == ClipWaitCore.kKeptObject;
            bool ctlInUse = ClipWaitCore.Decide(true, ClwCase(3f, 1, 0, true, ClipWaitCore.kDestObject, true), out kept, out dest) == ClipWaitCore.kNone && kept == ClipWaitCore.kKeptInUse;
            bool ctlFloor = ClipWaitCore.Decide(true, ClwCase(3f, 1, 0, true, ClipWaitCore.kDestFloor, false), out kept, out dest) == ClipWaitCore.kGo && dest;
            ClipWaitCore.Case k = ClwCase(3f, 1, 0, true, ClipWaitCore.kDestFloor, false);
            k.Social = true;
            bool ctlSocial = ClipWaitCore.Decide(true, k, out kept, out dest) == ClipWaitCore.kNone && kept == ClipWaitCore.kKeptSocial;
            k.Social = false; k.PlayerGoHere = true;
            bool ctlGoHere = ClipWaitCore.Decide(true, k, out kept, out dest) == ClipWaitCore.kNone && kept == ClipWaitCore.kKeptGoHere;
            k.SimAtDest = false;
            bool ctlGoHereWay = ClipWaitCore.Decide(true, k, out kept, out dest) == ClipWaitCore.kGo;
            k.PlayerGoHere = false; k.AllInScope = false;
            bool ctlScope = ClipWaitCore.Decide(true, k, out kept, out dest) == ClipWaitCore.kExempt;
            bool controls = ctlGrace && ctlObject && ctlInUse && ctlFloor && ctlSocial && ctlGoHere && ctlGoHereWay && ctlScope;
            return "clipwait check (switch " + (on ? "on" : "off") + ", household " + (ShortcutOn("clipwaithh") ? "on" : "off") + ", simpass " + (ShortcutOn("simpass") ? "on" : "off")
                + ", grace " + HhGraceMin().ToString("0.0") + " sim-min, timer " + Instantiator.kClipWaitMinutes.ToString("0.0") + " sim-min): a Sim that waited past the grace for a busy Sim walks through="
                + fixedNow + "; control: before the grace kept=" + ctlGrace + ", object in the way kept=" + ctlObject + ", Sim using the object at the route's end kept=" + ctlInUse
                + ", floor spot taken by a Sim accepted=" + ctlFloor + ", social kept=" + ctlSocial + ", player Go Here: end kept=" + ctlGoHere + ", way walked=" + ctlGoHereWay
                + ", Sim out of scope exempt=" + ctlScope + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sClwMissing.Length == 0) + "; " + ClwCounters();
        }
    }
}
