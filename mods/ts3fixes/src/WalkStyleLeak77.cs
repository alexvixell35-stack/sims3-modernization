using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // Walk-style requests EA leaves set on the Sim (docs/608, s77). All default off; EA bug fixes, for every Sim.
    //
    // SimRoutingComponent keeps one request count per walk style; the highest-priority style with a count above zero is
    // the Sim's walk style on every route (UpdateWalkStyle). InteractionInstance.RequestWalkStyle counts through a
    // helper that the interaction's Cleanup releases; Sim.RequestWalkStyle / SimRoutingComponent.RequestWalkStyle do not,
    // so the caller must call UnrequestWalkStyle. The table is saved with the Sim, so a leaked Run outlives save/load.
    //
    // megaphonerun / dragoncurserun: EA requests Run on the Sim, then pushes a RouteAway continuation ("run away from the
    // bed" / "run from the curse") and never takes the request back. The Sim (often a household Sim) runs on every route
    // for good. Fix as EA intended: after EA's method, the Sim-level request it added moves onto the queued RouteAway
    // (its interaction-scoped helper), so the Sim runs away and walks again once the route-away ends or is cancelled.
    // No RouteAway queued (the push failed, a reset): the request is just taken back.
    // vhomewalk: Sim.GoToVirtualHome.GoToVirtualHomeInternal.Run requests SimWalkStyle (Run for every busted-party guest
    // whose home is an apartment or a virtual home) like GoHome.Run (gohomewalk, docs/560 §2) and never releases it. Most
    // paths fade the Sim out and destroy it, but the active-household return, the autonomous reaction at the apartment
    // lot, a cancel or a failed route keep the Sim in the world, running. Fix as gohomewalk: after Run returns, the one
    // request Run added is taken back (in the existing VirtualHomeHook swap, RouteFixes.cs).
    public static partial class Fixes
    {
        static FieldInfo sWsReq;
        static string sWsMegaMissing = "", sWsCurseMissing = "";

        // Request count of one walk style on the Sim; -1 = cannot read.
        static int WsCount(Sim sim, Sim.WalkStyle style)
        {
            try
            {
                if (sim == null || sim.SimRoutingComponent == null) return -1;
                if (sWsReq == null) sWsReq = typeof(SimRoutingComponent).GetField("mWalkStyleRequests", kInstAll);
                IDictionary<Sim.WalkStyle, int> t = sWsReq != null ? sWsReq.GetValue(sim.SimRoutingComponent) as IDictionary<Sim.WalkStyle, int> : null;
                int n;
                return t != null && t.TryGetValue(style, out n) ? n : -1;
            }
            catch (Exception) { return -1; }
        }

        // RouteAway continuations already in the Sim's queue (null when none: no allocation on the common path).
        static List<InteractionInstance> WsRouteAways(Sim sim)
        {
            List<InteractionInstance> l = null;
            if (sim == null || sim.InteractionQueue == null) return null;
            foreach (InteractionInstance x in sim.InteractionQueue.InteractionList)
                if (x is Terrain.RouteAway) (l ?? (l = new List<InteractionInstance>())).Add(x);
            return l;
        }

        // The first RouteAway in the queue that was not there before EA's method ran.
        static InteractionInstance WsNewRouteAway(Sim sim, List<InteractionInstance> old)
        {
            if (sim == null || sim.InteractionQueue == null) return null;
            foreach (InteractionInstance x in sim.InteractionQueue.InteractionList)
                if (x is Terrain.RouteAway && (old == null || !old.Contains(x))) return x;
            return null;
        }

        // Pure: (Sim-level requests left after the interaction, requests held by the route-away) for EA's path or ours.
        internal static int[] WsMovePure(bool eaRequested, bool routeAwayQueued, bool fixedOn)
        {
            if (!eaRequested) return new[] { 0, 0 };
            if (!fixedOn) return new[] { 1, 0 };
            return routeAwayQueued ? new[] { 0, 1 } : new[] { 0, 0 };
        }

        // After EA's method: the Run request it added to `sim` goes onto the new RouteAway (or is taken back).
        // Slots: [first] moved, [first+1] released (no route-away), [first+2] left with the switch off, [first+3] errors.
        static void WsMove(Sim sim, Sim.WalkStyle style, int before, List<InteractionInstance> old, bool on, int first)
        {
            int[] c = PatchCounters();
            try
            {
                if (before < 0 || sim == null || sim.HasBeenDestroyed) return;
                if (WsCount(sim, style) <= before) return;  // EA's method left early without its request
                if (!on) { c[first + 2]++; return; }
                InteractionInstance ra = WsNewRouteAway(sim, old);
                if (ra != null) ra.RequestWalkStyle(style);  // scoped: its Cleanup (run, cancel or queue removal) releases it
                sim.UnrequestWalkStyle(style);
                c[ra != null ? first : first + 1]++;
            }
            catch (Exception) { c[first + 3]++; }
        }

        // ---- megaphonerun (kMegaphoneRun), slots 3394-3397 -------------------------------------------------------------
        static void ApplyMegaphoneRun()
        {
            if (typeof(Sim).GetNestedType("WakeWithMegaphone", BindingFlags.NonPublic) == null) { sWsMegaMissing = "Sim+WakeWithMegaphone not found"; AppliedLog().Add("Sim+WakeWithMegaphone: not found, megaphonerun off"); return; }
            Patch(typeof(Sim).GetNestedType("WakeWithMegaphone", BindingFlags.NonPublic), "Run", Type.EmptyTypes, typeof(WsMegaHook), "RunHook", "RunOrig");
            sWsMegaMissing = EpLive(typeof(WsMegaHook), 1);
        }

        public class WsMegaHook
        {
            public bool RunHook()
            {
                Sim a = null; int before = -1; List<InteractionInstance> old = null;
                try { a = ((InteractionInstance)(object)this).InstanceActor; before = WsCount(a, Sim.WalkStyle.Run); old = WsRouteAways(a); }
                catch (Exception) { PatchCounters()[3397]++; }
                try { return RunOrig(); }
                finally { WsMove(a, Sim.WalkStyle.Run, before, old, ShortcutOn("megaphonerun"), 3394); }
            }
            public bool RunOrig() { return false; } // receives Sim.WakeWithMegaphone.Run
        }

        // ---- dragoncurserun (kDragonCurseRun), slots 3398-3401 ---------------------------------------------------------
        static void ApplyDragonCurseRun()
        {
            if (typeof(BabyDragon).GetNestedType("PhantasmalCurse", BindingFlags.NonPublic) == null) { sWsCurseMissing = "BabyDragon+PhantasmalCurse not found"; AppliedLog().Add("BabyDragon+PhantasmalCurse: not found, dragoncurserun off"); return; }
            Patch(typeof(BabyDragon).GetNestedType("PhantasmalCurse", BindingFlags.NonPublic), "DoAnimation", Type.EmptyTypes, typeof(WsCurseHook), "AnimHook", "AnimOrig");
            sWsCurseMissing = EpLive(typeof(WsCurseHook), 1);
        }

        public class WsCurseHook
        {
            public void AnimHook()
            {
                Sim t = null; int before = -1; List<InteractionInstance> old = null;
                try { t = ((InteractionInstance)(object)this).GetSelectedObject() as Sim; before = WsCount(t, Sim.WalkStyle.Run); old = WsRouteAways(t); }
                catch (Exception) { PatchCounters()[3401]++; }
                try { AnimOrig(); }
                finally { WsMove(t, Sim.WalkStyle.Run, before, old, ShortcutOn("dragoncurserun"), 3398); }
            }
            public void AnimOrig() { } // receives BabyDragon.PhantasmalCurse.DoAnimation (TargetSim = GetSelectedObject())
        }

        // ---- vhomewalk (kVirtualHomeWalk), slots 3402-3404; hook: VirtualHomeHook (RouteFixes.cs) -----------------------
        // Pure: Sim-level requests GoToVirtualHomeInternal leaves when it returns (the Sim stays in the world).
        internal static int VhLeft(bool eaRequested, bool fixedOn) { return eaRequested && !fixedOn ? 1 : 0; }

        static void VhRelease(Sim sim, Sim.WalkStyle style, int before, bool on)
        {
            int[] c = PatchCounters();
            try
            {
                if (before < 0 || sim == null || sim.HasBeenDestroyed || WsCount(sim, style) <= before) return;
                if (!on) { c[3403]++; return; }
                sim.UnrequestWalkStyle(style);
                c[3402]++;
            }
            catch (Exception) { c[3404]++; }
        }

        // ---- checks ------------------------------------------------------------------------------------------------------
        // Live part shared by the two RouteAway checks: on the selected Sim, EA's Sim-level Run request, then (switch on)
        // our move onto a RouteAway made the way EA makes it (not queued; the check releases it itself). Returns the Run
        // count left on the Sim-level table after the route-away ends, minus the count before; -99 = N/A.
        static int WsLiveLeft(string sw, out string note)
        {
            note = "";
            Sim sim = Sim.ActiveActor;
            if (sim == null || sim.SimRoutingComponent == null) { note = "no selected Sim"; return -99; }
            int before = WsCount(sim, Sim.WalkStyle.Run);
            if (before < 0) { note = "walk-style table not readable"; return -99; }
            InteractionInstance ra = sim.SimRoutingComponent.CreateRouteAway(1f, 2f, false, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true, true, RouteDistancePreference.NoPreference);
            sim.RequestWalkStyle(Sim.WalkStyle.Run);                         // what EA's method does
            bool on = ShortcutOn(sw);
            if (on && ra != null) { ra.RequestWalkStyle(Sim.WalkStyle.Run); sim.UnrequestWalkStyle(Sim.WalkStyle.Run); }  // what WsMove does
            int during = WsCount(sim, Sim.WalkStyle.Run);
            if (ra != null) ra.UnrequestWalkStyle(Sim.WalkStyle.Run);         // the route-away's cleanup (no-op when it holds nothing)
            int left = WsCount(sim, Sim.WalkStyle.Run) - before;
            if (left > 0) sim.UnrequestWalkStyle(Sim.WalkStyle.Run);         // undo the check's own EA-style request
            note = "Run requests before " + before + ", during route-away " + during + ", after " + (before + left);
            return left;
        }

        static object WsRouteAwayCheck(string what, string sw, int first, string missing, string fixedText)
        {
            string counters = B582Counters(sw, first, new[] { "moved to the route-away", "released (no route-away)", "left with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return sw + " <run|result>";
            bool on = ShortcutOn(sw);
            bool fixedNow = false, controls = false;
            string ctl = "";
            try
            {
                int[] ea = WsMovePure(true, true, false), ours = WsMovePure(true, true, on), noRa = WsMovePure(true, false, on);
                string note;
                int left = WsLiveLeft(sw, out note);
                bool liveOk = left == -99 || left == (on ? 0 : 1);
                fixedNow = on && ours[0] == 0 && ours[1] == 1 && noRa[0] == 0 && left != -99 && left == 0;
                controls = ea[0] == 1 && WsMovePure(false, true, true)[0] == 0 && WsMovePure(false, true, false)[0] == 0 && liveOk;
                ctl = "EA leaves 1 Sim-level Run (" + (ea[0] == 1) + "), no EA request: nothing moved; live " + (left == -99 ? "N/A (" + note + ")" : note);
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            return B582Line(sw, on, fixedText, fixedNow, ctl, controls, missing, counters);
        }

        // MegaphoneRunCheck run: EA's Run request vs ours (pure + live on the selected Sim), swap live.
        public static object MegaphoneRunCheck(string what)
        {
            return WsRouteAwayCheck(what, "megaphonerun", 3394, sWsMegaMissing, "the megaphone waker runs only during its route-away");
        }

        // DragonCurseRunCheck run: the same for the baby dragon's Phantasmal Curse target.
        public static object DragonCurseRunCheck(string what)
        {
            return WsRouteAwayCheck(what, "dragoncurserun", 3398, sWsCurseMissing, "the cursed Sim runs only during its route-away");
        }

        // VirtualHomeWalkCheck run: GoToVirtualHomeInternal's request (pure + live on the selected Sim), swap live.
        public static object VirtualHomeWalkCheck(string what)
        {
            string counters = B582Counters("vhomewalk", 3402, new[] { "requests released", "left with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "vhomewalk <run|result>";
            bool on = ShortcutOn("vhomewalk");
            bool fixedNow = false, controls = false;
            string ctl = "", missing = "";
            try
            {
                Sim sim = Sim.ActiveActor;
                int before = WsCount(sim, Sim.WalkStyle.Run), left = -99;
                if (before >= 0)
                {
                    sim.RequestWalkStyle(Sim.WalkStyle.Run);   // what GoToVirtualHomeInternal.Run does
                    VhRelease(sim, Sim.WalkStyle.Run, before, on);  // what VirtualHomeHook does after Run
                    left = WsCount(sim, Sim.WalkStyle.Run) - before;
                    if (left > 0) sim.UnrequestWalkStyle(Sim.WalkStyle.Run);
                }
                fixedNow = on && VhLeft(true, on) == 0 && left == 0;
                controls = VhLeft(true, false) == 1 && VhLeft(false, true) == 0 && VhLeft(false, false) == 0 && (left == -99 || left == (on ? 0 : 1));
                ctl = "EA leaves 1 (" + (VhLeft(true, false) == 1) + "), no request: none; live left " + (left == -99 ? "N/A" : left.ToString());
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; }
            return B582Line("vhomewalk", on, "an apartment Sim sent home walks again after the trip", fixedNow, ctl, controls, missing, counters);
        }
    }
}
