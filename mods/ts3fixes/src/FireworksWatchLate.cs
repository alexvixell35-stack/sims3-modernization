using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fireworkswatchlate (tuning kFireworksWatchLate, off by default). EA's FireworksBase+Watch.Run routes the watcher to a
    // spot 5-10 m from the firework, enters "FireworksBigBooms" at "Enter", and runs the watch loop only while the firework
    // is not Finished; it then requests "Exit". A watcher who arrives after the show ended (Finished during the route: a
    // festival or broadcaster push lands near the end of a one-charge show) asks for Exit straight from Enter. The graph
    // has no Enter-to-Exit path, so the request throws SacsErrorException "During a request for Exit the state machine
    // became idle in a state other than the one requested: 0x8d31b469" (0x8d31b469 = FNV-1 of "enter") and the Sim is reset.
    // [verified: decompiled Run; hash matched; sl1 2026-11-17 Verde Park Summer Festival, 4 errors on 2 townies.]
    // Fix: EA's Run with the late case sent through "LoopWatch" (the state a normal show's watcher exits from), so the
    // watcher plays the exit and finishes as after a short show. Every other path is EA's.
    // Counters [2780] late watchers sent through LoopWatch, [2781] members not found (EA's Run used), [2782] EA's Run threw
    // SacsErrorException (switch off; rethrown).
    public static partial class Fixes
    {
        static readonly FieldInfo sFwReserved = typeof(FireworksBase).GetField("mReservedFireworks", kInstAll);
        static readonly MethodInfo sFwJazz = typeof(FireworksBase).GetMethod("SetWatchFireworksJazzParams", kInstAll);
        static readonly MethodInfo sFwOnWatched = typeof(FireworksBase).GetMethod("OnFinishedWatchingFireworks", kInstAll);
        static readonly MethodInfo sFwLoopDel = sFwWatch == null ? null : sFwWatch.GetMethod("LoopDel", kInstAll);

        public class FireworksWatchLateHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("fireworkswatchlate")) { try { return RunOrig(); } catch (SacsErrorException) { PatchCounters()[2782]++; throw; } }
                if (sFwReserved == null || sFwJazz == null || sFwOnWatched == null || sFwLoopDel == null) { PatchCounters()[2781]++; return RunOrig(); }
                return FwWatchRun((Interaction<Sim, FireworksBase>)(object)this);
            }
            public bool RunOrig() { return false; } // receives FireworksBase+Watch.Run's original body
        }

        static int FwReservedCount(FireworksBase t)
        {
            System.Collections.ICollection l = sFwReserved.GetValue(t) as System.Collections.ICollection;
            return l == null ? 0 : l.Count;
        }

        // EA's Watch.Run; the only change is the Finished branch (EA: Exit straight from Enter).
        static bool FwWatchRun(Interaction<Sim, FireworksBase> w)
        {
            Sim actor = w.Actor;
            FireworksBase t = w.Target;
            w.RequestWalkStyle(Sim.WalkStyle.Run);
            Route route = actor.CreateRoute();
            route.PlanToPointRadialRange(t.Position, t.TuningFireworks.kWatchMinDistanceFromFirework, t.TuningFireworks.kWatchMaxDistanceFromFirework,
                RouteDistancePreference.PreferNearestToRouteDestination, RouteOrientationPreference.TowardsObject, t.LotCurrent.LotId, new int[1] { t.RoomId });
            if (!actor.DoRoute(route)) return false;
            w.StandardEntry();
            w.BeginCommodityUpdates();
            w.EnterStateMachine("FireworksBigBooms", "Enter", "x", "firework");
            sFwJazz.Invoke(t, new object[] { w.mCurrentStateMachine });
            int before = FwReservedCount(t);
            bool flag = true;
            if (t.CurrentState != FireworksBase.FireworkState.Finished)
            {
                w.AnimateSim(t.CurrentState == FireworksBase.FireworkState.Lit ? "LoopAnticipate" : "LoopWatch");
                InteractionInstance.InsideLoopFunction loop = (InteractionInstance.InsideLoopFunction)Delegate.CreateDelegate(typeof(InteractionInstance.InsideLoopFunction), w, sFwLoopDel);
                flag = w.DoLoop(ExitReason.Default, loop, w.mCurrentStateMachine, 0.3f);
            }
            else
            {
                PatchCounters()[2780]++;
                w.AnimateSim("LoopWatch");  // the graph has no Enter-to-Exit path
            }
            w.AnimateSim("Exit");
            sFwOnWatched.Invoke(t, new object[] { actor, before - FwReservedCount(t) });
            w.EndCommodityUpdates(flag);
            w.StandardExit();
            return flag;
        }

        // Harness check (repro fireworks-watchlate-check; reflect_call_async Ts3Fixes.Fixes.FireworksWatchLateCheck "expect
        // broken"|"expect fixed", then reflect_call Ts3Fixes.Fixes.FireworksWatchLateResult): a firework placed 6 m in front of the active Sim and set Lit, Watch pushed
        // on that Sim, the firework set Finished once Watch runs (the Sim is routing), then the watch is waited out (30 sim
        // minutes at most) and the firework destroyed. Switch off: EA asks Exit from Enter (the SacsErrorException shows in
        // sim_gone_watch). Switch on: one late exit, no error.
        public static string sFwLateResult = "not run";

        // The repro's sync step after the async check: throws unless the last check ended as expected.
        public static object FireworksWatchLateResult()
        {
            if (!sFwLateResult.StartsWith("as expected")) throw new InvalidOperationException("fireworkswatchlate check: " + sFwLateResult);
            return sFwLateResult;
        }

        public static void FireworksWatchLateCheck(string what)
        {
            sFwLateResult = "running";
            // reflect_call runs this through runtime_invoke, whose frame cannot yield (docs/119 §11.2): wait on our own task
            Simulator.AddObject(new LoopTask(delegate { FireworksWatchLateRun(what); }));
        }

        static void FireworksWatchLateRun(string what)
        {
            FireworksBase fw = null;
            try
            {
                Sim sim = Sim.ActiveActor;
                if (sim == null || sFwWatch == null || sFwState == null) { sFwLateResult = "no active Sim or Watch members"; return; }
                string tried;
                fw = FwMakeOutOfWorld(out tried);
                if (fw == null) { sFwLateResult = "no FireworksBase (" + tried + ")"; return; }
                fw.SetPosition(sim.Position + sim.ForwardVector * 6f);
                fw.AddToWorld();
                sFwState.SetValue(fw, FireworksBase.FireworkState.Lit);
                int before = PatchCounters()[2780], eaBefore = PatchCounters()[2782];
                InteractionDefinition def = (InteractionDefinition)sFwWatch.GetField("Singleton", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
                InteractionInstance w = def.CreateInstance(fw, sim, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                sim.InteractionQueue.CancelAllInteractions();
                sim.InteractionQueue.Add(w);
                float end = SimClock.ElapsedTime(TimeUnit.Minutes) + 30f;
                for (int i = 0; sim.CurrentInteraction != w && i < 100000 && SimClock.ElapsedTime(TimeUnit.Minutes) < end; i++) Simulator.Sleep(1);
                if (sim.CurrentInteraction != w) { sFwLateResult = "Watch never started"; return; }
                sFwState.SetValue(fw, FireworksBase.FireworkState.Finished);
                for (int i = 0; sim.CurrentInteraction == w && i < 100000 && SimClock.ElapsedTime(TimeUnit.Minutes) < end; i++) Simulator.Sleep(1);
                bool on = ShortcutOn("fireworkswatchlate"), ended = sim.CurrentInteraction != w;
                int late = PatchCounters()[2780] - before, ea = PatchCounters()[2782] - eaBefore;
                bool ok = ended && (what == "expect broken" ? !on && ea == 1 && late == 0 : on && late == 1 && ea == 0);
                sFwLateResult = (ok ? "as expected (" : "WRONG (") + what + "); switch " + on + ", late exits " + late + ", EA Exit errors " + ea
                    + ", watch " + (ended ? "ended" : "still running");
            }
            catch (ResetException) { sFwLateResult = "check task reset"; throw; }
            catch (Exception e) { sFwLateResult = "error " + e.GetType().Name + ": " + e.Message; }
            finally { if (fw != null) fw.Destroy(); }
        }
    }
}
