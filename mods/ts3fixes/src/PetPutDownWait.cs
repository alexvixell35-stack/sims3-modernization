using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // petputdownwait (tuning kPetPutDownWait, off by default; docs/578 §2). EA bug fix: a carried pet keeps waiting up to
    // 30 sim-min for a carrier that has already given up putting it down.
    //
    // [verified, decompiled] Sim.PutDownPet runs on both Sims. The pet's instance (pushed by its BeingCarriedPetPosture
    // standing transition, or linked from the carrier's) calls StartSync(false). Its pre-sync loop ends as soon as the
    // carrier's current interaction is the linked PutDownPet, then Sim.WaitForSynchronizationLevelWithSim(carrier,
    // Started, kNumMinToWaitOnSyncStart 30) waits for the carrier to reach Started. The carrier's PutDownPet first places
    // a jig and routes to it, and returns false without FinishLinkedInteraction when FindGoodLocationNearby fails, the
    // route fails (DoRoute), or SafeToSync fails. SocialInteraction.Cleanup does not touch the linked instance either.
    // The pet's wait loop never checks that its partner's linked interaction still exists, so it runs to the 30-minute
    // giveup while the pet hangs in the carrier's arms. Measured: syncwait "in PutDownPet" waits of 28-45 sim-min, in
    // runs with only logging on too (phase-20261114-1737: 7 waits, 195 min).
    //
    // Fix: for the pet side of PutDownPet (waiter's running interaction is Sim.PutDownPet and the waiter is not human),
    // a Started wait with no loop callback of its own gets one, polled every sim-minute (EA's own notifySimMinutes
    // mechanism). It returns false, ending the wait through EA's failure call, once the carrier no longer has the linked
    // PutDownPet running or queued and is not syncing with the pet. While the carrier's PutDownPet is alive (even
    // routing for a long time) the callback returns true: EA's wait is unchanged. Every Sim, household included: the
    // wait cannot succeed once the partner's half is gone (vanilla intent of the linked pair), so only the 30 dead
    // minutes go.
    // Counters: [3976] waits ended (on), [3977] the same case seen with the switch off, [3978] errors.
    // Check: PetPutDownWaitChecks.cs.
    public static partial class Fixes
    {
        static void ApplyPetPutDownWait() { PatchSyncWaitCore(); }  // shared wait hook (SocialReceiveFail.cs)

        internal static bool PpwEnd(bool on, bool levelIsStarted, bool petSidePutDown, bool linkAlive, bool partnerSyncingWithMe)
        {
            return on && levelIsStarted && petSidePutDown && !linkAlive && !partnerSyncingWithMe;
        }

        // Called from SyncWaitHook.WaitHook: returns the callback to pass in place of a null one, or null to leave EA's.
        static SyncLoopCallbackFunction PpwCallback(Sim me, Sim target, Sim.SyncLevel level, SyncLoopCallbackFunction given)
        {
            if (given != null || level != Sim.SyncLevel.Started || me == null || target == null || me.IsHuman) return null;
            try
            {
                Sim.PutDownPet mine = me.InteractionQueue != null ? me.InteractionQueue.RunningInteraction as Sim.PutDownPet : null;
                if (mine == null) return null;
                InteractionInstance link = mine.LinkedInteractionInstance;
                bool counted = false;
                return delegate
                {
                    try
                    {
                        bool alive = link != null && target.InteractionQueue != null
                            && (target.InteractionQueue.IsRunning(link, true) || target.InteractionQueue.HasInteraction(link));
                        bool syncing = target.SynchronizationTarget == me && target.SynchronizationLevel >= Sim.SyncLevel.Started;
                        if (!PpwEnd(true, true, true, alive, syncing)) return true;
                        if (!ShortcutOn("petputdownwait")) { if (!counted) { counted = true; PatchCounters()[3977]++; } return true; }
                        PatchCounters()[3976]++;
                        return false;
                    }
                    catch (Exception) { PatchCounters()[3978]++; return true; }
                };
            }
            catch (Exception) { PatchCounters()[3978]++; return null; }
        }
    }
}
