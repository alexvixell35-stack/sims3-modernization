using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // rabbitholedoor (docs/505): RabbitHole.AnimateDoorOpen raises mDoorOpenCounts[door] and, when the count goes 0 -> 1, asks
    // the rabbit hole's "RabbitHoleGeneric" state machine for state "Open" of actor "Door<n>". SetupStateMachineDoors gave
    // that actor to the door number of every enter and exit slot. When the animation cannot open that door, RequestState
    // throws SacsErrorException ("SACS failed to process a request ..."). The throw leaves AnimateEnterRabbitHole and the
    // interaction, so the Sim's entry ends (seen: a teen going to school at Bridgeport's Public School 67, door 2, NRaas
    // Careers' GoToSchoolInRabbitHoleEx). AnimateDoorClose makes the same request at 1 -> 0 ("Closed").
    // Fix, switch on: a SacsErrorException from either method is caught after EA's code ran. The door stays as it is and the
    // caller goes on; the count changed exactly as in EA's code, so open and close stay paired. Other exceptions pass.
    // [2015] door requests that threw SacsErrorException and were skipped.
    public static partial class Fixes
    {
        public class RabbitHoleDoorHook
        {
            public void OpenHook(int doorNumber)
            {
                try { OpenOrig(doorNumber); }
                catch (Exception e)
                {
                    if (!RabbitHoleDoorCore.Swallow(e, ShortcutOn("rabbitholedoor"))) throw;
                    RabbitHoleDoorSkipped((RabbitHole)(object)this, doorNumber, "Open");
                }
            }
            public void CloseHook(int doorNumber)
            {
                try { CloseOrig(doorNumber); }
                catch (Exception e)
                {
                    if (!RabbitHoleDoorCore.Swallow(e, ShortcutOn("rabbitholedoor"))) throw;
                    RabbitHoleDoorSkipped((RabbitHole)(object)this, doorNumber, "Closed");
                }
            }
            public void OpenOrig(int doorNumber) { }  // receives RabbitHole.AnimateDoorOpen's original header
            public void CloseOrig(int doorNumber) { } // receives RabbitHole.AnimateDoorClose's original header
        }
        static int sRhdLines;
        static readonly FieldInfo sRhdCounts = typeof(RabbitHole).GetField("mDoorOpenCounts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void RabbitHoleDoorSkipped(RabbitHole rh, int door, string state)
        {
            PatchCounters()[2015]++;
            if (sRhdLines++ < 5) ShortcutLog("rabbitholedoor: " + RhdName(rh) + ": Door" + door + " \"" + state + "\" threw SacsErrorException, skipped (door count " + RhdCount(rh, door) + ")");
        }
        static string RhdName(RabbitHole rh)
        {
            return rh.GetType().Name + " on " + (rh.LotCurrent != null ? rh.LotCurrent.Name : "no lot");
        }
        static int RhdCount(RabbitHole rh, int door)
        {
            int[] c = sRhdCounts != null ? sRhdCounts.GetValue(rh) as int[] : null;
            return c != null && door >= 0 && door < c.Length ? c[door] : -1;
        }

        // Repro check (tools/harness/repros.json rabbit-hole-door-check). "state": rabbit holes, the found door, its count.
        // "broken" (switch off): tries each enter/exit slot door of every rabbit hole whose count is 0, in world order: open,
        // then close again if it opened. Stops at the first door whose open throws SacsErrorException, puts that door's count
        // back to 0 (EA leaves it raised) and keeps it for "fixed". Throws if no door threw. "fixed" (switch on): opens and
        // closes the kept door; throws unless both return, the count ends where it started (0) and [2015] went up.
        static RabbitHole sRhdTarget;
        static int sRhdDoor = -1;
        public static string RabbitHoleDoorCheck(string what)
        {
            RabbitHole[] all = Sims3.Gameplay.Queries.GetObjects<RabbitHole>();
            string state = "switch " + ShortcutOn("rabbitholedoor") + ", " + all.Length + " rabbit holes, door "
                + (sRhdTarget != null ? RhdName(sRhdTarget) + " Door" + sRhdDoor + " count " + RhdCount(sRhdTarget, sRhdDoor) : "none yet")
                + ", skipped so far " + PatchCounters()[2015];
            if (what == "state") return state;
            if (sRhdCounts == null) throw new InvalidOperationException("RabbitHole.mDoorOpenCounts not found");
            if (what == "broken")
            {
                int tried = 0;
                foreach (RabbitHole rh in all)
                {
                    if (rh == null || rh.HasBeenDestroyed || rh.RabbitHoleProxy == null || rh.RabbitHoleProxy.SlotToSlotInfo == null) continue;
                    List<int> doors = new List<int>();
                    foreach (RabbitHole.SlotInfo si in rh.RabbitHoleProxy.SlotToSlotInfo.Values)
                        if (!doors.Contains(si.Number)) doors.Add(si.Number);
                    foreach (int d in doors)
                    {
                        if (RhdCount(rh, d) != 0) continue;  // a Sim is using it
                        tried++;
                        try { rh.AnimateDoorOpen(d); }
                        catch (SacsErrorException)
                        {
                            ((int[])sRhdCounts.GetValue(rh))[d] = 0;
                            sRhdTarget = rh; sRhdDoor = d;
                            return "the open threw SacsErrorException at the caller on " + RhdName(rh) + " Door" + d + " (" + tried + " doors tried); " + state;
                        }
                        rh.AnimateDoorClose(d);
                    }
                }
                throw new InvalidOperationException("rabbitholedoor broken: wrong outcome: no door open threw SacsErrorException (" + tried
                    + " doors tried; with the switch on the hook takes it, or no rabbit hole here has such a door); " + state);
            }
            if (what != "fixed") return "unknown mode " + what + " (state, broken, fixed)";
            if (sRhdTarget == null) throw new InvalidOperationException("rabbitholedoor fixed: run broken first (no door kept)");
            if (RhdCount(sRhdTarget, sRhdDoor) != 0) throw new InvalidOperationException("rabbitholedoor fixed: the door is in use now; " + state);
            int c0 = PatchCounters()[2015];
            string r;
            try
            {
                sRhdTarget.AnimateDoorOpen(sRhdDoor);
                int mid = RhdCount(sRhdTarget, sRhdDoor);
                sRhdTarget.AnimateDoorClose(sRhdDoor);
                r = "open and close returned, count 0 -> " + mid + " -> " + RhdCount(sRhdTarget, sRhdDoor) + ", skipped " + (PatchCounters()[2015] - c0);
                if (mid != 1 || RhdCount(sRhdTarget, sRhdDoor) != 0 || PatchCounters()[2015] - c0 < 1)
                    throw new InvalidOperationException("rabbitholedoor fixed: wrong outcome: " + r + "; " + state);
            }
            catch (SacsErrorException e)
            {
                ((int[])sRhdCounts.GetValue(sRhdTarget))[sRhdDoor] = 0;
                throw new InvalidOperationException("rabbitholedoor fixed: wrong outcome: the request still threw at the caller (" + e.Message + "); " + state);
            }
            return r;
        }
    }
}
