using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Controllers;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // viproomrefresh (tuning kVipRoomRefresh, off by default; docs/555 §3). EA bug fix (crash path), exact for every Sim.
    //
    // ResortManager.RefreshVIPRooms walks VIPRooms with foreach and, for a VIP room none of whose doors still exists, calls
    // VIPRooms.Remove(room) inside that loop [verified, decompiled]. The enumerator's next MoveNext then throws "Collection
    // was modified", so every room after it keeps its stale door locks and owner, and the exception leaves the caller half
    // done: closing a resort (ResortManager.Closed setter) no longer sends its guests home, a check-out stops after the
    // guest was dropped, and a resort import (ResortFrontDesk) ends before the import's caller finishes. The door-delete
    // path (VIP door removal by id) keeps VIPRooms clean on its own, so it takes a door that vanished another way
    // (deleted while the lot was not loaded, an imported resort whose doors did not come along, a stale id after load).
    //
    // The fix runs EA's loop body unchanged over a copy of VIPRooms: the same rooms in the same order, the same calls,
    // and the empty room is removed from the real list as EA does; only the throw (and the rooms it skipped) go away.
    // Counters: [2417] calls where EA's loop would have thrown (counted with the switch off too), [2418] empty rooms the
    // fix removed, [2419] errors (registry fixes-2417). Harness check: VipRoomCheck below, repro vip-room-refresh-modified.
    public static partial class Fixes
    {
        static void ApplyVipRoomRefresh()
        {
            Patch(typeof(ResortManager), "RefreshVIPRooms", typeof(VipRefreshHook), "RefreshHook", "RefreshOrig");
        }

        public class VipRefreshHook
        {
            public void RefreshHook()
            {
                ResortManager rm = (object)this as ResortManager;
                bool fix = false;
                try { fix = VipCount(rm) && ShortcutOn("viproomrefresh"); } catch (Exception) { PatchCounters()[2419]++; }
                if (!fix) { RefreshOrig(); return; }
                VipRefresh(rm);
            }
            public void RefreshOrig() { } // receives ResortManager.RefreshVIPRooms's original body
        }

        // True when one room has no door left: EA removes it inside its foreach and the next MoveNext throws.
        static bool VipCount(ResortManager rm)
        {
            if (rm == null || rm.VIPRooms == null) return false;
            foreach (ResortManager.VIPRoomData room in rm.VIPRooms)
            {
                if (room == null || room.DoorIds == null) continue;
                bool any = false;
                foreach (ObjectGuid id in room.DoorIds) if (id.ObjectFromId<CommonDoor>() != null) { any = true; break; }
                if (any) continue;
                PatchCounters()[2417]++;
                return true;
            }
            return false;
        }

        // EA's RefreshVIPRooms body, iterating a copy (the only change).
        static void VipRefresh(ResortManager rm)
        {
            if (rm.VIPRooms.Count <= 0) return;
            foreach (ResortManager.VIPRoomData room in rm.VIPRooms.ToArray())
            {
                ulong id = room.SimDescriptionId;
                SimDescription desc = id != 0 ? SimDescription.Find(id) : null;
                if (desc != null && (desc.CreatedSim == null || !rm.IsCheckedIn(desc.CreatedSim))) desc = null;
                if (desc == null) room.ClearGuestList();
                int i = 0;
                while (i < room.DoorIds.Count)
                {
                    CommonDoor door = room.DoorIds[i].ObjectFromId<CommonDoor>();
                    if (door != null) { door.SetLockTypeAndOwner(CommonDoor.tLock.VIPRoom, desc); i++; }
                    else room.DoorIds.RemoveAt(i);
                }
                if (room.DoorIds.Count <= 0) { rm.VIPRooms.Remove(room); PatchCounters()[2418]++; }
            }
        }

        // VipRoomCheck
        //   run     a throwaway ResortManager (private constructor: no lot, no alarms, nothing registered) with two VIP rooms
        //           whose only door id names no object; EA's RefreshVIPRooms (through the patch) is called once.
        //           Off arm: BROKEN = "Collection was modified" thrown, 1 room left (the one after the removed room was
        //           never visited). On arm: FIXED = no exception, 0 rooms left, 2418 +2.
        //   result  counters 2417-2419
        public static object VipRoomCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "viproomrefresh: would throw 2417=" + c[2417] + ", removed 2418=" + c[2418] + ", errors 2419=" + c[2419];
            if (what == "result") return counters;
            if (what != "run") return "viproom <run|result>";
            ResortManager rm = Activator.CreateInstance(typeof(ResortManager), true) as ResortManager;
            if (rm == null || rm.VIPRooms == null) return "viproom check: no ResortManager (N/A)";
            rm.VIPRooms.Add(new ResortManager.VIPRoomData(0uL, ObjectGuid.InvalidObjectGuid));
            rm.VIPRooms.Add(new ResortManager.VIPRoomData(0uL, ObjectGuid.InvalidObjectGuid));
            string thrown = "none";
            try { rm.RefreshVIPRooms(); }
            catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; }
            int left = rm.VIPRooms.Count;
            bool on = ShortcutOn("viproomrefresh");
            string verdict = left == 0 && thrown == "none" ? "FIXED" : left == 1 && thrown != "none" ? "BROKEN" : "UNEXPECTED";
            return "viproom check (switch " + (on ? "on" : "off") + "): exception " + thrown + ", rooms left " + left + " of 2; verdict: " + verdict + "; " + counters;
        }
    }
}
