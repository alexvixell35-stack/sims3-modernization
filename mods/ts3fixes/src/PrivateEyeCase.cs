using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Opportunities;

namespace Ts3Fixes
{
    // privateeyecase (tuning kPrivateEyeCase, off by default; docs/562 §5). EA bug fixes in the Investigator (PrivateEye)
    // case interactions, exact for every Sim [all verified, decompiled PrivateEyeInteractions]:
    //
    // (a) LookThroughMail.Run reads GetActiveOpportunity(Career) without a null check, then
    //     `mailboxDoor = sim == null ? Target.GetAnyDoor() : Target.GetDoorOfSim(sim)` and `mailboxDoor.Slot` (EA
    //     null-checks the door only later). MailboxWall.GetDoorOfSim (apartment wall mailboxes) returns null when the case
    //     Sim's household has no assigned door there and the mailbox is not on that Sim's home lot (case Sim moved out,
    //     or its home is another building), and throws when the Sim has no household. Fix: no active career case -> the
    //     interaction fails before anything happens; while this Run is in progress, a GetDoorOfSim on this mailbox for
    //     the case Sim that would be null (or would throw on a null household) returns EA's own no-Sim choice,
    //     GetAnyDoor(). A wall mailbox with no doors at all fails the interaction cleanly.
    // (b) SearchThroughGarbage.Run, after the 30-minute search succeeds, reads
    //     `GetActiveOpportunity(Career).TargetObject` without a null check; a case that timed out or was cancelled during
    //     the search throws there (after StandardExit), so the Sim resets. Fix: that NullReferenceException, with the
    //     search succeeded and no active career opportunity left, is caught and Run returns EA's result (true); the
    //     blackmail tail that needs the case is skipped, no reward is involved (the dead case has no listeners).
    // (c) SnoopForClues.Run adds the "snoop finished" alarm (AlwaysPersisted, kTimeToSnoop x level multiplier, up to two
    //     hours) without storing its handle, so RemoveAlarm (Cleanup / loop end) never removes it. When the snoop ends
    //     first (loop lifetime reached, or cancelled by the player), the alarm still fires later and adds
    //     ExitReason.Finished to whatever the investigator is doing then. Fix: SnoopAlarmDelegate does nothing unless
    //     that SnoopForClues is still the Sim's current interaction.
    // (d) BreakIntoHouse.Run sets the picked door to tLock.Nobody and Cleanup sets it to tLock.Anybody, whatever it was:
    //     a front door the target household had locked (household only, owner list, one Sim) is left unlocked for good.
    //     Fix: the door's lock (type, owner, one-way room) is read before Run (EA picks the door deterministically:
    //     FindApartmentDoor / FindFrontDoor) and put back after Cleanup when Cleanup reset that door.
    // (e) Stakeout.InitiateCrimeToWitness (a stakeout whose result is WitnessCrime, chosen at random or required by the
    //     case's StakeoutInteractionCompleteListener): with a required Sim id it does
    //     `SimDescription.Find(id).IsValidDescription` with no null check, so a case whose required Sim was deleted
    //     (died and culled, moved out of town) throws in Run, every time the investigator tries. When it finds no
    //     candidate Sims at all it returns false without setting mResultType = Fail (EA sets Fail only when candidates
    //     existed but the crime could not be pushed), and WaitForCrimeLoop then dereferences the null Sketchy on its
    //     first tick (the Sketchy == null exit-reason test does not return). Fix: a required id with no SimDescription is
    //     treated as EA treats a required Sim that is not instantiated (the occupation candidates are used): the dead id
    //     is cleared for good (no SimDescription means nothing can ever match it again, so EA's fallback is the only
    //     outcome either way; no restore, so no window for a save or another writer). An id whose SimDescription
    //     exists (Sim merely not instantiated) is left untouched: EA handles it. No crime pushed and no Sketchy -> Fail, EA's
    //     own result for "the crime could not be set up".
    // Counters: [2499] mail fixed (no case / door fallback), [2500] garbage dead-case exits, [2501] stale snoop alarms
    // ignored, [2502] door locks restored, [2503] errors, [2504] stakeouts fixed (deleted required Sim / no crime). Check: PrivateEyeCaseChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kPeInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type sPeMail = typeof(PrivateEyeInteractions).GetNestedType("LookThroughMail", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sPeGarbageOk = typeof(PrivateEyeInteractions.SearchThroughGarbage).GetField("success", kPeInst);
        static readonly FieldInfo sPeDoor = typeof(PrivateEyeInteractions.BreakIntoHouse).GetField("mDoor", kPeInst);
        static readonly FieldInfo sPeResult = typeof(PrivateEyeInteractions.Stakeout).GetField("mResultType", kPeInst);

        // (a) mailbox -> case Sim whose door lookup gets the fallback while that LookThroughMail.Run runs
        static readonly Dictionary<Mailbox, Sim> sPeMailFallback = new Dictionary<Mailbox, Sim>();
        // (d) BreakIntoHouse instance -> lock read before its Run
        class PeLock { public Door Door; public CommonDoor.tLock Type; public SimDescription Owner; public int OneWay; }
        static readonly Dictionary<object, PeLock> sPeLocks = new Dictionary<object, PeLock>();

        static void ApplyPrivateEyeCase()
        {
            if (sPeMail != null)
            {
                Patch(sPeMail, "Run", typeof(PeMailHook), "RunHook", "RunOrig");
                Patch(typeof(MailboxWall), "GetDoorOfSim", typeof(PeMailboxHook), "DoorHook", "DoorOrig");
            }
            else AppliedLog().Add("privateeyecase: LookThroughMail not found, mail part not patched");
            if (sPeGarbageOk != null) Patch(typeof(PrivateEyeInteractions.SearchThroughGarbage), "Run", typeof(PeGarbageHook), "RunHook", "RunOrig");
            else AppliedLog().Add("privateeyecase: SearchThroughGarbage.success not found, garbage part not patched");
            Patch(typeof(PrivateEyeInteractions.SnoopForClues), "SnoopAlarmDelegate", typeof(PeSnoopHook), "AlarmHook", "AlarmOrig");
            if (sPeDoor != null)
            {
                Patch(typeof(PrivateEyeInteractions.BreakIntoHouse), "Run", typeof(PeBreakHook), "RunHook", "RunOrig");
                Patch(typeof(PrivateEyeInteractions.BreakIntoHouse), "Cleanup", typeof(PeBreakHook), "CleanupHook", "CleanupOrig");
            }
            else AppliedLog().Add("privateeyecase: BreakIntoHouse.mDoor not found, lock part not patched");
            if (sPeResult != null) Patch(typeof(PrivateEyeInteractions.Stakeout), "InitiateCrimeToWitness", typeof(PeStakeoutHook), "CrimeHook", "CrimeOrig");
            else AppliedLog().Add("privateeyecase: Stakeout.mResultType not found, stakeout part not patched");
        }

        // ---- decisions (also used by the check) ----
        // (a) 0 = EA's path, 1 = fail before anything (no case / no door at all), 2 = door fallback to GetAnyDoor
        internal static int PeMailAction(bool on, bool hasCase, bool caseSim, bool doorNull, bool anyDoor)
        {
            if (!on) return 0;
            if (!hasCase) return 1;
            if (!caseSim || !doorNull) return 0;
            return anyDoor ? 2 : 1;
        }
        // (b) swallow EA's NullReferenceException: the search succeeded and the case is gone
        internal static bool PeGarbageSwallow(bool on, bool searchOk, bool hasCase)
        {
            return on && searchOk && !hasCase;
        }
        // (c) the snoop alarm acts only on the snoop that set it, while it runs
        internal static bool PeSnoopAct(bool on, bool snoopIsCurrent)
        {
            return !on || snoopIsCurrent;
        }
        // (d) put the lock back: Cleanup reset the door it locked, which was not open to anybody before
        internal static bool PeRestoreLock(bool on, bool sameDoor, bool wasAnybody)
        {
            return on && sameDoor && !wasAnybody;
        }

        // (e) a required Sim id whose SimDescription is gone (EA's Find(...).IsValidDescription throws)
        internal static bool PeStakeoutClearId(bool on, bool idSet, bool descFound)
        {
            return on && idSet && !descFound;
        }
        // (e) no crime pushed, no Sketchy, result still WitnessCrime (WaitForCrimeLoop would dereference null)
        internal static bool PeStakeoutFail(bool on, bool pushed, bool hasSketchy, bool witness)
        {
            return on && !pushed && !hasSketchy && witness;
        }

        static Opportunity PeCase(Sim s)
        {
            return s == null || s.OpportunityManager == null ? null : s.OpportunityManager.GetActiveOpportunity(OpportunityCategory.Career);
        }

        // (a)
        public class PeMailHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("privateeyecase")) return RunOrig();  // fast path: switch off
                Mailbox box = null;
                bool marked = false;
                try
                {
                    InteractionInstance ia = (object)this as InteractionInstance;
                    Sim actor = ia == null ? null : ia.InstanceActor;
                    box = ia == null ? null : ia.Target as Mailbox;
                    Opportunity opp = PeCase(actor);
                    Sim caseSim = opp == null ? null : opp.TargetObject as Sim;
                    bool anyDoor = box != null && box.GetDoors().Length > 0;
                    // doorNull here means "may be null": the hook below decides per call; a wall mailbox is the only override
                    int act = PeMailAction(true, opp != null, caseSim != null, box is MailboxWall, anyDoor);
                    if (act == 1) { PatchCounters()[2499]++; return false; }
                    if (act == 2) { lock (sPeMailFallback) sPeMailFallback[box] = caseSim; marked = true; }
                }
                catch (Exception) { PatchCounters()[2503]++; }
                try { return RunOrig(); }
                finally { if (marked) lock (sPeMailFallback) sPeMailFallback.Remove(box); }
            }
            public bool RunOrig() { return false; } // receives PrivateEyeInteractions.LookThroughMail.Run's original body
        }

        public class PeMailboxHook
        {
            public MailboxDoor DoorHook(Sim sim)
            {
                Sim marked = null;
                MailboxWall box = (object)this as MailboxWall;
                if (sim != null && sPeMailFallback.Count > 0)
                    lock (sPeMailFallback) { sPeMailFallback.TryGetValue(box, out marked); }
                if (marked == null || !ReferenceEquals(marked, sim)) return DoorOrig(sim);  // fast path: not a marked lookup
                MailboxDoor d = null;
                if (sim.Household != null) d = DoorOrig(sim);
                if (d != null) return d;
                PatchCounters()[2499]++;
                return box.GetAnyDoor();
            }
            public MailboxDoor DoorOrig(Sim sim) { return null; } // receives MailboxWall.GetDoorOfSim's original body
        }

        // (b)
        public class PeGarbageHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                catch (NullReferenceException)
                {
                    bool swallow = false;
                    try
                    {
                        InteractionInstance ia = (object)this as InteractionInstance;
                        swallow = ia != null && PeGarbageSwallow(ShortcutOn("privateeyecase"), (bool)sPeGarbageOk.GetValue(ia), PeCase(ia.InstanceActor) == null);
                    }
                    catch (Exception) { PatchCounters()[2503]++; }
                    if (!swallow) throw;
                    PatchCounters()[2500]++;
                    return true;
                }
            }
            public bool RunOrig() { return false; } // receives PrivateEyeInteractions.SearchThroughGarbage.Run's original body
        }

        // (c)
        public class PeSnoopHook
        {
            public void AlarmHook()
            {
                bool act = true;
                try
                {
                    InteractionInstance ia = (object)this as InteractionInstance;
                    Sim actor = ia == null ? null : ia.InstanceActor;
                    act = PeSnoopAct(ShortcutOn("privateeyecase"), actor != null && ReferenceEquals(actor.CurrentInteraction, ia));
                }
                catch (Exception) { PatchCounters()[2503]++; act = true; }
                if (act) { AlarmOrig(); return; }
                PatchCounters()[2501]++;
            }
            public void AlarmOrig() { } // receives PrivateEyeInteractions.SnoopForClues.SnoopAlarmDelegate's original body
        }

        // (e)
        public class PeStakeoutHook
        {
            public bool CrimeHook(StakeoutInteractionCompleteListener listener)
            {
                if (!ShortcutOn("privateeyecase")) return CrimeOrig(listener);  // fast path: switch off
                bool cleared = false;
                try
                {
                    if (listener != null && PeStakeoutClearId(true, listener.mRequiredSimID != 0, listener.mRequiredSimID == 0 || SimDescription.Find(listener.mRequiredSimID) != null))
                    {
                        listener.mRequiredSimID = 0;  // the Sim is gone for good: permanent, no restore
                        cleared = true;
                    }
                }
                catch (Exception) { PatchCounters()[2503]++; }
                bool r = CrimeOrig(listener);
                bool fixedIt = cleared;
                try
                {
                    PrivateEyeInteractions.Stakeout st = (object)this as PrivateEyeInteractions.Stakeout;
                    if (st != null && PeStakeoutFail(true, r, st.Sketchy != null,
                        (PrivateEyeInteractions.StakeoutResult)sPeResult.GetValue(st) == PrivateEyeInteractions.StakeoutResult.WitnessCrime))
                    {
                        sPeResult.SetValue(st, PrivateEyeInteractions.StakeoutResult.Fail);
                        fixedIt = true;
                    }
                }
                catch (Exception) { PatchCounters()[2503]++; }
                if (fixedIt) PatchCounters()[2504]++;
                return r;
            }
            public bool CrimeOrig(StakeoutInteractionCompleteListener listener) { return false; } // receives Stakeout.InitiateCrimeToWitness's original body
        }

        // (d)
        public class PeBreakHook
        {
            public bool RunHook()
            {
                if (ShortcutOn("privateeyecase"))
                {
                    try
                    {
                        PrivateEyeInteractions.BreakIntoHouse ia = (object)this as PrivateEyeInteractions.BreakIntoHouse;
                        Lot lot = ia == null ? null : ia.Target;
                        Door d = lot == null ? null : (lot.HasVirtualResidentialSlots ? lot.FindApartmentDoor() : lot.FindFrontDoor());
                        if (d != null)
                        {
                            PeLock l = new PeLock { Door = d, Type = d.LockType, Owner = d.LockOwner, OneWay = d.OneWayRoomDestinationId };
                            lock (sPeLocks) sPeLocks[this] = l;
                        }
                    }
                    catch (Exception) { PatchCounters()[2503]++; }
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives PrivateEyeInteractions.BreakIntoHouse.Run's original body

            public void CleanupHook()
            {
                PeLock l = null;
                lock (sPeLocks) { if (sPeLocks.TryGetValue(this, out l)) sPeLocks.Remove(this); }
                Door used = null;
                if (l != null) { try { used = sPeDoor.GetValue((object)this) as Door; } catch (Exception) { PatchCounters()[2503]++; } }
                CleanupOrig();
                if (l == null || !PeRestoreLock(ShortcutOn("privateeyecase"), used != null && ReferenceEquals(used, l.Door), l.Type == CommonDoor.tLock.Anybody)) return;
                try
                {
                    if (l.Door.HasBeenDestroyed) return;
                    l.Door.SetLockTypeAndOwner(l.Type, l.Owner, l.OneWay);
                    PatchCounters()[2502]++;
                }
                catch (Exception) { PatchCounters()[2503]++; }
            }
            public void CleanupOrig() { } // receives PrivateEyeInteractions.BreakIntoHouse.Cleanup's original body
        }
    }
}
