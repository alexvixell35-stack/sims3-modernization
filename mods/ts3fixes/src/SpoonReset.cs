using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Beds;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // spoonreset (tuning kSpoonReset, off by default; docs/565 §6). EA bug fix in shared-bed spooning (BedSleep).
    //
    // [verified, decompiled] Two romantic partners sleeping in the same bed pair up in BedSleep.LoopHandler: the first to
    // tick becomes master, the other slave (mSpooningMaster/SlaveInteraction point at each other). The master later puts
    // both Sims' posture state machines into SpoonHold / Spoon, and ends it with FinishSpooning (both postures back to
    // "Sleep", sync data cleared, ClearSpooningContext). Only BedSleep.Run's post-loop code and the master's LoopHandler
    // ever end a pair. When one partner's interaction ends without that code (the Sim is reset, deleted, or its interaction
    // aborted), the other keeps the pair forever:
    //  - slave left alone: its LoopHandler returns at once every tick (`if (mSpooningSlave) return`), so it stays in the
    //    spoon pose on its own and skips EA's light-sleeper wake, the TiredFromMoving / NoMonsters timers and the evil-gnome
    //    wake for the rest of the night; at wake-up its posture machine is still in the spoon state;
    //  - master left alone: at the spoon end time FinishSpooning requests a state on the gone partner's posture machine
    //    (an error out of the loop callback that ends the sleep), or the master keeps re-pairing with no partner.
    // Fix: in LoopHandler, before EA's body, when this Sim is in a pair and the partner's BedSleep is gone (null, partner
    // destroyed, or no longer in the partner's queue: EA's own test in the slave's wake-up wait), this Sim's side is ended
    // the way FinishSpooning ends it: when the spoon had started, the master's spoon machine to "ToSleep" and this Sim's
    // posture to "Sleep"; then sync data cleared and EA's ClearSpooningContext. Pairs whose partner is still sleeping run
    // EA's code unchanged.
    //
    // Counters: [2536] pairs released after the partner was gone, [2537] errors. Check: SpoonResetChecks.cs.
    // The same hook runs spoonslavechecks (docs/543 §1, SpoonSlaveChecks.cs): EA's per-sleeper checks for a live pair's slave.
    // The same hook runs spoonslavechecks (docs/597 §1, SleepQueue597.cs): EA's per-sleeper checks for a live pair's slave.
    public static partial class Fixes
    {
        static readonly FieldInfo sSrMasterI = typeof(BedSleep).GetField("mSpooningMasterInteraction", kInstAll);
        static readonly FieldInfo sSrSlaveI = typeof(BedSleep).GetField("mSpooningSlaveInteraction", kInstAll);
        static readonly FieldInfo sSrIsSlave = typeof(BedSleep).GetField("mSpooningSlave", kInstAll);
        static readonly FieldInfo sSrIsMaster = typeof(BedSleep).GetField("mSpooningMaster", kInstAll);
        static readonly FieldInfo sSrSpooning = typeof(BedSleep).GetField("mSpooning", kInstAll);
        static readonly FieldInfo sSrSmc = typeof(BedSleep).GetField("mSpooningStateMachine", kInstAll);
        static readonly MethodInfo sSrClear = typeof(BedSleep).GetMethod("ClearSpooningContext", kInstAll, null, Type.EmptyTypes, null);

        static void ApplySpoonReset()
        {
            if (sSrMasterI == null || sSrSlaveI == null || sSrIsSlave == null || sSrIsMaster == null || sSrSpooning == null || sSrSmc == null || sSrClear == null)
            {
                AppliedLog().Add("spoonreset: BedSleep spooning members not found, not patched");
                return;
            }
            Patch(typeof(BedSleep), "LoopHandler", typeof(SrHook), "LoopHook", "LoopOrig");
        }

        // ---- decision (also used by the check) ----
        internal static bool SrPartnerGone(bool on, bool inPair, bool partnerNull, bool partnerDestroyed, bool partnerInQueue)
        {
            return on && inPair && (partnerNull || partnerDestroyed || !partnerInQueue);
        }

        public class SrHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData data)
            {
                if (ShortcutOn("spoonreset"))
                {
                    try
                    {
                        object st = this;
                        BedSleep me = (BedSleep)st;
                        BedSleep master = (BedSleep)sSrMasterI.GetValue(st), slave = (BedSleep)sSrSlaveI.GetValue(st);
                        if (master != null || slave != null)  // EA sets both links on both Sims when it pairs them
                        {
                            bool isSlave = (bool)sSrIsSlave.GetValue(st), isMaster = (bool)sSrIsMaster.GetValue(st);
                            BedSleep partner = isSlave ? master : slave;
                            Sim pa = partner == null ? null : partner.Actor;
                            bool destroyed = pa == null || pa.HasBeenDestroyed;
                            if (SrPartnerGone(true, isSlave || isMaster, partner == null, destroyed, !destroyed && pa.InteractionQueue != null && pa.InteractionQueue.HasInteraction(partner)))
                            {
                                PatchCounters()[2536]++;
                                Sim actor = me.Actor;
                                if ((bool)sSrSpooning.GetValue(st))
                                {
                                    StateMachineClient spoon = (StateMachineClient)sSrSmc.GetValue(st);
                                    if (isMaster && spoon != null) spoon.RequestState("x", "ToSleep");
                                    if (actor.Posture != null && actor.Posture.CurrentStateMachine != null) actor.Posture.CurrentStateMachine.RequestState("x", "Sleep");
                                }
                                actor.ClearSynchronizationData();
                                sSrClear.Invoke(st, null);
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[2537]++; }
                }
                object self = this;
                bool slaveChecks = SsBefore(self, ShortcutOn("spoonslavechecks"));  // spoonslavechecks (SpoonSlaveChecks.cs, docs/543 §1): read before EA's body
                LoopOrig(smc, data);
                if (slaveChecks) SsAfter(self);
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData data) { } // receives BedSleep.LoopHandler's original body
        }
    }
}
