using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // loopidlebridge (local 2026-11-18, default off).
    // [verified, decompiled] Sim.LoopIdle hands the Sim's BridgeOrigin (the exit state of the last state machine the Sim
    // played in) to BridgeOrigin.MakeRequest before PostureIdle. When that state machine's host object was reset, its C++
    // driver is gone: StateMachineClient.RequestState gets kProxyInvalid, stores a pending SacsErrorException and throws it
    // from CheckForException ("This state machine does not have a valid C++ driver ..."). EA's own RequestState means to
    // forgive kProxyInvalid (it maps it to kSuccess when the host is gone), but the pending exception throws first. The
    // Sim's task ends in ScriptProxy's error path and the Sim is reset (af8 2026-11-18 seed 31341, Sinjon Frank, actor 'x',
    // state 'standing'; an earlier natural run hit it after Car.GetOut).
    // Fix: when that exact error comes from the bridge LoopIdle just consumed (same actor and exit state), the bridge is
    // dropped (LoopIdle already cleared it) and the Sim idles on its next LoopIdle instead of being reset. An EA bug fix:
    // applies to every Sim. Any other error, and the same error from PostureIdle, is rethrown as before.
    // Counters [2797] stale bridges dropped, [2798] stale bridge with the switch off (EA throws), [2799] errors in the
    // guard (the original exception is rethrown).
    public static partial class Fixes
    {
        static readonly FieldInfo sLiIdling = typeof(Sim).GetField("mIsAlreadyIdling", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sLiActor = typeof(BridgeOrigin).GetField("mActorName", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sLiStates = typeof(BridgeOrigin).GetField("mExitStateNames", BindingFlags.Instance | BindingFlags.NonPublic);
        const string kLiProxyInvalid = "This state machine does not have a valid C++ driver";

        public class LoopIdleBridgeHook
        {
            public void IdleHook()
            {
                Sim sim = (Sim)(object)this;
                BridgeOrigin bo = sim.BridgeOrigin;
                if (bo == null) { IdleOrig(); return; }
                try { IdleOrig(); }
                catch (SacsErrorException e)
                {
                    if (!LiStaleBridge(sim, bo, e)) throw;
                    if (!ShortcutOn("loopidlebridge")) { PatchCounters()[2798]++; throw; }
                    try { sLiIdling.SetValue(sim, false); PatchCounters()[2797]++; }
                    catch (Exception) { PatchCounters()[2799]++; throw e; }
                }
            }
            public void IdleOrig() { } // receives Sim.LoopIdle's original body
        }

        // the error is kProxyInvalid for the bridge's own actor and exit state, and LoopIdle consumed that bridge
        static bool LiStaleBridge(Sim sim, BridgeOrigin bo, SacsErrorException e)
        {
            try
            {
                if (sLiIdling == null || sLiActor == null || sLiStates == null || sim.BridgeOrigin == bo) return false;
                string m = e.Message;
                string[] states = sLiStates.GetValue(bo) as string[];
                if (m == null || !m.StartsWith(kLiProxyInvalid) || states == null || states.Length == 0) return false;
                return m.EndsWith("(actor = '" + (string)sLiActor.GetValue(bo) + "', state = '" + states[0] + "')");
            }
            catch (Exception) { PatchCounters()[2799]++; return false; }
        }

        // Harness check (repro loopidlebridge-check; reflect_call_async Ts3Fixes.Fixes.LoopIdleBridgeCheck "expect broken"|
        // "expect fixed", then reflect_call Ts3Fixes.Fixes.LoopIdleBridgeResult): the active Sim stops what it does and gets a
        // BridgeOrigin on a state machine whose host car was destroyed (the Car.GetOut case), then idles. Off: the
        // stale bridge is seen and EA throws. On: it is dropped, no error, and the Sim idles again (PostureIdle ran).
        public static string sLiResult = "not run";

        public static object LoopIdleBridgeResult()
        {
            if (!sLiResult.StartsWith("as expected")) throw new InvalidOperationException("loopidlebridge check: " + sLiResult);
            return sLiResult;
        }

        public static void LoopIdleBridgeCheck(string what)
        {
            sLiResult = "running";
            // reflect_call runs this through runtime_invoke, whose frame cannot yield (docs/119 §11.2): wait on our own task
            Simulator.AddObject(new LoopTask(delegate { LoopIdleBridgeRun(what); }));
        }

        static void LoopIdleBridgeRun(string what)
        {
            try
            {
                Sim sim = Sim.ActiveActor;
                if (sim == null || sLiIdling == null) { sLiResult = "no active Sim or mIsAlreadyIdling"; return; }
                sim.InteractionQueue.CancelAllInteractions();
                for (int i = 0; i < 600 && (sim.CurrentInteraction != null || sim.InteractionQueue.Count > 0); i++) Simulator.Sleep(1);
                if (sim.CurrentInteraction != null) { sLiResult = "Sim still busy: " + sim.CurrentInteraction.GetType().Name; return; }
                int dropped = PatchCounters()[2797], seen = PatchCounters()[2798], err = PatchCounters()[2799];
                // host: a service car made for the check (EA makes NPC cars the same way: out of world, then added), destroyed
                // right after the Sim joins its state machine: the Car.GetOut case (a car gone after the Sim got out). Dispose
                // would not reproduce it (it clears HandleEventsAsynchronously: "Another thread interrupted"); an out-of-world
                // object has no SacsComponent, and no idle world object near the active Sim had one (etl6 / etl7 2026-11-18).
                Sims3.Gameplay.Abstracts.GameObject host = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld("CarServiceSedan", ProductVersion.BaseGame, null, null) as Sims3.Gameplay.Abstracts.GameObject;
                if (host == null) { sLiResult = "CarServiceSedan not created"; return; }
                string hostName = host.GetType().Name;
                StateMachineClient smc;
                try { host.AddToWorld(); smc = StateMachineClient.Acquire(host.ObjectId, "elevator"); }
                catch (Exception e) { host.Destroy(); sLiResult = "host " + hostName + ": " + e.Message; return; }
                if (smc == null) { host.Destroy(); sLiResult = "elevator state machine not acquired on " + hostName; return; }
                smc.SetActor("x", sim);
                host.Destroy();
                BridgeOrigin bo = new BridgeOrigin(smc, "x", "Exit");
                sim.BridgeOrigin = bo;
                sLiIdling.SetValue(sim, false);  // the Sim idles on its next DoInteraction pass
                bool consumed = false, idledAgain = false;
                for (int i = 0; i < 600; i++)
                {
                    Simulator.Sleep(1);
                    if (sim.BridgeOrigin != bo) consumed = true;
                    if (consumed && PatchCounters()[2797] + PatchCounters()[2798] != dropped + seen) { if ((bool)sLiIdling.GetValue(sim)) { idledAgain = true; break; } if (what == "expect broken") break; }
                }
                if (sim.BridgeOrigin == bo) sim.BridgeOrigin = null;
                bool on = ShortcutOn("loopidlebridge");
                int dDropped = PatchCounters()[2797] - dropped, dSeen = PatchCounters()[2798] - seen, dErr = PatchCounters()[2799] - err;
                bool ok = consumed && (what == "expect broken" ? !on && dSeen == 1 && dDropped == 0 : on && dDropped == 1 && dSeen == 0 && dErr == 0 && idledAgain);
                sLiResult = (ok ? "as expected (" : "WRONG (") + what + "); switch " + on + ", bridge consumed " + consumed + ", dropped " + dDropped
                    + ", EA throws (off) " + dSeen + ", errors " + dErr + ", idled again " + idledAgain + ", Sim " + sim.FullName + ", host " + hostName;
            }
            catch (ResetException) { sLiResult = "check task reset"; throw; }
            catch (Exception e) { sLiResult = "error " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
