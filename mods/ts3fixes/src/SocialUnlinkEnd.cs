using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;

namespace Ts3Fixes
{
    // socialunlinkend (tuning kSocialUnlinkEnd, off by default; docs/650 §3). B-side twin of socialreceivefail.
    //
    // Lock [measured, phase-2026111* syncwait lines]: a social partner (Receiver, in SocialInteractionB) waits up to
    // kSocialSyncGiveupTime (30 sim min) for Routed / Committed while its starter is gone: `partner in none (no link)`,
    // partner sync `Completed/None on none`.
    //
    // EA code [verified, decompiled]: SocialInteractionA.Run returns false on several paths after PushSocialB without
    // copying an exit reason to B. Dequeuing A (InteractionQueue.CleanupInteraction) nulls the link both ways and calls
    // CancelInteraction(B, false); CancelNthInteraction adds an exit reason only while the head is locked or in transition,
    // otherwise it removes B from the list (B.Cleanup runs) and B's script keeps running in ReceiveSocialInteraction's wait.
    // Sim.IsAtSynchronizationLevelWith needs the starter's SynchronizationTarget to be the waiting Sim, and the starter's
    // ClearSynchronizationData left it null, so the wait ends only by the giveup, an exit reason or a failed social test.
    //
    // Fix: a non-exempt NPC receiver whose SocialInteractionB has no link, while the partner neither syncs with it nor runs
    // (or transitions into) an interaction aimed at it, ends a Routed / Committed wait at once through the same failure call
    // the giveup uses (OnWaitForSynchronizationLevelWithSimFailed + false). B then runs EA's normal failure path:
    // ReceiveSocialInteraction's Committed wait stops on the SynchronizationFailed exit reason, Run adds RouteFailed, the
    // copy / master wait / sync-complete steps are no-ops without a link, the socializing posture was never entered, and the
    // social jig went with B.Cleanup. The partner is not touched (its SynchronizationTarget is not this Sim).
    // Household, selected and unknown-scope receivers (HouseholdExempt or IsPlayerSim) are counted and left to EA.
    // Rule: SocialUnlinkEndCore. Counters [4140] waits ended, [4141] exempt, [4142] errors. Log capped at 10 a minute.
    public static partial class Fixes
    {
        static int sSueMinute = -1, sSueLogged;

        static void SueLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sSueMinute) { sSueMinute = minute; sSueLogged = 0; }
            if (sSueLogged++ < 10) DiagLine("socialunlinkend: " + s);
        }

        static bool SueAims(InteractionInstance i, Sim me) { return i != null && object.ReferenceEquals(i.Target, me); }

        // The waiting Sim's B: the interaction whose script runs (it may already be off the list), else the head.
        static SocialInteractionB SueB(Sim me)
        {
            SocialInteractionB b = me.InteractionQueue != null ? me.InteractionQueue.RunningInteraction as SocialInteractionB : null;
            return b ?? me.CurrentInteraction as SocialInteractionB;
        }

        // Called first in the shared wait hook (SocialReceiveFail.cs). True = the wait was failed the vanilla way.
        static bool SueCannotSync(Sim me, Sim target, Sim.SyncLevel level)
        {
            if (target == null || (level != Sim.SyncLevel.Routed && level != Sim.SyncLevel.Committed)) return false;
            try
            {
                if (me.SynchronizationRole != Sim.SyncRole.Receiver || !ShortcutOn("socialunlinkend")) return false;
                SocialInteractionB b = SueB(me);
                bool inB = b != null && object.ReferenceEquals(b.Target, target) && object.ReferenceEquals(me.SynchronizationTarget, target);
                bool linked = b != null && b.LinkedInteractionInstance != null;
                bool syncsMe = object.ReferenceEquals(target.SynchronizationTarget, me);
                InteractionQueue q = target.InteractionQueue;
                bool aims = SueAims(target.CurrentInteraction, me) || (q != null && SueAims(q.TransitionInteraction, me));
                int r = SocialUnlinkEndCore.Decide((int)level, true, inB, linked, syncsMe, aims, false);
                if (r == SocialUnlinkEndCore.kNotCase) return false;
                if (HouseholdExempt(me) || IsPlayerSim(me)) { PatchCounters()[4141]++; return false; }
                me.SynchronizationSleeping = me.SynchronizationLevel >= level;  // as the vanilla wait sets it on entry
                me.OnWaitForSynchronizationLevelWithSimFailed(target);
                PatchCounters()[4140]++;
                SueLog(me.FullName + " -> " + target.FullName + ", " + level + ", partner in "
                    + (target.CurrentInteraction != null ? target.CurrentInteraction.GetType().Name : "none")
                    + " (no link): wait ended at once");
                return true;
            }
            catch (Exception ex) { PatchCounters()[4142]++; SueLog("error " + ex.GetType().Name); return false; }
        }
    }
}
