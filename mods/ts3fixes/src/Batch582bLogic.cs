using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.ChildAndTeenUpdates;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/582 batch 4 (REA tranche 2b, docs/584 §5): door locks, elevators, StarLight, listener leaks. Every switch off by
    // default. simler90 core UPDATE242 was checked first (docs/582 "simler90"): it only null-guards CommonDoor.IsAllowedThrough
    // and touches none of the other methods here. Checks: Batch582bChecks.cs.
    public static partial class Fixes
    {
        // ---- B4 §1 doorlockowner (tuning kDoorLockOwner) -------------------------------------------------------------------
        // [verified, decompiled] CommonDoor keeps mLockOwner (SelectedActor / SelectedHousehold locks) after that Sim died or
        // moved out, and the OwnerList cleanup (HouseholdSimChanged on the lot household's HouseholdSimsChanged) is
        // subscribed only by SetLockTypeOwnerList: nothing subscribes it again after a load, it misses Sims removed while not
        // instantiated (Household.Remove fires the event only when CreatedSim != null), and a household moving into the lot
        // inherits every lock. Result: a door locked to a gone Sim admits nobody (SelectedActor), or only the gone Sim's new
        // household (SelectedHousehold); Sims behind it are trapped; NPCs never re-lock. Fix at the source: after
        // Household.Remove (death, move-out, deletion) and after Lot.MoveIn (a household moving in), the lot's doors are
        // re-checked against the lot's current household: a SelectedActor lock whose owner is not a resident opens
        // (Anybody); a SelectedHousehold lock is re-pointed to a resident (still "household only"), or opens when nobody
        // lives there; OwnerList drops non-residents and opens only when that removal emptied it. Nobody, Pets, VIPRoom and
        // a player-chosen empty owner list are never touched. At world load (saved games only): EA's own
        // SetHouseholdCallback is called again for OwnerList doors, and the same check runs once for owners already gone.
        // Household-visible: a door locked to a gone Sim opens. Counters: [2834] locks fixed at removal / move-in,
        // [2835] the same seen with the switch off, [2836] saved stale locks fixed at load, [2837] owner-list callbacks
        // re-registered at load, [2838] errors.
        static readonly FieldInfo sDlOwnerList = typeof(CommonDoor).GetField("mOwnerList", kInstAll);
        static readonly FieldInfo sDlOneWay = typeof(CommonDoor).GetField("mOneWayDestinationRoomId", kInstAll);
        static readonly MethodInfo sDlCallback = typeof(CommonDoor).GetMethod("SetHouseholdCallback", kInstAll, null, Type.EmptyTypes, null);
        static string sDlMissing = "";

        internal const int kDlKeep = 0, kDlAnybody = 1, kDlRepoint = 2, kDlPrune = 3;

        static void ApplyDoorLockOwner()
        {
            sDlMissing = ((sDlOwnerList == null ? "CommonDoor.mOwnerList " : "") + (sDlOneWay == null ? "CommonDoor.mOneWayDestinationRoomId " : "")
                + (sDlCallback == null ? "CommonDoor.SetHouseholdCallback " : "")
                + (typeof(Household).GetMethod("Remove", kInstAll, null, new Type[] { typeof(SimDescription), typeof(bool) }, null) == null ? "Household.Remove(SimDescription,bool) " : "")
                + (typeof(Lot).GetMethod("MoveIn", kInstAll, null, new Type[] { typeof(Household), typeof(bool) }, null) == null ? "Lot.MoveIn(Household,bool)" : "")).Trim();
            if (sDlMissing.Length > 0) { AppliedLog().Add("doorlockowner: not found: " + sDlMissing); return; }
            Patch(typeof(Household), "Remove", new Type[] { typeof(SimDescription), typeof(bool) }, typeof(BDlHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(Lot), "MoveIn", new Type[] { typeof(Household), typeof(bool) }, typeof(BDlHook), "MoveInHook", "MoveInOrig");
        }

        // Pure: what happens to one lock. ownerResident: the lock owner lives on the door's lot; residents: somebody does;
        // listCount / listGone: owner-list size and how many of its ids are not residents.
        internal static int DlDecide(CommonDoor.tLock type, bool ownerResident, bool residents, int listCount, int listGone)
        {
            switch (type)
            {
                case CommonDoor.tLock.SelectedActor: return ownerResident ? kDlKeep : kDlAnybody;
                case CommonDoor.tLock.SelectedHousehold: return ownerResident ? kDlKeep : (residents ? kDlRepoint : kDlAnybody);
                case CommonDoor.tLock.OwnerList: return listGone <= 0 ? kDlKeep : (listGone >= listCount ? kDlAnybody : kDlPrune);
                default: return kDlKeep;
            }
        }

        // One lot's doors against its current household. Returns the number of locks changed (or that would change: apply false).
        static int DlLot(Lot lot, bool apply)
        {
            if (lot == null || lot.IsWorldLot) return 0;
            Household h = lot.Household;
            Dictionary<ulong, SimDescription> res = new Dictionary<ulong, SimDescription>();
            SimDescription first = null;
            if (h != null && h.AllSimDescriptions != null)
                foreach (SimDescription d in h.AllSimDescriptions)
                {
                    if (d == null || res.ContainsKey(d.SimDescriptionId)) continue;
                    res[d.SimDescriptionId] = d;
                    if (first == null && d.IsHuman) first = d;
                }
            if (first == null) foreach (SimDescription d in res.Values) { first = d; break; }
            int n = 0;
            foreach (CommonDoor door in lot.GetObjects<CommonDoor>())
            {
                CommonDoor.tLock t = door.LockType;
                if (t != CommonDoor.tLock.SelectedActor && t != CommonDoor.tLock.SelectedHousehold && t != CommonDoor.tLock.OwnerList) continue;
                SimDescription owner = door.LockOwner;
                List<ulong> list = sDlOwnerList.GetValue(door) as List<ulong>;
                int gone = 0;
                if (t == CommonDoor.tLock.OwnerList && list != null) foreach (ulong id in list) if (!res.ContainsKey(id)) gone++;
                int what = DlDecide(t, owner != null && res.ContainsKey(owner.SimDescriptionId), res.Count > 0, list == null ? 0 : list.Count, gone);
                if (what == kDlKeep) continue;
                n++;
                if (!apply) continue;
                int oneWay = (int)sDlOneWay.GetValue(door);
                if (what == kDlAnybody) door.SetLockTypeAndOwner(CommonDoor.tLock.Anybody, null, oneWay);
                else if (what == kDlRepoint) door.SetLockTypeAndOwner(CommonDoor.tLock.SelectedHousehold, first, oneWay);
                else
                {
                    list.RemoveAll(delegate (ulong id) { return !res.ContainsKey(id); });
                    door.SetLockTypeAndOwner(CommonDoor.tLock.OwnerList, owner, oneWay);  // EA's own refresh: routing restriction + lock state
                }
            }
            return n;
        }

        // Owner-list doors on the lot that list this Sim (before Household.Remove).
        static List<CommonDoor> DlListsHolding(Lot lot, SimDescription d)
        {
            if (lot == null || lot.IsWorldLot || d == null) return null;
            List<CommonDoor> r = null;
            foreach (CommonDoor door in lot.GetObjects<CommonDoor>())
            {
                if (door.LockType != CommonDoor.tLock.OwnerList) continue;
                List<ulong> list = sDlOwnerList.GetValue(door) as List<ulong>;
                if (list != null && list.Contains(d.SimDescriptionId)) (r ?? (r = new List<CommonDoor>())).Add(door);
            }
            return r;
        }

        // Review 2026-11-15: for an instantiated Sim, EA's CommonDoor.HouseholdSimChanged removes the id during
        // Household.Remove, so the after-check saw no gone owner; a list that this removal emptied opens (Anybody), the case
        // the comment above promises. A list the player emptied is never in `held`.
        static void DlEmptied(List<CommonDoor> held)
        {
            if (held == null) return;
            try
            {
                bool on = ShortcutOn("doorlockowner");
                foreach (CommonDoor door in held)
                {
                    List<ulong> list = sDlOwnerList.GetValue(door) as List<ulong>;
                    if (door.LockType != CommonDoor.tLock.OwnerList || list == null || list.Count > 0) continue;
                    if (!on) { PatchCounters()[2835]++; continue; }
                    door.SetLockTypeAndOwner(CommonDoor.tLock.Anybody, null, (int)sDlOneWay.GetValue(door));
                    PatchCounters()[2834]++;
                }
            }
            catch (Exception) { PatchCounters()[2838]++; }
        }

        static void DlAfter(Lot lot)
        {
            try
            {
                if (lot == null) return;
                if (!ShortcutOn("doorlockowner")) { PatchCounters()[2835] += DlLot(lot, false); return; }
                PatchCounters()[2834] += DlLot(lot, true);
            }
            catch (Exception) { PatchCounters()[2838]++; }
        }

        internal static void DoorLockOwnerWorldLoad()
        {
            if (!ShortcutOn("doorlockowner") || sDlMissing.Length > 0) return;
            int[] c = PatchCounters();
            foreach (Lot lot in LotManager.AllLots)
            {
                try
                {
                    if (lot == null || lot.IsWorldLot) continue;
                    if (lot.Household != null)
                        foreach (CommonDoor door in lot.GetObjects<CommonDoor>())
                            if (door.LockType == CommonDoor.tLock.OwnerList) { sDlCallback.Invoke(door, null); c[2837]++; }  // EA's -= then +=: never doubled
                    c[2836] += DlLot(lot, true);
                }
                catch (Exception) { c[2838]++; }
            }
        }

        public class BDlHook
        {
            public void RemoveHook(SimDescription simDescription, bool deleteHouseholdIfEmpty)
            {
                Lot lot = null;
                List<CommonDoor> held = null;
                try
                {
                    lot = ((Household)(object)this).LotHome;
                    held = DlListsHolding(lot, simDescription);
                }
                catch (Exception) { }
                try { RemoveOrig(simDescription, deleteHouseholdIfEmpty); }
                finally
                {
                    DlEmptied(held);  // EA's own HouseholdSimChanged may already have taken the id out (an instantiated Sim)
                    DlAfter(lot);
                }
            }
            public void RemoveOrig(SimDescription simDescription, bool deleteHouseholdIfEmpty) { } // receives Household.Remove(SimDescription,bool)'s original header
            public void MoveInHook(Household household, bool bOnLoad)
            {
                MoveInOrig(household, bOnLoad);
                if (!bOnLoad) DlAfter((Lot)(object)this);  // at load: DoorLockOwnerWorldLoad, once for all lots
            }
            public void MoveInOrig(Household household, bool bOnLoad) { } // receives Lot.MoveIn(Household,bool)'s original header
        }

        // ---- B4 §2 elevatorkitten (tuning kElevatorKitten) -----------------------------------------------------------------
        // [verified, decompiled] ElevatorDoors.IsAllowedThrough (the route planner's check, Sim.OnPortalPathPlan) refuses
        // toddlers, horses and wild animals; ElevatorDoorsPortalComponent.OnApproachingPortalObject (the ride itself) also
        // refuses kittens and puppies. EA's intent is the stricter one: the ride refuses them explicitly, and no ride path
        // exists for them. So a kitten or puppy plans through the elevator, walks to it, fails at the doors, and replans the
        // same route. Fix: the planner gives the doors' answer (DoorRestriction for kittens and puppies), so the route fails
        // at planning, as it does for toddlers. Household-visible only as a faster failure (the ride never happened).
        // Counters: [2839] kitten/puppy routes refused at planning, [2840] seen with the switch off, [2841] errors.
        static string sEkMissing = "";

        static void ApplyElevatorKitten()
        {
            sEkMissing = typeof(ElevatorDoors).GetMethod("IsAllowedThrough", kInstAll, null, new Type[] { typeof(int), typeof(int), typeof(Sim) }, null) == null ? "ElevatorDoors.IsAllowedThrough(int,int,Sim)" : "";
            if (sEkMissing.Length > 0) { AppliedLog().Add("elevatorkitten: not found: " + sEkMissing); return; }
            Patch(typeof(ElevatorDoors), "IsAllowedThrough", new Type[] { typeof(int), typeof(int), typeof(Sim) }, typeof(BEkHook), "AllowedHook", "AllowedOrig");
        }

        // Pure: refuse at planning what the doors refuse at the ride?
        internal static bool EkRefuse(bool on, bool plannerAllows, bool kittenOrPuppy)
        {
            return on && plannerAllows && kittenOrPuppy;
        }

        public class BEkHook
        {
            public PortalRestriction AllowedHook(int srcRoomId, int destRoomId, Sim actor)
            {
                PortalRestriction r = AllowedOrig(srcRoomId, destRoomId, actor);
                if (r != PortalRestriction.None || actor == null) return r;
                try
                {
                    if (!(actor.IsKitten || actor.IsPuppy)) return r;
                    if (!ShortcutOn("elevatorkitten")) { PatchCounters()[2840]++; return r; }
                    if (EkRefuse(true, true, true)) { PatchCounters()[2839]++; return PortalRestriction.DoorRestriction; }
                }
                catch (Exception) { PatchCounters()[2841]++; }
                return r;
            }
            public PortalRestriction AllowedOrig(int srcRoomId, int destRoomId, Sim actor) { return PortalRestriction.None; } // receives the original
        }

        // ---- B4 §3 elevatorresetriders (tuning kElevatorResetRiders) -------------------------------------------------------
        // [verified, decompiled] ElevatorPortalComponent.OnReset clears mAssignedSims while riders sit in OnPortalStart's wait
        // loop (mbSleeping). Their loop ends only on launch (IsElevatorReadyForLaunch, now false: the elevator is empty) or
        // on EA's 30 sim-minute failsafe, which resets the rider (Sim.SetObjectToReset). So every rider waiting when an
        // elevator resets stands frozen 30 sim-minutes and is then reset ("Late Night elevators freeze Sims"). Fix: the
        // riders that were waiting with a live task get EA's failsafe reset at once, instead of after 30 minutes. Same end
        // state as vanilla, without the frozen half hour; household-visible (it applies to household riders too).
        // Counters: [2842] waiting riders released at elevator reset, [2843] seen with the switch off, [2844] errors.
        static readonly FieldInfo sErAssigned = typeof(ElevatorInterior.ElevatorPortalComponent).GetField("mAssignedSims", kInstAll);
        static FieldInfo sErSleeping, sErTask;
        static string sErMissing = "";

        static void ErMembers()
        {
            Type entry = typeof(ElevatorInterior.ElevatorPortalComponent).GetNestedType("ActiveUserEntry", BindingFlags.Public | BindingFlags.NonPublic);
            sErSleeping = entry == null ? null : entry.GetField("mbSleeping", kInstAll);
            sErTask = entry == null ? null : entry.GetField("mTask", kInstAll);
        }

        static void ApplyElevatorResetRiders()
        {
            ErMembers();
            sErMissing = ((sErAssigned == null ? "ElevatorPortalComponent.mAssignedSims " : "") + (sErSleeping == null ? "ActiveUserEntry.mbSleeping " : "")
                + (sErTask == null ? "ActiveUserEntry.mTask" : "")).Trim();
            if (sErMissing.Length > 0) { AppliedLog().Add("elevatorresetriders: not found: " + sErMissing); return; }
            Patch(typeof(ElevatorInterior.ElevatorPortalComponent), "OnReset", Type.EmptyTypes, typeof(BErHook), "ResetHook", "ResetOrig");
        }

        // Pure: release this rider at the elevator reset?
        internal static bool ErRelease(bool on, bool sleeping, bool liveTask, bool simPresent)
        {
            return on && sleeping && liveTask && simPresent;
        }

        static bool ErLiveTask(object entry)
        {
            ObjectGuid t = (ObjectGuid)sErTask.GetValue(entry);
            return t != ObjectGuid.InvalidObjectGuid && Simulator.GetTask(t) != null;
        }

        // Riders whose entry says "waiting in the loop" with a live task.
        static List<Sim> ErWaiting(ElevatorInterior.ElevatorPortalComponent pc)
        {
            List<Sim> w = new List<Sim>();
            IDictionary d = sErAssigned.GetValue(pc) as IDictionary;
            if (d == null || d.Count == 0) return w;
            foreach (DictionaryEntry e in d)
            {
                SimDescription sd = e.Key as SimDescription;
                Sim s = sd == null ? null : sd.CreatedSim;
                if (ErRelease(true, (bool)sErSleeping.GetValue(e.Value), ErLiveTask(e.Value), s != null && !s.HasBeenDestroyed)) w.Add(s);
            }
            return w;
        }

        public class BErHook
        {
            public void ResetHook()
            {
                List<Sim> w = null;
                try { if (sErMissing.Length == 0) w = ErWaiting((ElevatorInterior.ElevatorPortalComponent)(object)this); }
                catch (Exception) { PatchCounters()[2844]++; }
                ResetOrig();
                if (w == null || w.Count == 0) return;
                try
                {
                    if (!ShortcutOn("elevatorresetriders")) { PatchCounters()[2843] += w.Count; return; }
                    foreach (Sim s in w) { s.SetObjectToReset(); PatchCounters()[2842]++; }  // EA's failsafe action, now instead of in 30 minutes
                }
                catch (Exception) { PatchCounters()[2844]++; }
            }
            public void ResetOrig() { } // receives ElevatorPortalComponent.OnReset's original body
        }

        // ---- B4 §3b elevatorridertimeout (tuning kElevatorRiderTimeout) ----------------------------------------------------
        // [verified, decompiled; REA E2] ElevatorPortalComponent.OnPortalStart keeps ONE failsafe start (mFailsafeTime) for all
        // riders and each new rider overwrites it before its wait loop. Every waiting rider's 30 sim-minute failsafe is then
        // measured from the LAST arrival: on a busy, stuck elevator nobody is released until 30 minutes after the newest rider
        // came, and then all are reset together. Fix (approved: replace the method): with the switch on, OnPortalStart runs our
        // own implementation of EA's ride (same steps, same order, same EA calls: button animation for the front-slot human,
        // full-elevator timeout, launch by the last sleeper, use list, leave effects, hand-over to the next floor's slot), the
        // only difference being that the failsafe test uses this rider's own start time. EA still gets mFailsafeTime written
        // (no other reader). With the switch off, EA's method runs unchanged. Household-visible: a stuck household rider is
        // released at its own 30 minutes. Counters: [2858] rides run by the replacement, [2859] failsafe resets (own 30
        // minutes), [2860] errors before the ride started (EA's method ran instead).
        static FieldInfo sE2DoorOpen, sE2Ready, sE2Required, sE2Failsafe, sE2Slot, sE2GoingUp;
        static MethodInfo sE2Timeout;
        static Slot[] sE2Contain;
        static Slot sE2Magic;
        static string sE2Missing = "";

        static void ApplyElevatorRiderTimeout()
        {
            if (sErSleeping == null) ErMembers();
            Type pc = typeof(ElevatorInterior.ElevatorPortalComponent), entry = pc.GetNestedType("ActiveUserEntry", BindingFlags.Public | BindingFlags.NonPublic);
            sE2DoorOpen = pc.GetField("mbDoorWasOpenWhenLaunched", kInstAll); sE2Ready = pc.GetField("mbElevatorReadyForLaunch", kInstAll);
            sE2Required = pc.GetField("mbRequiredTimeElapsedBeforeLaunch", kInstAll); sE2Failsafe = pc.GetField("mFailsafeTime", kInstAll);
            sE2Timeout = pc.GetMethod("OnSimApproachTimeout", kInstAll, null, Type.EmptyTypes, null);
            sE2Slot = entry == null ? null : entry.GetField("mSlotAssignment", kInstAll);
            sE2GoingUp = entry == null ? null : entry.GetField("mbGoingUp", kInstAll);
            FieldInfo contain = typeof(ElevatorInterior).GetField("kSimContainmentSlots", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo magic = typeof(ElevatorInterior).GetField("kSimMagicSlot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            sE2Contain = contain == null ? null : contain.GetValue(null) as Slot[];
            if (magic != null) sE2Magic = (Slot)magic.GetValue(null);
            sE2Missing = ((sErAssigned == null || sErSleeping == null || sErTask == null ? "ElevatorPortalComponent.mAssignedSims/ActiveUserEntry " : "")
                + (sE2DoorOpen == null || sE2Ready == null || sE2Required == null || sE2Failsafe == null ? "ElevatorPortalComponent launch fields " : "")
                + (sE2Timeout == null ? "OnSimApproachTimeout " : "") + (sE2Slot == null || sE2GoingUp == null ? "ActiveUserEntry.mSlotAssignment/mbGoingUp " : "")
                + (sE2Contain == null || sE2Contain.Length == 0 || magic == null ? "ElevatorInterior slots" : "")).Trim();
            if (sE2Missing.Length > 0) { AppliedLog().Add("elevatorridertimeout: not found: " + sE2Missing); return; }
            Patch(typeof(ElevatorInterior.ElevatorPortalComponent), "OnPortalStart", new Type[] { typeof(Sim) }, typeof(BE2Hook), "StartHook", "StartOrig");
        }

        // Pure: does this rider's failsafe fire? own = minutes since this rider started waiting, shared = since the newest one.
        internal static bool E2Fire(bool on, float own, float shared)
        {
            return (on ? own : shared) >= 30f;
        }

        static bool E2Bool(FieldInfo f, object o) { return (bool)f.GetValue(o); }

        // EA's ride with a per-rider failsafe start. entry: this rider's ActiveUserEntry.
        static bool E2Ride(ElevatorInterior.ElevatorPortalComponent pc, Sim s, object entry)
        {
            ElevatorInterior owner = pc.Owner as ElevatorInterior;
            ElevatorDoors doors = owner.DoorsObj;
            Slot slot = (Slot)sE2Slot.GetValue(entry);
            sE2DoorOpen.SetValue(pc, false);
            sErTask.SetValue(entry, Simulator.CurrentTask);
            if (doors.mIsOpen && slot == Slot.RoutingSlot_0)
            {
                s.SetForward(owner.GetForwardOfSlot(slot));
                if (s.IsHuman)
                {
                    StateMachineClient smc = StateMachineClient.Acquire(s.ObjectId, "elevator", AnimationPriority.kAPDefault, false);
                    smc.SetActor("x", s);
                    smc.SetActor("elevatorInterior", owner);
                    smc.EnterState("x", "Enter");
                    smc.RequestState("x", "PushButton");
                    doors.StartButtonEffect(pc.IsSimGoingUp(s));
                    smc.RequestState("x", "Exit");
                }
            }
            else if (slot == Slot.RoutingSlot_1) s.SetForward(owner.GetForwardOfSlot(slot));
            s.LoopIdle();
            if (pc.IsElevatorFull()) sE2Timeout.Invoke(pc, null);
            DateAndTime mine = SimClock.CurrentTime();
            sE2Failsafe.SetValue(pc, mine);  // EA's shared field, still written
            bool launchedByMe = false;
            sErSleeping.SetValue(entry, true);
            PatchCounters()[2858]++;
            while (!E2Bool(sE2Ready, pc))
            {
                if (E2Bool(sE2Required, pc) && pc.IsElevatorReadyForLaunch())
                {
                    sE2Ready.SetValue(pc, true);
                    sE2Required.SetValue(pc, false);
                    owner.AddToUseList(s);
                    launchedByMe = true;
                    if (doors.mIsOpen)
                    {
                        sE2DoorOpen.SetValue(pc, true);
                        owner.StartOneShotFunction(pc.TriggerLeaveEffect, GameObject.OneShotFunctionDisposeFlag.OnReset);
                    }
                    Simulator.Sleep(0u);
                    sE2Ready.SetValue(pc, false);
                    break;
                }
                if (E2Fire(true, SimClock.ElapsedTime(TimeUnit.Minutes, mine), 0f))
                {
                    PatchCounters()[2859]++;
                    sErSleeping.SetValue(entry, false);  // batch 9 tidy: EA's failsafe return left the entry asleep with its task
                    sErTask.SetValue(entry, Simulator.kBadObjectGuid);
                    s.SetObjectToReset();
                    Simulator.Sleep(0u);
                    return false;
                }
                Simulator.Sleep(0u);
            }
            if (!launchedByMe) owner.AddToUseList(s);
            if (E2Bool(sE2DoorOpen, pc))
            {
                Simulator.Sleep((uint)SimClock.ConvertToTicks(ElevatorDoors.kLongEffectsTime, TimeUnit.Minutes));
                doors.KillAllEffects();
            }
            sErSleeping.SetValue(entry, false);
            sErTask.SetValue(entry, Simulator.kBadObjectGuid);
            ElevatorInterior next = null;
            if (E2Bool(sE2GoingUp, entry) && owner.ElevatorInteriorAbove != null) next = owner.ElevatorInteriorAbove;
            else if (owner.ElevatorInteriorBelow != null) next = owner.ElevatorInteriorBelow;  // EA's order: going up with no floor above uses below
            if (next != null && next.ElevatorPortal != null)
                s.SetPosition(next, slot == sE2Magic ? sE2Contain[0] : slot);
            return true;
        }

        public class BE2Hook
        {
            public bool StartHook(Sim s)
            {
                object entry = null;
                ElevatorInterior.ElevatorPortalComponent pc = (ElevatorInterior.ElevatorPortalComponent)(object)this;
                try
                {
                    if (ShortcutOn("elevatorridertimeout") && sE2Missing.Length == 0 && s != null && pc.Owner is ElevatorInterior)
                    {
                        IDictionary d = sErAssigned.GetValue(pc) as IDictionary;
                        if (d == null || !d.Contains(s.SimDescription)) return false;  // EA: not registered -> false
                        entry = d[s.SimDescription];
                    }
                }
                catch (Exception) { PatchCounters()[2860]++; entry = null; }
                if (entry == null) return StartOrig(s);
                return E2Ride(pc, s, entry);
            }
            public bool StartOrig(Sim s) { return false; } // receives ElevatorPortalComponent.OnPortalStart's original body
        }

        // ---- B4 §4 elevatorsavedlog (tuning kElevatorSavedLog; log only) ---------------------------------------------------
        // [REA E4, unconfirmed] ActiveUserEntry is persisted. A save taken while a rider was registered keeps its entry; after
        // the load no task is in OnPortalStart for it, and an entry that is not sleeping keeps IsElevatorReadyForLaunch false
        // for every later rider (who then waits for the 30-minute failsafe). This pass changes nothing: at world load it logs
        // each elevator's saved entries by state, so a live run can confirm the state exists before any fix is built.
        // Counters: [2845] elevators with saved entries, [2846] entries not sleeping (block launch), [2847] entries whose
        // rider is missing or has no live task, [2848] errors.
        internal static int[] EsClassify(bool[] sleeping, bool[] liveTask, bool[] riderPresent)
        {
            int[] r = new int[2];
            for (int i = 0; i < sleeping.Length; i++)
            {
                if (!sleeping[i]) r[0]++;
                if (!riderPresent[i] || !liveTask[i]) r[1]++;
            }
            return r;
        }

        internal static void ElevatorSavedLogWorldLoad()
        {
            if (!ShortcutOn("elevatorsavedlog")) return;
            if (sErSleeping == null) ErMembers();
            if (sErAssigned == null || sErSleeping == null || sErTask == null) return;
            int[] c = PatchCounters();
            foreach (Lot lot in LotManager.AllLots)
            {
                try
                {
                    if (lot == null || lot.IsWorldLot) continue;
                    foreach (ElevatorInterior ei in lot.GetObjects<ElevatorInterior>())
                    {
                        ElevatorInterior.ElevatorPortalComponent pc = ei.ElevatorPortal;
                        IDictionary d = pc == null ? null : sErAssigned.GetValue(pc) as IDictionary;
                        if (d == null || d.Count == 0) continue;
                        bool[] sl = new bool[d.Count], lt = new bool[d.Count], rp = new bool[d.Count];
                        int i = 0;
                        foreach (DictionaryEntry e in d)
                        {
                            SimDescription sd = e.Key as SimDescription;
                            sl[i] = (bool)sErSleeping.GetValue(e.Value); lt[i] = ErLiveTask(e.Value); rp[i] = sd != null && sd.CreatedSim != null;
                            i++;
                        }
                        int[] r = EsClassify(sl, lt, rp);
                        c[2845]++; c[2846] += r[0]; c[2847] += r[1];
                        ShortcutLog("elevatorsavedlog: lot " + lot.LotId + " elevator " + ei.ObjectId + ": saved entries " + d.Count + ", not sleeping " + r[0] + ", rider missing or no live task " + r[1]);
                    }
                }
                catch (Exception) { c[2848]++; }
            }
        }

        // ---- B4 §5 starlightreset (tuning kStarLightReset) -----------------------------------------------------------------
        // [verified, decompiled] StarLight.DoReset (lit light) clears its use list, then adds PlumbBob.SelectedActor and calls
        // GameObject.DoReset, which resets every Sim in the use list and then clears it. So resetting a lit StarLight (lot
        // reset, object reset, moving it) resets whichever household Sim is selected, wherever that Sim is. The added entry
        // has no other effect (GameObject.DoReset clears the list right after). Fix: while StarLight.DoReset runs,
        // GameObject.AddToUseList skips adding the selected Sim to that light. Everything else in the reset is EA's.
        // SwitchLightInternalStarLight's own add is left as EA has it (it keeps the light "in use" while lit).
        // Household-visible: the selected Sim is no longer reset. Counters: [2849] selected-Sim resets skipped,
        // [2850] seen with the switch off, [2851] errors.
        static readonly Type sSlType = typeof(MoonDial).Assembly.GetType("Sims3.Gameplay.Objects.Lighting.StarLight");
        static readonly Type sSlResetInfo = typeof(GameObject).GetNestedType("ResetInformation", BindingFlags.Public | BindingFlags.NonPublic);
        static object sSlResetting;
        static string sSlMissing = "";

        static void ApplyStarLightReset()
        {
            Type star = sSlType;
            sSlMissing = ((star == null ? "StarLight " : "") + (sSlResetInfo == null ? "GameObject.ResetInformation " : "")
                + (star != null && sSlResetInfo != null && star.GetMethod("DoReset", kInstAll | BindingFlags.DeclaredOnly, null, new Type[] { sSlResetInfo }, null) == null ? "StarLight.DoReset" : "")).Trim();
            if (sSlMissing.Length > 0) { AppliedLog().Add("starlightreset: not found: " + sSlMissing); return; }
            Patch(star, "DoReset", new Type[] { sSlResetInfo }, typeof(BSlHook), "ResetHook", "ResetOrig");
            Patch(typeof(GameObject), "AddToUseList", new Type[] { typeof(Sim) }, typeof(BSlUseHook), "AddHook", "AddOrig");
        }

        // Pure: skip this AddToUseList?
        internal static bool SlSkip(bool on, bool inThisLightsReset, bool isSelected)
        {
            return on && inThisLightsReset && isSelected;
        }

        public class BSlHook
        {
            public void ResetHook(object resetInformation)
            {
                object prev = sSlResetting;
                sSlResetting = this;
                try { ResetOrig(resetInformation); }
                finally { sSlResetting = prev; }
            }
            public void ResetOrig(object resetInformation) { } // receives StarLight.DoReset(ResetInformation)'s original body
        }

        public class BSlUseHook
        {
            public void AddHook(Sim a)
            {
                if (sSlResetting != null && sSlResetting == (object)this && a != null)
                {
                    try
                    {
                        if (a == PlumbBob.SelectedActor)
                        {
                            if (!ShortcutOn("starlightreset")) PatchCounters()[2850]++;
                            else if (SlSkip(true, true, true)) { PatchCounters()[2849]++; return; }
                        }
                    }
                    catch (Exception) { PatchCounters()[2851]++; }
                }
                AddOrig(a);
            }
            public void AddOrig(Sim a) { } // receives GameObject.AddToUseList(Sim)'s original body
        }

        // ---- B4 §6 moondiallistener (tuning kMoonDialListener) -------------------------------------------------------------
        // [verified, decompiled] MoonDial.OnStartup registers mEventLunarCycleEvent (kMoonChangeEvent); MoonDial.Dispose never
        // removes it. A deleted moon dial keeps answering every moon change: it creates a new lunar VisualEffect for a gone
        // object (never disposed), dims a light that no longer exists, and stays reachable through the tracker. Fix at the
        // source: Dispose removes the held listener first (EventTracker.RemoveListener). Not household-visible.
        // Counters: [2852] listeners removed at Dispose, [2853] seen with the switch off, [2854] errors.
        static readonly FieldInfo sMdlListener = typeof(MoonDial).GetField("mEventLunarCycleEvent", kInstAll);
        static string sMdlMissing = "";

        static void ApplyMoonDialListener()
        {
            sMdlMissing = ((sMdlListener == null ? "MoonDial.mEventLunarCycleEvent " : "")
                + (typeof(MoonDial).GetMethod("Dispose", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) == null ? "MoonDial.Dispose" : "")).Trim();
            if (sMdlMissing.Length > 0) { AppliedLog().Add("moondiallistener: not found: " + sMdlMissing); return; }
            Patch(typeof(MoonDial), "Dispose", Type.EmptyTypes, typeof(BMdlHook), "DisposeHook", "DisposeOrig");
        }

        // Pure: remove the held listener at Dispose?
        internal static bool MdlRemove(bool on, bool held)
        {
            return on && held;
        }

        public class BMdlHook
        {
            public void DisposeHook()
            {
                try
                {
                    MoonDial m = (MoonDial)(object)this;
                    EventListener l = sMdlListener.GetValue(m) as EventListener;
                    if (l != null)
                    {
                        if (!ShortcutOn("moondiallistener")) PatchCounters()[2853]++;
                        else if (MdlRemove(true, true)) { EventTracker.RemoveListener(l); sMdlListener.SetValue(m, null); PatchCounters()[2852]++; }
                    }
                }
                catch (Exception) { PatchCounters()[2854]++; }
                DisposeOrig();
            }
            public void DisposeOrig() { } // receives MoonDial.Dispose's original body
        }

        // ---- B4 §7 snublistener (tuning kSnubListener) ---------------------------------------------------------------------
        // [verified, decompiled] new SnubManager(sim) registers EventTracker.AddListener(kSimAgeTransition,
        // OnSimAgeTransition) and drops the returned listener; nothing removes it. Sim.Destroy only stops the broadcaster, and
        // EventTracker.RemoveSimReferences does not match it (no ScriptObject). Every NPC teen instantiated while snubbing
        // leaves one listener per instantiation, holding the destroyed Sim, that runs StopSnubbing on the dead Sim at every
        // age-up in town. simler90 changes OnSimAgeTransition's body, not this registration. Fix at the source: at
        // Sim.Destroy, the kSimAgeTransition listeners whose delegate belongs to that Sim's SnubManager are removed.
        // Not household-visible (the Sim object is gone). Counters: [2855] listeners removed, [2856] seen with the switch
        // off, [2857] errors.
        static readonly FieldInfo sSnlListeners = typeof(EventTracker).GetField("mListeners", kInstAll);
        static string sSnlMissing = "";

        static void ApplySnubListener()
        {
            sSnlMissing = ((sSnlListeners == null || !typeof(IDictionary).IsAssignableFrom(sSnlListeners.FieldType) ? "EventTracker.mListeners " : "")
                + (typeof(Sim).GetMethod("Destroy", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) == null ? "Sim.Destroy" : "")).Trim();
            if (sSnlMissing.Length > 0) { AppliedLog().Add("snublistener: not found: " + sSnlMissing); return; }
            Patch(typeof(Sim), "Destroy", Type.EmptyTypes, typeof(BSnlHook), "DestroyHook", "DestroyOrig");
        }

        // Pure: remove this listener? (its delegate's target is the destroyed Sim's SnubManager)
        internal static bool SnlRemove(bool on, bool targetIsThisManager)
        {
            return on && targetIsThisManager;
        }

        // The age-transition listeners whose delegate belongs to m (collected first: RemoveListener edits these lists).
        static List<EventListener> SnlFind(SnubManager m)
        {
            List<EventListener> found = new List<EventListener>();
            IDictionary byEvent = sSnlListeners.GetValue(EventTracker.Instance) as IDictionary;
            object k = unchecked((ulong)(long)EventTypeId.kSimAgeTransition);
            IDictionary byKey = byEvent == null || !byEvent.Contains(k) ? null : byEvent[k] as IDictionary;
            if (byKey == null) return found;
            foreach (object list in byKey.Values)
            {
                IList ls = list as IList;
                if (ls == null) continue;
                foreach (object o in ls)
                {
                    DelegateListener dl = o as DelegateListener;
                    if (dl != null && dl.Delegate != null && dl.Delegate.Target == (object)m) found.Add(dl);
                }
            }
            return found;
        }

        public class BSnlHook
        {
            public void DestroyHook()
            {
                try
                {
                    Sim s = (Sim)(object)this;
                    SnubManager m = s.mSnubManager;
                    if (m != null && EventTracker.Instance != null)
                    {
                        List<EventListener> found = SnlFind(m);
                        if (found.Count > 0)
                        {
                            if (!ShortcutOn("snublistener")) PatchCounters()[2856] += found.Count;
                            else foreach (EventListener l in found) if (SnlRemove(true, true)) { EventTracker.RemoveListener(l); PatchCounters()[2855]++; }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2857]++; }
                DestroyOrig();
            }
            public void DestroyOrig() { } // receives Sim.Destroy's original body
        }
    }
}
