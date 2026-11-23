using System;

namespace Ts3Fixes
{
    // docs/321 lane 0: fill of this mod's fixed-size tables (always on, measurement only). High-water and full counts live in
    // counter slots 1518-1529; a "ts3fixes: caps ..." line goes to ts3native.log (UserDiag text channel) at most once a minute
    // and only when a value changed. ts3native's own tables log their "caps (...)" line (native/ts3native/caps.h).
    // Slots: 1518 AutShadow requests that hit a cap (objects or pairs; go-live drops them to the game's search), 1519 of them
    // the object cap, 1520 the pair cap, 1521 most meta objects in one request, 1522 most pairs; 1523 NearVenue groups over
    // kNvGroupMax, 1524 most open lots of one venue type; 1525 spawnerskip keys (high water), 1526 its clears at 4096; 1527
    // backoff entries (high water), 1528 its clears at 2048; 1529 cookclicklistener entries (high water; at 4096 the oldest goes).
    public static partial class Fixes
    {
        const int kCCapAny = 1518, kCCapObjs = 1519, kCCapPairs = 1520, kCCapObjsHw = 1521, kCCapPairsHw = 1522;
        const int kCSpkHw = 1525, kCSpkClears = 1526, kCBackoffHw = 1527, kCBackoffClears = 1528, kCCookHw = 1529;
        static bool sCapDirty;
        static int sCapAt;

        static void CapHw(int slot, int n)
        {
            int[] c = PatchCounters();
            if (n > c[slot]) { c[slot] = n; sCapDirty = true; }
            CapLogTick();
        }

        static void CapCount(int slot)
        {
            PatchCounters()[slot]++;
            sCapDirty = true;
            CapLogTick();
        }

        // sim thread only (the text channel is a Route call); a worker caller waits for the next sim-thread one
        static void CapLogTick()
        {
            if (!sCapDirty || unchecked(Environment.TickCount - sCapAt) < 60000 && sCapAt != 0) return;
            try
            {
                if (OnWorker()) return;
                sCapAt = Environment.TickCount | 1;
                sCapDirty = false;
                int[] c = PatchCounters();
                DiagLine("caps (docs/321 lane 0: hw/cap): autshadow objs " + c[kCCapObjsHw] + "/" + kMaxObjs + " pairs " + c[kCCapPairsHw] + "/"
                    + kMaxPairs + " capped " + c[kCCapAny] + " (objs " + c[kCCapObjs] + ", pairs " + c[kCCapPairs] + "); nearvenue group "
                    + c[kCNvGroupHw] + "/" + TripStageCore.kNvGroupMax + " over " + c[kCNvOverCap] + "; spawnerskip " + c[kCSpkHw]
                    + "/4096 clears " + c[kCSpkClears] + "; backoff " + c[kCBackoffHw] + "/" + kMaxBackoffEntries + " clears "
                    + c[kCBackoffClears] + "; cookclick " + c[kCCookHw] + "/" + kCcNotedMax);
            }
            catch (Exception) { }
        }
    }
}
