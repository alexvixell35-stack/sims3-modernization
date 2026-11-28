using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/597 §3-§5: the "Read, not built" leads that are real EA bugs. All off by default; all act on household Sims too
    // (EA bug fixes, no skip or throttle). The megaphone lead was checked and skipped (docs/597: harmless, the head is locked).
    //
    // subroutefail (kSubRouteFail; docs/597 §3). [verified, decompiled] SimQueue.WaitForTurn: when TryReplacementInteraction
    // pushes another object of the same need (another toilet, urinal, shower...) as a continuation (WasInUse, FailedToStart),
    // the wait loop breaks with num != 0 and falls into its timeout tail, which calls PlayRouteFail: a route-fail balloon
    // with the busy object's user plus Sim.RouteFailure (a blocking animation) before the Sim walks to the substitute it just
    // found. Nothing yields between the push and that call. Fix: TryReplacementInteraction's hook stamps (Sim, tick) when it
    // pushed; PlayRouteFail skips the route fail for that Sim on that tick. Exit reasons, queue removal and every other
    // route fail stay EA's. Counters: [2763] route fails skipped, [2764] would be skipped (switch off), [2765] errors.
    //
    // petwakebed (kPetWakeBed; docs/597 §4). [verified, decompiled] Sim.WakeUpSim (pet wakes a sleeping Sim) jumps on the
    // free side of a double bed with partData.SetContainedSim(Actor) and clears it only after the 5-minute wait and Jump Down
    // in Run. Cleanup removes the pet from the bed's use list and destroys the jig, but leaves the part's ContainedSim, so a
    // pet reset in between (InteractionQueue.OnReset runs Cleanup) leaves the pet "in" that side: GetSimInOtherPart returns
    // the pet, the side looks taken, and only a later route fail on that part heals it (SimRoutingComponent). Fix: Cleanup
    // also clears any part of mBed whose ContainedSim is the pet when the pet is not on the bed. The Run null-bed lead is
    // not built: RunTest re-runs Test (target sleeping, container IBed) with no yield before Run.
    // Counters: [2167] parts cleared, [2168] parts left (switch off), [2169] errors.
    //
    // slavewakereason (kSlaveWakeReason; docs/597 §5). [verified, decompiled] BedSleep.Run, spooning slave after its sleep
    // loop: `do { exitReason = Actor.ExitReason; Actor.ClearExitReasons(); synced = IsAtSynchronizationLevelWith(master,
    // Completed); if (!synced) { Sleep(5); synced = master gone; } } while (!synced); Actor.AddExitReason(exitReason);`.
    // The reason that ended the sleep is read again on every pass, so after a second pass (the master is still in its
    // spoon animation) it is lost: HigherPriorityNext (player queued an action) is gone and a tired slave plays the Slow
    // Wake instead of the Fast Wake, and the queue's post-Run reason checks see nothing. Fix (no 150-line Run copy): the
    // first pass's IsAtSynchronizationLevelWith call (after EA's first read and clear) is hooked; for the slave in this wait
    // the hook runs EA's wait itself (same order, same 5-tick sleep, same exits, plus a long hard ceiling) and keeps the
    // reasons that arrive during it, then answers true, so EA adds the first pass's reason back. Counters: [2170] waits
    // handled, [2171] waits of more than one pass (EA loses the reason), [2172] slave waits seen (switch off), [2173] errors
    // or ceiling.
    public static partial class Fixes
    {
        // ---- subroutefail ----

        static Sim sSrfSim;
        static long sSrfTick;

        static void ApplySubRouteFail()
        {
            Patch(typeof(SimQueue), "TryReplacementInteraction", typeof(SrfTryHook), "TryHook", "TryOrig");
            Patch(typeof(SimQueue), "PlayRouteFail", typeof(SrfPlayHook), "PlayHook", "PlayOrig");
        }

        // Pure: skip this route fail? Only when a replacement was pushed for this Sim on this tick and EA would play one.
        internal static bool SrfSkip(bool pushed, bool sameSim, bool sameTick, bool dontPlayFlag)
        {
            return pushed && sameSim && sameTick && !dontPlayFlag;
        }

        public class SrfTryHook
        {
            public bool TryHook(InteractionInstance instance)
            {
                bool r = TryOrig(instance);
                if (r && instance != null)
                {
                    sSrfSim = instance.InstanceActor;
                    sSrfTick = SimClock.CurrentTicks;
                }
                return r;
            }
            public bool TryOrig(InteractionInstance instance) { return false; } // SimQueue.TryReplacementInteraction
        }

        public class SrfPlayHook
        {
            public void PlayHook(SimQueue.WaitBehavior behavior, IGameObject target, Sim sim)
            {
                if (LswSkipRouteFail(sim)) return;  // docs/686 §25 (LineSwitch.cs): the Sim walks to the free twin it was just given
                if (sim != null && sim == sSrfSim)
                {
                    bool sameTick = sSrfTick == SimClock.CurrentTicks;
                    sSrfSim = null;
                    try
                    {
                        if (SrfSkip(true, true, sameTick, (behavior & SimQueue.WaitBehavior.DontPlayRouteFail) != 0))
                        {
                            if (ShortcutOn("subroutefail")) { PatchCounters()[2763]++; return; }
                            PatchCounters()[2764]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2765]++; }
                }
                PlayOrig(behavior, target, sim);
            }
            public void PlayOrig(SimQueue.WaitBehavior behavior, IGameObject target, Sim sim) { } // SimQueue.PlayRouteFail
        }

        public static object SubRouteFailCheck(string what)
        {
            string counters = B582Counters("subroutefail", 2763, new[] { "route fails skipped (replacement pushed)", "would be skipped (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "subroutefail <run|result>";
            bool on = ShortcutOn("subroutefail");
            bool fixedNow = on && SrfSkip(true, true, true, false);
            bool controls = !SrfSkip(false, true, true, false) && !SrfSkip(true, false, true, false) && !SrfSkip(true, true, false, false) && !SrfSkip(true, true, true, true);
            return B582Line("subroutefail", on, "no route fail after the substitute was pushed", fixedNow,
                "no push / other Sim / later tick / DontPlayRouteFail left to EA", controls, "", counters);
        }

        // ---- petwakebed ----

        static readonly FieldInfo sPwBed = typeof(Sim.WakeUpSim).GetField("mBed", kInstAll);

        static void ApplyPetWakeBed()
        {
            Patch(typeof(Sim.WakeUpSim), "Cleanup", typeof(PwCleanupHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: clear this part? It still holds the pet and the pet is not on the bed (a pet on the bed owns its part).
        internal static bool PwClear(bool holdsPet, bool petOnBed)
        {
            return holdsPet && !petOnBed;
        }

        public class PwCleanupHook
        {
            public void CleanupHook()
            {
                Sim actor = null;
                IBed bed = null;
                try
                {
                    Sim.WakeUpSim me = (Sim.WakeUpSim)(object)this;
                    actor = me.Actor;
                    bed = sPwBed != null ? sPwBed.GetValue(me) as IBed : null;
                }
                catch (Exception) { PatchCounters()[2169]++; }
                CleanupOrig();
                PwAfter(actor, bed);
            }
            public void CleanupOrig() { } // Sim.WakeUpSim.Cleanup
        }

        static void PwAfter(Sim actor, IBed bed)
        {
            if (actor == null || bed == null) return;
            try
            {
                if (bed.HasBeenDestroyed || bed.PartComponent == null) return;
                bool onBed = actor.Posture != null && actor.Posture.Container == bed;
                bool on = ShortcutOn("petwakebed");
                foreach (PartData p in bed.PartComponent.PartDataList.Values)
                {
                    if (p == null || !PwClear(p.ContainedSim == actor, onBed)) continue;
                    if (!on) { PatchCounters()[2168]++; continue; }
                    p.SetContainedSim(null, bed.PartComponent);
                    PatchCounters()[2167]++;
                }
            }
            catch (Exception) { PatchCounters()[2169]++; }
        }

        public static object PetWakeBedCheck(string what)
        {
            string miss = sPwBed == null ? "Sim.WakeUpSim.mBed" : "";
            string counters = B582Counters("petwakebed", 2167, new[] { "bed parts cleared", "parts left holding the pet (switch off)", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "petwakebed <run|result>";
            bool on = ShortcutOn("petwakebed");
            bool fixedNow = on && PwClear(true, false);
            bool controls = !PwClear(false, false) && !PwClear(true, true) && !PwClear(false, true);
            return B582Line("petwakebed", on, "reset pet's bed side cleared at Cleanup", fixedNow,
                "other Sim's part / pet still on the bed left alone", controls, miss, counters);
        }

        // ---- slavewakereason ----

        static readonly FieldInfo sSbSpooning = typeof(BedSleep).GetField("mSpooning", kInstAll);
        static readonly FieldInfo sSbMasterFlag = typeof(BedSleep).GetField("mSpooningMaster", kInstAll);
        static readonly FieldInfo sSbMaster = typeof(BedSleep).GetField("mSpooningMasterInteraction", kInstAll);
        static readonly FieldInfo sSbCancel = typeof(BedSleep).GetField("mSpooningCancel", kInstAll);
        const int kSbMaxPasses = 6000;  // 30000 ticks: hard ceiling only (EA's loop has none)

        static string SbMissing()
        {
            string m = "";
            if (sSbSpooning == null || sSbSpooning.FieldType != typeof(bool)) m += " BedSleep.mSpooning";
            if (sSbMasterFlag == null || sSbMasterFlag.FieldType != typeof(bool)) m += " BedSleep.mSpooningMaster";
            if (sSbMaster == null || sSbMaster.FieldType != typeof(BedSleep)) m += " BedSleep.mSpooningMasterInteraction";
            if (sSbCancel == null || sSbCancel.FieldType != typeof(bool)) m += " BedSleep.mSpooningCancel";
            return m.Trim();
        }

        static void ApplySlaveWakeReason()
        {
            if (SbMissing().Length > 0) { AppliedLog().Add("slavewakereason: " + SbMissing()); return; }
            Patch(typeof(Sim), "IsAtSynchronizationLevelWith", typeof(SbSyncHook), "SyncHook", "SyncOrig");
        }

        // Pure: the reason Run adds back. EA: the last pass's read; fixed: the first pass's read plus later arrivals.
        internal static ExitReason SbEaReason(ExitReason[] reads)
        {
            return reads.Length == 0 ? ExitReason.None : reads[reads.Length - 1];
        }
        internal static ExitReason SbFixReason(ExitReason[] reads)
        {
            ExitReason r = ExitReason.None;
            for (int i = 0; i < reads.Length; i++) r |= reads[i];
            return r;
        }

        // The slave's post-sleep wait: its current BedSleep is a spooning slave whose master was told to cancel.
        static BedSleep SbSlaveWait(Sim me, Sim targetSim)
        {
            BedSleep b = me.CurrentInteraction as BedSleep;
            if (b == null || !(bool)sSbSpooning.GetValue(b) || (bool)sSbMasterFlag.GetValue(b)) return null;
            BedSleep m = sSbMaster.GetValue(b) as BedSleep;
            if (m == null || m.Actor != targetSim || !(bool)sSbCancel.GetValue(m)) return null;
            return b;
        }

        public class SbSyncHook
        {
            public bool SyncHook(Sim targetSim, Sim.SyncLevel desiredSynchLevel)
            {
                Sim me = (Sim)(object)this;
                if (desiredSynchLevel != Sim.SyncLevel.Completed || targetSim == null || me.IsSleeping) return SyncOrig(targetSim, desiredSynchLevel);
                BedSleep b = null;
                bool on = false;
                try
                {
                    b = SbSlaveWait(me, targetSim);
                    if (b != null)
                    {
                        on = ShortcutOn("slavewakereason");
                        if (!on) PatchCounters()[2172]++;
                    }
                }
                catch (Exception) { PatchCounters()[2173]++; b = null; }
                if (b == null || !on) return SyncOrig(targetSim, desiredSynchLevel);
                // EA's loop, entered after its first read and clear; reasons arriving during the wait are kept.
                PatchCounters()[2170]++;
                ExitReason acc = ExitReason.None;
                int pass = 0;
                while (!SyncOrig(targetSim, desiredSynchLevel))
                {
                    Simulator.Sleep(5u);
                    BedSleep m = sSbMaster.GetValue(b) as BedSleep;
                    if (m == null || m.Actor == null || m.Actor.InteractionQueue == null || !m.Actor.InteractionQueue.HasInteraction(m)) break;
                    targetSim = m.Actor;
                    if (pass == 0) PatchCounters()[2171]++;
                    acc |= me.ExitReason;
                    me.ClearExitReasons();
                    if (++pass >= kSbMaxPasses) { PatchCounters()[2173]++; break; }
                }
                acc |= me.ExitReason;  // arrivals during the last sleep (EA's last read)
                me.ClearExitReasons();
                if (acc != ExitReason.None) me.AddExitReason(acc);
                return true;
            }
            public bool SyncOrig(Sim targetSim, Sim.SyncLevel desiredSynchLevel) { return false; } // Sim.IsAtSynchronizationLevelWith
        }

        public static object SlaveWakeReasonCheck(string what)
        {
            string miss = SbMissing();
            string counters = B582Counters("slavewakereason", 2170, new[] { "slave waits handled", "multi-pass waits (EA loses the reason)", "slave waits seen (switch off)", "errors / ceiling" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "slavewakereason <run|result>";
            bool on = ShortcutOn("slavewakereason");
            ExitReason[] two = { ExitReason.HigherPriorityNext, ExitReason.None };
            bool fixedNow = on && SbEaReason(two) == ExitReason.None && SbFixReason(two) == ExitReason.HigherPriorityNext;
            ExitReason[] one = { ExitReason.Finished };
            ExitReason[] late = { ExitReason.None, ExitReason.UserCanceled };
            bool controls = SbFixReason(one) == SbEaReason(one) && SbFixReason(new ExitReason[0]) == ExitReason.None
                && (SbFixReason(late) & ExitReason.UserCanceled) != 0;
            return B582Line("slavewakereason", on, "HigherPriorityNext kept over a two-pass wait", fixedNow,
                "one-pass wait unchanged, late reasons kept", controls, miss, counters);
        }
    }
}
