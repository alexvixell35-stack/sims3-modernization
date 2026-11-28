using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // scarepriority (tuning kScarePriority, off by default; docs/580 §14). EA bug fix: an autonomous Scare / StaticShock
    // (and the megaphone scare) waits up to 2 x 40 sim-minutes for a target that never comes.
    //
    // [verified, decompiled] Sim.BaseScare.Run creates the receiver (BeScared / BeShocked) with the scarer's own
    // GetPriority() and pushes it with Target.InteractionQueue.AddNext, behind the target's running head.
    // RemoveLowerPriorityInteractions cancels that head only for a strictly higher priority, so for an autonomous scare
    // (Autonomous vs Autonomous) the receiver waits behind WorkOut, BedSleep, Watch or ReadBook while the scarer routes and
    // then sits in WaitForSynchronizationLevelWithSim(Target, Routed / Committed, kMaxSyncTime = 40). EA's rule for every
    // other receiver (SocialInteraction.CreateSocialB) raises an Autonomous priority to NonCriticalNPCBehavior; BaseScare
    // skips it. Measured: 115 failed scare waits, 3423 sim-min, in 153 run logs.
    //
    // Fix: after SetupJazzGraph (the scarer has routed, the waits come next), if the linked receiver is still queued
    // behind another head with Autonomous priority, SetPriority(NonCriticalNPCBehavior). EA's
    // InteractionPriorityChangedCallback (set by AddInteraction) then runs RemoveLowerPriorityInteractions, which cancels
    // only an autonomous, non-MustRun head, exactly as for any autonomous social. A player-directed scare's receiver is
    // already UserDirected (unchanged), and a target busy with something the player ordered (UserDirected) still
    // outranks the raised receiver. Counters: [3541] receivers raised (on), [3542] the same case seen with the switch off,
    // [3543] errors. Check: ScarePriorityChecks.cs.
    public static partial class Fixes
    {
        static string sSpMissing = "";

        static void ApplyScarePriority()
        {
            sSpMissing = "";
            if (SpFound("Scare"))
                Patch(typeof(Sim).GetNestedType("Scare", BindingFlags.NonPublic), "SetupJazzGraph", Type.EmptyTypes, typeof(SpScareHook), "JazzHook", "JazzOrig");
            if (SpFound("StaticShock"))
                Patch(typeof(Sim).GetNestedType("StaticShock", BindingFlags.NonPublic), "SetupJazzGraph", Type.EmptyTypes, typeof(SpShockHook), "JazzHook", "JazzOrig");
            if (SpFound("ScareWithMegaPhone"))
                Patch(typeof(Sim).GetNestedType("ScareWithMegaPhone", BindingFlags.NonPublic), "SetupJazzGraph", Type.EmptyTypes, typeof(SpMegaHook), "JazzHook", "JazzOrig");
            sSpMissing = sSpMissing.Trim();
            if (sSpMissing.Length > 0) AppliedLog().Add("scarepriority: not found: " + sSpMissing);
        }

        static bool SpFound(string name)
        {
            Type t = typeof(Sim).GetNestedType(name, BindingFlags.NonPublic);
            if (t != null && t.GetMethod("SetupJazzGraph", kInstAll, null, Type.EmptyTypes, null) != null) return true;
            sSpMissing += "Sim+" + name + ".SetupJazzGraph ";
            return false;
        }

        // Pure: raise this receiver? Only a receiver still waiting in the target's queue (not its head) with Autonomous
        // priority; anything else keeps EA's priority.
        internal static bool SpRaise(bool on, bool queued, bool isHead, InteractionPriorityLevel level)
        {
            return on && queued && !isHead && level == InteractionPriorityLevel.Autonomous;
        }

        // Pure model of EA's RemoveLowerPriorityInteractions for the head behind which the receiver waits.
        internal static bool SpCancelsHead(InteractionPriorityLevel receiver, InteractionPriorityLevel head, bool headMustRun)
        {
            return !headMustRun && receiver > head;
        }

        // Shared by the three hooks; runs after EA's SetupJazzGraph on the scarer's task.
        static void SpAfterJazz(InteractionInstance scare)
        {
            try
            {
                InteractionInstance recv = scare == null ? null : scare.LinkedInteractionInstance;
                Sim target = scare == null ? null : scare.Target as Sim;
                if (recv == null || target == null || target.InteractionQueue == null) return;
                bool queued = target.InteractionQueue.HasInteraction(recv);
                bool isHead = object.ReferenceEquals(target.InteractionQueue.GetHeadInteraction(), recv);
                InteractionPriorityLevel level = recv.GetPriority().Level;
                if (!SpRaise(true, queued, isHead, level)) return;
                if (ShortcutOn("scarepriority"))
                {
                    PatchCounters()[3541]++;
                    recv.SetPriority(InteractionPriorityLevel.NonCriticalNPCBehavior);
                }
                else PatchCounters()[3542]++;
            }
            catch (Exception) { PatchCounters()[3543]++; }
        }

        public class SpScareHook
        {
            public void JazzHook() { JazzOrig(); SpAfterJazz((InteractionInstance)(object)this); }
            public void JazzOrig() { } // receives Sim+Scare.SetupJazzGraph's original body
        }

        public class SpShockHook
        {
            public void JazzHook() { JazzOrig(); SpAfterJazz((InteractionInstance)(object)this); }
            public void JazzOrig() { } // receives Sim+StaticShock.SetupJazzGraph's original body
        }

        public class SpMegaHook
        {
            public void JazzHook() { JazzOrig(); SpAfterJazz((InteractionInstance)(object)this); }
            public void JazzOrig() { } // receives Sim+ScareWithMegaPhone.SetupJazzGraph's original body
        }
    }
}
