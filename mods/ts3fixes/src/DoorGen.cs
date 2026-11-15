using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Door-answer generation for ts3native's [RouteSnap] DoorMemoX=3 (hostcalls.cpp): the native memo reuses another job's
    // "unlocked" door answer only while nothing it depends on changed. Its own generation covers the portal ICs, lot world events
    // and the global event 59 delegate. This adds the managed inputs of Lot.OnPortalPathPlan / CommonDoor.IsAllowedThrough that
    // change on the sim thread without any of those: each setter below sends UserDiag op 36 (lo 0 = bump) right after EA's body,
    // so the native generation moves before any later ask is served. Switch "doorgen" (default off): on at world load it also
    // sends op 36 lo 1 (armed); DoorMemoX=3 acts only once armed, so a package without this file (or the switch off) never
    // gets mode 3's memo. Observe-and-bump only: every hooked body runs unchanged.
    // Covered: lock type / owner / one-way room (both SetLockTypeAndOwner overloads), owner lists (SetLockTypeOwnerList,
    // AddToOwnerList, RemoveFromOwnerList), greeting (Household.AddGreetedLotToHousehold / RemoveGreetedLotForHousehold),
    // per-lot portal handlers (Lot.PortalPathPlan add / remove: PrivacySituation and mods, also the 2nd+ handler, which
    // SetPortalLockStatus does not report), household membership (Household.Add / Remove: SelectedHousehold locks).
    // Not covered (left to the native 1-in-8 re-ask, the self-disable and DoorMemoXMs): fire on the lot (pets, barn doors),
    // Sim.SimFlags.IgnoreDoorLocks, tomb mummy, proprietor roles, resort VIP data and closing, gender changes.
    public static partial class Fixes
    {
        const int kDoorGenOp = 36;
        static bool sDoorGenOn;

        static void ApplyDoorGen()
        {
            Patch(typeof(CommonDoor), "SetLockTypeAndOwner", new Type[] { typeof(CommonDoor.tLock), typeof(SimDescription) }, typeof(DoorGenDoorHook), "Lock2Hook", "Lock2Orig");
            Patch(typeof(CommonDoor), "SetLockTypeAndOwner", new Type[] { typeof(CommonDoor.tLock), typeof(SimDescription), typeof(int) }, typeof(DoorGenDoorHook), "Lock3Hook", "Lock3Orig");
            Patch(typeof(CommonDoor), "SetLockTypeOwnerList", new Type[] { typeof(List<SimDescription>) }, typeof(DoorGenDoorHook), "ListHook", "ListOrig");
            Patch(typeof(CommonDoor), "AddToOwnerList", new Type[] { typeof(Sim) }, typeof(DoorGenDoorHook), "AddHook", "AddOrig");
            Patch(typeof(CommonDoor), "RemoveFromOwnerList", new Type[] { typeof(Sim) }, typeof(DoorGenDoorHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(Household), "AddGreetedLotToHousehold", new Type[] { typeof(Lot), typeof(ObjectGuid) }, typeof(DoorGenHouseholdHook), "GreetHook", "GreetOrig");
            Patch(typeof(Household), "RemoveGreetedLotForHousehold", new Type[] { typeof(Lot), typeof(ObjectGuid) }, typeof(DoorGenHouseholdHook), "UngreetHook", "UngreetOrig");
            Patch(typeof(Household), "Add", new Type[] { typeof(SimDescription) }, typeof(DoorGenHouseholdHook), "AddHook", "AddOrig");
            Patch(typeof(Household), "Remove", new Type[] { typeof(SimDescription) }, typeof(DoorGenHouseholdHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(Lot), "add_PortalPathPlan", new Type[] { typeof(PortalPathPlanHandler) }, typeof(DoorGenLotHook), "AddHook", "AddOrig");
            Patch(typeof(Lot), "remove_PortalPathPlan", new Type[] { typeof(PortalPathPlanHandler) }, typeof(DoorGenLotHook), "RemoveHook", "RemoveOrig");
        }

        // world load: read the switch once and arm the native side (op 36 lo 1); off = nothing is sent all session
        public static void DoorGenWorldLoad()
        {
            sDoorGenOn = ShortcutOn("doorgen");
            if (sDoorGenOn) DoorGenSend(1);
        }

        static void DoorGenBump()
        {
            if (!sDoorGenOn) return;
            DoorGenSend(0);  // no PatchCounters slot: ts3native counts the bumps (dmx script generation in its log line)
        }

        static void DoorGenSend(uint lo)
        {
            try { DiagSend(kDoorGenOp, lo); } catch (Exception) { }
        }

        public class DoorGenDoorHook
        {
            public void Lock2Hook(CommonDoor.tLock t, SimDescription o) { Lock2Orig(t, o); DoorGenBump(); }
            public void Lock2Orig(CommonDoor.tLock t, SimDescription o) { }
            public void Lock3Hook(CommonDoor.tLock t, SimDescription o, int room) { Lock3Orig(t, o, room); DoorGenBump(); }
            public void Lock3Orig(CommonDoor.tLock t, SimDescription o, int room) { }
            public void ListHook(List<SimDescription> l) { ListOrig(l); DoorGenBump(); }
            public void ListOrig(List<SimDescription> l) { }
            public void AddHook(Sim s) { AddOrig(s); DoorGenBump(); }
            public void AddOrig(Sim s) { }
            public void RemoveHook(Sim s) { RemoveOrig(s); DoorGenBump(); }
            public void RemoveOrig(Sim s) { }
        }

        public class DoorGenHouseholdHook
        {
            public void GreetHook(Lot lot, ObjectGuid sim) { GreetOrig(lot, sim); DoorGenBump(); }
            public void GreetOrig(Lot lot, ObjectGuid sim) { }
            public void UngreetHook(Lot lot, ObjectGuid sim) { UngreetOrig(lot, sim); DoorGenBump(); }
            public void UngreetOrig(Lot lot, ObjectGuid sim) { }
            public void AddHook(SimDescription d) { AddOrig(d); DoorGenBump(); }
            public void AddOrig(SimDescription d) { }
            public void RemoveHook(SimDescription d) { RemoveOrig(d); DoorGenBump(); }
            public void RemoveOrig(SimDescription d) { }
        }

        public class DoorGenLotHook
        {
            public void AddHook(PortalPathPlanHandler h) { AddOrig(h); DoorGenBump(); }
            public void AddOrig(PortalPathPlanHandler h) { }
            public void RemoveHook(PortalPathPlanHandler h) { RemoveOrig(h); DoorGenBump(); }
            public void RemoveOrig(PortalPathPlanHandler h) { }
        }
    }
}
