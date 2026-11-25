using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI.Hud;

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
    // SetPortalLockStatus does not report; a lot with such a handler is held out of the memo, DoorGenHold), household membership (Household.Add / Remove: SelectedHousehold locks).
    // Also covered: fire on the lot (FireManager.RegisterFire / UnregisterFire, the OnFire / Torched buffs' OnAddition /
    // OnRemoval: Lot.IsFireOnLot), Sim.SimFlags.IgnoreDoorLocks (Sim.FlagSet with that bit), tomb mummy (OccultManager
    // AddOccultType / RemoveOccultType; its NPC-household half is Household.Add / Remove above), proprietor and every other role
    // (Role.CreateRole, Role.RemoveSimFromRole, RoleManagerTask.FillInWorldRole, which clears other Sims' roles), resort VIP data
    // and closing (ResortManager set_Closed, DoUpdate when Closed changed, CreateNewVIPDoorGroup, RemoveDoorFromVIPRoom,
    // FindVIPRoomData when it created a row, RefreshVIPRooms, VIPRoomData.AddGuest / RemoveGuest), gender, age and visible
    // pregnancy (SimDescription set_Gender / set_Age / set_IsVisuallyPregnant: gender doors, diving board).
    // Not covered: inputs of portal types with their own IsAllowedThrough beyond these (elevators, box stalls, horse jumps,
    // spiral stairs, pools, tube elevators, ladders) and world-switch role teardown (RoleManager.DestroyAllRoleBots: travel
    // starts a new world and table). Those stay on the native 1-in-8 re-ask, the self-disable and DoorMemoXMs.
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
            // fire
            Patch(typeof(FireManager), "RegisterFire", new Type[] { typeof(Fire), typeof(LotLocation) }, typeof(DoorGenFireHook), "RegHook", "RegOrig");
            Patch(typeof(FireManager), "UnregisterFire", new Type[] { typeof(Fire) }, typeof(DoorGenFireHook), "UnregHook", "UnregOrig");
            Patch(typeof(BuffOnFire), "OnAddition", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(bool) }, typeof(DoorGenBuffHook), "AddHook", "AddOrig");
            Patch(typeof(BuffOnFire), "OnRemoval", new Type[] { typeof(BuffManager), typeof(BuffInstance) }, typeof(DoorGenBuffHook), "RemHook", "RemOrig");
            Patch(typeof(BuffTorched), "OnAddition", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(bool) }, typeof(DoorGenBuffHook2), "AddHook", "AddOrig");
            Patch(typeof(BuffTorched), "OnRemoval", new Type[] { typeof(BuffManager), typeof(BuffInstance) }, typeof(DoorGenBuffHook2), "RemHook", "RemOrig");
            // IgnoreDoorLocks
            Patch(typeof(Sim), "FlagSet", new Type[] { typeof(Sim.SimFlags), typeof(bool) }, typeof(DoorGenSimHook), "FlagHook", "FlagOrig");
            // occults (tomb mummy)
            Patch(typeof(OccultManager), "AddOccultType", new Type[] { typeof(OccultTypes), typeof(bool), typeof(bool), typeof(bool), typeof(OccultBaseClass) }, typeof(DoorGenOccultHook), "AddHook", "AddOrig");
            Patch(typeof(OccultManager), "RemoveOccultType", new Type[] { typeof(OccultTypes) }, typeof(DoorGenOccultHook), "RemHook", "RemOrig");
            // roles
            Patch(typeof(Role), "CreateRole", new Type[] { typeof(RoleData), typeof(SimDescription), typeof(IRoleGiver) }, typeof(DoorGenRoleHook), "CreateHook", "CreateOrig");
            Patch(typeof(Role), "RemoveSimFromRole", Type.EmptyTypes, typeof(DoorGenRoleHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(RoleManagerTask), "FillInWorldRole", new Type[] { typeof(Role.RoleType), typeof(IRoleGiver), typeof(List<SimDescription>), typeof(List<SimDescription>) }, typeof(DoorGenRoleTaskHook), "FillHook", "FillOrig");
            // resort VIP data and closing
            Patch(typeof(ResortManager), "set_Closed", new Type[] { typeof(bool) }, typeof(DoorGenResortHook), "ClosedHook", "ClosedOrig");
            Patch(typeof(ResortManager), "DoUpdate", Type.EmptyTypes, typeof(DoorGenResortHook), "UpdateHook", "UpdateOrig");
            Patch(typeof(ResortManager), "CreateNewVIPDoorGroup", new Type[] { typeof(ulong), typeof(ObjectGuid) }, typeof(DoorGenResortHook), "GroupHook", "GroupOrig");
            Patch(typeof(ResortManager), "RemoveDoorFromVIPRoom", new Type[] { typeof(ObjectGuid) }, typeof(DoorGenResortHook), "RemDoorHook", "RemDoorOrig");
            Patch(typeof(ResortManager), "FindVIPRoomData", new Type[] { typeof(ulong), typeof(bool) }, typeof(DoorGenResortHook), "FindHook", "FindOrig");
            Patch(typeof(ResortManager), "RefreshVIPRooms", Type.EmptyTypes, typeof(DoorGenResortHook), "RefreshHook", "RefreshOrig");
            Patch(typeof(ResortManager.VIPRoomData), "AddGuest", new Type[] { typeof(ulong) }, typeof(DoorGenVipHook), "AddHook", "AddOrig");
            Patch(typeof(ResortManager.VIPRoomData), "RemoveGuest", new Type[] { typeof(ulong) }, typeof(DoorGenVipHook), "RemHook", "RemOrig");
            // gender, age, visible pregnancy
            Patch(typeof(SimDescription), "set_Gender", new Type[] { typeof(CASAgeGenderFlags) }, typeof(DoorGenDescHook), "GenderHook", "GenderOrig");
            Patch(typeof(SimDescription), "set_Age", new Type[] { typeof(CASAgeGenderFlags) }, typeof(DoorGenDescHook), "AgeHook", "AgeOrig");
            Patch(typeof(SimDescription), "set_IsVisuallyPregnant", new Type[] { typeof(bool) }, typeof(DoorGenDescHook), "PregHook", "PregOrig");
        }

        // world load: read the switch once and arm the native side (op 36 lo 1); off = nothing is sent all session
        public static void DoorGenWorldLoad()
        {
            sDoorGenOn = ShortcutOn("doorgen");
            if (!sDoorGenOn) return;
            DoorGenSend(1);
            foreach (Lot lot in LotManager.AllLots) DoorGenHold(lot);  // privacy situations restored before the switch was read
        }

        // A lot's PortalPathPlan handlers beyond EA's Sim.OnPortalPathPlan (PrivacySituation, mods) answer from the asking Sim's
        // current interaction, which nothing bumps: the native memo leaves such a lot alone (op 36 hi 1 = held, 2 = free; key is
        // the lot id's low dword). Unknown (field missing, error) = held.
        static FieldInfo sDgLotHandlers;
        static void DoorGenHold(Lot lot)
        {
            if (!sDoorGenOn || lot == null) return;
            bool held = false;
            try
            {
                if (sDgLotHandlers == null) sDgLotHandlers = typeof(Lot).GetField("mOnPortalPathPlanHandler", BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate d = sDgLotHandlers != null ? sDgLotHandlers.GetValue(lot) as Delegate : null;
                if (sDgLotHandlers == null) held = true;
                else if (d != null)
                    foreach (Delegate h in d.GetInvocationList())
                        if (h.Method.DeclaringType != typeof(Sim) || h.Method.Name != "OnPortalPathPlan") { held = true; break; }
            }
            catch (Exception) { held = true; }
            try { DiagSend(kDoorGenOp, (uint)lot.LotId, held ? (ushort)1 : (ushort)2); } catch (Exception) { }
        }

        // src = DoorShareCore.kSrc* (which input changed), id = the door's object id or the lot id it changed on (0 = neither).
        // ts3native bumps DoorMemoX's generation on every one and DoorShare's per door, per lot or not at all by the source
        // (docs/674 §9; native/ts3native/doorshare-core.h DsSrcScope). No PatchCounters slot: ts3native counts them by source.
        static void DoorGenBump(int src, ulong id)
        {
            if (!sDoorGenOn) return;
            try { DiagSend(kDoorGenOp, id != 0 ? DoorShareCore.IdHash(id) : 0u, (ushort)(DoorShareCore.kSrcOpBase + src)); } catch (Exception) { }  // hi 1 / 2 are DoorGenHold's
        }

        static void DoorGenBumpDoor(int src, object door)
        {
            if (!sDoorGenOn) return;
            ulong id = 0;
            try { id = ((CommonDoor)door).ObjectId.Value; } catch (Exception) { }
            DoorGenBump(id != 0 ? src : DoorShareCore.kSrcUnknown, id);  // no id: every row goes
        }

        static void DoorGenBumpLot(int src, object lot)
        {
            if (!sDoorGenOn) return;
            ulong id = 0;
            try { id = ((Lot)lot).LotId; } catch (Exception) { }
            DoorGenBump(id != 0 ? src : DoorShareCore.kSrcUnknown, id);
        }

        static void DoorGenSend(uint lo)
        {
            try { DiagSend(kDoorGenOp, lo); } catch (Exception) { }
        }

        public class DoorGenDoorHook
        {
            public void Lock2Hook(CommonDoor.tLock t, SimDescription o) { Lock2Orig(t, o); DoorGenBumpDoor(DoorShareCore.kSrcLock, this); }
            public void Lock2Orig(CommonDoor.tLock t, SimDescription o) { }
            public void Lock3Hook(CommonDoor.tLock t, SimDescription o, int room) { Lock3Orig(t, o, room); DoorGenBumpDoor(DoorShareCore.kSrcLock, this); }
            public void Lock3Orig(CommonDoor.tLock t, SimDescription o, int room) { }
            public void ListHook(List<SimDescription> l) { ListOrig(l); DoorGenBumpDoor(DoorShareCore.kSrcOwnerList, this); }
            public void ListOrig(List<SimDescription> l) { }
            public void AddHook(Sim s) { AddOrig(s); DoorGenBumpDoor(DoorShareCore.kSrcOwnerList, this); }
            public void AddOrig(Sim s) { }
            public void RemoveHook(Sim s) { RemoveOrig(s); DoorGenBumpDoor(DoorShareCore.kSrcOwnerList, this); }
            public void RemoveOrig(Sim s) { }
        }

        public class DoorGenHouseholdHook
        {
            public void GreetHook(Lot lot, ObjectGuid sim) { GreetOrig(lot, sim); DoorGenBump(DoorShareCore.kSrcGreet, 0); }
            public void GreetOrig(Lot lot, ObjectGuid sim) { }
            public void UngreetHook(Lot lot, ObjectGuid sim) { UngreetOrig(lot, sim); DoorGenBump(DoorShareCore.kSrcGreet, 0); }
            public void UngreetOrig(Lot lot, ObjectGuid sim) { }
            public void AddHook(SimDescription d) { AddOrig(d); DoorGenBump(DoorShareCore.kSrcHousehold, 0); }
            public void AddOrig(SimDescription d) { }
            public void RemoveHook(SimDescription d) { RemoveOrig(d); DoorGenBump(DoorShareCore.kSrcHousehold, 0); }
            public void RemoveOrig(SimDescription d) { }
        }

        public class DoorGenFireHook
        {
            public void RegHook(Fire f, LotLocation l) { RegOrig(f, l); DoorGenBump(DoorShareCore.kSrcFire, 0); }
            public void RegOrig(Fire f, LotLocation l) { }
            public void UnregHook(Fire f) { UnregOrig(f); DoorGenBump(DoorShareCore.kSrcFire, 0); }
            public void UnregOrig(Fire f) { }
        }

        public class DoorGenBuffHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travel) { AddOrig(bm, bi, travel); DoorGenBump(DoorShareCore.kSrcFireBuff, 0); }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travel) { }
            public void RemHook(BuffManager bm, BuffInstance bi) { RemOrig(bm, bi); DoorGenBump(DoorShareCore.kSrcFireBuff, 0); }
            public void RemOrig(BuffManager bm, BuffInstance bi) { }
        }

        public class DoorGenBuffHook2
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travel) { AddOrig(bm, bi, travel); DoorGenBump(DoorShareCore.kSrcFireBuff, 0); }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travel) { }
            public void RemHook(BuffManager bm, BuffInstance bi) { RemOrig(bm, bi); DoorGenBump(DoorShareCore.kSrcFireBuff, 0); }
            public void RemOrig(BuffManager bm, BuffInstance bi) { }
        }

        public class DoorGenSimHook
        {
            // only the IgnoreDoorLocks bit, and only when it really changes (FlagSet is common)
            public void FlagHook(Sim.SimFlags f, bool v)
            {
                Sim s = (Sim)(object)this;
                bool watch = sDoorGenOn && (f & Sim.SimFlags.IgnoreDoorLocks) != 0;
                bool before = watch && s.FlagIsSet(Sim.SimFlags.IgnoreDoorLocks);
                FlagOrig(f, v);
                if (watch && s.FlagIsSet(Sim.SimFlags.IgnoreDoorLocks) != before) DoorGenBump(DoorShareCore.kSrcIgnoreLocks, 0);
            }
            public void FlagOrig(Sim.SimFlags f, bool v) { }
        }

        public class DoorGenOccultHook
        {
            public bool AddHook(OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { bool r = AddOrig(t, outfit, reward, restore, o); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; }
            public bool AddOrig(OccultTypes t, bool outfit, bool reward, bool restore, OccultBaseClass o) { return false; }
            public bool RemHook(OccultTypes t) { bool r = RemOrig(t); DoorGenBump(DoorShareCore.kSrcOccult, 0); return r; }
            public bool RemOrig(OccultTypes t) { return false; }
        }

        public class DoorGenRoleHook
        {
            public static Role CreateHook(RoleData d, SimDescription s, IRoleGiver g) { Role r = CreateOrig(d, s, g); DoorGenBump(DoorShareCore.kSrcRole, 0); return r; }
            public static Role CreateOrig(RoleData d, SimDescription s, IRoleGiver g) { return null; }
            public void RemoveHook() { RemoveOrig(); DoorGenBump(DoorShareCore.kSrcRole, 0); }
            public void RemoveOrig() { }
        }

        public class DoorGenRoleTaskHook
        {
            public Role FillHook(Role.RoleType t, IRoleGiver g, List<SimDescription> res, List<SimDescription> town) { Role r = FillOrig(t, g, res, town); DoorGenBump(DoorShareCore.kSrcRole, 0); return r; }
            public Role FillOrig(Role.RoleType t, IRoleGiver g, List<SimDescription> res, List<SimDescription> town) { return null; }
        }

        public class DoorGenResortHook
        {
            public void ClosedHook(bool v) { ClosedOrig(v); DoorGenBump(DoorShareCore.kSrcResort, 0); }
            public void ClosedOrig(bool v) { }
            public void UpdateHook()
            {
                ResortManager m = (ResortManager)(object)this;
                bool before = m.Closed;
                UpdateOrig();
                if (m.Closed != before) DoorGenBump(DoorShareCore.kSrcResort, 0);
            }
            public void UpdateOrig() { }
            public ulong GroupHook(ulong id, ObjectGuid door) { ulong r = GroupOrig(id, door); DoorGenBump(DoorShareCore.kSrcResort, 0); return r; }
            public ulong GroupOrig(ulong id, ObjectGuid door) { return 0; }
            public void RemDoorHook(ObjectGuid door) { RemDoorOrig(door); DoorGenBump(DoorShareCore.kSrcResort, 0); }
            public void RemDoorOrig(ObjectGuid door) { }
            public ResortManager.VIPRoomData FindHook(ulong group, bool create)
            {
                ResortManager m = (ResortManager)(object)this;
                int n = m.VIPRooms != null ? m.VIPRooms.Count : 0;
                ResortManager.VIPRoomData r = FindOrig(group, create);
                if (create && (m.VIPRooms != null ? m.VIPRooms.Count : 0) != n) DoorGenBump(DoorShareCore.kSrcResort, 0);
                return r;
            }
            public ResortManager.VIPRoomData FindOrig(ulong group, bool create) { return null; }
            public void RefreshHook() { RefreshOrig(); DoorGenBump(DoorShareCore.kSrcResort, 0); }
            public void RefreshOrig() { }
        }

        public class DoorGenVipHook
        {
            public void AddHook(ulong id) { AddOrig(id); DoorGenBump(DoorShareCore.kSrcResort, 0); }
            public void AddOrig(ulong id) { }
            public void RemHook(ulong id) { RemOrig(id); DoorGenBump(DoorShareCore.kSrcResort, 0); }
            public void RemOrig(ulong id) { }
        }

        public class DoorGenDescHook
        {
            public void GenderHook(CASAgeGenderFlags g) { GenderOrig(g); DoorGenBump(DoorShareCore.kSrcDesc, 0); }
            public void GenderOrig(CASAgeGenderFlags g) { }
            public void AgeHook(CASAgeGenderFlags a) { AgeOrig(a); DoorGenBump(DoorShareCore.kSrcDesc, 0); }
            public void AgeOrig(CASAgeGenderFlags a) { }
            public void PregHook(bool v) { PregOrig(v); DoorGenBump(DoorShareCore.kSrcDesc, 0); }
            public void PregOrig(bool v) { }
        }

        public class DoorGenLotHook
        {
            public void AddHook(PortalPathPlanHandler h) { AddOrig(h); DoorGenHold((Lot)(object)this); DoorGenBumpLot(DoorShareCore.kSrcLotHandler, this); }
            public void AddOrig(PortalPathPlanHandler h) { }
            public void RemoveHook(PortalPathPlanHandler h) { RemoveOrig(h); DoorGenHold((Lot)(object)this); DoorGenBumpLot(DoorShareCore.kSrcLotHandler, this); }
            public void RemoveOrig(PortalPathPlanHandler h) { }
        }
    }
}
