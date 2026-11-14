using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // syncpartnergone (tuning kSyncPartnerGone, off by default; docs/546 §14). EA bug fix / exact speedup.
    //
    // Lock [verified, decompiled]: Sim.WaitForSynchronizationLevelWithSim loops until IsAtSynchronizationLevelWith(partner,
    // level), which for any level below Completed needs partner.SynchronizationTarget == this Sim. Each tick it also stops on
    // its own exit reason and, with performSocializeWithTest (every social wait), on CanSocializeWithSyncCheck, which fails
    // for a destroyed or out-of-world partner (so a partner that left the lot or was deleted is noticed at once). It never
    // looks at the partner's interaction. A partner that is reset (Sim.OnReset: InteractionQueue.OnReset kills its
    // interactions without running their Run tails, then ClearSynchronizationData: target null, level Completed) never
    // copies an exit reason back, so the other Sim stands until the giveup: 30 sim min for a social (SocialInteraction.
    // kSocialSyncGiveupTime: the initiator's Routed / Committed waits in BeginSocialInteraction, the receiver's in
    // ReceiveSocialInteraction), 30 for StartSync's Started wait (kNumMinToWaitOnSyncStart). A cancel is not the problem:
    // SocialInteractionB.Run and SocialInteractionA.Run copy their exit reason to the partner (CopyExitReasonToLinked-
    // Interaction), and StartSync's pre-sync loop returns once the linked interaction left the partner's queue.
    //
    // Fix: the wait gets a loop callback (only when the caller passed none) that EA's loop runs every kSpgEveryMin sim
    // minutes; it returns false, which EA answers with OnWaitForSynchronizationLevelWithSimFailed + return false (the
    // giveup path's exact call and result), once the partner is gone at two calls in a row:
    //   destroyed: HasBeenDestroyed (no task of its own can set its sync state again; every Sim);
    //   or, for a social (this Sim's running interaction and its link are SocialInteractions, link run by the partner):
    //   the partner's SynchronizationTarget is not this Sim (so the wait cannot succeed now) and the linked interaction is
    //   neither running (running stack or head, so nested and still-routing partners count as present), nor queued, nor
    //   in a posture transition: it has ended for good. A slow partner (queued, in transition, routing to the jig) is still
    //   running or queued, so it never matches. Completed waits are left alone (a reset partner is at Completed, and
    //   vanilla then succeeds).
    // Household rule: the destroyed case is exact (vanilla's result, only sooner), so it applies to every Sim. The social
    // case is vanilla's result unless the partner later re-targets this Sim from a new sync of its own before the giveup,
    // which cannot be ruled out, so it acts only when the waiting Sim is an NPC (IsNpcSim, PlayerSims.cs); the partner is
    // never touched (its SynchronizationTarget is not this Sim, so the failure call adds nothing to it).
    // Save: the callback object sits in the wait's frame and is [Persistable]; a frame saved mid-wait resumes with it.
    // The hook is the shared wait hook in SocialReceiveFail.cs (a method is patched once); test: SyncPartnerGoneCheck
    // (SyncChecks.cs). Counters: [2370] waits ended early, social partner gone; [2371] ended early, partner destroyed;
    // [2372] social partner gone but the waiting Sim is a player Sim (left to the giveup); [2373] errors.
    public static partial class Fixes
    {
        const float kSpgEveryMin = 1f;  // sim minutes between two looks at the partner; two looks in a row must agree

        // Called by SyncWaitHook before the vanilla wait: the loop callback to pass on (the caller's own when it has one).
        static SyncLoopCallbackFunction SpgCallback(Sim me, Sim target, Sim.SyncLevel level, SyncLoopCallbackFunction loopCallback, ref float notifySimMinutes)
        {
            if (loopCallback != null || target == null || level == Sim.SyncLevel.Completed || !ShortcutOn("syncpartnergone")) return loopCallback;
            notifySimMinutes = kSpgEveryMin;
            return new SpgWatch(me, target).Check;
        }

        enum SpgState { Present, Gone, Destroyed }

        static SpgState SpgPartner(Sim me, Sim target)
        {
            if (target.HasBeenDestroyed) return SpgState.Destroyed;
            if (target.SynchronizationTarget == me) return SpgState.Present;  // the wait can still succeed
            InteractionQueue mq = me.InteractionQueue, tq = target.InteractionQueue;
            if (mq == null || tq == null || target.SimDescription == null || target.SimDescription.ToddlerOrBelow) return SpgState.Present;
            SocialInteraction mine = mq.RunningInteraction as SocialInteraction;
            SocialInteraction link = mine != null ? mine.LinkedInteractionInstance as SocialInteraction : null;
            if (link == null || link.InstanceActor != target) return SpgState.Present;
            if (tq.IsRunning(link, true) || tq.HasInteraction(link) || tq.TransitionInteraction == link) return SpgState.Present;
            return SpgState.Gone;
        }

        [Persistable]
        public sealed class SpgWatch
        {
            public Sim mMe, mTarget;
            public int mGoneLooks;
            public bool mCountedPlayer;

            public SpgWatch() { }  // for the persister
            public SpgWatch(Sim me, Sim target) { mMe = me; mTarget = target; }

            // EA's loop callback: false ends the wait through the vanilla failure call.
            public bool Check()
            {
                try
                {
                    if (mMe == null || mTarget == null || !ShortcutOn("syncpartnergone")) return true;
                    SpgState s = SpgPartner(mMe, mTarget);
                    if (s == SpgState.Present) { mGoneLooks = 0; return true; }
                    if (++mGoneLooks < 2) return true;
                    int[] c = PatchCounters();
                    if (s == SpgState.Destroyed) { c[2371]++; SpgLog("partner destroyed"); return false; }
                    if (IsPlayerSim(mMe))
                    {
                        if (!mCountedPlayer) { mCountedPlayer = true; c[2372]++; }
                        return true;  // household rule: a player Sim keeps vanilla's giveup
                    }
                    c[2370]++;
                    SpgLog("partner's linked social ended");
                    return false;
                }
                catch (Exception) { PatchCounters()[2373]++; return true; }
            }

            // test switch synctime only (the off/on measure)
            void SpgLog(string why)
            {
                if (ShortcutOn("synctime")) HcLine("syncpartnergone: " + SwWho(mMe) + " -> " + SwWho(mTarget) + " wait ended, " + why + ", in " + SwIn(mMe) + ", " + HcSimTime());
            }
        }
    }
}
