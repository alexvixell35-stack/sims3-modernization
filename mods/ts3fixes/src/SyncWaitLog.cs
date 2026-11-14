using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // synctime (test switch, hooks only with kHitchCauseHooks; docs/546 §9): how long Sims stand locked waiting for a sync
    // partner. Sim.WaitForSynchronizationLevelWithSim [verified, decompiled] loops with Simulator.Sleep(0) until both Sims
    // reach the level, giving up after giveupTime sim minutes (StartSync: kNumMinToWaitOnSyncStart 30, after up to
    // kNumMinToWaitOnPreSync 15 in StartSync's own loop; WaitForSyncComplete: kNumMinToWaitOnSync 15); with
    // performSocializeWithTest it runs SocialInteractionA.Definition.CanSocializeWithSyncCheck every tick of the wait.
    // Logs each wait that took >= 1 sim minute or failed: actor (HH = household), partner, level, result, sim minutes,
    // social checks run during the wait and their total time, the actor's interaction.
    public static partial class Fixes
    {
        class SwStat { public int Checks; public uint CheckUs; }
        static Dictionary<Sim, SwStat> sSwIn = new Dictionary<Sim, SwStat>();
        static int sSwLines;

        static bool ApplySyncWaitLog()
        {
            bool ok = Patch(typeof(Sim), "WaitForSynchronizationLevelWithSim", new Type[] { typeof(Sim), typeof(Sim.SyncLevel), typeof(ExitReason), typeof(float), typeof(SyncLoopCallbackFunction), typeof(float), typeof(bool) }, typeof(SwHook), "WaitHook", "WaitOrig");
            ok &= Patch(typeof(SocialInteractionA.Definition), "CanSocializeWithSyncCheck", new Type[] { typeof(string), typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType(), typeof(bool), typeof(bool) }, typeof(SwHook), "CheckHook", "CheckOrig");
            return ok;
        }

        public class SwHook
        {
            public bool WaitHook(Sim targetSim, Sim.SyncLevel desiredSynchLevel, ExitReason exitReasonInterrupt, float giveupTime, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest)
            {
                Sim me = (Sim)(object)this;
                if (!ShortcutOn("synctime") || sSwIn.ContainsKey(me)) return WaitOrig(targetSim, desiredSynchLevel, exitReasonInterrupt, giveupTime, loopCallback, notifySimMinutes, performSocializeWithTest);
                SwStat st = new SwStat();
                sSwIn[me] = st;
                long t0 = SimClock.CurrentTicks;
                bool r = false;
                try { r = WaitOrig(targetSim, desiredSynchLevel, exitReasonInterrupt, giveupTime, loopCallback, notifySimMinutes, performSocializeWithTest); return r; }
                finally
                {
                    sSwIn.Remove(me);
                    float min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - t0, TimeUnit.Minutes);
                    if ((min >= 1f || !r) && sSwLines < 3000)
                    {
                        sSwLines++;
                        string res = r ? "ok" : (min >= giveupTime ? "timeout" : (me.HasExitReason(ExitReason.SynchronizationFailed) ? "failed" : "exit"));
                        HcLine("syncwait: " + SwWho(me) + " -> " + SwWho(targetSim) + ", " + desiredSynchLevel + ", " + res + ", " + min.ToString("0.0") + "/" + giveupTime.ToString("0") + " min, checks " + st.Checks + " " + (st.CheckUs / 1000) + " ms, in "
                            + (me.CurrentInteraction != null ? me.CurrentInteraction.GetType().Name : "none") + ", partner in " + SwPartner(me, targetSim) + ", " + HcSimTime());
                    }
                }
            }
            public bool WaitOrig(Sim targetSim, Sim.SyncLevel desiredSynchLevel, ExitReason exitReasonInterrupt, float giveupTime, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest) { return false; }

            public static InteractionTestResult CheckHook(string social, Sim actor, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback, bool bAllowOnToddlers, bool interactionHasAlreadyStarted)
            {
                SwStat st;
                if (actor == null || !sSwIn.TryGetValue(actor, out st)) return CheckOrig(social, actor, target, isAutonomous, ref greyedOutTooltipCallback, bAllowOnToddlers, interactionHasAlreadyStarted);
                uint t0 = SrcNowUs();
                InteractionTestResult r = CheckOrig(social, actor, target, isAutonomous, ref greyedOutTooltipCallback, bAllowOnToddlers, interactionHasAlreadyStarted);
                st.Checks++; st.CheckUs += unchecked(SrcNowUs() - t0);
                return r;
            }
            public static InteractionTestResult CheckOrig(string social, Sim actor, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback, bool bAllowOnToddlers, bool interactionHasAlreadyStarted) { return InteractionTestResult.GenericUnknown; }
        }

        // what the partner was doing when the wait ended, and whether our linked interaction is still in its queue
        static string SwPartner(Sim me, Sim p)
        {
            try
            {
                if (p == null || p.HasBeenDestroyed) return "gone";
                InteractionInstance mine = me.CurrentInteraction, linked = mine != null ? mine.LinkedInteractionInstance : null;
                string where = linked == null ? "no link" : (p.CurrentInteraction == linked ? "linked running" : (p.InteractionQueue != null && p.InteractionQueue.HasInteraction(linked) ? "linked queued" : (p.InteractionQueue != null && p.InteractionQueue.TransitionInteraction == linked ? "linked in transition" : "linked gone")));
                return (p.CurrentInteraction != null ? p.CurrentInteraction.GetType().Name : "none") + " (" + where + (p.SimRoutingComponent != null && p.SimRoutingComponent.IsRouting ? ", routing" : "") + ")";
            }
            catch (Exception) { return "?"; }
        }

        static string SwWho(Sim s)
        {
            if (s == null) return "null";
            return (s.IsSelectable ? "HH " : "") + s.FirstName + (s.SimDescription != null && !s.SimDescription.IsHuman ? "(" + s.SimDescription.Species + ")" : "");
        }
    }
}
