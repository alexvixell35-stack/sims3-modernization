using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Controllers;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // viproomrefresh harness check (docs/555 §3). Production code: VipRoomRefresh.cs.
    public static partial class Fixes
    {
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
