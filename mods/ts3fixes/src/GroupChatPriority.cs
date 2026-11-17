using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // groupchatpriority (tuning kGroupChatPriority, off by default; docs/638 §3). EA bug fix: a seated (or standing / mounted)
    // group chat starter waits 15 sim-minutes for a receiver whose GroupChatB never gets a turn.
    //
    // [verified, decompiled] SeatedSocialInteractionA (a SocialInteractionA), MountedSocialInteractionA and Sim.GroupChat push
    // the receiver's GroupChatB (GroupChatBWhileMounted) in PushGroupChatB with the starter's own GetPriority() and
    // Target.InteractionQueue.Add, then wait in WaitForSynchronizationLevelWithSim(Target, Committed, 15). For an autonomous
    // starter the receiver is Autonomous, so RemoveLowerPriorityInteractions never cancels the receiver's autonomous head
    // (TakeShower, Fridge_Have, Clean ...): the GroupChatB waits behind it and starts only after the starter gave up. EA's rule
    // for every other social receiver (SocialInteraction.CreateSocialB, which these PushGroupChatB bypass) raises an
    // Autonomous priority to NonCriticalNPCBehavior. Measured (phase-20261117-1703, household only): 5 seated starters timed
    // out at 15.2 min, every time the receiver's GroupChatB route began seconds before the timeout, once with the receiver
    // still in Fridge_Have.
    //
    // Fix: after EA's PushGroupChatB, if the linked GroupChatB is still queued behind another head with Autonomous priority,
    // SetPriority(NonCriticalNPCBehavior), the scarepriority rule (ScarePriority.cs SpRaise / SpCancelsHead). EA's
    // InteractionPriorityChangedCallback then runs RemoveLowerPriorityInteractions, which cancels only an autonomous,
    // non-MustRun head, as for any autonomous social. A player-directed chat keeps its UserDirected receiver, and a receiver
    // busy with something the player ordered keeps it. Counters: [3841] receivers raised (on), [3842] the same case seen with
    // the switch off, [3843] errors, [3844] spare. Check: GroupChatPriorityChecks.cs.
    public static partial class Fixes
    {
        static string sGcpMissing = "";

        static void ApplyGroupChatPriority()
        {
            sGcpMissing = "";
            if (GcpFound(typeof(SeatedSocialInteractionA)))
                Patch(typeof(SeatedSocialInteractionA), "PushGroupChatB", Type.EmptyTypes, typeof(GcpSeatedHook), "PushHook", "PushOrig");
            if (GcpFound(typeof(MountedSocialInteractionA)))
                Patch(typeof(MountedSocialInteractionA), "PushGroupChatB", Type.EmptyTypes, typeof(GcpMountedHook), "PushHook", "PushOrig");
            if (GcpFound(typeof(Sim.GroupChat)))
                Patch(typeof(Sim.GroupChat), "PushGroupChatB", Type.EmptyTypes, typeof(GcpChatHook), "PushHook", "PushOrig");
            sGcpMissing = sGcpMissing.Trim();
            if (sGcpMissing.Length > 0) AppliedLog().Add("groupchatpriority: not found: " + sGcpMissing);
        }

        static bool GcpFound(Type t)
        {
            if (t.GetMethod("PushGroupChatB", kInstAll | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null) return true;
            sGcpMissing += TypeName(t) + ".PushGroupChatB ";
            return false;
        }

        // Runs after EA's PushGroupChatB on the starter's task. EA links the receiver only when it pushed one.
        static void GcpAfterPush(InteractionInstance starter)
        {
            try
            {
                Sim.GroupChatB recv = starter == null ? null : starter.LinkedInteractionInstance as Sim.GroupChatB;
                Sim target = starter == null ? null : starter.Target as Sim;
                if (recv == null || target == null || target.InteractionQueue == null) return;
                bool queued = target.InteractionQueue.HasInteraction(recv);
                bool isHead = object.ReferenceEquals(target.InteractionQueue.GetHeadInteraction(), recv);
                if (!SpRaise(true, queued, isHead, recv.GetPriority().Level)) return;
                if (ShortcutOn("groupchatpriority"))
                {
                    PatchCounters()[3841]++;
                    recv.SetPriority(InteractionPriorityLevel.NonCriticalNPCBehavior);
                }
                else PatchCounters()[3842]++;
            }
            catch (Exception) { PatchCounters()[3843]++; }
        }

        public class GcpSeatedHook
        {
            public void PushHook() { PushOrig(); GcpAfterPush((InteractionInstance)(object)this); }
            public void PushOrig() { } // receives SeatedSocialInteractionA.PushGroupChatB's original body
        }

        public class GcpMountedHook
        {
            public void PushHook() { PushOrig(); GcpAfterPush((InteractionInstance)(object)this); }
            public void PushOrig() { } // receives MountedSocialInteractionA.PushGroupChatB's original body
        }

        public class GcpChatHook
        {
            public void PushHook() { PushOrig(); GcpAfterPush((InteractionInstance)(object)this); }
            public void PushOrig() { } // receives Sim+GroupChat.PushGroupChatB's original body
        }
    }
}
