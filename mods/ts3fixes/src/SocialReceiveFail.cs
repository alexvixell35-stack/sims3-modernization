using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // socialreceivefail (tuning kSocialReceiveFail, off by default; docs/546 §9).
    //
    // Lock [measured 2026-11-14, synctime step log]: the starter of a social (SocialInteractionA, sync Initiator) waits the
    // full 30 sim minutes (SocialInteraction.kSocialSyncGiveupTime) for its partner to reach SyncLevel.Routed, while the
    // partner's SocialInteractionB has already given up: its ReceiveSocialInteraction returned false 30+ minutes earlier.
    //
    // EA code [verified, decompiled]: after ReceiveSocialInteraction returns false, SocialInteractionB.Run adds RouteFailed to
    // its Sim, copies it to the starter (CopyExitReasonToLinkedInteraction: only if the starter's social is already running;
    // otherwise it cancels it, which did not stop it in these cases), then sits in WaitForMasterInteractionToFinish until the
    // starter's social stops running or the partner gets Finished. Until then the partner's SynchronizationLevel stays
    // Started (ReceiveSocialInteraction sets Started first; the only later writes are in its own success paths and in
    // WaitForSyncComplete after the master finished). Sim.IsAtSynchronizationLevelWith(partner, L) needs partner level >= L,
    // so for any L above Started the starter's wait cannot succeed; vanilla ends it only through the 30-minute giveup (or an
    // exit reason / failed social test), always via OnWaitForSynchronizationLevelWithSimFailed + false.
    //
    // Fix: in that state, end the wait at once through the same failure call. Same result, 30 sim minutes sooner; every Sim,
    // household included (EA bug fix). One case could differ [inferred, not seen]: if other code gave the partner a
    // Finished exit reason during those 30 minutes, it would leave WaitForMasterInteractionToFinish, reach Completed in
    // WaitForSyncComplete, and the vanilla wait would then succeed. Completed waits are left alone for that reason
    // (socialfinish adds Finished in exactly that wait).
    // Counters: [2312] waits ended early, [2313] errors.
    //
    // This file also owns the two hooks it shares with the synctime test log (SyncWaitLog.cs): a method can be patched
    // once, so the wait and receive hooks run the fix first and then call the test log's helpers when synctime is on.
    public static partial class Fixes
    {
        // ---- shared hooks (installed once, by whichever of socialreceivefail / the synctime test log comes first) ----

        static bool sSwCorePatched;

        static void ApplySocialReceiveFail() { PatchSyncWaitCore(); }

        static bool PatchSyncWaitCore()
        {
            if (sSwCorePatched) return true;
            bool ok = Patch(typeof(Sim), "WaitForSynchronizationLevelWithSim", new Type[] { typeof(Sim), typeof(Sim.SyncLevel), typeof(ExitReason), typeof(float), typeof(SyncLoopCallbackFunction), typeof(float), typeof(bool) }, typeof(SyncWaitHook), "WaitHook", "WaitOrig");
            ok &= Patch(typeof(SocialInteraction), "ReceiveSocialInteraction", new Type[0], typeof(ReceiveHook), "RecvHook", "RecvOrig");
            sSwCorePatched = ok;
            return ok;
        }

        public class SyncWaitHook
        {
            public bool WaitHook(Sim targetSim, Sim.SyncLevel desiredSynchLevel, ExitReason exitReasonInterrupt, float giveupTime, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest)
            {
                Sim me = (Sim)(object)this;
                if (RfCannotSync(me, targetSim, desiredSynchLevel)) { SwLogEndedEarly(me, targetSim, desiredSynchLevel); return false; }
                SwStat st = SwBegin(me, targetSim, desiredSynchLevel);  // null unless synctime is on (test)
                if (st == null) return WaitOrig(targetSim, desiredSynchLevel, exitReasonInterrupt, giveupTime, loopCallback, notifySimMinutes, performSocializeWithTest);
                bool r = false;
                try { r = WaitOrig(targetSim, desiredSynchLevel, exitReasonInterrupt, giveupTime, loopCallback, notifySimMinutes, performSocializeWithTest); return r; }
                finally { SwFinish(me, targetSim, desiredSynchLevel, giveupTime, r, st); }
            }
            public bool WaitOrig(Sim targetSim, Sim.SyncLevel desiredSynchLevel, ExitReason exitReasonInterrupt, float giveupTime, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest) { return false; } // receives the original
        }

        public class ReceiveHook
        {
            public bool RecvHook()
            {
                SwStep(this, "receive>");
                bool r = false;
                try { r = RecvOrig(); return r; }
                finally { SwStep(this, r ? "receive<ok" : "receive<fail"); RfReceiveDone(this, r); }
            }
            public bool RecvOrig() { return false; } // receives the original
        }

        // ---- the fix ----

        // SocialInteractionB instances whose ReceiveSocialInteraction returned false and that may still be running;
        // compared by reference, stale ones dropped whenever the list is touched.
        static readonly List<SocialInteraction> sRfFailed = new List<SocialInteraction>();

        static void RfPrune()
        {
            for (int i = sRfFailed.Count - 1; i >= 0; i--)
            {
                SocialInteraction x = sRfFailed[i];
                Sim xa = x.InstanceActor;
                if (xa == null || xa.CurrentInteraction != x) sRfFailed.RemoveAt(i);
            }
        }

        // Receiver side: remember a SocialInteractionB whose ReceiveSocialInteraction just returned false.
        static void RfReceiveDone(object ii, bool r)
        {
            try
            {
                if (r || !ShortcutOn("socialreceivefail")) return;
                SocialInteractionB b = ii as SocialInteractionB;
                if (b == null) return;
                RfPrune();
                if (sRfFailed.Count < 256) sRfFailed.Add(b);
            }
            catch (Exception) { PatchCounters()[2313]++; }
        }

        // Why a starter's wait is or is not the parked-receiver case (shared by the fix and the synctime test counters).
        enum RfCase { Parked, NoList, NotInitiator, NotLinked, NotFailed, PartnerMoved }

        static RfCase RfClassify(Sim me, Sim target, Sim.SyncLevel level)
        {
            if (sRfFailed.Count == 0 || target == null || level <= Sim.SyncLevel.Started || level == Sim.SyncLevel.Completed) return RfCase.NoList;
            if (me.SynchronizationRole != Sim.SyncRole.Initiator) return RfCase.NotInitiator;
            InteractionInstance a = me.CurrentInteraction;
            SocialInteractionB b = a != null ? a.LinkedInteractionInstance as SocialInteractionB : null;
            if (b == null || b.InstanceActor != target || target.CurrentInteraction != b) return RfCase.NotLinked;
            bool failed = false;
            for (int i = 0; i < sRfFailed.Count && !failed; i++) failed = object.ReferenceEquals(sRfFailed[i], b);
            if (!failed) return RfCase.NotFailed;
            // still parked after its failure: at Started, aimed at me, and not released by a Finished exit reason
            if (target.SynchronizationLevel != Sim.SyncLevel.Started || target.SynchronizationTarget != me || target.HasExitReason(ExitReason.Finished)) return RfCase.PartnerMoved;
            return RfCase.Parked;
        }

        // Starter side: true when the wait provably cannot succeed (see the header), after the vanilla failure call.
        static bool RfCannotSync(Sim me, Sim target, Sim.SyncLevel level)
        {
            try
            {
                if (sRfFailed.Count == 0 || !ShortcutOn("socialreceivefail") || RfClassify(me, target, level) != RfCase.Parked) return false;
                me.SynchronizationSleeping = me.SynchronizationLevel >= level;  // as the vanilla wait sets it on entry
                me.OnWaitForSynchronizationLevelWithSimFailed(target);
                PatchCounters()[2312]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2313]++; return false; }
        }
    }
}
