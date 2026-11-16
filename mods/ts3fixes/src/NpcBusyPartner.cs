using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // npcbusypartner (tuning kNpcBusyPartner, off by default; docs/577 §4). NPC-only wait cap, household exempt.
    //
    // [verified, decompiled] InteractionInstance.StartSync waits up to kNumMinToWaitOnPreSync (15 sim-min) for the partner
    // to start the linked interaction pushed onto its queue. When that time runs out the loop simply ends: there is no
    // failure branch, so the starter goes on to Sim.WaitForSynchronizationLevelWithSim(Started) for another
    // kNumMinToWaitOnSyncStart (30 sim-min) although the partner has still not begun. A partner busy with a long action
    // (a snowball fight, Play, a lot visit, another social; the linked interaction "queued") makes the starter stand for
    // the full 45 sim-min: wait census 2026-11-15 (wait-report.ts, phase-20261115-1146): DanceTogetherA / SocialInteractionA
    // "Started timeout 45.1 min, partner ... (linked queued)", all NPC pairs.
    //
    // Fix: at the start of a Started wait (inside the shared WaitForSynchronizationLevelWithSim hook, SocialReceiveFail.cs),
    // when both Sims are NPCs (PlayerSims.IsNpcSim: a Sim of unknown scope counts as household) and the waiter's linked
    // interaction is still only queued on the partner (in its queue, not its head, not its current or transition
    // interaction), the wait fails at once through EA's own failure call (OnWaitForSynchronizationLevelWithSimFailed),
    // as the vanilla timeout does 30 sim-min later. Inside StartSync that state means the 15-minute pre-sync already ran
    // out. Household Sims, and NPCs waiting on a household Sim, keep EA's wait unchanged.
    // Counters: [2696] waits ended (on), [2697] the same case seen with the switch off, [2698] errors.
    // Check: NpcBusyPartnerChecks.cs.
    public static partial class Fixes
    {
        static void ApplyNpcBusyPartner() { PatchSyncWaitCore(); }  // the hook is shared; this only makes sure it is in

        internal static bool NbpGiveUp(bool on, bool levelIsStarted, bool waiterNpc, bool partnerNpc, bool linkQueued,
            bool linkIsHeadOrCurrent, bool partnerAtLevel)
        {
            return on && levelIsStarted && waiterNpc && partnerNpc && linkQueued && !linkIsHeadOrCurrent && !partnerAtLevel;
        }

        // Called first in SyncWaitHook.WaitHook. True = the wait was ended here (caller returns false).
        static bool NbpCannotStart(Sim me, Sim target, Sim.SyncLevel level)
        {
            if (level != Sim.SyncLevel.Started || me == null || target == null) return false;
            try
            {
                InteractionInstance mine = me.InteractionQueue != null ? me.InteractionQueue.RunningInteraction : null;
                InteractionInstance link = mine != null ? mine.LinkedInteractionInstance : null;
                if (link == null || target.InteractionQueue == null) return false;
                InteractionInstance cur = target.InteractionQueue.TransitionInteraction ?? target.CurrentInteraction;
                bool headOrCur = link == cur || link == target.InteractionQueue.GetHeadInteraction();
                if (headOrCur || !target.InteractionQueue.HasInteraction(link)) return false;
                if (me.IsAtSynchronizationLevelWith(target, level) || !IsNpcSim(me) || !IsNpcSim(target)) return false;
                bool on = ShortcutOn("npcbusypartner");
                if (!NbpGiveUp(on, true, true, true, true, false, false)) { PatchCounters()[2697]++; return false; }
                me.SynchronizationSleeping = me.SynchronizationLevel >= level;  // as the vanilla wait sets it on entry
                me.OnWaitForSynchronizationLevelWithSimFailed(target);
                PatchCounters()[2696]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2698]++; return false; }
        }
    }
}
