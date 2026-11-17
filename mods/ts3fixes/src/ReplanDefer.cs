using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Route-follow replans out of callbacks (docs/141 §4 #2 and #4, §6).
    //
    // replandefer (#2): FollowRouteAction.ForceReplan(false) does not plan for a Sim: it inserts a ReplanRouteInterruptAction,
    // which stops the follow and replans in the Sim's own route loop (A10 async there, RouteBackoff.cs). The door, gate,
    // velvet-rope and elevator action PortalRouteThroughAction overrides it with a synchronous ReplanFromPoint inside the
    // caller (footprint/part callbacks, exit-reason callback, build/buy exit, elevator events), on the sim thread, while
    // the route is being followed (ts3native keeps it sync: Active). With the switch on, when the game's replan would end
    // in "insert the replanned path, kill this action" (route being followed, this action current, ReplanDeferCore), the
    // lane is freed as before and the same interrupt action as the base class is inserted: the Sim stops and reroutes a
    // tick or so later, from its own task. Load replans, routes not yet followed (the elevator approach inside DoPortal
    // follows the replanned route right after the call) and non-current actions keep the game's code.
    //
    // replanspread (#4): RoutingComponent.OnBuildBuyExit / OnRoutingDataChanged (ExitBuildBuyMode and
    // ForceReplanOfAllSimRoutes call them for every Sim in one loop) are held back and replayed by our own task, at most
    // kSpreadPerTick routing Sims per sim tick, so the replans (each Sim's interrupt action) start over several ticks
    // instead of all at once (an 8-deep async queue: the rest went sync as QueueFull). Same call, same component.
    //
    // Switches "replandefer", "replanspread" (default off, Instantiator). Counters: [132] interrupts inserted instead of a
    // sync replan, [133] eligible calls kept sync (route not followed, not current, no adaptor), [134] calls held back,
    // [135] calls replayed, [136] exceptions in a replayed call.
    public static partial class Fixes
    {
        static void ApplyReplanDefer()
        {
            Type t = typeof(FollowRouteAction).Assembly.GetType("Sims3.Gameplay.Routing.ReplanRouteInterruptAction");
            sReplanCtor = t != null ? t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(Sim) }, null) : null;
            if (sReplanCtor == null) AppliedLog().Add("ReplanRouteInterruptAction(Sim): not found, replandefer off");
            else Patch(typeof(PortalRouteThroughAction), "ForceReplan", typeof(PortalReplanHook), "ForceReplanHook", "ForceReplanOrig");
            Patch(typeof(RoutingComponent), "OnBuildBuyExit", typeof(ReplanSpreadHook), "BuildBuyExitHook", "BuildBuyExitOrig");
            Patch(typeof(RoutingComponent), "OnRoutingDataChanged", typeof(ReplanSpreadHook), "DataChangedHook", "DataChangedOrig");
        }

        static ConstructorInfo sReplanCtor;

        public class PortalReplanHook : PortalRouteThroughAction
        {
            public void ForceReplanHook(bool load)
            {
                Sim sim = RoutingObject as Sim;
                if (load || sim == null || !ShortcutOn("replandefer") || sim.RoutingComponent == null) { ForceReplanOrig(load); return; }
                if (!ReplanDeferCore.ShouldDefer(load, true, sim.RoutingComponent.GetCurrentRouteAction() == this,
                        mPathAdaptor != null, mRoute != null && mRoute.IsActive))
                {
                    PatchCounters()[133]++;
                    ForceReplanOrig(load);
                    return;
                }
                if (mRoutingSim != null && mPortalObject != null && mPortalObject.PortalComponent != null)
                    mPortalObject.PortalComponent.FreeRoutingLane(mRoutingSim); // as the game, before its replan
                // OnEnqueue kills this action (Kill, hard stop); PerformAction then replans this route in the Sim's task
                sim.RoutingComponent.InsertRouteActionNext((RouteAction)sReplanCtor.Invoke(new object[] { sim }));
                PatchCounters()[132]++;
            }
            public void ForceReplanOrig(bool load) { } // receives PortalRouteThroughAction.ForceReplan's original header
        }

        // RoutingComponent is abstract with many abstract members: the hooks take `this` as object (AutShadow.cs pattern).
        public class ReplanSpreadHook
        {
            public void BuildBuyExitHook() { if (!HoldBack(this)) BuildBuyExitOrig(); }
            public void BuildBuyExitOrig() { } // receives RoutingComponent.OnBuildBuyExit's original header
            public void DataChangedHook() { if (!HoldBack(this)) DataChangedOrig(); }
            public void DataChangedOrig() { } // receives RoutingComponent.OnRoutingDataChanged's original header
        }

        // Both game methods are the same one line (ForceRouteReplan(false)), so the replay calls OnRoutingDataChanged.
        static bool HoldBack(object self)
        {
            RoutingComponent rc = self as RoutingComponent;
            if (sSpreadReplaying || rc == null || !ShortcutOn("replanspread") || !GameStates.IsInWorld() || GameStates.IsWorldShuttingDown) return false;
            bool first = PfSpreadFirst(rc);  // docs/631 playerfirst: a player Sim's replan is replayed first
            if (sSpread.Add(rc, first)) { PatchCounters()[134]++; if (first) PatchCounters()[3662]++; }
            if (!(sSpreadTask.IsValid && Simulator.GetTask(sSpreadTask) != null))
                sSpreadTask = Simulator.AddObject(new LoopTask(SpreadReplay));
            return true;
        }

        static void SpreadReplay()
        {
            try
            {
                while (sSpread.Count > 0)
                {
                    if (GameStates.IsWorldShuttingDown || !GameStates.IsInWorld()) { sSpread.Clear(); return; }
                    PatchCounters()[135] += sSpread.DrainTick(kSpreadPerTick, SpreadAlive, SpreadRouting, SpreadCall);
                    if (sSpread.Count > 0) Simulator.Sleep(0);
                }
            }
            finally { sSpreadTask = ObjectGuid.InvalidObjectGuid; }
        }

        static bool SpreadAlive(RoutingComponent rc) { return rc.Owner != null && !rc.Owner.HasBeenDestroyed; }
        static bool SpreadRouting(RoutingComponent rc) { return rc.IsRouting; }
        static void SpreadCall(RoutingComponent rc)
        {
            sSpreadReplaying = true;
            try { rc.OnRoutingDataChanged(); }
            catch (Exception) { PatchCounters()[136]++; } // the game's loop would have stopped here; the others still replan
            finally { sSpreadReplaying = false; }
        }

        // shortcut: fixed rate; 4 routing Sims a tick spreads 40 Sims over ~10 ticks. Make it a tuning value if the A/B
        // shows QueueFull or visible late reroutes.
        const int kSpreadPerTick = 4;
        static readonly SpreadQueue<RoutingComponent> sSpread = new SpreadQueue<RoutingComponent>();
        static ObjectGuid sSpreadTask;
        static bool sSpreadReplaying;
    }
}
