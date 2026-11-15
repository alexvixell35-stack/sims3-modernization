using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.PerformanceObjects;

namespace Ts3Fixes
{
    // simfestkeep (tuning kSimFestKeep, off by default; docs/546 §11). EA bug fix, every Sim.
    //
    // At the end of a SimFest (Showtime) the emcee presents the award: ShowStage.EmceePresentSimFestAward (emcee = master,
    // winner = receiver). The prize itself (PerformanceWinner buff, kWinSimFest, career XP, prize money, the trophy) and
    // the SimFest's own cleanup (ClearSimFestData, the jig) are in EmceeAwardCeremonyWrapUp, which the master queues on
    // success and when its route to the jig or the Routed sync fails. Three earlier exits return false without queuing it:
    // SafeToSync false, StartSync false, and the 5 s wait for the winner to reach Started (the winner was just pulled out of
    // the audience with CancelAllInteractions). Then the prize is never given and the SimFest is never cleared [verified in
    // the decompiled game code; found by the 2026-11-14 audit after the City Hall award report].
    //
    // The hook runs EA's Run and, when the master's Run returned false and no wrap-up is in its queue, queues the wrap-up
    // exactly as EA's own failure branches do (same priority, autonomy, winner and jig). Every path EA already handles
    // leaves a wrap-up in the queue, so it is untouched. Counter [2324] wrap-ups queued, [2325] errors (registry fixes-2324).
    public static partial class Fixes
    {
        static readonly Type sSfPresent = typeof(ShowStage).GetNestedType("EmceePresentSimFestAward", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSfWrapUp = typeof(ShowStage).GetNestedType("EmceeAwardCeremonyWrapUp", BindingFlags.Public | BindingFlags.NonPublic);
        // looked up once at Apply; any missing = not patched
        static FieldInfo sSfIsMaster, sSfStage, sSfPresentJig, sSfWrapSingleton, sSfWinner, sSfWrapJig;

        static void ApplySimFestKeep()
        {
            if (sSfPresent == null || sSfWrapUp == null) { AppliedLog().Add("ShowStage SimFest award classes: not found, simfestkeep skipped"); return; }
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sSfIsMaster = sSfPresent.GetField("IsMaster", inst);
            sSfStage = sSfPresent.GetField("mShowStage", inst);
            sSfPresentJig = sSfPresent.GetField("mSocialJig", inst);
            sSfWrapSingleton = sSfWrapUp.GetField("Singleton", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            sSfWinner = sSfWrapUp.GetField("mWinner", inst);
            sSfWrapJig = sSfWrapUp.GetField("mSocialJig", inst);
            if (sSfIsMaster == null || sSfStage == null || sSfPresentJig == null || sSfWrapSingleton == null || sSfWinner == null || sSfWrapJig == null)
            {
                AppliedLog().Add("ShowStage SimFest award fields: not found, simfestkeep skipped");
                return;
            }
            Patch(sSfPresent, "Run", typeof(SimFestKeepHook), "RunHook", "RunOrig");
        }

        public class SimFestKeepHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (!r) SimFestWrapUpIfMissing((InteractionInstance)(object)this);
                return r;
            }
            public bool RunOrig() { return false; } // receives ShowStage.EmceePresentSimFestAward.Run's original body
        }

        static void SimFestWrapUpIfMissing(InteractionInstance present)
        {
            if (!ShortcutOn("simfestkeep")) return;
            try
            {
                if (!(bool)sSfIsMaster.GetValue(present)) return;
                Sim emcee = present.InstanceActor as Sim, winner = present.Target as Sim;
                ShowStage stage = sSfStage.GetValue(present) as ShowStage;
                InteractionDefinition wrapDef = sSfWrapSingleton.GetValue(null) as InteractionDefinition;
                if (emcee == null || winner == null || stage == null || wrapDef == null || emcee.InteractionQueue == null) return;
                if (emcee.InteractionQueue.HasInteractionOfType(wrapDef)) return;  // EA queued it on this path
                // as EA's route-failure branch: CreateInstance(mShowStage, Actor, RequiredNPCBehavior, Actor.IsNPC, false)
                InteractionInstance wrap = wrapDef.CreateInstance(stage, emcee, new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), emcee.IsNPC, false);
                sSfWinner.SetValue(wrap, winner);
                sSfWrapJig.SetValue(wrap, sSfPresentJig.GetValue(present));
                emcee.InteractionQueue.AddNext(wrap);
                PatchCounters()[2324]++;
            }
            catch (Exception) { PatchCounters()[2325]++; }
        }
    }
}
