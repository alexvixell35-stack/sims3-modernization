using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // socialfinish (tuning kSocialFinish, off by default until its off/on run; docs/546 §9). EA bug fix.
    //
    // A normal social ends with Actor.AddExitReason(Finished); CopyExitReasonToLinkedInteraction(); WaitForSyncComplete()
    // (SocialInteractionA.Run) [verified, decompiled]. The partner runs SocialInteractionB.Run: DoLoop(ExitReason.Default)
    // until it gets an exit reason, then WaitForMasterInteractionToFinish and its own WaitForSyncComplete. Six masters skip
    // the first two calls (ShowVideos, InkBlotTest, Interview, TriviaChallenge, MindMeld, PresentObjectToSim; Interaction.
    // DoLoop does not copy exit reasons either), so the partner keeps looping, never reaches SyncLevel.Completed, and the
    // master's WaitForSyncComplete runs to its timeout (kNumMinToWaitOnSync, 5 sim minutes in the shipped tuning): both
    // Sims stand locked, then both get SynchronizationFailed. Measured (synctime, phase-20261114-0212): 15 of 15 ShowVideos
    // waits timed out at 5.1 min.
    // The hook does what the normal social does, at the point those six forget it: in WaitForSyncComplete, when this Sim
    // is the sync initiator and its linked SocialInteractionB is still running on the partner with no exit reason, the
    // partner gets ExitReason.Finished (as CopyExitReasonToLinkedInteraction would give it). Such a wait cannot succeed
    // otherwise; a social that already signalled is untouched. Applies to every Sim (EA bug fix).
    // Counters: [2310] partners signalled, [2311] errors.
    public static partial class Fixes
    {
        static void ApplySocialFinish()
        {
            Patch(typeof(InteractionInstance), "WaitForSyncComplete", new Type[] { typeof(float) }, typeof(SfHook), "WaitHook", "WaitOrig");
        }

        public class SfHook
        {
            public bool WaitHook(float waitTime)
            {
                bool stuck = false, signalled = false;
                InteractionInstance me = null;
                try
                {
                    me = (InteractionInstance)(object)this;
                    Sim actor = me.InstanceActor;
                    SocialInteractionB b = me.LinkedInteractionInstance as SocialInteractionB;
                    Sim partner = b != null ? b.InstanceActor : null;
                    stuck = actor != null && partner != null && actor.SynchronizationRole == Sim.SyncRole.Initiator
                        && partner.InteractionQueue != null && partner.InteractionQueue.IsRunning(b, true) && !partner.HasExitReason();
                    if (stuck && ShortcutOn("socialfinish"))
                    {
                        partner.AddExitReason(ExitReason.Finished);
                        PatchCounters()[2310]++;
                        signalled = true;
                        if (ShortcutOn("synctime")) HcLine("socialfinish: " + me.GetType().Name + " signalling " + partner.FirstName + " (in " + (partner.CurrentInteraction != null ? partner.CurrentInteraction.GetType().Name : "none") + ", sync level " + partner.SynchronizationLevel + ", own " + actor.SynchronizationLevel + "), " + HcSimTime());
                    }
                }
                catch (Exception) { PatchCounters()[2311]++; }
                if (!stuck || !ShortcutOn("synctime")) return WaitOrig(waitTime);
                // test switch synctime: time this wait (the off/on measure for this fix; SyncWaitLog's own hook is not needed)
                long t0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                bool r = WaitOrig(waitTime);
                try
                {
                    float min = Sims3.Gameplay.Utilities.SimClock.ConvertFromTicks(Sims3.Gameplay.Utilities.SimClock.CurrentTicks - t0, Sims3.Gameplay.Utilities.TimeUnit.Minutes);
                    HcLine("socialfinish: " + me.GetType().Name + " " + (signalled ? "signalled" : "would signal") + ", wait " + (r ? "ok" : "failed") + " after " + min.ToString("0.0") + "/" + waitTime.ToString("0") + " min, " + HcSimTime());
                }
                catch (Exception) { PatchCounters()[2311]++; }
                return r;
            }
            public bool WaitOrig(float waitTime) { return false; } // receives the original
        }
    }
}
