using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // socialnoeffect guard (switch socialnoeffect, kSocialNoEffect, default off: the switch already meant the ts3native ilp_socialnoeffect
    // fix; Batch589dLogic.cs BSnHook counts the null rules in [3052]).
    // [verified, decompiled] SocialInteractionA.Run sets mTargetEffect from Conversation.UpdateOnSelectingInteraction
    // (UpdateConversationWhenSocialStarts), which is null when no social rule matched or the target is no longer in the
    // actor's conversation. GetStateMachine, the next step, reads mTargetEffect.RHS first and throws NullReferenceException;
    // the actor is reset, and the partner's SocialInteractionB then throws in CopyExitReasonToLinkedInteraction on the
    // reset A (af6 2026-11-17 seed 31339, The Prosper Room: 2 + 1 errors). The ts3native IL patch for this
    // (ilp_socialnoeffect, docs/589 §23) never reached this branch.
    // Fix: GetStateMachine with no rule ends the social the way Run's own tail does (Finished on the actor, copied to the
    // partner's SocialInteractionB) and returns null. Run's EA path for a null state machine skips the
    // social body and returns false; EA's Cleanup already allows a null mTargetEffect. Only a sure throw is replaced.
    // Counters [2785] socials ended, [2786] no rule with the switch off (EA's GetStateMachine then throws), [2787] errors.
    public static partial class Fixes
    {
        public class SocialNoEffectHook
        {
            public StateMachineClient GsmHook()
            {
                SocialInteractionA a = (SocialInteractionA)(object)this;
                if (sSnTestActor != null && a.Actor == sSnTestActor && sSnEffect != null) { sSnTestActor = null; sSnEffect.SetValue(a, null); }
                if (sSnEffect == null || sSnEffect.GetValue(a) != null) return GsmOrig();
                if (!ShortcutOn("socialnoeffect")) { PatchCounters()[2786]++; return GsmOrig(); }
                try
                {
                    // EA's own end of a social (Run: AddExitReason(Finished), CopyExitReasonToLinkedInteraction): the partner's
                    // WaitForMasterInteractionToFinish returns on Finished, so both reach WaitForSyncComplete without its timeout
                    a.Actor.AddExitReason(ExitReason.Finished);
                    a.CopyExitReasonToLinkedInteraction();
                    PatchCounters()[2785]++;
                }
                catch (Exception) { PatchCounters()[2787]++; }
                return null;
            }
            public StateMachineClient GsmOrig() { return null; } // receives SocialInteractionA.GetStateMachine's original body
        }

        // Harness check (repro socialnoeffect-guard-check; reflect_call_async Ts3Fixes.Fixes.SocialNoEffectGuardCheck "expect
        // broken"|"expect fixed", then reflect_call Ts3Fixes.Fixes.SocialNoEffectGuardResult): the active Sim chats with the
        // nearest other human on its lot, and the rule is cleared just before GetStateMachine (the state the game reaches when
        // no rule matched). Off: one no-rule start seen, EA throws (the actor is reset). On: one social ended, no error, and
        // the partner's SocialInteractionB ends too.
        static Sim sSnTestActor;
        public static string sSnGuardResult = "not run";

        public static object SocialNoEffectGuardResult()
        {
            if (!sSnGuardResult.StartsWith("as expected")) throw new InvalidOperationException("socialnoeffect guard check: " + sSnGuardResult);
            return sSnGuardResult;
        }

        public static void SocialNoEffectGuardCheck(string what)
        {
            sSnGuardResult = "running";
            // reflect_call runs this through runtime_invoke, whose frame cannot yield (docs/119 §11.2): wait on our own task
            Simulator.AddObject(new LoopTask(delegate { SocialNoEffectGuardRun(what); }));
        }

        static void SocialNoEffectGuardRun(string what)
        {
            try
            {
                Sim actor = Sim.ActiveActor;
                if (actor == null || sSnEffect == null) { sSnGuardResult = "no active Sim or mTargetEffect"; return; }
                Sim target = null;
                float best = 900f;  // 30 m
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>(actor.Position, 30f))
                {
                    if (s == actor || !s.IsHuman || s.SimDescription.ToddlerOrBelow || s.InteractionQueue == null) continue;
                    float d = (s.Position - actor.Position).LengthSqr();
                    if (d < best) { best = d; target = s; }
                }
                if (target == null) { sSnGuardResult = "no other human within 30 m"; return; }
                int ended = PatchCounters()[2785], off = PatchCounters()[2786], err = PatchCounters()[2787];
                InteractionDefinition def = new SocialInteractionA.Definition("Chat", new string[0], null, false);
                // up to 3 chats: one can end before GetStateMachine (etl4 2026-11-18 round 4, after round 3's EA reset of the actor)
                bool reached = false;
                int attempts = 0;
                for (; attempts < 3 && !reached; attempts++)
                {
                    InteractionInstance a = def.CreateInstance(target, actor, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                    actor.InteractionQueue.CancelAllInteractions();
                    sSnTestActor = actor;
                    actor.InteractionQueue.Add(a);
                    float end = SimClock.ElapsedTime(TimeUnit.Minutes) + 20f;
                    bool started = false;
                    for (int i = 0; i < 100000 && SimClock.ElapsedTime(TimeUnit.Minutes) < end; i++)
                    {
                        if (actor.CurrentInteraction == a) started = true;
                        else if (started || PatchCounters()[2785] != ended || PatchCounters()[2786] != off) break;
                        Simulator.Sleep(1);
                    }
                    InteractionInstance b = target.CurrentInteraction;
                    for (int i = 0; b is SocialInteractionB && target.CurrentInteraction == b && i < 600; i++) Simulator.Sleep(1);
                    reached = sSnTestActor == null;
                }
                sSnTestActor = null;
                bool on = ShortcutOn("socialnoeffect");
                int dEnded = PatchCounters()[2785] - ended, dOff = PatchCounters()[2786] - off, dErr = PatchCounters()[2787] - err;
                bool partnerFree = !(target.CurrentInteraction is SocialInteractionB);
                bool ok = reached && (what == "expect broken" ? !on && dOff == 1 && dEnded == 0 : on && dEnded == 1 && dOff == 0 && dErr == 0 && partnerFree);
                sSnGuardResult = (ok ? "as expected (" : "WRONG (") + what + "); switch " + on + ", GetStateMachine reached " + reached + " (chats " + attempts + "), ended " + dEnded
                    + ", EA throws (off) " + dOff + ", errors " + dErr + ", partner " + target.FullName + (partnerFree ? " free" : " still in SocialInteractionB");
            }
            catch (ResetException) { sSnTestActor = null; sSnGuardResult = "check task reset"; throw; }
            catch (Exception e) { sSnTestActor = null; sSnGuardResult = "error " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
