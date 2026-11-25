using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // autonomyfailblock (tuning kAutonomyFailBlock, off by default; docs/565 §3). EA bug fix: the failure memory of
    // autonomy (GameObject.UpdateBlockTime: a 1 sim-minute hard block for autonomous tests, then a score penalty that fades
    // over 30 sim-minutes) is skipped when an autonomous action fails because its posture cannot be reached.
    //
    // [verified, decompiled] InteractionQueue.ProcessOneInteraction clears the Sim's exit reasons at the top of each
    // posture-transition try. When the posture graph has no transition (GetTransition returns null: no reachable or free
    // part, e.g. every seat of the table taken or cut off) or keeps offering the same one, the loop ends with no exit
    // reason set, the posture check fails and OnValidInteractionFailed is called. That method blocks the target only when
    // ShouldUpdateBlockTime sees an ExitReason.CausesBlocking reason, which this path never sets. The object is not marked,
    // so the next autonomy pass scores it at full value and the Sim picks the same unusable object again (an NPC whose
    // last autonomous action failed runs autonomy again at once: Sim.CanRunAutonomyImmediately). A route failure inside a
    // transition sets RouteFailed and is blocked by EA already; this path is the one EA forgot.
    //
    // Fix: in OnValidInteractionFailed, for an autonomous interaction whose target is still valid, not cancelled, whose
    // Sim has no exit reason at all (so nothing EA tracks, no cancel, no higher-priority push) and whose Test() still
    // passes (a failed test means the action is no longer offered: not a blocking case), EA's UpdateBlockTime is called on
    // the target before EA's own handling. Sim targets (socials) are left as EA has them. Same mechanism and duration EA
    // uses for its other failures; player-directed actions are untouched (EA clears the block for them).
    //
    // Counters: [2530] autonomous posture failures blocked, [2531] failures left alone (exit reason set, test failed, Sim
    // target), [2532] errors. Check: AutonomyFailBlockChecks.cs.
    public static partial class Fixes
    {
        static void ApplyAutonomyFailBlock()
        {
            Patch(typeof(InteractionQueue), "OnValidInteractionFailed", typeof(AfbHook), "FailedHook", "FailedOrig");
        }

        // ---- decision (also used by the check) ----
        // testPasses is evaluated only when everything else says block (it runs the interaction's Test()).
        internal static bool AfbShouldBlock(bool on, bool autonomous, bool targetValid, bool cancelled, bool anyExitReason, bool targetIsSim, bool testPasses)
        {
            return on && autonomous && targetValid && !cancelled && !anyExitReason && !targetIsSim && testPasses;
        }

        public class AfbHook
        {
            public void FailedHook(InteractionInstance i)
            {
                if (ShortcutOn("autonomyfailblock") && i != null && i.Autonomous)
                {
                    try
                    {
                        Sim actor = i.InstanceActor;
                        bool valid = i.IsTargetValid(), cancelled = i.Cancelled, reason = actor == null || actor.ExitReason != ExitReason.None;
                        bool simTarget = i.Target is Sim;
                        bool pre = AfbShouldBlock(true, true, valid, cancelled, reason, simTarget, true);
                        if (pre && AfbShouldBlock(true, true, valid, cancelled, reason, simTarget, i.Test()))
                        {
                            i.Target.UpdateBlockTime();
                            PatchCounters()[2530]++;
                        }
                        else PatchCounters()[2531]++;
                    }
                    catch (Exception) { PatchCounters()[2532]++; }
                }
                FailedOrig(i);
            }
            public void FailedOrig(InteractionInstance i) { } // receives InteractionQueue.OnValidInteractionFailed's original body
        }
    }
}
