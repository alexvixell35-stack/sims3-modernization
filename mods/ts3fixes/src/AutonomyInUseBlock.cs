using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;

namespace Ts3Fixes
{
    // autonomyinuseblock (tuning kAutonomyInUseBlock, off by default; docs/577 §1). EA bug fix: autonomy's failure memory
    // (GameObject.UpdateBlockTime: 1 sim-minute hard block in autonomous tests, then a score penalty fading over 30
    // sim-minutes) is skipped when an autonomous action finds its object taken by another Sim and its Run() just returns
    // false.
    //
    // [verified, decompiled] InteractionQueue.ProcessOneInteraction clears the exit reasons, runs the head interaction and
    // blocks the target only when ShouldUpdateBlockTime sees an ExitReason.CausesBlocking reason. EA's routing helpers
    // RouteTo*AndCheckInUse add ExitReason.ObjectInUse when the object is taken on arrival, so that case is blocked. About
    // 96 Run() bodies (runfalse_scan.py, docs/577 §1) instead test Target.InUse / UseCount / ActorsUsingMe themselves and
    // return false with no exit reason: the object is not marked, the Sim's next autonomy pass scores it at full value, and
    // an NPC whose last autonomous action failed re-runs autonomy at once (Sim.CanRunAutonomyImmediately), so it routes
    // to the same taken object again and again (failure spam).
    //
    // Fix: a prefix on InteractionQueue.DeQueue(false) for the main run path only (mIsHeadInteractionActive, set just
    // before Run, so the posture/test failure paths of OnValidInteractionFailed are left to autonomyfailblock / EA): when
    // the head interaction is autonomous, not cancelled, its Sim has no exit reason at all, its target is a valid non-Sim
    // object and that object is in use by a Sim other than the actor and its linked partner, EA's UpdateBlockTime is
    // called: the same mark EA sets for the CheckInUse routes. Player-directed actions are untouched (EA clears the block
    // for them). The decision reads fields only; the one reflected field read happens after every cheap filter passed.
    //
    // Counters: [2693] taken objects blocked (on), [2694] same case seen with the switch off (a census of the spam),
    // [2695] errors. `result` also lists the last 8 cases (interaction @ object type). Check: AutonomyInUseBlockChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sAibHeadActive = typeof(InteractionQueue).GetField("mIsHeadInteractionActive",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly string[] sAibLast = new string[8];
        static int sAibN;

        static void ApplyAutonomyInUseBlock()
        {
            Patch(typeof(InteractionQueue), "DeQueue", typeof(AibHook), "DeQueueHook", "DeQueueOrig");
        }

        // ---- decision (also used by the check) ----
        internal static bool AibShouldBlock(bool on, bool succeeded, bool mainRunPath, bool autonomous, bool cancelled,
            bool anyExitReason, bool targetValid, bool targetIsSim, bool takenByOther)
        {
            return on && !succeeded && mainRunPath && autonomous && !cancelled && !anyExitReason && targetValid && !targetIsSim && takenByOther;
        }

        // True when a Sim other than the actor (and the actor's linked partner) holds the object: carried by one
        // (GameObject.InUse's parent walk) or on its use list.
        internal static bool AibTakenByOther(GameObject t, Sim actor, Sim partner)
        {
            for (IGameObject p = t.Parent; p != null; p = p.Parent)
            {
                Sim holder = p as Sim;
                if (holder != null) return holder != actor && holder != partner;
                if (p.ObjectId == t.ObjectId) break;
            }
            foreach (Sim s in t.ActorsUsingMe) if (s != null && s != actor && s != partner) return true;
            return false;
        }

        static void AibNote(InteractionInstance i, GameObject t)
        {
            sAibLast[sAibN++ % sAibLast.Length] = i.GetType().Name + " @ " + t.GetType().Name;
        }

        public class AibHook
        {
            public void DeQueueHook(bool succeeded)
            {
                if (!succeeded)
                {
                    try
                    {
                        InteractionQueue q = (InteractionQueue)(object)this;
                        InteractionInstance i = q.GetHeadInteraction();
                        Sim actor = i != null ? i.InstanceActor : null;
                        GameObject t = i != null ? i.Target as GameObject : null;
                        // cheap filters first; the reflected field read and the use-list walk only for the rare survivor
                        if (i != null && i.Autonomous && !i.Cancelled && actor != null && actor.ExitReason == ExitReason.None
                            && t != null && !(t is Sim) && sAibHeadActive != null && i.IsTargetValid()
                            && (bool)sAibHeadActive.GetValue(q))
                        {
                            InteractionInstance link = i.LinkedInteractionInstance;
                            bool taken = AibTakenByOther(t, actor, link != null ? link.InstanceActor : null);
                            if (taken)
                            {
                                bool on = ShortcutOn("autonomyinuseblock");
                                if (AibShouldBlock(on, false, true, true, false, false, true, false, true))
                                {
                                    t.UpdateBlockTime();
                                    PatchCounters()[2693]++;
                                }
                                else PatchCounters()[2694]++;
                                AibNote(i, t);
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[2695]++; }
                }
                DeQueueOrig(succeeded);
            }
            public void DeQueueOrig(bool succeeded) { } // receives InteractionQueue.DeQueue's original body
        }
    }
}
