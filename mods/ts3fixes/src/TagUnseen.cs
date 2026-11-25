using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // tagunseen (tuning kTagUnseen, off by default, townies only; docs/60 §3). EA's Tag (PlayTagSituation.PlayTagInteraction)
        // plans a new route every lap: the prey a 6-10 m radial-range run on the lot (RunSomewhereLoop), the hunter a run to the
        // prey's last destination (RunAfterPreyLoop). In a Bridgeport apartment-save game, 4 unseen townie kids at two parks made
        // 699 Tag plans, 93 s of worker time (~130 ms each, any length), and worker jobs hold the lock main-thread replans wait on.
        // When every player is an unseen townie, a lap keeps EA's own start (AddNecessaryExitReasons: route-fail cap, fun and
        // stress exits, called by reflection) and the prey's round report, and spends the lap's time standing instead of
        // routing. Nobody can see the run, the game still ends on fun/stress as before. One seen player, a household Sim, a
        // selected Sim or a Sim with no household = EA's laps for everyone (a seen hunter would chase a prey that never moves).
        // Counters: [2704] laps checked, [2705] laps without a route, [2706] errors / members not found, [2707] a player seen,
        // [2783] players sent out at the no-route cap.
        // No-route cap (af4-bp 2026-11-17: two unseen kids stood in one Tag game for a sim-day until the watchdog reset them):
        // standing laps never fail a route, never tag, and with unseen motives flat the fun/stress exits never fire either. After
        // kTagNoRouteMaxMinutes of this player's loop the player leaves as EA's fun exit does (Finished + MoodFailure).
        const float kTagLapMinutes = 3f;  // a 6-10 m run at run speed is about 3 sim minutes
        const float kTagNoRouteMaxMinutes = 120f;
        static PropertyInfo sTagSit;
        static MethodInfo sTagExit;
        static bool sTagBound;

        static void ApplyTagUnseen()
        {
            Type t = typeof(PlayTagSituation).GetNestedType("PlayTagInteraction", BindingFlags.NonPublic);
            Patch(t, "RunSomewhereLoop", typeof(TagLoopHook), "SomewhereHook", "SomewhereOrig");
            Patch(t, "RunAfterPreyLoop", typeof(TagLoopHook), "PreyHook", "PreyOrig");
        }

        // plain class: `this` is the PlayTagInteraction at run time
        public class TagLoopHook
        {
            public void SomewhereHook(StateMachineClient smc, InteractionInstance.LoopData ld) { if (!TagLap(this, false, ld.mLifeTime)) SomewhereOrig(smc, ld); }
            public void SomewhereOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { } // receives RunSomewhereLoop's original body
            public void PreyHook(StateMachineClient smc, InteractionInstance.LoopData ld) { if (!TagLap(this, true, ld.mLifeTime)) PreyOrig(smc, ld); }
            public void PreyOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { } // receives RunAfterPreyLoop's original body
        }

        // true = the lap ran here (no route); false = run EA's lap
        static bool TagLap(object self, bool hunter, float loopMinutes)
        {
            if (!ShortcutOn("tagunseen")) return false;
            int[] c = PatchCounters();
            PlayTagSituation sit;
            Sim actor;
            try
            {
                InteractionInstance ii = self as InteractionInstance;
                actor = ii != null ? ii.InstanceActor as Sim : null;
                if (!sTagBound)
                {
                    sTagBound = true;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    sTagSit = self.GetType().GetProperty("Situation", inst);
                    sTagExit = self.GetType().GetMethod("AddNecessaryExitReasons", inst);
                }
                if (actor == null || sTagSit == null || sTagExit == null) { c[2706]++; return false; }
                sit = sTagSit.GetValue(self, null) as PlayTagSituation;
                if (sit == null || sit.HasExited) return false;
                c[2704]++;
                foreach (Sim p in sit.GetPlayers())
                    if (p == null || p.Household == null || !TownieUnseen(p, null)) { c[2707]++; return false; }
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[2706]++; return false; }
            // from here on the lap is ours: EA's lap must not run after a partial one
            if (!(bool)sTagExit.Invoke(self, null)) return true;  // EA's exits set (route-fail cap, fun, stress)
            c[2705]++;
            if (loopMinutes >= kTagNoRouteMaxMinutes)
            {
                c[2783]++;
                actor.AddExitReason(ExitReason.Finished);
                actor.AddExitReason(ExitReason.MoodFailure);
                return true;
            }
            if (!hunter) sit.PreyDestination = actor.Position;  // the hunter's next lap aims where the prey stands
            Simulator.Sleep((uint)SimClock.ConvertToTicks(kTagLapMinutes, TimeUnit.Minutes));
            if (!hunter && (sit.HasExited || !sit.IsPlaying(sit.Hunter))) sit.ReportRoundCompleted();  // as EA's prey lap ends
            return true;
        }
    }
}
