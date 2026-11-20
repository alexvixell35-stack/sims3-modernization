using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Roles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // doorshare (tuning kDoorShare, default off; docs/674): the managed half of ts3native's [RouteSnap] DoorShare
    // (native/ts3native/doorshare.cpp). The route worker asks the sim thread "may this Sim pass this portal?" (World event
    // 59 -> LotManager.PortalLockedCallback -> Lot.OnPortalPathPlan -> Sim.OnPortalPathPlan) once per Sim and door; most of
    // those answers do not depend on the Sim. This file tells ts3native which ones:
    //  - per plan (UfPlanBody, next to the scope op): UserDiag op 40 with the route's Sim class. A Sim is in the ordinary
    //    class when every Sim fact the handler and CommonDoor.IsAllowedThrough read leaves the answer to the door and the
    //    lot (DoorShareCore.Ordinary: human, no tomb mummy, no proprietor role, no service, no stray, no IgnoreDoorLocks).
    //    Read fresh at every plan, so none of these needs a hook. Bit 1 marks the selected Sim's own command (never
    //    answered from a shared row); the Sim's id is folded in so the class is used for asks about that Sim only.
    //  - per answered ask (a hook after LotManager.PortalLockedCallback, the game's event 59 callback; not Sim.OnPortalPathPlan
    //    or Lot.OnPortalPathPlan: the first is a delegate target every lot holds, the second is invcache's swap): UserDiag op
    //    41 when the answer is "unlocked" and shareable:
    //    the lot's handler list is the game's handler alone, event 59 has the game's callback alone, the Sim is ordinary
    //    now, and the portal object is a plain door open to anybody (DoorShareCore.kDoor) or an object no rule of the handler
    //    names (kInert). ts3native keeps such an answer for every Sim of that class until its door generation moves.
    // The generation is doorgen's (DoorGen.cs, op 36): this file adds the two writers of the lot's hidden rooms
    // (Lot.WorldBuilderSetHiddenRoom, Lot.UpdateRoomMarkers) to its bumps. doorshare acts only with doorgen on.
    // Observe-and-send only: every hooked body runs unchanged, no answer is changed here.
    public static partial class Fixes
    {
        const int kDoorShareClassOp = 40, kDoorShareNoteOp = 41;
        static bool sDoorShareOn;       // the switch (with doorgen armed), read at every plan
        static bool sDoorShareNative;   // ts3native answered the last op 40 with "DoorShare on"
        static bool sDoorShareReady;    // the hook and the reflection handles are in
        static string sDshMissing = "";
        static FieldInfo sDshLotHandlers, sDshWorldCb;
        static IntPtr sDshSimHandler, sDshLotMgrCb;
        static object sDshWorldOk;       // the event 59 delegate object last verified as the game's callback alone
        static readonly Dictionary<ulong, object> sDshLotOk = new Dictionary<ulong, object>();  // lot id -> its verified handler list object
        static readonly Dictionary<Type, bool> sDshCommonRule = new Dictionary<Type, bool>();
        static readonly Type[] sDshCbArgs = { typeof(ulong), typeof(ulong), typeof(ulong), typeof(short), typeof(short), typeof(bool) };
        static int sDshClassSent, sDshOrdinarySent, sDshCommandSent, sDshNotes, sDshNoteDoor, sDshNoteInert, sDshNotPure, sDshErrors;

        static void ApplyDoorShare()
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                sDshLotHandlers = typeof(Lot).GetField("mOnPortalPathPlanHandler", any);
                sDshWorldCb = typeof(World).GetField("mOnPortalLocked_Delegate", any);
                MethodInfo simHandler = typeof(Sim).GetMethod("OnPortalPathPlan", any, null, new Type[] { typeof(PortalPlanEventArgs) }, null);
                MethodInfo lotMgrCb = typeof(LotManager).GetMethod("PortalLockedCallback", any, null, sDshCbArgs, null);
                if (sDshLotHandlers == null) sDshMissing = EpJoin(sDshMissing, "Lot.mOnPortalPathPlanHandler");
                if (sDshWorldCb == null) sDshMissing = EpJoin(sDshMissing, "World.mOnPortalLocked_Delegate");
                if (simHandler == null) sDshMissing = EpJoin(sDshMissing, "Sim.OnPortalPathPlan");
                if (lotMgrCb == null) sDshMissing = EpJoin(sDshMissing, "LotManager.PortalLockedCallback");
                if (sDshMissing.Length != 0) return;
                sDshSimHandler = simHandler.MethodHandle.Value;
                sDshLotMgrCb = lotMgrCb.MethodHandle.Value;
            }
            catch (Exception e) { sDshMissing = EpJoin(sDshMissing, e.GetType().Name); return; }
            bool hook = Patch(typeof(LotManager), "PortalLockedCallback", sDshCbArgs, typeof(DoorShareCbHook), "CbHook", "CbOrig");
            // the lot's hidden rooms are an input of every door answer: both writers bump doorgen's generation
            Patch(typeof(Lot), "WorldBuilderSetHiddenRoom", new Type[] { typeof(HiddenRoomMarker) }, typeof(DoorShareLotHook), "HiddenHook", "HiddenOrig");
            Patch(typeof(Lot), "UpdateRoomMarkers", Type.EmptyTypes, typeof(DoorShareLotHook), "MarkersHook", "MarkersOrig");
            sDoorShareReady = hook;
        }

        // world load, after DoorGenWorldLoad: doorshare needs doorgen's generation (ts3native answers nothing until that is armed)
        public static void DoorShareWorldLoad()
        {
            sDoorShareOn = false;  // the first plan reads the switch
            sDoorShareNative = false;
            sDshWorldOk = null;
            sDshLotOk.Clear();
        }

        // UfPlanBody, before the route's START or sync plan: the follower's class (0 = not a Sim of the ordinary class)
        static void DshRouteSend(Sim sim, uint handle, bool ownCheck)
        {
            try
            {
                bool on = sDoorShareReady && sDoorGenOn && ShortcutOn("doorshare");
                if (on != sDoorShareOn) { sDoorShareOn = on; sDoorShareNative = false; }  // off: no note goes out from here on
                if (!on || handle == 0) return;
                int bits = 0;
                if (sim != null && DshOrdinary(sim))  // our own route checks too: the class is the follower's, whatever the plan is for
                {
                    InteractionInstance ia = ownCheck ? null : sim.CurrentInteraction;
                    bool command = ia != null && NpmIsPlayerCommand(sim == PlumbBob.SelectedActor || sim.IsSelectable, ia.Autonomous,
                        ia.GetPriority().Level == InteractionPriorityLevel.UserDirected, ia is FollowerMeetUpAtPoint);
                    bits = DoorShareCore.ClassBits(true, command, sim.ObjectId.Value);
                    sDshOrdinarySent++;
                    if (command) sDshCommandSent++;
                }
                sDshClassSent++;
                sDoorShareNative = DiagSend(kDoorShareClassOp, handle, (ushort)bits);
            }
            catch (ResetException) { throw; }
            catch (Exception) { sDshErrors++; }
        }

        static bool DshOrdinary(Sim sim)
        {
            SimDescription d = sim.SimDescription;
            if (d == null) return false;
            return DoorShareCore.Ordinary(d.IsHuman, d.IsTombMummy, d.AssignedRole is Proprietor, d.Service != null, d.IsStray,
                sim.FlagIsSet(Sim.SimFlags.IgnoreDoorLocks));
        }

        // every entry of the delegate is the static method m (the game registers some callbacks more than once, docs LotCallbacks80)
        static bool DshOnly(object list, IntPtr m)
        {
            Delegate d = list as Delegate;
            if (d == null) return false;
            Delegate[] l = d.GetInvocationList();
            for (int i = 0; i < l.Length; i++)
                if (l[i].Target != null || l[i].Method.MethodHandle.Value != m) return false;
            return l.Length != 0;
        }

        // Delegate.Combine / Remove make a new object on every change, so an unchanged reference is an unchanged list
        static bool DshWorldPure()
        {
            object cb = sDshWorldCb.GetValue(null);
            if (cb == null) return false;
            if (object.ReferenceEquals(cb, sDshWorldOk)) return true;
            if (!DshOnly(cb, sDshLotMgrCb)) return false;
            sDshWorldOk = cb;
            return true;
        }

        static bool DshLotPure(Lot lot)
        {
            object h = sDshLotHandlers.GetValue(lot);
            if (h == null) return false;
            ulong id = lot.LotId;
            object ok;
            if (sDshLotOk.TryGetValue(id, out ok) && object.ReferenceEquals(ok, h)) return true;
            if (!DshOnly(h, sDshSimHandler)) { sDshLotOk.Remove(id); return false; }
            sDshLotOk[id] = h;
            return true;
        }

        // does this door type answer with CommonDoor.IsAllowedThrough itself (no override: single-gender doors, turnstiles, ...)?
        static bool DshCommonRule(Type t)
        {
            bool r;
            if (sDshCommonRule.TryGetValue(t, out r)) return r;
            MethodInfo m = t.GetMethod("IsAllowedThrough", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] { typeof(int), typeof(int), typeof(Sim) }, null);
            r = m != null && m.DeclaringType == typeof(CommonDoor);
            sDshCommonRule[t] = r;
            return r;
        }

        // the ask (lot, routing object, portal object, rooms) was just answered "unlocked" by the game's callback
        static void DshNote(ulong lotId, ulong simId, ulong portalId, int from, int to)
        {
            try
            {
                Lot lot = LotManager.GetLot(lotId);
                Sim actor = new ObjectGuid(simId).ObjectFromId<Sim>();
                GameObject p = new ObjectGuid(portalId).ObjectFromId<GameObject>();
                if (actor == null || p == null || lot == null) return;
                if (!DshOrdinary(actor)) return;
                bool special = p is FirefighterPole || p is IBoxStall || p is IHorseJump || p is ElevatorDoors || p is DivingBoard || p is Ladder;
                Door door = p as Door;
                bool lotPure = DshLotPure(lot), worldPure = lotPure && DshWorldPure();
                int kind = DoorShareCore.Kind(lotPure, worldPure, special, door != null, door != null && DshCommonRule(door.GetType()),
                    door != null && door.LockType == CommonDoor.tLock.Anybody);
                if (kind == DoorShareCore.kNone) { if (!lotPure || !worldPure) sDshNotPure++; return; }
                sDshNotes++;
                if (kind == DoorShareCore.kDoor) sDshNoteDoor++; else sDshNoteInert++;
                DiagSend(kDoorShareNoteOp, DoorShareCore.NoteHash(portalId, simId, from, to), (ushort)kind);
            }
            catch (ResetException) { throw; }
            catch (Exception) { sDshErrors++; }
        }

        public class DoorShareCbHook
        {
            public static uint CbHook(ulong lotId, ulong routingObjectId, ulong portalObjectId, short roomFrom, short roomTo, bool initialLocked)
            {
                uint r = CbOrig(lotId, routingObjectId, portalObjectId, roomFrom, roomTo, initialLocked);
                if (sDoorShareOn && sDoorShareNative && r == (uint)PortalLockedCallbackResults.PortalUnlocked)
                    DshNote(lotId, routingObjectId, portalObjectId, roomFrom, roomTo);
                return r;
            }
            public static uint CbOrig(ulong lotId, ulong routingObjectId, ulong portalObjectId, short roomFrom, short roomTo, bool initialLocked) { return 0; }
        }

        public class DoorShareLotHook
        {
            public void HiddenHook(HiddenRoomMarker marker) { HiddenOrig(marker); DoorGenBump(); }
            public void HiddenOrig(HiddenRoomMarker marker) { }
            public void MarkersHook() { MarkersOrig(); DoorGenBump(); }
            public void MarkersOrig() { }
        }

        // DoorShareCheck run: the two rules and the encoding shared with ts3native, with controls (pure; the native side has
        // its selftest and its own sampled live check). result: what was sent this session.
        public static object DoorShareCheck(string what)
        {
            string counters = "doorshare: on " + sDoorShareOn + " (hook " + sDoorShareReady + ", doorgen " + sDoorGenOn + ", ts3native DoorShare " + sDoorShareNative
                + "), class ops " + sDshClassSent + " (ordinary " + sDshOrdinarySent + ", player commands " + sDshCommandSent + "), notes " + sDshNotes + " (door "
                + sDshNoteDoor + ", inert " + sDshNoteInert + "), lot or event 59 handlers not the game's alone " + sDshNotPure + ", errors " + sDshErrors + "; "
                + GlMembers(EpJoin(sDshMissing, CkLive("doorshare")));
            if (what == "result") return counters;
            if (what != "run") return "doorshare <run|result>";
            bool on = ShortcutOn("doorshare");
            bool shared = DoorShareCore.Ordinary(true, false, false, false, false, false)
                && DoorShareCore.Kind(true, true, false, true, true, true) == DoorShareCore.kDoor
                && DoorShareCore.Kind(true, true, false, false, false, false) == DoorShareCore.kInert
                && DoorShareCore.NoteHash(0x0000004400000033UL, 0x0000B0B00000A0A0UL, 5, 6) == 0x7845B309u
                && DoorShareCore.ClassBits(true, false, 0x0000B0B00000A0A0UL) == (DoorShareCore.kOrdinary | 0x1010 << DoorShareCore.kSimShift);
            bool controls = !DoorShareCore.Ordinary(false, false, false, false, false, false)   // a pet or a horse
                && !DoorShareCore.Ordinary(true, true, false, false, false, false)               // tomb mummy
                && !DoorShareCore.Ordinary(true, false, true, false, false, false)               // proprietor
                && !DoorShareCore.Ordinary(true, false, false, true, false, false)               // service Sim
                && !DoorShareCore.Ordinary(true, false, false, false, false, true)               // IgnoreDoorLocks
                && DoorShareCore.Kind(true, true, false, true, true, false) == DoorShareCore.kNone    // a lock type that reads the Sim
                && DoorShareCore.Kind(true, true, false, true, false, true) == DoorShareCore.kNone    // a door with its own rule
                && DoorShareCore.Kind(true, true, true, false, false, false) == DoorShareCore.kNone   // elevator, ladder, ...
                && DoorShareCore.Kind(false, true, false, true, true, true) == DoorShareCore.kNone    // a privacy or mod handler on the lot
                && DoorShareCore.Kind(true, false, false, true, true, true) == DoorShareCore.kNone    // a mod on event 59
                && DoorShareCore.ClassBits(false, true, 1) == 0;
            return B582Line("doorshare", on, "door answers that do not depend on the Sim are marked for ts3native to share", on && shared,
                "Sims and portals whose answer reads the Sim are never marked", controls, sDshMissing, counters);
        }
    }
}
